using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// Steward 2.0: how much of each work waits on the map – measured by the game's
// own work givers, so every work type of every mod is counted the same way
// (bills, blueprints, plants, animals, patients, things to haul …). One work
// type is measured at a time, a few times a second, so there is never a hitch:
// the whole map is known after a few seconds and then kept up to date.
public sealed class Demand
{
	public readonly Dictionary<WorkTypeDef, int> jobs = new Dictionary<WorkTypeDef, int>();
	public readonly Dictionary<WorkTypeDef, int> measuredAt = new Dictionary<WorkTypeDef, int>();
	private int next;

	private static readonly HashSet<WorkGiverDef> broken = new HashSet<WorkGiverDef>();
	private const int SampleSize = 12;
	private const int MaxListed = 6000;
	private const double BudgetMs = 3.0;
	private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

	public bool Complete => WorkCatalog.Visible.All(w => WorkCatalog.Kind(w) == WorkKind.Emergency || measuredAt.ContainsKey(w));

	public int Jobs(WorkTypeDef w) => jobs.TryGetValue(w, out int v) ? v : 0;

	// how many colonists the waiting work needs: grows slowly with the amount
	// (1 job → 1, 4 → 2, 8 → 3, 32 → 4, 64 → 5, 256 → 6 …), minus working mechs
	public int Need(Map map, WorkTypeDef w, int workers)
	{
		int j = Jobs(w);
		if (j <= 0)
			return 0;
		int need = 1 + Mathf.FloorToInt(Mathf.Log(j, 2f) / 1.5f);
		int mechs = WorkCatalog.Mechs(map, w);
		if (mechs > 0)
			need -= Mathf.FloorToInt(mechs * 0.7f);
		return Mathf.Clamp(need, 1, Math.Max(1, workers));
	}

	// measure the next work type in turn
	public void Step(Map map, ForemanSettings s)
	{
		List<WorkTypeDef> all = WorkCatalog.Visible;
		if (all.Count == 0 || map == null)
			return;
		for (int tries = 0; tries < all.Count; tries++)
		{
			WorkTypeDef w = all[next++ % all.Count];
			if (WorkCatalog.Kind(w) == WorkKind.Emergency)
				continue;
			jobs[w] = Measure(map, w, s);
			measuredAt[w] = Find.TickManager.TicksGame;
			return;
		}
	}

	public void MeasureAll(Map map, ForemanSettings s)
	{
		foreach (WorkTypeDef w in WorkCatalog.Visible)
		{
			if (WorkCatalog.Kind(w) == WorkKind.Emergency)
				continue;
			jobs[w] = Measure(map, w, s);
			measuredAt[w] = Find.TickManager.TicksGame;
		}
	}

	public static int Measure(Map map, WorkTypeDef w, ForemanSettings s)
	{
		Pawn pawn = WorkCatalog.Workers(map, s).FirstOrDefault(p => p.Spawned && !p.Downed && !p.Drafted && WorkCatalog.CanDo(p, w, null));
		if (pawn == null || w.workGiversByPriority == null)
			return 0;
		long start = Stopwatch.GetTimestamp();
		int total = 0;
		foreach (WorkGiverDef g in w.workGiversByPriority)
		{
			if (broken.Contains(g) || (Stopwatch.GetTimestamp() - start) * TicksToMs > BudgetMs)
				continue;
			try
			{
				WorkGiver worker = g.Worker;
				if (worker == null || worker.ShouldSkip(pawn, false))
					continue;
				if (worker is WorkGiver_Scanner sc)
				{
					total += FromThings(map, pawn, sc);
					total += FromCells(pawn, sc);
				}
				else if (worker.NonScanJob(pawn) != null)
					total += 1;
			}
			catch (Exception ex)
			{
				broken.Add(g);
				MLog.WarnOnce("[Steward] work giver " + g.defName + " (" + (g.modContentPack?.Name ?? "?") + ") cannot be measured, skipped: " + ex.Message, g.index ^ 0x3d3d0000);
			}
		}
		return total;
	}

	private static int FromThings(Map map, Pawn pawn, WorkGiver_Scanner sc)
	{
		IEnumerable<Thing> src = sc.PotentialWorkThingsGlobal(pawn);
		if (src == null)
		{
			ThingRequest req = sc.PotentialWorkThingRequest;
			if (req.IsUndefined)
				return 0;
			src = map.listerThings.ThingsMatching(req);
		}
		var list = new List<Thing>();
		foreach (Thing t in src)
		{
			if (t != null && t.Spawned)
				list.Add(t);
			if (list.Count >= MaxListed)
				break;
		}
		if (list.Count == 0)
			return 0;
		int k = Math.Min(SampleSize, list.Count), ok = 0;
		PathEndMode pem = sc.PathEndMode;
		Danger danger = sc.MaxPathDanger(pawn);
		for (int i = 0; i < k; i++)
		{
			Thing t = list[(int)((long)i * list.Count / k)];
			if (t.IsForbidden(pawn) || !pawn.CanReach(t, pem, danger))
				continue;
			if (sc.HasJobOnThing(pawn, t, false))
				ok++;
		}
		return Scale(list.Count, k, ok);
	}

	private static int FromCells(Pawn pawn, WorkGiver_Scanner sc)
	{
		IEnumerable<IntVec3> src = sc.PotentialWorkCellsGlobal(pawn);
		if (src == null)
			return 0;
		var list = new List<IntVec3>();
		foreach (IntVec3 c in src)
		{
			list.Add(c);
			if (list.Count >= MaxListed)
				break;
		}
		if (list.Count == 0)
			return 0;
		int k = Math.Min(SampleSize, list.Count), ok = 0;
		for (int i = 0; i < k; i++)
		{
			IntVec3 c = list[(int)((long)i * list.Count / k)];
			if (c.IsForbidden(pawn) || !pawn.CanReach(c, sc.PathEndMode, sc.MaxPathDanger(pawn)))
				continue;
			if (sc.HasJobOnCell(pawn, c, false))
				ok++;
		}
		return Scale(list.Count, k, ok);
	}

	private static int Scale(int count, int sampled, int ok)
	{
		if (ok == 0)
			return 0;
		return Math.Max(1, (int)Math.Round((double)count * ok / sampled));
	}
}
