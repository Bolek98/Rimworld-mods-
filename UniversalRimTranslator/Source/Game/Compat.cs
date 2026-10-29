using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace UniversalRimTranslator
{
    // 1.0.1: two holes that no translation can fill.
    //  - Name lists (Strings/…/*.txt) that a mod ships only in its own language (Tailin: Chinese only).
    //    The game looks in your language and in English, finds nothing, and the settlement is called
    //    "ERR 5". Now the list is taken from any language the mod has – a name stays a name.
    //  - More Faction Interaction writes the English word " items" behind a category in code
    //    ("rostlinné produkty items"). In another language than English the word is left out.
    public static class Compat
    {
        public static int StringFallbacks;
        private static readonly Dictionary<string, List<string>> found = new Dictionary<string, List<string>>();
        private static readonly string[] preferred = { "English", "ChineseSimplified", "ChineseTraditional" };

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(Translator), nameof(Translator.TryGetTranslatedStringsForFile));
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(Compat), nameof(StringsPrefix)));
            }
            catch (Exception e) { MLog.Warn("[URT] name list fallback not active: " + e.Message); }

            try
            {
                Type t = AccessTools.TypeByName("MoreFactionInteraction.IncidentWorker_ReverseTradeRequest");
                if (t != null)
                {
                    int n = 0;
                    var types = new List<Type> { t };
                    types.AddRange(t.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic));
                    foreach (Type x in types)
                        foreach (MethodInfo mi in x.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        {
                            if (mi.IsAbstract || mi.GetMethodBody() == null || !HasItemsWord(mi)) continue;
                            h.Patch(mi, transpiler: new HarmonyMethod(typeof(Compat), nameof(ItemsTranspiler)));
                            n++;
                        }
                    if (n > 0) MLog.Info("[URT] More Faction Interaction: English word \"items\" removed from " + n + " text(s)");
                }
            }
            catch (Exception e) { MLog.Warn("[URT] More Faction Interaction fix not active: " + e.Message); }
        }

        // ---------- name lists ----------
        public static bool StringsPrefix(string fileName, ref List<string> stringList, ref bool __result)
        {
            try
            {
                if (LanguageDatabase.activeLanguage != null && LanguageDatabase.activeLanguage.TryGetStringsFromFile(fileName, out stringList)) return true;
                if (LanguageDatabase.defaultLanguage != null && LanguageDatabase.defaultLanguage.TryGetStringsFromFile(fileName, out stringList)) return true;
                List<string> any = FromAnyLanguage(fileName);
                if (any == null) return true;      // the game logs its own error
                stringList = any;
                __result = true;
                return false;
            }
            catch
            {
                return true;
            }
        }

        private static List<string> FromAnyLanguage(string fileName)
        {
            lock (found)
            {
                if (found.TryGetValue(fileName, out List<string> cached)) return cached;
                List<string> best = null;
                int bestRank = int.MaxValue;
                string rel = fileName.Replace('/', Path.DirectorySeparatorChar) + ".txt";
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                {
                    foreach (string folder in Folders(mod))
                    {
                        string langs = Path.Combine(folder, "Languages");
                        if (!Directory.Exists(langs)) continue;
                        foreach (string ld in Directory.GetDirectories(langs))
                        {
                            string file = Path.Combine(ld, "Strings", rel);
                            if (!File.Exists(file)) continue;
                            string name = Path.GetFileName(ld);
                            int rank = Array.FindIndex(preferred, p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));
                            if (rank < 0) rank = preferred.Length;
                            if (rank >= bestRank) continue;
                            var lines = File.ReadAllLines(file).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("//")).ToList();
                            if (lines.Count == 0) continue;
                            best = lines;
                            bestRank = rank;
                        }
                    }
                }
                found[fileName] = best;
                if (best != null)
                {
                    StringFallbacks++;
                    MLog.Info("[URT] name list " + fileName + " exists neither in your language nor in English – taken from another language of the mod");
                }
                return best;
            }
        }

        private static IEnumerable<string> Folders(ModContentPack mod)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (mod.RootDir != null && seen.Add(mod.RootDir)) yield return mod.RootDir;
            List<string> fs = null;
            try { fs = mod.foldersToLoadDescendingOrder; } catch { }
            if (fs != null)
                foreach (string f in fs)
                    if (f != null && seen.Add(f)) yield return f;
        }

        // ---------- More Faction Interaction ----------
        private static bool HasItemsWord(MethodInfo mi)
        {
            try
            {
                return PatchProcessor.GetOriginalInstructions(mi).Any(ci => ci.opcode == OpCodes.Ldstr && (ci.operand as string) == " items");
            }
            catch { return false; }
        }

        public static IEnumerable<CodeInstruction> ItemsTranspiler(IEnumerable<CodeInstruction> code)
        {
            MethodInfo word = AccessTools.Method(typeof(Compat), nameof(ItemsWord));
            foreach (CodeInstruction ci in code)
            {
                if (ci.opcode == OpCodes.Ldstr && (ci.operand as string) == " items")
                {
                    var call = new CodeInstruction(OpCodes.Call, word);
                    call.labels.AddRange(ci.labels);
                    call.blocks.AddRange(ci.blocks);
                    yield return call;
                }
                else
                    yield return ci;
            }
        }

        public static string ItemsWord()
        {
            LoadedLanguage a = LanguageDatabase.activeLanguage;
            return a == null || a == LanguageDatabase.defaultLanguage ? " items" : "";
        }
    }
}
