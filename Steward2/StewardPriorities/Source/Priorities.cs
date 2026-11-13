using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace StewardPriorities;

public sealed class PrioSettings : ModSettings
{
	public int levels = 9;          // 4..9
	public bool shiftScroll = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref levels, "levels", 9);
		Scribe_Values.Look(ref shiftScroll, "shiftScroll", true);
		levels = Mathf.Clamp(levels, 4, 9);
	}
}

// The game knows work priorities 1–4. This mod lets it use 1–N (N up to 9): clicking in the Work tab,
// shift-clicking a column header and the game's own check all use N. The job order itself already
// works with any number (lower number first).
public sealed class PrioritiesMod : Mod
{
	public static PrioSettings S;
	public const string Version = "1.0.0";

	public PrioritiesMod(ModContentPack content) : base(content)
	{
		S = GetSettings<PrioSettings>();
		if (OtherPriorityMod())
		{
			Log.Message("[Steward – 9 Priorities] another priority mod (Work Tab / Priority Master) is active – staying off.");
			return;
		}
		var h = new Harmony("misak.steward.priorities");
		try
		{
			var lim = new HarmonyMethod(typeof(Levels), nameof(Levels.Transpiler));
			h.Patch(AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.SetPriority)), transpiler: lim);
			h.Patch(AccessTools.Method(typeof(WidgetsWork), nameof(WidgetsWork.DrawWorkBoxFor)),
				prefix: new HarmonyMethod(typeof(Levels), nameof(Levels.ScrollPrefix)), transpiler: lim);
			h.Patch(AccessTools.Method(typeof(PawnColumnWorker_WorkPriority), "HeaderClicked"), transpiler: lim);
			h.Patch(AccessTools.Method(typeof(WidgetsWork), nameof(WidgetsWork.ColorOfPriority)),
				postfix: new HarmonyMethod(typeof(Levels), nameof(Levels.ColorPostfix)));
			Levels.Active = true;
		}
		catch (Exception e)
		{
			Log.Error("[Steward – 9 Priorities] could not hook into the game, priorities stay 1–4: " + e);
		}
		MUI.PatchWindow(h, typeof(PrioritiesMod));
		MNews.Register(content, Version);
		Log.Message("[Steward – 9 Priorities] v" + Version + " loaded (" + Levels.Max + " levels).");
	}

	private static bool OtherPriorityMod()
	{
		foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
		{
			string id = (m.PackageId ?? "").ToLowerInvariant();
			if (id.Contains("prioritymaster") || id.Contains("prioritymod") || id.Contains("worktab"))
				return true;
		}
		return false;
	}

	public override string SettingsCategory() => "SPR_Name".Translate();

	public override void DoSettingsWindowContents(Rect r)
	{
		MUI.Version(r, "v" + Version + " · Bolek98");
		var l = new Listing_Standard();
		l.Begin(new Rect(r.x, r.y, Mathf.Min(r.width, 640f), r.height));
		if (!Levels.Active)
		{
			GUI.color = MUI.Warn;
			l.Label("SPR_Off".Translate());
			GUI.color = Color.white;
			l.End();
			return;
		}
		GUI.color = MUI.Muted;
		l.Label("SPR_Help".Translate());
		GUI.color = Color.white;
		l.Gap(6f);
		l.Label("SPR_Levels".Translate(S.levels));
		S.levels = Mathf.RoundToInt(MUI.SliderBar(l.GetRect(22f), S.levels, 4f, 9f, 1f));
		// colour preview
		Rect row = l.GetRect(26f);
		for (int i = 1; i <= S.levels; i++)
		{
			Rect c = new Rect(row.x + (i - 1) * 30f, row.y, 26f, 26f);
			Widgets.DrawBoxSolid(c, new Color(0.12f, 0.12f, 0.12f));
			Text.Anchor = TextAnchor.MiddleCenter;
			GUI.color = WidgetsWork.ColorOfPriority(i);
			Widgets.Label(c, i.ToString());
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
		}
		l.Gap(6f);
		l.CheckboxLabeled("SPR_Scroll".Translate(), ref S.shiftScroll, "SPR_ScrollTip".Translate());
		l.Gap(12f);
		bool playing = Current.ProgramState == ProgramState.Playing;
		MUI.Section(l, "SPR_Convert".Translate());
		GUI.color = MUI.Muted;
		l.Label("SPR_ConvertHelp".Translate());
		GUI.color = Color.white;
		if (!playing)
		{
			GUI.color = MUI.Muted;
			l.Label("SPR_NeedGame".Translate());
			GUI.color = Color.white;
		}
		else
		{
			if (l.ButtonText("SPR_Spread".Translate(S.levels)))
				Messages.Message("SPR_Done".Translate(Convert(4, S.levels)), MessageTypeDefOf.TaskCompletion, false);
			if (l.ButtonText("SPR_Back".Translate()))
				Messages.Message("SPR_Done".Translate(Convert(S.levels, 4)), MessageTypeDefOf.TaskCompletion, false);
			int over = CountOver(S.levels);
			if (over > 0)
			{
				GUI.color = MUI.Warn;
				l.Label("SPR_Over".Translate(over, S.levels));
				GUI.color = Color.white;
				if (l.ButtonText("SPR_Clamp".Translate(S.levels)))
					Messages.Message("SPR_Done".Translate(Clamp(S.levels)), MessageTypeDefOf.TaskCompletion, false);
			}
		}
		l.End();
	}

	private static IEnumerable<Pawn> Workers()
	{
		foreach (Map m in Find.Maps)
			foreach (Pawn p in m.mapPawns.AllPawns)
				if (p.Faction == Faction.OfPlayer && p.workSettings != null && p.workSettings.EverWork)
					yield return p;
		foreach (Caravan c in Find.WorldObjects.Caravans)
			if (c.Faction == Faction.OfPlayer)
				foreach (Pawn p in c.PawnsListForReading)
					if (p.workSettings != null && p.workSettings.EverWork)
						yield return p;
	}

	// 1..from -> 1..to keeping the order (1 stays 1, the lowest stays the lowest)
	private static int Convert(int from, int to)
	{
		int n = 0;
		foreach (Pawn p in Workers().ToList())
			foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
			{
				if (p.WorkTypeIsDisabled(w)) continue;
				int v = p.workSettings.GetPriority(w);
				if (v <= 0) continue;
				int nv = Mathf.Clamp(Mathf.RoundToInt(1f + (Mathf.Min(v, from) - 1f) * (to - 1f) / (from - 1f)), 1, to);
				if (nv != v) { p.workSettings.SetPriority(w, nv); n++; }
			}
		return n;
	}

	private static int CountOver(int max)
	{
		int n = 0;
		foreach (Pawn p in Workers())
			foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
				if (p.workSettings.GetPriority(w) > max) n++;
		return n;
	}

	private static int Clamp(int max)
	{
		int n = 0;
		foreach (Pawn p in Workers().ToList())
			foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
				if (!p.WorkTypeIsDisabled(w) && p.workSettings.GetPriority(w) > max) { p.workSettings.SetPriority(w, max); n++; }
		return n;
	}
}

