using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 1.2.0 "Supplies, better and cooler":
//   Stock      – the colony's key supplies (food, meals, medicine, wood, steel,
//                components, cloth, fuel): stock, days left, trend, made / used per
//                day (15 days), what is being made right now. Green / yellow / red.
//   Forecast   – winter coming: more meals in stock; a raid or many wounded:
//                more medicine.
//   Rooms      – bills go to the right room (meals to the kitchen, drugs to the
//                lab) and to the least busy bench of the room.
//   Chains     – what stops a production target ("no steel") and, when there is a
//                mining or forestry job for it, its target goes up so people go there.
//   Recipes    – meals by the food in stock (simple when short, fine / lavish when
//                plenty), travel food when there is plenty, clothes made from the
//                material there is most of.
//   Traders    – when a trader comes: what to sell (plenty) and what to buy (short).
// ---------------------------------------------------------------------------
public sealed class StockLine
{
	public string key;
	public string labelKey;
	public ThingDef icon;
	public Func<SupplyManager, float> count;          // now
	public Func<ThingDef, bool> matches;               // products that count for this line
	public bool nutrition;                             // counted in nutrition (food)
}

public sealed class DayLog
{
	public const int Days = 15;
	public readonly List<int> made = new List<int>();
	public readonly List<int> used = new List<int>();
	public int madeToday, usedToday;
	public float lastValue = -1f;

	public void Roll()
	{
		made.Add(madeToday);
		used.Add(usedToday);
		madeToday = usedToday = 0;
		while (made.Count > Days) made.RemoveAt(0);
		while (used.Count > Days) used.RemoveAt(0);
	}

	public float AvgUsed(int days = 3)
	{
		var all = used.Skip(Math.Max(0, used.Count - days)).ToList();
		if (all.Count == 0)
			return usedToday;
		return (float)all.Average();
	}

	public void Expose(string label)
	{
		string m = Scribe.mode == LoadSaveMode.Saving ? string.Join(",", made) : null;
		string u = Scribe.mode == LoadSaveMode.Saving ? string.Join(",", used) : null;
		Scribe_Values.Look(ref m, label + "_m");
		Scribe_Values.Look(ref u, label + "_u");
		Scribe_Values.Look(ref madeToday, label + "_mt", 0);
		Scribe_Values.Look(ref usedToday, label + "_ut", 0);
		Scribe_Values.Look(ref lastValue, label + "_lv", -1f);
		if (Scribe.mode == LoadSaveMode.LoadingVars)
		{
			Parse(m, made);
			Parse(u, used);
		}
	}

	private static void Parse(string s, List<int> into)
	{
		into.Clear();
		if (s.NullOrEmpty())
			return;
		foreach (string part in s.Split(','))
			if (int.TryParse(part, out int v))
				into.Add(v);
	}
}

public sealed partial class SupplyManager
{
	public bool roomBills = true, forecastOn = true, recipeByStock = true, traderTips = true;
	public Dictionary<string, DayLog> dayLogs = new Dictionary<string, DayLog>();
	public Dictionary<string, StockHistory> stockHourly = new Dictionary<string, StockHistory>();
	private int nextStockLog, nextDayRoll;
	private List<string> toldTraders = new List<string>();
	public List<string> bottlenecks = new List<string>();     // "Components: no steel → mining" (refreshed each check)

