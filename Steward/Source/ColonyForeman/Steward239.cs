using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;

// 2.39: fire response. Priorities alone did not put a fire out: vanilla firefighting only looks at
// fires in the home area, one colonist per fire, and only when the colonist looks for new work –
// so during a big fire most colonists walked around and only now and then beat a flame.
// Now, every second, free colonists with firefighting on are sent straight to the nearest
// fire that nobody is beating – also fires in fields and stockpiles and next to your buildings.
public static class FireResponse
{
	private static readonly HashSet<string> keepJobs = new HashSet<string>
	{
		"BeatFire", "ExtinguishSelf", "TendPatient", "Rescue", "Capture", "Arrest", "Flee", "FleeAndCower",
		"Ingest", "LayDown", "Vomit", "AttackMelee", "AttackStatic", "DeliverToBed", "Wait_Combat"
	};

	public static bool Enabled => ForemanMod.Settings != null && ForemanMod.Settings.fireResponse;

	private static bool Ours(Fire f, Map map, Area_Home home)
	{
		if (!f.Spawned || f.parent is Pawn)
			return false;
		IntVec3 c = f.Position;
		if ((home != null && home[c]) || map.zoneManager.ZoneAt(c) != null)
			return true;
		// right next to a building of yours
		for (int i = 0; i < 9; i++)
		{
			IntVec3 n = c + GenAdj.AdjacentCellsAndInside[i];
			if (!n.InBounds(map))
				continue;
			Building b = n.GetFirstBuilding(map);
			if (b != null && b.Faction == Faction.OfPlayer)
				return true;
		}
		return false;
	}

	private static bool Free(Pawn p)
	{
		if (p.Downed || p.Drafted || p.InMentalState || !p.Awake() || p.workSettings == null || !p.workSettings.EverWork)
			return false;
		if (p.GetLord() != null || p.WorkTagIsDisabled(WorkTags.Firefighting) || p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter)
		    || p.workSettings.GetPriority(WorkTypeDefOf.Firefighter) <= 0)
			return false;
		if (HealthAIUtility.ShouldSeekMedicalRest(p) || (p.needs?.food != null && p.needs.food.Starving))
			return false;
		Job j = p.CurJob;
		return j == null || (!j.playerForced && !keepJobs.Contains(j.def.defName));
	}

	public static void Tick(Map map)
	{
		if (!Enabled || map == null || !map.IsPlayerHome)
			return;
		List<Thing> all = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
		if (all.Count == 0)
			return;
		Area_Home home = map.areaManager.Home;
		var fires = new List<Fire>();
		for (int i = 0; i < all.Count; i++)
			if (all[i] is Fire f && Ours(f, map, home))
				fires.Add(f);
		if (fires.Count == 0)
			return;
		var colonists = map.mapPawns.FreeColonistsSpawned;
		int fighting = colonists.Count(p => p.CurJobDef == JobDefOf.BeatFire);
		int want = Mathf.Clamp(Mathf.CeilToInt(fires.Count / 2f), 2, 40);
		if (fighting >= want)
			return;
		// fires already somebody's target
		var taken = new HashSet<Thing>();
		foreach (Pawn p in colonists)
			if (p.CurJobDef == JobDefOf.BeatFire && p.CurJob.targetA.Thing != null)
				taken.Add(p.CurJob.targetA.Thing);
		var free = colonists.Where(Free)
			.Select(p => (p, d: fires.Min(f => f.Position.DistanceToSquared(p.Position))))
			.OrderBy(x => x.d).Select(x => x.p).ToList();
		foreach (Pawn p in free)
		{
			if (fighting >= want)
				break;
			Fire target = null;
			int best = int.MaxValue;
			foreach (Fire f in fires)
			{
				if (taken.Contains(f))
					continue;
				int d = f.Position.DistanceToSquared(p.Position);
				if (d < best && p.CanReserve(f) && p.CanReach(f, PathEndMode.Touch, Danger.Deadly))
				{
					best = d;
					target = f;
				}
			}
			if (target == null)
				continue;
			try
			{
				p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.BeatFire, target), JobCondition.InterruptForced);
				taken.Add(target);
				fighting++;
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[Steward] fire response: " + ex.Message, 0x57e3a51);
			}
		}
	}
}

// 2.39: the schedule looks after people. A colonist who is exhausted gets the current and the next
// hour as Sleep, one on the edge of a mental break gets them as Recreation. When they are fine
// again, the hours go back to what they were (unless you changed them in the meantime).
public static class ScheduleCare
{
	private const float TiredBelow = 0.15f, RestedAbove = 0.9f;
	private const float BreakMargin = 0.10f, CalmMargin = 0.18f;   // 2.47/2.49: earlier help, longer

	public static bool Enabled => ForemanMod.Settings != null && ForemanMod.Settings.scheduleCare;

	public static void Tick(ForemanMapComponent comp, Map map)
	{
		if (!Enabled || comp == null || map == null)
			return;
		int hour = GenLocalDate.HourOfDay(map);
		int next = (hour + 1) % 24;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p.timetable == null || p.needs == null)
				continue;
			int id = p.thingIDNumber;
			comp.schedHold.TryGetValue(id, out string held);
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
				if (started && ForemanMod.Settings.crisisMessages)
					Messages.Message((want == TimeAssignmentDefOf.Sleep ? "CF_SchedTired" : "CF_SchedBreak").Translate(p.LabelShort), p, MessageTypeDefOf.SilentInput, historical: false);
			}
			if (holds.Count == 0)
				comp.schedHold.Remove(id);
			else
				comp.schedHold[id] = string.Join("|", holds.Select(x => x.h + "," + x.orig + "," + x.set));
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
