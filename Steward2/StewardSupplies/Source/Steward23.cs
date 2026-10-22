using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.3: production targets through work bills.
// A target ("30 simple meals") becomes one "do until 30" bill on the best
// workbench that can make it. The steward keeps that bill: creates it again
// if it is deleted, moves it when the bench is gone, updates the number,
// suspends it when the target is switched off and removes it when the target
// is removed. Bills the player made by hand are never touched.
// ---------------------------------------------------------------------------
public sealed class ProductionTarget : IExposable
{
	public ThingDef product;
	public RecipeDef recipe;
	public int target = 20;
	public bool enabled = true;
	public string billId;
	// 2.15: the steward paused the bill itself (switched off). A bill you
	// paused by hand stays paused.
	public bool pausedByUs;

	// The game can count "do until X" only for recipes with exactly one product
	// and no special products. Others (several products) are counted by the
	// steward: the bill runs "forever" and is paused when the stock is reached.
	public bool SelfCounted => recipe != null && (recipe.products == null || recipe.products.Count != 1 || recipe.specialProducts != null);

	public void ExposeData()
	{
		Scribe_Values.Look(ref pausedByUs, "pausedByUs", false);
		Scribe_Defs.Look(ref product, "product");
		Scribe_Defs.Look(ref recipe, "recipe");
		Scribe_Values.Look(ref target, "target", 20);
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref billId, "billId");
	}
}

public sealed partial class SupplyManager
{
	// ---- production ------------------------------------------------------------
	// 2.16.2: a copy – bills and buildings can change while it is walked
	private IEnumerable<Building> Benches()
	{
		foreach (Building b in map.listerBuildings.allBuildingsColonist.ToList())
			if (b is IBillGiver && b.def.AllRecipes != null && b.def.AllRecipes.Count > 0)
				yield return b;
	}

	// Products that can be made on a bench this colony has: product -> recipe.
	public List<(ThingDef product, RecipeDef recipe, ThingDef bench)> ProducibleProducts()
	{
		var seen = new HashSet<ThingDef>(production.Select(p => p.product));
		var result = new List<(ThingDef, RecipeDef, ThingDef)>();
		foreach (Building b in Benches())
			foreach (RecipeDef r in b.def.AllRecipes)
			{
				// 2.15: also recipes with several products (counted by the steward)
				if (r.products == null || r.products.Count == 0 || !r.AvailableNow)
					continue;
				ThingDef prod = r.products[0].thingDef;
				if (prod == null || !seen.Add(prod))
					continue;
				result.Add((prod, r, b.def));
			}
		return result.OrderBy(x => x.Item1.label).ToList();
	}

	// Every product of every bench (also those already added), for the picker.
	public List<(ThingDef product, RecipeDef recipe, ThingDef bench)> AllProducibleProducts()
	{
		var seen = new HashSet<(ThingDef, ThingDef)>();
		var result = new List<(ThingDef, RecipeDef, ThingDef)>();
		foreach (Building b in Benches())
			foreach (RecipeDef r in b.def.AllRecipes)
			{
				if (r.products == null || r.products.Count == 0 || !r.AvailableNow)
					continue;
				ThingDef prod = r.products[0].thingDef;
				if (prod == null || !seen.Add((prod, b.def)))
					continue;
				result.Add((prod, r, b.def));
			}
		return result;
	}

	// 2.5: "do until X" bills made elsewhere (vanilla, Dubs Mint Menus) that
	// the steward does not manage yet – they can be adopted with one click.
	public List<(Bill_Production bill, Building bench)> AdoptableBills()
	{
		var mine = new HashSet<string>(production.Where(p => p.billId != null).Select(p => p.billId));
		// 2.17: the self-supply bills are the steward's too
		foreach (AutoBill a in autoBills)
			if (a?.billId != null)
				mine.Add(a.billId);
		var result = new List<(Bill_Production, Building)>();
		foreach (Building b in Benches())
		{
			BillStack stack = ((IBillGiver)b).BillStack;
			if (stack == null)
				continue;
			foreach (Bill bill in stack.Bills)
				if (bill is Bill_Production bp && bp.repeatMode == BillRepeatModeDefOf.TargetCount
				    && bp.recipe?.products != null && bp.recipe.products.Count == 1
				    && !mine.Contains(bp.GetUniqueLoadID()))
					result.Add((bp, b));
		}
		return result;
	}

