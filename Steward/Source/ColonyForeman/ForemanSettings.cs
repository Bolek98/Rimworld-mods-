using System.Collections.Generic;
using Verse;

namespace ColonyForeman;

public sealed class ForemanSettings : ModSettings
{
	public bool autoByDefault = true;

	// 2.45: 0 Simple, 1 Recommended, 2 Advanced. A new install starts on
	// Recommended; settings from an older version stay Advanced (missing key).
	public int mode = 1;
	// your Advanced values while a calm mode runs ("name=value;…")
	public string advancedValues = "";
	// 2.49.8: your own values in the Recommended mode (it is no longer locked)
	public string recommendedValues = "";

	public bool useDemand = true;

	public bool useCrisis = true;

	public bool crisisMessages = true;
	public bool autoMessages = true;

	public bool apprentices = true;

	public bool stability = true;

	public bool countMechs = true;

	public bool assignOpposed = true;

	public bool coverage = true;

	public int intervalHours = 4;

	public float preferenceBonus = 0.25f;

	public float specialistShare = 0.5f;

	public Dictionary<string, float> urgencyOverrides = new Dictionary<string, float>();

	// Supplies: a message whenever the foreman marks something.
	public bool supplyMessages = true;

	// 0 = detect from Work Tab / PriorityMaster; 4–9 = use this many levels.
	public int maxPriorityOverride = 0;

	// 2.7: colonists who are not picked for a job keep it at the lowest
	// priority as a backup (instead of having it switched off).
	public bool backupWorkers = true;

	public bool usePickUpAndHaul = true;

	// 2.13: crises switched off in the settings (bits of Crisis)
	public long crisisOff;   // 2.29: long – more than 31 crises

	// Steward 2.10: how strongly a colony style changes the plan (1 = as in
	// 2.9 and before, 2 = default, 3 = very strong).
	public float styleStrength = 2f;

	// 2.40: a style plans only its own work; the rest goes to the lowest level (or off)
	public bool styleStrict = true;
	public bool styleOthersOff = false;

	// 2.19: Balanced picks a focus style by itself (new key – on for everyone)
	public bool balancedAutoFocus = true;

	// 2.20: situations (Balanced) – on for everyone; switched-off ones by name
	public bool useSituations = true;
	public List<string> situationOff = new List<string>();

	// 2.21: a colonist keeps at most one job on priority 1 (new key – on for everyone)
	public bool oneTopPriority = true;
	// 2.32.4: switching the style also sets its recommended values
	public bool recPerStyle = true;
	// 2.33.1: urgent hauling for a few colonists only
	public bool urgentHaulFew = true;
	public bool childLearning = true;
	public bool parentCare = true;   // 2.48
	public bool noStorageAlert = true;
	// 2.35: the steward counts the workers itself from the workload; your level
	// plan and min / max apply only when you switch them on
	public bool autoStaff = true;
	public bool useMyRules = false;
	// 2.25.0: guests from quests (lodgers) who can work are planned too
	public bool includeLodgers = true;
	// 2.27.0: free doctors go to patients who need help at once
	public bool medicalResponse = true;
	public bool fireResponse = true;   // 2.39
	public bool sitToEat = true;       // 2.46
	public bool quietMessages = false; // 2.46.1 (opt-in since 2.46.2)
	public bool scheduleCare = true;   // 2.39
	// 2.32: extras
	public bool raidSafeArea = true;
	public bool lightWorkInjured = true;
	public bool moodPassion = true;
	public int learningSlots = 1;
	public int learningMaxSkill = 8;
	public bool masterBills = false;
	public bool autoSchedule = true;
	public bool nightShift;
	public bool skillAlert = true;
	public List<PriorityProfile> profiles = new List<PriorityProfile>();

	// 2.22: priority areas (Orders menu) – how many colonists go to each area (0 = everyone free)
	public bool priorityAreas = true;
	public int priorityAreaWorkers = 3;
	// 2.7: per work type, how many colonists at least / at most (0 = automatic).
	public Dictionary<string, int> workerMin = new Dictionary<string, int>();
	public Dictionary<string, int> workerMax = new Dictionary<string, int>();

	// Steward 2.8: per work type, colonists on priority 1, 2, 3 … ("1,2,3").
	public Dictionary<string, string> levelPlan = new Dictionary<string, string>();

	public int[] PlanFor(WorkTypeDef w, int levels)
	{
		if (w == null || !levelPlan.TryGetValue(w.defName, out string text) || string.IsNullOrEmpty(text))
			return null;
		var result = new int[levels];
		string[] parts = text.Split(',');
		int sum = 0;
		for (int i = 0; i < levels && i < parts.Length; i++)
		{
			int.TryParse(parts[i], out result[i]);
			sum += result[i];
		}
		return sum > 0 ? result : null;
	}

	public void SetPlan(WorkTypeDef w, int[] plan)
	{
		if (plan == null) levelPlan.Remove(w.defName);
		else levelPlan[w.defName] = string.Join(",", plan);
	}

	public int MinFor(WorkTypeDef w) => w != null && workerMin.TryGetValue(w.defName, out int v) ? v : 0;
	public int MaxFor(WorkTypeDef w) => w != null && workerMax.TryGetValue(w.defName, out int v) ? v : 0;