	private void ExposeStock()
	{
		Scribe_Values.Look(ref roomBills, "roomBills", true);
		Scribe_Values.Look(ref forecastOn, "forecastOn", true);
		Scribe_Values.Look(ref recipeByStock, "recipeByStock", true);
		Scribe_Values.Look(ref traderTips, "traderTips", true);
		Scribe_Values.Look(ref nextStockLog, "nextStockLog", 0);
		Scribe_Values.Look(ref nextDayRoll, "nextDayRoll", 0);
		Scribe_Collections.Look(ref toldTraders, "toldTraders", LookMode.Value);
		foreach (StockLine l in Lines())
		{
			if (!dayLogs.TryGetValue(l.key, out DayLog d))
				dayLogs[l.key] = d = new DayLog();
			d.Expose("day_" + l.key);
			if (!stockHourly.TryGetValue(l.key, out StockHistory h))
				stockHourly[l.key] = h = new StockHistory();
			h.Expose("hour_" + l.key);
		}
		if (Scribe.mode == LoadSaveMode.PostLoadInit && toldTraders == null)
			toldTraders = new List<string>();
	}

	// ---- the lines ---------------------------------------------------------------
	private static List<StockLine> lines;

	public static List<StockLine> Lines()
	{
		if (lines != null)
			return lines;
		lines = new List<StockLine>
		{
			new StockLine { key = "food", labelKey = "CF_St_Food", icon = DefDatabase<ThingDef>.GetNamedSilentFail("RawPotatoes") ?? ThingDefOf.MealSimple, nutrition = true,
				count = sm => ColonyStats.FoodNutrition(sm.map), matches = d => d.IsNutritionGivingIngestible && d.ingestible.HumanEdible && !d.IsDrug },
			new StockLine { key = "meals", labelKey = "CF_St_Meals", icon = ThingDefOf.MealSimple,
				count = sm => sm.CountWhere(IsMeal), matches = IsMeal },
			new StockLine { key = "medicine", labelKey = "CF_St_Medicine", icon = ThingDefOf.MedicineIndustrial,
				count = sm => sm.CountWhere(d => d.IsMedicine), matches = d => d.IsMedicine },
			new StockLine { key = "wood", labelKey = "CF_St_Wood", icon = ThingDefOf.WoodLog,
				count = sm => sm.ProductCount(ThingDefOf.WoodLog), matches = d => d == ThingDefOf.WoodLog },
			new StockLine { key = "steel", labelKey = "CF_St_Steel", icon = ThingDefOf.Steel,
				count = sm => sm.ProductCount(ThingDefOf.Steel), matches = d => d == ThingDefOf.Steel },
			new StockLine { key = "components", labelKey = "CF_St_Components", icon = ThingDefOf.ComponentIndustrial,
				count = sm => sm.ProductCount(ThingDefOf.ComponentIndustrial), matches = d => d == ThingDefOf.ComponentIndustrial },
			new StockLine { key = "cloth", labelKey = "CF_St_Cloth", icon = ThingDefOf.Cloth,
				count = sm => sm.CountWhere(IsTextile), matches = IsTextile },
			new StockLine { key = "fuel", labelKey = "CF_St_Fuel", icon = DefDatabase<ThingDef>.GetNamedSilentFail("Chemfuel") ?? ThingDefOf.WoodLog,
				count = sm => { ThingDef c = DefDatabase<ThingDef>.GetNamedSilentFail("Chemfuel"); return c == null ? 0 : sm.ProductCount(c); },
				matches = d => d.defName == "Chemfuel" },
		};
		return lines;
	}

	private static bool IsTextile(ThingDef d) => d != null && d.IsStuff && d.thingCategories != null && d.thingCategories.Contains(ThingCategoryDefOf.Textiles);

	private int CountWhere(Func<ThingDef, bool> f)
	{
		int n = 0;
		foreach (var kv in map.resourceCounter.AllCountedAmounts)
			if (kv.Value > 0 && f(kv.Key))
				n += kv.Value;
		// meals and medicine are not always counted resources – count the rest by hand
		if (n == 0)
		{
			Area_Home home = map.areaManager.Home;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
				if (f(t.def) && t.Spawned && !t.IsForbidden(Faction.OfPlayer) && (t.IsInAnyStorage() || home[t.Position]))
					n += t.stackCount;
		}
		return n;
	}

