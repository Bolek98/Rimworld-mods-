using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    public enum ItemKind : byte { Def, DefListItem, Keyed }

    // One piece of text of the game or a mod that the active language does not translate yet.
    public sealed class Item
    {
        public ItemKind Kind;
        public Type DefType;
        public string Path;            // def injection path (suggested), or the Keyed key
        public int ListIndex = -1;     // for one entry of a list (rulesStrings …)
        public string Source;          // original text
        public string SourceLang;      // null = Latin script, let the service detect
        public string Mod;             // mod name (for progress per mod)
        public bool IsRule;
        public int Priority;           // lower = sooner (labels before descriptions)
    }

    // Finds everything translatable that the active language has no translation for, using the
    // game's own lists of translatable def fields (the same ones language packs use) and the
    // English Keyed texts of all mods.
    public static class Scan
    {
        // the original full lists (rulesStrings …) – a list is injected as a whole
        public static readonly Dictionary<string, List<string>> OriginalLists = new Dictionary<string, List<string>>();

        public static string ListKey(Type defType, string path) => defType.Name + "|" + path;

        public static List<Item> Run(out string report)
        {
            OriginalLists.Clear();
            var items = new List<Item>();
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            LoadedLanguage english = LanguageDatabase.defaultLanguage;
            int defsDone = 0, keyedDone = 0, skippedInjected = 0;

            // def fields already translated by a language pack of the active language
            var injected = new HashSet<string>();
            if (active != null && active != english)
                foreach (DefInjectionPackage p in active.defInjections)
                    foreach (var kv in p.injections)
                        if (kv.Value.injected && !kv.Value.isPlaceholder)
                        {
                            injected.Add(p.defType.Name + "|" + (kv.Value.normalizedPath ?? kv.Key));
                            injected.Add(p.defType.Name + "|" + (kv.Value.suggestedPath ?? kv.Key));
                        }

            foreach (Type defType in GenDefDatabase.AllDefTypesWithDatabases())
            {
                try
                {
                    DefInjectionUtility.ForEachPossibleDefInjection(defType,
                        (string suggestedPath, string normalizedPath, bool isCollection, string currentValue, IEnumerable<string> currentValueCollection,
                         bool translationAllowed, bool fullListTranslationAllowed, FieldInfo fieldInfo, Def def) =>
                        {
                            if (!translationAllowed)
                                return;
                            if (injected.Contains(defType.Name + "|" + normalizedPath) || injected.Contains(defType.Name + "|" + suggestedPath))
                            {
                                skippedInjected++;
                                return;
                            }
                            string mod = def?.modContentPack?.Name ?? "?";
                            if (!isCollection)
                            {
                                if (!Wanted(currentValue)) return;
                                items.Add(new Item
                                {
                                    Kind = ItemKind.Def, DefType = defType, Path = suggestedPath, Source = currentValue, Mod = mod,
                                    IsRule = Protect.IsRule(currentValue), SourceLang = Lang.GuessFromScript(currentValue),
                                    Priority = PriorityOf(fieldInfo?.Name)
                                });
                                defsDone++;
                            }
                            else if (fullListTranslationAllowed && currentValueCollection != null)
                            {
                                OriginalLists[ListKey(defType, suggestedPath)] = currentValueCollection.ToList();
                                int i = 0;
                                foreach (string v in currentValueCollection)
                                {
                                    if (Wanted(v))
                                    {
                                        items.Add(new Item
                                        {
                                            Kind = ItemKind.DefListItem, DefType = defType, Path = suggestedPath, ListIndex = i, Source = v, Mod = mod,
                                            IsRule = Protect.IsRule(v), SourceLang = Lang.GuessFromScript(v), Priority = 5
                                        });
                                        defsDone++;
                                    }
                                    i++;
                                }
                            }
                        });
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] scan of " + defType.Name + " failed: " + e.Message);
                }
            }

            // Keyed texts: every English key the active language lacks (or has only as a placeholder)
            if (active != null && english != null && active != english)
            {
                foreach (var kv in english.keyedReplacements)
                {
                    if (active.keyedReplacements.TryGetValue(kv.Key, out var have) && !have.isPlaceholder)
                        continue;
                    string v = kv.Value?.value;
                    if (!Wanted(v)) continue;
                    items.Add(new Item
                    {
                        Kind = ItemKind.Keyed, Path = kv.Key, Source = v, Mod = ModOfFile(kv.Value.fileSourceFullPath),
                        SourceLang = Lang.GuessFromScript(v), IsRule = false, Priority = 1
                    });
                    keyedDone++;
                }
            }
            report = "def texts " + defsDone + ", Keyed texts " + keyedDone + ", already translated by language packs " + skippedInjected;
            return items;
        }

        private static bool Wanted(string v) => !string.IsNullOrWhiteSpace(v) && !Protect.LooksLikeNonText(v);

        private static int PriorityOf(string field)
        {
            switch (field)
            {
                case "label": case "labelShort": case "labelNoun": case "labelPlural": case "LabelCap": return 0;
                case "description": case "descriptionShort": return 2;
                case "jobString": case "reportString": case "gerund": case "verb": return 1;
                default: return 3;
            }
        }

        private static readonly Dictionary<string, string> modByRoot = new Dictionary<string, string>();

        private static string ModOfFile(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            if (modByRoot.Count == 0)
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                    if (!string.IsNullOrEmpty(m.RootDir)) modByRoot[m.RootDir.Replace('\\', '/')] = m.Name;
            string p = path.Replace('\\', '/');
            foreach (var kv in modByRoot)
                if (p.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return "?";
        }
    }
}
