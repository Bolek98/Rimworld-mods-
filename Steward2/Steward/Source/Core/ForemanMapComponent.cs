using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

public sealed class CalmCell
{
	public int tick;
	public bool crisis;
}

// The steward on one map: on/off, the colony style, when to plan, the calm hold.
// Add-ons keep their own values in here too (StewardApi.MapExpose).
public sealed class ForemanMapComponent : MapComponent
{
	public bool auto;
	public ForemanStyle style = ForemanStyle.Balanced;
	public int nextRun;
	public int lastRun = -999999;
	// the crisis an add-on reports (shared, so priority areas and others can pause)
	public Crisis crisis;
	public Dictionary<string, CalmCell> calmCells = new Dictionary<string, CalmCell>();
	public List<string> history = new List<string>();

	// runtime only
	public readonly Demand demand = new Demand();
	public readonly Dictionary<long, List<Reason>> tips = new Dictionary<long, List<Reason>>();
	public int lastPlanTick = -1;
	public int lastPlanChanged;
	private string lastRoster;
	private Dictionary<WorkTypeDef, int> lastNeed = new Dictionary<WorkTypeDef, int>();
	private bool requested, requestedUrgent;
	public string lastWhy = "";
	private int ticker;
	private bool initDone;

	private static readonly Dictionary<Map, ForemanMapComponent> cache = new Dictionary<Map, ForemanMapComponent>();

	public ForemanMapComponent(Map map) : base(map)
	{
		auto = ForemanMod.Settings?.autoByDefault ?? false;
	}

	public Crisis CurrentCrisis => crisis;

	public static ForemanMapComponent For(Map map)
	{
		if (map == null)
			return null;
		if (!cache.TryGetValue(map, out var c) || c == null || c.map != map)
		{
			c = map.GetComponent<ForemanMapComponent>();
			if (c != null)
				cache[map] = c;
		}
		return c;
	}

	public static void ResetStatics()
	{
		cache.Clear();
		ChildLearning.ResetGame();
		ParentCare.ResetGame();
	}

	// an add-on (or you) wants a new plan; urgent = at the next check, else after the calm gap
	public void RequestPlan(bool urgent)
	{
		requested = true;
		requestedUrgent |= urgent;
	}

	public void SetAuto(bool on)
	{
		if (auto == on)
			return;
		auto = on;
		if (on)
		{
			demand.MeasureAll(map, ForemanMod.Settings);
			RequestPlan(true);
		}
	}

	public void SetStyle(ForemanStyle s)
	{
		if (style == s)
			return;
		style = s;
		RequestPlan(true);
		AddHistory("CF2_HistStyle".Translate(Styles.Label(s)));
	}

	public int PlanNow(bool manual)
	{
		PrepareDemand(manual);
		int changed = 0;
		try
		{
			changed = Planner.Run(this, manual);
		}
		catch (Exception ex)
		{
			Log.ErrorOnce("[Steward] plan failed: " + ex, 0x57e30001);
		}
		AfterPlan();
		return changed;
	}

	private void PrepareDemand(bool manual)
	{
		if (!initDone || (manual && !auto))
		{
			demand.MeasureAll(map, ForemanMod.Settings);
			initDone = true;
		}
	}

	// 3.1.0: "Plan now" first shows what would change; nothing is written until you confirm
	public Planner.PlanResult PreviewPlan()
	{
		PrepareDemand(true);
		try
		{
			return Planner.Compute(this, manual: true);
		}
		catch (Exception ex)
		{
			Log.ErrorOnce("[Steward] plan failed: " + ex, 0x57e30001);
			return null;
		}
	}

	public int ApplyPlan(Planner.PlanResult r)
	{
		int changed = 0;
		try
		{
			if (r != null)
				changed = Planner.Write(r);
		}
		catch (Exception ex)
		{
			Log.ErrorOnce("[Steward] plan failed: " + ex, 0x57e30002);
		}
		AfterPlan();
		return changed;
	}

	private void AfterPlan()
	{
		int now = Find.TickManager.TicksGame;
		lastRun = now;
		nextRun = now + Math.Max(1, ForemanMod.Settings?.intervalHours ?? 6) * 2500;
		lastRoster = Roster();
		lastNeed = Needs();
		requested = requestedUrgent = false;
	}

