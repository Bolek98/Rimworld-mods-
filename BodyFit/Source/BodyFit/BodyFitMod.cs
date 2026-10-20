using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

public class BodyFitSettings : ModSettings
{
	public bool fixHeads = true;
	public bool fixBodies = true;
	public bool fixApparel = true;
	public bool fitApparel = true;
	public Dictionary<string, Vector2> headOverride = new Dictionary<string, Vector2>();
	public Dictionary<string, float> raceHeight = new Dictionary<string, float>();
	// 1.1: your own replacement body per body type (FemaleBB → Female …)
	public Dictionary<string, string> bodyFallback = new Dictionary<string, string>();
	// 1.1: hair
	public bool fixHair = true;
	public bool fitHair = true;
	// 1.2
	public bool fitAll = true;
	public bool fitHeadgear = true;
	public bool fitWeapons = true;
	// 2.0: refitting clothes row by row
	public bool refit = true;          // 2.2: humans and body mods (FemaleBB, Beautiful Bodies …) – on
	public bool refitAliens = true;    // 2.3.6: alien races – on
	public bool refitSides = true;
	public bool refitHeight = true;
	// 2.1
	public bool refitHeads = true;   // 2.3.6: on
	public bool refitCore = true;
	public Dictionary<string, ItemTweak> items = new Dictionary<string, ItemTweak>();
	public Dictionary<string, string> raceBody = new Dictionary<string, string>();
	private List<string> k9; private List<ItemTweak> v9; private List<string> k10; private List<string> v10;
	public ItemTweak Tweak(ThingDef d)
	{
		if (d == null || items.Count == 0)
			return null;
		items.TryGetValue(d.defName, out ItemTweak t);
		return t;
	}
	public Dictionary<string, float> weaponHeight = new Dictionary<string, float>();
	// 1.2.1: per race – no automatic fitting, own size of clothes
	public HashSet<string> noFitRaces = new HashSet<string>();
	// 2.3.0: races (other than humans) that Body Fit changes – none by default
	public HashSet<string> fitRaces = new HashSet<string>();
	public HashSet<string> offRaces = new HashSet<string>();
	public Dictionary<string, float> clothesWidth = new Dictionary<string, float>();
	public Dictionary<string, float> clothesHeight = new Dictionary<string, float>();
	private List<string> k7; private List<float> v7; private List<string> k8; private List<float> v8;
	public Vector2 ClothesSize(ThingDef race)
	{
		if (race == null)
			return Vector2.one;
		clothesWidth.TryGetValue(race.defName, out float w);
		clothesHeight.TryGetValue(race.defName, out float h);
		return new Vector2(w == 0f ? 1f : w, h == 0f ? 1f : h);
	}
	private List<string> k6; private List<float> v6;
	public float WeaponHeight(ThingDef race) => race != null && weaponHeight.TryGetValue(race.defName, out float h) ? h : 0f;
	public Dictionary<string, float> hairHeight = new Dictionary<string, float>();
	public Dictionary<string, float> hairSize = new Dictionary<string, float>();
	private List<string> k3; private List<string> v3; private List<string> k4; private List<float> v4; private List<string> k5; private List<float> v5;

	public float HairHeight(ThingDef race) => race != null && hairHeight.TryGetValue(race.defName, out float h) ? h : 0f;
	public float HairSize(ThingDef race) => race != null && hairSize.TryGetValue(race.defName, out float h) ? h : 1f;

	private List<string> k1; private List<Vector2> v1; private List<string> k2; private List<float> v2;

