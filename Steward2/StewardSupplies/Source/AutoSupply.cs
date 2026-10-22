using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.16: self-supply. When the colony runs short of something it can
// make, the steward adds a bill for exactly what is missing, on the best
// bench that can make it (not a campfire or a spot when a real bench exists),
// and removes the bill when it is done. Never "forever".
//
//   Meals      – below the ready-meal target (or 2 days), no meal bill → bill
//   Medicine   – fewer medicines than colonists (at least 3)
//   Clothes    – worn-out clothes and accessories are replaced; a colonist
//                with nothing on the body or legs gets a basic piece
//   Armor      – worn-out armor is replaced
//   Weapons    – an unarmed colonist (who can fight) gets a weapon, if none
//                is lying in storage
//   Materials  – components; steel from slag when steel runs low
//
// Things in storage count first, bills of your own count too – the steward
// only adds what is still missing. Alien races get only what they may wear
// or use (Humanoid Alien Races restrictions).
// ---------------------------------------------------------------------------
public sealed class AutoBill : IExposable
{
	public string billId;
	public ThingDef product;
	public string kind;

	public void ExposeData()
	{
		Scribe_Values.Look(ref billId, "billId");
		Scribe_Defs.Look(ref product, "product");
		Scribe_Values.Look(ref kind, "kind");
	}
}

public sealed partial class SupplyManager
{
	public bool autoOn = true;
	public bool autoMeals = true, autoMedicine = true, autoClothes = true, autoArmor = true, autoWeapons = true, autoMaterials = true;
	public float autoWorn = 0.5f;
	// 2.16.1: switch off when another mod lifts the race restrictions
	public bool autoRaceRules = true;
	public List<AutoBill> autoBills = new List<AutoBill>();
	public string lastAutoSummary = "";

