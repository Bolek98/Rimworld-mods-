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

	public static void Reset() => cache.Clear();

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
				if (w == main || w.modContentPack == null || w.modContentPack.IsOfficialMod)
					continue;
				if (SameSkills(w, main) && (w.workTags & main.workTags) != 0)
					list.Add(w);
			}
		}
		cache[name] = list;
		return list;
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
