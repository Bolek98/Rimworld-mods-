using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTurrets
{
    // ------------------------------------------------------------------
    // 1.4.0 Firing arc for turrets in walls: a wall turret only shoots at
    // targets on the outer side of its wall (the half-plane facing away from
    // the room). The outer side is found once after the turret is built – the
    // side whose neighbouring cells are outdoors (unroofed or an outdoor room)
    // while the opposite side is indoors – and remembered. If no side is
    // clearly outside (wall between two rooms, free-standing wall …) the
    // turret shoots everywhere as before; it looks again now and then.
    // ------------------------------------------------------------------
    public static class WallArc
    {
        private struct Facing
        {
            public IntVec3 dir;     // IntVec3.Invalid = shoots everywhere
            public int nextCheck;
        }

        private static readonly Dictionary<int, Facing> facing = new Dictionary<int, Facing>();
        private const int RecheckUndetermined = 2500;   // an hour, while the room is being built
        private const int RecheckKnown = 60000;         // a day, in case the base changed

        public static bool On => ATMod.S != null && ATMod.S.wallTurretsOutward;

        public static bool IsWallTurret(Thing t) => t?.def?.defName != null && t.def.defName.EndsWith(WallTurrets.Suffix);

        public static void Forget(Thing t)
        {
            if (t != null) facing.Remove(t.thingIDNumber);
        }

        public static IntVec3 Outward(Building b)
        {
            int now = Find.TickManager.TicksGame;
            if (facing.TryGetValue(b.thingIDNumber, out Facing f) && now < f.nextCheck)
                return f.dir;
            IntVec3 dir = Compute(b);
            facing[b.thingIDNumber] = new Facing { dir = dir, nextCheck = now + (dir.IsValid ? RecheckKnown : RecheckUndetermined) };
            return dir;
        }

        // 1 = outdoors, -1 = indoors, 0 = nothing to say (wall, out of map)
        private static int Side(Map map, IntVec3 c)
        {
            if (!c.InBounds(map)) return 0;
            Building ed = c.GetEdifice(map);
            if (ed != null && ed.def.passability == Traversability.Impassable) return 0;
            Room room = c.GetRoom(map);
            if (room == null) return 0;
            if (room.PsychologicallyOutdoors || room.UsesOutdoorTemperature || !c.Roofed(map)) return 1;
            return -1;
        }

        private static int Score(Building b, IntVec3 dir)
        {
            Map map = b.Map;
            CellRect r = b.OccupiedRect();
            int score = 0;
            foreach (IntVec3 c in r.EdgeCells)
            {
                IntVec3 n = c + dir;
                if (!r.Contains(n)) score += Side(map, n);
            }
            return score;
        }

        private static IntVec3 Compute(Building b)
        {
            try
            {
                if (b.Map == null) return IntVec3.Invalid;
                IntVec3 best = IntVec3.Invalid;
                int bestGap = 0;
                bool tie = false;
                foreach (IntVec3 dir in new[] { IntVec3.North, IntVec3.East })
                {
                    int a = Score(b, dir), o = Score(b, -dir);
                    // one side outdoors, the other indoors
                    if (!(a > 0 && o < 0) && !(o > 0 && a < 0)) continue;
                    int gap = Math.Abs(a - o);
                    if (gap > bestGap) { best = a > 0 ? dir : -dir; bestGap = gap; tie = false; }
                    else if (gap == bestGap) tie = true;
                }
                return tie ? IntVec3.Invalid : best;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Auto Turrets] wall turret facing: " + e.Message, 0x41545334);
                return IntVec3.Invalid;
            }
        }

        public static bool InArc(Building b, Thing target)
        {
            IntVec3 dir = Outward(b);
            if (!dir.IsValid) return true;
            Vector3 d = target.TrueCenter() - b.TrueCenter();
            return d.x * dir.x + d.z * dir.z > 0.01f;
        }
    }

    [HarmonyPatch(typeof(Building_TurretGun), "IsValidTarget")]
    public static class Patch_WallArcTarget
    {
        public static void Postfix(Building_TurretGun __instance, Thing t, ref bool __result)
        {
            if (!__result || t == null || !WallArc.On || !WallArc.IsWallTurret(__instance) || !__instance.Spawned)
                return;
            try
            {
                if (!WallArc.InArc(__instance, t))
                    __result = false;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Auto Turrets] wall turret arc: " + e.Message, 0x41545335);
            }
        }
    }

    // a rebuilt turret looks again
    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.SpawnSetup))]
    public static class Patch_WallArcSpawn
    {
        public static void Postfix(Building_TurretGun __instance)
        {
            if (WallArc.IsWallTurret(__instance))
                WallArc.Forget(__instance);
        }
    }
}
