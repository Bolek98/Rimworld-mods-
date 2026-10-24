using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 3.1.0: why the steward does (or does not) plan a colonist, and every lock in one place.
public static class StewardStatus
{
	// the tooltip on a colonist's name in the Work tab
	public static string For(Pawn p, ForemanMapComponent comp)
	{
		if (p == null || comp == null)
			return null;
		ForemanGameComponent game = ForemanGameComponent.Get();
		ForemanSettings s = ForemanMod.Settings;
		var lines = new List<string>();
		string skip = SkipReason(p, comp.map, s, game);
		lines.Add(skip ?? (string)"CF31_StOk".Translate());
		if (skip == null && !comp.auto)
			lines.Add("CF31_StAutoOff".Translate());
		if (game != null && p.workSettings != null)
		{
			int cells = 0;
			var cols = new List<string>();
			var unable = new List<string>();
			foreach (WorkTypeDef w in WorkCatalog.Visible)
			{
				if (p.WorkTypeIsDisabled(w))
					continue;
				if (game.WorkLocked(w))
					cols.Add(w.labelShort);
				else if (game.CellLocked(p, w))
					cells++;
				if (WorkCatalog.UnableForNow(p, w))
					unable.Add(w.labelShort);
			}
			if (cells > 0)
				lines.Add("CF31_StCells".Translate(cells));
			if (cols.Count > 0)
				lines.Add("CF31_StColumns".Translate(string.Join(", ", cols)));
			if (unable.Count > 0)
				lines.Add("CF31_StUnable".Translate(string.Join(", ", unable)));
		}
		return string.Join("\n", lines);
	}

	// null = the steward plans him (the same rules as WorkCatalog.Workers and the planner)
	public static string SkipReason(Pawn p, Map map, ForemanSettings s, ForemanGameComponent game)
	{
		if (p.IsSlave)
			return "CF31_StSlave".Translate();
		if (p.workSettings == null || !p.workSettings.EverWork)
			return "CF31_StNoWork".Translate();
		if (p.DevelopmentalStage.Baby())
			return "CF31_StBaby".Translate();
		if (p.IsQuestLodger() && (s == null || !s.includeLodgers))
			return "CF31_StGuest".Translate();
		if (map == null || p.MapHeld != map || !p.Spawned)
			return "CF31_StNotHere".Translate();
		if (map.mapPawns?.FreeColonistsSpawned == null || !map.mapPawns.FreeColonistsSpawned.Contains(p))
			return "CF31_StGuest".Translate();
		if (p.Downed)
			return "CF31_StDowned".Translate();
		if (ModCompat.AchtungForced(p))
			return "CF31_StAchtung".Translate();
		if (game != null && game.PawnLocked(p))
			return "CF31_StLockedPawn".Translate();
		return null;
	}

	public static int LockCount(ForemanGameComponent game)
	{
		if (game == null)
			return 0;
		return game.lockedPawns.Count + game.lockedWork.Count + game.lockedCells.Count;
	}

	public static Dictionary<string, Pawn> PawnsById()
	{
		var d = new Dictionary<string, Pawn>();
		foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive)
		{
			try
			{
				string id = p.GetUniqueLoadID();
				if (!d.ContainsKey(id))
					d[id] = p;
			}
			catch
			{
			}
		}
		return d;
	}
}

// "Plan now": every change of the plan, written only after "Apply"
public class Dialog_StewardPreview : Window
{
	private readonly ForemanMapComponent comp;
	private readonly Planner.PlanResult plan;
	private readonly List<Planner.Change> changes;
	private Vector2 scroll;

	public Dialog_StewardPreview(ForemanMapComponent comp, Planner.PlanResult plan)
	{
		this.comp = comp;
		this.plan = plan;
		changes = plan?.Changes() ?? new List<Planner.Change>();
		changes.Sort((x, y) =>
		{
			int c = string.Compare(x.pawn.LabelShort, y.pawn.LabelShort, StringComparison.CurrentCulture);
			return c != 0 ? c : x.work.naturalPriority.CompareTo(y.work.naturalPriority) * -1;
		});
		doCloseX = true;
		closeOnClickedOutside = false;
		absorbInputAroundWindow = true;
		forcePause = true;
	}

