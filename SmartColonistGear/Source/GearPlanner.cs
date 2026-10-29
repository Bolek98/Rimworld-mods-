using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchedLoadout
{
	// 2.18.0: what the colony is missing (helmets, body armor, guns, worn-out pieces) and the
	// best thing you can make for it right now – one click adds the bill, or the planner does it by itself.
	public static class GearPlanner
	{
		public enum Slot { Helmet, Body, Gun, Worn }

		public sealed class Need
		{
			public Slot slot;
			public int without;      // colonists without it
			public int inStock;      // usable pieces in storage
			public int missing;      // to make
			public ThingDef best;    // best thing you can make
			public RecipeDef recipe;
			public Building_WorkTable bench;
			public List<string> who = new List<string>();
			public bool hasBill;
		}

		private static readonly Dictionary<ThingDef, float> armorCache = new Dictionary<ThingDef, float>();

		private static float DefArmor(ThingDef d)
		{
			float v;
			if (armorCache.TryGetValue(d, out v)) return v;
			ThingDef stuff = d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null;
			try { v = Mathf.Max(d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff)); } catch { v = 0f; }
			armorCache[d] = v;
			return v;
		}

		private static bool Covers(ThingDef d, string group)
		{
			var g = d.apparel?.bodyPartGroups;
			if (g == null) return false;
			for (int i = 0; i < g.Count; i++) if (g[i].defName == group) return true;
			return false;
		}

		public static bool Fits(ThingDef d, Slot s)
		{
			if (d == null) return false;
			switch (s)
			{
				case Slot.Helmet:
					return d.IsApparel && (Covers(d, "FullHead") || Covers(d, "UpperHead")) && DefArmor(d) >= 0.2f;
				case Slot.Body:
					return d.IsApparel && Covers(d, "Torso") && d.apparel.layers != null
						&& (d.apparel.layers.Contains(ApparelLayerDefOf.Shell) || d.apparel.layers.Contains(ApparelLayerDefOf.Middle)) && DefArmor(d) >= 0.25f;
				case Slot.Gun:
					return d.IsRangedWeapon && d.equipmentType == EquipmentType.Primary && !d.destroyOnDrop && LoadoutFilter.IsManagedWeapon(d);
				default:
					return false;
			}
		}

		private static bool Has(Pawn p, Slot s)
		{
			if (s == Slot.Gun) return p.equipment?.Primary != null && p.equipment.Primary.def.IsRangedWeapon;
			if (p.apparel == null) return false;
			foreach (Apparel a in p.apparel.WornApparel) if (Fits(a.def, s)) return true;
			return false;
		}

		private static List<Pawn> Colonists(Map map) =>
			map.mapPawns.FreeColonistsSpawned.Where(p => p != null && !p.Dead && LoadoutFilter.IsPlayerColonist(p) && !ExemptPawns.IsExempt(p)).ToList();

		public static List<Need> Compute(Map map)
		{
			var list = new List<Need>();
			if (map == null) return list;
			List<Pawn> pawns = Colonists(map);
			foreach (Slot s in new[] { Slot.Helmet, Slot.Body, Slot.Gun })
			{
				var n = new Need { slot = s };
				foreach (Pawn p in pawns)
				{
					if (s == Slot.Gun && (p.WorkTagIsDisabled(WorkTags.Violent) || (p.story?.traits != null && p.story.traits.HasTrait(TraitDefOf.Brawler)))) continue;
					if (!Has(p, s)) { n.without++; n.who.Add(p.LabelShort); }
				}
				n.inStock = Stock(map, d => Fits(d, s));
				n.missing = Mathf.Max(0, n.without - n.inStock);
				BestCraftable(map, d => Fits(d, s), s == Slot.Gun ? (Func<ThingDef, float>)(d => d.BaseMarketValue) : DefArmor, out n.best, out n.recipe, out n.bench);
				n.hasBill = n.best != null && HasBill(map, n.best);
				list.Add(n);
			}
			// worn-out pieces: one row per kind
			var worn = new Dictionary<ThingDef, Need>();
			foreach (Pawn p in pawns)
			{
				if (p.apparel == null) continue;
				foreach (Apparel a in p.apparel.WornApparel)
				{
					if (!a.def.useHitPoints || (float)a.HitPoints / a.MaxHitPoints >= 0.5f) continue;
					Need n;
					if (!worn.TryGetValue(a.def, out n)) worn[a.def] = n = new Need { slot = Slot.Worn };
					n.without++;
					n.who.Add(p.LabelShort);
				}
			}
			foreach (var kv in worn)
			{
				Need n = kv.Value;
				ThingDef def = kv.Key;
				n.inStock = Stock(map, d => d == def);
				n.missing = Mathf.Max(0, n.without - n.inStock);
				BestCraftable(map, d => d == def, d => 1f, out n.best, out n.recipe, out n.bench);
				if (n.best == null) n.best = def;
				n.hasBill = HasBill(map, def);
				list.Add(n);
			}
			return list;
		}

		private static int Stock(Map map, Func<ThingDef, bool> fits)
		{
			int c = 0;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
			{
				if (t == null || !t.Spawned || !fits(t.def) || t.IsForbidden(Faction.OfPlayer) || !t.IsInAnyStorage()) continue;
				if (t.def.useHitPoints && (float)t.HitPoints / t.MaxHitPoints < 0.5f) continue;
				if (!LoadoutFilter.IsAllowed(t.def)) continue;
				c += t.stackCount;
			}
			return c;
		}

		private static void BestCraftable(Map map, Func<ThingDef, bool> fits, Func<ThingDef, float> value, out ThingDef best, out RecipeDef recipe, out Building_WorkTable bench)
		{
			best = null; recipe = null; bench = null;
			float bestV = float.MinValue;
			List<Building_WorkTable> benches = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>().ToList();
			foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
			{
				if (r.products == null || r.products.Count != 1) continue;
				ThingDef d = r.products[0].thingDef;
				if (d == null || !fits(d) || !r.AvailableNow || !LoadoutFilter.IsAllowed(d) || LoadoutFilter.IsManuallyBanned(d)) continue;
				float v = value(d);
				if (v <= bestV) continue;
				Building_WorkTable b = benches.FirstOrDefault(x => x.def.AllRecipes != null && x.def.AllRecipes.Contains(r) && x.CurrentlyUsableForBills());
				if (b == null) continue;
				best = d; recipe = r; bench = b; bestV = v;
			}
		}

		private static bool HasBill(Map map, ThingDef def)
		{
			foreach (Building_WorkTable b in map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>())
				foreach (Bill bill in b.BillStack)
					if (bill.recipe?.products != null && bill.recipe.products.Any(p => p.thingDef == def) && !bill.suspended)
					{
						var bp = bill as Bill_Production;
						if (bp == null || bp.repeatMode != BillRepeatModeDefOf.RepeatCount || bp.repeatCount > 0) return true;
					}
			return false;
		}

		public static bool AddBill(Map map, Need n, bool quiet)
		{
			if (n?.recipe == null || n.bench == null || n.missing <= 0) return false;
			Bill_Production bill = n.recipe.MakeNewBill() as Bill_Production;
			if (bill == null) return false;
			bill.repeatMode = BillRepeatModeDefOf.TargetCount;
			bill.targetCount = Mathf.Max(1, Stock(map, d => d == n.best) + n.missing);
			bill.SetStoreMode(BillStoreModeDefOf.BestStockpile);
			n.bench.BillStack.AddBill(bill);
			SCGBattleComponent.Get()?.plannerBills.Add(bill.GetUniqueLoadID());
			n.hasBill = true;
			if (!quiet) Messages.Message("RL_GP_Added".Translate(n.best.LabelCap, n.bench.LabelCap, n.missing), n.bench, MessageTypeDefOf.PositiveEvent, false);
			return true;
		}

		public static void AutoTick()
		{
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (st == null || !st.enabled || !st.plannerAuto || Find.Maps == null) return;
			try
			{
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome) continue;
					var added = new List<string>();
					foreach (Need n in Compute(map))
						if (n.missing > 0 && !n.hasBill && AddBill(map, n, true)) added.Add(n.best.label + " ×" + n.missing);
					if (added.Count > 0)
						Messages.Message("RL_GP_AutoAdded".Translate(string.Join(", ", added.ToArray())), MessageTypeDefOf.PositiveEvent, false);
				}
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] planner: " + e.Message, 7310104);
			}
		}
	}

	public sealed class Dialog_GearPlanner : Window
	{
		private List<GearPlanner.Need> needs;
		private int built = -99999;
		private Vector2 scroll;

		public override Vector2 InitialSize => new Vector2(760f, 560f);

		public Dialog_GearPlanner()
		{
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = false;
			draggable = true;
		}

		public override void DoWindowContents(Rect r)
		{
			Map map = Find.CurrentMap;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "RL_GP_Title".Translate());
			Text.Font = GameFont.Tiny;
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(r.x, r.y + 32f, r.width, 30f), "RL_GP_Desc".Translate());
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			if (map == null) return;
			if (needs == null || Find.TickManager.TicksGame - built > 250)
			{
				needs = GearPlanner.Compute(map);
				built = Find.TickManager.TicksGame;
			}
			float y = r.y + 66f;
			Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 80f);
			const float rowH = 54f;
			Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(needs.Count, 1) * rowH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			try
			{
				if (needs.All(n => n.without == 0))
				{
					GUI.color = MUI.Accent;
					Widgets.Label(new Rect(0f, 0f, view.width, 30f), "RL_GP_AllGood".Translate());
					GUI.color = Color.white;
				}
				int i = 0;
				foreach (GearPlanner.Need n in needs)
				{
					if (n.without == 0) continue;
					Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
					MUI.Fill(row, MUI.CardBg);
					if (n.best != null) Widgets.DefIcon(new Rect(row.x + 6f, row.y + 9f, 32f, 32f), n.best);
					string title = n.slot == GearPlanner.Slot.Worn ? "RL_GP_Worn".Translate(n.best?.LabelCap ?? "?").ToString() : ("RL_GP_Slot_" + n.slot).Translate().ToString();
					Widgets.Label(new Rect(row.x + 46f, row.y + 3f, row.width - 260f, 24f), title);
					GUI.color = MUI.Muted;
					Text.Font = GameFont.Tiny;
					string line = "RL_GP_Line".Translate(n.without, n.inStock, n.missing).ToString();
					if (n.slot != GearPlanner.Slot.Worn && n.best != null) line += "   " + "RL_GP_Best".Translate(n.best.LabelCap, n.bench?.LabelCap ?? "-").ToString();
					if (n.slot != GearPlanner.Slot.Worn && n.best == null) line += "   " + "RL_GP_NoRecipe".Translate().ToString();
					Rect lr = new Rect(row.x + 46f, row.y + 26f, row.width - 260f, 24f);
					Widgets.Label(lr, line);
					TooltipHandler.TipRegion(row, string.Join(", ", n.who.ToArray()));
					Text.Font = GameFont.Small;
					GUI.color = Color.white;
					Rect b = new Rect(row.xMax - 206f, row.y + 11f, 200f, 28f);
					if (n.missing <= 0)
					{
						GUI.color = MUI.Accent;
						Text.Anchor = TextAnchor.MiddleCenter;
						Widgets.Label(b, "RL_GP_InStock".Translate());
						Text.Anchor = TextAnchor.UpperLeft;
						GUI.color = Color.white;
					}
					else if (n.hasBill)
					{
						GUI.color = MUI.Muted;
						Text.Anchor = TextAnchor.MiddleCenter;
						Widgets.Label(b, "RL_GP_HasBill".Translate());
						Text.Anchor = TextAnchor.UpperLeft;
						GUI.color = Color.white;
					}
					else if (n.recipe != null && Widgets.ButtonText(b, "RL_GP_Add".Translate(n.missing)))
					{
						GearPlanner.AddBill(map, n, false);
					}
					i++;
				}
			}
			finally { Widgets.EndScrollView(); }
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (st != null)
			{
				Rect cb = new Rect(r.x, r.yMax - 72f, r.width, 28f);
				bool v = st.plannerAuto;
				Widgets.CheckboxLabeled(cb, "RL_GP_Auto".Translate(), ref st.plannerAuto);
				TooltipHandler.TipRegion(cb, "RL_GP_Auto_Desc".Translate());
				if (v != st.plannerAuto) ResearchedLoadoutMod.Settings.Write();
			}
		}
	}
}
