using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace Urt2
{
    // Everything a translation service must not touch is replaced by {0}, {1} … before sending
    // (the services keep curly placeholders reliably – tested with Edge and Yandex) and put back
    // afterwards. Grammar rules ("keyword(p=2)->text") send only the text after "->".
    public sealed class Masked
    {
        public string Original = "";            // the text without the rule keyword
        public string Prefix = "";              // rule keyword incl. "->", sent back unchanged
        public string Text;                     // what the service gets
        public readonly List<string> Tokens = new List<string>();
        // (*Gender)his|her(/Gender): token index -> the options; each option is translated as its own
        // text and the block is rebuilt with the translated options
        public readonly Dictionary<int, string[]> Choices = new Dictionary<int, string[]>();
        public readonly Dictionary<int, string[]> ChoicesDone = new Dictionary<int, string[]>();
        public bool Empty => string.IsNullOrWhiteSpace(Text) || !Protect.HasWords(Text);
    }

    public static class Protect
    {
        // tokens that must survive exactly (order matters: longer patterns first)
        private static readonly Regex Token = new Regex(
            @"\{\{[^{}\r\n]*\}\}" +                       // {{0}}
            @"|\{[^{}\r\n]*\}" +                          // {0} {PAWN_name} {1_label}
            @"|\[\[[^\]\r\n]*\]\]" +                      // [[x]]
            @"|\[[^\[\]\r\n]*\]" +                        // [PAWN_nameDef] [gore]
            @"|\(\*[A-Za-z_][^)\r\n]*\)|\(/[A-Za-z_][A-Za-z0-9_]*\)" + // (*Gender)…(/Gender)
            @"|</?[A-Za-z][^<>\r\n]*>" +                  // <color=#fff> </b>
            @"|\\[nrt]" +                                 // literal \n in Keyed files
            @"|%(?:\d+\$)?[-+#0]*(?:\d+|\*)?(?:\.(?:\d+|\*))?[diuoxXfFeEgGsc%]" + // %s %d %1$s
            @"|\$[A-Za-z_][A-Za-z0-9_.]*" +               // $variable
            @"|https?://\S+",                             // links
            RegexOptions.Compiled);

        private static readonly Regex GenderBlock = new Regex(@"\(\*(Gender|Plural|Name)[^)\r\n]*\)([^()\r\n]*)\(/\1\)", RegexOptions.Compiled);
        private static readonly Regex RulePrefix = new Regex(@"^\s*[A-Za-z_][A-Za-z0-9_]*(?:\([^)\r\n]*\))?\s*->", RegexOptions.Compiled);
        private static readonly Regex Placeholder = new Regex(@"\{\s*(\d+)\s*\}", RegexOptions.Compiled);
        private static readonly Regex Letters = new Regex(@"\p{L}{2,}", RegexOptions.Compiled);

        public static bool IsRule(string text) => text != null && RulePrefix.IsMatch(text);

        public static string RuleKeyword(string text)
        {
            Match m = text == null ? null : RulePrefix.Match(text);
            return m != null && m.Success ? m.Value : null;
        }

        public static bool HasWords(string text) => !string.IsNullOrEmpty(text) && Letters.IsMatch(Token.Replace(text, " "));

        public static Masked Mask(string text)
        {
            var m = new Masked();
            if (text == null) { m.Text = ""; return m; }
            string body = text;
            m.Original = text;
            string kw = RuleKeyword(text);
            if (kw != null)
            {
                m.Prefix = kw;
                body = text.Substring(kw.Length);
            }
            m.Original = body;
            body = GenderBlock.Replace(body, x =>
            {
                string inner = x.Groups[2].Value;
                int idx = m.Tokens.Count;
                m.Tokens.Add(x.Value);
                if (inner.IndexOf('|') >= 0 && HasWords(inner))
                    m.Choices[idx] = inner.Split('|');
                return "\u0001" + idx + "\u0002";
            });
            m.Text = Token.Replace(body, x =>
            {
                m.Tokens.Add(x.Value);
                return "{" + (m.Tokens.Count - 1) + "}";
            });
            m.Text = Regex.Replace(m.Text, "\u0001(\\d+)\u0002", x => "{" + x.Groups[1].Value + "}");
            return m;
        }

        // null when the result lost, doubled or invented a placeholder
        public static string Unmask(Masked m, string translated)
        {
            if (translated == null) return null;
            var seen = new int[m.Tokens.Count];
            bool bad = false;
            string body = Placeholder.Replace(translated, x =>
            {
                int i;
                if (!int.TryParse(x.Groups[1].Value, out i) || i < 0 || i >= m.Tokens.Count) { bad = true; return x.Value; }
                seen[i]++;
                if (m.ChoicesDone.TryGetValue(i, out string[] opts))
                {
                    Match g = GenderBlock.Match(m.Tokens[i]);
                    string head = m.Tokens[i].Substring(0, m.Tokens[i].IndexOf(')') + 1);
                    return head + string.Join("|", opts) + "(/" + g.Groups[1].Value + ")";
                }
                return m.Tokens[i];
            });
            if (bad) return null;
            for (int i = 0; i < seen.Length; i++)
                if (seen[i] != 1) return null;
            body = FixSpacing(body, m);
            body = MatchFirstLetter(m.Original, body);
            return m.Prefix + body;
        }

        // services sometimes add or drop spaces next to placeholders: "<color=red> text" stays as
        // the original had it around markup tags
        // the first letter keeps the case of the original: grammar rule parts and labels that are
        // put in the middle of a sentence start lower-case
        private static string MatchFirstLetter(string original, string s)
        {
            int a = FirstLetter(original), b = FirstLetter(s);
            if (a < 0 || b < 0) return s;
            char oc = original[a], tc = s[b];
            if (char.IsLower(oc) && char.IsUpper(tc) && (b + 1 >= s.Length || !char.IsUpper(s[b + 1])))
                return s.Substring(0, b) + char.ToLowerInvariant(tc) + s.Substring(b + 1);
            if (char.IsUpper(oc) && char.IsLower(tc))
                return s.Substring(0, b) + char.ToUpperInvariant(tc) + s.Substring(b + 1);
            return s;
        }

        private static int FirstLetter(string s)
        {
            if (string.IsNullOrEmpty(s)) return -1;
            // skip leading tokens like {0} or <b>
            string masked = Token.Replace(s, x => new string(' ', x.Length));
            for (int i = 0; i < masked.Length; i++)
                if (char.IsLetter(masked[i])) return i;
            return -1;
        }

        private static string FixSpacing(string s, Masked m)
        {
            s = s.Replace(" \\n", "\\n").Replace("\\n ", "\\n");
            foreach (string t in m.Tokens)
            {
                if (t.StartsWith("<") && t.EndsWith(">") && !t.StartsWith("</"))
                    s = s.Replace(t + " ", t);
                if (t.StartsWith("</"))
                    s = s.Replace(" " + t, t);
            }
            return s;
        }

        // the [tokens] of a grammar rule body as a multiset
        public static Dictionary<string, int> RuleTokens(string rule)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            if (rule == null) return d;
            string kw = RuleKeyword(rule);
            string body = kw != null ? rule.Substring(kw.Length) : rule;
            foreach (Match x in Regex.Matches(body, @"\[[^\[\]\r\n]*\]"))
            {
                d.TryGetValue(x.Value, out int n);
                d[x.Value] = n + 1;
            }
            return d;
        }

        // things that are not text at all: paths, defNames, numbers, colours, single tokens
        public static bool LooksLikeNonText(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return true;
            string t = s.Trim();
            if (!HasWords(t)) return true;
            if (t.IndexOf(' ') < 0)
            {
                if (t.IndexOf('/') >= 0 || t.IndexOf('\\') >= 0) return true;              // Things/Item/Foo
                if (t.IndexOf('_') >= 0) return true;                                       // Some_DefName
                int upperInside = 0;
                for (int i = 1; i < t.Length; i++) if (char.IsUpper(t[i]) && char.IsLower(t[i - 1])) upperInside++;
                if (upperInside >= 1 && t.Length > 6) return true;                          // CamelCaseName
                if (t.EndsWith(".png") || t.EndsWith(".xml") || t.EndsWith(".dll")) return true;
                if (t.IndexOf("://", StringComparison.Ordinal) >= 0) return true;
            }
            return false;
        }

        // split long texts into lines; every non-empty line is translated on its own and the
        // structure (empty lines, bullets, indentation) is kept exactly
        public static List<string> Lines(string text) => new List<string>(text.Replace("\r\n", "\n").Split('\n'));

        public static string Join(List<string> lines) => string.Join("\n", lines.ToArray());

        public static string Normalize(string s) => s?.Replace("\r\n", "\n").Trim();
    }
}
