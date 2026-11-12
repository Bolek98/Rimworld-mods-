using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.4.0: "may do this work only from skill N" – below it the colonist gets 0,
// unless he is the best the colony has for that work (somebody always keeps it)
public sealed class SkillGateStep : IPlanStep
{
	public int Order => 810;
	public string Id => "core.gates";

	public void Apply(PlanContext ctx)
	{
		var s = ctx.settings;
		if (s?.gateAny == null || s.gateAny.Count == 0)
			return;
		foreach (WorkTypeDef w in ctx.types)
		{
			if (!s.gateAny.TryGetValue(w.defName, out int min) || min <= 0)
				continue;
			List<Pawn> best = ctx.Best(w);
			Pawn keep = best.FirstOrDefault(p => ctx.CanDo(p, w));
			foreach (Pawn p in ctx.pawns)
			{
				if (p == keep || ctx.Locked(p, w) || ctx.Get(p, w) <= 0)
					continue;
				int lv = Planner.SkillLevel(p, w);
				if (lv < min)
					ctx.Set(p, w, 0, "CF2_TipGate", lv, min);
			}
		}
	}
}

public static class SkillGatesPage
{
	private static Vector2 scroll;
	private static readonly int[] Steps = { -1, 0, 1, 2, 3, 4, 5, 6, 7, 8, 10, 12, 15 };

	public static void Register()
	{
		StewardApi.AddSettingsPage(new SettingsPage { label = "CF2_TabSkills".Translate(), draw = Draw, write = () => ForemanMod.Settings.Write() });
	}

	private static IEnumerable<WorkTypeDef> Works() => WorkCatalog.Visible.Where(w => w.relevantSkills != null && w.relevantSkills.Count > 0
		&& WorkCatalog.Kind(w) != WorkKind.Emergency && WorkCatalog.Kind(w) != WorkKind.Hidden);

	private static string Num(int v, string def) => v < 0 ? def : v == 0 ? "0" : v.ToString();

	private static int Cycle(int cur, int dir)
	{
		int i = Array.IndexOf(Steps, cur);
		if (i < 0) i = 0;
		i = Mathf.Clamp(i + dir, 0, Steps.Length - 1);
		return Steps[i];
	}

	private static void Cell(Rect r, Dictionary<string, int> map, string key, string defText)
	{
		int v = map.TryGetValue(key, out int x) ? x : -1;
		if (Widgets.ButtonText(new Rect(r.x, r.y, 26f, r.height), "−")) v = Cycle(v, -1);
		GUI.color = v < 0 ? MUI.Muted : MUI.Accent;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(new Rect(r.x + 26f, r.y, r.width - 52f, r.height), Num(v, defText));
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		if (Widgets.ButtonText(new Rect(r.xMax - 26f, r.y, 26f, r.height), "+")) v = Cycle(v, +1);
		if (v < 0) map.Remove(key); else map[key] = v;
	}

	public static void Draw(Rect r)
	{
		ForemanSettings s = ForemanMod.Settings;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, r.y, r.width, 46f), "CF2_SkillsHelp".Translate());
		GUI.color = Color.white;
		float y = r.y + 50f;
		// header
		float wName = 200f, wSkill = 150f, wBest = 90f, wCell = 120f;
		Text.Font = GameFont.Tiny;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x + 4f, y, wName, 20f), "CF2_SkillsColWork".Translate());
		Widgets.Label(new Rect(r.x + wName, y, wSkill, 20f), "CF2_SkillsColSkill".Translate());
		Widgets.Label(new Rect(r.x + wName + wSkill, y, wBest, 20f), "CF2_SkillsColBest".Translate());
		Widgets.Label(new Rect(r.x + wName + wSkill + wBest, y, wCell, 20f), "CF2_SkillsColAny".Translate());
		Widgets.Label(new Rect(r.x + wName + wSkill + wBest + wCell + 10f, y, wCell, 20f), "CF2_SkillsColBackup".Translate());
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		if (Widgets.ButtonText(new Rect(r.xMax - 150f, y - 4f, 150f, 26f), "CF2_SkillsReset".Translate()))
		{
			s.gateAny.Clear();
			s.gateBackup.Clear();
		}
		y += 24f;
		List<WorkTypeDef> list = Works().ToList();
		List<Pawn> colonists = Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null
			? Find.CurrentMap.mapPawns.FreeColonistsSpawned.ToList() : new List<Pawn>();
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		Rect view = new Rect(0f, 0f, outR.width - 18f, list.Count * 30f);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			WorkTypeDef w = list[i];
			Rect row = new Rect(0f, i * 30f, view.width, 28f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.Label(new Rect(row.x + 4f, row.y + 4f, wName - 8f, 24f), ((string)w.labelShort.CapitalizeFirst()).Truncate(wName - 8f));
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(row.x + wName, row.y + 4f, wSkill - 6f, 24f), string.Join(", ", w.relevantSkills.Select(sk => sk.skillLabel.CapitalizeFirst())).Truncate(wSkill - 6f));
			GUI.color = Color.white;
			if (colonists.Count > 0)
			{
				Pawn bp = colonists.Where(p => !p.WorkTypeIsDisabled(w)).OrderByDescending(p => Planner.SkillLevel(p, w)).FirstOrDefault();
				Widgets.Label(new Rect(row.x + wName + wSkill, row.y + 4f, wBest, 24f), bp == null ? "–" : Planner.SkillLevel(bp, w).ToString());
				if (bp != null) TooltipHandler.TipRegion(new Rect(row.x + wName + wSkill, row.y, wBest, 28f), bp.LabelShortCap);
			}
			Cell(new Rect(row.x + wName + wSkill + wBest, row.y + 1f, wCell, 26f), s.gateAny, w.defName, "–");
			int def = Planner.BackupMinDefault(w, s);
			Cell(new Rect(row.x + wName + wSkill + wBest + wCell + 10f, row.y + 1f, wCell, 26f), s.gateBackup, w.defName, def <= 0 ? "–" : "(" + def + ")");
		}
		Widgets.EndScrollView();
	}
}

[StaticConstructorOnStartup]
public static class SkillGatesInit
{
	static SkillGatesInit()
	{
		// the core's own page comes first among the extra tabs
		SkillGatesPage.Register();
	}
}
