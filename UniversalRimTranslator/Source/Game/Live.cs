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
        public static int Suspended;   // URT's own window
        public static int Version;                    // bumped when new live translations arrived
        public static int Shown, Queued, Arrived;

        public static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>();

        private struct Entry { public string Out; public int Ver; public bool Collected; }
        private static readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();
        private static HashSet<string> vocab = new HashSet<string>();
        private static HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // whole names
        private static HashSet<string> nameWords = new HashSet<string>(StringComparer.Ordinal);            // single words of names
        private static Game namesGame;
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
            // words of the target language (from the translations): "pacient", "mramor" are not English
            // even when an English text somewhere contains them – such words never count as English
            var tv = new HashSet<string>();
            try
            {
                foreach (var kv in Runtime.Store.Snapshot())
                    if (kv.Value.Text != null && kv.Value.Text != kv.Key)
                        foreach (Match m in Word.Matches(kv.Value.Text)) tv.Add(m.Value.ToLowerInvariant());
                LoadedLanguage act = LanguageDatabase.activeLanguage;
                if (act != null && act != LanguageDatabase.defaultLanguage)
                {
                    foreach (var kv in act.keyedReplacements)
                        if (kv.Value?.value != null) foreach (Match m in Word.Matches(kv.Value.value)) tv.Add(m.Value.ToLowerInvariant());
                    foreach (DefInjectionPackage p in act.defInjections)
                        foreach (var kv in p.injections)
                            if (kv.Value.injection != null) foreach (Match m in Word.Matches(kv.Value.injection)) tv.Add(m.Value.ToLowerInvariant());
                }
            }
            catch { }
            // a word both languages use (OK, TPS, names) proves nothing either
            foreach (string w in tv) v.Remove(w);
            vocab = v;
            var mn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
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
                        // 2.2.0: texts the newer checks do not accept (cut, versions, the target language) are dropped
                        if (t.Length > 0 && Candidate(t) && Runtime.Store.NeedsTranslation(t) && tried.Add(Store.Key(t)) && Pending.TryAdd(Store.Key(t), 0)) back++;
                    }
                // the file keeps only what still waits
                File.WriteAllLines(collectorPath, Pending.Keys.Select(k => k.Replace("\r", "").Replace("\n", "\\n")).ToArray());
            }
            catch { }
            BuildLabels();
            MLog.Info("[URT] live translation: " + vocab.Count + " English words known, " + back + " texts from earlier sessions waiting");
            liveLogAt = 0f;
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

        public static void PrefixLabel(ref string label) { if (On && Suspended == 0) label = Tr(label, true); }
        public static void PostfixTip(ref string __result) { if (On && Suspended == 0) __result = Tr(__result, true); }

        // measuring only: the same lookup so the size fits the drawn text, but nothing is collected
        // (the game measures cut pieces "Harvested 25 ri..." and log lines that are never shown)
        public static void PrefixText(ref string text)
        {
            if (!On || Suspended > 0 || text == null || text.EndsWith("...") || text.EndsWith("\u2026")) return;
            text = Tr(text, false);
        }

        // ---------------- the lookup (main thread, every frame – must be cheap) ----------------
        private static Dictionary<string, Entry> old = new Dictionary<string, Entry>();

        public static string Tr(string s, bool collect)
        {
            if (s == null || s.Length < 2 || s.Length > MaxLen) return s;
            try
            {
                // the spaces and line breaks around the text stay as they are
                int a = 0, b = s.Length;
                while (a < b && char.IsWhiteSpace(s[a])) a++;
                while (b > a && char.IsWhiteSpace(s[b - 1])) b--;
                if (b - a < 2) return s;
                string core = a == 0 && b == s.Length ? s : s.Substring(a, b - a);
                // numbers do not matter: "Harvested 25 rice" and "Harvested 3 rice" share one entry
                List<string> nums = null;
                string key = HasDigit(core) ? Template(core, out nums) : core;
                if (!Lookup(key, out Entry e) || (e.Out == null && e.Ver != Version) || (e.Out == null && collect && !e.Collected))
                {
                    e = Compute(core, key, collect, nums == null ? (core.IndexOf('{') >= 0 ? -1 : 0) : nums.Count);
                    Remember(key, e);
                }
                if (e.Out == null) return s;
                string res = nums == null ? e.Out : Fill(e.Out, nums);
                return a == 0 && b == s.Length ? res : s.Substring(0, a) + res + s.Substring(b);
            }
            catch
            {
                return s;
            }
        }

        private static bool Lookup(string key, out Entry e)
        {
            if (cache.TryGetValue(key, out e)) return true;
            if (old.TryGetValue(key, out e)) { cache[key] = e; return true; }
            return false;
        }

        // two generations: a full cache becomes the old one instead of throwing all away
        private static void Remember(string key, Entry e)
        {
            if (cache.Count >= CacheMax / 2)
            {
                old = new Dictionary<string, Entry>(cache);
                cache.Clear();
            }
            cache[key] = e;
        }

        private static bool HasDigit(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] >= '0' && s[i] <= '9') return true;
            return false;
        }

        private static Entry Compute(string core, string key, bool collect, int numCount)
        {
            var e = new Entry { Ver = Version, Collected = collect };
            // 2.5.1: text in another script than the game language (a Chinese quest from a mod's
            // generator, Russian names …): drawn translated when the memory knows it, otherwise collected
            if (ForeignScript(core))
            {
                string ft = Runtime.Store?.Use(key);
                if (ft != null)
                {
                    e.Out = ft;
                    Shown++;
                    return e;
                }
                if (collect) Collect(key);
                return e;
            }
            if (!Candidate(core))
            {
                // 2.4.0: half-translated texts – "Mine kus břidlice": the translated names of things
                // become placeholders, the English rest is translated ("Těžit {0}") and the names go back
                if (numCount >= 0 && HasForeign(core) && Mixed(key, numCount, out string tpl, out List<string> parts))
                {
                    string mt = Runtime.Store?.Use(tpl);
                    if (mt != null)
                    {
                        var sb = new StringBuilder(mt);
                        for (int i = parts.Count - 1; i >= 0; i--) sb.Replace("{" + (numCount + i) + "}", parts[i]);
                        e.Out = sb.ToString();
                        Shown++;
                        return e;
                    }
                    if (collect) Collect(tpl);
                    return e;
                }
                e.Collected = true;
                return e;      // never worth collecting
            }
            string t = Runtime.Store?.Use(key);
            if (t != null)
            {
                e.Out = t;
                Shown++;
                return e;
            }
            if (collect) Collect(key);
            return e;
        }

        // most of the letters are in a script the game language does not use
        private static bool ForeignScript(string s)
        {
            string sl = Lang.GuessFromScript(s);
            if (sl == null) return false;
            string t = Runtime.Target ?? "";
            if (sl == "zh-Hans") return !t.StartsWith("zh");
            if (sl == "ru") return !(t == "ru" || t == "uk" || t == "be" || t == "bg" || t == "mk" || t == "sr" || t == "kk");
            return !t.StartsWith(sl);
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
            if (names.Contains(s) || modNames.Contains(s)) return false;     // a name, or a mod's name
            // cut by the game to fit ("Mod Sort Manage..."), versions ("Harmony v2.3"), hashes
            if (s.EndsWith("...") || s.EndsWith("\u2026") || Junk.IsMatch(s)) return false;
            int words = 0, known = 0;
            foreach (Match m in Word.Matches(StripTags(s)))
            {
                string w = m.Value;
                words++;
                if (char.IsUpper(w[0]) && nameWords.Contains(w)) return false;    // a colonist, faction or place (also first)
                if (vocab.Contains(w.ToLowerInvariant())) known++;
            }
            if (words == 0 || known == 0) return false;
            return known * 10 >= words * 7;
        }

        // ---------------- 2.4.0: half-translated texts ----------------
        private static HashSet<string> labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static int labelsMaxWords = 1;
        private static int labelsStoreCount = -1;
        private static float labelsAt;
        private static readonly Regex AnyWord = new Regex(@"[\p{L}][\p{L}'\-]*", RegexOptions.Compiled);

        private static bool HasForeign(string s)
        {
            for (int i = 0; i < s.Length; i++) if (s[i] > 127 && char.IsLetter(s[i])) return true;
            return false;
        }

        private static bool AllEnglish(string s)
        {
            int n = 0;
            foreach (Match m in Word.Matches(s)) { n++; if (!vocab.Contains(m.Value.ToLowerInvariant())) return false; }
            return n > 0;
        }

        private static void AddLabel(HashSet<string> set, string s)
        {
            if (string.IsNullOrEmpty(s)) return;
            s = s.Trim();
            if (s.Length < 3 || s.Length > 60 || s.IndexOf('{') >= 0 || s.IndexOf('<') >= 0) return;
            // a label that is plain English proves nothing ("OK", "steel" in both)
            if (!HasForeign(s) && AllEnglish(s)) return;
            set.Add(s);
        }

        // labels of the game's things in the target language (defs after translation, the translation memory)
        public static void BuildLabels()
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                foreach (Type type in GenDefDatabase.AllDefTypesWithDatabases())
                    foreach (Def d in GenDefDatabase.GetAllDefsInDatabaseForDef(type))
                    {
                        AddLabel(set, d.label);
                        if (d is ThingDef td) AddLabel(set, td.stuffProps?.stuffAdjective);
                        if (d is PawnKindDef pk) AddLabel(set, pk.labelPlural);
                    }
                foreach (var kv in Runtime.Store.Snapshot())
                {
                    string tr = kv.Value.Text;
                    if (tr != null && tr != kv.Key && kv.Key.Length <= 40 && kv.Key.IndexOf(' ') == kv.Key.LastIndexOf(' ')) AddLabel(set, tr);
                }
                labelsStoreCount = Runtime.Store.Count;
            }
            catch (Exception e) { MLog.Warn("[URT] live labels: " + e.Message); }
            int max = 1;
            foreach (string s in set) { int w = AnyWord.Matches(s).Count; if (w > max) max = w; }
            labelsMaxWords = Math.Min(max, 6);
            labels = set;
            labelsBusy = false;
        }
        private static volatile bool labelsBusy;

        // replaces known translated labels by {start}, {start+1} …; true when an English text is left
        private static bool Mixed(string s, int start, out string tpl, out List<string> parts)
        {
            tpl = null;
            parts = null;
            if (labels.Count == 0) return false;
            MatchCollection ws = AnyWord.Matches(s);
            if (ws.Count < 2 || ws.Count > 80) return false;
            var sb = new StringBuilder();
            var found = new List<string>();
            int pos = 0, i = 0;
            while (i < ws.Count)
            {
                int best = -1;
                for (int n = Math.Min(labelsMaxWords, ws.Count - i); n >= 1; n--)
                {
                    Match a = ws[i], b = ws[i + n - 1];
                    string piece = s.Substring(a.Index, b.Index + b.Length - a.Index);
                    if (labels.Contains(piece)) { best = n; break; }
                }
                if (best < 0) { i++; continue; }
                Match first = ws[i], last = ws[i + best - 1];
                sb.Append(s, pos, first.Index - pos);
                sb.Append('{').Append(start + found.Count).Append('}');
                found.Add(s.Substring(first.Index, last.Index + last.Length - first.Index));
                pos = last.Index + last.Length;
                i += best;
                if (found.Count > 6) return false;
            }
            if (found.Count == 0) return false;
            sb.Append(s, pos, s.Length - pos);
            string t = sb.ToString();
            if (HasForeign(t) || !Candidate(t)) return false;
            tpl = t;
            parts = found;
            return true;
        }

        private static readonly Regex Junk = new Regex(@"\bv\d+(\.\d+)+|[0-9A-Fa-f]{16,}|\bSHA-?\d", RegexOptions.Compiled);

        private static readonly Regex Tag = new Regex(@"</?[A-Za-z][^<>]*>|\{[^{}]*\}|\[[^\[\]]*\]", RegexOptions.Compiled);
        private static string StripTags(string s) => s.IndexOf('<') < 0 && s.IndexOf('{') < 0 && s.IndexOf('[') < 0 ? s : Tag.Replace(s, " ");

        // numbers become {0}, {1} … (not in texts with own placeholders – those keep their braces)
        private static string Template(string s, out List<string> nums)
        {
            nums = null;
            if (s.IndexOf('{') >= 0) return s;
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

        private static readonly HashSet<string> tried = new HashSet<string>();
        private static readonly ConcurrentQueue<string> toFile = new ConcurrentQueue<string>();

        private static void Collect(string tpl)
        {
            // only in a running game: names of colonists and places are known only then
            if (Current.ProgramState != ProgramState.Playing) return;
            string key = Store.Key(tpl);
            if (key.Length == 0 || Pending.ContainsKey(key) || Pending.Count >= MaxPending) return;
            float now = Time.realtimeSinceStartup;
            if (now - minuteStart > 60f) { minuteStart = now; queuedThisMinute = 0; }
            if (queuedThisMinute >= PerMinute) return;
            if (!Runtime.Store.NeedsTranslation(key)) return;
            // each text once per session: one the services could not translate is not asked again
            if (!tried.Add(key)) return;
            if (!Pending.TryAdd(key, 0)) return;
            queuedThisMinute++;
            Queued++;
            toFile.Enqueue(key);
            Runtime.Kick();
        }

        // translator thread: the collected texts go to the file (no disk work while drawing)
        public static void Flush()
        {
            if (toFile.IsEmpty || collectorPath == null) return;
            var sb = new StringBuilder();
            while (toFile.TryDequeue(out string k)) sb.Append(k.Replace("\r", "").Replace("\n", "\\n")).Append('\n');
            try
            {
                lock (fileGate)
                {
                    if (File.Exists(collectorPath) && new FileInfo(collectorPath).Length > 4_000_000)
                    {
                        // keep the newer half
                        string[] lines = File.ReadAllLines(collectorPath);
                        File.WriteAllLines(collectorPath, lines.Skip(lines.Length / 2).ToArray());
                    }
                    File.AppendAllText(collectorPath, sb.ToString());
                }
            }
            catch { }
        }

        // translator thread: texts that do not need a translation any more leave the queue
        public static void Prune(Func<string, bool> stillWanted)
        {
            foreach (string k in Pending.Keys.ToList())
                if (!stillWanted(k)) Pending.TryRemove(k, out _);
        }

        // ---------------- from the translator thread ----------------
        public static void Done(string key, bool ok)
        {
            Pending.TryRemove(key, out _);
            if (ok) { System.Threading.Interlocked.Increment(ref Arrived); arrivedFlag = true; }
        }

        // main thread, every frame: new translations become visible (at most once a second)
        private static float liveLogAt;

        public static void Tick()
        {
            // a line in the log every 10 minutes while live translation works
            float rt = Time.realtimeSinceStartup;
            if (On && rt - liveLogAt > 600f)
            {
                if (liveLogAt > 0f) MLog.Info("[URT] live: shown translated " + Shown + ", collected " + Queued + ", waiting " + Pending.Count + ", arrived " + Arrived);
                liveLogAt = rt;
            }
            if (On) RefreshNames();
            // new translations of names of things: the label list follows (at most once a minute)
            if (On && !labelsBusy && rt - labelsAt > 60f && Runtime.Store != null && Runtime.Store.Count != labelsStoreCount)
            {
                labelsAt = rt;
                labelsBusy = true;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => { try { BuildLabels(); } finally { labelsBusy = false; } });
            }
            if (!arrivedFlag) return;
            float now = Time.realtimeSinceStartup;
            if (now - lastBump < 1f) return;
            lastBump = now;
            arrivedFlag = false;
            Version++;
        }

        public static void ClearCache() { cache.Clear(); old.Clear(); Version++; }

        // 2.4.0: for the quality check
        private static readonly HashSet<string> commonSet = new HashSet<string>(Common);
        public static bool IsEnglishWord(string w)
        {
            string l = w.ToLowerInvariant();
            return commonSet.Contains(l) || vocab.Contains(l);
        }

        // whole names (colonists, factions, places) and mod names – never translated
        public static List<string> WholeNames()
        {
            var l = new List<string>();
            foreach (string s in names) if (s.IndexOf(' ') > 0 && s.Length >= 5) l.Add(s);
            foreach (string s in modNames) if (s.Length >= 5) l.Add(s);
            return l;
        }

        // a text the player wants translated again: back into the queue even if it was tried this session
        public static void Requeue(string source)
        {
            string key = Store.Key(source);
            if (key.Length == 0) return;
            lock (tried) tried.Remove(key);
            cache.Remove(key);
            old.Remove(key);
            if (Pending.Count < MaxPending && Pending.TryAdd(key, 0)) toFile.Enqueue(key);
            Runtime.Kick();
        }

        // names of colonists, factions and places: never translated ("Rose", "Hunter" …)
        private static void RefreshNames()
        {
            int f = Time.frameCount;
            bool newGame = Current.Game != namesGame;
            if (!newGame && f - namesFrame < 2500 && f >= namesFrame) return;
            namesFrame = f;
            namesGame = Current.Game;
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
            // 2.5.2: race and xenotype names of mods are names too
            try
            {
                if (!UniversalRimTranslatorMod.Settings.translateRaceNames)
                    foreach (string rn in Runtime.ProperNames) AddWords(n, rn);
            }
            catch { }
            var w = new HashSet<string>(StringComparer.Ordinal);
            foreach (string s in n)
                foreach (Match m in Word.Matches(s))
                    if (char.IsUpper(m.Value[0])) w.Add(m.Value);
            names = n;
            nameWords = w;
            if (newGame) ClearCache();
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