	public float RaceHeight(ThingDef race) => race != null && raceHeight.TryGetValue(race.defName, out float h) ? h : 0f;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref fixHeads, "fixHeads", true);
		Scribe_Values.Look(ref fixBodies, "fixBodies", true);
		Scribe_Values.Look(ref fixApparel, "fixApparel", true);
		Scribe_Values.Look(ref fitApparel, "fitApparel", true);
		Scribe_Collections.Look(ref headOverride, "headOverride", LookMode.Value, LookMode.Value, ref k1, ref v1);
		Scribe_Collections.Look(ref raceHeight, "raceHeight", LookMode.Value, LookMode.Value, ref k2, ref v2);
		Scribe_Collections.Look(ref bodyFallback, "bodyFallback", LookMode.Value, LookMode.Value, ref k3, ref v3);
		Scribe_Values.Look(ref fixHair, "fixHair", true);
		Scribe_Values.Look(ref fitHair, "fitHair12", true);
		Scribe_Values.Look(ref fitAll, "fitAll", true);
		Scribe_Values.Look(ref fitHeadgear, "fitHeadgear", true);
		Scribe_Values.Look(ref fitWeapons, "fitWeapons", true);
		Scribe_Values.Look(ref refit, "refit22", true);
		Scribe_Values.Look(ref refitAliens, "refitAliens236", true);
		Scribe_Values.Look(ref refitSides, "refitSides", true);
		Scribe_Values.Look(ref refitHeight, "refitHeight", true);
		Scribe_Values.Look(ref refitHeads, "refitHeads236", true);
		Scribe_Values.Look(ref refitCore, "refitCore", true);
		Scribe_Collections.Look(ref items, "items", LookMode.Value, LookMode.Deep, ref k9, ref v9);
		Scribe_Collections.Look(ref raceBody, "raceBody", LookMode.Value, LookMode.Value, ref k10, ref v10);
		if (items == null) items = new Dictionary<string, ItemTweak>();
		if (raceBody == null) raceBody = new Dictionary<string, string>();
		Scribe_Collections.Look(ref weaponHeight, "weaponHeight", LookMode.Value, LookMode.Value, ref k6, ref v6);
		if (weaponHeight == null) weaponHeight = new Dictionary<string, float>();
		Scribe_Collections.Look(ref noFitRaces, "noFitRaces", LookMode.Value);
		Scribe_Collections.Look(ref fitRaces, "fitRaces", LookMode.Value);
		if (fitRaces == null)
			fitRaces = new HashSet<string>();
		// 2.3.6: every race is fitted unless you switch it off
		Scribe_Collections.Look(ref offRaces, "offRaces", LookMode.Value);
		if (offRaces == null)
			offRaces = new HashSet<string>();
		Scribe_Collections.Look(ref clothesWidth, "clothesWidth", LookMode.Value, LookMode.Value, ref k7, ref v7);
		Scribe_Collections.Look(ref clothesHeight, "clothesHeight", LookMode.Value, LookMode.Value, ref k8, ref v8);
		if (noFitRaces == null) noFitRaces = new HashSet<string>();
		if (clothesWidth == null) clothesWidth = new Dictionary<string, float>();
		if (clothesHeight == null) clothesHeight = new Dictionary<string, float>();
		Scribe_Collections.Look(ref hairHeight, "hairHeight", LookMode.Value, LookMode.Value, ref k4, ref v4);
		Scribe_Collections.Look(ref hairSize, "hairSize", LookMode.Value, LookMode.Value, ref k5, ref v5);
		if (bodyFallback == null) bodyFallback = new Dictionary<string, string>();
		if (hairHeight == null) hairHeight = new Dictionary<string, float>();
		if (hairSize == null) hairSize = new Dictionary<string, float>();
		if (headOverride == null) headOverride = new Dictionary<string, Vector2>();
		if (raceHeight == null) raceHeight = new Dictionary<string, float>();
		if (BodyFitMod.Simple)
			ApplySimple();
	}

	// 2.5: the simple Body Fit – no tweaks, everything automatic. Only "which races" stays yours.
	// It never moves heads, hair, hats, weapons or body parts of alien races (their own mod knows
	// best where they go); it only reshapes the pictures of clothes, hats and masks for the body
	// and head they are worn on, and finds a picture for bodies the clothes were not painted for.
	public void ApplySimple()
	{
		fixHeads = true;       // only bodies from mods that have no head position at all
		fixBodies = true;      // only bodies that cannot be drawn
		fixApparel = true;     // a picture for bodies the clothes were not painted for
		fitApparel = false;    // no whole-picture scaling or moving
		fixHair = true;        // missing hair picture -> a plain one instead of a pink square
		fitHair = false;
		fitHeadgear = false;
		fitWeapons = false;
		fitAll = true;
		refit = refitAliens = refitSides = refitHeight = refitHeads = refitCore = true;
		headOverride.Clear();
		raceHeight.Clear();
		bodyFallback.Clear();
		items.Clear();
		raceBody.Clear();
		weaponHeight.Clear();
		noFitRaces.Clear();
		fitRaces.Clear();
		clothesWidth.Clear();
		clothesHeight.Clear();
		hairHeight.Clear();
		hairSize.Clear();
	}
}

