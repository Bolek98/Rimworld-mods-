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

	public bool assignOpposed = false;
	public bool backupWorkers = true;
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
		Scribe_Values.Look(ref assignOpposed, "assignOpposed", false);
		Scribe_Values.Look(ref backupWorkers, "backupWorkers", true);
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
		workerMin ??= new Dictionary<string, int>();
		workerMax ??= new Dictionary<string, int>();
		urgencyOverrides ??= new Dictionary<string, float>();
	}
}
