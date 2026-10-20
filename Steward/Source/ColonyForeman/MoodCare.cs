using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// 2.49: mood care. Steward looks at WHY each colonist feels bad (the thoughts that pull the mood
// down and the needs that are empty) and answers the cause, not only the symptom:
//  * a colonist right at the edge of a break stops ordinary work at once and goes to rest or
//    relax (the schedule hour is changed first, so they do not walk straight back to work),
//  * the work that helps the colony's most common causes gets more hands (cooking for hunger
//    and raw food, cleaning for ugly/dirty rooms, doctors for pain, tailoring for ragged clothes),
//  * when many colonists are slipping, everybody gets recreation earlier,
//  * causes Steward cannot fix by itself (no table, no chairs, darkness, cramped rooms, cabin
//    fever) are told to you once a day, with how many colonists they hurt,
//  * a report for every colonist is written to Zalohy_modu\Steward_nalady.txt.
public static class MoodCare
{
	public enum Cause { Rest, Joy, Food, Table, Pain, Room, Comfort, Dark, Cramped, Outdoors, Apparel, Temperature, Social, Other }

	public sealed class Diag
	{
		public Pawn pawn;
		public float mood, minor, major, extreme;
		public readonly List<(string label, float offset, Cause cause)> bad = new List<(string, float, Cause)>();
		public readonly List<(string label, float offset)> good = new List<(string, float)>();
		public readonly Dictionary<Cause, float> byCause = new Dictionary<Cause, float>();
		public float Margin => mood - minor;              // below 0 = a minor break can happen
		public bool Slipping => Margin < 0.10f;
		public bool Edge => Margin < 0.03f;
		public Cause Top => byCause.Count == 0 ? Cause.Other : byCause.OrderBy(kv => kv.Value).First().Key;
	}

	private static readonly Dictionary<int, int> interruptedAt = new Dictionary<int, int>();
	private static readonly Dictionary<int, Dictionary<Cause, int>> colonyCauses = new Dictionary<int, Dictionary<Cause, int>>();
	private static readonly Dictionary<int, float> stressedShare = new Dictionary<int, float>();
	private static readonly Dictionary<int, int> nextReport = new Dictionary<int, int>();
	private static readonly Dictionary<string, int> lastHint = new Dictionary<string, int>();
	private static readonly List<Thought> tmp = new List<Thought>();

	public static bool Enabled => ForemanMod.Settings != null && ForemanMod.Settings.scheduleCare;

