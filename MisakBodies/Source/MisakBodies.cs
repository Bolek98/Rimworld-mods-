using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakBodies;

public class MBSettings : ModSettings
{
	public float chance = 0.5f;   // share of new pawns that get a new body
	public bool showNipples, showVagina, showPenis;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref chance, "chance", 0.5f);
		Scribe_Values.Look(ref showNipples, "showNipples", false);
		Scribe_Values.Look(ref showVagina, "showVagina", false);
		Scribe_Values.Look(ref showPenis, "showPenis", false);
	}
}

public class MBMod : Mod
{
	public static MBSettings Settings;

	public MBMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<MBSettings>();
		new Harmony("bolek98.misakbodies").PatchAll();
	}

	public override string SettingsCategory() => "Mišák Bodies";

	public override void DoSettingsWindowContents(Rect inRect)
	{
		MBSettings s = Settings;
		var l = new Listing_Standard();
		l.Begin(inRect);
		l.Label("MB_Bodies".Translate());
		l.Label("MB_Chance".Translate((s.chance * 100f).ToString("0")));
		s.chance = Mathf.Round(l.Slider(s.chance, 0f, 1f) * 20f) / 20f;
		l.GapLine();
		l.Label("MB_Overlays".Translate());
		l.CheckboxLabeled("MB_Nipples".Translate(), ref s.showNipples);
		l.CheckboxLabeled("MB_Vagina".Translate(), ref s.showVagina);
		l.CheckboxLabeled("MB_Penis".Translate(), ref s.showPenis);
		l.Gap();
		l.Label("MB_OverlayNote".Translate());
		l.End();
	}

	public override void WriteSettings()
	{
		base.WriteSettings();
		OverlayHide.Clear();
		Bodies.RedrawAll();
	}
}

[StaticConstructorOnStartup]
public static class Bodies
{
	// new body type -> the existing body type whose clothes it wears
	public static readonly Dictionary<BodyTypeDef, BodyTypeDef> Base = new Dictionary<BodyTypeDef, BodyTypeDef>();
	public static BodyTypeDef Z1, Z2, M1, M2;

	static Bodies()
	{
		try
		{
			BodyTypeDef bb = DefDatabase<BodyTypeDef>.GetNamedSilentFail("FemaleBB");
			Z1 = Make("MB_Z1", bb ?? BodyTypeDefOf.Female, "MisakBodies/Naked_FemaleBB");
			Z2 = Make("MB_Z2", BodyTypeDefOf.Female, "MisakBodies/Naked_Female");
			M1 = Make("MB_M1", BodyTypeDefOf.Hulk, "MisakBodies/Naked_Hulk");
			M2 = Make("MB_M2", BodyTypeDefOf.Male, "MisakBodies/Naked_Male");
			Log.Message("[Mišák Bodies] v0.2.0 loaded – 4 new body types" + (bb == null ? " (no FemaleBB: F1 wears Female clothes)." : "."));
		}
		catch (Exception e)
		{
			Log.Error("[Mišák Bodies] setup failed: " + e);
		}
	}

	private static BodyTypeDef Make(string defName, BodyTypeDef from, string path)
	{
		BodyTypeDef d = DefDatabase<BodyTypeDef>.GetNamedSilentFail(defName);
		if (d == null)
		{
			d = (BodyTypeDef)typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(from, null);
			d.defName = defName;
			d.shortHash = 0;
			d.generated = true;
			d.modContentPack = LoadedModManager.GetMod<MBMod>()?.Content;
			d.ResolveDefNameHash();
			DefDatabase<BodyTypeDef>.Add(d);
		}
		d.label = (defName + "_Label").Translate();
		d.description = (defName + "_Desc").Translate();
		d.bodyNakedGraphicPath = path;
		Base[d] = from;
		return d;
	}

	public static bool IsOurs(BodyTypeDef b) => b != null && Base.ContainsKey(b);

	// races that draw the game's own bodies (not HAR races, which have their own)
	public static bool VanillaBodies(Pawn p) =>
		p?.RaceProps != null && p.RaceProps.Humanlike && p.def.GetType().Name != "ThingDef_AlienRace";

	public static void RedrawAll()
	{
		try
		{
			if (Current.Game == null || Find.Maps == null)
				return;
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
					if (p.RaceProps.Humanlike)
					{
						p.Drawer?.renderer?.SetAllGraphicsDirty();
						PortraitsCache.SetDirty(p);
					}
		}
		catch
		{
		}
	}
}

// overlays of other mods (Sized Apparel, RJW addons …) recognised by their texture path or node name
[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.CanDrawNow))]
public static class OverlayHide
{
	private sealed class Box
	{
		public Graphic g;
		public int kind = -1; // 0 none, 1 nipples, 2 vagina, 3 penis
	}

	private static ConditionalWeakTable<PawnRenderNode, Box> cache = new ConditionalWeakTable<PawnRenderNode, Box>();

	public static void Clear() => cache = new ConditionalWeakTable<PawnRenderNode, Box>();

	public static void Postfix(PawnRenderNode node, ref bool __result)
	{
		if (!__result || node == null)
			return;
		MBSettings s = MBMod.Settings;
		if (s.showNipples && s.showVagina && s.showPenis)
			return;
		Box b = cache.GetOrCreateValue(node);
		Graphic g = node.PrimaryGraphic;
		if (b.g != g || b.kind < 0)
		{
			b.g = g;
			b.kind = Kind(node, g);
		}
		if ((b.kind == 1 && !s.showNipples) || (b.kind == 2 && !s.showVagina) || (b.kind == 3 && !s.showPenis))
			__result = false;
	}

	private static int Kind(PawnRenderNode node, Graphic g)
	{
		try
		{
			string t = ((g?.path ?? "") + "|" + (node.Props?.texPath ?? "") + "|" + (node.Props?.debugLabel ?? "") + "|" + node.GetType().Name).ToLowerInvariant();
			if (t.Contains("nipple"))
				return 1;
			if (t.Contains("vagina") || t.Contains("pussy") || t.Contains("vulva"))
				return 2;
			if (t.Contains("penis") || t.Contains("cock") || t.Contains("dick"))
				return 3;
		}
		catch
		{
		}
		return 0;
	}
}

// clothes: a new body wears the clothes of its base body (Body Fit then fits them to the new shape)
[HarmonyPatch(typeof(ApparelGraphicRecordGetter), nameof(ApparelGraphicRecordGetter.TryGetGraphicApparel))]
public static class ApparelBase
{
	[HarmonyPriority(1000)]
	public static void Prefix(ref BodyTypeDef bodyType)
	{
		if (bodyType != null && Bodies.Base.TryGetValue(bodyType, out BodyTypeDef b))
			bodyType = b;
	}
}

// new pawns get a new body with the chance from the settings
[HarmonyPatch(typeof(PawnGenerator), "GenerateBodyType")]
public static class NewPawns
{
	public static void Postfix(Pawn pawn, PawnGenerationRequest request)
	{
		try
		{
			if (request.ForceBodyType != null || !Bodies.VanillaBodies(pawn) || pawn.story == null || !Rand.Chance(MBMod.Settings.chance))
				return;
			BodyTypeDef b = pawn.story.bodyType;
			if (b == BodyTypeDefOf.Female || b?.defName == "FemaleBB")
				pawn.story.bodyType = Rand.Bool ? Bodies.Z1 : Bodies.Z2;
			else if (b == BodyTypeDefOf.Male)
				pawn.story.bodyType = Bodies.M2;
			else if (b == BodyTypeDefOf.Hulk)
				pawn.story.bodyType = Bodies.M1;
		}
		catch
		{
		}
	}
}
