using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // The whole life of the translator in one game session:
    //  start  – load the translation memory (or take over the 0.9.x cache once), find untranslated
    //           texts, put in everything that is already known;
    //  then   – a background thread translates the rest with the chain of services, results go
    //           into the memory at once and into the game a few at a time each frame (no stutter).
    [StaticConstructorOnStartup]
    public static class Runtime
    {
        public static Store Store;
        public static readonly Chain Chain = new Chain();
        public static string Target;             // ISO code of the active language
        public static string TargetName;
        public static List<Item> Items = new List<Item>();
        public static string ScanReport = "", MigrationReport = "";
        public static volatile string Phase = "starting";
        public static int Applied, Translated, Failed, Rejected;
        public static volatile bool Paused;
        public static bool Active;

        private static readonly ConcurrentQueue<(Item, string)> toApply = new ConcurrentQueue<(Item, string)>();
        private static readonly ConcurrentQueue<Action> mainThread = new ConcurrentQueue<Action>();
        private static Thread worker;
        private static CancellationTokenSource cts = new CancellationTokenSource();
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);
        private static Dictionary<string, List<Item>> bySource = new Dictionary<string, List<Item>>();

        public static string Folder => Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator2");

        static Runtime()
        {
            var h = new Harmony("bolek98.urt2");
            try { h.Patch(AccessTools.Method(typeof(Root), "Update"), postfix: new HarmonyMethod(typeof(Runtime), nameof(RootUpdate))); }
            catch (Exception e) { MLog.Warn("[URT] main thread pump not active: " + e.Message); }
            try { GrammarFallback.Patch(h); }
            catch (Exception e) { MLog.Warn("[URT] grammar fallback not active: " + e.Message); }
            try { Live.Patch(h); }
            catch (Exception e) { MLog.Warn("[URT] live translation not active: " + e.Message); }
            try { Start(); }
            catch (Exception e) { MLog.Error("[URT] start failed: " + e); Phase = "error: " + e.Message; }
        }

        private static void Start()
        {
            var s = UniversalRimTranslatorMod.Settings;
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            if (active == null || active == LanguageDatabase.defaultLanguage)
            {
                Phase = "the game is in English – nothing to translate";
                MLog.Info("[URT] " + Phase);
                return;
            }
            TargetName = active.FriendlyNameNative ?? active.folderName;
            Target = Lang.ToIso(active.folderName);
            Active = true;
            Configure();

            Store = new Store(Folder, Target) { Log = m => MLog.Warn("[URT] " + m) };
            Stopwatch sw = Stopwatch.StartNew();
            bool haveNew = File.Exists(Path.Combine(Folder, "memory_" + Target + ".bin")) || File.Exists(Path.Combine(Folder, "memory_" + Target + ".journal"));
            if (haveNew) Store.Load();
            long loadMs = sw.ElapsedMilliseconds;

            sw.Restart();
            // 2.1.0: the scan result is kept on disk while mods, files and language stay the same
            string fp = null;
            long fpMs = 0;
            try
            {
                var fsw = Stopwatch.StartNew();
                fp = ScanCache.Fingerprint();
                fpMs = fsw.ElapsedMilliseconds;
            }
            catch (Exception e) { MLog.Warn("[URT] scan fingerprint: " + e.Message); }
            string cachePath = ScanCache.FilePath(Folder, Target);
            if (fp != null && ScanCache.TryLoad(cachePath, fp, out List<Item> cachedItems, out string cachedReport))
            {
                Items = cachedItems;
                ScanReport = cachedReport + "; from the scan cache, checked in " + fpMs + " ms";
            }
            else
            {
                Items = Scan.Run(out ScanReport);
                if (fp != null && Scan.Failures == 0)
                {
                    List<Item> snapshot = Items;
                    string rep = ScanReport;
                    ThreadPool.QueueUserWorkItem(_ => ScanCache.Save(cachePath, fp, snapshot, rep));
                }
            }
            bySource = Items.GroupBy(i => Store.Key(i.Source)).ToDictionary(g => g.Key, g => g.ToList());
            long scanMs = sw.ElapsedMilliseconds;

            sw.Restart();
            int known = ApplyKnown();
            MLog.Game("[Universal RimTranslator] v" + UrtVersion.Display + " – " + TargetName + ": " + Items.Count + " untranslated texts found (" + ScanReport + "), " + known +
                      " translated from memory (" + Store.Count + " remembered). Load " + loadMs + " ms, scan " + scanMs + " ms, apply " + sw.ElapsedMilliseconds + " ms.");

            try
            {
                Live.Init(Items, Folder, Target);
                UpdateLive();
            }
            catch (Exception e) { MLog.Warn("[URT] live translation: " + e.Message); }

            worker = new Thread(() => Work(!haveNew)) { IsBackground = true, Name = "URT translator", Priority = ThreadPriority.BelowNormal };
            worker.Start();
        }

        public static void UpdateLive()
        {
            var s = UniversalRimTranslatorMod.Settings;
            bool on = Active && Store != null && s.enabled && s.translateLive;
            if (on != Live.On) Live.ClearCache();
            Live.On = on;
        }

        public static void Configure()
        {
            var s = UniversalRimTranslatorMod.Settings;
            Chain.Order = s.ProviderOrder();
            Chain.Config = s.ProviderConfig();
            Chain.Log = m => MLog.Info("[URT] " + m);
        }

        // everything the memory already knows goes into the game now
        private static int ApplyKnown()
        {
            var work = new List<(Item, string)>();
            foreach (Item it in Items)
            {
                if (!Allowed(it)) continue;
                string t = Store.Use(it.Source);
                if (t != null && !Apply.IsApplied(it, t)) work.Add((it, t));
            }
            int n = Apply.Many(work);
            Applied += n;
            return n;
        }

        public static bool Allowed(Item it)
        {
            var s = UniversalRimTranslatorMod.Settings;
            if (it.Kind == ItemKind.Keyed) return s.translateKeyed;
            if (it.IsRule) return s.translateRules;
            return s.translateDefs;
        }

        // ---------------- background ----------------
        private static void Work(bool migrate)
        {
            try
            {
                if (migrate) Migrate();
                Phase = "translating";
                while (!cts.IsCancellationRequested)
                {
                    if (Paused || !UniversalRimTranslatorMod.Settings.enabled) { Phase = "paused"; wake.WaitOne(2000); continue; }
                    Live.Flush();
                    Live.Prune(k => Store.NeedsTranslation(k) && !(giveUp.TryGetValue(k, out int gg) && gg >= 3));
                    List<string> todo = Pending(200);
                    if (todo.Count == 0)
                    {
                        PendingCached = 0;
                        Phase = "done – everything found is translated";
                        if (Store.JournalBig) Store.Compact();
                        wake.WaitOne(30000);
                        continue;
                    }
                    Phase = "translating (" + PendingCount() + " left)";
                    var jobs = todo.Select(src => new Job { Source = src, SourceLang = bySource.TryGetValue(src, out var its) ? its[0].SourceLang : "en" }).ToList();
                    Chain.Run(jobs, Target, cts.Token);
                    int got = 0;
                    foreach (Job j in jobs)
                    {
                        string key = Store.Key(j.Source);
                        bool live = Live.Pending.ContainsKey(key);
                        if (j.Result == null)
                        {
                            Failed++;
                            int g2 = giveUp[key] = giveUp.TryGetValue(key, out int g) ? g + 1 : 1;
                            if (live && g2 >= 3) Live.Done(key, false);
                            continue;
                        }
                        Store.PutAuto(j.Source, j.Result, j.Provider, j.Verdict);
                        if (j.Verdict != Verdict.Ok) { Rejected++; giveUp[key] = 99; if (live) Live.Done(key, false); continue; }
                        Translated++;
                        got++;
                        if (live) Live.Done(key, true);
                        if (bySource.TryGetValue(key, out var targets))
                            foreach (Item it in targets) toApply.Enqueue((it, j.Result));
                    }
                    if (got == 0 && jobs.Count > 0)
                    {
                        // nothing worked (all services paused or offline): wait a bit
                        Phase = "waiting for the services";
                        wake.WaitOne(20000);
                    }
                    if (Store.JournalBig) Store.Compact();
                }
            }
            catch (Exception e)
            {
                Phase = "error: " + e.Message;
                MLog.Error("[URT] translator thread: " + e);
            }
        }

        private static readonly ConcurrentDictionary<string, int> giveUp = new ConcurrentDictionary<string, int>();

        private static IEnumerable<string> PendingSources()
        {
            // texts on the screen right now first (live), then the game's texts
            IEnumerable<string> live = Live.On ? Live.Pending.Keys.ToList() : Enumerable.Empty<string>();
            return live.Concat(Items.Where(Allowed).OrderBy(i => i.Priority).Select(i => Store.Key(i.Source))).Distinct()
                        .Where(k => Store.NeedsTranslation(k) && !(giveUp.TryGetValue(k, out int g) && g >= 3));
        }

        private static List<string> Pending(int max) => PendingSources().Take(max).ToList();

        // counted by the translator thread (counting every frame in the settings window would be slow)
        public static volatile int PendingCached;

        public static int PendingCount()
        {
            int n = Active && Store != null ? PendingSources().Count() : 0;
            PendingCached = n;
            return n;
        }

        public static void Kick() => wake.Set();

        public static void RetryFailed() { giveUp.Clear(); Chain.ClearPauses(); Kick(); }

        // once: the 0.9.x cache becomes the new memory (each translation checked)
        private static void Migrate()
        {
            string cfg = GenFilePaths.ConfigFolderPath;
            string main = Path.Combine(cfg, "UniversalRimTranslatorCache.xml");
            string live = Path.Combine(cfg, "UniversalRimTranslatorCache.live.xml");
            if (!File.Exists(main) && !File.Exists(live)) return;
            Phase = "taking over the translations of version 0.9 (once)";
            var sw = Stopwatch.StartNew();
            var rep = new Store.ImportReport();
            try { Store.ImportOldCache(main, rep); } catch (Exception e) { MLog.Warn("[URT] old cache: " + e.Message); }
            try { Store.ImportOldCache(live, rep); } catch (Exception e) { MLog.Warn("[URT] old live cache: " + e.Message); }
            int manual = 0;
            try { manual = Store.ImportOldManual(Path.Combine(cfg, "UniversalRimTranslator", "ManualOverrides.txt")); } catch { }
            int keep = 0;
            try
            {
                string kp = Path.Combine(cfg, "URT_keep_original.txt");
                if (File.Exists(kp))
                    foreach (string line in File.ReadAllLines(kp))
                        if (!string.IsNullOrWhiteSpace(line)) { Store.PutManual(line.Replace("\\n", "\n"), line.Replace("\\n", "\n")); keep++; }
            }
            catch { }
            Store.Compact();
            MigrationReport = rep + "; manual " + manual + ", keep original " + keep + " (" + sw.ElapsedMilliseconds / 1000 + " s)";
            MLog.Game("[Universal RimTranslator] took over the 0.9 translations: " + MigrationReport + ". The old files stay untouched.");
            mainThread.Enqueue(() => { int n = ApplyKnown(); MLog.Info("[URT] applied " + n + " taken-over translations"); });
        }

        // ---------------- main thread ----------------
        public static void RootUpdate()
        {
            if (!Active) return;
            try
            {
                Live.Tick();
                while (mainThread.TryDequeue(out Action a)) a();
                if (toApply.IsEmpty) return;
                var sw = Stopwatch.StartNew();
                var batch = new List<(Item, string)>();
                while (batch.Count < 400 && sw.ElapsedMilliseconds < 4 && toApply.TryDequeue(out var x))
                    batch.Add(x);
                if (batch.Count > 0) Applied += Apply.Many(batch);
            }
            catch (Exception e)
            {
                MLog.WarnOnce("[URT] applying translations: " + e.Message, 0x4a11);
            }
        }

        public static void RunOnMain(Action a) => mainThread.Enqueue(a);

        // after a manual edit or setting change: put the current state of everything into the game
        public static void Reapply()
        {
            UpdateLive();
            RunOnMain(() =>
            {
                Live.ClearCache();     // edited or removed translations show at once
                var work = new List<(Item, string)>();
                foreach (Item it in Items)
                {
                    string t = Allowed(it) ? Store.Use(it.Source) : null;
                    if (t != null && !Apply.IsApplied(it, t)) work.Add((it, t));
                    else if (t == null && Apply.HasAny(it)) work.Add((it, null));
                }
                Apply.Many(work);
            });
        }
    }
}
