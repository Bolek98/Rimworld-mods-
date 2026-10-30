using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 3.2.0: tools you use by hand – copying priorities, saved priority sets, the plan for a newcomer.
// Nothing here changes a priority without a window you confirm.
public static class StewardTools
{
	// writes the changes directly (locks respected); used by every hand tool
	public static int Apply(List<Planner.Change> changes, string history, Map map)
	{
		if (!Find.PlaySettings.useWorkPriorities)
		{
			Find.PlaySettings.useWorkPriorities = true;
			foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
				p.workSettings?.Notify_UseWorkPrioritiesChanged();
		}
		ForemanGameComponent game = ForemanGameComponent.Get();
		int n = 0;
		foreach (Planner.Change c in changes)
		{
			Pawn p = c.pawn;
			if (p == null || p.Dead || p.workSettings == null || p.WorkTypeIsDisabled(c.work))
				continue;
			if (game != null && game.AnyLock(p, c.work))
				continue;
			if (p.workSettings.GetPriority(c.work) == c.to)
				continue;
			try
			{
				p.workSettings.SetPriority(c.work, c.to);
				n++;
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Steward] cannot set " + c.work.defName + " for " + p.LabelShort + ": " + ex.Message, c.work.index ^ 0x5e70100);
			}
		}
		if (n > 0 && history != null && map != null)
			ForemanMapComponent.For(map)?.AddHistory(history);
		return n;
	}

	public static int Max => Math.Max(4, PriorityRange.Max);

	// ---- copying ------------------------------------------------------------------
	public static List<Planner.Change> CopyChanges(Pawn from, IEnumerable<Pawn> to)
	{
		var list = new List<Planner.Change>();
		ForemanGameComponent game = ForemanGameComponent.Get();
		foreach (Pawn p in to)
		{
			if (p == from || p.workSettings == null || !p.workSettings.EverWork)
				continue;
			foreach (WorkTypeDef w in WorkCatalog.Visible)
			{
				if (from.WorkTypeIsDisabled(w) || p.WorkTypeIsDisabled(w) || (game != null && game.AnyLock(p, w)))
					continue;
				int want = Math.Min(from.workSettings.GetPriority(w), Max);
				int cur = p.workSettings.GetPriority(w);
				if (want != cur)
					list.Add(new Planner.Change { pawn = p, work = w, from = cur, to = want, why = "CF32_WhyCopy".Translate(from.LabelShort) });
			}
		}
		return list;
	}

	public static void CopyMenu(Pawn from, List<FloatMenuOption> list)
	{
		Map map = from.MapHeld;
		if (map == null || from.workSettings == null)
			return;
		List<Pawn> others = map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned).Distinct()
			.Where(p => p != from && p.workSettings != null && p.workSettings.EverWork).OrderBy(p => p.LabelShort).ToList();
		if (others.Count == 0)
			return;
		list.Add(new FloatMenuOption("CF32_CopyTo".Translate(from.LabelShort), () =>
		{
			var sub = new List<FloatMenuOption>();
			sub.Add(new FloatMenuOption("CF32_CopyToAll".Translate(), () => Preview(from, others)));
			foreach (Pawn p in others)
			{
				Pawn local = p;
				sub.Add(new FloatMenuOption(p.LabelShortCap, () => Preview(from, new List<Pawn> { local })));
			}
			Find.WindowStack.Add(new FloatMenu(sub));
		}));
	}

	private static void Preview(Pawn from, List<Pawn> to)
	{
		Map map = from.MapHeld;
		string hist = "CF32_HistCopy".Translate(from.LabelShort, to.Count == 1 ? to[0].LabelShort : (string)"CF32_Others".Translate(to.Count));
		Find.WindowStack.Add(new Dialog_StewardPreview("CF32_CopyTitle".Translate(from.LabelShort), CopyChanges(from, to), ch => Apply(ch, hist, map)));
	}
}

// ---- saved priority sets (per game) -----------------------------------------------
public sealed class StewardSets : GameComponent
{
	// set name → "pawnLoadId=defName:level,defName:level;pawnLoadId=…"
	public Dictionary<string, string> sets = new Dictionary<string, string>();

	public StewardSets(Game game)
	{
	}

