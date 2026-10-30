using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public static class MasterBillsRule
{
	private static SuppliesSettings S => SuppliesMod.S;

	// ---- 5. master crafter on quality bills -------------------------------
	private static Dictionary<ThingDef, WorkTypeDef> benchWork;

	public static WorkTypeDef WorkAt(ThingDef bench)
	{
		if (benchWork == null)
		{
			benchWork = new Dictionary<ThingDef, WorkTypeDef>();
			foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
				if (g?.workType != null && g.fixedBillGiverDefs != null)
					foreach (ThingDef d in g.fixedBillGiverDefs)
						if (!benchWork.ContainsKey(d))
							benchWork[d] = g.workType;
		}
		benchWork.TryGetValue(bench, out WorkTypeDef w);
		return w;
	}

	// 2.52.3: the minimum skill the steward last wrote – a different value means you changed it yourself
	private static readonly Dictionary<string, int> wroteMin = new Dictionary<string, int>();

	public static void ResetGame() => wroteMin.Clear();

	public static void MasterBills(ForemanMapComponent comp, Map map)
	{
		bool on = S != null && S.masterBills;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null)
				continue;
			WorkTypeDef work = WorkAt(b.def);
			foreach (Bill bill in g.BillStack)
			{
				if (!(bill is Bill_Production bp) || bp.recipe == null)
					continue;
				string id = bp.GetUniqueLoadID();
				bool ours = SupState.Of(comp).masterBills.Contains(id);
				// 2.32.5: your minimum of 2+ colonists for this work wins – nobody is
				// bound to one colonist then
				if (!on || (work != null && ForemanMod.Settings != null && ForemanMod.Settings.MinFor(work) >= 2))
				{
					if (ours)
					{
						bp.allowedSkillRange = new IntRange(0, bp.allowedSkillRange.max);
						SupState.Of(comp).masterBills.Remove(id);
					}
					continue;
				}
				ThingDef prod = bp.recipe.ProducedThingDef;
				if (prod == null || bp.recipe.workSkill == null || work == null || !(prod.IsWeapon || prod.IsApparel) || !prod.HasComp(typeof(CompQuality)))
					continue;
				// 2.52.3: a colonist, slaves-only or mechs-only choice is yours – the bill is left alone for good
				if (bp.PawnRestriction != null || bp.SlavesOnly || bp.MechsOnly || bp.NonMechsOnly)
				{
					if (ours)
						SupState.Of(comp).masterBills.Remove(id);
					continue;
				}
				// 2.50.4: the best crafters share it: skill at most 3 below the best
				List<int> levels = map.mapPawns.FreeColonistsSpawned
					.Where(p => !p.WorkTypeIsDisabled(work) && p.workSettings != null && p.workSettings.WorkIsActive(work) && !p.Downed)
					.Select(p => p.skills?.GetSkill(bp.recipe.workSkill)?.Level ?? 0).OrderByDescending(x => x).ToList();
				if (levels.Count == 0)
					continue;
				// 2.52.3: always at least two who may do it (before one crafter far ahead locked it to him alone),
				// and never above your maximum
				int min = Math.Max(0, levels[0] - 3);
				if (levels.Count >= 2)
					min = Math.Min(min, levels[1]);
				min = Math.Min(min, bp.allowedSkillRange.max);
				if (!ours && bp.allowedSkillRange.min != 0)
					continue;   // your own skill range
				if (ours && wroteMin.TryGetValue(id, out int wrote) && bp.allowedSkillRange.min != wrote)
				{
					SupState.Of(comp).masterBills.Remove(id);   // you changed the range – yours from now on
					wroteMin.Remove(id);
					continue;
				}
				if (bp.allowedSkillRange.min != min)
					bp.allowedSkillRange = new IntRange(min, bp.allowedSkillRange.max);
				if (wroteMin.Count > 2000)
					wroteMin.Clear();
				wroteMin[id] = min;
				if (!ours)
					SupState.Of(comp).masterBills.Add(id);
			}
		}
	}

}
