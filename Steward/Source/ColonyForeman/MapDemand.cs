using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public sealed class MapDemand
{
	public readonly Dictionary<WorkTypeDef, DemandInfo> byType = new Dictionary<WorkTypeDef, DemandInfo>();

	public float foodDays = 99f;

	public int patients;

	public int harvestable;

	public Crisis crisis;

	private static Dictionary<ThingDef, List<WorkTypeDef>> billWork;

	public DemandInfo For(WorkTypeDef w)
	{
		DemandInfo value;
		return (!byType.TryGetValue(w, out value)) ? null : value;
	}

	public static MapDemand Quick(Map map, int lastHarvestable)
	{
		MapDemand mapDemand = new MapDemand();
		mapDemand.foodDays = FoodDays(map);
		mapDemand.patients = CountPatients(map);
		mapDemand.harvestable = lastHarvestable;
		mapDemand.crisis = Modes.Mask(DetectCrisis(map, mapDemand));
		return mapDemand;
	}

	public static MapDemand Measure(Map map)
	{
		MapDemand mapDemand = new MapDemand();
		mapDemand.foodDays = FoodDays(map);
		mapDemand.patients = CountPatients(map);
		Dictionary<WorkTypeDef, int> dictionary = CountBills(map);
		foreach (WorkTypeDef item in WorkPolicies.VisibleWorkTypes())
		{
			DemandInfo demandInfo = null;
			switch (item.defName)
			{
			case "Construction":
			{
				int count = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count + map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer).Count + ConstructionMarks(map);
				demandInfo = Scaled(count, 10f, "CF_D_Construction");
				break;
			}
			case "Growing":
			{
				mapDemand.harvestable = CountGrowing(map, out var sowable);
				demandInfo = Scaled(mapDemand.harvestable + sowable, 40f, "CF_D_Growing");
				break;
			}
			case "PlantCutting":
				demandInfo = Scaled(Designations(map, DesignationDefOf.CutPlant) + Designations(map, DesignationDefOf.HarvestPlant), 15f, "CF_D_Cut");
				break;
			case "Mining":
				demandInfo = Scaled(Designations(map, DesignationDefOf.Mine), 15f, "CF_D_Mine");
				break;
			case "Hauling":
				// 2.6.5: stone chunks are not counted – marking them for hauling
				// must not make hauling look urgent.
				demandInfo = Scaled(map.listerHaulables.ThingsPotentiallyNeedingHauling().Count(t => !IsChunk(t)), 30f, "CF_D_Haul");
				break;
			case "Cleaning":
				demandInfo = Scaled(map.listerFilthInHomeArea.FilthInHomeArea.Count, 40f, "CF_D_Clean");
				break;
			case "Doctor":
				demandInfo = Scaled(mapDemand.patients, 2f, "CF_D_Doctor");
				demandInfo.factor = Mathf.Max(demandInfo.factor, 1f);
				break;
			case "Hunting":
				demandInfo = Scaled(Designations(map, DesignationDefOf.Hunt), 3f, "CF_D_Hunt");
				break;
			case "Warden":
			{
				int prisonersOfColonySpawnedCount = map.mapPawns.PrisonersOfColonySpawnedCount;
				DemandInfo demandInfo2 = new DemandInfo();
				demandInfo2.factor = ((prisonersOfColonySpawnedCount != 0) ? Mathf.Min(2f, 1f + 0.2f * (float)prisonersOfColonySpawnedCount) : 0.4f);
				demandInfo2.note = "CF_D_Warden".Translate(prisonersOfColonySpawnedCount);
				demandInfo = demandInfo2;
				break;
			}
			case "Handling":
			{
				int count2 = Designations(map, DesignationDefOf.Tame) + Designations(map, DesignationDefOf.Slaughter);
				bool flag2 = map.mapPawns.PawnsInFaction(Faction.OfPlayer).Any((Pawn p) => p.RaceProps.Animal);
				demandInfo = Scaled(count2, 3f, "CF_D_Handling");
				if (flag2)
				{
					demandInfo.factor = Mathf.Max(demandInfo.factor, 0.9f);
				}
				break;
			}
			case "Research":
			{
				bool flag = Find.ResearchManager != null && Find.ResearchManager.GetProject() != null;
				DemandInfo demandInfo2 = new DemandInfo();
				demandInfo2.factor = ((!flag) ? 0.4f : 1f);
				demandInfo2.note = ((!flag) ? "CF_D_ResearchOff" : "CF_D_ResearchOn").Translate();
				demandInfo = demandInfo2;
				break;
			}
			}
			if (dictionary.TryGetValue(item, out var value))
			{
				DemandInfo demandInfo3 = Scaled(value, 3f, "CF_D_Bills");
				if (item.defName == "Cooking")
				{
					demandInfo3 = Combine(demandInfo3, FoodInfo(mapDemand.foodDays));
				}
				if (demandInfo == null || demandInfo3.factor > demandInfo.factor)
				{
					demandInfo = demandInfo3;
				}
			}
			else if (item.defName == "Cooking")
			{
				demandInfo = FoodInfo(mapDemand.foodDays);
			}
			if (demandInfo != null)
			{
				mapDemand.byType[item] = demandInfo;
			}
		}
		mapDemand.crisis = Modes.Mask(DetectCrisis(map, mapDemand));
		return mapDemand;
	}

	// Steward 2.11: deconstruct, uninstall, smoothing and floor removal are
	// construction work too. Before they were not counted, so with only these
	// marks Construction looked idle (factor 0.5) and got half the colonists
	// at a lower priority – nobody went to deconstruct.
	private static List<DesignationDef> constructionMarkDefs;

	public static int ConstructionMarks(Map map)
	{
		if (constructionMarkDefs == null)
		{
			constructionMarkDefs = new List<DesignationDef>();
			foreach (string n in new[] { "Deconstruct", "Uninstall", "SmoothFloor", "SmoothWall", "RemoveFloor", "ReplaceFloor" })
			{
				DesignationDef d = DefDatabase<DesignationDef>.GetNamedSilentFail(n);
				if (d != null)
					constructionMarkDefs.Add(d);
			}
		}
		int n2 = 0;
		foreach (DesignationDef d in constructionMarkDefs)
			n2 += map.designationManager.SpawnedDesignationsOfDef(d).Count();
		return n2;
	}

	private static DemandInfo Scaled(int count, float normal, string key)
	{
		float factor = ((count > 0) ? Mathf.Min(2f, 0.8f + 0.6f * Mathf.Log(1f + (float)count / normal, 2f)) : 0.5f);
		DemandInfo demandInfo = new DemandInfo();
		demandInfo.factor = factor;
		demandInfo.note = key.Translate(count);
		return demandInfo;
	}

	private static DemandInfo FoodInfo(float days)
	{
		float factor = ((days < 1f) ? 2f : ((days < 3f) ? 1.5f : ((!(days < 6f)) ? 0.8f : 1.1f)));
		DemandInfo demandInfo = new DemandInfo();
		demandInfo.factor = factor;
		demandInfo.note = "CF_D_Food".Translate(days.ToString("0.0"));
		return demandInfo;
	}

	private static DemandInfo Combine(DemandInfo a, DemandInfo b)
	{
		DemandInfo demandInfo = new DemandInfo();
		demandInfo.factor = Mathf.Max(a.factor, b.factor);
		demandInfo.note = a.note + ", " + b.note;
		return demandInfo;
	}

	private static int Designations(Map map, DesignationDef def)
	{
		return (def != null) ? map.designationManager.SpawnedDesignationsOfDef(def).Count() : 0;
	}

	public static bool IsChunk(Thing t)
	{
		return t?.def?.thingCategories != null && t.def.thingCategories.Any(c => c.defName.Contains("Chunk"));
	}

	public static int Eaters(Map map)
	{
		if (map == null)
			return 0;
		int n = map.mapPawns.FreeColonistsSpawnedCount + map.mapPawns.PrisonersOfColonySpawnedCount + map.mapPawns.SlavesOfColonySpawned.Count;
		// 2.15: guests staying with the colony (quest lodgers, visitors you host) eat too
		foreach (Pawn p in map.mapPawns.AllHumanlikeSpawned)
			if (p.HostFaction == Faction.OfPlayer && p.guest != null && p.guest.GuestStatus == GuestStatus.Guest && !p.IsPrisoner && !p.IsSlave)
				n++;
		return n;
	}

	// 2.15: one food count for everything (crisis, emergency, Balanced, work
	// planner): human food in storage plus what lies in the home area, not
	// forbidden and not rotten. Before, some parts counted only storage and
	// a crisis could run with meals lying next to the stove.
	public static float FoodNutrition(Map map)
	{
		float sum = 0f;
		Area_Home home = map.areaManager.Home;
		List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (t is Corpse || !t.Spawned || t.def.ingestible == null || !t.def.IsNutritionGivingIngestible || !t.def.ingestible.HumanEdible)
				continue;
			if (t.def.IsDrug || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (!t.IsInAnyStorage() && !home[t.Position])
				continue;
			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot != null && rot.Stage != RotStage.Fresh)
				continue;
			sum += t.GetStatValue(StatDefOf.Nutrition) * t.stackCount;
		}
		return sum;
	}

	// 2.15: middle of the colony (average position of the colonists)
	public static IntVec3 BaseCenter(Map map)
	{
		var cs = map.mapPawns.FreeColonistsSpawned;
		if (cs.Count == 0)
			return map.Center;
		long x = 0, z = 0;
		foreach (Pawn p in cs)
		{
			x += p.Position.x;
			z += p.Position.z;
		}
		return new IntVec3((int)(x / cs.Count), 0, (int)(z / cs.Count));
	}

	public static int Workers(Map map)
	{
		if (map == null)
			return 0;
		return map.mapPawns.FreeColonistsSpawnedCount + map.mapPawns.SlavesOfColonySpawned.Count;
	}

	public static float FoodDays(Map map)
	{
		int num = Eaters(map);
		if (num <= 0)
		{
			return 99f;
		}
		return FoodNutrition(map) / ((float)num * 1.6f);
	}

	public static int CountPatients(Map map)
	{
		int num = 0;
		var seen = new HashSet<int>();
		Count(map.mapPawns.FreeColonistsAndPrisonersSpawned);
		Count(map.mapPawns.SlavesOfColonySpawned);
		return num;
		void Count(IEnumerable<Pawn> pawns)
		{
			if (pawns == null)
				return;
			foreach (Pawn item in pawns)
			{
				if (item?.health == null || !seen.Add(item.thingIDNumber))
					continue;
				if (HealthAIUtility.ShouldBeTendedNowByPlayer(item) || item.health.surgeryBills.AnyShouldDoNow
				    || (item.Downed && !item.InBed() && !item.IsPrisonerOfColony))   // 2.27.0: lying hurt outside a bed
					num++;
			}
		}
	}

	private static int CountGrowing(Map map, out int sowable)
	{
		int num = 0;
		sowable = 0;
		foreach (Zone allZone in map.zoneManager.AllZones)
		{
			if (!(allZone is Zone_Growing zone_Growing))
			{
				continue;
			}
			ThingDef plantDefToGrow = zone_Growing.GetPlantDefToGrow();
			foreach (IntVec3 cell in zone_Growing.Cells)
			{
				Plant plant = cell.GetPlant(map);
				if (plant == null)
				{
					if (zone_Growing.allowSow && plantDefToGrow != null)
					{
						sowable++;
					}
				}
				else if (plant.HarvestableNow && plant.def == plantDefToGrow)
				{
					num++;
				}
			}
		}
		return num;
	}

	private static Dictionary<WorkTypeDef, int> CountBills(Map map)
	{
		if (billWork == null)
		{
			billWork = new Dictionary<ThingDef, List<WorkTypeDef>>();
			foreach (WorkGiverDef item in DefDatabase<WorkGiverDef>.AllDefsListForReading)
			{
				if (item == null || item.workType == null || item.fixedBillGiverDefs == null)
				{
					continue;
				}
				foreach (ThingDef fixedBillGiverDef in item.fixedBillGiverDefs)
				{
					if (!billWork.TryGetValue(fixedBillGiverDef, out var value))
					{
						value = (billWork[fixedBillGiverDef] = new List<WorkTypeDef>());
					}
					if (!value.Contains(item.workType))
					{
						value.Add(item.workType);
					}
				}
			}
		}
		Dictionary<WorkTypeDef, int> dictionary = new Dictionary<WorkTypeDef, int>();
		foreach (Building item2 in map.listerBuildings.allBuildingsColonist)
		{
			if (!(item2 is IBillGiver { BillStack: not null } billGiver) || !billWork.TryGetValue(item2.def, out var value2))
			{
				continue;
			}
			int num = 0;
			foreach (Bill item3 in billGiver.BillStack)
			{
				if (item3 != null && item3.ShouldDoNow())
				{
					num++;
				}
			}
			if (num != 0)
			{
				for (int i = 0; i < value2.Count; i++)
				{
					dictionary.TryGetValue(value2[i], out var value3);
					dictionary[value2[i]] = value3 + num;
				}
			}
		}
		return dictionary;
	}

	// 2.13: the crisis rules and their detection live in CrisisRules.
	private static Crisis DetectCrisis(Map map, MapDemand d)
	{
		ForemanSettings settings = ForemanMod.Settings;
		if (settings == null || !settings.useCrisis)
			return Crisis.None;
		return CrisisRules.Detect(map, d);
	}

	public static string Label(Crisis c)
	{
		var list = new List<string>();
		foreach (Crisis f in CrisisRules.All)
			if ((c & f) != 0)
				list.Add(CrisisRules.Label(f));
		return string.Join(", ", list);
	}
}