	public static StewardSets Get() => Current.Game?.GetComponent<StewardSets>();

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref sets, "cfSets", LookMode.Value, LookMode.Value);
		sets ??= new Dictionary<string, string>();
	}

	public void Save(string name, Map map)
	{
		var parts = new List<string>();
		foreach (Pawn p in Pawns(map))
		{
			var cells = new List<string>();
			foreach (WorkTypeDef w in WorkCatalog.Visible)
				if (!p.WorkTypeIsDisabled(w))
					cells.Add(w.defName + ":" + p.workSettings.GetPriority(w));
			parts.Add(p.GetUniqueLoadID() + "=" + string.Join(",", cells));
		}
		sets[name] = string.Join(";", parts);
	}

	public List<Planner.Change> LoadChanges(string name, Map map, out int missing)
	{
		missing = 0;
		var list = new List<Planner.Change>();
		if (!sets.TryGetValue(name, out string data) || data == null)
			return list;
		var here = new Dictionary<string, Pawn>();
		foreach (Pawn p in Pawns(map))
			here[p.GetUniqueLoadID()] = p;
		ForemanGameComponent game = ForemanGameComponent.Get();
		foreach (string part in data.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
		{
			int eq = part.IndexOf('=');
			if (eq <= 0)
				continue;
			if (!here.TryGetValue(part.Substring(0, eq), out Pawn p))
			{
				missing++;
				continue;
			}
			foreach (string cell in part.Substring(eq + 1).Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				int c = cell.LastIndexOf(':');
				if (c <= 0 || !int.TryParse(cell.Substring(c + 1), out int v))
					continue;
				WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(cell.Substring(0, c));
				if (w == null || p.WorkTypeIsDisabled(w) || (game != null && game.AnyLock(p, w)))
					continue;
				v = Math.Min(v, StewardTools.Max);
				int cur = p.workSettings.GetPriority(w);
				if (cur != v)
					list.Add(new Planner.Change { pawn = p, work = w, from = cur, to = v, why = "CF32_WhySet".Translate(name) });
			}
		}
		return list;
	}

	public static IEnumerable<Pawn> Pawns(Map map)
	{
		if (map?.mapPawns == null)
			yield break;
		// 3.2.1: a pawn can be in both lists – each once
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned).Distinct())
			if (p.workSettings != null && p.workSettings.EverWork)
				yield return p;
	}
}

public class Dialog_StewardSets : Window
{
	private readonly Map map;
	private string newName = "";
	private Vector2 scroll;

	public Dialog_StewardSets(Map map)
	{
		this.map = map;
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
	}

	public override Vector2 InitialSize => new Vector2(520f, 520f);

	public override void DoWindowContents(Rect inRect)
	{
		StewardSets store = StewardSets.Get();
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "CF32_SetsTitle".Translate());
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		string hint = "CF32_SetsHint".Translate();
		float hh = Text.CalcHeight(hint, inRect.width);
		Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, hh), hint);
		GUI.color = Color.white;
		if (store == null)
			return;
		float y = inRect.y + 40f + hh + 6f;
		// save the current priorities under a name
		newName = Widgets.TextField(new Rect(inRect.x, y, inRect.width - 160f, 30f), newName ?? "");
		if (Widgets.ButtonText(new Rect(inRect.xMax - 150f, y, 150f, 30f), "CF32_SetsSave".Translate()))
		{
			string name = (newName ?? "").Trim();
			if (name.Length == 0)
				name = "CF32_SetsDefaultName".Translate(store.sets.Count + 1);
			store.Save(name, map);
			ForemanMapComponent.For(map)?.AddHistory("CF32_HistSetSaved".Translate(name));
			Messages.Message("CF32_SetsSaved".Translate(name), MessageTypeDefOf.SilentInput, historical: false);
			newName = "";
		}
		y += 40f;
		Rect outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
		if (store.sets.Count == 0)
		{
			Widgets.Label(outer, "CF32_SetsNone".Translate());
			return;
		}
		const float row = 32f;
		Rect view = new Rect(0f, 0f, outer.width - 16f, store.sets.Count * row);
		Widgets.BeginScrollView(outer, ref scroll, view);
		float yy = 0f;
		string load = null, del = null;
		foreach (string name in store.sets.Keys.OrderBy(k => k))
		{
			Rect r = new Rect(0f, yy, view.width, row - 2f);
			if (Mouse.IsOver(r))
				Widgets.DrawHighlight(r);
			Widgets.Label(new Rect(r.x + 6f, r.y + 4f, r.width - 230f, row), name.Truncate(r.width - 240f));
			if (Widgets.ButtonText(new Rect(r.xMax - 220f, r.y + 1f, 110f, row - 4f), "CF32_SetsLoad".Translate()))
				load = name;
			if (Widgets.ButtonText(new Rect(r.xMax - 105f, r.y + 1f, 100f, row - 4f), "CF32_SetsDelete".Translate()))
				del = name;
			yy += row;
		}
		Widgets.EndScrollView();
		if (del != null)
			store.sets.Remove(del);
		if (load != null)
		{
			List<Planner.Change> ch = store.LoadChanges(load, map, out int missing);
			string title = (string)"CF32_SetsLoadTitle".Translate(load, ch.Count) + (missing > 0 ? " " + (string)"CF32_SetsMissing".Translate(missing) : "");
			string name = load;
			Find.WindowStack.Add(new Dialog_StewardPreview(title, ch, c => StewardTools.Apply(c, "CF32_HistSetLoaded".Translate(name), map)));
		}
	}
}

