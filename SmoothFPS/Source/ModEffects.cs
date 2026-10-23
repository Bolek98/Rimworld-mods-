using System;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.3.2: effects from mods. The effect settings (share, cap per second, fewer at speed or low FPS,
    // none when zoomed out) worked only for the game's own effects ("flecks"). Many mods make their
    // smoke, sparks, dust, footprints, magic particles … as flying "motes" instead – those now follow
    // the same settings. Only flying particles are touched: progress bars, speech bubbles, texts and
    // icons above pawns always stay.
    public static class ModEffects
    {
        private static readonly ConditionalWeakTable<Mote, object> hidden = new ConditionalWeakTable<Mote, object>();
        private static readonly object Mark = new object();

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Mote), nameof(Mote.SpawnSetup)), postfix: new HarmonyMethod(typeof(ModEffects), nameof(SpawnPostfix)));
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DynamicDrawPhase)), prefix: new HarmonyMethod(typeof(ModEffects), nameof(DrawPrefix)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] mod effects not limited: " + ex.Message);
            }
        }

        private static bool Particle(Mote m) => m is MoteThrown && !(m is MoteText);

        public static void SpawnPostfix(Mote __instance, bool respawningAfterLoad)
        {
            try
            {
                if (respawningAfterLoad || !Particle(__instance))
                    return;
                if (!Runtime.AllowEffect())
                    hidden.AddOrUpdate(__instance, Mark);
            }
            catch { }
        }

        public static bool DrawPrefix(Thing __instance)
        {
            if (!(__instance is Mote m) || !Particle(m))
                return true;
            try
            {
                if (hidden.TryGetValue(m, out _))
                    return false;
                GfxSettings s = GraphicsTunerMod.Settings;
                if (s != null && s.cullEffectsZoomed && Runtime.ZoomedOut(s))
                    return false;
            }
            catch { }
            return true;
        }
    }
}
