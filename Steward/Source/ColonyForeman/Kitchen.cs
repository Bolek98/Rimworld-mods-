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
			ForemanSettings s = ForemanMod.Settings;
			if (s == null || !s.butcherFirst || __result == null || __result.Count < 2)
				return;
			Pawn p = pawnRef(__instance);
			if (p?.Map == null || !p.IsColonist || !Planner.FoodWorkWaiting(p.Map))
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
