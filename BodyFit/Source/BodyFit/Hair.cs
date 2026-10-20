using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 5. Hair (and beards).
//   * A hair style with no picture (removed mod, broken path) would be a pink
//     square: the pawn gets a short vanilla hair picture for drawing instead.
//     The pawn's hair style itself is not changed.
//   * Fit hair to the head (optional): hair pictures are made for human heads.
//     The outline of the head really drawn (alien races too) is traced and
//     compared with a human head; hair is stretched and moved to cover it.
//   * Your own hair height and size per race (settings, tab Races).
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderNode_Hair), nameof(PawnRenderNode_Hair.GraphicFor))]
public static class Patch_HairGraphic
{
	public static bool Prefix(PawnRenderNode_Hair __instance, Pawn pawn, ref Graphic __result)
	{
		if (!BodyFitMod.Settings.fixHair || !BF.Handles(pawn))
			return true;
		try
		{
			HairDef h = pawn?.story?.hairDef;
			if (h == null || h.noGraphic || h.texPath.NullOrEmpty())
				return true;
			bool wasNotReady = BF.NotReady;
			BF.NotReady = false;
			bool exists = Bodies.TextureExists(h.texPath);
			bool guessed = BF.NotReady;
			BF.NotReady = wasNotReady || guessed;
			if (exists || guessed)
				return true;   // 1.2.3: not known yet (worker thread) – leave it to the game
			if (pawn.DevelopmentalStage.Baby() || pawn.DevelopmentalStage.Newborn())
				return true;
			HairDef fb = HairFix.Fallback(pawn.gender);
			if (fb == null)
				return true;
			__result = fb.GraphicFor(pawn, __instance.ColorFor(pawn));
			return false;
		}
		catch
		{
			return true;
		}
	}
}

public static class HairFix
{
	private static HairDef male, female;

	public static void Clear()
	{
		male = female = null;
	}

	public static HairDef Fallback(Gender g)
	{
		HairDef m = male, f = female;
		if (m == null)
		{
			// 1.2.3: only decided on the main thread, where hair pictures can be checked
			if (!BF.Main)
				return null;
			m = Find("Short") ?? Find("Shaved");
			f = Find("Bob") ?? Find("Long") ?? m;
			female = f;
			male = m;
		}
		return g == Gender.Female ? f : m;
	}

	private static HairDef Find(string name)
	{
		HairDef d = DefDatabase<HairDef>.GetNamedSilentFail(name);
		return d != null && Bodies.TextureExists(d.texPath) ? d : null;
	}

	public static bool IsHairNode(PawnRenderNode n) => n is PawnRenderNode_Hair || n is PawnRenderNode_Beard;

	// 1.2: hats, helmets, masks … drawn on the head
	public static bool IsHeadgear(PawnRenderNode n) => n is PawnRenderNode_Apparel a && a.useHeadMesh && a.apparel != null;

	private static readonly Dictionary<Gender, string> refHead = new Dictionary<Gender, string>();

	// a human head of the same gender to compare with
	public static string ReferenceHead(Gender g)
	{
		string p;
		lock (BF.Gate)
			if (refHead.TryGetValue(g, out p))
				return p;
		HeadTypeDef h = DefDatabase<HeadTypeDef>.GetNamedSilentFail(g == Gender.Female ? "Female_AverageNormal" : "Male_AverageNormal");
		p = h?.graphicPath;
		lock (BF.Gate)
			refHead[g] = p;
		return p;
	}

	public static string DrawnHead(Pawn p)
	{
		try { return p?.Drawer?.renderer?.HeadGraphic?.path; } catch { return null; }
	}

	public static bool AutoFit(Pawn p, Rot4 rot, out Vector2 scale, out Vector2 shift, bool headgear = false)
	{
		scale = Vector2.one;
		shift = Vector2.zero;
		if (p == null || Refit.Suspended || !(headgear ? BodyFitMod.Settings.fitHeadgear : BodyFitMod.Settings.fitHair))
			return false;
		string head = DrawnHead(p);
		// human heads need nothing
		if (head.NullOrEmpty() || head.StartsWith("Things/Pawn/Humanlike/Heads/"))
			return false;
		return Silhouette.Fit(head, ReferenceHead(p.gender), rot, out scale, out shift);
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.ScaleFor))]
public static class Patch_HairScale
{
	public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Vector3 __result)
	{
		if (!BF.Handles(parms.pawn))
			return;
		bool hat = HairFix.IsHeadgear(node);
		if (!hat && !HairFix.IsHairNode(node))
			return;
		try
		{
			Pawn p = parms.pawn;
			float size = hat ? 1f : BodyFitMod.Settings.HairSize(p?.def);
			// 2.1: a refitted picture already follows the head
			if (!Refit.IsRefit(node.PrimaryGraphic) && HairFix.AutoFit(p, parms.facing, out Vector2 sc, out _, hat))
			{
				__result.x *= sc.x;
				__result.z *= sc.y;
			}
			if (size != 1f)
			{
				__result.x *= size;
				__result.z *= size;
			}
		}
		catch
		{
		}
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.OffsetFor))]
public static class Patch_HairOffset
{
	public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Vector3 __result)
	{
		if (!BF.Handles(parms.pawn))
			return;
		bool hat = HairFix.IsHeadgear(node);
		if (!hat && !HairFix.IsHairNode(node))
			return;
		try
		{
			Pawn p = parms.pawn;
			// hats sit where the hair sits
			__result.z += BodyFitMod.Settings.HairHeight(p?.def);
			if (!Refit.IsRefit(node.PrimaryGraphic) && HairFix.AutoFit(p, parms.facing, out _, out Vector2 sh, hat))
			{
				float size = HumanlikeMeshPoolUtility.HumanlikeHeadWidthForPawn(p);
				float x = sh.x * size;
				if (parms.facing == Rot4.West)
					x = -x;
				__result.x += x;
				__result.z += sh.y * size;
			}
		}
		catch
		{
		}
	}
}