	public void Adopt(Bill_Production bill)
	{
		ThingDef prod = bill.recipe.products[0].thingDef;
		ProductionTarget existing = production.FirstOrDefault(p => p.product == prod);
		if (existing != null)
		{
			// 2.15: the old steward bill of this product is removed, otherwise
			// both kept running and two batches were made
			Bill_Production old = FindBill(existing, out Building oldBench);
			if (old != null && old != bill && oldBench is IBillGiver oldGiver)
				oldGiver.BillStack.Delete(old);
			existing.pausedByUs = false;
			existing.billId = bill.GetUniqueLoadID();
			existing.recipe = bill.recipe;
			existing.target = bill.targetCount;
			return;
		}
		production.Add(new ProductionTarget { product = prod, recipe = bill.recipe, target = bill.targetCount, billId = bill.GetUniqueLoadID() });
	}

	public static MainButtonDef MintBillTab => MintMenusActive ? DefDatabase<MainButtonDef>.GetNamedSilentFail("MintBillMenu") : null;

	public static bool MintMenusActive => ModsConfig.ActiveModsInLoadOrder.Any(m => (m.PackageId ?? "").ToLowerInvariant().Contains("dubsmintmenus") || (m.PackageId ?? "").ToLowerInvariant().Contains("mintmenus"));

	public Bill_Production FindBill(ProductionTarget t, out Building bench)
	{
		bench = null;
		if (string.IsNullOrEmpty(t.billId))
			return null;
		foreach (Building b in Benches())
		{
			BillStack stack = ((IBillGiver)b).BillStack;
			if (stack == null)
				continue;
			foreach (Bill bill in stack.Bills)
				if (bill is Bill_Production bp && bp.GetUniqueLoadID() == t.billId)
				{
					bench = b;
					return bp;
				}
		}
		return null;
	}

	// 2.16: the best bench, not the first one – a working bench with power
	// before one without, a real workbench before a campfire or a crafting
	// spot, the fastest one (work speed) before slower ones, then the nearest.
	private Building BestBench(RecipeDef recipe)
	{
		IntVec3 center = ColonyCenter();
		return Benches()
			.Where(b => b.def.AllRecipes.Contains(recipe) && !b.IsForbidden(Faction.OfPlayer) && !b.IsBrokenDown())
			.OrderByDescending(b => b.GetComp<CompPowerTrader>() == null || b.GetComp<CompPowerTrader>().PowerOn)
			.ThenBy(b => IsMakeshift(b) ? 1 : 0)
			.ThenByDescending(b => b.GetStatValue(StatDefOf.WorkTableWorkSpeedFactor))
			.ThenBy(b => b.Position.DistanceToSquared(center))
			.FirstOrDefault();
	}

	// campfires and "spots" (crafting spot, butcher spot …)
	private static bool IsMakeshift(Building b)
	{
		string n = b.def.defName.ToLowerInvariant();
		return n.Contains("campfire") || n.Contains("spot");
	}

	// 2.15: saves from before 2.15 do not know which paused bills the steward
	// paused – once, all paused steward bills are treated as paused by it
	private int prodVersion;

	public void ExposeProductionVersion()
	{
		Scribe_Values.Look(ref prodVersion, "prodVersion", 0);
	}

