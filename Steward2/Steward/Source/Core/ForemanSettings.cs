using System;
using System.Collections.Generic;
using Verse;

namespace ColonyForeman;

// Steward 2.0 core settings. The names of the saved values are the same as in
// Steward 1.x, so your settings stay. Add-ons keep their own settings.
public sealed class ForemanSettings : ModSettings
{
	// a new map starts with the steward off – you switch him on in the Work tab
	public bool autoByDefault = false;

	// hours between the regular plans (calm: nobody is moved back and forth)
	public int intervalHours = 6;

	// how long a cell the steward changed stays as it is (in-game hours)
	public int holdHours = 12;

	public bool assignOpposed = true;
	public bool backupWorkers = true;
	// 1.1.0: skilled work only goes to backups with at least this skill (0 = everybody)
	public int backupMinSkill = 6;
	// 1.2.0: no work with a skill as backup below this level (unless nobody else can do it)
	public int backupHardMin = 3;
	// 1.2.0: plants, mining and animals as backup only with at least this yield (0 = off)
	public float backupMinYield = 0.75f;
	// 1.2.0: top job – the colonist's own skill against the colony's current needs (0 = like before)
	public float skillFirst = 0.6f;
	// 1.3.0: who has priority when steps want different things for the same cell (strongest first)
	public static readonly List<string> DefaultStepOrder = new List<string> { "life", "rules", "roles", "area", "crisis", "addons", "apprentice", "droids", "top", "children", "idle", "base" };
	public List<string> stepOrder = new List<string>(DefaultStepOrder);
	// 1.1.0: surgery is left to doctors with at least this Medicine when one is there (0 = off)
	public int surgeryMinMedicine = 8;
	public bool oneTopPriority = true;
	public bool includeLodgers = true;
	public bool countMechs = true;
	public bool lightWorkInjured = true;
	public bool moodPassion = true;
	public float preferenceBonus = 0.25f;

	// 0 = detect from Work Tab / PriorityMaster; 4–9 = use this many levels
	public int maxPriorityOverride = 0;

	// colony styles
	public float styleStrength = 2f;
	public bool styleStrict = true;
	public bool styleOthersOff = false;

	// children and babies
	public bool childLearning = true;
	public bool parentCare = true;
	public int learningSlots = 1;
	public int learningMaxSkill = 8;

	// priority areas and benches
	public bool priorityAreas = true;
	public int priorityAreaWorkers = 3;

	// your rules per work type: at least / at most this many colonists, importance
	public bool useMyRules = false;
	public Dictionary<string, int> workerMin = new Dictionary<string, int>();
	public Dictionary<string, int> workerMax = new Dictionary<string, int>();
	public Dictionary<string, float> urgencyOverrides = new Dictionary<string, float>();

	public bool planMessages = false;
	// 3.1.0: "Plan now" shows the changes first
	public bool planPreview = true;
	// 3.2.0
	public bool offerNewcomers = true;
	public bool weeklySummary = true;
	public bool apprentices = false;

	public int MinFor(WorkTypeDef w) => useMyRules && w != null && workerMin.TryGetValue(w.defName, out int v) ? v : 0;
	public int MaxFor(WorkTypeDef w) => useMyRules && w != null && workerMax.TryGetValue(w.defName, out int v) ? v : 0;
	public float Importance(WorkTypeDef w) => w != null && urgencyOverrides.TryGetValue(w.defName, out float v) ? v : 0.5f;

	public void SetCount(Dictionary<string, int> map, WorkTypeDef w, int v)
	{
		if (v <= 0) map.Remove(w.defName);
		else map[w.defName] = v;
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref autoByDefault, "autoByDefault2", false);
		Scribe_Values.Look(ref intervalHours, "intervalHours2", 6);
		Scribe_Values.Look(ref holdHours, "holdHours", 12);
		Scribe_Values.Look(ref assignOpposed, "assignOpposed", true);
		Scribe_Values.Look(ref backupWorkers, "backupWorkers", true);
		Scribe_Values.Look(ref backupMinSkill, "backupMinSkill", 6);
		Scribe_Values.Look(ref backupHardMin, "backupHardMin", 3);
		Scribe_Values.Look(ref backupMinYield, "backupMinYield", 0.75f);
		Scribe_Values.Look(ref skillFirst, "skillFirst", 0.6f);
		Scribe_Collections.Look(ref stepOrder, "stepOrder", LookMode.Value);
		if (stepOrder == null || stepOrder.Count == 0) stepOrder = new List<string>(DefaultStepOrder);
		// a category added in a later version goes in at its default place
		foreach (string c in DefaultStepOrder)
			if (!stepOrder.Contains(c)) stepOrder.Insert(Math.Min(stepOrder.Count, DefaultStepOrder.IndexOf(c)), c);
		Scribe_Values.Look(ref surgeryMinMedicine, "surgeryMinMedicine", 8);
		Scribe_Values.Look(ref oneTopPriority, "oneTopPriority", true);
		Scribe_Values.Look(ref includeLodgers, "includeLodgers", true);
		Scribe_Values.Look(ref countMechs, "countMechs", true);
		Scribe_Values.Look(ref lightWorkInjured, "lightWorkInjured", true);
		Scribe_Values.Look(ref moodPassion, "moodPassion", true);
		Scribe_Values.Look(ref preferenceBonus, "preferenceBonus", 0.25f);
		Scribe_Values.Look(ref maxPriorityOverride, "maxPriorityOverride", 0);
		Scribe_Values.Look(ref styleStrength, "styleStrength", 2f);
		Scribe_Values.Look(ref styleStrict, "styleStrict", true);
		Scribe_Values.Look(ref styleOthersOff, "styleOthersOff", false);
		Scribe_Values.Look(ref childLearning, "childLearning", true);
		Scribe_Values.Look(ref parentCare, "parentCare", true);
		Scribe_Values.Look(ref learningSlots, "learningSlots", 1);
		Scribe_Values.Look(ref learningMaxSkill, "learningMaxSkill", 8);
		Scribe_Values.Look(ref priorityAreas, "priorityAreas", true);
		Scribe_Values.Look(ref priorityAreaWorkers, "priorityAreaWorkers", 3);
		Scribe_Values.Look(ref useMyRules, "useMyRules2", false);
		Scribe_Collections.Look(ref workerMin, "workerMin", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref workerMax, "workerMax", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref urgencyOverrides, "urgencyOverrides", LookMode.Value, LookMode.Value);
		Scribe_Values.Look(ref planMessages, "planMessages", false);
		Scribe_Values.Look(ref planPreview, "planPreview", true);
		Scribe_Values.Look(ref offerNewcomers, "offerNewcomers", true);
		Scribe_Values.Look(ref weeklySummary, "weeklySummary", true);
		Scribe_Values.Look(ref apprentices, "apprentices", false);
		workerMin ??= new Dictionary<string, int>();
		workerMax ??= new Dictionary<string, int>();
		urgencyOverrides ??= new Dictionary<string, float>();
	}
}
