using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.3.2: World tick. Every 1.5 in-game days the game checks, for every planet layer, how many
    // generated locations (Odyssey landmarks, sites …) exist. It does so by walking EVERY tile of the
    // layer and, for each tile, EVERY world object – on a big world with many objects that is half a
    // second or more of a frozen game. Here the same number is counted from the world objects alone
    // (each tile once), and the game's own generation runs exactly as before when it is too low.
    public static class LocationGeneratorFast
    {
        private static AccessTools.FieldRef<WorldComponent_LocationGenerator, int> target;
        private static MethodInfo generate;
        private static bool broken;

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo tick = AccessTools.Method(typeof(WorldComponent_LocationGenerator), nameof(WorldComponent_LocationGenerator.WorldComponentTick));
                generate = AccessTools.Method(typeof(WorldComponent_LocationGenerator), "GenerateUntilTarget", new[] { typeof(PlanetLayer) });
                target = AccessTools.FieldRefAccess<WorldComponent_LocationGenerator, int>("worldLocationsTarget");
                if (tick == null || generate == null || target == null)
                    return;
                h.Patch(tick, prefix: new HarmonyMethod(typeof(LocationGeneratorFast), nameof(Prefix)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] world location check not sped up: " + ex.Message);
            }
        }

        private static readonly HashSet<int> tiles = new HashSet<int>();

        public static bool Prefix(WorldComponent_LocationGenerator __instance)
        {
            if (broken)
                return true;
            try
            {
                World world = Find.World;
                foreach (KeyValuePair<int, PlanetLayer> kv in Find.WorldGrid.PlanetLayers)
                {
                    PlanetLayer layer = kv.Value;
                    if (!GenTicks.IsTickInterval(layer.GetHashCode().HashOffset(), 90000))
                        continue;
                    int want = Mathf.RoundToInt(layer.Def.generatedLocationFactor * (float)target(__instance));
                    tiles.Clear();
                    List<WorldObject> all = world.worldObjects.AllWorldObjects;
                    for (int i = 0; i < all.Count; i++)
                    {
                        WorldObject o = all[i];
                        if (o != null && o.isGeneratedLocation && o.Tile.Valid && o.Tile.Layer == layer)
                            tiles.Add(o.Tile.tileId);
                    }
                    if (tiles.Count < want)
                        generate.Invoke(__instance, new object[] { layer });
                }
                return false;
            }
            catch (Exception ex)
            {
                broken = true;
                MLog.Warn("[Smooth FPS] world location check: " + ex.Message + " – the game's own check is used again.");
                return true;
            }
        }
    }
}

namespace GraphicsTuner
{
    // 1.7.0: once per in-game day the game recalculates how hard it is to travel over EVERY world
    // tile (season, snow) – in one tick. On a big world that is a 200+ ms stop. Here the same
    // recalculation runs in parts (a few thousand tiles per tick); until it is done the old values
    // are used, exactly as during the rest of the day.
    public static class WorldPathSpread
    {
        private static AccessTools.FieldRef<WorldPathGrid, Dictionary<PlanetLayer, int>> dayRef;
        private static readonly Dictionary<PlanetLayer, int> progress = new Dictionary<PlanetLayer, int>();
        private static readonly Dictionary<PlanetLayer, bool> recache = new Dictionary<PlanetLayer, bool>();
        private static bool broken;
        private const int PerTick = 2500;

        public static void Patch(Harmony h)
        {
            try
            {
                dayRef = AccessTools.FieldRefAccess<WorldPathGrid, Dictionary<PlanetLayer, int>>("layerAllPathCostsRecalculatedDayOfYear");
                h.Patch(AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.WorldPathGridTick)),
                    prefix: new HarmonyMethod(typeof(WorldPathSpread), nameof(Prefix)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] world path spread could not start: " + ex.Message);
            }
        }

        public static bool Prefix(WorldPathGrid __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (broken || s == null || !s.smartTick || dayRef == null)
                return true;
            try
            {
                int today = GenDate.DayOfYear(GenTicks.TicksAbs, 0f);
                Dictionary<PlanetLayer, int> days = dayRef(__instance);
                foreach (KeyValuePair<int, PlanetLayer> kv in Find.WorldGrid.PlanetLayers)
                {
                    PlanetLayer layer = kv.Value;
                    if (!days.TryGetValue(layer, out int d) || d == today)
                        continue;
                    progress.TryGetValue(layer, out int from);
                    recache.TryGetValue(layer, out bool any);
                    int to = Math.Min(layer.TilesCount, from + PerTick);
                    for (int i = from; i < to; i++)
                    {
                        __instance.RecalculatePerceivedMovementDifficultyAt(new PlanetTile(i, layer), out bool need);
                        any |= need;
                    }
                    if (to >= layer.TilesCount)
                    {
                        progress.Remove(layer);
                        recache.Remove(layer);
                        days[layer] = today;
                        if (any)
                            Find.WorldReachability.ClearCache();
                    }
                    else
                    {
                        progress[layer] = to;
                        recache[layer] = any;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                broken = true;
                progress.Clear();
                recache.Clear();
                MLog.Warn("[Smooth FPS] world path spread: " + ex.Message + " – the game's own recalculation is used again.");
                return true;
            }
        }
    }
}
