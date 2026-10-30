using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.0 – the core and its add-ons.
//
// The core plans the work priorities. Add-ons (Crises & Situations, Schedules,
// Supplies …) are separate mods that hook in here:
//  - plan steps: run inside every plan, after the core's base plan and before
//    your own rules – they can raise people for urgent work ("pin" a cell so
//    nothing later lowers it again);
//  - map data: they save their own values inside the steward's map record,
//    so old saves keep everything and a missing add-on loses nothing;
//  - ticks: a call every 250 ticks per map;
//  - settings pages: an extra tab in the steward's settings;
//  - work tab line: a short text next to the steward's buttons.
// ---------------------------------------------------------------------------
public interface IPlanStep
{
	// lower runs first. Core: 100 base plan, 200 children, 800 your rules, 900 one top job, 950 nobody idle
	int Order { get; }

	string Id { get; }

	void Apply(PlanContext ctx);
}

public sealed class SettingsPage
{
	public string label;
	public Action<Rect> draw;
	public Action write;
}

public static class StewardApi
{
	private static readonly List<IPlanStep> steps = new List<IPlanStep>();
	private static readonly List<SettingsPage> pages = new List<SettingsPage>();
	private static float slowTold = -999f;

	public static event Action<ForemanMapComponent> MapExpose;
	public static event Action<ForemanMapComponent> MapTick;
	public static event Action<ForemanMapComponent> MapLoaded;
	public static event Action GameStarted;
	public static event Action<Map> MapRemoved;
	public static event Func<ForemanMapComponent, string> WorkTabLine;
	// a pawn's cell tooltip: extra lines from add-ons
	public static event Action<Pawn, WorkTypeDef, List<string>> CellTip;

	public static IReadOnlyList<IPlanStep> Steps => steps;
	public static IReadOnlyList<SettingsPage> Pages => pages;

	public static void Register(IPlanStep step)
	{
		if (step == null || steps.Any(s => s.Id == step.Id))
			return;
		steps.Add(step);
		steps.Sort((a, b) => a.Order.CompareTo(b.Order));
	}

	public static void AddSettingsPage(SettingsPage page)
	{
		if (page != null && !pages.Any(p => p.label == page.label))
			pages.Add(page);
	}

	// every add-on on its own: one failing does not stop the others (their save data above all)
	internal static void RaiseExpose(ForemanMapComponent c) => Each("map data", MapExpose, d => ((Action<ForemanMapComponent>)d)(c));
	internal static void RaiseTick(ForemanMapComponent c) => Each("map tick", MapTick, d => ((Action<ForemanMapComponent>)d)(c));
	internal static void RaiseLoaded(ForemanMapComponent c) => Each("map loaded", MapLoaded, d => ((Action<ForemanMapComponent>)d)(c));

	private static void Each(string what, Delegate ev, Action<Delegate> call)
	{
		if (ev == null)
			return;
		foreach (Delegate d in ev.GetInvocationList())
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			try { call(d); }
			catch (Exception ex) { MLog.WarnOnce("[Steward] add-on " + what + " (" + d.Method.DeclaringType?.Assembly.GetName().Name + ") failed: " + ex, (what + d.Method.Name).GetHashCode()); }
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (ms > 40.0 && UnityEngine.Time.realtimeSinceStartup - slowTold > 20f)
			{
				slowTold = UnityEngine.Time.realtimeSinceStartup;
				MLog.Info("[Steward] slow " + what + ": " + d.Method.DeclaringType?.Name + "." + d.Method.Name + " " + ms.ToString("0") + " ms");
			}
		}
	}

	// is an add-on with this plan step installed
	public static bool HasStep(string idPrefix) => steps.Any(s => s.Id.StartsWith(idPrefix));
	internal static void RaiseGameStarted() => Each("game start", GameStarted, d => ((Action)d)());
	internal static void RaiseMapRemoved(Map m) => Each("map removed", MapRemoved, d => ((Action<Map>)d)(m));

	public static IEnumerable<string> WorkTabLines(ForemanMapComponent c)
	{
		if (WorkTabLine == null)
			yield break;
		foreach (Delegate d in WorkTabLine.GetInvocationList())
		{
			string s = null;
			try { s = ((Func<ForemanMapComponent, string>)d)(c); }
			catch (Exception ex) { MLog.WarnOnce("[Steward] work tab line: " + ex.Message, d.Method.GetHashCode()); }
			if (!string.IsNullOrEmpty(s))
				yield return s;
		}
	}

	internal static void Tip(Pawn p, WorkTypeDef w, List<string> lines) => Safe("cell tip", () => CellTip?.Invoke(p, w, lines));

	private static void Safe(string what, Action a)
	{
		try { a(); }
		catch (Exception ex) { MLog.WarnOnce("[Steward] add-on " + what + " failed: " + ex, what.GetHashCode()); }
	}
}

// one reason line for a cell tooltip – translated only when the tooltip is shown
public struct Reason
{
	public string key;
	public object a, b;

