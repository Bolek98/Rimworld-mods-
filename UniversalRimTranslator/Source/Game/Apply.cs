using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Urt2;
using Verse;
using Verse.Grammar;

namespace UniversalRimTranslator
{
    // Puts translations into the running game the same way language packs do: def fields through
    // the game's own DefInjectionPackage, Keyed texts into the active language. Nothing in mod
    // folders is changed. Main thread only.
    public static class Apply
    {
        private static readonly Dictionary<Type, DefInjectionPackage> packages = new Dictionary<Type, DefInjectionPackage>();
        private static readonly Dictionary<string, string> appliedText = new Dictionary<string, string>();  // what we put in, per item key
        private static bool rulesTouched;

        private static readonly FieldInfo rulesResolved = AccessTools.Field(typeof(RulePack), "rulesResolved");
        private static readonly FieldInfo cachedRules = AccessTools.Field(typeof(RulePackDef), "cachedRules");
        private static readonly FieldInfo defRulePack = AccessTools.Field(typeof(RulePackDef), "rulePack");

        public static string ItemKey(Item it) => it.Kind == ItemKind.Keyed ? "K|" + it.Path : it.DefType.Name + "|" + it.Path + (it.ListIndex >= 0 ? "#" + it.ListIndex : "");

        public static bool HasAny(Item it) => appliedText.ContainsKey(ItemKey(it));

        public static bool IsApplied(Item it, string text) => appliedText.TryGetValue(ItemKey(it), out string t) && t == text;

        // Applies the translations of many items at once. text == null puts the original back.
        public static int Many(List<(Item item, string text)> work)
        {
            int n = 0;
            var lists = new Dictionary<string, List<(Item, string)>>();
            var touchedTypes = new HashSet<Type>();
            var directTypes = new HashSet<Type>();
            foreach (var (it, text) in work)
            {
                try
                {
                    if (it.Kind == ItemKind.Keyed)
                    {
                        if (Keyed(it, text)) n++;
                    }
                    else if (it.Kind == ItemKind.Def && Direct(it, text ?? it.Source))
                    {
                        // 2.1.0: a plain field of a def (defName.label …) is set directly – the same
                        // result as the game's injection, many times faster
                        Remember(it, text);
                        directTypes.Add(it.DefType);
                        n++;
                    }
                    else if (it.Kind == ItemKind.Def)
                    {
                        DefInjectionPackage p = Package(it.DefType);
                        p.injections[it.Path] = new DefInjectionPackage.DefInjection { path = it.Path, injection = text ?? it.Source, fileSource = "Universal RimTranslator" };
                        Remember(it, text);
                        touchedTypes.Add(it.DefType);
                        n++;
                    }
                    else
                    {
                        string lk = Scan.ListKey(it.DefType, it.Path);
                        if (!lists.TryGetValue(lk, out var l)) lists[lk] = l = new List<(Item, string)>();
                        l.Add((it, text));
                    }
                }
                catch (Exception e)
                {
                    MLog.WarnOnce("[URT] could not apply " + ItemKey(it) + ": " + e.Message, ItemKey(it).GetHashCode());
                }
            }

            // a list (grammar rules …) is injected as a whole: originals with the translated entries
            foreach (var kv in lists)
            {
                Item first = kv.Value[0].Item1;
                if (!Scan.OriginalLists.TryGetValue(kv.Key, out List<string> orig)) continue;
                List<string> full = CurrentList(first.DefType, first.Path, orig);
                foreach (var (it, text) in kv.Value)
                {
                    if (it.ListIndex < 0 || it.ListIndex >= full.Count) continue;
                    full[it.ListIndex] = text ?? it.Source;
                    Remember(it, text);
                    n++;
                }
                Package(first.DefType).injections[first.Path] = new DefInjectionPackage.DefInjection { path = first.Path, fullListInjection = full, fileSource = "Universal RimTranslator" };
                touchedTypes.Add(first.DefType);
                if (first.IsRule || kv.Value.Any(x => x.Item1.IsRule)) rulesTouched = true;
            }

            foreach (Type t in touchedTypes)
            {
                try { packages[t].InjectIntoDefs(false); }
                catch (Exception e) { MLog.Warn("[URT] injecting " + t.Name + " failed: " + e.Message); }
            }
            foreach (Type t in directTypes)
            {
                if (touchedTypes.Contains(t)) continue;      // InjectIntoDefs cleared it already
                try { GenGeneric.InvokeStaticMethodOnGenericType(typeof(DefDatabase<>), t, "ClearCachedData"); }
                catch (Exception e) { MLog.WarnOnce("[URT] cache of " + t.Name + ": " + e.Message, t.Name.GetHashCode()); }
            }
            if (rulesTouched) FlushRules();
            return n;
        }

        private static readonly Dictionary<string, KeyValuePair<Def, FieldInfo>> direct = new Dictionary<string, KeyValuePair<Def, FieldInfo>>();
        private static readonly Dictionary<string, FieldInfo> fieldsByName = new Dictionary<string, FieldInfo>();

