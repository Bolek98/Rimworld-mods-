using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.1.0: work hours for single kinds of work (like Schedule Everything), each with its own colour.
// In an hour painted as e.g. "construction" the colonist does construction FIRST – the priorities stay
// as they are (Steward keeps planning them); when there is nothing to build he goes on with his other
// work, so nobody stands idle. For the game it is a work hour (no recreation, no sleep unless tired).
[StaticConstructorOnStartup]
public static class WorkHours
{
	public const string Prefix = "CFW_";
	private static readonly Dictionary<TimeAssignmentDef, WorkTypeDef> workOf = new Dictionary<TimeAssignmentDef, WorkTypeDef>();
	public static readonly List<TimeAssignmentDef> All = new List<TimeAssignmentDef>();

	static WorkHours()
	{
		try
		{
			var types = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).OrderByDescending(w => w.naturalPriority).ToList();
			for (int i = 0; i < types.Count; i++)
			{
				WorkTypeDef w = types[i];
				string name = Prefix + w.defName;
				TimeAssignmentDef d = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(name);
				if (d == null)
				{
					// colours spread around the colour wheel, every second one a bit darker
					float hue = (i * 0.618034f) % 1f;
					d = new TimeAssignmentDef
					{
						defName = name,
						label = w.labelShort.NullOrEmpty() ? w.label : w.labelShort,
						description = w.description,
						color = Color.HSVToRGB(hue, 0.55f, i % 2 == 0 ? 0.80f : 0.62f),
						allowJoy = false,
						allowRest = true,
						generated = true,
					};
					d.PostLoad();
					DefDatabase<TimeAssignmentDef>.Add(d);
				}
				workOf[d] = w;
				All.Add(d);
			}
		}
		catch (Exception e)
		{
			Log.Warning("[Steward] work hours could not be made: " + e.Message);
		}
	}

	public static WorkTypeDef WorkOf(TimeAssignmentDef d) => d != null && workOf.TryGetValue(d, out WorkTypeDef w) ? w : null;

	public static TimeAssignmentDef Raw(Pawn p)
	{
		if (p?.timetable == null || !p.IsColonist)
			return null;
		try { return p.timetable.GetAssignment(GenLocalDate.HourOfDay(p)); }
		catch { return null; }
	}
}

// for the game a work-type hour is a work hour, a meal hour is "anything"
[HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.CurrentAssignment), MethodType.Getter)]
public static class CustomHoursAsVanilla
{
	public static void Postfix(ref TimeAssignmentDef __result)
	{
		if (__result == null)
			return;
		if (__result == MealHour.Def)
			__result = TimeAssignmentDefOf.Anything;
		else if (WorkHours.WorkOf(__result) != null)
			__result = TimeAssignmentDefOf.Work;
	}
}

// in a work-type hour that work comes first in the colonist's list of jobs
[HarmonyPatch(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.WorkGiversInOrderNormal), MethodType.Getter)]
public static class WorkHourFirst
{
	private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> pawnRef = AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");
	private static readonly Dictionary<Pawn_WorkSettings, List<WorkGiver>> lists = new Dictionary<Pawn_WorkSettings, List<WorkGiver>>();

	[HarmonyPriority(Priority.Last)]
	public static void Postfix(Pawn_WorkSettings __instance, ref List<WorkGiver> __result)
	{
		try
		{
			if (__result == null || __result.Count < 2)
				return;
			Pawn p = pawnRef(__instance);
			WorkTypeDef w = WorkHours.WorkOf(WorkHours.Raw(p));
			if (w == null || p.WorkTypeIsDisabled(w) || __instance.GetPriority(w) <= 0)
				return;
			if (!lists.TryGetValue(__instance, out List<WorkGiver> res))
			{
				if (lists.Count > 400)
					lists.Clear();
				lists[__instance] = res = new List<WorkGiver>(__result.Count);
			}
			res.Clear();
			foreach (WorkGiver g in __result)
				if (g.def.workType == w)
					res.Add(g);
			if (res.Count == 0)
				return;
			foreach (WorkGiver g in __result)
				if (g.def.workType != w)
					res.Add(g);
			__result = res;
		}
		catch { }
	}

	public static void Reset() => lists.Clear();
}

// a timetable from a game where a work type (its mod) is gone: those hours become "anything"
[HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.ExposeData))]
public static class TimetableRepair
{
	public static void Postfix(Pawn_TimetableTracker __instance)
	{
		if (Scribe.mode != LoadSaveMode.PostLoadInit || __instance.times == null)
			return;
		for (int i = 0; i < __instance.times.Count; i++)
			if (__instance.times[i] == null)
				__instance.times[i] = TimeAssignmentDefOf.Anything;
		while (__instance.times.Count < 24)
			__instance.times.Add(TimeAssignmentDefOf.Anything);
	}
}

// the Schedule tab: the meal box, one box for work hours (click = choose the kind of work) and the calendar button
[HarmonyPatch(typeof(MainTabWindow_Schedule), nameof(MainTabWindow_Schedule.DoWindowContents))]
public static class ScheduleTabExtras
{
	private static readonly Action<Rect, TimeAssignmentDef> drawFor =
		AccessTools.MethodDelegate<Action<Rect, TimeAssignmentDef>>(AccessTools.Method(typeof(TimeAssignmentSelector), "DrawTimeAssignmentSelectorFor"));

	private static TimeAssignmentDef lastWork;

	public static void Postfix()
	{
		try
		{
			Rect grid = new Rect(0f, 0f, 191f, 63f);
			float w = grid.width / 2f, h = grid.height / 2f;
			int n = ModsConfig.RoyaltyActive ? 5 : 4;
			if (MealHour.Def != null && drawFor != null)
				drawFor(new Rect(n * w, 0f, w, h), MealHour.Def);
			n++;
			// the work-hour box: shows the last chosen kind, a click opens the list
			if (WorkHours.All.Count > 0)
			{
				lastWork ??= WorkHours.All[0];
				Rect b = new Rect(n * w, 0f, w * 1.6f, h).ContractedBy(2f);
				GUI.DrawTexture(b, lastWork.ColorTexture);
				Text.Font = GameFont.Tiny;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(b, lastWork.LabelCap + " ▾");
				Text.Anchor = TextAnchor.UpperLeft;
				Text.Font = GameFont.Small;
				if (TimeAssignmentSelector.selectedAssignment == lastWork)
					Widgets.DrawBox(b, 2);
				if (Mouse.IsOver(b))
					Widgets.DrawHighlight(b);
				TooltipHandler.TipRegion(b, "CF2S_WorkHourTip".Translate());
				if (Widgets.ButtonInvisible(b))
				{
					var opts = new List<FloatMenuOption>();
					foreach (TimeAssignmentDef d in WorkHours.All)
					{
						TimeAssignmentDef dd = d;
						opts.Add(new FloatMenuOption(d.LabelCap, () => { lastWork = dd; TimeAssignmentSelector.selectedAssignment = dd; }, BaseContent.WhiteTex, d.color));
					}
					Find.WindowStack.Add(new FloatMenu(opts));
				}
			}
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
