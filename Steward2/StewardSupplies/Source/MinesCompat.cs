using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 1.1.0: Mines 2.0 (Cain.Mineshaft). The mine is a work table with one
// "mine for X" recipe per resource. A mining target can use it as a source:
//   veins only (as before) / veins + mine / mine only.
// The steward keeps one bill of its own per resource on a mine – "do until
// you have X" with the target of the job – and removes it when the job is
// switched off. Bills the player made are never touched.
// ---------------------------------------------------------------------------
public static class MinesCompat
{
	private static bool? active;
	private static ThingDef mineDef;
	private static Dictionary<ThingDef, RecipeDef> recipes;

	public static bool Active
	{
		get
		{
			if (active == null)
			{
				mineDef = DefDatabase<ThingDef>.GetNamedSilentFail("MinesAutomated_ThingDef_Mine");
				active = mineDef != null;
			}
			return active.Value;
		}
	}

	public static ThingDef MineDef => Active ? mineDef : null;

	public static bool IsMineRecipe(RecipeDef r) =>
		r?.defName != null && r.defName.StartsWith("MinesAutomated", StringComparison.Ordinal);

	// the mine recipe for a resource (any of them – the mod makes one per product)
	public static RecipeDef RecipeFor(ThingDef res)
	{
		if (!Active || res == null)
			return null;
		if (recipes == null)
		{
			recipes = new Dictionary<ThingDef, RecipeDef>();
			try
			{
				foreach (RecipeDef r in mineDef.AllRecipes)
				{
					if (!IsMineRecipe(r) || r.products == null || r.products.Count != 1)
						continue;
					ThingDef p = r.products[0].thingDef;
					if (p != null && !recipes.ContainsKey(p))
						recipes[p] = r;
				}
			}
			catch (Exception ex) { MLog.WarnOnce("[Steward] Mines 2.0 recipes: " + ex.Message, 0x5e1100); }
		}
		recipes.TryGetValue(res, out RecipeDef rec);
		return rec;
	}

	// the mod adds resources in game (its own settings) – read the list again then
	public static void Invalidate() => recipes = null;

	public static IEnumerable<ThingDef> Resources()
	{
		if (!Active)
			yield break;
		RecipeFor(ThingDefOf.Steel);
		foreach (ThingDef d in recipes.Keys)
			yield return d;
	}

	public static List<Building> Mines(Map map)
	{
		if (!Active || map == null)
			return new List<Building>();
		return map.listerBuildings.AllBuildingsColonistOfDef(mineDef).Where(b => b.Spawned).ToList();
	}
}

public sealed partial class SupplyManager
{
	// resource defName -> unique load id of the steward's bill on a mine
	public Dictionary<string, string> mineBills = new Dictionary<string, string>();

	// 1.2.0: the steward's mine bills wait at night and during an attack
	public bool mineNotAtNight;
	public bool mineNotInRaid = true;
	private int nextMinePause;

	public void ExposeMines()
	{
		Scribe_Values.Look(ref mineNotAtNight, "mineNotAtNight", false);
		Scribe_Values.Look(ref mineNotInRaid, "mineNotInRaid", true);
		Scribe_Collections.Look(ref mineBills, "mineBills", LookMode.Value, LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit && mineBills == null)
			mineBills = new Dictionary<string, string>();
	}

	public bool HasVeins(ThingDef res)
	{
		foreach (ThingDef md in DefDatabase<ThingDef>.AllDefsListForReading)
			if (md.building?.mineableThing == res && map.listerThings.ThingsOfDef(md).Count > 0)
				return true;
		return false;
	}

	// a mine is built and the recipe for this resource is researched
	public bool MineCanMake(ThingDef res)
	{
		RecipeDef r = MinesCompat.RecipeFor(res);
		if (r == null || !r.AvailableNow)
			return false;
		foreach (Building b in MinesCompat.Mines(map))
			if (r.AvailableOnNow(b))
				return true;
		return false;
	}

	// the job takes marks from veins (not in "mine only")
	public static bool UsesVeins(SupplyJob job) => job.kind != JobKind.Mining || job.mineMode != 2;

	private Bill_Production FindMineBill(string id)
	{
		if (string.IsNullOrEmpty(id))
			return null;
		foreach (Building b in MinesCompat.Mines(map))
			foreach (Bill bill in ((IBillGiver)b).BillStack.Bills)
				if (bill is Bill_Production bp && bp.GetUniqueLoadID() == id)
					return bp;
		return null;
	}

