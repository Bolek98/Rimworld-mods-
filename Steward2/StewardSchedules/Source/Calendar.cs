using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// 1.1.0: a calendar like in real life. Every colonist has a kind of day – work day, night shift,
// day off, holiday, recovery after an attack – from a repeating shift cycle (5 + 2, 2 day / 2 night /
// 4 off …), colony holidays and the days after a fight. The kind decides the 24 hours of that day.
// Any day can be set by hand in the calendar window (button in the Schedule tab).
public static class DayPlan
{
	// W work day · N night shift · O day off · H holiday · R recovery
	public const char Work = 'W', Night = 'N', Off = 'O', Holiday = 'H', Recovery = 'R';
	public static readonly char[] Kinds = { Work, Night, Off, Holiday, Recovery };

	public static readonly (string key, string cycle)[] Presets =
	{
		("Classic", ""),
		("5_2", "WWWWWOO"),
		("6_1", "WWWWWWO"),
		("2_2", "WWOO"),
		("Shifts", "WWNNOOOO"),
		("Custom", null),
	};

	public static Color ColorOf(char k)
	{
		switch (k)
		{
			case Night: return new Color(0.46f, 0.33f, 0.72f);
			case Off: return new Color(0.30f, 0.62f, 0.36f);
			case Holiday: return new Color(0.86f, 0.70f, 0.20f);
			case Recovery: return new Color(0.86f, 0.46f, 0.22f);
			default: return new Color(0.26f, 0.46f, 0.78f);
		}
	}

	public static string Label(char k) => ("CF2S_Kind_" + k).Translate();
	public static string Short(char k) => ("CF2S_KindShort_" + k).Translate();

	public static string Clean(string cycle)
	{
		if (cycle == null)
			return "";
		// Czech letters too: P = work, V = off
		var c = new string(cycle.ToUpperInvariant().Select(x => x == 'P' ? Work : x == 'V' ? Off : x).Where(x => x == Work || x == Night || x == Off).ToArray());
		return c.Length > 28 ? c.Substring(0, 28) : c;
	}

	public static string CycleOf(string key)
	{
		if (key == "Custom")
			return Clean(SchedulesMod.S?.customCycle);
		foreach (var p in Presets)
			if (p.key == key)
				return p.cycle ?? "";
		return "";
	}

	public static string CycleLabel(string key) => key == null ? (string)"CF2S_CycleDefault".Translate() : (string)("CF2S_Cycle_" + key).Translate();

	// the local day number of the map (day 0 = start of the world)
	public static int Today(Map map)
	{
		float lon = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
		return (int)((Find.TickManager.TicksAbs + GenDate.LocalTicksOffsetFromLongitude(lon)) / GenDate.TicksPerDay);
	}

	public static string DayLabel(Map map, int day)
	{
		float lon = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
		long abs = (long)day * GenDate.TicksPerDay - GenDate.LocalTicksOffsetFromLongitude(lon) + GenDate.TicksPerDay / 2;
		return (GenDate.DayOfQuadrum(abs, lon) + 1) + ". " + GenDate.Quadrum(abs, lon).LabelShort();
	}

	public static bool FirstOfQuadrum(Map map, int day)
	{
		float lon = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
		long abs = (long)day * GenDate.TicksPerDay - GenDate.LocalTicksOffsetFromLongitude(lon) + GenDate.TicksPerDay / 2;
		return GenDate.DayOfQuadrum(abs, lon) == 0;
	}

	public static string PawnCycleKey(SchedState st, Pawn p) => st.cycle.TryGetValue(p.thingIDNumber, out string k) ? k : null;

	public static string PawnCycle(SchedState st, Pawn p)
	{
		string key = PawnCycleKey(st, p) ?? SchedulesMod.S?.defaultCycle ?? "Classic";
		return CycleOf(key);
	}

