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

        public static int Failures;

        public static List<Item> Run(out string report)
        {
            Failures = 0;
            OriginalLists.Clear();
            var items = new List<Item>();
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            LoadedLanguage english = LanguageDatabase.defaultLanguage;
            int defsDone = 0, keyedDone = 0, skippedInjected = 0;
            // 2.2.2: mod names stay as they are ("Simple Sidearms" in the mod settings list became a Czech sentence)
            modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                if (!string.IsNullOrEmpty(m.Name)) modNames.Add(m.Name.Trim());

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
                            if (!translationAllowed || !TextField(fieldInfo))
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
                    Failures++;
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

        // Only fields that hold text a player reads. The game allows translating any string field
        // that is not marked [NoTranslate], but many mods keep identifiers in such fields (body part
        // names, enum values, defNames in lists, texture paths) – translating those breaks the mod.
        public static bool TextField(FieldInfo f)
        {
            if (f == null) return false;
            if (f.IsDefined(typeof(MustTranslateAttribute), true)) return true;
            string n = f.Name.ToLowerInvariant();
            if (n == "rulesstrings") return true;
            if (n.EndsWith("path") || n.EndsWith("defname") || n.EndsWith("class") || n.EndsWith("texture") || n.EndsWith("url") ||
                n.EndsWith("texname") || n.EndsWith("key") || n.EndsWith("id") || n.EndsWith("ids") || n.EndsWith("tag") || n.EndsWith("tags") ||
                n.EndsWith("name") && n != "labelnoun" || n.Contains("shader") || n.Contains("sound") || n.Contains("graphic") || n.Contains("icon"))
                return false;
            switch (n)
            {
                case "label": case "labelshort": case "labelplural": case "description": case "text": case "message": case "reportstring":
                case "jobstring": case "gerund": case "verb": case "title": case "titleshort": case "titlefemale": case "titleshortfemale":
                case "basedesc": case "inspectstring": case "customlabel": case "letterlabel": case "lettertext": case "pawnlabel": case "pawnslabel":
                case "pawnsplural": case "pawnsingular": case "leadertitle": case "thinglabel": case "labelsocial": case "headertip": case "labelmale":
                case "labelfemale": case "uselabel": case "gerundlabel": case "stufflabel": case "tooltiplabel": case "deathmessage":
                case "ingestcommandstring": case "ingestreportstring": case "destroymessage": case "discoveredletterlabel": case "discoveredlettertext":
                case "beginletter": case "beginletterlabel": case "recoverymessage": case "extratooltip": case "stuffadjective": case "labelnoun":
                case "labelnounpretty": case "descriptionshort": case "helptext": case "baselinestring": case "inspectstringextra": case "customdescription":
                case "effectdescription": case "oneliner": case "quote": case "flavortext": case "flavourtext": case "outcomelabel": case "outcometext":
                case "rolelabel": case "spectatorlabel": case "jobreportstring": case "jobreport": case "gizmolabel": case "commandlabel": case "commanddesc":
                case "desc": case "tooltip": case "tip": case "productdescription": case "researchdescription": case "discoveredmessage":
                case "labelusedinevent": case "gerunds": case "adjective": case "fixedname": case "labelnounplural": case "verbing":
                    return true;
            }
            return n.Contains("label") || n.Contains("desc") || n.Contains("message") || n.Contains("letter") || n.Contains("tooltip") ||
                   n.Contains("title") || n.Contains("flavor") || n.Contains("quote") || n.Contains("gerund") || n.Contains("inspect") ||
                   n.EndsWith("text") || n.EndsWith("string") || n.EndsWith("tip") || n.EndsWith("help") || n.EndsWith("verb");
        }

        private static HashSet<string> modNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool Wanted(string v) => !string.IsNullOrWhiteSpace(v) && !Protect.LooksLikeNonText(v) && !modNames.Contains(v.Trim());

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
