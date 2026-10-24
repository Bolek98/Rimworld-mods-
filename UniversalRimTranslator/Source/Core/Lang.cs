using System;
using System.Collections.Generic;

namespace Urt2
{
    // Languages: RimWorld language folder names <-> ISO codes of the translation services,
    // and a cheap script detector (which writing system a text uses).
    public static class Lang
    {
        // RimWorld folder name (or its start) -> ISO 639-1 (+ region where services need it)
        private static readonly Dictionary<string, string> Iso = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "English", "en" }, { "Czech", "cs" }, { "Slovak", "sk" }, { "German", "de" }, { "French", "fr" },
            { "Italian", "it" }, { "Spanish", "es" }, { "SpanishLatin", "es" }, { "Portuguese", "pt" },
            { "PortugueseBrazilian", "pt-BR" }, { "Polish", "pl" }, { "Russian", "ru" }, { "Ukrainian", "uk" },
            { "Dutch", "nl" }, { "Danish", "da" }, { "Swedish", "sv" }, { "Norwegian", "nb" }, { "Finnish", "fi" },
            { "Turkish", "tr" }, { "Japanese", "ja" }, { "Korean", "ko" }, { "ChineseSimplified", "zh-Hans" },
            { "ChineseTraditional", "zh-Hant" }, { "Chinese", "zh-Hans" }, { "SChinese", "zh-Hans" }, { "TChinese", "zh-Hant" },
            { "Vietnamese", "vi" }, { "Hungarian", "hu" }, { "Romanian", "ro" }, { "Bulgarian", "bg" }, { "Greek", "el" },
            { "Hebrew", "he" }, { "Indonesian", "id" }, { "Latvian", "lv" }, { "Lithuanian", "lt" }, { "Estonian", "et" },
            { "Arabic", "ar" }, { "Catalan", "ca" }, { "Croatian", "hr" }, { "Serbian", "sr" }, { "Slovenian", "sl" },
            { "Thai", "th" }, { "Hindi", "hi" }, { "Persian", "fa" }, { "Malay", "ms" }, { "Filipino", "fil" },
            { "Belarusian", "be" }, { "Kazakh", "kk" }, { "Georgian", "ka" }, { "Armenian", "hy" }, { "Icelandic", "is" },
            { "Irish", "ga" }, { "Welsh", "cy" }, { "Basque", "eu" }, { "Galician", "gl" }, { "Esperanto", "eo" },
            { "Latin", "la" }, { "Mongolian", "mn" }, { "Bengali", "bn" }, { "Tamil", "ta" }, { "Urdu", "ur" }
        };

        // "Czech", "Czech (Čeština)", "Čeština [cs]" … -> "cs"; an ISO code stays as it is
        public static string ToIso(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return "en";
            string s = language.Trim();
            if (Iso.TryGetValue(s, out string iso))
                return iso;
            int cut = s.IndexOfAny(new[] { ' ', '(', '[', '_', '-' });
            if (cut > 0 && Iso.TryGetValue(s.Substring(0, cut), out iso))
                return iso;
            foreach (var kv in Iso)
                if (s.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                    return kv.Value;
            if (s.Length <= 7 && s.IndexOf(' ') < 0)
                return s.ToLowerInvariant() == "zh" ? "zh-Hans" : s;
            return s.ToLowerInvariant();
        }

        // two-letter base code: "pt-BR" -> "pt", "zh-Hans" -> "zh"
        public static string Base(string iso)
        {
            if (string.IsNullOrEmpty(iso)) return "";
            int i = iso.IndexOf('-');
            return (i > 0 ? iso.Substring(0, i) : iso).ToLowerInvariant();
        }

        public static bool Same(string a, string b) => string.Equals(Base(ToIso(a)), Base(ToIso(b)), StringComparison.OrdinalIgnoreCase);

        public enum Script { None, Latin, Cyrillic, Greek, Cjk, Kana, Hangul, Arabic, Hebrew, Thai, Devanagari, Other }

        // the writing system most letters of the text use (tokens and markup should be removed first)
        public static Script Dominant(string text)
        {
            if (string.IsNullOrEmpty(text)) return Script.None;
            var counts = new int[(int)Script.Other + 1];
            foreach (char c in text)
            {
                Script s = Of(c);
                if (s != Script.None) counts[(int)s]++;
            }
            // Japanese text is kana + kanji: kana decides
            if (counts[(int)Script.Kana] > 0) counts[(int)Script.Kana] += counts[(int)Script.Cjk];
            Script best = Script.None;
            int n = 0;
            for (int i = 1; i < counts.Length; i++)
                if (counts[i] > n) { n = counts[i]; best = (Script)i; }
            return best;
        }

        public static Script Of(char c)
        {
            if (c < 'A') return Script.None;
            if (c <= 'z') return char.IsLetter(c) ? Script.Latin : Script.None;
            if (c >= 0x00C0 && c <= 0x024F) return Script.Latin;
            if (c >= 0x1E00 && c <= 0x1EFF) return Script.Latin;
            if (c >= 0x0370 && c <= 0x03FF) return Script.Greek;
            if (c >= 0x0400 && c <= 0x052F) return Script.Cyrillic;
            if (c >= 0x0590 && c <= 0x05FF) return Script.Hebrew;
            if (c >= 0x0600 && c <= 0x06FF) return Script.Arabic;
            if (c >= 0x0900 && c <= 0x097F) return Script.Devanagari;
            if (c >= 0x0E00 && c <= 0x0E7F) return Script.Thai;
            if (c >= 0x3040 && c <= 0x30FF) return Script.Kana;
            if (c >= 0xAC00 && c <= 0xD7AF) return Script.Hangul;
            if ((c >= 0x4E00 && c <= 0x9FFF) || (c >= 0x3400 && c <= 0x4DBF) || (c >= 0xF900 && c <= 0xFAFF)) return Script.Cjk;
            return char.IsLetter(c) ? Script.Other : Script.None;
        }

        // the script a language is written in
        public static Script ScriptOf(string iso)
        {
            switch (Base(iso))
            {
                case "ru": case "uk": case "bg": case "sr": case "be": case "kk": case "mn": return Script.Cyrillic;
                case "el": return Script.Greek;
                case "zh": return Script.Cjk;
                case "ja": return Script.Kana;
                case "ko": return Script.Hangul;
                case "ar": case "fa": case "ur": return Script.Arabic;
                case "he": return Script.Hebrew;
                case "th": return Script.Thai;
                case "hi": return Script.Devanagari;
                case "ka": case "hy": case "bn": case "ta": return Script.Other;
                default: return Script.Latin;
            }
        }

        // a guess of the source language from the script alone (null = Latin, ask the service)
        public static string GuessFromScript(string text)
        {
            switch (Dominant(text))
            {
                case Script.Cjk: return "zh-Hans";
                case Script.Kana: return "ja";
                case Script.Hangul: return "ko";
                case Script.Cyrillic: return "ru";
                case Script.Greek: return "el";
                case Script.Arabic: return "ar";
                case Script.Hebrew: return "he";
                case Script.Thai: return "th";
                default: return null;
            }
        }
    }
}
