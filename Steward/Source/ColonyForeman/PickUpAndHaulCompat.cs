using System;
using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// Steward 2.11: Pick Up And Haul (Mehni / Mlie). When it is active, the
// steward's own hauling (finished goods from workbenches, stone chunks and
// slag from the home area) goes through its "haul to inventory" job: a
// colonist fills his pockets with this item and other things lying near it
// and carries them all at once. Without the mod, or when its job cannot be
// used, the game's normal hauling is used as before. No hard dependency –
// the mod is found by its work giver class.
public static class PickUpAndHaulCompat
{
	private static bool searched;
	private static WorkGiver_Scanner worker;
	private static bool failed;
	private static int failures;
	private static int retryAt;

	public static bool Present
	{
		get
		{
			if (!searched)
			{
				searched = true;
				foreach (WorkGiverDef d in DefDatabase<WorkGiverDef>.AllDefsListForReading)
				{
					if (d.giverClass != null && d.giverClass.Name == "WorkGiver_HaulToInventory"
					    && d.giverClass.Namespace != null && d.giverClass.Namespace.StartsWith("PickUpAndHaul"))
					{
						worker = d.Worker as WorkGiver_Scanner;
						break;
					}
				}
				if (worker != null)
					MLog.Info("[Steward] Pick Up And Haul found – steward hauling uses its inventory hauling.");
			}
			// 2.14.5: after an error it is tried again an in-game day later;
			// only five errors in a row switch it off until restart
			if (failed && failures < 5 && Find.TickManager != null && Find.TickManager.TicksGame >= retryAt)
				failed = false;
			return worker != null && !failed;
		}
	}

	// 2.18: the retry time is a tick of the game that was running – after
	// loading another save (often with fewer ticks) Pick Up And Haul stayed
	// off for that long. Five failures in a row still switch it off for good.
	public static void Reset()
	{
		if (failures < 5)
		{
			failed = false;
			retryAt = 0;
		}
	}

	public static bool Active => Present && ForemanMod.Settings != null && ForemanMod.Settings.usePickUpAndHaul;

	// 2.11.1: a "haul to inventory" job with an empty or uneven list of
	// things crashes Pick Up And Haul when it starts (index out of range in
	// TryMakePreToilReservations). Such a job is never handed out – the
	// game's normal hauling is used for that item instead.
	private static bool Usable(Job job)
	{
		if (job == null)
			return false;
		if (job.def == null || job.def.defName != "HaulToInventory")
			return true;
		return job.targetQueueA != null && job.targetQueueA.Count > 0
		       && job.countQueue != null && job.countQueue.Count == job.targetQueueA.Count
		       && job.targetQueueA.TrueForAll(x => x.HasThing && x.Thing.Spawned);
	}

	public static Job HaulToStorage(Pawn pawn, Thing t, bool forced)
	{
		if (Active && pawn.RaceProps.Humanlike)
		{
			try
			{
				if (!worker.ShouldSkip(pawn, forced))
				{
					Job job = worker.JobOnThing(pawn, t, forced);
					if (Usable(job))
						return job;
				}
			}
			catch (Exception e)
			{
				failed = true;
				failures++;
				retryAt = (Find.TickManager?.TicksGame ?? 0) + 60000;
				MLog.Warn("[Steward] Pick Up And Haul call failed (" + failures + "/5), normal hauling for now: " + e.Message);
			}
		}
		return HaulAIUtility.HaulToStorageJob(pawn, t, forced);
	}
}
