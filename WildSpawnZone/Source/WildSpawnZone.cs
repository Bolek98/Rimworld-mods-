using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WildSpawnZone;

// ---------------------------------------------------------------------------
// Wild Spawn Zone: paint an area (Zone menu) – wild animals that appear on
// their own (the game's normal wildlife spawning, also birds flying in) then
// appear only inside it. With an empty area the game works as usual.
// Events (migrations, manhunters, thrumbos …) are not changed.
// ---------------------------------------------------------------------------
public class WildSpawnSettings : ModSettings
{
	public bool enabled = true;
	public bool avoidHome = true;       // never inside the home area
	public bool birdsAnywhere;          // flying animals may still land anywhere
	public float density = 1f;          // 1.1.0: how many wild animals live on the map
	public bool recommended = true;     // missing key = off, so old settings stay

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref avoidHome, "avoidHome", true);
		Scribe_Values.Look(ref birdsAnywhere, "birdsAnywhere", false);
		Scribe_Values.Look(ref density, "density", 1f);
		Scribe_Values.Look(ref recommended, "recommended", false);
	}
}

public class WildSpawnZoneMod : Mod
{
	public const string Version = "1.1.0";
	public static WildSpawnSettings Settings;
	private static List<string> recBullets;

	public WildSpawnZoneMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<WildSpawnSettings>();
		MUI.Init(new WildSpawnSettings(), null, "recommended");
		if (Settings.recommended)
			MUI.ApplyAll(Settings);
		try
		{
			var h = new Harmony("bolek98.wildspawnzone");
			MUI.PatchWindow(h, typeof(WildSpawnZoneMod));
			var m = AccessTools.Method(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.SpawnRandomWildAnimalAt));
			if (m == null)
			{
				Log.Error("[Wild Spawn Zone] WildAnimalSpawner.SpawnRandomWildAnimalAt not found – mod inactive.");
				return;
			}
			h.Patch(m, prefix: new HarmonyMethod(typeof(SpawnPatch), nameof(SpawnPatch.Prefix)));
			var d = AccessTools.PropertyGetter(typeof(WildAnimalSpawner), "DesiredAnimalDensity");
			if (d != null)
				h.Patch(d, postfix: new HarmonyMethod(typeof(SpawnPatch), nameof(SpawnPatch.DensityPostfix)));
			Log.Message("[Wild Spawn Zone] v" + Version + " loaded.");
		}
		catch (Exception ex)
		{
			Log.Error("[Wild Spawn Zone] patch failed: " + ex);
		}
	}

	public override string SettingsCategory() => "Wild Spawn Zone";

	private static void Check(Listing_Standard l, string key, ref bool value, string field)
	{
		MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref value, Settings, field);
	}

	public override void DoSettingsWindowContents(Rect r)
	{
		WildSpawnSettings s = Settings;
		MUI.Version(r, "v" + Version + " · Bolek98");
		if (recBullets == null)
		{
			recBullets = new List<string>();
			for (int i = 1; i <= 3; i++)
				recBullets.Add(("WSZ_Rec" + i).Translate());
		}
		float y0 = r.y;
		y0 += MUI.RecommendedCard(new Rect(r.x, y0, r.width, 0f), s, s.recommended, v =>
		{
			s.recommended = v;
			if (v) MUI.ApplyAll(s);
		}, recBullets) + 6f;
		if (s.recommended)
			y0 += MUI.LockNote(new Rect(r.x, y0, r.width, 0f)) + 4f;
		Rect top = new Rect(r.x, y0, r.width, 260f);
		var l = new Listing_Standard();
		MUI.BeginLock(s.recommended);
		try
		{
			l.Begin(top);
			l.ColumnWidth = (top.width - 24f) / 2f;
			MUI.Section(l, "WSZ_SecZone".Translate());
			Check(l, "WSZ_Enabled", ref s.enabled, "enabled");
			Check(l, "WSZ_AvoidHome", ref s.avoidHome, "avoidHome");
			Check(l, "WSZ_Birds", ref s.birdsAnywhere, "birdsAnywhere");
			l.NewColumn();
			MUI.Section(l, "WSZ_SecAnimals".Translate());
			s.density = Mathf.Round(MUI.Slider(l, "WSZ_Density".Translate(Mathf.RoundToInt(s.density * 100f)), s.density, 0f, 3f, "WSZ_Density_Desc".Translate(), s, "density") * 20f) / 20f;
			l.End();
		}
		finally
		{
			MUI.EndLock(top);
		}
		float y = top.yMax + 8f;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, y, r.width, 60f), "WSZ_HowTo".Translate());
		GUI.color = Color.white;
	}
}

