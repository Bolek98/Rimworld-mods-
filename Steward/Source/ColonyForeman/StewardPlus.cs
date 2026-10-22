using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.32: extras – safe area during attacks, light work for the hurt,
// passion work for low mood, learning, master crafter on quality bills,
// automatic schedules, skill-gap alert, saved priority profiles, plan history.
// ---------------------------------------------------------------------------
public sealed class PriorityProfile : IExposable
{
	public string name;
	public List<string> entries = new List<string>();   // "pawn name|WorkTypeDef|priority"

	public void ExposeData()
	{
		Scribe_Values.Look(ref name, "name");
		Scribe_Collections.Look(ref entries, "entries", LookMode.Value);
		if (entries == null)
			entries = new List<string>();
	}
}

public static class StewardPlus
{
	private static ForemanSettings S => ForemanMod.Settings;

	// ---- 1. safe area during attacks ---------------------------------------
	// 2.52.3: no infestation / escaped entity – they come out exactly in the roofed rooms of the safe area
	public const Crisis ThreatMask = Crisis.Raid | Crisis.Manhunters | Crisis.Siege;

	public static void SafeAreaTick(ForemanMapComponent comp, Map map, bool stewardOff = false)
	{
		if (S == null)
			return;
		bool threat = !stewardOff && S.raidSafeArea && (comp.CurrentCrisis & ThreatMask) != 0 && map.IsPlayerHome;
		if (threat && !comp.safeActive)
		{
			Area_Allowed area = SafeArea(map, true);
			if (area == null || area.TrueCount < 10)
				return;
			comp.safePawns.Clear();
			comp.safeOldArea.Clear();
			// 2.35.6: a copy – changing an area restriction can change the pawn list
			comp.safeActive = true;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
			{
				// 2.35.5: a caravan (or ritual) keeps its route
				if (p.playerSettings == null || p.Drafted || p.GetLord() != null)
					continue;
				comp.safePawns.Add(p);
				comp.safeOldArea.Add(p.playerSettings.AreaRestrictionInPawnCurrentMap?.ID ?? -1);
				p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
			}
			Messages.Message("CF_SafeOn".Translate(comp.safePawns.Count), MessageTypeDefOf.CautionInput, historical: false);
		}
		else if (!threat && comp.safeActive)
		{
			Area_Allowed area = SafeArea(map, false);
			int n = 0;
			for (int i = 0; i < comp.safePawns.Count; i++)
			{
				Pawn p = comp.safePawns[i];
				if (p?.playerSettings == null || p.Dead || p.MapHeld != map)
					continue;
				if (area != null && p.playerSettings.AreaRestrictionInPawnCurrentMap != area)
					continue;   // you changed it yourself meanwhile
				int id = i < comp.safeOldArea.Count ? comp.safeOldArea[i] : -1;
				p.playerSettings.AreaRestrictionInPawnCurrentMap = id < 0 ? null : map.areaManager.AllAreas.FirstOrDefault(a => a.ID == id);
				n++;
			}
			comp.safePawns.Clear();
			comp.safeOldArea.Clear();
			comp.safeActive = false;
			if (n > 0)
				Messages.Message("CF_SafeOff".Translate(n), MessageTypeDefOf.PositiveEvent, historical: false);
		}
	}