	public static string ReportPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "Zalohy_modu", "Steward_nalady.txt");

	public static void ResetGame()
	{
		interruptedAt.Clear();
		colonyCauses.Clear();
		stressedShare.Clear();
		nextReport.Clear();
		lastHint.Clear();
	}

	// ---- diagnosis ---------------------------------------------------------
	public static Diag Diagnose(Pawn p)
	{
		Need_Mood m = p.needs?.mood;
		if (m == null || p.mindState?.mentalBreaker == null)
			return null;
		var d = new Diag { pawn = p, mood = m.CurLevel };
		try
		{
			MentalBreaker b = p.mindState.mentalBreaker;
			d.minor = b.BreakThresholdMinor;
			d.major = b.BreakThresholdMajor;
			d.extreme = b.BreakThresholdExtreme;
		}
		catch { }
		try
		{
			tmp.Clear();
			m.thoughts.GetDistinctMoodThoughtGroups(tmp);
			foreach (Thought t in tmp)
			{
				float off = m.thoughts.MoodOffsetOfGroup(t);
				string label = t.LabelCap.ToString();
				if (off < -0.01f)
				{
					Cause c = Classify(t);
					d.bad.Add((label, off, c));
					d.byCause.TryGetValue(c, out float s);
					d.byCause[c] = s + off;
				}
				else if (off > 0.01f)
					d.good.Add((label, off));
			}
		}
		catch { }
		d.bad.Sort((a, b) => a.offset.CompareTo(b.offset));
		d.good.Sort((a, b) => b.offset.CompareTo(a.offset));
		return d;
	}

	private static Cause Classify(Thought t)
	{
		string n = t.def?.defName ?? "";
		string l = n.ToLowerInvariant();
		if (t is ISocialThought || l.Contains("social") || l.Contains("insult") || l.Contains("rebuff") || l.Contains("slight") || l.Contains("fight") || l.Contains("breakup") || l.Contains("divorce") || l.Contains("rejected"))
			return Cause.Social;
		if (l.Contains("withouttable"))
			return Cause.Table;
		if (l.Contains("rest") || l.Contains("slept") || l.Contains("sleep") || l.Contains("tired") || l.Contains("exhaust"))
			return Cause.Rest;
		if (l.Contains("joy") || l.Contains("recreation") || l.Contains("bored"))
			return Cause.Joy;
		if (l.Contains("food") || l.Contains("hungry") || l.Contains("starv") || l.Contains("ate") && (l.Contains("raw") || l.Contains("awful") || l.Contains("kibble") || l.Contains("insect")))
			return Cause.Food;
		if (l.Contains("pain"))
			return Cause.Pain;
		if (l.Contains("dark"))
			return Cause.Dark;
		if (l.Contains("cramped") || l.Contains("roomsize"))
			return Cause.Cramped;
		if (l.Contains("outdoors") || l.Contains("cabinfever") || l.Contains("indoors"))
			return Cause.Outdoors;
		if (l.Contains("comfort"))
			return Cause.Comfort;
		if (l.Contains("beauty") || l.Contains("ugly") || l.Contains("dirty") || l.Contains("filth") || l.Contains("disgust"))
			return Cause.Room;
		if (l.Contains("apparel") || l.Contains("naked") || l.Contains("nude") || l.Contains("tattered") || l.Contains("deadman"))
			return Cause.Apparel;
		if (l.Contains("cold") || l.Contains("hot") || l.Contains("heat") || l.Contains("freez"))
			return Cause.Temperature;
		return Cause.Other;
	}

	public static float StressedShare(Map map) => map != null && stressedShare.TryGetValue(map.uniqueID, out float f) ? f : 0f;

	// ---- every 250 ticks, after the schedule rules -------------------------
	public static void Tick(ForemanMapComponent comp, Map map)
	{
		if (!Enabled || comp == null || map == null)
			return;
		int now = Find.TickManager.TicksGame;
		List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.Where(p => p.needs?.mood != null && !p.DevelopmentalStage.Baby()).ToList();
		bool full = now % 2500 < 250;   // once an in-game hour: everybody, causes, hints
		var counts = new Dictionary<Cause, int>();
		int slipping = 0;
		var diags = new List<Diag>();
		foreach (Pawn p in pawns)
		{
			// quick check first – the full look only for those in trouble (or once an hour)
			float mood = p.needs.mood.CurLevel, minor = 0f;
			try { minor = p.mindState?.mentalBreaker?.BreakThresholdMinor ?? 0f; } catch { }
			if (!full && mood - minor > 0.10f)
				continue;
			Diag d = Diagnose(p);
			if (d == null)
				continue;
			diags.Add(d);
			if (d.Slipping)
			{
				slipping++;
				foreach (var kv in d.byCause)
				{
					counts.TryGetValue(kv.Key, out int c);
					counts[kv.Key] = c + 1;
				}
			}
			if (d.Edge && !p.InMentalState)
				Rescue(comp, d, now);
		}
		if (!full)
			return;
		colonyCauses[map.uniqueID] = counts;
		stressedShare[map.uniqueID] = pawns.Count == 0 ? 0f : (float)slipping / pawns.Count;
		Hints(map, counts, now);
		if (!nextReport.TryGetValue(map.uniqueID, out int nr) || now >= nr || nr - now > 15000)
		{
			nextReport[map.uniqueID] = now + 15000;   // every 6 in-game hours
			Report(comp, map, diags, counts, slipping, pawns.Count);
		}
	}

	// right at the edge: stop ordinary work now, rest or relax (the schedule rule already gave this hour)
	private static void Rescue(ForemanMapComponent comp, Diag d, int now)
	{
		Pawn p = d.pawn;
		if (p.Drafted || p.Downed || p.jobs == null || p.timetable == null)
			return;
		if (interruptedAt.TryGetValue(p.thingIDNumber, out int at) && now >= at && now - at < 2500)
			return;
		Job job = p.CurJob;
		if (job == null || job.playerForced || job.workGiverDef == null)
			return;   // already resting, eating, relaxing, or you told them
		// 2.49.2: hunger is answered with food, not with games
		if (d.Top == Cause.Food || (p.needs.food != null && p.needs.food.CurLevelPercentage < 0.25f))
		{
			interruptedAt[p.thingIDNumber] = now;
			Feed(p);
			return;
		}
		TimeAssignmentDef cur = p.timetable.CurrentAssignment;
		if (cur != TimeAssignmentDefOf.Joy && cur != TimeAssignmentDefOf.Sleep)
		{
			// the hour was not changed (you set it by hand, or it is a sleep-anything mix) – change it now
			bool tired = (p.needs.rest?.CurLevel ?? 1f) < 0.35f;
			TimeAssignmentDef want = tired ? TimeAssignmentDefOf.Sleep : TimeAssignmentDefOf.Joy;
			int hour = GenLocalDate.HourOfDay(p);
			comp.schedHold.TryGetValue(p.thingIDNumber, out string held);
			string orig = cur?.defName ?? "Work";
			string entry = hour + "," + orig + "," + want.defName;
			if (held == null || !held.Split('|').Any(x => x.StartsWith(hour + ",")))
				comp.schedHold[p.thingIDNumber] = string.IsNullOrEmpty(held) ? entry : held + "|" + entry;
			p.timetable.SetAssignment(hour, want);
		}
		interruptedAt[p.thingIDNumber] = now;
		try
		{
			p.jobs.EndCurrentJob(JobCondition.InterruptForced, true);
			MLog.Info("[Steward] " + p.LabelShort + " stops work to calm down (mood " + d.mood.ToString("P0") + ", break at " + d.minor.ToString("P0") + ", mostly: " + CauseLabel(d.Top) + ").");
			if (ForemanMod.Settings.crisisMessages)
				Messages.Message("CF_MoodRescue".Translate(p.LabelShort, CauseLabel(d.Top)), p, MessageTypeDefOf.SilentInput, historical: false);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] mood rescue: " + ex.Message, 0x57e3b01);
		}
	}

	private static readonly Dictionary<int, int> toldNoFood = new Dictionary<int, int>();

	private static void Feed(Pawn p)
	{
		try
		{
			if (FoodUtility.TryFindBestFoodSourceFor(p, p, true, out Thing food, out ThingDef foodDef) && food != null)
			{
				Job eat = JobMaker.MakeJob(JobDefOf.Ingest, food);
				eat.count = FoodUtility.WillIngestStackCountOf(p, foodDef, FoodUtility.NutritionForEater(p, food));
				p.jobs.StartJob(eat, JobCondition.InterruptForced, null, false, true);
				MLog.Info("[Steward] " + p.LabelShort + " is hungry and close to a break – goes to eat " + food.LabelShort + ".");
				return;
			}
			int now = Find.TickManager.TicksGame;
			if (toldNoFood.TryGetValue(p.thingIDNumber, out int t) && now - t < 30000)
				return;
			toldNoFood[p.thingIDNumber] = now;
			MLog.Warn("[Steward] " + p.LabelShort + " is hungry but no food can be reached.");
			if (ForemanMod.Settings.crisisMessages)
				Messages.Message("CF_MoodNoFood".Translate(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, historical: false);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] feed: " + ex.Message, 0x57e3b03);
		}
	}

	// ---- work that helps the most common causes ----------------------------
	// used by the "mood slipping" situation instead of a fixed list
	public static List<(string work, int workers)> FocusWorks(Map map, int colonists)
	{
		var list = new List<(string, int)>();
		if (map == null || !colonyCauses.TryGetValue(map.uniqueID, out var c) || c.Count == 0)
			return list;
		int hands = Math.Max(1, colonists / 8);
		void Add(string w, int n)
		{
			if (!list.Any(x => x.Item1 == w))
				list.Add((w, n));
		}
		foreach (var kv in c.OrderByDescending(x => x.Value))
		{
			if (kv.Value < 1)
				continue;
			switch (kv.Key)
			{
				case Cause.Food: Add("Cooking", hands); break;
				case Cause.Room: Add("Cleaning", hands + (kv.Value >= 3 ? 1 : 0)); break;
				case Cause.Pain: Add("Doctor", 1); break;
				case Cause.Apparel: Add("Tailoring", 1); break;
				case Cause.Joy: Add("Art", 1); break;   // art makes rooms nicer and gives work a break from the rest
			}
		}
		return list;
	}

	// ---- causes only you can fix: once a day each -------------------------
	private static void Hints(Map map, Dictionary<Cause, int> counts, int now)
	{
		if (!ForemanMod.Settings.crisisMessages)
			return;
		foreach (Cause c in new[] { Cause.Table, Cause.Comfort, Cause.Dark, Cause.Cramped, Cause.Outdoors, Cause.Temperature })
		{
			if (!counts.TryGetValue(c, out int n) || n < 2)
				continue;
			string key = map.uniqueID + "|" + c;
			if (lastHint.TryGetValue(key, out int t) && now - t < 60000)
				continue;
			lastHint[key] = now;
			Messages.Message(("CF_MoodHint_" + c).Translate(n), MessageTypeDefOf.CautionInput, historical: false);
		}
	}

	public static string CauseLabel(Cause c) => ("CF_MoodCause_" + c).Translate().ToString();

	// ---- report ------------------------------------------------------------
	private static void Report(ForemanMapComponent comp, Map map, List<Diag> diags, Dictionary<Cause, int> counts, int slipping, int total)
	{
		try
		{
			var sb = new StringBuilder();
			sb.AppendLine("Steward – nálady " + DateTime.Now.ToString("d.M. HH:mm") + " | " + GenDate.DateFullStringWithHourAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile)));
			sb.AppendLine("Kolonistů " + total + ", blízko zhroucení " + slipping + ". Nejčastější příčiny: " +
				(counts.Count == 0 ? "–" : string.Join(", ", counts.OrderByDescending(x => x.Value).Select(x => CauseLabel(x.Key) + " " + x.Value + "x").ToArray())));
			sb.AppendLine();
			foreach (Diag d in diags.OrderBy(x => x.Margin))
			{
				Pawn p = d.pawn;
				comp.scheduled.TryGetValue(p.thingIDNumber, out string sch);
				string schedule = sch == "manual" ? "rozvrh ručně (Steward ho nemění)" : sch == null ? "rozvrh –" : "rozvrh auto, zábava " + (sch.Split('|').Length > 1 ? sch.Split('|')[1] : "?") + " h";
				sb.AppendLine((d.Edge ? "!! " : d.Slipping ? "!  " : "   ") + p.LabelShort + ": nálada " + d.mood.ToString("P0") + " (zhroucení pod " + d.minor.ToString("P0") + " / " + d.major.ToString("P0") + " / " + d.extreme.ToString("P0") + ") | " + schedule + (p.InMentalState ? " | TEĎ ZHROUCENÝ" : ""));
				var needs = new List<string>();
				if (p.needs?.AllNeeds != null)
					foreach (Need n in p.needs.AllNeeds)
						if (!(n is Need_Mood) && n.def.showOnNeedList && n.CurLevelPercentage < 0.3f)
							needs.Add(n.LabelCap + " " + n.CurLevelPercentage.ToString("P0"));
				if (needs.Count > 0)
					sb.AppendLine("     prázdné potřeby: " + string.Join(", ", needs.ToArray()));
				if (d.bad.Count > 0)
					sb.AppendLine("     dolů: " + string.Join("; ", d.bad.Take(6).Select(b => b.label + " " + b.offset.ToString("0") + " [" + CauseLabel(b.cause) + "]").ToArray()));
				if (d.good.Count > 0)
					sb.AppendLine("     nahoru: " + string.Join("; ", d.good.Take(4).Select(b => b.label + " +" + b.offset.ToString("0")).ToArray()));
				sb.AppendLine("     teď: " + (p.CurJob?.def?.label ?? "-") + ", hodina: " + (p.timetable?.CurrentAssignment?.label ?? "-"));
			}
			Directory.CreateDirectory(Path.GetDirectoryName(ReportPath));
			File.WriteAllText(ReportPath, sb.ToString());
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] mood report: " + ex.Message, 0x57e3b02);
		}
	}
}
