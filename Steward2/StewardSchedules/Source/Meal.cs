using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.1.0: a "meal" hour (orange). Colonists eat in it when they are not full, so meals come at
// regular times instead of in the middle of work; otherwise the hour works like "anything".
public static class MealHour
{
	private static TimeAssignmentDef def;

	public static TimeAssignmentDef Def => def ??= DefDatabase<TimeAssignmentDef>.GetNamedSilentFail("CF_Meal");

	public static bool IsMealNow(Pawn p)
	{
		if (p?.timetable == null || Def == null || !p.IsColonist)
			return false;
		try { return p.timetable.GetAssignment(GenLocalDate.HourOfDay(p)) == Def; }
		catch { return false; }
	}
}

// for the game a meal hour is "anything" (work, recreation and sleep rules stay as they are)
[HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.CurrentAssignment), MethodType.Getter)]
public static class MealAsAnything
{
	public static void Postfix(ref TimeAssignmentDef __result)
	{
		if (__result != null && __result == MealHour.Def)
			__result = TimeAssignmentDefOf.Anything;
	}
}

// in a meal hour the colonist goes to eat already when a bit hungry (below 85 %)
[HarmonyPatch(typeof(JobGiver_GetFood), nameof(JobGiver_GetFood.GetPriority))]
public static class MealEatEarly
{
	public static void Postfix(Pawn pawn, ref float __result)
	{
		try
		{
			if (__result > 0f || pawn?.needs?.food == null || pawn.Drafted || !MealHour.IsMealNow(pawn))
				return;
			if (pawn.needs.food.CurLevelPercentage < 0.85f && !FoodUtility.ShouldBeFedBySomeone(pawn))
				__result = 9.5f;
		}
		catch { }
	}
}

// the GetFood job giver itself refuses when the pawn is not hungry enough – in a meal hour it may
[HarmonyPatch(typeof(JobGiver_GetFood), "TryGiveJob")]
public static class MealEatEarlyJob
{
	private static readonly AccessTools.FieldRef<JobGiver_GetFood, HungerCategory> minCat = AccessTools.FieldRefAccess<JobGiver_GetFood, HungerCategory>("minCategory");

	public static void Prefix(JobGiver_GetFood __instance, Pawn pawn, out HungerCategory __state)
	{
		__state = minCat(__instance);
		try
		{
			if (__state != HungerCategory.Fed && MealHour.IsMealNow(pawn) && pawn.needs?.food != null && pawn.needs.food.CurLevelPercentage < 0.85f)
				minCat(__instance) = HungerCategory.Fed;
		}
		catch { }
	}

	public static void Finalizer(JobGiver_GetFood __instance, HungerCategory __state) => minCat(__instance) = __state;
}

// the orange meal box next to the game's four in the Schedule tab, and the calendar button under them
[HarmonyPatch(typeof(MainTabWindow_Schedule), nameof(MainTabWindow_Schedule.DoWindowContents))]
public static class ScheduleTabExtras
{
	private static readonly Action<Rect, TimeAssignmentDef> drawFor =
		AccessTools.MethodDelegate<Action<Rect, TimeAssignmentDef>>(AccessTools.Method(typeof(TimeAssignmentSelector), "DrawTimeAssignmentSelectorFor"));

	public static void Postfix()
	{
		try
		{
			Rect grid = new Rect(0f, 0f, 191f, 63f);
			float w = grid.width / 2f, h = grid.height / 2f;
			int n = ModsConfig.RoyaltyActive ? 5 : 4;
			if (MealHour.Def != null && drawFor != null)
				drawFor(new Rect(n * w, 0f, w, h), MealHour.Def);
			Map map = Find.CurrentMap;
			if (map != null && SchedulesMod.S != null && Widgets.ButtonText(new Rect(2f, h + 2f, 186f, h - 2f), "CF2S_CalButton".Translate()))
				Find.WindowStack.Add(new Window_StewardCalendar(map));
		}
		catch (Exception e)
		{
			MLog.WarnOnce("[Steward] schedule tab extras: " + e.Message, 0x5d0011);
		}
	}
}
