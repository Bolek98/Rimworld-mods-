using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;
using System.Linq;

namespace GraphicsTuner
{
    // 1.7.0: lazy pawns off screen. The game updates every pawn every tick (Tick(): walking, jobs, health,
    // stances, weapons – and the patches of every mod on top). Pawns you cannot see and that do nothing
    // that matters are updated only every 2nd–4th tick: wild animals far from your colonists, tame animals
    // resting or wandering, visitors waiting around, sleeping colonists (optional). Walking still happens
    // every tick, so they move as fast as before. Needs, growth and the rest of the "interval" part are
    // not touched – the game counts them with the time that passed.
    // Full speed again at once: on screen, drafted, downed, mental state, hurt recently, designated
    // (hunt, tame, slaughter …), near a colonist (wild / visitors), and on a map with danger.
    public static class LazyTick
    {
        public static int LazyNow, SkippedTicks;
        private static int countTick = -1, countLazy;

        private static readonly Dictionary<int, int> lazyUntil = new Dictionary<int, int>();   // pawn id -> tick until the decision holds (negative = full)
        private static readonly Dictionary<int, int> decidedAt = new Dictionary<int, int>();
        private static readonly Dictionary<int, (int tick, bool danger, List<IntVec3> colonists)> mapInfo = new Dictionary<int, (int, bool, List<IntVec3>)>();
        private static CellRect view;
        private static int viewFrame = -1;

        private static GfxSettings S => GraphicsTunerMod.Settings;
        private static bool On => S != null && S.lazyTick;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(Pawn), "Tick");
                if (m == null) { MLog.Warn("[Smooth FPS] lazy pawns: Pawn.Tick not found"); return; }
                h.Patch(m, prefix: new HarmonyMethod(typeof(LazyTick), nameof(Prefix)) { priority = Priority.First });
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] lazy pawns not available: " + e.Message); }
        }

        public static bool Prefix(Pawn __instance)
        {
            if (!On) return true;
            try
            {
                Pawn p = __instance;
                if (!p.Spawned || p.Map == null) return true;
                int now = Find.TickManager.TicksGame;
                if (now != countTick) { LazyNow = countLazy; countLazy = 0; countTick = now; }
                if (!IsLazy(p, now)) return true;
                countLazy++;
                int every = Mathf.Clamp(S.lazyEvery, 2, 4);
                if ((now + p.thingIDNumber) % every == 0) return true;   // its full tick
                // skipped tick: only the walking, so it moves as fast as before
                if (p.pather != null && p.pather.Moving) p.pather.PatherTick();
                SkippedTicks++;
                return false;
            }
            catch { return true; }
        }

        private static bool IsLazy(Pawn p, int now)
        {
            int id = p.thingIDNumber;
            if (decidedAt.TryGetValue(id, out int at) && now - at < 30 && now >= at)
                return lazyUntil.TryGetValue(id, out int v) && v > 0;
            bool lazy = Decide(p, now);
            decidedAt[id] = now;
            lazyUntil[id] = lazy ? 1 : -1;
            if (decidedAt.Count > 5000) { decidedAt.Clear(); lazyUntil.Clear(); }
            return lazy;
        }

        private static bool Decide(Pawn p, int now)
        {
            if (p.Drafted || p.Downed || p.InMentalState || p.IsBurning() || p.CarriedBy != null) return false;
            if (p.mindState != null && now - p.mindState.lastHarmTick < 2500) return false;
            if (p.carryTracker?.CarriedThing != null) return false;
            Map map = p.Map;
            var info = Info(map, now);
            if (info.danger) return false;
            if (OnScreen(p)) return false;
            if (map.designationManager.AllDesignationsOn(p).Any()) return false;
            if (p.GetLord() != null && !(p.Faction != null && !p.Faction.IsPlayer && !p.HostileTo(Faction.OfPlayer))) return false;   // rituals, caravans, raids – full
            Job j = p.CurJob;
            if (p.RaceProps.Animal)
            {
                if (p.Faction == null)
                    return !Near(p.Position, info.colonists, 18);          // wild animal far from your people
                if (p.Faction.IsPlayer)
                    return j == null || j.def == JobDefOf.Wait_Wander || j.def == JobDefOf.GotoWander || j.def == JobDefOf.LayDown || j.def == JobDefOf.Wait;   // tame animal resting / wandering
                return false;
            }
            if (p.RaceProps.Humanlike)
            {
                if (p.Faction != null && !p.Faction.IsPlayer && !p.HostileTo(Faction.OfPlayer) && p.HostFaction == null)
                    return (j == null || j.def == JobDefOf.Wait_Wander || j.def == JobDefOf.GotoWander || j.def == JobDefOf.Wait || j.def == JobDefOf.Wait_MaintainPosture)
                        && !Near(p.Position, info.colonists, 8);           // visitors / traders waiting around
                if (S.lazySleepers && p.IsColonist && j != null && j.def == JobDefOf.LayDown && p.jobs?.curDriver != null && p.jobs.curDriver.asleep)
                    return true;                                         // a sleeping colonist
            }
            return false;
        }

        private static bool OnScreen(Pawn p)
        {
            if (WorldRendererUtility.WorldSelected || p.Map != Find.CurrentMap) return false;
            if (viewFrame != Time.frameCount)
            {
                viewFrame = Time.frameCount;
                view = Find.CameraDriver.CurrentViewRect.ExpandedBy(6);
            }
            return view.Contains(p.Position);
        }

        private static bool Near(IntVec3 c, List<IntVec3> list, int r)
        {
            int r2 = r * r;
            for (int i = 0; i < list.Count; i++)
                if ((list[i] - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        private static (int tick, bool danger, List<IntVec3> colonists) Info(Map map, int now)
        {
            if (mapInfo.TryGetValue(map.uniqueID, out var mi) && now - mi.tick < 120 && now >= mi.tick) return mi;
            bool danger = false;
            try { danger = GenHostility.AnyHostileActiveThreatToPlayer(map, true); } catch { }
            var list = mi.colonists ?? new List<IntVec3>();
            list.Clear();
            foreach (Pawn c in map.mapPawns.FreeColonistsSpawned) list.Add(c.Position);
            mi = (now, danger, list);
            mapInfo[map.uniqueID] = mi;
            return mi;
        }

        public static void Reset() { decidedAt.Clear(); lazyUntil.Clear(); mapInfo.Clear(); }
    }
}
