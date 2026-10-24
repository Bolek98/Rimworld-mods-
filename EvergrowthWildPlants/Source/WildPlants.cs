using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EvergrowthWild
{
    // ------------------------------------------------------------------
    // Evergrowth – Wild Plants
    // Which wild plants grow in which biome, how dense and how fast they
    // come back – for every biome from the game, DLCs and all mods.
    // Written from scratch (no code from other mods).
    // ------------------------------------------------------------------

    public class WPSettings : ModSettings
    {
        public bool recommended = true;
        public bool fixBiome = true;          // wild plants follow the world tile biome (fix for map mods)
        public Dictionary<string, string> lists = new Dictionary<string, string>();   // biome -> "plant:value|plant:value"
        public Dictionary<string, float> density = new Dictionary<string, float>();
        public Dictionary<string, float> regrow = new Dictionary<string, float>();
        public Dictionary<string, float> cave = new Dictionary<string, float>();      // cave plant -> weight

        public static WPSettings Recommended()
        {
            return new WPSettings();
        }

        public bool Custom => !recommended;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref fixBiome, "fixBiome", true);
            Scribe_Collections.Look(ref lists, "lists", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref density, "density", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref regrow, "regrow", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref cave, "cave", LookMode.Value, LookMode.Value);
            if (lists == null) lists = new Dictionary<string, string>();
            if (density == null) density = new Dictionary<string, float>();
            if (regrow == null) regrow = new Dictionary<string, float>();
            if (cave == null) cave = new Dictionary<string, float>();
        }
    }

    // ------------------------------------------------------------------
    // Core: original values, the lists in use, applying them to the game
    // ------------------------------------------------------------------
    public static class WP
    {
        public const string Version = "1.1.2";
        public static WPSettings S => WildPlantsMod.Settings;

        private class Orig
        {
            public List<BiomePlantRecord> plants;
            public float density;
            public float regrow;
        }

        private static Dictionary<BiomeDef, Orig> orig;
        private static Dictionary<ThingDef, List<PlantBiomeRecord>> origWild;
        private static Dictionary<ThingDef, float> origCave;
        public static List<BiomeDef> Biomes = new List<BiomeDef>();
        public static List<ThingDef> Plants = new List<ThingDef>();
        public static List<ThingDef> CavePlants = new List<ThingDef>();
        private static readonly HashSet<ThingDef> naturallyWild = new HashSet<ThingDef>();
        private static Dictionary<BiomeDef, Dictionary<ThingDef, float>> defaultLists = new Dictionary<BiomeDef, Dictionary<ThingDef, float>>();

        public static bool Ready => orig != null;

        public static void Snapshot()
        {
            if (orig != null)
                return;
            orig = new Dictionary<BiomeDef, Orig>();
            origWild = new Dictionary<ThingDef, List<PlantBiomeRecord>>();
            origCave = new Dictionary<ThingDef, float>();
            foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
            {
                orig[b] = new Orig
                {
                    plants = (b.wildPlants ?? new List<BiomePlantRecord>()).Where(r => r != null && r.plant != null)
                        .Select(r => new BiomePlantRecord { plant = r.plant, commonality = r.commonality }).ToList(),
                    density = b.plantDensity,
                    regrow = b.wildPlantRegrowDays
                };
            }
            foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (t.plant == null || t.IsBlueprint || t.IsFrame)
                    continue;
                Plants.Add(t);
                origWild[t] = t.plant.wildBiomes == null ? null
                    : t.plant.wildBiomes.Where(r => r != null && r.biome != null)
                        .Select(r => new PlantBiomeRecord { biome = r.biome, commonality = r.commonality }).ToList();
                if (t.plant.cavePlant)
                {
                    origCave[t] = t.plant.cavePlantWeight;
                    CavePlants.Add(t);
                }
            }
            foreach (var o in orig.Values)
                foreach (var r in o.plants)
                    naturallyWild.Add(r.plant);
            foreach (var kv in origWild)
                if (kv.Value != null && kv.Value.Count > 0)
                    naturallyWild.Add(kv.Key);
            Plants.Sort((a, b) => string.Compare(Label(a), Label(b), StringComparison.CurrentCultureIgnoreCase));
            CavePlants.Sort((a, b) => string.Compare(Label(a), Label(b), StringComparison.CurrentCultureIgnoreCase));
            Biomes = DefDatabase<BiomeDef>.AllDefsListForReading
                .OrderBy(b => ModOrder(b.modContentPack))
                .ThenBy(b => Label(b), StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static int ModOrder(ModContentPack p)
        {
            if (p == null) return 9;
            if (p.IsCoreMod) return 0;
            if (p.IsOfficialMod) return 1;
            return 2;
        }

        public static string Label(Def d)
        {
            if (d == null) return "?";
            string l = d.label.NullOrEmpty() ? d.defName : d.LabelCap.ToString();
            return l;
        }

        public static string ModName(Def d)
        {
            if (d?.modContentPack == null) return "?";
            if (d.modContentPack.IsCoreMod) return "RimWorld";
            return d.modContentPack.Name;
        }

        // ---------- lists ----------

        public static Dictionary<ThingDef, float> DefaultList(BiomeDef b)
        {
            Snapshot();
            if (defaultLists.TryGetValue(b, out var cached))
                return cached;
            var d = new Dictionary<ThingDef, float>();
            if (orig.TryGetValue(b, out Orig o))
                foreach (var r in o.plants)
                    d[r.plant] = d.TryGetValue(r.plant, out float v) ? Mathf.Max(v, r.commonality) : r.commonality;
            foreach (var kv in origWild)
            {
                if (kv.Value == null) continue;
                foreach (var r in kv.Value)
                    if (r.biome == b)
                        d[kv.Key] = d.TryGetValue(kv.Key, out float v) ? Mathf.Max(v, r.commonality) : r.commonality;
            }
            defaultLists[b] = d;
            return d;
        }

        // tooltip: where a plant grows by default
        public static string WhereGrows(ThingDef t)
        {
            var names = Biomes.Where(b => DefaultList(b).ContainsKey(t)).Select(b => Label(b)).ToList();
            string head = t.defName + "\n";
            if (names.Count == 0)
                return head + "EWP_GrowsNowhere".Translate();
            return head + "EWP_GrowsIn".Translate(string.Join(", ", names.Take(12)) + (names.Count > 12 ? "…" : ""));
        }

        public static bool IsCustom(BiomeDef b)
        {
            return S.lists.ContainsKey(b.defName) || S.density.ContainsKey(b.defName) || S.regrow.ContainsKey(b.defName);
        }

        // the list the player edits (custom if any, else the default one)
        public static Dictionary<ThingDef, float> EditList(BiomeDef b)
        {
            if (S.lists.TryGetValue(b.defName, out string s))
                return Parse(s, out _);
            return new Dictionary<ThingDef, float>(DefaultList(b));
        }

        // the list the game uses right now
        public static Dictionary<ThingDef, float> ActiveList(BiomeDef b)
        {
            if (S.Custom && S.lists.TryGetValue(b.defName, out string s))
                return Parse(s, out _);
            return DefaultList(b);
        }

        public static Dictionary<ThingDef, float> Parse(string s, out int missing)
        {
            missing = 0;
            var d = new Dictionary<ThingDef, float>();
            if (s.NullOrEmpty())
                return d;
            foreach (string part in s.Split('|'))
            {
                int i = part.LastIndexOf(':');
                if (i <= 0) continue;
                ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(part.Substring(0, i));
                if (t?.plant == null) { missing++; continue; }
                if (float.TryParse(part.Substring(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                    d[t] = Mathf.Max(0f, v);
            }
            return d;
        }

        public static string Ser(Dictionary<ThingDef, float> d)
        {
            var sb = new StringBuilder();
            foreach (var kv in d)
            {
                if (sb.Length > 0) sb.Append('|');
                sb.Append(kv.Key.defName).Append(':').Append(kv.Value.ToString("0.####", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }

        public static void Store(BiomeDef b, Dictionary<ThingDef, float> d)
        {
            S.lists[b.defName] = Ser(d);
        }

        public static float OrigDensity(BiomeDef b) { Snapshot(); return orig.TryGetValue(b, out Orig o) ? o.density : b.plantDensity; }
        public static float OrigRegrow(BiomeDef b) { Snapshot(); return orig.TryGetValue(b, out Orig o) ? o.regrow : b.wildPlantRegrowDays; }
        public static float OrigCave(ThingDef t) { Snapshot(); return origCave.TryGetValue(t, out float v) ? v : t.plant.cavePlantWeight; }

        public static float Density(BiomeDef b) => S.Custom && S.density.TryGetValue(b.defName, out float v) ? v : OrigDensity(b);
        public static float Regrow(BiomeDef b) => S.Custom && S.regrow.TryGetValue(b.defName, out float v) ? v : OrigRegrow(b);

        public static void ResetBiome(BiomeDef b)
        {
            S.lists.Remove(b.defName);
            S.density.Remove(b.defName);
            S.regrow.Remove(b.defName);
        }

        // ---------- what kind of plant ----------
        public enum Kind { Normal, Dead, Stump, Crop, Cave, Water }

        public static Kind KindOf(ThingDef t)
        {
            if (t.IsDeadPlant) return Kind.Dead;
            if (t.plant.isStump) return Kind.Stump;
            if (t.plant.cavePlant) return Kind.Cave;
            if (t.plant.wildPlantUseDistanceToShore) return Kind.Water;
            if (t.plant.Sowable && !naturallyWild.Contains(t))
                return Kind.Crop;
            return Kind.Normal;
        }

        // problems in a list, for the health check
        public static List<string> Problems(BiomeDef b, Dictionary<ThingDef, float> list, int missing)
        {
            var p = new List<string>();
            var on = list.Where(kv => kv.Value > 0f).Select(kv => kv.Key).ToList();
            int dead = on.Count(t => KindOf(t) == Kind.Dead || KindOf(t) == Kind.Stump);
            int crops = on.Count(t => KindOf(t) == Kind.Crop);
            int land = on.Count(t => KindOf(t) == Kind.Normal || KindOf(t) == Kind.Crop);
            if (missing > 0) p.Add("EWP_ProbMissing".Translate(missing));
            if (dead > 0) p.Add("EWP_ProbDead".Translate(dead));
            if (crops > 0) p.Add("EWP_ProbCrops".Translate(crops));
            if (land == 0 && on.Count > 0) p.Add("EWP_ProbNoLand".Translate());
            if (on.Count == 0 && DefaultList(b).Count > 0) p.Add("EWP_ProbEmpty".Translate());
            if (S.Custom && S.density.TryGetValue(b.defName, out float d) && d <= 0.001f && OrigDensity(b) > 0f)
                p.Add("EWP_ProbZeroDensity".Translate());
            return p;
        }

        // ---------- apply to the game ----------
        private static readonly AccessTools.FieldRef<BiomeDef, List<ThingDef>> bCachedWild = AccessTools.FieldRefAccess<BiomeDef, List<ThingDef>>("cachedWildPlants");
        private static readonly AccessTools.FieldRef<BiomeDef, Dictionary<ThingDef, float>> bCachedComm = AccessTools.FieldRefAccess<BiomeDef, Dictionary<ThingDef, float>>("cachedPlantCommonalities");
        private static readonly AccessTools.FieldRef<BiomeDef, float?> bCachedOrder = AccessTools.FieldRefAccess<BiomeDef, float?>("cachedLowestWildPlantOrder");
        private static readonly AccessTools.FieldRef<BiomeDef, int?> bCachedRadius = AccessTools.FieldRefAccess<BiomeDef, int?>("cachedMaxWildPlantsClusterRadius");

        public static void Apply()
        {
            try
            {
                Snapshot();
                bool custom = S.Custom;
                var customBiomes = new HashSet<BiomeDef>();
                foreach (BiomeDef b in Biomes)
                    if (custom && S.lists.ContainsKey(b.defName))
                        customBiomes.Add(b);

                // plants' own biome lists: back to the original, minus biomes with our own list
                foreach (var kv in origWild)
                {
                    if (kv.Value == null)
                    {
                        kv.Key.plant.wildBiomes = null;
                        continue;
                    }
                    kv.Key.plant.wildBiomes = kv.Value.Where(r => !customBiomes.Contains(r.biome))
                        .Select(r => new PlantBiomeRecord { biome = r.biome, commonality = r.commonality }).ToList();
                }

                foreach (BiomeDef b in Biomes)
                {
                    if (customBiomes.Contains(b))
                        b.wildPlants = ActiveList(b).Where(kv => kv.Value > 0f)
                            .Select(kv => new BiomePlantRecord { plant = kv.Key, commonality = kv.Value }).ToList();
                    else
                        b.wildPlants = orig[b].plants.Select(r => new BiomePlantRecord { plant = r.plant, commonality = r.commonality }).ToList();
                    b.plantDensity = Density(b);
                    b.wildPlantRegrowDays = Mathf.Max(0.1f, Regrow(b));
                    bCachedWild(b) = null;
                    bCachedComm(b) = null;
                    bCachedOrder(b) = null;
                    bCachedRadius(b) = null;
                }

                foreach (ThingDef t in CavePlants)
                    t.plant.cavePlantWeight = custom && S.cave.TryGetValue(t.defName, out float w) ? w : OrigCave(t);
                WildPlantSpawner.ResetStaticData();
                ResetSpawners();
            }
            catch (Exception e)
            {
                Log.Error("[Evergrowth – Wild Plants] apply failed: " + e);
            }
        }

        private static readonly string[] spawnerCaches =
        {
            "cachedWildPlants", "cachedMutatorWildPlants", "cachedPlantCommonalities", "cachedPlantsCommonalitiesSum",
            "cachedCavePlantsCommonalitiesSum", "cachedHaveAnyPlantsWhichIgnoreFertility"
        };

        public static void ResetSpawners()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
                return;
            foreach (Map m in Find.Maps)
            {
                WildPlantSpawner sp = m?.wildPlantSpawner;
                if (sp == null) continue;
                var tr = Traverse.Create(sp);
                foreach (string f in spawnerCaches)
                {
                    try { tr.Field(f).SetValue(null); } catch { }
                }
                try { tr.Field("hasWholeMapNumDesiredPlantsCalculated").SetValue(false); } catch { }
            }
        }

        // ---------- import from Choose Wild Plant Spawns (reads its settings file only) ----------
        public static string Import(out int biomes, out int plants, out int skipped)
        {
            biomes = plants = skipped = 0;
            string file = null;
            try
            {
                file = Directory.GetFiles(GenFilePaths.ConfigFolderPath, "Mod_*_ChooseWildPlantSpawns_Mod.xml").FirstOrDefault();
            }
            catch { }
            if (file == null)
                return "EWP_ImportNone".Translate();
            var doc = new XmlDocument();
            doc.Load(file);
            XmlNode rates = doc.SelectSingleNode("//CustomSpawnRates");
            if (rates != null)
            {
                var keys = rates.SelectNodes("keys/li").Cast<XmlNode>().Select(n => n.InnerText).ToList();
                var vals = rates.SelectNodes("values/li").Cast<XmlNode>().Select(n => n.InnerText).ToList();
                for (int i = 0; i < keys.Count && i < vals.Count; i++)
                {
                    BiomeDef b = DefDatabase<BiomeDef>.GetNamedSilentFail(keys[i]);
                    if (b == null) { skipped++; continue; }
                    var d = new Dictionary<ThingDef, float>();
                    foreach (string part in vals[i].Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        int c = part.LastIndexOf(':');
                        if (c <= 0) continue;
                        ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(part.Substring(0, c));
                        if (t?.plant == null || t.IsDeadPlant || t.plant.isStump) { skipped++; continue; }
                        if (float.TryParse(part.Substring(c + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float v) && v > 0f)
                        {
                            d[t] = v;
                            plants++;
                        }
                    }
                    Store(b, d);
                    biomes++;
                }
            }
            XmlNode dens = doc.SelectSingleNode("//CustomDensities");
            if (dens != null)
            {
                var keys = dens.SelectNodes("keys/li").Cast<XmlNode>().Select(n => n.InnerText).ToList();
                var vals = dens.SelectNodes("values/li").Cast<XmlNode>().Select(n => n.InnerText).ToList();
                for (int i = 0; i < keys.Count && i < vals.Count; i++)
                    if (DefDatabase<BiomeDef>.GetNamedSilentFail(keys[i]) != null
                        && float.TryParse(vals[i], NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
                        S.density[keys[i]] = v;
            }
            S.recommended = false;
            return "EWP_ImportDone".Translate(biomes, plants, skipped);
        }

        // ---------- fill the current map now ----------
        public static int FillMap(Map map)
        {
            WildPlantSpawner sp = map?.wildPlantSpawner;
            if (sp == null) return 0;
            int n = 0;
            Patch_BiomeAt.spawning = map;
            try
            {
                float f = sp.CurrentPlantDensityFactor;
                float desired = sp.CurrentWholeMapNumDesiredPlants;
                foreach (IntVec3 c in map.cellsInRandomOrder.GetAll())
                {
                    if (!CanGrowInHere(map, c)) continue;
                    if (sp.CheckSpawnWildPlantAt(c, f, desired, true))
                        n++;
                }
            }
            finally
            {
                Patch_BiomeAt.spawning = null;
            }
            return n;
        }

        // wild plants are put only outside the home area, zones and built rooms
        public static bool CanGrowInHere(Map map, IntVec3 c)
        {
            Area home = map.areaManager.Home;
            if (home != null && home[c]) return false;
            if (c.GetZone(map) != null) return false;
            RoofDef roof = c.GetRoof(map);
            if (roof != null && !roof.isNatural) return false;
            if (c.Fogged(map)) return false;
            return true;
        }

        // biome reported for the map by map mods vs the world tile
        public static BiomeDef MapReportedBiome(Map map)
        {
            Map keep = Patch_BiomeAt.spawning;
            Patch_BiomeAt.spawning = null;
            try { return map.BiomeAt(map.Center); }
            catch { return null; }
            finally { Patch_BiomeAt.spawning = keep; }
        }
    }

    // ------------------------------------------------------------------
    // Fix: while wild plants regrow, use the biome of the world tile.
    // Map mods (e.g. Geological Landforms) can report a different biome for
    // the map than the tile has; the game then picks plants from one biome
    // but weighs them by another, and almost nothing grows on land.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(WildPlantSpawner), "WildPlantSpawnerTickInternal")]
    public static class Patch_SpawnerTick
    {
        private static readonly AccessTools.FieldRef<WildPlantSpawner, Map> mapRef = AccessTools.FieldRefAccess<WildPlantSpawner, Map>("map");

        public static void Prefix(WildPlantSpawner __instance, ref Map __state)
        {
            __state = Patch_BiomeAt.spawning;
            Patch_BiomeAt.spawning = mapRef(__instance);
        }

        public static void Finalizer(Map __state)
        {
            Patch_BiomeAt.spawning = __state;
        }
    }

    [HarmonyPatch(typeof(MapGenUtility), nameof(MapGenUtility.BiomeAt))]
    public static class Patch_BiomeAt
    {
        [ThreadStatic] public static Map spawning;

        [HarmonyPriority(Priority.Last)]
        public static void Postfix(Map map, IntVec3 c, ref BiomeDef __result)
        {
            if (spawning == null || map != spawning || WP.S == null || !WP.S.fixBiome)
                return;
            try
            {
                BiomeDef v = map.MixedBiomeComp?.GetBiomeAt(c) ?? map.Biome;
                if (v != null)
                    __result = v;
            }
            catch
            {
            }
        }
    }

    [StaticConstructorOnStartup]
    public static class WPStartup
    {
        static WPStartup()
        {
            WP.Snapshot();
            WP.Apply();
        }
    }

    // Per game: how overgrown each map should be, and maps that are growing in step by step
    public class WPGameComp : GameComponent
    {
        public Dictionary<int, float> density = new Dictionary<int, float>();   // map id -> overgrowth factor (1 = normal)
        public Dictionary<int, int> fillEnd = new Dictionary<int, int>();       // map id -> tick when it should be overgrown
        private readonly Dictionary<int, float> acc = new Dictionary<int, float>();
        private readonly Dictionary<int, int> have = new Dictionary<int, int>();
        private readonly Dictionary<int, float> want = new Dictionary<int, float>();
        private readonly Dictionary<int, int> countedAt = new Dictionary<int, int>();

        public WPGameComp(Game g) { }

        public static WPGameComp I => Current.Game?.GetComponent<WPGameComp>();

        public static float MapFactor(Map m)
        {
            WPGameComp c = I;
            return c != null && m != null && c.density.TryGetValue(m.uniqueID, out float f) ? f : 1f;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref density, "ewpDensity", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref fillEnd, "ewpFillEnd", LookMode.Value, LookMode.Value);
            if (density == null) density = new Dictionary<int, float>();
            if (fillEnd == null) fillEnd = new Dictionary<int, int>();
        }

        public override void FinalizeInit()
        {
            WP.ResetSpawners();
        }

        public void StartFill(Map m, float days)
        {
            fillEnd[m.uniqueID] = Find.TickManager.TicksGame + Mathf.Max(1, Mathf.RoundToInt(days * 60000f));
            acc[m.uniqueID] = 0f;
            countedAt.Remove(m.uniqueID);
        }

        public void StopFill(Map m)
        {
            fillEnd.Remove(m.uniqueID);
        }

        public bool Filling(Map m, out float daysLeft, out float pct)
        {
            daysLeft = 0f;
            pct = 0f;
            if (m == null || !fillEnd.TryGetValue(m.uniqueID, out int end))
                return false;
            daysLeft = Mathf.Max(0f, (end - Find.TickManager.TicksGame) / 60000f);
            if (want.TryGetValue(m.uniqueID, out float w) && w > 0f && have.TryGetValue(m.uniqueID, out int h))
                pct = Mathf.Clamp01(h / w);
            return true;
        }

        public static int CountWild(Map m)
        {
            return m.listerThings.ThingsInGroup(ThingRequestGroup.NonStumpPlant).Count(t => t.Spawned && t.Position.GetZone(m) == null);
        }

        public override void GameComponentTick()
        {
            if (fillEnd.Count == 0)
                return;
            int now = Find.TickManager.TicksGame;
            if (now % 60 != 0)
                return;
            foreach (int id in fillEnd.Keys.ToList())
            {
                Map m = Find.Maps.FirstOrDefault(x => x.uniqueID == id);
                if (m == null) { fillEnd.Remove(id); continue; }
                try { Step(m, id, now); }
                catch (Exception e) { Log.Warning("[Evergrowth – Wild Plants] growing in: " + e.Message); fillEnd.Remove(id); }
            }
        }

        private void Step(Map m, int id, int now)
        {
            int end = fillEnd[id];
            WildPlantSpawner sp = m.wildPlantSpawner;
            if (!countedAt.TryGetValue(id, out int at) || now - at >= 2500)
            {
                have[id] = CountWild(m);
                want[id] = sp.CurrentWholeMapNumDesiredPlants;
                countedAt[id] = now;
            }
            float w = want[id];
            int h = have[id];
            if (h >= w * 0.97f || now >= end)
            {
                fillEnd.Remove(id);
                Messages.Message("EWP_FillDone".Translate(m.Parent?.LabelCap ?? m.ToString()), MessageTypeDefOf.PositiveEvent, false);
                return;
            }
            float steps = Mathf.Max(1f, (end - now) / 60f);
            acc.TryGetValue(id, out float a);
            a += Mathf.Max(0f, w - h) / steps;
            if (a < 1f) { acc[id] = a; return; }
            float f = sp.CurrentPlantDensityFactor;
            int area = m.Area;
            int tries = Mathf.Min(400, Mathf.CeilToInt(a) * 8);
            Patch_BiomeAt.spawning = m;
            try
            {
                for (int i = 0; i < tries && a >= 1f; i++)
                {
                    IntVec3 c = m.cellsInRandomOrder.Get(Rand.Range(0, area));
                    if (!WP.CanGrowInHere(m, c)) continue;
                    if (sp.CheckSpawnWildPlantAt(c, f, w, false))
                    {
                        a -= 1f;
                        h++;
                    }
                }
            }
            finally
            {
                Patch_BiomeAt.spawning = null;
            }
            // what could not be placed now is not carried on forever
            acc[id] = Mathf.Min(a, 50f);
            have[id] = h;
        }
    }

    // map overgrowth factor for wild plants
    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CurrentPlantDensityFactor), MethodType.Getter)]
    public static class Patch_MapDensity
    {
        private static readonly AccessTools.FieldRef<WildPlantSpawner, Map> mapRef = AccessTools.FieldRefAccess<WildPlantSpawner, Map>("map");

        public static void Postfix(WildPlantSpawner __instance, ref float __result)
        {
            if (Current.Game == null) return;
            __result *= WPGameComp.MapFactor(mapRef(__instance));
        }
    }

    // ------------------------------------------------------------------
    // Mod + settings window
    // ------------------------------------------------------------------
    public class WildPlantsMod : Mod
    {
        public static WPSettings Settings;
        private int tab;
        private BiomeDef sel;
        private Dictionary<ThingDef, float> edit;
        private int editMissing;
        private BiomeDef editFor;
        private Vector2 leftScroll, rightScroll, caveScroll;
        private string biomeSearch = "", plantSearch = "", caveSearch = "";
        private string mapInfo;
        private int mapInfoTick = -99999;
        private int mapInfoId = -1;
        private float fillDays = 10f;
        private bool showAll;

        public WildPlantsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<WPSettings>();
            MUI.Init(WPSettings.Recommended(), new[] { "fixBiome" });
            var h = new Harmony("bolek98.evergrowthwild");
            h.PatchAll();
            MUI.PatchWindow(h, typeof(WildPlantsMod));
        }

        public override string SettingsCategory() => "EWP_Title".Translate();

        public override void WriteSettings()
        {
            base.WriteSettings();
            if (WP.Ready)
                WP.Apply();
        }

        private void Changed()
        {
            if (sel != null && edit != null)
                WP.Store(sel, edit);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            WPSettings s = Settings;
            WP.Snapshot();
            MUI.Version(inRect, "v" + WP.Version + " · Bolek98");
            float y = inRect.y;
            y += MUI.RecommendedCard(new Rect(inRect.x, y, inRect.width, 0f), s, s.recommended, v =>
            {
                s.recommended = v;
                if (v) s.fixBiome = true;
            }, new List<string>
            {
                "EWP_Bullet1".Translate(), "EWP_Bullet2".Translate(), "EWP_Bullet3".Translate(), "EWP_Bullet4".Translate()
            }) + 6f;
            if (s.recommended)
                y += MUI.LockNote(new Rect(inRect.x, y, inRect.width, 0f)) + 4f;

            var tabs = new List<TabRecord>
            {
                new TabRecord("EWP_TabBiomes".Translate(), () => tab = 0, tab == 0),
                new TabRecord("EWP_TabCave".Translate(), () => tab = 1, tab == 1),
                new TabRecord("EWP_TabMap".Translate(), () => tab = 2, tab == 2)
            };
            Rect body = new Rect(inRect.x, y + 32f, inRect.width, inRect.yMax - y - 32f);
            TabDrawer.DrawTabs(body, tabs, 1, 220f);
            body = body.ContractedBy(10f);
            if (tab == 0) DrawBiomes(body);
            else if (tab == 1) DrawCave(body);
            else DrawMap(body);
        }

        // ---------------- biomes ----------------
        private void DrawBiomes(Rect r)
        {
            WPSettings s = Settings;
            Rect left = new Rect(r.x, r.y, 290f, r.height);
            Rect right = new Rect(left.xMax + 12f, r.y, r.width - left.width - 12f, r.height);

            // left: tools + search + biome list
            float ly = left.y;
            MUI.BeginLock(s.recommended);
            if (Widgets.ButtonText(new Rect(left.x, ly, left.width, 28f), "EWP_Import".Translate()))
            {
                string msg = WP.Import(out _, out _, out _);
                edit = null;
                Messages.Message(msg, MessageTypeDefOf.NeutralEvent, false);
            }
            TooltipHandler.TipRegion(new Rect(left.x, ly, left.width, 28f), "EWP_ImportTip".Translate());
            ly += 32f;
            if (Widgets.ButtonText(new Rect(left.x, ly, left.width, 28f), "EWP_ResetAll".Translate()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("EWP_ResetAllConfirm".Translate(), delegate
                {
                    s.lists.Clear(); s.density.Clear(); s.regrow.Clear();
                    edit = null;
                }, true));
            }
            ly += 34f;
            MUI.EndLock();
            biomeSearch = Widgets.TextField(new Rect(left.x, ly, left.width, 28f), biomeSearch);
            if (biomeSearch.NullOrEmpty())
                U.Label(new Rect(left.x + 6f, ly, left.width, 28f), "EWP_SearchBiome".Translate(), MUI.Muted, GameFont.Tiny);
            ly += 32f;

            var list = WP.Biomes.Where(b => Match(b, biomeSearch)).ToList();
            Rect outR = new Rect(left.x, ly, left.width, left.yMax - ly);
            Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * 40f);
            Widgets.BeginScrollView(outR, ref leftScroll, view);
            float yy = 0f;
            foreach (BiomeDef b in list)
            {
                Rect row = new Rect(0f, yy, view.width, 38f);
                if (sel == b) MUI.Fill(row, MUI.OnBg);
                Widgets.DrawHighlightIfMouseover(row);
                bool custom = s.Custom && WP.IsCustom(b);
                RowInfo ri = Info(b);
                bool warn = ri.warn;
                U.Label(new Rect(row.x + 6f, row.y + 1f, row.width - 40f, 22f), WP.Label(b), custom ? MUI.Accent : MUI.Text1);
                U.Label(new Rect(row.x + 6f, row.y + 19f, row.width - 40f, 18f), WP.ModName(b) + " · " + "EWP_NPlants".Translate(ri.count), MUI.Muted, GameFont.Tiny);
                if (warn)
                    U.Label(new Rect(row.xMax - 28f, row.y, 24f, 38f), "!", MUI.Warn, GameFont.Medium, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(row))
                {
                    sel = b;
                    edit = null;
                    rightScroll = Vector2.zero;
                }
                yy += 40f;
            }
            Widgets.EndScrollView();

            DrawBiome(right);
        }

        private class RowInfo { public string key; public bool rec; public float dens; public int count; public bool warn; }
        private readonly Dictionary<BiomeDef, RowInfo> rowInfo = new Dictionary<BiomeDef, RowInfo>();

        private RowInfo Info(BiomeDef b)
        {
            WPSettings s = Settings;
            string key = null;
            s.lists.TryGetValue(b.defName, out key);
            float dens = WP.Density(b);
            if (rowInfo.TryGetValue(b, out RowInfo ri) && ReferenceEquals(ri.key, key) && ri.rec == s.recommended && Mathf.Approximately(ri.dens, dens))
                return ri;
            var plist = WP.ActiveList(b);
            ri = new RowInfo { key = key, rec = s.recommended, dens = dens, count = plist.Count(kv => kv.Value > 0f), warn = WP.Problems(b, plist, 0).Count > 0 };
            rowInfo[b] = ri;
            return ri;
        }

        // shown while Recommended settings lock the editing: one click unlocks
        private float UnlockBanner(Rect r)
        {
            WPSettings s = Settings;
            if (!s.recommended) return 0f;
            Rect b = new Rect(r.x, r.y, r.width, 40f);
            MUI.Fill(b, MUI.CardBg2);
            MUI.Frame(b, MUI.Warn);
            U.Label(new Rect(b.x + 10f, b.y, b.width - 200f, 40f), "EWP_LockedHere".Translate(), MUI.Text1, GameFont.Tiny);
            if (Widgets.ButtonText(new Rect(b.xMax - 186f, b.y + 6f, 180f, 28f), "EWP_Unlock".Translate()))
            {
                s.recommended = false;
                edit = null;
            }
            return 46f;
        }

        private static bool Match(Def d, string q)
        {
            if (q.NullOrEmpty()) return true;
            q = q.ToLowerInvariant();
            return WP.Label(d).ToLowerInvariant().Contains(q) || d.defName.ToLowerInvariant().Contains(q) || WP.ModName(d).ToLowerInvariant().Contains(q);
        }

        private void DrawBiome(Rect r)
        {
            WPSettings s = Settings;
            MUI.Card(r);
            r = r.ContractedBy(12f);
            if (sel == null)
            {
                U.Label(r, "EWP_PickBiome".Translate(), MUI.Muted, GameFont.Small, TextAnchor.UpperLeft);
                return;
            }
            if (edit == null || editFor != sel)
            {
                editFor = sel;
                if (s.Custom && s.lists.TryGetValue(sel.defName, out string str))
                    edit = WP.Parse(str, out editMissing);
                else
                {
                    edit = s.Custom ? WP.EditList(sel) : new Dictionary<ThingDef, float>(WP.DefaultList(sel));
                    editMissing = 0;
                }
            }

            float y = r.y;
            U.Label(new Rect(r.x, y, r.width, 30f), WP.Label(sel), MUI.Text1, GameFont.Medium);
            y += 30f;
            bool custom = s.Custom && WP.IsCustom(sel);
            U.Label(new Rect(r.x, y, r.width, 20f), WP.ModName(sel) + " · " + (custom ? "EWP_Customized".Translate() : "EWP_Default".Translate()),
                custom ? MUI.Accent : MUI.Muted, GameFont.Tiny);
            y += 22f;

            // problems
            foreach (string p in WP.Problems(sel, edit, editMissing))
            {
                float h = Text.CalcHeight("⚠ " + p, r.width) ;
                U.Label(new Rect(r.x, y, r.width, h), "⚠ " + p, MUI.Warn, GameFont.Small, TextAnchor.UpperLeft);
                y += h + 2f;
            }

            y += UnlockBanner(new Rect(r.x, y, r.width, 0f));
            MUI.BeginLock(s.recommended);
            // buttons
            float bw = (r.width - 16f) / 3f;
            if (Widgets.ButtonText(new Rect(r.x, y, bw, 28f), "EWP_AddPlant".Translate()))
                Find.WindowStack.Add(new Dialog_PickPlant(edit, t =>
                {
                    edit[t] = edit.Count > 0 ? Mathf.Clamp(edit.Values.Average(), 0.05f, 2f) : 1f;
                    Changed();
                }));
            if (Widgets.ButtonText(new Rect(r.x + bw + 8f, y, bw, 28f), "EWP_CopyFrom".Translate()))
            {
                var opts = WP.Biomes.Where(b => b != sel).Select(b => new FloatMenuOption(WP.Label(b) + " (" + WP.ModName(b) + ")", () =>
                {
                    edit = new Dictionary<ThingDef, float>(WP.EditList(b));
                    editMissing = 0;
                    Changed();
                })).ToList();
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (Widgets.ButtonText(new Rect(r.x + 2 * (bw + 8f), y, bw, 28f), "EWP_ResetBiome".Translate()))
            {
                WP.ResetBiome(sel);
                edit = null;
            }
            y += 34f;

            // density + regrow
            float half = (r.width - 16f) / 2f;
            float dens = WP.Density(sel);
            float od = WP.OrigDensity(sel);
            U.Label(new Rect(r.x, y, half, 22f), "EWP_Density".Translate(dens.ToString("0.##"), od.ToString("0.##")), MUI.Text1, GameFont.Tiny);
            float rg = WP.Regrow(sel);
            float or = WP.OrigRegrow(sel);
            U.Label(new Rect(r.x + half + 16f, y, half, 22f), "EWP_Regrow".Translate(rg.ToString("0.#"), or.ToString("0.#")), MUI.Text1, GameFont.Tiny);
            TooltipHandler.TipRegion(new Rect(r.x, y, half, 44f), "EWP_DensityTip".Translate());
            TooltipHandler.TipRegion(new Rect(r.x + half + 16f, y, half, 44f), "EWP_RegrowTip".Translate());
            y += 20f;
            float nd = U.Slider(new Rect(r.x, y, half, 22f), dens, 0f, Mathf.Max(3f, od * 2f), 0.01f);
            if (!s.recommended && Mathf.Abs(nd - dens) > 0.0001f) s.density[sel.defName] = nd;
            float nr = U.Slider(new Rect(r.x + half + 16f, y, half, 22f), rg, 1f, Mathf.Max(100f, or * 2f), 0.5f);
            if (!s.recommended && Mathf.Abs(nr - rg) > 0.0001f) s.regrow[sel.defName] = nr;
            y += 28f;

            // plant list
            float cbW = 230f;
            MUI.EndLock();
            plantSearch = Widgets.TextField(new Rect(r.x, y, r.width - cbW - 8f, 26f), plantSearch);
            if (plantSearch.NullOrEmpty())
                U.Label(new Rect(r.x + 6f, y, r.width - cbW, 26f), "EWP_SearchPlant".Translate(), MUI.Muted, GameFont.Tiny);
            Widgets.CheckboxLabeled(new Rect(r.xMax - cbW, y, cbW, 26f), "EWP_ShowAll".Translate(WP.Plants.Count), ref showAll);
            TooltipHandler.TipRegion(new Rect(r.xMax - cbW, y, cbW, 26f), "EWP_ShowAllTip".Translate());
            MUI.BeginLock(s.recommended);
            y += 30f;
            float sum = edit.Values.Sum();
            List<ThingDef> rows;
            if (showAll)
                rows = WP.Plants.Where(t => (edit.ContainsKey(t) || (!t.IsDeadPlant && !t.plant.isStump)) && Match(t, plantSearch))
                    .OrderByDescending(t => edit.TryGetValue(t, out float ev) ? ev : -1f).ThenBy(t => WP.Label(t)).ToList();
            else
                rows = edit.Keys.Where(t => Match(t, plantSearch)).OrderByDescending(t => edit[t]).ThenBy(t => WP.Label(t)).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows.Count * 34f, 30f));
            Widgets.BeginScrollView(outR, ref rightScroll, view);
            if (rows.Count == 0)
                U.Label(new Rect(0f, 0f, view.width, 30f), "EWP_NoPlants".Translate(), MUI.Muted);
            float yy = 0f;
            ThingDef remove = null;
            foreach (ThingDef t in rows)
            {
                if (yy + 34f < rightScroll.y || yy > rightScroll.y + outR.height)
                {
                    yy += 34f;
                    continue;
                }
                Rect row = new Rect(0f, yy, view.width, 32f);
                bool inList = edit.TryGetValue(t, out float v);
                Widgets.DrawHighlightIfMouseover(row);
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 4f, 24f, 24f), t);
                WP.Kind k = WP.KindOf(t);
                string tag = k == WP.Kind.Normal ? "" : " · " + ("EWP_Kind" + k).Translate();
                Color c = k == WP.Kind.Dead || k == WP.Kind.Stump ? MUI.Warn : (inList && v > 0f ? MUI.Text1 : MUI.Muted);
                float nameW = row.width * 0.42f;
                U.Label(new Rect(row.x + 32f, row.y, nameW, 18f), WP.Label(t), c, GameFont.Small);
                U.Label(new Rect(row.x + 32f, row.y + 16f, nameW, 16f), WP.ModName(t) + tag, MUI.Muted, GameFont.Tiny);
                TooltipHandler.TipRegion(new Rect(row.x, row.y, nameW + 32f, 32f), () => WP.WhereGrows(t) + (k == WP.Kind.Normal ? "" : "\n" + ("EWP_Kind" + k + "Tip").Translate()), t.GetHashCode() ^ 77);
                float pct = sum > 0f ? v / sum * 100f : 0f;
                Rect sl = new Rect(row.x + 36f + nameW, row.y + 6f, row.width - nameW - 36f - 120f, 20f);
                float nv = U.Slider(sl, v, 0f, Mathf.Max(5f, v), 0.01f);
                if (!s.recommended && Mathf.Abs(nv - v) > 0.00001f) { edit[t] = nv; Changed(); }
                U.Label(new Rect(sl.xMax + 6f, row.y, 80f, 32f), v.ToString("0.##") + "  (" + pct.ToString("0.#") + " %)", MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
                if (inList && Widgets.ButtonText(new Rect(row.xMax - 28f, row.y + 4f, 24f, 24f), "×"))
                    remove = t;
                yy += 34f;
            }
            Widgets.EndScrollView();
            if (remove != null)
            {
                edit.Remove(remove);
                Changed();
            }
            MUI.EndLock();
        }

        // ---------------- cave plants ----------------
        private void DrawCave(Rect r)
        {
            WPSettings s = Settings;
            U.Label(new Rect(r.x, r.y, r.width, 44f), "EWP_CaveIntro".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            float y = r.y + 46f;
            y += UnlockBanner(new Rect(r.x, y, r.width, 0f));
            caveSearch = Widgets.TextField(new Rect(r.x, y, 300f, 26f), caveSearch);
            if (caveSearch.NullOrEmpty())
                U.Label(new Rect(r.x + 6f, y, 300f, 26f), "EWP_SearchPlant".Translate(), MUI.Muted, GameFont.Tiny);
            MUI.BeginLock(s.recommended);
            if (Widgets.ButtonText(new Rect(r.xMax - 200f, y, 200f, 26f), "EWP_ResetCave".Translate()))
                s.cave.Clear();
            MUI.EndLock();
            y += 32f;
            var rows = WP.CavePlants.Where(t => Match(t, caveSearch)).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(30f, rows.Count * 34f));
            Widgets.BeginScrollView(outR, ref caveScroll, view);
            MUI.BeginLock(s.recommended);
            float yy = 0f;
            foreach (ThingDef t in rows)
            {
                Rect row = new Rect(0f, yy, view.width, 32f);
                Widgets.DrawHighlightIfMouseover(row);
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 4f, 24f, 24f), t);
                float nameW = row.width * 0.42f;
                U.Label(new Rect(row.x + 32f, row.y, nameW, 18f), WP.Label(t), MUI.Text1);
                U.Label(new Rect(row.x + 32f, row.y + 16f, nameW, 16f), WP.ModName(t), MUI.Muted, GameFont.Tiny);
                float o = WP.OrigCave(t);
                float v = s.Custom && s.cave.TryGetValue(t.defName, out float cv) ? cv : o;
                Rect sl = new Rect(row.x + 36f + nameW, row.y + 6f, row.width - nameW - 36f - 130f, 20f);
                float nv = U.Slider(sl, v, 0f, Mathf.Max(5f, o * 2f), 0.01f);
                if (!s.recommended && Mathf.Abs(nv - v) > 0.00001f) s.cave[t.defName] = nv;
                U.Label(new Rect(sl.xMax + 6f, row.y, 124f, 32f), "EWP_CaveValue".Translate(v.ToString("0.##"), o.ToString("0.##")), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
                yy += 34f;
            }
            if (rows.Count == 0)
                U.Label(new Rect(0f, 0f, view.width, 30f), "EWP_NoPlants".Translate(), MUI.Muted);
            MUI.EndLock();
            Widgets.EndScrollView();
        }

        // ---------------- this map ----------------
        private void DrawMap(Rect r)
        {
            WPSettings s = Settings;
            var l = new Listing_Standard();
            l.Begin(r);
            MUI.Section(l, "EWP_SecFix".Translate());
            MUI.BeginLock(s.recommended);
            MUI.Check(l, "EWP_FixBiome".Translate(), "EWP_FixBiomeTip".Translate(), ref s.fixBiome, s, "fixBiome");
            MUI.EndLock();

            Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            MUI.Section(l, "EWP_SecMap".Translate());
            if (map == null)
            {
                l.Label("EWP_NoGame".Translate());
                l.End();
                return;
            }
            if (mapInfo == null || mapInfoId != map.uniqueID || Find.TickManager.TicksGame - mapInfoTick > 2500)
            {
                mapInfo = BuildMapInfo(map);
                mapInfoId = map.uniqueID;
                mapInfoTick = Find.TickManager.TicksGame;
            }
            Text.Font = GameFont.Small;
            l.Label(mapInfo);

            // how overgrown this map should be
            WPGameComp gc = WPGameComp.I;
            MUI.Section(l, "EWP_SecGrow".Translate());
            if (gc != null)
            {
                float f = WPGameComp.MapFactor(map);
                MUI.RowLabel(l, "EWP_MapDensity".Translate(Mathf.RoundToInt(f * 100f)), "EWP_MapDensityTip".Translate(), null, null);
                float nf = U.Slider(l.GetRect(22f), f, 0f, 3f, 0.05f);
                if (Mathf.Abs(nf - f) > 0.0001f)
                {
                    if (Mathf.Abs(nf - 1f) < 0.001f) gc.density.Remove(map.uniqueID);
                    else gc.density[map.uniqueID] = nf;
                    mapInfo = null;
                }
                l.Gap(8f);

                if (gc.Filling(map, out float left, out float pct))
                {
                    l.Label("EWP_FillRunning".Translate(left.ToString("0.#"), Mathf.RoundToInt(pct * 100f)));
                    Rect sb = l.GetRect(32f);
                    if (Widgets.ButtonText(new Rect(sb.x, sb.y, 300f, 32f), "EWP_FillStop".Translate()))
                        gc.StopFill(map);
                }
                else
                {
                    MUI.RowLabel(l, "EWP_FillDays".Translate(fillDays.ToString("0")), "EWP_FillDaysTip".Translate(), null, null);
                    fillDays = U.Slider(l.GetRect(22f), fillDays, 1f, 60f, 1f);
                    l.Gap(6f);
                    Rect gb = l.GetRect(32f);
                    if (Widgets.ButtonText(new Rect(gb.x, gb.y, 300f, 32f), "EWP_FillGradual".Translate(fillDays.ToString("0"))))
                    {
                        gc.StartFill(map, fillDays);
                        Messages.Message("EWP_FillStarted".Translate(fillDays.ToString("0")), MessageTypeDefOf.NeutralEvent, false);
                    }
                    U.Label(new Rect(gb.x + 310f, gb.y, gb.width - 310f, 32f), "EWP_FillGradualTip".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
                }
            }
            l.Gap(10f);
            Rect b = l.GetRect(32f);
            if (Widgets.ButtonText(new Rect(b.x, b.y, 300f, 32f), "EWP_Fill".Translate()))
            {
                int n = 0;
                LongEventHandler.QueueLongEvent(() => { n = WP.FillMap(map); }, "EWP_Filling", false, null);
                LongEventHandler.ExecuteWhenFinished(() =>
                {
                    Messages.Message("EWP_Filled".Translate(n), MessageTypeDefOf.PositiveEvent, false);
                    mapInfo = null;
                });
            }
            U.Label(new Rect(b.x + 310f, b.y, b.width - 310f, 32f), "EWP_FillTip".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
            l.End();
        }

        private static string BuildMapInfo(Map map)
        {
            var sb = new StringBuilder();
            BiomeDef tile = map.Biome;
            BiomeDef rep = WP.MapReportedBiome(map);
            sb.AppendLine("EWP_TileBiome".Translate(WP.Label(tile)));
            if (rep != null && rep != tile)
            {
                sb.AppendLine("EWP_MapBiome".Translate(WP.Label(rep)));
                sb.AppendLine(Settings.fixBiome ? "EWP_MismatchFixed".Translate() : "EWP_MismatchWarn".Translate());
            }
            try
            {
                int have = WPGameComp.CountWild(map);
                float want = map.wildPlantSpawner.CurrentWholeMapNumDesiredPlants;
                sb.AppendLine("EWP_PlantCount".Translate(have, Mathf.RoundToInt(want), want > 0f ? Mathf.RoundToInt(have / want * 100f) : 0));
            }
            catch
            {
            }
            var plist = WP.ActiveList(Settings.fixBiome ? tile : (rep ?? tile));
            sb.Append("EWP_UsedList".Translate(plist.Count(kv => kv.Value > 0f)));
            return sb.ToString().TrimEnd();
        }
    }

    public static class U
    {
        // Slider that only reports a change when the player really drags it. With roundTo, the game's
        // slider returns a rounded value every frame when the stored value is not on the step and plays
        // the drag sound over and over (the ticking).
        public static float Slider(Rect r, float value, float min, float max, float step)
        {
            float nv = Widgets.HorizontalSlider(r, value, min, max, true);
            if (nv == value)
                return value;
            return step > 0f ? Mathf.Round(nv / step) * step : nv;
        }

        public static void Label(Rect r, string text, Color c, GameFont font = GameFont.Small, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            GameFont f = Text.Font;
            TextAnchor a = Text.Anchor;
            Color g = GUI.color;
            Text.Font = font;
            Text.Anchor = anchor;
            GUI.color = c;
            Widgets.Label(r, text);
            GUI.color = g;
            Text.Anchor = a;
            Text.Font = f;
        }
    }

    // ------------------------------------------------------------------
    // Add plants: every plant, tree, grass, bush, mushroom… from the game,
    // all DLCs and all mods – also ones that never grow in this biome.
    // The window stays open, so many plants can be added at once.
    // ------------------------------------------------------------------
    public class Dialog_PickPlant : Window
    {
        private readonly Dictionary<ThingDef, float> current;
        private readonly Action<ThingDef> pick;
        private string q = "";
        private Vector2 scroll;
        private bool hideDead = true;
        private int group;
        private string mod;
        private int added;
        private static readonly string[] Groups = { "All", "Trees", "Grass", "Bushes", "Crops", "Cave", "Water" };

        public override Vector2 InitialSize => new Vector2(720f, 760f);

        public Dialog_PickPlant(Dictionary<ThingDef, float> current, Action<ThingDef> pick)
        {
            this.current = current;
            this.pick = pick;
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public static int GroupOf(ThingDef t)
        {
            WP.Kind k = WP.KindOf(t);
            if (k == WP.Kind.Cave) return 5;
            if (k == WP.Kind.Water) return 6;
            if (k == WP.Kind.Crop) return 4;
            if (t.plant.IsTree) return 1;
            if (t.plant.wildOrder <= 1.01f || t.defName.IndexOf("grass", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            return 3;
        }

        public override void DoWindowContents(Rect r)
        {
            U.Label(new Rect(r.x, r.y, r.width, 30f), "EWP_AddPlant".Translate(), MUI.Text1, GameFont.Medium);
            U.Label(new Rect(r.x, r.y + 30f, r.width, 20f), "EWP_AddIntro".Translate(), MUI.Muted, GameFont.Tiny);
            float y = r.y + 54f;
            q = Widgets.TextField(new Rect(r.x, y, r.width - 400f, 28f), q);
            if (q.NullOrEmpty())
                U.Label(new Rect(r.x + 6f, y, r.width - 400f, 28f), "EWP_SearchPlant".Translate(), MUI.Muted, GameFont.Tiny);
            if (Widgets.ButtonText(new Rect(r.xMax - 392f, y, 200f, 28f), mod == null ? "EWP_AllMods".Translate().ToString() : mod))
            {
                var opts = new List<FloatMenuOption> { new FloatMenuOption("EWP_AllMods".Translate(), () => mod = null) };
                foreach (string m in WP.Plants.Select(WP.ModName).Distinct().OrderBy(x => x))
                {
                    string mm = m;
                    opts.Add(new FloatMenuOption(mm, () => mod = mm));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            Widgets.CheckboxLabeled(new Rect(r.xMax - 182f, y, 182f, 28f), "EWP_HideDead".Translate(), ref hideDead);
            y += 34f;
            float gw = (r.width - 6f * 4f) / Groups.Length;
            for (int g = 0; g < Groups.Length; g++)
            {
                Rect gr = new Rect(r.x + g * (gw + 4f), y, gw, 26f);
                MUI.Fill(gr, group == g ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(gr, group == g ? MUI.Accent : MUI.Border);
                U.Label(gr, ("EWP_Group" + Groups[g]).Translate(), group == g ? MUI.Accent : MUI.Text1, GameFont.Tiny, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(gr)) group = g;
            }
            y += 32f;

            var rows = WP.Plants.Where(t => !current.ContainsKey(t)
                && (!hideDead || (!t.IsDeadPlant && !t.plant.isStump))
                && (group == 0 || GroupOf(t) == group)
                && (mod == null || WP.ModName(t) == mod)
                && Matches(t)).ToList();

            if (Widgets.ButtonText(new Rect(r.x, y, 260f, 28f), "EWP_AddAllShown".Translate(rows.Count)) && rows.Count > 0)
            {
                foreach (ThingDef t in rows) pick(t);
                added += rows.Count;
            }
            if (added > 0)
                U.Label(new Rect(r.x + 270f, y, r.width - 270f, 28f), "EWP_AddedN".Translate(added), MUI.Accent, GameFont.Small);
            y += 34f;

            Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 50f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, rows.Count * 32f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float yy = 0f;
            ThingDef chosen = null;
            foreach (ThingDef t in rows)
            {
                Rect row = new Rect(0f, yy, view.width, 30f);
                if (yy + 30f >= scroll.y && yy <= scroll.y + outR.height)
                {
                    Widgets.DrawHighlightIfMouseover(row);
                    Widgets.DefIcon(new Rect(row.x + 2f, row.y + 3f, 24f, 24f), t);
                    WP.Kind k = WP.KindOf(t);
                    U.Label(new Rect(row.x + 32f, row.y, row.width * 0.5f, 30f), WP.Label(t), MUI.Text1);
                    U.Label(new Rect(row.x + 32f + row.width * 0.5f, row.y, row.width * 0.5f - 110f, 30f),
                        WP.ModName(t) + (k == WP.Kind.Normal ? "" : " · " + ("EWP_Kind" + k).Translate()), MUI.Muted, GameFont.Tiny);
                    TooltipHandler.TipRegion(row, () => WP.WhereGrows(t), t.GetHashCode());
                    if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y + 2f, 86f, 26f), "EWP_AddOne".Translate()))
                        chosen = t;
                }
                yy += 32f;
            }
            Widgets.EndScrollView();
            if (chosen != null)
            {
                pick(chosen);
                added++;
            }
        }

        private bool Matches(ThingDef t)
        {
            if (q.NullOrEmpty()) return true;
            string s = q.ToLowerInvariant();
            return WP.Label(t).ToLowerInvariant().Contains(s) || t.defName.ToLowerInvariant().Contains(s) || WP.ModName(t).ToLowerInvariant().Contains(s);
        }
    }
}
