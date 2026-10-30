using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;


// 2.39: the schedule looks after people. A colonist who is exhausted gets the current and the next
// hour as Sleep, one on the edge of a mental break gets them as Recreation. When they are fine
// again, the hours go back to what they were (unless you changed them in the meantime).
public static class ScheduleCare
{
	private const float TiredBelow = 0.15f, RestedAbove = 0.9f;
	private const float BreakMargin = 0.05f, CalmMargin = 0.12f;   // 2.52.3: only close to a break (0.10 kept people on recreation for days)

	public static bool Enabled => SchedulesMod.S != null && SchedulesMod.S.scheduleCare;

	// 2.52.3: hours held for rest/recreation are given back when the help is switched off
	public static void ReleaseAll(ForemanMapComponent comp, Map map)
	{
		if (comp == null || SchedState.Of(comp).schedHold.Count == 0 || map == null)
			return;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p.timetable == null || !SchedState.Of(comp).schedHold.TryGetValue(p.thingIDNumber, out string held))
				continue;
			foreach (var e in Parse(held))
			{
				TimeAssignmentDef now = p.timetable.GetAssignment(e.Item1);
				TimeAssignmentDef orig = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(e.Item2);
				if (now != null && now.defName == e.Item3 && orig != null)
					p.timetable.SetAssignment(e.Item1, orig);
			}
		}
		SchedState.Of(comp).schedHold.Clear();
	}

	public static void Tick(ForemanMapComponent comp, Map map)
	{
		if (comp == null || map == null)
			return;
		if (!Enabled)
		{
			ReleaseAll(comp, map);
			return;
		}
		int hour = GenLocalDate.HourOfDay(map);
		int next = (hour + 1) % 24;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p.timetable == null || p.needs == null)
				continue;
			int id = p.thingIDNumber;
			SchedState.Of(comp).schedHold.TryGetValue(id, out string held);
			List<(int h, string orig, string set)> holds = Parse(held);
			float rest = p.needs.rest?.CurLevel ?? 1f;
			float mood = p.needs.mood?.CurLevel ?? 1f;
			float minor = 0f;
			try { minor = p.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f; } catch { }
			bool tired = p.needs.rest != null && rest < TiredBelow;
			// 2.47: also when recreation is almost empty – the mood would follow soon
			float joyLvl = p.needs.joy?.CurLevelPercentage ?? 1f;
			bool breaking = !p.InMentalState && ((p.needs.mood != null && mood < minor + BreakMargin) || joyLvl < 0.12f);
			bool rested = rest > RestedAbove;
			bool calm = mood > minor + CalmMargin && joyLvl > 0.4f;
			// 1. give back hours that are not needed any more
			for (int i = holds.Count - 1; i >= 0; i--)
			{
				var e = holds[i];
				bool past = e.h != hour && e.h != next;
				bool done = e.set == "Sleep" ? rested : calm;
				if (!past && !done)
					continue;
				TimeAssignmentDef now = p.timetable.GetAssignment(e.h);
				TimeAssignmentDef orig = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(e.orig);
				if (now != null && now.defName == e.set && orig != null)
					p.timetable.SetAssignment(e.h, orig);
				holds.RemoveAt(i);
			}
			// 2. help now
			// 2.49: someone breaking who is also quite tired sleeps first (rest lifts the mood faster than games)
			TimeAssignmentDef want = tired || (breaking && rest < 0.35f) ? TimeAssignmentDefOf.Sleep : breaking ? TimeAssignmentDefOf.Joy : null;
			if (want != null)
			{
				bool started = false;
				foreach (int h in new[] { hour, next })
				{
					TimeAssignmentDef cur = p.timetable.GetAssignment(h);
					if (cur == null || cur == want || cur == TimeAssignmentDefOf.Sleep)
						continue;
					// sleep replaces work, "anything" and recreation; recreation replaces work and "anything" (2.49)
					if (cur != TimeAssignmentDefOf.Work && cur != TimeAssignmentDefOf.Anything && !(want == TimeAssignmentDefOf.Sleep && cur == TimeAssignmentDefOf.Joy))
						continue;
					int k = holds.FindIndex(x => x.h == h);
					string orig = k >= 0 ? holds[k].orig : cur.defName;
					if (k >= 0)
						holds.RemoveAt(k);
					holds.Add((h, orig, want.defName));
					p.timetable.SetAssignment(h, want);
					started = true;
				}
				if (started && SchedulesMod.S.crisisMessages)
					Messages.Message((want == TimeAssignmentDefOf.Sleep ? "CF_SchedTired" : "CF_SchedBreak").Translate(p.LabelShort), p, MessageTypeDefOf.SilentInput, historical: false);
			}
			if (holds.Count == 0)
				SchedState.Of(comp).schedHold.Remove(id);
			else
				SchedState.Of(comp).schedHold[id] = string.Join("|", holds.Select(x => x.h + "," + x.orig + "," + x.set));
		}
	}

	private static List<(int, string, string)> Parse(string s)
	{
		var list = new List<(int, string, string)>();
		if (string.IsNullOrEmpty(s))
			return list;
		foreach (string part in s.Split('|'))
		{
			string[] a = part.Split(',');
			if (a.Length == 3 && int.TryParse(a[0], out int h))
				list.Add((h, a[1], a[2]));
		}
		return list;
	}
}
