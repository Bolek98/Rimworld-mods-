using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.2.3: with more than 4 levels (Steward – 9 Priorities, Work Tab …) every colonist gets a real order of his
// work, not just 1, 2 and the last level. For each colonist and work: how much the colony needs it now
// (averaged over the last hours) and how good he is at it compared with the others (rank, skill, passion).
// With 9 levels:
//   1     emergency (as before)
//   2–3   main work – the most needed work the colonist is one of the best at
//   3–4   helps with it
//   5–8   backups, ordered for this colonist: what he is good at and what is needed comes first
//   9     only what he is bad at and nobody needs now
// Other level counts are scaled. Crises, roles, your rules and locked cells keep their levels.
// Calm: the need is smoothed, one-step moves between backups are not made and the hold applies (Planner.Final).
public static class Spread
{
	public static bool Active => (ForemanMod.Settings?.spreadLevels ?? true) && PriorityRange.Max >= 5;

	// 9-level plan → this game's levels
	public static int Scale(int lv9, int L)
	{
		if (L == 9)
			return lv9;
		return Mathf.Clamp(1 + Mathf.RoundToInt((lv9 - 1) * (L - 1) / 8f), 1, L);
	}

	// the levels that count as "main work" (somebody really works on it)
	public static int MainMax => Active ? Math.Max(2, Scale(3, PriorityRange.Max)) : 2;

	public static bool IsMain(int v) => v > 0 && v <= MainMax;

	private const float HalfLifeHours = 6f;

	// cells only the base plan set – these get spread; anything else (crisis, role, your rule …) stays
	private static readonly HashSet<string> BaseKeys = new HashSet<string>
	{
		"CF2_TipMain", "CF2_TipHelper", "CF2_TipOnCall", "CF2_TipBackup", "CF2_TipStyleReady"
	};

	// smoothed waiting work per work type (runtime, per map)
	private static float Smoothed(ForemanMapComponent comp, WorkTypeDef w, int now)
	{
		float cur = comp.demand?.Jobs(w) ?? 0;
		if (!comp.urgency.TryGetValue(w, out var u))
		{
			comp.urgency[w] = (cur, now);
			return cur;
		}
		float hours = Mathf.Max(0f, (now - u.tick) / 2500f);
		float a = 1f - Mathf.Pow(0.5f, hours / HalfLifeHours);
		// more work arriving counts at once a bit more than work going away (it is felt sooner)
		if (cur > u.val)
			a = Mathf.Max(a, 0.35f);
		float v = u.val + (cur - u.val) * a;
		comp.urgency[w] = (v, now);
		return v;
	}

	private static string NeedWord(float n) =>
		(n >= 0.6f ? "CF2_NeedHigh" : n >= 0.25f ? "CF2_NeedMid" : n > 0.02f ? "CF2_NeedLow" : "CF2_NeedNone").Translate();