	private int CheckProduction()
	{
		int changed = 0;
		if (prodVersion < 1)
		{
			prodVersion = 1;
			foreach (ProductionTarget pt in production)
			{
				Bill_Production b0 = pt == null ? null : FindBill(pt, out _);
				if (b0 != null && b0.suspended && !IsMealPaused(b0))
					pt.pausedByUs = true;
			}
		}
		foreach (ProductionTarget t in production.ToList())
		{
			Bill_Production bill = FindBill(t, out Building bench);
			if (!enabled || !t.enabled)
			{
				if (bill != null && !bill.suspended)
				{
					bill.suspended = true;
					t.pausedByUs = true;
					changed++;
				}
				continue;
			}
			if (bill == null)
			{
				// 2.15: no bill for a recipe that is not researched yet
				if (t.recipe == null || !t.recipe.AvailableNow)
					continue;
				Building best = BestBench(t.recipe);
				if (best == null)
					continue;
				bill = t.recipe.MakeNewBill() as Bill_Production;
				if (bill == null)
					continue;
				if (t.SelfCounted)
				{
					bill.repeatMode = BillRepeatModeDefOf.Forever;
				}
				else
				{
					bill.repeatMode = BillRepeatModeDefOf.TargetCount;
					bill.targetCount = t.target;
				}
				t.pausedByUs = false;
				if (haulProducts)
					bill.SetStoreMode(BillStoreModeDefOf.BestStockpile);
				((IBillGiver)best).BillStack.AddBill(bill);
				t.billId = bill.GetUniqueLoadID();
				changed++;
				continue;
			}
			if (t.SelfCounted)
			{
				// several products: the steward counts the first one in storage
				bool enough = StockOf(t.product) >= t.target;
				if (enough && !bill.suspended) { bill.suspended = true; t.pausedByUs = true; changed++; }
				else if (!enough && bill.suspended && t.pausedByUs && !IsMealPaused(bill)) { bill.suspended = false; t.pausedByUs = false; changed++; }
				if (bill.repeatMode != BillRepeatModeDefOf.Forever) bill.repeatMode = BillRepeatModeDefOf.Forever;
			}
			else
			{
				// 2.15: only bills the steward paused itself are switched on again –
				// a bill you paused by hand stays paused; the meal target too
				if (bill.suspended && t.pausedByUs && !IsMealPaused(bill)) { bill.suspended = false; t.pausedByUs = false; changed++; }
				if (bill.repeatMode != BillRepeatModeDefOf.TargetCount) bill.repeatMode = BillRepeatModeDefOf.TargetCount;
				if (bill.targetCount != t.target) bill.targetCount = t.target;
			}
			// "Drop on floor" leaves the goods at the bench; the steward's bills
			// take them to the best stockpile (a specific stockpile is kept).
			if (haulProducts && bill.GetStoreMode() == BillStoreModeDefOf.DropOnFloor)
				bill.SetStoreMode(BillStoreModeDefOf.BestStockpile);
		}
		return changed;
	}

	// Butcher and cook bills set to "drop on the floor" leave meat at the table,
	// and the next meal then takes meat from storage instead. Carrying finished
	// goods means the worker takes the product to a stockpile before the job ends.
	// 2.7.1: only the steward's own bills. "Drop on floor" is never the game's
	// default – a player bill that has it was set so on purpose (and in 2.6.4
	// it could not be kept: it flipped back every few seconds).
	// 2.18: runs every 250 ticks – one walk over the benches instead of a
	// copy of every colony building and a search per production target
	public void PreferStockpileForBills()
	{
		if (!haulProducts || production.Count == 0)
			return;
		HashSet<string> ids = null;
		foreach (ProductionTarget t in production)
			if (t?.billId != null)
				(ids ??= new HashSet<string>()).Add(t.billId);
		if (ids == null)
			return;
		List<Building> all = map.listerBuildings.allBuildingsColonist;
		for (int i = 0; i < all.Count; i++)
		{
			if (!(all[i] is IBillGiver g) || g.BillStack == null)
				continue;
			List<Bill> bills = g.BillStack.Bills;
			for (int k = 0; k < bills.Count; k++)
				if (bills[k] is Bill_Production bp && bp.GetStoreMode() == BillStoreModeDefOf.DropOnFloor && ids.Contains(bp.GetUniqueLoadID()))
					bp.SetStoreMode(BillStoreModeDefOf.BestStockpile);
		}
	}

	// The steward is off, or power management is off: switch back on whatever
	// this mod switched off. Devices the player switched off were never recorded.
	public void ReleasePower()
	{
		if (poweredOffByUs == null || poweredOffByUs.Count == 0)
			return;
		// 2.17: switched on through the remembered things; a device that is
		// uninstalled right now stays in the list and is switched on once it is
		// placed again. Devices that cannot be found are forgotten (as before).
		PrunePower(true);
		foreach (string id in poweredOffByUs.ToList())
		{
			if (!powerRefs.TryGetValue(id, out Thing t))
			{
				poweredOffByUs.Remove(id);
				continue;
			}
			if (!t.Spawned)
				continue;
			SetSwitch(t, true);
			poweredOffByUs.Remove(id);
			powerRefs.Remove(id);
		}
	}

	// 2.17: the devices the steward switched off, by ThingID (not saved).
	// Filled when a device is switched off, and after loading by one search
	// of the map (buildings and uninstalled ones).
	private readonly Dictionary<string, Thing> powerRefs = new Dictionary<string, Thing>();
	private int nextPowerResolve;