        // "Steel.label": the def and its string field, found once; anything nested goes the game's way
        private static bool Direct(Item it, string value)
        {
            string key = it.DefType.Name + "|" + it.Path;
            if (!direct.TryGetValue(key, out var target))
            {
                target = default(KeyValuePair<Def, FieldInfo>);
                int dot = it.Path.IndexOf('.');
                if (dot > 0 && dot == it.Path.LastIndexOf('.') && dot < it.Path.Length - 1)
                {
                    Def def = GenDefDatabase.GetDefSilentFail(it.DefType, it.Path.Substring(0, dot), false);
                    if (def != null)
                    {
                        string fname = it.Path.Substring(dot + 1);
                        string fk = def.GetType().FullName + "|" + fname;
                        if (!fieldsByName.TryGetValue(fk, out FieldInfo fi))
                        {
                            fi = AccessTools.Field(def.GetType(), fname);
                            if (fi != null && (fi.IsStatic || fi.FieldType != typeof(string) || fi.IsInitOnly)) fi = null;
                            fieldsByName[fk] = fi;
                        }
                        if (fi != null) target = new KeyValuePair<Def, FieldInfo>(def, fi);
                    }
                }
                direct[key] = target;
            }
            if (target.Key == null) return false;
            target.Value.SetValue(target.Key, value);
            return true;
        }

        private static void Remember(Item it, string text)
        {
            if (text == null) appliedText.Remove(ItemKey(it));
            else appliedText[ItemKey(it)] = text;
        }

        // the list as it is now in the def (earlier translated entries included)
        private static List<string> CurrentList(Type defType, string path, List<string> orig)
        {
            var full = new List<string>(orig);
            for (int i = 0; i < full.Count; i++)
                if (appliedText.TryGetValue(defType.Name + "|" + path + "#" + i, out string t))
                    full[i] = t;
            return full;
        }

        private static DefInjectionPackage Package(Type t)
        {
            if (!packages.TryGetValue(t, out var p))
                packages[t] = p = new DefInjectionPackage(t);
            // every change is a new DefInjection object (injected = false), so InjectIntoDefs only
            // applies what is new
            return p;
        }

        private static bool Keyed(Item it, string text)
        {
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            if (active == null) return false;
            if (text == null)
            {
                if (active.keyedReplacements.TryGetValue(it.Path, out var r) && r.fileSource == "Universal RimTranslator")
                    active.keyedReplacements.Remove(it.Path);
            }
            else
            {
                active.keyedReplacements[it.Path] = new LoadedLanguage.KeyedReplacement
                {
                    key = it.Path, value = text, fileSource = "Universal RimTranslator", fileSourceLine = 0, fileSourceFullPath = "", isPlaceholder = false
                };
            }
            Remember(it, text);
            return true;
        }

        public static void FlushRules()
        {
            rulesTouched = false;
            try
            {
                foreach (RulePackDef d in DefDatabase<RulePackDef>.AllDefsListForReading)
                {
                    cachedRules?.SetValue(d, null);
                    object rp = defRulePack?.GetValue(d);
                    if (rp != null) rulesResolved?.SetValue(rp, null);
                }
                foreach (var d in DefDatabase<RimWorld.TaleDef>.AllDefsListForReading)
                    if (d.rulePack != null) rulesResolved?.SetValue(d.rulePack, null);
            }
            catch (Exception e)
            {
                MLog.WarnOnce("[URT] rule cache flush: " + e.Message, 0x7e1);
            }
        }
    }

    // A language with grammatical cases (Czech: imageNom_HasTale, imageGen …) asks for keywords
    // that tales and rules of mods do not have – the game then writes "ERR". When such a keyword
    // cannot be resolved, the plain keyword ("image") is used instead.
    public static class GrammarFallback
    {
        private static readonly System.Text.RegularExpressions.Regex CaseKeyword =
            new System.Text.RegularExpressions.Regex("^(.+?)(Nom|Gen|Dat|Acc|Akk|Loc|Lok|Ins|Voc|Abl|Par)(_[A-Za-z]+)?$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static readonly System.Text.RegularExpressions.Regex Variant =
            new System.Text.RegularExpressions.Regex("^(.+?)_(HasTale|Taleless|[A-Z][A-Za-z]+)$", System.Text.RegularExpressions.RegexOptions.Compiled);
        private static System.Collections.IDictionary rules;
        public static int Used;

        public static void Patch(Harmony h)
        {
            MethodInfo pick = AccessTools.Method(typeof(GrammarResolver), "RandomPossiblyResolvableEntry");
            rules = AccessTools.Field(typeof(GrammarResolver), "rules")?.GetValue(null) as System.Collections.IDictionary;
            if (pick == null || rules == null) { MLog.Warn("[URT] grammar fallback: GrammarResolver changed – not active"); return; }
            h.Patch(pick, prefix: new HarmonyMethod(typeof(GrammarFallback), nameof(Prefix)));
        }

        // before the lookup: a keyword nobody defines is swapped for the plain one that exists
        public static void Prefix(ref string keyword)
        {
            if (keyword == null || rules.Contains(keyword) || !UniversalRimTranslatorMod.Settings.grammarFallback) return;
            if (LanguageDatabase.activeLanguage == LanguageDatabase.defaultLanguage) return;
            foreach (string alt in Alternatives(keyword))
                if (rules.Contains(alt))
                {
                    keyword = alt;
                    Used++;
                    return;
                }
        }

        private static IEnumerable<string> Alternatives(string kw)
        {
            var m = CaseKeyword.Match(kw);
            if (m.Success)
            {
                if (m.Groups[3].Success) yield return m.Groups[1].Value + m.Groups[2].Value;  // imageNom
                yield return m.Groups[1].Value;                                                  // image
            }
            var v = Variant.Match(kw);
            if (v.Success) yield return v.Groups[1].Value;
        }
    }
}
