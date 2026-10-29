using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Vehicles;
using Vehicles.World;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace RideDrive
{
    // ---------------------------------------------------------------------------
    // Riders and vehicles. Neither framework knows the other: a rider who boards simply vanishes from
    // under Giddy-Up, the animal is told to wait for someone who is not on the map, gives up and the
    // rider is later sent to lead it to the pen – and the animal is no longer "his". Here:
    //  - a rider gets off properly before boarding; the animal rides along as cargo, waits by the
    //    vehicle, or goes free (setting);
    //  - riders may ride right up to a vehicle (no parking 25 cells away);
    //  - the animal stays the rider's own while he is inside;
    //  - after getting out the rider takes the animal out of the cargo and gets back on;
    //  - vehicle caravans: riders walking with the vehicles ride their animals, and get back on them
    //    when the caravan arrives;
    //  - Mount Passengers: passengers on an animal get off before its rider boards.
    // ---------------------------------------------------------------------------
    public static class VehicleBridge
    {
        public static int Dismounts, Remounts;
        private static RDSettings S => RDMod.S;
        private static HashSet<JobDef> vehicleJobs;

        public static void Install(Harmony h)
        {
            vehicleJobs = new HashSet<JobDef>(DefDatabase<JobDef>.AllDefsListForReading.Where(j => j.driverClass != null && j.driverClass.Namespace == "Vehicles"));

            h.Patch(AccessTools.Method(typeof(VehiclePawn), nameof(VehiclePawn.TryAddPawn), new[] { typeof(Pawn), typeof(VehicleRoleHandler) }),
                prefix: new HarmonyMethod(typeof(VehicleBridge), nameof(BoardPrefix)) { priority = Priority.First });
            h.Patch(AccessTools.Method(typeof(VehiclePawn), nameof(VehiclePawn.DisembarkPawn)),
                postfix: new HarmonyMethod(typeof(VehicleBridge), nameof(DisembarkPostfix)));
            h.Patch(AccessTools.Method(GU.tMountUtility, "Dismount"),
                prefix: new HarmonyMethod(typeof(VehicleBridge), nameof(DismountPrefix)));
            h.Patch(AccessTools.Method(typeof(LordToil_PrepareCaravan_LeaveWithVehicles), nameof(LordToil.UpdateAllDuties)),
                postfix: new HarmonyMethod(typeof(VehicleBridge), nameof(LeavePostfix)));
            MethodInfo enter = AccessTools.Method(typeof(EnterMapUtilityVehicles), nameof(EnterMapUtilityVehicles.EnterAndSpawn));
            if (enter != null)
                h.Patch(enter, prefix: new HarmonyMethod(typeof(VehicleBridge), nameof(EnterPrefix)), postfix: new HarmonyMethod(typeof(VehicleBridge), nameof(EnterPostfix)));
            ApplyAllowedJobs();
            GuSafety.SkipMount = SkipMountFor;
            MP.Init();
            Log.Message("[Ride & Drive] riders and vehicles work together (" + vehicleJobs.Count + " vehicle jobs known).");
        }

        // riding up to a vehicle (boarding, following, escorting) is allowed on the animal's back
        public static void ApplyAllowedJobs()
        {
            if (!S.rideToVehicle) return;
            foreach (JobDef j in new[] { JobDefOf_Vehicles.Board, JobDefOf_Vehicles.FollowVehicle, JobDefOf_Vehicles.EscortVehicle })
                if (j != null)
                {
                    try { GU.AllowWhileMounted(j); } catch { }
                }
        }

        // no automatic "mount" just to walk to a vehicle that is close anyway (ride-to-vehicle off), or
        // for a vehicle job other than boarding (loading cargo, repairing … go on foot)
        public static bool SkipMountFor(Pawn p)
        {
            if (vehicleJobs == null || p?.jobs?.jobQueue == null || p.jobs.jobQueue.Count == 0) return false;
            Job next = p.jobs.jobQueue[0].job;
            if (next == null || !vehicleJobs.Contains(next.def)) return false;
            if (next.def == JobDefOf_Vehicles.Board && S.rideToVehicle) return false;
            return true;
        }

        // ---------- getting in ----------
        public static void BoardPrefix(VehiclePawn __instance, Pawn pawn)
        {
            if (!S.dismountToBoard || pawn == null || !pawn.Spawned || !pawn.RaceProps.Humanlike) return;
            try
            {
                MP.ForPassenger(pawn);
                if (!GU.IsMounted(pawn)) return;
                Pawn animal = GU.MountOf(pawn);
                if (animal == null) return;
                MP.ForRider(pawn);
                Dismounts++;
                Map map = pawn.Map;
                bool cargo = S.animalPlace == AnimalPlace.Cargo && animal.Spawned && CargoFits(__instance, animal);
                IntVec3 park = IntVec3.Invalid;
                if (!cargo && S.animalPlace != AnimalPlace.Free)
                    park = SpotNear(__instance, animal);
                GU.Dismount(pawn, animal, !S.keepOwnership, park.IsValid ? park : default(IntVec3), park.IsValid, false);
                if (cargo)
                {
                    if (animal.Spawned && animal.jobs != null) animal.jobs.StopAll();
                    if (!__instance.TryAddPawn(animal))
                        Log.Warning("[Ride & Drive] " + animal.LabelShort + " did not fit into " + __instance.LabelShort + ".");
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Ride & Drive] boarding: " + e.Message, 0x52440001);
            }
        }

        private static bool CargoFits(VehiclePawn v, Pawn animal)
        {
            try
            {
                float cap = v.GetStatValue(VehicleStatDefOf.CargoCapacity);
                float used = MassUtility.GearAndInventoryMass(v);
                return used + animal.GetStatValue(StatDefOf.Mass) <= cap;
            }
            catch { return false; }
        }

        private static IntVec3 SpotNear(VehiclePawn v, Pawn animal)
        {
            Map map = v.Map;
            CellRect rect = v.OccupiedRect();
            IntVec3 best = IntVec3.Invalid;
            float bestD = float.MaxValue;
            foreach (IntVec3 c in rect.ExpandedBy(3).ClipInsideMap(map))
            {
                if (rect.Contains(c) || !c.Standable(map) || c.GetFirstPawn(map) != null) continue;
                float d = c.DistanceToSquared(rect.CenterCell);
                if (d < bestD && animal.CanReach(c, PathEndMode.OnCell, Danger.Some)) { best = c; bestD = d; }
            }
            return best;
        }

        // a rider who is already inside a vehicle (or gone from the map with it) – the animal is not
        // told to wait for him and the rider is not sent to take it to the pen afterwards
        public static void DismountPrefix(Pawn rider, ref bool clearReservation, ref bool waitForRider)
        {
            try
            {
                if (rider == null || rider.Spawned || !rider.InVehicle()) return;
                waitForRider = false;
                if (S.keepOwnership) clearReservation = false;
            }
            catch { }
        }

        // ---------- getting out ----------
        public static void DisembarkPostfix(VehiclePawn __instance, Pawn pawn)
        {
            if (!S.remountAfter || pawn == null || !pawn.RaceProps.Humanlike || !pawn.Spawned || pawn.Downed) return;
            try
            {
                Pawn animal = GU.ReservedMount(pawn);
                if (animal == null || animal.Dead) return;
                if (!animal.Spawned && animal.InVehicle() && animal.GetVehicle() == __instance)
                    __instance.DisembarkPawn(animal);
                TryRemount(pawn, animal, GuSafety.Inject);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Ride & Drive] getting out: " + e.Message, 0x52440002);
            }
        }

        private static bool TryRemount(Pawn pawn, Pawn animal, int how)
        {
            if (!animal.Spawned || animal.Map != pawn.Map || GU.IsMounted(pawn) || GuSafety.IsPaused(pawn)) return false;
            if (!GU.IsMountable(animal, pawn)) return false;
            GuSafety.GoMount(pawn, animal, how);
            Remounts++;
            return true;
        }

        // ---------- vehicle caravans ----------
        public static void LeavePostfix(LordToil_PrepareCaravan_LeaveWithVehicles __instance)
        {
            if (!S.caravanMounts) return;
            try
            {
                Lord lord = __instance.lord;
                if (lord == null) return;
                foreach (Pawn p in lord.ownedPawns.ToList())
                {
                    if (p is VehiclePawn || !p.RaceProps.Humanlike || !p.Spawned || p.InVehicle() || GU.IsMounted(p)) continue;
                    Pawn animal = GU.ReservedMount(p);
                    if (animal == null || !lord.ownedPawns.Contains(animal)) continue;
                    TryRemount(p, animal, GuSafety.Inject);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Ride & Drive] caravan leaving: " + e.Message, 0x52440003);
            }
        }

        public static void EnterPrefix(VehicleCaravan caravan, out List<Pawn> __state)
        {
            __state = null;
            try { if (S.caravanMounts && caravan != null) __state = caravan.PawnsListForReading.ToList(); } catch { }
        }

        public static void EnterPostfix(List<Pawn> __state)
        {
            if (__state == null) return;
            try
            {
                foreach (Pawn p in __state)
                {
                    if (p == null || !p.RaceProps.Humanlike || !p.Spawned || GU.IsMounted(p)) continue;
                    Pawn animal = GU.ReservedMount(p);
                    if (animal != null && __state.Contains(animal)) TryRemount(p, animal, GuSafety.Instant);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Ride & Drive] caravan arriving: " + e.Message, 0x52440004);
            }
        }
    }

    // Mount Passengers (optional, by reflection): passengers on an animal get off before boarding
    public static class MP
    {
        private static PropertyInfo comp;
        private static MethodInfo isPassenger, dismountPassenger, dismountForRider;

        public static void Init()
        {
            if (!Compat.MountPassengers) return;
            try
            {
                Type u = AccessTools.TypeByName("MountPassengers.MountPassengerUtility");
                Type g = AccessTools.TypeByName("MountPassengers.MountPassengerGameComponent");
                comp = AccessTools.Property(u, "Component");
                isPassenger = AccessTools.Method(u, "IsPassenger", new[] { typeof(Pawn) });
                dismountPassenger = AccessTools.Method(g, "DismountPassenger", new[] { typeof(Pawn), typeof(bool) });
                dismountForRider = AccessTools.Method(g, "DismountPassengersForPrimaryRider", new[] { typeof(Pawn), typeof(bool) });
            }
            catch (Exception e) { Log.Warning("[Ride & Drive] Mount Passengers: " + e.Message); }
        }

        public static void ForPassenger(Pawn p)
        {
            try
            {
                if (isPassenger == null || dismountPassenger == null || !(bool)isPassenger.Invoke(null, new object[] { p })) return;
                object c = comp?.GetValue(null);
                if (c != null) dismountPassenger.Invoke(c, new object[] { p, true });
            }
            catch { }
        }

        public static void ForRider(Pawn rider)
        {
            try
            {
                object c = comp?.GetValue(null);
                if (c != null && dismountForRider != null) dismountForRider.Invoke(c, new object[] { rider, true });
            }
            catch { }
        }
    }
}
