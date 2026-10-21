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
	public const string Version = "2.50.1";

	// 2.35.8: recommended values for each style (interval h, preferences,
	// style strength, specialists share)
	public struct Rec
	{
		public int interval; public float pref, strength, spec;
		public Rec(int i, float p, float st, float sp) { interval = i; pref = p; strength = st; spec = sp; }
	}

	public static Rec RecFor(ForemanStyle style)
	{
		switch (style)
		{
		case ForemanStyle.Survival:
		case ForemanStyle.Defense: return new Rec(2, 0.15f, 2.5f, 0.6f);
		case ForemanStyle.Production:
		case ForemanStyle.Industry:
		case ForemanStyle.Trade:
		case ForemanStyle.Research: return new Rec(4, 0.25f, 2.2f, 0.4f);
		case ForemanStyle.Care: return new Rec(3, 0.3f, 2.2f, 0.4f);
		case ForemanStyle.Comfort: return new Rec(6, 0.4f, 1.5f, 0.5f);
		case ForemanStyle.Tidy: return new Rec(3, 0.15f, 2.5f, 0.3f);
		case ForemanStyle.Mining:
		case ForemanStyle.Smithing:
		case ForemanStyle.Tailoring:
		case ForemanStyle.Art: return new Rec(4, 0.3f, 2.2f, 0.4f);
		case ForemanStyle.Kitchen:
		case ForemanStyle.HuntFish:
		case ForemanStyle.Prison: return new Rec(3, 0.25f, 2.2f, 0.4f);
		default: return new Rec(4, 0.25f, 2f, 0.5f);
		}
	}

	public static ForemanStyle CurrentStyle()
	{
		try { return Find.CurrentMap?.GetComponent<ForemanMapComponent>()?.style ?? ForemanStyle.Balanced; }
		catch { return ForemanStyle.Balanced; }
	}

	public static void ApplyRec(ForemanSettings s, ForemanStyle style)
	{
		Rec r = RecFor(style);
		s.intervalHours = r.interval;
		s.preferenceBonus = r.pref;
		s.styleStrength = r.strength;
		s.specialistShare = r.spec;
	}

	private static float SliderRec(Listing_Standard l, string label, float value, float min, float max, float rec, string recText, string tip)
	{
		Rect r = l.GetRect(30f);
		Rect btn = r.RightPartPixels(150f);
		Rect main = new Rect(r.x, r.y, r.width - 158f, r.height);
		Widgets.Label(new Rect(main.x, main.y + 3f, main.width * 0.55f, 24f), label);
		value = Widgets.HorizontalSlider(new Rect(main.x + main.width * 0.55f, main.y + 8f, main.width * 0.45f, 18f), value, min, max, middleAlignment: true);
		if (!tip.NullOrEmpty())
			TooltipHandler.TipRegion(main, tip);
		bool isRec = Mathf.Abs(value - rec) < 0.001f;
		GUI.color = isRec ? Color.gray : Color.white;
		if (Widgets.ButtonText(new Rect(btn.x, btn.y + 2f, btn.width, 26f), "CF_Recommended".Translate(recText), active: !isRec))
			value = rec;
		GUI.color = Color.white;
		TooltipHandler.TipRegion(btn, "CF_Recommended_Desc".Translate());
		l.Gap(l.verticalSpacing);
		return value;
	}

	public static ForemanSettings Settings;

	private Vector2 scroll;

	public ForemanMod(ModContentPack content)
		: base(content)
	{
		Settings = GetSettings<ForemanSettings>();
		Instance = this;
		// 2.45: modes – Simple and Recommended keep their values up to date
		Modes.InitMarks();
		// 2.49.8: only Simple is locked and refreshed; Recommended keeps your changes
		if (Modes.Current == StewardMode.Simple)
			Modes.ApplyPreset(Settings, Modes.Current);
		LongEventHandler.ExecuteWhenFinished(delegate
		{
			var harmony = new Harmony("misak.colonyforeman");
			harmony.PatchAll();
			MUI.PatchWindow(harmony, typeof(ForemanMod));
			MLog.Game("[Steward] v" + Version + " (messages: Logs\\Steward.log) loaded (balanced with auto focus, my style, 22 styles, " + CrisisRules.All.Length + " crises, " + SituationRules.Count + " situations, self-supply, meal target, supplies, livestock, production, power, household).");
		});
	}

	public override string SettingsCategory()
	{
		return "Steward";
	}

	private int tab;
	private Vector2 levelScroll;

	// 2.8 (Steward): settings in tabs – General, Work types (importance,
	// min/max colonists) and Priority levels (colonists on each level).
	private static readonly StewardMode[] ModeOrder = { StewardMode.Simple, StewardMode.Recommended, StewardMode.Advanced };

	public override void DoSettingsWindowContents(Rect inRect)
	{
		// 2.45: three mode cards on top; the details are locked unless Advanced
		MUI.Version(inRect, "v" + Version + " · Bolek98");
		GUI.color = MUI.Muted;
		Text.Font = GameFont.Tiny;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 20f), "CF_ModeHeader".Translate());
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
		float cw = (inRect.width - 24f) / 3f;
		for (int i = 0; i < 3; i++)
		{
			StewardMode m = ModeOrder[i];
			Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 84f);
			if (MUI.ProfileCard(c, Modes.Label(m), ("CF_Mode_" + m + "_Card").Translate(), Modes.Current == m, m == StewardMode.Recommended) && Modes.Current != m)
			{
				StewardMode target = m;
				int changed = MUI.Diff(Settings);
				if (target != StewardMode.Advanced && Modes.Current == StewardMode.Advanced && changed > 0)
					Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("CF_ModeConfirm".Translate(Modes.Label(target), changed), () => Modes.Set(target), true));
				else
					Modes.Set(target);
			}
		}
		float top = inRect.y + 22f + 84f + 8f;
		bool locked = Modes.Current == StewardMode.Simple;
		if (locked)
			top += MUI.LockNote(new Rect(inRect.x, top, inRect.width, 0f)) + 4f;
		inRect = new Rect(inRect.x, top - 2f, inRect.width, inRect.yMax - (top - 2f));
		var tabs = new List<TabRecord>
		{
			new TabRecord("CF_TabGeneral".Translate(), () => tab = 0, tab == 0),
			new TabRecord("CF_TabWork".Translate(), () => tab = 1, tab == 1),
			new TabRecord("CF_TabLevels".Translate(), () => tab = 2, tab == 2),
			new TabRecord("CF_TabCrisis".Translate(), () => tab = 3, tab == 3),
			new TabRecord("CF_TabExtras".Translate(), () => tab = 4, tab == 4),
			new TabRecord("CF_TabMessages".Translate(), () => tab = 5, tab == 5)
		};
		Rect body = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
		Widgets.DrawMenuSection(body);
		TabDrawer.DrawTabs(body, tabs, 220f);
		Rect inner = body.ContractedBy(10f);
		// 2.46.2: messages and comfort – your own choice, outside the modes, never locked
		if (tab == 5)
		{
			DrawMessages(inner);
			return;
		}
		// 2.49.7: a click on the locked details offers the switch to Advanced at once –
		// before, the click did nothing and it looked like the setting jumped back.
		if (locked && Event.current.type == EventType.MouseDown && Mouse.IsOver(inner))
		{
			Event.current.Use();
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("CF_LockedClick".Translate(Modes.Label(Modes.Current)), () => Modes.Set(StewardMode.Recommended), false));
		}
		MUI.BeginLock(locked);
		try
		{
		switch (tab)
		{
			case 0: DrawGeneral(inner); break;
			case 1: DrawWorkTypes(inner); break;
			case 2: DrawLevels(inner); break;
			case 4: DrawExtras(inner); break;
			default: DrawCrises(inner); break;
		}
		}
		finally
		{
			MUI.EndLock(inner);
		}
	}

	private void DrawMessages(Rect inRect)
	{
		ForemanSettings s = Settings;
		Listing_Standard l = new Listing_Standard();
		l.Begin(inRect);
		l.ColumnWidth = (inRect.width - 34f) / 2f;
		l.CheckboxLabeled("CF_QuietMessages".Translate(), ref s.quietMessages, "CF_QuietMessages_Desc".Translate());
		l.Gap(6f);
		l.CheckboxLabeled("CF_SitToEat".Translate(), ref s.sitToEat, "CF_SitToEat_Desc".Translate());
		l.Gap(10f);
		GUI.color = MUI.Muted;
		l.Label("CF_MessagesNote".Translate());
		GUI.color = Color.white;
		l.End();
	}

	// 2.32: extras
	private void DrawExtras(Rect inRect)
	{
		ForemanSettings s = Settings;
		Listing_Standard l = new Listing_Standard();
		l.Begin(inRect);
		l.ColumnWidth = (inRect.width - 34f) / 2f;
		Check(l, "CF_RaidSafeArea", ref s.raidSafeArea, "raidSafeArea");
		Check(l, "CF_LightWork", ref s.lightWorkInjured, "lightWorkInjured");
		Check(l, "CF_MoodPassion", ref s.moodPassion, "moodPassion");
		l.Gap(6f);
		s.learningSlots = Mathf.RoundToInt(l.SliderLabeled("CF_Learning".Translate(s.learningSlots), s.learningSlots, 0f, 3f, 0.6f, "CF_Learning_Desc".Translate()));
		s.learningMaxSkill = Mathf.RoundToInt(l.SliderLabeled("CF_LearningMax".Translate(s.learningMaxSkill), s.learningMaxSkill, 3f, 15f, 0.6f, "CF_LearningMax_Desc".Translate()));
		l.Gap(6f);
		Check(l, "CF_MasterBills", ref s.masterBills, "masterBills");
		l.NewColumn();
		bool schedBefore = s.autoSchedule;
		Check(l, "CF_AutoSchedule", ref s.autoSchedule, "autoSchedule");
		// 2.39: switched on → everybody gets the schedule at once
		if (!schedBefore && s.autoSchedule && Current.Game != null)
			foreach (Map m in Find.Maps)
				m.GetComponent<ForemanMapComponent>()?.scheduled.Clear();
		Check(l, "CF_ScheduleCare", ref s.scheduleCare, "scheduleCare");
		if (s.autoSchedule)
			Check(l, "CF_NightShift", ref s.nightShift, "nightShift");
		Check(l, "CF_SkillAlert", ref s.skillAlert, "skillAlert");
		l.Gap(10f);
		l.Label("CF_ExtrasNote".Translate());
		l.End();
	}

	private void DrawGeneral(Rect inRect)
	{
		ForemanSettings settings = Settings;
		Listing_Standard l = new Listing_Standard();
		l.Begin(inRect);
		l.ColumnWidth = (inRect.width - 34f) / 2f;
		Check(l, "CF_AutoByDefault", ref settings.autoByDefault, "autoByDefault");
		Check(l, "CF_UseDemand", ref settings.useDemand, "useDemand");
		Check(l, "CF_UseCrisis", ref settings.useCrisis, "useCrisis");
		Check(l, "CF_CrisisMessages", ref settings.crisisMessages, "crisisMessages");
		Check(l, "CF_AutoMessages", ref settings.autoMessages, "autoMessages");
		if (DefDatabase<WorkTypeDef>.GetNamedSilentFail("KAU_UrgentHaul") != null)
			Check(l, "CF_UrgentHaulFew", ref settings.urgentHaulFew, "urgentHaulFew");
			Check(l, "CF_ChildLearning", ref settings.childLearning, "childLearning");
			Check(l, "CF_ParentCare", ref settings.parentCare, "parentCare");
			Check(l, "CF_NoStorageAlert", ref settings.noStorageAlert, "noStorageAlert");
		Check(l, "CF_Apprentices", ref settings.apprentices, "apprentices");
		Check(l, "CF_Stability", ref settings.stability, "stability");
		Check(l, "CF_CountMechs", ref settings.countMechs, "countMechs");
		Check(l, "CF_AssignOpposed", ref settings.assignOpposed, "assignOpposed");
		Check(l, "CF_Coverage", ref settings.coverage, "coverage");
		Check(l, "CF_BalancedAutoFocus", ref settings.balancedAutoFocus, "balancedAutoFocus");
		Check(l, "CF_Backup", ref settings.backupWorkers, "backupWorkers");
		Check(l, "CF_OneTop", ref settings.oneTopPriority, "oneTopPriority");
		Check(l, "CF_Lodgers", ref settings.includeLodgers, "includeLodgers");
		Check(l, "CF_MedicalResponse", ref settings.medicalResponse, "medicalResponse");
		Check(l, "CF_FireResponse", ref settings.fireResponse, "fireResponse");
		Check(l, "CF_S_Messages", ref settings.supplyMessages, "supplyMessages");
		if (PickUpAndHaulCompat.Present)
			Check(l, "CF_PUAH", ref settings.usePickUpAndHaul, "usePickUpAndHaul");
		l.NewColumn();
		// Priority levels: − / + instead of a slider.
		string levelText = !PriorityRange.PriorityModActive
			? (string)"CF_P_LevelsNoMod".Translate()
			: settings.maxPriorityOverride == 0
				? (string)"CF_P_LevelsAuto".Translate(AutoLevels())
				: (string)"CF_P_LevelsManual".Translate(settings.maxPriorityOverride);
		Rect lv = l.GetRect(28f);
		Widgets.Label(new Rect(lv.x, lv.y + 3f, lv.width - 80f, 24f), levelText);
		TooltipHandler.TipRegion(lv, "CF_P_LevelsTip".Translate());
		if (Widgets.ButtonText(new Rect(lv.xMax - 76f, lv.y, 36f, 26f), "−"))
			settings.maxPriorityOverride = settings.maxPriorityOverride <= 4 ? 0 : settings.maxPriorityOverride - 1;
		if (Widgets.ButtonText(new Rect(lv.xMax - 38f, lv.y, 36f, 26f), "+"))
			settings.maxPriorityOverride = Mathf.Clamp(settings.maxPriorityOverride == 0 ? 4 : settings.maxPriorityOverride + 1, 4, 9);
		l.Gap(6f);
		// 2.35.8: each slider has a "Recommended" button, plus one for all of them
		ForemanStyle curStyle = CurrentStyle();
		Rec rec = RecFor(curStyle);
		Rect recRow = l.GetRect(28f);
		Widgets.CheckboxLabeled(recRow.LeftPartPixels(recRow.width - 300f), "CF_RecAuto".Translate(), ref settings.recPerStyle);
		TooltipHandler.TipRegion(recRow.LeftPartPixels(recRow.width - 300f), "CF_RecAuto_Desc".Translate());
		if (Widgets.ButtonText(recRow.RightPartPixels(290f), ((string)"CF_RecommendedAll".Translate(Styles.Label(curStyle))).Truncate(286f)))
			ApplyRec(settings, curStyle);
		l.Gap(4f);
		settings.intervalHours = Mathf.RoundToInt(SliderRec(l, "CF_Interval".Translate(settings.intervalHours), settings.intervalHours, 1f, 24f, rec.interval, rec.interval.ToString(), "CF_Interval_Desc".Translate()));
		settings.preferenceBonus = SliderRec(l, "CF_PreferenceBonus".Translate(settings.preferenceBonus.ToString("0.00")), settings.preferenceBonus, 0f, 0.6f, rec.pref, rec.pref.ToString("0.00"), "CF_PreferenceBonus_Desc".Translate());
		settings.styleStrength = Mathf.Round(SliderRec(l, "CF_StyleStrength".Translate(settings.styleStrength.ToString("0.0")), settings.styleStrength, 0.5f, 3f, rec.strength, rec.strength.ToString("0.0") + "×", "CF_StyleStrength_Desc".Translate()) * 10f) / 10f;
		Check(l, "CF_StyleStrict", ref settings.styleStrict, "styleStrict");
		if (settings.styleStrict)
			Check(l, "CF_StyleOthersOff", ref settings.styleOthersOff, "styleOthersOff");
		settings.specialistShare = SliderRec(l, "CF_SpecialistShare".Translate(Mathf.RoundToInt(settings.specialistShare * 100f)), settings.specialistShare, 0.2f, 1f, rec.spec, Mathf.RoundToInt(rec.spec * 100f) + " %", "CF_SpecialistShare_Desc".Translate());
		// 2.22: priority areas
		l.GapLine();
		Check(l, "CF_PA_Enable", ref settings.priorityAreas, "priorityAreas");
		if (settings.priorityAreas)
		{
			string wl = settings.priorityAreaWorkers <= 0 ? (string)"CF_PA_WorkersAll".Translate() : (string)"CF_PA_Workers".Translate(settings.priorityAreaWorkers);
			settings.priorityAreaWorkers = Mathf.RoundToInt(l.SliderLabeled(wl, settings.priorityAreaWorkers, 0f, 10f, 0.55f, "CF_PA_Workers_Desc".Translate()));
		}
		// 2.18: re-assign work at once with the settings just changed
		l.Gap(12f);
		bool inGame = Current.ProgramState == ProgramState.Playing;
		Rect re = l.GetRect(32f);
		if (Widgets.ButtonText(re, "CF_W_Reassign".Translate(), active: inGame) && inGame)
		{
			int changed = ForemanMapComponent.ReassignAll(out int maps);
			if (changed < 0)
				Messages.Message("CF_W_ReassignNone".Translate(), MessageTypeDefOf.RejectInput, historical: false);
			else
				Messages.Message("CF_W_ReassignDone".Translate(maps, changed), MessageTypeDefOf.SilentInput, historical: false);
		}
		TooltipHandler.TipRegion(re, inGame ? "CF_W_Reassign_Desc".Translate() : "CF_W_ReassignNoGame".Translate());
		l.End();
	}

	private static int AutoLevels()
	{
		int keep = Settings.maxPriorityOverride;
		Settings.maxPriorityOverride = 0;
		int auto = PriorityRange.Max;
		Settings.maxPriorityOverride = keep;
		return auto;
	}

	private void DrawWorkTypes(Rect inRect)
	{
		ForemanSettings settings = Settings;
		float y = inRect.y;
		y = BalancedOnlyNote(inRect, y);
		y = MyRulesToggle(inRect, y, settings);
		Widgets.Label(new Rect(inRect.x, y, inRect.width - 160f, 28f), "CF_ImportanceHeader".Translate());
		if (Widgets.ButtonText(new Rect(inRect.xMax - 150f, y, 150f, 26f), "CF_ResetImportance".Translate()))
		{
			settings.urgencyOverrides.Clear();
			settings.workerMin.Clear();
			settings.workerMax.Clear();
			WorkPolicies.Invalidate();
		}
		y += 30f;
		List<WorkTypeDef> list = WorkPolicies.VisibleWorkTypes();
		float rowH = 30f;
		Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
		Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, list.Count * rowH + 26f);
		Widgets.BeginScrollView(outRect, ref scroll, viewRect);
		float w = viewRect.width;
		GUI.color = new Color(0.75f, 0.75f, 0.75f);
		Widgets.Label(new Rect(w * 0.27f, 0f, w * 0.3f, 24f), "CF_ColImportance".Translate());
		Widgets.Label(new Rect(w * 0.66f, 0f, w * 0.17f, 24f), "CF_ColMin".Translate());
		Widgets.Label(new Rect(w * 0.83f, 0f, w * 0.17f, 24f), "CF_ColMax".Translate());
		GUI.color = Color.white;
		TooltipHandler.TipRegion(new Rect(w * 0.66f, 0f, w * 0.34f, 24f), "CF_CountTip".Translate());
		for (int i = 0; i < list.Count; i++)
		{
			WorkTypeDef wt = list[i];
			Rect rect = new Rect(0f, 26f + i * rowH, w, rowH);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(rect);
			float def = WorkPolicies.DefaultUrgency(wt);
			float urgency = WorkPolicies.Get(wt).urgency;
			Widgets.Label(new Rect(rect.x + 4f, rect.y + 4f, w * 0.26f, 24f), wt.labelShort.CapitalizeFirst());
			float slid = Widgets.HorizontalSlider(new Rect(w * 0.27f, rect.y + 8f, w * 0.3f, 18f), urgency, 0f, 1f, middleAlignment: true, null, null, null, 0.05f);
			GUI.color = Mathf.Abs(urgency - def) > 0.001f ? new Color(1f, 0.85f, 0.4f) : Color.white;
			Widgets.Label(new Rect(w * 0.58f, rect.y + 4f, w * 0.07f, 24f), urgency.ToString("0.00"));
			GUI.color = Color.white;
			TooltipHandler.TipRegion(new Rect(0f, rect.y, w * 0.65f, rowH), "CF_ImportanceTip".Translate(def.ToString("0.00")));
			// only a real change by the player is written
			if (Mathf.Abs(slid - urgency) > 0.02f)
			{
				if (Mathf.Abs(slid - def) < 0.001f)
					settings.urgencyOverrides.Remove(wt.defName);
				else
					settings.urgencyOverrides[wt.defName] = slid;
				WorkPolicies.Invalidate();
			}
			CountBox(new Rect(w * 0.66f, rect.y + 2f, w * 0.16f, 26f), settings, settings.workerMin, wt);
			CountBox(new Rect(w * 0.83f, rect.y + 2f, w * 0.16f, 26f), settings, settings.workerMax, wt);
		}
		Widgets.EndScrollView();
	}

	// How many colonists on priority 1, 2, 3 … for each work type.
	// Steward 2.11: these tabs are used only by the Balanced style.
	private static float BalancedOnlyNote(Rect inRect, float y)
	{
		string note = "CF_BalancedOnly".Translate();
		float h = Text.CalcHeight(note, inRect.width);
		GUI.color = new Color(1f, 0.85f, 0.4f);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, h), note);
		GUI.color = Color.white;
		return y + h + 6f;
	}

	private Vector2 crisisScroll;

	// 2.13: every crisis with its trigger and what the colonists do – each
	// one can be switched off.
	private void DrawCrises(Rect inRect)
	{
		ForemanSettings settings = Settings;
		float y = inRect.y;
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		string head = "CF_CrisisHead".Translate();
		float hh = Text.CalcHeight(head, inRect.width);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, hh), head);
		GUI.color = Color.white;
		y += hh + 6f;
		if (!settings.useCrisis)
		{
			GUI.color = new Color(1f, 0.85f, 0.4f);
			Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "CF_CrisisAllOff".Translate());
			GUI.color = Color.white;
			y += 28f;
		}
		Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
		float w = outRect.width - 20f;
		float total = 0f;
		var heights = new List<float>();
		foreach (Crisis c in CrisisRules.All)
		{
			string body = Body(c);
			float h = 28f + Text.CalcHeight(body, w - 30f) + 8f;
			heights.Add(h);
			total += h;
		}
		// 2.20: situations below the crises
		string sitHead = "CF_SitSettingsHead".Translate();
		float sitHeadH = Text.CalcHeight(sitHead, w) + 40f;
		var sits = SituationRules.All.ToList();
		var sitHeights = new List<float>();
		foreach (Situation st in sits)
		{
			float h = 28f + Text.CalcHeight(SitBody(st), w - 30f) + 8f;
			sitHeights.Add(h);
			total += h;
		}
		total += sitHeadH + 16f;
		Rect view = new Rect(0f, 0f, w, total);
		Widgets.BeginScrollView(outRect, ref crisisScroll, view);
		float cy = 0f;
		for (int i = 0; i < CrisisRules.All.Length; i++)
		{
			Crisis c = CrisisRules.All[i];
			Rect row = new Rect(0f, cy, w, heights[i]);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool on = (settings.crisisOff & (long)c) == 0;
			bool was = on;
			Widgets.CheckboxLabeled(new Rect(4f, cy + 2f, 360f, 26f), CrisisRules.Label(c).CapitalizeFirst(), ref on);
			if (on != was)
			{
				if (on) settings.crisisOff &= ~(long)c;
				else settings.crisisOff |= (long)c;
			}
			GUI.color = on ? new Color(0.85f, 0.85f, 0.85f) : Color.gray;
			Widgets.Label(new Rect(30f, cy + 28f, w - 30f, heights[i] - 30f), Body(c));
			GUI.color = Color.white;
			cy += heights[i];
		}
		cy += 16f;
		Widgets.DrawLineHorizontal(0f, cy - 8f, w);
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(0f, cy, w, 32f), "CF_SitSettingsTitle".Translate());
		Text.Font = GameFont.Small;
		Rect master = new Rect(w - 260f, cy + 4f, 256f, 26f);
		Widgets.CheckboxLabeled(master, "CF_UseSituations".Translate(), ref settings.useSituations);
		TooltipHandler.TipRegion(master, "CF_UseSituations_Desc".Translate());
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		Widgets.Label(new Rect(0f, cy + 34f, w, sitHeadH - 40f), sitHead);
		GUI.color = Color.white;
		cy += sitHeadH;
		for (int i = 0; i < sits.Count; i++)
		{
			Situation st = sits[i];
			Rect row = new Rect(0f, cy, w, sitHeights[i]);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool on = !settings.situationOff.Contains(st.ToString());
			bool was = on;
			Widgets.CheckboxLabeled(new Rect(4f, cy + 2f, 360f, 26f), SituationRules.Label(st).CapitalizeFirst(), ref on);
			if (on != was)
			{
				if (on) settings.situationOff.Remove(st.ToString());
				else settings.situationOff.Add(st.ToString());
			}
			GUI.color = on && settings.useSituations ? new Color(0.85f, 0.85f, 0.85f) : Color.gray;
			Widgets.Label(new Rect(30f, cy + 28f, w - 30f, sitHeights[i] - 30f), SitBody(st));
			GUI.color = Color.white;
			cy += sitHeights[i];
		}
		Widgets.EndScrollView();
	}

	private static string SitBody(Situation s)
	{
		return ("CF_SitWhen_" + s).Translate() + "\n" + "CF_CrisisDoes".Translate(SituationRules.Describe(s));
	}

	private static string Body(Crisis c)
	{
		return ("CF_CrisisWhen_" + c).Translate() + "\n" + "CF_CrisisDoes".Translate(CrisisRules.Describe(c));
	}

	private void DrawLevels(Rect inRect)
	{
		ForemanSettings settings = Settings;
		int levels = PriorityRange.Max;
		float y = inRect.y;
		y = MyRulesToggle(inRect, y, settings);
		// 2.17: the plan of levels applies in every colony style
		string allNote = "CF_LevelsAllStyles".Translate();
		float nh = Text.CalcHeight(allNote, inRect.width);
		GUI.color = new Color(0.6f, 0.9f, 0.6f);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, nh), allNote);
		GUI.color = Color.white;
		y += nh + 6f;
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		string desc = "CF_LevelsDesc".Translate();
		float h = Text.CalcHeight(desc, inRect.width);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, h), desc);
		GUI.color = Color.white;
		y += h + 6f;
		if (Widgets.ButtonText(new Rect(inRect.xMax - 150f, y, 150f, 26f), "CF_LevelsReset".Translate()))
			settings.levelPlan.Clear();
		y += 30f;
		List<WorkTypeDef> list = WorkPolicies.VisibleWorkTypes();
		float rowH = 30f;
		Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
		Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, list.Count * rowH + 26f);
		Widgets.BeginScrollView(outRect, ref levelScroll, viewRect);
		float w = viewRect.width;
		float labelW = w * 0.24f;
		float colW = (w - labelW - 90f) / levels;
		GUI.color = new Color(0.75f, 0.75f, 0.75f);
		for (int lvl = 1; lvl <= levels; lvl++)
		{
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(new Rect(labelW + (lvl - 1) * colW, 0f, colW, 24f), "CF_LevelCol".Translate(lvl));
			Text.Anchor = TextAnchor.UpperLeft;
		}
		Widgets.Label(new Rect(w - 86f, 0f, 86f, 24f), "CF_LevelRest".Translate());
		GUI.color = Color.white;
		for (int i = 0; i < list.Count; i++)
		{
			WorkTypeDef wt = list[i];
			Rect row = new Rect(0f, 26f + i * rowH, w, rowH);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			Widgets.Label(new Rect(4f, row.y + 4f, labelW - 6f, 24f), wt.labelShort.CapitalizeFirst());
			// 2.18: the planner never applies a level plan to emergency work
			// (firefighting, patients, doctors …) – it is not offered here
			if (WorkPolicies.Get(wt).emergency)
			{
				GUI.color = Color.gray;
				Widgets.Label(new Rect(labelW + 2f, row.y + 4f, w - labelW - 2f, 24f), "CF_LevelEmergency".Translate());
				GUI.color = Color.white;
				TooltipHandler.TipRegion(row, "CF_LevelEmergency_Desc".Translate());
				continue;
			}
			int[] plan = settings.PlanFor(wt, levels) ?? new int[levels];
			bool any = false;
			bool changed = false;
			for (int lvl = 0; lvl < levels; lvl++)
			{
				Rect c = new Rect(labelW + lvl * colW + 2f, row.y + 2f, colW - 4f, 26f);
				int v = plan[lvl];
				int step = Event.current.shift ? 5 : 1;
				if (Widgets.ButtonText(new Rect(c.x, c.y, 22f, c.height), "−")) { v -= step; changed = true; }
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(new Rect(c.x + 22f, c.y, c.width - 44f, c.height), v <= 0 ? "–" : v.ToString());
				Text.Anchor = TextAnchor.UpperLeft;
				if (Widgets.ButtonText(new Rect(c.xMax - 22f, c.y, 22f, c.height), "+")) { v += step; changed = true; }
				plan[lvl] = Mathf.Clamp(v, 0, 99);
				any |= plan[lvl] > 0;
			}
			if (changed)
				settings.SetPlan(wt, any ? plan : null);
			GUI.color = new Color(0.75f, 0.75f, 0.75f);
			Widgets.Label(new Rect(w - 86f, row.y + 4f, 86f, 24f),
				!any ? (string)"CF_LevelAuto".Translate() : settings.backupWorkers ? (string)"CF_LevelBackup".Translate(levels) : (string)"CF_LevelOff".Translate());
			GUI.color = Color.white;
		}
		Widgets.EndScrollView();
	}

	// − value + ; 0 shows "auto"
	// 2.35: one switch for your own counts (level plan + min / max)
	private static float MyRulesToggle(Rect inRect, float y, ForemanSettings settings)
	{
		Rect r = new Rect(inRect.x, y, inRect.width, 28f);
		Widgets.CheckboxLabeled(r, "CF_UseMyRules".Translate(), ref settings.useMyRules);
		TooltipHandler.TipRegion(r, "CF_UseMyRules_Desc".Translate());
		y += 30f;
		Rect r2 = new Rect(inRect.x, y, inRect.width, 28f);
		Widgets.CheckboxLabeled(r2, "CF_AutoStaff".Translate(), ref settings.autoStaff);
		TooltipHandler.TipRegion(r2, "CF_AutoStaff_Desc".Translate());
		y += 30f;
		GUI.color = settings.useMyRules ? new Color(0.6f, 0.9f, 0.6f) : new Color(0.9f, 0.75f, 0.5f);
		string note = settings.useMyRules ? "CF_MyRulesOn".Translate() : "CF_MyRulesOff".Translate();
		float h = Text.CalcHeight(note, inRect.width);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, h), note);
		GUI.color = Color.white;
		return y + h + 6f;
	}

	private static void CountBox(Rect r, ForemanSettings settings, Dictionary<string, int> map, WorkTypeDef w)
	{
		map.TryGetValue(w.defName, out int v);
		int old = v;
		int step = Event.current.shift ? 5 : 1;
		if (Widgets.ButtonText(new Rect(r.x, r.y, 24f, r.height), "−")) v -= step;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(new Rect(r.x + 24f, r.y, r.width - 48f, r.height), v <= 0 ? (string)"CF_CountAuto".Translate() : v.ToString());
		Text.Anchor = TextAnchor.UpperLeft;
		if (Widgets.ButtonText(new Rect(r.xMax - 24f, r.y, 24f, r.height), "+")) v += step;
		v = Mathf.Clamp(v, 0, 99);
		if (v != old)
			settings.SetCount(map, w, v);
	}

	private static void Check(Listing_Standard list, string key, ref bool value, string field)
	{
		string key2 = key + "_Desc";
		MUI.Check(list, key.Translate(), (!key2.CanTranslate()) ? null : key2.Translate().ToString(), ref value, Settings, field);
	}

	private static void Check(Listing_Standard list, string key, ref bool value)
	{
		string key2 = key + "_Desc";
		list.CheckboxLabeled(key.Translate(), ref value, (!key2.CanTranslate()) ? null : key2.Translate().ToString());
	}
}
