using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace CaravanHelper;

// ---------------------------------------------------------------------------
// 1.1.0: travel items. The player picks food and equipment that is meant only for caravans:
//  - travel food is not eaten at home (not even put into a pocket) – only by someone who is
//    starving; on the road, on other maps and in the caravan it is eaten as usual;
//  - when a caravan is formed with automatic supplies, travel food goes first and the chosen
//    equipment is packed (a set number per colonist).
// ---------------------------------------------------------------------------
public static class TravelItems
{
	private static HashSet<string> foodSet, gearSet;

	public static void Invalidate()
	{
		foodSet = null;
		gearSet = null;
	}

	public static bool IsFood(ThingDef d)
	{
		if (d == null) return false;
		foodSet ??= new HashSet<string>(CaravanHelperMod.S.travelFood ?? new List<string>());
		return foodSet.Contains(d.defName);
	}

	public static bool IsGear(ThingDef d)
	{
		if (d == null) return false;
		gearSet ??= new HashSet<string>(CaravanHelperMod.S.travelGear ?? new List<string>());
		return gearSet.Contains(d.defName);
	}

	// what can be chosen
	public static bool FoodCandidate(ThingDef d) =>
		d != null && d.IsNutritionGivingIngestible && !d.IsDrug && !d.IsCorpse && d.ingestible.HumanEdible && d.category == ThingCategory.Item;

	public static bool GearCandidate(ThingDef d) =>
		d != null && d.category == ThingCategory.Item && !d.IsCorpse && !FoodCandidate(d) && d.EverHaulable
		&& (d.IsApparel || d.IsWeapon || d.IsMedicine || d.IsDrug || d.IsStuff || d.stackLimit > 1 || d.Minifiable || d.thingCategories?.Count > 0)
		&& !d.IsBlueprint && !d.IsFrame && d.defName != "Silver";
}

// travel food stays untouched at home (except by someone starving)
[HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.WillEat), new[] { typeof(Pawn), typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool) })]
public static class Patch_WillEatTravelFood
{
	public static void Postfix(Pawn p, Thing food, ref bool __result)
	{
		if (!__result || food == null || p == null) return;
		try
		{
			if (!CaravanHelperMod.S.keepTravelFood || !TravelItems.IsFood(food.def)) return;
			Map map = food.MapHeld;
			if (map == null || !map.IsPlayerHome || p.IsCaravanMember()) return;
			if (p.needs?.food != null && p.needs.food.CurCategory >= HungerCategory.Starving) return;
			__result = false;
		}
		catch { }
	}
}

// the picker
public class Dialog_TravelItems : Window
{
	private bool gear;
	private string search = "";
	private Vector2 scroll;
	private List<ThingDef> food, equipment;

	public override Vector2 InitialSize => new Vector2(620f, 720f);

	public Dialog_TravelItems()
	{
		doCloseX = true;
		doCloseButton = true;
		absorbInputAroundWindow = true;
		food = DefDatabase<ThingDef>.AllDefsListForReading.Where(TravelItems.FoodCandidate).OrderBy(d => d.label ?? d.defName).ToList();
		equipment = DefDatabase<ThingDef>.AllDefsListForReading.Where(TravelItems.GearCandidate).OrderBy(d => d.label ?? d.defName).ToList();
	}

	public override void PostClose()
	{
		TravelItems.Invalidate();
		CaravanHelperMod.Instance?.WriteSettings();
	}

	public override void DoWindowContents(Rect r)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "CH_TravelTitle".Translate());
		Text.Font = GameFont.Tiny;
		GUI.color = Color.gray;
		Widgets.Label(new Rect(r.x, r.y + 32f, r.width, 34f), (gear ? "CH_TravelGearDesc" : "CH_TravelFoodDesc").Translate());
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		float y = r.y + 70f;
		List<string> chosen = gear ? CaravanHelperMod.S.travelGear : CaravanHelperMod.S.travelFood;
		var tabs = new List<TabRecord>
		{
			new TabRecord("CH_TravelTabFood".Translate(CaravanHelperMod.S.travelFood.Count), () => { gear = false; scroll = Vector2.zero; }, !gear),
			new TabRecord("CH_TravelTabGear".Translate(CaravanHelperMod.S.travelGear.Count), () => { gear = true; scroll = Vector2.zero; }, gear),
		};
		Rect body = new Rect(r.x, y + 32f, r.width, r.yMax - y - 32f - 50f);
		Widgets.DrawMenuSection(body);
		TabDrawer.DrawTabs(body, tabs);
		Rect inner = body.ContractedBy(8f);
		Widgets.Label(new Rect(inner.x, inner.y + 3f, 70f, 24f), "CH_TravelSearch".Translate());
		search = Widgets.TextField(new Rect(inner.x + 70f, inner.y, inner.width - 70f - 130f, 26f), search ?? "");
		if (Widgets.ButtonText(new Rect(inner.xMax - 120f, inner.y, 120f, 26f), "CH_TravelClear".Translate()))
		{
			chosen.Clear();
			TravelItems.Invalidate();
		}
		List<ThingDef> list = (gear ? equipment : food)
			.Where(d => search.NullOrEmpty() || (d.label ?? d.defName).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
			.OrderByDescending(d => chosen.Contains(d.defName)).ToList();
		Rect outR = new Rect(inner.x, inner.y + 32f, inner.width, inner.height - 32f);
		Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * 28f);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * 28f, view.width, 28f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
			ThingDef d = list[i];
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.DrawHighlightIfMouseover(row);
			try { Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), d); } catch { }
			bool on = chosen.Contains(d.defName), was = on;
			string mod = d.modContentPack != null && !d.modContentPack.IsCoreMod ? "  (" + d.modContentPack.Name + ")" : "";
			Widgets.CheckboxLabeled(new Rect(row.x + 32f, row.y + 2f, row.width - 36f, 24f), d.LabelCap + mod, ref on);
			TooltipHandler.TipRegion(row, d.description ?? "");
			if (on != was)
			{
				if (on) chosen.Add(d.defName); else chosen.Remove(d.defName);
				TravelItems.Invalidate();
			}
		}
		Widgets.EndScrollView();
	}
}
