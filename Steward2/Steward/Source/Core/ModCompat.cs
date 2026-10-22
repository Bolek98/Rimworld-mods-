using System;
using System.Reflection;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.14.3: Achtung! and Keyz' Allow Utilities. Both are optional and
// found by name – no hard dependency.
//
// Achtung! – "forced work": a colonist keeps doing a job you forced, and
// Achtung stops it when that work type is off in the Work tab. The steward
// does not touch the priorities of a colonist with forced work, so a re-plan
// can never cancel it. He is planned again when the forced work is done.
//
// Keyz' Allow Utilities – its own work types "Haul+" (haul urgently) and
// "Finish" (finish off downed) are your direct orders: everyone who can do
// them gets them high (see WorkPolicies). Things marked "do not haul" are
// never carried or marked for hauling by the steward.
// ---------------------------------------------------------------------------
public static class ModCompat
{
	private static bool searched;
	private static Type forcedWorkType;
	private static MethodInfo hasForcedJob;
	private static DesignationDef noHaul;

	private static void Find()
	{
		if (searched)
			return;
		searched = true;
		try
		{
			forcedWorkType = GenTypes.GetTypeInAnyAssembly("AchtungMod.ForcedWork");
			hasForcedJob = forcedWorkType?.GetMethod("HasForcedJob", new[] { typeof(Pawn), typeof(bool) });
		}
		catch
		{
			forcedWorkType = null;
		}
		noHaul = DefDatabase<DesignationDef>.GetNamedSilentFail("KAU_NoHaulDesignation");
		if (hasForcedJob != null)
			MLog.Info("[Steward] Achtung! found – colonists with forced work keep their priorities.");
		if (DefDatabase<WorkTypeDef>.GetNamedSilentFail("KAU_UrgentHaul") != null)
			MLog.Info("[Steward] Keyz' Allow Utilities found – urgent hauling and finishing off get high priority, \"do not haul\" is respected.");
	}

	public static bool AchtungForced(Pawn p)
	{
		Find();
		if (hasForcedJob == null || p == null || Verse.Find.World == null)
			return false;
		try
		{
			WorldComponent fw = null;
			foreach (WorldComponent c in Verse.Find.World.components)
			{
				if (forcedWorkType.IsInstanceOfType(c))
				{
					fw = c;
					break;
				}
			}
			return fw != null && (bool)hasForcedJob.Invoke(fw, new object[] { p, false });
		}
		catch
		{
			hasForcedJob = null;   // something changed in Achtung – stop asking
			return false;
		}
	}

	public static bool NoHaul(Thing t)
	{
		Find();
		return noHaul != null && t?.Map != null && t.Map.designationManager.DesignationOn(t, noHaul) != null;
	}
}
