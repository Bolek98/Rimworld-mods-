using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 1.0.4: chained production, deep drills by target, hourly stock history.
//   Ingredients – a production target with "also keep enough ingredients"
//     adds or raises the targets of its fixed ingredients (e.g. Components 30
//     -> Steel 360). Targets are only raised, never lowered.
//   Deep drills – a mining target can switch the deep drills on its resource
//     off when the stock reaches the target and on again below it. Only drills
//     the steward switched off are switched on again.
//   History – every target keeps its stock once per in-game hour for 7 days.
// ---------------------------------------------------------------------------
public sealed class StockHistory
{
	public const int Max = 168;   // 7 days x 24 hours
	public readonly List<int> values = new List<int>();

	public void Add(int v)
	{
		values.Add(v);
		if (values.Count > Max)
			values.RemoveRange(0, values.Count - Max);
	}

	// saved as one compact line ("12,15,20") instead of 168 list items
	public void Expose(string label)
	{
		string s = Scribe.mode == LoadSaveMode.Saving ? string.Join(",", values) : null;
		Scribe_Values.Look(ref s, label);
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			values.Clear();
			if (!s.NullOrEmpty())
				foreach (string part in s.Split(','))
					if (int.TryParse(part, out int v))
						values.Add(v);
			if (values.Count > Max)
				values.RemoveRange(0, values.Count - Max);
		}
	}
}

public sealed partial class SupplyManager
{
	public List<string> drillsOffByUs = new List<string>();
	private int nextHourly;

	private void ExposeChains()
	{
		Scribe_Collections.Look(ref drillsOffByUs, "drillsOffByUs", LookMode.Value);
		Scribe_Values.Look(ref nextHourly, "nextHourly", 0);
		if (Scribe.mode == LoadSaveMode.PostLoadInit && drillsOffByUs == null)
			drillsOffByUs = new List<string>();
	}

	// ---- hourly stock history -----------------------------------------------------
	private void TickStockHistory(int now)
	{
		if (now < nextHourly)
			return;
		nextHourly = now + 2500;
		try
		{
			foreach (SupplyJob j in AllJobs())
				if (j != null && j.enabled)
					j.hourly.Add(CountStock(j, Products(j)));
			foreach (ProductionTarget t in production)
				if (t?.product != null && t.enabled)
					t.hourly.Add(ProductCount(t.product));
		}
		catch (Exception ex) { MLog.WarnOnce("[Steward] stock history: " + ex.Message, 0x5e0105); }
	}

	// ---- chained production ---------------------------------------------------------
	// The fixed ingredients of a target's recipe and how many of each the whole
	// target needs (ingredient count per batch x batches).
	public static List<(ThingDef def, int count)> IngredientNeeds(ProductionTarget t)
	{
		var result = new List<(ThingDef, int)>();
		RecipeDef r = t?.recipe;
		if (r?.ingredients == null || t.target <= 0)
			return result;
		int perBatch = 1;
		if (r.products != null)
			foreach (ThingDefCountClass p in r.products)
				if (p.thingDef == t.product && p.count > 0)
					perBatch = p.count;
		int batches = Mathf.CeilToInt(t.target / (float)perBatch);
		foreach (IngredientCount ic in r.ingredients)
		{
			// only one allowed thing (steel, components …) – "any meat" or
			// "any fabric" has no single target to raise
			if (ic == null || !ic.IsFixedIngredient)
				continue;
			ThingDef d = ic.FixedIngredient;
			if (d == null || d == t.product)
				continue;
			int n = ic.CountRequiredOfFor(d, r) * batches;
			if (n > 0)
				result.Add((d, n));
		}
		return result;
	}

	private int CheckChains()
	{
		if (!enabled)
			return 0;
		int changed = 0;
		List<ThingDef> mineable = null;
		List<(ThingDef product, RecipeDef recipe, ThingDef bench)> producible = null;
		foreach (ProductionTarget t in production.ToList())
		{
			if (t == null || !t.enabled || !t.feedIngredients || t.recipe == null || !t.recipe.AvailableNow)
				continue;
			foreach (var (d, need) in IngredientNeeds(t))
			{
				// an existing target first: mining, production, wood
				SupplyJob mj = MiningJob(d, create: false);
				if (mj != null)
				{
					if (mj.target < need) { mj.target = need; changed++; }
					continue;
				}
				ProductionTarget pt = production.FirstOrDefault(p => p != null && p.product == d);
				if (pt != null)
				{
					if (pt.target < need) { pt.target = need; changed++; }
					continue;
				}
				if (d == ThingDefOf.WoodLog)
				{
					if (forestry.target < need) { forestry.target = need; changed++; }
					continue;
				}
				// none yet: a new mining target (veins on the map) or a new
				// production target (a bench here can make it)
				mineable ??= MineableResources();
				if (mineable.Contains(d))
				{
					SupplyJob nj = MiningJob(d, create: true);
					nj.target = need;
					nj.enabled = true;
					changed++;
					continue;
				}
				producible ??= ProducibleProducts();
				foreach (var x in producible)
					if (x.product == d)
					{
						production.Add(new ProductionTarget { product = d, recipe = x.recipe, target = need });
						changed++;
						break;
					}
			}
		}
		return changed;
	}

	// ---- deep drills ------------------------------------------------------------------
	private int CheckDrills()
	{
		int changed = 0;
		var seen = new HashSet<string>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist.ToList())
		{
			if (b == null || b.TryGetComp<CompDeepDrill>() == null)
				continue;
			CompFlickable f = b.TryGetComp<CompFlickable>();
			if (f == null)
				continue;
			seen.Add(b.ThingID);
			bool ours = drillsOffByUs.Contains(b.ThingID);
			// a drill type chosen on the power page belongs to power management
			bool power = powerOn && powerDevices.Contains(b.def.defName);
			SupplyJob job = null;
			if (!power && DeepDrillUtility.GetNextResource(b.Position, map, out ThingDef res, out _, out _) && res != null)
				job = MiningJob(res, create: false);
			bool control = enabled && job != null && job.enabled && job.controlDrills && !power;
			bool isOn = WantSwitchOn(f);
			if (!control)
			{
				if (ours)
				{
					if (!isOn && !power)
						SetSwitch(b, true);
					drillsOffByUs.Remove(b.ThingID);
					changed++;
				}
				continue;
			}
			bool enough = job.lastStock >= job.target;
			if (enough && isOn)
			{
				SetSwitch(b, false);
				if (!ours)
					drillsOffByUs.Add(b.ThingID);
				changed++;
			}
			else if (!enough && ours)
			{
				if (!isOn)
					SetSwitch(b, true);
				drillsOffByUs.Remove(b.ThingID);
				changed++;
			}
		}
		// drills that are gone (destroyed, uninstalled) are forgotten
		drillsOffByUs.RemoveAll(id => id == null || !seen.Contains(id));
		return changed;
	}

	public int DrillCount(ThingDef res)
	{
		int n = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
			if (b.TryGetComp<CompDeepDrill>() != null && DeepDrillUtility.GetNextResource(b.Position, map, out ThingDef d, out _, out _) && d == res)
				n++;
		return n;
	}
}
