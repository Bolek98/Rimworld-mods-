using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.6.3 (1.7 / Waylith): the engine starts moving textures out of the graphics card into normal
    // memory already when the card is about 80 % full (or 512 MB before its limit), so pictures look
    // washed out or arrive late long before the card is really full. With this switch textures stay in
    // the card until only ~200 MB are left; when the card is really full the engine still makes room
    // itself (otherwise the game would run out of video memory).
    // ---------------------------------------------------------------------------
    public static class KeepTextures
    {
        private static MethodInfo high;

        public static bool Possible
        {
            get
            {
                if (high == null)
                {
                    try
                    {
                        Type t = Type.GetType("Waylith.Rendering.TextureResidency, Waylith");
                        high = t == null ? null : AccessTools.Method(t, "HighWatermark", new[] { typeof(long) });
                    }
                    catch { high = null; }
                }
                return high != null;
            }
        }

        public static void Patch(Harmony h)
        {
            try
            {
                if (!Possible)
                    return;
                h.Patch(high, postfix: new HarmonyMethod(typeof(KeepTextures), nameof(Postfix)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] keep textures in the card could not start: " + ex.Message);
            }
        }

        public static void Postfix(long budget, ref long __result)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.keepTexInCard || budget <= 0)
                return;
            long late = budget - 200L * 1024 * 1024;
            if (late > __result)
                __result = late;
        }
    }
}