	// 2.17: an ID is forgotten only when its device is really gone (destroyed),
	// or when the player switched the device on again – the steward then never
	// switches on later what the player switched off himself. Duplicates go.
	private void PrunePower(bool resolveNow)
	{
		if (poweredOffByUs.Count == 0)
		{
			powerRefs.Clear();
			return;
		}
		var seen = new HashSet<string>();
		poweredOffByUs.RemoveAll(id => id == null || !seen.Add(id));
		bool unresolved = false;
		foreach (string id in poweredOffByUs)
			if (!powerRefs.ContainsKey(id)) { unresolved = true; break; }
		int now = Find.TickManager.TicksGame;
		if (unresolved && (resolveNow || now >= nextPowerResolve))
		{
			nextPowerResolve = now + 2500;
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
				if (b.GetComp<CompFlickable>() != null && seen.Contains(b.ThingID))
					powerRefs[b.ThingID] = b;
			foreach (Thing m in map.listerThings.ThingsInGroup(ThingRequestGroup.MinifiedThing))
			{
				Thing inner = (m as MinifiedThing)?.InnerThing;
				if (inner != null && seen.Contains(inner.ThingID))
					powerRefs[inner.ThingID] = inner;
			}
		}
		poweredOffByUs.RemoveAll(id =>
		{
			if (!powerRefs.TryGetValue(id, out Thing t))
				return false;
			CompFlickable f = t.TryGetComp<CompFlickable>();
			return t.Destroyed || f == null || WantSwitchOn(f);
		});
		if (powerRefs.Count > poweredOffByUs.Count)
			foreach (string id in powerRefs.Keys.ToList())
				if (!seen.Contains(id) || !poweredOffByUs.Contains(id))
					powerRefs.Remove(id);
	}

	public void SuspendProduction()
	{
		foreach (ProductionTarget t in production.ToList())
		{
			if (t == null)
				continue;
			Bill_Production bill = FindBill(t, out _);
			if (bill != null && !bill.suspended)
			{
				bill.suspended = true;
				t.pausedByUs = true;
			}
		}
	}

	// in storage, not forbidden
	private int StockOf(ThingDef d)
	{
		if (d == null)
			return 0;
		int n = 0;
		foreach (Thing th in map.listerThings.ThingsOfDef(d))
			if (th.Spawned && th.IsInAnyStorage() && !th.IsForbidden(Faction.OfPlayer))
				n += th.stackCount;
		return n;
	}

	public void RemoveProduction(ProductionTarget t)
	{
		Bill_Production bill = FindBill(t, out Building bench);
		if (bill != null && bench is IBillGiver giver)
			giver.BillStack.Delete(bill);
		production.Remove(t);
	}

	public int ProductCount(ThingDef d) => map.resourceCounter.GetCount(d);

	// ---- power -------------------------------------------------------------------
	private static readonly AccessTools.FieldRef<CompFlickable, bool> WantSwitchOn =
		AccessTools.FieldRefAccess<CompFlickable, bool>("wantSwitchOn");