public static class Levels
{
	public static bool Active;

	// read by Steward – Work Priorities (PriorityRange) and anyone else who wants to know
	public static int Max => Active && PrioritiesMod.S != null ? Mathf.Clamp(PrioritiesMod.S.levels, 4, 9) : 4;

	// every "4" that means the lowest priority in the patched methods (stored as the new value, or
	// compared with) becomes the chosen number of levels; other 4s (text anchor, tutor amount) stay
	public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
	{
		MethodInfo max = AccessTools.PropertyGetter(typeof(Levels), nameof(Max));
		List<CodeInstruction> list = code.ToList();
		int done = 0;
		for (int i = 0; i < list.Count; i++)
		{
			CodeInstruction ci = list[i];
			if (ci.opcode == OpCodes.Ldc_I4_4 && i + 1 < list.Count && IsPriorityUse(list[i + 1].opcode))
			{
				var n = new CodeInstruction(OpCodes.Call, max);
				n.labels.AddRange(ci.labels);
				n.blocks.AddRange(ci.blocks);
				list[i] = n;
				done++;
			}
		}
		if (done == 0)
			Log.Warning("[Steward – 9 Priorities] nothing to change in one game method – it was changed by the game or another mod.");
		return list;
	}

	private static bool IsPriorityUse(OpCode o) =>
		o == OpCodes.Stloc || o == OpCodes.Stloc_S || o == OpCodes.Stloc_0 || o == OpCodes.Stloc_1 || o == OpCodes.Stloc_2 || o == OpCodes.Stloc_3
		|| o == OpCodes.Ble || o == OpCodes.Ble_S || o == OpCodes.Bgt || o == OpCodes.Bgt_S
		|| o == OpCodes.Ble_Un || o == OpCodes.Ble_Un_S || o == OpCodes.Bgt_Un || o == OpCodes.Bgt_Un_S
		|| o == OpCodes.Cgt || o == OpCodes.Starg || o == OpCodes.Starg_S;

	// shift + mouse wheel over a cell: one step up or down
	public static void ScrollPrefix(float x, float y, Pawn p, WorkTypeDef wType)
	{
		try
		{
			Event e = Event.current;
			if (e.type != EventType.ScrollWheel || !e.shift || PrioritiesMod.S == null || !PrioritiesMod.S.shiftScroll
				|| !Find.PlaySettings.useWorkPriorities || p?.workSettings == null || p.WorkTypeIsDisabled(wType))
				return;
			if (!Mouse.IsOver(new Rect(x, y, 25f, 25f)))
				return;
			int v = p.workSettings.GetPriority(wType);
			// wheel down = less important (higher number), up = more important; 0 (off) sits below the last level
			int nv = e.delta.y > 0 ? (v == 0 ? 0 : v >= Max ? 0 : v + 1) : (v == 0 ? Max : Math.Max(1, v - 1));
			if (nv != v)
			{
				p.workSettings.SetPriority(wType, nv);
				SoundDefOf.DragSlider.PlayOneShotOnCamera();
			}
			e.Use();
		}
		catch
		{
		}
	}

	private static readonly Color[] Anchors =
	{
		new Color(0f, 1f, 0f),
		new Color(1f, 0.9f, 0.5f),
		new Color(0.8f, 0.7f, 0.5f),
		new Color(0.74f, 0.74f, 0.74f)
	};

	// the game's 4 colours spread over all levels (1 bright green … last grey)
	public static void ColorPostfix(int prio, ref Color __result)
	{
		int m = Max;
		if (m <= 4 || prio < 1)
			return;
		if (prio > m)
			prio = m;
		float t = (prio - 1f) / (m - 1f) * 3f;
		int i = Mathf.Min(2, Mathf.FloorToInt(t));
		__result = Color.Lerp(Anchors[i], Anchors[i + 1], t - i);
	}
}
