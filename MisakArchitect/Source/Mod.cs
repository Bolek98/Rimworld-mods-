using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakArchitect;

public class MASettings : ModSettings
{
	public bool enabled = true;
	public float height = 380f;
	public float width = 0f;     // 0.2.0: 0 = automatic
	public bool byMod, showLocked;
	public bool showInfo = true;
	public List<string> favorites = new List<string>();
	public List<string> recent = new List<string>();
	public string lastCategory;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref height, "height", 380f);
		Scribe_Values.Look(ref width, "width", 0f);
		Scribe_Values.Look(ref byMod, "byMod", false);
		Scribe_Values.Look(ref showLocked, "showLocked", false);
		Scribe_Values.Look(ref showInfo, "showInfo", true);
		Scribe_Values.Look(ref lastCategory, "lastCategory");
		Scribe_Collections.Look(ref favorites, "favorites", LookMode.Value);
		Scribe_Collections.Look(ref recent, "recent", LookMode.Value);
		favorites ??= new List<string>();
		recent ??= new List<string>();
	}
}

public class MisakArchitectMod : Mod
{
	public static MASettings S;
	public static MisakArchitectMod Instance;
	public const string Version = "0.2.1";

	public MisakArchitectMod(ModContentPack content) : base(content)
	{
		Instance = this;
		S = GetSettings<MASettings>();
		var h = new Harmony("bolek98.misakarchitect");
		h.PatchAll();
		MNews.Register(content, Version);
		Log.Message("[Clear Architect] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Clear Architect";

	public override void DoSettingsWindowContents(Rect r)
	{
		MUI.Version(r, "v" + Version + " · Bolek98");
		var l = new Listing_Standard();
		l.Begin(new Rect(r.x, r.y, Mathf.Min(r.width, 640f), r.height));
		bool was = S.enabled;
		l.CheckboxLabeled("MA_Enabled".Translate(), ref S.enabled, "MA_Enabled_Desc".Translate());
		if (was != S.enabled) ArchitectSwap.Apply();
		l.CheckboxLabeled("MA_ShowInfo".Translate(), ref S.showInfo, "MA_ShowInfo_Desc".Translate());
		l.Label("MA_Height".Translate(Mathf.RoundToInt(S.height)));
		S.height = Mathf.Round(l.Slider(S.height, 260f, 640f) / 10f) * 10f;
		if (l.ButtonText("MA_ResetSize".Translate())) { S.width = 0f; S.height = 380f; }
		l.Gap();
		if (l.ButtonText("MA_ClearRecent".Translate()))
			S.recent.Clear();
		if (l.ButtonText("MA_ClearFav".Translate()))
			S.favorites.Clear();
		l.End();
	}

	public static void Save()
	{
		try { Instance?.WriteSettings(); } catch { }
	}
}

// the architect button opens our window instead of the game's (ours is a subclass, so code that expects
// the game's window keeps working)
[StaticConstructorOnStartup]
public static class ArchitectSwap
{
	private static Type vanilla;

	static ArchitectSwap() => Apply();

	public static void Apply()
	{
		try
		{
			MainButtonDef def = MainButtonDefOf.Architect;
			if (def == null) return;
			vanilla ??= def.tabWindowClass;
			Type want = MisakArchitectMod.S.enabled ? typeof(MainTabWindow_MisakArchitect) : vanilla;
			if (def.tabWindowClass == want) return;
			if (Current.ProgramState == ProgramState.Playing && def.TabWindow != null && def.TabWindow.IsOpen)
				Find.WindowStack.TryRemove(def.TabWindow, false);
			def.tabWindowClass = want;
			AccessTools.Field(typeof(MainButtonDef), "tabWindowInt").SetValue(def, null);
		}
		catch (Exception e) { Log.Error("[Clear Architect] " + e); }
	}
}

// remembers what was built lately
[HarmonyPatch(typeof(DesignatorManager), nameof(DesignatorManager.Select))]
public static class Patch_Select
{
	public static void Postfix(Designator des)
	{
		if (des == null || MisakArchitectMod.S == null) return;
		string k = Catalog.Key(des);
		if (k == null) return;
		var r = MisakArchitectMod.S.recent;
		r.Remove(k);
		r.Insert(0, k);
		if (r.Count > 24) r.RemoveRange(24, r.Count - 24);
		MainTabWindow_MisakArchitect.dirty = true;
	}
}

// all designators of all categories
public static class Catalog
{
	public class Entry
	{
		public Designator des;
		public DesignationCategoryDef cat;
		public string key;
		public string mod;
		public string search;
	}

	public static List<DesignationCategoryDef> cats = new List<DesignationCategoryDef>();
	public static Dictionary<DesignationCategoryDef, List<Designator>> grid = new Dictionary<DesignationCategoryDef, List<Designator>>();
	public static List<Entry> flat = new List<Entry>();
	public static Dictionary<string, Entry> byKey = new Dictionary<string, Entry>();

	public static string Key(Designator d)
	{
		if (d == null) return null;
		if (d is Designator_Build b) return b.PlacingDef != null ? "B:" + b.PlacingDef.defName : null;
		if (d is Designator_Dropdown) return null;
		return "D:" + d.GetType().FullName + ":" + d.defaultLabel;
	}

	public static string ModOf(Designator d)
	{
		if (d is Designator_Build b) return b.PlacingDef?.modContentPack?.Name;
		if (d is Designator_Dropdown dd) return dd.Elements.Count > 0 ? ModOf(dd.Elements[0]) : null;
		return null;
	}

	public static void Rebuild()
	{
		cats.Clear(); grid.Clear(); flat.Clear(); byKey.Clear();
		foreach (DesignationCategoryDef c in DefDatabase<DesignationCategoryDef>.AllDefs.OrderByDescending(c => c.order))
		{
			List<Designator> list;
			try { list = c.ResolvedAllowedDesignators.ToList(); }
			catch { continue; }
			cats.Add(c);
			grid[c] = list;
			foreach (Designator d in list)
			{
				if (d is Designator_Dropdown dd)
					foreach (Designator e in dd.Elements) Add(e, c);
				else
					Add(d, c);
			}
		}
	}

	private static void Add(Designator d, DesignationCategoryDef c)
	{
		string k = Key(d);
		if (k == null || byKey.ContainsKey(k)) return;
		string mod = ModOf(d);
		var e = new Entry
		{
			des = d, cat = c, key = k, mod = mod,
			search = (d.Label + " " + (d is Designator_Build b ? b.PlacingDef?.defName : "") + " " + mod + " " + c.label).ToLowerInvariant()
		};
		flat.Add(e);
		byKey[k] = e;
	}

	public static bool Shown(Designator d)
	{
		try { return d.Visible; }
		catch { return false; }
	}
}
