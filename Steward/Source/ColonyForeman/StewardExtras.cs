using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Alert: a supply target is missing and there is nothing to mark, or hunting
// is needed but nobody can hunt with a ranged weapon.
// ---------------------------------------------------------------------------
public class Alert_StewardSupplies : Alert
{
	public Alert_StewardSupplies()
	{
		defaultLabel = "CF_A_Label".Translate();
		defaultPriority = AlertPriority.Medium;
	}

	private IEnumerable<string> Problems()
	{
		foreach (Map map in Find.Maps)
		{
			SupplyManager sm = SupplyManager.For(map);
			if (sm == null || !sm.enabled || !map.IsPlayerHome)
				continue;
			foreach (SupplyJob j in sm.AllJobs())
				if (j.enabled && j.noSources)
					yield return "CF_A_NoSources".Translate(SupplyManager.JobLabel(j));
			if (sm.huntNoRanged && (sm.hunting.enabled || sm.emergencyActive))
				yield return "CF_A_NoHunter".Translate();
		}
	}

	public override TaggedString GetExplanation()
	{
		return "CF_A_Explanation".Translate() + "\n\n" + string.Join("\n", Problems().Select(p => "- " + p));
	}

	public override AlertReport GetReport()
	{
		return Problems().Any() ? AlertReport.Active : AlertReport.Inactive;
	}
}

// ---------------------------------------------------------------------------
// Tidying the home area. Things in the home area are hauled to storage by the
// game anyway. When there is no room for them anywhere, a colonist carries
// them out: to the dump area if one is set, otherwise to the nearest free
// cell outside the home area (no zone, not in the way).
// ---------------------------------------------------------------------------
public class WorkGiver_StewardTidyHome : WorkGiver_Scanner
{
	public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

