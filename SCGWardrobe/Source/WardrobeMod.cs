using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ResearchedLoadout;
using RimWorld;
using UnityEngine;
using Verse;

namespace SCGWardrobe;

public sealed class WardrobeMod : Mod
{
	public const string Version = "0.1.0";
	public static WardrobeSettings Settings;
	private static readonly WardrobeSettings rec = new WardrobeSettings();

	private enum Tab { Overview, Weapons, Armor, Work, Care, Occasions, Uniforms, Wardrobe, Tailor, Colonists, Advanced }
	private static Tab tab = Tab.Overview;
	private static Vector2 scroll;
	private static float viewH = 600f;

	public WardrobeMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<WardrobeSettings>();
		UseRec();
		if (Settings.recommended) MUI.ApplyAll(Settings);
		var h = new Harmony("bolek98.scg.wardrobe");
		h.PatchAll(typeof(WardrobeMod).Assembly);
		MUI.PatchWindow(h, typeof(WardrobeMod));
		MNews.Register(content, Version);
		Dressing.Register(WardrobeProvider.Instance);
		Log.Message("[SCG – Wardrobe] v" + Version + " loaded – takes over the dressing decisions of Smart Colonist Gear.");
	}

	private static void UseRec() => MUI.Init(rec, null, "recommended", "workCustomized", "occasionsCustomized", "verbose");

	public override string SettingsCategory() => "WR_Name".CanTranslate() ? (string)"WR_Name".Translate() : "Smart Colonist Gear – Wardrobe";

	private static string T(string key) => key.Translate();

	private static void Check(Listing_Standard l, string key, ref bool v, string field)
	{
		string tip = key + "_Desc";
		MUI.Check(l, key.Translate(), tip.CanTranslate() ? tip.Translate().ToString() : null, ref v, Settings, field);
	}

	private static float Slider(Listing_Standard l, string key, float v, float min, float max, string field, string shown)
	{
		string tip = key + "_Desc";
		return MUI.Slider(l, key.Translate() + ": " + shown, v, min, max, tip.CanTranslate() ? tip.Translate().ToString() : null, Settings, field);
	}

	private static void Muted(Listing_Standard l, string text)
	{
		GUI.color = MUI.Muted;
		l.Label(text);
		GUI.color = Color.white;
	}

	public override void DoSettingsWindowContents(Rect r)
	{
		MUI.Version(r, "v" + Version + " · Bolek98");
		var all = (Tab[])Enum.GetValues(typeof(Tab));
		int half = (all.Length + 1) / 2;
		float top = r.y + 64f;
		for (int row = 0; row < 2; row++)
		{
			var recs = new List<TabRecord>();
			for (int i = row * half; i < Math.Min(all.Length, (row + 1) * half); i++)
			{
				Tab t = all[i];
				recs.Add(new TabRecord(("WR_Tab_" + t).Translate(), () => { tab = t; scroll = Vector2.zero; }, tab == t));
			}
			TabDrawer.DrawTabs(new Rect(r.x, r.y + 32f + row * 32f, r.width, 1f), recs, Mathf.Max(110f, r.width / half));
		}
		Rect body = new Rect(r.x, top + 6f, r.width, r.yMax - top - 6f);
		Widgets.DrawMenuSection(body);
		body = body.ContractedBy(10f);
		UseRec();
		switch (tab)
		{
			case Tab.Overview: Overview(body); break;
			case Tab.Weapons: Weapons(body); break;
			case Tab.Armor: Armor(body); break;
			case Tab.Work: Work(body); break;
			case Tab.Care: Care(body); break;
			case Tab.Occasions: Occasions(body); break;
			case Tab.Uniforms: Uniforms(body); break;
			case Tab.Wardrobe: WardrobeTab(body); break;
			case Tab.Tailor: TailorTab(body); break;
			case Tab.Colonists: Colonists(body); break;
			case Tab.Advanced: Advanced(body); break;
		}
	}

	// scrolled listing helper
	private static Listing_Standard Begin(Rect r)
	{
		Rect view = new Rect(0f, 0f, r.width - 16f, viewH);
		Widgets.BeginScrollView(r, ref scroll, view);
		var l = new Listing_Standard();
		l.Begin(new Rect(0f, 0f, view.width, 99999f));
		return l;
	}

	private static void End(Listing_Standard l)
	{
		viewH = Mathf.Max(200f, l.CurHeight + 20f);
		l.End();
		Widgets.EndScrollView();
	}

	// ---------------- Overview ----------------
	private static List<string> recBullets;

	private static void Overview(Rect r)
	{
		recBullets ??= new List<string> { T("WR_Rec1"), T("WR_Rec2"), T("WR_Rec3"), T("WR_Rec4"), T("WR_Rec5"), T("WR_Rec6") };
		float y = r.y;
		y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), Settings, Settings.recommended, v =>
		{
			Settings.recommended = v;
			if (v) { UseRec(); MUI.ApplyAll(Settings); }
		}, recBullets) + 8f;
		var l = new Listing_Standard();
		l.Begin(new Rect(r.x, y, r.width, r.yMax - y));
		Check(l, "WR_Enabled", ref Settings.enabled, "enabled");
		l.Gap(6f);
		GUI.color = MUI.Accent;
		l.Label(Settings.enabled ? T("WR_TakesOver") : T("WR_ScgDecides"));
		GUI.color = Color.white;
		l.Gap(6f);
		Muted(l, T("WR_OverviewText"));
		l.Gap(6f);
		Muted(l, "WR_RolesSource".Translate(Roles.StewardRolesOn ? T("WR_RolesSteward") : T("WR_RolesSkills")));
		if (ModsConfig.IsActive("PeteTimesSix.SimpleSidearms")) Muted(l, T("WR_SidearmsNote"));
		l.End();
	}

	// ---------------- role table helper ----------------
	private static void RoleTable<E>(Listing_Standard l, List<RolePref> prefs, string labelPrefix) where E : Enum
	{
		foreach (RolePref p in prefs)
		{
			Rect row = l.GetRect(28f);
			Widgets.Label(row.LeftPart(0.45f), Roles.Label(p.role));
			Rect btn = new Rect(row.x + row.width * 0.45f, row.y, Mathf.Min(220f, row.width * 0.5f), 26f);
			E cur = (E)Enum.ToObject(typeof(E), p.value);
			if (Widgets.ButtonText(btn, (labelPrefix + cur).Translate()))
			{
				var opts = new List<FloatMenuOption>();
				foreach (E v in Enum.GetValues(typeof(E)))
				{
					E vv = v;
					opts.Add(new FloatMenuOption((labelPrefix + vv).Translate(), () => { p.value = Convert.ToInt32(vv); Settings.recommended = false; }));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
		}
	}

	// ---------------- Weapons ----------------
	private static void Weapons(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_RoleWeapons", ref Settings.roleWeapons, "roleWeapons");
		Settings.damagedWeaponBelow = Slider(l, "WR_DamagedWeapon", Settings.damagedWeaponBelow, 0f, 0.9f, "damagedWeaponBelow", Settings.damagedWeaponBelow.ToStringPercent());
		Muted(l, T("WR_WeaponsText"));
		if (ModsConfig.IsActive("PeteTimesSix.SimpleSidearms")) Muted(l, T("WR_SidearmsNote"));
		l.GapLine();
		if (Settings.roleWeapons) RoleTable<WeaponPref>(l, Settings.WeaponPrefs, "WR_WP_");
		if (l.ButtonText(T("WR_ResetTable"))) { Settings.weaponPrefs = Defaults.Weapons(); }
		End(l);
	}

	// ---------------- Armour ----------------
	private static void Situation(Listing_Standard l, string key, Func<int> get, Action<int> set)
	{
		Rect row = l.GetRect(28f);
		Widgets.Label(row.LeftPart(0.45f), key.Translate());
		Rect btn = new Rect(row.x + row.width * 0.45f, row.y, Mathf.Min(220f, row.width * 0.5f), 26f);
		if (Widgets.ButtonText(btn, ("WR_AW_" + (ArmorWeight)get()).Translate()))
		{
			var opts = new List<FloatMenuOption>();
			foreach (ArmorWeight v in Enum.GetValues(typeof(ArmorWeight)))
			{
				ArmorWeight vv = v;
				opts.Add(new FloatMenuOption(("WR_AW_" + vv).Translate(), () => { set((int)vv); Settings.recommended = false; }));
			}
			Find.WindowStack.Add(new FloatMenu(opts));
		}
	}

	private static void Armor(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_ArmorSets", ref Settings.armorSets, "armorSets");
		Muted(l, T("WR_ArmorText"));
		if (Settings.armorSets)
		{
			l.GapLine();
			Situation(l, "WR_Sit_Defense", () => Settings.defenseArmor, v => Settings.defenseArmor = v);
			Situation(l, "WR_Sit_Hunt", () => Settings.huntArmor, v => Settings.huntArmor = v);
			Situation(l, "WR_Sit_Caravan", () => Settings.caravanArmor, v => Settings.caravanArmor = v);
			l.GapLine();
			l.Label(T("WR_ArmorRoles"));
			RoleTable<ArmorWeight>(l, Settings.ArmorPrefs, "WR_AW_");
			if (l.ButtonText(T("WR_ResetTable"))) Settings.armorPrefs = Defaults.Armor();
		}
		End(l);
	}

	// ---------------- Work outfits ----------------
	private static void Work(Rect r)
	{
		var l = Begin(r);
		bool before = Settings.workEditor;
		Check(l, "WR_WorkEditor", ref Settings.workEditor, "workEditor");
		if (before != Settings.workEditor) WardrobeProvider.EditorChanged();
		Muted(l, T("WR_WorkText"));
		if (l.ButtonText(T("WR_WorkReset")))
		{
			Settings.workOutfits = Defaults.WorkOutfits();
			Settings.workCustomized = false;
			WardrobeProvider.EditorChanged();
		}
		l.GapLine();
		if (Settings.workEditor)
		{
			var used = new HashSet<string>(WorkTypes().Select(w => w.defName));
			foreach (WorkTypeDef wt in WorkTypes())
			{
				WorkOutfit wo = Settings.WorkOutfits.FirstOrDefault(x => x.work == wt.defName);
				Rect head = l.GetRect(28f);
				Text.Font = GameFont.Small;
				Widgets.Label(head.LeftPart(0.6f), wt.labelShort.CapitalizeFirst() + "  (" + (wt.relevantSkills?.FirstOrDefault()?.LabelCap.ToString() ?? "-") + ")");
				if (Widgets.ButtonText(new Rect(head.xMax - 130f, head.y, 130f, 26f), T("WR_AddPiece")))
				{
					var opts = new List<FloatMenuOption>();
					foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.IsApparel).OrderBy(x => x.label))
					{
						if (wo != null && wo.apparel.Contains(d.defName)) continue;
						ThingDef dd = d;
						opts.Add(new FloatMenuOption(d.LabelCap, () =>
						{
							WorkOutfit w = Settings.WorkOutfits.FirstOrDefault(x => x.work == wt.defName);
							if (w == null) Settings.WorkOutfits.Add(w = new WorkOutfit(wt.defName));
							w.apparel.Add(dd.defName);
							Settings.workCustomized = true;
							WardrobeProvider.EditorChanged();
						}, dd));
					}
					Find.WindowStack.Add(new FloatMenu(opts));
				}
				if (wo == null || wo.apparel.Count == 0) { Muted(l, "    " + T("WR_NoPieces")); continue; }
				foreach (string a in wo.apparel.ToList())
				{
					Rect row = l.GetRect(24f);
					ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(a);
					Widgets.Label(new Rect(row.x + 20f, row.y, row.width - 60f, 24f), d?.LabelCap.ToString() ?? a);
					if (Widgets.ButtonText(new Rect(row.xMax - 30f, row.y, 28f, 22f), "×"))
					{
						wo.apparel.Remove(a);
						Settings.workCustomized = true;
						WardrobeProvider.EditorChanged();
					}
				}
			}
		}
		End(l);
	}

	private static List<WorkTypeDef> workTypes;

	private static List<WorkTypeDef> WorkTypes()
	{
		return workTypes ??= DefDatabase<WorkTypeDef>.AllDefsListForReading
			.Where(w => w.visible && w.relevantSkills != null && w.relevantSkills.Count > 0 && Defaults.WorkFor(w.relevantSkills[0]) == w)
			.OrderByDescending(w => w.naturalPriority).ToList();
	}

	// ---------------- Care ----------------
	private static void Care(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_Care", ref Settings.care, "care");
		if (Settings.care)
		{
			Settings.replaceBelow = Slider(l, "WR_ReplaceBelow", Settings.replaceBelow, 0.2f, 0.9f, "replaceBelow", Settings.replaceBelow.ToStringPercent());
			Check(l, "WR_NoCorpse", ref Settings.noCorpse, "noCorpse");
			Check(l, "WR_RetireBills", ref Settings.retireBills, "retireBills");
			if (Settings.retireBills)
				Settings.retireBelow = Slider(l, "WR_RetireBelow", Settings.retireBelow, 0.05f, 0.8f, "retireBelow", Settings.retireBelow.ToStringPercent());
			Muted(l, T("WR_CareText"));
		}
		End(l);
	}

	// ---------------- Occasions ----------------
	private static void DefList(Listing_Standard l, List<string> list)
	{
		foreach (string a in list.ToList())
		{
			Rect row = l.GetRect(24f);
			ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(a);
			Widgets.Label(new Rect(row.x + 20f, row.y, row.width - 60f, 24f), d?.LabelCap.ToString() ?? a);
			if (Widgets.ButtonText(new Rect(row.xMax - 30f, row.y, 28f, 22f), "×"))
			{
				list.Remove(a);
				Settings.occasionsCustomized = true;
				WardrobeProvider.SetsVersion++;
			}
		}
		if (list.Count == 0) Muted(l, "    " + T("WR_NoPieces"));
		if (l.ButtonText(T("WR_AddPiece")))
		{
			var opts = new List<FloatMenuOption>();
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.IsApparel && !list.Contains(x.defName)).OrderBy(x => x.label))
			{
				ThingDef dd = d;
				opts.Add(new FloatMenuOption(d.LabelCap, () => { list.Add(dd.defName); Settings.occasionsCustomized = true; WardrobeProvider.SetsVersion++; }, dd));
			}
			Find.WindowStack.Add(new FloatMenu(opts));
		}
	}

	private static void Occasions(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_Sleepwear", ref Settings.sleepwear, "sleepwear");
		if (Settings.sleepwear)
		{
			Settings.sleepFrom = (int)Slider(l, "WR_SleepFrom", Settings.sleepFrom, 0f, 23f, "sleepFrom", Settings.sleepFrom + ":00");
			Settings.sleepTo = (int)Slider(l, "WR_SleepTo", Settings.sleepTo, 0f, 23f, "sleepTo", Settings.sleepTo + ":00");
			l.Label(T("WR_SleepList"));
			DefList(l, Settings.SleepDefs);
		}
		l.GapLine();
		Check(l, "WR_Festive", ref Settings.festive, "festive");
		if (Settings.festive)
		{
			Check(l, "WR_FestiveOnly", ref Settings.festiveOnlyOccasions, "festiveOnlyOccasions");
			Muted(l, ModsConfig.IsActive("misak.seasonsholidays") ? T("WR_HolidaysOn") : T("WR_HolidaysOff"));
			l.Label(T("WR_FestiveList"));
			DefList(l, Settings.FestiveDefs);
		}
		l.GapLine();
		if (l.ButtonText(T("WR_OccasionsReset")))
		{
			Settings.sleepDefs = Defaults.Sleepwear();
			Settings.festiveDefs = Defaults.Festive();
			Settings.occasionsCustomized = false;
			WardrobeProvider.SetsVersion++;
		}
		End(l);
	}

	// ---------------- Uniforms ----------------
	private static readonly Color[] Palette =
	{
		Color.white, new Color(0.92f, 0.92f, 0.92f), new Color(0.2f, 0.2f, 0.2f), new Color(0.33f, 0.42f, 0.27f), new Color(0.45f, 0.36f, 0.24f),
		new Color(0.85f, 0.6f, 0.2f), new Color(0.95f, 0.95f, 0.85f), new Color(0.3f, 0.45f, 0.7f), new Color(0.4f, 0.6f, 0.3f),
		new Color(0.7f, 0.2f, 0.2f), new Color(0.55f, 0.3f, 0.6f), new Color(0.2f, 0.6f, 0.65f)
	};

	private static void Uniforms(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_Uniforms", ref Settings.uniforms, "uniforms");
		Muted(l, ModsConfig.IdeologyActive ? T("WR_UniformsText") : T("WR_NeedIdeology"));
		if (Settings.uniforms)
		{
			l.GapLine();
			foreach (RoleColor rc in Settings.RoleColors)
			{
				Rect row = l.GetRect(28f);
				bool on = rc.on;
				Widgets.CheckboxLabeled(new Rect(row.x, row.y, row.width * 0.45f, 26f), Roles.Label(rc.role), ref on);
				if (on != rc.on) { rc.on = on; Settings.recommended = false; }
				float x = row.x + row.width * 0.48f;
				foreach (Color c in Palette)
				{
					Rect sw = new Rect(x, row.y + 2f, 22f, 22f);
					Widgets.DrawBoxSolid(sw, c);
					if (rc.color.IndistinguishableFrom(c)) Widgets.DrawBox(sw, 2);
					if (Widgets.ButtonInvisible(sw)) { rc.color = c; rc.on = true; Settings.recommended = false; }
					x += 26f;
				}
			}
		}
		End(l);
	}

	// ---------------- Personal wardrobe ----------------
	private static void WardrobeTab(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_Favorites", ref Settings.favorites, "favorites");
		if (Settings.favorites) Check(l, "WR_ToBedroom", ref Settings.favoritesToBedroom, "favoritesToBedroom");
		Muted(l, T("WR_FavoritesText"));
		WardrobeGame g = WardrobeGame.Get();
		if (g != null && Find.CurrentMap != null)
		{
			l.GapLine();
			foreach (Pawn p in Find.CurrentMap.mapPawns.FreeColonistsSpawned)
			{
				Rect row = l.GetRect(26f);
				int n = g.FavoriteCount(p);
				Widgets.Label(row.LeftPart(0.5f), p.LabelShort);
				Widgets.Label(new Rect(row.x + row.width * 0.5f, row.y, 200f, 26f), n > 0 ? "WR_FavoritesNow".Translate(n).ToString() : T("WR_NoFavorites"));
				if (n > 0 && Widgets.ButtonText(new Rect(row.xMax - 110f, row.y, 110f, 24f), T("WR_ClearFavorites"))) g.ClearFavorites(p);
			}
		}
		End(l);
	}

	// ---------------- Tailor ----------------
	private static List<TailorSuggestion> sugg;
	private static int suggTick = -99999;

	private static void TailorTab(Rect r)
	{
		var l = Begin(r);
		Check(l, "WR_Tailor", ref Settings.tailorPlanner, "tailorPlanner");
		Muted(l, T("WR_TailorText"));
		Map map = Find.CurrentMap;
		if (Settings.tailorPlanner && map != null)
		{
			int now = Find.TickManager.TicksGame;
			if (sugg == null || now - suggTick > 600 || l.ButtonText(T("WR_Refresh"))) { sugg = Tailor.Suggest(map); suggTick = now; }
			l.GapLine();
			if (sugg.Count == 0) Muted(l, T("WR_NothingToMake"));
			foreach (TailorSuggestion s in sugg.ToList())
			{
				Rect row = l.GetRect(30f);
				string who = s.who.Count > 0 ? " – " + string.Join(", ", s.who.Take(4)) + (s.who.Count > 4 ? "…" : "") : "";
				Widgets.Label(new Rect(row.x, row.y, row.width - 130f, 30f), (s.recipe.ProducedThingDef?.LabelCap.ToString() ?? s.recipe.LabelCap.ToString()) + " ×" + s.count + "  (" + s.reason.Translate() + ")" + who);
				if (Widgets.ButtonText(new Rect(row.xMax - 124f, row.y + 2f, 124f, 26f), T("WR_AddBill")))
				{
					TailorSuggestion ss = s;
					Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("WR_ConfirmBill".Translate(ss.recipe.LabelCap, ss.count), () => { Tailor.AddBill(map, ss); sugg.Remove(ss); }));
				}
			}
		}
		else if (map == null) Muted(l, T("WR_NoMap"));
		End(l);
	}

	// ---------------- Colonists ----------------
	private static void Colonists(Rect r)
	{
		var l = Begin(r);
		Muted(l, T("WR_ColonistsText"));
		WardrobeGame g = WardrobeGame.Get();
		if (g == null || Find.CurrentMap == null) { Muted(l, T("WR_NoMap")); End(l); return; }
		l.GapLine();
		foreach (Pawn p in Find.CurrentMap.mapPawns.FreeColonistsSpawned)
		{
			Rect row = l.GetRect(28f);
			bool managed = !g.Exempt(p);
			bool was = managed;
			Widgets.CheckboxLabeled(new Rect(row.x, row.y, row.width * 0.35f, 26f), p.LabelShort, ref managed);
			if (managed != was) { if (managed) g.exempt.Remove(p.thingIDNumber); else g.exempt.Add(p.thingIDNumber); }
			bool ov = g.roleOverride.ContainsKey(p.thingIDNumber);
			string role = Roles.Of(p);
			if (Widgets.ButtonText(new Rect(row.x + row.width * 0.38f, row.y, 200f, 26f), Roles.Label(role) + (ov ? " *" : "")))
			{
				var opts = new List<FloatMenuOption> { new FloatMenuOption(T("WR_RoleAuto"), () => g.roleOverride.Remove(p.thingIDNumber)) };
				foreach (string rr in Roles.All)
				{
					string x = rr;
					opts.Add(new FloatMenuOption(Roles.Label(x), () => g.roleOverride[p.thingIDNumber] = x));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
			string hold = WardrobeProvider.HoldOf(p);
			Widgets.Label(new Rect(row.x + row.width * 0.38f + 210f, row.y, 220f, 26f), hold == null ? "" : ("WR_Hold_" + hold).Translate().ToString());
		}
		End(l);
	}

	// ---------------- Advanced ----------------
	private static void Advanced(Rect r)
	{
		var l = Begin(r);
		Settings.maxDistance = (int)Slider(l, "WR_MaxDistance", Settings.maxDistance, 15f, 200f, "maxDistance", Settings.maxDistance.ToString());
		MUI.Check(l, T("WR_Verbose"), null, ref Settings.verbose, Settings, "verbose");
		l.GapLine();
		if (l.ButtonText(T("WR_ResetAll")))
		{
			Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(T("WR_ResetAll_Confirm"), () =>
			{
				Settings.weaponPrefs = Defaults.Weapons();
				Settings.armorPrefs = Defaults.Armor();
				Settings.roleColors = Defaults.Colors();
				Settings.workOutfits = Defaults.WorkOutfits();
				Settings.workCustomized = false;
				Settings.sleepDefs = Defaults.Sleepwear();
				Settings.festiveDefs = Defaults.Festive();
				Settings.occasionsCustomized = false;
				Settings.recommended = true;
				UseRec();
				MUI.ApplyAll(Settings);
				WardrobeProvider.EditorChanged();
				WardrobeProvider.SetsVersion++;
			}, true));
		}
		Muted(l, T("WR_AdvancedText"));
		End(l);
	}
}
