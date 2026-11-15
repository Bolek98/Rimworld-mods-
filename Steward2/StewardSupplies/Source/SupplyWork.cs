using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.4.0: a stock target below its level used to only mark trees, ore and animals – the work priorities
// did not follow, so the marks waited (growing comes before cutting at the same level, a crisis took
// everybody, Steward 1.2.3 put helpers far down). Now a supply that is missing and has something marked
// gets the best suited colonist on its work (main level); when much is missing (under a third of the
// target) two colonists in a bigger colony, and the work goes one level above his other main work that
// would come first in the game's order (growing before cutting …). Crises and your rules are stronger
// (category "addons" in the plan order) and keep their people.
public sealed class SupplyWorkStep : IPlanStep
{
	public int Order => 350;
	public string Id => "supplies.work";

	public void Apply(PlanContext ctx)
	{
		if (SuppliesMod.S != null && !SuppliesMod.S.supplyWork)
			return;
		Map map = ctx?.map;
		SupplyManager sm = map?.GetComponent<SupplyManager>();
		if (sm == null || !sm.enabled || ctx.pawns.Count == 0)
			return;
		var jobs = new List<SupplyJob> { sm.forestry, sm.foraging, sm.hunting };
		if (sm.mining != null)
			jobs.AddRange(sm.mining);
		// one entry per work type – the job that misses most decides
		var want = new Dictionary<WorkTypeDef, (SupplyJob job, float miss)>();
		foreach (SupplyJob job in jobs)
		{
			if (job == null || !job.enabled || job.target <= 0 || job.lastStock >= job.target)
				continue;
			WorkTypeDef w = WorkOf(job);
			if (w == null || !ctx.types.Contains(w) || !Marked(map, job))
				continue;
			float miss = 1f - job.lastStock / (float)job.target;
			if (!want.TryGetValue(w, out var cur) || miss > cur.miss)
				want[w] = (job, miss);
		}
		foreach (var kv in want)
			Staff(ctx, kv.Key, kv.Value.job, kv.Value.miss);
	}

	private static WorkTypeDef WorkOf(SupplyJob job)
	{
		switch (job.kind)
		{
			case JobKind.Hunting: return DefDatabase<WorkTypeDef>.GetNamedSilentFail("Hunting");
			case JobKind.Mining: return DefDatabase<WorkTypeDef>.GetNamedSilentFail("Mining");
			default: return DefDatabase<WorkTypeDef>.GetNamedSilentFail("PlantCutting");   // cutting and harvesting wild plants
		}
	}

	private static bool Marked(Map map, SupplyJob job)
	{
		DesignationManager dm = map.designationManager;
		switch (job.kind)
		{
			case JobKind.Hunting: return dm.AnySpawnedDesignationOfDef(DesignationDefOf.Hunt);
			case JobKind.Mining: return dm.AnySpawnedDesignationOfDef(DesignationDefOf.Mine);
			case JobKind.Foraging: return dm.AnySpawnedDesignationOfDef(DesignationDefOf.HarvestPlant);
			default: return dm.AnySpawnedDesignationOfDef(DesignationDefOf.CutPlant);
		}
	}

	private static void Staff(PlanContext ctx, WorkTypeDef w, SupplyJob job, float miss)
	{
		bool much = miss >= 0.67f;
		int people = much && ctx.pawns.Count >= 4 ? 2 : 1;
		string label = SupplyManager.JobLabel(job);
		string amount = job.lastStock + " / " + job.target;
		int done = 0;
		foreach (Pawn p in ctx.Best(w))
		{
			if (done >= people)
				break;
			if (job.kind == JobKind.Hunting && (!WorkGiver_HunterHunt.HasHuntingWeapon(p) || WorkGiver_HunterHunt.HasShieldAndRangedWeapon(p)))
				continue;
			if (ctx.Fit(p, w) < 0.1f && WorkCatalog.Kind(w) == WorkKind.Skilled)
				continue;
			// somebody a crisis or your rule holds on urgent work is left to it
			if (BusyElsewhere(ctx, p, w))
				continue;
			int cur = ctx.Get(p, w);
			int lv = cur == 1 ? 1 : 2;
			if (!ctx.Set(p, w, lv, "CF2U_TipSupply", label, amount, pin: true) && cur != lv)
				continue;
			done++;
			if (much && lv == 2 && ctx.Levels >= 3)
				Yield(ctx, p, w, label);
		}
	}

	// held on level 1 by something stronger than the base plan (crisis, situation, your rule …)
	private static bool BusyElsewhere(PlanContext ctx, Pawn p, WorkTypeDef w)
	{
		if (!ctx.plan.TryGetValue(p, out var jobs))
			return true;
		foreach (var j in jobs)
			if (j.Key != w && j.Value == 1 && WorkCatalog.Kind(j.Key) != WorkKind.Emergency && ctx.Pinned(p, j.Key))
				return true;
		return false;
	}

	// other main work that the game would do first (it stands left of this work in the Work tab) moves one level down
	private static void Yield(PlanContext ctx, Pawn p, WorkTypeDef w, string label)
	{
		if (!ctx.plan.TryGetValue(p, out var jobs))
			return;
		foreach (WorkTypeDef o in jobs.Keys.ToList())
		{
			if (o == w || jobs[o] != 2 || o.naturalPriority <= w.naturalPriority)
				continue;
			if (ctx.Locked(p, o) || ctx.Pinned(p, o))
				continue;
			string owner = ctx.OwnerOf(p, o);
			if (owner != null && owner != "base")
				continue;
			ctx.Set(p, o, 3, "CF2U_TipSupplyYield", label, null, pin: true);
		}
	}
}
