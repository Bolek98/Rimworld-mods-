using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

public enum WorkKind
{
	Hidden,      // not in the Work tab
	Emergency,   // firefighting, patient, bed rest, basic work … – everyone on top
	General,     // no skill: hauling, cleaning and what mods add like them
	Skilled      // uses skills – planned by suitability
}

// Steward 2.0: everything about work types and who can do them is read from
// the game and the mods actually loaded – nothing is fixed by name except a few
// vanilla special cases. A work type a mod adds is planned like any other.
public static class WorkCatalog
{
	private static List<WorkTypeDef> visible;
	private static Dictionary<WorkTypeDef, WorkKind> kinds;
	private static Dictionary<WorkTypeDef, List<PawnCapacityDef>[]> capacities;

	private static readonly HashSet<string> EmergencyNames = new HashSet<string>
	{
		"Firefighter", "Patient", "PatientBedRest", "BasicWorker",
		// Keyz' Allow Utilities: your direct orders
		"HaulingUrgent", "FinishingOff"
	};

	public static void Reset()
	{
		visible = null;
		kinds = null;
		capacities = null;
		canCache.Clear();
	}

	public static List<WorkTypeDef> Visible
	{
		get
		{
			if (visible == null)
			{
				visible = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w != null && w.visible).ToList();
				visible.Sort((a, b) => b.naturalPriority.CompareTo(a.naturalPriority));
			}
			return visible;
		}
	}

	public static WorkKind Kind(WorkTypeDef w)
	{
		if (w == null)
			return WorkKind.Hidden;
		kinds ??= new Dictionary<WorkTypeDef, WorkKind>();
		if (!kinds.TryGetValue(w, out WorkKind k))
		{
			if (!w.visible)
				k = WorkKind.Hidden;
			else if (EmergencyNames.Contains(w.defName) || (w.workTags & WorkTags.Firefighting) != 0)
				k = WorkKind.Emergency;
			else if (w.relevantSkills == null || w.relevantSkills.Count == 0)
				k = WorkKind.General;
			else
				k = WorkKind.Skilled;
			kinds[w] = k;
		}
		return k;
	}

	// ---- who works ----------------------------------------------------------
	public static List<Pawn> Workers(Map map, ForemanSettings s)
	{
		var list = new List<Pawn>();
		if (map?.mapPawns == null)
			return list;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p?.workSettings == null || !p.workSettings.EverWork || p.Dead)
				continue;
			if (p.DevelopmentalStage.Baby())
				continue;
			if (p.IsQuestLodger() && (s == null || !s.includeLodgers))
				continue;
			list.Add(p);
		}
		list.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
		return list;
	}

	// working mechanoids that do this work (they lower how many colonists it needs)
	public static int Mechs(Map map, WorkTypeDef w)
	{
		if (!ModsConfig.BiotechActive || map?.mapPawns == null)
			return 0;
		int n = 0;
		foreach (Pawn m in map.mapPawns.SpawnedColonyMechs)
		{
			List<WorkTypeDef> wt = m.RaceProps?.mechEnabledWorkTypes;
			if (wt == null || !wt.Contains(w) || m.Downed || !m.Awake())
				continue;
			n++;
		}
		return n;
	}

	// ---- can a pawn do it ----------------------------------------------------
	private static readonly Dictionary<long, (int tick, bool can)> canCache = new Dictionary<long, (int, bool)>();

	public static bool CanDo(Pawn p, WorkTypeDef w, ForemanSettings s)
	{
		if (p?.workSettings == null || w == null)
			return false;
		int now = Find.TickManager?.TicksGame ?? 0;
		long key = ((long)p.thingIDNumber << 16) | (uint)w.index;
		if (canCache.TryGetValue(key, out var c) && now - c.tick < 250 && now >= c.tick)
			return c.can && (s == null || s.assignOpposed || !Opposed(p, w));
		bool can = !p.WorkTypeIsDisabled(w) && p.workSettings.EverWork && Capable(p, w);
		if (canCache.Count > 20000)
			canCache.Clear();
		canCache[key] = (now, can);
		return can && (s == null || s.assignOpposed || !Opposed(p, w));
	}

	public static bool Opposed(Pawn p, WorkTypeDef w)
	{
		return ModsConfig.IdeologyActive && p.Ideo != null && p.Ideo.IsWorkTypeConsideredDangerous(w);
	}

	// at least one of the work's jobs is possible with the pawn's body (sight, manipulation …)
	private static bool Capable(Pawn p, WorkTypeDef w)
	{
		List<WorkGiverDef> givers = w.workGiversByPriority;
		if (givers == null || givers.Count == 0 || p.health?.capacities == null)
			return true;
		foreach (WorkGiverDef g in givers)
		{
			bool ok = true;
			if (g.requiredCapacities != null)
				foreach (PawnCapacityDef cap in g.requiredCapacities)
					if (!p.health.capacities.CapableOf(cap))
					{
						ok = false;
						break;
					}
			if (ok)
				return true;
		}
		return false;
	}
}
