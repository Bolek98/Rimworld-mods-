using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// Steward 2.35: how many colonists each work needs right now – counted from the
// map (benches with bills, things to haul, dirt, blueprints, plants, prisoners …)
// and the size of the colony. -1 = not measured, the old share rule decides.
public static class Workload
{
	private static Map lastMap;
	private static int lastTick = -1;
	private static readonly Dictionary<WorkTypeDef, int> cache = new Dictionary<WorkTypeDef, int>();

	public static int Wanted(Map map, WorkTypeDef w, int colonists)
	{
		if (map == null || w == null)
			return -1;
		int now = Find.TickManager.TicksGame;
		if (map != lastMap || now != lastTick)
		{
			cache.Clear();
			lastMap = map;
			lastTick = now;
		}
		if (cache.TryGetValue(w, out int v))
			return v;
		try { v = Measure(map, w, colonists); }
		catch { v = -1; }
		if (v >= 0)
			v = Math.Min(v, Math.Max(1, colonists));
		cache[w] = v;
		return v;
	}

	private static int Ceil(float f) => Mathf.CeilToInt(f);

	private static int Measure(Map map, WorkTypeDef w, int colonists)
	{
		// work at benches: one colonist per bench that has a bill to do now,
		// one more when the benches are buried in bills
		HashSet<ThingDef> benchDefs = BenchDefs(w);
		if (benchDefs.Count > 0)
		{
			int benches = 0, bills = 0;
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (!benchDefs.Contains(b.def) || !(b is IBillGiver g) || g.BillStack == null)
					continue;
				int n = 0;
				foreach (Bill bill in g.BillStack)
					if (bill.ShouldDoNow())
						n++;
				if (n > 0)
				{
					benches++;
					bills += n;
				}
			}
			if (benches == 0)
				return 0;
			return benches + (bills > benches * 3 ? 1 : 0);
		}
		DesignationManager dm = map.designationManager;
		switch (w.defName)
		{
		case "Hauling":
		{
			int n = map.listerHaulables.ThingsPotentiallyNeedingHauling().Count;
			return n == 0 ? 0 : Math.Max(1, Math.Min(Ceil(colonists * 0.6f), Ceil(n / 40f)));
		}
		case "Cleaning":
		{
			int n = map.listerFilthInHomeArea.FilthInHomeArea.Count;
			return n == 0 ? 0 : Math.Max(1, Math.Min(Ceil(colonists * 0.5f), Ceil(n / 50f)));
		}
		case "Construction":
		{
			int n = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count
			        + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count
			        + MapDemand.ConstructionMarks(map)   // 2.52.3: smoothing, uninstall, floors too
			        + map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer).Count / 2;
			return n == 0 ? 0 : Math.Max(1, Ceil(n / 15f));
		}
		case "Mining":
		{
			int n = dm.SpawnedDesignationsOfDef(DesignationDefOf.Mine).Count();
			return n == 0 ? 0 : Math.Max(1, Ceil(n / 12f));
		}
		case "PlantCutting":
		{
			int n = dm.SpawnedDesignationsOfDef(DesignationDefOf.CutPlant).Count() + dm.SpawnedDesignationsOfDef(DesignationDefOf.HarvestPlant).Count();
			return n == 0 ? 0 : Math.Max(1, Ceil(n / 20f));
		}
		case "Growing":
		{
			int cells = 0, ready = 0;
			foreach (Zone z in map.zoneManager.AllZones)
			{
				if (!(z is Zone_Growing g))
					continue;
				ThingDef want = g.GetPlantDefToGrow();
				foreach (IntVec3 c in g.Cells)
				{
					cells++;
					Plant p = c.GetPlant(map);
					// 2.35.7: an empty cell only counts when it can be sown now
					if (p == null)
					{
						if (g.allowSow && want != null && PlantUtility.GrowthSeasonNow(c, map, want))
							ready++;
					}
					else if (p.HarvestableNow && p.LifeStage == PlantLifeStage.Mature && g.allowCut)
						ready++;
				}
			}
			if (cells == 0)
				return 0;
			// sowing / harvesting waiting counts more than just tending;
			// 2.35.7: at most a third of the colony (more only in a crisis / situation)
			return Math.Max(1, Math.Min(Math.Max(1, Ceil(colonists / 3f)), Ceil(cells / 300f) + Ceil(ready / 80f)));
		}
		case "Hunting":
		{
			int n = dm.SpawnedDesignationsOfDef(DesignationDefOf.Hunt).Count();
			return n == 0 ? 0 : Math.Max(1, Ceil(n / 3f));
		}
		case "Handling":
		{
			int animals = map.mapPawns.SpawnedColonyAnimals.Count;
			int tame = dm.SpawnedDesignationsOfDef(DesignationDefOf.Tame).Count();
			return animals + tame == 0 ? 0 : Math.Max(1, Ceil(animals / 12f) + Ceil(tame / 3f));
		}
		case "Warden":
		{
			int n = map.mapPawns.PrisonersOfColonySpawnedCount + (ModsConfig.IdeologyActive ? map.mapPawns.SlavesOfColonySpawned.Count : 0);
			return n == 0 ? 0 : Math.Max(1, Ceil(n / 4f));
		}
		case "Doctor":
		{
			int n = 0;
			foreach (Pawn p in map.mapPawns.FreeColonistsAndPrisonersSpawned)
				if (p.health != null && (p.health.HasHediffsNeedingTend() || (p.Downed && !p.InBed() && !p.DevelopmentalStage.Baby())))
					n++;
			// always one doctor on call
			return Math.Max(1, n);
		}
		case "Research":
		{
			if (Find.ResearchManager?.GetProject() == null)
				return 0;
			int benches = map.listerBuildings.allBuildingsColonist.Count(b => b is Building_ResearchBench);
			return Math.Max(1, Math.Min(benches, Ceil(colonists / 3f)));
		}
		}
		return -1;
	}

	private static readonly Dictionary<WorkTypeDef, HashSet<ThingDef>> benchCache = new Dictionary<WorkTypeDef, HashSet<ThingDef>>();

	private static HashSet<ThingDef> BenchDefs(WorkTypeDef w)
	{
		if (benchCache.TryGetValue(w, out var set))
			return set;
		set = new HashSet<ThingDef>();
		if (w.workGiversByPriority != null)
			foreach (WorkGiverDef g in w.workGiversByPriority)
				if (g.giverClass != null && typeof(WorkGiver_DoBill).IsAssignableFrom(g.giverClass) && g.fixedBillGiverDefs != null)
					foreach (ThingDef d in g.fixedBillGiverDefs)
						set.Add(d);
		benchCache[w] = set;
		return set;
	}
}
