using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // Hauling several things in one trip.
    //
    // A colonist walks to every item, puts it in his inventory (no item ever
    // jumps into the pockets from a distance), then carries everything to
    // storage. The store cell is chosen for every item at the moment it is
    // unloaded, by the game's own storage search – so shelves, deep storage
    // and modded storage that hold many things in one cell work like the
    // game expects. What does not fit anywhere is dropped next to the
    // colonist, never destroyed.
    // ------------------------------------------------------------------
    public static class HaulUtil
    {
        // How many kilos the pawn can still take before he is over-encumbered.
        public static float FreeKg(Pawn pawn)
        {
            float bonus = HSMod.S != null ? HSMod.S.bonusKg : 0f;
            return MassUtility.Capacity(pawn) + bonus - MassUtility.GearAndInventoryMass(pawn);
        }

        public static float UnitMass(Thing t)
        {
            float m = t.GetStatValue(StatDefOf.Mass);
            return m > 0f ? m : 0f;
        }

        public static int CountThatFits(Thing t, float freeKg)
        {
            float m = UnitMass(t);
            if (m <= 0.0001f)
                return t.stackCount;
            return Mathf.Min(t.stackCount, Mathf.FloorToInt(freeKg / m));
        }

        public static bool CanUsePockets(Pawn pawn)
        {
            return pawn != null && pawn.RaceProps.Humanlike && pawn.inventory != null && pawn.Faction == Faction.OfPlayer
                   && !pawn.IsQuestLodger() && MassUtility.CanEverCarryAnything(pawn);
        }

        // 3.3: who may carry several things in one hauling trip – colonists, and (if on) hauling mechanoids and trained animals
        public static bool CanHaulMany(Pawn pawn)
        {
            if (CanUsePockets(pawn))
                return true;
            HSSettings s = HSMod.S;
            if (s == null || pawn == null || pawn.inventory == null || pawn.Faction != Faction.OfPlayer || !MassUtility.CanEverCarryAnything(pawn))
                return false;
            if (pawn.RaceProps.IsMechanoid)
                return s.mechHaul;
            if (pawn.RaceProps.Animal)
                return s.animalHaul;
            return false;
        }

        // animals might eat food from their pockets
        private static bool NotFor(Pawn pawn, Thing t) => pawn.RaceProps.Animal && t.def.IsIngestible;

        // Puts a part of the stack into the pockets and remembers it. It only
        // merges with stacks we picked up ourselves – the colonist's own
        // medicine or food is never unloaded by mistake.
        public static int PickUp(Pawn pawn, Thing thing, int count)
        {
            if (thing == null || !thing.Spawned || count <= 0)
                return 0;
            count = Mathf.Min(count, thing.stackCount);
            Thing part = thing.SplitOff(count);
            ThingOwner inv = pawn.inventory.innerContainer;
            foreach (Thing have in inv)
            {
                if (HSData.IsTracked(have) && have.CanStackWith(part) && have.stackCount < have.def.stackLimit)
                {
                    have.TryAbsorbStack(part, true);
                    if (part.Destroyed || part.stackCount <= 0)
                        return count;
                }
            }
            if (inv.TryAdd(part, false))
            {
                HSData.Track(part);
                if (thing.def.soundPickup != null)
                    thing.def.soundPickup.PlayOneShot(new TargetInfo(pawn.Position, pawn.Map));
                return count;
            }
            GenPlace.TryPlaceThing(part, pawn.Position, pawn.Map, ThingPlaceMode.Near);
            return 0;
        }

        public static IEnumerable<Thing> TrackedItems(Pawn pawn)
        {
            if (pawn?.inventory == null)
                yield break;
            List<Thing> list = pawn.inventory.innerContainer.InnerListForReading;
            for (int i = list.Count - 1; i >= 0; i--)
                if (i < list.Count && HSData.IsTracked(list[i]))
                    yield return list[i];
        }

        public static bool HasTracked(Pawn pawn) => TrackedItems(pawn).Any();

        // Unload only at home and only if at least one item has a place;
        // on a quest map the items stay in the pockets.
        public static bool CanUnloadHere(Pawn pawn)
        {
            if (pawn?.Map == null || !pawn.Map.IsPlayerHome || !HasTracked(pawn))
                return false;
            foreach (Thing t in TrackedItems(pawn))
                if (!HSData.ForBuild(t) && StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out _, false))
                    return true;
            return false;
        }

        public static float TrackedMass(Pawn pawn)
        {
            float m = 0f;
            foreach (Thing t in TrackedItems(pawn))
                m += UnitMass(t) * t.stackCount;
            return m;
        }

        // 3.0.1: things in the pockets must not be lost for the colony – food, medicine and
        // anything carried for longer than an hour go to storage after the current job
        public const int MaxPocketTicks = 2500;
        // 3.3: both times can be set in the settings (sliders)
        public static int PocketTicks => Mathf.Max(250, Mathf.RoundToInt((HSMod.S?.pocketHours ?? 1f) * 2500f));
        public static int FoodTicks => Mathf.Max(125, Mathf.RoundToInt((HSMod.S?.foodMinutes ?? 15) * 2500f / 60f));

        public static bool NeededByOthers(Thing t) => t.def.IsIngestible || t.def.IsMedicine || t.def.IsDrug;

        public static bool MustUnloadSoon(Pawn pawn)
        {
            foreach (Thing t in TrackedItems(pawn))
                if (!HSData.ForBuild(t) && HSData.AgeTicks(t) > (NeededByOthers(t) ? FoodTicks : PocketTicks))
                    return true;
            return false;
        }

        public static bool ShouldUnloadNow(Pawn pawn)
        {
            if (!HasTracked(pawn))
                return false;
            float cap = MassUtility.Capacity(pawn) + (HSMod.S?.bonusKg ?? 0f);
            if (cap <= 0f)
                return true;
            if (MassUtility.GearAndInventoryMass(pawn) >= cap * (HSMod.S?.unloadAt ?? 0.8f))
                return true;
            // a colonist who never hauls would carry it around for ever
            return pawn.WorkTypeIsDisabled(WorkTypeDefOf.Hauling) || pawn.workSettings == null
                   || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling);
        }

        public static void DropAllTracked(Pawn pawn)
        {
            if (pawn?.inventory == null || !pawn.Spawned)
                return;
            foreach (Thing t in TrackedItems(pawn).ToList())
                if (pawn.inventory.innerContainer.TryDrop(t, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _))
                    HSData.Untrack(t);
        }

        // ---- planning -----------------------------------------------------
        private static bool GoodExtra(Pawn pawn, Thing t, Thing first)
        {
            if (NotFor(pawn, t))
                return false;
            if (t == first || !t.Spawned || t.def.category != ThingCategory.Item || t is Corpse || t.IsForbidden(pawn))
                return false;
            if (t.IsInValidBestStorage())
                return false;
            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false))
                return false;
            return StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction, out _, false);
        }

        public static Job MakeJob(Pawn pawn, Thing first, bool forced, float radiusOverride = -1f, int maxOverride = -1, Predicate<Thing> extraOk = null)
        {
            if (NotFor(pawn, first))
                return HaulAIUtility.HaulToStorageJob(pawn, first, forced);
            if (first is Corpse || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, first, forced))
                return null;
            if (!StoreUtility.TryFindBestBetterStorageFor(first, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(first), pawn.Faction,
                    out IntVec3 cell, out IHaulDestination dest))
            {
                JobFailReason.Is(HaulAIUtility.NoEmptyPlaceLowerTrans);
                return null;
            }
            // containers (and pawns that cannot use pockets) are hauled the normal way
            if (!(dest is ISlotGroupParent) || !CanHaulMany(pawn) || !Compat.Hauling)
                return HaulAIUtility.HaulToStorageJob(pawn, first, forced);

            float free = FreeKg(pawn);
            int firstCount = CountThatFits(first, free);
            if (firstCount <= 0)
                return HaulAIUtility.HaulToStorageJob(pawn, first, forced);

            var things = new List<LocalTargetInfo> { first };
            var counts = new List<int> { firstCount };
            free -= firstCount * UnitMass(first);

            int maxItems = maxOverride > 0 ? maxOverride : HSMod.S?.maxItems ?? 30;
            float radius = radiusOverride > 0f ? radiusOverride : HSMod.S?.scanRadius ?? 14;
            if (free > 0.5f && maxItems > 1)
            {
                IntVec3 from = first.Position;
                var near = new List<Thing>();
                float r2 = radius * radius;
                foreach (Thing t in pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling())
                {
                    if (t != first && t.Spawned && t.Position.DistanceToSquared(from) <= r2)
                        near.Add(t);
                }
                // nearest first, walking on from the last item taken
                IntVec3 at = from;
                int checks = 0;
                while (near.Count > 0 && things.Count < maxItems && free > 0.5f && checks < 120)
                {
                    int bi = 0;
                    int bd = int.MaxValue;
                    for (int i = 0; i < near.Count; i++)
                    {
                        int d = near[i].Position.DistanceToSquared(at);
                        if (d < bd) { bd = d; bi = i; }
                    }
                    Thing t = near[bi];
                    near.RemoveAt(bi);
                    checks++;
                    if (!GoodExtra(pawn, t, first) || (extraOk != null && !extraOk(t)))
                        continue;
                    int n = CountThatFits(t, free);
                    if (n <= 0)
                        continue;
                    things.Add(t);
                    counts.Add(n);
                    free -= n * UnitMass(t);
                    at = t.Position;
                }
            }

            // 3.0: shortest way from the colonist through all items to the storage
            if (HSMod.S != null && HSMod.S.smartRoute && things.Count > 2)
                Route.Order(pawn.Position, cell, things, counts);

            Job job = JobMaker.MakeJob(HSDefOf.HaulSmarterMany);
            job.targetQueueA = things;
            job.countQueue = counts;
            job.targetC = first;
            job.playerForced = forced;
            return job;
        }
    }

    public class WorkGiver_HaulMany : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling();

        // 3.0: perishable, weather-threatened and valuable things first
        public override bool Prioritized => HSMod.S != null && (HSMod.S.AnyPriority || Cleanup.Any || Bought.Any);

        public override float GetPriority(Pawn pawn, TargetInfo t) => Priority.Of(t.Thing);

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return !Compat.Hauling || !Safe.On("haul") || !HaulUtil.CanHaulMany(pawn) || pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling().Count == 0;
        }

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is Corpse || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced))
                return false;
            if (!StoreUtility.TryFindBestBetterStorageFor(t, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction, out _, out _, false))
            {
                JobFailReason.Is(HaulAIUtility.NoEmptyPlaceLowerTrans);
                return false;
            }
            return true;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (t is Corpse)
                return null;
            try
            {
                return HaulUtil.MakeJob(pawn, t, forced);
            }
            catch (Exception e)
            {
                Safe.Fail("haul", e);
                return null;
            }
        }
    }

    // Things still in the pockets (job interrupted, harvest/mining yields).
    public class WorkGiver_UnloadPockets : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !HaulUtil.HasTracked(pawn);

        public override Job NonScanJob(Pawn pawn)
        {
            return HaulUtil.CanUnloadHere(pawn) ? JobMaker.MakeJob(HSDefOf.HaulSmarterUnload) : null;
        }
    }

    // ------------------------------------------------------------------
    // The job: collect every queued item, then unload all of it.
    // HaulSmarterUnload uses the same driver with an empty queue.
    // ------------------------------------------------------------------
    public class JobDriver_HaulMany : JobDriver
    {
        private Thing unloading;
        private int fails;
        private bool placedAny;

        public Thing Unloading => unloading;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref unloading, "unloading");
            Scribe_Values.Look(ref fails, "fails", 0);
            Scribe_Values.Look(ref placedAny, "placedAny", false);
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            List<LocalTargetInfo> q = job.GetTargetQueue(TargetIndex.A);
            if (q.NullOrEmpty())
                return true;
            // the item the job was made for must be ours; the rest is skipped if someone else takes it
            LocalTargetInfo main = job.targetC.HasThing && q.Contains(job.targetC) ? job.targetC : q[0];
            if (!pawn.Reserve(main, job, 1, -1, null, errorOnFailed))
                return false;
            for (int i = 0; i < q.Count; i++)
                if (q[i] != main && pawn.CanReserve(q[i]))
                    pawn.Reserve(q[i], job, 1, -1, null, false);
            return true;
        }

        public override string GetReport()
        {
            if (unloading != null)
                return "HS_ReportUnload".Translate(unloading.LabelShort);
            return base.GetReport();
        }

        private bool Usable(Thing t)
        {
            return t != null && t.Spawned && t.MapHeld == pawn.Map && !t.IsForbidden(pawn) && !t.IsBurning()
                   && (pawn.CanReserve(t) || pawn.Map.reservationManager.ReservedBy(t, pawn, job));
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            Toil collectStart = Toils_General.Label();
            Toil unloadStart = Toils_General.Label();
            Toil done = Toils_General.Label();

            // ---- collect ------------------------------------------------
            yield return collectStart;
            Toil next = ToilMaker.MakeToil("HS_NextItem");
            next.initAction = delegate
            {
                List<LocalTargetInfo> q = job.GetTargetQueue(TargetIndex.A);
                while (!q.NullOrEmpty())
                {
                    LocalTargetInfo t = q[0];
                    q.RemoveAt(0);
                    int count = job.countQueue.NullOrEmpty() ? int.MaxValue : job.countQueue[0];
                    if (!job.countQueue.NullOrEmpty())
                        job.countQueue.RemoveAt(0);
                    if (Usable(t.Thing))
                    {
                        job.SetTarget(TargetIndex.A, t);
                        job.count = count;
                        return;
                    }
                }
                job.SetTarget(TargetIndex.A, LocalTargetInfo.Invalid);
                JumpToToil(unloadStart);
            };
            yield return next;

            Toil go = ToilMaker.MakeToil("HS_GoToItem");
            go.initAction = delegate
            {
                Thing t = job.GetTarget(TargetIndex.A).Thing;
                if (!Usable(t))
                    JumpToToil(collectStart);
                else if (pawn.Position.AdjacentTo8WayOrInside(t.Position))
                    ReadyForNextToil();
                else
                    pawn.pather.StartPath(t, PathEndMode.ClosestTouch);
            };
            go.defaultCompleteMode = ToilCompleteMode.PatherArrival;
            go.AddPreTickAction(delegate
            {
                if (!Usable(job.GetTarget(TargetIndex.A).Thing))
                    JumpToToil(collectStart);
            });
            yield return go;

            Toil take = ToilMaker.MakeToil("HS_Take");
            take.initAction = delegate
            {
                Thing t = job.GetTarget(TargetIndex.A).Thing;
                if (t != null && t.Spawned && pawn.Position.AdjacentTo8WayOrInside(t.Position))
                {
                    int n = Mathf.Min(job.count, HaulUtil.CountThatFits(t, HaulUtil.FreeKg(pawn)));
                    if (n > 0)
                        HaulUtil.PickUp(pawn, t, n);
                }
                if (t != null && pawn.Map.reservationManager.ReservedBy(t, pawn, job))
                    pawn.Map.reservationManager.Release(t, pawn, job);
                JumpToToil(collectStart);
            };
            take.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return take;

            // ---- unload -------------------------------------------------
            yield return unloadStart;
            // 3.4: a materials pick-up for building ends here – the things stay in the pockets for the builder
            Toil fetchEnd = ToilMaker.MakeToil("HS_FetchEnd");
            fetchEnd.initAction = delegate
            {
                if (job.def != HSDefOf.HaulSmarterFetch)
                    return;
                foreach (Thing t in HaulUtil.TrackedItems(pawn))
                    if (MaterialPickup.Wanted(job, t.def))
                        HSData.MarkBuild(t);
                JumpToToil(done);
            };
            fetchEnd.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return fetchEnd;
            Toil choose = ToilMaker.MakeToil("HS_ChooseStore");
            choose.initAction = delegate
            {
                unloading = null;
                Thing best = null;
                IntVec3 bestCell = IntVec3.Invalid;
                int bestDist = int.MaxValue;
                foreach (Thing item in HaulUtil.TrackedItems(pawn).ToList())
                {
                    if (HSData.ForBuild(item))
                        continue;   // 3.4: material the builder fetched – kept for the construction
                    if (StoreUtility.TryFindBestBetterStoreCellFor(item, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out IntVec3 c))
                    {
                        int d = c.DistanceToSquared(pawn.Position);
                        if (d < bestDist) { bestDist = d; best = item; bestCell = c; }
                    }
                    else if (pawn.Map.IsPlayerHome)
                    {
                        // nowhere to put it at home: leave it here, it is not lost
                        if (pawn.inventory.innerContainer.TryDrop(item, pawn.Position, pawn.Map, ThingPlaceMode.Near, out _))
                            HSData.Untrack(item);
                    }
                    // away from home it stays in the pockets
                }
                if (best == null)
                {
                    if (placedAny)
                        HSData.RecordTrip(pawn);
                    placedAny = false;
                    JumpToToil(done);
                    return;
                }
                unloading = best;
                if (pawn.CanReserve(bestCell))
                    pawn.Reserve(bestCell, job, 1, -1, null, false);
                job.SetTarget(TargetIndex.B, bestCell);
            };
            yield return choose;

            yield return Toils_Goto.GotoCell(TargetIndex.B, PathEndMode.ClosestTouch);

            Toil place = ToilMaker.MakeToil("HS_Place");
            place.initAction = delegate
            {
                IntVec3 cell = job.GetTarget(TargetIndex.B).Cell;
                Thing item = unloading;
                unloading = null;
                if (item == null || !pawn.inventory.innerContainer.Contains(item))
                {
                    JumpToToil(unloadStart);
                    return;
                }
                if (pawn.carryTracker.CarriedThing != null)
                    pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out _);
                pawn.inventory.innerContainer.TryTransferToContainer(item, pawn.carryTracker.innerContainer, item.stackCount, out Thing carried, false);
                int placedCount = 0;
                float unitKg = HaulUtil.UnitMass(item);
                if (carried != null)
                {
                    HSData.Untrack(item);
                    HSData.Untrack(carried);
                    int had = carried.stackCount;
                    if (pawn.carryTracker.TryDropCarriedThing(cell, ThingPlaceMode.Direct, out _))
                        placedCount = had;
                    else
                    {
                        Thing rest = pawn.carryTracker.CarriedThing;
                        placedCount = had - (rest?.stackCount ?? 0);
                        if (rest != null)
                        {
                            // the cell was not big enough: the rest goes back into the pockets
                            pawn.carryTracker.innerContainer.TryTransferToContainer(rest, pawn.inventory.innerContainer, rest.stackCount, out Thing back, false);
                            if (back != null)
                                HSData.Track(back);
                        }
                    }
                }
                if (placedCount <= 0)
                {
                    fails++;
                    if (fails > 6)
                    {
                        HaulUtil.DropAllTracked(pawn);
                        JumpToToil(done);
                        return;
                    }
                }
                else
                {
                    fails = 0;
                    placedAny = true;
                    HSData.RecordKg(pawn, placedCount * unitKg);
                }
                if (pawn.Map.reservationManager.ReservedBy(cell, pawn, job))
                    pawn.Map.reservationManager.Release(cell, pawn, job);
                JumpToToil(unloadStart);
            };
            place.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return place;

            yield return done;
        }
    }

    // ------------------------------------------------------------------
    // Harvest / mining: the colonist puts the yield straight into his
    // pockets (only what lies right next to him) and keeps working. He
    // unloads when the pockets are full, or when he runs out of that work.
    // ------------------------------------------------------------------
    public static class Pockets
    {
        private static HashSet<ThingDef> harvestDefs, mineDefs;

        private static void Build()
        {
            if (harvestDefs != null)
                return;
            harvestDefs = new HashSet<ThingDef>();
            mineDefs = new HashSet<ThingDef>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                if (d.plant?.harvestedThingDef != null)
                    harvestDefs.Add(d.plant.harvestedThingDef);
                if (d.building?.mineableThing != null)
                    mineDefs.Add(d.building.mineableThing);
            }
        }

        public static void Scoop(Pawn pawn, bool mine)
        {
            if (pawn == null || !pawn.Spawned || !Compat.Pockets(mine) || !HaulUtil.CanUsePockets(pawn) || pawn.Drafted)
                return;
            Build();
            HashSet<ThingDef> defs = mine ? mineDefs : harvestDefs;
            Map map = pawn.Map;
            bool took = false;
            for (int i = 0; i < 9; i++)
            {
                IntVec3 c = pawn.Position + GenAdj.AdjacentCellsAndInside[i];
                if (!c.InBounds(map))
                    continue;
                List<Thing> here = c.GetThingList(map);
                for (int k = here.Count - 1; k >= 0; k--)
                {
                    Thing t = here[k];
                    if (t.def.category != ThingCategory.Item || !defs.Contains(t.def) || t.IsForbidden(pawn) || t.IsInValidStorage())
                        continue;
                    if (!pawn.CanReserve(t) || !StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, map, StoragePriority.Unstored, pawn.Faction, out _, false))
                        continue;
                    int n = HaulUtil.CountThatFits(t, HaulUtil.FreeKg(pawn));
                    if (n > 0 && HaulUtil.PickUp(pawn, t, n) > 0)
                        took = true;
                }
            }
            if (took && HaulUtil.ShouldUnloadNow(pawn) && !pawn.jobs.jobQueue.Any(q => q.job?.def == HSDefOf.HaulSmarterUnload))
                pawn.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(HSDefOf.HaulSmarterUnload));
        }
    }

    // The yield is picked up when the work toil ends (finish action), so the
    // driver keeps its own toils and indices – saves stay compatible.
    [HarmonyPatch(typeof(JobDriver_PlantWork), "MakeNewToils")]
    public static class Patch_PlantWork
    {
        public static void Postfix(JobDriver_PlantWork __instance, ref IEnumerable<Toil> __result)
        {
            __result = Hook(__instance, __result, false);
        }

        public static IEnumerable<Toil> Hook(JobDriver d, IEnumerable<Toil> toils, bool mine)
        {
            foreach (Toil t in toils)
            {
                if (t.defaultCompleteMode == ToilCompleteMode.Never)
                    t.AddFinishAction(() =>
                    {
                        if (!Safe.On("pockets"))
                            return;
                        try
                        {
                            Pockets.Scoop(d.pawn, mine);
                        }
                        catch (Exception e)
                        {
                            Safe.Fail("pockets", e);
                        }
                    });
                yield return t;
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver_Mine), "MakeNewToils")]
    public static class Patch_Mine
    {
        public static void Postfix(JobDriver_Mine __instance, ref IEnumerable<Toil> __result)
        {
            __result = Patch_PlantWork.Hook(__instance, __result, true);
        }
    }

    // A colonist with nothing else to do empties his pockets first.
    // Only the normal work node (not the emergency one before food and sleep).
    [HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
    public static class Patch_IdleUnload
    {
        public static void Postfix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
        {
            if (__instance.emergency || __result.Job != null || pawn == null || pawn.Drafted || !pawn.Spawned || !pawn.IsColonistPlayerControlled)
                return;
            try
            {
                if (HaulUtil.CanUnloadHere(pawn))
                    __result = new ThinkResult(JobMaker.MakeJob(HSDefOf.HaulSmarterUnload), __instance);
            }
            catch (Exception e)
            {
                Safe.Fail("unload", e);
            }
        }
    }

    // The game throws raw food and unused drugs out of the pockets after a
    // while – but not the things we are carrying to storage.
    [HarmonyPatch(typeof(JobGiver_DropUnusedInventory), "Drop")]
    public static class Patch_KeepHauled
    {
        public static bool Prefix(Thing thing) => !HSData.IsTracked(thing);
    }

}
