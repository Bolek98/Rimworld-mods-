using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.3.0: butcher by stock. For each kind of meat (human, insect, animal) a target: while the stored meat of that
// kind is at or above it, corpses of that kind are not taken by butcher bills – they wait (or go to your burn /
// bury bills). Your bills and their filters are not changed; only "may this corpse be used now?" is answered.
public enum MeatKind { Human, Insect, Animal }

public sealed partial class SupplyManager
{
	public bool butcherByStock;
	// -1 = always butcher, 0 = never, n = only while less than n is in storage
	public int targetHuman = 0, targetInsect = -1, targetAnimal = -1;

	private readonly Dictionary<MeatKind, int> meatCache = new Dictionary<MeatKind, int>();
	private int meatCacheTick = -99999;

	private void ExposeButcher()
	{
		Scribe_Values.Look(ref butcherByStock, "butcherByStock", false);
		Scribe_Values.Look(ref targetHuman, "butcherHuman", 0);
		Scribe_Values.Look(ref targetInsect, "butcherInsect", -1);
		Scribe_Values.Look(ref targetAnimal, "butcherAnimal", -1);
	}

	public int Target(MeatKind k) => k == MeatKind.Human ? targetHuman : k == MeatKind.Insect ? targetInsect : targetAnimal;

	public void SetTarget(MeatKind k, int v)
	{
		if (k == MeatKind.Human) targetHuman = v;
		else if (k == MeatKind.Insect) targetInsect = v;
		else targetAnimal = v;
	}

	public static MeatKind KindOf(RaceProperties r)
	{
		if (r == null) return MeatKind.Animal;
		if (r.Humanlike) return MeatKind.Human;
		if (r.FleshType == FleshTypeDefOf.Insectoid) return MeatKind.Insect;
		return MeatKind.Animal;
	}

	private static Dictionary<ThingDef, MeatKind> meatKinds;

	private static Dictionary<ThingDef, MeatKind> MeatKinds()
	{
		if (meatKinds != null)
			return meatKinds;
		meatKinds = new Dictionary<ThingDef, MeatKind>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			ThingDef meat = d.race?.meatDef;
			if (meat == null || meatKinds.ContainsKey(meat))
				continue;
			meatKinds[meat] = KindOf(d.race);
		}
		return meatKinds;
	}

	// stored meat of one kind (counted at most once every 250 ticks)
	public int MeatStock(MeatKind k)
	{
		int now = Find.TickManager.TicksGame;
		if (now - meatCacheTick > 250)
		{
			meatCache.Clear();
			foreach (var kv in MeatKinds())
			{
				int n = map.resourceCounter.GetCount(kv.Key);
				meatCache.TryGetValue(kv.Value, out int old);
				meatCache[kv.Value] = old + n;
			}
			meatCacheTick = now;
		}
		return meatCache.TryGetValue(k, out int v) ? v : 0;
	}

	public bool MayButcher(Corpse c)
	{
		if (!butcherByStock || c?.InnerPawn == null)
			return true;
		MeatKind k = KindOf(c.InnerPawn.RaceProps);
		int t = Target(k);
		if (t < 0)
			return true;
		if (t == 0)
			return false;
		return MeatStock(k) < t;
	}
}

[HarmonyPatch(typeof(Bill), nameof(Bill.IsFixedOrAllowedIngredient), new[] { typeof(Thing) })]
public static class Patch_ButcherByStock
{
	public static void Postfix(Bill __instance, Thing thing, ref bool __result)
	{
		if (!__result || !(thing is Corpse c))
			return;
		try
		{
			RecipeDef r = __instance.recipe;
			if (r?.specialProducts == null || !r.specialProducts.Contains(SpecialProductType.Butchery))
				return;
			Map map = (__instance.billStack?.billGiver as Thing)?.Map ?? c.MapHeld;
			SupplyManager sm = SupplyManager.For(map);
			if (sm != null && !sm.MayButcher(c))
				__result = false;
		}
		catch
		{
		}
	}
}
