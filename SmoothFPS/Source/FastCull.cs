using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.7.0: faster choice of what to draw. Every frame the game walks over EVERY thing on the
    // map that is drawn in real time (pawns, animals, animated buildings, many mod things …) and,
    // for each, works out its exact drawn position and size – even for things far outside the
    // screen, only to throw them away a moment later. On a big map with a big mod pack that is
    // tens of thousands of things per frame. Here a thing whose cell is clearly outside the
    // screen (plus a safe margin for its size and shadow) gets its cell only – the game then
    // throws it away exactly as before, just without the expensive part. Things on screen and
    // near it are measured exactly as in the game.
    // ---------------------------------------------------------------------------
    public static class FastCull
    {
        private static CellRect near;
        private static bool active;
        public static int LastCount;
        private static readonly Dictionary<ThingDef, int> margin = new Dictionary<ThingDef, int>();
        private static readonly AccessTools.FieldRef<DynamicDrawManager, List<Thing>> thingsRef =
            AccessTools.FieldRefAccess<DynamicDrawManager, List<Thing>>("drawThings");

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(DynamicDrawManager), "ComputeCulledThings");
                if (m == null)
                    return;
                h.Patch(m, prefix: new HarmonyMethod(typeof(FastCull), nameof(Prefix)), transpiler: new HarmonyMethod(typeof(FastCull), nameof(Transpiler)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] faster draw choice could not start: " + ex.Message);
            }
        }

        public static void Prefix(DynamicDrawManager __instance)
        {
            active = false;
            try
            {
                LastCount = thingsRef(__instance)?.Count ?? 0;
                GfxSettings s = GraphicsTunerMod.Settings;
                if (s == null || !s.fastCull)
                    return;
                // the sun shadows of things just outside the screen still show – keep a wide margin
                near = Find.CameraDriver.CurrentViewRect.ExpandedBy(12);
                active = true;
            }
            catch
            {
                active = false;
            }
        }

        private static int Margin(Thing t)
        {
            ThingDef d = t.def;
            if (d == null)
                return 8;
            if (margin.TryGetValue(d, out int m))
                return m;
            m = Math.Max(d.size.x, d.size.z);
            Vector2 ds = d.graphicData?.drawSize ?? Vector2.one;
            m = Math.Max(m, Mathf.CeilToInt(Math.Max(ds.x, ds.y)));
            m = m / 2 + 4;
            if (d.drawOffscreen)
                m = 9999;
            margin[d] = m;
            return m;
        }

        private static bool Far(Thing t)
        {
            if (!active)
                return false;
            IntVec3 c = t.Position;
            int m = Margin(t);
            if (m >= 9999)
                return false;
            return c.x < near.minX - m || c.x > near.maxX + m || c.z < near.minZ - m || c.z > near.maxZ + m;
        }

        public static Vector3 DrawPos(Thing t)
        {
            if (Far(t))
                return t.Position.ToVector3Shifted();
            return t.DrawPos;
        }

        public static CellRect Rect(Thing t)
        {
            if (Far(t))
                return CellRect.SingleCell(t.Position);
            return t.OccupiedDrawRect();
        }

        public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo drawPos = AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.DrawPos));
            MethodInfo rect = AccessTools.Method(typeof(GenAdj), nameof(GenAdj.OccupiedDrawRect));
            MethodInfo myPos = AccessTools.Method(typeof(FastCull), nameof(DrawPos));
            MethodInfo myRect = AccessTools.Method(typeof(FastCull), nameof(Rect));
            int replaced = 0;
            foreach (CodeInstruction ci in instructions)
            {
                if ((ci.opcode == OpCodes.Callvirt || ci.opcode == OpCodes.Call) && ci.operand is MethodInfo mi)
                {
                    if (mi == drawPos)
                    {
                        ci.opcode = OpCodes.Call;
                        ci.operand = myPos;
                        replaced++;
                    }
                    else if (mi == rect)
                    {
                        ci.operand = myRect;
                        replaced++;
                    }
                }
                yield return ci;
            }
            if (replaced < 2)
                MLog.Warn("[Smooth FPS] faster draw choice: only " + replaced + " of 2 places found – partly active.");
        }
    }
}
