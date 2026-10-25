using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// Steward 2.30: 16 more (non-critical) situations – the measures.
public static partial class SituationRules
{
	private static int StorageFull(Ctx c)
	{
		int n = 0, checkedCount = 0;
		// 1.0.5: a colonist as the carrier – storage mods (Deep Storage, PUAH …) patch this search and
		// expect one; without it the whole situation failed with an error
		Pawn carrier = c.Colonists.FirstOrDefault(p => p.Spawned && p.Map == c.map);
		if (carrier == null)
			return 0;
		foreach (Thing t in c.Haulables)
		{
			if (++checkedCount > 250)
				break;
			if (t == null || !t.Spawned || t.Map != c.map || !t.def.EverStorable(false) || !c.home[t.Position])
				continue;
			try
			{
				if (!StoreUtility.TryFindBestBetterStorageFor(t, carrier, c.map, StoragePriority.Unstored, Faction.OfPlayer, out _, out _, false))
					n++;
			}
			catch { }      // one odd item must not stop the count
		}
		return n;
	}

	private static int SowingBacklog(Ctx c)
	{
		int n = 0;
		foreach (Zone z in c.map.zoneManager.AllZones)
		{
			if (!(z is Zone_Growing g) || !g.allowSow)
				continue;
			ThingDef want = g.GetPlantDefToGrow();
			if (want == null)
				continue;
			foreach (IntVec3 cell in g.Cells)
			{
				if (cell.GetPlant(c.map) != null || !PlantUtility.GrowthSeasonNow(cell, c.map, want))
					continue;
				n++;
			}
		}
		return n;
	}

	private static int UglyRooms(Ctx c)
	{
		var seen = new HashSet<Room>();
		int n = 0;
		foreach (Pawn p in c.Colonists)
		{
			Building_Bed bed = p.ownership?.OwnedBed;
			Room r = bed?.GetRoom();
			if (r == null || r.PsychologicallyOutdoors || !seen.Add(r))
				continue;
			if (r.GetStat(RoomStatDefOf.Beauty) < -0.5f || r.GetStat(RoomStatDefOf.Cleanliness) < -1f)
				n++;
		}
		return n;
	}

	private static int BarrelsReady(Ctx c)
	{
		int n = 0;
		foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
			if (b is Building_FermentingBarrel fb && fb.Fermented)
				n++;
		return n;
	}

