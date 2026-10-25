using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // Stage 2: text that mods build in code (messages, letters, buttons, tooltips, inspect lines)
    // never goes through the language files. It is translated where it is drawn: only on screen,
    // nothing in the game data changes. Known texts come from the memory at once; English texts
    // that are not known yet are collected and translated in the background (numbers become
    // placeholders, so "Harvested 25 rice" and "Harvested 3 rice" are one text).
    public static class Live
    {
        public static volatile bool On;
        public static int Version;                    // bumped when new live translations arrived
        public static int Shown, Queued, Arrived;

        public static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>();

        private struct Entry { public string Out; public int Ver; }
        private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        private static HashSet<string> vocab = new HashSet<string>();
        private static HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static HashSet<string> modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int namesFrame = -100000;
        private static volatile bool arrivedFlag;
        private static float lastBump;
        private static int queuedThisMinute;
        private static float minuteStart;
        private const int MaxPending = 5000, PerMinute = 120, CacheMax = 40000, MaxLen = 600;
        private static string collectorPath;
        private static readonly object fileGate = new object();

        private static readonly Regex Word = new Regex(@"[A-Za-z][A-Za-z']+", RegexOptions.Compiled);
        private static readonly Regex Number = new Regex(@"(?<![A-Za-z0-9])[-+]?\d+(?:[.,:]\d+)*%?", RegexOptions.Compiled);

        private static readonly string[] Common =
        {
            "the","of","and","to","in","is","it","you","that","he","was","for","on","are","as","with","his","they","at","be","this","have",
            "from","or","one","had","by","not","but","what","all","were","we","when","your","can","said","there","use","an","each","which",
            "she","do","how","their","if","will","up","other","about","out","many","then","them","these","so","some","her","would","make",
            "like","him","into","time","has","look","two","more","go","see","no","way","could","my","than","first","been","who","its","now",
            "people","over","down","only","new","after","any","get","here","need","must","cannot","can't","don't","won't","is't","isn't",
            "colonist","colonists","pawn","pawns","enable","disable","enabled","disabled","settings","show","hide","open","close","apply",
            "cancel","reset","select","selected","all","none","add","remove","delete","edit","save","load","yes","ok","item","items",
            "work","priority","needs","need","has","have","too","very","low","high","no","not","available","required","missing","target",
            "click","right","left","drag","toggle","mode","current","default","value","amount","total","per","day","days","hour","hours",
            "while","until","before","after","again","still","already","also","just","even","less","most","more","much","few","every"
        };

        // ---------------- start ----------------
        public static void Init(List<Item> items, string folder, string target)
        {
            var v = new HashSet<string>(Common);
            void AddWords(string s)
            {
                if (string.IsNullOrEmpty(s)) return;
                foreach (Match m in Word.Matches(s)) v.Add(m.Value.ToLowerInvariant());
            }
            try
            {
                foreach (Item it in items) AddWords(it.Source);
                LoadedLanguage en = LanguageDatabase.defaultLanguage;
                if (en != null) foreach (var kv in en.keyedReplacements) AddWords(kv.Value?.value);
                LoadedLanguage active = LanguageDatabase.activeLanguage;
                if (active != null && active != en)
                    foreach (DefInjectionPackage p in active.defInjections)
                        foreach (var kv in p.injections)
                        {
                            AddWords(kv.Value.replacedString);
                            if (kv.Value.replacedList != null) foreach (string s in kv.Value.replacedList) AddWords(s);
                        }
            }
            catch (Exception e)
            {
                MLog.Warn("[URT] live words: " + e.Message);
            }
            vocab = v;
            var mn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                if (!string.IsNullOrEmpty(m.Name)) mn.Add(m.Name.Trim());
            foreach (ModMetaData m in ModLister.AllInstalledMods)
                if (!string.IsNullOrEmpty(m.Name)) mn.Add(m.Name.Trim());
            modNames = mn;
            collectorPath = Path.Combine(folder, "live_" + target + ".txt");
            // texts collected in earlier sessions that still wait for a translation
            int back = 0;
            try
            {
                if (File.Exists(collectorPath))
                    foreach (string line in File.ReadAllLines(collectorPath))
                    {
                        if (Pending.Count >= MaxPending) break;
                        string t = line.Replace("\\n", "\n");
                        if (t.Length > 0 && Runtime.Store.NeedsTranslation(t) && Pending.TryAdd(Store.Key(t), 0)) back++;
                    }
            }
            catch { }
            MLog.Info("[URT] live translation: " + vocab.Count + " English words known, " + back + " texts from earlier sessions waiting");
        }

        public static void Patch(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(Live), nameof(PrefixLabel));
            h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new[] { typeof(Rect), typeof(string) }), prefix: pre);
            h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.LabelEllipses), new[] { typeof(Rect), typeof(string) }), prefix: pre);
            var bw = AccessTools.Method(typeof(Widgets), "ButtonTextWorker");
            if (bw != null) h.Patch(bw, prefix: pre);
            h.Patch(AccessTools.Method(typeof(Text), nameof(Text.CalcHeight), new[] { typeof(string), typeof(float) }), prefix: new HarmonyMethod(typeof(Live), nameof(PrefixText)));
            h.Patch(AccessTools.Method(typeof(Text), nameof(Text.CalcSize), new[] { typeof(string) }), prefix: new HarmonyMethod(typeof(Live), nameof(PrefixText)));
            var tip = AccessTools.PropertyGetter(typeof(ActiveTip), "FinalText");
            if (tip != null) h.Patch(tip, postfix: new HarmonyMethod(typeof(Live), nameof(PostfixTip)));
        }

        public static void PrefixLabel(ref string label) { if (On) label = Tr(label); }
        public static void PrefixText(ref string text) { if (On) text = Tr(text); }
        public static void PostfixTip(ref string __result) { if (On) __result = Tr(__result); }

        // ---------------- the lookup (main thread, every frame – must be cheap) ----------------
        public static string Tr(string s)
        {
            if (s == null || s.Length < 2 || s.Length > MaxLen) return s;
            try
            {
                if (cache.TryGetValue(s, out Entry e) && (e.Out != null || e.Ver == Version))
                    return e.Out ?? s;
                string res = Compute(s);
                if (cache.Count >= CacheMax) cache.Clear();
                cache[s] = new Entry { Out = res, Ver = Version };
                if (res != null) Shown++;
                return res ?? s;
            }
            catch
            {
                return s;
            }
        }

        private static string Compute(string s)
        {
            if (!Candidate(s)) return null;
            string tpl = Template(s, out List<string> nums);
            string t = Runtime.Store?.Use(tpl);
            if (t != null)
                return nums == null ? t : Fill(t, nums);
            Collect(tpl);
            return null;
        }

        // English text worth translating: Latin letters only, mostly known English words, no names
        private static bool Candidate(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c > 127 && char.IsLetter(c)) return false;     // already another language (or a name with accents)
            }
            if (!Protect.HasWords(s) || Protect.LooksLikeNonText(s) || Protect.IsRule(s)) return false;
            RefreshNames();
            string whole = s.Trim();
            if (names.Contains(whole) || modNames.Contains(whole)) return false;     // a name, or a mod's name
            int words = 0, known = 0;
            bool first = true;
            foreach (Match m in Word.Matches(StripTags(s)))
            {
                string w = m.Value;
                words++;
                if (!first && char.IsUpper(w[0]) && names.Contains(w)) return false;    // a colonist, faction or place
                first = false;
                if (vocab.Contains(w.ToLowerInvariant())) known++;
            }
            if (words == 0 || known == 0) return false;

            return known * 10 >= words * 7;
        }

        private static readonly Regex Tag = new Regex(@"</?[A-Za-z][^<>]*>|\{[^{}]*\}|\[[^\[\]]*\]", RegexOptions.Compiled);
        private static string StripTags(string s) => s.IndexOf('<') < 0 && s.IndexOf('{') < 0 && s.IndexOf('[') < 0 ? s : Tag.Replace(s, " ");

        // numbers become {0}, {1} … (only in plain text – tags and placeholders stay as they are)
        private static string Template(string s, out List<string> nums)
        {
            nums = null;
            if (s.IndexOf('{') >= 0 || s.IndexOf('<') >= 0 || s.IndexOf('[') >= 0) return s;
            var found = new List<string>();
            string t = Number.Replace(s, m => { found.Add(m.Value); return "{" + (found.Count - 1) + "}"; });
            if (found.Count == 0 || found.Count > 8) return s;
            nums = found;
            return t;
        }

        private static string Fill(string t, List<string> nums)
        {
            var sb = new StringBuilder(t);
            for (int i = nums.Count - 1; i >= 0; i--) sb.Replace("{" + i + "}", nums[i]);
            return sb.ToString();
        }

        private static void Collect(string tpl)
        {
            string key = Store.Key(tpl);
            if (key.Length == 0 || Pending.ContainsKey(key) || Pending.Count >= MaxPending) return;
            float now = Time.realtimeSinceStartup;
            if (now - minuteStart > 60f) { minuteStart = now; queuedThisMinute = 0; }
            if (queuedThisMinute >= PerMinute) return;
            if (!Runtime.Store.NeedsTranslation(key)) return;
            if (!Pending.TryAdd(key, 0)) return;
            queuedThisMinute++;
            Queued++;
            Runtime.Kick();
            try
            {
                lock (fileGate)
                    if (collectorPath != null && (!File.Exists(collectorPath) || new FileInfo(collectorPath).Length < 4_000_000))
                        File.AppendAllText(collectorPath, key.Replace("\r", "").Replace("\n", "\\n") + "\n");
            }
            catch { }
        }

        // ---------------- from the translator thread ----------------
        public static void Done(string key, bool ok)
        {
            Pending.TryRemove(key, out _);
            if (ok) { Arrived++; arrivedFlag = true; }
        }

        // main thread, every frame: new translations become visible (at most once a second)
        public static void Tick()
        {
            if (!arrivedFlag) return;
            float now = Time.realtimeSinceStartup;
            if (now - lastBump < 1f) return;
            lastBump = now;
            arrivedFlag = false;
            Version++;
        }

        public static void ClearCache() { cache.Clear(); Version++; }

        // names of colonists, factions and places: never translated ("Rose", "Hunter" …)
        private static void RefreshNames()
        {
            int f = Time.frameCount;
            if (f - namesFrame < 2500 && f >= namesFrame) return;
            namesFrame = f;
            var n = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (Current.ProgramState == ProgramState.Playing)
                {
                    foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)
                        AddName(n, p?.Name);
                    foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
                        if (p.Faction != null && p.Faction.IsPlayer) AddName(n, p.Name);
                    foreach (Faction fa in Find.FactionManager.AllFactionsListForReading)
                        AddWords(n, fa.Name);
                    foreach (Settlement st in Find.WorldObjects.Settlements)
                        AddWords(n, st.Name);
                }
            }
            catch { }
            names = n;
        }

        private static void AddName(HashSet<string> n, Name name)
        {
            if (name is NameTriple t) { AddWords(n, t.First); AddWords(n, t.Nick); AddWords(n, t.Last); }
            else if (name != null) AddWords(n, name.ToStringFull);
        }

        private static void AddWords(HashSet<string> n, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            n.Add(s.Trim());
            foreach (Match m in Word.Matches(s)) n.Add(m.Value);
        }
    }
}