	public void SetCount(Dictionary<string, int> map, WorkTypeDef w, int v)
	{
		if (v <= 0) map.Remove(w.defName);
		else map[w.defName] = v;
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref autoByDefault, "autoByDefault", defaultValue: true);
		Scribe_Values.Look(ref mode, "mode", 2);
		Scribe_Values.Look(ref advancedValues, "advancedValues", "");
		Scribe_Values.Look(ref recommendedValues, "recommendedValues", "");
		Scribe_Values.Look(ref useDemand, "useDemand", defaultValue: true);
		Scribe_Values.Look(ref useCrisis, "useCrisis", defaultValue: true);
		Scribe_Values.Look(ref crisisMessages, "crisisMessages", defaultValue: true);
		Scribe_Values.Look(ref autoMessages, "autoMessages", defaultValue: true);
		Scribe_Values.Look(ref apprentices, "apprentices", defaultValue: true);
		Scribe_Values.Look(ref stability, "stability", defaultValue: true);
		Scribe_Values.Look(ref countMechs, "countMechs", defaultValue: true);
		Scribe_Values.Look(ref assignOpposed, "assignOpposed", defaultValue: true);
		Scribe_Values.Look(ref coverage, "coverage", defaultValue: true);
		Scribe_Values.Look(ref intervalHours, "intervalHours", 4);
		Scribe_Values.Look(ref preferenceBonus, "preferenceBonus", 0.25f);
		Scribe_Values.Look(ref specialistShare, "specialistShare", 0.5f);
		Scribe_Values.Look(ref supplyMessages, "supplyMessages", defaultValue: true);
		Scribe_Values.Look(ref maxPriorityOverride, "maxPriorityOverride", 0);
		Scribe_Collections.Look(ref urgencyOverrides, "urgencyOverrides", LookMode.Value, LookMode.Value);
		Scribe_Values.Look(ref backupWorkers, "backupWorkers", defaultValue: true);
		Scribe_Values.Look(ref usePickUpAndHaul, "usePickUpAndHaul", defaultValue: true);
		Scribe_Values.Look(ref crisisOff, "crisisOff", 0);
		Scribe_Values.Look(ref styleStrength, "styleStrength", 2f);
		Scribe_Values.Look(ref styleStrict, "styleStrict", true);
		Scribe_Values.Look(ref styleOthersOff, "styleOthersOff", false);
		Scribe_Values.Look(ref balancedAutoFocus, "balancedAutoFocus", defaultValue: true);
		Scribe_Values.Look(ref useSituations, "useSituations", defaultValue: true);
		Scribe_Collections.Look(ref situationOff, "situationOff", LookMode.Value);
		Scribe_Values.Look(ref oneTopPriority, "oneTopPriority", defaultValue: true);
		Scribe_Values.Look(ref recPerStyle, "recPerStyle", defaultValue: true);
		Scribe_Values.Look(ref urgentHaulFew, "urgentHaulFew", defaultValue: true);
		Scribe_Values.Look(ref childLearning, "childLearning", defaultValue: true);
		Scribe_Values.Look(ref parentCare, "parentCare", defaultValue: true);
		Scribe_Values.Look(ref noStorageAlert, "noStorageAlert", defaultValue: true);
		Scribe_Values.Look(ref autoStaff, "autoStaff", defaultValue: true);
		Scribe_Values.Look(ref useMyRules, "useMyRules2", defaultValue: false);
		Scribe_Values.Look(ref includeLodgers, "includeLodgers", true);
		Scribe_Values.Look(ref medicalResponse, "medicalResponse", true);
		Scribe_Values.Look(ref fireResponse, "fireResponse", true);
		Scribe_Values.Look(ref sitToEat, "sitToEat", true);
		Scribe_Values.Look(ref quietMessages, "quietMessages2", false);
		Scribe_Values.Look(ref scheduleCare, "scheduleCare", true);
		Scribe_Values.Look(ref raidSafeArea, "raidSafeArea", true);
		Scribe_Values.Look(ref lightWorkInjured, "lightWorkInjured", true);
		Scribe_Values.Look(ref moodPassion, "moodPassion", true);
		Scribe_Values.Look(ref learningSlots, "learningSlots", 1);
		Scribe_Values.Look(ref learningMaxSkill, "learningMaxSkill", 8);
		Scribe_Values.Look(ref masterBills, "masterBills", false);
		Scribe_Values.Look(ref autoSchedule, "autoSchedule", true);
		Scribe_Values.Look(ref nightShift, "nightShift", false);
		Scribe_Values.Look(ref skillAlert, "skillAlert", true);
		Scribe_Collections.Look(ref profiles, "profiles", LookMode.Deep);
		if (profiles == null) profiles = new List<PriorityProfile>();
		Scribe_Values.Look(ref priorityAreas, "priorityAreas", defaultValue: true);
		Scribe_Values.Look(ref priorityAreaWorkers, "priorityAreaWorkers", 3);
		if (situationOff == null) situationOff = new List<string>();
		Scribe_Collections.Look(ref workerMin, "workerMin", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref workerMax, "workerMax", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref levelPlan, "levelPlan", LookMode.Value, LookMode.Value);
		if (levelPlan == null) levelPlan = new Dictionary<string, string>();
		if (workerMin == null) workerMin = new Dictionary<string, int>();
		if (workerMax == null) workerMax = new Dictionary<string, int>();
		if (urgencyOverrides == null)
		{
			urgencyOverrides = new Dictionary<string, float>();
		}
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			WorkPolicies.Invalidate();
		}
	}
}
