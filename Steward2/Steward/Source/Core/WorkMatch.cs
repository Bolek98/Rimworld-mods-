using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

// Add-ons name work by its vanilla name ("Doctor", "Hunting" …). Here such a
// name becomes the real work types of this game: the vanilla one (when it
// exists) plus work types mods add for the same thing – same skills and the
// same kind of work (tags). A mod that splits butchering, fishing or tree
// cutting into its own work type is covered without knowing it.
public static class WorkMatch
{
	private static readonly Dictionary<string, List<WorkTypeDef>> cache = new Dictionary<string, List<WorkTypeDef>>();

	private const WorkTags Generic = WorkTags.ManualDumb | WorkTags.ManualSkilled | WorkTags.Commoner | WorkTags.AllWork;

	public static void Reset() { cache.Clear(); splitOf = null; }

	// 1.1.2: Steward – Work Split (or any mod using the same registry) makes smaller work types out of
	// a big one; they count as their parent (always someone on surgery, styles, priority areas …)
	private static Dictionary<string, string> splitOf;

	public static string Base(WorkTypeDef w)
	{
		if (w == null)
			return null;
		if (splitOf == null)
			splitOf = AppDomain.CurrentDomain.GetData("Bolek98.WorkSplit.Parents") as Dictionary<string, string> ?? new Dictionary<string, string>();
		return splitOf.TryGetValue(w.defName, out string p) ? p : w.defName;
	}

	public static List<WorkTypeDef> Resolve(string name)
	{
		if (name == null)
			return new List<WorkTypeDef>();
		if (cache.TryGetValue(name, out var list))
			return list;
		list = new List<WorkTypeDef>();
		WorkTypeDef main = DefDatabase<WorkTypeDef>.GetNamedSilentFail(name);
		if (main != null && main.visible)
		{
			list.Add(main);
			foreach (WorkTypeDef w in WorkCatalog.Visible)
			{
				if (w == main)
					continue;
				if (Base(w) == name)
				{
					list.Add(w);
					continue;
				}
				if (Base(w) != w.defName)
					continue;   // 1.1.3: a split part belongs only to its own parent
				if (w.modContentPack == null || w.modContentPack.IsOfficialMod)
					continue;
				// 3.0.5: the shared kind of work must be a real one – "manual dumb" alone made every
				// no-skill work type (cleaning, power, recycling …) count as hauling
				// 3.2.1: and the same kind of jobs – at least one job type (work giver class) in common;
				// "social" alone made diplomacy, waiting tables and shrine care count as warden work
				if (SameSkills(w, main) && (w.workTags & main.workTags & ~Generic) != 0 && SameJobs(w, main))
					list.Add(w);
			}
		}
		cache[name] = list;
		return list;
	}

	private static bool SameJobs(WorkTypeDef a, WorkTypeDef b)
	{
		var mine = new HashSet<Type>();
		if (b.workGiversByPriority != null)
			foreach (WorkGiverDef g in b.workGiversByPriority)
				if (g.giverClass != null)
					mine.Add(g.giverClass);
		if (mine.Count == 0 || a.workGiversByPriority == null)
			return false;
		foreach (WorkGiverDef g in a.workGiversByPriority)
			for (Type t = g.giverClass; t != null && t != typeof(object); t = t.BaseType)
				if (mine.Contains(t))
					return true;
		return false;
	}

	private static bool SameSkills(WorkTypeDef a, WorkTypeDef b)
	{
		List<SkillDef> x = a.relevantSkills ?? new List<SkillDef>();
		List<SkillDef> y = b.relevantSkills ?? new List<SkillDef>();
		if (x.Count == 0 && y.Count == 0)
			return true;
		return x.Count > 0 && x.All(y.Contains);
	}

	// the work type a work giver belongs to in this game (mods move givers between types)
	public static WorkTypeDef OfGiver(string giverDefName, string fallbackWork)
	{
		WorkGiverDef g = DefDatabase<WorkGiverDef>.GetNamedSilentFail(giverDefName);
		return g?.workType ?? DefDatabase<WorkTypeDef>.GetNamedSilentFail(fallbackWork);
	}
}
