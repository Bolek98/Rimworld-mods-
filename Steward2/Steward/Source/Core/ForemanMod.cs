using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public sealed class ForemanMod : Mod
{
	public static ForemanMod Instance;
	public const string Version = "3.4.0";
	public static ForemanSettings Settings;

	private int tab;
	private Vector2 scroll;
	private float viewH = 1200f;

	public ForemanMod(ModContentPack content) : base(content)
	{
		Instance = this;
		Settings = GetSettings<ForemanSettings>();
		MUI.Init(new ForemanSettings(), null, "maxPriorityOverride");
		var h = new Harmony("misak.colonyforeman");
		h.PatchAll(typeof(ForemanMod).Assembly);
		MUI.PatchWindow(h, typeof(ForemanMod));
		Planner.RegisterCoreSteps();
		MNews.Register(content, Version);
		MLog.Info("[Steward] " + Version + " core loaded.");
	}

	public override string SettingsCategory() => "Steward";

	public override void DoSettingsWindowContents(Rect inRect)
	{
		ForemanSettings s = Settings;
		var tabs = new List<TabRecord>
		{
			new TabRecord("CF2_TabGeneral".Translate(), () => tab = 0, tab == 0),
			new TabRecord("CF2_TabRules".Translate(), () => tab = 1, tab == 1)
		};
		IReadOnlyList<SettingsPage> pages = StewardApi.Pages;
		for (int i = 0; i < pages.Count; i++)
		{
			int k = i + 2;
			tabs.Add(new TabRecord(pages[i].label, () => tab = k, tab == k));
		}
		Rect body = new Rect(inRect.x, inRect.y + 32f, inRect.width, inRect.height - 32f);
		TabDrawer.DrawTabs(body, tabs);
		Rect inner = body.ContractedBy(10f);
		if (tab >= 2 && tab - 2 < pages.Count)
		{
			pages[tab - 2].draw?.Invoke(inner);
			MUI.Version(inRect, "Steward " + Version);
			return;
		}
		Rect view = new Rect(0f, 0f, inner.width - 18f, viewH);
		Widgets.BeginScrollView(inner, ref scroll, view);
		var l = new Listing_Standard();
		l.Begin(view);
		l.maxOneColumn = true;   // no hidden second column when the list is taller than the view
		if (tab == 0)
			General(l, s);
		else
			Rules(l, s);
		viewH = l.CurHeight + 40f;
		l.End();
		Widgets.EndScrollView();
		MUI.Version(inRect, "Steward " + Version);
	}

	private static void General(Listing_Standard l, ForemanSettings s)
	{
		MUI.Section(l, "CF2_SecPlan".Translate());
		MUI.Check(l, "CF2_AutoByDefault".Translate(), "CF2_AutoByDefaultTip".Translate(), ref s.autoByDefault, s, "autoByDefault");
		s.intervalHours = Mathf.RoundToInt(MUI.Slider(l, "CF2_Interval".Translate(s.intervalHours), s.intervalHours, 1f, 24f, "CF2_IntervalTip".Translate(), s, "intervalHours"));
		s.holdHours = Mathf.RoundToInt(MUI.Slider(l, "CF2_Hold".Translate(s.holdHours), s.holdHours, 0f, 48f, "CF2_HoldTip".Translate(), s, "holdHours"));
		MUI.Check(l, "CF2_OneTop".Translate(), "CF2_OneTopTip".Translate(), ref s.oneTopPriority, s, "oneTopPriority");
		MUI.Check(l, "CF2_Backup".Translate(), "CF2_BackupTip".Translate(), ref s.backupWorkers, s, "backupWorkers");
		MUI.Check(l, "CF2_Opposed".Translate(), "CF2_OpposedTip".Translate(), ref s.assignOpposed, s, "assignOpposed");
		MUI.Check(l, "CF2_Injured".Translate(), "CF2_InjuredTip".Translate(), ref s.lightWorkInjured, s, "lightWorkInjured");
		MUI.Check(l, "CF2_Passion".Translate(), "CF2_PassionTip".Translate(), ref s.moodPassion, s, "moodPassion");
		s.preferenceBonus = MUI.Slider(l, "CF2_Prefer".Translate(s.preferenceBonus.ToStringPercent()), s.preferenceBonus, 0f, 1f, "CF2_PreferTip".Translate(), s, "preferenceBonus");
		MUI.Check(l, "CF2_Lodgers".Translate(), "CF2_LodgersTip".Translate(), ref s.includeLodgers, s, "includeLodgers");
		MUI.Check(l, "CF2_Mechs".Translate(), "CF2_MechsTip".Translate(), ref s.countMechs, s, "countMechs");
		MUI.Check(l, "CF32_Apprentices".Translate(), "CF32_ApprenticesTip".Translate(), ref s.apprentices, s, "apprentices");

		MUI.Section(l, "CF2_SecStyles".Translate());
		s.styleStrength = MUI.Slider(l, "CF2_StyleStrength".Translate(s.styleStrength.ToString("0.0")), s.styleStrength, 0f, 3f, "CF2_StyleStrengthTip".Translate(), s, "styleStrength");
		MUI.Check(l, "CF2_StyleStrict".Translate(), "CF2_StyleStrictTip".Translate(), ref s.styleStrict, s, "styleStrict");
		if (s.styleStrict)
			MUI.Check(l, "CF2_StyleOthersOff".Translate(), "CF2_StyleOthersOffTip".Translate(), ref s.styleOthersOff, s, "styleOthersOff");

		if (ModsConfig.BiotechActive)
		{
			MUI.Section(l, "CF2_SecChildren".Translate());
			MUI.Check(l, "CF2_ChildLearning".Translate(), "CF2_ChildLearningTip".Translate(), ref s.childLearning, s, "childLearning");
			MUI.Check(l, "CF2_ParentCare".Translate(), "CF2_ParentCareTip".Translate(), ref s.parentCare, s, "parentCare");
		}

		MUI.Section(l, "CF2_SecPriorities".Translate());
		string lv = s.maxPriorityOverride <= 0 ? (string)"CF2_LevelsAuto".Translate(PriorityRange.Max) : s.maxPriorityOverride.ToString();
		MUI.RowLabel(l, "CF2_Levels".Translate(lv), "CF2_LevelsTip".Translate(), s, "maxPriorityOverride");
		s.maxPriorityOverride = Mathf.RoundToInt(MUI.SliderBar(l.GetRect(22f), s.maxPriorityOverride, 0f, 9f, 1f));
		if (s.maxPriorityOverride > 0 && s.maxPriorityOverride < 4)
			s.maxPriorityOverride = 4;
		l.Gap(6f);
		MUI.Check(l, "CF2_PlanMessages".Translate(), "CF2_PlanMessagesTip".Translate(), ref s.planMessages, s, "planMessages");
		MUI.Check(l, "CF31_PlanPreview".Translate(), "CF31_PlanPreviewTip".Translate(), ref s.planPreview, s, "planPreview");
		MUI.Check(l, "CF32_OfferNew".Translate(), "CF32_OfferNewTip".Translate(), ref s.offerNewcomers, s, "offerNewcomers");
		MUI.Check(l, "CF32_Weekly".Translate(), "CF32_WeeklyTip".Translate(), ref s.weeklySummary, s, "weeklySummary");
	}

	private static void Rules(Listing_Standard l, ForemanSettings s)
	{
		MUI.Check(l, "CF2_UseMyRules".Translate(), "CF2_UseMyRulesTip".Translate(), ref s.useMyRules, s, "useMyRules");
		l.Gap(4f);
		Text.Font = GameFont.Tiny;
		GUI.color = MUI.Muted;
		l.Label("CF2_RulesHint".Translate());
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		l.Gap(6f);
		foreach (WorkTypeDef w in WorkCatalog.Visible)
		{
			if (WorkCatalog.Kind(w) == WorkKind.Emergency)
				continue;
			Rect row = l.GetRect(26f);
			Widgets.DrawHighlightIfMouseover(row);
			float x = row.x;
			Widgets.Label(new Rect(x, row.y + 3f, 170f, 24f), w.labelShort.CapitalizeFirst().Truncate(165f));
			x += 175f;
			int min = s.workerMin.TryGetValue(w.defName, out int a) ? a : 0;
			int max = s.workerMax.TryGetValue(w.defName, out int b) ? b : 0;
			float imp = s.Importance(w);
			Widgets.Label(new Rect(x, row.y + 3f, 40f, 24f), "CF2_Min".Translate());
			if (Widgets.ButtonText(new Rect(x + 40f, row.y, 24f, 24f), "-")) s.SetCount(s.workerMin, w, min - 1);
			Widgets.Label(new Rect(x + 68f, row.y + 3f, 24f, 24f), min == 0 ? "–" : min.ToString());
			if (Widgets.ButtonText(new Rect(x + 90f, row.y, 24f, 24f), "+")) s.SetCount(s.workerMin, w, min + 1);
			x += 125f;
			Widgets.Label(new Rect(x, row.y + 3f, 40f, 24f), "CF2_Max".Translate());
			if (Widgets.ButtonText(new Rect(x + 40f, row.y, 24f, 24f), "-")) s.SetCount(s.workerMax, w, max - 1);
			Widgets.Label(new Rect(x + 68f, row.y + 3f, 24f, 24f), max == 0 ? "–" : max.ToString());
			if (Widgets.ButtonText(new Rect(x + 90f, row.y, 24f, 24f), "+")) s.SetCount(s.workerMax, w, max + 1);
			x += 125f;
			Widgets.Label(new Rect(x, row.y + 3f, 90f, 24f), "CF2_Importance".Translate());
			float ni = MUI.SliderBar(new Rect(x + 92f, row.y + 2f, Math.Max(80f, row.xMax - x - 140f), 22f), imp, 0f, 1f, 0.05f);
			if (Math.Abs(ni - imp) > 0.001f)
				s.urgencyOverrides[w.defName] = ni;
			Widgets.Label(new Rect(row.xMax - 40f, row.y + 3f, 40f, 24f), ni.ToStringPercent());
		}
	}

	public override void WriteSettings()
	{
		base.WriteSettings();
		foreach (SettingsPage p in StewardApi.Pages)
		{
			try { p.write?.Invoke(); }
			catch (Exception ex) { MLog.Warn("[Steward] add-on settings: " + ex.Message); }
		}
	}
}