// ---- newcomers and the weekly summary (per map) -------------------------------------
public sealed class StewardWatch : MapComponent
{
	private HashSet<int> known = new HashSet<int>();
	private bool knownReady;
	private int weekStart = -1;
	private Dictionary<string, int> uncoveredHours = new Dictionary<string, int>();
	private Dictionary<string, int> crisisHours = new Dictionary<string, int>();
	private int weekNew, startPlans, startChanges;

	public StewardWatch(Map map) : base(map)
	{
	}

	public override void ExposeData()
	{
		List<int> k = known.ToList();
		Scribe_Collections.Look(ref k, "cfKnown", LookMode.Value);
		Scribe_Values.Look(ref knownReady, "cfKnownReady", false);
		Scribe_Values.Look(ref weekStart, "cfWeekStart", -1);
		Scribe_Collections.Look(ref uncoveredHours, "cfUncovered", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref crisisHours, "cfCrisisHours", LookMode.Value, LookMode.Value);
		Scribe_Values.Look(ref startPlans, "cfWeekStartPlans", 0);
		Scribe_Values.Look(ref startChanges, "cfWeekStartChanges", 0);
		Scribe_Values.Look(ref weekNew, "cfWeekNew", 0);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			known = new HashSet<int>(k ?? new List<int>());
			uncoveredHours ??= new Dictionary<string, int>();
			crisisHours ??= new Dictionary<string, int>();
		}
	}

	public override void MapComponentTick()
	{
		int now = Find.TickManager.TicksGame;
		if (now % 2500 != 1234)
			return;
		ForemanMapComponent comp = ForemanMapComponent.For(map);
		ForemanSettings s = ForemanMod.Settings;
		if (comp == null || s == null || !map.IsPlayerHome)
			return;
		try
		{
			Newcomers(comp, s);
			if (s.weeklySummary)
				Week(comp, now);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] watch failed: " + ex.Message, 0x5e70200);
		}
	}

	// a colonist who was not here before: with the steward off, offer his plan (a window you confirm)
	private void Newcomers(ForemanMapComponent comp, ForemanSettings s)
	{
		List<Pawn> ws = WorkCatalog.Workers(map, s);
		if (!knownReady)
		{
			foreach (Pawn p in ws)
				known.Add(p.thingIDNumber);
			knownReady = true;
			return;
		}
		List<Pawn> fresh = ws.Where(p => !known.Contains(p.thingIDNumber)).ToList();
		foreach (Pawn p in fresh)
			known.Add(p.thingIDNumber);
		if (fresh.Count == 0)
			return;
		weekNew += fresh.Count;
		// with the steward on, the next plan includes him anyway
		if (comp.auto || !s.offerNewcomers || Find.WindowStack.WindowsForcePause)
			return;
		Planner.PlanResult plan = comp.PreviewPlan();
		if (plan == null)
			return;
		var mine = new HashSet<Pawn>(fresh);
		List<Planner.Change> ch = plan.Changes().Where(c => mine.Contains(c.pawn)).ToList();
		if (ch.Count == 0)
			return;
		string names = string.Join(", ", fresh.Select(p => p.LabelShort));
		Find.WindowStack.Add(new Dialog_StewardPreview("CF32_NewTitle".Translate(names), ch,
			c => StewardTools.Apply(c, "CF32_HistNew".Translate(names), map)));
	}

	private void Week(ForemanMapComponent comp, int now)
	{
		if (weekStart < 0)
		{
			weekStart = now;
			startPlans = comp.totalPlans;
			startChanges = comp.totalChanges;
		}
		if (comp.crisis != Crisis.None)
			foreach (Crisis c in Enum.GetValues(typeof(Crisis)))
				if (c != Crisis.None && (comp.crisis & c) != 0 && IsSingleFlag((int)c))
					Add(crisisHours, c.ToString());
		// waiting work with nobody on the main levels (1–2)
		if (comp.demand != null)
		{
			List<Pawn> ws = WorkCatalog.Workers(map, ForemanMod.Settings);
			foreach (WorkTypeDef w in WorkCatalog.Visible)
			{
				WorkKind k = WorkCatalog.Kind(w);
				if (k == WorkKind.Hidden || k == WorkKind.Emergency || comp.demand.Jobs(w) <= 0)
					continue;
				bool any = ws.Any(p => { int v = p.workSettings.GetPriority(w); return v > 0 && v <= 2; });
				if (!any)
					Add(uncoveredHours, w.defName);
			}
		}
		if (now - weekStart < GenDate.TicksPerDay * 7)
			return;
		Report(comp);
		weekStart = now;
		uncoveredHours.Clear();
		crisisHours.Clear();
		weekNew = 0;
		startPlans = comp.totalPlans;
		startChanges = comp.totalChanges;
	}

	private static bool IsSingleFlag(int v) => v > 0 && (v & (v - 1)) == 0;

	private static void Add(Dictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int v) ? v + 1 : 1;

	private void Report(ForemanMapComponent comp)
	{
		// the steward was off all week: nothing to tell
		if (!comp.auto && comp.totalPlans == startPlans)
			return;
		var parts = new List<string>();
		parts.Add("CF32_WeekPlans".Translate(comp.totalPlans - startPlans, comp.totalChanges - startChanges));
		if (weekNew > 0)
			parts.Add("CF32_WeekNew".Translate(weekNew));
		var gaps = uncoveredHours.Where(kv => kv.Value >= 6).OrderByDescending(kv => kv.Value).Take(3)
			.Select(kv => (DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key)?.labelShort ?? kv.Key) + " " + Hours(kv.Value)).ToList();
		parts.Add(gaps.Count > 0 ? (string)"CF32_WeekGaps".Translate(string.Join(", ", gaps)) : (string)"CF32_WeekNoGaps".Translate());
		var cr = crisisHours.OrderByDescending(kv => kv.Value).Take(3)
			.Select(kv => (Enum.TryParse(kv.Key, out Crisis c) ? CrisisText.Label(c) : kv.Key) + " " + Hours(kv.Value)).ToList();
		if (cr.Count > 0)
			parts.Add("CF32_WeekCrises".Translate(string.Join(", ", cr)));
		string text = "CF32_WeekTitle".Translate(map.Parent?.LabelCap ?? "") + " " + string.Join(" · ", parts);
		comp.AddHistory(text);
		Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: true);
	}

	private static string Hours(int h) => h >= 24 ? (h / 24f).ToString("0.#") + " d" : h + " h";
}

