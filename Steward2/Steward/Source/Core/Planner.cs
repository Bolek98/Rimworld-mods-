using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// Steward 2.0 planner.
//  1. Everyone's suitability for every work (Fitness) and how much work waits (Demand).
//  2. Plan steps in order: the core's base plan, children, add-ons (crises, situations …),
//     your rules, one top job, nobody idle.
//  3. The plan is written calmly: a cell the steward changed stays at least half a day,
//     no small back-and-forth – unless something urgent (pinned) or you press "Plan now".
public static class Planner
{
	private static bool registered;

	public static void RegisterCoreSteps()
	{
		if (registered)
			return;
		registered = true;
		StewardApi.Register(new BaseStep());
		StewardApi.Register(new ChildrenStep());
		StewardApi.Register(new MyRulesStep());
		StewardApi.Register(new TopJobStep());
		StewardApi.Register(new NobodyIdleStep());
	}

	public static bool IsStyleWork(ForemanStyle style, WorkTypeDef w) => Styles.Delta(style, w) > 0f;

	// work a colony always needs somebody for, whatever the style (patients, prisoners, babies …)
	public static bool AlwaysSomeone(WorkTypeDef w) => w.defName == "Doctor" || w.defName == "Warden" || w.defName == "Childcare" || w.defName == "Cooking";

	public static bool StrictStyle(ForemanStyle style, ForemanSettings s)
		=> s != null && s.styleStrict && style != ForemanStyle.Balanced && style != ForemanStyle.Custom;

	public static PlanContext Build(ForemanMapComponent comp, bool manual)
	{
		ForemanSettings s = ForemanMod.Settings;
		Map map = comp.map;
		var ctx = new PlanContext
		{
			map = map,
			comp = comp,
			game = ForemanGameComponent.Get(),
			settings = s,
			pawns = WorkCatalog.Workers(map, s).Where(p => !ModCompat.AchtungForced(p)).ToList(),
			types = WorkCatalog.Visible,
			Levels = Math.Max(3, PriorityRange.Max),
			style = comp.style,
			manual = manual,
			demand = comp.demand
		};
		foreach (Pawn p in ctx.pawns)
		{
			var jobs = new Dictionary<WorkTypeDef, int>();
			var fits = new Dictionary<WorkTypeDef, float>();
			foreach (WorkTypeDef w in ctx.types)
			{
				// locked cells keep what they have; 3.0.5: so does work the pawn just cannot do for
				// now (anaesthetized, no sight …) – it is not wiped and comes back when he recovers
				jobs[w] = ctx.Locked(p, w) || WorkCatalog.UnableForNow(p, w) ? p.workSettings.GetPriority(w) : 0;
				if (WorkCatalog.CanDo(p, w, s))
					fits[w] = Fitness.Of(p, w, s, ctx.game);
			}
			ctx.plan[p] = jobs;
			ctx.fit[p] = fits;
		}
		return ctx;
	}

	public static int Run(ForemanMapComponent comp, bool manual)
	{
		PlanResult r = Compute(comp, manual);
		return r == null ? 0 : Write(r);
	}

