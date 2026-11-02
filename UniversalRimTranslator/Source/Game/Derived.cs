using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace UniversalRimTranslator
{
    // 1.0.1: texts the game builds from other texts once, while loading – before URT has translated
    // anything. A bill "vyrobit {0}" got the English name of a mod item ("Vyrobit plasteel sword"), a
    // blueprint "{0} (plán)", a corpse "mrtvola {0}", meat "{0} maso" … and stayed half English even
    // after the item itself was translated. Here they are built again from the game's own Czech (or
    // other) template and the translated name – the same way the game does it – and kept up to date
    // whenever a name is translated. URT does not send these texts to the services any more.
    public static class Derived
    {
        private static readonly ConcurrentDictionary<string, string> owned = new ConcurrentDictionary<string, string>();
        private static bool dirty = true;
        private static float dirtySince;
        public static int Rebuilt;

        // item key ("RecipeDef|Make_X.label") of a text built here – the scan and the apply leave it alone
        public static bool Owns(string itemKey) => owned.ContainsKey(itemKey);

        public static void MarkDirty()
        {
            if (!dirty) dirtySince = UnityEngine.Time.realtimeSinceStartup;
            dirty = true;
        }

        // main thread, every frame: rebuild a moment after the last name changed (one pass for a whole batch)
        public static void Tick()
        {
            if (!dirty || UnityEngine.Time.realtimeSinceStartup - dirtySince < 1.5f) return;
            dirty = false;
            try { Refresh(); }
            catch (Exception e) { MLog.WarnOnce("[URT] rebuilding built names: " + e.Message, 0x7e2); }
        }

        private static string Orig(Def d)
        {
            if (d == null) return null;
            Type t = d is ThingDef ? typeof(ThingDef) : d.GetType();
            return Apply.TryOriginal(t.Name + "|" + d.defName + ".label", d.label, out string o) ? o : d.label;
        }

        private static string T(string key, string arg) => key.CanTranslate() ? (string)key.Translate(arg) : null;

        // sets def.<field> to "want" when it still holds what the game built from the English name
        // (or a translation of that, or what we set before)
        private static int Set(Def d, string typeName, string field, string current, string builtFromOriginal, string want, Action<string> set)
        {
            if (want == null || builtFromOriginal == null || current == null) return 0;
            string key = typeName + "|" + d.defName + "." + field;
            bool ours = owned.TryGetValue(key, out string last) && last == current;
            bool fromGame = current == builtFromOriginal;
            bool translatedByUs = Apply.TryOriginal(key, current, out string o) && o == builtFromOriginal;
            if (!ours && !fromGame && !translatedByUs) return 0;     // a mod or a language pack wrote its own text
            owned[key] = want;
            if (current == want) return 0;
            set(want);
            return 1;
        }

        public static void Refresh()
        {
            if (LanguageDatabase.activeLanguage == null || LanguageDatabase.activeLanguage == LanguageDatabase.defaultLanguage) return;
            int n = 0;
            var touched = new HashSet<Def>();

            // bills: "Make_X" / "Make_XBulk", "Administer_X"
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (r?.defName == null) continue;
                try
                {
                    if (r.defName.StartsWith("Make_") && r.products != null && r.products.Count == 1 && r.products[0].thingDef != null)
                    {
                        ThingDef p = r.products[0].thingDef;
                        if (r.defName != "Make_" + p.defName && r.defName != "Make_" + p.defName + "Bulk") continue;
                        string o = Orig(p), now = p.label;
                        if (o == null || now == null) continue;
                        foreach (int adj in Adjusted(r, p))
                        {
                            string so = adj == 1 ? o : o + " x" + adj, sn = adj == 1 ? now : now + " x" + adj;
                            int k = Set(r, "RecipeDef", "label", r.label, T("RecipeMake", so), T("RecipeMake", sn), v => r.label = v)
                                  + Set(r, "RecipeDef", "jobString", r.jobString, T("RecipeMakeJobString", so), T("RecipeMakeJobString", sn), v => r.jobString = v);
                            if (k > 0 || owned.ContainsKey("RecipeDef|" + r.defName + ".label"))
                            {
                                n += k;
                                n += Set(r, "RecipeDef", "description", r.description, Desc(r, true), Desc(r, false), v => r.description = v);
                                if (k > 0) touched.Add(r);
                                break;
                            }
                        }
                    }
                    else if (r.defName.StartsWith("Administer_"))
                    {
                        ThingDef drug = DefDatabase<ThingDef>.GetNamedSilentFail(r.defName.Substring("Administer_".Length));
                        if (drug == null) continue;
                        string o = Orig(drug), now = drug.label;
                        int k = Set(r, "RecipeDef", "label", r.label, T("RecipeAdminister", o), T("RecipeAdminister", now), v => r.label = v)
                              + Set(r, "RecipeDef", "jobString", r.jobString, T("RecipeAdministerJobString", o), T("RecipeAdministerJobString", now), v => r.jobString = v);
                        if (k > 0) { n += k; touched.Add(r); }
                    }
                }
                catch { }
            }

            // blueprints, frames, corpses, meat
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d?.defName == null) continue;
                try
                {
                    int k = 0;
                    if (d.defName.StartsWith("Blueprint_") || d.defName.StartsWith("Frame_"))
                    {
                        bool frame = d.defName.StartsWith("Frame_");
                        string baseName = d.defName.Substring(frame ? 6 : 10);
                        if (baseName.StartsWith("Install_")) baseName = baseName.Substring(8);
                        BuildableDef b = (BuildableDef)DefDatabase<ThingDef>.GetNamedSilentFail(baseName) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(baseName);
                        if (b == null) continue;
                        string extra = (frame ? "FrameLabelExtra" : "BlueprintLabelExtra").Translate();
                        string o = Orig(b);
                        k += Set(d, "ThingDef", "label", d.label, o + extra, b.label + extra, v => d.label = v);
                    }
                    else if (d.defName.StartsWith("Corpse_"))
                    {
                        ThingDef race = DefDatabase<ThingDef>.GetNamedSilentFail(d.defName.Substring(7));
                        if (race == null) continue;
                        string o = Orig(race);
                        k += Set(d, "ThingDef", "label", d.label, T("CorpseLabel", o), T("CorpseLabel", race.label), v => d.label = v);
                        k += Set(d, "ThingDef", "description", d.description, T("CorpseDesc", o), T("CorpseDesc", race.label), v => d.description = v);
                    }
                    else if (d.defName.StartsWith("Meat_"))
                    {
                        ThingDef race = DefDatabase<ThingDef>.GetNamedSilentFail(d.defName.Substring(5));
                        if (race?.race == null || !race.race.meatLabel.NullOrEmpty()) continue;
                        string o = Orig(race);
                        k += Set(d, "ThingDef", "label", d.label, T("MeatLabel", o), T("MeatLabel", race.label), v => d.label = v);
                        string dk = race.race.Humanlike ? "MeatHumanDesc" : race.race.FleshType == FleshTypeDefOf.Insectoid ? "MeatInsectDesc" : "MeatDesc";
                        k += Set(d, "ThingDef", "description", d.description, T(dk, o), T(dk, race.label), v => d.description = v);
                    }
                    if (k > 0) { n += k; touched.Add(d); }
                }
                catch { }
            }

            foreach (Def d in touched)
                try { d.ClearCachedData(); } catch { }
            if (n > 0)
            {
                Rebuilt += n;
                MLog.Info("[URT] rebuilt " + n + " names the game had made from English names (bills, blueprints, corpses, meat)");
            }
        }

        private static IEnumerable<int> Adjusted(RecipeDef r, ThingDef p)
        {
            if (!r.defName.EndsWith("Bulk")) { yield return 1; yield break; }
            int per = Math.Max(1, p.recipeMaker?.productCount ?? 1);
            int c = r.products[0].count;
            yield return Math.Max(1, c / per);
            if (c != c / per) yield return c;
        }

        // "RecipeMakeDescription" with the products' names, English (original) or current
        private static string Desc(RecipeDef r, bool original)
        {
            if (!"RecipeMakeDescription".CanTranslate()) return null;
            var items = r.products.Select(p =>
            {
                string l = original ? Orig(p.thingDef) : p.thingDef.label;
                return p.count != 1 ? l + " x" + p.count : Find.ActiveLanguageWorker.WithIndefiniteArticle(l);
            }).ToArray();
            return "RecipeMakeDescription".Translate(items.ToCommaList(useAnd: true));
        }
    }
}
