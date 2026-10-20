using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public static class WorkPolicies
{
	private static Dictionary<WorkTypeDef, WorkPolicy> cache;

	private static List<WorkTypeDef> visible;

	public static void Invalidate()
	{
		cache = null;
	}

	public static List<WorkTypeDef> VisibleWorkTypes()
	{
		if (visible == null)
		{
			visible = new List<WorkTypeDef>();
			foreach (WorkTypeDef item in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (item != null && item.visible)
				{
					visible.Add(item);
				}
			}
			visible.Sort((WorkTypeDef a, WorkTypeDef b) => b.naturalPriority.CompareTo(a.naturalPriority));
		}
		return visible;
	}

	public static WorkPolicy Get(WorkTypeDef w)
	{
		if (cache == null)
		{
			cache = new Dictionary<WorkTypeDef, WorkPolicy>();
		}
		if (!cache.TryGetValue(w, out var value))
		{
			value = Build(w);
			cache[w] = value;
		}
		return value;
	}

	public static float DefaultUrgency(WorkTypeDef w)
	{
		return BuildDefault(w).urgency;
	}

	private static WorkPolicy Build(WorkTypeDef w)
	{
		WorkPolicy workPolicy = BuildDefault(w);
		ForemanSettings settings = ForemanMod.Settings;
		if (settings != null && settings.urgencyOverrides.TryGetValue(w.defName, out var value))
		{
			workPolicy.urgency = Mathf.Clamp01(value);
		}
		return workPolicy;
	}

	private static WorkPolicy BuildDefault(WorkTypeDef w)
	{
		WorkPolicy workPolicy = new WorkPolicy();
		switch (w.defName)
		{
		case "Firefighter":
			workPolicy.urgency = 1f;
			workPolicy.everyone = true;
			workPolicy.emergency = true;
			break;
		case "Patient":
			workPolicy.urgency = 1f;
			workPolicy.everyone = true;
			workPolicy.emergency = true;
			break;
		case "BasicWorker":
			workPolicy.urgency = 1f;
			workPolicy.everyone = true;
			workPolicy.emergency = true;
			break;
		case "PatientBedRest":
			workPolicy.urgency = 0.7f;
			workPolicy.everyone = true;
			workPolicy.emergency = true;
			break;
		case "Doctor":
			workPolicy.urgency = 1f;
			workPolicy.pinned = true;
			workPolicy.emergency = true;
			workPolicy.minWorkers = 2;
			workPolicy.share = 0.25f;
			Stats(workPolicy, "MedicalTendSpeed", "MedicalTendQuality", "MedicalSurgerySuccessChance");
			break;
		case "Childcare":
			workPolicy.urgency = 0.75f;
			workPolicy.emergency = true;
			workPolicy.share = 0.3f;
			break;
		case "Warden":
			workPolicy.urgency = 0.65f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "NegotiationAbility");
			break;
		case "Handling":
			workPolicy.urgency = 0.55f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "AnimalGatherSpeed", "AnimalGatherYield", "TameAnimalChance");
			break;
		case "Cooking":
			workPolicy.urgency = 0.75f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "CookSpeed", "ButcheryFleshSpeed");
			break;
		case "Hunting":
			workPolicy.urgency = 0.5f;
			workPolicy.minWorkers = 0;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "ShootingAccuracyPawn");
			break;
		case "Construction":
			workPolicy.urgency = 0.6f;
			Stats(workPolicy, "ConstructionSpeed", "ConstructSuccessChance");
			break;
		case "Growing":
			workPolicy.urgency = 0.6f;
			Stats(workPolicy, "PlantWorkSpeed", "PlantHarvestYield");
			break;
		case "Mining":
			workPolicy.urgency = 0.45f;
			workPolicy.share = 0.4f;
			Stats(workPolicy, "MiningSpeed", "MiningYield");
			break;
		case "PlantCutting":
			workPolicy.urgency = 0.45f;
			Stats(workPolicy, "PlantWorkSpeed");
			break;
		case "Smithing":
			workPolicy.urgency = 0.45f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "GeneralLaborSpeed");
			break;
		case "Tailoring":
			workPolicy.urgency = 0.45f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "GeneralLaborSpeed");
			break;
		case "Crafting":
			workPolicy.urgency = 0.45f;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "GeneralLaborSpeed");
			break;
		case "Art":
			workPolicy.urgency = 0.3f;
			workPolicy.minWorkers = 0;
			workPolicy.share = 0.25f;
			Stats(workPolicy, "GeneralLaborSpeed");
			break;
		// 2.14.3: Keyz' Allow Utilities – your direct orders
		case "KAU_UrgentHaul":
			workPolicy.urgency = 0.95f;
			workPolicy.everyone = true;
			break;
		case "KAU_FinishingOff":
			workPolicy.urgency = 0.85f;
			workPolicy.everyone = true;
			break;
		case "Hauling":
			workPolicy.urgency = 0.3f;
			workPolicy.everyone = true;
			workPolicy.statWeight = 1f;
			Stats(workPolicy, "CarryingCapacity", "MoveSpeed");
			break;
		case "Cleaning":
			workPolicy.urgency = 0.3f;
			workPolicy.everyone = true;
			Stats(workPolicy, "CleaningSpeed", "MoveSpeed");
			break;
		case "Research":
			workPolicy.urgency = 0.5f;
			workPolicy.share = 0.4f;
			Stats(workPolicy, "ResearchSpeed");
			break;
		case "DarkStudy":
			workPolicy.urgency = 0.35f;
			workPolicy.minWorkers = 0;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "EntityStudyRate");
			break;
		case "Fishing":
			workPolicy.urgency = 0.45f;
			workPolicy.minWorkers = 0;
			workPolicy.share = 0.3f;
			Stats(workPolicy, "FishingSpeed", "FishingYield");
			break;
		default:
			workPolicy.urgency = DerivedUrgency(w);
			if (w.relevantSkills == null || w.relevantSkills.Count == 0)
			{
				workPolicy.everyone = true;
				workPolicy.urgency = Mathf.Min(workPolicy.urgency, 0.45f);
			}
			else
			{
				workPolicy.share = 0.4f;
			}
			break;
		}
		if (workPolicy.stats.Count == 0)
		{
			Stats(workPolicy, "WorkSpeedGlobal");
		}
		return workPolicy;
	}

	private static float DerivedUrgency(WorkTypeDef w)
	{
		float num = 1f;
		float num2 = float.MaxValue;
		foreach (WorkTypeDef item in VisibleWorkTypes())
		{
			num = Mathf.Max(num, item.naturalPriority);
			num2 = Mathf.Min(num2, item.naturalPriority);
		}
		float num3 = ((!(num > num2)) ? 0.5f : (((float)w.naturalPriority - num2) / (num - num2)));
		return Mathf.Clamp(0.3f + 0.45f * num3, 0.3f, 0.75f);
	}

	private static void Stats(WorkPolicy p, params string[] names)
	{
		for (int i = 0; i < names.Length; i++)
		{
			StatDef namedSilentFail = DefDatabase<StatDef>.GetNamedSilentFail(names[i]);
			if (namedSilentFail != null)
			{
				p.stats.Add(namedSilentFail);
			}
		}
	}
}