	// ---- hourly / daily log -------------------------------------------------------
	private void TickStock(int now)
	{
		if (now < nextStockLog)
			return;
		nextStockLog = now + 2500;
		try
		{
			foreach (StockLine l in Lines())
			{
				float v = l.count(this);
				if (!dayLogs.TryGetValue(l.key, out DayLog d))
					dayLogs[l.key] = d = new DayLog();
				if (!stockHourly.TryGetValue(l.key, out StockHistory h))
					stockHourly[l.key] = h = new StockHistory();
				h.Add(Mathf.RoundToInt(v));
				if (d.lastValue >= 0f)
				{
					int delta = Mathf.RoundToInt(v - d.lastValue);
					if (delta > 0) d.madeToday += delta;
					else d.usedToday -= delta;
				}
				d.lastValue = v;
			}
			if (now >= nextDayRoll)
			{
				bool first = nextDayRoll == 0;
				nextDayRoll = now + 60000;
				if (!first)
					foreach (DayLog d in dayLogs.Values)
						d.Roll();
			}
			if (traderTips)
				CheckTraders();
		}
		catch (Exception ex) { MLog.WarnOnce("[Steward] stock log: " + ex.Message, 0x5e1201); }
	}

	// days the line lasts at the recent use (food: what the colony eats)
	public float DaysLeft(StockLine l)
	{
		float now = l.count(this);
		if (l.key == "food")
			return ColonyStats.FoodDays(map);
		if (l.key == "meals")
			return MealDays();
		if (!dayLogs.TryGetValue(l.key, out DayLog d))
			return 999f;
		float use = Math.Max(d.AvgUsed(), d.usedToday);
		if (use <= 0.5f)
			return 999f;
		return now / use;
	}

	public int Trend(StockLine l)
	{
		if (!stockHourly.TryGetValue(l.key, out StockHistory h) || h.values.Count < 2)
			return 0;
		int back = Math.Min(24, h.values.Count - 1);
		return h.values[h.values.Count - 1] - h.values[h.values.Count - 1 - back];
	}

	public static Color DaysColor(float days) =>
		days < 3f ? new Color(1f, 0.45f, 0.4f) : days < 7f ? new Color(1f, 0.85f, 0.4f) : new Color(0.55f, 0.9f, 0.5f);

	// what is being made for this line now: "Simple meal ×4 (stove)"
	public List<string> BeingMade(StockLine l)
	{
		var res = new List<string>();
		foreach (Building b in Benches())
		{
			BillStack st = ((IBillGiver)b).BillStack;
			if (st == null)
				continue;
			foreach (Bill bill in st.Bills)
			{
				if (!(bill is Bill_Production bp) || bp.suspended || bp.recipe?.products == null)
					continue;
				ThingDefCountClass p = bp.recipe.products.FirstOrDefault(x => x.thingDef != null && l.matches(x.thingDef));
				if (p == null)
					continue;
				string n = bp.repeatMode == BillRepeatModeDefOf.Forever ? "∞"
					: bp.repeatMode == BillRepeatModeDefOf.RepeatCount ? "×" + bp.repeatCount
					: "→" + bp.targetCount;
				res.Add(p.thingDef.LabelCap + " " + n + " (" + b.LabelShortCap + ")");
			}
		}
		return res;
	}

