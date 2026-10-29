using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RideDrive
{
    // ---------------------------------------------------------------------------
    // Safe riding. Giddy-Up builds its list of mountable animals and the rider draw offsets once, at
    // start. When anything throws in there (seen: another mod's "is it a mech?" check), the list stays
    // half empty and the offsets missing – every mounting then throws halfway, leaving the rider
    // "mounted" on an animal that never got the order, the next think undoes it, and the rider tries
    // again… until the game runs out of stack. Here:
    //  - a broken start is repaired (mountable list, offsets made on demand),
    //  - a mounting that throws is rolled back cleanly,
    //  - a rider whose mounting keeps failing pauses for a while instead of retrying for ever,
    //  - mounting calls nested inside each other are cut off.
    // ---------------------------------------------------------------------------
    public static class GuSafety
    {
        public static bool SetupBroken;
        public static int Rollbacks, Paused;
        private static readonly Dictionary<int, int> pauseUntil = new Dictionary<int, int>();
        private static readonly Dictionary<int, int[]> tries = new Dictionary<int, int[]>();   // id → [first tick, count]
        private static readonly HashSet<int> told = new HashSet<int>();
        [ThreadStatic] private static int depth;
        private static JobDef mountDef;
        public static Func<Pawn, bool> SkipMount;   // set by the vehicle part when Vehicle Framework is there

        private static RDSettings S => RDMod.S;
        private static int Now => Find.TickManager?.TicksGame ?? 0;

        public static void Install(Harmony h)
        {
            if (!GU.Init()) { Log.Warning("[Ride & Drive] Giddy-Up looks different than expected – riding safety off."); return; }
            mountDef = GU.MountJob;
            // does Giddy-Up's own start work?
            if (GU.SetupFails())
            {
                SetupBroken = true;
                Log.Warning("[Ride & Drive] Giddy-Up could not finish its start – repairing what it needs for riding.");
                Repair();
            }
            GU.Offsets();

            h.Patch(AccessTools.Method(GU.tTexture, "FetchCache"),
                prefix: new HarmonyMethod(typeof(GuSafety), nameof(FetchCachePrefix)));
            h.Patch(AccessTools.Method(GU.tMountUtility, "Mount"),
                postfix: new HarmonyMethod(typeof(GuSafety), nameof(MountPostfix)),
                finalizer: new HarmonyMethod(typeof(GuSafety), nameof(MountFinalizer)));
            h.Patch(AccessTools.Method(GU.tMountUtility, "GoMount"),
                prefix: new HarmonyMethod(typeof(GuSafety), nameof(GoMountPrefix)) { priority = Priority.First },
                finalizer: new HarmonyMethod(typeof(GuSafety), nameof(GoMountFinalizer)));
            h.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"),
                postfix: new HarmonyMethod(typeof(GuSafety), nameof(DetermineNextJobPostfix)) { priority = Priority.Last - 1 });
            foreach (MethodInfo m in typeof(Pawn_JobTracker).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == nameof(Pawn_JobTracker.StartJob) && m.DeclaringType == typeof(Pawn_JobTracker))
                    h.Patch(m, prefix: new HarmonyMethod(typeof(GuSafety), nameof(StartJobPrefix)));
            Log.Message("[Ride & Drive] riding safety on" + (SetupBroken ? " (Giddy-Up start repaired)." : "."));
        }

        // ---------- repair of a broken Giddy-Up start ----------
        private static void Repair()
        {
            try
            {
                int n = GU.RebuildMountable();
                Log.Message("[Ride & Drive] Giddy-Up repaired: " + n + " animals added to the mountable list, rider offsets are made when needed.");
            }
            catch (Exception e)
            {
                Log.Warning("[Ride & Drive] Giddy-Up repair: " + e.Message);
            }
        }

        // the draw offset of a rider: made on the spot when Giddy-Up has none (instead of throwing)
        public static void FetchCachePrefix(Pawn animal)
        {
            if (!S.safeMounting || animal?.kindDef == null) return;
            try { GU.EnsureOffset(animal); } catch { }
        }

        // a mounting that threw halfway is undone – no rider "on" an animal that never got the order
        public static Exception MountFinalizer(Exception __exception, Pawn rider, Pawn animal)
        {
            if (__exception == null || !S.safeMounting) return __exception;
            try
            {
                Rollbacks++;
                GU.ClearMount(rider, animal);
                Pause(rider, animal, "RD_LogMountThrew", (__exception.InnerException ?? __exception).GetType().Name);
            }
            catch { }
            return null;
        }

        public static bool IsPaused(Pawn p)
        {
            if (p == null || !S.mountPause) return false;
            return pauseUntil.TryGetValue(p.thingIDNumber, out int until) && Now < until;
        }

        public static void Pause(Pawn rider, Pawn animal, string why, string detail = "")
        {
            if (rider == null || !S.mountPause) return;
            pauseUntil[rider.thingIDNumber] = Now + (int)(S.pauseHours * GenDate.TicksPerHour);
            tries.Remove(rider.thingIDNumber);
            Paused++;
            if (told.Add(rider.thingIDNumber))
                Log.Warning("[Ride & Drive] " + why.Translate(rider.LabelShort, animal?.LabelShort ?? "?", detail, S.pauseHours.ToString("0.#")));
        }

        // mounting orders for a paused rider are dropped (a player's own order still goes through)
        public const int Inject = GU.Inject, TryOrder = GU.TryOrder, Instant = GU.Instant;
        public static void GoMount(Pawn rider, Pawn animal, int how) => GU.GoMount(rider, animal, how);

        public static bool GoMountPrefix(Pawn rider, Pawn animal, object[] __args, ref ThinkResult? __result)
        {
            int giveJobMethod = 0;
            ThinkResult? thinkResult = null;
            try { giveJobMethod = Convert.ToInt32(__args[2]); thinkResult = __args[3] as ThinkResult?; } catch { }
            depth++;
            if (depth > 4 && S.safeMounting)
            {
                if (told.Add(-1)) Log.Warning("[Ride & Drive] mounting called itself again and again – stopped.\n" + Environment.StackTrace);
                __result = thinkResult;
                return false;
            }
            if (giveJobMethod != TryOrder && (IsPaused(rider) || CarryingJob(rider)))
            {
                __result = thinkResult;
                return false;
            }
            return true;
        }

        public static Exception GoMountFinalizer(Exception __exception)
        {
            depth--;
            return __exception;
        }

        // the last word on the next job: no automatic "mount" for a paused rider (Giddy-Up's or Companion Mounts')
        public static void DetermineNextJobPostfix(Pawn_JobTracker __instance, ref ThinkResult __result, Pawn ___pawn)
        {
            try
            {
                if (mountDef == null || __result.Job?.def != mountDef || ___pawn == null) return;
                if (IsPaused(___pawn) || CarryingJob(___pawn) || (SkipMount != null && SkipMount(___pawn)))
                    __result = ThinkResult.NoJob;   // the job it was going to do is already queued first
            }
            catch { }
        }

        // 0.1.1: no automatic mounting with a corpse or a person to carry – Giddy-Up wanted the rider to get on
        // first, the mounting failed with the load in his arms and he tried again and again
        public static bool CarryingJob(Pawn p)
        {
            if (p == null || !S.noMountCarrying) return false;
            try
            {
                if (p.carryTracker?.CarriedThing != null) return true;
                Job next = p.jobs?.jobQueue != null && p.jobs.jobQueue.Count > 0 ? p.jobs.jobQueue[0].job : p.CurJob;
                if (next == null) return false;
                if (CarriesBody(next.targetA) || CarriesBody(next.targetB)) return true;
                string d = next.def?.defName ?? "";
                return d.Contains("Corpse") || d == "Rescue" || d == "Capture" || d == "Arrest" || d == "Kidnap" || d.StartsWith("Carry") || d.Contains("ToBed") || d.Contains("Cryptosleep");
            }
            catch { return false; }
        }

        private static bool CarriesBody(LocalTargetInfo t)
        {
            Thing th = t.Thing;
            return th is Corpse || (th is Pawn other && other.Downed);
        }

        // the same rider starting "mount" over and over without ever sitting on the animal → pause
        public static bool StartJobPrefix(Pawn_JobTracker __instance, Job newJob, Pawn ___pawn)
        {
            if (mountDef == null || newJob?.def != mountDef || ___pawn == null || !S.mountPause) return true;
            try
            {
                if (IsPaused(___pawn) && !newJob.playerForced) return false;
                int id = ___pawn.thingIDNumber, now = Now;
                if (!tries.TryGetValue(id, out int[] t) || now - t[0] > 2500)
                    tries[id] = t = new[] { now, 0 };
                t[1]++;
                if (t[1] >= 5 && !GU.IsMounted(___pawn) && !newJob.playerForced)
                {
                    Pause(___pawn, newJob.targetA.Thing as Pawn, "RD_LogMountLoop");
                    return false;
                }
            }
            catch { }
            return true;
        }

        // sitting on the animal at last – the count of tries starts again
        public static void MountPostfix(Pawn rider)
        {
            try { if (rider != null && GU.IsMounted(rider)) tries.Remove(rider.thingIDNumber); } catch { }
        }
    }
}
