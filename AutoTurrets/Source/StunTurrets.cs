using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoTurrets
{
	// ---------------------------------------------------------------------------
	// 1.2.0: stunning (non-lethal) turrets of yours also shoot at your own prisoners and slaves who
	// break out, rebel, flee or go berserk. The game never lets a turret aim at a prisoner and
	// does not count rebelling slaves as enemies, so stun turrets just watched them run.
	// Only turrets whose shot does no lasting harm (stun, EMP-like, or a def named stun / taser /
	// non-lethal) – lethal turrets keep leaving them to your colonists.
	// ---------------------------------------------------------------------------
	public static class StunTurrets
	{
		private static readonly Dictionary<ThingDef, bool> stunCache = new Dictionary<ThingDef, bool>();

		public static bool On => ATMod.S == null || ATMod.S.stunRunaways;

		// a prisoner or slave of ours who is running or fighting
		public static bool Runaway(Pawn p)
		{
			if (p == null || p.Dead || p.Downed || !p.Spawned)
				return false;
			bool prisoner = p.IsPrisonerOfColony, slave = p.IsSlaveOfColony;
			if (!prisoner && !slave)
				return false;
			try
			{
				if (prisoner && PrisonBreakUtility.IsPrisonBreaking(p))
					return true;
				if (slave && ModsConfig.IdeologyActive && SlaveRebellionUtility.IsRebelling(p))
					return true;
			}
			catch { }
			if (p.InAggroMentalState)
				return true;
			// fleeing the colony (mental break "run away", slave escape …)
			MentalStateDef ms = p.MentalStateDef;
			if (ms != null && (ms.defName.IndexOf("Run", StringComparison.OrdinalIgnoreCase) >= 0 || ms.defName.IndexOf("Escap", StringComparison.OrdinalIgnoreCase) >= 0 || ms.defName.IndexOf("Flee", StringComparison.OrdinalIgnoreCase) >= 0))
				return true;
			JobDef j = p.CurJobDef;
			if (j != null && (j.defName.IndexOf("Escape", StringComparison.OrdinalIgnoreCase) >= 0 || j.defName == "PrisonerEscape"))
				return true;
			return false;
		}

		public static bool IsStunTurret(Building_TurretGun t)
		{
			ThingDef def = t?.def;
			if (def == null)
				return false;
			if (stunCache.TryGetValue(def, out bool v))
				return v;
			v = false;
			try
			{
				if (Named(def.defName) || Named(t.gun?.def?.defName))
					v = true;
				else
				{
					ThingDef proj = t.AttackVerb?.GetProjectile();
					DamageDef dd = proj?.projectile?.damageDef;
					if (dd != null && (dd == DamageDefOf.Stun || !dd.harmsHealth || Named(dd.defName) || Named(proj.defName)))
						v = true;
				}
			}
			catch { }
			stunCache[def] = v;
			return v;
		}

		private static bool Named(string n) =>
			n != null && (n.IndexOf("Stun", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Taser", StringComparison.OrdinalIgnoreCase) >= 0
			              || n.IndexOf("NonLethal", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Non_Lethal", StringComparison.OrdinalIgnoreCase) >= 0
			              || n.IndexOf("Shock", StringComparison.OrdinalIgnoreCase) >= 0);

		public static Pawn FindRunaway(Building_TurretGun t)
		{
			Map map = t.Map;
			Verb verb = t.AttackVerb;
			if (map == null || verb == null)
				return null;
			Pawn best = null;
			float bestD = float.MaxValue;
			IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				if (!Runaway(p) || p.stances?.stunner?.Stunned == true)
					continue;
				float d = p.Position.DistanceToSquared(t.Position);
				if (d >= bestD)
					continue;
				try
				{
					if (!verb.CanHitTarget(p))
						continue;
				}
				catch { continue; }
				best = p;
				bestD = d;
			}
			return best;
		}
	}

	[HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget))]
	public static class Patch_StunRunaways
	{
		public static void Postfix(Building_TurretGun __instance, ref LocalTargetInfo __result)
		{
			try
			{
				if (__result.IsValid || !StunTurrets.On || __instance.Faction != Faction.OfPlayer || TurretPage.HoldsFire(__instance) || !StunTurrets.IsStunTurret(__instance))
					return;
				Pawn p = StunTurrets.FindRunaway(__instance);
				if (p != null)
					__result = p;
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] stun turrets: " + e.Message, 0x5a71b0); }
		}
	}
}
