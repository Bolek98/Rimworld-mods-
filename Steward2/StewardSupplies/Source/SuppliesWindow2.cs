using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// The "Supplies" tab: a list of jobs on the left, the selected job on the right.
public sealed class MainTabWindow_Supplies : MainTabWindow
{
	private enum Page { Overview, Hunting, Livestock, Forestry, Foraging, Mining, Production, Auto, Meals, Power, Household, Emergency }

	private Page page = Page.Overview;
	private Vector2 listScroll;
	private string bufTarget;
	private object bufOwner;
	private readonly Dictionary<SupplyJob, string> miningBufs = new Dictionary<SupplyJob, string>();
	private readonly Dictionary<string, string> miningDraft = new Dictionary<string, string>();

	private static readonly Color Dim = new Color(0.75f, 0.75f, 0.75f);
	private const float NavWidth = 220f;

	public override Vector2 RequestedTabSize => new Vector2(1010f, 640f);

	// 2.18: the tab window lives as long as the game runs (it is kept on its
	// MainButtonDef) – its edit buffers held targets, and through their areas
	// whole maps, of a game that was left. Emptied whenever the tab opens or
	// closes and when a game is loaded.
	public void ResetBuffers()
	{
		miningBufs.Clear();
		miningDraft.Clear();
		prodBufs.Clear();
		bufOwner = null;
		bufTarget = null;
		listScroll = Vector2.zero;
	}

	public override void PreOpen()
	{
		base.PreOpen();
		ResetBuffers();
	}

	public override void PostClose()
	{
		base.PostClose();
		ResetBuffers();
	}

	public static void ResetOpenWindow()
	{
		MainButtonDef def = DefDatabase<MainButtonDef>.GetNamedSilentFail("CF_Supplies");
		if (def?.TabWindow is MainTabWindow_Supplies w)
			w.ResetBuffers();
	}

	public override void DoWindowContents(Rect inRect)
	{
		SupplyManager sm = SupplyManager.For(Find.CurrentMap);
		if (sm == null)
			return;
		Rect nav = new Rect(inRect.x, inRect.y, NavWidth, inRect.height);
		Rect body = new Rect(nav.xMax + 14f, inRect.y, inRect.width - NavWidth - 14f, inRect.height);
		DrawNav(nav, sm);
		Widgets.DrawLineVertical(nav.xMax + 6f, inRect.y, inRect.height);
		switch (page)
		{
			case Page.Overview: DrawOverview(body, sm); break;
			case Page.Hunting: DrawHunting(body, sm); break;
			case Page.Livestock: DrawLivestock(body, sm); break;
			case Page.Forestry: DrawForestry(body, sm); break;
			case Page.Foraging: DrawForaging(body, sm); break;
			case Page.Mining: DrawMining(body, sm); break;
			case Page.Emergency: DrawEmergency(body, sm); break;
			case Page.Production: DrawProduction(body, sm); break;
			case Page.Meals: DrawMeals(body, sm); break;
			case Page.Auto: DrawAuto(body, sm); break;
			case Page.Power: DrawPower(body, sm); break;
			case Page.Household: DrawHousehold(body, sm); break;
		}
		Text.Anchor = TextAnchor.UpperLeft;
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
	}

