using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// Steward 2.24.0: Harvest And Haul (Laredson). Colonists put their harvest,
// mined ore, cut wood … into their pockets and later unload them with one
// "unload harvest" job. Harvest And Haul does not save which things it is
// carrying – after loading a game they stayed in the pockets for ever.
// The steward hands such things back to Harvest And Haul (registers them and
// queues its unload job), so they go to storage in one trip. Things Harvest
// And Haul still tracks are left to it. No hard dependency – found by name.
public static class HarvestAndHaulCompat
{
	private static bool searched;
	private static PropertyInfo instProp;
	private static FieldInfo harvestedField;
	private static MethodInfo register;
	private static JobDef unloadDef;

	public static bool Present
	{
		get
		{
			Find();
			return register != null && unloadDef != null;
		}
	}

	private static void Find()
	{
		if (searched)
			return;
		searched = true;
		try
		{
			Type comp = GenTypes.GetTypeInAnyAssembly("HarvestAndHaul.HAH_GameComponent");
			if (comp == null)
				return;
			instProp = AccessTools.Property(comp, "Inst");
			harvestedField = AccessTools.Field(comp, "_harvested");
			register = AccessTools.Method(comp, "RegisterHarvest", new[] { typeof(Pawn), typeof(Thing) });
			unloadDef = DefDatabase<JobDef>.GetNamedSilentFail("HAH_UnloadHarvestInventory");
			if (register != null && unloadDef != null)
				MLog.Info("[Steward] Harvest And Haul found – forgotten harvest in pockets is unloaded by its own job.");
		}
		catch (Exception ex)
		{
			register = null;
			MLog.Warn("[Steward] Harvest And Haul hook failed: " + ex.Message);
		}
	}

	public static bool IsUnloadJob(Job j) => j != null && unloadDef != null && j.def == unloadDef;

	// true when Harvest And Haul itself still knows this thing in this pawn's pockets
	public static bool Tracks(Pawn pawn, Thing t)
	{
		if (!Present || instProp == null || harvestedField == null)
			return false;
		try
		{
			object inst = instProp.GetValue(null);
			if (!(harvestedField.GetValue(inst) is IDictionary dict) || !dict.Contains(pawn))
				return false;
			return dict[pawn] is HashSet<Thing> set && set.Contains(t);
		}
		catch
		{
			return false;
		}
	}

	// hands the things to Harvest And Haul and queues one unload job
	public static bool Unload(Pawn pawn, List<Thing> things)
	{
		if (!Present || pawn?.jobs == null || things == null || things.Count == 0)
			return false;
		try
		{
			foreach (Thing t in things)
				register.Invoke(null, new object[] { pawn, t });
			if (IsUnloadJob(pawn.CurJob))
				return true;
			foreach (QueuedJob q in pawn.jobs.jobQueue)
				if (IsUnloadJob(q?.job))
					return true;
			pawn.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(unloadDef), JobTag.Misc);
			return true;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] Harvest And Haul unload failed: " + ex.Message, 0x5a7e11);
			return false;
		}
	}
}