	private static Area_Allowed SafeArea(Map map, bool rebuild)
	{
		string label = "CF_SafeAreaName".Translate();
		Area_Allowed a = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => x.Label == label);
		if (a == null)
		{
			if (!rebuild || !map.areaManager.TryMakeNewAllowed(out a))
				return null;
			a.RenamableLabel = label;
		}
		if (rebuild)
		{
			foreach (IntVec3 c in a.ActiveCells.ToList())
				a[c] = false;
			Area_Home home = map.areaManager.Home;
			foreach (IntVec3 c in home.ActiveCells)
			{
				if (!c.Roofed(map))
					continue;
				Room r = c.GetRoom(map);
				if (r != null && !r.PsychologicallyOutdoors && !r.TouchesMapEdge)
					a[c] = true;
			}
		}
		return a;
	}

	// ---- 2.–4. changes to the plan -----------------------------------------
	private static readonly HashSet<string> heavy = new HashSet<string> { "Mining", "Construction", "Hauling", "PlantCutting", "Hunting", "Growing", "Smithing" };

	// 2.32.2: a hurt colonist is not pulled back onto heavy work by your minimum
	public static bool KeepLight(Pawn p, WorkTypeDef w)
	{
		ForemanSettings s = S;
		return s != null && s.lightWorkInjured && heavy.Contains(w.defName) && Hurt(p);
	}

	public static void AdjustPlan(Map map, ForemanMapComponent comp, Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan, int max)
	{
		ForemanSettings s = S;
		if (s == null || max < 2)
			return;
		foreach (var kv in plan)
		{
			Pawn p = kv.Key;
			Dictionary<WorkTypeDef, int> jobs = kv.Value;
			try
			{
				// 2. light work while hurt
				if (s.lightWorkInjured && Hurt(p))
					foreach (WorkTypeDef w in jobs.Keys.ToList())
						if (jobs[w] > 0 && jobs[w] < max && heavy.Contains(w.defName) && !WorkPolicies.Get(w).emergency)
						{
							jobs[w] = max;
							Note(comp, p, w, "CF_TipHurt".Translate());
						}
				// 3. passion work when the mood is low
				if (s.moodPassion && LowMood(p))
					foreach (WorkTypeDef w in jobs.Keys.ToList())
						if (jobs[w] > 1 && BestPassion(p, w) == Passion.Major)
						{
							jobs[w] = Math.Max(1, jobs[w] - 1);
							Note(comp, p, w, "CF_TipMoodPassion".Translate());
						}
			}
			catch
			{
			}
		}
		// 4. learning: a colonist with a passion but little skill gets the work
		if (s.learningSlots > 0)
		{
			var types = plan.Values.SelectMany(j => j.Keys).Distinct().Where(w => w.relevantSkills != null && w.relevantSkills.Count > 0).ToList();
			foreach (WorkTypeDef w in types)
			{
				var learners = plan.Where(kv => kv.Value.TryGetValue(w, out int v) && (v == 0 || v == max) && !kv.Key.WorkTypeIsDisabled(w)
				                               && BestPassion(kv.Key, w) != Passion.None && SkillOf(kv.Key, w) < s.learningMaxSkill && !Hurt(kv.Key))
					.OrderByDescending(kv => (int)BestPassion(kv.Key, w)).ThenByDescending(kv => SkillOf(kv.Key, w))
					.Take(s.learningSlots).ToList();
				foreach (var kv in learners)
				{
					kv.Value[w] = Math.Max(1, max - 1);
					Note(comp, kv.Key, w, "CF_TipLearning".Translate());
				}
			}
		}
	}

	private static void Note(ForemanMapComponent comp, Pawn p, WorkTypeDef w, string text)
	{
		if (comp.reasons.TryGetValue(p.thingIDNumber, out var texts) && texts.TryGetValue(w, out string t))
			texts[w] = t + "\n" + text.Colorize(ColorLibrary.Gold);
	}

	public static bool Hurt(Pawn p)
	{
		if (p.health?.hediffSet == null)
			return false;
		return p.health.hediffSet.PainTotal >= 0.25f || (p.health.summaryHealth.SummaryHealthPercent < 0.75f && p.health.hediffSet.hediffs.Any(h => h is Hediff_Injury));
	}

	private static bool LowMood(Pawn p)
	{
		if (p.needs?.mood == null || p.mindState?.mentalBreaker == null)
			return false;
		return p.needs.mood.CurLevel < p.mindState.mentalBreaker.BreakThresholdMinor + 0.05f;
	}

	private static Passion BestPassion(Pawn p, WorkTypeDef w)
	{
		Passion best = Passion.None;
		if (p.skills == null || w.relevantSkills == null)
			return best;
		foreach (SkillDef sd in w.relevantSkills)
		{
			SkillRecord r = p.skills.GetSkill(sd);
			if (r != null && !r.TotallyDisabled && (int)r.passion > (int)best)
				best = r.passion;
		}
		return best;
	}

	private static int SkillOf(Pawn p, WorkTypeDef w)
	{
		if (p.skills == null || w.relevantSkills == null || w.relevantSkills.Count == 0)
			return 0;
		return (int)w.relevantSkills.Average(sd => p.skills.GetSkill(sd)?.Level ?? 0);
	}

	// ---- 5. master crafter on quality bills -------------------------------
	private static Dictionary<ThingDef, WorkTypeDef> benchWork;

	public static WorkTypeDef WorkAt(ThingDef bench)
	{
		if (benchWork == null)
		{
			benchWork = new Dictionary<ThingDef, WorkTypeDef>();
			foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
				if (g?.workType != null && g.fixedBillGiverDefs != null)
					foreach (ThingDef d in g.fixedBillGiverDefs)
						if (!benchWork.ContainsKey(d))
							benchWork[d] = g.workType;
		}
		benchWork.TryGetValue(bench, out WorkTypeDef w);
		return w;
	}

	// 2.52.3: the minimum skill the steward last wrote – a different value means you changed it yourself
	private static readonly Dictionary<string, int> wroteMin = new Dictionary<string, int>();

	public static void MasterBills(ForemanMapComponent comp, Map map)
	{
		bool on = S != null && S.masterBills;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null)
				continue;
			WorkTypeDef work = WorkAt(b.def);
			foreach (Bill bill in g.BillStack)
			{
				if (!(bill is Bill_Production bp) || bp.recipe == null)
					continue;
				string id = bp.GetUniqueLoadID();
				bool ours = comp.masterBills.Contains(id);
				// 2.32.5: your minimum of 2+ colonists for this work wins – nobody is
				// bound to one colonist then
				if (!on || (work != null && S.MyRulesOn && S.MinFor(work) >= 2))
				{
					if (ours)
					{
						bp.allowedSkillRange = new IntRange(0, bp.allowedSkillRange.max);
						comp.masterBills.Remove(id);
					}
					continue;
				}
				ThingDef prod = bp.recipe.ProducedThingDef;
				if (prod == null || bp.recipe.workSkill == null || work == null || !(prod.IsWeapon || prod.IsApparel) || !prod.HasComp(typeof(CompQuality)))
					continue;
				// 2.52.3: a colonist, slaves-only or mechs-only choice is yours – the bill is left alone for good
				if (bp.PawnRestriction != null || bp.SlavesOnly || bp.MechsOnly || bp.NonMechsOnly)
				{
					if (ours)
						comp.masterBills.Remove(id);
					continue;
				}
				// 2.50.4: the best crafters share it: skill at most 3 below the best
				List<int> levels = map.mapPawns.FreeColonistsSpawned
					.Where(p => !p.WorkTypeIsDisabled(work) && p.workSettings != null && p.workSettings.WorkIsActive(work) && !p.Downed)
					.Select(p => p.skills?.GetSkill(bp.recipe.workSkill)?.Level ?? 0).OrderByDescending(x => x).ToList();
				if (levels.Count == 0)
					continue;
				// 2.52.3: always at least two who may do it (before one crafter far ahead locked it to him alone),
				// and never above your maximum
				int min = Math.Max(0, levels[0] - 3);
				if (levels.Count >= 2)
					min = Math.Min(min, levels[1]);
				min = Math.Min(min, bp.allowedSkillRange.max);
				if (!ours && bp.allowedSkillRange.min != 0)
					continue;   // your own skill range
				if (ours && wroteMin.TryGetValue(id, out int wrote) && bp.allowedSkillRange.min != wrote)
				{
					comp.masterBills.Remove(id);   // you changed the range – yours from now on
					wroteMin.Remove(id);
					continue;
				}
				if (bp.allowedSkillRange.min != min)
					bp.allowedSkillRange = new IntRange(min, bp.allowedSkillRange.max);
				if (wroteMin.Count > 2000)
					wroteMin.Clear();
				wroteMin[id] = min;
				if (!ours)
					comp.masterBills.Add(id);
			}
		}
	}

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
		bool resetDone = comp.manualReset2492;
		int k = 0;
		int now = Find.TickManager.TicksGame;
		foreach (Pawn p in pawns)
		{
			bool night = (owl != null && p.story?.traits != null && p.story.traits.HasTrait(owl));
			if (!night && S.nightShift && pawns.Count >= 6 && k++ % 4 == 3)
				night = true;
			int id = p.thingIDNumber;
			comp.scheduled.TryGetValue(id, out string had);
			// 2.49.1: older versions marked a colonist "manual" whenever ANY mod changed the schedule.
			// Once per save every colonist goes back to Steward; from now on only your own changes count.
			if (had == "manual" && !comp.manualReset2492)
				had = null;
			if (had == "manual")
				continue;
			// state: "<D|N>|<joy hours>|<pattern>|<next check tick>"; old saves have only "D" / "N"
			string[] st = (had ?? "").Split('|');
			int joy = st.Length >= 2 && int.TryParse(st[1], out int j0) ? j0 : JoyStart;
			string lastPattern = st.Length >= 3 ? st[2] : null;
			int nextCheck = st.Length >= 4 && int.TryParse(st[3], out int n0) ? n0 : 0;
			// you changed the schedule yourself (hours held by the care rules do not count)
			if (lastPattern != null && lastPattern.Length == 24 && !comp.schedHold.ContainsKey(id) && Read(p) != lastPattern)
			{
				// 2.49.1: only when YOU had the schedule tab open – a change made by another mod is undone
				if (ScheduleTabWatch.EditedByYou.Remove(id))
				{
					comp.scheduled[id] = "manual";
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
				if (!comp.schedHold.ContainsKey(id))
					Apply(p, want);
				else
					want = lastPattern ?? want;   // wait until the care rules give their hours back
			}
			comp.scheduled[id] = (night ? "N" : "D") + "|" + joy + "|" + want + "|" + nextCheck;
		}
		if (!resetDone)
		{
			comp.manualReset2492 = true;
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

	// ---- 8. saved priority profiles ----------------------------------------
	public static void SaveProfile(Map map)
	{
		var prof = new PriorityProfile { name = "CF_ProfileName".Translate(S.profiles.Count + 1, GenDate.DateFullStringAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile))) };
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
				if (w.visible && !p.WorkTypeIsDisabled(w))
					prof.entries.Add(p.LabelShort + "|" + w.defName + "|" + p.workSettings.GetPriority(w));
		S.profiles.Add(prof);
		ForemanMod.Instance?.WriteSettings();
		Messages.Message("CF_ProfileSaved".Translate(prof.name), MessageTypeDefOf.SilentInput, historical: false);
	}

	public static void LoadProfile(ForemanMapComponent comp, Map map, PriorityProfile prof)
	{
		var byName = map.mapPawns.FreeColonistsSpawned.GroupBy(p => p.LabelShort).ToDictionary(g => g.Key, g => g.First());
		int n = 0;
		foreach (string e in prof.entries)
		{
			string[] parts = e.Split('|');
			if (parts.Length != 3 || !byName.TryGetValue(parts[0], out Pawn p) || !int.TryParse(parts[2], out int prio))
				continue;
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(parts[1]);
			if (w == null || p.WorkTypeIsDisabled(w))
				continue;
			p.workSettings.SetPriority(w, prio);
			n++;
		}
		comp.auto = false;   // otherwise the next plan would overwrite it
		Messages.Message("CF_ProfileLoaded".Translate(prof.name, n), MessageTypeDefOf.SilentInput, historical: false);
	}

	public static List<FloatMenuOption> ProfileMenu(ForemanMapComponent comp, Map map)
	{
		var list = new List<FloatMenuOption> { new FloatMenuOption("CF_ProfileSave".Translate(), () => SaveProfile(map)) };
		foreach (PriorityProfile p in S.profiles.ToList())
		{
			PriorityProfile local = p;
			list.Add(new FloatMenuOption("CF_ProfileLoad".Translate(p.name), () => LoadProfile(comp, map, local)));
		}
		foreach (PriorityProfile p in S.profiles.ToList())
		{
			PriorityProfile local = p;
			list.Add(new FloatMenuOption("CF_ProfileDelete".Translate(p.name), () =>
			{
				S.profiles.Remove(local);
				ForemanMod.Instance?.WriteSettings();
			}));
		}
		return list;
	}

	// ---- 9. history --------------------------------------------------------
	public static void AddHistory(ForemanMapComponent comp, Map map, int changed, bool manual)
	{
		var sb = new StringBuilder();
		sb.Append(GenDate.DateFullStringWithHourAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile)));
		sb.Append(" – ").Append(Styles.Label(comp.style));
		if (manual)
			sb.Append(" (").Append("CF_HistManual".Translate()).Append(")");
		sb.Append(": ").Append("CF_HistChanged".Translate(changed));
		if (comp.CurrentCrisis != Crisis.None)
			sb.Append(" | ").Append("CF_HistCrisis".Translate(MapDemand.Label(comp.CurrentCrisis)));
		if (comp.situations != null && comp.situations.Count > 0)
			sb.Append(" | ").Append("CF_HistSit".Translate(string.Join(", ", comp.situations.Select(x => SituationRules.Label(x.id)))));
		comp.history.Insert(0, sb.ToString());
		while (comp.history.Count > 60)
			comp.history.RemoveAt(comp.history.Count - 1);
	}
}