	// ---- navigation ----------------------------------------------------------
	private void DrawNav(Rect r, SupplyManager sm)
	{
		float y = r.y;
		NavButton(ref y, r, Page.Overview, "CF_J_Overview".Translate(), ThingDefOf.MealSimple, null);
		NavButton(ref y, r, Page.Hunting, "CF_J_Hunting".Translate(), MeatIcon(), sm.hunting);
		NavButton(ref y, r, Page.Livestock, "CF_J_Livestock".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("Cow") ?? MeatIcon(), null, sm.livestock.Count(l => l.enabled));
		NavButton(ref y, r, Page.Forestry, "CF_J_Forestry".Translate(), ThingDefOf.WoodLog, sm.forestry);
		NavButton(ref y, r, Page.Foraging, "CF_J_Foraging".Translate(), BerryIcon(), sm.foraging);
		NavButton(ref y, r, Page.Mining, "CF_J_Mining".Translate(), ThingDefOf.Steel, null, sm.mining.Count(m => m.enabled));
		NavButton(ref y, r, Page.Production, "CF_J_Production".Translate(), ThingDefOf.MealSimple, null, sm.production.Count(p => p.enabled));
		// 2.16: self-supply
		NavButton(ref y, r, Page.Auto, "CF_A_Page".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt") ?? ThingDefOf.MealSimple, null, -1, false,
			!sm.autoOn ? "CF_J_Off".Translate().ToString() : "CF_A_Short".Translate(sm.autoBills.Count).ToString());
		// 2.13.1: own page for the ready-meal target (was hidden at the top of Production)
		string mealSub = !sm.mealTargetOn ? "CF_J_Off".Translate().ToString()
			: "CF_M_Short".Translate(sm.lastMealDays < 0f ? "…" : sm.lastMealDays >= 99f ? "–" : sm.lastMealDays.ToString("0.0"), sm.mealTargetDays.ToString("0.#")).ToString();
		NavButton(ref y, r, Page.Meals, "CF_M_Page".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("MealFine") ?? ThingDefOf.MealSimple, null, -1, false, mealSub);
		NavButton(ref y, r, Page.Power, "CF_J_Power".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("Battery"), null, -1, false, sm.powerOn ? "CF_J_On".Translate().ToString() : "CF_J_Off".Translate().ToString());
		NavButton(ref y, r, Page.Household, "CF_J_Household".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("Shelf") ?? ThingDefOf.WoodLog, null, -1, false, sm.cleanPriority || sm.tidyHome ? "CF_J_On".Translate().ToString() : "CF_J_Off".Translate().ToString());
		NavButton(ref y, r, Page.Emergency, "CF_J_Emergency".Translate(), ThingDefOf.MealSimple, null, -1, sm.emergencyActive);

		// bottom: on/off and check now
		float by = r.yMax - 132f;
		Rect cancel = new Rect(r.x, by - 2f, r.width, 30f);
		if (Widgets.ButtonText(cancel, "CF_X_Cancel".Translate()))
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("CF_X_CancelConfirm".Translate(), () => sm.CancelAllMarks()));
		TooltipHandler.TipRegion(cancel, "CF_X_CancelTip".Translate());
		by += 34f;
		Rect on = new Rect(r.x, by, r.width, 28f);
		Widgets.CheckboxLabeled(on, "CF_S_On".Translate(), ref sm.enabled);
		TooltipHandler.TipRegion(on, "CF_S_OnTip".Translate());
		GUI.color = Dim;
		Widgets.Label(new Rect(r.x, by + 30f, r.width, 24f), sm.enabled ? "CF_J_NextCheck".Translate(sm.HoursToNextCheck) : "CF_J_Off".Translate());
		GUI.color = Color.white;
		if (Widgets.ButtonText(new Rect(r.x, by + 58f, r.width, 34f), "CF_S_RunNow".Translate()))
			sm.CheckAll(manual: true);
	}

	private void NavButton(ref float y, Rect r, Page p, string label, ThingDef icon, SupplyJob job, int count = -1, bool alert = false, string subText = null)
	{
		Rect row = new Rect(r.x, y, r.width, 36f);
		if (page == p)
			Widgets.DrawHighlightSelected(row);
		else
			Widgets.DrawHighlightIfMouseover(row);
		if (icon != null)
			Widgets.DefIcon(new Rect(row.x + 6f, row.y + 4f, 28f, 28f), icon);
		Text.Font = GameFont.Small;
		Widgets.Label(new Rect(row.x + 46f, row.y + 1f, row.width - 50f, 22f), label);
		GUI.color = Dim;
		string sub = "";
		if (job != null)
			sub = job.enabled ? "CF_J_Short".Translate(job.lastStock, job.target).ToString() : "CF_J_Off".Translate().ToString();
		else if (count >= 0)
			sub = "CF_J_Active".Translate(count);
		else if (p == Page.Emergency)
			sub = alert ? "CF_J_EmergencyNow".Translate().ToString() : "CF_J_EmergencyIdle".Translate().ToString();
		if (subText != null)
			sub = subText;
		if (alert)
			GUI.color = new Color(1f, 0.5f, 0.4f);
		Text.Font = GameFont.Tiny;
		Widgets.Label(new Rect(row.x + 46f, row.y + 19f, row.width - 50f, 18f), sub);
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
		if (Widgets.ButtonInvisible(row))
		{
			if (page != p)
				listScroll = Vector2.zero;
			page = p;
		}
		y += 38f;
	}

	private static ThingDef MeatIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("Meat_Cow") ?? ThingDefOf.Meat_Human;
	private static ThingDef BerryIcon() => DefDatabase<ThingDef>.GetNamedSilentFail("RawBerries") ?? ThingDefOf.MealSimple;

	// ---- shared parts ---------------------------------------------------------
	private void Title(ref float y, Rect r, string title, string desc)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, y, r.width, 34f), title);
		Text.Font = GameFont.Small;
		y += 36f;
		GUI.color = Dim;
		float h = Text.CalcHeight(desc, r.width);
		Widgets.Label(new Rect(r.x, y, r.width, h), desc);
		GUI.color = Color.white;
		y += h + 8f;
	}

	// enabled checkbox, target with buttons, progress bar, area
	private void JobHeader(ref float y, Rect r, SupplyManager sm, SupplyJob job, string unit)
	{
		Rect on = new Rect(r.x, y, 200f, 28f);
		Widgets.CheckboxLabeled(on, "CF_J_Enabled".Translate(), ref job.enabled);
		DrawTarget(new Rect(r.x + 220f, y, 330f, 28f), job, ref bufTarget, job == bufOwner, () => bufOwner = job);
		DrawArea(new Rect(r.xMax - 160f, y, 160f, 28f), sm, job);
		y += 36f;
		DrawProgress(new Rect(r.x, y, r.width, 24f), job, unit);
		y += 30f;
		if (job.enabled && job.noSources)
		{
			GUI.color = new Color(1f, 0.6f, 0.45f);
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_A_NoSourcesHere".Translate());
			GUI.color = Color.white;
			y += 24f;
		}
		y += 4f;
	}

	private static int SmallNumber(Rect r, string label, int value, int min, int max)
	{
		Widgets.Label(new Rect(r.x, r.y + 3f, r.width - 92f, 24f), label);
		float x = r.xMax - 90f;
		// Shift = 10, Ctrl = 100
		int step = Event.current.control ? 100 : Event.current.shift ? 10 : 1;
		if (Widgets.ButtonText(new Rect(x, r.y, 26f, 26f), "-")) value -= step;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(new Rect(x + 26f, r.y, 38f, 26f), value.ToString());
		Text.Anchor = TextAnchor.UpperLeft;
		if (Widgets.ButtonText(new Rect(x + 64f, r.y, 26f, 26f), "+")) value += step;
		TooltipHandler.TipRegion(new Rect(x, r.y, 90f, 26f), "CF_J_StepTip".Translate());
		return Mathf.Clamp(value, min, max);
	}

	private static void DrawGraph(Rect r, SupplyJob job)
	{
		Widgets.DrawBoxSolid(r, new Color(0.1f, 0.1f, 0.1f));
		var h = job.history;
		if (h.Count < 2)
		{
			GUI.color = Dim;
			Text.Font = GameFont.Tiny;
			Widgets.Label(r.ContractedBy(2f), "CF_J_GraphWait".Translate());
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			return;
		}
		float max = Mathf.Max(job.target, h.Max(), 1);
		Vector2 prev = Vector2.zero;
		for (int i = 0; i < h.Count; i++)
		{
			Vector2 pt = new Vector2(r.x + r.width * i / (h.Count - 1), r.yMax - r.height * h[i] / max);
			if (i > 0)
				Widgets.DrawLine(prev, pt, new Color(0.45f, 0.8f, 0.45f), 2f);
			prev = pt;
		}
		float ty = r.yMax - r.height * job.target / max;
		Widgets.DrawLine(new Vector2(r.x, ty), new Vector2(r.xMax, ty), new Color(0.9f, 0.8f, 0.4f, 0.6f), 1f);
		TooltipHandler.TipRegion(r, "CF_J_GraphTip".Translate());
	}

	private static void DrawTarget(Rect r, SupplyJob job, ref string buf, bool owns, System.Action takeBuffer)
	{
		Widgets.Label(new Rect(r.x, r.y + 3f, 60f, 24f), "CF_J_Target".Translate());
		float x = r.x + 62f;
		if (Widgets.ButtonText(new Rect(x, r.y, 46f, 28f), "-100")) { job.target = Mathf.Max(0, job.target - 100); buf = null; }
		if (Widgets.ButtonText(new Rect(x + 48f, r.y, 38f, 28f), "-10")) { job.target = Mathf.Max(0, job.target - 10); buf = null; }
		if (!owns) { buf = null; takeBuffer(); }
		int v = job.target;
		Widgets.TextFieldNumeric(new Rect(x + 90f, r.y, 70f, 28f), ref v, ref buf, 0, 100000);
		job.target = v;
		if (Widgets.ButtonText(new Rect(x + 164f, r.y, 38f, 28f), "+10")) { job.target += 10; buf = null; }
		if (Widgets.ButtonText(new Rect(x + 204f, r.y, 46f, 28f), "+100")) { job.target += 100; buf = null; }
	}

	private static void DrawArea(Rect r, SupplyManager sm, SupplyJob job)
	{
		if (Widgets.ButtonText(r, job.area == null ? (string)"CF_S_AreaAny".Translate() : job.area.Label))
		{
			var menu = new List<FloatMenuOption> { new FloatMenuOption("CF_S_AreaAny".Translate(), () => job.area = null) };
			foreach (Area a in sm.map.areaManager.AllAreas)
			{
				Area local = a;
				if (a.AssignableAsAllowed() || a is Area_Home)
					menu.Add(new FloatMenuOption(a.Label, () => job.area = local));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		TooltipHandler.TipRegion(r, "CF_S_AreaTip".Translate());
	}

	private static void DrawProgress(Rect r, SupplyJob job, string unit)
	{
		float max = Mathf.Max(1, job.target);
		Widgets.DrawBoxSolid(r, new Color(0.12f, 0.12f, 0.12f));
		float a = Mathf.Clamp01(job.lastStock / max);
		float b = Mathf.Clamp01((job.lastStock + job.lastPending) / max);
		Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width * b, r.height), new Color(0.75f, 0.65f, 0.25f, 0.6f));
		Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width * a, r.height), new Color(0.35f, 0.65f, 0.35f));
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(r, "CF_S_Status".Translate(job.lastStock, job.lastPending, job.target) + " " + unit);
		Text.Anchor = TextAnchor.UpperLeft;
		TooltipHandler.TipRegion(r, "CF_J_ProgressTip".Translate());
	}

	// list of defs with checkboxes
	private void DrawChoiceList(Rect r, SupplyManager sm, SupplyJob job, List<ThingDef> defs, System.Func<ThingDef, bool> byDefault, System.Func<ThingDef, string> info)
	{
		float y = r.y;
		if (Widgets.ButtonText(new Rect(r.x, y, 120f, 26f), "CF_J_All".Translate()))
			foreach (ThingDef d in defs) job.SetChosen(d, true, byDefault(d));
		if (Widgets.ButtonText(new Rect(r.x + 126f, y, 120f, 26f), "CF_J_None".Translate()))
			foreach (ThingDef d in defs) job.SetChosen(d, false, byDefault(d));
		if (Widgets.ButtonText(new Rect(r.x + 252f, y, 140f, 26f), "CF_J_Default".Translate()))
			job.choices.Clear();
		y += 32f;
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 32f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, defs.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < defs.Count; i++)
		{
			ThingDef d = defs[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool def = byDefault(d);
			bool on = job.IsChosen(d, def);
			bool was = on;
			Widgets.DefIcon(new Rect(row.x + 4f, row.y + 2f, 26f, 26f), d);
			Widgets.Label(new Rect(row.x + 36f, row.y + 4f, 260f, 24f), d.LabelCap);
			GUI.color = Dim;
			Widgets.Label(new Rect(row.x + 300f, row.y + 4f, row.width - 340f, 24f), info(d));
			GUI.color = Color.white;
			Widgets.Checkbox(new Vector2(row.xMax - 30f, row.y + 3f), ref on);
			if (on != was)
				job.SetChosen(d, on, def);
			if (Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 36f, row.height)))
				job.SetChosen(d, !on, def);
		}
		Widgets.EndScrollView();
	}

	// ---- pages -----------------------------------------------------------------
	private void DrawOverview(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Overview".Translate(), "CF_S_Desc".Translate());
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_Interval".Translate(sm.intervalHours));
		y += 24f;
		sm.intervalHours = Mathf.RoundToInt(Widgets.HorizontalSlider(new Rect(r.x, y, r.width * 0.6f, 22f), sm.intervalHours, 1f, 24f, true));
		// 2.18: work priorities – re-assign now and when they were planned last
		ForemanMapComponent wc = ForemanMapComponent.For(sm.map);
		if (wc != null)
		{
			Rect re = new Rect(r.xMax - 220f, y - 26f, 220f, 30f);
			if (Widgets.ButtonText(re, "CF_W_Reassign".Translate()))
				wc.PlanNow(manual: true);
			TooltipHandler.TipRegion(re, "CF_W_ReassignHere_Desc".Translate());
		}
		y += 30f;
		if (wc != null)
		{
			string plan = wc.lastPlanTick < 0
				? (string)"CF_W_LastPlanNever".Translate()
				: (string)"CF_W_LastPlan".Translate((Find.TickManager.TicksGame - wc.lastPlanTick).ToStringTicksToPeriod(), wc.lastPlanChanged, Styles.Label(wc.style));
			if (!wc.auto)
				plan += " " + "CF_W_StewardOff".Translate();
			if (wc.CurrentCrisis != Crisis.None)
				plan += " " + "CF_TipCrisis".Translate(CrisisText.Label(wc.CurrentCrisis));
			Rect pl = new Rect(r.x, y, r.width, 24f);
			GUI.color = Dim;
			Widgets.Label(pl, plan.Truncate(r.width));
			GUI.color = Color.white;
			string planTip = "CF_W_LastPlan_Desc".Translate();
			foreach (string line in StewardApi.WorkTabLines(wc))
				planTip += "\n\n" + line;
			TooltipHandler.TipRegion(pl, planTip);
			y += 26f;
		}
		y += 6f;
		foreach (SupplyJob job in sm.AllJobs())
		{
			if (job.kind == JobKind.Mining && !job.enabled)
				continue;
			Widgets.Label(new Rect(r.x, y, 200f, 24f), SupplyManager.JobLabel(job));
			if (job.enabled)
			{
				DrawProgress(new Rect(r.x + 210f, y, r.width - 210f - 130f, 22f), job, "");
				DrawGraph(new Rect(r.xMax - 120f, y, 120f, 22f), job);
			}
			else
			{
				GUI.color = Dim;
				Widgets.Label(new Rect(r.x + 210f, y, 200f, 24f), "CF_J_Off".Translate());
				GUI.color = Color.white;
			}
			y += 30f;
		}
		y += 10f;
		GUI.color = sm.emergencyActive ? new Color(1f, 0.5f, 0.4f) : Dim;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_FoodDays".Translate(sm.lastFoodDays < 0 ? "?" : sm.lastFoodDays.ToString("0.0")));
		GUI.color = Color.white;
		y += 30f;
		// other colonies of the player
		foreach (Map other in SupplyManager.OtherHomeMaps(sm.map))
		{
			SupplyManager o = SupplyManager.For(other);
			if (o == null)
				continue;
			var bits = o.AllJobs().Where(j => j.enabled).Select(j => SupplyManager.JobLabel(j) + " " + j.lastStock + "/" + j.target).ToList();
			string line = "CF_X_OtherMap".Translate(other.Parent?.LabelCap ?? "?", bits.Count == 0 ? (string)"CF_J_Off".Translate() : string.Join(", ", bits));
			GUI.color = Dim;
			float h = Text.CalcHeight(line, r.width);
			Widgets.Label(new Rect(r.x, y, r.width, h), line);
			GUI.color = Color.white;
			y += h + 4f;
		}
	}

	// ---- production ------------------------------------------------------------------
	private readonly Dictionary<ProductionTarget, string> prodBufs = new Dictionary<ProductionTarget, string>();

	// 2.16: self-supply page
	private void DrawAuto(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_A_Page".Translate(), "CF_A_Desc".Translate());
		Rect on = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(on, "CF_A_On".Translate(), ref sm.autoOn);
		y += 34f;
		if (sm.autoOn)
		{
			void Box(string key, ref bool v, float col)
			{
				Rect b = new Rect(r.x + col, y, 300f, 26f);
				Widgets.CheckboxLabeled(b, key.Translate(), ref v);
				TooltipHandler.TipRegion(b, (key + "Tip").Translate());
			}
			Box("CF_A_Meals", ref sm.autoMeals, 0f); Box("CF_A_Clothes", ref sm.autoClothes, 320f); y += 30f;
			Box("CF_A_Medicine", ref sm.autoMedicine, 0f); Box("CF_A_Armor", ref sm.autoArmor, 320f); y += 30f;
			Box("CF_A_Materials", ref sm.autoMaterials, 0f); Box("CF_A_Weapons", ref sm.autoWeapons, 320f); y += 36f;
			Rect race = new Rect(r.x, y, 380f, 26f);
			Widgets.CheckboxLabeled(race, "CF_A_Race".Translate(), ref sm.autoRaceRules);
			TooltipHandler.TipRegion(race, "CF_A_RaceTip".Translate());
			y += 32f;
			Widgets.Label(new Rect(r.x, y, 360f, 24f), "CF_A_Worn".Translate(Mathf.RoundToInt(sm.autoWorn * 100f)));
			sm.autoWorn = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x + 370f, y + 6f, 220f, 18f), sm.autoWorn, 0.2f, 0.9f) * 20f) / 20f;
			y += 34f;
		}
		Widgets.Label(new Rect(r.x, y, r.width - 230f, 24f), "CF_A_Bills".Translate(sm.autoBills.Count));
		if (sm.autoBills.Count > 0 && Widgets.ButtonText(new Rect(r.xMax - 220f, y - 2f, 220f, 28f), "CF_A_RemoveAll".Translate()))
			sm.RemoveAuto(null);
		y += 30f;
		if (!sm.lastAutoSummary.NullOrEmpty())
		{
			GUI.color = Dim;
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_A_Last".Translate(sm.lastAutoSummary).Truncate(r.width));
			GUI.color = Color.white;
			y += 26f;
		}
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 30f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, sm.autoBills.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < sm.autoBills.Count; i++)
		{
			AutoBill a = sm.autoBills[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			if (a.product != null)
				Widgets.DefIcon(new Rect(row.x + 4f, row.y + 2f, 24f, 24f), a.product);
			Widgets.Label(new Rect(row.x + 34f, row.y + 4f, row.width * 0.4f, 24f), a.product?.LabelCap ?? "?");
			Widgets.Label(new Rect(row.x + 34f + row.width * 0.4f, row.y + 4f, row.width * 0.25f, 24f), ("CF_A_Kind_" + a.kind).Translate());
			Bill_Production b = sm.AutoBillOf(a);
			string st = b == null ? "-" : b.repeatMode == BillRepeatModeDefOf.RepeatCount ? "CF_A_Left".Translate(b.repeatCount).ToString()
				: "CF_A_Until".Translate(b.targetCount).ToString();
			string bench = b?.billStack?.billGiver is Thing t ? t.LabelCap : "";
			Widgets.Label(new Rect(row.xMax - row.width * 0.33f, row.y + 4f, row.width * 0.33f, 24f), (st + "  " + bench).Truncate(row.width * 0.33f));
		}
		Widgets.EndScrollView();
	}

	private int mealFrame;
	private float mealNow;

	private void DrawMeals(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_M_Page".Translate(), "CF_M_TargetTip".Translate());
		Rect mt = new Rect(r.x, y, 300f, 28f);
		Widgets.CheckboxLabeled(mt, "CF_M_Target".Translate(), ref sm.mealTargetOn);
		float x = r.x + 320f;
		if (Widgets.ButtonText(new Rect(x, y, 28f, 28f), "-"))
			sm.mealTargetDays = Mathf.Max(0.5f, sm.mealTargetDays - (Event.current.shift ? 2f : 0.5f));
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(new Rect(x + 28f, y, 90f, 28f), "CF_M_Days".Translate(sm.mealTargetDays.ToString("0.#")));
		Text.Anchor = TextAnchor.UpperLeft;
		if (Widgets.ButtonText(new Rect(x + 118f, y, 28f, 28f), "+"))
			sm.mealTargetDays = Mathf.Min(60f, sm.mealTargetDays + (Event.current.shift ? 2f : 0.5f));
		y += 38f;
		// current state – counted live here, so it shows even before the first check
		if (Time.frameCount - mealFrame > 30 || mealFrame == 0)
		{
			mealFrame = Time.frameCount;
			mealNow = sm.MealDays();
		}
		float days = mealNow;
		string now = days >= 99f ? "–" : days.ToString("0.0");
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_M_Now".Translate(now).CapitalizeFirst());
		y += 26f;
		if (sm.mealTargetOn)
		{
			bool paused = sm.mealPaused.Count > 0;
			GUI.color = paused ? new Color(1f, 0.85f, 0.4f) : new Color(0.7f, 1f, 0.7f);
			Widgets.Label(new Rect(r.x, y, r.width, 24f), paused ? "CF_M_Paused".Translate(sm.mealPaused.Count).ToString().CapitalizeFirst() : "CF_M_Cooking".Translate().ToString().CapitalizeFirst());
			GUI.color = Color.white;
			y += 26f;
		}
		y += 8f;
		// every bill that makes meals
		var bills = sm.MealBillList();
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_M_Bills".Translate(bills.Count));
		y += 28f;
		if (bills.Count == 0)
		{
			GUI.color = Dim;
			Widgets.Label(new Rect(r.x, y, r.width, 48f), "CF_M_NoBills".Translate());
			GUI.color = Color.white;
			return;
		}
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 30f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, bills.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < bills.Count; i++)
		{
			var (bill, bench) = bills[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			ThingDef prod = bill.recipe.products[0].thingDef;
			Widgets.DefIcon(new Rect(row.x + 4f, row.y + 2f, 24f, 24f), prod);
			Widgets.Label(new Rect(row.x + 34f, row.y + 4f, row.width * 0.45f, 24f), bill.LabelCap);
			Widgets.Label(new Rect(row.x + 34f + row.width * 0.45f, row.y + 4f, row.width * 0.3f, 24f), bench.LabelCap);
			string st = !bill.suspended ? "CF_M_BillOn".Translate().ToString()
				: sm.IsMealPaused(bill) ? "CF_M_BillSteward".Translate().ToString() : "CF_M_BillYou".Translate().ToString();
			GUI.color = !bill.suspended ? new Color(0.7f, 1f, 0.7f) : sm.IsMealPaused(bill) ? new Color(1f, 0.85f, 0.4f) : Dim;
			Widgets.Label(new Rect(row.xMax - row.width * 0.22f, row.y + 4f, row.width * 0.22f, 24f), st);
			GUI.color = Color.white;
		}
		Widgets.EndScrollView();
	}

	private void DrawProduction(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Production".Translate(), "CF_J_ProductionDesc".Translate());
		if (Widgets.ButtonText(new Rect(r.x, y, 200f, 30f), "CF_X_AddProduct".Translate()))
			Find.WindowStack.Add(new Dialog_StewardProductPicker(sm));
		MainButtonDef mint = SupplyManager.MintBillTab;
		if (mint != null)
		{
			Rect mr = new Rect(r.x + 210f, y, 220f, 30f);
			if (Widgets.ButtonText(mr, "CF_X_OpenMint".Translate()))
				Find.MainTabsRoot.SetCurrentTab(mint);
			TooltipHandler.TipRegion(mr, "CF_X_OpenMintTip".Translate());
		}
		var adoptable = sm.AdoptableBills();
		Rect ar = new Rect(r.x + 440f, y, 220f, 30f);
		if (adoptable.Count > 0 && Widgets.ButtonText(ar, "CF_X_Adopt".Translate(adoptable.Count)))
		{
			var menu = new List<FloatMenuOption>();
			foreach (var a in adoptable)
			{
				var local = a;
				menu.Add(new FloatMenuOption(local.bill.LabelCap + "  →  " + local.bill.targetCount + "  (" + local.bench.LabelCap + ")",
					() => sm.Adopt(local.bill), local.bill.recipe.products[0].thingDef, (ThingStyleDef)null));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		if (adoptable.Count > 0)
			TooltipHandler.TipRegion(ar, "CF_X_AdoptTip".Translate());
		y += 38f;
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 40f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, sm.production.Count * rowH);
		ProductionTarget remove = null;
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < sm.production.Count; i++)
		{
			ProductionTarget t = sm.production[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 6f), ref t.enabled);
			Widgets.DefIcon(new Rect(row.x + 34f, row.y + 3f, 30f, 30f), t.product);
			Widgets.Label(new Rect(row.x + 70f, row.y + 7f, 200f, 24f), t.product.LabelCap);
			int have = sm.ProductCount(t.product);
			GUI.color = have >= t.target ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.75f, 0.45f);
			Widgets.Label(new Rect(row.x + 270f, row.y + 7f, 110f, 24f), have + " / " + t.target);
			GUI.color = Color.white;
			string buf;
			prodBufs.TryGetValue(t, out buf);
			int v = t.target;
			if (Widgets.ButtonText(new Rect(row.x + 380f, row.y + 4f, 38f, 28f), "-10")) { v = Mathf.Max(0, v - 10); buf = null; }
			Widgets.TextFieldNumeric(new Rect(row.x + 420f, row.y + 4f, 60f, 28f), ref v, ref buf, 0, 100000);
			if (Widgets.ButtonText(new Rect(row.x + 482f, row.y + 4f, 38f, 28f), "+10")) { v += 10; buf = null; }
			t.target = v;
			prodBufs[t] = buf;
			Building bench;
			Bill_Production bill = sm.FindBill(t, out bench);
			GUI.color = Dim;
			Widgets.Label(new Rect(row.x + 530f, row.y + 7f, row.width - 570f, 24f),
				bill == null ? (string)"CF_X_NoBillYet".Translate() : "CF_X_BillAt".Translate(bench.LabelCap).ToString());
			GUI.color = Color.white;
			Rect del = new Rect(row.xMax - 28f, row.y + 6f, 24f, 24f);
			if (Widgets.ButtonImage(del, TexButton.Delete))
				remove = t;
			TooltipHandler.TipRegion(del, "CF_X_RemoveProduct".Translate());
		}
		Widgets.EndScrollView();
		if (remove != null)
			sm.RemoveProduction(remove);
	}

	// ---- power -------------------------------------------------------------------------
	private void DrawPower(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Power".Translate(), "CF_J_PowerDesc".Translate());
		Widgets.CheckboxLabeled(new Rect(r.x, y, 260f, 28f), "CF_J_Enabled".Translate(), ref sm.powerOn);
		y += 34f;
		sm.PowerSummary(out float gain, out float stored, out float max);
		GUI.color = gain >= 0f ? new Color(0.55f, 0.85f, 0.55f) : new Color(0.95f, 0.55f, 0.45f);
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_X_PowerNow".Translate(gain.ToString("0"), stored.ToString("0"), max.ToString("0"),
			max > 0f ? (stored / max).ToStringPercent() : "–"));
		GUI.color = Color.white;
		y += 30f;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_X_PowerLow".Translate(sm.powerLow.ToStringPercent()));
		y += 22f;
		sm.powerLow = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x, y, r.width * 0.5f, 20f), sm.powerLow, 0f, 0.9f, true) * 20f) / 20f;
		y += 28f;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_X_PowerHigh".Translate(sm.powerHigh.ToStringPercent()));
		y += 22f;
		sm.powerHigh = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x, y, r.width * 0.5f, 20f), sm.powerHigh, 0.1f, 1f, true) * 20f) / 20f;
		if (sm.powerHigh <= sm.powerLow) sm.powerHigh = Mathf.Min(1f, sm.powerLow + 0.1f);
		y += 32f;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_X_PowerDevices".Translate());
		y += 26f;
		List<ThingDef> devices = sm.SwitchableDevices();
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 30f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, devices.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < devices.Count; i++)
		{
			ThingDef d = devices[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool on = sm.powerDevices.Contains(d.defName);
			bool was = on;
			Widgets.DefIcon(new Rect(row.x + 4f, row.y + 2f, 24f, 24f), d);
			int count = sm.map.listerBuildings.allBuildingsColonist.Count(b => b.def == d);
			float watts = d.GetCompProperties<CompProperties_Power>()?.PowerConsumption ?? 0f;
			Widgets.Label(new Rect(row.x + 34f, row.y + 3f, 300f, 24f), d.LabelCap);
			GUI.color = Dim;
			Widgets.Label(new Rect(row.x + 340f, row.y + 3f, 300f, 24f), "CF_X_DeviceInfo".Translate(count, watts.ToString("0")));
			GUI.color = Color.white;
			Widgets.Checkbox(new Vector2(row.xMax - 30f, row.y + 2f), ref on);
			if (on != was)
			{
				if (on) sm.powerDevices.Add(d.defName);
				else sm.powerDevices.Remove(d.defName);
			}
		}
		Widgets.EndScrollView();
	}

	// ---- household: tidy home + cleaning -----------------------------------------------
	private void DrawHousehold(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Household".Translate(), "CF_J_HouseholdDesc".Translate());
		Rect tidy = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(tidy, "CF_T_Tidy".Translate(), ref sm.tidyHome);
		TooltipHandler.TipRegion(tidy, "CF_T_TidyTip".Translate());
		Rect dump = new Rect(r.x + 400f, y, 260f, 26f);
		if (Widgets.ButtonText(dump, sm.dumpArea == null ? (string)"CF_T_DumpAuto".Translate() : "CF_T_Dump".Translate(sm.dumpArea.Label).ToString()))
		{
			var menu = new List<FloatMenuOption> { new FloatMenuOption("CF_T_DumpAuto".Translate(), () => sm.dumpArea = null) };
			foreach (Area a in sm.map.areaManager.AllAreas)
			{
				Area local = a;
				if (a.AssignableAsAllowed())
					menu.Add(new FloatMenuOption(a.Label, () => sm.dumpArea = local));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		TooltipHandler.TipRegion(dump, "CF_T_DumpTip".Translate());
		y += 32f;
		Rect prods = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(prods, "CF_H_Products".Translate(), ref sm.haulProducts);
		TooltipHandler.TipRegion(prods, "CF_H_ProductsTip".Translate());
		y += 30f;
		Rect chunks = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(chunks, "CF_H_Chunks".Translate(), ref sm.tidyChunks);
		TooltipHandler.TipRegion(chunks, "CF_H_ChunksTip".Translate());
		y += 30f;
		// 2.23.0: loose things in the home area
		Rect loose = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(loose, "CF_H_Items".Translate(), ref sm.tidyItems);
		TooltipHandler.TipRegion(loose, "CF_H_ItemsTip".Translate());
		y += 30f;
		Rect pockets = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(pockets, "CF_H_Pockets".Translate(), ref sm.unloadPockets);
		TooltipHandler.TipRegion(pockets, "CF_H_PocketsTip".Translate());
		y += 30f;
		// 2.14.4: blighted plants are marked for cutting
		Rect blight = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(blight, "CF_H_Blight".Translate(), ref sm.cutBlight);
		TooltipHandler.TipRegion(blight, "CF_H_BlightTip".Translate());
		y += 30f;
		sm.tidyWorkers = SmallNumber(new Rect(r.x, y, 380f, 26f), "CF_H_Workers".Translate(), sm.tidyWorkers, 0, 50);
		GUI.color = Dim;
		Widgets.Label(new Rect(r.x + 400f, y + 3f, r.width - 400f, 24f), sm.ChosenWorkerNames(WorkTypeDefOf.Hauling, sm.tidyWorkers));
		GUI.color = Color.white;
		TooltipHandler.TipRegion(new Rect(r.x, y, 380f, 26f), "CF_H_WorkersTip".Translate());
		y += 40f;
		Rect clean = new Rect(r.x, y, 380f, 26f);
		Widgets.CheckboxLabeled(clean, "CF_C_Clean".Translate(), ref sm.cleanPriority);
		TooltipHandler.TipRegion(clean, "CF_C_CleanTip".Translate());
		GUI.color = Dim;
		Widgets.Label(new Rect(r.x + 400f, y + 3f, 300f, 24f), "CF_C_FilthNow".Translate(sm.FilthInPriorityRooms()));
		GUI.color = Color.white;
		y += 30f;
		sm.cleanWorkers = SmallNumber(new Rect(r.x, y, 380f, 26f), "CF_H_Workers".Translate(), sm.cleanWorkers, 0, 50);
		GUI.color = Dim;
		Widgets.Label(new Rect(r.x + 400f, y + 3f, r.width - 400f, 24f), sm.ChosenWorkerNames(WorkTypeDefOf.Cleaning, sm.cleanWorkers));
		GUI.color = Color.white;
		TooltipHandler.TipRegion(new Rect(r.x, y, 380f, 26f), "CF_H_WorkersTip".Translate());
		y += 34f;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_C_Rooms".Translate());
		y += 26f;
		List<RoomRoleDef> roles = SupplyManager.CleanableRoles();
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 28f;
		int cols = 2;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.CeilToInt(roles.Count / (float)cols) * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		float cw = view.width / cols;
		for (int i = 0; i < roles.Count; i++)
		{
			RoomRoleDef role = roles[i];
			Rect cell = new Rect((i % cols) * cw, (i / cols) * rowH, cw - 10f, rowH - 2f);
			bool on = sm.cleanRoles.Contains(role.defName);
			bool was = on;
			Widgets.CheckboxLabeled(cell, role.LabelCap, ref on);
			if (on != was)
			{
				if (on) sm.cleanRoles.Add(role.defName);
				else sm.cleanRoles.Remove(role.defName);
			}
		}
		Widgets.EndScrollView();
	}

	private void DrawHunting(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Hunting".Translate(), "CF_J_HuntingDesc".Translate());
		JobHeader(ref y, r, sm, sm.hunting, "CF_J_UnitMeat".Translate());
		Widgets.CheckboxLabeled(new Rect(r.x, y, 300f, 26f), "CF_S_HuntPregnant".Translate(), ref sm.hunting.huntPregnant);
		sm.hunting.keepPerSpecies = SmallNumber(new Rect(r.x + 330f, y, 300f, 26f), "CF_J_KeepSpecies".Translate(), sm.hunting.keepPerSpecies, 0, 50);
		y += 30f;
		if (sm.huntNoRanged)
		{
			GUI.color = new Color(1f, 0.6f, 0.45f);
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_A_NoHunter".Translate());
			GUI.color = Color.white;
			y += 26f;
		}
		DrawChoiceList(new Rect(r.x, y, r.width, r.yMax - y), sm, sm.hunting, sm.AnimalChoices(), SupplyManager.DefaultAnimal,
			d => "CF_J_AnimalInfo".Translate(sm.CountOnMap(d), SupplyManager.DangerousAnimal(d) ? (string)"CF_J_Dangerous".Translate() : ""));
	}

	private void DrawLivestock(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Livestock".Translate(), "CF_J_LivestockDesc".Translate());
		List<ThingDef> species = sm.LivestockSpecies();
		if (species.Count == 0)
		{
			GUI.color = Dim;
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_NoAnimals".Translate());
			GUI.color = Color.white;
			return;
		}
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 96f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, species.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < species.Count; i++)
		{
			ThingDef d = species[i];
			LivestockRule rule = sm.Rule(d, create: true);
			sm.CountHerd(d, out int f, out int m, out int yg);
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 8f), ref rule.enabled);
			Widgets.DefIcon(new Rect(row.x + 34f, row.y + 4f, 32f, 32f), d);
			Widgets.Label(new Rect(row.x + 72f, row.y + 8f, 200f, 24f), d.LabelCap);
			GUI.color = Dim;
			Widgets.Label(new Rect(row.x + 72f, row.y + 32f, 260f, 24f), "CF_J_HerdNow".Translate(f, m, yg));
			GUI.color = Color.white;
			float cx = row.x + 340f;
			rule.females = SmallNumber(new Rect(cx, row.y + 4f, 200f, 26f), "CF_J_KeepFemales".Translate(), rule.females, 0, 500);
			rule.males = SmallNumber(new Rect(cx, row.y + 32f, 200f, 26f), "CF_J_KeepMales".Translate(), rule.males, 0, 500);
			rule.young = SmallNumber(new Rect(cx, row.y + 60f, 200f, 26f), "CF_J_KeepYoung".Translate(), rule.young, 0, 500);
			Rect kp = new Rect(row.xMax - 200f, row.y + 4f, 200f, 26f);
			Widgets.CheckboxLabeled(kp, "CF_J_KeepPregnant".Translate(), ref rule.keepPregnant);
			// 1.0.3: release surplus to the wild instead of slaughtering it
			Rect rel = new Rect(row.xMax - 200f, row.y + 64f, 200f, 26f);
			Widgets.CheckboxLabeled(rel, "CF_J_ReleaseSurplus".Translate(), ref rule.release);
			TooltipHandler.TipRegion(rel, "CF_J_ReleaseSurplusTip".Translate());
			Rect ar = new Rect(row.xMax - 200f, row.y + 34f, 200f, 26f);
			if (Widgets.ButtonText(ar, rule.area == null ? (string)"CF_J_AnimalAreaNone".Translate() : rule.area.Label))
			{
				var menu = new List<FloatMenuOption> { new FloatMenuOption("CF_J_AnimalAreaNone".Translate(), () => rule.area = null) };
				foreach (Area a in sm.map.areaManager.AllAreas)
				{
					Area local = a;
					if (a.AssignableAsAllowed())
						menu.Add(new FloatMenuOption(a.Label, () => rule.area = local));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			TooltipHandler.TipRegion(ar, "CF_J_AnimalAreaTip".Translate());
		}
		Widgets.EndScrollView();
	}

	private void DrawForestry(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Forestry".Translate(), "CF_J_ForestryDesc".Translate());
		JobHeader(ref y, r, sm, sm.forestry, ThingDefOf.WoodLog.label);
		Rect oh = new Rect(r.x, y, 280f, 26f);
		Widgets.CheckboxLabeled(oh, "CF_J_OutsideHome".Translate(), ref sm.forestry.onlyOutsideHome);
		TooltipHandler.TipRegion(oh, "CF_J_OutsideHomeTip".Translate());
		sm.forestry.keepTrees = SmallNumber(new Rect(r.x + 300f, y, 290f, 26f), "CF_J_KeepTrees".Translate(), sm.forestry.keepTrees, 0, 5000);
		Rect ca = new Rect(r.xMax - 200f, y, 200f, 26f);
		if (Widgets.ButtonText(ca, sm.clearArea == null ? (string)"CF_J_ClearOff".Translate() : "CF_J_ClearOn".Translate(sm.clearArea.Label).ToString()))
		{
			var menu = new List<FloatMenuOption> { new FloatMenuOption("CF_J_ClearOff".Translate(), () => sm.SetClearArea(null)) };
			foreach (Area a in sm.map.areaManager.AllAreas)
			{
				Area local = a;
				if (!a.AssignableAsAllowed())
					continue;
				// 2.39.2: an area covering a big part of the map would cut the whole forest
				if (SupplyManager.TooBigToClear(a))
					menu.Add(new FloatMenuOption("CF_J_ClearTooBig".Translate(a.Label), null));
				else
					menu.Add(new FloatMenuOption(a.Label, () => sm.SetClearArea(local)));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		TooltipHandler.TipRegion(ca, "CF_J_ClearTip".Translate());
		y += 32f;
		DrawChoiceList(new Rect(r.x, y, r.width, r.yMax - y), sm, sm.forestry, sm.TreeChoices(), SupplyManager.DefaultTree,
			d => "CF_J_PlantInfo".Translate(sm.CountOnMap(d), d.plant.harvestedThingDef.label));
	}

	private void DrawForaging(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Foraging".Translate(), "CF_J_ForagingDesc".Translate());
		JobHeader(ref y, r, sm, sm.foraging, "");
		DrawChoiceList(new Rect(r.x, y, r.width, r.yMax - y), sm, sm.foraging, sm.ForageChoices(), SupplyManager.DefaultForage,
			d => "CF_J_PlantInfo".Translate(sm.CountOnMap(d), d.plant.harvestedThingDef.label));
	}

	private void DrawMining(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Mining".Translate(), "CF_J_MiningDesc".Translate());
		List<ThingDef> res = sm.MineableResources();
		foreach (SupplyJob m in sm.mining)
			if (!res.Contains(m.def))
				res.Add(m.def);
		if (res.Count == 0)
		{
			GUI.color = Dim;
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_NoVeins".Translate());
			GUI.color = Color.white;
			return;
		}
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 70f;
		Rect view = new Rect(0f, 0f, outRect.width - 16f, res.Count * rowH);
		Widgets.BeginScrollView(outRect, ref listScroll, view);
		for (int i = 0; i < res.Count; i++)
		{
			ThingDef d = res[i];
			SupplyJob job = sm.MiningJob(d, create: false);
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool on = job != null && job.enabled;
			bool was = on;
			Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 6f), ref on);
			if (on != was)
			{
				job = sm.MiningJob(d, create: true);
				job.enabled = on;
			}
			Widgets.DefIcon(new Rect(row.x + 34f, row.y + 2f, 30f, 30f), d);
			Widgets.Label(new Rect(row.x + 70f, row.y + 6f, 160f, 24f), d.LabelCap);
			int target = job?.target ?? (d == ThingDefOf.Steel ? 300 : 100);
			string buf = null;
			if (job != null)
				miningBufs.TryGetValue(job, out buf);
			else
				miningDraft.TryGetValue(d.defName, out buf);
			int edited = target;
			DrawTargetValue(new Rect(row.x + 240f, row.y + 2f, 330f, 28f), ref edited, ref buf);
			if (edited != target)
			{
				job = sm.MiningJob(d, create: true);
				job.target = edited;
			}
			if (job != null)
				miningBufs[job] = buf;
			else
				miningDraft[d.defName] = buf;
			ThingDef localDef = d;
			Rect ar = new Rect(row.xMax - 150f, row.y + 2f, 150f, 28f);
			string areaLabel = job?.area == null ? (string)"CF_S_AreaAny".Translate() : job.area.Label;
			if (Widgets.ButtonText(ar, areaLabel))
			{
				var menu = new List<FloatMenuOption>
				{
					new FloatMenuOption("CF_S_AreaAny".Translate(), () => sm.MiningJob(localDef, true).area = null)
				};
				foreach (Area a in sm.map.areaManager.AllAreas)
				{
					Area local = a;
					if (a.AssignableAsAllowed() || a is Area_Home)
						menu.Add(new FloatMenuOption(a.Label, () => sm.MiningJob(localDef, true).area = local));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			TooltipHandler.TipRegion(ar, "CF_S_AreaTip".Translate());
			if (job != null)
				DrawProgress(new Rect(row.x + 34f, row.y + 38f, row.width - 34f, 22f), job, "");
		}
		Widgets.EndScrollView();
	}

	private static void DrawTargetValue(Rect r, ref int value, ref string buf)
	{
		Widgets.Label(new Rect(r.x, r.y + 3f, 60f, 24f), "CF_J_Target".Translate());
		float x = r.x + 62f;
		if (Widgets.ButtonText(new Rect(x, r.y, 46f, 28f), "-100")) { value = Mathf.Max(0, value - 100); buf = null; }
		if (Widgets.ButtonText(new Rect(x + 48f, r.y, 38f, 28f), "-10")) { value = Mathf.Max(0, value - 10); buf = null; }
		Widgets.TextFieldNumeric(new Rect(x + 90f, r.y, 70f, 28f), ref value, ref buf, 0, 100000);
		if (Widgets.ButtonText(new Rect(x + 164f, r.y, 38f, 28f), "+10")) { value += 10; buf = null; }
		if (Widgets.ButtonText(new Rect(x + 204f, r.y, 46f, 28f), "+100")) { value += 100; buf = null; }
	}

	private void DrawEmergency(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_J_Emergency".Translate(), "CF_J_EmergencyDesc".Translate());
		Widgets.CheckboxLabeled(new Rect(r.x, y, 300f, 28f), "CF_J_Enabled".Translate(), ref sm.emergencyOn);
		y += 34f;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_EmergencyDays".Translate(sm.emergencyDays.ToString("0.0")));
		y += 24f;
		sm.emergencyDays = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x, y, r.width * 0.6f, 22f), sm.emergencyDays, 0.5f, 10f, true) * 2f) / 2f;
		y += 36f;
		GUI.color = sm.emergencyActive ? new Color(1f, 0.5f, 0.4f) : Dim;
		Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF_J_FoodDays".Translate(sm.lastFoodDays < 0 ? "?" : sm.lastFoodDays.ToString("0.0")));
		GUI.color = Color.white;
		y += 34f;
		GUI.color = Dim;
		string howto = "CF_J_EmergencyHow".Translate();
		Widgets.Label(new Rect(r.x, y, r.width, Text.CalcHeight(howto, r.width)), howto);
		GUI.color = Color.white;
	}
}
