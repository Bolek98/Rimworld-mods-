using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.6.0: rain washes the filth away. Thousands of filth things outdoors (blood, dirt, ash,
    // trash from mods …) cost every tick and make the game stutter. Two optional helpers, both
    // off by default:
    //  * call rain – every ~2500 ticks the outdoor filth on your home maps is counted; above the
    //    threshold, when it is not raining, above 0 °C, the weather is not forced by an event,
    //    the game allows rain and no rain was called in the last 2 days, a rain weather the biome
    //    knows starts (preferably "Rain");
    //  * rain washes all outdoor filth – the game thins only filth marked "rainWashes" while it
    //    rains; this thins the other outdoor filth too, with the same chance. Never indoors.
    public static class RainWash
    {
        private const int CheckEvery = 2500;
        private const int CooldownTicks = 120000;   // 2 days
        private static readonly Dictionary<int, int> lastCalled = new Dictionary<int, int>();
        private static readonly Dictionary<int, int> counts = new Dictionary<int, int>();
        private static AccessTools.FieldRef<WeatherDecider, Map> deciderMap;
        private static AccessTools.FieldRef<WeatherDecider, int> deciderDuration;
        private static AccessTools.FieldRef<WeatherDecider, int> rainAllowedAgain;
        private static AccessTools.FieldRef<SteadyEnvironmentEffects, Map> steadyMap;
        private static AccessTools.FieldRef<SteadyEnvironmentEffects, float> steadyRain;
        private static bool callBroken, washBroken;

        public static void Patch(Harmony h)
        {
            try
            {
                deciderMap = AccessTools.FieldRefAccess<WeatherDecider, Map>("map");
                deciderDuration = AccessTools.FieldRefAccess<WeatherDecider, int>("curWeatherDuration");
                rainAllowedAgain = AccessTools.FieldRefAccess<WeatherDecider, int>("ticksWhenRainAllowedAgain");
                h.Patch(AccessTools.Method(typeof(WeatherDecider), nameof(WeatherDecider.WeatherDeciderTick)),
                    postfix: new HarmonyMethod(typeof(RainWash), nameof(DeciderPostfix)));
            }
            catch (Exception ex)
            {
                callBroken = true;
                MLog.Warn("[Smooth FPS] calling rain not available: " + ex.Message);
            }
            try
            {
                steadyMap = AccessTools.FieldRefAccess<SteadyEnvironmentEffects, Map>("map");
                steadyRain = AccessTools.FieldRefAccess<SteadyEnvironmentEffects, float>("rainRate");
                h.Patch(AccessTools.Method(typeof(SteadyEnvironmentEffects), "DoCellSteadyEffects"),
                    postfix: new HarmonyMethod(typeof(RainWash), nameof(CellPostfix)));
            }
            catch (Exception ex)
            {
                washBroken = true;
                MLog.Warn("[Smooth FPS] rain washing all filth not available: " + ex.Message);
            }
        }

        public static void ResetGame()
        {
            lastCalled.Clear();
            counts.Clear();
        }

        // outdoor filth on all home maps (last count), -1 = not counted yet
        public static int OutdoorFilth()
        {
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
                return -1;
            int sum = 0;
            bool any = false;
            foreach (Map m in Find.Maps)
                if (counts.TryGetValue(m.uniqueID, out int c))
                {
                    sum += c;
                    any = true;
                }
            return any ? sum : -1;
        }

        private static int Count(Map map)
        {
            List<Thing> filth = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth);
            int n = 0;
            for (int i = 0; i < filth.Count; i++)
            {
                Thing t = filth[i];
                if (t == null || !t.Spawned)
                    continue;
                IntVec3 c = t.Position;
                if (map.roofGrid.Roofed(c))
                    continue;
                Room room = c.GetRoom(map);
                if (room == null || room.UsesOutdoorTemperature)
                    n++;
            }
            return n;
        }

        public static void DeciderPostfix(WeatherDecider __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (callBroken || s == null || (!s.rainCall && !s.rainWashAll))
                return;
            try
            {
                Map map = deciderMap(__instance);
                if (map == null || !map.IsPlayerHome)
                    return;
                int now = Find.TickManager.TicksGame;
                if ((now + map.uniqueID * 97) % CheckEvery != 0)
                    return;
                int n = Count(map);
                counts[map.uniqueID] = n;
                if (!s.rainCall || n < s.rainThreshold)
                    return;
                if (lastCalled.TryGetValue(map.uniqueID, out int last) && now - last < CooldownTicks && now >= last)
                    return;
                WeatherManager wm = map.weatherManager;
                if (wm.curWeather == null || wm.curWeather.rainRate > 0f || wm.RainRate > 0f)
                    return;
                float temp = map.mapTemperature.OutdoorTemp;
                if (temp <= 0f)
                    return;
                if (__instance.ForcedWeather != null || now < rainAllowedAgain(__instance))
                    return;
                WeatherDef rain = PickRain(map, temp);
                if (rain == null)
                    return;
                wm.TransitionTo(rain);
                deciderDuration(__instance) = rain.durationRange.RandomInRange;
                lastCalled[map.uniqueID] = now;
                MLog.Info("[Smooth FPS] " + n + " filth outdoors on " + map + " – started " + rain.defName + ".");
                Messages.Message("GT_RainStarted".Translate(), new LookTargets(map.Parent), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception ex)
            {
                callBroken = true;
                MLog.Warn("[Smooth FPS] calling rain: " + ex.Message + " – switched off until restart.");
            }
        }

        // a rain the biome knows, not a bad one, that fits the temperature; "Rain" first
        private static WeatherDef PickRain(Map map, float temp)
        {
            WeatherDef best = null;
            float bestW = 0f;
            var list = map.Biome?.baseWeatherCommonalities;
            if (list == null)
                return null;
            foreach (WeatherCommonalityRecord r in list)
            {
                WeatherDef w = r?.weather;
                if (w == null || r.commonality <= 0f || w.rainRate <= 0f || w.snowRate > 0f || w.isBad || w.favorability == Favorability.VeryBad)
                    continue;
                if (!w.temperatureRange.Includes(temp))
                    continue;
                if (w.defName == "Rain")
                    return w;
                if (r.commonality > bestW)
                {
                    bestW = r.commonality;
                    best = w;
                }
            }
            return best;
        }

        public static void CellPostfix(SteadyEnvironmentEffects __instance, IntVec3 c)
        {
            if (washBroken)
                return;
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.rainWashAll)
                return;
            try
            {
                float rain = steadyRain(__instance);
                if (rain <= 0f)
                    return;
                Map map = steadyMap(__instance);
                if (map == null || !c.InBounds(map) || map.roofGrid.Roofed(c))
                    return;
                List<Thing> things = map.thingGrid.ThingsListAtFast(c);
                for (int i = things.Count - 1; i >= 0; i--)
                {
                    if (i >= things.Count)
                        continue;
                    if (things[i] is Filth f && f.def.filth != null && !f.def.filth.rainWashes && !f.Destroyed)
                    {
                        Room room = c.GetRoom(map);
                        if (room != null && !room.UsesOutdoorTemperature)
                            return;   // never indoors
                        if (Rand.Chance(rain))
                            f.ThinFilth();
                    }
                }
            }
            catch (Exception ex)
            {
                washBroken = true;
                MLog.Warn("[Smooth FPS] rain washing all filth: " + ex.Message + " – switched off until restart.");
            }
        }
    }
}
