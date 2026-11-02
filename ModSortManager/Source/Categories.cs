using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ModSortManager
{
    // ===========================================================================
    // 0.6.0: mod categories. Every installed mod gets one main category (races, apparel, weapons, UI,
    // performance …), worked out once in the background from what is inside the mod (its defs,
    // textures, translations, assemblies) and from its name. The result is kept in a small file next
    // to the game's config, so it is only redone for mods that changed. The player can move any mod
    // to another category or to own categories; that is kept in the settings.
    // ===========================================================================
    public static class ModCats
    {
        public const string All = "all";
        public const string Custom = "u:";
        // the built-in categories, in the order they are listed
        public static readonly string[] BuiltIn =
        {
            "game", "frameworks", "races", "apparel", "weapons", "buildings", "production", "food", "animals",
            "medical", "events", "factions", "ui", "performance", "textures", "translations", "adult", "other"
        };

        private const string CacheVersion = "v1";
        private static readonly object locker = new object();
        private static readonly Dictionary<string, KeyValuePair<long, string>> cache = new Dictionary<string, KeyValuePair<long, string>>();
        private static bool cacheLoaded;
        private static Thread worker;
        private static volatile int done, total;
        private static volatile int version;
        public static bool Busy => worker != null && worker.IsAlive;
        public static int Done => done;
        public static int Total => total;
        // grows whenever results arrive or the player moves a mod – lists cached on it are redone
        public static int Version => version + overrideVer;

        private static string CacheFile => Path.Combine(GenFilePaths.ConfigFolderPath, "ModSortManager_Categories.txt");

        public static string Label(string key)
        {
            if (key == null) return "…";
            if (key.StartsWith(Custom, StringComparison.Ordinal)) return key.Substring(Custom.Length);
            return ("MSM_Cat_" + key).Translate();
        }

        // ---------------- the player's choices ----------------
        private static Dictionary<string, string> overrides;
        private static int overrideVer;

        private static string Pid(ModMetaData m)
        {
            try { return m.PackageIdNonUnique ?? ""; } catch { return ""; }
        }

        private static Dictionary<string, string> Overrides()
        {
            if (overrides != null) return overrides;
            overrides = new Dictionary<string, string>();
            foreach (string s in MSMMod.Settings.catOverrides)
            {
                int i = s.IndexOf('|');
                if (i > 0) overrides[s.Substring(0, i)] = s.Substring(i + 1);
            }
            return overrides;
        }

        private static void SaveOverrides()
        {
            MSMMod.Settings.catOverrides = Overrides().Select(kv => kv.Key + "|" + kv.Value).OrderBy(x => x).ToList();
            overrideVer++;
            MSMMod.Settings.Write();
        }

        public static bool IsManual(ModMetaData m) => Overrides().ContainsKey(Pid(m));

        public static void Move(ModMetaData m, string key)
        {
            string pid = Pid(m);
            if (pid.NullOrEmpty()) return;
            if (key == null) Overrides().Remove(pid);
            else Overrides()[pid] = key;
            SaveOverrides();
        }

        public static List<string> CustomCats => MSMMod.Settings.customCats;

        public static bool AddCustom(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0 || name.Length > 40 || CustomCats.Any(c => string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) return false;
            CustomCats.Add(name);
            overrideVer++;
            MSMMod.Settings.Write();
            return true;
        }

        public static bool RenameCustom(string old, string name)
        {
            name = (name ?? "").Trim();
            int i = CustomCats.IndexOf(old);
            if (i < 0 || name.Length == 0 || name.Length > 40) return false;
            if (CustomCats.Any(c => c != old && string.Equals(c, name, StringComparison.OrdinalIgnoreCase))) return false;
            CustomCats[i] = name;
            foreach (string k in Overrides().Keys.ToList())
                if (overrides[k] == Custom + old) overrides[k] = Custom + name;
            SaveOverrides();
            return true;
        }

        public static void DeleteCustom(string name)
        {
            CustomCats.Remove(name);
            foreach (string k in Overrides().Keys.ToList())
                if (overrides[k] == Custom + name) overrides.Remove(k);
            SaveOverrides();
        }

        // ---------------- lookups ----------------
        private static string PathKey(ModMetaData m)
        {
            try { return m.RootDir.FullName.ToLowerInvariant(); } catch { return Pid(m); }
        }

        // the automatic category, or null while it is still being worked out
        public static string Auto(ModMetaData m)
        {
            try { if (m.Official) return "game"; } catch { }
            lock (locker)
            {
                KeyValuePair<long, string> e;
                return cache.TryGetValue(PathKey(m), out e) ? e.Value : null;
            }
        }

        public static string Of(ModMetaData m)
        {
            string o;
            if (Overrides().TryGetValue(Pid(m), out o))
            {
                // a custom category that was deleted meanwhile – back to automatic
                if (!o.StartsWith(Custom, StringComparison.Ordinal) || CustomCats.Contains(o.Substring(Custom.Length))) return o;
            }
            return Auto(m);
        }

        // ---------------- the background sorting ----------------
        private class Info
        {
            public string key, path, name, pid;
            public int dependents;
        }

        private static bool started;
        public static bool Started => started;

        public static void Start(bool again = false)
        {
            if (Busy || (started && !again)) return;
            started = true;
            try
            {
                LoadCache();
                // how many installed mods need this one – libraries and frameworks are needed by many
                var need = new Dictionary<string, int>();
                List<ModMetaData> mods = ModLister.AllInstalledMods.ToList();
                foreach (ModMetaData m in mods)
                {
                    List<ModDependency> deps = null;
                    try { deps = m.Dependencies; } catch { }
                    if (deps == null) continue;
                    foreach (ModDependency d in deps)
                    {
                        if (d?.packageId == null) continue;
                        string k = d.packageId.ToLowerInvariant();
                        need.TryGetValue(k, out int n);
                        need[k] = n + 1;
                    }
                }
                var infos = new List<Info>();
                foreach (ModMetaData m in mods)
                {
                    bool off;
                    try { off = m.Official; } catch { off = false; }
                    if (off) continue;
                    var i = new Info { key = PathKey(m), name = m.Name ?? "", pid = Pid(m) };
                    try { i.path = m.RootDir.FullName; } catch { continue; }
                    need.TryGetValue(i.pid, out i.dependents);
                    infos.Add(i);
                }
                if (again) lock (locker) cache.Clear();
                done = 0;
                total = infos.Count;
                worker = new Thread(() => Run(infos)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "MSM categories" };
                worker.Start();
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] categories: " + e.Message); }
        }

        private static long Stamp(string path)
        {
            long s = 0;
            try { s = Directory.GetLastWriteTimeUtc(path).Ticks; } catch { }
            try
            {
                string about = Path.Combine(path, "About", "About.xml");
                if (File.Exists(about)) s ^= File.GetLastWriteTimeUtc(about).Ticks * 31;
            }
            catch { }
            return s;
        }

        private static void Run(List<Info> infos)
        {
            bool changed = false;
            foreach (Info i in infos)
            {
                try
                {
                    long st = Stamp(i.path);
                    KeyValuePair<long, string> e;
                    bool have;
                    lock (locker) have = cache.TryGetValue(i.key, out e) && e.Key == st;
                    if (!have)
                    {
                        string cat = Classify(i);
                        lock (locker) cache[i.key] = new KeyValuePair<long, string>(st, cat);
                        changed = true;
                        version++;
                    }
                }
                catch { }
                done++;
            }
            version++;
            if (changed) SaveCache(infos);
        }

        private static void LoadCache()
        {
            if (cacheLoaded) return;
            cacheLoaded = true;
            try
            {
                if (!File.Exists(CacheFile)) return;
                string[] lines = File.ReadAllLines(CacheFile);
                if (lines.Length == 0 || lines[0] != CacheVersion) return;
                lock (locker)
                    foreach (string l in lines.Skip(1))
                    {
                        string[] p = l.Split('\t');
                        if (p.Length != 3 || !long.TryParse(p[0], out long st) || !BuiltIn.Contains(p[1])) continue;
                        cache[p[2]] = new KeyValuePair<long, string>(st, p[1]);
                    }
            }
            catch { }
        }

        private static void SaveCache(List<Info> installed)
        {
            try
            {
                var keep = new HashSet<string>(installed.Select(i => i.key));
                var lines = new List<string> { CacheVersion };
                lock (locker)
                    foreach (var kv in cache)
                        if (keep.Contains(kv.Key)) lines.Add(kv.Value.Key + "\t" + kv.Value.Value + "\t" + kv.Key);
                File.WriteAllLines(CacheFile, lines.ToArray());
            }
            catch { }
        }

        // ---------------- deciding the category ----------------
        private static readonly Regex Adult = new Regex(@"\b(rjw|rimjobworld|nsfw|sexperience|licentia|18\+)", RegexOptions.IgnoreCase);
        private static readonly Regex Perf = new Regex(@"performance|\bfps\b|\btps\b|optimi[sz]|rocketman|faster (game|loading|startup)|\bspeed ?up\b|\blag\b|\bstutter|\bloading time|garbage collect", RegexOptions.IgnoreCase);
        private static readonly Regex Frame = new Regex(@"framework|\blibrary\b|\blib\b|\bapi\b|harmony|hugslib|xml ?extensions|prepatcher", RegexOptions.IgnoreCase);
        private static readonly Regex Transl = new Regex(@"translation|locali[sz]ation|汉化|中文|翻译|日本語|한국어|перевод|русиф|překlad|tradu[cç][aã]o|traducci[oó]n|übersetzung|traduction|t[łl]umaczenie", RegexOptions.IgnoreCase);
        private static readonly Regex Tex = new Regex(@"texture|retex|\bgraphics?\b|\bsprites?\b|\breskin", RegexOptions.IgnoreCase);
        private static readonly Regex Ui = new Regex(@"\b(ui|qol|quality of life|interface|tabs?|menus?|hud|overlays?|inspect\w*|buttons?|windows?|notifications?|alerts?|info|search|sort\w*|manager|tools?|tweaks?|camera|zoom|hotkeys?|keybind\w*|gizmos?|tooltips?)\b", RegexOptions.IgnoreCase);
        private static readonly string[] KnownFrameworks =
        {
            "brrainz.harmony", "unlimitedhugs.hugslib", "oskarpotocki.vanillafactionsexpanded.core", "erdelf.humanoidalienraces",
            "imranfish.xmlextensions", "zetrith.prepatcher", "jecrell.jecstools", "mlie.jecstools", "owlchemist.cherrypicker"
        };

        private struct Marker
        {
            public string text, cat;
            public int weight;
            public Marker(string t, string c, int w) { text = t; cat = c; weight = w; }
        }

        private static readonly Marker[] Markers =
        {
            new Marker("AlienRace.ThingDef_AlienRace", "races", 20),
            new Marker("<XenotypeDef", "races", 8),
            new Marker("<intelligence>Humanlike", "races", 10),
            new Marker("<GeneDef", "races", 1),
            new Marker("<apparel>", "apparel", 2),
            new Marker("<weaponTags>", "weapons", 2),
            new Marker("<equipmentType>Primary", "weapons", 1),
            new Marker("<designationCategory>", "buildings", 1),
            new Marker("Building_WorkTable", "production", 3),
            new Marker("<RecipeDef", "production", 1),
            new Marker("<plant>", "food", 2),
            new Marker("<preferability>", "food", 1),
            new Marker("<race>", "animals", 2),
            new Marker("<HediffDef", "medical", 1),
            new Marker("<drugCategory>", "medical", 2),
            new Marker("Recipe_Surgery", "medical", 1),
            new Marker("Recipe_Install", "medical", 1),
            new Marker("<StorytellerDef", "events", 25),
            new Marker("<IncidentDef", "events", 3),
            new Marker("<QuestScriptDef", "events", 3),
            new Marker("<GameConditionDef", "events", 2),
            new Marker("<FactionDef", "factions", 6),
            new Marker("<BiomeDef", "factions", 6),
            new Marker("<WorldObjectDef", "factions", 3),
            new Marker("<SitePartDef", "factions", 2),
        };

        private static readonly HashSet<string> SkipDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "About", "Languages", "Source", "Sources", ".git", ".vs", "obj", "bin", "Textures", "Sounds" };

        private static string Classify(Info i)
        {
            string text = i.name + " " + i.pid;
            if (Adult.IsMatch(text) || i.pid.Contains("rjw")) return "adult";
            if (KnownFrameworks.Contains(i.pid) || Frame.IsMatch(i.name) || i.dependents >= 4) return "frameworks";
            if (Perf.IsMatch(i.name)) return "performance";

            var score = new Dictionary<string, int>();
            int patchOps = 0, xmlFiles = 0;
            bool dll = false, tex = false, lang = false;
            long bytes = 0;
            var stack = new Stack<KeyValuePair<string, int>>();
            stack.Push(new KeyValuePair<string, int>(i.path, 0));
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                string[] dirs, files;
                try { dirs = Directory.GetDirectories(cur.Key); files = Directory.GetFiles(cur.Key); } catch { continue; }
                foreach (string d in dirs)
                {
                    string n = Path.GetFileName(d);
                    if (n.Equals("Textures", StringComparison.OrdinalIgnoreCase)) { tex = tex || HasFiles(d); continue; }
                    if (n.Equals("Languages", StringComparison.OrdinalIgnoreCase)) { lang = lang || HasFiles(d); continue; }
                    if (n.Equals("Assemblies", StringComparison.OrdinalIgnoreCase))
                    {
                        try { dll = dll || Directory.GetFiles(d, "*.dll").Any(f => !Path.GetFileName(f).Equals("0Harmony.dll", StringComparison.OrdinalIgnoreCase)); } catch { }
                        continue;
                    }
                    if (SkipDirs.Contains(n) || cur.Value >= 6) continue;
                    stack.Push(new KeyValuePair<string, int>(d, cur.Value + 1));
                }
                foreach (string f in files)
                {
                    if (!f.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                    if (xmlFiles >= 1500 || bytes > 60L * 1024 * 1024) break;
                    try
                    {
                        var fi = new FileInfo(f);
                        if (fi.Length > 3 * 1024 * 1024) continue;
                        bytes += fi.Length;
                        string s = File.ReadAllText(f);
                        xmlFiles++;
                        patchOps += CountOf(s, "<Operation");
                        foreach (Marker mk in Markers)
                        {
                            int c = CountOf(s, mk.text);
                            if (c == 0) continue;
                            score.TryGetValue(mk.cat, out int v);
                            score[mk.cat] = v + c * mk.weight;
                        }
                    }
                    catch { }
                }
            }
            int content = score.Values.Sum();
            // what a mod without defs is
            if (content == 0 && !dll)
            {
                if (lang && !tex) return "translations";
                if (tex) return "textures";
                if (lang) return "translations";
            }
            if (Transl.IsMatch(i.name) && content < 10 && !dll) return "translations";
            if (Tex.IsMatch(i.name) && content < 20) return "textures";
            if (Ui.IsMatch(i.name) && content < 30) return "ui";
            string best = null;
            int bestV = 0;
            foreach (string c in BuiltIn)   // fixed order – the same mod always gets the same answer
            {
                if (score.TryGetValue(c, out int v) && v > bestV) { best = c; bestV = v; }
            }
            if (best != null && bestV >= 3) return best;
            if (dll) return "ui";
            if (tex && patchOps == 0) return "textures";
            return "other";
        }

        private static bool HasFiles(string d)
        {
            try { return Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(); } catch { return false; }
        }

        private static int CountOf(string s, string what)
        {
            int n = 0, at = 0;
            while ((at = s.IndexOf(what, at, StringComparison.Ordinal)) >= 0) { n++; at += what.Length; }
            return n;
        }
    }

    // a short name for a new or renamed category
    public class Dialog_CatName : Window
    {
        private string text;
        private readonly Func<string, bool> done;
        private readonly string title;
        private bool focused;

        public Dialog_CatName(string title, string start, Func<string, bool> done)
        {
            this.title = title;
            text = start ?? "";
            this.done = done;
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 170f);

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, r.width, 30f), title);
            GUI.SetNextControlName("MSMCatName");
            text = Widgets.TextField(new Rect(r.x, r.y + 36f, r.width, 32f), text, 40);
            if (!focused) { UI.FocusControl("MSMCatName", this); focused = true; }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (Widgets.ButtonText(new Rect(r.xMax - 120f, r.yMax - 36f, 120f, 34f), "OK".Translate()) || enter)
            {
                if (done(text)) Close();
                else Messages.Message("MSM_CatNameBad".Translate(), MessageTypeDefOf.RejectInput, false);
                if (enter) Event.current.Use();
            }
            if (Widgets.ButtonText(new Rect(r.xMax - 250f, r.yMax - 36f, 120f, 34f), "CancelButton".Translate())) Close();
        }
    }

    // ================= the Categories tab of the window =================
    public partial class Dialog_ModSortManager
    {
        private string catSel = ModCats.All;
        private string catSearch = "";
        private bool catOnlyActive;
        private Vector2 sCatList, sCatMods;
        private const float CatRowH = 34f;
        // the category filter of the inactive list in the load order tab (null = all)
        private string catMain;

        private int catCacheVer = -1, catCacheMods = -1;
        private string catCacheSearch, catCacheSel;
        private bool catCacheOnly;
        private readonly Dictionary<string, int> catInstalled = new Dictionary<string, int>(), catActive = new Dictionary<string, int>();
        private List<ModMetaData> catList = new List<ModMetaData>();

        private IEnumerable<string> CatKeys()
        {
            foreach (string k in ModCats.BuiltIn) yield return k;
            foreach (string c in ModCats.CustomCats) yield return ModCats.Custom + c;
        }

        private List<ModMetaData> Installed() => ModLister.AllInstalledMods.Where(m => !Removal.IsGone(m)).ToList();

        private void CatRecount()
        {
            int stamp = ver * 7919 + ModCats.Version;
            if (catCacheVer == stamp && catCacheSearch == catSearch && catCacheSel == catSel && catCacheOnly == catOnlyActive && catCacheMods == active.Count) return;
            catCacheVer = stamp;
            catCacheSearch = catSearch;
            catCacheSel = catSel;
            catCacheOnly = catOnlyActive;
            catCacheMods = active.Count;
            catInstalled.Clear();
            catActive.Clear();
            var act = new HashSet<ModMetaData>(active);
            List<ModMetaData> all = Installed();
            foreach (ModMetaData m in all)
            {
                string c = ModCats.Of(m) ?? "";
                catInstalled.TryGetValue(c, out int n);
                catInstalled[c] = n + 1;
                if (act.Contains(m)) { catActive.TryGetValue(c, out int a); catActive[c] = a + 1; }
            }
            IEnumerable<ModMetaData> l = catSel == ModCats.All ? all : all.Where(m => ModCats.Of(m) == catSel);
            if (catOnlyActive) l = l.Where(act.Contains);
            catList = Filter(l.OrderByDescending(act.Contains).ThenBy(m => m.Name).ToList(), catSearch);
        }

        private void DrawCats(Rect body)
        {
            if (!ModCats.Started) ModCats.Start();
            CatRecount();
            float leftW = 270f;
            float detailW = Mathf.Max(340f, body.width * 0.28f);
            Rect left = new Rect(body.x, body.y, leftW, body.height);
            Rect mid = new Rect(left.xMax, body.y, body.width - leftW - detailW, body.height);
            Rect right = new Rect(mid.xMax, body.y, detailW, body.height);
            MUI.Fill(new Rect(left.xMax, body.y, 1f, body.height), Line);
            MUI.Fill(new Rect(mid.xMax, body.y, 1f, body.height), Line);

            // ---- categories ----
            Rect l = left.ContractedBy(Pad, 10f);
            Label(new Rect(l.x, l.y, l.width, 22f), "MSM_CatHead".Translate(), MUI.Muted, GameFont.Tiny);
            float bottom = 74f + (ModCats.Busy ? 22f : 0f);
            Rect listR = new Rect(l.x, l.y + 24f, l.width, l.height - 24f - bottom);
            var keys = new List<string> { ModCats.All };
            foreach (string k in CatKeys())
                if (k.StartsWith(ModCats.Custom, StringComparison.Ordinal) || catInstalled.ContainsKey(k) || k == catSel) keys.Add(k);
            Rect view = new Rect(0f, 0f, listR.width - 16f, keys.Count * 30f);
            Widgets.BeginScrollView(listR, ref sCatList, view);
            int nAll = catInstalled.Values.Sum(), nAllAct = catActive.Values.Sum();
            for (int i = 0; i < keys.Count; i++)
            {
                string k = keys[i];
                Rect row = new Rect(0f, i * 30f, view.width, 28f);
                bool sel = k == catSel;
                if (sel) { MUI.Fill(row, MUI.OnBg, 4f); MUI.Frame(row, MUI.Accent, 1f, 4f); }
                else if (Mouse.IsOver(row)) MUI.Fill(row, Hover, 4f);
                int inst = k == ModCats.All ? nAll : (catInstalled.TryGetValue(k, out int a) ? a : 0);
                int act = k == ModCats.All ? nAllAct : (catActive.TryGetValue(k, out int b) ? b : 0);
                string cnt = act + " / " + inst;
                float cw = Text.CalcSize(cnt).x * 0.8f + 10f;
                bool custom = k.StartsWith(ModCats.Custom, StringComparison.Ordinal);
                Label(new Rect(row.x + 8f, row.y, row.width - cw - 12f, row.height), (custom ? "★ " : "") + ModCats.Label(k).Truncate(row.width - cw - 30f), sel ? MUI.Text1 : (inst == 0 ? MUI.Muted : (Color)new Color32(205, 209, 213, 255)), GameFont.Small, TextAnchor.MiddleLeft, false);
                Label(new Rect(row.xMax - cw - 4f, row.y, cw, row.height), cnt, act > 0 ? MUI.Accent : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight, false);
                TooltipHandler.TipRegion(row, "MSM_CatCountTip".Translate(act, inst).ToString() + (custom ? "\n" + "MSM_CatCustomTip".Translate().ToString() : ""));
                Event ev = Event.current;
                if (ev.type == EventType.MouseDown && Mouse.IsOver(row))
                {
                    if (ev.button == 0) { catSel = k; sCatMods = Vector2.zero; }
                    else if (ev.button == 1 && custom) CustomMenu(k.Substring(ModCats.Custom.Length));
                    ev.Use();
                }
            }
            Widgets.EndScrollView();
            float by = l.yMax - bottom + 6f;
            if (ModCats.Busy)
            {
                Label(new Rect(l.x, by, l.width, 20f), "MSM_CatScanning".Translate(ModCats.Done, ModCats.Total), MUI.Warn, GameFont.Tiny);
                by += 22f;
            }
            if (Button(new Rect(l.x, by, l.width, 30f), "MSM_CatNew".Translate()))
                Find.WindowStack.Add(new Dialog_CatName("MSM_CatNewTitle".Translate(), "", n => { bool ok = ModCats.AddCustom(n); if (ok) catSel = ModCats.Custom + n.Trim(); return ok; }));
            Rect rs = new Rect(l.x, by + 36f, l.width, 30f);
            if (Button(rs, "MSM_CatRescan".Translate(), false, !ModCats.Busy)) ModCats.Start(true);
            TooltipHandler.TipRegion(rs, "MSM_CatRescanTip".Translate());

            // ---- the mods of the category ----
            Rect m = mid.ContractedBy(Pad, 10f);
            int shownAct = catList.Count(active.Contains);
            Label(new Rect(m.x, m.y, m.width, 22f), ModCats.Label(catSel).ToUpper() + " · " + "MSM_CatCountTip".Translate(shownAct, catList.Count), MUI.Muted, GameFont.Tiny);
            string ol = "MSM_CatOnlyActive".Translate();
            float ow = Mathf.Min(m.width * 0.4f, Text.CalcSize(ol).x + 24f);
            string sf = SearchField(new Rect(m.x, m.y + 24f, m.width - ow - 6f, 30f), catSearch, "MSM_CatSearch".Translate());
            if (sf != catSearch) { catSearch = sf; sCatMods = Vector2.zero; }
            if (Button(new Rect(m.xMax - ow, m.y + 24f, ow, 30f), ol.Truncate(ow - 8f), catOnlyActive)) catOnlyActive = !catOnlyActive;
            Rect hint = new Rect(m.x, m.yMax - 20f, m.width, 20f);
            Label(hint, "MSM_CatHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            Rect lr = new Rect(m.x, m.y + 62f, m.width, m.height - 62f - 24f);
            if (catList.Count == 0)
                Label(lr, ModCats.Busy ? "MSM_CatScanning".Translate(ModCats.Done, ModCats.Total).ToString() : "MSM_CatEmpty".Translate().ToString(), MUI.Muted, GameFont.Small, TextAnchor.MiddleCenter);
            else DrawCatList(lr);

            DrawDetail(right.ContractedBy(Pad, 10f));
        }

        private void DrawCatList(Rect outRect)
        {
            List<ModMetaData> list = catList;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(list.Count * CatRowH, outRect.height - 2f));
            Widgets.BeginScrollView(outRect, ref sCatMods, view);
            int first = Mathf.Max(0, (int)(sCatMods.y / CatRowH) - 1);
            int last = Mathf.Min(list.Count - 1, (int)((sCatMods.y + outRect.height) / CatRowH) + 1);
            Event ev = Event.current;
            for (int i = first; i <= last; i++)
            {
                ModMetaData mod = list[i];
                bool on = active.Contains(mod);
                Rect row = new Rect(0f, i * CatRowH, view.width, CatRowH);
                if (mod == selected) { MUI.Fill(row, MUI.OnBg); MUI.Frame(row, MUI.Accent); }
                else if (Mouse.IsOver(row)) MUI.Fill(row, Hover);
                else if (i % 2 == 0) MUI.Fill(row, Row1);
                float x = 6f;
                Texture2D icon = null;
                try { icon = mod.Icon; } catch { }
                if (icon != null) GUI.DrawTexture(new Rect(x, row.y + 5f, 24f, 24f), icon, ScaleMode.ScaleToFit);
                x += 32f;
                // on/off switch on the right
                Rect sw = new Rect(row.xMax - 52f, row.y + (CatRowH - 22f) / 2f, 44f, 22f);
                float nameW = sw.x - x - 8f;
                string pill = null;
                if (catSel == ModCats.All || ModCats.IsManual(mod))
                    pill = (ModCats.IsManual(mod) ? "✎ " : "") + ModCats.Label(ModCats.Of(mod));
                if (pill != null)
                {
                    float pw = Mathf.Min(nameW * 0.4f, Text.CalcSize(pill).x * 0.8f + 14f);
                    Rect pr = new Rect(sw.x - 8f - pw, row.y + 8f, pw, 18f);
                    Pill(pr, pill.Truncate(pw - 6f), ModCats.IsManual(mod) ? MUI.Warn : Info);
                    nameW -= pw + 8f;
                }
                if (on) DrawDot(new Rect(x, row.y, 8f, CatRowH), Dot(mod));
                float tx = x + (on ? 14f : 0f);
                Label(new Rect(tx, row.y + 2f, nameW - (tx - x), 18f), mod.Name.Truncate(nameW - (tx - x)), on ? MUI.Text1 : (Color)new Color32(196, 200, 204, 255), GameFont.Small, TextAnchor.MiddleLeft, false);
                string sub = mod.AuthorsString ?? "";
                string note = on ? ShortNote(mod) : (Rules.VersionOk(mod) ? "" : "MSM_OldShort".Translate(MSM.Versions(mod)).ToString());
                if (!note.NullOrEmpty()) sub = note + (sub.NullOrEmpty() ? "" : " · " + sub);
                Label(new Rect(tx, row.y + 17f, nameW - (tx - x), 16f), sub.Truncate(nameW - (tx - x)), note.NullOrEmpty() ? MUI.Muted : (on ? Dot(mod) : MUI.Warn), GameFont.Tiny, TextAnchor.MiddleLeft, false);
                TooltipHandler.TipRegion(row, mod.Name + "\n" + mod.PackageIdPlayerFacing + (mod.AuthorsString.NullOrEmpty() ? "" : "\n" + mod.AuthorsString)
                    + "\n" + "MSM_CatOf".Translate(ModCats.Label(ModCats.Of(mod))).ToString() + (ModCats.IsManual(mod) ? " (" + "MSM_CatManual".Translate().ToString() + ")" : ""));

                bool core = mod.IsCoreMod;
                if (!core)
                {
                    if (MUI.Switch(sw, on))
                    {
                        if (on) Deactivate(mod); else Activate(mod);
                        selected = mod;
                        multi.Clear();
                        SoundDefOf.Click.PlayOneShotOnCamera();
                    }
                }
                if (ev.type == EventType.MouseDown && Mouse.IsOver(row) && !Mouse.IsOver(sw))
                {
                    multi.Clear();
                    selected = mod;
                    if (ev.button == 1) MoveMenu(mod);
                    else if (ev.clickCount == 2)
                    {
                        // to the load order, with the mod in view
                        tab = 0;
                        fInactive = "";
                        catMain = null;
                    }
                    ev.Use();
                }
            }
            Widgets.EndScrollView();
        }

        private void MoveMenu(ModMetaData mod)
        {
            var opts = new List<FloatMenuOption>();
            string cur = ModCats.Of(mod);
            bool manual = ModCats.IsManual(mod);
            string auto = ModCats.Auto(mod);
            opts.Add(new FloatMenuOption((!manual ? "✓ " : "") + "MSM_CatAuto".Translate(ModCats.Label(auto ?? "other")), () => ModCats.Move(mod, null)));
            foreach (string k in CatKeys())
            {
                string kk = k;
                if (kk == "game" && !mod.Official) continue;
                opts.Add(new FloatMenuOption((manual && cur == kk ? "✓ " : "") + ModCats.Label(kk), () => ModCats.Move(mod, kk)));
            }
            opts.Add(new FloatMenuOption("MSM_CatNew".Translate(), () =>
                Find.WindowStack.Add(new Dialog_CatName("MSM_CatNewTitle".Translate(), "", n =>
                {
                    if (!ModCats.AddCustom(n)) return false;
                    ModCats.Move(mod, ModCats.Custom + n.Trim());
                    return true;
                }))));
            Find.WindowStack.Add(new FloatMenu(opts, "MSM_CatMove".Translate()));
        }

        private void CustomMenu(string name)
        {
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("MSM_CatRename".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_CatName("MSM_CatRename".Translate(), name, n =>
                    {
                        bool ok = ModCats.RenameCustom(name, n);
                        if (ok && catSel == ModCats.Custom + name) catSel = ModCats.Custom + n.Trim();
                        if (ok && catMain == ModCats.Custom + name) catMain = ModCats.Custom + n.Trim();
                        return ok;
                    }))),
                new FloatMenuOption("MSM_CatDelete".Translate(), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_CatDeleteConfirm".Translate(name), delegate
                    {
                        ModCats.DeleteCustom(name);
                        if (catSel == ModCats.Custom + name) catSel = ModCats.All;
                        if (catMain == ModCats.Custom + name) catMain = null;
                    }, true)))
            };
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ---- the small category filter above the inactive list (load order tab) ----
        private void CatFilterButton(Rect r)
        {
            string lbl = "MSM_CatFilter".Translate(catMain == null ? "MSM_Cat_all".Translate().ToString() : ModCats.Label(catMain)) + " ▾";
            if (Button(r, lbl.Truncate(r.width - 8f), catMain != null))
            {
                if (!ModCats.Started) ModCats.Start();
                var counts = new Dictionary<string, int>();
                foreach (ModMetaData m in inactiveCache)
                {
                    string c = ModCats.Of(m) ?? "";
                    counts.TryGetValue(c, out int n);
                    counts[c] = n + 1;
                }
                var opts = new List<FloatMenuOption> { new FloatMenuOption("MSM_Cat_all".Translate() + " (" + inactiveCache.Count + ")", () => { catMain = null; ver++; }) };
                foreach (string k in CatKeys())
                {
                    string kk = k;
                    if (!counts.TryGetValue(kk, out int n)) continue;
                    opts.Add(new FloatMenuOption(ModCats.Label(kk) + " (" + n + ")", () => { catMain = kk; sInactive = Vector2.zero; ver++; }));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(r, "MSM_CatFilterTip".Translate());
        }

        private List<ModMetaData> CatFilterInactive(List<ModMetaData> list)
        {
            if (catMain == null) return list;
            return list.Where(m => ModCats.Of(m) == catMain).ToList();
        }
    }
}
