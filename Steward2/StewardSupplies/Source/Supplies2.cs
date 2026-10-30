using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Supplies (1.2): jobs like a colony manager.
//   Hunting  – keep X meat; which animals may be hunted.
//   Forestry – keep X wood; which trees may be cut.
//   Foraging – keep X wild plant food; which wild plants may be harvested.
//   Mining   – a target for every resource; mineable veins are marked.
//   Food emergency – when food drops below N days, edible wild plants are
//                    harvested and allowed animals hunted, whatever the targets.
// The foreman checks every N in-game hours (set in the window) or on demand.
// It marks the nearest sources first and only as many as are missing.
// ---------------------------------------------------------------------------

public enum JobKind
{
	Hunting,
	Forestry,
	Foraging,
	Mining
}

public sealed class SupplyJob : IExposable
{
	public JobKind kind;
	public ThingDef def;              // Mining: the resource
	public bool enabled;
	public int target;
	public Area area;
	public bool huntPregnant;
	// Player's choices that differ from the default selection (defName -> on/off).
	public Dictionary<string, bool> choices = new Dictionary<string, bool>();

	public int lastStock, lastPending, lastMarked;

	// 1.3: marks this job made itself (ThingID or "x,z"), so they can be
	// withdrawn when the target is reached; player marks are never touched.
	public List<string> ownMarks = new List<string>();
	// Daily stock for the small graph (last 15 days).
	public List<int> history = new List<int>();
	public bool noSources;       // a target is missing and nothing can be marked
	// Forestry
	public bool onlyOutsideHome;
	public int keepTrees;        // never go below this many chosen trees on the map
	// Hunting
	public int keepPerSpecies = 3;
	// 1.0.4: stock once per in-game hour, last 7 days (sparkline)
	public StockHistory hourly = new StockHistory();
	// 1.0.4: Mining – deep drills on this resource are switched off above the
	// target and back on below it
	public bool controlDrills;
	// 1.1.0: Mines 2.0 – 0 veins only, 1 veins + mine, 2 mine only
	public int mineMode;

	public void ExposeData()
	{
		Scribe_Values.Look(ref kind, "kind", JobKind.Hunting);
		Scribe_Defs.Look(ref def, "def");
		Scribe_Values.Look(ref enabled, "enabled", false);
		Scribe_Values.Look(ref target, "target", 0);
		Scribe_References.Look(ref area, "area");
		Scribe_Values.Look(ref huntPregnant, "huntPregnant", false);
		Scribe_Collections.Look(ref choices, "choices", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref ownMarks, "ownMarks", LookMode.Value);
		Scribe_Collections.Look(ref history, "history", LookMode.Value);
		Scribe_Values.Look(ref onlyOutsideHome, "onlyOutsideHome", false);
		Scribe_Values.Look(ref keepTrees, "keepTrees", 0);
		Scribe_Values.Look(ref keepPerSpecies, "keepPerSpecies", 3);
		Scribe_Values.Look(ref controlDrills, "controlDrills", false);
		Scribe_Values.Look(ref mineMode, "mineMode", 0);
		hourly.Expose("hourly");
		if (choices == null)
			choices = new Dictionary<string, bool>();
		if (ownMarks == null)
			ownMarks = new List<string>();
		if (history == null)
			history = new List<int>();
	}

	public bool IsChosen(ThingDef d, bool byDefault)
	{
		bool v;
		return choices.TryGetValue(d.defName, out v) ? v : byDefault;
	}

	public void SetChosen(ThingDef d, bool on, bool byDefault)
	{
		if (on == byDefault)
			choices.Remove(d.defName);
		else
			choices[d.defName] = on;
	}
}

public sealed partial class SupplyManager : MapComponent
{
	public bool enabled = true;
	public int intervalHours = 2;
	public SupplyJob hunting = new SupplyJob { kind = JobKind.Hunting, target = 200 };
	public SupplyJob forestry = new SupplyJob { kind = JobKind.Forestry, target = 500 };
	public SupplyJob foraging = new SupplyJob { kind = JobKind.Foraging, target = 100 };
	public List<SupplyJob> mining = new List<SupplyJob>();

	// 1.3: livestock, area clearing, tidying the home area.
	public List<LivestockRule> livestock = new List<LivestockRule>();
	public Area clearArea;
	public bool tidyHome = true;
	public Area dumpArea;
	public bool huntNoRanged;
	private int nextHistory;
	private int nextChunkMark;
	private int nextItemMark;
	private int nextPocketCheck;