	public List<ThingDef> SwitchableDevices()
	{
		var set = new HashSet<ThingDef>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			var pt = b.GetComp<CompPowerTrader>();
			if (pt != null && pt.Props.PowerConsumption > 0f && b.GetComp<CompFlickable>() != null)
				set.Add(b.def);
		}
		return set.OrderBy(d => d.label).ToList();
	}

	public static void SetSwitch(Thing t, bool on)
	{
		CompFlickable f = t.TryGetComp<CompFlickable>();
		if (f == null)
			return;
		WantSwitchOn(f) = on;
		f.SwitchIsOn = on;
		t.Map?.designationManager.DesignationOn(t, DesignationDefOf.Flick)?.Delete();
	}

	// Each power net with batteries on its own: below "low" the chosen devices
	// are switched off, above "high" the steward switches back on what it
	// switched off. Devices the player switched off stay off.
	// 2.15: power is managed device by device, not by type. When batteries
	// run low (or a net without batteries cannot feed everything), only as
	// many chosen devices are switched off as needed to balance the net – the
	// biggest consumers first. Devices you exempted (gizmo "Steward may switch
	// off") are never touched. They are switched back on one by one when the
	// batteries are charged again, or – without batteries – when the
	// generators have enough spare power for them.
	private static float SupplyW(PowerNet net)
	{
		float w = 0f;
		foreach (CompPowerTrader c in net.powerComps)
			if (c.Props.PowerConsumption < 0f && c.PowerOutput > 0f)
				w += c.PowerOutput;
		return w;
	}

	// 2.23.0: what the devices really draw now (idle benches, thermostats and
	// lamps draw less than their label says). Only a device that is switched
	// on but gets no power counts with its full need. Before, the full need of
	// every device was counted and the steward switched things off although
	// there was enough power.
	private static float DemandW(PowerNet net)
	{
		float w = 0f;
		foreach (CompPowerTrader c in net.powerComps)
		{
			if (c.Props.PowerConsumption <= 0f)
				continue;
			CompFlickable f = c.parent.TryGetComp<CompFlickable>();
			if (f != null && !f.SwitchIsOn)
				continue;
			if (c.PowerOn)
				w += Mathf.Max(0f, -c.PowerOutput);
			else
				w += c.Props.PowerConsumption;
		}
		return w;
	}

	private static bool HasWorkToDo(Thing t)
	{
		if (!(t is IBillGiver g) || g.BillStack == null)
			return false;
		foreach (Bill b in g.BillStack)
			if (b != null && !b.suspended && b.ShouldDoNow())
				return true;
		return false;
	}

	private void CheckPower()
	{
		PrunePower(false);   // 2.17
		var mine = new HashSet<string>(poweredOffByUs);
		var chosen = new HashSet<string>(powerDevices);
		foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
		{
			// a net without any generator or battery cannot be helped by switching off
			if (!net.hasPowerSource)
				continue;
			bool battery = net.batteryComps.Count > 0;
			float max = battery ? net.batteryComps.Sum(b => b.Props.storedEnergyMax) : 0f;
			float pct = max > 0f ? net.batteryComps.Sum(b => b.StoredEnergy) / max : 0f;
			float balance = SupplyW(net) - DemandW(net);
			bool shed = battery ? pct < powerLow && balance < 0f : balance < 0f;
			if (shed)
			{
				var candidates = net.powerComps
					.Where(c => c.Props.PowerConsumption > 0f && chosen.Contains(c.parent.def.defName)
					            && !powerExempt.Contains(c.parent.ThingID) && !HasWorkToDo(c.parent)
					            && c.parent.TryGetComp<CompFlickable>() is CompFlickable f && f.SwitchIsOn)
					.OrderByDescending(c => c.Props.PowerConsumption)
					.ToList();
				foreach (CompPowerTrader c in candidates)
				{
					if (balance >= 0f)
						break;
					SetSwitch(c.parent, false);
					// 2.17: no duplicates; the device is remembered for later
					string offId = c.parent.ThingID;
					if (!poweredOffByUs.Contains(offId))
						poweredOffByUs.Add(offId);
					powerRefs[offId] = c.parent;
					balance += c.Props.PowerConsumption;
				}
			}
			// 2.24.1: a workbench with a bill to do (smelter, crematorium …) is switched
			// back on at once – off, nobody could ever work its bills
			foreach (CompPowerTrader c in net.powerComps.Where(c => mine.Contains(c.parent.ThingID) && HasWorkToDo(c.parent)).ToList())
			{
				SetSwitch(c.parent, true);
				poweredOffByUs.Remove(c.parent.ThingID);
				powerRefs.Remove(c.parent.ThingID);
				mine.Remove(c.parent.ThingID);
				balance -= c.Props.PowerConsumption;
			}
			if (!shed && mine.Count > 0 && (battery ? pct > powerHigh : balance > 0f))
			{
				// smallest first, only while there is room for them
				var back = net.powerComps.Where(c => mine.Contains(c.parent.ThingID))
					.OrderBy(c => c.Props.PowerConsumption).ToList();
				foreach (CompPowerTrader c in back)
				{
					if (!battery && balance - c.Props.PowerConsumption < 0f)
						break;
					SetSwitch(c.parent, true);
					poweredOffByUs.Remove(c.parent.ThingID);
					powerRefs.Remove(c.parent.ThingID);   // 2.17
					balance -= c.Props.PowerConsumption;
				}
			}
		}
		// 2.17: gone things are pruned in PrunePower – only destroyed ones; an
		// uninstalled device is kept and switched on again after it is placed
	}

	public void PowerSummary(out float gain, out float stored, out float max)
	{
		gain = stored = max = 0f;
		foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
		{
			if (!net.hasPowerSource)
				continue;
			gain += net.CurrentEnergyGainRate() / CompPower.WattsToWattDaysPerTick;
			stored += net.CurrentStoredEnergy();
			max += net.batteryComps.Sum(b => b.Props.storedEnergyMax);
		}
	}

	// ---- cancel everything the steward marked -------------------------------------
	public int CancelAllMarks()
	{
		var own = new HashSet<string>();
		foreach (SupplyJob j in AllJobs())
		{
			own.UnionWith(j.ownMarks);
			j.ownMarks.Clear();
		}
		own.UnionWith(slaughterMarks);
		slaughterMarks.Clear();
		int removed = 0;
		DesignationManager dm = map.designationManager;
		foreach (DesignationDef dd in new[] { DesignationDefOf.Hunt, DesignationDefOf.Mine, DesignationDefOf.CutPlant, DesignationDefOf.HarvestPlant, DesignationDefOf.Slaughter })
			foreach (Designation d in dm.SpawnedDesignationsOfDef(dd).ToList())
			{
				string key = d.target.HasThing ? d.target.Thing.ThingID : d.target.Cell.x + "," + d.target.Cell.z;
				if (own.Contains(key))
				{
					dm.RemoveDesignation(d);
					removed++;
				}
			}
		removed += CancelTracked(dm, chunkMarks, DesignationDefOf.Haul);
		removed += CancelTracked(dm, clearMarks, DesignationDefOf.CutPlant);
		removed += CancelTracked(dm, blightMarks, DesignationDefOf.CutPlant);
		Messages.Message("CF_X_Cancelled".Translate(removed), MessageTypeDefOf.SilentInput, historical: false);
		return removed;
	}

	private int CancelTracked(DesignationManager dm, List<string> ids, DesignationDef def)
	{
		if (ids == null || ids.Count == 0 || def == null)
			return 0;
		var own = new HashSet<string>(ids);
		ids.Clear();
		int removed = 0;
		foreach (Designation d in dm.SpawnedDesignationsOfDef(def).ToList())
		{
			string key = d.target.HasThing ? d.target.Thing.ThingID : d.target.Cell.x + "," + d.target.Cell.z;
			if (!own.Contains(key))
				continue;
			dm.RemoveDesignation(d);
			removed++;
		}
		return removed;
	}

	// ---- cleaning ------------------------------------------------------------------
	public static List<RoomRoleDef> CleanableRoles()
	{
		return DefDatabase<RoomRoleDef>.AllDefsListForReading
			.Where(r => r != RoomRoleDefOf.None && r.defName != "Outdoors" && r.defName != "Room")
			.OrderBy(r => r.label)
			.ToList();
	}

	public int FilthInPriorityRooms()
	{
		int n = 0;
		var roles = new HashSet<string>(cleanRoles);
		foreach (Thing f in map.listerFilthInHomeArea.FilthInHomeArea)
		{
			Room room = f.GetRoom();
			if (room != null && room.Role != null && roles.Contains(room.Role.defName))
				n++;
		}
		return n;
	}

	// ---- who does the household jobs ------------------------------------------------
	// The N colonists allowed to do this work who have the fewest top-priority
	// jobs, faster walkers first. Recomputed every in-game hour.
	private readonly Dictionary<WorkTypeDef, (int tick, HashSet<Pawn> pawns)> chosenWorkers =
		new Dictionary<WorkTypeDef, (int, HashSet<Pawn>)>();

	public bool IsChosenWorker(Pawn pawn, WorkTypeDef work, int count)
	{
		if (count <= 0 || work == null)
			return true;
		int now = Find.TickManager.TicksGame;
		if (!chosenWorkers.TryGetValue(work, out var c) || now - c.tick > 2500)
		{
			var list = map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned)
				.Where(p => !p.WorkTypeIsDisabled(work) && p.workSettings != null && p.workSettings.WorkIsActive(work))
				.OrderBy(p => TopJobs(p))
				.ThenByDescending(p => p.GetStatValue(StatDefOf.MoveSpeed))
				.Take(count)
				.ToList();
			c = (now, new HashSet<Pawn>(list));
			chosenWorkers[work] = c;
		}
		return c.pawns.Contains(pawn);
	}

	private static int TopJobs(Pawn p)
	{
		int n = 0;
		foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			if (p.workSettings.GetPriority(w) == 1)
				n++;
		return n;
	}

	public string ChosenWorkerNames(WorkTypeDef work, int count)
	{
		if (count <= 0)
			return "CF_H_Everyone".Translate();
		IsChosenWorker(null, work, count);
		return chosenWorkers.TryGetValue(work, out var c) && c.pawns.Count > 0
			? string.Join(", ", c.pawns.Select(p => p.LabelShort))
			: (string)"CF_H_Nobody".Translate();
	}

	// ---- other maps --------------------------------------------------------------------
	public static IEnumerable<Map> OtherHomeMaps(Map current)
	{
		foreach (Map m in Find.Maps)
			if (m != current && m.IsPlayerHome)
				yield return m;
	}
}

