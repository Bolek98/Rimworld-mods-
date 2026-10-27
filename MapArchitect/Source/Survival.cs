using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MapArchitect
{
	// ------------------------------------------------------------------ 0.4.3: survival mode
	// Optional (off by default). Map Architect becomes a part of the game instead of
	// a free editor: only the terraforming tools are shown (terrain, smoothing,
	// mountains and natural roofs, deep resources), they need the Fabrication
	// research and every use costs materials taken from storage on this map
	// (a floor costs what it costs to build, anything else steel per cell; ore
	// veins and deep resources also cost what they give). The change itself is
	// still instant – turning it into colonist jobs would need its own
	// construction system, which the instant-edit design of the mod does not have.
	public class MASettings : ModSettings
	{
		public bool survival;
		public int steelPerCell = 5;

		public override void ExposeData()
		{
			Scribe_Values.Look(ref survival, "survival", false);
			Scribe_Values.Look(ref steelPerCell, "steelPerCell", 5);
		}
	}

	public class MapArchitectMod : Mod
	{
		public static MASettings S;

		public MapArchitectMod(ModContentPack content) : base(content)
		{
			S = GetSettings<MASettings>();
		}

		public override string SettingsCategory() => "Map Architect";

		public override void DoSettingsWindowContents(Rect r)
		{
			var l = new Listing_Standard();
			l.Begin(r);
			l.CheckboxLabeled("MA_Survival".Translate(), ref S.survival, "MA_SurvivalDesc".Translate());
			if (S.survival)
			{
				l.Label("MA_SurvivalSteel".Translate(S.steelPerCell));
				S.steelPerCell = Mathf.RoundToInt(l.Slider(S.steelPerCell, 0f, 50f));
			}
			l.End();
		}
	}

	public static class Survival
	{
		public static bool On => MapArchitectMod.S != null && MapArchitectMod.S.survival;

		private static ResearchProjectDef gate;
		private static bool gateLooked;

		public static bool Researched(out string reason)
		{
			reason = null;
			if (!gateLooked)
			{
				gateLooked = true;
				gate = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Fabrication");
			}
			if (gate == null || gate.IsFinished)
				return true;
			reason = "MA_SurvivalResearch".Translate(gate.LabelCap);
			return false;
		}

		public static Dictionary<ThingDef, int> PerCell(int cells)
		{
			var cost = new Dictionary<ThingDef, int>();
			int per = MapArchitectMod.S?.steelPerCell ?? 5;
			if (cells > 0 && per > 0)
				cost[ThingDefOf.Steel] = per * cells;
			return cost;
		}

		public static void Add(Dictionary<ThingDef, int> cost, ThingDef d, int n)
		{
			if (d == null || n <= 0)
				return;
			cost[d] = (cost.TryGetValue(d, out int a) ? a : 0) + n;
		}

		private static IEnumerable<Thing> Stored(Map map, ThingDef d)
		{
			foreach (Thing t in map.listerThings.ThingsOfDef(d))
				if (t.Spawned && !t.Destroyed && t.IsInAnyStorage() && !t.IsForbidden(Faction.OfPlayer))
					yield return t;
		}

		public static int Available(Map map, ThingDef d) => Stored(map, d).Sum(t => t.stackCount);

		// everything there -> taken from storage, true; something missing -> a message, false
		public static bool TryPay(Map map, Dictionary<ThingDef, int> cost)
		{
			if (cost == null || cost.Count == 0)
				return true;
			var missing = new List<string>();
			foreach (KeyValuePair<ThingDef, int> kv in cost)
			{
				int have = Available(map, kv.Key);
				if (have < kv.Value)
					missing.Add(kv.Key.LabelCap + " " + have + "/" + kv.Value);
			}
			if (missing.Count > 0)
			{
				Messages.Message("MA_SurvivalMissing".Translate(string.Join(", ", missing)), MessageTypeDefOf.RejectInput, historical: false);
				return false;
			}
			foreach (KeyValuePair<ThingDef, int> kv in cost)
			{
				int need = kv.Value;
				foreach (Thing t in Stored(map, kv.Key).ToList())
				{
					if (need <= 0)
						break;
					int take = Math.Min(need, t.stackCount);
					if (take >= t.stackCount)
						t.Destroy(DestroyMode.Vanish);
					else
						t.SplitOff(take).Destroy(DestroyMode.Vanish);
					need -= take;
				}
			}
			Messages.Message("MA_SurvivalPaid".Translate(string.Join(", ", cost.Select(kv => kv.Key.LabelCap + " x" + kv.Value))), MessageTypeDefOf.SilentInput, historical: false);
			return true;
		}
	}
}