	// 2.6.5: chunks are marked a few at a time (nearest to the colony first)
	// and only while there is little other hauling and few chunks already
	// waiting. Before, up to 300 were marked at once and hauling crowded out
	// the real work.
	public int MarkChunks()
	{
		Area_Home home = map.areaManager.Home;
		DesignationManager dm = map.designationManager;
		// 2.24.1: no longer waits until other hauling is done – in a big colony
		// there were always more than 40 things to haul and chunks were never marked
		int waiting = dm.SpawnedDesignationsOfDef(DesignationDefOf.Haul).Count(d => d.target.HasThing && ColonyStats.IsChunk(d.target.Thing));
		int budget = Mathf.Min(20, 30 - waiting);
		if (budget <= 0)
			return 0;
		IntVec3 center = ColonyStats.BaseCenter(map);   // 2.15: middle of the colony, not the first colonist
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Chunk).OrderBy(c => c.Position.DistanceToSquared(center)).ToList())
		{
			if (!t.Spawned || !home[t.Position] || t.IsInAnyStorage())
				continue;
			if (map.fogGrid.IsFogged(t.Position) || !t.def.designateHaulable)
				continue;
			// 2.52.3: forbidden chunks are left as they are – it may be your choice
			if (t.IsForbidden(Faction.OfPlayer))
				continue;
			if (dm.DesignationOn(t, DesignationDefOf.Haul) != null || ModCompat.NoHaul(t))
				continue;
			dm.AddDesignation(new Designation(t, DesignationDefOf.Haul));
			chunkMarks.Add(t.ThingID);
			if (++n >= budget)
				break;
		}
		return n;
	}

	// 2.23.0: everything lying in the home area that is not on a shelf or in a
	// stockpile (and has a place to go) is marked for hauling – also things
	// forbidden after a raid. A few at a time, nearest to the colony first.
	public int MarkLoose()
	{
		Area_Home home = map.areaManager.Home;
		DesignationManager dm = map.designationManager;
		int waiting = dm.SpawnedDesignationsOfDef(DesignationDefOf.Haul).Count(d => d.target.HasThing && !ColonyStats.IsChunk(d.target.Thing));
		int budget = Mathf.Min(25, 60 - waiting);
		if (budget <= 0)
			return 0;
		IntVec3 center = ColonyStats.BaseCenter(map);
		int n = 0;
		var items = map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
			.Where(t => t.Spawned && t.def.category == ThingCategory.Item && home[t.Position] && !ColonyStats.IsChunk(t))
			.OrderBy(t => t.Position.DistanceToSquared(center)).ToList();
		Pawn carrier = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
		foreach (Thing t in items)
		{
			if (t.IsInAnyStorage() || map.fogGrid.IsFogged(t.Position) || t.IsBurning())
				continue;
			if (dm.DesignationOn(t, DesignationDefOf.Haul) != null || ModCompat.NoHaul(t))
				continue;
			if (map.reservationManager.IsReservedByAnyoneOf(t, Faction.OfPlayer))
				continue;
			bool place;
			try { place = carrier != null && StoreUtility.TryFindBestBetterStorageFor(t, carrier, map, StoragePriority.Unstored, Faction.OfPlayer, out _, out _, false); }
			catch { place = false; }      // a storage mod expecting a carrier must not stop the rest
			if (!place)
				continue;
			if (t.IsForbidden(Faction.OfPlayer))
				continue;   // 2.52.3: never un-forbid what you forbade
			dm.AddDesignation(new Designation(t, DesignationDefOf.Haul));
			chunkMarks.Add(t.ThingID);
			if (++n >= budget)
				break;
		}
		return n;
	}

	// 2.23.1: things colonists keep in their pockets for no reason (left over
	// from inventory hauling, picked up and forgotten) are put down and carried
	// to storage. Kept: weapons (sidearms), drugs and stock from the colonist's
	// policies, travel food. A thing must sit there for two checks in a row, so
	// Pick Up And Haul has time to unload it itself.
	private readonly Dictionary<int, int> strayFirstSeen = new Dictionary<int, int>();

	private static bool KeepInInventory(Pawn p, Thing t)
	{
		if (t.def.IsWeapon || t is Apparel)
			return true;
		if (p.drugs?.CurrentPolicy != null)
		{
			DrugPolicy pol = p.drugs.CurrentPolicy;
			for (int i = 0; i < pol.Count; i++)
				if (pol[i].drug == t.def && pol[i].takeToInventory > 0)
					return true;
		}
		if (p.inventoryStock?.stockEntries != null)
			foreach (InventoryStockEntry e in p.inventoryStock.stockEntries.Values)
				if (e.thingDef == t.def && e.count > 0)
					return true;
		if (t.def.IsNutritionGivingIngestible && !t.def.IsDrug && JobGiver_PackFood.IsGoodPackableFoodFor(t, p, false))
			return true;
		return false;
	}

	public int UnloadPockets()
	{
		int now = Find.TickManager.TicksGame;
		Area_Home home = map.areaManager.Home;
		int n = 0;
		var seen = new HashSet<int>();
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
		{
			if (p.inventory == null || p.inventory.innerContainer.Count == 0 || p.Drafted || p.Downed || p.InMentalState
			    || p.GetLord() != null || !home[p.Position] || p.inventory.UnloadEverything)
				continue;
			Job cur = p.CurJob;
			if (cur != null && (cur.playerForced || (cur.def.defName.IndexOf("Unload", StringComparison.OrdinalIgnoreCase) >= 0)
			                    || cur.def.defName.IndexOf("HaulToInventory", StringComparison.Ordinal) >= 0
			                    || cur.def == JobDefOf.HaulToTransporter || cur.def == JobDefOf.HaulToContainer || cur.def == JobDefOf.Ingest))
				continue;
			var strays = new List<Thing>();
			bool hah = HarvestAndHaulCompat.Present;
			foreach (Thing t in p.inventory.innerContainer)
			{
				if (t == null || KeepInInventory(p, t) || ModCompat.NoHaul(t))
					continue;
				if (!StoreUtility.TryFindBestBetterStorageFor(t, p, map, StoragePriority.Unstored, Faction.OfPlayer, out _, out _, false))
					continue;
				seen.Add(t.thingIDNumber);
				if (!strayFirstSeen.TryGetValue(t.thingIDNumber, out int first))
				{
					strayFirstSeen[t.thingIDNumber] = now;
					continue;
				}
				// 2.24.0: Harvest And Haul unloads what it still tracks – give it time (a few hours)
				int wait = hah && HarvestAndHaulCompat.Tracks(p, t) ? 7500 : 1400;
				if (now - first < wait)
					continue;
				strays.Add(t);
			}
			if (strays.Count == 0)
				continue;
			foreach (Thing t in strays)
				strayFirstSeen.Remove(t.thingIDNumber);
			// 2.24.0: with Harvest And Haul everything goes to storage in one trip by its own job
			if (hah && HarvestAndHaulCompat.Unload(p, strays))
			{
				n += strays.Count;
				continue;
			}
			Thing stray = strays[0];
			if (!p.inventory.innerContainer.TryDrop(stray, p.Position, map, ThingPlaceMode.Near, out Thing dropped) || dropped == null)
				continue;
			if (dropped.IsForbidden(Faction.OfPlayer))
				dropped.SetForbidden(false, false);
			Job haul = HaulAIUtility.HaulToStorageJob(p, dropped, false);
			if (haul != null)
				p.jobs.jobQueue.EnqueueFirst(haul);
			n++;
		}
		// forget things that are no longer in anyone's pockets
		foreach (int id in strayFirstSeen.Keys.ToList())
			if (!seen.Contains(id))
				strayFirstSeen.Remove(id);
		return n;
	}

	// 2.3: production bills, power, cleaning, livestock marks
	public List<ProductionTarget> production = new List<ProductionTarget>();
	public bool powerOn;
	public float powerLow = 0.2f;
	public float powerHigh = 0.6f;
	public List<string> powerDevices = new List<string>();
	public List<string> poweredOffByUs = new List<string>();
	// 2.15: single devices the steward must never switch off
	public HashSet<string> powerExempt = new HashSet<string>();
	public bool cleanPriority = true;
	// 2.4: stone chunks and slag in the home area; how many colonists do it
	public bool tidyChunks = true;
	public bool tidyItems = true;
	public bool unloadPockets = true;
	// 2.6: finished goods are carried away from the workbench right away
	public bool haulProducts = true;
	public int tidyWorkers = 2;    // 0 = everyone who hauls
	public int cleanWorkers = 2;   // 0 = everyone who cleans
	public List<string> cleanRoles = new List<string> { "Kitchen", "Hospital", "DiningRoom" };
	public List<string> slaughterMarks = new List<string>();
	public List<string> chunkMarks = new List<string>();
	public List<string> clearMarks = new List<string>();
	private int nextPowerCheck;

	public bool emergencyOn = true;
	public float emergencyDays = 2f;
	public bool emergencyActive;
	public float lastFoodDays = -1f;

	private int nextCheck;
	private const int MaxMarksPerJob = 30;
	private const float NutritionPerUnit = 0.05f;   // raw meat, berries ...
	private const float NeedPerPersonPerDay = 1.6f;

	public SupplyManager(Map map) : base(map)
	{
	}

	public static SupplyManager For(Map map) => map?.GetComponent<SupplyManager>();

	// 2.17: areas the player deleted are forgotten – before, a deleted area
	// was still given to herds and cleared, and saving it failed
	// 2.35.2: a broken area list (a mod with its own area removed) must not stop saving
	public bool LiveArea(Area a)
	{
		if (a == null)
			return false;
		var all = map?.areaManager?.AllAreas;
		if (all == null)
			return true;
		try { return all.Contains(a); }
		catch { return true; }
	}

	// 2.17: the dump area of tidying, checked when a work scan starts
	public Area ValidDumpArea()
	{
		if (dumpArea != null && !LiveArea(dumpArea))
			dumpArea = null;
		return dumpArea;
	}

	// 2.39.2: clearing is meant for a building site or a fire break, not for half the map
	public static bool TooBigToClear(Area a)
	{
		if (a?.Map == null)
			return false;
		return a.TrueCount > Math.Max(1500, a.Map.Area / 8);
	}

	// switching the clear area off (or to another one) takes back the marks it made
	public void SetClearArea(Area a)
	{
		if (a == clearArea)
			return;
		if (clearArea != null && clearMarks != null && clearMarks.Count > 0)
		{
			int n = CancelTracked(map.designationManager, clearMarks, DesignationDefOf.CutPlant);
			if (n > 0)
				Messages.Message("CF_J_ClearCancelled".Translate(n), MessageTypeDefOf.SilentInput, historical: false);
		}
		clearArea = a;
	}

	public void ValidateAreas(bool check = false)
	{
		if (clearArea != null && !LiveArea(clearArea)) clearArea = null;
		// 2.39.3: clearing switched off (also in an older version) – its leftover marks go away
		if (check && clearArea == null && clearMarks != null && clearMarks.Count > 0)
		{
			int n = CancelTracked(map.designationManager, clearMarks, DesignationDefOf.CutPlant);
			if (n > 0)
				Messages.Message("CF_J_ClearCancelled".Translate(n), MessageTypeDefOf.SilentInput, historical: false);
		}
		if (check && clearArea != null && TooBigToClear(clearArea))
		{
			string label = clearArea.Label;
			SetClearArea(null);
			Messages.Message("CF_J_ClearTooBigOff".Translate(label), MessageTypeDefOf.CautionInput, historical: false);
		}
		if (dumpArea != null && !LiveArea(dumpArea)) dumpArea = null;
		foreach (SupplyJob j in AllJobs())
			if (j?.area != null && !LiveArea(j.area))
				j.area = null;
		if (livestock != null)
			foreach (LivestockRule r in livestock)
			{
				if (r == null) continue;
				if (r.area != null && !LiveArea(r.area)) r.area = null;
				if (r.appliedArea != null && !LiveArea(r.appliedArea)) r.appliedArea = null;
			}
	}

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
			ValidateAreas();   // 2.17: no references to deleted areas in the save
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref intervalHours, "intervalHours", 2);
		Scribe_Deep.Look(ref hunting, "hunting");
		Scribe_Deep.Look(ref forestry, "forestry");
		Scribe_Deep.Look(ref foraging, "foraging");
		Scribe_Collections.Look(ref mining, "mining", LookMode.Deep);
		Scribe_Values.Look(ref emergencyOn, "emergencyOn", true);
		Scribe_Values.Look(ref emergencyDays, "emergencyDays", 2f);
		Scribe_Values.Look(ref emergencyActive, "emergencyActive", false);
		Scribe_Values.Look(ref nextCheck, "nextCheck", 0);
		Scribe_Collections.Look(ref livestock, "livestock", LookMode.Deep);
		Scribe_References.Look(ref clearArea, "clearArea");
		Scribe_Values.Look(ref tidyHome, "tidyHome", true);
		Scribe_References.Look(ref dumpArea, "dumpArea");
		Scribe_Values.Look(ref nextHistory, "nextHistory", 0);
		Scribe_Collections.Look(ref production, "production", LookMode.Deep);
		Scribe_Values.Look(ref powerOn, "powerOn", false);
		Scribe_Values.Look(ref powerLow, "powerLow", 0.2f);
		Scribe_Values.Look(ref powerHigh, "powerHigh", 0.6f);
		Scribe_Collections.Look(ref powerDevices, "powerDevices", LookMode.Value);
		Scribe_Collections.Look(ref poweredOffByUs, "poweredOffByUs", LookMode.Value);
		Scribe_Collections.Look(ref powerExempt, "powerExempt", LookMode.Value);
		Scribe_Values.Look(ref cleanPriority, "cleanPriority", true);
		Scribe_Values.Look(ref tidyChunks, "tidyChunks", true);
		Scribe_Values.Look(ref tidyItems, "tidyItems", true);
		Scribe_Values.Look(ref unloadPockets, "unloadPockets", true);
		Scribe_Values.Look(ref haulProducts, "haulProducts", true);
		Scribe_Values.Look(ref tidyWorkers, "tidyWorkers", 2);
		Scribe_Values.Look(ref cleanWorkers, "cleanWorkers", 2);
		Scribe_Collections.Look(ref cleanRoles, "cleanRoles", LookMode.Value);
		Scribe_Collections.Look(ref slaughterMarks, "slaughterMarks", LookMode.Value);
		Scribe_Collections.Look(ref chunkMarks, "chunkMarks", LookMode.Value);
		Scribe_Collections.Look(ref clearMarks, "clearMarks", LookMode.Value);
		ExposeMeals();
		ExposeBlight();
		ExposeProductionVersion();
		ExposeAuto();
		ExposeChains();
		ExposeMines();
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			if (livestock == null) livestock = new List<LivestockRule>();
			if (production == null) production = new List<ProductionTarget>();
			production.RemoveAll(x => x == null || x.product == null || x.recipe == null);
			if (powerDevices == null) powerDevices = new List<string>();
			if (poweredOffByUs == null) poweredOffByUs = new List<string>();
			if (powerExempt == null) powerExempt = new HashSet<string>();
			if (cleanRoles == null) cleanRoles = new List<string> { "Kitchen", "Hospital", "DiningRoom" };
			if (slaughterMarks == null) slaughterMarks = new List<string>();
			if (chunkMarks == null) chunkMarks = new List<string>();
			if (clearMarks == null) clearMarks = new List<string>();
			livestock.RemoveAll(l => l == null || l.race == null);
			if (hunting == null) hunting = new SupplyJob { kind = JobKind.Hunting, target = 200 };
			if (forestry == null) forestry = new SupplyJob { kind = JobKind.Forestry, target = 500 };
			if (foraging == null) foraging = new SupplyJob { kind = JobKind.Foraging, target = 100 };
			if (mining == null) mining = new List<SupplyJob>();
			mining.RemoveAll(m => m == null || m.def == null);
			hunting.kind = JobKind.Hunting;
			forestry.kind = JobKind.Forestry;
			foraging.kind = JobKind.Foraging;
		}
	}

	public IEnumerable<SupplyJob> AllJobs()
	{
		yield return hunting;
		yield return forestry;
		yield return foraging;
		foreach (SupplyJob m in mining)
			yield return m;
	}

	public override void MapComponentTick()
	{
		int now = Find.TickManager.TicksGame;
		try { TickMinePause(now); }   // 1.2.0
		catch (Exception ex) { MLog.WarnOnce("[Steward] mines pause: " + ex.Message, 0x5e1102); }
		if (now >= nextHistory)
		{
			nextHistory = now + 60000;
			foreach (SupplyJob j in AllJobs())
			{
				if (!j.enabled)
					continue;
				j.history.Add(j.lastStock);
				while (j.history.Count > 15)
					j.history.RemoveAt(0);
			}
		}
		// 2.6.1: stone chunks and slag in the home area are marked for hauling
		// by the steward, exactly as if the player had marked them. Every
		// hauler then takes them to storage; with no storage for chunks the
		// "carry out" job takes them out of the home area.
		TickBlight();
		TickStockHistory(now);
		if (enabled && tidyChunks && now >= nextChunkMark)
		{
			nextChunkMark = now + 1000;
			try { MarkChunks(); }
			catch (Exception ex) { MLog.Warn("[Steward] chunks: " + ex.Message); }
		}
		if (enabled && tidyItems && now >= nextItemMark)
		{
			nextItemMark = now + 1250;
			try { MarkLoose(); }
			catch (Exception ex) { MLog.Warn("[Steward] loose items: " + ex.Message); }
		}
		if (enabled && unloadPockets && now >= nextPocketCheck)
		{
			nextPocketCheck = now + 750;
			try { UnloadPockets(); }
			catch (Exception ex) { MLog.Warn("[Steward] pockets: " + ex.Message); }
		}
		if (now >= nextPowerCheck)
		{
			nextPowerCheck = now + 250;
			try
			{
				if (enabled && powerOn)
					CheckPower();
				else
					ReleasePower();
				if (enabled && haulProducts)
					PreferStockpileForBills();
			}
			catch (Exception ex) { MLog.Warn("[Steward] power: " + ex.Message); }
		}
		TickMeals();
		// 2.17: once an hour the lists of the steward's own marks lose the marks
		// that are gone (done, cancelled) – they grew for ever in the save
		if (now >= nextMarkPrune)
		{
			nextMarkPrune = now + 2500;
			try { PruneMarks(); }
			catch (Exception ex) { MLog.Warn("[Steward] marks: " + ex.Message); }
		}
		if (!enabled)
		{
			if (now >= nextCheck)
			{
				nextCheck = now + Mathf.Clamp(intervalHours, 1, 48) * 2500;
				try { SuspendProduction(); RemoveAuto(null); }
				catch (Exception ex) { MLog.Warn("[Steward] production: " + ex.Message); }
				// 1.0.4: drills the steward switched off are switched on again
				try { CheckDrills(); }
				catch (Exception ex) { MLog.WarnOnce("[Steward] deep drills: " + ex.Message, 0x5e0104); }
				try { CheckMines(); }
				catch (Exception ex) { MLog.WarnOnce("[Steward] mines: " + ex.Message, 0x5e1101); }
			}
			return;
		}
		if (now < nextCheck)
			return;
		nextCheck = now + Mathf.Clamp(intervalHours, 1, 48) * 2500;
		if (!map.IsPlayerHome || ColonyStats.Workers(map) == 0)
			return;
		CheckAll(false);
	}

	// 2.17: keys as everywhere else: ThingID, or "x,z" for a cell (mining)
	private int nextMarkPrune;

	private HashSet<string> MarkKeys(params DesignationDef[] defs)
	{
		var set = new HashSet<string>();
		DesignationManager dm = map.designationManager;
		foreach (DesignationDef dd in defs)
		{
			if (dd == null)
				continue;
			foreach (Designation d in dm.SpawnedDesignationsOfDef(dd))
				set.Add(d.target.HasThing ? d.target.Thing.ThingID : d.target.Cell.x + "," + d.target.Cell.z);
		}
		return set;
	}

	private static void PruneList(List<string> ids, HashSet<string> keys)
	{
		if (ids != null && ids.Count > 0)
			ids.RemoveAll(id => !keys.Contains(id));
	}

	private void PruneMarks()
	{
		bool any = (chunkMarks?.Count ?? 0) > 0 || (clearMarks?.Count ?? 0) > 0 || (blightMarks?.Count ?? 0) > 0
		           || AllJobs().Any(j => j != null && j.ownMarks.Count > 0);
		if (!any)
			return;
		HashSet<string> haul = MarkKeys(DesignationDefOf.Haul);
		HashSet<string> plants = MarkKeys(DesignationDefOf.CutPlant, DesignationDefOf.HarvestPlant);
		HashSet<string> hunt = MarkKeys(DesignationDefOf.Hunt);
		HashSet<string> mine = MarkKeys(DesignationDefOf.Mine);
		PruneList(chunkMarks, haul);
		PruneList(clearMarks, plants);
		PruneList(blightMarks, plants);
		foreach (SupplyJob j in AllJobs())
		{
			if (j == null)
				continue;
			PruneList(j.ownMarks, j.kind == JobKind.Hunting ? hunt : j.kind == JobKind.Mining ? mine : plants);
		}
	}

	private void Step(string name, string labelKey, Func<int> step, ref int total, List<string> parts)
	{
		try
		{
			int n = step();
			if (n > 0)
			{
				total += n;
				parts.Add(labelKey.Translate() + " " + n);
			}
		}
		catch (Exception ex)
		{
			Log.ErrorOnce("[Steward] " + name + " failed: " + ex, ("StewardStep" + name).GetHashCode());
		}
	}

	public int HoursToNextCheck => Mathf.Max(0, (nextCheck - Find.TickManager.TicksGame) / 2500);

	// ---------------------------------------------------------------------
	public int CheckAll(bool manual)
	{
		var parts = new List<string>();
		int total = 0;
		ValidateAreas(check: true);   // 2.17: once per check, not per cell

		// Food emergency first: it may need the same sources.
		int emergency = 0;
		try { emergency = CheckEmergency(manual); }
		catch (Exception ex) { MLog.Warn("[Steward] food emergency: " + ex.Message); }
		if (emergency > 0)
		{
			total += emergency;
			parts.Add("CF_J_Emergency".Translate() + " " + emergency);
		}

		// 2.16.2: every step on its own – an error in one does not stop the
		// others, and the log names the step with the full error (once)
		Step("clearing", "CF_J_Clear", MarkClearArea, ref total, parts);
		Step("ingredients", "CF_J_Production", CheckChains, ref total, parts);   // 1.0.4
		Step("production", "CF_J_Production", CheckProduction, ref total, parts);
		Step("self-supply", "CF_A_Page", CheckAuto, ref total, parts);
		Step("livestock", "CF_J_Livestock", CheckLivestock, ref total, parts);

		foreach (SupplyJob job in AllJobs())
		{
			int marked = 0;
			try { marked = Check(job, enabled && job.enabled && UsesVeins(job)); }
			catch (Exception ex) { MLog.Warn("[Steward] supplies " + job.kind + ": " + ex.Message); }
			if (marked > 0)
			{
				total += marked;
				parts.Add(JobLabel(job) + " " + marked);
			}
		}
		Step("drills", "CF_D_Drills", CheckDrills, ref total, parts);   // 1.0.4: after the stock was counted
		Step("mines", "CF_M_Mines", CheckMines, ref total, parts);       // 1.1.0: Mines 2.0
		bool tell = SuppliesMod.S == null || SuppliesMod.S.supplyMessages;
		if (total > 0 && (manual || tell))
			Messages.Message("CF_S_Marked".Translate(string.Join(", ", parts)), MessageTypeDefOf.SilentInput, historical: false);
		else if (manual)
			Messages.Message("CF_S_NothingToMark".Translate(), MessageTypeDefOf.SilentInput, historical: false);
		return total;
	}

	public static string JobLabel(SupplyJob job)
	{
		switch (job.kind)
		{
			case JobKind.Hunting: return "CF_J_Hunting".Translate();
			case JobKind.Forestry: return "CF_J_Forestry".Translate();
			case JobKind.Foraging: return "CF_J_Foraging".Translate();
			default: return job.def?.LabelCap.ToString() ?? "?";
		}
	}

	private int Check(SupplyJob job, bool mark)
	{
		if (job != null && !job.enabled && job.kind == JobKind.Mining)
		{
			job.noSources = false;
			return 0;
		}
		HashSet<ThingDef> products = Products(job);
		job.lastStock = CountStock(job, products);
		job.lastPending = CountPending(job, products);
		job.lastMarked = 0;
		job.noSources = false;
		bool foodJob = job.kind == JobKind.Hunting || job.kind == JobKind.Foraging;
		if (!mark)
		{
			// 2.52.3: a job switched off takes back its own marks (before the trees were still cut)
			if (!(emergencyActive && foodJob))
				WithdrawOwnMarks(job);
			return 0;
		}
		int missing = job.target - job.lastStock - job.lastPending;
		if (job.lastStock >= job.target)
		{
			// 2.52.3: the food emergency's hunt/forage marks stay (they were removed in the same check)
			if (!(emergencyActive && foodJob))
				WithdrawOwnMarks(job);
			job.lastPending = CountPending(job, products);
			return 0;
		}
		if (missing <= 0)
			return 0;
		int marked = Mark(job, missing);
		job.noSources = marked == 0;
		job.lastMarked = marked;
		if (marked > 0)
			job.lastPending = CountPending(job, products);
		return marked;
	}

	// ---- food emergency ----------------------------------------------------
	public float FoodDays() => ColonyStats.FoodDays(map);

	private int CheckEmergency(bool manual)
	{
		lastFoodDays = FoodDays();
		bool was = emergencyActive;
		emergencyActive = emergencyOn && enabled && lastFoodDays < emergencyDays;
		if (emergencyActive != was && map.IsPlayerHome)
		{
			if (emergencyActive)
				Messages.Message("CF_J_EmergencyStart".Translate(lastFoodDays.ToString("0.0")), MessageTypeDefOf.NegativeEvent, historical: false);
			else
				Messages.Message("CF_J_EmergencyEnd".Translate(), MessageTypeDefOf.PositiveEvent, historical: false);
		}
		if (!emergencyActive)
			return 0;

		int people = ColonyStats.Eaters(map);
		// 2.15: the same food count as the crisis and the work planner
		// (storage and home area), and the real nutrition of what is gathered
		float missingNutrition = emergencyDays * people * NeedPerPersonPerDay - ColonyStats.FoodNutrition(map);
		HashSet<ThingDef> forage = Products(foraging, edibleOnly: true);
		HashSet<ThingDef> meat = Products(hunting);
		float perUnit = Mathf.Max(0.01f, AvgNutrition(forage, meat));
		int units = Mathf.CeilToInt(missingNutrition / perUnit);
		// Already on the way: marked plants and animals.
		units -= CountPending(foraging, forage);
		units -= CountPending(hunting, meat);
		if (units <= 0)
			return 0;
		// Safe food first (edible wild plants), then hunting – only the jobs
		// that are switched on in the steward.
		int marked = 0;
		if (foraging.enabled)
		{
			marked += MarkForaging(foraging, units, edibleOnly: true, out int covered);
			units -= covered;
		}
		if (units > 0 && hunting.enabled)
			marked += MarkHunting(hunting, units);
		return marked;
	}

	private static float AvgNutrition(HashSet<ThingDef> a, HashSet<ThingDef> b)
	{
		float sum = 0f;
		int n = 0;
		foreach (ThingDef d in a.Concat(b))
		{
			if (d?.ingestible == null)
				continue;
			sum += d.GetStatValueAbstract(StatDefOf.Nutrition);
			n++;
		}
		return n == 0 ? NutritionPerUnit : sum / n;
	}

	// ---- what counts --------------------------------------------------------
	public HashSet<ThingDef> Products(SupplyJob job, bool edibleOnly = false)
	{
		var set = new HashSet<ThingDef>();
		switch (job.kind)
		{
			case JobKind.Hunting:
				// 2.15: meat of animals only – human and insect meat do not
				// fill the hunting target any more
				foreach (ThingDef race in DefDatabase<ThingDef>.AllDefsListForReading)
				{
					RaceProperties rp = race.race;
					if (rp == null || !rp.Animal || rp.Insect || rp.meatDef == null || rp.IsMechanoid)
						continue;
					set.Add(rp.meatDef);
				}
				set.Remove(ThingDefOf.Meat_Human);
				break;
			case JobKind.Forestry:
				set.Add(ThingDefOf.WoodLog);
				foreach (ThingDef t in TreeChoices())
					if (job.IsChosen(t, DefaultTree(t)) && t.plant.harvestedThingDef != null)
						set.Add(t.plant.harvestedThingDef);
				break;
			case JobKind.Foraging:
				foreach (ThingDef p in ForageChoices())
					if (job.IsChosen(p, DefaultForage(p)) && p.plant.harvestedThingDef != null
					    && (!edibleOnly || p.plant.harvestedThingDef.IsNutritionGivingIngestible))
						set.Add(p.plant.harvestedThingDef);
				break;
			case JobKind.Mining:
				if (job.def != null)
					set.Add(job.def);
				break;
		}
		return set;
	}

	private int CountStock(SupplyJob job, HashSet<ThingDef> products)
	{
		int n = 0;
		foreach (ThingDef d in products)
			foreach (Thing th in map.listerThings.ThingsOfDef(d))
				if (th.Spawned && th.IsInAnyStorage())
					n += th.stackCount;
		return n;
	}

	private int CountPending(SupplyJob job, HashSet<ThingDef> products)
	{
		int n = 0;
		foreach (ThingDef d in products)
			foreach (Thing th in map.listerThings.ThingsOfDef(d))
				if (th.Spawned && !th.IsInAnyStorage() && !th.IsForbidden(Faction.OfPlayer))
					n += th.stackCount;
		DesignationManager dm = map.designationManager;
		switch (job.kind)
		{
			case JobKind.Hunting:
				foreach (Designation des in dm.SpawnedDesignationsOfDef(DesignationDefOf.Hunt))
					if (des.target.Thing is Pawn p)
						n += MeatOf(p);
				// Killed animals not butchered yet.
				foreach (Thing th in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
					if (th is Corpse c && c.InnerPawn != null && c.InnerPawn.RaceProps.Animal && !c.IsForbidden(Faction.OfPlayer) && !c.IsDessicated())
						n += MeatOf(c.InnerPawn);
				break;
			case JobKind.Forestry:
			case JobKind.Foraging:
			{
				// 2.39.2: plants marked only to clear an area are not wood "on its way"
				var clearing = clearMarks != null && clearMarks.Count > 0 ? new HashSet<string>(clearMarks) : null;
				int marked = 0;
				foreach (DesignationDef dd in new[] { DesignationDefOf.CutPlant, DesignationDefOf.HarvestPlant })
					foreach (Designation des in dm.SpawnedDesignationsOfDef(dd))
						if (des.target.Thing is Plant pl && products.Contains(pl.def.plant?.harvestedThingDef)
						    && (clearing == null || !clearing.Contains(pl.ThingID)))
							marked += pl.YieldNow();
				// 2.39.3: hundreds of marked trees far away are not wood "on its way" – at most half
				// the target counts, so the nearest trees still get marked when the stock is empty
				n += Math.Min(marked, Math.Max(50, job.target / 2));
				break;
			}
			case JobKind.Mining:
				foreach (Designation des in dm.SpawnedDesignationsOfDef(DesignationDefOf.Mine))
				{
					Building b = des.target.Cell.GetFirstMineable(map);
					if (b != null && b.def.building?.mineableThing == job.def)
						n += b.def.building.EffectiveMineableYield;
				}
				break;
		}
		return n;
	}

	public static int MeatOf(Pawn p)
	{
		try { return Mathf.RoundToInt(p.GetStatValue(StatDefOf.MeatAmount)); }
		catch { return 0; }
	}

	// ---- choices and defaults ---------------------------------------------
	public static bool DangerousAnimal(ThingDef race)
	{
		RaceProperties rp = race.race;
		return rp != null && (rp.predator || rp.manhunterOnDamageChance > 0.05f);
	}

	public static bool DefaultAnimal(ThingDef race) => !DangerousAnimal(race);

	// 2.17: an animal venerated by the ideoligion of a colonist (or of the
	// player faction) is never hunted or slaughtered by the steward
	public static bool Venerated(Pawn animal)
	{
		if (!ModsConfig.IdeologyActive || animal == null)
			return false;
		Map m = animal.MapHeld;
		if (m != null)
			foreach (Pawn c in m.mapPawns.FreeColonists)
				if (c.Ideo != null && c.Ideo.IsVeneratedAnimal(animal))
					return true;
		FactionIdeosTracker ideos = Faction.OfPlayerSilentFail?.ideos;
		if (ideos != null)
			foreach (Ideo i in ideos.AllIdeos)
				if (i != null && i.IsVeneratedAnimal(animal))
					return true;
		return false;
	}

	// Sacred trees (anima, gauranlen) are off by default.
	public static bool DefaultTree(ThingDef tree) => !IsSpecialTree(tree);

	private static bool IsSpecialTree(ThingDef t)
	{
		string n = t.defName.ToLowerInvariant();
		return n.Contains("anima") || n.Contains("gauranlen") || n.Contains("polux") || n.Contains("harbinger");
	}

	// 1.0.5: sowable crops (rice, potatoes, corn …) are not foraging by default – their harvest from your
	// fields counted as forage stock, so a full barn of rice meant "enough" and wild berries were never marked
	public static bool DefaultForage(ThingDef plant) => plant.plant.harvestedThingDef != null && plant.plant.harvestedThingDef.IsNutritionGivingIngestible && !plant.plant.Sowable;

	// Animals: wild ones on this map and those of this biome.
	public List<ThingDef> AnimalChoices()
	{
		var set = new HashSet<ThingDef>();
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			if (p.RaceProps != null && p.RaceProps.Animal && p.Faction == null)
				set.Add(p.def);
		try
		{
			foreach (PawnKindDef k in map.Biome.AllWildAnimals)
				if (k.race != null)
					set.Add(k.race);
		}
		catch { }
		return set.Where(d => d.race != null && d.race.Animal).OrderBy(d => d.label).ToList();
	}

	public List<ThingDef> TreeChoices()
	{
		var set = new HashSet<ThingDef>();
		foreach (Thing th in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
			if (th.def.plant != null && th.def.plant.IsTree && th.def.plant.harvestedThingDef != null && !th.def.plant.isStump)
				set.Add(th.def);
		try
		{
			foreach (ThingDef p in map.Biome.AllWildPlants)
				if (p.plant != null && p.plant.IsTree && p.plant.harvestedThingDef != null && !p.plant.isStump)
					set.Add(p);
		}
		catch { }
		return set.OrderBy(d => d.label).ToList();
	}

	public List<ThingDef> ForageChoices()
	{
		var set = new HashSet<ThingDef>();
		foreach (Thing th in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
			if (IsForage(th.def))
				set.Add(th.def);
		try
		{
			foreach (ThingDef p in map.Biome.AllWildPlants)
				if (IsForage(p))
					set.Add(p);
		}
		catch { }
		return set.OrderBy(d => d.label).ToList();
	}

	private static bool IsForage(ThingDef d)
	{
		return d.plant != null && !d.plant.IsTree && !d.plant.isStump && d.plant.harvestedThingDef != null && d.plant.Harvestable;
	}

	// Resources in mineable veins on this map.
	public List<ThingDef> MineableResources()
	{
		var set = new HashSet<ThingDef>();
		foreach (ThingDef md in DefDatabase<ThingDef>.AllDefsListForReading)
			if (md.building?.mineableThing != null && map.listerThings.ThingsOfDef(md).Count > 0)
				set.Add(md.building.mineableThing);
		// 1.1.0: resources a built mine (Mines 2.0) can dig up
		if (MinesCompat.Active && MinesCompat.Mines(map).Count > 0)
			foreach (ThingDef d in MinesCompat.Resources())
				if (MineCanMake(d))
					set.Add(d);
		return set.OrderBy(d => d.label).ToList();
	}

	public SupplyJob MiningJob(ThingDef res, bool create)
	{
		SupplyJob j = mining.FirstOrDefault(m => m.def == res);
		if (j == null && create)
		{
			j = new SupplyJob { kind = JobKind.Mining, def = res, target = res == ThingDefOf.Steel ? 300 : 100 };
			// 1.1.0: no veins on the map but a mine can dig it – the mine is the source
			if (MinesCompat.Active && !HasVeins(res) && MineCanMake(res))
				j.mineMode = 2;
			mining.Add(j);
		}
		return j;
	}

	public int CountOnMap(ThingDef d)
	{
		if (d.race != null)
			return map.mapPawns.AllPawnsSpawned.Count(p => p.def == d && p.Faction == null);
		return map.listerThings.ThingsOfDef(d).Count;
	}

	// ---- marking -------------------------------------------------------------
	private bool Allowed(SupplyJob job, IntVec3 c)
	{
		if (!c.InBounds(map) || map.fogGrid.IsFogged(c))
			return false;
		if (job.area != null && !job.area[c])
			return false;
		if (job.onlyOutsideHome && map.areaManager.Home[c])
			return false;
		return !NearDanger(c);
	}

	// Enemies and insect hives: nothing within 15 cells is marked.
	private List<IntVec3> dangerCache;
	private int dangerTick = -1;

	private bool NearDanger(IntVec3 c)
	{
		int now = Find.TickManager.TicksGame;
		if (dangerCache == null || now != dangerTick)
		{
			dangerTick = now;
			dangerCache = new List<IntVec3>();
			foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
				if (!p.Dead && !p.Downed && p.HostileTo(Faction.OfPlayer))
					dangerCache.Add(p.Position);
			ThingDef hive = DefDatabase<ThingDef>.GetNamedSilentFail("Hive");
			if (hive != null)
				foreach (Thing h in map.listerThings.ThingsOfDef(hive))
					dangerCache.Add(h.Position);
		}
		foreach (IntVec3 d in dangerCache)
			if (d.DistanceToSquared(c) < 225)
				return true;
		return false;
	}

	// 2.14.5: pregnancy from any mod – the vanilla one, or any hediff whose
	// name says it is a pregnancy
	public static bool IsPregnant(Pawn p)
	{
		var hs = p?.health?.hediffSet?.hediffs;
		if (hs == null)
			return false;
		for (int i = 0; i < hs.Count; i++)
		{
			Hediff h = hs[i];
			if (h.def == HediffDefOf.Pregnant || h is Hediff_Pregnant || h.def.defName.IndexOf("pregnan", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
		}
		return false;
	}

	private bool HasRangedHunter()
	{
		// 2.14.5: slaves hunt too
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned))
		{
			if (p.Downed || p.WorkTypeIsDisabled(WorkTypeDefOf.Hunting))
				continue;
			ThingWithComps w = p.equipment?.Primary;
			if (w != null && w.def.IsRangedWeapon)
				return true;
		}
		return false;
	}

	private static List<(Thing t, IntVec3 c, int y, DesignationDef d)> KeepSpecies(
		List<(Thing t, IntVec3 c, int y, DesignationDef d)> options, Dictionary<ThingDef, int> alive, int keep)
	{
		// order by distance happens later; here only cap per species
		var left = new Dictionary<ThingDef, int>(alive);
		var result = new List<(Thing t, IntVec3 c, int y, DesignationDef d)>();
		foreach (var o in options)
		{
			int n;
			left.TryGetValue(o.t.def, out n);
			if (n <= keep)
				continue;
			left[o.t.def] = n - 1;
			result.Add(o);
		}
		return result;
	}

	// Removes this job's own pending marks (the player's are kept).
	private void WithdrawOwnMarks(SupplyJob job)
	{
		if (job.ownMarks.Count == 0)
			return;
		var own = new HashSet<string>(job.ownMarks);
		job.ownMarks.Clear();
		DesignationManager dm = map.designationManager;
		DesignationDef[] defs = job.kind == JobKind.Hunting ? new[] { DesignationDefOf.Hunt }
			: job.kind == JobKind.Mining ? new[] { DesignationDefOf.Mine }
			: new[] { DesignationDefOf.CutPlant, DesignationDefOf.HarvestPlant };
		foreach (DesignationDef dd in defs)
			foreach (Designation d in dm.SpawnedDesignationsOfDef(dd).ToList())
			{
				string key = d.target.HasThing ? d.target.Thing.ThingID : d.target.Cell.x + "," + d.target.Cell.z;
				if (own.Contains(key))
					dm.RemoveDesignation(d);
			}
	}

	private int Mark(SupplyJob job, int missing)
	{
		switch (job.kind)
		{
			case JobKind.Hunting: return MarkHunting(job, missing);
			case JobKind.Forestry: return MarkTrees(job, missing);
			case JobKind.Foraging: return MarkForaging(job, missing, false, out _);
			default: return MarkMining(job, missing);
		}
	}

	private int MarkHunting(SupplyJob job, int missing)
	{
		var options = new List<(Thing t, IntVec3 c, int y, DesignationDef d)>();
		DesignationManager dm = map.designationManager;
		huntNoRanged = !HasRangedHunter();
		if (huntNoRanged)
			return 0;
		// keep a breeding population: count wild animals per species, minus the
		// ones already marked
		var alive = new Dictionary<ThingDef, int>();
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			if (p.RaceProps != null && p.RaceProps.Animal && p.Faction == null && !p.Dead)
			{
				int n;
				alive.TryGetValue(p.def, out n);
				alive[p.def] = n + (dm.DesignationOn(p, DesignationDefOf.Hunt) != null ? 0 : 1);
			}
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
		{
			if (p.RaceProps == null || !p.RaceProps.Animal || p.Faction != null || p.Dead || p.Downed || p.InMentalState)
				continue;
			if (!job.IsChosen(p.def, DefaultAnimal(p.def)))
				continue;
			if (!Allowed(job, p.Position) || dm.DesignationOn(p) != null)
				continue;
			if (!job.huntPregnant && IsPregnant(p))
				continue;
			if (Venerated(p))   // 2.17: venerated animals are not hunted
				continue;
			if (alive.TryGetValue(p.def, out int left) && left <= job.keepPerSpecies)
				continue;
			int meat = MeatOf(p);
			if (meat > 0)
				options.Add((p, p.Position, meat, DesignationDefOf.Hunt));
		}
		// Nearest surplus is hunted. The ones left standing are the farthest,
		// not whichever animals happened to be last in the pawn list.
		IntVec3 center = ColonyCenter();
		options.Sort((a, b) => a.c.DistanceToSquared(center).CompareTo(b.c.DistanceToSquared(center)));
		options = KeepSpecies(options, alive, job.keepPerSpecies);
		return MarkNearest(options, missing, out _, job);
	}

	private int MarkTrees(SupplyJob job, int missing)
	{
		var options = new List<(Thing t, IntVec3 c, int y, DesignationDef d)>();
		DesignationManager dm = map.designationManager;
		foreach (Thing th in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
		{
			if (!(th is Plant pl) || pl.def.plant == null || !pl.def.plant.IsTree || pl.def.plant.isStump)
				continue;
			if (!job.IsChosen(pl.def, DefaultTree(pl.def)) || pl.IsCrop || !pl.HarvestableNow)
				continue;
			if (!Allowed(job, pl.Position) || dm.DesignationOn(pl) != null || pl.IsForbidden(Faction.OfPlayer))
				continue;
			int y = pl.YieldNow();
			// 2.42.1: "chop wood" like the game's own tool (harvest designation), not "cut"
			if (y > 0)
				options.Add((pl, pl.Position, y, DesignationDefOf.HarvestPlant));
		}
		if (job.keepTrees > 0)
		{
			int standing = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant)
				.Count(t => t.def.plant != null && t.def.plant.IsTree && !t.def.plant.isStump
				            && job.IsChosen(t.def, DefaultTree(t.def)) && dm.DesignationOn(t) == null);
			int canCut = Mathf.Max(0, standing - job.keepTrees);
			if (canCut == 0)
				return 0;
			IntVec3 cc = ColonyCenter();
			options = options.OrderBy(o => o.c.DistanceToSquared(cc)).Take(canCut).ToList();
		}
		return MarkNearest(options, missing, out _, job);
	}

	private int MarkForaging(SupplyJob job, int missing, bool edibleOnly, out int covered)
	{
		var options = new List<(Thing t, IntVec3 c, int y, DesignationDef d)>();
		DesignationManager dm = map.designationManager;
		foreach (Thing th in map.listerThings.ThingsInGroup(ThingRequestGroup.Plant))
		{
			if (!(th is Plant pl) || !IsForage(pl.def))
				continue;
			if (edibleOnly && !pl.def.plant.harvestedThingDef.IsNutritionGivingIngestible)
				continue;
			if (!job.IsChosen(pl.def, DefaultForage(pl.def)) || pl.IsCrop || !pl.HarvestableNow)
				continue;
			if (!Allowed(job, pl.Position) || dm.DesignationOn(pl) != null || pl.IsForbidden(Faction.OfPlayer))
				continue;
			int y = pl.YieldNow();
			if (y > 0)
				options.Add((pl, pl.Position, y, DesignationDefOf.HarvestPlant));
		}
		return MarkNearest(options, missing, out covered, job);
	}

	private int MarkMining(SupplyJob job, int missing)
	{
		var options = new List<(Thing t, IntVec3 c, int y, DesignationDef d)>();
		DesignationManager dm = map.designationManager;
		foreach (ThingDef md in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (md.building == null || md.building.mineableThing != job.def)
				continue;
			int y = md.building.EffectiveMineableYield;
			if (y <= 0)
				continue;
			foreach (Thing b in map.listerThings.ThingsOfDef(md))
				if (Allowed(job, b.Position) && dm.DesignationAt(b.Position, DesignationDefOf.Mine) == null)
					options.Add((b, b.Position, y, DesignationDefOf.Mine));
		}
		return MarkNearest(options, missing, out _, job);
	}

	// ---- clearing an area ----------------------------------------------------
	// Every plant in the chosen area is cut (trees, bushes, grass), e.g. for a
	// building site or a fire break. Crops in growing zones are left alone.
	private int MarkClearArea()
	{
		if (clearArea == null)
			return 0;
		int marked = 0;
		DesignationManager dm = map.designationManager;
		foreach (IntVec3 c in clearArea.ActiveCells.ToList())
		{
			Plant pl = c.GetPlant(map);
			if (pl == null || pl.IsCrop || dm.DesignationOn(pl) != null || map.fogGrid.IsFogged(c))
				continue;
			dm.AddDesignation(new Designation(pl, DesignationDefOf.CutPlant));
			clearMarks.Add(pl.ThingID);
			if (++marked >= 200)
				break;
		}
		return marked;
	}

	// ---- livestock -------------------------------------------------------------
	public List<ThingDef> LivestockSpecies()
	{
		var set = new HashSet<ThingDef>();
		foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals)
			set.Add(p.def);
		// 1.0.6: species you no longer have only stay while their rule is switched on
		foreach (LivestockRule r in livestock)
			if (r.enabled)
				set.Add(r.race);
		return set.Where(d => d != null).OrderBy(d => d.label).ToList();
	}

	public void RemoveRule(ThingDef race)
	{
		livestock.RemoveAll(x => x.race == race);
	}

	public LivestockRule Rule(ThingDef race, bool create)
	{
		LivestockRule r = livestock.FirstOrDefault(x => x.race == race);
		if (r == null && create)
		{
			r = new LivestockRule { race = race };
			livestock.Add(r);
		}
		return r;
	}

	public void CountHerd(ThingDef race, out int females, out int males, out int young)
	{
		females = males = young = 0;
		foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals)
		{
			if (p.def != race || p.Dead || IsGuestAnimal(p))
				continue;
			if (!p.ageTracker.Adult) young++;
			else if (p.gender == Gender.Female) females++;
			else if (p.gender == Gender.Male) males++;
			// 2.14.5: animals without gender count against the whole adult limit
			else females++;
		}
	}

	private int CheckLivestock()
	{
		int marked = 0;
		DesignationManager dm = map.designationManager;
		if (slaughterMarks == null)
			slaughterMarks = new List<string>();
		var aliveIds = new HashSet<string>();
		foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals.ToList())
			if (!p.Dead)
				aliveIds.Add(p.ThingID);
		slaughterMarks.RemoveAll(id => !aliveIds.Contains(id));
		foreach (LivestockRule r in livestock.ToList())
		{
			if (r == null || r.race == null)
				continue;
			// 1.0.3: quest guest animals (lodgers) are not part of the herd
			var herd = map.mapPawns.SpawnedColonyAnimals.Where(p => p.def == r.race && !p.Dead && !IsGuestAnimal(p)).ToList();
			if (!r.enabled)
			{
				UnmarkSlaughter(herd, dm);
				continue;
			}
			// 2.15: the herd area is given when you change it in the rule, and to
			// animals that have no area yet (newborn, bought, tamed). An area you
			// set by hand on one animal is no longer overwritten at every check.
			if (r.area != null)
			{
				bool changed = r.area != r.appliedArea;
				// a save from before 2.52.3 has no list – the animals there are taken as already known
				bool firstRun = r.given.Count == 0 && !r.GivenKnown && r.appliedArea != null;
				var ids = new HashSet<string>(r.given);
				foreach (Pawn p in herd)
				{
					if (p.playerSettings == null)
						continue;
					bool isNew = !ids.Contains(p.ThingID);
					ids.Add(p.ThingID);
					if (firstRun)
						continue;
					Area cur = p.playerSettings.AreaRestrictionInPawnCurrentMap;
					if (cur != r.area && (changed || (isNew && cur == null)))
						p.playerSettings.AreaRestrictionInPawnCurrentMap = r.area;
				}
				ids.IntersectWith(herd.Select(p => p.ThingID));
				r.given = ids.ToList();
			}
			r.appliedArea = r.area;
			marked += Cull(herd.Where(p => p.ageTracker.Adult && p.gender == Gender.Female), r.females, r, dm);
			marked += Cull(herd.Where(p => p.ageTracker.Adult && p.gender == Gender.Male), r.males, r, dm);
			// 2.14.5: a species without genders was counted as all males
			marked += Cull(herd.Where(p => p.ageTracker.Adult && p.gender == Gender.None), r.females + r.males, r, dm);
			// 1.0.6: young animals can be spared completely
			if (r.spareYoung)
				UnmarkSlaughter(herd.Where(p => !p.ageTracker.Adult), dm);
			else
				marked += Cull(herd.Where(p => !p.ageTracker.Adult), r.young, r, dm);
		}
		return marked;
	}

	// 1.0.3: animals that only stay with the colony for a quest (lodgers,
	// borrowed animals) never count toward the herd targets and are never culled.
	private static bool IsGuestAnimal(Pawn p)
	{
		try
		{
			return p.Faction != Faction.OfPlayer || p.IsQuestLodger();
		}
		catch
		{
			return false;
		}
	}

	// 1.0.3: the steward's cull mark is either a slaughter or a release-to-the-wild designation
	private static Designation CullMark(Pawn p, DesignationManager dm)
	{
		return dm.DesignationOn(p, DesignationDefOf.Slaughter) ?? dm.DesignationOn(p, DesignationDefOf.ReleaseAnimalToWild);
	}

	private static bool CanRelease(Pawn p)
	{
		return p.RaceProps != null && p.RaceProps.canReleaseToWild && p.Faction == Faction.OfPlayer;
	}

	private static bool HerdProtected(Pawn p, LivestockRule r)
	{
		if (p.relations != null && p.relations.GetFirstDirectRelationPawn(PawnRelationDefOf.Bond) != null)
			return true;
		if (Venerated(p))   // 2.17: venerated animals are never culled
			return true;
		return r.keepPregnant && IsPregnant(p);
	}

	private void UnmarkSlaughter(IEnumerable<Pawn> pawns, DesignationManager dm)
	{
		foreach (Pawn p in pawns)
		{
			if (p == null || !slaughterMarks.Contains(p.ThingID))
				continue;
			Designation d = CullMark(p, dm);
			if (d != null)
				dm.RemoveDesignation(d);
			slaughterMarks.Remove(p.ThingID);
		}
	}

	// Surplus is marked for slaughter, oldest first. Marks the steward itself
	// made are taken off again when the herd would otherwise fall under the
	// target (a death, a grown juvenile, a higher target, a disabled rule).
	// Bonded animals, pregnant ones (if kept) and the player's own marks stay.
	private int Cull(IEnumerable<Pawn> group, int keep, LivestockRule r, DesignationManager dm)
	{
		var list = group.ToList();
		// 1.0.3: the rule switched between slaughter and release – the steward's own
		// marks of the other kind are taken off and placed again below.
		DesignationDef wanted = r.release ? DesignationDefOf.ReleaseAnimalToWild : DesignationDefOf.Slaughter;
		foreach (Pawn p in list)
		{
			if (!slaughterMarks.Contains(p.ThingID))
				continue;
			Designation d = CullMark(p, dm);
			if (d == null || d.def == wanted)
				continue;
			dm.RemoveDesignation(d);
			slaughterMarks.Remove(p.ThingID);
		}
		foreach (Pawn p in list)
		{
			if (!HerdProtected(p, r))
				continue;
			if (!slaughterMarks.Contains(p.ThingID))
				continue;
			Designation d = CullMark(p, dm);
			if (d != null)
				dm.RemoveDesignation(d);
			slaughterMarks.Remove(p.ThingID);
		}
		int maxMarks = Math.Max(0, list.Count - keep);
		var marked = list.Where(p => CullMark(p, dm) != null).ToList();
		int marks = marked.Count;
		if (marks > maxMarks)
		{
			foreach (Pawn p in marked.OrderBy(p => p.ageTracker.AgeBiologicalTicks))
			{
				if (marks <= maxMarks)
					break;
				if (!slaughterMarks.Contains(p.ThingID))
					continue;
				Designation d = CullMark(p, dm);
				if (d != null)
					dm.RemoveDesignation(d);
				slaughterMarks.Remove(p.ThingID);
				marks--;
			}
		}
		int current = list.Count(p => CullMark(p, dm) != null);
		int surplus = list.Count - current - keep;
		if (surplus <= 0)
			return 0;
		int done = 0;
		foreach (Pawn p in list.OrderByDescending(p => p.ageTracker.AgeBiologicalTicks))
		{
			if (done >= surplus)
				break;
			if (CullMark(p, dm) != null)
				continue;
			if (HerdProtected(p, r))
				continue;
			// 1.0.3: release to the wild instead of slaughter (only species that can be released)
			DesignationDef def = r.release && CanRelease(p) ? DesignationDefOf.ReleaseAnimalToWild : DesignationDefOf.Slaughter;
			dm.AddDesignation(new Designation(p, def));
			slaughterMarks.Add(p.ThingID);
			done++;
		}
		return done;
	}

	private IntVec3 ColonyCenter()
	{
		var pawns = map.mapPawns.FreeColonistsSpawned;
		if (pawns.Count == 0)
			return map.Center;
		int x = 0, z = 0;
		foreach (Pawn p in pawns) { x += p.Position.x; z += p.Position.z; }
		return new IntVec3(x / pawns.Count, 0, z / pawns.Count);
	}

	private int MarkNearest(List<(Thing t, IntVec3 c, int y, DesignationDef d)> options, int missing, out int covered, SupplyJob owner = null)
	{
		covered = 0;
		if (options.Count == 0)
			return 0;
		IntVec3 center = ColonyCenter();
		options.Sort((a, b) => a.c.DistanceToSquared(center).CompareTo(b.c.DistanceToSquared(center)));
		int marked = 0;
		foreach (var o in options)
		{
			if (covered >= missing || marked >= MaxMarksPerJob)
				break;
			map.designationManager.AddDesignation(o.d == DesignationDefOf.Mine ? new Designation(o.c, o.d) : new Designation(o.t, o.d));
			owner?.ownMarks.Add(o.d == DesignationDefOf.Mine ? o.c.x + "," + o.c.z : o.t.ThingID);
			covered += o.y;
			marked++;
		}
		return marked;
	}
}

