using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Urt2
{
    // The player's own words: "kolonista" → "osadník" is used in every translation (machine and
    // manual), whole words only, the first letter's case kept. Kept per target language in
    // glossary_<iso>.txt (one "word<TAB>replacement" per line).
    public sealed class Glossary
    {
        private readonly string path;
        private List<KeyValuePair<string, string>> pairs = new List<KeyValuePair<string, string>>();
        private Regex rx;
        private Dictionary<string, string> map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public int Version { get; private set; }

        public Glossary(string folder, string targetIso)
        {
            path = Path.Combine(folder, "glossary_" + targetIso + ".txt");
            Load();
        }

        public List<KeyValuePair<string, string>> Pairs => new List<KeyValuePair<string, string>>(pairs);

        public void Load()
        {
            var l = new List<KeyValuePair<string, string>>();
            try
            {
                if (File.Exists(path))
                    foreach (string line in File.ReadAllLines(path, Encoding.UTF8))
                    {
                        int tab = line.IndexOf('\t');
                        if (tab <= 0) continue;
                        string a = line.Substring(0, tab).Trim(), b = line.Substring(tab + 1).Trim();
                        if (a.Length > 0) l.Add(new KeyValuePair<string, string>(a, b));
                    }
            }
            catch { }
            Build(l);
        }

        public void Set(List<KeyValuePair<string, string>> l)
        {
            var clean = l.Where(p => !string.IsNullOrWhiteSpace(p.Key))
                         .Select(p => new KeyValuePair<string, string>(p.Key.Trim(), (p.Value ?? "").Trim())).ToList();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllLines(path, clean.Select(p => p.Key + "\t" + p.Value).ToArray(), Encoding.UTF8);
            }
            catch { }
            Build(clean);
        }

        private void Build(List<KeyValuePair<string, string>> l)
        {
            var m = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in l) m[p.Key] = p.Value;
            // longer words first, so "steel wall" wins over "steel"
            var keys = m.Keys.OrderByDescending(k => k.Length).Select(Regex.Escape).ToList();
            lock (this)
            {
                pairs = l;
                map = m;
                rx = keys.Count == 0 ? null : new Regex(@"(?<![\p{L}\p{N}])(?:" + string.Join("|", keys.ToArray()) + @")(?![\p{L}\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
                Version++;
            }
        }

        // 2.5.2: the entries whose word occurs in the text (for the context of AI services)
        public List<KeyValuePair<string, string>> Found(string text, int max)
        {
            var res = new List<KeyValuePair<string, string>>();
            Regex r;
            Dictionary<string, string> m;
            lock (this) { r = rx; m = map; }
            if (r == null || string.IsNullOrEmpty(text)) return res;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match x in r.Matches(text))
            {
                if (res.Count >= max) break;
                if (seen.Add(x.Value) && m.TryGetValue(x.Value, out string to))
                    res.Add(new KeyValuePair<string, string>(x.Value, to));
            }
            return res;
        }

        public string Apply(string text)
        {
            Regex r;
            Dictionary<string, string> m;
            lock (this) { r = rx; m = map; }
            if (r == null || string.IsNullOrEmpty(text)) return text;
            return r.Replace(text, x =>
            {
                if (!m.TryGetValue(x.Value, out string to)) return x.Value;
                if (to.Length == 0) return to;
                // the case of the first letter follows the text ("Kolonista" → "Osadník")
                if (char.IsUpper(x.Value[0]) && char.IsLower(to[0])) return char.ToUpper(to[0]) + to.Substring(1);
                if (char.IsLower(x.Value[0]) && char.IsUpper(to[0]) && !(to.Length > 1 && char.IsUpper(to[1]))) return char.ToLower(to[0]) + to.Substring(1);
                return to;
            });
        }
    }
}