public partial class BodyFitMod : Mod
{
	public static BodyFitSettings Settings;
	public const bool Simple = true;   // 2.5
	public static ModContentPack Content;
	private int tab;
	private Vector2 scroll;

	public BodyFitMod(ModContentPack content) : base(content)
	{
		Content = content;
		Settings = GetSettings<BodyFitSettings>();
		try
		{
			new Harmony("bolek98.bodyfit").PatchAll();
			MLog.Game("[Body Fit] v2.5.0 loaded (messages: Logs\\BodyFit.log).");
		}
		catch (Exception ex)
		{
			Log.Error("[Body Fit] patching failed: " + ex);
		}
	}

	public override string SettingsCategory() => "Body Fit";

	public static void Changed()
	{
		HeadFix.Clear();
		Patch_Apparel.Clear();
		HairFix.Clear();
		if (Current.Game != null && Find.Maps != null)
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
					if (p.RaceProps.Humanlike)
						p.Drawer?.renderer?.SetAllGraphicsDirty();
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		if (Simple)
		{
			DrawSimple(inRect);
			return;
		}
		var tabs = new List<TabRecord>
		{
			new TabRecord("BF_TabGeneral".Translate(), () => { tab = 0; scroll = Vector2.zero; }, tab == 0),
			new TabRecord("BF_TabBodies".Translate(), () => { tab = 1; scroll = Vector2.zero; }, tab == 1),
			new TabRecord("BF_TabRaces".Translate(), () => { tab = 2; scroll = Vector2.zero; }, tab == 2),
			new TabRecord("BF_TabClothes".Translate(), () => { tab = 3; scroll = Vector2.zero; }, tab == 3),
			new TabRecord("BF_TabPreview".Translate(), () => { tab = 4; scroll = Vector2.zero; }, tab == 4),
			new TabRecord("BF_TabWardrobe".Translate(), () => { tab = 5; scroll = Vector2.zero; }, tab == 5)
		};
		Rect body = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
		Widgets.DrawMenuSection(body);
		TabDrawer.DrawTabs(body, tabs, 200f);
		Rect inner = body.ContractedBy(10f);
		if (tab == 0) DrawGeneral(inner);
		else if (tab == 1) DrawBodies(inner);
		else if (tab == 2) DrawRaces(inner);
		else if (tab == 3) DrawClothes(inner);
		else if (tab == 4) DrawPreview(inner);
		else DrawWardrobe(inner);
	}

	// 2.5: the whole settings window of the simple Body Fit
	private Vector2 simpleScroll;

