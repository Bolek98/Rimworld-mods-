using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Xml;

namespace Urt2
{
    [Flags]
    public enum RecFlags : byte
    {
        None = 0,
        Manual = 1,          // written or fixed by the player – never replaced automatically
        Retranslate = 2,     // came from the old cache and failed the check – translate again
        FromOldCache = 4
    }

    public sealed class Rec
    {
        public string Text;          // the translation in use
        public string Auto;          // the machine translation (kept when the player edits Text)
        public string Provider = "";
        public RecFlags Flags;
        public Verdict Verdict;
        public long Time;            // UTC ticks

        public bool Manual => (Flags & RecFlags.Manual) != 0;
        public bool Usable => !string.IsNullOrEmpty(Text) && (Manual ? Verdict != Verdict.BrokenRule && Verdict != Verdict.BrokenTokens : Verdict == Verdict.Ok);
    }

    // Translation memory for ONE target language: source text -> translation.
    // The same text in two mods is translated once. Saved as a compact binary file plus an
    // append-only journal, so new translations are on disk at once without rewriting everything.
    public sealed class Store
    {
        public readonly string Target;     // ISO code
        private readonly string folder;
        private readonly Dictionary<string, Rec> map = new Dictionary<string, Rec>(StringComparer.Ordinal);
        private readonly object gate = new object();
        private int journalCount;
        public int Version { get; private set; }
        public Action<string> Log = s => { };

        private const string Magic = "URT2MEM1";

        public Store(string folder, string targetIso)
        {
            this.folder = folder;
            Target = targetIso;
        }

        private string MainPath => Path.Combine(folder, "memory_" + Safe(Target) + ".bin");
        private string JournalPath => Path.Combine(folder, "memory_" + Safe(Target) + ".journal");
        private static string Safe(string s) => s.Replace('/', '_').Replace('\\', '_');

        public int Count { get { lock (gate) return map.Count; } }

        public static string Key(string source) => Protect.Normalize(source) ?? "";

        public bool TryGet(string source, out Rec r)
        {
            lock (gate) return map.TryGetValue(Key(source), out r);
        }

        public Glossary Glossary;     // the player's own words, used on every translation

        public string Use(string source)
        {
            string t;
            lock (gate) t = map.TryGetValue(Key(source), out Rec r) && r.Usable ? r.Text : null;
            return t == null || Glossary == null ? t : Glossary.Apply(t);
        }

        public List<KeyValuePair<string, Rec>> Snapshot()
        {
            lock (gate) return new List<KeyValuePair<string, Rec>>(map);
        }

        // machine translation arrived
        public void PutAuto(string source, string text, string provider, Verdict v)
        {
            string k = Key(source);
            if (k.Length == 0) return;
            Rec r;
            lock (gate)
            {
                if (map.TryGetValue(k, out r) && r.Manual)
                {
                    r.Auto = text;      // keep the player's text, remember the new machine one
                }
                else
                {
                    r = new Rec { Text = text, Auto = text, Provider = provider ?? "", Verdict = v, Flags = RecFlags.None, Time = DateTime.UtcNow.Ticks };
                    map[k] = r;
                }
                Version++;
            }
            Append(k, r);
        }

        // the player wrote or fixed a translation (null/empty = remove the manual text)
        public void PutManual(string source, string text)
        {
            string k = Key(source);
            if (k.Length == 0) return;
            Rec r;
            lock (gate)
            {
                map.TryGetValue(k, out r);
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (r == null) return;
                    r.Flags &= ~RecFlags.Manual;
                    r.Text = r.Auto;
                    r.Verdict = r.Auto == null ? Verdict.Empty : Validate.Check(k, r.Auto, Target);
                }
                else
                {
                    if (r == null) { r = new Rec(); map[k] = r; }
                    r.Text = text;
                    r.Flags |= RecFlags.Manual;
                    r.Flags &= ~RecFlags.Retranslate;
                    r.Verdict = Validate.Check(k, text, Target);
                    if (r.Verdict != Verdict.BrokenRule && r.Verdict != Verdict.BrokenTokens) r.Verdict = Verdict.Ok;
                    r.Time = DateTime.UtcNow.Ticks;
                }
                Version++;
            }
            Append(k, r);
        }

        public void Forget(string source)
        {
            string k = Key(source);
            lock (gate)
            {
                if (!map.Remove(k)) return;
                Version++;
            }
            Append(k, null);
        }