// ---------------------------------------------------------------------------
// Cleaning: dirt in the rooms that matter (kitchen, hospital, dining room by
// default) is cleaned before dirt anywhere else. Same job as the game's own
// cleaning, just with a higher priority and only those rooms.
// ---------------------------------------------------------------------------
public class WorkGiver_StewardCleanPriority : WorkGiver_CleanFilth
{
	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		SupplyManager sm = SupplyManager.For(pawn.Map);
		if (sm == null || !sm.cleanPriority || sm.cleanRoles.Count == 0)
			return Enumerable.Empty<Thing>();
		var roles = new HashSet<string>(sm.cleanRoles);
		IntVec3 at = pawn.Position;
		// 2.9.1: the nearest 40 only – fewer checks per work search
		return pawn.Map.listerFilthInHomeArea.FilthInHomeArea.Where(f =>
		{
			Room room = f.GetRoom();
			return room != null && room.Role != null && roles.Contains(room.Role.defName);
		}).OrderBy(f => f.Position.DistanceToSquared(at)).Take(40).ToList();
	}

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		SupplyManager sm = SupplyManager.For(pawn.Map);
		if (sm == null || !sm.cleanPriority || sm.cleanRoles.Count == 0
		    || pawn.Map.listerFilthInHomeArea.FilthInHomeArea.Count == 0)
			return true;
		return !forced && !sm.IsChosenWorker(pawn, WorkTypeDefOf.Cleaning, sm.cleanWorkers);
	}

	// Undefined request: the game then uses PotentialWorkThingsGlobal (only the
	// priority rooms) instead of scanning every filth nearby.
	public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForGroup(ThingRequestGroup.Undefined);
}

