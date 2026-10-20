using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.4: "Balanced" is no longer neutral. It reads the colony every time the
// steward plans and leans towards what the colony needs right now – so the
// style does not have to be switched by hand:
//   little food            -> cooking, growing, hunting, fishing
//   patients / prisoners   -> doctors, wardens
//   many blueprints        -> construction
//   lots lying around      -> hauling
//   dirty base             -> cleaning
//   autumn                 -> harvest and hunt before winter
//   wood / steel running out -> cutting / mining
//   nothing urgent         -> research and art
// Shifts are small (max ±0.3) and add to the real-demand logic.
// ---------------------------------------------------------------------------
public static class SmartBalance
{
	private sealed class Snapshot
	{
		public int tick = -1;
		public Dictionary<string, float> delta = new Dictionary<string, float>();
		public List<string> reasons = new List<string>();
	}

	private static readonly Dictionary<Map, Snapshot> cache = new Dictionary<Map, Snapshot>();

	public static void Reset() => cache.Clear();
	public static void Forget(Map m) => cache.Remove(m);

	public static float Delta(Map map, WorkTypeDef w)
	{
		if (map == null || w == null)
			return 0f;
		Snapshot s = Get(map);
		return s.delta.TryGetValue(w.defName, out float d) ? d : 0f;
	}

	public static List<string> Reasons(Map map) => map == null ? new List<string>() : Get(map).reasons;

	private static Snapshot Get(Map map)
	{
		int now = Find.TickManager.TicksGame;
		if (!cache.TryGetValue(map, out Snapshot s) || now - s.tick > 600 || s.tick < 0)
		{
			s = Compute(map);
			s.tick = now;
			cache[map] = s;
		}
		return s;
	}

	private static void Add(Snapshot s, string work, float d)
	{
		s.delta.TryGetValue(work, out float cur);
		// 2.6.5: at most ±0.15 – the real-demand logic already does the heavy lifting
		s.delta[work] = Mathf.Clamp(cur + d, -0.15f, 0.15f);
	}

	private static Snapshot Compute(Map map)
	{
		var s = new Snapshot();

		int people = MapDemand.Eaters(map);
		float foodDays = MapDemand.FoodDays(map);   // 2.15: same food count everywhere
		if (foodDays < 2f)
		{
			Add(s, "Cooking", 0.2f); Add(s, "Growing", 0.2f); Add(s, "Hunting", 0.15f); Add(s, "Fishing", 0.15f);
			s.reasons.Add("CF_B_FoodLow".Translate(foodDays.ToString("0.0")));
		}
		else if (foodDays < 5f)
		{
			Add(s, "Cooking", 0.1f); Add(s, "Growing", 0.1f); Add(s, "Hunting", 0.05f); Add(s, "Fishing", 0.05f);
			s.reasons.Add("CF_B_FoodLow".Translate(foodDays.ToString("0.0")));
		}

		// 2.6.5: patients, blueprints, things to haul and dirt are NOT boosted
		// here any more. The planner's demand already raises doctors,
		// construction, hauling and cleaning by exactly those numbers; adding
		// them again pushed hauling and cleaning above real work for everyone
		// ("colonists do things around instead of their job").
		int prisoners = map.mapPawns.PrisonersOfColonySpawnedCount;
		if (prisoners > 0)
		{
			Add(s, "Warden", 0.1f);
			s.reasons.Add("CF_B_Prisoners".Translate(prisoners));
		}


		// 2.12: Balanced is really balanced – no autumn or "calm → research"
		// leanings any more. It only reacts to real shortages (food, prisoners,
		// wood, steel); everything else stays at its normal importance and the
		// work is spread evenly over the colonists (Planner).

		if (map.resourceCounter.GetCount(ThingDefOf.WoodLog) < 150)
		{
			Add(s, "PlantCutting", 0.1f);
			s.reasons.Add("CF_B_Wood".Translate());
		}
		if (map.resourceCounter.GetCount(ThingDefOf.Steel) < 100)
		{
			Add(s, "Mining", 0.1f);
			s.reasons.Add("CF_B_Steel".Translate());
		}

		return s;
	}
}