	public void AddHistory(string text)
	{
		history ??= new List<string>();
		history.Add(GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile)) + ": " + text);
		while (history.Count > 60)
			history.RemoveAt(0);
	}

	public override void MapComponentTick()
	{
		int now = Find.TickManager.TicksGame;
		if (auto && now % 20 == 7)
		{
			try { demand.Step(map, ForemanMod.Settings); }
			catch (Exception ex) { MLog.WarnOnce("[Steward] measuring work failed: " + ex.Message, 0x57e30002); }
		}
		if (++ticker < 250)
			return;
		ticker = 0;
		StewardApi.RaiseTick(this);
		// a crisis saved by the Crises add-on that is not installed any more
		if (crisis != Crisis.None && !StewardApi.HasStep("crises."))
			crisis = Crisis.None;
		if (!auto || !map.IsPlayerHome && map.mapPawns.FreeColonistsSpawnedCount == 0)
			return;
		// most people drafted (a fight): no plan now – it comes when they are free again
		List<Pawn> ws = WorkCatalog.Workers(map, ForemanMod.Settings);
		if (ws.Count > 0 && ws.Count(p => p.Drafted) * 2 > ws.Count)
			return;
		// 3.0.1: calm – only an urgent request (a crisis) plans at once; other reasons wait at least 2 in-game hours
		string why = null;
		if (requestedUrgent)
			why = "urgent request";
		else if (now >= nextRun)
			why = "regular plan";
		else if (now - lastRun >= 5000)
		{
			if (requested)
				why = "add-on request";
			else if (Roster() != lastRoster)
				why = "colonists changed";
			else
				why = NeedShifted();
		}
		if (why != null)
		{
			lastWhy = why;
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			PlanNow(manual: false);
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (ms > 40.0)
				MLog.Info("[Steward] the plan took " + ms.ToString("0") + " ms");
		}
	}

	// who works here and what they can do
	private string Roster()
	{
		var parts = new List<string>();
		foreach (Pawn p in WorkCatalog.Workers(map, ForemanMod.Settings))
			parts.Add(p.thingIDNumber + (p.Downed ? "d" : "") + (p.DevelopmentalStage == DevelopmentalStage.Child ? "c" : ""));
		return string.Join(",", parts);
	}

	private Dictionary<WorkTypeDef, int> Needs()
	{
		int n = WorkCatalog.Workers(map, ForemanMod.Settings).Count;
		var d = new Dictionary<WorkTypeDef, int>();
		foreach (WorkTypeDef w in WorkCatalog.Visible)
			d[w] = demand.Need(map, w, n);
		return d;
	}

	// 3.0.1: only work that appeared where nobody does it, or that grew a lot (not the small ups and downs of measuring)
	private string NeedShifted()
	{
		var now = Needs();
		foreach (var kv in now)
		{
			lastNeed.TryGetValue(kv.Key, out int before);
			if (kv.Value >= before + 3 || (before == 0 && kv.Value > 0 && !Covered(kv.Key)))
				return "more work: " + kv.Key.defName + " " + before + "→" + kv.Value;
		}
		return null;
	}

	private bool Covered(WorkTypeDef w)
	{
		foreach (Pawn p in WorkCatalog.Workers(map, ForemanMod.Settings))
		{
			int v = p.workSettings.GetPriority(w);
			if (v > 0 && v <= 2)
				return true;
		}
		return false;
	}

	public void PruneCalm(int now)
	{
		if (calmCells.Count < 64)
			return;
		foreach (string k in calmCells.Where(kv => now - kv.Value.tick > 180000).Select(kv => kv.Key).ToList())
			calmCells.Remove(k);
	}

	public string TipFor(Pawn p, WorkTypeDef w)
	{
		var lines = new List<string>();
		if (tips.TryGetValue(PlanContext.Key(p, w), out var rs))
			foreach (Reason r in rs)
				lines.Add(r.Text());
		StewardApi.Tip(p, w, lines);
		return lines.Count == 0 ? null : string.Join("\n", lines);
	}

	public override void MapRemoved()
	{
		cache.Remove(map);
		StewardApi.RaiseMapRemoved(map);
	}

	public override void FinalizeInit()
	{
		cache[map] = this;
		StewardApi.RaiseLoaded(this);
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref auto, "auto", false);
		Scribe_Values.Look(ref style, "style", ForemanStyle.Balanced);
		Scribe_Values.Look(ref nextRun, "nextRun", 0);
		Scribe_Values.Look(ref crisis, "crisis", Crisis.None);
		Scribe_Collections.Look(ref history, "cfHistory", LookMode.Value);
		Dictionary<string, string> calmSave = null;
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			calmSave = new Dictionary<string, string>();
			foreach (var kv in calmCells)
				calmSave[kv.Key] = kv.Value.tick + "," + (kv.Value.crisis ? 1 : 0);
		}
		Scribe_Collections.Look(ref calmSave, "cfCalmCells", LookMode.Value, LookMode.Value);
		if (Scribe.mode == LoadSaveMode.LoadingVars && calmSave != null)
		{
			calmCells = new Dictionary<string, CalmCell>();
			foreach (var kv in calmSave)
			{
				string[] a = kv.Value.Split(',');
				if (a.Length == 2 && int.TryParse(a[0], out int t))
					calmCells[kv.Key] = new CalmCell { tick = t, crisis = a[1] == "1" };
			}
		}
		StewardApi.RaiseExpose(this);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			history ??= new List<string>();
			calmCells ??= new Dictionary<string, CalmCell>();
			// Steward 1.x styles that 2.0 does not know any more fall back to Balanced
			if (!Enum.IsDefined(typeof(ForemanStyle), style))
				style = ForemanStyle.Balanced;
		}
	}
}