	// the kind of a day and where it comes from (for the tooltip)
	public static char KindOf(SchedState st, Map map, Pawn p, int day, out string source)
	{
		string key = p.thingIDNumber + ":" + day;
		if (st.dayManual.TryGetValue(key, out string m) && !m.NullOrEmpty())
		{
			source = "CF2S_SrcManual".Translate();
			return m[0];
		}
		if (st.holidays.Contains(day) || (SchedulesMod.S.autoHolidays && FirstOfQuadrum(map, day)))
		{
			source = "CF2S_SrcHoliday".Translate();
			return Holiday;
		}
		if (st.recovery.Contains(key))
		{
			source = "CF2S_SrcRecovery".Translate();
			return Recovery;
		}
		string cycle = PawnCycle(st, p);
		if (cycle.Length == 0)
		{
			source = "CF2S_SrcClassic".Translate();
			return NightOwl(p) || (st.scheduled.TryGetValue(p.thingIDNumber, out string s) && s != null && s.StartsWith("N")) ? Night : Work;
		}
		st.phase.TryGetValue(p.thingIDNumber, out int ph);
		char c = cycle[((day + ph) % cycle.Length + cycle.Length) % cycle.Length];
		if (c == Work && NightOwl(p))
			c = Night;
		source = "CF2S_SrcCycle".Translate(CycleLabel(PawnCycleKey(st, p) ?? SchedulesMod.S.defaultCycle));
		return c;
	}

	private static TraitDef owl;

	public static bool NightOwl(Pawn p)
	{
		owl ??= DefDatabase<TraitDef>.GetNamedSilentFail("NightOwl");
		return owl != null && p.story?.traits != null && p.story.traits.HasTrait(owl);
	}

	// S sleep · J recreation · A anything · W work
	public static string OffPattern()
	{
		// sleep a bit longer (23–7), meals and chores in "anything", the rest free
		return Meals("SSSSSSSMJJJJMAJJJJJMAAJS");
	}

	public static string HolidayPattern()
	{
		// a long sleep-in and recreation almost all day
		return Meals("SSSSSSSSMJJJJMJJJJJJJMJS");
	}

	public static string RecoveryPattern()
	{
		// rest: 10 hours of sleep, quiet hours in between
		return Meals("SSSSSSSMAJJMAJJAAMJASSSS");
	}

	private static string Meals(string s)
	{
		if (s.Length != 24)
			Log.ErrorOnce("[Steward] calendar pattern length " + s.Length, 0x5d0020);
		return SchedulesMod.S != null && SchedulesMod.S.mealHours ? s : s.Replace('M', 'A');
	}

	// a colonist gets a free day spread over the cycle: the phase staggers people so the colony is
	// never off all at once
	public static void EnsurePhases(SchedState st, List<Pawn> pawns)
	{
		int i = st.phase.Count;
		foreach (Pawn p in pawns)
			if (!st.phase.ContainsKey(p.thingIDNumber))
				st.phase[p.thingIDNumber] = i++ * 3;
	}

	public static void Forget(SchedState st, int today)
	{
		foreach (string k in st.dayManual.Keys.ToList())
			if (DayOf(k) < today - 1)
				st.dayManual.Remove(k);
		st.recovery.RemoveWhere(k => DayOf(k) < today - 1);
		st.holidays.RemoveWhere(d => d < today - 1);
	}

	private static int DayOf(string key)
	{
		int i = key.IndexOf(':');
		return i > 0 && int.TryParse(key.Substring(i + 1), out int d) ? d : int.MinValue;
	}

	// ---- after an attack the fighters and the wounded get a recovery day ----
	private static readonly Dictionary<Map, (int start, HashSet<int> fought)> threat = new Dictionary<Map, (int, HashSet<int>)>();

	public static void ResetGame() => threat.Clear();

	public static void WatchThreat(ForemanMapComponent comp, Map map)
	{
		if (SchedulesMod.S == null || !SchedulesMod.S.recoveryDays)
		{
			threat.Remove(map);
			return;
		}
		int now = Find.TickManager.TicksGame;
		bool danger = GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: false);
		if (danger)
		{
			if (!threat.TryGetValue(map, out var t))
				threat[map] = t = (now, new HashSet<int>());
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				if (p.Drafted || (p.mindState != null && now - p.mindState.lastHarmTick < 2500))
					t.fought.Add(p.thingIDNumber);
			return;
		}
		if (!threat.TryGetValue(map, out var done))
			return;
		threat.Remove(map);
		if (now - done.start < 2500 || done.fought.Count == 0)
			return;   // a short skirmish is no reason for a day off
		SchedState st = SchedState.Of(comp);
		int tomorrow = Today(map) + 1;
		var names = new List<string>();
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (!done.fought.Contains(p.thingIDNumber))
				continue;
			if (st.recovery.Add(p.thingIDNumber + ":" + tomorrow))
				names.Add(p.LabelShort);
		}
		if (names.Count > 0)
			Messages.Message("CF2S_RecoveryMsg".Translate(string.Join(", ", names)), MessageTypeDefOf.PositiveEvent, historical: false);
	}
}