        // 2.6.0: a translation taken from another translator mod or a shared pack. The player's own
        // translations win and a working translation is never replaced; the text must pass the check.
        // Returns 1 = taken, 0 = already translated (or manual), -1 = failed the check.
        public int PutImported(string source, string text, string provider)
        {
            string k = Key(source);
            string t = Protect.Normalize(text);
            if (k.Length == 0 || string.IsNullOrWhiteSpace(t)) return -1;
            if (Protect.LooksLikeNonText(k)) return -1;
            Verdict v = Validate.Check(k, t, Target);
            if (v != Verdict.Ok) return -1;
            Rec r;
            lock (gate)
            {
                if (map.TryGetValue(k, out Rec have) && (have.Manual || (have.Verdict == Verdict.Ok && !string.IsNullOrEmpty(have.Text) && (have.Flags & RecFlags.Retranslate) == 0)))
                    return 0;
                r = new Rec { Text = t, Auto = t, Provider = provider ?? "import", Verdict = Verdict.Ok, Flags = RecFlags.None, Time = DateTime.UtcNow.Ticks };
                map[k] = r;
                Version++;
            }
            Append(k, r);
            return 1;
        }

        // 2.6.0: "translate everything now" – machine translations that failed the checks are
        // forgotten so they are translated again (the player's own translations stay)
        public int ForgetFailedAuto()
        {
            int n = 0;
            try
            {
                Directory.CreateDirectory(folder);
                lock (gate)
                {
                    var drop = new List<string>();
                    foreach (var kv in map)
                    {
                        Rec r = kv.Value;
                        if (r == null || r.Manual) continue;
                        if (r.Verdict != Verdict.Ok || string.IsNullOrEmpty(r.Text)) drop.Add(kv.Key);
                    }
                    if (drop.Count == 0) return 0;
                    foreach (string k in drop) map.Remove(k);
                    Version++;
                    n = drop.Count;
                    // one journal write for all of them (tens of thousands of single writes would stall the game)
                    using (var fs = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var bw = new BinaryWriter(new BufferedStream(fs, 1 << 16), Encoding.UTF8))
                        foreach (string k in drop) Write(bw, k, null);
                    journalCount += drop.Count;
                }
            }
            catch (Exception e)
            {
                Log("memory journal write failed: " + e.Message);
            }
            return n;
        }

        // 2.6.0: how many machine translations failed the checks (kept, but not used)
        public int CountFailedAuto()
        {
            int n = 0;
            lock (gate)
                foreach (var kv in map)
                {
                    Rec r = kv.Value;
                    if (r != null && !r.Manual && (r.Verdict != Verdict.Ok || string.IsNullOrEmpty(r.Text))) n++;
                }
            return n;
        }

        // 1.1.0: machine translations that are still English (the first service gave the text back)
        // go back into the queue – now the other services get a chance too. Returns how many.
        public int FlagStillEnglish(Func<string, string, bool> stillEnglish)
        {
            var hit = new List<KeyValuePair<string, Rec>>();
            lock (gate)
                foreach (var kv in map)
                {
                    Rec r = kv.Value;
                    if (r == null || r.Manual || r.Verdict != Verdict.Ok || string.IsNullOrEmpty(r.Text) || (r.Flags & RecFlags.Retranslate) != 0) continue;
                    bool bad;
                    try { bad = stillEnglish(kv.Key, r.Text); } catch { bad = false; }
                    if (!bad) continue;
                    r.Flags |= RecFlags.Retranslate;
                    hit.Add(kv);
                }
            foreach (var kv in hit) Append(kv.Key, kv.Value);
            if (hit.Count > 0) Version++;
            return hit.Count;
        }

        // texts that need a (new) machine translation
        public bool NeedsTranslation(string source)
        {
            lock (gate)
            {
                if (!map.TryGetValue(Key(source), out Rec r)) return true;
                if (r.Manual) return false;
                return (r.Flags & RecFlags.Retranslate) != 0 || r.Verdict != Verdict.Ok;
            }
        }

        // ---------------- disk ----------------
        public void Load()
        {
            lock (gate)
            {
                map.Clear();
                if (File.Exists(MainPath))
                    ReadFile(MainPath, journal: false);
                if (File.Exists(JournalPath))
                    journalCount = ReadFile(JournalPath, journal: true);
                Version++;
            }
        }

