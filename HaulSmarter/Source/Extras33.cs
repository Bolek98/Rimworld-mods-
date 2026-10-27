using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // 3.3 Detour hauling (like While You're Up): the game already lets a
    // colonist on his way to work take ONE thing that lies on the way and has
    // storage near where he is going. Haul Master turns that single thing
    // into a small round: other things lying close to it, whose storage is
    // also near the destination, go into the pockets too – then he unloads
    // and goes on with his work.
    // ------------------------------------------------------------------
    [HarmonyPatch]
    public static class Patch_Detour
    {
        public static MethodBase TargetMethod() => AccessTools.Method(typeof(Pawn_JobTracker), "TryOpportunisticJob");

        public static void Postfix(object[] __args, Pawn ___pawn, ref Job __result)
        {
            HSSettings s = HSMod.S;
            if (__result == null || s == null || !s.detourHaul || !Compat.Hauling || !Safe.On("detour"))
                return;
            if (__result.def != JobDefOf.HaulToCell || !__result.targetA.HasThing)
                return;
            try
            {
                Pawn pawn = ___pawn;
                Job work = __args != null && __args.Length > 0 ? __args[__args.Length - 1] as Job : null;
                if (pawn?.Map == null || work == null || !HaulUtil.CanUsePockets(pawn))
                    return;
                // 3.4.1: the game's single haul stays as it is – no detour round in a ceremony, caravan, emergency
                if (StepAside.Now(pawn))
                    return;
                IntVec3 dest = work.targetA.Cell;
                if (!dest.IsValid)
                    return;
                float way = pawn.Position.DistanceTo(dest);
                Thing first = __result.targetA.Thing;
                // the extra things: close to the first one, with storage that is still on the way
                Job many = HaulUtil.MakeJob(pawn, first, false, 6f, 8, t =>
                    StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, pawn.Map, StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction, out IntVec3 c, false)
                    && c.DistanceTo(dest) <= Mathf.Max(8f, way * 0.6f));
                if (many != null && many.def == HSDefOf.HaulSmarterMany && many.targetQueueA != null && many.targetQueueA.Count > 1)
                    __result = many;
            }
            catch (Exception e)
            {
                Safe.Fail("detour", e);
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.3 Refill threshold (like Hauling Hysteresis): a stack in storage is
    // not topped up from ANOTHER storage until it falls below the threshold.
    // No more "one meal taken from the dining room shelf → someone brings
    // one meal from the kitchen". Loose things on the floor are stored as
    // always. Each stockpile/shelf can have its own threshold (button).
    // ------------------------------------------------------------------
    public static class Refill
    {
        public static readonly float[] Steps = { -1f, 0f, 0.8f, 0.5f, 0.25f };

        public static string Key(ISlotGroupParent p)
        {
            if (p is Zone z) return "Zone_" + z.ID;
            if (p is Thing t) return "Thing_" + t.thingIDNumber;
            return null;
        }

        // -1 = like in the settings
        public static float Own(ISlotGroupParent p)
        {
            string k = Key(p);
            return k != null && HSData.I != null && HSData.I.refill.TryGetValue(k, out float v) ? v : -1f;
        }

        public static float For(ISlotGroupParent p)
        {
            float own = Own(p);
            return own >= 0f ? own : (HSMod.S?.refillBelow ?? 0f);
        }

        public static void Set(ISlotGroupParent p, float v)
        {
            string k = Key(p);
            if (k == null || HSData.I == null) return;
            if (v < 0f) HSData.I.refill.Remove(k);
            else HSData.I.refill[k] = v;
        }

        public static string Label(float v)
        {
            if (v < 0f) return "HS_RefillDefault".Translate(Label(HSMod.S?.refillBelow ?? 0f));
            if (v <= 0f) return "HS_RefillNow".Translate();
            return "HS_RefillBelow".Translate(v.ToStringPercent());
        }

        public static bool Active => (HSMod.S != null && HSMod.S.refillBelow > 0f) || (HSData.I != null && HSData.I.refill.Count > 0);

        public static Gizmo Button(ISlotGroupParent p)
        {
            float cur = Own(p);
            return new Command_Action
            {
                defaultLabel = "HS_RefillGizmo".Translate() + ": " + Label(cur),
                defaultDesc = "HS_RefillGizmo_Desc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", false) ?? BaseContent.BadTex,
                action = delegate
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (float v in Steps)
                    {
                        float vv = v;
                        opts.Add(new FloatMenuOption(Label(vv), () => Set(p, vv)));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            };
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.IsGoodStoreCell))]
    public static class Patch_Refill
    {
        public static void Postfix(IntVec3 c, Map map, Thing t, ref bool __result)
        {
            if (!__result || t == null || !t.Spawned || t.def.stackLimit <= 1 || !Refill.Active || !Safe.On("refill"))
                return;
            try
            {
                SlotGroup from = t.GetSlotGroup();
                if (from == null)
                    return;                                   // loose things are stored as always
                SlotGroup to = map.haulDestinationManager.SlotGroupAt(c);
                if (to == null || to == from)
                    return;
                float th = Refill.For(to.parent);
                if (th <= 0f)
                    return;
                if (c.GetItemCount(map) < c.GetMaxItemsAllowedInCell(map))
                    return;                                   // a free place in the cell: a new stack, not a top-up
                List<Thing> list = map.thingGrid.ThingsListAt(c);
                for (int i = 0; i < list.Count; i++)
                {
                    Thing h = list[i];
                    if (h != t && h.def == t.def && h.CanStackWith(t) && h.stackCount < h.def.stackLimit && h.stackCount >= h.def.stackLimit * th)
                    {
                        __result = false;
                        return;
                    }
                }
            }
            catch (Exception e)
            {
                Safe.Fail("refill", e);
            }
        }
    }

    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.GetGizmos))]
    public static class Patch_RefillGizmoZone
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Zone_Stockpile __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            if (HSMod.S != null && Safe.On("refill"))
                yield return Refill.Button(__instance);
        }
    }

    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.GetGizmos))]
    public static class Patch_RefillGizmoShelf
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building_Storage __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            if (HSMod.S != null && Safe.On("refill") && __instance.Faction == Faction.OfPlayer)
                yield return Refill.Button(__instance);
        }
    }

    // ------------------------------------------------------------------
    // 3.3 Traders put it away: what you buy from a trade caravan at home is
    // carried into your storage by the traders themselves. If they cannot
    // (busy, leaving, no place), colonists take it first after a while.
    // ------------------------------------------------------------------
    public static class Bought
    {
        private static readonly List<KeyValuePair<Thing, Lord>> pending = new List<KeyValuePair<Thing, Lord>>();
        private static readonly Dictionary<int, int> waitSince = new Dictionary<int, int>();
        public const int Wait = 1500;   // ~ 25 in-game minutes, then colonists help

        public static bool Any => waitSince.Count > 0;

        public static void Add(Thing t, Lord lord)
        {
            if (t != null && lord != null)
                pending.Add(new KeyValuePair<Thing, Lord>(t, lord));
        }

        public static bool Urgent(Thing t)
        {
            if (t == null || !waitSince.TryGetValue(t.thingIDNumber, out int at))
                return false;
            if (!t.Spawned || t.IsInAnyStorage())
            {
                waitSince.Remove(t.thingIDNumber);
                return false;
            }
            return Find.TickManager.TicksGame - at > Wait;
        }

        public static void Dispatch()
        {
            if (pending.Count == 0)
                return;
            var list = pending.ToList();
            pending.Clear();
            HSSettings s = HSMod.S;
            if (s == null || !s.traderCarry || !Safe.On("trader"))
                return;
            try
            {
                foreach (var group in list.GroupBy(kv => kv.Value))
                {
                    Lord lord = group.Key;
                    var carriers = lord.ownedPawns.Where(p => p.Spawned && !p.Downed && !p.Dead && p.RaceProps.Humanlike && !p.InMentalState
                                                             && p.Map != null && p.Map.IsPlayerHome && p.carryTracker != null).ToList();
                    int i = 0;
                    foreach (var kv in group)
                    {
                        Thing t = kv.Key;
                        if (t == null || !t.Spawned || t.Map == null || !t.Map.IsPlayerHome)
                            continue;
                        waitSince[t.thingIDNumber] = Find.TickManager.TicksGame;
                        for (int k = 0; k < carriers.Count; k++)
                        {
                            Pawn p = carriers[(i + k) % carriers.Count];
                            if (p.Map != t.Map)
                                continue;
                            if (!StoreUtility.TryFindBestBetterStoreCellFor(t, p, t.Map, StoragePriority.Unstored, Faction.OfPlayer, out IntVec3 cell, true))
                                break;    // no place for it anywhere – the colonists decide later
                            lord.extraForbiddenThings.Remove(t);
                            Job job = HaulAIUtility.HaulToCellStorageJob(p, t, cell, false);
                            if (job == null)
                                continue;
                            p.jobs.jobQueue.EnqueueLast(job, JobTag.Misc);
                            i = (i + k + 1) % Mathf.Max(1, carriers.Count);
                            break;
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Safe.Fail("trader", e);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_TraderTracker), nameof(Pawn_TraderTracker.GiveSoldThingToPlayer))]
    public static class Patch_BoughtThing
    {
        public static void Prefix(Pawn ___pawn, out int __state)
        {
            __state = ___pawn?.GetLord()?.extraForbiddenThings?.Count ?? -1;
        }

        public static void Postfix(Pawn ___pawn, int __state)
        {
            if (__state < 0)
                return;
            try
            {
                Lord lord = ___pawn.GetLord();
                if (lord == null || lord.extraForbiddenThings.Count <= __state)
                    return;
                Bought.Add(lord.extraForbiddenThings[lord.extraForbiddenThings.Count - 1], lord);
            }
            catch (Exception e)
            {
                Safe.Fail("trader", e);
            }
        }
    }

    [HarmonyPatch(typeof(TradeDeal), nameof(TradeDeal.TryExecute))]
    public static class Patch_TradeDone
    {
        public static void Postfix() => Bought.Dispatch();
    }

    // ------------------------------------------------------------------
    // 3.3 Animals: a trained hauling animal takes several things in one trip
    // too (never food – it might eat it). Mechanoids that haul use the
    // normal hauling work, so they get it through WorkGiver_HaulMany.
    // ------------------------------------------------------------------
    [HarmonyPatch]
    public static class Patch_AnimalHaul
    {
        public static MethodBase TargetMethod() => AccessTools.Method(typeof(JobGiver_Haul), "TryGiveJob");

        public static void Postfix(Pawn pawn, ref Job __result)
        {
            HSSettings s = HSMod.S;
            if (s == null || !s.animalHaul || pawn == null || !pawn.RaceProps.Animal || !Safe.On("animals"))
                return;
            try
            {
                if (HaulUtil.HasTracked(pawn) && HaulUtil.CanUnloadHere(pawn))
                {
                    __result = JobMaker.MakeJob(HSDefOf.HaulSmarterUnload);
                    return;
                }
                if (__result == null || __result.def != JobDefOf.HaulToCell || !__result.targetA.HasThing || !HaulUtil.CanHaulMany(pawn))
                    return;
                Job many = HaulUtil.MakeJob(pawn, __result.targetA.Thing, false);
                if (many != null && many.def == HSDefOf.HaulSmarterMany)
                    __result = many;
            }
            catch (Exception e)
            {
                Safe.Fail("animals", e);
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.4.1 Extra hauling steps aside: no pocket filling on the way and no
    // detour rounds while a colonist takes part in a ritual, party, speech,
    // wedding or any other gathering, while he forms a caravan or loads a
    // shuttle / transport pods (every one of these is a Lord), while he is
    // bleeding, or while a downed colonist he could tend is waiting.
    // ------------------------------------------------------------------
    public static class StepAside
    {
        private static readonly Dictionary<int, int> patientTick = new Dictionary<int, int>();
        private static readonly Dictionary<int, bool> patientAny = new Dictionary<int, bool>();

        // a ceremony, gathering, caravan forming or transporter loading (any Lord) – the pawn belongs to it
        public static bool InLord(Pawn pawn)
        {
            try
            {
                return pawn != null && pawn.GetLord() != null;
            }
            catch
            {
                return false;
            }
        }

        // cached per map for 250 ticks – a downed colonist who needs tending now
        private static bool PatientWaiting(Map map)
        {
            int now = Find.TickManager.TicksGame;
            if (patientTick.TryGetValue(map.uniqueID, out int at) && now - at < 250 && patientAny.TryGetValue(map.uniqueID, out bool v))
                return v;
            bool any = false;
            List<Pawn> list = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < list.Count && !any; i++)
            {
                Pawn p = list[i];
                if (p.Downed && HealthAIUtility.ShouldBeTendedNowByPlayer(p))
                    any = true;
            }
            patientTick[map.uniqueID] = now;
            patientAny[map.uniqueID] = any;
            return any;
        }

        // true = do not start extra (opportunistic / detour) hauling now
        public static bool Now(Pawn pawn)
        {
            if (pawn == null)
                return true;
            if (InLord(pawn))
                return true;
            if (HSMod.S == null || !HSMod.S.stepAside)
                return false;
            try
            {
                if (pawn.health?.hediffSet != null && pawn.health.hediffSet.BleedRateTotal > 0.01f)
                    return true;
                if (pawn.Map != null && pawn.workSettings != null && !pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)
                    && pawn.workSettings.WorkIsActive(WorkTypeDefOf.Doctor) && PatientWaiting(pawn.Map))
                    return true;
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Haul Master] step-aside check: " + e.Message, 0x4a51e9);
            }
            return false;
        }
    }

    // 3.4.1: a colonist who leaves with a caravan or boards a shuttle / transport pod keeps
    // everything in his pockets – Haul Master only forgets that it was meant for storage here
    public static class ForgetPockets
    {
        public static void Run(Pawn pawn)
        {
            try
            {
                if (pawn?.inventory == null || HSData.I == null)
                    return;
                foreach (Thing t in HaulUtil.TrackedItems(pawn).ToList())
                    HSData.Untrack(t);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Haul Master] forget pockets: " + e.Message, 0x4a51ea);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.ExitMap))]
    public static class Patch_ForgetOnExitMap
    {
        public static void Prefix(Pawn __instance) => ForgetPockets.Run(__instance);
    }

    [HarmonyPatch(typeof(CompTransporter), nameof(CompTransporter.Notify_ThingAdded))]
    public static class Patch_ForgetOnBoard
    {
        public static void Prefix(Thing t)
        {
            if (t is Pawn p)
                ForgetPockets.Run(p);
        }
    }
}