public sealed class Window_StewardCalendar : Window
{
	private const int Days = 15;
	private Vector2 scroll;
	private readonly Map map;

	public override Vector2 InitialSize => new Vector2(Mathf.Min(1180f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 40f));

	public Window_StewardCalendar(Map map)
	{
		this.map = map;
		doCloseX = true;
		doCloseButton = false;
		closeOnClickedOutside = false;
		absorbInputAroundWindow = false;
		draggable = true;
		resizeable = true;
	}

	public override void DoWindowContents(Rect r)
	{
		ForemanMapComponent comp = map != null ? ForemanMapComponent.For(map) : null;
		if (comp == null)
		{
			Widgets.Label(r, "CF2S_CalNoMap".Translate());
			return;
		}
		SchedState st = SchedState.Of(comp);
		SchedulesSettings s = SchedulesMod.S;
		float y = r.y;
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, y, r.width - 40f, 34f), "CF2S_CalTitle".Translate());
		Text.Font = GameFont.Small;
		y += 38f;

		// colony settings row
		float x = r.x;
		Widgets.Label(new Rect(x, y + 4f, 150f, 26f), "CF2S_CalColony".Translate());
		x += 150f;
		if (Widgets.ButtonText(new Rect(x, y, 230f, 28f), DayPlan.CycleLabel(s.defaultCycle)))
			Find.WindowStack.Add(new FloatMenu(DayPlan.Presets.Select(p => new FloatMenuOption(DayPlan.CycleLabel(p.key) + Sample(DayPlan.CycleOf(p.key)), () => { s.defaultCycle = p.key; s.Write(); })).ToList()));
		x += 240f;
		bool hol = s.autoHolidays;
		Widgets.CheckboxLabeled(new Rect(x, y + 2f, 260f, 26f), "CF2S_CalAutoHoliday".Translate(), ref s.autoHolidays);
		x += 270f;
		bool rec = s.recoveryDays;
		Widgets.CheckboxLabeled(new Rect(x, y + 2f, 260f, 26f), "CF2S_CalRecovery".Translate(), ref s.recoveryDays);
		if (hol != s.autoHolidays || rec != s.recoveryDays)
			s.Write();
		y += 34f;
		if (s.defaultCycle == "Custom")
		{
			Widgets.Label(new Rect(r.x, y + 4f, 150f, 26f), "CF2S_CalCustom".Translate());
			string before = s.customCycle ?? "";
			string after = Widgets.TextField(new Rect(r.x + 150f, y, 230f, 28f), before);
			if (after != before)
			{
				s.customCycle = DayPlan.Clean(after);
				s.Write();
			}
			GUI.color = Color.gray;
			Widgets.Label(new Rect(r.x + 390f, y + 4f, r.width - 390f, 26f), "CF2S_CalCustomHint".Translate());
			GUI.color = Color.white;
			y += 34f;
		}

		// legend
		x = r.x;
		foreach (char k in DayPlan.Kinds)
		{
			Rect chip = new Rect(x, y, 22f, 22f);
			Widgets.DrawBoxSolid(chip, DayPlan.ColorOf(k));
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(chip, DayPlan.Short(k));
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(x + 26f, y, 130f, 22f), DayPlan.Label(k));
			Text.Anchor = TextAnchor.UpperLeft;
			x += 160f;
		}
		y += 30f;
		if (!s.autoSchedule || !comp.auto)
		{
			GUI.color = new Color(1f, 0.75f, 0.4f);
			Widgets.Label(new Rect(r.x, y, r.width, 24f), "CF2S_CalOff".Translate());
			GUI.color = Color.white;
			y += 26f;
		}

		// the table
		int today = DayPlan.Today(map);
		var pawns = map.mapPawns.FreeColonistsSpawned.Where(p => p.timetable != null).OrderBy(p => p.LabelShort).ToList();
		DayPlan.EnsurePhases(st, pawns.OrderBy(p => p.thingIDNumber).ToList());
		float nameW = 150f, cycW = 150f;
		float cellW = Mathf.Max(28f, (r.width - nameW - cycW - 20f) / Days);
		float rowH = 28f;
		// header: the days (click = holiday for everybody)
		for (int i = 0; i < Days; i++)
		{
			int day = today + i;
			Rect h = new Rect(r.x + nameW + cycW + i * cellW, y, cellW - 2f, 36f);
			if (i == 0)
				Widgets.DrawHighlight(h);
			Text.Font = GameFont.Tiny;
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(h, DayPlan.DayLabel(map, day));
			Text.Anchor = TextAnchor.UpperLeft;
			Text.Font = GameFont.Small;
			bool isHol = st.holidays.Contains(day);
			if (isHol)
				Widgets.DrawBoxSolid(new Rect(h.x, h.yMax - 4f, h.width, 4f), DayPlan.ColorOf(DayPlan.Holiday));
			TooltipHandler.TipRegion(h, (isHol ? "CF2S_CalHolidayOff" : "CF2S_CalHolidayOn").Translate());
			if (Widgets.ButtonInvisible(h))
			{
				if (!st.holidays.Add(day))
					st.holidays.Remove(day);
			}
		}
		y += 40f;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 30f);
		Rect view = new Rect(0f, 0f, outR.width - 18f, pawns.Count * rowH);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int row = 0; row < pawns.Count; row++)
		{
			Pawn p = pawns[row];
			float ry = row * rowH;
			if (row % 2 == 0)
				Widgets.DrawLightHighlight(new Rect(0f, ry, view.width, rowH));
			bool manualSched = st.scheduled.TryGetValue(p.thingIDNumber, out string sc) && sc == "manual";
			Rect nr = new Rect(4f, ry, nameW - 6f, rowH);
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(nr, p.LabelShort.Truncate(nr.width));
			Text.Anchor = TextAnchor.UpperLeft;
			if (manualSched)
				TooltipHandler.TipRegion(nr, "CF2S_CalManualSched".Translate());
			if (Widgets.ButtonInvisible(nr))
				CameraJumper.TryJumpAndSelect(p);
			string key = DayPlan.PawnCycleKey(st, p);
			if (Widgets.ButtonText(new Rect(nameW, ry + 2f, cycW - 6f, rowH - 4f), DayPlan.CycleLabel(key).Truncate(cycW - 16f)))
			{
				var opts = new List<FloatMenuOption> { new FloatMenuOption(DayPlan.CycleLabel(null), () => st.cycle.Remove(p.thingIDNumber)) };
				foreach (var pr in DayPlan.Presets)
				{
					string k = pr.key;
					opts.Add(new FloatMenuOption(DayPlan.CycleLabel(k) + Sample(DayPlan.CycleOf(k)), () => st.cycle[p.thingIDNumber] = k));
				}
				opts.Add(new FloatMenuOption("CF2S_CalShift".Translate(), () => st.phase[p.thingIDNumber] = (st.phase.TryGetValue(p.thingIDNumber, out int ph) ? ph : 0) + 1));
				Find.WindowStack.Add(new FloatMenu(opts));
			}
			for (int i = 0; i < Days; i++)
			{
				int day = today + i;
				char kind = DayPlan.KindOf(st, map, p, day, out string src);
				Rect c = new Rect(nameW + cycW + i * cellW, ry + 2f, cellW - 2f, rowH - 4f);
				Widgets.DrawBoxSolid(c, DayPlan.ColorOf(kind) * (i == 0 ? 1f : 0.85f));
				if (st.dayManual.ContainsKey(p.thingIDNumber + ":" + day))
					Widgets.DrawBox(c, 2);
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(c, DayPlan.Short(kind));
				Text.Anchor = TextAnchor.UpperLeft;
				TooltipHandler.TipRegion(c, DayPlan.DayLabel(map, day) + ": " + DayPlan.Label(kind) + "\n" + src);
				if (Widgets.ButtonInvisible(c))
				{
					string dk = p.thingIDNumber + ":" + day;
					var opts = new List<FloatMenuOption>();
					foreach (char k in DayPlan.Kinds)
					{
						char kk = k;
						opts.Add(new FloatMenuOption(DayPlan.Label(kk), () => st.dayManual[dk] = kk.ToString()));
					}
					if (st.dayManual.ContainsKey(dk))
						opts.Add(new FloatMenuOption("CF2S_CalByPlan".Translate(), () => st.dayManual.Remove(dk)));
					Find.WindowStack.Add(new FloatMenu(opts));
				}
			}
		}
		Widgets.EndScrollView();
		GUI.color = Color.gray;
		Widgets.Label(new Rect(r.x, r.yMax - 26f, r.width, 26f), "CF2S_CalFoot".Translate());
		GUI.color = Color.white;
	}

	private static string Sample(string cycle) => cycle.NullOrEmpty() ? "" : "  (" + string.Concat(cycle.Select(DayPlan.Short)) + ")";
}