	public override Danger MaxPathDanger(Pawn pawn) => Danger.Some;

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		Map map = pawn.Map;
		SupplyManager sm = SupplyManager.For(map);
		if (sm == null || !sm.tidyHome)
			return Enumerable.Empty<Thing>();
		// 2.9.1: without a dump area nothing can be carried out, so there is no
		// work at all (this list was built for every colonist on every work
		// search and cost a lot of TPS on maps with many chunks). Chunks are
		// already marked for hauling and taken by the game's own hauling.
		if (sm.ValidDumpArea() == null)   // 2.17: a deleted dump area counts as none
			return Enumerable.Empty<Thing>();
		Area_Home home = map.areaManager.Home;
		IntVec3 at = pawn.Position;
		return map.listerHaulables.ThingsPotentiallyNeedingHauling()
			.Where(t => t.Spawned && home[t.Position] && !t.IsInAnyStorage() && !sm.dumpArea[t.Position])
			.OrderBy(t => t.Position.DistanceToSquared(at))
			.Take(30)
			.ToList();
	}

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		SupplyManager sm = SupplyManager.For(pawn.Map);
		if (sm == null || !sm.tidyHome || sm.ValidDumpArea() == null)   // 2.17: deleted area = none
			return true;
		return !forced && !sm.IsChosenWorker(pawn, WorkTypeDefOf.Hauling, sm.tidyWorkers);
	}

	private static bool IsChunk(Thing t) => t.def.thingCategories != null && t.def.thingCategories.Any(c => c.defName.Contains("Chunk"));

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		return JobOnThing(pawn, t, forced) != null;
	}

	public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		Map map = pawn.Map;
		if (!t.Spawned || t.IsForbidden(pawn) || t.IsInAnyStorage() || !t.def.EverHaulable || ModCompat.NoHaul(t))
			return null;
		// Dead colonists and other people belong in graves, not on the dump.
		if (t is Corpse corpse && corpse.InnerPawn != null && corpse.InnerPawn.RaceProps.Humanlike)
			return null;
		if (!map.areaManager.Home[t.Position])
			return null;
		// 2.11.2: a thing already lying on the dump area is done. When the dump
		// area was inside the home area, carried-out things were picked up
		// again and moved to the next dump tile – colonists looped for ever.
		SupplyManager sm0 = SupplyManager.For(map);
		if (sm0?.dumpArea != null && sm0.dumpArea[t.Position])
			return null;
		if (!pawn.CanReserve(t, 1, -1, null, forced))
			return null;
		// 2.3: results are remembered for a while per item, so a pile of things
		// with no storage does not cost a full search on every work scan.
		int now = Find.TickManager.TicksGame;
		IntVec3 cell;
		if (Cache.TryGetValue(t.thingIDNumber, out var c) && now < c.until)
		{
			if (c.hasStorage)
				return IsChunk(t) ? PickUpAndHaulCompat.HaulToStorage(pawn, t, forced) : null;
			cell = c.cell;
			if (!cell.IsValid || !CellAcceptsDump(map, cell, pawn, t, true))
				cell = FindDumpCell(pawn, t);
		}
		else
		{
			// It belongs in storage: the game's own hauling does that.
			bool storage = StoreUtility.TryFindBestBetterStorageFor(t, pawn, map, StoragePriority.Unstored, pawn.Faction, out _, out _, needAccurateResult: false);
			cell = storage ? IntVec3.Invalid : FindDumpCell(pawn, t);
			if (Cache.Count > 4000)
				Cache.Clear();
			Cache[t.thingIDNumber] = (now + 600, storage, cell);
			if (storage && IsChunk(t))
				return PickUpAndHaulCompat.HaulToStorage(pawn, t, forced);
			if (storage)
				return null;
		}
		if (!cell.IsValid || cell == t.Position)
			return null;
		Job job = JobMaker.MakeJob(JobDefOf.HaulToCell, t, cell);
		Thing existing = cell.GetFirstItem(map);
		int room = existing == null ? t.stackCount : existing.def.stackLimit - existing.stackCount;
		job.count = Mathf.Max(1, Mathf.Min(t.stackCount, room));
		job.haulMode = HaulMode.ToCellNonStorage;
		return job;
	}

	// 2.15: emptied when a game is loaded – the old ticks and cells made
	// colonists carry things to a place of the previous game for a while
	public static void Reset() => Cache.Clear();

	private static readonly Dictionary<int, (int until, bool hasStorage, IntVec3 cell)> Cache =
		new Dictionary<int, (int until, bool hasStorage, IntVec3 cell)>();

	// Empty ground, or a partial stack of the same thing. Growing zones are never used.
	// The dump area may sit on a stockpile, so zones are allowed there.
	private static bool CellAcceptsDump(Map map, IntVec3 c, Pawn pawn, Thing t, bool allowZones)
	{
		if (!c.InBounds(map) || !c.Standable(map) || c.IsForbidden(pawn) || c.GetEdifice(map) != null)
			return false;
		Zone zone = map.zoneManager.ZoneAt(c);
		if (zone is Zone_Growing)
			return false;
		if (!allowZones && zone != null)
			return false;
		Thing item = c.GetFirstItem(map);
		if (item == null)
			return true;
		int limit = Mathf.Min(item.def.stackLimit, t.def.stackLimit);
		return item.def == t.def && item.stackCount < limit;
	}

	private static IntVec3 FindDumpCell(Pawn pawn, Thing t)
	{
		SupplyManager sm = SupplyManager.For(pawn.Map);
		// No dump area: do not pick the item up and drop it on a random tile.
		// A stockpile, if there is one, is handled by ordinary hauling.
		if (sm?.ValidDumpArea() == null)   // 2.17: deleted area = none
			return IntVec3.Invalid;
		return ClosestInArea(pawn, t, sm.dumpArea);
	}

	// Nearest acceptable cell of the dump area. A spiral covers a nearby area
	// without walking every cell; a far area is sampled, not scanned whole.
	private static IntVec3 ClosestInArea(Pawn pawn, Thing t, Area area)
	{
		Map map = pawn.Map;
		int num = GenRadial.NumCellsInRadius(Mathf.Min(48f, GenRadial.MaxRadialPatternRadius - 1f));
		int reachChecks = 0;
		for (int i = 0; i < num; i++)
		{
			IntVec3 c = t.Position + GenRadial.RadialPattern[i];
			if (!c.InBounds(map) || !area[c] || !CellAcceptsDump(map, c, pawn, t, true))
				continue;
			if (pawn.CanReach(c, PathEndMode.OnCell, Danger.Some))
				return c;
			if (++reachChecks > 12)
				break;
		}
		IntVec3 best = IntVec3.Invalid;
		float bestD = float.MaxValue;
		int seen = 0;
		foreach (IntVec3 c in area.ActiveCells)
		{
			if (++seen > 2500)
				break;
			float d = c.DistanceToSquared(t.Position);
			if (d >= bestD || !CellAcceptsDump(map, c, pawn, t, true))
				continue;
			best = c;
			bestD = d;
			if (bestD <= 36f)
				break;
		}
		if (best.IsValid && pawn.CanReach(best, PathEndMode.OnCell, Danger.Some))
			return best;
		return IntVec3.Invalid;
	}
}
