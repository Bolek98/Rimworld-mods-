using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.4.0: game speed and the flow of time.
    //  * smart work search – a work that found nothing for a colonist and took long to check is
    //    not checked again by that colonist for a few seconds (game time);
    //  * spread job searches – when the job searches of one tick already took too long, the
    //    remaining colonists look for work a tick or two later instead (never drafted or in danger);
    //  * room temperatures in parts – every room is still evened out every 2 s, but not all in one tick;
    //  * fast nights – while everybody sleeps and nothing happens the game runs at speed 3, and
    //    goes back to your speed when someone wakes up, a letter comes or danger shows.
    // ---------------------------------------------------------------------------
    public static class SmartWork
    {
        private static readonly Dictionary<long, int> cool = new Dictionary<long, int>();
        private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> jtPawn = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");
        private static Pawn scanning;
        private static WorkGiver current;
        private static long currentStart;
        private static readonly List<(WorkGiver giver, double ms)> checkedNow = new List<(WorkGiver, double)>();
        public static int Skipped, Cooled;
        public static double SavedMs;
        private static readonly Dictionary<int, double> costOf = new Dictionary<int, double>();   // work giver index → last cost

        public const double SlowMs = 1.5;
        public const int CoolTicks = 300;          // 5 s at speed 1

        private static bool On => GraphicsTunerMod.Settings?.smartWork ?? false;

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfoPair(h, AccessTools.Method(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage)), nameof(IssuePre), nameof(IssuePost));
                h.Patch(AccessTools.Method(typeof(JobGiver_Work), "PawnCanUseWorkGiver"),
                    prefix: new HarmonyMethod(typeof(SmartWork), nameof(CanUsePre)),
                    postfix: new HarmonyMethod(typeof(SmartWork), nameof(CanUsePost)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] smart work search not available: " + ex.Message); }
        }

        private static void MethodInfoPair(Harmony h, System.Reflection.MethodInfo m, string pre, string post)
        {
            if (m == null) throw new Exception("JobGiver_Work.TryIssueJobPackage not found");
            h.Patch(m, prefix: new HarmonyMethod(typeof(SmartWork), pre), postfix: new HarmonyMethod(typeof(SmartWork), post));
        }

        private static long Key(Pawn p, WorkGiver g) => ((long)p.thingIDNumber << 20) | (uint)g.def.index;

        public static void IssuePre(Pawn pawn)
        {
            if (!On || pawn == null || !pawn.IsColonist) { scanning = null; return; }
            scanning = pawn;
            current = null;
            checkedNow.Clear();
        }

        private static void Close()
        {
            if (current == null) return;
            double ms = (Stopwatch.GetTimestamp() - currentStart) * 1000.0 / Stopwatch.Frequency;
            checkedNow.Add((current, ms));
            costOf[current.def.index] = ms;
            current = null;
        }

        public static void IssuePost(Pawn pawn, ThinkResult __result)
        {
            if (scanning == null || pawn != scanning) return;
            try
            {
                Close();
                WorkGiverDef winner = __result.Job?.workGiverDef;
                int now = Find.TickManager.TicksGame;
                foreach (var (giver, ms) in checkedNow)
                {
                    if (ms < SlowMs || giver.def == winner || giver.def.emergency) continue;
                    cool[Key(pawn, giver)] = now + CoolTicks;
                    Cooled++;
                }
                if (cool.Count > 20000) cool.Clear();
            }
            catch { }
            finally
            {
                scanning = null;
                checkedNow.Clear();
            }
        }

        // a slow work that just found nothing is skipped for a few seconds
        public static bool CanUsePre(Pawn pawn, WorkGiver giver, ref bool __result)
        {
            if (scanning == null || pawn != scanning || giver?.def == null || giver.def.emergency) return true;
            if (cool.TryGetValue(Key(pawn, giver), out int until) && Find.TickManager.TicksGame < until)
            {
                Skipped++;
                if (costOf.TryGetValue(giver.def.index, out double c)) SavedMs += c;
                __result = false;
                return false;
            }
            return true;
        }

        public static void CanUsePost(Pawn pawn, WorkGiver giver, bool __result)
        {
            if (scanning == null || pawn != scanning) return;
            Close();
            if (__result)
            {
                current = giver;
                currentStart = Stopwatch.GetTimestamp();
            }
        }

        // ---------------- spread job searches ----------------
        private static int tick = -1;
        private static double spentThisTick;
        private static int depth;
        private static readonly Dictionary<int, int> putOff = new Dictionary<int, int>();
        public static int Deferred;
        public const double TickBudgetMs = 6.0;

        public static void PatchSpread(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "TryFindAndStartJob"),
                    prefix: new HarmonyMethod(typeof(SmartWork), nameof(FindPre)),
                    postfix: new HarmonyMethod(typeof(SmartWork), nameof(FindPost)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] spread job search not available: " + ex.Message); }
        }

        private static bool CanWait(Pawn p)
        {
            if (p == null || p.Drafted || p.Downed || p.InMentalState || !p.Spawned || p.Map == null) return false;
            if (p.mindState?.enemyTarget != null || p.mindState?.meleeThreat != null) return false;
            if (p.IsBurning() || p.health?.hediffSet?.BleedRateTotal > 0.1f) return false;
            if (p.Faction != Faction.OfPlayer) return false;
            if (SmoothFPS.ExclusionRules.SkipPawn(p)) return false;   // 1.6.0: opt-out rules of other mods
            if (GenHostility.AnyHostileActiveThreatToPlayer(p.Map, true)) return false;
            return true;
        }

        public static bool FindPre(Pawn_JobTracker __instance, out long __state)
        {
            __state = 0L;
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.spreadJobs || depth > 0) { depth++; return true; }
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now != tick) { tick = now; spentThisTick = 0; }
            Pawn p = jtPawn(__instance);
            if (spentThisTick > TickBudgetMs && __instance.curJob == null && CanWait(p))
            {
                int id = p.thingIDNumber;
                int n = putOff.TryGetValue(id, out int k) ? k : 0;
                if (n < 3)      // never more than 3 ticks in a row
                {
                    putOff[id] = n + 1;
                    Deferred++;
                    return false;   // the job tracker asks again next tick
                }
            }
            if (p != null) putOff.Remove(p.thingIDNumber);
            depth++;
            __state = Stopwatch.GetTimestamp();
            return true;
        }

        public static void FindPost(long __state)
        {
            if (depth > 0) depth--;
            if (__state != 0L)
                spentThisTick += (Stopwatch.GetTimestamp() - __state) * 1000.0 / Stopwatch.Frequency;
        }
    }

    // room temperatures evened out in parts instead of all in one tick
    public static class TempSpread
    {
        private static readonly AccessTools.FieldRef<MapTemperature, Map> mapRef = AccessTools.FieldRefAccess<MapTemperature, Map>("map");

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(MapTemperature), nameof(MapTemperature.MapTemperatureTick)),
                    prefix: new HarmonyMethod(typeof(TempSpread), nameof(Prefix)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] temperature in parts not available: " + ex.Message); }
        }

        public static bool Prefix(MapTemperature __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.tempSpread || DebugSettings.fastEcology) return true;
            try
            {
                Map map = mapRef(__instance);
                int t = Find.TickManager.TicksGame;
                int slot = ((t - 7) % 120 + 120) % 120;
                IReadOnlyList<Room> rooms = map.regionGrid.AllRooms;
                // every room still once per 120 ticks, the same as the game – only spread over the ticks
                for (int i = slot; i < rooms.Count; i += 120)
                    rooms[i].TempTracker.EqualizeTemperature();
                if (t % 60 == 0)
                    __instance.Drawer.SetDirty();
                return false;
            }
            catch
            {
                return true;
            }
        }
    }

    // ---------------- fast nights ----------------
    public static class FastNight
    {
        public static bool Active;
        private static TimeSpeed before;
        private static int holdUntil;
        private static bool cancelled;
        public static int Nights;

        private static bool On => GraphicsTunerMod.Settings?.nightFast ?? false;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(LetterStack), nameof(LetterStack.ReceiveLetter), new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) }),
                    postfix: new HarmonyMethod(typeof(FastNight), nameof(OnLetter)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] fast nights: letters not watched: " + ex.Message); }
        }

        public static void OnLetter()
        {
            if (!Active) return;
            Stop(true);
            holdUntil = Find.TickManager.TicksGame + 2500;   // an hour of game time before it may start again
        }

        private static void Stop(bool restore)
        {
            if (!Active) return;
            Active = false;
            TickManager tm = Find.TickManager;
            if (restore && tm != null && tm.CurTimeSpeed == TimeSpeed.Superfast && before != TimeSpeed.Superfast)
                tm.CurTimeSpeed = before;
        }

        // every second of game time, from the game component
        public static void Tick()
        {
            TickManager tm = Find.TickManager;
            if (tm == null) return;
            if (!On) { if (Active) Stop(true); return; }
            if (tm.Paused) return;
            if (Active && tm.CurTimeSpeed != TimeSpeed.Superfast)
            {
                // the player changed the speed: leave it alone until the next night
                Active = false;
                cancelled = true;
                return;
            }
            bool night = AllAsleepAndQuiet();
            if (!night)
            {
                cancelled = false;
                if (Active) Stop(true);
                return;
            }
            if (Active || cancelled || tm.TicksGame < holdUntil) return;
            if (tm.CurTimeSpeed >= TimeSpeed.Superfast) return;   // already fast
            before = tm.CurTimeSpeed;
            tm.CurTimeSpeed = TimeSpeed.Superfast;
            Active = true;
            Nights++;
            Messages.Message("GT_NightFastOn".Translate(), MessageTypeDefOf.SilentInput, false);
        }

        private static bool AllAsleepAndQuiet()
        {
            int sleeping = 0;
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome) continue;
                if (GenHostility.AnyHostileActiveThreatToPlayer(map, true)) return false;
                foreach (Thing f in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
                    if (map.areaManager.Home[f.Position]) return false;
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.Downed && !p.InBed()) return false;       // someone needs rescue
                    if (p.Awake()) return false;
                    sleeping++;
                }
            }
            return sleeping > 0;
        }
    }

    public class SpeedGameComp : GameComponent
    {
        public SpeedGameComp(Game game) { }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 60 == 23)
            {
                try { FastNight.Tick(); } catch (Exception ex) { MLog.WarnOnce("[Smooth FPS] fast nights: " + ex.Message, 0x7e3c55); }
            }
        }
    }
}
