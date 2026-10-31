using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.6.6: brighter nights – only the picture. After the game has set the light colour of the map
    // (SkyManager.SkyManagerUpdate) the colour of the light overlay and the fog of war is lifted toward
    // white, more the darker the sky is. The sky glow the game uses for plants, darkness moods, work
    // speed and the like (SkyManager.CurSkyGlow) is not touched.
    // ---------------------------------------------------------------------------
    public static class BrightNights
    {
        private static readonly AccessTools.FieldRef<SkyManager, Map> mapRef = AccessTools.FieldRefAccess<SkyManager, Map>("map");
        private static bool failed;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(SkyManager), nameof(SkyManager.SkyManagerUpdate)), postfix: new HarmonyMethod(typeof(BrightNights), nameof(Postfix)));
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] brighter nights skipped: " + e.Message); }
        }

        public static void Postfix(SkyManager __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.brighterNights || failed)
                return;
            try
            {
                Map map = mapRef(__instance);
                if (map == null || map != Find.CurrentMap || (map.Biome != null && map.Biome.disableSkyLighting))
                    return;
                // 0 in full daylight, 1 in the darkest night
                float dark = Mathf.Clamp01(1f - __instance.CurSkyGlow);
                float k = Mathf.Clamp(s.nightLift, 0f, 0.5f) * dark;
                if (k <= 0.001f)
                    return;
                Color c = MatBases.LightOverlay.color;
                Color lifted = Color.Lerp(c, new Color(1f, 1f, 1f, c.a), k);
                lifted.a = c.a;
                MatBases.LightOverlay.color = lifted;
                Color f = MatBases.FogOfWar.color;
                Color lf = Color.Lerp(f, Color.white, k * 0.5f);
                lf.a = f.a;
                MatBases.FogOfWar.color = lf;
            }
            catch (Exception e)
            {
                failed = true;
                MLog.Warn("[Smooth FPS] brighter nights off: " + e.Message);
            }
        }
    }
}