// ---------------------------------------------------------------------------
// 2.6: finished goods lying next to a workbench, stove or crafting spot are
// carried to storage before other hauling – meals do not cool and rot at the
// stove, clothes and medicine do not pile up at the bench. Part of Hauling.
// ---------------------------------------------------------------------------
public class WorkGiver_StewardHaulProducts : WorkGiver_Scanner
{
	public static void Reset() { benchCells.Clear(); benchCache.Clear(); }
	public static void Forget(Map m) { benchCells.Remove(m); benchCache.Remove(m); }

	private static readonly Dictionary<Map, (int tick, HashSet<IntVec3> cells)> benchCells =
		new Dictionary<Map, (int, HashSet<IntVec3>)>();

	public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

	public override Danger MaxPathDanger(Pawn pawn) => Danger.Deadly;

	private static HashSet<IntVec3> NearBenches(Map map)
	{
		int now = Find.TickManager.TicksGame;
		if (benchCells.TryGetValue(map, out var c) && now - c.tick < 250)
			return c.cells;
		var set = new HashSet<IntVec3>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver))
				continue;
			foreach (IntVec3 cell in GenRadial.RadialCellsAround(b.Position, 2.9f + Mathf.Max(b.def.size.x, b.def.size.z) / 2f, true))
				if (cell.InBounds(map))
					set.Add(cell);
		}
		benchCells[map] = (now, set);
		return set;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		Map map = pawn.Map;
		SupplyManager sm = SupplyManager.For(map);
		if (sm == null || !sm.haulProducts)
			return Enumerable.Empty<Thing>();
		HashSet<IntVec3> near = NearBenches(map);
		if (near.Count == 0)
			return Enumerable.Empty<Thing>();
		return map.listerHaulables.ThingsPotentiallyNeedingHauling()
			.Where(t => t.Spawned && near.Contains(t.Position) && !t.IsInAnyStorage() && t.def.category == ThingCategory.Item
			            && !(t is Corpse) && IsFinishedProduct(map, t))
			.OrderBy(t => t.Position.DistanceToSquared(pawn.Position))
			.Take(40)
			.ToList();
	}

	// A product of a nearby bill. Butchery meat and leather are not listed on the
	// recipe, so they have to be recognized separately. An ingredient waiting on
	// the bench that will use it is left alone.
	// 2.7.1: the workbenches of a map are collected once per 250 ticks instead
	// of walking every colony building for every item on every work scan.
	private static readonly Dictionary<Map, (int tick, List<Building> benches)> benchCache =
		new Dictionary<Map, (int, List<Building>)>();

	private static List<Building> Benches(Map map)
	{
		int now = Find.TickManager.TicksGame;
		if (benchCache.TryGetValue(map, out var c) && now - c.tick < 250)
			return c.benches;
		var list = new List<Building>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
			if (b is IBillGiver giver && giver.BillStack != null)
				list.Add(b);
		benchCache[map] = (now, list);
		return list;
	}

	private static bool IsFinishedProduct(Map map, Thing t)
	{
		// 2.15: a raw material waiting at the work spot of a nearby bench
		// (meat at the stove next to the butcher table …) is an ingredient,
		// not a product – it stays where the cook put it
		foreach (Building b in Benches(map))
		{
			if (!(b is IBillGiver g) || g.BillStack == null || !b.Spawned)
				continue;
			if (b.InteractionCell.DistanceToSquared(t.Position) > 2)
				continue;
			foreach (Bill bill in g.BillStack.Bills)
				if (bill?.recipe != null && !bill.suspended && !BillProduces(bill, t) && bill.IsFixedOrAllowedIngredient(t))
					return false;
		}
		bool product = false;
		foreach (Building b in Benches(map))
		{
			if (!(b is IBillGiver giver) || giver.BillStack == null || !b.Spawned)
				continue;
			float radius = 2.9f + Mathf.Max(b.def.size.x, b.def.size.z) / 2f;
			if (b.Position.DistanceToSquared(t.Position) > radius * radius)
				continue;
			bool benchButchers = (t.def.IsMeat || t.def.IsLeather) && BenchButchers(b);
			if (benchButchers)
				product = true;
			foreach (Bill bill in giver.BillStack.Bills)
			{
				if (bill?.recipe == null)
					continue;
				bool produced = BillProduces(bill, t);
				if (produced)
					product = true;
				if (!produced && !benchButchers && !bill.suspended && t.Position == b.InteractionCell && bill.IsFixedOrAllowedIngredient(t))
					return false;
			}
		}
		return product;
	}

	private static bool BillProduces(Bill bill, Thing t)
	{
		RecipeDef recipe = bill.recipe;
		if (recipe.products != null)
		{
			for (int i = 0; i < recipe.products.Count; i++)
				if (recipe.products[i].thingDef == t.def)
					return true;
		}
		if (recipe.specialProducts == null)
			return false;
		if (recipe.specialProducts.Contains(SpecialProductType.Butchery) && (t.def.IsMeat || t.def.IsLeather))
			return true;
		// 2.15: smelting – the metal that comes out is carried away too
		return recipe.specialProducts.Contains(SpecialProductType.Smelted) && t.def.IsStuff && t.def.stuffProps?.categories != null
		       && t.def.stuffProps.categories.Contains(StuffCategoryDefOf.Metallic);
	}

	private static bool BenchButchers(Building b)
	{
		List<RecipeDef> recipes = b.def.AllRecipes;
		if (recipes == null)
			return false;
		for (int i = 0; i < recipes.Count; i++)
			if (recipes[i].specialProducts != null && recipes[i].specialProducts.Contains(SpecialProductType.Butchery))
				return true;
		return false;
	}

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		SupplyManager sm = SupplyManager.For(pawn.Map);
		return sm == null || !sm.haulProducts || pawn.Map.listerHaulables.ThingsPotentiallyNeedingHauling().Count == 0;
	}

	public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, forced) || ModCompat.NoHaul(t))
			return null;
		if (!IsFinishedProduct(pawn.Map, t))
			return null;
		return PickUpAndHaulCompat.HaulToStorage(pawn, t, forced);
	}
}