	public override Vector2 InitialSize => new Vector2(720f, 600f);

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "CF31_PreviewTitle".Translate(changes.Count));
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 24f), "CF31_PreviewHint".Translate());
		GUI.color = Color.white;
		float top = inRect.y + 62f, bottom = inRect.yMax - 40f;
		Rect outer = new Rect(inRect.x, top, inRect.width, bottom - top - 6f);
		if (changes.Count == 0)
		{
			Widgets.Label(outer, "CF31_PreviewNone".Translate());
		}
		else
		{
			const float row = 24f;
			int pawnRows = changes.Select(c => c.pawn).Distinct().Count();
			Rect view = new Rect(0f, 0f, outer.width - 16f, (changes.Count + pawnRows) * row + 8f);
			Widgets.BeginScrollView(outer, ref scroll, view);
			float y = 0f;
			Pawn last = null;
			foreach (Planner.Change c in changes)
			{
				if (c.pawn != last)
				{
					last = c.pawn;
					Text.Font = GameFont.Small;
					GUI.color = MUI.Accent;
					Widgets.Label(new Rect(0f, y, view.width, row), c.pawn.LabelShortCap);
					GUI.color = Color.white;
					y += row;
				}
				Rect r = new Rect(16f, y, view.width - 16f, row);
				if (Mouse.IsOver(r))
					Widgets.DrawHighlight(r);
				Widgets.Label(new Rect(r.x, r.y, 220f, row), c.work.labelShort.CapitalizeFirst().Truncate(215f));
				GUI.color = c.to == 0 ? MUI.Warn : (c.to < c.from || c.from == 0 ? MUI.Accent : Color.white);
				Widgets.Label(new Rect(r.x + 225f, r.y, 90f, row), Level(c.from) + " → " + Level(c.to));
				GUI.color = MUI.Muted;
				if (c.why != null)
					Widgets.Label(new Rect(r.x + 320f, r.y, r.width - 320f, row), c.why.Truncate(r.width - 325f));
				GUI.color = Color.white;
				if (c.why != null)
					TooltipHandler.TipRegion(r, c.why);
				y += row;
			}
			Widgets.EndScrollView();
		}
		Rect bApply = new Rect(inRect.xMax - 300f, inRect.yMax - 34f, 145f, 34f);
		Rect bCancel = new Rect(inRect.xMax - 145f, inRect.yMax - 34f, 145f, 34f);
		if (changes.Count > 0 && Widgets.ButtonText(bApply, "CF31_Apply".Translate()))
		{
			int n = comp.ApplyPlan(plan);
			Messages.Message("CF2_PlanDone".Translate(n), MessageTypeDefOf.SilentInput, historical: false);
			Close();
		}
		if (Widgets.ButtonText(bCancel, (changes.Count > 0 ? "CF31_Cancel" : "CF31_Close").Translate()))
			Close();
	}

	private static string Level(int v) => v <= 0 ? "–" : v.ToString();
}

// every lock: colonists, work columns and single cells, with unlocking
public class Dialog_StewardLocks : Window
{
	private Vector2 scroll;

	public Dialog_StewardLocks()
	{
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
	}

	public override Vector2 InitialSize => new Vector2(560f, 560f);

	public override void DoWindowContents(Rect inRect)
	{
		ForemanGameComponent game = ForemanGameComponent.Get();
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "CF31_LocksTitle".Translate());
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		string how = "CF31_LocksHowTo".Translate();
		float hh = Text.CalcHeight(how, inRect.width);
		Widgets.Label(new Rect(inRect.x, inRect.y + 36f, inRect.width, hh), how);
		GUI.color = Color.white;
		if (game == null)
			return;
		var byId = StewardStatus.PawnsById();
		var rows = new List<(string section, string label, Action unlock)>();
		foreach (string id in game.lockedPawns.ToList())
		{
			string local = id;
			rows.Add(("CF31_LocksPawns", byId.TryGetValue(id, out Pawn p) ? (string)p.LabelShortCap : id, () => Remove(game.lockedPawns, local, game)));
		}
		foreach (string d in game.lockedWork.ToList())
		{
			string local = d;
			rows.Add(("CF31_LocksWork", DefDatabase<WorkTypeDef>.GetNamedSilentFail(d)?.labelShort.CapitalizeFirst() ?? d, () => Remove(game.lockedWork, local, game)));
		}
		foreach (string k in game.lockedCells.ToList())
		{
			string local = k;
			int cut = k.LastIndexOf('|');
			string who = cut > 0 && byId.TryGetValue(k.Substring(0, cut), out Pawn p) ? (string)p.LabelShortCap : (cut > 0 ? k.Substring(0, cut) : k);
			string what = cut > 0 ? DefDatabase<WorkTypeDef>.GetNamedSilentFail(k.Substring(cut + 1))?.labelShort ?? k.Substring(cut + 1) : "";
			rows.Add(("CF31_LocksCells", who + ": " + what, () => Remove(game.lockedCells, local, game)));
		}
		float top = inRect.y + 40f + hh + 6f;
		Rect outer = new Rect(inRect.x, top, inRect.width, inRect.yMax - 40f - top);
		if (rows.Count == 0)
		{
			Widgets.Label(outer, "CF31_LocksNone".Translate());
		}
		else
		{
			const float row = 26f;
			int sections = rows.Select(r => r.section).Distinct().Count();
			Rect view = new Rect(0f, 0f, outer.width - 16f, (rows.Count + sections) * row + 8f);
			Widgets.BeginScrollView(outer, ref scroll, view);
			float y = 0f;
			string last = null;
			Action todo = null;
			foreach (var r in rows)
			{
				if (r.section != last)
				{
					last = r.section;
					GUI.color = MUI.Accent;
					Widgets.Label(new Rect(0f, y, view.width, row), r.section.Translate());
					GUI.color = Color.white;
					y += row;
				}
				Rect rr = new Rect(16f, y, view.width - 16f, row);
				if (Mouse.IsOver(rr))
					Widgets.DrawHighlight(rr);
				GUI.DrawTexture(new Rect(rr.x, rr.y + 6f, 12f, 12f), ForemanTex.Lock);
				Widgets.Label(new Rect(rr.x + 18f, rr.y + 2f, rr.width - 130f, row), r.label);
				if (Widgets.ButtonText(new Rect(rr.xMax - 105f, rr.y + 1f, 100f, row - 2f), "CF31_Unlock".Translate()))
					todo = r.unlock;
				y += row;
			}
			Widgets.EndScrollView();
			todo?.Invoke();
			if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 34f, 180f, 34f), "CF31_UnlockAll".Translate()))
			{
				game.lockedPawns.Clear();
				game.lockedWork.Clear();
				game.lockedCells.Clear();
				game.ClearCaches();
			}
		}
	}

	private static void Remove(HashSet<string> set, string key, ForemanGameComponent game)
	{
		set.Remove(key);
		game.ClearCaches();
	}
}
