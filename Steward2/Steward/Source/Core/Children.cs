using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// Children need time to learn: a child keeps its work only on the lowest level,
// and while its learning need is low it gets no work at all (until it is filled again).
public static class ChildLearning
{
	private static readonly HashSet<int> learningNow = new HashSet<int>();

	public static void ResetGame() => learningNow.Clear();

	public static float Learning(Pawn p) => p?.needs?.learning?.CurLevelPercentage ?? 1f;

	public static bool IsChild(Pawn p) => p?.DevelopmentalStage == DevelopmentalStage.Child;

	// children whose learning is low (for add-ons: someone gives lessons)
	public static int NeedLessons(IEnumerable<Pawn> pawns) => pawns.Count(p => IsChild(p) && p.needs?.learning != null && Learning(p) < 0.5f);

	public static void Apply(PlanContext ctx)
	{
		if (!ModsConfig.BiotechActive)
			return;
		foreach (Pawn p in ctx.pawns)
		{
			if (!IsChild(p) || p.needs?.learning == null)
				continue;
			float l = Learning(p);
			if (l < 0.35f)
				learningNow.Add(p.thingIDNumber);
			else if (l > 0.85f)
				learningNow.Remove(p.thingIDNumber);
			bool learn = learningNow.Contains(p.thingIDNumber);
			int pct = Mathf.RoundToInt(l * 100f);
			foreach (WorkTypeDef w in ctx.plan[p].Keys.ToList())
			{
				int v = ctx.Get(p, w);
				if (v <= 0 || WorkCatalog.Kind(w) == WorkKind.Emergency)
					continue;
				ctx.Set(p, w, learn ? 0 : ctx.Levels, learn ? "CF_TipChildLearn" : "CF_TipChildLight", pct, null, pin: true);
			}
		}
	}
}

// Mothers (or fathers) look after their babies; nannies help when there is no parent or too many babies.
public static class ParentCare
{
	private static readonly Dictionary<int, int> nannyFor = new Dictionary<int, int>();   // baby id -> nanny id

	public static void ResetGame() => nannyFor.Clear();

	private static bool CanCare(Pawn p, WorkTypeDef cc, PlanContext ctx)
		=> p != null && ctx.plan.ContainsKey(p) && !p.Dead && !p.Downed && p.Spawned && p.DevelopmentalStage == DevelopmentalStage.Adult && ctx.CanDo(p, cc);

	public static void Apply(PlanContext ctx)
	{
		if (!ModsConfig.BiotechActive)
			return;
		WorkTypeDef cc = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Childcare");
		if (cc == null)
			return;
		var babies = ctx.map.mapPawns.FreeColonistsSpawned.Where(p => p.DevelopmentalStage.Baby()).ToList();
		var ids = new HashSet<int>(babies.Select(b => b.thingIDNumber));
		foreach (int id in nannyFor.Keys.ToList())
			if (!ids.Contains(id))
				nannyFor.Remove(id);
		if (babies.Count == 0)
			return;
		var parents = new Dictionary<Pawn, int>();
		var needNanny = new List<Pawn>();
		foreach (Pawn b in babies)
		{
			Pawn m = b.GetMother();
			if (!(CanCare(m, cc, ctx) && m.Map == b.Map))
				m = b.GetFather();
			if (CanCare(m, cc, ctx) && m.Map == b.Map)
			{
				parents.TryGetValue(m, out int n);
				parents[m] = n + 1;
				if (n >= 2)
					needNanny.Add(b);
			}
			else
				needNanny.Add(b);
		}
		foreach (var kv in parents)
			Give(ctx, kv.Key, cc, true);
		int wanted = (needNanny.Count + 1) / 2;
		var nannies = new List<Pawn>();
		foreach (Pawn b in needNanny)
			if (nannyFor.TryGetValue(b.thingIDNumber, out int nid))
			{
				Pawn n = ctx.pawns.FirstOrDefault(p => p.thingIDNumber == nid);
				if (CanCare(n, cc, ctx) && !parents.ContainsKey(n) && !nannies.Contains(n))
					nannies.Add(n);
			}
		if (nannies.Count < wanted)
			nannies.AddRange(ctx.pawns.Where(p => CanCare(p, cc, ctx) && !parents.ContainsKey(p) && !nannies.Contains(p) && !ctx.Locked(p, cc))
				.OrderByDescending(p => ctx.Fit(p, cc)).Take(wanted - nannies.Count));
		while (nannies.Count > wanted)
			nannies.RemoveAt(nannies.Count - 1);
		for (int i = 0; i < needNanny.Count && nannies.Count > 0; i++)
			nannyFor[needNanny[i].thingIDNumber] = nannies[Math.Min(i / 2, nannies.Count - 1)].thingIDNumber;
		foreach (Pawn n in nannies)
			Give(ctx, n, cc, false);
	}

	private static void Give(PlanContext ctx, Pawn p, WorkTypeDef cc, bool parent)
	{
		if (!ctx.Set(p, cc, 1, parent ? "CF_TipMother" : "CF_TipNanny", null, null, pin: true))
			return;
		// the babies come first: other top work one step down
		foreach (WorkTypeDef w in ctx.plan[p].Keys.ToList())
			if (w != cc && ctx.Get(p, w) == 1 && WorkCatalog.Kind(w) != WorkKind.Emergency && !ctx.Pinned(p, w))
				ctx.Set(p, w, 2);
	}
}
