using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Urt2
{
    // 1.2.2: texts the game wrote once and keeps (quest letters, quest descriptions, messages saved in the
    // game) were made from a def text with [tokens] BEFORE its translation existed – e.g. a quest generated
    // in English stays English in the save forever. When such a finished text is shown, it is matched
    // against the known def texts with [tokens] ("The quest [resolvedQuestName] has expired.") and the
    // translation is filled with the same parts – no service is asked, one translation serves all variants.
    public sealed class Templates
    {
        private static readonly Regex TokenRx = new Regex(@"\[([A-Za-z_][A-Za-z0-9_]*)\]", RegexOptions.Compiled);
        private const int KeyLen = 12;

        private sealed class Tpl
        {
            public string Source;
            public string Translation;
            public string[] Literals;   // n+1 literals around n tokens
            public string[] Names;      // n token names
            public Regex Rx;
        }

        private readonly Dictionary<string, List<Tpl>> byStart = new Dictionary<string, List<Tpl>>(StringComparer.Ordinal);
        public int Count { get; private set; }

        // pairs: source text -> its accepted translation
        public static Templates Build(IEnumerable<KeyValuePair<string, string>> pairs)
        {
            var t = new Templates();
            foreach (var kv in pairs) t.Add(kv.Key, kv.Value);
            return t;
        }

        public bool Add(string source, string translation)
        {
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(translation)) return false;
            if (source.Length < 20 || source.Length > 800 || source.IndexOf('[') < 0) return false;
            if (Protect.IsRule(source)) return false;
            MatchCollection ms = TokenRx.Matches(source);
            if (ms.Count == 0 || ms.Count > 6) return false;
            var lits = new List<string>();
            var names = new List<string>();
            int pos = 0;
            foreach (Match m in ms)
            {
                lits.Add(source.Substring(pos, m.Index - pos));
                names.Add(m.Groups[1].Value);
                pos = m.Index + m.Length;
            }
            lits.Add(source.Substring(pos));
            // the text must start with words (that is where it is found) and two tokens may not touch
            if (lits[0].Length < KeyLen) return false;
            for (int i = 1; i < lits.Count - 1; i++) if (lits[i].Length == 0) return false;
            // the translation must keep every token
            foreach (string n in names) if (translation.IndexOf("[" + n + "]", StringComparison.Ordinal) < 0) return false;
            var tpl = new Tpl { Source = source, Translation = translation, Literals = lits.ToArray(), Names = names.ToArray() };
            string key = lits[0].Substring(0, KeyLen);
            if (!byStart.TryGetValue(key, out List<Tpl> l)) byStart[key] = l = new List<Tpl>();
            l.Add(tpl);
            Count++;
            return true;
        }

        // the finished text → its translation, or null; part(x) translates one filled-in part (null = keep it)
        public string Fill(string text, Func<string, string> part)
        {
            if (string.IsNullOrEmpty(text) || text.Length < KeyLen) return null;
            if (!byStart.TryGetValue(text.Substring(0, KeyLen), out List<Tpl> l)) return null;
            foreach (Tpl t in l)
            {
                if (!text.StartsWith(t.Literals[0], StringComparison.Ordinal)) continue;
                string last = t.Literals[t.Literals.Length - 1];
                if (last.Length > 0 && !text.EndsWith(last, StringComparison.Ordinal)) continue;
                if (t.Rx == null)
                {
                    var sb = new StringBuilder("^");
                    for (int i = 0; i < t.Literals.Length; i++)
                    {
                        sb.Append(Regex.Escape(t.Literals[i]));
                        if (i < t.Names.Length) sb.Append("(.+?)");
                    }
                    sb.Append('$');
                    t.Rx = new Regex(sb.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant);
                }
                Match m = t.Rx.Match(text);
                if (!m.Success) continue;
                var values = new Dictionary<string, string>(StringComparer.Ordinal);
                bool ok = true;
                for (int i = 0; i < t.Names.Length; i++)
                {
                    string v = m.Groups[i + 1].Value;
                    if (values.TryGetValue(t.Names[i], out string had)) { if (had != v) { ok = false; break; } continue; }
                    values[t.Names[i]] = v;
                }
                if (!ok) continue;
                string res = t.Translation;
                foreach (var kv in values)
                {
                    string v = kv.Value;
                    string tr = part?.Invoke(v);
                    res = res.Replace("[" + kv.Key + "]", string.IsNullOrEmpty(tr) ? v : tr);
                }
                return res;
            }
            return null;
        }
    }
}