public class Area_WildSpawn : Area
{
	public override string Label => "WSZ_Area".Translate();
	public override Color Color => new Color(0.35f, 0.8f, 0.3f);
	public override int ListPriority => 3000;

	public Area_WildSpawn() { }
	public Area_WildSpawn(AreaManager areaManager) : base(areaManager) { }

	public override string GetUniqueLoadID() => "Area_" + ID + "_WildSpawn";

	public static Area_WildSpawn For(Map map, bool create)
	{
		if (map?.areaManager == null)
			return null;
		Area_WildSpawn a = map.areaManager.Get<Area_WildSpawn>();
		if (a == null && create)
		{
			a = new Area_WildSpawn(map.areaManager);
			map.areaManager.AllAreas.Add(a);
		}
		return a;
	}
}

public static class SpawnPatch
{
	private static readonly AccessTools.FieldRef<WildAnimalSpawner, Map> mapRef = AccessTools.FieldRefAccess<WildAnimalSpawner, Map>("map");

	public static void Prefix(WildAnimalSpawner __instance, ref IntVec3 loc, ref bool canFlyIn)
	{
		try
		{
			WildSpawnSettings s = WildSpawnZoneMod.Settings;
			if (s != null && !s.enabled)
				return;
			Map map = mapRef(__instance);
			if (map == null || MapGenerator.mapBeingGenerated == map)
				return;
			Area_WildSpawn a = Area_WildSpawn.For(map, false);
			if (a == null || a.TrueCount == 0)
				return;
			if (canFlyIn && s != null && s.birdsAnywhere)
				return;
			// 1.0.1: never inside the home area – animals spawned there walk around the base
			Area_Home home = (s == null || s.avoidHome) ? map.areaManager.Home : null;
			if (!a.ActiveCells.Where(c => c.Standable(map) && !c.Fogged(map) && (home == null || !home[c])).TryRandomElement(out IntVec3 cell))
				return;
			loc = cell;
			canFlyIn = false;   // a flying animal would pick its own landing cell
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Wild Spawn Zone] " + ex.Message, 0x3c51a7);
		}
	}

	// 1.1.0: more or fewer wild animals (also the most the map can hold)
	public static void DensityPostfix(ref float __result)
	{
		WildSpawnSettings s = WildSpawnZoneMod.Settings;
		if (s != null && s.enabled && !Mathf.Approximately(s.density, 1f))
			__result *= Mathf.Max(0f, s.density);
	}
}

public abstract class Designator_WildSpawn : Designator_Cells
{
	private readonly bool add;

	public override bool DragDrawMeasurements => true;
	public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.Areas;

	protected Designator_WildSpawn(bool add)
	{
		this.add = add;
		useMouseIcon = true;
		soundDragSustain = add ? SoundDefOf.Designate_DragAreaAdd : SoundDefOf.Designate_DragAreaDelete;
		soundDragChanged = add ? SoundDefOf.Designate_DragZone_Changed : null;
		soundSucceeded = add ? SoundDefOf.Designate_ZoneAdd : SoundDefOf.Designate_ZoneDelete;
		icon = ContentFinder<Texture2D>.Get(add ? "UI/Designators/HomeAreaOn" : "UI/Designators/HomeAreaOff");
		defaultIconColor = new Color(0.45f, 0.9f, 0.4f);
	}

	public override AcceptanceReport CanDesignateCell(IntVec3 c)
	{
		if (!c.InBounds(Map))
			return false;
		Area_WildSpawn a = Area_WildSpawn.For(Map, false);
		bool on = a != null && a[c];
		return add ? !on : on;
	}

	public override void DesignateSingleCell(IntVec3 c)
	{
		Area_WildSpawn a = Area_WildSpawn.For(Map, add);
		if (a != null)
			a[c] = add;
	}

	public override void SelectedUpdate()
	{
		GenUI.RenderMouseoverBracket();
		Area_WildSpawn.For(Map, false)?.MarkForDraw();
	}
}

public class Designator_WildSpawnExpand : Designator_WildSpawn
{
	public Designator_WildSpawnExpand() : base(true)
	{
		defaultLabel = "WSZ_Expand".Translate();
		defaultDesc = "WSZ_ExpandDesc".Translate();
	}
}

public class Designator_WildSpawnClear : Designator_WildSpawn
{
	public Designator_WildSpawnClear() : base(false)
	{
		defaultLabel = "WSZ_Clear".Translate();
		defaultDesc = "WSZ_ClearDesc".Translate();
	}
}
