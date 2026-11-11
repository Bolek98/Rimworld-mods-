using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BattleInstinct;

public class BISettings : ModSettings
{
	public bool enabled = true;
	public bool defaultOn = true;
	public int defaultStance = 1;
	public bool mechs = true;
	public int interval = 30;
	public bool focusFire = true;
	public bool useCover = true;
	public bool avoidFriendlyFire = true;
	public bool retreat = true;
	public float retreatHp = 0.35f;
	public float bleedHours = 3f;
	public bool useAbilities = true;
	public bool noOverheat = true;
	public int aoeMinEnemies = 2;
	public float buffRange = 25f;
	public float defensiveRadius = 8f;
	public float aggressiveRadius = 30f;
	public bool showText = true;
	public bool recommended = true;
	public HashSet<string> blocked = new HashSet<string>();

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref defaultOn, "defaultOn", true);
		Scribe_Values.Look(ref defaultStance, "defaultStance", 1);
		Scribe_Values.Look(ref mechs, "mechs", true);
		Scribe_Values.Look(ref interval, "interval", 30);
		Scribe_Values.Look(ref focusFire, "focusFire", true);
		Scribe_Values.Look(ref useCover, "useCover", true);
		Scribe_Values.Look(ref avoidFriendlyFire, "avoidFriendlyFire", true);
		Scribe_Values.Look(ref retreat, "retreat", true);
		Scribe_Values.Look(ref retreatHp, "retreatHp", 0.35f);
		Scribe_Values.Look(ref bleedHours, "bleedHours", 3f);
		Scribe_Values.Look(ref useAbilities, "useAbilities", true);
		Scribe_Values.Look(ref noOverheat, "noOverheat", true);
		Scribe_Values.Look(ref aoeMinEnemies, "aoeMinEnemies", 2);
		Scribe_Values.Look(ref buffRange, "buffRange", 25f);
		Scribe_Values.Look(ref defensiveRadius, "defensiveRadius", 8f);
		Scribe_Values.Look(ref aggressiveRadius, "aggressiveRadius", 30f);
		Scribe_Values.Look(ref showText, "showText", true);
		Scribe_Values.Look(ref recommended, "recommended", true);
		Scribe_Collections.Look(ref blocked, "blocked", LookMode.Value);
		if (blocked == null) blocked = new HashSet<string>();
	}
}

public class BattleInstinctMod : Mod
{
	public static BISettings Settings;
	public const string Version = "0.1.0";
	private static List<string> recBullets;
	private Vector2 scroll;
	private static string search = "";
	private static List<AbilityDef> all;