public class Dialog_StewardHistory : Window
{
	private readonly ForemanMapComponent comp;
	private Vector2 scroll;

	public Dialog_StewardHistory(ForemanMapComponent comp)
	{
		this.comp = comp;
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = false;
	}

	public override Vector2 InitialSize => new Vector2(760f, 560f);

	public override void DoWindowContents(Rect r)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width, 34f), "CF_HistTitle".Translate());
		Text.Font = GameFont.Small;
		Rect outR = new Rect(r.x, r.y + 40f, r.width, r.height - 40f);
		float w = outR.width - 18f;
		float h = 0f;
		foreach (string e in comp.history)
			h += Text.CalcHeight(e, w) + 6f;
		Rect view = new Rect(0f, 0f, w, Mathf.Max(h, outR.height));
		Widgets.BeginScrollView(outR, ref scroll, view);
		float y = 0f;
		bool odd = false;
		foreach (string e in comp.history)
		{
			float eh = Text.CalcHeight(e, w);
			Rect row = new Rect(0f, y, w, eh + 4f);
			if (odd)
				Widgets.DrawLightHighlight(row);
			odd = !odd;
			Widgets.Label(new Rect(4f, y + 2f, w - 8f, eh), e);
			y += eh + 6f;
		}
		if (comp.history.Count == 0)
			Widgets.Label(new Rect(0f, 0f, w, 30f), "CF_HistEmpty".Translate());
		Widgets.EndScrollView();
	}
}

