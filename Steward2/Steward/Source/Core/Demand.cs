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
// (blueprints, plants, animals, patients, things to haul …). One work type is
// measured at a time, a few times a second, so there is never a hitch.
//  - up to three of the best-suited colonists are tried (a hunter without a gun,
//    a colonist with an area restriction or a skill limit on a bill would see nothing)
//  - bills are counted from the bill stacks (no ingredient search: that would also
//    make the game wait before the real crafters search again)
//  - short lists are checked whole, long ones sampled at moving places
//  - when nobody can be asked (everyone drafted, downed …) the last value stays
public sealed class Demand
{
	public readonly Dictionary<WorkTypeDef, int> jobs = new Dictionary<WorkTypeDef, int>();
	public readonly Dictionary<WorkTypeDef, int> measuredAt = new Dictionary<WorkTypeDef, int>();
	private readonly Dictionary<WorkTypeDef, int> lastHit = new Dictionary<WorkTypeDef, int>();
	private int next;
	private static int rotate;

	private static readonly HashSet<WorkGiverDef> broken = new HashSet<WorkGiverDef>();
	private const int SampleSize = 12;
	private const int CheckAllBelow = 60;
	private const int MaxListed = 6000;
	private const double BudgetMs = 4.0;
	private static readonly double TicksToMs = 1000.0 / Stopwatch.Frequency;

	public int Jobs(WorkTypeDef w) => jobs.TryGetValue(w, out int v) ? v : 0;

	// how many colonists the waiting work needs: grows slowly with the amount
	// (1 job → 1, 4 → 2, 8 → 3, 32 → 4, 64 → 5, 256 → 6 …), minus working mechs
	public int Need(Map map, WorkTypeDef w, int workers)
	{
		int j = Jobs(w);
		if (j <= 0)
			return 0;
		int need = 1 + Mathf.FloorToInt(Mathf.Log(j, 2f) / 1.5f);
		if (ForemanMod.Settings == null || ForemanMod.Settings.countMechs)
		{
			int mechs = WorkCatalog.Mechs(map, w);
			if (mechs > 0)
				need -= Mathf.FloorToInt(mechs * 0.7f);
		}
		return Mathf.Clamp(need, 1, Math.Max(1, workers));
	}

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
			Store(w, Measure(map, w, s));
			return;
		}
	}

	public void MeasureAll(Map map, ForemanSettings s)
	{
		foreach (WorkTypeDef w in WorkCatalog.Visible)
			if (WorkCatalog.Kind(w) != WorkKind.Emergency)
				Store(w, Measure(map, w, s));
	}

	private void Store(WorkTypeDef w, int v)
	{
		int now = Find.TickManager.TicksGame;
		if (v < 0)
			return;   // nobody could be asked – keep what we knew
		if (v > 0)
			lastHit[w] = now;
		else if (lastHit.TryGetValue(w, out int hit) && now - hit < 1250 && now >= hit)
			v = 1;   // rare work just seen (one patient among many) – not gone at once
		jobs[w] = v;
		measuredAt[w] = now;
	}

	// -1 = nobody could be asked
	public static int Measure(Map map, WorkTypeDef w, ForemanSettings s)
	{
		if (w.workGiversByPriority == null)
			return 0;
		List<Pawn> askers = WorkCatalog.Workers(map, s)
			.Where(p => p.Spawned && !p.Downed && !p.Drafted && !p.InMentalState && WorkCatalog.CanDo(p, w, null))
			.OrderByDescending(p => Fitness.Of(p, w, s, null))
			.Take(3).ToList();
		if (askers.Count == 0)
			return -1;
		long start = Stopwatch.GetTimestamp();
		int total = 0;
		rotate++;
		foreach (WorkGiverDef g in w.workGiversByPriority)
		{
			if (broken.Contains(g) || Over(start))
				continue;
			try
			{
				WorkGiver worker = g.Worker;
				if (worker == null)
					continue;
				if (worker is WorkGiver_DoBill bills)
				{
					total += Bills(map, bills);
					continue;
				}
				int best = 0;
				foreach (Pawn pawn in askers)
				{
					if (Over(start))
						break;
					if (worker.ShouldSkip(pawn, false))
						continue;
					int n = 0;
					if (worker is WorkGiver_Scanner sc)
						n = FromThings(map, pawn, sc, start) + FromCells(pawn, sc, start);
					else if (worker.NonScanJob(pawn) != null)
						n = 1;
					if (n > best)
						best = n;
					if (best > 0)
						break;   // somebody sees the work – enough
				}
				total += best;
			}
			catch (Exception ex)
			{
				broken.Add(g);
				MLog.WarnOnce("[Steward] work giver " + g.defName + " (" + (g.modContentPack?.Name ?? "?") + ") cannot be measured, skipped: " + ex.Message, g.index ^ 0x3d3d0000);
			}
		}
		return total;
	}

	private static bool Over(long start) => (Stopwatch.GetTimestamp() - start) * TicksToMs > BudgetMs;

	// bills that want doing on the benches of this giver
	private static int Bills(Map map, WorkGiver_DoBill g)
	{
		ThingRequest req = g.PotentialWorkThingRequest;
		if (req.IsUndefined)
			return 0;
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsMatching(req))
		{
			if (!(t is IBillGiver bg) || bg.BillStack == null || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (t is Building_WorkTable wt && !wt.CurrentlyUsableForBills())
				continue;
			if (g.def.fixedBillGiverDefs != null && !g.def.fixedBillGiverDefs.Contains(t.def))
				continue;
			foreach (Bill b in bg.BillStack.Bills)
				if (b.ShouldDoNow())
					n++;
		}
		return n;
	}

	private static IEnumerable<int> Picks(int count)
	{
		if (count <= CheckAllBelow)
		{
			for (int i = 0; i < count; i++)
				yield return i;
			yield break;
		}
		int off = rotate % Math.Max(1, count / SampleSize);
		for (int i = 0; i < SampleSize; i++)
			yield return Math.Min(count - 1, (int)((long)i * count / SampleSize) + off);
	}

	private static int FromThings(Map map, Pawn pawn, WorkGiver_Scanner sc, long start)
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
		int k = 0, ok = 0;
		PathEndMode pem = sc.PathEndMode;
		Danger danger = sc.MaxPathDanger(pawn);
		foreach (int i in Picks(list.Count))
		{
			if (Over(start) && k > 0)
				break;
			k++;
			Thing t = list[i];
			if (t.IsForbidden(pawn) || !pawn.CanReach(t, pem, danger))
				continue;
			if (sc.HasJobOnThing(pawn, t, false))
				ok++;
		}
		return Scale(list.Count, k, ok);
	}

	private static int FromCells(Pawn pawn, WorkGiver_Scanner sc, long start)
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
		int k = 0, ok = 0;
		foreach (int i in Picks(list.Count))
		{
			if (Over(start) && k > 0)
				break;
			k++;
			IntVec3 c = list[i];
			if (c.IsForbidden(pawn))
				continue;
			if (sc.HasJobOnCell(pawn, c, false))
				ok++;
		}
		return Scale(list.Count, k, ok);
	}

	private static int Scale(int count, int sampled, int ok)
	{
		if (ok == 0 || sampled == 0)
			return 0;
		return Math.Max(1, (int)Math.Round((double)count * ok / sampled));
	}
}
