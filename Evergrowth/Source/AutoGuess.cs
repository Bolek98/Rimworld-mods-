using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // 1.9.0 Plants from other mods: many are not listed in any biome and some
    // have odd flags (a "tree" that gives wood but is not marked as a tree).
    // Evergrowth guesses from the plant's own values:
    //   * kind – tree (wood, tree category), crop (can be sown) or wild,
    //   * cold-hardy – it already grows at 0 °C or below,
    //   * wild plants of mods that no biome lists are added to the "wild plants"
    //     group (and to "cold biomes" when cold-hardy), so the Hardy plants
    //     settings reach them too.
    // The Plants tab marks such plants "auto" with the reason.
    // ------------------------------------------------------------------
    public static class AutoGuess
    {
        public sealed class Info
        {
            public int kind;            // 0 wild, 1 tree, 2 crop
            public bool kindGuessed;    // differs from the game's own flags
            public bool cold;
            public bool inBiome;
            public bool modded;
        }

        private static readonly Dictionary<ThingDef, Info> cache = new Dictionary<ThingDef, Info>();
        private static HashSet<ThingDef> inBiomes;

        public static bool On => HardyPlantsMod.Settings == null || HardyPlantsMod.Settings.autoGuess;

        public static void Reset()
        {
            cache.Clear();
            inBiomes = null;
        }

        public static bool IsModded(ThingDef d) => d?.modContentPack != null && !d.modContentPack.IsOfficialMod;

        private static bool InAnyBiome(ThingDef d)
        {
            if (inBiomes == null)
            {
                inBiomes = new HashSet<ThingDef>();
                foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
                {
                    try
                    {
                        foreach (ThingDef p in b.AllWildPlants)
                            if (p != null)
                                inBiomes.Add(p);
                    }
                    catch
                    {
                    }
                }
            }
            return inBiomes.Contains(d);
        }

        public static Info Of(ThingDef d)
        {
            if (d?.plant == null)
                return null;
            Info i;
            if (cache.TryGetValue(d, out i))
                return i;
            PlantProperties pp = d.plant;
            int game = pp.IsTree ? 1 : pp.Sowable ? 2 : 0;
            int guess = game;
            bool modded = IsModded(d);
            if (modded && game != 1)
            {
                bool wood = pp.harvestedThingDef == ThingDefOf.WoodLog || pp.harvestTag == "Wood" || pp.treeCategory != TreeCategory.None;
                if (wood && !pp.Sowable)
                    guess = 1;
            }
            i = new Info
            {
                kind = guess,
                kindGuessed = guess != game,
                cold = pp.minGrowthTemperature <= 0f,
                inBiome = InAnyBiome(d),
                modded = modded
            };
            cache[d] = i;
            return i;
        }

        public static int KindOf(ThingDef d)
        {
            Info i = On ? Of(d) : null;
            if (i != null)
                return i.kind;
            return d.plant.IsTree ? 1 : d.plant.Sowable ? 2 : 0;
        }

        // a wild plant of a mod that no biome lists
        public static bool ExtraWild(ThingDef d)
        {
            if (!On)
                return false;
            Info i = Of(d);
            return i != null && i.modded && !i.inBiome && i.kind == 0 && !d.plant.isStump;
        }

        public static bool ExtraCold(ThingDef d) => ExtraWild(d) && Of(d).cold;

        // short tag for the Plants tab, null when nothing was guessed
        public static string Tag(ThingDef d)
        {
            if (!On)
                return null;
            Info i = Of(d);
            if (i == null || !i.modded || (!i.kindGuessed && i.inBiome))
                return null;
            return "HardyPlants_AutoTag".Translate();
        }

        public static string Tip(ThingDef d)
        {
            Info i = Of(d);
            if (i == null)
                return null;
            string kind = ("HardyPlants_Kind" + i.kind).Translate();
            string text = "HardyPlants_AutoTip".Translate(d.modContentPack?.Name ?? "?", kind,
                d.plant.minGrowthTemperature.ToStringTemperature("F0"), d.plant.growDays.ToString("0.#"));
            if (i.kindGuessed)
                text += "\n" + "HardyPlants_AutoKindGuessed".Translate(kind);
            if (!i.inBiome && i.kind == 0)
                text += "\n" + (i.cold ? "HardyPlants_AutoAddedCold" : "HardyPlants_AutoAddedWild").Translate();
            return text;
        }
    }
}
