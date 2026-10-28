using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Urt2
{
    public enum Verdict
    {
        Ok,
        Empty,
        BrokenTokens,     // a {token}/[token]/<tag> lost, changed or invented
        BrokenRule,       // grammar rule keyword or [tokens] changed – the game would write "ERR"
        Untranslated,     // still the source text
        WrongScript,      // e.g. Chinese letters in a Czech translation
        ServiceError,     // the service answered with an error message instead of a translation
        OddLength         // much shorter/longer than the source – probably cut or garbage
    }

    // Checks a finished translation against its source. Used for every new translation and to
    // audit translations taken over from the old cache.
    public static class Validate
    {
        private static readonly Regex ErrorLike = new Regex(@"\b(error|exception|rate.?limit|too many requests|quota|unauthorized|forbidden|invalid api|service unavailable|bad request)\b|<html|<!doctype|<body", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex AnyToken = new Regex(@"\{[^{}\r\n]*\}|\[[^\[\]\r\n]*\]|</?[A-Za-z][^<>\r\n]*>|\(\*[A-Za-z_][^)\r\n]*\)|\(/[A-Za-z_][A-Za-z0-9_]*\)|\\[nrt]|%(?:\d+\$)?[-+#0]*(?:\d+|\*)?(?:\.(?:\d+|\*))?[diuoxXfFeEgGsc%]", RegexOptions.Compiled);
        private static readonly Regex Word = new Regex(@"\p{L}{3,}", RegexOptions.Compiled);

        public static Verdict Check(string source, string translation, string targetIso)
        {
            if (string.IsNullOrWhiteSpace(translation))
                return Verdict.Empty;
            string src = Protect.Normalize(source) ?? "";
            string tr = Protect.Normalize(translation);

            if (Protect.IsRule(src))
            {
                if (Protect.RuleKeyword(src).Trim() != (Protect.RuleKeyword(tr) ?? "").Trim())
                    return Verdict.BrokenRule;
                if (!SameCounts(Protect.RuleTokens(src), Protect.RuleTokens(tr)))
                    return Verdict.BrokenRule;
            }
            if (!SameCounts(Tokens(src), Tokens(tr)))
                return Verdict.BrokenTokens;

            string srcText = AnyToken.Replace(src, " ");
            string trText = AnyToken.Replace(tr, " ");
            if (ErrorLike.IsMatch(tr) && !ErrorLike.IsMatch(src))
                return Verdict.ServiceError;

            Lang.Script want = Lang.ScriptOf(targetIso);
            Lang.Script got = Lang.Dominant(trText);
            bool cjkMix = (want == Lang.Script.Kana && got == Lang.Script.Cjk) || (want == Lang.Script.Cjk && got == Lang.Script.Kana);
            if (got != Lang.Script.None && got != want && !cjkMix)
                return Verdict.WrongScript;

            if (!Lang.Same(targetIso, "en") || Lang.Dominant(srcText) != Lang.Script.Latin)
            {
                if (string.Equals(src, tr, StringComparison.Ordinal) && Word.Matches(srcText).Count >= 3)
                    return Verdict.Untranslated;
                if (MostlySameWords(srcText, trText))
                    return Verdict.Untranslated;
            }

            int ls = srcText.Trim().Length, lt = trText.Trim().Length;
            if (ls >= 40 && (lt < ls * 0.3 || lt > ls * 4))
                return Verdict.OddLength;
            return Verdict.Ok;
        }

        public static bool IsGood(Verdict v) => v == Verdict.Ok;

        private static Dictionary<string, int> Tokens(string s)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            string kw = Protect.RuleKeyword(s);
            if (kw != null) s = s.Substring(kw.Length);
            foreach (Match m in AnyToken.Matches(s))
            {
                d.TryGetValue(m.Value, out int n);
                d[m.Value] = n + 1;
            }
            return d;
        }

        private static bool SameCounts(Dictionary<string, int> a, Dictionary<string, int> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var kv in a)
                if (!b.TryGetValue(kv.Key, out int n) || n != kv.Value) return false;
            return true;
        }

        // 70 %+ of the longer words unchanged = the text was (mostly) not translated
        private static bool MostlySameWords(string src, string tr)
        {
            var s = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in Word.Matches(src)) if (m.Value.Length >= 4) s.Add(m.Value);
            if (s.Count < 4) return false;
            int same = 0, all = 0;
            foreach (Match m in Word.Matches(tr))
            {
                if (m.Value.Length < 4) continue;
                all++;
                if (s.Contains(m.Value)) same++;
            }
            return all > 0 && same >= all * 0.7;
        }
    }
}
