using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.2.2: with more than 4 levels (Steward – 9 Priorities, Work Tab …) the plan used only 1, 2, 3 and the
// last level. Now the main work (plan level 2) is spread over 2 … Max-3 by how much it is needed: the most
// needed work gets 2, less needed 3, 4 … Helpers sit one level below their main level, "on call" near the
// end, backups stay on the last level. How much is needed is an average over the last hours, so one busy
// moment does not reshuffle everything; one-step moves are not made (see Planner.Final).
public static class Spread
{
	// the levels that count as "main work" (somebody really works on it)
	public static int MainMax => Active ? PriorityRange.Max - 3 : 2;

	public static bool Active => (ForemanMod.Settings?.spreadLevels ?? true) && PriorityRange.Max >= 5;

	public static bool IsMain(int v) => v > 0 && v <= MainMax;

	private const float HalfLifeHours = 6f;

	private static bool CoreReason(string k) => k != null && k.StartsWith("CF2_Tip", StringComparison.Ordinal);

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

	public static void Apply(PlanContext ctx)
	{
		if (!Active || ctx?.comp == null)
			return;
		int L = ctx.Levels;
		int top = MainMax;              // 9 levels → main 2–6
		int bands = top - 1;            // number of main levels
		int now = Find.TickManager.TicksGame;
		// how much each work is needed now
		var score = new Dictionary<WorkTypeDef, float>();
		var waiting = new Dictionary<WorkTypeDef, int>();
		foreach (WorkTypeDef w in ctx.types)
		{
			WorkKind kind = WorkCatalog.Kind(w);
			if (kind == WorkKind.Hidden || kind == WorkKind.Emergency)
				continue;
			float jobs = Smoothed(ctx.comp, w, now);
			waiting[w] = Mathf.RoundToInt(jobs);
			float imp = ctx.settings?.Importance(w) ?? 0.5f;
			if (Planner.AlwaysSomeone(w))
				imp += 0.3f;
			if (kind == WorkKind.General)
				imp -= 0.15f;
			score[w] = Mathf.Log(1f + jobs, 2f) * (0.6f + imp) + (jobs > 0.5f ? 0.5f : 0f);
		}
		// rank the work that somebody has as main work
		List<WorkTypeDef> ranked = score.Keys
			.Where(w => ctx.pawns.Any(p => ctx.Get(p, w) == 2))
			.OrderByDescending(w => score[w]).ThenBy(w => w.naturalPriority * -1).ToList();
		var level = new Dictionary<WorkTypeDef, int>();
		int n = ranked.Count;
		for (int i = 0; i < n; i++)
			level[ranked[i]] = n <= 1 ? 2 : 2 + Mathf.Min(bands - 1, i * bands / n);
		// the most needed work keeps 2 even when many works are on the list
		foreach (Pawn p in ctx.pawns)
		{
			var jobs = ctx.plan[p];
			foreach (WorkTypeDef w in jobs.Keys.ToList())
			{
				int v = jobs[w];
				if (v < 2 || v >= L || !score.ContainsKey(w) || ctx.Locked(p, w) || ctx.Pinned(p, w))
					continue;
				// a crisis, role, situation or your rule set it: that level stays
				if (ctx.reasons.TryGetValue(PlanContext.Key(p, w), out var rs) && rs.Any(r => !CoreReason(r.key)))
					continue;
				int lv = level.TryGetValue(w, out int x) ? x : top;
				int rank = ranked.IndexOf(w) + 1;
				int nv;
				if (v == 2)
					nv = lv;
				else if (v == 3)
					nv = waiting[w] > 0 ? Mathf.Min(L - 1, lv + 1) : L - 1;   // helper / on call
				else
					nv = Mathf.Clamp(v + (top - 2), top + 1, L - 1);           // other in-between levels
				if (nv == v)
					continue;
				jobs[w] = nv;
				if (v == 2 && rank > 0)
					ctx.AddReason(p, w, "CF2_TipSpread", rank, waiting[w]);
			}
		}
	}
}