	// a bill the player made himself for this resource
	private bool PlayerMineBill(ThingDef res)
	{
		var ours = new HashSet<string>(mineBills.Values);
		foreach (Building b in MinesCompat.Mines(map))
			foreach (Bill bill in ((IBillGiver)b).BillStack.Bills)
				if (bill is Bill_Production bp && !ours.Contains(bp.GetUniqueLoadID())
				    && MinesCompat.IsMineRecipe(bp.recipe) && bp.recipe.ProducedThingDef == res)
					return true;
		return false;
	}

	public enum MineState { None, NoMine, NoRecipe, PlayerBill, Working, Enough, Paused }

	public bool MinePauseNow(out bool raid)
	{
		raid = false;
		if (mineNotInRaid)
		{
			try { raid = GenHostility.AnyHostileActiveThreatToPlayer(map); } catch { }
			if (raid) return true;
		}
		if (mineNotAtNight)
		{
			try { return GenCelestial.CurCelestialSunGlow(map) < 0.3f; } catch { }
		}
		return false;
	}

	// every few in-game minutes: our mine bills follow night and attacks (bills of the player stay)
	public void TickMinePause(int now)
	{
		if (now < nextMinePause || mineBills.Count == 0)
			return;
		nextMinePause = now + 250;
		bool pause = MinePauseNow(out _);
		foreach (string id in mineBills.Values)
		{
			Bill_Production bp = FindMineBill(id);
			if (bp != null && bp.suspended != pause)
				bp.suspended = pause;
		}
	}

	public MineState MineStatus(SupplyJob job)
	{
		if (!MinesCompat.Active || job == null || job.mineMode == 0)
			return MineState.None;
		if (MinesCompat.RecipeFor(job.def) == null)
			return MineState.NoRecipe;
		if (MinesCompat.Mines(map).Count == 0)
			return MineState.NoMine;
		if (!MineCanMake(job.def))
			return MineState.NoRecipe;
		mineBills.TryGetValue(job.def.defName, out string id);
		Bill_Production bp = FindMineBill(id);
		if (bp == null)
			return PlayerMineBill(job.def) ? MineState.PlayerBill : MineState.Enough;
		if (bp.suspended) return MineState.Paused;
		return job.lastStock >= job.target ? MineState.Enough : MineState.Working;
	}

	// the source changed in the window – the bill follows at once
	public void ForceMinesCheck()
	{
		try { CheckMines(); }
		catch (Exception ex) { MLog.WarnOnce("[Steward] mines: " + ex.Message, 0x5e1101); }
	}

	private int CheckMines()
	{
		if (!MinesCompat.Active)
			return 0;
		MinesCompat.Invalidate();   // resources added in the mod's settings
		int changed = 0;
		foreach (SupplyJob job in mining)
		{
			if (job?.def == null)
				continue;
			string key = job.def.defName;
			mineBills.TryGetValue(key, out string id);
			Bill_Production bp = FindMineBill(id);
			bool want = enabled && job.enabled && job.mineMode != 0 && MineCanMake(job.def) && !PlayerMineBill(job.def);
			if (!want)
			{
				if (bp != null)
				{
					bp.billStack?.Delete(bp);
					changed++;
				}
				mineBills.Remove(key);
				continue;
			}
			if (bp != null)
			{
				if (bp.targetCount != job.target)
					bp.targetCount = job.target;
				continue;
			}
			RecipeDef r = MinesCompat.RecipeFor(job.def);
			Building mine = MinesCompat.Mines(map)
				.Where(b => r.AvailableOnNow(b))
				.OrderBy(b => ((IBillGiver)b).BillStack.Count)
				.FirstOrDefault();
			if (mine == null)
				continue;
			bp = r.MakeNewBill() as Bill_Production;
			if (bp == null)
				continue;
			bp.repeatMode = BillRepeatModeDefOf.TargetCount;
			bp.targetCount = job.target;
			bp.SetStoreMode(BillStoreModeDefOf.BestStockpile);
			((IBillGiver)mine).BillStack.AddBill(bp);
			mineBills[key] = bp.GetUniqueLoadID();
			changed++;
		}
		// bills of resources that have no job any more
		foreach (string key in mineBills.Keys.ToList())
			if (!mining.Any(m => m?.def != null && m.def.defName == key))
			{
				Bill_Production old = FindMineBill(mineBills[key]);
				old?.billStack?.Delete(old);
				mineBills.Remove(key);
			}
		return changed;
	}
}
