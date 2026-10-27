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
            try { Pick.Patch(h); }
            catch (Exception e) { MLog.Warn("[URT] Alt + right click fix not active: " + e.Message); }
            try { Start(); }
            catch (Exception e) { MLog.Error("[URT] start failed: " + e); Phase = "error: " + e.Message; }
            Dialog_UrtWelcome.ShowOnce();
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
            Store.Glossary = new Glossary(Folder, Target);
            Stopwatch sw = Stopwatch.StartNew();
            bool haveNew = File.Exists(Path.Combine(Folder, "memory_" + Target + ".bin")) || File.Exists(Path.Combine(Folder, "memory_" + Target + ".journal"));
            if (haveNew) Store.Load();
            if (haveNew) Cleanup251();
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
            bool rescan = false;
            if (fp != null && ScanCache.TryLoad(cachePath, fp, out List<Item> cachedItems, out string cachedReport))
            {
                Items = cachedItems;
                ScanReport = cachedReport + "; from the scan cache, checked in " + fpMs + " ms";
            }
            else
            {
                // 2.5.0: mods changed (or first start) – the game does not wait 25 s for the scan any more.
                // The last scan is used at once (texts that changed are skipped by the check when applied),
                // the new scan runs in the background and replaces it when done.
                rescan = true;
                if (ScanCache.TryLoad(cachePath, null, true, out List<Item> lastItems, out string lastReport, out var lastLists))
                {
                    Items = lastItems;
                    Scan.OriginalLists = lastLists;
                    ScanReport = lastReport + "; previous scan – mods changed, new scan in the background";
                }
                else
                {
                    Items = new List<Item>();
                    ScanReport = "first scan in the background";
                }
            }
            bySource = Items.GroupBy(i => Store.Key(i.Source)).ToDictionary(g => g.Key, g => g.ToList());
            long scanMs = sw.ElapsedMilliseconds;

            sw.Restart();
            int known = ApplyKnown();
            if (rescan) BackgroundScan(fp, cachePath);
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

        // 2.5.1, once: 2.5.0 sometimes scanned the game after our translations were in, so it sent
        // already translated texts to the services again ("Untranslated" results, wrong totals in Mods).
        // Those records and the misread "Mine" labels are dropped; the rest of the memory stays.
        private static void Cleanup251()
        {
            string marker = Path.Combine(Folder, "cleanup251_" + Target + ".done");
            if (File.Exists(marker)) return;
            try
            {
                int n = 0;
                foreach (var kv in Store.Snapshot())
                {
                    Rec r = kv.Value;
                    if (r == null || r.Manual) continue;
                    if (r.Verdict == Verdict.Untranslated || Protect.NeedsContext(kv.Key))
                    {
                        Store.Forget(kv.Key);
                        n++;
                    }
                }
                if (n > 0) Store.Compact();
                File.WriteAllText(marker, DateTime.Now.ToString("s") + " removed " + n);
                MLog.Info("[URT] cleanup 2.5.1: removed " + n + " records (texts translated twice, misread labels)");
            }
            catch (Exception e) { MLog.Warn("[URT] cleanup 2.5.1: " + e.Message); }
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

        // 2.5.0: the def scan off the main thread; the result replaces the list on the main thread
        private static void BackgroundScan(string fp, string cachePath)
        {
            Phase = "scanning the mods in the background";
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var sw = Stopwatch.StartNew();
                    List<Item> fresh = Scan.Run(out string rep, out var lists);
                    long ms = sw.ElapsedMilliseconds;
                    bool ok = Scan.Failures == 0;
                    // the word lists of live translation follow the new scan (heavy – here, not on the main thread)
                    try { Live.Init(fresh, Folder, Target); } catch (Exception e) { MLog.Warn("[URT] live after scan: " + e.Message); }
                    RunOnMain(() =>
                    {
                        Items = fresh;
                        Scan.OriginalLists = lists;
                        bySource = fresh.GroupBy(i => Store.Key(i.Source)).ToDictionary(g => g.Key, g => g.ToList());
                        ScanReport = rep + "; scanned in the background in " + ms + " ms";
                        // only what the last scan did not have – usually a few hundred texts of the changed mods
                        int n = ApplyKnown();
                        MLog.Info("[URT] background scan done: " + fresh.Count + " texts (" + rep + ") in " + ms + " ms, " + n + " known translations queued");
                        Kick();
                    });
                    if (fp != null && ok) ScanCache.Save(cachePath, fp, fresh, rep, lists);
                }
                catch (Exception e)
                {
                    MLog.Error("[URT] background scan: " + e);
                }
            });
        }

        public static bool Allowed(Item it)
        {
            var s = UniversalRimTranslatorMod.Settings;
            if (ModsTab.IsSkipped(it.Mod)) return false;     // 2.3.0: left in English by the player
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
                    var jobs = todo.Select(src => new Job { Source = src, SourceLang = bySource.TryGetValue(src, out var its) ? its[0].SourceLang : "en", Avoid = AvoidOnce.TryRemove(src, out string av) ? av : null }).ToList();
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

        // 2.4.1: "translate again" – the next try skips the service that made the old translation
        public static readonly ConcurrentDictionary<string, string> AvoidOnce = new ConcurrentDictionary<string, string>();

        public static void TranslateAgain(string source)
        {
            string key = Store.Key(source);
            if (key.Length == 0) return;
            if (Store.TryGet(source, out Rec old) && !string.IsNullOrEmpty(old.Provider) && !old.Manual)
                AvoidOnce[key] = old.Provider;
            Store.Forget(source);
            giveUp.TryRemove(key, out _);
            Live.Requeue(source);
            RetryFailed();
        }

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
                // 2.5.0: more per frame while not in a running game (menu, loading)
                bool menu = Current.ProgramState != ProgramState.Playing;
                int maxN = menu ? 3000 : 400, maxMs = menu ? 12 : 4;
                while (batch.Count < maxN && sw.ElapsedMilliseconds < maxMs && toApply.TryDequeue(out var x))
                    if (Allowed(x.Item1)) batch.Add(x);     // not into a mod left in English
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
