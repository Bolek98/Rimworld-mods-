using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.12: target for ready meals, in days. The steward counts all
// cooked meals the colony has (in storage or lying in the home area) and
// divides them by what the colony eats in a day. At or above the target,
// every bill that makes meals is paused; when the stock drops below the
// target again, the bills paused by the steward are switched back on.
// Bills you paused yourself are never switched on.
// ---------------------------------------------------------------------------
public sealed partial class SupplyManager
{
	public bool mealTargetOn;
	public float mealTargetDays = 3f;
	public List<string> mealPaused = new List<string>();
	public float lastMealDays = -1f;
	private int nextMealCheck;

	private void ExposeMeals()
	{
		Scribe_Values.Look(ref mealTargetOn, "mealTargetOn", false);
		Scribe_Values.Look(ref mealTargetDays, "mealTargetDays", 3f);
		Scribe_Collections.Look(ref mealPaused, "mealPaused", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit && mealPaused == null)
			mealPaused = new List<string>();
	}

	public static bool IsMeal(ThingDef d) => d != null && d.ingestible != null && d.ingestible.IsMeal;

	public static bool MakesMeals(RecipeDef r)
	{
		if (r?.products == null)
			return false;
		for (int i = 0; i < r.products.Count; i++)
			if (IsMeal(r.products[i].thingDef))
				return true;
		return false;
	}

	// Days of ready meals for everyone who eats (colonists, slaves, prisoners).
	public float MealDays()
	{
		int eaters = MapDemand.Eaters(map);
		if (eaters <= 0)
			return 99f;
		float nutrition = 0f;
		Area_Home home = map.areaManager.Home;
		List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (!IsMeal(t.def) || !t.Spawned || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (!t.IsInAnyStorage() && !home[t.Position])
				continue;
			nutrition += t.GetStatValue(StatDefOf.Nutrition) * t.stackCount;
		}
		return nutrition / (eaters * NeedPerPersonPerDay);
	}

	public bool IsMealPaused(Bill b) => b != null && mealPaused.Contains(b.GetUniqueLoadID());

	private IEnumerable<Bill_Production> MealBills()
	{
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null)
				continue;
			foreach (Bill bill in g.BillStack.Bills)
			{
				if (bill is Bill_Production bp && MakesMeals(bp.recipe))
					yield return bp;
			}
		}
	}

	public List<(Bill_Production bill, Building bench)> MealBillList()
	{
		var list = new List<(Bill_Production, Building)>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null)
				continue;
			foreach (Bill bill in g.BillStack.Bills)
				if (bill is Bill_Production bp && MakesMeals(bp.recipe))
					list.Add((bp, b));
		}
		return list;
	}

	private void TickMeals()
	{
		int now = Find.TickManager.TicksGame;
		if (now < nextMealCheck)
			return;
		nextMealCheck = now + 1250;
		try
		{
			CheckMeals();
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] meals: " + ex.Message);
		}
	}

	private void CheckMeals()
	{
		if (!enabled || !mealTargetOn)
		{
			ReleaseMeals();
			lastMealDays = -1f;
			return;
		}
		float days = MealDays();
		lastMealDays = days;
		float target = Mathf.Max(0.5f, mealTargetDays);
		// 2.52.3: raw food about to spoil – the cooks may cook it even with enough meals
		bool spoiling = false;
		try { spoiling = SituationRules.SpoilingNutrition(map) >= 10; } catch { }
		if (spoiling)
		{
			ReleaseMeals();
		}
		else if (days >= target)
		{
			foreach (Bill_Production bill in MealBills())
			{
				if (bill.suspended)
					continue;
				// meals that never rot (packaged survival meals for caravans) are left alone
				ThingDef prod = bill.recipe.products.FirstOrDefault(x => IsMeal(x.thingDef))?.thingDef;
				if (prod != null && prod.GetCompProperties<CompProperties_Rottable>() == null)
					continue;
				bill.suspended = true;
				string id = bill.GetUniqueLoadID();
				if (!mealPaused.Contains(id))
					mealPaused.Add(id);
			}
		}
		// a small gap, so the bills do not flip on and off with every meal
		else if (days < target - Mathf.Max(0.25f, target * 0.1f))
		{
			ReleaseMeals();
		}
	}

	private void ReleaseMeals()
	{
		if (mealPaused.Count == 0)
			return;
		foreach (Bill_Production bill in MealBills())
		{
			if (bill.suspended && mealPaused.Contains(bill.GetUniqueLoadID()))
				bill.suspended = false;
		}
		mealPaused.Clear();
	}
}