        private int ReadFile(string path, bool journal)
        {
            int n = 0;
            try
            {
                using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var br = new BinaryReader(fs, Encoding.UTF8))
                {
                    if (!journal)
                    {
                        if (br.ReadString() != Magic) { Log("memory file has an unknown format: " + path); return 0; }
                        br.ReadString();    // target
                        br.ReadInt32();     // count (informative)
                    }
                    while (fs.Position < fs.Length)
                    {
                        long start = fs.Position;
                        try
                        {
                            string k = br.ReadString();
                            bool present = br.ReadBoolean();
                            if (!present) { map.Remove(k); n++; continue; }
                            var r = new Rec
                            {
                                Text = Null(br.ReadString()),
                                Auto = Null(br.ReadString()),
                                Provider = br.ReadString(),
                                Flags = (RecFlags)br.ReadByte(),
                                Verdict = (Verdict)br.ReadByte(),
                                Time = br.ReadInt64()
                            };
                            map[k] = r;
                            n++;
                        }
                        catch (EndOfStreamException)
                        {
                            // a record cut off by a crash at the end of the journal – drop it
                            Log("dropped a half-written record at " + start + " in " + Path.GetFileName(path));
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log("memory read failed (" + Path.GetFileName(path) + "): " + e.Message);
            }
            return n;
        }

        private static string Null(string s) => s.Length == 0 ? null : s;

        private static void Write(BinaryWriter bw, string k, Rec r)
        {
            bw.Write(k);
            bw.Write(r != null);
            if (r == null) return;
            bw.Write(r.Text ?? "");
            bw.Write(r.Auto ?? "");
            bw.Write(r.Provider ?? "");
            bw.Write((byte)r.Flags);
            bw.Write((byte)r.Verdict);
            bw.Write(r.Time);
        }

        private void Append(string k, Rec r)
        {
            try
            {
                Directory.CreateDirectory(folder);
                lock (gate)
                {
                    using (var fs = new FileStream(JournalPath, FileMode.Append, FileAccess.Write, FileShare.Read))
                    using (var bw = new BinaryWriter(fs, Encoding.UTF8))
                        Write(bw, k, r);
                    journalCount++;
                }
            }
            catch (Exception e)
            {
                Log("memory journal write failed: " + e.Message);
            }
        }

        public bool JournalBig => journalCount > 5000;

        // rewrite the main file and empty the journal (safe: new file first, then swap)
        public void Compact()
        {
            List<KeyValuePair<string, Rec>> all;
            lock (gate) all = new List<KeyValuePair<string, Rec>>(map);
            string tmp = MainPath + ".tmp";
            try
            {
                Directory.CreateDirectory(folder);
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(new BufferedStream(fs, 1 << 16), Encoding.UTF8))
                {
                    bw.Write(Magic);
                    bw.Write(Target);
                    bw.Write(all.Count);
                    foreach (var kv in all) Write(bw, kv.Key, kv.Value);
                }
                lock (gate)
                {
                    if (File.Exists(MainPath)) File.Replace(tmp, MainPath, MainPath + ".bak");
                    else File.Move(tmp, MainPath);
                    // anything appended after the snapshot is written again into a fresh journal
                    var later = new List<KeyValuePair<string, Rec>>();
                    var snap = new HashSet<string>();
                    foreach (var kv in all) snap.Add(kv.Key);
                    foreach (var kv in map) if (!snap.Contains(kv.Key)) later.Add(kv);
                    File.Delete(JournalPath);
                    journalCount = 0;
                    foreach (var kv in later) Append(kv.Key, kv.Value);
                }
            }
            catch (Exception e)
            {
                Log("memory save failed: " + e.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
        }

        // ---------------- taking over 0.9.x ----------------
        public sealed class ImportReport
        {
            public int Read, Taken, Manual, Broken, Untranslated, OtherLanguage, Skipped;
            public readonly Dictionary<Verdict, int> ByVerdict = new Dictionary<Verdict, int>();
            public override string ToString()
            {
                var sb = new StringBuilder();
                sb.Append("read " + Read + ", taken " + Taken + " (manual " + Manual + "), to translate again " + Broken + ", other languages " + OtherLanguage + ", skipped " + Skipped);
                foreach (var kv in ByVerdict) sb.Append("; " + kv.Key + " " + kv.Value);
                return sb.ToString();
            }
        }

        // Reads UniversalRimTranslatorCache(.live).xml of 0.9.x (streaming – the file can be 100+ MB).
        // Every translation is checked; broken ones are kept only as "translate again".
        public ImportReport ImportOldCache(string path, ImportReport rep = null)
        {
            rep = rep ?? new ImportReport();
            if (!File.Exists(path)) return rep;
            var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Ignore };
            using (XmlReader x = XmlReader.Create(path, settings))
            {
                var f = new Dictionary<string, string>();
                while (x.Read())
                {
                    if (x.NodeType != XmlNodeType.Element || x.Name != "Entry") continue;
                    f.Clear();
                    using (XmlReader e = x.ReadSubtree())
                    {
                        e.Read();   // <Entry>
                        e.Read();
                        while (!e.EOF)
                        {
                            if (e.NodeType == XmlNodeType.Element && e.Depth == 1)
                            {
                                string name = e.Name;
                                f[name] = e.ReadElementContentAsString();
                            }
                            else
                                e.Read();
                        }
                    }
                    rep.Read++;
                    TakeOld(f, rep);
                }
            }
            return rep;
        }

        private void TakeOld(Dictionary<string, string> f, ImportReport rep)
        {
            f.TryGetValue("TargetLanguage", out string tl);
            if (!Lang.Same(tl, Target)) { rep.OtherLanguage++; return; }
            f.TryGetValue("OriginalSourceText", out string orig);
            f.TryGetValue("SourceText", out string src);
            string source = !string.IsNullOrWhiteSpace(orig) ? orig : src;
            f.TryGetValue("Translation", out string text);
            f.TryGetValue("AutomaticTranslation", out string auto);
            if (string.IsNullOrWhiteSpace(source) || (string.IsNullOrWhiteSpace(text) && string.IsNullOrWhiteSpace(auto))) { rep.Skipped++; return; }
            if (Protect.LooksLikeNonText(source)) { rep.Skipped++; return; }
            string k = Key(source);
            bool manual = !string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(auto) && Protect.Normalize(text) != Protect.Normalize(auto);
            string use = Protect.Normalize(!string.IsNullOrWhiteSpace(text) ? text : auto);
            Verdict v = Validate.Check(k, use, Target);
            rep.ByVerdict.TryGetValue(v, out int c);
            rep.ByVerdict[v] = c + 1;
            lock (gate)
            {
                if (map.TryGetValue(k, out Rec have) && (have.Manual || (have.Verdict == Verdict.Ok && !manual))) return;
                var r = new Rec
                {
                    Text = use,
                    Auto = Protect.Normalize(auto) ?? use,
                    Provider = "0.9",
                    Verdict = manual && v != Verdict.BrokenRule && v != Verdict.BrokenTokens ? Verdict.Ok : v,
                    Flags = RecFlags.FromOldCache | (manual ? RecFlags.Manual : 0),
                    Time = DateTime.UtcNow.Ticks
                };
                if (r.Verdict != Verdict.Ok)
                {
                    r.Flags |= RecFlags.Retranslate;
                    rep.Broken++;
                }
                else
                {
                    rep.Taken++;
                    if (manual) rep.Manual++;
                }
                map[k] = r;
            }
        }

        // ManualOverrides.txt of 0.9.x: "source<TAB>translation" with \t \n escaped
        public int ImportOldManual(string path)
        {
            if (!File.Exists(path)) return 0;
            int n = 0;
            foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
            {
                int i = line.IndexOf('\t');
                if (i <= 0) continue;
                string s = line.Substring(0, i).Replace("\\t", "\t").Replace("\\n", "\n");
                string t = line.Substring(i + 1).Replace("\\t", "\t").Replace("\\n", "\n");
                if (string.IsNullOrWhiteSpace(t)) continue;
                lock (gate)
                {
                    map.TryGetValue(Key(s), out Rec r);
                    if (r == null) { r = new Rec(); map[Key(s)] = r; }
                    r.Text = Protect.Normalize(t);
                    r.Flags = RecFlags.Manual | RecFlags.FromOldCache;
                    Verdict v = Validate.Check(Key(s), r.Text, Target);
                    r.Verdict = v == Verdict.BrokenRule || v == Verdict.BrokenTokens ? v : Verdict.Ok;
                    r.Time = DateTime.UtcNow.Ticks;
                }
                n++;
            }
            return n;
        }
    }
}
