using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace ResearchLockedRaidGear
{
	// 2.16.0: raiders' implants and drugs follow the world level, and (optional) research above the
	// world level costs more, below it less.
	public static class RaiderImplants
	{
		private static bool Hostile(Pawn pawn)
		{
			return pawn != null && pawn.Faction != null && Faction.OfPlayer != null && pawn.Faction.HostileTo(Faction.OfPlayer) && ResearchLockedGearFilter.ShouldFilter(pawn);
		}

		private static bool AboveWorld(ThingDef def)
		{
			if (def == null) return false;
			TechLevel tl = def.techLevel;
			if (tl == TechLevel.Undefined || tl == TechLevel.Animal) return false;
			return (int)tl > (int)ResearchLockedGearFilter.TechLimit(ResearchLockedRaidGearMod.Settings);
		}

		// bionics, archotech parts and other tech implants above the world level are simply not installed
		// (patched before the part is installed – nothing is cut off, so no missing body parts)
		public static bool InstallPartPrefix(Pawn pawn, ThingDef partDef)
		{
			try
			{
				ResearchLockedRaidGearSettings s = ResearchLockedRaidGearMod.Settings;
				if (s == null || !s.filterImplants || !Hostile(pawn) || !AboveWorld(partDef))
				{
					return true;
				}
				GearLog.NoteImplant(pawn, partDef);
				return false;
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[World at Your Level] implant filter failed: " + ex.Message, 0x57a11a);
				return true;
			}
		}

		// go-juice, yayo and other drugs above the world level are taken out of a raider's pockets right
		// after his inventory is made (so he cannot take them before the fight)
		public static void InventoryPostfix(Pawn p)
		{
			try
			{
				ResearchLockedRaidGearSettings s = ResearchLockedRaidGearMod.Settings;
				if (s == null || !s.filterRaiderDrugs || p?.inventory == null || !Hostile(p))
				{
					return;
				}
				List<Thing> list = p.inventory.innerContainer.InnerListForReading;
				for (int i = list.Count - 1; i >= 0; i--)
				{
					Thing t = list[i];
					if (t?.def != null && t.def.IsDrug && AboveWorld(t.def))
					{
						p.inventory.innerContainer.Remove(t);
						t.Destroy();
					}
				}
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[World at Your Level] drug filter failed: " + ex.Message, 0x57a11b);
			}
		}
	}

	public static class ResearchCostByLevel
	{
		[ThreadStatic]
		private static bool busy;

		// multiplies the game's cost factor (used for the shown cost and progress and for research speed)
		public static void CostFactorPostfix(ResearchProjectDef __instance, ref float __result)
		{
			ResearchLockedRaidGearSettings s = ResearchLockedRaidGearMod.Settings;
			if (busy || s == null || !s.researchCostByLevel || !s.enabled || __instance == null || Current.ProgramState != ProgramState.Playing || Current.Game == null)
			{
				return;
			}
			if (!ResearchLockedGearFilter.CountsForTier(__instance))
			{
				return;
			}
			busy = true;
			try
			{
				int level = (int)WorldTech.Level();
				int tl = (int)__instance.techLevel;
				if (tl > level)
				{
					__result *= 1f + s.researchAbovePercent / 100f;
				}
				else if (tl < level)
				{
					__result *= Math.Max(0.1f, 1f - s.researchBelowPercent / 100f);
				}
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[World at Your Level] research cost failed: " + ex.Message, 0x57a11c);
			}
			finally
			{
				busy = false;
			}
		}
	}
}
