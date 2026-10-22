using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// The steward's buttons in the Work tab, laid out from the right edge.
[HarmonyPatch(typeof(MainTabWindow_Work), "DoWindowContents")]
public static class WorkTabControlsPatch
{
	public static void Postfix(Rect rect)
	{
		if (Event.current.type == EventType.Layout)
			return;
		ForemanMapComponent comp = ForemanMapComponent.For(Find.CurrentMap);
		if (comp == null)
			return;
		const float h = 28f, gap = 8f;
		float wAuto = 120f, wPlan = 120f, wStyle = 180f, wHist = 90f;
		float x = rect.xMax - (wAuto + wPlan + wStyle + wHist + 3 * gap) - 4f;
		float y = rect.y + 5f;
		// a narrow window: under the vanilla text instead of over it
		if (x < 420f)
			x = Math.Max(rect.x, x);
		Text.Font = GameFont.Small;
		int now = Find.TickManager.TicksGame;

		Rect rAuto = new Rect(x, y, wAuto, h);
		bool on = comp.auto;
		string label = "CF_Auto".Translate();
		if (comp.CurrentCrisis != Crisis.None)
		{
			label += " (!)";
			GUI.color = new Color(1f, 0.7f, 0.3f);
		}
		Widgets.CheckboxLabeled(rAuto, label, ref on);
		GUI.color = Color.white;
		if (on != comp.auto)
			comp.SetAuto(on);
		string tip = "CF2_AutoTip".Translate();
		if (comp.CurrentCrisis != Crisis.None)
			tip += "\n\n" + "CF_TipCrisis".Translate(CrisisText.Label(comp.CurrentCrisis));
		foreach (string line in StewardApi.WorkTabLines(comp))
			tip += "\n\n" + line;
		TooltipHandler.TipRegion(rAuto, tip);
		x += wAuto + gap;

		Rect rPlan = new Rect(x, y, wPlan, h);
		if (Widgets.ButtonText(rPlan, "CF2_PlanNow".Translate()))
		{
			int n = comp.PlanNow(manual: true);
			Messages.Message("CF2_PlanDone".Translate(n), MessageTypeDefOf.SilentInput, historical: false);
		}
		string ptip = "CF2_PlanNowTip".Translate();
		if (comp.lastPlanTick > 0)
			ptip += "\n\n" + "CF_LastRun".Translate((now - comp.lastPlanTick).ToStringTicksToPeriod());
		ptip += "\n" + (comp.auto ? "CF_NextRun".Translate(Math.Max(0, comp.nextRun - now).ToStringTicksToPeriod()) : "CF_AutoOff".Translate());
		TooltipHandler.TipRegion(rPlan, ptip);
		x += wPlan + gap;

		Rect rStyle = new Rect(x, y, wStyle, h);
		if (Widgets.ButtonText(rStyle, ((string)"CF_StyleButton".Translate(Styles.Label(comp.style))).Truncate(wStyle - 8f)))
		{
			var list = new List<FloatMenuOption>();
			foreach (ForemanStyle s in Styles.All)
			{
				ForemanStyle local = s;
				var opt = new FloatMenuOption((comp.style == local ? "● " : "") + Styles.Label(s), () => comp.SetStyle(local));
				opt.tooltip = new TipSignal(Styles.Describe(local));
				list.Add(opt);
			}
			Find.WindowStack.Add(new FloatMenu(list));
		}
		TooltipHandler.TipRegion(rStyle, "CF_StyleTip".Translate());
		x += wStyle + gap;

		Rect rHist = new Rect(x, y, wHist, h);
		if (Widgets.ButtonText(rHist, "CF_HistButton".Translate()))
			Find.WindowStack.Add(new Dialog_StewardHistory(comp));
		TooltipHandler.TipRegion(rHist, "CF_HistTip".Translate());
	}
}

public class Dialog_StewardHistory : Window
{
	private readonly ForemanMapComponent comp;
	private Vector2 scroll;

	public Dialog_StewardHistory(ForemanMapComponent comp)
	{
		this.comp = comp;
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
	}

	public override Vector2 InitialSize => new Vector2(620f, 520f);

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "CF_HistTitle".Translate());
		Text.Font = GameFont.Small;
		Rect outer = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
		List<string> lines = (comp.history ?? new List<string>()).AsEnumerable().Reverse().ToList();
		float hh = lines.Sum(l => Text.CalcHeight(l, outer.width - 20f) + 4f) + 10f;
		Rect view = new Rect(0f, 0f, outer.width - 16f, Math.Max(hh, outer.height));
		Widgets.BeginScrollView(outer, ref scroll, view);
		float yy = 0f;
		foreach (string l in lines)
		{
			float lh = Text.CalcHeight(l, view.width);
			Widgets.Label(new Rect(0f, yy, view.width, lh), l);
			yy += lh + 4f;
		}
		if (lines.Count == 0)
			Widgets.Label(new Rect(0f, 0f, view.width, 30f), "CF_HistEmpty".Translate());
		Widgets.EndScrollView();
	}
}
