using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// small colony numbers the add-ons share
public static class ColonyStats
{
	public static int Workers(Map map) => WorkCatalog.Workers(map, ForemanMod.Settings).Count;

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

	public static float FoodDays(Map map)
	{
		int num = Eaters(map);
		if (num <= 0)
		{
			return 99f;
		}
		return FoodNutrition(map) / ((float)num * 1.6f);
	}

	public static int SpoilingNutrition(Map map)
	{
		float nutrition = 0f;
		Area_Home home = map.areaManager.Home;
		List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (t is Corpse || !t.Spawned || t.def.ingestible == null || !t.def.ingestible.HumanEdible || t.def.IsDrug)
				continue;
			FoodPreferability pref = t.def.ingestible.preferability;
			if (pref != FoodPreferability.RawBad && pref != FoodPreferability.RawTasty)
				continue;
			if (t.IsForbidden(Faction.OfPlayer) || (!t.IsInAnyStorage() && !home[t.Position]))
				continue;
			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot == null || rot.Stage != RotStage.Fresh || rot.TicksUntilRotAtCurrentTemp > 90000)
				continue;
			nutrition += t.GetStatValue(StatDefOf.Nutrition) * t.stackCount;
		}
		return Mathf.FloorToInt(nutrition);
	}
}
