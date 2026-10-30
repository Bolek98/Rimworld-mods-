using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace QoLFW
{
    // 1.0.1: things the game only lets you destroy by attacking (ancient walls, ruins, wrecks …)
    // can be deconstructed like your own buildings. Buildings of hostile factions stay as they are.
    public static class DeconAll
    {
        public static bool Forced(Building b)
        {
            if (b == null || !QoLMod.S.deconstructAll) return false;
            ThingDef d = b.def;
            if (d.building == null || d.building.IsDeconstructible) return false;
            if (d.building.isNaturalRock || d.mineable || !d.destroyable || !d.useHitPoints) return false;
            if (d.category != ThingCategory.Building) return false;
            Faction f = b.Faction;
            if (f != null && !f.IsPlayer && f.HostileTo(Faction.OfPlayer)) return false;
            return true;
        }
    }

    [HarmonyPatch(typeof(Building), "DeconstructibleBy")]
    public static class Patch_DeconstructibleBy
    {
        public static void Postfix(Building __instance, Faction faction, ref AcceptanceReport __result)
        {
            try
            {
                if (__result.Accepted || faction == null || !faction.IsPlayer) return;
                if (DeconAll.Forced(__instance)) __result = AcceptanceReport.WasAccepted;
            }
            catch { }
        }
    }

    // they have no build work, so the game would remove them at once – instead a colonist takes them apart
    [HarmonyPatch(typeof(Designator_Deconstruct), "DesignateThing")]
    public static class Patch_DeconstructDesignate
    {
        public static bool Prefix(Designator_Deconstruct __instance, Thing t)
        {
            try
            {
                if (DebugSettings.godMode || !(t is Building b) || !DeconAll.Forced(b)) return true;
                if (b.Map.designationManager.DesignationOn(b, DesignationDefOf.Deconstruct) == null)
                    b.Map.designationManager.AddDesignation(new Designation(b, DesignationDefOf.Deconstruct));
                return false;
            }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(JobDriver_Deconstruct), "TotalNeededWork", MethodType.Getter)]
    public static class Patch_DeconstructWork
    {
        public static void Postfix(JobDriver_Deconstruct __instance, ref float __result)
        {
            try
            {
                Building b = __instance.job?.targetA.Thing as Building;
                if (DeconAll.Forced(b))
                    __result = Mathf.Clamp(Mathf.Max(__result, b.MaxHitPoints * 1.5f), 120f, 3000f);
            }
            catch { }
        }
    }
}
