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
        private static readonly Regex AnyWord = new Regex(@"[\p{L}\p{M}]+", RegexOptions.Compiled);

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
            var ts = Tokens(src);
            var tt = Tokens(tr);
            // 1.2.1: Chinese/Japanese brackets 【试炼】 are no tokens – services write them as [soud] in the
            // translation; that many extra [..] (not in the source) are allowed
            int wide = WideBrackets(src);
            if (wide > 0)
                foreach (string k in new List<string>(tt.Keys))
                {
                    if (wide <= 0) break;
                    if (k.Length < 2 || k[0] != '[' || ts.ContainsKey(k)) continue;
                    int take = Math.Min(wide, tt[k]);
                    tt[k] -= take; wide -= take;
                    if (tt[k] == 0) tt.Remove(k);
                }
            if (!SameCounts(ts, tt))
                return Verdict.BrokenTokens;

            string srcText = AnyToken.Replace(src, " ");
            string trText = AnyToken.Replace(tr, " ");
            if (ErrorLike.IsMatch(tr) && !ErrorLike.IsMatch(src))
                return Verdict.ServiceError;

            Lang.Script want = Lang.ScriptOf(targetIso);
            // 1.1.1: Chinese/Japanese/Korean letters carry a whole syllable or word – one counts as 3 Latin
            // letters, so a Chinese text with an English mod name in it is not "wrong script"
            Lang.Script got = WideTarget(want) ? Lang.Dominant(trText, true) : Lang.Dominant(trText);
            // a Chinese label that keeps an English name ("Neural Racks 存储") is still Chinese
            // 1.1.1: the same for every non-Latin target (Arabic, Thai, Russian…); the "untranslated" check
            // below still catches an English answer with only a letter or two of the target script
            if (want != Lang.Script.Latin && got == Lang.Script.Latin && HasScript(trText, want)) got = want;
            bool cjkMix = (want == Lang.Script.Kana && got == Lang.Script.Cjk) || (want == Lang.Script.Cjk && got == Lang.Script.Kana);
            if (got != Lang.Script.None && got != want && !cjkMix)
                return Verdict.WrongScript;

            if (!Lang.Same(targetIso, "en") || Lang.Dominant(srcText) != Lang.Script.Latin)
            {
                if (string.Equals(src, tr, StringComparison.Ordinal))
                {
                    // 1.2.1: only words that still need translating count – a text that is already in the target
                    // language ("oblak toxického prachu") or keeps only names stays as it is
                    int all = 0, foreign = 0;
                    foreach (Match m in Word.Matches(srcText)) { all++; if (Foreign(m.Value)) foreign++; }
                    if (foreign >= 3)
                        return Verdict.Untranslated;
                    // a target-language text with an English word left in it ("Replicate Zdravotní péče") – only
                    // when the game told us which words are English (Czech words without accents look the same)
                    if (foreign >= 1 && foreign < all && EnglishWord != null)
                        return Verdict.Untranslated;
                }
                if (MostlySameWords(srcText, trText))
                    return Verdict.Untranslated;
            }

            // 1.1.1: the length compares weighted letters – a Chinese translation is about a third of the
            // English character count and was rejected as "cut off" (most Chinese translations failed)
            int ls = Weighted(srcText.Trim()), lt = Weighted(trText.Trim());
            if (ls >= 40 && (lt < ls * 0.3 || lt > ls * 4))
                return Verdict.OddLength;
            return Verdict.Ok;
        }

        public static bool IsGood(Verdict v) => v == Verdict.Ok;

        // 1.2.1: set by the game – is this an English word (from the English texts of all mods, without names and
        // words the target language uses too). Null = unknown: every plain ASCII word counts as English.
        public static Func<string, bool> EnglishWord;

        // a word that still needs translating: ASCII letters only (accented letters belong to the target language)
        // and, when known, one of the English words
        private static bool Foreign(string w)
        {
            foreach (char c in w) if (c > 127) return false;
            Func<string, bool> f = EnglishWord;
            if (f == null) return true;
            try { return f(w); } catch { return true; }
        }

        // opening Chinese/Japanese brackets in the text: 【 〔 ［ 「 『 〖
        private static int WideBrackets(string s)
        {
            int n = 0;
            foreach (char c in s) if (c == '\u3010' || c == '\u3014' || c == '\uFF3B' || c == '\u300C' || c == '\u300E' || c == '\u3016') n++;
            return n;
        }

        private static bool WideTarget(Lang.Script s) => s == Lang.Script.Cjk || s == Lang.Script.Kana || s == Lang.Script.Hangul;

        private static bool HasScript(string s, Lang.Script want)
        {
            foreach (char c in s)
            {
                Lang.Script k = Lang.Of(c);
                if (k == want || (want == Lang.Script.Kana && k == Lang.Script.Cjk) || (want == Lang.Script.Cjk && k == Lang.Script.Kana)) return true;
            }
            return false;
        }

        private static int Weighted(string s)
        {
            int n = 0;
            foreach (char c in s)
            {
                Lang.Script k = Lang.Of(c);
                n += k == Lang.Script.Cjk || k == Lang.Script.Kana || k == Lang.Script.Hangul ? 3 : 1;
            }
            return n;
        }

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
            // 1.2.1: words of the source that are already in the target language do not count
            foreach (Match m in Word.Matches(src)) if (m.Value.Length >= 4 && Foreign(m.Value)) s.Add(m.Value);
            if (s.Count < 4) return false;
            int same = 0, all = 0;
            foreach (Match m in AnyWord.Matches(tr))
            {
                string w = m.Value;
                if (Lang.Dominant(w) != Lang.Script.Latin)
                {
                    // 1.1.1: words of other scripts count too (a Chinese word is short but carries a lot);
                    // before, "Neural Racks 储物架" counted only the two English words = "untranslated"
                    all += Math.Max(1, Weighted(w) / 4);
                    continue;
                }
                if (w.Length < 4) continue;
                all++;
                if (s.Contains(w)) same++;
            }
            return all > 0 && same >= all * 0.7;
        }
    }
}
