using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // weather by month: the biome's own weather chances are multiplied by the month's factor
    // (by the climate – in the south the seasons are turned around), and three new weathers
    // come in their months where the biome fits
    public static class WeatherMonths
    {
        // climate month 0 = January (north) … 11 = December
        private static readonly Dictionary<string, float[]> factor = new Dictionary<string, float[]>
        {
            //                                 Jan   Feb   Mar   Apr   May   Jun   Jul   Aug   Sep   Oct   Nov   Dec
            ["Clear"] =             new[] { 0.9f, 0.9f, 1.0f, 0.9f, 1.1f, 1.2f, 1.2f, 1.2f, 1.1f, 1.0f, 0.8f, 0.9f },
            ["Rain"] =              new[] { 0.6f, 0.7f, 1.0f, 1.4f, 1.2f, 1.0f, 0.9f, 0.9f, 1.0f, 1.2f, 1.3f, 0.8f },
            ["FoggyRain"] =         new[] { 0.8f, 0.8f, 1.1f, 1.2f, 0.9f, 0.7f, 0.6f, 0.7f, 1.0f, 1.3f, 1.5f, 1.0f },
            ["Fog"] =               new[] { 1.2f, 1.1f, 1.2f, 0.9f, 0.7f, 0.6f, 0.5f, 0.6f, 1.0f, 1.5f, 2.0f, 1.4f },
            ["RainyThunderstorm"] = new[] { 0.3f, 0.3f, 0.6f, 0.9f, 1.3f, 1.6f, 1.8f, 1.7f, 1.1f, 0.7f, 0.4f, 0.3f },
            ["DryThunderstorm"] =   new[] { 0.3f, 0.3f, 0.5f, 0.8f, 1.2f, 1.5f, 1.7f, 1.7f, 1.2f, 0.7f, 0.4f, 0.3f },
            ["SnowGentle"] =        new[] { 1.5f, 1.4f, 1.0f, 0.6f, 0.5f, 0.5f, 0.5f, 0.5f, 0.6f, 0.8f, 1.1f, 1.5f },
            ["SnowHard"] =          new[] { 1.6f, 1.5f, 1.0f, 0.6f, 0.5f, 0.5f, 0.5f, 0.5f, 0.6f, 0.7f, 1.0f, 1.3f },
        };

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(WeatherDecider), "CurrentWeatherCommonality");
                if (m != null) h.Patch(m, postfix: new HarmonyMethod(typeof(WeatherMonths), nameof(Postfix)));
                else Log.Warning("[Seasons & Holidays] weather chances not found – weather by month is off.");
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] weather: " + e.Message); }
        }

        public static int ClimateMonthNow(Map map)
        {
            Months.Now(map, out int m, out _);
            return Months.ClimateMonth(m, Find.WorldGrid.LongLatOf(map.Tile).y);
        }

        public static void Postfix(WeatherDef weather, Map ___map, ref float __result)
        {
            try
            {
                if (SHMod.S == null || ___map == null || weather == null) return;
                if (weather.defName.StartsWith("SH_"))
                {
                    __result = SHMod.S.newWeathers ? NewWeather(weather, ___map) : 0f;
                    return;
                }
                if (!SHMod.S.monthWeather || __result <= 0f) return;
                if (!factor.TryGetValue(weather.defName, out float[] f)) return;
                float k = f[ClimateMonthNow(___map)];
                __result *= Mathf.Max(0.05f, 1f + (k - 1f) * SHMod.S.weatherStrength);
            }
            catch { }
        }

        private static bool BiomeHas(Map map, string def)
        {
            foreach (WeatherCommonalityRecord r in map.Biome.baseWeatherCommonalities)
                if (r.weather != null && r.weather.defName == def && r.commonality > 0f) return true;
            return false;
        }

        // the chances of the new weathers (the clear weather of a temperate biome is about 18)
        private static float NewWeather(WeatherDef w, Map map)
        {
            if (!w.temperatureRange.Includes(map.mapTemperature.OutdoorTemp)) return 0f;
            if (map.weatherManager.curWeather == w) return 0f;   // one day of it is enough
            int c = ClimateMonthNow(map);
            float s = Mathf.Max(0.2f, SHMod.S.weatherStrength);
            switch (w.defName)
            {
                case "SH_Hoarfrost":
                    return (c == 11 || c == 0 || c == 1) && BiomeHas(map, "SnowGentle") ? 3f * s : 0f;
                case "SH_IndianSummer":
                    return (c == 8 || c == 9) && BiomeHas(map, "Clear") ? 4f * s : 0f;
                case "SH_SpringShowers":
                    return (c == 3 || c == 4) && BiomeHas(map, "Rain") && GenDate.DaysPassedSinceSettle >= 8 ? 4f * s : 0f;
            }
            // 0.3.0: the new weathers – after the first week, in the right climate and months
            if (GenDate.DaysPassedSinceSettle < 8 && w.favorability < Favorability.Neutral) return 0f;
            if (w.rainRate > 0.1f && map.gameConditionManager.ActiveConditions.Any(x => x.def.preventRain)) return 0f;
            float lat = Mathf.Abs(Find.WorldGrid.LongLatOf(map.Tile).y);
            float rain = map.TileInfo.rainfall;
            string biome = map.Biome.defName ?? "";
            bool winter = c == 11 || c == 0 || c == 1, summer = c >= 5 && c <= 7;
            bool dry = rain < 600f || biome.Contains("Desert") || biome.Contains("Arid");
            bool tropic = biome.Contains("Tropical") || (rain > 1800f && lat < 25f);
            switch (w.defName)
            {
                case "SH_Aurora":
                    return lat >= 52f && (winter || c == 2 || c == 10) ? 2.5f * s : 0f;
                case "SH_Blizzard":
                    return BiomeHas(map, "SnowHard") && (winter || c == 2) ? 1.5f * s : 0f;
                case "SH_FreezingRain":
                    return BiomeHas(map, "Rain") && BiomeHas(map, "SnowGentle") && (winter || c == 2 || c == 10) ? 2f * s : 0f;
                case "SH_Hail":
                    return BiomeHas(map, "RainyThunderstorm") && (c >= 4 && c <= 7) ? 1.2f * s : 0f;
                case "SH_Heatwave":
                    return summer || dry ? 2f * s : 0f;
                case "SH_Sandstorm":
                    return dry && !BiomeHas(map, "SnowHard") ? (summer ? 3f : 1.5f) * s : 0f;
                case "SH_Monsoon":
                    return tropic && (c >= 5 && c <= 8) ? 4f * s : 0f;
            }
            return 0f;
        }
    }
}
