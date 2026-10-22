using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

public static class AutoSchedules
{
	private static SchedulesSettings S => SchedulesMod.S;

	// ---- 6. automatic schedules --------------------------------------------
	// 2.47: the schedule adapts to every colonist. Recreation hours grow while someone lacks
	// recreation or the mood sinks, and shrink again when they are fine (1–5 hours, before sleep).
	// The hour after waking up and the hour before recreation are "Anything", so hungry or bored
	// colonists can eat or relax without waiting for the evening. If you change a colonist's
	// schedule yourself, Steward leaves that colonist alone.
	public const int JoyMin = 1, JoyMax = 5, JoyStart = 2;

	public static void Schedules(ForemanMapComponent comp, Map map)
	{
		if (S == null || !S.autoSchedule)
			return;
		TraitDef owl = DefDatabase<TraitDef>.GetNamedSilentFail("NightOwl");
		var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => p.timetable != null).OrderBy(p => p.thingIDNumber).ToList();
		bool resetDone = SchedState.Of(comp).manualReset2492;
		int now = Find.TickManager.TicksGame;
		// 3.0: the night shift is kept per colonist (it no longer reshuffles when someone leaves);
		// at most a quarter of the colony, only with the night shift switched on
		SchedState ss = SchedState.Of(comp);
		int nightWanted = S.nightShift && pawns.Count >= 6 ? pawns.Count / 4 : 0;
		int nightNow = pawns.Count(p => ss.scheduled.TryGetValue(p.thingIDNumber, out string h) && h != null && h.StartsWith("N") && !(owl != null && p.story?.traits != null && p.story.traits.HasTrait(owl)));
		foreach (Pawn p in pawns)
		{
			bool night = (owl != null && p.story?.traits != null && p.story.traits.HasTrait(owl));
			if (!night && nightWanted > 0)
			{
				bool was = ss.scheduled.TryGetValue(p.thingIDNumber, out string h0) && h0 != null && h0.StartsWith("N");
				if (was && nightNow <= nightWanted)
					night = true;
				else if (was)
					nightNow--;   // too many on nights – this one goes back to days
				else if (nightNow < nightWanted)
				{
					night = true;
					nightNow++;
				}
			}
			int id = p.thingIDNumber;
			SchedState.Of(comp).scheduled.TryGetValue(id, out string had);
			// 2.49.1: older versions marked a colonist "manual" whenever ANY mod changed the schedule.
			// Once per save every colonist goes back to Steward; from now on only your own changes count.
			if (had == "manual" && !SchedState.Of(comp).manualReset2492)
				had = null;
			if (had == "manual")
				continue;
			// state: "<D|N>|<joy hours>|<pattern>|<next check tick>"; old saves have only "D" / "N"
			string[] st = (had ?? "").Split('|');
			int joy = st.Length >= 2 && int.TryParse(st[1], out int j0) ? j0 : JoyStart;
			string lastPattern = st.Length >= 3 ? st[2] : null;
			int nextCheck = st.Length >= 4 && int.TryParse(st[3], out int n0) ? n0 : 0;
			// you changed the schedule yourself (hours held by the care rules do not count)
			if (lastPattern != null && lastPattern.Length == 24 && !SchedState.Of(comp).schedHold.ContainsKey(id) && Read(p) != lastPattern)
			{
				// 2.49.1: only when YOU had the schedule tab open – a change made by another mod is undone
				if (ScheduleTabWatch.EditedByYou.Remove(id))
				{
					SchedState.Of(comp).scheduled[id] = "manual";
					MLog.Info("[Steward] " + p.LabelShort + ": schedule changed by hand – Steward leaves it alone.");
					continue;
				}
				MLog.WarnOnce("[Steward] the schedule of " + p.LabelShort + " was changed by another mod – Steward's schedule restored.", id ^ 0x5c4ed);
				lastPattern = null;
			}
			// every 6 in-game hours: more or fewer recreation hours
			if (now >= nextCheck)
			{
				int before = joy;
				joy = Mathf.Clamp(joy + JoyChange(p), JoyMin, JoyMax);
				nextCheck = now + 15000;
				if (joy > before && S.crisisMessages)
					Messages.Message("CF_SchedMoreJoy".Translate(p.LabelShort, joy), p, MessageTypeDefOf.SilentInput, historical: false);
				if (joy >= JoyMax && Bored(p) >= 2)
					BoredHint(p);
			}
			string want = Pattern(night, joy);
			if (want != lastPattern || st.Length < 4)
			{
				if (!SchedState.Of(comp).schedHold.ContainsKey(id))
					Apply(p, want);
				else
					want = lastPattern ?? want;   // wait until the care rules give their hours back
			}
			SchedState.Of(comp).scheduled[id] = (night ? "N" : "D") + "|" + joy + "|" + want + "|" + nextCheck;
		}
		if (!resetDone)
		{
			SchedState.Of(comp).manualReset2492 = true;
			MLog.Info("[Steward] schedules: every colonist is looked after by Steward again (" + pawns.Count + ").");
		}
	}

	// +1: lacks recreation or the mood is low; -1: clearly fine; 0: keep
	private static int JoyChange(Pawn p)
	{
		float joy = p.needs?.joy?.CurLevelPercentage ?? 1f;
		float mood = p.needs?.mood?.CurLevel ?? 1f;
		float minor = 0f;
		try { minor = p.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f; } catch { }
		if (joy < 0.35f || mood < minor + 0.10f)
			return 1;
		// 2.49: when many colonists are slipping, everybody who is not clearly fine gets more recreation
		if (MoodCare.StressedShare(p.MapHeld) >= 0.3f && mood < minor + 0.20f)
			return 1;
		if (joy > 0.75f && mood > minor + 0.25f)
			return -1;
		return 0;
	}

	// how many kinds of recreation the colonist is bored of
	public static int Bored(Pawn p)
	{
		var t = p.needs?.joy?.tolerances;
		if (t == null)
			return 0;
		int n = 0;
		foreach (JoyKindDef d in DefDatabase<JoyKindDef>.AllDefsListForReading)
			if (t.BoredOf(d))
				n++;
		return n;
	}

	private static int lastBoredHint = -999999;

	private static void BoredHint(Pawn p)
	{
		int now = Find.TickManager.TicksGame;
		if (now - lastBoredHint < 60000)
			return;
		lastBoredHint = now;
		Messages.Message("CF_SchedBored".Translate(p.LabelShort), p, MessageTypeDefOf.CautionInput, historical: false);
	}

	// S = sleep, J = recreation, A = anything, W = work
	public static string Pattern(bool night, int joy)
	{
		var c = new char[24];
		int wake = night ? 18 : 6, sleep = night ? 10 : 22;
		for (int h = 0; h < 24; h++)
			c[h] = 'W';
		for (int i = 0; i < 8; i++)
			c[(sleep + i) % 24] = 'S';
		c[wake % 24] = 'A';
		for (int i = 1; i <= joy; i++)
			c[(sleep - i + 24) % 24] = 'J';
		c[(sleep - joy - 1 + 24) % 24] = 'A';
		return new string(c);
	}

	private static TimeAssignmentDef Def(char ch)
	{
		switch (ch)
		{
			case 'S': return TimeAssignmentDefOf.Sleep;
			case 'J': return TimeAssignmentDefOf.Joy;
			case 'A': return TimeAssignmentDefOf.Anything;
			default: return TimeAssignmentDefOf.Work;
		}
	}

	private static char Ch(TimeAssignmentDef d)
	{
		if (d == TimeAssignmentDefOf.Sleep) return 'S';
		if (d == TimeAssignmentDefOf.Joy) return 'J';
		if (d == TimeAssignmentDefOf.Anything) return 'A';
		if (d == TimeAssignmentDefOf.Work) return 'W';
		return '?';
	}

	public static string Read(Pawn p)
	{
		var c = new char[24];
		for (int h = 0; h < 24; h++)
			c[h] = Ch(p.timetable.GetAssignment(h));
		return new string(c);
	}

	private static void Apply(Pawn p, string pattern)
	{
		for (int h = 0; h < 24; h++)
			p.timetable.SetAssignment(h, Def(pattern[h]));
	}

}

