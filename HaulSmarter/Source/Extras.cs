using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // 3.2 Self-safety: a part of the mod that keeps failing (usually because
    // of another mod) switches itself off after 5 errors and says so once,
    // instead of filling the log. Back on after a restart or the settings button.
    // ------------------------------------------------------------------
    public static class Safe
    {
        private const int Limit = 5;
        private static readonly Dictionary<string, int> errors = new Dictionary<string, int>();
        private static readonly HashSet<string> off = new HashSet<string>();

        public static bool On(string feature) => !off.Contains(feature);
        public static bool AnyOff => off.Count > 0;

        public static string Label(string feature) => ("HS_Feat_" + feature).Translate();

        public static string OffList() => string.Join(", ", off.Select(Label).ToArray());

        public static void Reset()
        {
            off.Clear();
            errors.Clear();
        }

        public static void Fail(string feature, Exception e)
        {
            errors.TryGetValue(feature, out int n);
            errors[feature] = ++n;
            if (n == 1)
                Log.Warning("[Haul Smarter] " + feature + ": " + e);
            if (n < Limit || off.Contains(feature))
                return;
            off.Add(feature);
            Log.Warning("[Haul Smarter] '" + feature + "' failed " + n + " times and is switched off until the next start. Last error: " + e.Message);
            try
            {
                Messages.Message("HS_SafeOff".Translate(Label(feature)), MessageTypeDefOf.CautionInput, false);
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.2 Unload before a fight: a drafted colonist puts the things he carries
    // for storage down at once – not slowed and nothing lost if he goes down.
    // The pile is hauled again later like anything else.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
    public static class Patch_DropOnDraft
    {
        public static void Postfix(Pawn_DraftController __instance, bool value)
        {
            if (!value || HSMod.S == null || !HSMod.S.dropOnDraft || !Safe.On("draft"))
                return;
            try
            {
                Pawn pawn = __instance.pawn;
                if (pawn?.Map == null || !pawn.Spawned || !HaulUtil.HasTracked(pawn))
                    return;
                foreach (Thing t in HaulUtil.TrackedItems(pawn).ToList())
                {
                    HSData.Untrack(t);
                    pawn.inventory.innerContainer.TryDrop(t, pawn.Position, pawn.Map, ThingPlaceMode.Near, out Thing _);
                }
            }
            catch (Exception e)
            {
                Safe.Fail("draft", e);
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.2 Bench supply: a button on a workbench makes a small stockpile right
    // next to it. It takes exactly the ingredients of the bench's active bills
    // (updated by itself), with "preferred" priority – the crafter takes them
    // from beside the bench instead of walking across the map.
    // It is an ordinary stockpile: without this mod it simply stays one.
    // ------------------------------------------------------------------
    public sealed class BenchSupply : IExposable
    {
        public Building bench;
        public Zone_Stockpile zone;
        [Unsaved] public HashSet<ThingDef> lastAllowed;

        public void ExposeData()
        {
            Scribe_References.Look(ref bench, "bench");
            Scribe_References.Look(ref zone, "zone");
        }
    }

    public sealed class BenchSupplyComp : MapComponent
    {
        public List<BenchSupply> list = new List<BenchSupply>();

        public BenchSupplyComp(Map map) : base(map) { }

        public static BenchSupplyComp For(Map m) => m?.GetComponent<BenchSupplyComp>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref list, "benchSupply", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                list ??= new List<BenchSupply>();
                list.RemoveAll(b => b == null || b.bench == null || b.zone == null);
            }
        }

        public BenchSupply Of(Building b)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].bench == b)
                    return list[i];
            return null;
        }

        public override void MapComponentTick()
        {
            if (list.Count == 0 || Find.TickManager.TicksGame % 300 != 113 || !Safe.On("bench"))
                return;
            try
            {
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    BenchSupply s = list[i];
                    bool zoneGone = s.zone == null || s.zone.Map != map || !map.zoneManager.AllZones.Contains(s.zone);
                    if (s.bench == null || s.bench.Destroyed || !s.bench.Spawned || s.bench.Map != map)
                    {
                        if (!zoneGone)
                            s.zone.Delete();
                        list.RemoveAt(i);
                        continue;
                    }
                    if (zoneGone)
                    {
                        list.RemoveAt(i);
                        continue;
                    }
                    Sync(s);
                }
            }
            catch (Exception e)
            {
                Safe.Fail("bench", e);
            }
        }

        public static HashSet<ThingDef> Wanted(Building bench)
        {
            var set = new HashSet<ThingDef>();
            if (!(bench is IBillGiver giver) || giver.BillStack == null)
                return set;
            foreach (Bill bill in giver.BillStack)
            {
                if (bill?.recipe?.ingredients == null || bill.suspended || !bill.ShouldDoNow())
                    continue;
                foreach (IngredientCount ing in bill.recipe.ingredients)
                    foreach (ThingDef d in ing.filter.AllowedThingDefs)
                        if (ing.IsFixedIngredient || bill.ingredientFilter == null || bill.ingredientFilter.Allows(d))
                            if (d.EverStorable(false))
                                set.Add(d);
            }
            return set;
        }

        public static void Sync(BenchSupply s)
        {
            HashSet<ThingDef> want = Wanted(s.bench);
            if (s.lastAllowed != null && s.lastAllowed.SetEquals(want))
                return;
            s.lastAllowed = want;
            ThingFilter f = s.zone.settings.filter;
            f.SetDisallowAll();
            foreach (ThingDef d in want)
                f.SetAllow(d, true);
        }

        public void Start(Building bench)
        {
            if (Of(bench) != null)
                return;
            int want = Mathf.Clamp(HSMod.S?.benchCells ?? 4, 1, 9);
            IntVec3 from = bench.InteractionCell.IsValid ? bench.InteractionCell : bench.Position;
            var cells = new List<IntVec3>();
            CellRect around = bench.OccupiedRect().ExpandedBy(2).ClipInsideMap(map);
            foreach (IntVec3 c in around)
            {
                if (bench.OccupiedRect().Contains(c) || c == bench.InteractionCell || !c.Standable(map) || c.GetZone(map) != null
                    || c.GetEdifice(map) != null || c.Fogged(map) || c.GetRoom(map) != from.GetRoom(map))
                    continue;
                cells.Add(c);
            }
            cells = cells.OrderBy(c => c.DistanceToSquared(from)).Take(want).ToList();
            if (cells.Count == 0)
            {
                Messages.Message("HS_BenchNoRoom".Translate(), bench, MessageTypeDefOf.RejectInput, false);
                return;
            }
            var zone = new Zone_Stockpile(StorageSettingsPreset.DefaultStockpile, map.zoneManager);
            zone.label = map.zoneManager.NewZoneName("HS_BenchZone".Translate(bench.LabelShortCap));
            map.zoneManager.RegisterZone(zone);
            foreach (IntVec3 c in cells)
                zone.AddCell(c);
            zone.settings.Priority = StoragePriority.Preferred;
            zone.settings.filter.SetDisallowAll();
            var s = new BenchSupply { bench = bench, zone = zone };
            list.Add(s);
            Sync(s);
            Messages.Message("HS_BenchStarted".Translate(bench.LabelShortCap, cells.Count), bench, MessageTypeDefOf.TaskCompletion, false);
        }

        public void Stop(Building bench)
        {
            BenchSupply s = Of(bench);
            if (s == null)
                return;
            list.Remove(s);
            if (s.zone != null && map.zoneManager.AllZones.Contains(s.zone))
                s.zone.Delete();
        }
    }

    [HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
    public static class Patch_BenchSupplyGizmo
    {
        private static Texture2D icon;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building __instance)
        {
            foreach (Gizmo g in __result)
                yield return g;
            if (!(__instance is Building_WorkTable) || __instance.Faction != Faction.OfPlayerSilentFail || !__instance.Spawned || !Safe.On("bench"))
                yield break;
            BenchSupplyComp comp = BenchSupplyComp.For(__instance.Map);
            if (comp == null)
                yield break;
            icon ??= ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile", false) ?? BaseContent.BadTex;
            bool on = comp.Of(__instance) != null;
            Building b = __instance;
            yield return new Command_Toggle
            {
                defaultLabel = "HS_Bench".Translate(),
                defaultDesc = "HS_Bench_Desc".Translate(),
                icon = icon,
                isActive = () => on,
                toggleAction = () =>
                {
                    if (comp.Of(b) != null)
                        comp.Stop(b);
                    else
                        comp.Start(b);
                }
            };
        }
    }

    // ------------------------------------------------------------------
    // 3.2 Fuel round: a colonist who goes to refuel takes enough fuel for the
    // other torches, stoves and generators nearby that also need it, and fills
    // them one after another – one trip to the stockpile instead of one each.
    // ------------------------------------------------------------------
    public static class FuelRound
    {
        private const float Radius = 22f;
        private const int MaxExtra = 5;

        private static bool Wants(Pawn pawn, Thing x, ThingDef fuel)
        {
            CompRefuelable c = x.TryGetComp<CompRefuelable>();
            if (c == null || c.Props.atomicFueling || c.IsFull || !c.allowAutoRefuel || !c.ShouldAutoRefuelNow)
                return false;
            if (c.FuelPercentOfMax > 0f && !c.Props.allowRefuelIfNotEmpty)
                return false;
            if (!c.Props.fuelFilter.Allows(fuel) || x.Faction != pawn.Faction || x.Fogged() || x.IsBurning() || x.IsForbidden(pawn))
                return false;
            if (x.TryGetComp(out CompInteractable ci) && ci.Props.cooldownPreventsRefuel && ci.OnCooldown)
                return false;
            return pawn.CanReserve(x) && pawn.CanReach(x, PathEndMode.Touch, Danger.Some);
        }

        public static Job Make(Pawn pawn, Thing first, Thing fuel)
        {
            Map map = pawn.Map;
            var extra = new List<Thing>();
            foreach (Thing x in map.listerThings.ThingsInGroup(ThingRequestGroup.Refuelable))
            {
                if (x == first || !x.Spawned || (x.Position - first.Position).LengthHorizontalSquared > Radius * Radius || x is Building_Turret)
                    continue;
                if (Wants(pawn, x, fuel.def))
                    extra.Add(x);
            }
            if (extra.Count == 0)
                return null;
            // nearest first, then always the next nearest to the last one
            var order = new List<Thing>();
            IntVec3 at = first.Position;
            while (extra.Count > 0 && order.Count < MaxExtra)
            {
                Thing best = extra.MinBy(t => (t.Position - at).LengthHorizontalSquared);
                extra.Remove(best);
                order.Add(best);
                at = best.Position;
            }
            int total = first.TryGetComp<CompRefuelable>().GetFuelCountToFullyRefuel();
            foreach (Thing t in order)
                total += t.TryGetComp<CompRefuelable>().GetFuelCountToFullyRefuel();
            Job job = JobMaker.MakeJob(HSDefOf.HaulSmarterRefuelRound, first, fuel);
            job.targetQueueA = order.Select(t => new LocalTargetInfo(t)).ToList();
            job.count = Mathf.Min(total, fuel.stackCount);
            return job;
        }
    }

    [HarmonyPatch(typeof(RefuelWorkGiverUtility), nameof(RefuelWorkGiverUtility.RefuelJob))]
    public static class Patch_FuelRound
    {
        public static void Postfix(Pawn pawn, Thing t, JobDef customRefuelJob, ref Job __result)
        {
            if (__result == null || __result.def != JobDefOf.Refuel || customRefuelJob != null && customRefuelJob != JobDefOf.Refuel)
                return;
            if (HSMod.S == null || !HSMod.S.fuelRound || !Safe.On("fuel") || pawn?.Map == null || !HaulUtil.CanUsePockets(pawn))
                return;
            try
            {
                Thing fuel = __result.targetB.Thing;
                if (fuel == null || !fuel.Spawned)
                    return;
                Job round = FuelRound.Make(pawn, t, fuel);
                if (round != null)
                    __result = round;
            }
            catch (Exception e)
            {
                Safe.Fail("fuel", e);
            }
        }
    }

    public class JobDriver_RefuelRound : JobDriver
    {
        private Thing Refuelable => job.GetTarget(TargetIndex.A).Thing;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            if (!pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed))
                return false;
            if (!pawn.Reserve(job.GetTarget(TargetIndex.B), job, 1, -1, null, errorOnFailed))
                return false;
            if (job.targetQueueA != null)
                foreach (LocalTargetInfo t in job.targetQueueA)
                    pawn.Reserve(t, job, 1, -1, null, false);
            return true;
        }

        private bool NextTarget()
        {
            while (job.targetQueueA != null && job.targetQueueA.Count > 0)
            {
                LocalTargetInfo next = job.targetQueueA[0];
                job.targetQueueA.RemoveAt(0);
                Thing t = next.Thing;
                CompRefuelable c = t?.TryGetComp<CompRefuelable>();
                if (t == null || !t.Spawned || c == null || c.IsFull)
                    continue;
                job.SetTarget(TargetIndex.A, t);
                return true;
            }
            return false;
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch)
                .FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
            yield return Toils_Haul.StartCarryThing(TargetIndex.B, false, true).FailOnDestroyedNullOrForbidden(TargetIndex.B);

            Toil gotoTarget = Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            Toil next = ToilMaker.MakeToil("HSRefuelNext");
            next.defaultCompleteMode = ToilCompleteMode.Instant;
            Toil done = ToilMaker.MakeToil("HSRefuelDone");
            done.defaultCompleteMode = ToilCompleteMode.Instant;

            yield return gotoTarget;
            Toil wait = Toils_General.Wait(240).WithProgressBarToilDelay(TargetIndex.A);
            wait.AddFailCondition(() => Refuelable == null || Refuelable.Destroyed || pawn.carryTracker.CarriedThing == null);
            yield return wait;
            Toil fill = ToilMaker.MakeToil("HSRefuelFill");
            fill.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                CompRefuelable c = Refuelable?.TryGetComp<CompRefuelable>();
                if (carried != null && c != null && !c.IsFull && c.Props.fuelFilter.Allows(carried))
                    c.Refuel(new List<Thing> { carried });
            };
            fill.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return fill;

            next.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried != null && !carried.Destroyed && carried.stackCount > 0 && NextTarget())
                    JumpToToil(gotoTarget);
            };
            yield return next;

            // whatever is left goes into the pockets and to storage with the next unload
            done.initAction = () =>
            {
                Thing carried = pawn.carryTracker.CarriedThing;
                if (carried == null || carried.Destroyed)
                    return;
                if (pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing dropped) && dropped != null
                    && HaulUtil.CanUsePockets(pawn) && dropped.Spawned)
                    HaulUtil.PickUp(pawn, dropped, HaulUtil.CountThatFits(dropped, HaulUtil.FreeKg(pawn)));
            };
            yield return done;
        }
    }

    // ------------------------------------------------------------------
    // 3.2 Merge half stacks: when nothing else waits, haulers join half
    // stacks of the same thing in the same stockpile – more free cells.
    // The smaller stack always goes onto the bigger one (no back and forth).
    // ------------------------------------------------------------------
    public class WorkGiver_MergeStacks : WorkGiver_Scanner
    {
        private static readonly Dictionary<int, KeyValuePair<int, List<Thing>>> cache = new Dictionary<int, KeyValuePair<int, List<Thing>>>();
        private static readonly Dictionary<Thing, Thing> targetOf = new Dictionary<Thing, Thing>();

        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

        public override bool ShouldSkip(Pawn pawn, bool forced = false)
        {
            return HSMod.S == null || !HSMod.S.mergeStacks || !Safe.On("merge") || pawn.Map == null || Candidates(pawn.Map).Count == 0;
        }

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => Candidates(pawn.Map);

        private static List<Thing> Candidates(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(map.uniqueID, out var kv) && now - kv.Key < 600 && now >= kv.Key)
                return kv.Value;
            var list = new List<Thing>();
            try
            {
                foreach (SlotGroup g in map.haulDestinationManager.AllGroupsListForReading)
                {
                    // one stack per cell only – shelves and deep storage are left alone
                    if (g.parent is Building_Storage bs && bs.def.building != null && bs.def.building.maxItemsInCell > 1)
                        continue;
                    var byKind = new Dictionary<string, List<Thing>>();
                    foreach (Thing t in g.HeldThings)
                    {
                        if (t.def.stackLimit <= 1 || t.stackCount >= t.def.stackLimit || t.def.category != ThingCategory.Item)
                            continue;
                        string key = t.def.defName + "|" + (t.Stuff?.defName ?? "");
                        if (!byKind.TryGetValue(key, out var l))
                            byKind[key] = l = new List<Thing>();
                        l.Add(t);
                    }
                    foreach (List<Thing> l in byKind.Values)
                    {
                        if (l.Count < 2)
                            continue;
                        l.Sort((a, b) => b.stackCount != a.stackCount ? b.stackCount.CompareTo(a.stackCount) : a.thingIDNumber.CompareTo(b.thingIDNumber));
                        Thing big = l[0];
                        for (int i = l.Count - 1; i >= 1; i--)
                        {
                            Thing small = l[i];
                            if (small.CanStackWith(big))
                            {
                                list.Add(small);
                                targetOf[small] = big;
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Safe.Fail("merge", e);
            }
            if (targetOf.Count > 2000)
                targetOf.Clear();
            cache[map.uniqueID] = new KeyValuePair<int, List<Thing>>(now, list);
            return list;
        }

        public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            try
            {
                if (!targetOf.TryGetValue(t, out Thing big) || big == null || !big.Spawned || !t.Spawned || big == t)
                    return null;
                if (big.stackCount >= big.def.stackLimit || !t.CanStackWith(big) || t.IsForbidden(pawn) || big.IsForbidden(pawn))
                    return null;
                if (!pawn.CanReserve(t) || !pawn.CanReserve(big.Position) || !pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Some))
                    return null;
                int room = big.def.stackLimit - big.stackCount;
                Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, t, big.Position);
                job.count = Mathf.Min(t.stackCount, room);
                job.haulMode = HaulMode.ToCellStorage;
                return job;
            }
            catch (Exception e)
            {
                Safe.Fail("merge", e);
                return null;
            }
        }
    }
}