	// ---- forecast -----------------------------------------------------------------
	public bool WinterComing()
	{
		if (!forecastOn || map.Tile < 0)
			return false;
		try
		{
			int tw = (int)GenLocalDate.Twelfth(map);
			float nowT = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, (Twelfth)tw);
			float next = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, (Twelfth)((tw + 1) % 12));
			return next < 0f || (nowT < 0f);
		}
		catch { return false; }
	}

	public bool DangerNow()
	{
		if (!forecastOn)
			return false;
		try
		{
			if (GenHostility.AnyHostileActiveThreatToPlayer(map))
				return true;
			int hurt = map.mapPawns.FreeColonistsSpawned.Count(p => p.health.HasHediffsNeedingTend());
			return hurt >= 2;
		}
		catch { return false; }
	}

	public float MealForecastFactor() => WinterComing() ? 1.5f : 1f;
	public float MedicineForecastFactor() => DangerNow() ? 2f : 1f;

	// ---- rooms -----------------------------------------------------------------------
	private static RoomRoleDef kitchenRole, labRole;

	// how well the bench's room suits the recipe: meals in the kitchen, drugs in the lab
	public int RoomFit(RecipeDef recipe, Building b)
	{
		if (!roomBills || recipe?.products == null || b == null)
			return 0;
		kitchenRole ??= DefDatabase<RoomRoleDef>.GetNamedSilentFail("Kitchen");
		labRole ??= DefDatabase<RoomRoleDef>.GetNamedSilentFail("Laboratory");
		Room room = b.GetRoom();
		RoomRoleDef role = room?.Role;
		bool meal = recipe.products.Any(p => IsMeal(p.thingDef));
		bool drug = recipe.products.Any(p => p.thingDef != null && p.thingDef.IsDrug);
		if (meal)
			return role != null && role == kitchenRole ? 2 : role != null && role == labRole ? -2 : 0;
		if (drug)
			return role != null && role == labRole ? 2 : role != null && role == kitchenRole ? -1 : 0;
		return 0;
	}

	public static int BusyBills(Building b)
	{
		BillStack st = (b as IBillGiver)?.BillStack;
		return st == null ? 0 : st.Bills.Count(x => !x.suspended && x.ShouldDoNow());
	}

	// ---- chains: what stops a production target -------------------------------------
	private void CheckBottlenecks()
	{
		bottlenecks = new List<string>();
		foreach (ProductionTarget t in production)
		{
			if (t == null || !t.enabled || t.recipe?.ingredients == null || ProductCount(t.product) >= t.target)
				continue;
			foreach (IngredientCount ic in t.recipe.ingredients)
			{
				if (ic == null || !ic.IsFixedIngredient)
					continue;
				ThingDef d = ic.FixedIngredient;
				if (d == null)
					continue;
				int perRun = ic.CountRequiredOfFor(d, t.recipe);
				if (ProductCount(d) >= perRun)
					continue;
				string hint;
				SupplyJob job = mining.FirstOrDefault(m => m.def == d);
				if (job != null && job.enabled)
				{
					// people go there: the mining target goes up to what the target still needs
					int need = IngredientNeeds(t).Where(x => x.def == d).Select(x => x.count).DefaultIfEmpty(perRun).Sum();
					if (job.target < need)
						job.target = need;
					hint = "CF_St_HintMining".Translate();
				}
				else if (d == ThingDefOf.WoodLog)
				{
					if (forestry.enabled && forestry.target < ProductCount(d) + perRun * 5)
						forestry.target = ProductCount(d) + perRun * 5;
					hint = forestry.enabled ? "CF_St_HintForestry".Translate() : "CF_St_HintForestryOff".Translate();
				}
				else if (Mineable(d))
					hint = "CF_St_HintMiningOff".Translate();
				else if (RecipesFor(d).Any(r => r.AvailableNow))
					hint = "CF_St_HintMake".Translate();
				else
					hint = "CF_St_HintBuy".Translate();
				bottlenecks.Add("CF_St_Bottleneck".Translate(t.product.LabelCap, d.label, hint));
			}
		}
	}

	private static bool Mineable(ThingDef d)
	{
		foreach (ThingDef b in DefDatabase<ThingDef>.AllDefsListForReading)
			if (b.building != null && b.building.mineableThing == d)
				return true;
		return false;
	}

	// ---- recipes by stock ---------------------------------------------------------------
	// the best meal kind the food in stock allows: short → simple, some → fine, plenty → lavish
	public FoodPreferability MaxMealByStock()
	{
		if (!recipeByStock)
			return FoodPreferability.MealLavish;
		float days = ColonyStats.FoodDays(map);
		return days < 3f ? FoodPreferability.MealSimple : days < 8f ? FoodPreferability.MealFine : FoodPreferability.MealLavish;
	}

	// travel food (pemmican, packaged meals) – a small stock when there is plenty of food
	private int TravelFoodNeed(List<string> made)
	{
		if (!recipeByStock || ColonyStats.FoodDays(map) < 6f)
		{
			foreach (AutoBill a in autoBills.Where(x => x.kind == "travel").ToList())
			{
				DeleteBill(FindBillById(a.billId));
				autoBills.Remove(a);
			}
			return 0;
		}
		ThingDef pem = DefDatabase<ThingDef>.GetNamedSilentFail("Pemmican");
		if (pem == null || !CanMake(pem, out RecipeDef r, out Building b))
			return 0;
		int target = Mathf.Max(10, ColonyStats.Eaters(map) * 4);
		return EnsureStock(pem, r, b, target, "travel", made);
	}

	// clothes from the material there is most of (only the steward's own bills)
	private void PickStuff(Bill_Production bill, ThingDef product)
	{
		if (!recipeByStock || bill == null || product == null || !product.MadeFromStuff)
			return;
		int cost = Math.Max(1, product.CostStuffCount);
		ThingDef best = null;
		int bestCount = 0;
		foreach (ThingDef s in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (!s.IsStuff || !bill.ingredientFilter.Allows(s))
				continue;
			int n = map.resourceCounter.GetCount(s);
			if (n >= cost && n > bestCount)
			{
				best = s;
				bestCount = n;
			}
		}
		if (best == null)
			return;
		bill.ingredientFilter.SetDisallowAll();
		bill.ingredientFilter.SetAllow(best, true);
	}

	// ---- traders ---------------------------------------------------------------------------
	private void CheckTraders()
	{
		var here = new List<(string id, string name)>();
		foreach (Lord lord in map.lordManager.lords)
			if (lord?.LordJob is LordJob_TradeWithColony && lord.faction != null && !lord.faction.HostileTo(Faction.OfPlayer))
				here.Add(("lord" + lord.loadID, lord.faction.Name));
		if (map.passingShipManager != null)
			foreach (PassingShip s in map.passingShipManager.passingShips)
				if (s is TradeShip ts)
					here.Add(("ship" + ts.GetUniqueLoadID(), ts.FullTitle));
		toldTraders.RemoveAll(id => !here.Any(h => h.id == id));
		foreach (var h in here)
		{
			if (toldTraders.Contains(h.id))
				continue;
			toldTraders.Add(h.id);
			TradeAdvice(out List<string> sell, out List<string> buy);
			if (sell.Count == 0 && buy.Count == 0)
				continue;
			string text = "CF_St_TraderTitle".Translate(h.name);
			if (buy.Count > 0)
				text += "\n" + "CF_St_Buy".Translate(string.Join(", ", buy));
			if (sell.Count > 0)
				text += "\n" + "CF_St_Sell".Translate(string.Join(", ", sell));
			Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: true);
		}
	}

	public void TradeAdvice(out List<string> sell, out List<string> buy)
	{
		sell = new List<string>();
		buy = new List<string>();
		foreach (StockLine l in Lines())
		{
			float days = DaysLeft(l);
			float n = l.count(this);
			string label = l.labelKey.Translate();
			if (days < 3f)
				buy.Add(label);
			else if (!l.nutrition && l.key != "meals" && l.key != "medicine" && days >= 999f && n >= 600)
				sell.Add(label + " (" + Mathf.RoundToInt(n) + ")");
		}
		foreach (ProductionTarget t in production)
			if (t?.product != null && t.enabled && t.target > 0 && ProductCount(t.product) > t.target * 3)
				sell.Add(t.product.LabelCap + " (" + ProductCount(t.product) + ")");
	}
}