// ---- 7. / 10. skill gap alert (every map of the colony) ------------------
public class Alert_StewardSkillGap : Alert
{
	private int cacheTick = -9999;
	private string cacheText;
	private List<GlobalTargetInfo> targets = new List<GlobalTargetInfo>();

	public Alert_StewardSkillGap()
	{
		defaultPriority = AlertPriority.Medium;
	}

	public override string GetLabel() => "CF_GapLabel".Translate();

	public override TaggedString GetExplanation() => cacheText ?? "";

	public override AlertReport GetReport()
	{
		if (ForemanMod.Settings == null || !ForemanMod.Settings.skillAlert)
			return AlertReport.Inactive;
		int now = Find.TickManager.TicksGame;
		if (now - cacheTick >= 600)
		{
			cacheTick = now;
			cacheText = Build();
		}
		return string.IsNullOrEmpty(cacheText) ? AlertReport.Inactive : AlertReport.CulpritsAre(targets);
	}

	private string Build()
	{
		targets.Clear();
		var sb = new StringBuilder();
		foreach (Map map in Find.Maps)
		{
			if (!map.IsPlayerHome)
				continue;
			List<Pawn> cols = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Downed).ToList();
			if (cols.Count < 2)
				continue;
			var lines = new List<string>();
			Check(map, cols, WorkTypeDefOf.Doctor, SkillDefOf.Medicine, cols.Count >= 5 ? 2 : 1, lines);
			Check(map, cols, DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking"), SkillDefOf.Cooking, cols.Count >= 6 ? 2 : 1, lines);
			Check(map, cols, WorkTypeDefOf.Construction, SkillDefOf.Construction, 1, lines);
			Check(map, cols, WorkTypeDefOf.Growing, SkillDefOf.Plants, 1, lines);
			Check(map, cols, WorkTypeDefOf.Firefighter, null, 2, lines);
			if (map.mapPawns.PrisonersOfColonySpawnedCount > 0)
				Check(map, cols, WorkTypeDefOf.Warden, SkillDefOf.Social, 1, lines);
			if (map.mapPawns.SpawnedColonyAnimals.Count >= 3)
				Check(map, cols, WorkTypeDefOf.Handling, SkillDefOf.Animals, 1, lines);
			if (lines.Count == 0)
				continue;
			if (Find.Maps.Count(m => m.IsPlayerHome) > 1)
				sb.AppendLine(map.Parent?.LabelCap ?? map.ToString());
			foreach (string l in lines)
				sb.AppendLine(l);
			targets.Add(new GlobalTargetInfo(cols[0]));
		}
		return sb.ToString().TrimEnd();
	}

	private static void Check(Map map, List<Pawn> cols, WorkTypeDef w, SkillDef skill, int want, List<string> lines)
	{
		if (w == null)
			return;
		int n = cols.Count(p => !p.WorkTypeIsDisabled(w) && p.workSettings != null && p.workSettings.WorkIsActive(w));
		if (n >= want)
			return;
		string line = n == 0 ? "CF_GapNobody".Translate(w.labelShort.CapitalizeFirst()).ToString() : "CF_GapOnly".Translate(w.labelShort.CapitalizeFirst(), n).ToString();
		if (skill != null)
		{
			var good = map.mapPawns.PrisonersOfColonySpawned.Where(p => p.skills?.GetSkill(skill) != null && p.skills.GetSkill(skill).Level >= 6 && !p.WorkTypeIsDisabled(w))
				.OrderByDescending(p => p.skills.GetSkill(skill).Level).Take(3).Select(p => p.LabelShort + " (" + p.skills.GetSkill(skill).Level + ")").ToList();
			if (good.Count > 0)
				line += " " + "CF_GapRecruit".Translate(string.Join(", ", good));
		}
		lines.Add(line);
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
