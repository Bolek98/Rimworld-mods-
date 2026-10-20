using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

public sealed class ForemanMapComponent : MapComponent
{
	public bool auto;

	public ForemanStyle style = ForemanStyle.Balanced;

	// 2.19: the focus Balanced picked by itself (Balanced = none), since when
	// and why; the last raid (for the Defense focus after it)
	public ForemanStyle autoFocus = ForemanStyle.Balanced;
	public int autoFocusSince;
	public string autoFocusReason;
	public int autoFocusEval = -999999;
	public int lastRaidTick = -999999;

	private int nextRun;
	public int NextRunTick => nextRun;
	public int LastRunTick => lastRun;

	private Crisis crisis;

	private int lastColonists = -1;
	// 2.25.0: who was in the last plan – a newcomer (recruited, joined, healed
	// up, back from a caravan) gets his priorities within seconds
	private int lastRoster;

	private int lastHarvestable;

	private int ticker;

	private int lastRun = -999999;

	private int nextChangeCheck;
	private Crisis? crisisSeen;
	// 2.32: extras
	public bool safeActive;
	public List<Pawn> safePawns = new List<Pawn>();
	public List<int> safeOldArea = new List<int>();
	public List<string> masterBillList = new List<string>();
	public HashSet<string> masterBills = new HashSet<string>();
	public Dictionary<int, string> scheduled = new Dictionary<int, string>();
	public Dictionary<int, string> schedHold = new Dictionary<int, string>();   // 2.39
	public List<string> history = new List<string>();
	private int nextExtras;
	private int crisisSeenTick;
	private int[] workSnapshot;
	// 2.45: calm modes – when the steward last changed each cell ("pawnId|work")
	public Dictionary<string, CalmCell> calmCells = new Dictionary<string, CalmCell>();
	public bool planFull;

	public void PruneCalm(int now)
	{
		if (calmCells.Count < 64) return;
		var old = new List<string>();
		foreach (var kv in calmCells)
			if (now - kv.Value.tick > 180000)
				old.Add(kv.Key);
		foreach (string k in old)
			calmCells.Remove(k);
	}