	public static void Apply(PlanContext ctx)
	{
		if (!Active || ctx?.comp == null)
			return;
		int L = ctx.Levels;
		int now = Find.TickManager.TicksGame;

		// 1) how much each work is needed now, 0 … 1
		var raw = new Dictionary<WorkTypeDef, float>();
		foreach (WorkTypeDef w in ctx.types)
		{
			WorkKind kind = WorkCatalog.Kind(w);
			if (kind == WorkKind.Hidden || kind == WorkKind.Emergency)
				continue;
			float jobs = Smoothed(ctx.comp, w, now);
			float imp = ctx.settings?.Importance(w) ?? 0.5f;
			if (Planner.AlwaysSomeone(w))
				imp += 0.3f;
			if (kind == WorkKind.General)
				imp -= 0.15f;
			raw[w] = jobs < 0.3f ? (Planner.AlwaysSomeone(w) ? 0.15f : 0f) : Mathf.Log(1f + jobs, 2f) * (0.6f + imp) + 0.5f;
		}
		float maxRaw = raw.Count == 0 ? 0f : raw.Values.Max();
		var need = raw.ToDictionary(kv => kv.Key, kv => maxRaw <= 0f ? 0f : Mathf.Clamp01(kv.Value / maxRaw));

		// 2) how good each colonist is at each work compared with the others
		var skill = new Dictionary<long, float>();
		var rankOf = new Dictionary<long, (int r, int n)>();
		foreach (WorkTypeDef w in need.Keys)
		{
			bool general = WorkCatalog.Kind(w) == WorkKind.General;
			List<Pawn> able = ctx.pawns.Where(p => ctx.CanDo(p, w))
				.OrderByDescending(p => ctx.Fit(p, w) + (Spread.IsMain(p.workSettings?.GetPriority(w) ?? 0) ? 0.05f : 0f))
				.ThenBy(p => p.thingIDNumber).ToList();
			int n = able.Count;
			for (int i = 0; i < n; i++)
			{
				Pawn p = able[i];
				long k = PlanContext.Key(p, w);
				float rel = n <= 1 ? 1f : 1f - i / (float)(n - 1);
				float abs = Mathf.Clamp01(ctx.Fit(p, w) / 1.0f);
				skill[k] = general ? 0.5f : rel * 0.6f + abs * 0.4f;
				rankOf[k] = (i + 1, n);
			}
		}

		// 3) each colonist: his own order of the work
		foreach (Pawn p in ctx.pawns)
		{
			if (!ctx.plan.TryGetValue(p, out var jobs))
				continue;
			var backups = new List<(WorkTypeDef w, float s)>();
			foreach (WorkTypeDef w in jobs.Keys.ToList())
			{
				int v = jobs[w];
				if (v < 2 || !need.ContainsKey(w) || ctx.Locked(p, w) || ctx.Pinned(p, w))
					continue;
				long k = PlanContext.Key(p, w);
				string owner = ctx.OwnerOf(p, w);
				if (owner != null && owner != "base")
					continue;
				if (ctx.reasons.TryGetValue(k, out var rs) && rs.Any(r => !BaseKeys.Contains(r.key)))
					continue;
				float nd = need[w];
				float sk = skill.TryGetValue(k, out float x) ? x : 0.3f;
				int lv9;
				if (v == 2)
					lv9 = nd >= 0.35f ? 2 : 3;
				else if (v == 3 || v < L)
					lv9 = nd >= 0.6f ? 3 : 4;
				else
				{
					backups.Add((w, nd * 0.5f + sk * 0.5f));
					continue;
				}
				Put(ctx, p, w, jobs, Scale(lv9, L), k, nd, rankOf);
			}
			// backups: ordered for this colonist, spread over 5 … 8; what he is bad at and nobody needs → 9
			backups = backups.OrderByDescending(b => b.s).ThenBy(b => b.w.index).ToList();
			int m = backups.Count;
			for (int i = 0; i < m; i++)
			{
				WorkTypeDef w = backups[i].w;
				long k = PlanContext.Key(p, w);
				float nd = need[w];
				float sk = skill.TryGetValue(k, out float x) ? x : 0.3f;
				int lv9;
				if (nd <= 0.02f && sk < 0.35f && WorkCatalog.Kind(w) == WorkKind.Skilled)
					lv9 = 9;
				else
				{
					lv9 = 5 + Mathf.Min(3, i * 4 / Math.Max(1, m));
					// very needed work never sinks to the bottom, unneeded work never stays on top
					if (nd >= 0.6f) lv9 = Math.Min(lv9, 6);
					if (nd <= 0.02f) lv9 = Math.Max(lv9, 7);
				}
				Put(ctx, p, w, jobs, Scale(lv9, L), k, nd, rankOf);
			}
		}
	}

	private static void Put(PlanContext ctx, Pawn p, WorkTypeDef w, Dictionary<WorkTypeDef, int> jobs, int nv, long k, float nd,
		Dictionary<long, (int r, int n)> rankOf)
	{
		nv = Mathf.Clamp(nv, 2, ctx.Levels);
		jobs[w] = nv;
		var rk = rankOf.TryGetValue(k, out var t) ? t : (0, 0);
		ctx.AddReason(p, w, "CF2_TipSpread", rk.Item1 > 0 ? rk.Item1 + "/" + rk.Item2 : "–", NeedWord(nd));
	}
}