	private void ExposeAuto()
	{
		Scribe_Values.Look(ref autoOn, "autoOn", true);
		Scribe_Values.Look(ref autoMeals, "autoMeals", true);
		Scribe_Values.Look(ref autoMedicine, "autoMedicine", true);
		Scribe_Values.Look(ref autoClothes, "autoClothes", true);
		Scribe_Values.Look(ref autoArmor, "autoArmor", true);
		Scribe_Values.Look(ref autoWeapons, "autoWeapons", true);
		Scribe_Values.Look(ref autoMaterials, "autoMaterials", true);
		Scribe_Values.Look(ref autoWorn, "autoWorn", 0.5f);
		Scribe_Values.Look(ref autoRaceRules, "autoRaceRules", true);
		Scribe_Collections.Look(ref autoBills, "autoBills", LookMode.Deep);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			if (autoBills == null) autoBills = new List<AutoBill>();
			autoBills.RemoveAll(a => a == null || a.product == null);
		}
	}

	// ---- defs looked at (built once) -----------------------------------------
	private static List<ThingDef> apparelDefs, weaponDefs, mealDefs, medicineDefs;

	private static void BuildDefLists()
	{
		if (apparelDefs != null)
			return;
		apparelDefs = new List<ThingDef>();
		weaponDefs = new List<ThingDef>();
		mealDefs = new List<ThingDef>();
		medicineDefs = new List<ThingDef>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (RecipesFor(d).Count == 0)
				continue;   // nothing that cannot be made at all
			if (d.apparel != null) apparelDefs.Add(d);
			else if (d.IsWeapon && !d.IsStuff && !d.destroyOnDrop) weaponDefs.Add(d);
			else if (IsMeal(d)) mealDefs.Add(d);
			else if (d.IsMedicine) medicineDefs.Add(d);
		}
	}

	// ---- recipes ------------------------------------------------------------
	private static Dictionary<ThingDef, List<RecipeDef>> recipesFor;

	private static List<RecipeDef> RecipesFor(ThingDef d)
	{
		if (recipesFor == null)
		{
			recipesFor = new Dictionary<ThingDef, List<RecipeDef>>();
			foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
			{
				if (r.products == null)
					continue;
				foreach (ThingDefCountClass p in r.products)
				{
					if (p?.thingDef == null)
						continue;
					if (!recipesFor.TryGetValue(p.thingDef, out var l))
						recipesFor[p.thingDef] = l = new List<RecipeDef>();
					l.Add(r);
				}
			}
		}
		return recipesFor.TryGetValue(d, out var list) ? list : new List<RecipeDef>();
	}

	// a researched recipe for d and the best bench for it
	private bool CanMake(ThingDef d, out RecipeDef recipe, out Building bench)
	{
		recipe = null;
		bench = null;
		foreach (RecipeDef r in RecipesFor(d))
		{
			if (!r.AvailableNow)
				continue;
			Building b = BestBench(r);
			if (b == null)
				continue;
			// prefer a recipe whose best bench is a real bench
			if (bench == null || (IsMakeshift(bench) && !IsMakeshift(b)))
			{
				recipe = r;
				bench = b;
			}
		}
		return recipe != null;
	}

	// ---- what is already coming ---------------------------------------------
	private int Coming(ThingDef d, Bill_Production except)
	{
		int n = 0;
		foreach (Building b in Benches())
		{
			BillStack st = ((IBillGiver)b).BillStack;
			if (st == null)
				continue;
			foreach (Bill bill in st.Bills.ToList())
			{
				if (!(bill is Bill_Production bp) || bp == except || bp.suspended || bp.recipe?.products == null)
					continue;
				ThingDefCountClass p = bp.recipe.products.FirstOrDefault(x => x.thingDef == d);
				if (p == null)
					continue;
				if (bp.repeatMode == BillRepeatModeDefOf.Forever)
					return 9999;
				if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount)
					n += bp.repeatCount * p.count;
				else
					n += Mathf.Max(0, bp.targetCount - StockOf(d));
			}
		}
		return n;
	}

	private AutoBill Mine(ThingDef d, out Bill_Production bill)
	{
		bill = null;
		foreach (AutoBill a in autoBills)
		{
			if (a.product != d)
				continue;
			bill = FindBillById(a.billId);
			return a;
		}
		return null;
	}

	private Bill_Production FindBillById(string id)
	{
		if (string.IsNullOrEmpty(id))
			return null;
		foreach (Building b in Benches())
		{
			BillStack st = ((IBillGiver)b).BillStack;
			if (st == null)
				continue;
			foreach (Bill bill in st.Bills.ToList())
				if (bill is Bill_Production bp && bp.GetUniqueLoadID() == id)
					return bp;
		}
		return null;
	}

	private void DeleteBill(Bill_Production bill)
	{
		if (bill?.billStack != null)
			bill.billStack.Delete(bill);
	}

	// "make it N more times" – exactly the missing number
	private int EnsureCount(ThingDef d, int need, string kind, List<string> made)
	{
		AutoBill mine = Mine(d, out Bill_Production bill);
		int missing = need - Coming(d, bill);
		if (missing <= 0)
		{
			if (mine != null)
			{
				DeleteBill(bill);
				autoBills.Remove(mine);
			}
			return 0;
		}
		if (bill != null)
		{
			// 2.18: repeatCount is in runs of the recipe, not in pieces – a
			// recipe that makes several at once was asked for too many
			int perRun = bill.recipe?.products?.FirstOrDefault(x => x.thingDef == d)?.count ?? 1;
			int runs = Mathf.CeilToInt((float)missing / Mathf.Max(1, perRun));
			if (bill.repeatCount != runs)
				bill.repeatCount = runs;
			return 0;
		}
		if (!CanMake(d, out RecipeDef recipe, out Building bench))
			return 0;
		int per = recipe.products.First(x => x.thingDef == d).count;
		bill = recipe.MakeNewBill() as Bill_Production;
		if (bill == null)
			return 0;
		bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
		bill.repeatCount = Mathf.Max(1, Mathf.CeilToInt((float)missing / Mathf.Max(1, per)));
		bill.SetStoreMode(BillStoreModeDefOf.BestStockpile);
		((IBillGiver)bench).BillStack.AddBill(bill);
		if (mine == null)
			autoBills.Add(mine = new AutoBill { product = d, kind = kind });
		mine.billId = bill.GetUniqueLoadID();
		made.Add(d.LabelCap + " ×" + bill.repeatCount);
		return 1;
	}

	// "make until there are X" – for things kept in stock (meals, medicine, components)
	private int EnsureStock(ThingDef d, RecipeDef recipe, Building bench, int target, string kind, List<string> made)
	{
		AutoBill mine = Mine(d, out Bill_Production bill);
		bool enough = StockOf(d) >= target;
		if (enough || Coming(d, bill) >= 9999)
		{
			if (mine != null && enough)
			{
				DeleteBill(bill);
				autoBills.Remove(mine);
			}
			return 0;
		}
		if (bill != null)
		{
			if (bill.targetCount != target)
				bill.targetCount = target;
			return 0;
		}
		// a bill of your own for this already runs – nothing to add
		if (Coming(d, null) > 0)
			return 0;
		bill = recipe.MakeNewBill() as Bill_Production;
		if (bill == null)
			return 0;
		bill.repeatMode = BillRepeatModeDefOf.TargetCount;
		bill.targetCount = target;
		bill.SetStoreMode(BillStoreModeDefOf.BestStockpile);
		((IBillGiver)bench).BillStack.AddBill(bill);
		if (mine == null)
			autoBills.Add(mine = new AutoBill { product = d, kind = kind });
		mine.billId = bill.GetUniqueLoadID();
		made.Add(d.LabelCap + " → " + target);
		return 1;
	}

	// ---- main check (with the other supplies) --------------------------------
	public int CheckAuto()
	{
		// bills that are gone or done
		foreach (AutoBill a in autoBills.ToList())
		{
			Bill_Production b = FindBillById(a.billId);
			if (b == null)
				autoBills.Remove(a);
			else if (b.repeatMode == BillRepeatModeDefOf.RepeatCount && b.repeatCount <= 0)
			{
				DeleteBill(b);
				autoBills.Remove(a);
			}
		}
		if (!enabled || !autoOn)
		{
			RemoveAuto(null);
			return 0;
		}
		var made = new List<string>();
		int n = 0;
		var keepKinds = new HashSet<string>();
		if (autoClothes) keepKinds.Add("clothes");
		if (autoArmor) keepKinds.Add("armor");
		if (autoWeapons) keepKinds.Add("weapon");
		if (autoMeals) keepKinds.Add("meals");
		if (autoMedicine) keepKinds.Add("medicine");
		if (autoMaterials) keepKinds.Add("material");
		foreach (AutoBill a in autoBills.ToList())
			if (!keepKinds.Contains(a.kind))
				RemoveAuto(a.kind);

		var needs = new Dictionary<ThingDef, (int count, string kind)>();
		if (autoClothes || autoArmor)
			ApparelNeeds(needs);
		if (autoWeapons)
			WeaponNeeds(needs);
		// count-bills: only for needs; a count-bill whose need is gone is removed
		foreach (AutoBill a in autoBills.Where(x => x.kind == "clothes" || x.kind == "armor" || x.kind == "weapon").ToList())
			if (!needs.ContainsKey(a.product))
				needs[a.product] = (0, a.kind);
		foreach (var kv in needs)
			n += EnsureCount(kv.Key, kv.Value.count, kv.Value.kind, made);

		if (autoMeals)
			n += MealNeed(made);
		if (autoMedicine)
			n += MedicineNeed(made);
		if (autoMaterials)
			n += MaterialNeeds(made);
		if (made.Count > 0)
			lastAutoSummary = string.Join(", ", made);
		return n;
	}

	public Bill_Production AutoBillOf(AutoBill a) => FindBillById(a.billId);

	public void RemoveAuto(string kind)
	{
		foreach (AutoBill a in autoBills.ToList())
		{
			if (kind != null && a.kind != kind)
				continue;
			DeleteBill(FindBillById(a.billId));
			autoBills.Remove(a);
		}
	}

	// ---- who needs what ------------------------------------------------------
	private IEnumerable<Pawn> People()
	{
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
			yield return p;
		foreach (Pawn p in map.mapPawns.SlavesOfColonySpawned.ToList())
			yield return p;
	}

	private static bool IsArmor(ThingDef d) => d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp) >= 0.4f;

	private bool Worn(Apparel a) => a.def.useHitPoints && a.HitPoints < a.MaxHitPoints * autoWorn;

	// good pieces of this def waiting in storage
	private int GoodInStorage(ThingDef d)
	{
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsOfDef(d))
		{
			if (!t.Spawned || !t.IsInAnyStorage() || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (t is Apparel ap && (ap.WornByCorpse || Worn(ap)))
				continue;
			n += t.stackCount;
		}
		return n;
	}

	private void ApparelNeeds(Dictionary<ThingDef, (int, string)> needs)
	{
		var want = new Dictionary<ThingDef, int>();
		foreach (Pawn p in People())
		{
			if (p.apparel == null || !p.RaceProps.Humanlike)
				continue;
			foreach (Apparel a in p.apparel.WornApparel)
			{
				if (!Worn(a))
					continue;
				bool armor = IsArmor(a.def);
				if (armor ? !autoArmor : !autoClothes)
					continue;
				want.TryGetValue(a.def, out int c);
				want[a.def] = c + 1;
			}
			if (!autoClothes || p.DevelopmentalStage.Baby())
				continue;
			// nothing on the body or the legs: a basic piece
			foreach (BodyPartGroupDef g in new[] { BodyPartGroupDefOf.Torso, BodyPartGroupDefOf.Legs })
			{
				if (p.apparel.WornApparel.Any(x => x.def.apparel.bodyPartGroups.Contains(g)))
					continue;
				ThingDef basic = BasicApparel(p, g);
				if (basic == null)
					continue;
				want.TryGetValue(basic, out int c);
				want[basic] = c + 1;
			}
		}
		foreach (var kv in want)
		{
			int missing = kv.Value - GoodInStorage(kv.Key);
			if (missing > 0)
				needs[kv.Key] = (missing, IsArmor(kv.Key) ? "armor" : "clothes");
		}
	}

	// cheapest piece on the skin that covers this part and that the pawn may wear
	private ThingDef BasicApparel(Pawn p, BodyPartGroupDef g)
	{
		ThingDef best = null;
		float bestWork = float.MaxValue;
		BuildDefLists();
		foreach (ThingDef d in apparelDefs)
		{
			if (d.apparel == null || !d.apparel.bodyPartGroups.Contains(g) || !d.apparel.layers.Contains(ApparelLayerDefOf.OnSkin))
				continue;
			if (IsArmor(d) || !d.apparel.developmentalStageFilter.Has(p.DevelopmentalStage))
				continue;
			if (d.apparel.gender != Gender.None && d.apparel.gender != p.gender)
				continue;
			if (!RaceAllows(d, p, false) || !CanMake(d, out RecipeDef r, out _))
				continue;
			float work = r.workAmount > 0 ? r.workAmount : d.GetStatValueAbstract(StatDefOf.WorkToMake);
			if (work < bestWork)
			{
				best = d;
				bestWork = work;
			}
		}
		return best;
	}

	private void WeaponNeeds(Dictionary<ThingDef, (int, string)> needs)
	{
		var unarmed = People().Where(p => p.equipment != null && p.equipment.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent)
		                                  && p.DevelopmentalStage.Adult()).ToList();
		if (unarmed.Count == 0)
			return;
		// weapons waiting in storage are taken first
		int stored = 0;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
			if (t.Spawned && t.IsInAnyStorage() && !t.IsForbidden(Faction.OfPlayer) && t.def.IsWeapon && !t.def.IsStuff)
				stored++;
		var want = new Dictionary<ThingDef, int>();
		foreach (Pawn p in unarmed.Skip(stored))
		{
			bool melee = p.story?.traits?.HasTrait(TraitDefOf.Brawler) == true
			             || (p.skills != null && p.skills.GetSkill(SkillDefOf.Melee).Level > p.skills.GetSkill(SkillDefOf.Shooting).Level);
			ThingDef w = ChooseWeapon(p, melee);
			if (w == null)
				continue;
			want.TryGetValue(w, out int c);
			want[w] = c + 1;
		}
		foreach (var kv in want)
			needs[kv.Key] = (kv.Value, "weapon");
	}

	// the weapon of this kind most used by the colony if it can be made,
	// else the most valuable one that can be made
	private ThingDef ChooseWeapon(Pawn p, bool melee)
	{
		var used = People().Where(x => x.equipment?.Primary != null).Select(x => x.equipment.Primary.def)
			.Where(d => d.IsMeleeWeapon == melee).GroupBy(d => d).OrderByDescending(g => g.Count()).Select(g => g.Key);
		foreach (ThingDef d in used)
			if (RaceAllows(d, p, true) && CanMake(d, out _, out _))
				return d;
		ThingDef best = null;
		float value = 0f;
		BuildDefLists();
		foreach (ThingDef d in weaponDefs)
		{
			if (!d.IsWeapon || d.IsStuff || d.IsMeleeWeapon != melee || (!melee && !d.IsRangedWeapon) || d.destroyOnDrop)
				continue;
			if (!RaceAllows(d, p, true) || !CanMake(d, out _, out _))
				continue;
			float v = d.BaseMarketValue;
			if (v > value)
			{
				best = d;
				value = v;
			}
		}
		return best;
	}

	private int MealNeed(List<string> made)
	{
		float days = mealTargetOn ? Mathf.Max(0.5f, mealTargetDays) : 2f;
		if (MealDays() >= days)
		{
			// enough meals: the steward's own meal bill is done
			foreach (AutoBill a in autoBills.Where(x => x.kind == "meals").ToList())
			{
				DeleteBill(FindBillById(a.billId));
				autoBills.Remove(a);
			}
			return 0;
		}
		// 2.17: a bill paused by the meal target counts as running – otherwise a
		// new meal bill was made in the target's gap and deleted again later
		if (MealBills().Any(b => !b.suspended || IsMealPaused(b)))
			return 0;
		// the best meal a colonist can cook (skill), the least work on a tie
		WorkTypeDef cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
		int skill = People().Where(p => (cooking == null || !p.WorkTypeIsDisabled(cooking)) && p.skills != null)
			.Select(p => p.skills.GetSkill(SkillDefOf.Cooking).Level).DefaultIfEmpty(0).Max();
		ThingDef best = null;
		RecipeDef bestRecipe = null;
		Building bestBench = null;
		BuildDefLists();
		foreach (ThingDef d in mealDefs)
		{
			if (!IsMeal(d) || d.defName.IndexOf("Survival", StringComparison.OrdinalIgnoreCase) >= 0)
				continue;
			if (!CanMake(d, out RecipeDef r, out Building b))
				continue;
			if (r.skillRequirements != null && r.skillRequirements.Any(sr => sr.skill == SkillDefOf.Cooking && sr.minLevel > skill))
				continue;
			if (best == null || d.ingestible.preferability > best.ingestible.preferability
			    || (d.ingestible.preferability == best.ingestible.preferability && r.workAmount < bestRecipe.workAmount))
			{
				best = d;
				bestRecipe = r;
				bestBench = b;
			}
		}
		if (best == null)
			return 0;
		float per = Mathf.Max(0.3f, best.GetStatValueAbstract(StatDefOf.Nutrition));
		int target = Mathf.Max(2, Mathf.CeilToInt(days * ColonyStats.Eaters(map) * NeedPerPersonPerDay / per));
		return EnsureStock(best, bestRecipe, bestBench, target, "meals", made);
	}

	private int MedicineNeed(List<string> made)
	{
		int people = People().Count();
		int target = Mathf.Max(3, people);
		int have = 0;
		Area_Home home = map.areaManager.Home;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine))
			if (!t.IsForbidden(Faction.OfPlayer) && (t.IsInAnyStorage() || home[t.Position]))
				have += t.stackCount;
		ThingDef best = null;
		RecipeDef bestRecipe = null;
		Building bestBench = null;
		BuildDefLists();
		foreach (ThingDef d in medicineDefs)
		{
			if (!d.IsMedicine || !CanMake(d, out RecipeDef r, out Building b))
				continue;
			if (best == null || d.GetStatValueAbstract(StatDefOf.MedicalPotency) > best.GetStatValueAbstract(StatDefOf.MedicalPotency))
			{
				best = d;
				bestRecipe = r;
				bestBench = b;
			}
		}
		if (best == null)
			return 0;
		AutoBill mine = Mine(best, out Bill_Production bill);
		if (have >= target && mine == null)
			return 0;
		// keep the stock of this medicine up to what is missing overall
		int goal = StockOf(best) + Mathf.Max(0, target - have);
		return EnsureStock(best, bestRecipe, bestBench, goal, "medicine", made);
	}

	private int MaterialNeeds(List<string> made)
	{
		int n = 0;
		ThingDef comp = ThingDefOf.ComponentIndustrial;
		if (comp != null && CanMake(comp, out RecipeDef cr, out Building cb))
			n += EnsureStock(comp, cr, cb, Mathf.Max(5, People().Count()), "material", made);
		// steel from slag when steel runs low
		ThingDef slag = ThingDefOf.ChunkSlagSteel;
		RecipeDef extract = DefDatabase<RecipeDef>.GetNamedSilentFail("ExtractMetalFromSlag");
		int steel = map.resourceCounter.GetCount(ThingDefOf.Steel);
		if (slag != null && extract != null && extract.AvailableNow && steel < 100)
		{
			int chunks = map.listerThings.ThingsOfDef(slag).Count(t => t.Spawned && !t.IsForbidden(Faction.OfPlayer) && !map.fogGrid.IsFogged(t.Position));
			int times = Mathf.Min(chunks, Mathf.CeilToInt((100 - steel) / 15f));
			AutoBill mine = autoBills.FirstOrDefault(a => a.kind == "material" && a.product == ThingDefOf.Steel);
			Bill_Production bill = mine == null ? null : FindBillById(mine.billId);
			if (times > 0 && bill == null)
			{
				Building bench = BestBench(extract);
				if (bench != null && extract.MakeNewBill() is Bill_Production b)
				{
					b.repeatMode = BillRepeatModeDefOf.RepeatCount;
					b.repeatCount = times;
					b.SetStoreMode(BillStoreModeDefOf.BestStockpile);
					((IBillGiver)bench).BillStack.AddBill(b);
					if (mine == null)
						autoBills.Add(mine = new AutoBill { product = ThingDefOf.Steel, kind = "material" });
					mine.billId = b.GetUniqueLoadID();
					made.Add(ThingDefOf.Steel.LabelCap + " ×" + times);
					n++;
				}
			}
		}
		return n;
	}

	// ---- alien races (Humanoid Alien Races, optional) -------------------------
	private static bool harSearched;
	private static MethodInfo harCanWear, harCanEquip;

	private bool RaceAllows(ThingDef d, Pawn p, bool weapon)
	{
		if (!autoRaceRules)
			return true;
		if (!harSearched)
		{
			harSearched = true;
			Type t = GenTypes.GetTypeInAnyAssembly("AlienRace.RaceRestrictionSettings");
			harCanWear = t?.GetMethod("CanWear", new[] { typeof(ThingDef), typeof(ThingDef) });
			harCanEquip = t?.GetMethod("CanEquip", new[] { typeof(ThingDef), typeof(ThingDef) });
		}
		MethodInfo m = weapon ? harCanEquip : harCanWear;
		if (m == null)
			return true;
		try
		{
			return (bool)m.Invoke(null, new object[] { d, p.def });
		}
		catch
		{
			return true;
		}
	}
}