// 2.49.1: was the schedule tab open lately? A schedule change counts as "yours" only then.
// 2.49.2: only an hour YOU paint in the schedule tab counts as yours (an open tab is not enough –
// another mod could change schedules at the same moment)
[HarmonyLib.HarmonyPatch(typeof(MainTabWindow_Schedule), nameof(MainTabWindow_Schedule.DoWindowContents))]
public static class ScheduleTabWatch
{
	public static bool Inside;
	public static readonly HashSet<int> EditedByYou = new HashSet<int>();

	public static void Prefix() => Inside = true;

	public static void Postfix() => Inside = false;
}

[HarmonyLib.HarmonyPatch(typeof(Pawn_TimetableTracker), nameof(Pawn_TimetableTracker.SetAssignment))]
public static class ScheduleEditWatch
{
	public static void Prefix(Pawn ___pawn)
	{
		if (ScheduleTabWatch.Inside && ___pawn != null)
			ScheduleTabWatch.EditedByYou.Add(___pawn.thingIDNumber);
	}
}

// 2.52.3: the schedule tab's Paste button writes the hours directly (no SetAssignment) – it is your change too
[HarmonyLib.HarmonyPatch(typeof(PawnColumnWorker_CopyPasteTimetable), "PasteTo")]
public static class SchedulePasteWatch
{
	public static void Prefix(Pawn p)
	{
		if (p != null)
			ScheduleTabWatch.EditedByYou.Add(p.thingIDNumber);
	}
}

