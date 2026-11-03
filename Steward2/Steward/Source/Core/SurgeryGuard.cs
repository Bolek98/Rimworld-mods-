using System;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// 1.1.0: a backup doctor with little Medicine may tend, but leaves surgery to a better doctor
// when one is on the map, able and assigned to doctoring. Nobody better there (or a forced
// order from the player) = no change, so an emergency is never blocked.
[HarmonyPatch(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.JobOnThing))]
public static class SurgeryGuard
{
	private static int cacheTick = -1;
	private static Map cacheMap;
	private static int cacheMin = -1;
	private static bool cacheHasGood;

	public static void Postfix(Pawn pawn, bool forced, ref Job __result)
	{
		try
		{
			if (__result?.bill?.recipe == null || forced || !__result.bill.recipe.IsSurgery)
				return;
			ForemanSettings s = ForemanMod.Settings;
			int min = s?.surgeryMinMedicine ?? 0;
			if (min <= 0 || pawn?.Map == null || pawn.Faction != Faction.OfPlayer || pawn.skills == null)
				return;
			ForemanMapComponent comp = ForemanMapComponent.For(pawn.Map);
			if (comp == null || !comp.auto)
				return;
			if (pawn.skills.GetSkill(SkillDefOf.Medicine)?.Level >= min)
				return;
			if (!BetterDoctorThere(pawn.Map, min))
				return;
			__result = null;
			JobFailReason.Is("CF2_SurgeryLeft".Translate());
		}
		catch (Exception e)
		{
			MLog.Warn("[Steward] surgery guard: " + e.Message);
		}
	}

	private static bool BetterDoctorThere(Map map, int min)
	{
		int now = Find.TickManager.TicksGame;
		if (map == cacheMap && min == cacheMin && now - cacheTick < 250)
			return cacheHasGood;
		bool good = false;
		WorkTypeDef doctor = WorkTypeDefOf.Doctor;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p.Downed || p.InMentalState || p.skills == null || p.workSettings == null || !p.workSettings.EverWork)
				continue;
			if (p.WorkTypeIsDisabled(doctor) || p.workSettings.GetPriority(doctor) <= 0)
				continue;
			if ((p.skills.GetSkill(SkillDefOf.Medicine)?.Level ?? 0) >= min)
			{
				good = true;
				break;
			}
		}
		cacheMap = map;
		cacheMin = min;
		cacheTick = now;
		cacheHasGood = good;
		return good;
	}
}
