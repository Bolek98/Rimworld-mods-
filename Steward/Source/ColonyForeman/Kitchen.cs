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
	private static readonly Dictionary<Pawn_WorkSettings, KeyValuePair<List<WorkGiver>, List<WorkGiver>>> cache = new Dictionary<Pawn_WorkSettings, KeyValuePair<List<WorkGiver>, List<WorkGiver>>>();

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
			if (cache.TryGetValue(__instance, out var kv) && ReferenceEquals(kv.Key, __result))
			{
				__result = kv.Value;
				return;
			}
			List<WorkGiver> src = __result;
			var res = new List<WorkGiver>(src);
			bool moved = false;
			for (int i = 0; i < res.Count; i++)
			{
				WorkGiver b = res[i];
				if (!IsButcher(b))
					continue;
				// the first giver of the same work type that comes before it (cooking) – butchering goes in front of it
				int first = i;
				for (int j = i - 1; j >= 0; j--)
				{
					if (res[j].def.workType != b.def.workType)
						break;
					first = j;
				}
				if (first < i)
				{
					res.RemoveAt(i);
					res.Insert(first, b);
					moved = true;
				}
			}
			if (!moved)
				return;
			if (cache.Count > 300)
				cache.Clear();
			cache[__instance] = new KeyValuePair<List<WorkGiver>, List<WorkGiver>>(src, res);
			__result = res;
		}
		catch
		{
		}
	}
}
