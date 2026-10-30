using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.52: cooks butcher first. In the game a cook always looks for cooking bills
// before butchering (both are Cooking work, cooking comes first), so with a
// "do forever" meal bill the carcasses lie there and rot. While raw meat is
// low and fresh carcasses wait for an active butcher bill, Steward puts
// butchering in front of cooking for every colonist – in every mode, also
// with situations switched off. Switch: Messages & comfort tab.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.WorkGiversInOrderNormal), MethodType.Getter)]
public static class ButcherFirstPatch
{
	private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> pawnRef = AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");
	private static readonly Dictionary<Pawn_WorkSettings, List<WorkGiver>> cache = new Dictionary<Pawn_WorkSettings, List<WorkGiver>>();

	public static void Reset() => cache.Clear();

	public static bool IsButcher(WorkGiver g)
	{
		return g?.def != null && g.def.defName.IndexOf("Butcher", StringComparison.OrdinalIgnoreCase) >= 0;
	}

	public static void Postfix(Pawn_WorkSettings __instance, ref List<WorkGiver> __result)
	{
		try
		{
			CrisesSettings s = CrisesMod.S;
			if (s == null || !s.butcherFirst || __result == null || __result.Count < 2)
				return;
			Pawn p = pawnRef(__instance);
			if (p?.Map == null || !p.IsColonist || !Kitchen.FoodWorkWaiting(p.Map))
				return;
			// 2.52.3: the game refills the SAME list when priorities change – so nothing may be cached
			// by the list itself (that froze every colonist's work order). The order is rebuilt on every
			// call into one reused list per colonist (no garbage).
			List<WorkGiver> src = __result;
			int first = -1, bIdx = -1;
			for (int i = 0; i < src.Count; i++)
			{
				if (!IsButcher(src[i]))
					continue;
				int f = i;
				for (int j = i - 1; j >= 0; j--)
				{
					if (src[j].def.workType != src[i].def.workType)
						break;
					f = j;
				}
				if (f < i) { first = f; bIdx = i; }
				break;
			}
			if (bIdx < 0)
				return;
			if (!cache.TryGetValue(__instance, out List<WorkGiver> res))
			{
				if (cache.Count > 300)
					cache.Clear();
				cache[__instance] = res = new List<WorkGiver>(src.Count);
			}
			res.Clear();
			for (int i = 0; i < src.Count; i++)
			{
				if (i == first)
					res.Add(src[bIdx]);
				if (i != bIdx)
					res.Add(src[i]);
			}
			__result = res;
		}
		catch
		{
		}
	}
}

// food helpers (butcher first, situations)
public static class Kitchen
{
	// 2.50.0 / 2.52.3: fresh animal carcasses that an active butcher bill accepts, and enough ready meals.
	// Cached per map (it is asked on every job search of every colonist).
	private static readonly Dictionary<int, KeyValuePair<int, bool>> foodCache = new Dictionary<int, KeyValuePair<int, bool>>();

	public static void ResetFood() => foodCache.Clear();

	// fresh, allowed animal carcasses that an active butcher bill takes (counted up to 'max')
	public static int ButcherableCarcasses(Map map, int max)
	{
		var bills = new List<Bill>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver bg) || bg.BillStack == null || (b is Building_WorkTable wt && !wt.CurrentlyUsableForBills()))
				continue;
			foreach (Bill bill in bg.BillStack.Bills)
				if (!bill.suspended && bill.recipe?.specialProducts != null && bill.recipe.specialProducts.Contains(SpecialProductType.Butchery) && bill.ShouldDoNow())
					bills.Add(bill);
		}
		if (bills.Count == 0)
			return 0;
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
		{
			if (!(t is Corpse c) || c.InnerPawn == null || !c.InnerPawn.RaceProps.Animal || c.IsForbidden(Faction.OfPlayer) || c.GetRotStage() != RotStage.Fresh)
				continue;
			foreach (Bill bill in bills)
				if (bill.IsFixedOrAllowedIngredient(c))
				{
					if (++n >= max)
						return n;
					break;
				}
		}
		return n;
	}

	public static bool MealsShort(Map map)
	{
		if (map == null)
			return false;
		int colonists = map.mapPawns.FreeColonistsSpawnedCount;
		if (colonists == 0)
			return false;
		int meals = 0;
		foreach (Thing m in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
			if (m.def.ingestible != null && m.def.ingestible.IsMeal && !m.IsForbidden(Faction.OfPlayer))
			{
				meals += m.stackCount;
				if (meals >= colonists)
					return false;
			}
		return true;
	}

	public static bool FoodWorkWaiting(Map map)
	{
		if (map == null)
			return false;
		int now = Find.TickManager.TicksGame;
		if (foodCache.TryGetValue(map.uniqueID, out var kv) && now - kv.Key < 1250 && now >= kv.Key)
			return kv.Value;
		bool want = false;
		try
		{
			// no raw-meat limit – the butcher bill's own "do until X" decides; only with fewer ready meals
			// than colonists the cooks keep cooking first
			int colonists = map.mapPawns.FreeColonistsSpawnedCount;
			int meals = 0;
			foreach (Thing m in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
				if (m.def.ingestible != null && m.def.ingestible.IsMeal && !m.IsForbidden(Faction.OfPlayer))
				{
					meals += m.stackCount;
					if (meals >= colonists)
						break;
				}
			if (meals >= colonists)
				want = ButcherableCarcasses(map, 1) > 0;
		}
		catch
		{
		}
		if (foodCache.Count > 32)
			foodCache.Clear();
		foodCache[map.uniqueID] = new KeyValuePair<int, bool>(now, want);
		return want;
	}

}
