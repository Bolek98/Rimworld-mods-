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

        // 2.5.2: labels of humanlike races and xenotypes from mods – proper names, kept as they are
        public static HashSet<string> ProperNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 2.5.2: mods added since the last start that wait for the player (names, as in Item.Mod)
        public static volatile HashSet<string> HeldMods = new HashSet<string>(StringComparer.Ordinal);
        private static Dictionary<string, string> modIdByName = new Dictionary<string, string>(StringComparer.Ordinal);

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
            BuildProperNames();
            CheckNewMods();

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
            Chain.Notes = GlossaryNotes();
        }

        // 2.5.2: the player's own words for the AI services ("use osadník for colonist" …)
        private static string GlossaryNotes()
        {
            try
            {
                var pairs = Store?.Glossary?.Pairs;
                if (pairs == null || pairs.Count == 0) return null;
                var use = pairs.Where(p => !string.IsNullOrEmpty(p.Value)).Take(30).Select(p => p.Key + " -> " + p.Value).ToList();
                if (use.Count == 0) return null;
                return "The player's glossary (word -> the word the player wants in the translation; use these words where they fit): " + string.Join("; ", use.ToArray());
            }
            catch { return null; }
        }

        // 2.5.2: a short context of one text for the AI services: what kind of text, which mod,
        // glossary words that occur in it
        private static string HintFor(string key, string source)
        {
            try
            {
                string kind = "text shown in the game", mod = null;
                if (bySource.TryGetValue(key, out var its) && its.Count > 0)
                {
                    Item it = its[0];
                    mod = it.Mod;
                    string field = it.Path ?? "";
                    int dot = field.LastIndexOf('.');
                    if (it.Kind != ItemKind.Keyed && dot >= 0) field = field.Substring(dot + 1);
                    string f = field.ToLowerInvariant();
                    string of = it.DefType != null ? " of a " + it.DefType.Name : "";
                    if (it.IsRule || it.Kind == ItemKind.DefListItem) kind = "grammar rule / generated text piece" + of;
                    else if (it.Kind == ItemKind.Keyed) kind = "interface text";
                    else if (f.Contains("letter")) kind = "letter" + of;
                    else if (f.Contains("desc")) kind = "description" + of;
                    else if (f.Contains("label") || f.Contains("title")) kind = "label (short name)" + of;
                    else if (f.Contains("message")) kind = "message" + of;
                    else kind = "text" + of;
                }
                string h = kind + (string.IsNullOrEmpty(mod) || mod == "?" ? "" : ", mod: " + mod);
                var found = Store?.Glossary?.Found(source, 8);
                if (found != null && found.Count > 0)
                    h += ", glossary: " + string.Join("; ", found.Select(p => p.Key + " -> " + p.Value).ToArray());
                return h;
            }
            catch { return null; }
        }

        // 2.5.2: humanlike race and xenotype names of mods are proper names ("Yuran" became "Fees")
        private static void BuildProperNames()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (td?.race != null && td.race.Humanlike && Official(td) == false && !string.IsNullOrWhiteSpace(td.label))
                        set.Add(td.label.Trim());
                foreach (RimWorld.XenotypeDef xd in DefDatabase<RimWorld.XenotypeDef>.AllDefsListForReading)
                    if (xd != null && Official(xd) == false && !string.IsNullOrWhiteSpace(xd.label))
                        set.Add(xd.label.Trim());
            }
            catch (Exception e) { MLog.Warn("[URT] race names: " + e.Message); }
            ProperNames = set;
        }

        // official content (the game and its DLCs) is translated by the game's language packs
        private static bool? Official(Def d) => d.modContentPack == null ? (bool?)null : d.modContentPack.IsOfficialMod;

        public static bool IsProperName(Item it)
        {
            if (it?.Source == null || UniversalRimTranslatorMod.Settings.translateRaceNames) return false;
            HashSet<string> p = ProperNames;
            return p.Count > 0 && p.Contains(it.Source.Trim());
        }

        // 2.5.2: "don't translate newly added mods automatically" – the mods known at the last start
        // are kept in a file; mods that are new wait in the Mods tab until the player lets them in
        private static string KnownModsPath => Path.Combine(Folder, "known_mods.txt");

        private static void CheckNewMods()
        {
            try
            {
                var ids = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                    if (!string.IsNullOrEmpty(m.Name) && !string.IsNullOrEmpty(m.PackageId)) ids[m.Name] = m.PackageId.ToLowerInvariant();
                modIdByName = ids;
                bool exists = File.Exists(KnownModsPath);
                if (!UniversalRimTranslatorMod.Settings.holdNewMods || !exists)
                {
                    // off (or the very first start): every mod present now counts as known
                    SaveKnown(new HashSet<string>(ids.Values));
                    HeldMods = new HashSet<string>(StringComparer.Ordinal);
                    return;
                }
                var known = new HashSet<string>(File.ReadAllLines(KnownModsPath).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0));
                HeldMods = new HashSet<string>(ids.Where(kv => !known.Contains(kv.Value)).Select(kv => kv.Key), StringComparer.Ordinal);
                if (HeldMods.Count > 0)
                    MLog.Info("[URT] new mods wait for the player: " + string.Join(", ", HeldMods.ToArray()));
            }
            catch (Exception e)
            {
                MLog.Warn("[URT] new mods: " + e.Message);
                HeldMods = new HashSet<string>(StringComparer.Ordinal);
            }
        }

        private static void SaveKnown(HashSet<string> ids)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                File.WriteAllLines(KnownModsPath, ids.OrderBy(x => x).ToArray());
            }
            catch (Exception e) { MLog.Warn("[URT] known mods: " + e.Message); }
        }

        public static bool IsHeld(string mod)
        {
            if (!UniversalRimTranslatorMod.Settings.holdNewMods || mod == null) return false;
            HashSet<string> h = HeldMods;
            return h.Count > 0 && h.Contains(mod);
        }

        // the player lets the waiting mods be translated
        public static void AllowNewMods()
        {
            try
            {
                var known = File.Exists(KnownModsPath)
                    ? new HashSet<string>(File.ReadAllLines(KnownModsPath).Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0))
                    : new HashSet<string>();
                foreach (string id in modIdByName.Values) known.Add(id);
                SaveKnown(known);
            }
            catch (Exception e) { MLog.Warn("[URT] known mods: " + e.Message); }
            HeldMods = new HashSet<string>(StringComparer.Ordinal);
            Kick();
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

        // 2.6.0: is this text (Store.Key form) one of the texts of the current scan
        public static bool KnownSource(string key)
        {
            var b = bySource;
            return key != null && b != null && b.ContainsKey(key);
        }

        public static volatile bool Scanning;

        // 2.6.0: "translate everything now" – a fresh scan of all mods, the scan cache is ignored
        // (and replaced when the scan is done)
        public static bool Rescan()
        {
            if (!Active || Scanning) return false;
            string fp = null;
            try { fp = ScanCache.Fingerprint(); }
            catch (Exception e) { MLog.Warn("[URT] scan fingerprint: " + e.Message); }
            BackgroundScan(fp, ScanCache.FilePath(Folder, Target));
            return true;
        }

        // 2.5.0: the def scan off the main thread; the result replaces the list on the main thread
        private static void BackgroundScan(string fp, string cachePath)
        {
            Phase = "scanning the mods in the background";
            Scanning = true;
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
                        Scanning = false;
                        Kick();
                    });
                    if (fp != null && ok) ScanCache.Save(cachePath, fp, fresh, rep, lists);
                }
                catch (Exception e)
                {
                    Scanning = false;
                    MLog.Error("[URT] background scan: " + e);
                }
            });
        }

        public static bool Allowed(Item it)
        {
            var s = UniversalRimTranslatorMod.Settings;
            if (ModsTab.IsSkipped(it.Mod)) return false;     // 2.3.0: left in English by the player
            if (IsProperName(it)) return false;               // 2.5.2: race and xenotype names stay
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
                    // 2.5.2: offline – only the memory is used, no service is asked
                    if (UniversalRimTranslatorMod.Settings.offlineOnly) { Phase = "offline – memory only"; wake.WaitOne(5000); continue; }
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
                    // 2.5.2: Latin texts the scan left to detection: short ones are English (names were taken for Malay);
                    // AI services get the kind of text, the mod and the glossary words as context
                    var jobs = todo.Select(src => new Job
                    {
                        Source = src,
                        SourceLang = bySource.TryGetValue(src, out var its) ? (its[0].SourceLang ?? Lang.LatinDefault(src)) : (Lang.GuessFromScript(src) ?? "en"),
                        Avoid = AvoidOnce.TryRemove(src, out string av) ? av : null,
                        Hint = HintFor(src, src)
                    }).ToList();
                    Chain.Notes = GlossaryNotes();
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
            // 2.5.2: texts of new mods that wait for the player are not sent to the services
            return live.Concat(Items.Where(i => Allowed(i) && !IsHeld(i.Mod)).OrderBy(i => i.Priority).Select(i => Store.Key(i.Source))).Distinct()
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
                UrtTools.Tick();     // 2.6.0: "translate everything now" progress / done message
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