public sealed class LivestockRule : IExposable
{
	public ThingDef race;
	public bool enabled;
	public int females = 4;
	public int males = 1;
	public int young = 2;
	public bool keepPregnant = true;
	public bool spareYoung;     // 1.0.6: never cull the young
	// 1.0.3: surplus is released to the wild instead of slaughtered
	public bool release;
	public Area area;
	public Area appliedArea;
	// 2.52.3: animals that already got the herd area once – "Unrestricted" set by you later is kept
	public List<string> given = new List<string>();
	private bool givenLoaded;

	public bool GivenKnown => givenLoaded;

	public void ExposeData()
	{
		Scribe_Collections.Look(ref given, "given", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			givenLoaded = given != null;
			given ??= new List<string>();
		}
		Scribe_Defs.Look(ref race, "race");
		Scribe_Values.Look(ref enabled, "enabled", false);
		Scribe_Values.Look(ref females, "females", 4);
		Scribe_Values.Look(ref males, "males", 1);
		Scribe_Values.Look(ref young, "young", 2);
		Scribe_Values.Look(ref keepPregnant, "keepPregnant", true);
		Scribe_Values.Look(ref spareYoung, "spareYoung", false);
		Scribe_Values.Look(ref release, "release", false);
		Scribe_References.Look(ref area, "area");
		Scribe_References.Look(ref appliedArea, "appliedArea");
	}
}