	private void DrawSimple(Rect r)
	{
		var l = new Listing_Standard();
		Rect top = new Rect(r.x, r.y, Mathf.Min(r.width, 760f), 150f);
		l.Begin(top);
		l.Label("BF_SimpleIntro".Translate());
		l.Gap(6f);
		GUI.color = new Color(0.75f, 0.75f, 0.75f);
		l.Label("BF_SimpleStats".Translate(Refit.made, RefitCache.Hits, RefitCache.Saved));
		GUI.color = Color.white;
		l.End();
		var races = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && d.race.Humanlike && d != ThingDefOf.Human).OrderBy(d => d.label ?? d.defName).ToList();
		Rect head = new Rect(r.x, r.y + 156f, Mathf.Min(r.width, 760f), 28f);
		Widgets.Label(head, "BF_SimpleRaces".Translate());
		Rect outer = new Rect(r.x, head.yMax + 4f, Mathf.Min(r.width, 760f), r.yMax - head.yMax - 8f);
		Rect view = new Rect(0f, 0f, outer.width - 20f, races.Count * 28f);
		Widgets.BeginScrollView(outer, ref simpleScroll, view);
		float y = 0f;
		foreach (ThingDef d in races)
		{
			bool on = !Settings.offRaces.Contains(d.defName);
			bool was = on;
			Widgets.CheckboxLabeled(new Rect(0f, y, view.width, 26f), (d.LabelCap.ToString().NullOrEmpty() ? d.defName : d.LabelCap.ToString()) + "  (" + (d.modContentPack?.Name ?? "?") + ")", ref on);
			if (on != was)
			{
				if (on) Settings.offRaces.Remove(d.defName); else Settings.offRaces.Add(d.defName);
				Refit.Clear();
				WriteSettings();
			}
			y += 28f;
		}
		Widgets.EndScrollView();
	}

	private void DrawGeneral(Rect r)
	{
		var l = new Listing_Standard();
		l.Begin(r);
		l.ColumnWidth = Mathf.Min(r.width, 700f);
		l.Label("BF_Intro".Translate());
		l.GapLine();
		Check(l, "BF_FixHeads", ref Settings.fixHeads);
		Check(l, "BF_FixBodies", ref Settings.fixBodies);
		Check(l, "BF_FixApparel", ref Settings.fixApparel);
		bool r0 = Settings.refit, r1 = Settings.refitSides, r2 = Settings.refitHeight, r3 = Settings.refitHeads, r4 = Settings.refitCore, r5 = Settings.refitAliens;
		Check(l, "BF_Refit", ref Settings.refit);
		Check(l, "BF_RefitAliens", ref Settings.refitAliens);
		if (Settings.refit || Settings.refitAliens)
		{
			Check(l, "BF_RefitSides", ref Settings.refitSides);
			Check(l, "BF_RefitHeight", ref Settings.refitHeight);
			Check(l, "BF_RefitCore", ref Settings.refitCore);
		}
		if (Settings.refitAliens)
			Check(l, "BF_RefitHeads", ref Settings.refitHeads);
		if (r5 != Settings.refitAliens || r0 != Settings.refit || r1 != Settings.refitSides || r2 != Settings.refitHeight || r3 != Settings.refitHeads || r4 != Settings.refitCore)
		{
			Refit.Clear();
			Changed();
		}
		Check(l, "BF_FitApparel", ref Settings.fitApparel);
		Check(l, "BF_FitAll", ref Settings.fitAll);
		Check(l, "BF_FitHeadgear", ref Settings.fitHeadgear);
		Check(l, "BF_FitWeapons", ref Settings.fitWeapons);
		Check(l, "BF_FixHair", ref Settings.fixHair);
		Check(l, "BF_FitHair", ref Settings.fitHair);
		l.GapLine();
		if (Current.Game != null && l.ButtonText("BF_RepairNow".Translate()))
		{
			int n = BodyRepair.CheckAll(true);
			Changed();
			Messages.Message("BF_RepairDone".Translate(n), MessageTypeDefOf.NeutralEvent, false);
		}
		if (l.ButtonText("BF_Report".Translate()))
			Find.WindowStack.Add(new Dialog_MessageBox(Report.Write()));
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		l.Label("BF_ReportTip".Translate());
		GUI.color = Color.white;
		l.End();
	}

	private static void Check(Listing_Standard l, string key, ref bool v)
	{
		bool was = v;
		l.CheckboxLabeled(key.Translate(), ref v, (key + "_Desc").Translate());
		if (v != was)
			Changed();
	}

	// every body type of every mod: status and your own head position
	private void DrawBodies(Rect r)
	{
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		string head = "BF_BodiesHead".Translate();
		float hh = Text.CalcHeight(head, r.width);
		Widgets.Label(new Rect(r.x, r.y, r.width, hh), head);
		GUI.color = Color.white;
		List<BodyTypeDef> all = DefDatabase<BodyTypeDef>.AllDefsListForReading.OrderBy(b => b.modContentPack?.IsOfficialMod ?? true ? 0 : 1).ThenBy(b => b.defName).ToList();
		Rect outRect = new Rect(r.x, r.y + hh + 6f, r.width, r.height - hh - 6f);
		float rowH = 56f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, all.Count * rowH);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		for (int i = 0; i < all.Count; i++)
		{
			BodyTypeDef b = all[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			Texture2D icon = Bodies.TextureExists(b.bodyNakedGraphicPath) ? ContentFinder<Texture2D>.Get(b.bodyNakedGraphicPath + "_south", false) : null;
			if (icon != null)
				GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 48f, 48f), icon);
			Widgets.Label(new Rect(row.x + 56f, row.y + 2f, 300f, 24f), b.defName + "  (" + (b.modContentPack?.Name ?? "?") + ")");
			var notes = new List<string>();
			if (!Bodies.TextureExists(b.bodyNakedGraphicPath)) notes.Add("BF_NoBodyTex".Translate());
			if (HeadFix.Missing(b)) notes.Add("BF_NoHead".Translate());
			GUI.color = notes.Count > 0 ? new Color(1f, 0.8f, 0.4f) : new Color(0.7f, 1f, 0.7f);
			Widgets.Label(new Rect(row.x + 56f, row.y + 26f, 300f, 24f), notes.Count > 0 ? string.Join(", ", notes) : (string)"BF_Ok".Translate());
			GUI.color = Color.white;
			// head position: height and sideways, steps of 0.01 (Shift 0.05)
			bool own = Settings.headOverride.TryGetValue(b.defName, out Vector2 v);
			if (!own)
				v = HeadFix.Wanted(b, Gender.None);
			float x = row.x + 370f;
			Vector2 nv = v;
			nv.y = Stepper(new Rect(x, row.y + 2f, 220f, 24f), "BF_HeadUp".Translate(), nv.y);
			nv.x = Stepper(new Rect(x, row.y + 26f, 220f, 24f), "BF_HeadSide".Translate(), nv.x);
			if (nv != v)
			{
				Settings.headOverride[b.defName] = nv;
				Changed();
			}
			// replacement body for clothes with no picture for this body
			Settings.bodyFallback.TryGetValue(b.defName, out string fb);
			Rect fbr = new Rect(row.x + 600f, row.y + 12f, Mathf.Max(120f, row.xMax - 690f), 26f);
			if (Widgets.ButtonText(fbr, "BF_Fallback".Translate(fb ?? (string)"BF_Auto".Translate())))
			{
				BodyTypeDef local = b;
				var menu = new List<FloatMenuOption> { new FloatMenuOption("BF_Auto".Translate(), () => { Settings.bodyFallback.Remove(local.defName); Changed(); }) };
				foreach (BodyTypeDef o in DefDatabase<BodyTypeDef>.AllDefsListForReading)
				{
					if (o == local)
						continue;
					BodyTypeDef lo = o;
					menu.Add(new FloatMenuOption(o.defName, () => { Settings.bodyFallback[local.defName] = lo.defName; Changed(); }));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			TooltipHandler.TipRegion(fbr, "BF_FallbackTip".Translate());
			if (own && Widgets.ButtonText(new Rect(row.xMax - 80f, row.y + 12f, 76f, 26f), "BF_Reset".Translate()))
			{
				Settings.headOverride.Remove(b.defName);
				Changed();
			}
		}
		Widgets.EndScrollView();
	}

	// every humanlike race: extra head height
	private void DrawRaces(Rect r)
	{
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		string head = "BF_RacesHead".Translate();
		float hh = Text.CalcHeight(head, r.width);
		Widgets.Label(new Rect(r.x, r.y, r.width, hh), head);
		GUI.color = Color.white;
		List<ThingDef> races = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && d.race.Humanlike).OrderBy(d => d.label ?? d.defName).ToList();
		Rect outRect = new Rect(r.x, r.y + hh + 6f, r.width, r.height - hh - 6f);
		float rowH = 82f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, races.Count * rowH);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		for (int i = 0; i < races.Count; i++)
		{
			ThingDef d = races[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			List<BodyTypeDef> bt = Bodies.HarBodyTypes(d);
			string info = (d.LabelCap.NullOrEmpty() ? d.defName : (string)d.LabelCap) + "  (" + (d.modContentPack?.Name ?? "?") + ")"
			              + (bt.Count > 0 ? "  –  " + string.Join(", ", bt.Select(b => b.defName)) : "");
			// 2.3.0: Body Fit changes this race only when switched on (humans always)
			bool human = d == ThingDefOf.Human;
			bool on = human || !Settings.offRaces.Contains(d.defName);
			if (!human)
			{
				bool was = on;
				Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 4f), ref on, 24f);
				TooltipHandler.TipRegion(new Rect(row.x + 4f, row.y + 4f, 24f, 24f), "BF_RaceOn_Desc".Translate());
				if (on != was)
				{
					if (on) Settings.offRaces.Remove(d.defName);
					else Settings.offRaces.Add(d.defName);
					Refit.Clear();
					Changed();
				}
			}
			Widgets.Label(new Rect(row.x + 34f, row.y + 4f, row.width - 360f, 24f), info.Truncate(row.width - 360f));
			if (!on)
			{
				GUI.color = new Color(0.65f, 0.65f, 0.65f);
				Widgets.Label(new Rect(row.x + 34f, row.y + 30f, row.width - 40f, 24f), "BF_RaceOff".Translate());
				GUI.color = Color.white;
				continue;
			}
			float v = Settings.RaceHeight(d);
			float nv = Stepper(new Rect(row.xMax - 320f, row.y + 2f, 220f, 24f), "BF_HeadUp".Translate(), v);
			if (nv != v)
			{
				if (Mathf.Abs(nv) < 0.0001f) Settings.raceHeight.Remove(d.defName);
				else Settings.raceHeight[d.defName] = nv;
				Changed();
			}
			// hair: height and size
			float hh2 = Settings.HairHeight(d);
			float nh = Stepper(new Rect(row.x + 4f, row.y + 28f, 220f, 24f), "BF_HairUp".Translate(), hh2);
			if (nh != hh2)
			{
				if (Mathf.Abs(nh) < 0.0001f) Settings.hairHeight.Remove(d.defName);
				else Settings.hairHeight[d.defName] = nh;
				Changed();
			}
			float hs = Settings.HairSize(d);
			float ns = Stepper(new Rect(row.x + 240f, row.y + 28f, 220f, 24f), "BF_HairSize".Translate(), hs);
			if (ns != hs)
			{
				if (Mathf.Abs(ns - 1f) < 0.0001f) Settings.hairSize.Remove(d.defName);
				else Settings.hairSize[d.defName] = Mathf.Clamp(ns, 0.3f, 2f);
				Changed();
			}
			float wh = Settings.WeaponHeight(d);
			float nw = Stepper(new Rect(row.x + 476f, row.y + 28f, 220f, 24f), "BF_WeaponUp".Translate(), wh);
			if (nw != wh)
			{
				if (Mathf.Abs(nw) < 0.0001f) Settings.weaponHeight.Remove(d.defName);
				else Settings.weaponHeight[d.defName] = nw;
			}
			// clothes: automatic fitting on/off, own width and height
			bool fit = !Settings.noFitRaces.Contains(d.defName);
			bool fitWas = fit;
			Widgets.CheckboxLabeled(new Rect(row.x + 4f, row.y + 54f, 220f, 24f), "BF_RaceFit".Translate(), ref fit);
			if (fit != fitWas)
			{
				if (fit) Settings.noFitRaces.Remove(d.defName);
				else Settings.noFitRaces.Add(d.defName);
				Changed();
			}
			Vector2 cs = Settings.ClothesSize(d);
			float cw = Stepper(new Rect(row.x + 240f, row.y + 54f, 220f, 24f), "BF_ClothesW".Translate(), cs.x);
			float ch = Stepper(new Rect(row.x + 476f, row.y + 54f, 220f, 24f), "BF_ClothesH".Translate(), cs.y);
			if (cw != cs.x)
			{
				if (Mathf.Abs(cw - 1f) < 0.0001f) Settings.clothesWidth.Remove(d.defName);
				else Settings.clothesWidth[d.defName] = Mathf.Clamp(cw, 0.3f, 2f);
			}
			if (ch != cs.y)
			{
				if (Mathf.Abs(ch - 1f) < 0.0001f) Settings.clothesHeight.Remove(d.defName);
				else Settings.clothesHeight[d.defName] = Mathf.Clamp(ch, 0.3f, 2f);
			}
			// 2.1: the body clothes are fitted to for this race
			Settings.raceBody.TryGetValue(d.defName, out string rb);
			Rect rbr = new Rect(row.xMax - 250f, row.y + 54f, 246f, 24f);
			if (Widgets.ButtonText(rbr, "BF_RaceBody".Translate(rb.NullOrEmpty() ? (string)"BF_Auto".Translate() : rb)))
			{
				ThingDef ld = d;
				var menu = new List<FloatMenuOption> { new FloatMenuOption("BF_Auto".Translate(), () => { Settings.raceBody.Remove(ld.defName); Refit.Clear(); Changed(); }) };
				IEnumerable<BodyTypeDef> opts = bt.Count > 0 ? bt : DefDatabase<BodyTypeDef>.AllDefsListForReading;
				foreach (BodyTypeDef o in opts)
				{
					BodyTypeDef lo = o;
					menu.Add(new FloatMenuOption(o.defName, () => { Settings.raceBody[ld.defName] = lo.defName; Refit.Clear(); Changed(); }));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			TooltipHandler.TipRegion(rbr, "BF_RaceBody_Desc".Translate());
			bool anyCloth = !fit || cs != Vector2.one || !rb.NullOrEmpty();
			if ((v != 0f || hh2 != 0f || hs != 1f || wh != 0f || anyCloth) && Widgets.ButtonText(new Rect(row.xMax - 90f, row.y + 2f, 86f, 26f), "BF_Reset".Translate()))
			{
				Settings.raceHeight.Remove(d.defName);
				Settings.hairHeight.Remove(d.defName);
				Settings.hairSize.Remove(d.defName);
				Settings.weaponHeight.Remove(d.defName);
				Settings.noFitRaces.Remove(d.defName);
				Settings.clothesWidth.Remove(d.defName);
				Settings.clothesHeight.Remove(d.defName);
				if (Settings.raceBody.Remove(d.defName))
					Refit.Clear();
				Changed();
			}
		}
		Widgets.EndScrollView();
	}

	private static float Stepper(Rect r, string label, float value)
	{
		float step = Event.current.shift ? 0.05f : 0.01f;
		Widgets.Label(new Rect(r.x, r.y + 2f, 80f, 22f), label);
		float x = r.x + 84f;
		if (Widgets.ButtonText(new Rect(x, r.y, 24f, 24f), "-")) value -= step;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(new Rect(x + 24f, r.y, 60f, 24f), value.ToString("0.00"));
		Text.Anchor = TextAnchor.UpperLeft;
		if (Widgets.ButtonText(new Rect(x + 84f, r.y, 24f, 24f), "+")) value += step;
		return Mathf.Round(Mathf.Clamp(value, -1f, 2f) * 100f) / 100f;
	}
}

