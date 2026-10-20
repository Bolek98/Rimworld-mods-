using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 1.2: weapons (and things drawn with them, like shields and packs) are held
// at the height of the body the pawn really has. Weapons are placed for a
// human body; for a body whose picture sits higher or lower (alien races,
// modded bodies) the hands are elsewhere. The centre of the real body outline
// is compared with the vanilla body of the same type and the weapon follows.
// Plus your own weapon height per race (settings, tab Races).
// Note: mods that draw weapons themselves (e.g. Yayo's Combat) may skip this.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
public static class Patch_Weapons
{
	public static void Prefix(Pawn pawn, ref Vector3 drawPos, Rot4 facing)
	{
		try
		{
			if (pawn?.story?.bodyType == null || !pawn.RaceProps.Humanlike || !BF.Handles(pawn))
				return;
			BodyFitSettings s = BodyFitMod.Settings;
			float z = s.WeaponHeight(pawn.def);
			if (s.fitWeapons)
			{
				BodyTypeDef b = pawn.story.bodyType;
				string real = Patch_FitScale.WearerBodyPath(pawn, b);
				// 2.2.1: not for alien bodies that are not human-shaped (tentacles, tails …)
				if ((!AlienFit.OwnBody(pawn, b, real) || AlienFit.Of(real, b.bodyNakedGraphicPath) != AlienFit.Trust.None)
				    && Silhouette.Fit(real, b.bodyNakedGraphicPath, facing, out _, out Vector2 sh))
					z += sh.y * HumanlikeMeshPoolUtility.HumanlikeBodyWidthForPawn(pawn);
			}
			drawPos.z += z;
		}
		catch
		{
		}
	}
}
