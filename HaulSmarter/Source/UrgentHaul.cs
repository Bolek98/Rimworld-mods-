using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // 3.5.0 "Haul urgently": an order (Orders tab) that marks things to be
    // hauled before anything else. The mark goes away when the thing is
    // picked up, destroyed or lies in its best storage.
    // ------------------------------------------------------------------
    [DefOf]
    public static class HSUrgentDefOf
    {
        public static DesignationDef HaulSmarterUrgent;

        static HSUrgentDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(HSUrgentDefOf));
    }

    public class Designator_HaulUrgent : Designator_Haul
    {
        protected override DesignationDef Designation => HSUrgentDefOf.HaulSmarterUrgent;

        public Designator_HaulUrgent()
        {
            defaultLabel = "HS_HaulUrgent".Translate();
            defaultDesc = "HS_HaulUrgent_Desc".Translate();
            icon = ContentFinder<Texture2D>.Get("UI/Designators/Haul");
            defaultIconColor = new Color(1f, 0.62f, 0.3f);
            hotKey = null;
        }

        public override void DesignateThing(Thing t)
        {
            // the player wants it hauled now: allow it and make it haulable like the game's own "haul" order
            if (t.IsForbidden(Faction.OfPlayer))
                t.SetForbidden(false, false);
            base.DesignateThing(t);
            DesignationManager dm = Map.designationManager;
            if (!t.def.alwaysHaulable && !t.IsInAnyStorage() && dm.DesignationOn(t, DesignationDefOf.Haul) == null)
                dm.AddDesignation(new Designation(t, DesignationDefOf.Haul));
        }
    }

    public static class Urgent
    {
        public static bool Is(Thing t)
        {
            Map map = t?.MapHeld;
            return map != null && t.Spawned && HSUrgentDefOf.HaulSmarterUrgent != null && map.designationManager.DesignationOn(t, HSUrgentDefOf.HaulSmarterUrgent) != null;
        }

        public static bool AnyOn(Map map) => map != null && HSUrgentDefOf.HaulSmarterUrgent != null && map.designationManager.AnySpawnedDesignationOfDef(HSUrgentDefOf.HaulSmarterUrgent);

        public static IEnumerable<Thing> Things(Map map)
        {
            if (map == null || HSUrgentDefOf.HaulSmarterUrgent == null) return Enumerable.Empty<Thing>();
            return map.designationManager.SpawnedDesignationsOfDef(HSUrgentDefOf.HaulSmarterUrgent).Where(d => d.target.HasThing).Select(d => d.target.Thing).ToList();
        }

        // in the Orders tab next to the game's "Haul things"
        public static void AddDesignator()
        {
            try
            {
                DesignationCategoryDef orders = DefDatabase<DesignationCategoryDef>.GetNamedSilentFail("Orders");
                if (orders == null) return;
                if (orders.specialDesignatorClasses != null && !orders.specialDesignatorClasses.Contains(typeof(Designator_HaulUrgent)))
                    orders.specialDesignatorClasses.Add(typeof(Designator_HaulUrgent));
                List<Designator> list = orders.AllResolvedDesignators;
                if (list == null || list.Any(d => d is Designator_HaulUrgent)) return;
                int at = list.FindIndex(d => d.GetType() == typeof(Designator_Haul));
                Designator_HaulUrgent des = new Designator_HaulUrgent { isOrder = true };
                if (at >= 0) list.Insert(at + 1, des);
                else list.Add(des);
            }
            catch (Exception e)
            {
                Log.Warning("[Haul Master] \"Haul urgently\" order not available: " + e.Message);
            }
        }

        // every 250 ticks: drop marks on things that are gone or already in their best storage
        public static void Tidy(Map map)
        {
            try
            {
                if (!AnyOn(map)) return;
                List<Designation> list = map.designationManager.SpawnedDesignationsOfDef(HSUrgentDefOf.HaulSmarterUrgent).ToList();
                foreach (Designation d in list)
                {
                    Thing t = d.target.Thing;
                    if (t == null || t.Destroyed || !t.Spawned || t.IsInValidBestStorage())
                        map.designationManager.RemoveDesignation(d);
                }
            }
            catch (Exception e)
            {
                Safe.Fail("urgent", e);
            }
        }
    }

    // hauls the marked things first (before every other hauling job), several at once when possible
    public class WorkGiver_HaulUrgent : WorkGiver_Scanner
    {
        public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;
        public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

        public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn) => Urgent.Things(pawn.Map);

        public override bool ShouldSkip(Pawn pawn, bool forced = false) => !Safe.On("urgent") || !Urgent.AnyOn(pawn.Map);

        public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
        {
            if (!Urgent.Is(t) || !HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced))
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
            try
            {
                if (!(t is Corpse) && Compat.Hauling && HaulUtil.CanHaulMany(pawn))
                {
                    Job many = HaulUtil.MakeJob(pawn, t, forced);
                    if (many != null)
                        return many;
                }
                return HaulAIUtility.HaulToStorageJob(pawn, t, forced);
            }
            catch (Exception e)
            {
                Safe.Fail("urgent", e);
                return null;
            }
        }
    }
}