// A list of everything that does not fit – written to Player.log.
public static class Report
{
	public static string Write()
	{
		BF.TraceAllowed = true;
		try
		{
			return WriteInner();
		}
		finally
		{
			BF.TraceAllowed = false;
		}
	}

	private static string WriteInner()
	{
		var sb = new StringBuilder();
		sb.AppendLine("[Body Fit] ===== report =====");
		sb.AppendLine("Refit: " + Refit.made + " clothes refitted to bodies, " + Refit.skipped + " already fitting.");
		int badBodies = 0, noHead = 0;
		foreach (BodyTypeDef b in DefDatabase<BodyTypeDef>.AllDefsListForReading)
		{
			bool tex = Bodies.TextureExists(b.bodyNakedGraphicPath);
			bool head = !HeadFix.Missing(b);
			if (!tex) badBodies++;
			if (!head) noHead++;
			sb.AppendLine($"  body {b.defName} ({b.modContentPack?.Name}) headOffset={b.headOffset} texture={(tex ? "ok" : "MISSING")} {(head ? "" : "NO HEAD OFFSET")}");
		}
		var races = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.race != null && d.race.Humanlike).ToList();
		foreach (ThingDef d in races)
		{
			var bt = Bodies.HarBodyTypes(d);
			sb.AppendLine($"  race {d.defName} ({d.modContentPack?.Name}) HAR={(Bodies.IsHar(d) ? "yes" : "no")} bodies=[{string.Join(", ", bt.Select(x => x.defName))}]");
		}
		// apparel: which body types have no picture
		var bodies = DefDatabase<BodyTypeDef>.AllDefsListForReading.Where(b => b != BodyTypeDefOf.Baby).ToList();
		var missing = bodies.ToDictionary(b => b, b => 0);
		int apparelCount = 0;
		foreach (ThingDef a in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (a.apparel == null || a.apparel.wornGraphicPath.NullOrEmpty())
				continue;
			ApparelLayerDef last = a.apparel.LastLayer;
			if (last == ApparelLayerDefOf.Overhead || last == ApparelLayerDefOf.EyeCover || a.apparel.wornGraphicData?.renderUtilityAsPack == true)
				continue;
			apparelCount++;
			foreach (BodyTypeDef b in bodies)
				if (!Bodies.TextureExists(a.apparel.wornGraphicPath + "_" + b.defName))
					missing[b]++;
		}
		sb.AppendLine($"  apparel drawn on the body: {apparelCount}");
		foreach (var kv in missing.OrderByDescending(x => x.Value))
			sb.AppendLine($"    no picture for body {kv.Key.defName}: {kv.Value}");
		sb.AppendLine($"  pawns repaired since start: {BodyRepair.repaired}");
		if (Current.Game != null && Find.Maps != null)
		{
			foreach (Map m in Find.Maps)
			{
				foreach (Pawn p in m.mapPawns.FreeColonistsSpawned)
				{
					BodyTypeDef b = p.story?.bodyType;
					string real = Patch_FitScale.WearerBodyPath(p, b);
					string headPath = HairFix.DrawnHead(p);
					Rect? ro = Silhouette.Of(real, Rot4.South);
					Rect? rv = Silhouette.Of(b?.bodyNakedGraphicPath, Rot4.South);
					sb.AppendLine($"  pawn {p.LabelShort}: race={p.def.defName} body={b?.defName} headOffset={(b != null ? HeadFix.Wanted(b, p.gender).ToString() : "-")} head={p.story?.headType?.defName}");
					sb.AppendLine($"    drawn body={real} outline={ro}  vanilla body outline={rv}  drawn head={headPath}");
					// 2.2.1: why clothes of an alien race are (not) fitted, and whether its body picture loads
					if (b != null && AlienFit.OwnBody(p, b, real))
						sb.AppendLine($"    alien body: clothes fitting={AlienFit.Of(real, b.bodyNakedGraphicPath)} (None = left as the race draws them)  body picture broken={(BodyRepair.DrawnBodyBroken(p) ? "YES" : "no")}");
					if (p.apparel != null)
						foreach (Apparel a in p.apparel.WornApparel)
						{
							ThingDef home = Bodies.HomeRace(a.def);
							string from = home != null && home != p.def ? Bodies.RaceBodyPath(home, Patch_Apparel.Substitute(a, b) ?? b) : null;
							string skip = a.def.apparel?.renderSkipFlags != null && a.def.apparel.renderSkipFlags.Count > 0 ? string.Join("+", a.def.apparel.renderSkipFlags.Select(f => f?.defName ?? "?")) : "-";
							sb.AppendLine($"    wears {a.def.defName} worn={a.WornGraphicPath} borrowed={(Patch_Apparel.Substitute(a, b)?.defName ?? "-")} madeForRace={(home?.defName ?? "-")} madeForBody={(from ?? "-")} outline={(from != null ? Silhouette.Of(from, Rot4.South)?.ToString() : "-")} hides={skip}");
						}
				}
			}
		}
		MLog.Info(sb.ToString());
		return "BF_ReportDone".Translate(DefDatabase<BodyTypeDef>.DefCount, badBodies, noHead, races.Count, apparelCount, BodyRepair.repaired);
	}
}