	private static int TextilePile(Ctx c)
	{
		bool tailoringBills = false;
		foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
		{
			if (b is IBillGiver g && g.BillStack != null && g.BillStack.AnyShouldDoNow && b.def.defName.IndexOf("Tailor", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				tailoringBills = true;
				break;
			}
		}
		if (!tailoringBills)
			return 0;
		int n = 0;
		foreach (KeyValuePair<ThingDef, int> kv in c.map.resourceCounter.AllCountedAmounts)
		{
			ThingDef d = kv.Key;
			if (d.IsStuff && d.stuffProps?.categories != null && (d.stuffProps.categories.Contains(StuffCategoryDefOf.Fabric) || d.stuffProps.categories.Contains(StuffCategoryDefOf.Leathery)))
				n += kv.Value;
		}
		return n;
	}

	private static bool? repairExists;

	private static int WornGear(Ctx c)
	{
		if (repairExists == null)
			repairExists = DefDatabase<RecipeDef>.AllDefsListForReading.Any(r => r.defName.IndexOf("Repair", StringComparison.OrdinalIgnoreCase) >= 0 && !r.IsSurgery);
		if (repairExists != true)
			return 0;
		int n = 0;
		foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon).Concat(c.map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel)))
			if (t.Spawned && t.def.useHitPoints && t.IsInAnyStorage() && t.HitPoints < t.MaxHitPoints * 0.6f)
				n++;
		return n;
	}

	private static int LordsOf<T>(Map map) where T : LordJob
	{
		int n = 0;
		foreach (Lord l in map.lordManager.lords)
			if (l?.LordJob is T)
				n++;
		return n;
	}

	// ---- 2.33: slaves and prisoners ------------------------------------------
	private static int LowSuppression(Ctx c, float below)
	{
		if (!ModsConfig.IdeologyActive)
			return 0;
		int n = 0;
		foreach (Pawn p in c.map.mapPawns.SlavesOfColonySpawned)
		{
			Need_Suppression sup = p.needs?.TryGetNeed<Need_Suppression>();
			if (sup != null && !p.Downed && sup.CurLevelPercentage < below)
				n++;
		}
		return n;
	}

	// slaves whose rebellion could start within ~a month
	private static int RebellionRisk(Ctx c)
	{
		if (!ModsConfig.IdeologyActive)
			return 0;
		int n = 0;
		foreach (Pawn p in c.map.mapPawns.SlavesOfColonySpawned)
		{
			if (p.Downed || SlaveRebellionUtility.IsRebelling(p))
				continue;
			try
			{
				float mtb = SlaveRebellionUtility.InitiateSlaveRebellionMtbDays(p);
				if (mtb > 0f && mtb < 30f)
					n++;
			}
			catch
			{
			}
		}
		return n;
	}

	private static int PrisonersHurt(Ctx c)
	{
		int n = 0;
		foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
			if (p.health != null && (p.health.HasHediffsNeedingTend() || HealthAIUtility.ShouldSeekMedicalRest(p) && p.Downed))
				n++;
		return n;
	}

	private static int PrisonRelease(Ctx c)
	{
		int n = 0;
		foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
			if (p.guest != null && !p.Downed && p.guest.ExclusiveInteractionMode == PrisonerInteractionModeDefOf.Release)
				n++;
		return n;
	}

	private static int DirtyPrison(Ctx c)
	{
		var seen = new HashSet<Room>();
		int n = 0;
		foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
		{
			Room r = p.GetRoom();
			if (r == null || !r.IsPrisonCell || !seen.Add(r))
				continue;
			if (r.GetStat(RoomStatDefOf.Cleanliness) < -1f)
				n++;
		}
		return n;
	}

	private static int MissingPrisonBeds(Ctx c)
	{
		int prisoners = c.map.mapPawns.PrisonersOfColonySpawnedCount;
		if (prisoners == 0)
			return 0;
		int beds = 0;
		foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
			if (b is Building_Bed bed && bed.ForPrisoners && !bed.Medical)
				beds += bed.SleepingSlotsCount;
		return Math.Max(0, prisoners - beds);
	}

	private static int Guests(Ctx c)
	{
		int n = 0;
		foreach (Lord l in c.map.lordManager.lords)
			if (l?.LordJob is LordJob_VisitColony && l.faction != null && !l.faction.HostileTo(Faction.OfPlayer))
				n += l.ownedPawns.Count;
		n += c.Colonists.Count(p => p.IsQuestLodger());
		return n;
	}

	private static int SnowCells(Ctx c)
	{
		Area a = c.map.areaManager.SnowOrSandClear;
		if (a == null || a.TrueCount == 0 || c.map.snowGrid == null)
			return 0;
		int n = 0;
		foreach (IntVec3 cell in a.ActiveCells)
			if (c.map.snowGrid.GetDepth(cell) > 0.3f)
				n++;
		return n;
	}

	private static int OrbitalTrade(Ctx c)
	{
		if (c.map.passingShipManager == null || c.map.passingShipManager.passingShips.Count == 0)
			return 0;
		bool beacon = c.map.listerBuildings.allBuildingsColonist.Any(b => b.def == ThingDefOf.OrbitalTradeBeacon);
		return beacon ? c.map.passingShipManager.passingShips.Count : 0;
	}

	private static int ChunksOnFields(Ctx c)
	{
		int n = 0;
		foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.Chunk))
			if (t.Spawned && c.map.zoneManager.ZoneAt(t.Position) is Zone_Growing)
				n++;
		return n;
	}

	private static Dictionary<ThingDef, WorkTypeDef> idleBenchWork;

	// benches with a bill to do that nobody is working at; out = the most common work
	private static int IdleBenches(Map map, out WorkTypeDef work)
	{
		work = null;
		if (idleBenchWork == null)
		{
			idleBenchWork = new Dictionary<ThingDef, WorkTypeDef>();
			foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
				if (g?.workType != null && g.fixedBillGiverDefs != null)
					foreach (ThingDef d in g.fixedBillGiverDefs)
						if (!idleBenchWork.ContainsKey(d))
							idleBenchWork[d] = g.workType;
		}
		var busy = new HashSet<Thing>();
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			if (p.CurJob != null && p.CurJob.bill != null && p.CurJob.targetA.Thing != null)
				busy.Add(p.CurJob.targetA.Thing);
		var count = new Dictionary<WorkTypeDef, int>();
		int n = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null || !g.BillStack.AnyShouldDoNow || busy.Contains(b))
				continue;
			if (!idleBenchWork.TryGetValue(b.def, out WorkTypeDef w))
				continue;
			n++;
			count.TryGetValue(w, out int k);
			count[w] = k + 1;
		}
		if (count.Count > 0)
			work = count.OrderByDescending(kv => kv.Value).First().Key;
		return n;
	}

	// ---- Steward 2.51: care ----

	// colonists and slaves that need a doctor now (1–2 – bigger numbers are Mass casualties / Medical crisis)
	private static int Wounded(Ctx c)
	{
		int n = 0;
		var seen = new HashSet<int>();
		void Check(IEnumerable<Pawn> pawns)
		{
			if (pawns == null) return;
			foreach (Pawn p in pawns)
			{
				if (p?.health == null || p.Dead || !seen.Add(p.thingIDNumber))
					continue;
				if (HealthAIUtility.ShouldBeTendedNowByPlayer(p) || (p.Downed && !p.InBed()))
					n++;
			}
		}
		Check(c.map.mapPawns.FreeColonistsSpawned);
		Check(c.map.mapPawns.SlavesOfColonySpawned);
		return n;
	}

	// hungry colonists/slaves in a sickbed (fed by doctors) and hungry prisoners in bed (fed by wardens)
	private static int BedHungry(Ctx c, out int colonists, out int prisoners)
	{
		colonists = prisoners = 0;
		var seen = new HashSet<int>();
		foreach (Pawn p in c.map.mapPawns.FreeColonistsSpawned.Concat(c.map.mapPawns.SlavesOfColonySpawned))
			if (p?.needs?.food != null && seen.Add(p.thingIDNumber) && FeedPatientUtility.ShouldBeFed(p) && FeedPatientUtility.IsHungry(p))
				colonists++;
		foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
			if (p?.needs?.food != null && p.guest != null && WardenFeedUtility.ShouldBeFed(p) && FeedPatientUtility.IsHungry(p))
				prisoners++;
		return colonists + prisoners;
	}

	// hungry people (colonists, slaves, prisoners) and not enough ready meals for them
	private static int HungryNoMeals(Ctx c)
	{
		int hungry = 0;
		foreach (Pawn p in c.map.mapPawns.FreeColonistsSpawned.Concat(c.map.mapPawns.SlavesOfColonySpawned).Concat(c.map.mapPawns.PrisonersOfColonySpawned))
			if (p?.needs?.food != null && !p.Dead && FeedPatientUtility.IsHungry(p))
				hungry++;
		if (hungry == 0)
			return 0;
		// a working nutrient paste dispenser feeds everybody
		foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
			if (b is Building_NutrientPasteDispenser d && d.CanDispenseNow)
				return 0;
		int meals = 0;
		foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
		{
			if (t?.def?.ingestible == null || !t.Spawned || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (t.def.ingestible.preferability < FoodPreferability.MealAwful || !t.def.ingestible.HumanEdible)
				continue;
			meals += t.stackCount;
			if (meals >= hungry)
				return 0;
		}
		return hungry - meals;
	}
}