	public string Text()
	{
		if (a == null) return key.Translate();
		if (b == null) return key.Translate(a.ToString());
		return key.Translate(a.ToString(), b.ToString());
	}
}

// Everything a plan step needs. The plan maps every planned pawn to his
// priority for every work type (0 = off, 1 = top … Levels = lowest).
public sealed class PlanContext
{
	public Map map;
	public ForemanMapComponent comp;
	public ForemanGameComponent game;
	public ForemanSettings settings;
	public List<Pawn> pawns;
	public List<WorkTypeDef> types;
	public int Levels;
	public ForemanStyle style;
	public bool manual;
	public Demand demand;

	public readonly Dictionary<Pawn, Dictionary<WorkTypeDef, int>> plan = new Dictionary<Pawn, Dictionary<WorkTypeDef, int>>();
	public readonly Dictionary<Pawn, Dictionary<WorkTypeDef, float>> fit = new Dictionary<Pawn, Dictionary<WorkTypeDef, float>>();
	// cells something urgent set (a crisis, a situation …): later steps and the calm hold leave them
	public readonly HashSet<long> pinned = new HashSet<long>();
	// pawns nobody may raise for urgent work (children who need time to learn …)
	public readonly HashSet<Pawn> noRaise = new HashSet<Pawn>();
	public readonly Dictionary<long, List<Reason>> reasons = new Dictionary<long, List<Reason>>();
	// the work types a step asked to be planned again at once (urgent)
	public bool urgent;

	public static long Key(Pawn p, WorkTypeDef w) => ((long)p.thingIDNumber << 16) | (uint)w.index;

	public int Get(Pawn p, WorkTypeDef w) => plan.TryGetValue(p, out var j) && j.TryGetValue(w, out int v) ? v : 0;

	public float Fit(Pawn p, WorkTypeDef w) => fit.TryGetValue(p, out var f) && f.TryGetValue(w, out float v) ? v : 0f;

	public bool CanDo(Pawn p, WorkTypeDef w) => WorkCatalog.CanDo(p, w, settings);

	public bool Locked(Pawn p, WorkTypeDef w) => game != null && game.AnyLock(p, w);

	public bool Pinned(Pawn p, WorkTypeDef w) => pinned.Contains(Key(p, w));

	// set a level; returns false when the cell is locked, pinned (unless pin) or the pawn cannot do it
	public bool Set(Pawn p, WorkTypeDef w, int level, string reasonKey = null, object a = null, object b = null, bool pin = false)
	{
		if (p == null || w == null || !plan.TryGetValue(p, out var jobs))
			return false;
		if (Locked(p, w) || (level > 0 && !CanDo(p, w)))
			return false;
		long k = Key(p, w);
		if (!pin && pinned.Contains(k))
			return false;
		jobs[w] = Mathf.Clamp(level, 0, Levels);
		if (pin)
			pinned.Add(k);
		if (reasonKey != null)
			AddReason(p, w, reasonKey, a, b);
		return true;
	}

	public void AddReason(Pawn p, WorkTypeDef w, string key, object a = null, object b = null)
	{
		long k = Key(p, w);
		if (!reasons.TryGetValue(k, out var list))
			reasons[k] = list = new List<Reason>(2);
		list.Add(new Reason { key = key, a = a, b = b });
	}

	// pawns who can do the work, best suited first
	public List<Pawn> Best(WorkTypeDef w)
	{
		// 3.0.5: whoever already does the work keeps it unless somebody is clearly better,
		// and equal fits always end in the same order – no swapping work back and forth
		return pawns.Where(p => CanDo(p, w) && !noRaise.Contains(p))
			.OrderByDescending(p => Fit(p, w) + (Holds(p, w) ? 0.1f : 0f))
			.ThenBy(p => p.thingIDNumber).ToList();
	}

	// the work is on the pawn's main levels (1–2) in the game right now
	public static bool Holds(Pawn p, WorkTypeDef w)
	{
		int c = p.workSettings?.GetPriority(w) ?? 0;
		return c > 0 && c <= 2;
	}

	// how many pawns have the work on level <= maxLevel
	public int CountOn(WorkTypeDef w, int maxLevel)
	{
		int n = 0;
		foreach (var kv in plan)
			if (kv.Value.TryGetValue(w, out int v) && v > 0 && v <= maxLevel)
				n++;
		return n;
	}

	// raise up to 'count' of the best suited pawns to 'level' for urgent work (pinned)
	public int Raise(WorkTypeDef w, int level, int count, string reasonKey, object a = null)
	{
		int done = 0;
		foreach (Pawn p in Best(w))
		{
			if (done >= count)
				break;
			int cur = Get(p, w);
			if (cur > 0 && cur <= level)
			{
				done++;
				pinned.Add(Key(p, w));
				continue;
			}
			if (Set(p, w, level, reasonKey, a, null, pin: true))
				done++;
		}
		return done;
	}
}