	// 3.1.0: the plan without writing it (the preview window shows it first)
	public static PlanResult Compute(ForemanMapComponent comp, bool manual)
	{
		PlanContext ctx = Build(comp, manual);
		if (ctx.pawns.Count == 0)
			return null;
		foreach (IPlanStep step in StewardApi.Steps)
		{
			try
			{
				step.Apply(ctx);
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Steward] plan step '" + step.Id + "' failed: " + ex, step.Id.GetHashCode());
				// 3.0.5: without the base plan the rest is half empty – better keep the priorities as they are
				if (step.Id == "core.base")
					return null;
			}
		}
		return new PlanResult { ctx = ctx, final = Final(ctx) };
	}

	// ---- the plan calmly: what really changes ----------------------------------
	private static Dictionary<Pawn, Dictionary<WorkTypeDef, int>> Final(PlanContext ctx)
	{
		ForemanMapComponent comp = ctx.comp;
		int now = Find.TickManager.TicksGame;
		int hold = Math.Max(0, ctx.settings?.holdHours ?? 12) * 2500;
		// what the plan wants vs what stays because of the calm hold
		var final = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
		foreach (var kv in ctx.plan)
		{
			Pawn p = kv.Key;
			var f = new Dictionary<WorkTypeDef, int>();
			foreach (var j in kv.Value)
			{
				WorkTypeDef w = j.Key;
				int cur = p.workSettings.GetPriority(w);
				int want = j.Value;
				if (ctx.Locked(p, w) || p.WorkTypeIsDisabled(w))
				{
					f[w] = cur;
					continue;
				}
				if (want == cur || ctx.manual || ctx.Pinned(p, w))
				{
					f[w] = want;
					continue;
				}
				bool held = comp.calmCells.TryGetValue(CalmKey(p, w), out CalmCell cc) && now >= cc.tick && now - cc.tick < hold && !cc.crisis;
				// small steps between backup levels (3 ↔ 4 …) are not worth moving anyone;
				// main work (1–2) always follows the plan
				bool small = cur >= 3 && want >= 3 && Math.Abs(want - cur) == 1;
				f[w] = held || small ? cur : want;
			}
			final[p] = f;
		}
		// the hold must not leave a colonist with two top jobs: the plan's top job wins
		if (ctx.settings == null || ctx.settings.oneTopPriority)
			foreach (var kv in final)
			{
				Pawn p = kv.Key;
				var tops = kv.Value.Where(j => j.Value == 1 && WorkCatalog.Kind(j.Key) != WorkKind.Emergency && !ctx.Locked(p, j.Key)).Select(j => j.Key).ToList();
				if (tops.Count <= 1)
					continue;
				foreach (WorkTypeDef w in tops)
					if (ctx.Get(p, w) != 1)
						kv.Value[w] = 2;
			}
		// the hold must never leave waiting work without anybody on it
		foreach (WorkTypeDef w in ctx.types)
		{
			if (WorkCatalog.Kind(w) == WorkKind.Hidden || ctx.demand == null || ctx.demand.Jobs(w) <= 0)
				continue;
			bool covered = final.Any(kv => kv.Value.TryGetValue(w, out int v) && v > 0 && v <= 2);
			if (covered)
				continue;
			foreach (var kv in ctx.plan)
				if (kv.Value.TryGetValue(w, out int v) && v > 0 && v <= 2 && !ctx.Locked(kv.Key, w))
					final[kv.Key][w] = v;
		}
		return final;
	}

	// ---- writing the plan --------------------------------------------------------
	public static int Write(PlanResult r)
	{
		PlanContext ctx = r.ctx;
		ForemanMapComponent comp = ctx.comp;
		var final = r.final;
		int now = Find.TickManager.TicksGame;
		if (!Find.PlaySettings.useWorkPriorities)
		{
			Find.PlaySettings.useWorkPriorities = true;
			foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
				p.workSettings?.Notify_UseWorkPrioritiesChanged();
			Messages.Message("CF2_ManualOn".Translate(), MessageTypeDefOf.NeutralEvent, historical: false);
		}
		int changed = 0;
		var log = new List<string>();
		comp.tips.Clear();
		foreach (var kv in final)
		{
			Pawn p = kv.Key;
			foreach (var j in kv.Value)
			{
				WorkTypeDef w = j.Key;
				int cur = p.workSettings.GetPriority(w);
				if (j.Value != cur && !ctx.Locked(p, w) && !p.Dead && p.workSettings != null && !p.WorkTypeIsDisabled(w))
				{
					try
					{
						p.workSettings.SetPriority(w, j.Value);
						changed++;
						if (log.Count < 60)
						{
							string why = ctx.reasons.TryGetValue(PlanContext.Key(p, w), out var rr) && rr.Count > 0 ? rr[rr.Count - 1].key : "-";
							log.Add(p.LabelShort + ": " + w.defName + " " + cur + "→" + j.Value + " (" + why + ")");
						}
						comp.calmCells[CalmKey(p, w)] = new CalmCell { tick = now, crisis = ctx.Pinned(p, w) };
					}
					catch (Exception ex)
					{
						MLog.WarnOnce("[Steward] cannot set " + w.defName + " for " + p.LabelShort + ": " + ex.Message, w.index ^ 0x5e70000);
					}
				}
				long k = PlanContext.Key(p, w);
				if (ctx.reasons.TryGetValue(k, out var rs))
					comp.tips[k] = rs;
			}
		}
		comp.PruneCalm(now);
		comp.lastPlanTick = now;
		comp.lastPlanChanged = changed;
		MLog.Info("[Steward] plan for " + comp.map.Parent?.LabelCap + ": " + ctx.pawns.Count + " pawns, " + changed + " changes"
			+ (ctx.manual ? " (by you)" : " – " + comp.lastWhy) + ", crisis: " + (comp.crisis == Crisis.None ? "none" : comp.crisis.ToString())
			+ (log.Count > 0 ? "\n    " + string.Join("\n    ", log) : "") + (changed > log.Count ? "\n    … and " + (changed - log.Count) + " more" : ""));
		if (ctx.settings != null && ctx.settings.planMessages && changed > 0 && comp.map == Find.CurrentMap)
			Messages.Message("CF2_PlanDone".Translate(changed), MessageTypeDefOf.SilentInput, historical: false);
		return changed;
	}

	// one change of the plan (for the preview window)
	public struct Change
	{
		public Pawn pawn;
		public WorkTypeDef work;
		public int from, to;
		public string why;
	}

	public sealed class PlanResult
	{
		public PlanContext ctx;
		public Dictionary<Pawn, Dictionary<WorkTypeDef, int>> final;

		public List<Change> Changes()
		{
			var list = new List<Change>();
			foreach (var kv in final)
			{
				Pawn p = kv.Key;
				if (p?.workSettings == null || p.Dead)
					continue;
				foreach (var j in kv.Value)
				{
					int cur = p.workSettings.GetPriority(j.Key);
					if (j.Value == cur || ctx.Locked(p, j.Key) || p.WorkTypeIsDisabled(j.Key))
						continue;
					string why = ctx.reasons.TryGetValue(PlanContext.Key(p, j.Key), out var rs) && rs.Count > 0 ? rs[rs.Count - 1].Text() : null;
					list.Add(new Change { pawn = p, work = j.Key, from = cur, to = j.Value, why = why });
				}
			}
			return list;
		}
	}

	public static string CalmKey(Pawn p, WorkTypeDef w) => p.thingIDNumber + "|" + w.defName;

	// ==== core steps =========================================================

	// 100: the base plan from suitability, waiting work and the colony style
	private sealed class BaseStep : IPlanStep
	{
		public int Order => 100;
		public string Id => "core.base";

		public void Apply(PlanContext ctx)
		{
			ForemanSettings s = ctx.settings;
			int n = ctx.pawns.Count;
			int L = ctx.Levels;
			bool strict = StrictStyle(ctx.style, s);
			bool othersOff = strict && s.styleOthersOff;
			foreach (WorkTypeDef w in ctx.types)
			{
				WorkKind kind = WorkCatalog.Kind(w);
				if (kind == WorkKind.Hidden)
					continue;
				List<Pawn> best = ctx.Best(w);
				if (best.Count == 0)
					continue;
				if (kind == WorkKind.Emergency)
				{
					foreach (Pawn p in best)
						ctx.Set(p, w, 1, "CF2_TipEmergency");
					continue;
				}
				float d = Styles.Delta(ctx.style, w) * Mathf.Max(0f, s.styleStrength);
				int jobs = ctx.demand?.Jobs(w) ?? 0;
				int need = ctx.demand?.Need(ctx.map, w, n) ?? 0;
				if (need > 0)
					need = Mathf.CeilToInt(need * (1f + d));
				else if (d > 0f)
					need = 1;   // the style's own work: somebody is ready for it
				if (strict && d <= 0f && kind == WorkKind.Skilled)
					need = Math.Min(need, 1);
				if (kind == WorkKind.General)
					need = Math.Min(need, Mathf.CeilToInt(n * 0.5f));
				need = Mathf.Clamp(need, 0, best.Count);
				int helpers = need > 0 ? Mathf.CeilToInt(need / 2f) : 0;
				for (int i = 0; i < best.Count; i++)
				{
					Pawn p = best[i];
					float fit = ctx.Fit(p, w);
					if (i < need)
						ctx.Set(p, w, 2, jobs > 0 ? "CF2_TipMain" : "CF2_TipStyleReady", jobs > 0 ? (object)jobs : Styles.Label(ctx.style));
					else if (i < need + helpers && (kind == WorkKind.General || fit >= 0.25f))
						ctx.Set(p, w, Math.Min(3, L), "CF2_TipHelper");
					else if (need == 0 && i == 0)
						ctx.Set(p, w, Math.Min(3, L), "CF2_TipOnCall");
					else if (othersOff && d <= 0f && kind == WorkKind.Skilled && !Planner.AlwaysSomeone(w))
						ctx.Set(p, w, 0, "CF2_TipStyleOff", Styles.Label(ctx.style));
					else if (kind == WorkKind.General || s.backupWorkers)
						ctx.Set(p, w, L, "CF2_TipBackup");
					else
						ctx.Set(p, w, 0);
				}
			}
		}
	}

	// 200: children learn, mothers and nannies care for babies (Biotech)
	private sealed class ChildrenStep : IPlanStep
	{
		public int Order => 200;
		public string Id => "core.children";

		public void Apply(PlanContext ctx)
		{
			if (ctx.settings.childLearning)
				ChildLearning.Apply(ctx);
			if (ctx.settings.parentCare)
				ParentCare.Apply(ctx);
		}
	}

	// 800: your minimum / maximum colonists per work type
	private sealed class MyRulesStep : IPlanStep
	{
		public int Order => 800;
		public string Id => "core.myrules";

		public void Apply(PlanContext ctx)
		{
			ForemanSettings s = ctx.settings;
			if (!s.useMyRules)
				return;
			foreach (WorkTypeDef w in ctx.types)
			{
				int min = s.MinFor(w), max = s.MaxFor(w);
				if (min > 0)
				{
					int have = ctx.CountOn(w, 2);
					foreach (Pawn p in ctx.Best(w))
					{
						if (have >= min)
							break;
						int v = ctx.Get(p, w);
						if ((v > 0 && v <= 2) || ctx.noRaise.Contains(p))
							continue;
						if (ctx.Set(p, w, 2, "CF2_TipYourMin", min))
							have++;
					}
				}
				if (max > 0)
				{
					var on = ctx.Best(w).Where(p => { int v = ctx.Get(p, w); return v > 0 && v <= 2; }).ToList();
					for (int i = on.Count - 1; i >= 0 && on.Count > max; i--)
					{
						Pawn p = on[i];
						if (ctx.Pinned(p, w) || ctx.Locked(p, w))
							continue;
						if (ctx.Set(p, w, s.backupWorkers ? ctx.Levels : 0, "CF2_TipYourMax", max))
							on.RemoveAt(i);
					}
				}
			}
		}
	}

	// 900: one work on priority 1 per colonist – the one that suits him and matters most now
	private sealed class TopJobStep : IPlanStep
	{
		public int Order => 900;
		public string Id => "core.topjob";

		public void Apply(PlanContext ctx)
		{
			int n = Math.Max(1, ctx.pawns.Count);
			foreach (Pawn p in ctx.pawns)
			{
				var jobs = ctx.plan[p];
				bool pinnedTop = jobs.Any(j => j.Value == 1 && ctx.Pinned(p, j.Key) && WorkCatalog.Kind(j.Key) != WorkKind.Emergency);
				WorkTypeDef best = null;
				float bestScore = float.MinValue;
				foreach (var j in jobs)
				{
					WorkTypeDef w = j.Key;
					if (WorkCatalog.Kind(w) == WorkKind.Emergency || j.Value <= 0 || j.Value > 2)
						continue;
					// 3.0.5: pinned or locked cells cannot be made the top job (a pinned 2 chosen here
					// left the colonist without any top job at all)
					if (ctx.Pinned(p, w) || ctx.Locked(p, w))
						continue;
					float pressure = ctx.demand == null ? 0f : Mathf.Min(1f, ctx.demand.Need(ctx.map, w, n) / (float)n);
					float score = ctx.Fit(p, w) + 0.4f * Styles.Delta(ctx.style, w) + 0.3f * ctx.settings.Importance(w) + 0.3f * pressure;
					// the current top job stays unless another one is clearly better (no flip-flop)
					if (p.workSettings.GetPriority(w) == 1)
						score += 0.15f;
					if (WorkCatalog.Kind(w) == WorkKind.General)
						score -= 0.25f;   // hauling / cleaning are rarely anyone's main work
					if (score > bestScore)
					{
						bestScore = score;
						best = w;
					}
				}
				if (!ctx.settings.oneTopPriority)
				{
					// several: every main work that suits him well
					foreach (var w in jobs.Keys.ToList())
						if (jobs[w] == 2 && WorkCatalog.Kind(w) == WorkKind.Skilled && ctx.Fit(p, w) >= 0.6f)
							ctx.Set(p, w, 1, "CF2_TipTop");
					continue;
				}
				// only one: other (not urgent) 1s go to 2
				foreach (var w in jobs.Keys.ToList())
					if (jobs[w] == 1 && WorkCatalog.Kind(w) != WorkKind.Emergency && !ctx.Pinned(p, w) && w != best)
						ctx.Set(p, w, 2);
				if (best != null && !pinnedTop)
					ctx.Set(p, best, 1, "CF2_TipTop");
			}
		}
	}

	// 950: nobody without real work – the pawn gets his best-suited work that waits
	private sealed class NobodyIdleStep : IPlanStep
	{
		public int Order => 950;
		public string Id => "core.idle";

		public void Apply(PlanContext ctx)
		{
			foreach (Pawn p in ctx.pawns)
			{
				var jobs = ctx.plan[p];
				if (jobs.Any(j => j.Value > 0 && j.Value <= 2 && WorkCatalog.Kind(j.Key) != WorkKind.Emergency))
					continue;
				if (ctx.noRaise.Contains(p))
					continue;
				foreach (WorkTypeDef pick in jobs.Keys
					.Where(w => WorkCatalog.Kind(w) != WorkKind.Emergency && ctx.CanDo(p, w) && !ctx.Locked(p, w) && (ctx.demand?.Jobs(w) ?? 0) > 0)
					.OrderByDescending(w => ctx.Fit(p, w)).ToList())
					if (ctx.Set(p, pick, 2, "CF2_TipNobodyIdle"))
						break;
			}
		}
	}
}