// ---- apprentices (off by default) ---------------------------------------------------
// 850: a colonist with a passion and little skill gets the work on a learning level (3), next to
// a master who has it as main work. One apprenticeship per colonist, one apprentice per work.
public sealed class ApprenticeStep : IPlanStep
{
	public int Order => 850;
	public string Id => "core.apprentice";

	public void Apply(PlanContext ctx)
	{
		if (!ctx.settings.apprentices)
			return;
		int level = Math.Min(3, ctx.Levels);
		var busy = new HashSet<Pawn>();
		foreach (WorkTypeDef w in ctx.types)
		{
			if (WorkCatalog.Kind(w) != WorkKind.Skilled || w.relevantSkills == null || w.relevantSkills.Count == 0)
				continue;
			// a master: somebody on the main levels who is good at it
			bool master = ctx.pawns.Any(p => { int v = ctx.Get(p, w); return v > 0 && v <= 2 && Skill(p, w) >= 10f; });
			if (!master)
				continue;
			Pawn pick = null;
			float best = 0f;
			foreach (Pawn p in ctx.pawns)
			{
				if (busy.Contains(p) || ctx.noRaise.Contains(p) || !ctx.CanDo(p, w))
					continue;
				int v = ctx.Get(p, w);
				if (v > 0 && v <= level)
					continue;
				float sk = Skill(p, w), pas = Passion(p, w);
				if (pas <= 0f || sk >= 8f)
					continue;
				float score = pas * 2f + (8f - sk) * 0.1f;
				if (score > best)
				{
					best = score;
					pick = p;
				}
			}
			// pinned: the calm rules would otherwise ignore the small step 4 → 3
			if (pick != null && ctx.Set(pick, w, level, "CF32_TipApprentice", null, null, pin: true))
				busy.Add(pick);
		}
	}

	private static float Skill(Pawn p, WorkTypeDef w)
	{
		if (p.skills == null)
			return 0f;
		float s = 0f;
		int n = 0;
		foreach (SkillDef sd in w.relevantSkills)
		{
			SkillRecord r = p.skills.GetSkill(sd);
			if (r == null || r.TotallyDisabled)
				continue;
			s += r.Level;
			n++;
		}
		return n == 0 ? 0f : s / n;
	}

	private static float Passion(Pawn p, WorkTypeDef w)
	{
		if (p.skills == null)
			return 0f;
		float best = 0f;
		foreach (SkillDef sd in w.relevantSkills)
		{
			SkillRecord r = p.skills.GetSkill(sd);
			if (r == null || r.TotallyDisabled)
				continue;
			float v = r.passion == RimWorld.Passion.Major ? 1f : r.passion == RimWorld.Passion.Minor ? 0.5f : (r.passion != RimWorld.Passion.None ? 0.5f : 0f);
			best = Math.Max(best, v);
		}
		return best;
	}
}
