using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

[HarmonyPatch(typeof(MainTabWindow_Work), "DoWindowContents")]
public static class WorkTabControlsPatch
{
	public static void Postfix(Rect rect)
	{
		if (Event.current.type == EventType.Layout)
		{
			return;
		}
		Map currentMap = Find.CurrentMap;
		ForemanMapComponent comp = ForemanMapComponent.For(currentMap);
		if (comp == null)
		{
			return;
		}
		float num = Mathf.Max(800f, rect.xMax - 440f);
		float y = rect.y + 5f;
		Text.Font = GameFont.Small;
		Rect rect2 = new Rect(num, y, 110f, 28f);
		bool auto = comp.auto;
		string text = "CF_Auto".Translate();
		if (comp.CurrentCrisis != 0)
		{
			text += " (!)";
			GUI.color = new Color(1f, 0.7f, 0.3f);
		}
		Widgets.CheckboxLabeled(rect2, text, ref comp.auto);
		GUI.color = Color.white;
		string text2 = "CF_AutoTip".Translate();
		if (comp.CurrentCrisis != 0)
		{
			text2 += "\n\n" + "CF_TipCrisis".Translate(MapDemand.Label(comp.CurrentCrisis));
		}
		// 2.20: situations (Balanced)
		if (comp.situations != null && comp.situations.Count > 0)
			text2 += "\n\n" + SituationRules.Summary(comp.situations);
		TooltipHandler.TipRegion(rect2, text2);
		if (comp.auto && !auto)
		{
			comp.Run(manual: true);
		}
		Rect rect3 = new Rect(num + 120f, y, 130f, 28f);
		if (Widgets.ButtonText(rect3, "CF_Optimize".Translate()))
		{
			comp.Run(manual: true);
		}
		// 2.32.4: when the steward last planned and when it plans next
		{
			int now = Find.TickManager.TicksGame;
			string tip3 = "CF_OptimizeTip".Translate();
			if (comp.LastRunTick > 0)
				tip3 += "\n\n" + "CF_LastRun".Translate((now - comp.LastRunTick).ToStringTicksToPeriod());
			if (comp.auto)
				tip3 += "\n" + "CF_NextRun".Translate(Math.Max(0, comp.NextRunTick - now).ToStringTicksToPeriod());
			else
				tip3 += "\n" + "CF_AutoOff".Translate();
			TooltipHandler.TipRegion(rect3, tip3);
		}
		Rect rect4 = new Rect(num + 260f, y, 170f, 28f);
		// 2.45: Simple mode has no styles
		if (Modes.Simple)
		{
			Widgets.ButtonText(rect4, ((string)"CF_StyleSimple".Translate()).Truncate(rect4.width - 8f), active: false);
			TooltipHandler.TipRegion(rect4, "CF_StyleSimple_Desc".Translate());
		}
		else if (Widgets.ButtonText(rect4, ((string)"CF_StyleButton".Translate(AutoFocus.ShortLabel(comp))).Truncate(rect4.width - 8f)))
		{
			List<FloatMenuOption> list = new List<FloatMenuOption>();
			ForemanStyle[] array = Styles.All;
			foreach (ForemanStyle foremanStyle in array)
			{
				ForemanStyle local = foremanStyle;
				var opt = new FloatMenuOption((comp.style == local ? "● " : "") + Styles.Label(foremanStyle), delegate
				{
					comp.style = local;
					// 2.32.4: each style brings its own recommended settings
					ForemanSettings fs = ForemanMod.Settings;
					if (fs != null && fs.recPerStyle && !Modes.Calm)
					{
						ForemanMod.ApplyRec(fs, local);
						ForemanMod.Instance?.WriteSettings();
					}
					comp.Run(manual: true);
				});
				opt.tooltip = new TipSignal(Styles.Describe(local));
				list.Add(opt);
			}
			Find.WindowStack.Add(new FloatMenu(list));
		}
		// 2.19: the focus Balanced picked by itself
		string styleTip = "CF_StyleTip".Translate();
		if (AutoFocus.Current(comp) != AutoFocus.None)
			styleTip += "\n\n" + AutoFocus.LongLabel(comp);
		if (comp.situations != null && comp.situations.Count > 0)
			styleTip += "\n\n" + SituationRules.Summary(comp.situations);
		TooltipHandler.TipRegion(rect4, styleTip);
		// Supplies (stock targets) – the same window as the bottom-bar tab.
		Rect rect5 = new Rect(num - 130f, y, 120f, 28f);
		if (Widgets.ButtonText(rect5, "CF_S_Button".Translate()))
		{
			MainButtonDef tab = DefDatabase<MainButtonDef>.GetNamedSilentFail("CF_Supplies");
			if (tab != null)
				Find.MainTabsRoot.ToggleTab(tab);
		}
		TooltipHandler.TipRegion(rect5, "CF_S_ButtonTip".Translate());
		// 2.32: saved priority profiles and the plan history
		Rect rect6 = new Rect(num - 240f, y, 100f, 28f);
		if (Widgets.ButtonText(rect6, "CF_ProfilesButton".Translate()))
			Find.WindowStack.Add(new FloatMenu(StewardPlus.ProfileMenu(comp, currentMap)));
		TooltipHandler.TipRegion(rect6, "CF_ProfilesTip".Translate());
		// 2.45: the mode (Simple / Recommended / Advanced)
		Rect rectMode = new Rect(num - 480f, y, 120f, 28f);
		if (Widgets.ButtonText(rectMode, ((string)Modes.Label(Modes.Current)).Truncate(rectMode.width - 8f)))
			Find.WindowStack.Add(new FloatMenu(Modes.Menu()));
		TooltipHandler.TipRegion(rectMode, "CF_ModeGizmo_Desc".Translate(Modes.Desc(Modes.Current)));
		Rect rect7 = new Rect(num - 350f, y, 100f, 28f);
		if (Widgets.ButtonText(rect7, "CF_HistButton".Translate()))
			Find.WindowStack.Add(new Dialog_StewardHistory(comp));
		TooltipHandler.TipRegion(rect7, "CF_HistTip".Translate());
	}
}