	public BattleInstinctMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<BISettings>();
		MUI.Init(new BISettings(), null, "recommended", "blocked");
		if (Settings.recommended)
			MUI.ApplyAll(Settings);
		var harmony = new Harmony("bolek98.battleinstinct");
		harmony.PatchAll();
		MUI.PatchWindow(harmony, typeof(BattleInstinctMod));
		MNews.Register(content, Version);
		Log.Message("[Battle Instinct] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Battle Instinct";

	private static void Check(Listing_Standard l, string key, ref bool value, string field)
	{
		string tip = key + "_Desc";
		MUI.Check(l, key.Translate(), tip.CanTranslate() ? tip.Translate().ToString() : null, ref value, Settings, field);
	}

	public static bool OtherAutoCombat => ModLister.AllInstalledMods.Any(m => m.Active && m.Name != null
		&& m.Name.IndexOf("Auto-Combat", StringComparison.OrdinalIgnoreCase) >= 0 && m.PackageId.IndexOf("bolek98", StringComparison.OrdinalIgnoreCase) < 0);

	public override void DoSettingsWindowContents(Rect r)
	{
		var s = Settings;
		MUI.Version(r, "v" + Version + " · Bolek98");
		if (recBullets == null)
		{
			recBullets = new List<string>();
			for (int i = 1; i <= 3; i++) recBullets.Add(("BI_Rec" + i).Translate());
		}
		float y0 = r.y;
		y0 += MUI.RecommendedCard(new Rect(r.x, y0, r.width, 0f), s, s.recommended, v =>
		{
			s.recommended = v;
			if (v) MUI.ApplyAll(s);
		}, recBullets) + 6f;
		if (s.recommended)
			y0 += MUI.LockNote(new Rect(r.x, y0, r.width, 0f)) + 4f;
		if (OtherAutoCombat)
		{
			GUI.color = MUI.Warn;
			Widgets.Label(new Rect(r.x, y0, r.width, 24f), "BI_OtherMod".Translate());
			GUI.color = Color.white;
			y0 += 26f;
		}
		Rect top = new Rect(r.x, y0, r.width, 370f);
		var l = new Listing_Standard();
		MUI.BeginLock(s.recommended);
		try
		{
			l.Begin(top);
			l.ColumnWidth = (top.width - 24f) / 2f;
			Check(l, "BI_Enabled", ref s.enabled, "enabled");
			Check(l, "BI_DefaultOn", ref s.defaultOn, "defaultOn");
			if (l.ButtonTextLabeled("BI_DefaultStance".Translate(), ("BI_Stance" + s.defaultStance).Translate()))
			{
				var menu = new List<FloatMenuOption>();
				for (int i = 0; i < 3; i++)
				{
					int li = i;
					menu.Add(new FloatMenuOption(("BI_Stance" + i).Translate(), () => s.defaultStance = li));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			Check(l, "BI_Mechs", ref s.mechs, "mechs");
			Check(l, "BI_FocusFire", ref s.focusFire, "focusFire");
			Check(l, "BI_UseCover", ref s.useCover, "useCover");
			Check(l, "BI_AvoidFF", ref s.avoidFriendlyFire, "avoidFriendlyFire");
			Check(l, "BI_Retreat", ref s.retreat, "retreat");
			Check(l, "BI_ShowText", ref s.showText, "showText");
			l.NewColumn();
			if (s.retreat)
			{
				s.retreatHp = MUI.Slider(l, "BI_RetreatHp".Translate((s.retreatHp * 100f).ToString("0")), s.retreatHp, 0.1f, 0.7f, "BI_RetreatHp_Desc".Translate(), s, "retreatHp");
				s.bleedHours = Mathf.Round(MUI.Slider(l, "BI_BleedHours".Translate(s.bleedHours.ToString("0")), s.bleedHours, 1f, 12f, "BI_BleedHours_Desc".Translate(), s, "bleedHours"));
			}
			s.defensiveRadius = Mathf.Round(MUI.Slider(l, "BI_DefRadius".Translate(s.defensiveRadius.ToString("0")), s.defensiveRadius, 3f, 20f, "BI_DefRadius_Desc".Translate(), s, "defensiveRadius"));
			s.aggressiveRadius = Mathf.Round(MUI.Slider(l, "BI_AggRadius".Translate(s.aggressiveRadius.ToString("0")), s.aggressiveRadius, 10f, 60f, "BI_AggRadius_Desc".Translate(), s, "aggressiveRadius"));
			Check(l, "BI_UseAbilities", ref s.useAbilities, "useAbilities");
			if (s.useAbilities)
			{
				Check(l, "BI_NoOverheat", ref s.noOverheat, "noOverheat");
				s.aoeMinEnemies = Mathf.RoundToInt(MUI.Slider(l, "BI_AoeMin".Translate(s.aoeMinEnemies), s.aoeMinEnemies, 1f, 6f, "BI_AoeMin_Desc".Translate(), s, "aoeMinEnemies"));
			}
			s.interval = Mathf.RoundToInt(MUI.Slider(l, "BI_Interval".Translate((s.interval / 60f).ToString("0.0")), s.interval, 15f, 90f, "BI_Interval_Desc".Translate(), s, "interval"));
			l.End();
		}
		finally
		{
			MUI.EndLock(top);
		}

		// every ability in the game and what the mod thinks it is
		if (all == null)
			all = DefDatabase<AbilityDef>.AllDefsListForReading.OrderBy(a => AbilityBrain.KindOf(a) == AKind.Ignore).ThenBy(a => a.label ?? a.defName).ToList();
		float y = top.yMax + 6f;
		Widgets.Label(new Rect(r.x, y + 3f, 300f, 24f), "BI_ListHead".Translate(all.Count));
		search = Widgets.TextField(new Rect(r.x + 310f, y, r.width - 310f, 28f), search ?? "");
		if (string.IsNullOrEmpty(search))
		{
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(r.x + 316f, y + 4f, 300f, 24f), "BI_Search".Translate());
			GUI.color = Color.white;
		}
		y += 32f;
		string q = (search ?? "").Trim();
		List<AbilityDef> list = q.Length == 0 ? all : all.Where(a =>
			(a.label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			a.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			(a.modContentPack?.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			((string)("BI_Kind_" + AbilityBrain.KindOf(a)).Translate()).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		const float rowH = 30f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, list.Count * rowH);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outRect.height) continue;
			AbilityDef a = list[i];
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			AKind k = AbilityBrain.KindOf(a);
			if (k != AKind.Ignore)
			{
				bool on = !s.blocked.Contains(a.defName);
				bool was = on;
				Widgets.Checkbox(new Vector2(row.x + 2f, row.y + 2f), ref on);
				if (on != was)
				{
					if (on) s.blocked.Remove(a.defName);
					else s.blocked.Add(a.defName);
				}
			}
			if (a.uiIcon != null) GUI.DrawTexture(new Rect(row.x + 32f, row.y + 1f, 26f, 26f), a.uiIcon);
			Widgets.Label(new Rect(row.x + 64f, row.y + 3f, 240f, 24f), ((string)a.LabelCap).Truncate(240f));
			GUI.color = k == AKind.Ignore ? MUI.Muted : MUI.Accent;
			Widgets.Label(new Rect(row.x + 310f, row.y + 3f, 200f, 24f), ("BI_Kind_" + k).Translate());
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(row.x + 516f, row.y + 3f, row.width - 516f, 24f), (a.modContentPack?.Name ?? "").Truncate(row.width - 516f));
			GUI.color = Color.white;
			TooltipHandler.TipRegion(row, a.LabelCap + " (" + (a.modContentPack?.Name ?? "?") + ")\n" + ("BI_Kind_" + k + "_Desc").Translate());
		}
		Widgets.EndScrollView();
	}
}
