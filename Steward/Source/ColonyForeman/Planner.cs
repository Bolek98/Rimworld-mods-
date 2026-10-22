using System.Linq;
using System;
using System.Collections.Generic;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public static class Planner
{
	private sealed class Candidate
	{
		public Pawn pawn;

		public float score;

		public float skill;

		public Passion passion;

		public float statFactor = 1f;

		public bool opposed;

		public bool preferred;

		public bool inspired;

		public bool incumbent;
	}

	// 2.20: what the situations want for one work type (summed up)
	private sealed class SitStaff
	{
		public int level = 99;
		public int workers;
		public readonly List<string> labels = new List<string>();
	}

	private sealed class Crisiseffect
	{
		public float urgency;

		public float workers = 1f;

		public int minWorkers;
	}

	// 2.25.0: everyone who works for the colony – colonists, slaves and (setting)
	// quest guests who can work. Used for the plan and to notice newcomers.
	public static List<Pawn> Roster(Map map)
	{
		var list = new List<Pawn>();
		bool lodgers = ForemanMod.Settings == null || ForemanMod.Settings.includeLodgers;
		void Consider(IEnumerable<Pawn> pawns)
		{
			if (pawns == null)
				return;
			foreach (Pawn item in pawns)
			{
				if (item == null || list.Contains(item))
					continue;
				if (item.workSettings != null && item.workSettings.EverWork && (lodgers || !item.IsQuestLodger()) && !item.Downed && !item.InMentalState)
					list.Add(item);
			}
		}
		Consider(map.mapPawns.FreeColonistsSpawned);
		Consider(map.mapPawns.SlavesOfColonySpawned);
		return list;
	}

	public static int RosterSignature(Map map)
	{
		int h = 17;
		foreach (Pawn p in Roster(map).OrderBy(p => p.thingIDNumber))
			h = h * 31 + p.thingIDNumber;
		return h;
	}

	// 2.34.1: your level plan is a target, not a lock – it applies only while
	// the work has something to do (bills waiting). With no bills the colonists
	// are planned freely and get the work back as soon as bills appear.
	private static readonly HashSet<WorkTypeDef> activePlans = new HashSet<WorkTypeDef>();

	private static bool BillWork(WorkTypeDef w) =>
		w.workGiversByPriority != null && w.workGiversByPriority.Any(g => g.giverClass != null && typeof(WorkGiver_DoBill).IsAssignableFrom(g.giverClass));

	public static bool PlanActive(WorkTypeDef w) => activePlans.Contains(w);

	// child work type -> the work type it follows
	private static readonly Dictionary<string, string> LinkedWork = new Dictionary<string, string>
	{
		{ "TreeChopping", "PlantCutting" }
	};

	public static int Plan(Map map, ForemanMapComponent comp, MapDemand demand)
	{
		activePlans.Clear();
		ForemanSettings settings = ForemanMod.Settings;
		ForemanGameComponent foremanGameComponent = ForemanGameComponent.Get();
		int max = PriorityRange.Max;
		List<Pawn> list = new List<Pawn>();
		list.AddRange(Roster(map));
		comp.reasons.Clear();
		if (list.Count == 0)
		{
			return 0;
		}
		if (!Find.PlaySettings.useWorkPriorities)
		{
			Find.PlaySettings.useWorkPriorities = true;
			foreach (Pawn item2 in PawnsFinder.AllMapsWorldAndTemporary_Alive)
			{
				if (item2.Faction == Faction.OfPlayer && item2.workSettings != null)
				{
					item2.workSettings.Notify_UseWorkPrioritiesChanged();
				}
			}
		}
		Dictionary<StatDef, float> statAvg = new Dictionary<StatDef, float>();
		Dictionary<Pawn, Dictionary<WorkTypeDef, int>> dictionary = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
		// Steward 2.9.0: how well each colonist fits each job (for the personal order)
		var fitOf = new Dictionary<Pawn, Dictionary<WorkTypeDef, float>>();
		var fixedTypes = new HashSet<WorkTypeDef>();
		var styleOf = new Dictionary<WorkTypeDef, float>();
		foreach (Pawn item3 in list)
		{
			dictionary[item3] = new Dictionary<WorkTypeDef, int>();
		}
		List<Pawn> list2 = ((!settings.countMechs) ? null : WorkMechs(map));
		// Steward 2.12: your own settings (importance, min/max colonists,
		// colonists per level) are used only in the Custom ("My style") style.
		// 2.45: Simple mode plans as Balanced, without styles
		ForemanStyle planStyle = Modes.PlanStyle(comp);
		bool manual = planStyle == ForemanStyle.Custom;
		bool even = planStyle == ForemanStyle.Balanced;
		// Balanced: how many real jobs (not backup) each colonist already got,
		// so the next job goes to someone with less on his plate.
		var load = new Dictionary<Pawn, int>();
		// 2.15: level of each cell before the personal order / nobody-idle step
		var planned = new Dictionary<(Pawn, WorkTypeDef), int>();
		// 2.20: situations (Balanced only): "N colonists on priority L" per
		// work type. Several situations for one job add up their colonists and
		// use the higher priority.
		var sitNeeds = new Dictionary<WorkTypeDef, SitStaff>();
		var minOrder = new Dictionary<WorkTypeDef, (int, List<Pawn>, int)>();
		// 2.34: your rules (level plan, min, max) are applied once more at the
		// very end, so no later step (one top job, style, situations) undoes them
		var planOrder = new Dictionary<WorkTypeDef, (int[] plan, List<Candidate> order)>();
		var maxOrder = new Dictionary<WorkTypeDef, (int max, List<Pawn> order)>();
		var sitCells = new HashSet<(Pawn, WorkTypeDef)>();
		var sitLoad = new Dictionary<Pawn, int>();
		HashSet<Pawn> exhausted = null;
		// 2.21: at most one priority-1 job per colonist (setting)
		bool oneTop = settings.oneTopPriority && max >= 2;
		var importance = new Dictionary<WorkTypeDef, float>();
		var crisisUrg = new Dictionary<WorkTypeDef, float>();
		var sitWant = new Dictionary<WorkTypeDef, int>();
		var sitPool = new Dictionary<WorkTypeDef, List<Pawn>>();
		if (SituationRules.Active(comp) && comp.situations != null)
		{
			foreach (ActiveSituation s in comp.situations)
			{
				if (s.id == Situation.Exhausted && s.pawns != null && s.pawns.Count > 0)
					exhausted = new HashSet<Pawn>(s.pawns);
				foreach (SitNeed n in s.needs)
				{
					if (n.work == null)
						continue;
					if (!sitNeeds.TryGetValue(n.work, out SitStaff st))
						sitNeeds[n.work] = st = new SitStaff();
					st.level = Math.Min(st.level, n.level);
					st.workers += n.workers;
					if (!st.labels.Contains(s.Label))
						st.labels.Add(s.Label);
				}
			}
		}
		foreach (WorkTypeDef item4 in WorkPolicies.VisibleWorkTypes())
		{
			WorkPolicy workPolicy = WorkPolicies.Get(item4);
			float baseUrgency = manual ? workPolicy.urgency : WorkPolicies.DefaultUrgency(item4);
			List<Candidate> list3 = Score(list, item4, workPolicy, statAvg, foremanGameComponent, settings);
			if (list3.Count == 0)
			{
				continue;
			}
			DemandInfo demandInfo = ((!settings.useDemand) ? null : demand.For(item4));
			float num = demandInfo?.factor ?? 1f;
			// 2.14: My style is planned only from your settings – no crises,
			// no demand, no automatic shifts. Firefighting still comes first.
			Crisiseffect crisiseffect = CrisisFor(demand.crisis, item4);   // 2.26.0: crises in every style
			// Steward 2.10: a chosen style is felt. Its shift is multiplied by
			// the "style strength" setting (default 2×) and it also changes how
			// many colonists get the job (below) and their personal order of
			// jobs (PersonalOrder). Before, a style moved the urgency by at most
			// 0.35 and almost nothing in the Work tab changed.
			// 2.19: plus the focus Balanced picked by itself (AutoFocus)
			float styleDelta = even
				? (Modes.Simple ? 0f : SmartBalance.Delta(map, item4) + AutoFocus.Delta(comp, item4))
				: manual ? 0f : Styles.Delta(planStyle, item4) * settings.styleStrength;
			// 2.26.0: a style never pushes down work that a crisis needs right now
			// 2.39.1: only a real push (≥ 0.2) – a small nudge like "death in the colony" (+0.1)
			// used to cancel the style's minus for construction completely
			if (crisiseffect.urgency >= 0.2f && styleDelta < 0f)
				styleDelta = 0f;
			styleOf[item4] = styleDelta;
			float demandBonus = 0f;
			if (settings.useDemand && !manual)
			{
				// 2.14: Balanced reacts to what is waiting more strongly (±0.25) –
				// it is the style for everything, so it has to follow the work
				demandBonus = even
					? Mathf.Clamp(0.3f * (num - 1f), -0.2f, 0.25f)
					: Mathf.Clamp(0.2f * (num - 1f), -0.15f, 0.15f);
			}
			// Hauling and cleaning (given to everyone): demand may lift them by
			// at most 0.1. A style that asks for them (Comfort, Building …) or a
			// real crisis still can.
			// 2.14.1: not in Balanced – there a littered map or a dirty base must
			// be able to lift hauling and cleaning above the other work
			if (workPolicy.everyone && baseUrgency < 0.5f && !even)
			{
				demandBonus = Mathf.Min(demandBonus, 0.1f);
			}
			float bonus = styleDelta + demandBonus;
			float num2 = baseUrgency + bonus + crisiseffect.urgency;
			if (workPolicy.emergency)
			{
				num2 = Mathf.Max(num2, baseUrgency);
			}
			num2 = Mathf.Clamp01(num2);
			// 2.14.2: the personal order of jobs follows everything that makes a
			// job urgent now (style, waiting work, crisis), not only the style
			styleOf[item4] = num2 - baseUrgency;
			importance[item4] = num2;
			crisisUrg[item4] = crisiseffect.urgency;
			int num3 = 1 + Mathf.RoundToInt((1f - num2) * (float)(max - 1));
			int num4 = 0;
			if (list2 != null && !workPolicy.emergency)
			{
				// 2.15: a work mech replaces one colonist (was one and a half)
				num4 = CountMechsFor(list2, item4);
			}
			list3.Sort((Candidate a, Candidate b) => b.score.CompareTo(a.score));
			float num5 = Mathf.Max(0.001f, list3[0].score);
			// 2.12 Balanced: among similarly good colonists the one with fewer
			// jobs so far is picked first – the work is spread evenly and no
			// colonist ends up with eight jobs while another has two.
			if (even && load.Count > 0)
			{
				list3 = list3.OrderByDescending(c =>
				{
					load.TryGetValue(c.pawn, out int n);
					return c.score / (1f + 0.3f * n);
				}).ToList();
			}
			Dictionary<Pawn, int> dictionary2 = new Dictionary<Pawn, int>();
			// 2.18: what decided each colonist's level (shown in the Work tab tooltip)
			var why = new Dictionary<Pawn, string>();
			void Why(Pawn p, string text) => why[p] = why.TryGetValue(p, out string old) ? old + " " + text : text;
			Candidate candidate = null;
			// 2.7: per work type limits from the mod settings
			// 2.31.0: your min / max colonists per work type apply in every style
			// 2.35: your own counts only when you switched them on
			int minO = settings.MyRulesOn ? settings.MinFor(item4) : 0;
			int maxO = settings.MyRulesOn ? settings.MaxFor(item4) : 0;
			// 2.35: how many colonists the work needs now (benches, dirt, hauling …)
			int wantW = settings.autoStaff && !manual ? Workload.Wanted(map, item4, list3.Count) : -1;
			// 2.32.1: remember the order of the best colonists for your minimum
			if (minO > 0 && (!BillWork(item4) || (demand?.For(item4)?.factor ?? 0f) >= 0.35f))
				// 2.32.4: when the style (not a crisis) pushes the work down, your
				// minimum is met on the style's level, not on priority 1
				minOrder[item4] = (minO, list3.Where(c => !c.opposed).Select(c => c.pawn).ToList(),
					!manual && !even && styleDelta < 0f ? Math.Max(1, num3) : 1);
			bool limited = maxO > 0;
			bool flag = workPolicy.everyone && num4 == 0 && !limited;
			if (workPolicy.emergency || flag)
				fixedTypes.Add(item4);
			// Steward 2.8: a player's plan "N colonists on each level" wins
			// 2.17: in every colony style, not only in "My style"
			int[] plan = settings.MyRulesOn ? settings.PlanFor(item4, max) : null;
			if (plan != null && BillWork(item4))
			{
				DemandInfo di = demand?.For(item4);
				if (di == null || di.factor < 0.35f)
					plan = null;   // nothing to do now – planned freely this time
			}
			if (plan != null && !workPolicy.emergency)
			{
				activePlans.Add(item4);
				planOrder[item4] = (plan, list3.ToList());
			}
			if (maxO > 0)
				maxOrder[item4] = (maxO, list3.Select(c => c.pawn).ToList());
			if (plan != null && !workPolicy.emergency)
			{
				int idx = 0;
				for (int lvl = 1; lvl <= max; lvl++)
				{
					int need = plan[lvl - 1];
					while (need > 0 && idx < list3.Count)
					{
						Candidate c = list3[idx++];
						if (c.opposed && !settings.assignOpposed)
							continue;
						dictionary2[c.pawn] = c.opposed ? max : lvl;
						Why(c.pawn, "CF_Why_Plan".Translate(lvl, plan[lvl - 1]));
						need--;
					}
				}
				foreach (Candidate c in list3)
				{
					if (!manual && c.inspired && !c.opposed)
					{
						dictionary2[c.pawn] = 1;
						Why(c.pawn, "CF_Why_Inspired".Translate());
					}
				}
				foreach (Candidate c in list3)
				{
					if (dictionary2.ContainsKey(c.pawn))
						continue;
					if (settings.backupWorkers && !c.opposed)
					{
						dictionary2[c.pawn] = max;
						Why(c.pawn, "CF_Why_PlanBackup".Translate());
					}
					else
						Why(c.pawn, c.opposed ? "CF_Why_OpposedSkip".Translate() : "CF_Why_PlanOff".Translate());
				}
			}
			else if (flag)
			{
				int num6 = Mathf.CeilToInt((float)list3.Count / 2f);
				// 2.35: as many colonists as the work needs get the better level,
				// the others keep it as a backup
				if (wantW >= 0)
					num6 = Mathf.Clamp(wantW, 1, list3.Count);
				for (int i = 0; i < list3.Count; i++)
				{
					Candidate candidate2 = list3[i];
					if (!candidate2.opposed || settings.assignOpposed)
					{
						int num7 = num3;
						// 2.14.1: with backup workers every other job sits on the lowest
						// level, and the game takes jobs of one level from left to right –
						// hauling and cleaning (far right) were never reached. They now
						// stay at least one level above the backups, for everybody.
						if (!workPolicy.emergency && i >= num6 && !settings.backupWorkers)
						{
							num7++;
						}
						if (settings.backupWorkers && !workPolicy.emergency && max >= 3)
						{
							num7 = Math.Min(num7, max - 1);
						}
						if (candidate2.opposed)
						{
							num7 = max;
						}
						dictionary2[candidate2.pawn] = Mathf.Clamp(num7, 1, max);
						Why(candidate2.pawn, candidate2.opposed ? "CF_Why_Opposed".Translate() : "CF_Why_Everyone".Translate(num2.ToString("0.00")));
					}
				}
			}
			else
			{
				float num8 = ((!(workPolicy.share > 0f)) ? settings.specialistShare : workPolicy.share);
				if (workPolicy.everyone)
				{
					num8 = 1f;
				}
				int count = list3.Count;
				int num9 = Math.Max(workPolicy.minWorkers, crisiseffect.minWorkers);
				// 2.14 Balanced: every job that has work waiting has someone on it –
				// at least one colonist, about a quarter of them when there is a lot
				if (even && settings.useDemand && demandInfo != null && num >= 0.9f)
					num9 = Math.Max(num9, Math.Max(1, Mathf.RoundToInt(list3.Count * (num >= 1.4f ? 0.25f : 0.12f))));
				// Steward 2.10: the style also decides how many colonists do it:
				// a job the style favours gets up to about twice as many, a job
				// it pushes back about half.
				float styleWorkers = Mathf.Clamp(1f + styleDelta * 2.5f, 0.4f, 2.5f);
				bool quotaFromLoad = false;
				int num10 = Mathf.CeilToInt((float)count * num8 * styleWorkers * crisiseffect.workers * ((!settings.useDemand || manual) ? 1f : Mathf.Clamp(num, 0.5f, 2f)));
				if (num4 > 0)
				{
					num10 = Math.Max(1, num10 - num4);
					// 2.15: hauling and cleaning – mechs never push more than half
					// of the colonists out of it
					if (workPolicy.everyone)
						num10 = Math.Max(num10, Mathf.CeilToInt(list3.Count * 0.5f));
				}
				// 2.35: the workload decides the number (style and crises still
				// scale it); no work waiting = nobody needs it right now
				if (wantW >= 0 && !workPolicy.emergency)
				{
					int byLoad = Mathf.CeilToInt(wantW * Mathf.Clamp(styleWorkers, 0.5f, 1.6f) * crisiseffect.workers);
					num10 = Math.Max(0, byLoad - num4);
					quotaFromLoad = true;
				}
				num10 = Mathf.Clamp(Math.Max(num10, settings.coverage ? num9 : 0), 0, count);
				if (minO > 0)
					num10 = Math.Max(num10, Math.Min(minO, count));
				if (limited)
					num10 = Math.Min(num10, maxO);
				int num11 = 0;
				string quota = quotaFromLoad ? (string)"CF_Why_Load".Translate(num10, wantW) : (string)"CF_Why_Quota".Translate(num10, count);
				for (int j = 0; j < list3.Count; j++)
				{
					Candidate candidate3 = list3[j];
					if (num11 >= num10)
					{
						Why(candidate3.pawn, quota + " " + "CF_Why_QuotaFull".Translate(num10));
						continue;
					}
					bool flag2 = ((settings.coverage || even) && num11 < num9) || num11 < minO;
					bool lowFit = candidate3.score < 0.2f;
					// 2.35.8: the colonists the workload asks for all get the job (even with
					// little skill – they learn), and all on the job's level
					if ((!candidate3.opposed || (settings.assignOpposed && flag2)) && (!lowFit || flag2 || quotaFromLoad || candidate3.preferred || candidate3.inspired))
					{
						int value;
						int lower = 0;
						float num12 = candidate3.score / num5;
						if (workPolicy.pinned)
						{
							value = num3;
						}
						else if (quotaFromLoad)
						{
							value = num3;
						}
						else
						{
							lower = ((num12 < 0.6f) ? 1 : 0) + ((num12 < 0.35f) ? 1 : 0);
							value = num3 + lower;
						}
						if (candidate3.opposed)
						{
							value = max;
						}
						// 2.32.1: the colonists of your minimum all get the job's top level
						if (num11 < minO && !candidate3.opposed)
							value = num3;
						dictionary2[candidate3.pawn] = Mathf.Clamp(value, 1, max);
						num11++;
						string picked = candidate3.opposed ? "CF_Why_Opposed".Translate()
							: lowFit && flag2 && !candidate3.preferred && !candidate3.inspired ? "CF_Why_Coverage".Translate(Math.Max(num9, minO))
							: workPolicy.pinned ? "CF_Why_Pinned".Translate(num11)
							: lower > 0 ? "CF_Why_PickedLower".Translate(num11, lower, Mathf.RoundToInt(num12 * 100f))
							: "CF_Why_Picked".Translate(num11, num2.ToString("0.00"));
						Why(candidate3.pawn, quota + " " + picked);
					}
					else
					{
						Why(candidate3.pawn, quota + " " + (candidate3.opposed ? "CF_Why_OpposedSkip".Translate() : "CF_Why_LowFit".Translate(candidate3.score.ToString("0.00"))));
					}
				}
				foreach (Candidate item5 in list3)
				{
					if (!manual && item5.inspired && !item5.opposed && (!limited || dictionary2.ContainsKey(item5.pawn) || dictionary2.Count < maxO))
					{
						dictionary2[item5.pawn] = 1;
						Why(item5.pawn, "CF_Why_Inspired".Translate());
					}
				}
				if (!manual && settings.apprentices && item4.relevantSkills != null && item4.relevantSkills.Count > 0 && list3.Count >= 3)
				{
					candidate = PickApprentice(list3, dictionary2, item4);
				}
				if (candidate != null && (!limited || dictionary2.Count < maxO || dictionary2.ContainsKey(candidate.pawn)))
				{
					dictionary2[candidate.pawn] = max;
					Why(candidate.pawn, "CF_Why_Apprentice".Translate());
				}
				// 2.7: everybody else who can do the job keeps it at the lowest
				// priority – they do it when nothing more important is left,
				// instead of standing around. Not when the player set a maximum.
				foreach (Candidate c in list3)
				{
					if (c.opposed || dictionary2.ContainsKey(c.pawn))
						continue;
					if (!limited && settings.backupWorkers)
					{
						dictionary2[c.pawn] = max;
						Why(c.pawn, "CF_Why_Backup".Translate());
					}
					else
						Why(c.pawn, limited ? "CF_Why_Max".Translate(maxO) : "CF_Why_Off".Translate());
				}
				// 2.15: hauling and cleaning stay above the backups here too
				// (this branch is used when work mechs share the job)
				if (workPolicy.everyone && settings.backupWorkers && max >= 3)
				{
					foreach (Candidate c in list3)
						if (!c.opposed && dictionary2.TryGetValue(c.pawn, out int lv) && lv >= max)
						{
							dictionary2[c.pawn] = max - 1;
							Why(c.pawn, "CF_Why_AboveBackups".Translate());
						}
				}
			}
			// 2.20: a situation rewrites this job: the best-suited colonists (skill,
			// passion, speed – the order above) are raised to its priority until
			// there are as many as it needs. Locked cells and Achtung! forced work
			// are not changed (but count when they already do the job on that
			// level); very tired colonists and those whose ideoligion opposes the
			// work are not picked; work mechs cover part of it. Your own plan of
			// colonists per level (settings) wins.
			// 2.20.1: situations also override your own plan while they last (they only add colonists)
			if (sitNeeds.TryGetValue(item4, out SitStaff sitNeed))
			{
				int lvl = Mathf.Clamp(sitNeed.level, 1, max);
				int want = sitNeed.workers;
				if (num4 > 0)
					want = Math.Max(1, want - num4);
				want = Math.Min(want, Math.Max(1, Mathf.CeilToInt(list3.Count * 0.75f)));
				string label = string.Join(", ", sitNeed.labels);
				int have = 0;
				var fixedPawns = new HashSet<Pawn>();
				foreach (Candidate c in list3)
				{
					if (!(foremanGameComponent?.AnyLock(c.pawn, item4) ?? false) && !ModCompat.AchtungForced(c.pawn))
						continue;
					fixedPawns.Add(c.pawn);
					int cur = c.pawn.workSettings.GetPriority(item4);
					if (cur > 0 && cur <= lvl)
						have++;
				}
				var order = list3.Where(c => !c.opposed && !fixedPawns.Contains(c.pawn)).OrderByDescending(c =>
				{
					sitLoad.TryGetValue(c.pawn, out int k);
					float sc = c.score / (1f + 0.5f * k);
					// 2.21: a priority-1 situation prefers colonists who have no
					// priority-1 job yet (only one per colonist is kept)
					if (oneTop && lvl == 1 && HasTopJob(c.pawn, dictionary[c.pawn], foremanGameComponent))
						sc *= 0.5f;
					return sc;
				}).ToList();
				if (oneTop && lvl == 1)
				{
					sitWant[item4] = want;
					sitPool[item4] = order.Where(c => !SituationRules.IsTired(c.pawn) && (exhausted == null || !exhausted.Contains(c.pawn))).Select(c => c.pawn).ToList();
				}
				int rank = 0;
				foreach (Candidate c in order)
				{
					if (have >= want)
						break;
					if (SituationRules.IsTired(c.pawn) || (exhausted != null && exhausted.Contains(c.pawn)))
						continue;
					dictionary2.TryGetValue(c.pawn, out int cur);
					if (cur <= 0 || cur > lvl)
						dictionary2[c.pawn] = lvl;
					have++;
					rank++;
					sitCells.Add((c.pawn, item4));
					sitLoad.TryGetValue(c.pawn, out int k2);
					sitLoad[c.pawn] = k2 + 1;
					Why(c.pawn, "CF_Why_Situation".Translate(label, want, lvl, rank));
				}
			}
			if (!flag && !workPolicy.emergency)
			{
				foreach (var kv in dictionary2)
				{
					if (kv.Value > 0 && kv.Value < max)
					{
						load.TryGetValue(kv.Key, out int n);
						load[kv.Key] = n + 1;
					}
				}
			}
			for (int k = 0; k < list3.Count; k++)
			{
				Candidate candidate4 = list3[k];
				dictionary2.TryGetValue(candidate4.pawn, out var value2);
				dictionary[candidate4.pawn][item4] = value2;
				if (!fitOf.TryGetValue(candidate4.pawn, out var fits))
					fitOf[candidate4.pawn] = fits = new Dictionary<WorkTypeDef, float>();
				fits[item4] = candidate4.score / num5;
				why.TryGetValue(candidate4.pawn, out string decision);
				string value3 = Explain(candidate4, value2, k + 1, list3.Count, item4, workPolicy, demandInfo, demand.crisis, crisiseffect, planStyle, flag, candidate4 == candidate, num4, foremanGameComponent?.AnyLock(candidate4.pawn, item4) ?? false, decision)
					// 2.15: how the importance was put together
					+ "\n" + "CF_TipMath".Translate(num2.ToString("0.00"), baseUrgency.ToString("0.00"), Signed(styleDelta), Signed(demandBonus), Signed(crisiseffect.urgency)).Colorize(ColorLibrary.Grey);
				planned[(candidate4.pawn, item4)] = value2;
				if (!comp.reasons.TryGetValue(candidate4.pawn.thingIDNumber, out var value4))
				{
					value4 = (comp.reasons[candidate4.pawn.thingIDNumber] = new Dictionary<WorkTypeDef, string>());
				}
				value4[item4] = value3;
			}
		}
		// 2.14: My style keeps exactly the levels from your settings
		if (!manual)
			PersonalOrder(dictionary, fitOf, fixedTypes, max, foremanGameComponent, settings, comp, styleOf, sitCells);
		NobodyIdle(dictionary, fixedTypes, max, settings, manual);
		// 2.20: a very tired colonist is not kept on priority 1 work (except
		// emergencies – firefighting, doctor, patient, childcare)
		if (exhausted != null && max >= 2)
		{
			foreach (Pawn p in exhausted)
			{
				if (!dictionary.TryGetValue(p, out var jobs))
					continue;
				foreach (WorkTypeDef w in jobs.Keys.ToList())
				{
					if (jobs[w] != 1 || WorkPolicies.Get(w).emergency)
						continue;
					jobs[w] = 2;
					if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
						texts[w] = t + "\n" + "CF_TipExhausted".Translate().Colorize(ColorLibrary.Gold);
				}
			}
		}
		// 2.40: a chosen style rules the Work tab – only the work it lists is planned
		// (1–3); everything else goes to the lowest priority or off. Emergencies,
		// crises, situations, your locks and your own rules (below) still win.
		if (!manual && !even && settings.styleStrict)
		{
			try { StyleStrict(comp, dictionary, crisisUrg, sitCells, foremanGameComponent, max, settings, demand.crisis, fitOf, settings.useDemand ? demand : null); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] strict style failed: " + ex, 0x57e3a43); }
		}
		// 2.21: one priority-1 job per colonist (after styles, situations, crises)
		if (oneTop)
			OneTopJob(dictionary, fitOf, importance, crisisUrg, sitNeeds, sitCells, sitWant, sitPool, max, foremanGameComponent, comp);
		// 2.15: say in the tooltip when the final level differs from the plan
		foreach (var kv in dictionary)
		{
			if (!comp.reasons.TryGetValue(kv.Key.thingIDNumber, out var texts))
				continue;
			foreach (var j in kv.Value)
			{
				if (!planned.TryGetValue((kv.Key, j.Key), out int was) || was == j.Value || !texts.TryGetValue(j.Key, out string t))
					continue;
				// 2.18: a move in the personal order already has its own line
				// (CF_TipPersonal) – only the nobody-idle step is added here
				if (was == 0)
					texts[j.Key] = t + "\n" + "CF_TipIdle".Translate(j.Value).Colorize(ColorLibrary.Gold);
			}
		}
		// 2.32: light work when hurt, passion work for low mood, learning
		try { StewardPlus.AdjustPlan(map, comp, dictionary, max); }
		catch (Exception ex) { Log.ErrorOnce("[Steward] plan extras failed: " + ex, 0x57e3a32); }
		// 2.34: YOUR RULES WIN – the level plan ("N colonists on level L") is
		// set again here, after the one-top-job rule, styles, situations and
		// extras. Only your locks, disabled work and hurt colonists on heavy work
		// are skipped (the next colonist in line takes the place).
		foreach (var kv in planOrder)
		{
			WorkTypeDef w = kv.Key;
			int[] lp = kv.Value.plan;
			int idx = 0;
			for (int lvl = 1; lvl <= max && lvl <= lp.Length; lvl++)
			{
				int need = lp[lvl - 1];
				while (need > 0 && idx < kv.Value.order.Count)
				{
					Candidate c = kv.Value.order[idx++];
					Pawn p = c.pawn;
					if (c.opposed && !settings.assignOpposed)
						continue;
					if (!dictionary.TryGetValue(p, out var jobs) || p.WorkTypeIsDisabled(w) || StewardPlus.KeepLight(p, w))
						continue;
					if (foremanGameComponent != null && foremanGameComponent.AnyLock(p, w))
						continue;
					int target = c.opposed ? max : lvl;
					// 2.34.1: during a crisis its job keeps priority 1
					if (target == 1 && max >= 2 && jobs.Any(j => j.Key != w && j.Value == 1 && crisisUrg.TryGetValue(j.Key, out float cu) && cu > 0f))
						target = 2;
					if (!jobs.TryGetValue(w, out int cur) || cur != target)
					{
						jobs[w] = target;
						if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
							texts[w] = t + "\n" + "CF_TipPlanKept".Translate(lvl, lp[lvl - 1]).Colorize(ColorLibrary.Gold);
					}
					need--;
				}
			}
		}
		// 2.33.1: urgent hauling (Allow Tool) – only a few colonists drop their
		// work for it, the rest keep it on their hauling level
		if (settings.urgentHaulFew)
		{
			try { UrgentHaulFew(map, dictionary, foremanGameComponent, max); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] urgent haul failed: " + ex, 0x57e3a41); }
		}
		// 2.32.1/2.32.2: your minimum wins over every other step (one top job,
		// personal order, hurt, learning): at least that many colonists on priority 1
		foreach (var kv in minOrder)
		{
			WorkTypeDef w = kv.Key;
			int want = kv.Value.Item1;
			// 2.32.2: priority 1 – 2.32.4: or the level of a style that does not want the work
			int top = kv.Value.Item3;
			int have = dictionary.Count(pj => pj.Value.TryGetValue(w, out int v) && v > 0 && v <= top);
			foreach (Pawn p in kv.Value.Item2)
			{
				if (have >= want)
					break;
				if (!dictionary.TryGetValue(p, out var jobs) || p.WorkTypeIsDisabled(w) || StewardPlus.KeepLight(p, w))
					continue;
				if (foremanGameComponent != null && foremanGameComponent.AnyLock(p, w))
					continue;
				if (jobs.TryGetValue(w, out int cur) && cur > 0 && cur <= top)
					continue;
				jobs[w] = top;
				have++;
				if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
					texts[w] = t + "\n" + "CF_TipMinimum".Translate(want, top).Colorize(ColorLibrary.Gold);
			}
		}
		// 2.34: your maximum – the colonists last in line lose the work again
		foreach (var kv in maxOrder)
		{
			WorkTypeDef w = kv.Key;
			int limit = kv.Value.max;
			var on = kv.Value.order.Where(p => dictionary.TryGetValue(p, out var j) && j.TryGetValue(w, out int v) && v > 0).ToList();
			for (int i = on.Count - 1; i >= 0 && on.Count > limit; i--)
			{
				Pawn p = on[i];
				if (foremanGameComponent != null && foremanGameComponent.AnyLock(p, w))
					continue;
				// 2.52.3: what a situation or a crisis needs now is not cut by your maximum
				if (sitCells.Contains((p, w)) || (crisisUrg.TryGetValue(w, out float mcu) && mcu > 0f))
					continue;
				dictionary[p][w] = 0;
				on.RemoveAt(i);
			}
		}
		// 2.37: children – time to learn
		if (settings.childLearning)
		{
			try { ChildLearning.Apply(dictionary, foremanGameComponent, comp, max); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] child learning failed: " + ex, 0x57e3a42); }
		}
		// 2.48: mothers and nannies for the babies
		if (settings.parentCare)
		{
			try { ParentCare.Apply(dictionary, foremanGameComponent, comp); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] parent care failed: " + ex, 0x57e3a43); }
		}
		// 2.45.1: work types that mods split off from another one follow it
		// (Tree Chopping Speed Stat moves tree chopping out of Plant cutting)
		foreach (var link in LinkedWork)
		{
			WorkTypeDef child = DefDatabase<WorkTypeDef>.GetNamedSilentFail(link.Key);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(link.Value);
			if (child == null || parent == null)
				continue;
			foreach (var pj in dictionary)
			{
				if (!pj.Value.TryGetValue(parent, out int pv) || pj.Key.WorkTypeIsDisabled(child))
					continue;
				// 2.52.3: not work the colonist's ideology forbids (tree cutting)
				if (pv > 0 && pj.Key.Ideo != null && pj.Key.Ideo.IsWorkTypeConsideredDangerous(child) && !settings.assignOpposed)
					continue;
				if (foremanGameComponent != null && foremanGameComponent.AnyLock(pj.Key, child))
					continue;
				pj.Value[child] = pv;
				if (comp.reasons.TryGetValue(pj.Key.thingIDNumber, out var lt))
					lt[child] = "CF_TipLinked".Translate(parent.labelShort);
			}
		}
		int num13 = 0;
		// 2.45: calm modes (Simple, Recommended) – no small back-and-forth, nobody
		// moved again within half a day, a crisis takes its people and gives them back
		bool calm = Modes.Calm && !comp.planFull;
		int nowTick = Find.TickManager.TicksGame;
		int hold = Modes.HoldTicks;
		// a real danger (fire, raid, wounded …) may move anyone; it is remembered,
		// so everything it moved goes back freely when it ends
		bool dangerNow = (demand.crisis & Modes.Danger) != 0;
		if (Modes.Calm)
			comp.PruneCalm(nowTick);
		foreach (KeyValuePair<Pawn, Dictionary<WorkTypeDef, int>> item6 in dictionary)
		{
			// 2.14.3: Achtung! forced work – priorities stay as they are
			if (ModCompat.AchtungForced(item6.Key))
				continue;
			Pawn key = item6.Key;
			foreach (KeyValuePair<WorkTypeDef, int> item7 in item6.Value)
			{
				WorkTypeDef w = item7.Key;
				int nv = item7.Value;
				if ((foremanGameComponent != null && foremanGameComponent.AnyLock(key, w)) || key.WorkTypeIsDisabled(w))
					continue;
				int cur = key.workSettings.GetPriority(w);
				if (cur == nv)
					continue;
				string ck = key.thingIDNumber + "|" + w.defName;
				bool crisisCell = dangerNow || (crisisUrg.TryGetValue(w, out float cuNow) && cuNow > 0f);
				if (Modes.Calm)
				{
					comp.calmCells.TryGetValue(ck, out CalmCell cc);
					// a job switched on is never held back (a job must not end up with nobody)
					// 2.49.3: childcare (parents, nannies) is never held back – in every style
					bool free = !calm || crisisCell || (cc != null && cc.crisis) || WorkPolicies.Get(w).emergency || (cur == 0 && nv > 0) || w.defName == "Childcare"
						|| sitCells.Contains((key, w));   // 2.50.2: a situation is never held back by the calm modes
					if (!free)
					{
						bool recent = cc != null && nowTick - cc.tick < hold;
						bool small = cc != null && nowTick - cc.tick < 2 * hold
							&& nv != 0 && cur != 0 && nv != 1 && cur != 1 && Math.Abs(nv - cur) <= 1;
						if (small || recent)
						{
							if (comp.reasons.TryGetValue(key.thingIDNumber, out var kt) && kt.TryGetValue(w, out string kts))
								kt[w] = kts + "\n" + (small ? "CF_TipCalmSmall" : "CF_TipCalmRecent").Translate(cur).Colorize(ColorLibrary.Gold);
							continue;
						}
					}
					// do not pull a colonist off the work he is doing right now
					if (nv == 0 && !crisisCell && !StewardPlus.KeepLight(key, w) && key.CurJob?.workGiverDef?.workType == w)
						nv = max;
					if (cur == nv)
						continue;
					comp.calmCells[ck] = new CalmCell { tick = nowTick, crisis = crisisCell };
				}
				key.workSettings.SetPriority(w, nv);
				num13++;
			}
		}
		return num13;
	}

	// 2.21: work that never counts as "the" priority-1 job and is never moved
	// by the one-job rule: firefighting, patient, bed rest, basic work (the
	// emergency jobs everybody keeps on top) and your direct orders (Allow
	// Utilities urgent hauling / finishing off).
	private static void UrgentHaulFew(Map map, Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, ForemanGameComponent game, int max)
	{
		WorkTypeDef urg = DefDatabase<WorkTypeDef>.GetNamedSilentFail("KAU_UrgentHaul");
		DesignationDef mark = DefDatabase<DesignationDef>.GetNamedSilentFail("KAU_HaulUrgentlyDesignation");
		if (urg == null)
			return;
		WorkTypeDef haul = WorkTypeDefOf.Hauling;
		int marks = mark == null ? 0 : map.designationManager.SpawnedDesignationsOfDef(mark).Count();
		var pawns = plan.Where(kv => kv.Value.TryGetValue(urg, out int v) && v > 0 && !kv.Key.WorkTypeIsDisabled(urg)
		                             && (game == null || !game.AnyLock(kv.Key, urg))).ToList();
		if (pawns.Count == 0)
			return;
		// 1 colonist per 5 marked things, at most a third of the colony
		int want = marks == 0 ? 0 : Mathf.Clamp(Mathf.CeilToInt(marks / 5f), 1, Math.Max(1, pawns.Count / 3));
		// the colonists with the fewest important (priority 1) jobs go first
		var order = pawns.OrderBy(kv => kv.Value.Count(j => j.Key != urg && j.Value == 1 && !TopExempt(j.Key)))
			.ThenBy(kv => kv.Value.TryGetValue(haul, out int h) && h > 0 ? h : max).ToList();
		for (int i = 0; i < order.Count; i++)
		{
			var jobs = order[i].Value;
			if (i < want)
				jobs[urg] = 1;
			else
				jobs[urg] = jobs.TryGetValue(haul, out int h) && h > 0 ? h : max;
		}
	}

	// work that is never "finished" (always something to research or make art of) – not kept by 2.50.1
	private static bool OpenEnded(WorkTypeDef w)
	{
		switch (w.defName)
		{
		case "Research":
		case "Art":
		case "DarkStudy":
			return true;
		}
		return false;
	}

	// 2.50.0 / 2.52.3: fresh animal carcasses that an active butcher bill accepts, and enough ready meals.
	// Cached per map (it is asked on every job search of every colonist).
	private static readonly Dictionary<int, KeyValuePair<int, bool>> foodCache = new Dictionary<int, KeyValuePair<int, bool>>();

	public static void ResetFood() => foodCache.Clear();

	// fresh, allowed animal carcasses that an active butcher bill takes (counted up to 'max')
	public static int ButcherableCarcasses(Map map, int max)
	{
		var bills = new List<Bill>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver bg) || bg.BillStack == null || (b is Building_WorkTable wt && !wt.CurrentlyUsableForBills()))
				continue;
			foreach (Bill bill in bg.BillStack.Bills)
				if (!bill.suspended && bill.recipe?.specialProducts != null && bill.recipe.specialProducts.Contains(SpecialProductType.Butchery) && bill.ShouldDoNow())
					bills.Add(bill);
		}
		if (bills.Count == 0)
			return 0;
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
		{
			if (!(t is Corpse c) || c.InnerPawn == null || !c.InnerPawn.RaceProps.Animal || c.IsForbidden(Faction.OfPlayer) || c.GetRotStage() != RotStage.Fresh)
				continue;
			foreach (Bill bill in bills)
				if (bill.IsFixedOrAllowedIngredient(c))
				{
					if (++n >= max)
						return n;
					break;
				}
		}
		return n;
	}

	public static bool MealsShort(Map map)
	{
		if (map == null)
			return false;
		int colonists = map.mapPawns.FreeColonistsSpawnedCount;
		if (colonists == 0)
			return false;
		int meals = 0;
		foreach (Thing m in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
			if (m.def.ingestible != null && m.def.ingestible.IsMeal && !m.IsForbidden(Faction.OfPlayer))
			{
				meals += m.stackCount;
				if (meals >= colonists)
					return false;
			}
		return true;
	}

	public static bool FoodWorkWaiting(Map map)
	{
		if (map == null)
			return false;
		int now = Find.TickManager.TicksGame;
		if (foodCache.TryGetValue(map.uniqueID, out var kv) && now - kv.Key < 1250 && now >= kv.Key)
			return kv.Value;
		bool want = false;
		try
		{
			// no raw-meat limit – the butcher bill's own "do until X" decides; only with fewer ready meals
			// than colonists the cooks keep cooking first
			int colonists = map.mapPawns.FreeColonistsSpawnedCount;
			int meals = 0;
			foreach (Thing m in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
				if (m.def.ingestible != null && m.def.ingestible.IsMeal && !m.IsForbidden(Faction.OfPlayer))
				{
					meals += m.stackCount;
					if (meals >= colonists)
						break;
				}
			if (meals >= colonists)
				want = ButcherableCarcasses(map, 1) > 0;
		}
		catch
		{
		}
		if (foodCache.Count > 32)
			foodCache.Clear();
		foodCache[map.uniqueID] = new KeyValuePair<int, bool>(now, want);
		return want;
	}

	// work that is never switched off by a strict style (it only goes to the lowest level)
	private static bool Essential(WorkTypeDef w)
	{
		return w == WorkTypeDefOf.Hauling || w.defName == "Cleaning" || w.defName == "KAU_UrgentHaul" || w.defName == "KAU_FinishingOff"
		       || w.defName == "HaulSmarterUrgent";
	}

	// 2.44: order in a chosen style – CRISIS > STYLE > what is needed (situations) > the rest.
	private static void StyleStrict(ForemanMapComponent comp, Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan,
		Dictionary<WorkTypeDef, float> crisisUrg, HashSet<(Pawn, WorkTypeDef)> sitCells, ForemanGameComponent game, int max, ForemanSettings settings,
		Crisis crisis, Dictionary<Pawn, Dictionary<WorkTypeDef, float>> fitOf, MapDemand demand = null)
	{
		string styleLabel = Styles.Label(comp.style);
		// 2.50.0: fresh carcasses wait for the butcher and meat is low – cooking is not pushed down by the style
		bool food = FoodWorkWaiting(comp.map);
		// 2.52.3: with fewer ready meals than colonists cooking is never pushed down by the style
		bool mealsShort = MealsShort(comp.map);
		WorkTypeDef cookW = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
		foreach (var kv in plan)
		{
			Pawn p = kv.Key;
			if (ModCompat.AchtungForced(p) || p.workSettings == null)
				continue;
			Dictionary<WorkTypeDef, int> jobs = kv.Value;
			foreach (WorkTypeDef w in WorkPolicies.VisibleWorkTypes())
			{
				if (p.WorkTypeIsDisabled(w) || Styles.Delta(comp.style, w) > 0f)
					continue;
				// 2.52.3: only work that was in the plan – never opposed or impossible work as "the lowest level"
				if (!jobs.ContainsKey(w))
					continue;
				if (mealsShort && w == cookW)
					continue;
				if (TopExempt(w) || WorkPolicies.Get(w).emergency)
					continue;
				// 2.50.2: every crisis push counts in every style (before only a strong one)
				if (crisisUrg.TryGetValue(w, out float cu) && cu > 0f)
					continue;
				// 2.50.2: what a situation needs now keeps its level in every style – the "one job on 1"
				// step afterwards puts crisis > situation > style, so the style's job moves to 2 instead
				if (sitCells.Contains((p, w)))
					continue;
				if (game != null && game.AnyLock(p, w))
					continue;
				if (food && w == cookW && jobs.TryGetValue(w, out int ck) && ck > 0 && ck <= 2)
				{
					if (comp.reasons.TryGetValue(p.thingIDNumber, out var ftexts))
					{
						string fl = "CF_TipButcherWaiting".Translate().Colorize(ColorLibrary.Gold);
						ftexts[w] = ftexts.TryGetValue(w, out string ft) ? ft + "\n" + fl : fl;
					}
					continue;
				}
				int target = settings.styleOthersOff && !Essential(w) ? 0 : max;
				if (jobs.TryGetValue(w, out int cur) && cur == target)
					continue;
				if (target > cur && !settings.assignOpposed && p.Ideo != null && p.Ideo.IsWorkTypeConsideredDangerous(w))
					continue;   // 2.52.3: never raised into work the ideology forbids
				jobs[w] = target;
				if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts))
				{
					string line = (target == 0 ? "CF_TipStyleOff" : "CF_TipStyleOther").Translate(styleLabel, target).Colorize(ColorLibrary.Gold);
					texts[w] = texts.TryGetValue(w, out string t) ? t + "\n" + line : line;
				}
			}
			// a colonist whose top job was work the style does not have gets the
			// style's most important work he does on priority 1 instead
			if (max >= 2 && !jobs.Any(j => j.Value == 1 && !TopExempt(j.Key) && !WorkPolicies.Get(j.Key).emergency))
			{
				WorkTypeDef best = null;
				float bestD = 0f;
				foreach (var j in jobs)
				{
					float d = Styles.Delta(comp.style, j.Key);
					if (j.Value == 2 && d > bestD && (game == null || !game.AnyLock(p, j.Key)))
					{
						best = j.Key;
						bestD = d;
					}
				}
				if (best != null)
					jobs[best] = 1;
			}
		}
		// 2.50.0: at least one cook on priority 2 while carcasses wait
		if (food && cookW != null && max >= 2 && !plan.Any(kv => kv.Value.TryGetValue(cookW, out int v) && v > 0 && v <= 2))
		{
			Pawn bestCook = plan.Keys.Where(p => !p.WorkTypeIsDisabled(cookW) && (game == null || !game.AnyLock(p, cookW)) && !ModCompat.AchtungForced(p))
				.OrderByDescending(p => fitOf.TryGetValue(p, out var f) && f.TryGetValue(cookW, out float x) ? x : 0f).FirstOrDefault();
			if (bestCook != null)
			{
				plan[bestCook][cookW] = 2;
				if (comp.reasons.TryGetValue(bestCook.thingIDNumber, out var btexts))
				{
					string bl = "CF_TipButcherWaiting".Translate().Colorize(ColorLibrary.Gold);
					btexts[cookW] = btexts.TryGetValue(cookW, out string bt) ? bt + "\n" + bl : bl;
				}
			}
		}
		// 2.50.1: no work starves under a style – work that really waits (bills with ingredients, patients,
		// ripe plants, things to build …) keeps at least one colonist on priority 2
		if (demand != null && max >= 2)
		{
			foreach (WorkTypeDef w in WorkPolicies.VisibleWorkTypes())
			{
				if (Styles.Delta(comp.style, w) > 0f || TopExempt(w) || WorkPolicies.Get(w).emergency || Essential(w) || OpenEnded(w))
					continue;
				DemandInfo di = demand.For(w);
				if (di == null || di.factor < 0.9f)
					continue;
				if (plan.Any(kv => kv.Value.TryGetValue(w, out int v) && v > 0 && v <= 2))
					continue;
				Pawn best = plan.Keys.Where(p => !p.WorkTypeIsDisabled(w) && (game == null || !game.AnyLock(p, w)) && !ModCompat.AchtungForced(p))
					.OrderByDescending(p => fitOf.TryGetValue(p, out var f) && f.TryGetValue(w, out float x) ? x : 0f).FirstOrDefault();
				if (best == null)
					continue;
				plan[best][w] = 2;
				if (comp.reasons.TryGetValue(best.thingIDNumber, out var ntexts))
				{
					string nl = "CF_TipNeedKept".Translate(styleLabel, di.note ?? "").Colorize(ColorLibrary.Gold);
					ntexts[w] = ntexts.TryGetValue(w, out string nt) ? nt + "\n" + nl : nl;
				}
			}
		}
		// a crisis beats the style: its work gets enough colonists on priority 1
		if (crisis == Crisis.None)
			return;
		foreach (var cw in crisisUrg)
		{
			WorkTypeDef w = cw.Key;
			if (cw.Value <= 0f || w == null)
				continue;
			Crisiseffect e = CrisisFor(crisis, w);
			int able = plan.Count(kv => !kv.Key.WorkTypeIsDisabled(w));
			int need = Math.Min(able, Math.Max(Math.Max(1, e.minWorkers), Mathf.CeilToInt(able * 0.25f * e.workers)));
			int have = plan.Count(kv => kv.Value.TryGetValue(w, out int v) && v == 1);
			if (have >= need)
				continue;
			var order = plan.Keys.Where(p => !p.WorkTypeIsDisabled(w) && (game == null || !game.AnyLock(p, w)) && !ModCompat.AchtungForced(p)
			                                  && !(plan[p].TryGetValue(w, out int v) && v == 1))
				.OrderByDescending(p => fitOf.TryGetValue(p, out var f) && f.TryGetValue(w, out float x) ? x : 0f).ToList();
			foreach (Pawn p in order)
			{
				if (have >= need)
					break;
				plan[p][w] = 1;
				have++;
				if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts))
				{
					string line = "CF_TipCrisisTop".Translate(MapDemand.Label(crisis)).Colorize(ColorLibrary.RedReadable);
					texts[w] = texts.TryGetValue(w, out string t) ? t + "\n" + line : line;
				}
			}
		}
	}

	public static bool TopExempt(WorkTypeDef w)
	{
		if (w == null)
			return true;
		switch (w.defName)
		{
		case "Firefighter":
		case "Patient":
		case "PatientBedRest":
		case "BasicWorker":
		case "KAU_UrgentHaul":
		case "KAU_FinishingOff":
			return true;
		}
		if ((w.workTags & WorkTags.Firefighting) != 0)
			return true;
		WorkPolicy pol = WorkPolicies.Get(w);
		return pol.emergency && pol.everyone;
	}

	// the level that will really be in the Work tab: locked cells keep theirs
	private static int Effective(Pawn p, WorkTypeDef w, Dictionary<WorkTypeDef, int> jobs, ForemanGameComponent game)
	{
		if (game != null && game.AnyLock(p, w))
			return p.WorkTypeIsDisabled(w) ? 0 : p.workSettings.GetPriority(w);
		return jobs != null && jobs.TryGetValue(w, out int v) ? v : 0;
	}

	private static bool HasTopJob(Pawn p, Dictionary<WorkTypeDef, int> jobs, ForemanGameComponent game, WorkTypeDef except = null)
	{
		foreach (WorkTypeDef w in WorkPolicies.VisibleWorkTypes())
			if (w != except && !TopExempt(w) && Effective(p, w, jobs, game) == 1)
				return true;
		return false;
	}

	private static void Note(ForemanMapComponent comp, Pawn p, WorkTypeDef w, string text)
	{
		if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
			texts[w] = t + "\n" + text.Colorize(ColorLibrary.Gold);
	}

	// 2.21: a colonist keeps at most one job on priority 1. The one that stays:
	// a locked cell on 1, else work a crisis raises, else work a priority-1
	// situation put him on (the bigger situation first), else the most
	// important job he fits best (importance × suitability). The others go
	// to 2. Achtung! forced work and locked cells are never changed.
	// Afterwards a priority-1 situation that lost workers this way gets other
	// suitable colonists who have no priority-1 job yet.
	private static void OneTopJob(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan,
		Dictionary<Pawn, Dictionary<WorkTypeDef, float>> fitOf, Dictionary<WorkTypeDef, float> importance,
		Dictionary<WorkTypeDef, float> crisisUrg, Dictionary<WorkTypeDef, SitStaff> sitNeeds,
		HashSet<(Pawn, WorkTypeDef)> sitCells, Dictionary<WorkTypeDef, int> sitWant,
		Dictionary<WorkTypeDef, List<Pawn>> sitPool, int max, ForemanGameComponent game, ForemanMapComponent comp)
	{
		int down = Math.Min(2, max);
		foreach (var kv in plan)
		{
			Pawn pawn = kv.Key;
			if (ModCompat.AchtungForced(pawn))
				continue;
			var jobs = kv.Value;
			WorkTypeDef lockedTop = null;
			foreach (WorkTypeDef w in WorkPolicies.VisibleWorkTypes())
			{
				if (game != null && game.AnyLock(pawn, w) && !TopExempt(w) && Effective(pawn, w, jobs, game) == 1)
				{
					lockedTop = w;
					break;
				}
			}
			var tops = jobs.Where(j => j.Value == 1 && !TopExempt(j.Key) && (game == null || !game.AnyLock(pawn, j.Key)))
				.Select(j => j.Key).ToList();
			// 2.34: a job your level plan puts on priority 1 counts like a lock –
			// it stays, the colonist's other priority-1 jobs move down
			if (lockedTop == null)
			{
				ForemanSettings ps = ForemanMod.Settings;
				bool crisisTop = tops.Any(w => crisisUrg.TryGetValue(w, out float cu) && cu > 0f);
				WorkTypeDef planned = ps == null || crisisTop ? null : tops.FirstOrDefault(w => { if (!PlanActive(w)) return false; int[] lp = ps.MyRulesOn ? ps.PlanFor(w, max) : null; return lp != null && lp[0] > 0; });
				if (planned != null)
				{
					lockedTop = planned;
					tops.Remove(planned);
				}
			}
			if (tops.Count + (lockedTop != null ? 1 : 0) <= 1)
				continue;
			fitOf.TryGetValue(pawn, out var fits);
			int Class(WorkTypeDef w)
			{
				if (crisisUrg.TryGetValue(w, out float cu) && cu > 0f)
					return 2;
				if (sitCells.Contains((pawn, w)) && sitNeeds.TryGetValue(w, out SitStaff st) && st.level <= 1)
					return 1;
				return 0;
			}
			float Sub(WorkTypeDef w)
			{
				int c = Class(w);
				if (c == 2)
					return crisisUrg[w];
				if (c == 1)
					return sitNeeds[w].workers;
				return 0f;
			}
			float Value(WorkTypeDef w)
			{
				importance.TryGetValue(w, out float imp);
				float fit = fits != null && fits.TryGetValue(w, out float f) ? f : 0f;
				return imp * fit;
			}
			WorkTypeDef keep = lockedTop ?? tops.OrderByDescending(Class).ThenByDescending(Sub).ThenByDescending(Value).First();
			string keepLabel = keep.labelShort.CapitalizeFirst();
			foreach (WorkTypeDef w in tops)
			{
				if (w == keep)
					continue;
				jobs[w] = down;
				Note(comp, pawn, w, "CF_TipOneTop".Translate(keepLabel, down));
			}
		}
		// a priority-1 situation that lost colonists: others without a priority-1 job
		foreach (var sw in sitWant)
		{
			WorkTypeDef w = sw.Key;
			int have = 0;
			foreach (var kv in plan)
				if (Effective(kv.Key, w, kv.Value, game) == 1)
					have++;
			if (have >= sw.Value || !sitPool.TryGetValue(w, out var pool))
				continue;
			foreach (Pawn p in pool)
			{
				if (have >= sw.Value)
					break;
				if (!plan.TryGetValue(p, out var jobs) || !jobs.ContainsKey(w) || jobs[w] == 1)
					continue;
				if (ModCompat.AchtungForced(p) || (game != null && game.AnyLock(p, w)))
					continue;
				if (HasTopJob(p, jobs, game, w))
					continue;
				jobs[w] = 1;
				sitCells.Add((p, w));
				have++;
				Note(comp, p, w, "CF_TipOneTopFill".Translate(string.Join(", ", sitNeeds[w].labels)));
			}
		}
	}

	// Steward 2.9.0: a colonist's own order of jobs.
	// Jobs are planned one by one, so a colonist used to end up with five or
	// six jobs on the same priority. The game then takes them left to right
	// in the Work tab: a great crafter spent the day hauling stone or growing
	// rice because those columns come first. Here every colonist's jobs are
	// sorted by priority and then by how well he fits them, and each level
	// holds at most two of them – the best-fitting ones stay on top, the rest
	// move one level down. Emergency jobs, jobs everybody does (hauling,
	// cleaning), jobs with your own level plan and locked cells are not moved.
	// 2.14.2: nobody stands around. A colonist with fewer than three real jobs
	// (besides firefighting, patient, hauling and cleaning) gets every other
	// job he can do on the lowest level. Jobs with your own maximum in My style
	// and jobs his ideology forbids are left out.
	private static void NobodyIdle(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, HashSet<WorkTypeDef> fixedTypes,
		int max, ForemanSettings settings, bool manual)
	{
		foreach (var kv in plan)
		{
			Pawn pawn = kv.Key;
			int real = 0;
			foreach (var j in kv.Value)
				if (j.Value > 0 && !fixedTypes.Contains(j.Key))
					real++;
			if (real >= 3)
				continue;
			foreach (WorkTypeDef w in kv.Value.Keys.ToList())
			{
				if (kv.Value[w] > 0 || fixedTypes.Contains(w))
					continue;
				if (manual && settings.MyRulesOn && settings.MaxFor(w) > 0)
					continue;
				// 2.18: never work his ideoligion opposes – "assign opposed" means
				// only when nobody else can do it, not as filler for an idle colonist
				if (pawn.Ideo != null && pawn.Ideo.IsWorkTypeConsideredDangerous(w))
					continue;
				kv.Value[w] = max;
			}
		}
	}

	private static void PersonalOrder(Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan,
		Dictionary<Pawn, Dictionary<WorkTypeDef, float>> fitOf, HashSet<WorkTypeDef> fixedTypes,
		int max, ForemanGameComponent game, ForemanSettings settings, ForemanMapComponent comp,
		Dictionary<WorkTypeDef, float> styleOf, HashSet<(Pawn, WorkTypeDef)> sitCells)
	{
		const int perLevel = 2;
		foreach (var kv in plan)
		{
			Pawn pawn = kv.Key;
			fitOf.TryGetValue(pawn, out var fits);
			var jobs = kv.Value
				.Where(j => j.Value > 0 && j.Value < max && !fixedTypes.Contains(j.Key)
				            && !PlanActive(j.Key)
				            && (game == null || !game.AnyLock(pawn, j.Key))
				            // 2.20: a level set by a situation stays
				            && !sitCells.Contains((pawn, j.Key)))
				.Select(j => (work: j.Key, level: j.Value, fit: fits != null && fits.TryGetValue(j.Key, out float f) ? f : 0f))
				.OrderBy(j => j.level)
				// the style's favourite jobs come first on a level, then fit
				.ThenByDescending(j => j.fit + (styleOf.TryGetValue(j.work, out float sd) ? sd * 2f : 0f))
				.ToList();
			int level = 0, used = 0;
			foreach (var j in jobs)
			{
				int target = Math.Max(j.level, level);
				if (target == level && used >= perLevel)
					target = level + 1;
				target = Math.Min(target, max - 1);
				if (target != level)
				{
					level = target;
					used = 0;
				}
				used++;
				if (target != j.level)
				{
					kv.Value[j.work] = target;
					if (comp.reasons.TryGetValue(pawn.thingIDNumber, out var why) && why.TryGetValue(j.work, out string text))
						why[j.work] = text + "\n" + "CF_TipPersonal".Translate(j.level, target);
				}
			}
		}
	}

	private static List<Candidate> Score(List<Pawn> pawns, WorkTypeDef w, WorkPolicy pol, Dictionary<StatDef, float> statAvg, ForemanGameComponent game, ForemanSettings s)
	{
		List<Candidate> list = new List<Candidate>();
		foreach (Pawn pawn in pawns)
		{
			if (pawn.WorkTypeIsDisabled(w) || IncapableByCapacities(pawn, w))
			{
				continue;
			}
			Candidate candidate = new Candidate();
			candidate.pawn = pawn;
			Candidate candidate2 = candidate;
			candidate2.opposed = pawn.Ideo != null && pawn.Ideo.IsWorkTypeConsideredDangerous(w);
			if (candidate2.opposed && !s.assignOpposed)
			{
				continue;
			}
			float num;
			if (w.relevantSkills == null || w.relevantSkills.Count == 0)
			{
				num = 0.5f;
			}
			else
			{
				float num2 = 0f;
				int num3 = 0;
				foreach (SkillDef relevantSkill in w.relevantSkills)
				{
					SkillRecord skillRecord = ((pawn.skills == null) ? null : pawn.skills.GetSkill(relevantSkill));
					if (skillRecord != null)
					{
						num2 += ((!skillRecord.TotallyDisabled) ? ((float)skillRecord.Level) : 0f);
						num3++;
						if ((int)skillRecord.passion > (int)candidate2.passion)
						{
							candidate2.passion = skillRecord.passion;
						}
					}
				}
				candidate2.skill = ((num3 <= 0) ? 0f : (num2 / (float)num3));
				num = candidate2.skill / 20f;
				if (candidate2.passion == Passion.Minor)
				{
					num += 0.15f;
				}
				else if (candidate2.passion == Passion.Major)
				{
					num += 0.3f;
				}
			}
			candidate2.statFactor = StatFactor(pawn, pol, pawns, statAvg);
			num *= candidate2.statFactor;
			candidate2.score = num;
			candidate2.preferred = game?.Prefers(pawn, w) ?? false;
			candidate2.inspired = IsInspiredFor(pawn, w);
			// 2.14.2: only a real assignment counts, not a backup on the lowest
			// level – with backups everybody had every job and stability did nothing
			int cur = pawn.workSettings.GetPriority(w);
			candidate2.incumbent = cur > 0 && cur < PriorityRange.Max;
			list.Add(candidate2);
		}
		if (list.Count == 0)
		{
			return list;
		}
		float num4 = 0.001f;
		foreach (Candidate item in list)
		{
			num4 = Mathf.Max(num4, item.score);
		}
		foreach (Candidate item2 in list)
		{
			float num5 = item2.score / num4;
			item2.score = 0.5f * item2.score + 0.5f * num5;
			if (item2.preferred)
			{
				item2.score += s.preferenceBonus;
			}
			if (item2.inspired)
			{
				item2.score += 0.5f;
			}
			if (s.stability && item2.incumbent)
			{
				item2.score += 0.1f;
			}
			if (item2.opposed)
			{
				item2.score *= 0.3f;
			}
		}
		return list;
	}

	private static float StatFactor(Pawn p, WorkPolicy pol, List<Pawn> pawns, Dictionary<StatDef, float> statAvg)
	{
		if (pol.stats.Count == 0 || pol.statWeight <= 0f)
		{
			return 1f;
		}
		float num = 0f;
		int num2 = 0;
		foreach (StatDef stat in pol.stats)
		{
			if (!statAvg.TryGetValue(stat, out var value))
			{
				float num3 = 0f;
				int num4 = 0;
				foreach (Pawn pawn in pawns)
				{
					float num5 = SafeStat(pawn, stat);
					if (num5 > 0f)
					{
						num3 += num5;
						num4++;
					}
				}
				value = (statAvg[stat] = ((num4 <= 0) ? 0f : (num3 / (float)num4)));
			}
			if (!(value <= 0f))
			{
				num += SafeStat(p, stat) / value;
				num2++;
			}
		}
		if (num2 == 0)
		{
			return 1f;
		}
		float num7 = num / (float)num2;
		return Mathf.Clamp(1f + pol.statWeight * (num7 - 1f), 0.3f, 2f);
	}

	private static float SafeStat(Pawn p, StatDef st)
	{
		try
		{
			return p.GetStatValue(st);
		}
		catch
		{
			return 0f;
		}
	}

	private static bool IncapableByCapacities(Pawn p, WorkTypeDef w)
	{
		List<WorkGiverDef> workGiversByPriority = w.workGiversByPriority;
		if (workGiversByPriority == null || workGiversByPriority.Count == 0 || p.health == null)
		{
			return false;
		}
		foreach (WorkGiverDef item in workGiversByPriority)
		{
			bool flag = true;
			if (item.requiredCapacities != null)
			{
				foreach (PawnCapacityDef requiredCapacity in item.requiredCapacities)
				{
					if (!p.health.capacities.CapableOf(requiredCapacity))
					{
						flag = false;
						break;
					}
				}
			}
			if (flag)
			{
				return false;
			}
		}
		return true;
	}

	private static bool IsInspiredFor(Pawn p, WorkTypeDef w)
	{
		InspirationDef inspirationDef = ((p.mindState == null || p.mindState.inspirationHandler == null) ? null : p.mindState.inspirationHandler.CurStateDef);
		if (inspirationDef == null)
		{
			return false;
		}
		return inspirationDef.defName switch
		{
			"Inspired_Creativity" => w.defName == "Art" || w.defName == "Crafting" || w.defName == "Smithing" || w.defName == "Tailoring" || w.defName == "Construction", 
			"Inspired_Surgery" => w.defName == "Doctor", 
			"Inspired_Taming" => w.defName == "Handling", 
			"Inspired_Recruitment" => w.defName == "Warden", 
			_ => false, 
		};
	}

	private static Candidate PickApprentice(List<Candidate> cands, Dictionary<Pawn, int> assigned, WorkTypeDef w)
	{
		float num = 0f;
		foreach (Candidate cand in cands)
		{
			if (assigned.ContainsKey(cand.pawn))
			{
				num = Mathf.Max(num, cand.skill);
			}
		}
		if (num < 6f)
		{
			return null;
		}
		Candidate candidate = null;
		foreach (Candidate cand2 in cands)
		{
			if (!assigned.ContainsKey(cand2.pawn) && !cand2.opposed && cand2.passion != 0 && !(cand2.skill > num - 4f) && (candidate == null || (int)cand2.passion > (int)candidate.passion || (cand2.passion == candidate.passion && cand2.skill > candidate.skill)))
			{
				candidate = cand2;
			}
		}
		return candidate;
	}

	private static List<Pawn> WorkMechs(Map map)
	{
		List<Pawn> list = new List<Pawn>();
		foreach (Pawn item in map.mapPawns.PawnsInFaction(Faction.OfPlayer))
		{
			if (item.RaceProps != null && item.RaceProps.IsMechanoid && !item.Downed && item.Spawned && item.RaceProps.mechEnabledWorkTypes != null && item.RaceProps.mechEnabledWorkTypes.Count > 0)
			{
				list.Add(item);
			}
		}
		return list;
	}

	private static int CountMechsFor(List<Pawn> mechs, WorkTypeDef w)
	{
		int num = 0;
		foreach (Pawn mech in mechs)
		{
			if (mech.RaceProps.mechEnabledWorkTypes.Contains(w))
			{
				num++;
			}
		}
		return num;
	}

	// 2.13: from the crisis table (CrisisRules); several crises add up.
	private static Crisiseffect CrisisFor(Crisis c, WorkTypeDef w)
	{
		Crisiseffect e = new Crisiseffect();
		if (c == Crisis.None || w == null)
			return e;
		foreach (Crisis f in CrisisRules.All)
		{
			if ((c & f) == 0 || !CrisisRules.Table.TryGetValue(f, out var t) || !t.TryGetValue(w.defName, out var x))
				continue;
			e.urgency += x.urgency;
			e.workers *= x.workers;
			e.minWorkers = Math.Max(e.minWorkers, x.min);
		}
		e.urgency = Mathf.Clamp(e.urgency, -0.5f, 0.5f);
		e.workers = Mathf.Clamp(e.workers, 0.3f, 3f);
		return e;
	}

	private static readonly Color DecisionColor = new Color(0.6f, 0.85f, 1f);

	private static string Signed(float v) => (v >= 0f ? "+" : "") + v.ToString("0.00");

	private static string Explain(Candidate c, int prio, int rank, int total, WorkTypeDef w, WorkPolicy pol, DemandInfo dem, Crisis crisis, Crisiseffect ce, ForemanStyle style, bool everyone, bool apprentice, int mechCap, bool locked, string decision)
	{
		StringBuilder stringBuilder = new StringBuilder();
		stringBuilder.AppendLine("CF_TipHeader".Translate().Colorize(ColoredText.TipSectionTitleColor));
		stringBuilder.AppendLine((prio <= 0) ? "CF_TipNotAssigned".Translate().ToString() : "CF_TipAssigned".Translate(prio).ToString());
		if (w.relevantSkills != null && w.relevantSkills.Count > 0)
		{
			string text = "CF_TipSkill".Translate(c.skill.ToString("0.#")).ToString();
			if (c.passion == Passion.Minor)
			{
				text += ", " + "CF_TipPassionMinor".Translate();
			}
			else if (c.passion == Passion.Major)
			{
				text += ", " + "CF_TipPassionMajor".Translate();
			}
			stringBuilder.AppendLine(text);
		}
		else
		{
			stringBuilder.AppendLine("CF_TipNoSkill".Translate());
		}
		if (Mathf.Abs(c.statFactor - 1f) > 0.02f)
		{
			stringBuilder.AppendLine("CF_TipSpeed".Translate(c.statFactor.ToString("0.00")));
		}
		stringBuilder.AppendLine((!everyone) ? "CF_TipRank".Translate(rank, total).ToString() : "CF_TipEveryone".Translate().ToString());
		// 2.18: the rule that decided this level
		if (!string.IsNullOrEmpty(decision))
			stringBuilder.AppendLine("CF_TipDecision".Translate(decision).Colorize(DecisionColor));
		if (dem != null && !string.IsNullOrEmpty(dem.note))
		{
			stringBuilder.AppendLine("CF_TipDemand".Translate(dem.note));
		}
		if (crisis != 0 && (ce.urgency != 0f || ce.workers != 1f || ce.minWorkers > 0))
		{
			stringBuilder.AppendLine("CF_TipCrisis".Translate(MapDemand.Label(crisis)));
		}
		if (style == ForemanStyle.Balanced)
		{
			Map m = c.pawn?.Map;
			if (m != null && SmartBalance.Delta(m, w) != 0f)
				stringBuilder.AppendLine("CF_TipBalanced".Translate(string.Join(", ", SmartBalance.Reasons(m))));
			// 2.19: "Balanced → now: Building (32 blueprints)"
			ForemanMapComponent fc = ForemanMapComponent.For(m);
			if (fc != null && AutoFocus.Delta(fc, w) != 0f)
				stringBuilder.AppendLine("CF_TipAutoFocus".Translate(AutoFocus.LongLabel(fc)));
		}
		else if (style == ForemanStyle.Custom)
		{
			stringBuilder.AppendLine("CF_TipCustom".Translate());
		}
		else if (Styles.Delta(style, w) != 0f)
		{
			stringBuilder.AppendLine("CF_TipStyle".Translate(Styles.Label(style)));
		}
		if (mechCap > 0)
		{
			stringBuilder.AppendLine("CF_TipMechs".Translate(mechCap));
		}
		if (c.preferred)
		{
			stringBuilder.AppendLine("CF_TipPreferred".Translate());
		}
		if (c.inspired)
		{
			stringBuilder.AppendLine("CF_TipInspired".Translate());
		}
		if (apprentice)
		{
			stringBuilder.AppendLine("CF_TipApprentice".Translate());
		}
		if (c.opposed)
		{
			stringBuilder.AppendLine("CF_TipOpposed".Translate());
		}
		if (locked)
		{
			stringBuilder.AppendLine("CF_TipLocked".Translate().Colorize(ColorLibrary.Gold));
		}
		stringBuilder.Append("CF_TipHint".Translate().Colorize(Color.gray));
		return stringBuilder.ToString();
	}
}

