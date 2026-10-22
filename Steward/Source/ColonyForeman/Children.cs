using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 2.37: children need time to learn. A child keeps its work only on the lowest level,
// and while its learning need is low it gets no work at all (until it is well filled again).
public static class ChildLearning
{
	private static readonly HashSet<int> learningNow = new HashSet<int>();

	public static float Learning(Pawn p) => p?.needs?.learning?.CurLevelPercentage ?? 1f;

	public static bool IsChild(Pawn p) => p?.DevelopmentalStage == DevelopmentalStage.Child;

	public static void Apply(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, ForemanGameComponent game, ForemanMapComponent comp, int max)
	{
		if (!ModsConfig.BiotechActive)
			return;
		foreach (var kv in plan)
		{
			Pawn p = kv.Key;
			if (!IsChild(p) || p.needs?.learning == null)
				continue;
			float l = Learning(p);
			if (l < 0.35f)
				learningNow.Add(p.thingIDNumber);
			else if (l > 0.85f)
				learningNow.Remove(p.thingIDNumber);
			bool learn = learningNow.Contains(p.thingIDNumber);
			foreach (WorkTypeDef w in kv.Value.Keys.ToList())
			{
				int v = kv.Value[w];
				if (v <= 0 || Planner.TopExempt(w) || (game != null && game.AnyLock(p, w)))
					continue;
				int nv = learn ? 0 : max;
				if (nv == v)
					continue;
				kv.Value[w] = nv;
				if (comp != null && comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
					texts[w] = t + "\n" + (learn ? "CF_TipChildLearn" : "CF_TipChildLight").Translate(Mathf.RoundToInt(l * 100f)).Colorize(ColorLibrary.Gold);
			}
		}
	}

	// children whose learning is low (for the childcare situation: someone gives lessons)
	public static int NeedLessons(IEnumerable<Pawn> pawns) =>
		pawns.Count(p => IsChild(p) && p.needs?.learning != null && Learning(p) < 0.5f);
}

// 2.48: mothers look after their babies, and nannies help when there are no mothers or too many babies.
// A mother (on this map, able to do childcare) gets childcare on the top priority until her baby is a
// child; her other top-priority work moves one step down, so the baby always comes first. Babies
// without a mother who can care for them – and every third baby of one mother – get a nanny: the
// colonist best at it (social skill, passion, few top jobs), kept as long as needed.
public static class ParentCare
{
	private static readonly Dictionary<int, int> nannyFor = new Dictionary<int, int>();   // baby id -> nanny id

	public static void ResetGame() => nannyFor.Clear();

	private static bool IsBaby(Pawn p) => p != null && p.DevelopmentalStage.Baby();

	private static bool CanCare(Pawn p, WorkTypeDef cc, Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan)
		=> p != null && plan.ContainsKey(p) && !p.Dead && !p.Downed && p.Spawned && p.DevelopmentalStage == DevelopmentalStage.Adult && !p.WorkTypeIsDisabled(cc);

	public static void Apply(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, ForemanGameComponent game, ForemanMapComponent comp)
	{
		if (!ModsConfig.BiotechActive)
			return;
		WorkTypeDef cc = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Childcare");
		if (cc == null)
			return;
		var babies = new List<Pawn>();
		Map map = plan.Keys.FirstOrDefault()?.Map;
		if (map == null)
			return;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			if (IsBaby(p))
				babies.Add(p);
		// forget nannies of babies that grew up or are gone
		foreach (int id in nannyFor.Keys.ToList())
			if (!babies.Any(b => b.thingIDNumber == id))
				nannyFor.Remove(id);
		if (babies.Count == 0)
			return;
		var mothers = new Dictionary<Pawn, int>();
		var needNanny = new List<Pawn>();
		foreach (Pawn b in babies)
		{
			// 2.49.3: the mother first; when she can't (away, no childcare, gone), the father
			Pawn m = b.GetMother();
			if (!(CanCare(m, cc, plan) && m.Map == b.Map))
				m = b.GetFather();
			if (CanCare(m, cc, plan) && m.Map == b.Map)
			{
				mothers.TryGetValue(m, out int n);
				mothers[m] = n + 1;
				if (n >= 2)
					needNanny.Add(b);   // three or more babies: help
			}
			else
				needNanny.Add(b);
		}
		foreach (var kv in mothers)
			Give(plan, game, comp, kv.Key, cc, true);
		// one nanny per two babies that need one
		int wanted = (needNanny.Count + 1) / 2;
		// 2.50.3: with two or more babies there is always at least one nanny helping the parents
		if (babies.Count >= 2 && wanted < 1)
			wanted = 1;
		var nannies = new List<Pawn>();
		foreach (Pawn b in needNanny)
			if (nannyFor.TryGetValue(b.thingIDNumber, out int nid))
			{
				Pawn n = plan.Keys.FirstOrDefault(p => p.thingIDNumber == nid);
				if (CanCare(n, cc, plan) && !mothers.ContainsKey(n) && !nannies.Contains(n))
					nannies.Add(n);
			}
		if (nannies.Count < wanted)
		{
			var pick = plan.Keys.Where(p => CanCare(p, cc, plan) && !mothers.ContainsKey(p) && !nannies.Contains(p) && !(game != null && game.AnyLock(p, cc)))
				.OrderByDescending(p => (p.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0) + (int)(p.skills?.GetSkill(SkillDefOf.Social)?.passion ?? Passion.None) * 4)
				.ThenBy(p => plan[p].Count(j => j.Value == 1))
				.Take(wanted - nannies.Count);
			nannies.AddRange(pick);
		}
		while (nannies.Count > wanted)
			nannies.RemoveAt(nannies.Count - 1);
		for (int i = 0; i < needNanny.Count && nannies.Count > 0; i++)
			nannyFor[needNanny[i].thingIDNumber] = nannies[Math.Min(i / 2, nannies.Count - 1)].thingIDNumber;
		foreach (Pawn n in nannies)
			Give(plan, game, comp, n, cc, false);
	}

	private static void Give(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, ForemanGameComponent game, ForemanMapComponent comp, Pawn p, WorkTypeDef cc, bool mother)
	{
		if (game != null && game.AnyLock(p, cc))
			return;
		var jobs = plan[p];
		jobs[cc] = 1;
		// 2.50.3: a nanny puts the babies first too (before, only parents did – a nanny with
		// other priority-1 jobs hardly ever got to the babies)
		foreach (WorkTypeDef w in jobs.Keys.ToList())
			{
				if (w == cc || jobs[w] != 1 || Planner.TopExempt(w) || (game != null && game.AnyLock(p, w)))
					continue;
				jobs[w] = 2;
			}
		if (comp != null)
		{
			if (!comp.reasons.TryGetValue(p.thingIDNumber, out var texts))
				comp.reasons[p.thingIDNumber] = texts = new Dictionary<WorkTypeDef, string>();
			string tip = (mother ? "CF_TipMother" : "CF_TipNanny").Translate().Colorize(ColorLibrary.Gold);
			texts.TryGetValue(cc, out string t);
			texts[cc] = t.NullOrEmpty() ? tip : t + "\n" + tip;
		}
	}
}