	// Blueprints, marks (cut, mine, hunt, haul …), bills that want doing and
	// patients. A plan is made again when one of them appears out of nothing,
	// drops to nothing, or grows / shrinks by half (and by at least 5).
	private int[] WorkNow()
	{
		int blueprints = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count
		                 + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count
		                 + MapDemand.ConstructionMarks(map);
		int marks = map.designationManager.AllDesignations.Count;
		int bills = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (b is IBillGiver g && g.BillStack != null)
			{
				foreach (Bill bill in g.BillStack.Bills)
				{
					// 2.9.1: no ShouldDoNow here – for "do until X" bills it counts
					// the stock on the map, for every bill every half hour.
					if (!bill.suspended)
						bills++;
				}
			}
		}
		return new[] { blueprints, marks, bills, MapDemand.CountPatients(map) };
	}

	private bool WorkShifted()
	{
		int[] now = WorkNow();
		if (workSnapshot == null)
		{
			workSnapshot = now;
			return false;
		}
		for (int i = 0; i < now.Length; i++)
		{
			int a = workSnapshot[i], b = now[i];
			if ((a == 0) != (b == 0))
				return true;
			if (Math.Abs(b - a) >= 5 && Math.Abs(b - a) * 2 >= Math.Max(a, b))
				return true;
		}
		return false;
	}

	public MapDemand lastDemand;

	// 2.20: situations (Balanced) – what is going on now, when it was last
	// looked at and the result of a background check for the next plan
	public List<ActiveSituation> situations = new List<ActiveSituation>();
	private List<string> situationNames = new List<string>();   // saved
	private string sitSignature = "";
	private int sitEvalTick = -999999;
	private List<ActiveSituation> sitPending;
	private int sitPendingTick = -1;

	private HashSet<Situation> SituationSet()
	{
		var set = new HashSet<Situation>();
		if (situations.Count > 0)
		{
			foreach (ActiveSituation s in situations)
				set.Add(s.id);
		}
		else if (situationNames != null)
		{
			// after loading a save: the names, so nothing is announced twice
			foreach (string n in situationNames)
				if (Enum.TryParse(n, out Situation x))
					set.Add(x);
		}
		return set;
	}

	// background check: true when the situations (or the colonists they
	// need) changed and the work has to be planned again
	private bool SituationsChanged()
	{
		int now = Find.TickManager.TicksGame;
		sitEvalTick = now;
		if (!SituationRules.Active(this))
			return situations.Count > 0;
		List<ActiveSituation> found = SituationRules.Detect(map, crisis, SituationSet());
		sitPending = found;
		sitPendingTick = now;
		return SituationRules.Signature(found) != sitSignature;
	}

	// at every plan: the situations for this plan, with messages
	private void RefreshSituations()
	{
		int now = Find.TickManager.TicksGame;
		List<ActiveSituation> found;
		if (!SituationRules.Active(this))
			found = new List<ActiveSituation>();
		else if (sitPending != null && sitPendingTick == now)
			found = sitPending;
		else
			found = SituationRules.Detect(map, crisis, SituationSet());
		sitPending = null;
		sitEvalTick = now;
		HashSet<Situation> before = SituationSet();
		var after = new HashSet<Situation>(found.Select(x => x.id));
		ForemanSettings settings = ForemanMod.Settings;
		// no "over" messages when the situations were simply switched off
		if (settings != null && settings.crisisMessages && map.IsPlayerHome && SituationRules.Active(this))
		{
			var started = found.Where(x => !before.Contains(x.id)).ToList();
			var ended = before.Where(x => !after.Contains(x)).ToList();
			if (started.Count > 0)
			{
				string text = string.Join("; ", started.Select(x =>
				{
					string t = x.Label + " (" + x.reason + ")";
					string staff = x.Staffing();
					return staff.NullOrEmpty() ? t : t + " → " + staff;
				}));
				Messages.Message("CF_SitStart".Translate(text), MessageTypeDefOf.NeutralEvent, historical: false);
			}
			if (ended.Count > 0)
				Messages.Message("CF_SitEnd".Translate(string.Join(", ", ended.Select(x => SituationRules.Label(x)))), MessageTypeDefOf.PositiveEvent, historical: false);
		}
		situations = found;
		situationNames = found.Select(x => x.id.ToString()).ToList();
		sitSignature = SituationRules.Signature(found);
	}

	public readonly Dictionary<int, Dictionary<WorkTypeDef, string>> reasons = new Dictionary<int, Dictionary<WorkTypeDef, string>>();

	public Crisis CurrentCrisis => crisis;

	// 2.18: the last work plan (not saved – shown in the Steward overview)
	public int lastPlanTick = -1;
	public int lastPlanChanged;

	// 2.18: "Re-assign work now" – the same plan as the Work tab button, for
	// every map where the steward is on (settings window). Returns the number
	// of changed priorities, -1 when no map was planned.
	public static int ReassignAll(out int maps)
	{
		maps = 0;
		int changed = 0;
		if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
			return -1;
		foreach (Map m in Find.Maps)
		{
			ForemanMapComponent c = For(m);
			if (c == null || !c.auto || MapDemand.Workers(m) == 0)
				continue;
			try
			{
				changed += c.Run(manual: false);
				maps++;
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[Steward] re-assign failed: " + ex, 0x57e3a0e);
			}
		}
		return maps == 0 ? -1 : changed;
	}

	public ForemanMapComponent(Map map)
		: base(map)
	{
		auto = ForemanMod.Settings?.autoByDefault ?? true;
	}

	public static ForemanMapComponent For(Map map)
	{
		return map?.GetComponent<ForemanMapComponent>();
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref auto, "auto", defaultValue: false);
		// 2.32: extras
		Scribe_Values.Look(ref safeActive, "cfSafeActive", false);
		Scribe_Collections.Look(ref safePawns, "cfSafePawns", LookMode.Reference);
		Scribe_Collections.Look(ref safeOldArea, "cfSafeOldArea", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.Saving)
			masterBillList = masterBills.ToList();
		Scribe_Collections.Look(ref masterBillList, "cfMasterBills", LookMode.Value);
		Scribe_Collections.Look(ref scheduled, "cfScheduled", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref schedHold, "cfSchedHold", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref history, "cfHistory", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			safePawns ??= new List<Pawn>();
			safePawns.RemoveAll(p => p == null);
			safeOldArea ??= new List<int>();
			masterBills = new HashSet<string>(masterBillList ?? new List<string>());
			scheduled ??= new Dictionary<int, string>();
			schedHold ??= new Dictionary<int, string>();
			history ??= new List<string>();
		}
		Scribe_Values.Look(ref style, "style", ForemanStyle.Balanced);
		Scribe_Values.Look(ref nextRun, "nextRun", 0);
		Scribe_Values.Look(ref crisis, "crisis", Crisis.None);
		Scribe_Values.Look(ref autoFocus, "autoFocus", ForemanStyle.Balanced);
		Scribe_Values.Look(ref autoFocusSince, "autoFocusSince", 0);
		Scribe_Values.Look(ref autoFocusReason, "autoFocusReason");
		Scribe_Values.Look(ref lastRaidTick, "lastRaidTick", -999999);
		// 2.20: names of the active situations (kept up to date at every plan)
		Scribe_Collections.Look(ref situationNames, "situations", LookMode.Value);
		if (situationNames == null)
			situationNames = new List<string>();
	}

	public string Reason(Pawn p, WorkTypeDef w)
	{
		if (reasons.TryGetValue(p.thingIDNumber, out var value) && value.TryGetValue(w, out var value2))
		{
			return value2;
		}
		return null;
	}

	// 2.15: a map that is removed (abandoned site, finished quest map) is
	// dropped from the static caches at once
	public override void MapRemoved()
	{
		CrisisRules.Forget(map);
		SmartBalance.Forget(map);
		WorkGiver_StewardHaulProducts.Forget(map);
	}

	public override void MapComponentTick()
	{
		if (!auto)
			return;
		// 2.39: colonists are sent to fires every second
		if (Find.TickManager.TicksGame % 60 == 0)
		{
			try { FireResponse.Tick(map); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] fire response failed: " + ex, 0x57e3a52); }
		}
		if (++ticker < 250)
			return;
		ticker = 0;
		// 2.39: schedules for everyone at once, and help for exhausted / breaking colonists
		try
		{
			StewardPlus.Schedules(this, map);
			ScheduleCare.Tick(this, map);
		}
		catch (Exception ex) { Log.ErrorOnce("[Steward] schedules failed: " + ex, 0x57e3a53); }
		// 2.32: extras
		try
		{
			StewardPlus.SafeAreaTick(this, map);
			if (Find.TickManager.TicksGame >= nextExtras)
			{
				nextExtras = Find.TickManager.TicksGame + 2500;
				StewardPlus.MasterBills(this, map);
				NoStorage.Check(map);
			}
		}
		catch (Exception ex) { Log.ErrorOnce("[Steward] extras failed: " + ex, 0x57e3a31); }
		// 2.27.0: free doctors go to patients at once
		try { MedicalResponse.Tick(map); }
		catch (Exception ex) { Log.ErrorOnce("[Steward] medical response failed: " + ex, 0x57e3a21); }
		// Steward 2.9.0: the regular plan also runs while there is a hostile
		// on the map (a single angry animal used to stop planning for days);
		// only the extra early re-plans wait for the danger to pass.
		if (map.IsPlayerHome || map.mapPawns.FreeColonistsSpawnedCount != 0)
		{
			bool danger = GenHostility.AnyHostileActiveThreatToPlayer(map);
			int ticksGame = Find.TickManager.TicksGame;
			int freeColonistsSpawnedCount = MapDemand.Workers(map);
			bool flag = ticksGame >= nextRun;
			// re-plan early when the work on the map changed a lot
			if (!flag && !danger && !Modes.Simple && ticksGame - lastRun >= Modes.ShiftGap && ticksGame >= nextChangeCheck)
			{
				nextChangeCheck = ticksGame + 1250;
				// 2.14: My style does not follow the work on the map
				flag = style != ForemanStyle.Custom && WorkShifted();
			}
			if (!flag && ticksGame - lastRun >= 2500)
			{
				flag = freeColonistsSpawnedCount != lastColonists;
			}
			if (!flag && ticksGame - lastRun >= 250)
			{
				flag = Planner.RosterSignature(map) != lastRoster;
			}
			// 2.28.0: every crisis is checked every few seconds and starts / ends
			// a new plan at once (was: only fire and raid, the rest once an hour).
			// A crisis that only flickers (food days around the limit) waits 10 s.
			if (!flag && ticksGame - lastRun >= 250)
			{
				Crisis now = MapDemand.Quick(map, lastHarvestable).crisis;
				Crisis diff = now ^ crisis;
				if (diff != 0)
				{
					if (!crisisSeen.HasValue || crisisSeen.Value != now)
					{
						crisisSeen = now;
						crisisSeenTick = ticksGame;
					}
					const Crisis instant = Crisis.Fire | Crisis.Raid | Crisis.Medical | Crisis.Toxic | Crisis.Manhunters | Crisis.PrisonBreak | Crisis.DangerousBreak
						| Crisis.EntityEscape | Crisis.VacuumBreach | Crisis.ToxGas | Crisis.NoFirefighters | Crisis.Labor | Crisis.Siege | Crisis.Infestation;
					flag = (diff & instant & now) != 0 || ticksGame - crisisSeenTick >= (Modes.Calm ? 2500 : 600);
				}
				else
				{
					crisisSeen = null;
				}
			}
			// 2.13: a fire in the home area or a raid starts / ends – re-plan
			// within a few seconds, not after up to an hour.
			if (!flag && ticksGame - lastRun >= 250)
			{
				Crisis urgent = CrisisRules.Urgent(map);
				flag = (urgent & CrisisRules.UrgentMask) != (crisis & CrisisRules.UrgentMask);
			}
			// 2.19: Balanced looks for a new focus every ~5000 ticks
			if (!flag && ticksGame - autoFocusEval >= AutoFocus.EvalInterval && AutoFocus.Active(this))
			{
				flag = AutoFocus.Update(map, this, lastDemand, force: true);
			}
			// 2.20: situations (Balanced) are looked at every hour
			if (!flag && ticksGame - sitEvalTick >= SituationRules.EvalInterval && ticksGame - lastRun >= Modes.SituationGap)
			{
				try
				{
					flag = SituationsChanged();
				}
				catch (Exception ex)
				{
					Log.ErrorOnce("[Steward] situations failed: " + ex, 0x57e3a11);
				}
			}
			if (!flag && ticksGame - lastRun >= 2500)
			{
				MapDemand mapDemand = MapDemand.Quick(map, lastHarvestable);
				flag = mapDemand.crisis != crisis;
			}
			if (flag)
			{
				// 2.14.1: one bad pawn or modded work type must not break the
				// planner every 250 ticks
				try
				{
					Run(manual: false);
				}
				catch (Exception ex)
				{
					lastRun = ticksGame;
					nextRun = ticksGame + 2500;
					Log.ErrorOnce("[Steward] planning failed: " + ex, 0x57e3a0d);
				}
			}
		}
	}

	public int Run(bool manual)
	{
		ForemanSettings settings = ForemanMod.Settings;
		MapDemand mapDemand = (lastDemand = MapDemand.Measure(map));
		lastHarvestable = mapDemand.harvestable;
		lastColonists = MapDemand.Workers(map);
		lastRoster = Planner.RosterSignature(map);
		lastRun = Find.TickManager.TicksGame;
		try { workSnapshot = WorkNow(); } catch { workSnapshot = null; }
		nextRun = lastRun + Modes.IntervalHours(settings) * 2500;
		planFull = manual;
		Crisis crisis = this.crisis;
		// 2.14: no crises (and no crisis messages) in My style
		this.crisis = mapDemand.crisis;   // 2.26.0: crises also in My style
		if (settings != null && settings.crisisMessages && crisis != this.crisis && map.IsPlayerHome)
		{
			Crisis crisis2 = this.crisis & ~crisis;
			Crisis crisis3 = crisis & ~this.crisis;
			if (crisis2 != 0)
			{
				Messages.Message("CF_CrisisStart".Translate(MapDemand.Label(crisis2)), MessageTypeDefOf.CautionInput, historical: false);
			}
			if (crisis3 != 0)
			{
				Messages.Message("CF_CrisisEnd".Translate(MapDemand.Label(crisis3)), MessageTypeDefOf.PositiveEvent, historical: false);
			}
		}
		// 2.19: remember raids (Defense focus in the two days after one)
		if (((crisis | this.crisis) & Crisis.Raid) != 0)
			lastRaidTick = lastRun;
		// 2.19: Balanced picks (or keeps) its focus before planning
		AutoFocus.Update(map, this, mapDemand, force: manual);
		// 2.20: the situations for this plan (Balanced)
		try
		{
			RefreshSituations();
		}
		catch (Exception ex)
		{
			situations = new List<ActiveSituation>();
			Log.ErrorOnce("[Steward] situations failed: " + ex, 0x57e3a12);
		}
		int num = Planner.Plan(map, this, mapDemand);
		lastPlanTick = lastRun;   // 2.18: for the Steward overview
		lastPlanChanged = num;
		try { StewardPlus.AddHistory(this, map, num, manual); } catch { }
		if (manual)
		{
			Messages.Message("CF_Optimized".Translate(num), MessageTypeDefOf.SilentInput, historical: false);
		}
		// 2.32.4: the automatic plan says so too (only when something changed)
		else if (num > 0 && settings != null && settings.autoMessages && map == Find.CurrentMap)
		{
			Messages.Message("CF_AutoOptimized".Translate(num, AutoFocus.ShortLabel(this)), MessageTypeDefOf.SilentInput, historical: false);
		}
		return num;
	}
}
