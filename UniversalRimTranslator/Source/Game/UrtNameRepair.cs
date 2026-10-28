using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace UniversalRimTranslator
{
    // "ERR" on the world map and in quests (since 0.9.57).
    // The game writes "ERR" when it cannot build a text from grammar rules. A machine translation
    // that changed a [token] inside a rule (or added one that does not exist) breaks the rule.
    // Names that were already saved as "ERR" (settlements, factions, world features, landmarks,
    //     sites) are generated again by the game's own name makers when a save loads.
    [StaticConstructorOnStartup]
    internal static class UrtNameRepair
    {
        static UrtNameRepair()
        {
            try
            {
                var h = new Harmony("bolek98.urt.namerepair");
                h.Patch(AccessTools.Method(typeof(Game), nameof(Game.FinalizeInit)),
                    postfix: new HarmonyMethod(typeof(UrtNameRepair), nameof(FinalizeInitPostfix)));
            }
            catch (Exception e)
            {
                MLog.Warn("[Universal RimTranslator] name repair not active: " + e.Message);
            }
        }

        private static bool IsErr(string name)
        {
            if (string.IsNullOrEmpty(name))
                return false;
            string t = name.Trim();
            return t == "ERR" || t == "err" || t.StartsWith("ERR:", StringComparison.Ordinal) || t.StartsWith("ERR ", StringComparison.Ordinal);
        }

        public static void FinalizeInitPostfix()
        {
            int fixedCount = 0;
            try
            {
                World world = Find.World;
                if (world == null)
                    return;

                // factions
                foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
                {
                    if (f == null || !IsErr(f.Name) || f.def?.factionNameMaker == null)
                        continue;
                    string n = Try(() => NameGenerator.GenerateName(f.def.factionNameMaker, Find.FactionManager.AllFactionsListForReading.Where(x => x != f).Select(x => x.Name)));
                    if (n != null) { f.Name = n; fixedCount++; }
                }

                // settlements and other named world objects (sites, camps …)
                foreach (WorldObject wo in Find.WorldObjects.AllWorldObjects)
                {
                    if (wo is Settlement s && IsErr(s.Name))
                    {
                        string n = Try(() => SettlementNameGenerator.GenerateSettlementName(s, s.Faction?.def?.settlementNameMaker));
                        if (n != null) { s.Name = n; fixedCount++; }
                    }
                    else if (!(wo is Settlement) && wo is INameableWorldObject nw && IsErr(nw.Name) && wo.def?.nameMaker != null)
                    {
                        string n = Try(() => NameGenerator.GenerateName(wo.def.nameMaker, null, false, "r_name"));
                        if (n != null) { nw.Name = n; fixedCount++; }
                    }
                }

                // world features (mountains, seas …)
                bool features = false;
                if (world.features?.features != null)
                {
                    var used = new HashSet<string>(world.features.features.Where(x => x != null && !IsErr(x.name)).Select(x => x.name));
                    foreach (WorldFeature wf in world.features.features)
                    {
                        if (wf == null || !IsErr(wf.name) || wf.def?.nameMaker == null)
                            continue;
                        string n = Try(() => NameGenerator.GenerateName(wf.def.nameMaker, used));
                        if (n != null) { wf.name = n; used.Add(n); fixedCount++; features = true; }
                    }
                    if (features)
                        world.features.textsCreated = false;
                }

                // landmarks (1.6+)
                if (world.landmarks?.landmarks != null)
                {
                    foreach (Landmark lm in world.landmarks.landmarks.Values)
                    {
                        if (lm == null || !IsErr(lm.name) || lm.def?.nameMaker == null)
                            continue;
                        string n = Try(() => NameGenerator.GenerateName(lm.def.nameMaker, null, false, "r_name"));
                        if (n != null) { lm.name = n; fixedCount++; }
                    }
                }

                // quests: only the name can be made again; the text of an existing quest stays
                if (Find.QuestManager?.QuestsListForReading != null)
                    foreach (Quest q in Find.QuestManager.QuestsListForReading)
                        if (q != null && IsErr(q.name) && q.root != null)
                        {
                            q.name = q.root.LabelCap.ToString();
                            fixedCount++;
                        }
            }
            catch (Exception e)
            {
                MLog.Warn("[Universal RimTranslator] name repair: " + e.Message);
            }
            if (fixedCount > 0)
                MLog.Game("[Universal RimTranslator] repaired " + fixedCount + " names that showed as ERR (broken grammar from older translations).");
        }

        private static string Try(Func<string> make)
        {
            try
            {
                string n = make();
                return string.IsNullOrEmpty(n) || IsErr(n) ? null : n;
            }
            catch
            {
                return null;
            }
        }
    }
}
