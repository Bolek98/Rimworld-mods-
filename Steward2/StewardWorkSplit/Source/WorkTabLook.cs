using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace StewardWorkSplit;

// 0.2.0: a tidy Work tab with many columns.
//  - labels drawn slanted (or upright) – they no longer run over each other
//  - a coloured band over each split group (parent + its parts) and lines between groups
//  - a group can be folded into its parent column (▸ / ▾): one cell then sets all its parts,
//    "2–4" shows when the parts differ. Only the view changes – each part keeps its own priority.
//  - optional narrow columns
// Off when another Work tab mod (Fluffy's Work Tab, Compact Work Tab …) draws the tab.
public static class WorkTabLook
{
	public const int StyleSlanted = 0, StyleUpright = 1, StyleOriginal = 2;
	private const float Band = 20f;
	private const float MaxLabel = 118f;

	private static readonly Color[] GroupColors =
	{
		new Color(0.35f, 0.55f, 0.85f, 0.45f),
		new Color(0.85f, 0.6f, 0.3f, 0.45f),
		new Color(0.45f, 0.75f, 0.45f, 0.45f),
		new Color(0.75f, 0.45f, 0.75f, 0.45f),
		new Color(0.4f, 0.75f, 0.75f, 0.45f),
		new Color(0.8f, 0.75f, 0.35f, 0.45f)
	};

	private static bool? otherTab;
	private static Dictionary<WorkTypeDef, List<WorkTypeDef>> groups;   // parent -> its active parts
	private static Dictionary<WorkTypeDef, WorkTypeDef> parentOf;       // part -> parent
	private static Dictionary<WorkTypeDef, int> colorOf;
	private static float headerH = -1f;

	private static MethodInfo tipMethod, clickMethod;

	public static bool OtherWorkTab
	{
		get
		{
			if (!otherTab.HasValue)
				otherTab = ModsConfig.ActiveModsInLoadOrder.Any(m => m.PackageIdPlayerFacing != null
					&& m.PackageIdPlayerFacing.ToLowerInvariant().Contains("worktab"));
			return otherTab.Value;
		}
	}

	private static WorkSplitSettings S => WorkSplitMod.Settings;

	public static bool Enabled => S != null && !OtherWorkTab;

	public static bool OwnHeader => Enabled && S.headerStyle != StyleOriginal;

	public static void Patch(Harmony h)
	{
		try
		{
			Type t = typeof(PawnColumnWorker_WorkPriority);
			tipMethod = AccessTools.Method(t, "GetHeaderTip");
			clickMethod = AccessTools.Method(t, "HeaderClicked");
			h.Patch(AccessTools.PropertyGetter(t, nameof(PawnColumnWorker_WorkPriority.VisibleCurrently)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(VisiblePostfix)));
			h.Patch(AccessTools.Method(t, nameof(PawnColumnWorker_WorkPriority.DoHeader)),
				prefix: new HarmonyMethod(typeof(WorkTabLook), nameof(HeaderPrefix)));
			h.Patch(AccessTools.Method(t, nameof(PawnColumnWorker_WorkPriority.GetMinHeaderHeight)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(HeaderHeightPostfix)));
			h.Patch(AccessTools.Method(t, nameof(PawnColumnWorker_WorkPriority.GetMinWidth)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(WidthPostfix)));
			h.Patch(AccessTools.Method(t, nameof(PawnColumnWorker_WorkPriority.GetOptimalWidth)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(WidthPostfix)));
			h.Patch(AccessTools.Method(t, nameof(PawnColumnWorker_WorkPriority.DoCell)),
				prefix: new HarmonyMethod(typeof(WorkTabLook), nameof(CellPrefix)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(CellPostfix)));
		}
		catch (Exception e)
		{
			Log.Error("[Steward – Work Split] Work tab look could not hook in: " + e);
		}
	}

	// ---------------- groups ----------------
	private static void BuildGroups()
	{
		if (groups != null)
			return;
		groups = new Dictionary<WorkTypeDef, List<WorkTypeDef>>();
		parentOf = new Dictionary<WorkTypeDef, WorkTypeDef>();
		colorOf = new Dictionary<WorkTypeDef, int>();
		foreach (var kv in Split.Active)
		{
			WorkTypeDef part = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Value);
			if (part == null || parent == null || !part.visible)
				continue;
			if (!groups.TryGetValue(parent, out var list))
				groups[parent] = list = new List<WorkTypeDef>();
			list.Add(part);
			parentOf[part] = parent;
		}
		int i = 0;
		foreach (WorkTypeDef p in groups.Keys.OrderByDescending(x => x.naturalPriority))
			colorOf[p] = i++ % GroupColors.Length;
		foreach (var kv in groups)
			kv.Value.Sort((a, b) => b.naturalPriority.CompareTo(a.naturalPriority));
	}

	public static IEnumerable<WorkTypeDef> GroupParents()
	{
		BuildGroups();
		return groups.Keys.OrderByDescending(x => x.naturalPriority);
	}

	public static List<WorkTypeDef> PartsOf(WorkTypeDef parent)
	{
		BuildGroups();
		return groups.TryGetValue(parent, out var l) ? l : null;
	}

	public static bool Folded(WorkTypeDef parent)
	{
		if (!Enabled || !S.groups)
			return false;
		BuildGroups();
		return groups.ContainsKey(parent) && !S.expanded.Contains(parent.defName);
	}

	public static void SetFolded(WorkTypeDef parent, bool folded)
	{
		if (folded)
			S.expanded.Remove(parent.defName);
		else if (!S.expanded.Contains(parent.defName))
			S.expanded.Add(parent.defName);
		Changed();
	}

	public static void Changed()
	{
		headerH = -1f;
		WorkSplitMod.Instance?.WriteSettings();
		try
		{
			if (Find.WindowStack?.WindowOfType<MainTabWindow_Work>() is MainTabWindow_Work w)
				(AccessTools.Field(typeof(MainTabWindow_PawnTable), "table")?.GetValue(w) as PawnTable)?.SetDirty();
		}
		catch
		{
		}
	}

	// ---------------- patches ----------------
	public static void VisiblePostfix(PawnColumnWorker_WorkPriority __instance, ref bool __result)
	{
		if (!__result || !Enabled || !S.groups)
			return;
		BuildGroups();
		WorkTypeDef w = __instance.def?.workType;
		if (w != null && parentOf.TryGetValue(w, out WorkTypeDef parent) && Folded(parent))
			__result = false;
	}

	public static void WidthPostfix(ref int __result)
	{
		if (Enabled && S.narrow)
			__result = Math.Min(__result, 28);
	}

	public static void HeaderHeightPostfix(ref int __result)
	{
		if (!OwnHeader)
			return;
		if (headerH < 0f)
		{
			Text.Font = GameFont.Tiny;
			float w = 40f, lineH = Text.LineHeight;
			foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefsListForReading)
				if (d.visible)
					w = Math.Max(w, Math.Min(MaxLabel, Text.CalcSize(Label(d)).x + 14f));
			Text.Font = GameFont.Small;
			float lab = S.headerStyle == StyleSlanted ? w * 0.7071f + lineH * 0.7071f : w;
			headerH = Mathf.Ceil(Band + 6f + lab + 6f);
		}
		__result = Math.Max(__result, (int)headerH);
	}

	private static string Label(WorkTypeDef w)
	{
		string s = (w.labelShort.NullOrEmpty() ? w.label : w.labelShort).CapitalizeFirst();
		if (Folded(w))
			s += " +" + PartsOf(w).Count;
		return s;
	}

	public static bool HeaderPrefix(PawnColumnWorker_WorkPriority __instance, Rect rect, PawnTable table)
	{
		if (!OwnHeader || table == null)
			return true;
		try
		{
			DrawHeader(__instance, rect, table);
		}
		catch (Exception e)
		{
			Log.ErrorOnce("[Steward – Work Split] Work tab header: " + e, 0x5175a1);
			Text.Anchor = TextAnchor.UpperLeft;
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			return true;
		}
		return false;
	}

	private static void DrawHeader(PawnColumnWorker_WorkPriority col, Rect rect, PawnTable table)
	{
		BuildGroups();
		WorkTypeDef w = col.def.workType;
		WorkTypeDef parent = groups.ContainsKey(w) ? w : parentOf.TryGetValue(w, out var pp) ? pp : null;
		bool isParent = parent == w;
		// band
		Rect band = new Rect(rect.x, rect.y, rect.width, Band - 2f);
		if (parent != null && S.groups)
		{
			Color c = GroupColors[colorOf[parent]];
			Widgets.DrawBoxSolid(band, c);
			if (isParent)
			{
				bool folded = Folded(w);
				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(band, folded ? "▸" : "▾");
				Text.Anchor = TextAnchor.UpperLeft;
				if (Mouse.IsOver(band))
				{
					Widgets.DrawHighlight(band);
					TooltipHandler.TipRegion(band, (folded ? "SWS_Unfold" : "SWS_Fold").Translate(w.labelShort.CapitalizeFirst(), PartsOf(w).Count));
				}
				if (Widgets.ButtonInvisible(band))
				{
					SetFolded(w, !folded);
					(folded ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
				}
			}
			// lines around the group
			GUI.color = new Color(c.r, c.g, c.b, 0.9f);
			if (isParent)
				Widgets.DrawLineVertical(rect.x, rect.y, rect.height);
			if (IsGroupEnd(w, parent))
				Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
			GUI.color = Color.white;
		}
		// label
		Text.Font = GameFont.Tiny;
		string label = Label(w);
		Vector2 size = Text.CalcSize(label);
		float lw = Math.Min(size.x + 2f, MaxLabel);
		Rect area = new Rect(rect.x, rect.y + Band, rect.width, rect.height - Band);
		bool over = Mouse.IsOver(area);
		if (over)
			Widgets.DrawHighlight(area);
		GUI.color = over ? Color.white : new Color(0.9f, 0.9f, 0.9f);
		if (Folded(w))
			GUI.color = new Color(1f, 0.85f, 0.55f);
		Vector2 pivot = new Vector2(rect.center.x, rect.yMax - 6f);
		Matrix4x4 keep = GUI.matrix;
		try
		{
			if (S.headerStyle == StyleSlanted)
			{
				GUIUtility.RotateAroundPivot(45f, pivot);
				Text.Anchor = TextAnchor.MiddleRight;
				Widgets.Label(new Rect(pivot.x - lw, pivot.y - size.y / 2f, lw, size.y), label.Truncate(lw));
			}
			else
			{
				GUIUtility.RotateAroundPivot(-90f, pivot);
				Text.Anchor = TextAnchor.MiddleLeft;
				Widgets.Label(new Rect(pivot.x, pivot.y - size.y / 2f, lw, size.y), label.Truncate(lw));
			}
		}
		finally
		{
			GUI.matrix = keep;
		}
		Text.Anchor = TextAnchor.UpperLeft;
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
		// sorted by this column
		if (table.SortingBy == col.def)
		{
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(rect.xMax - 12f, rect.yMax - 16f, 12f, 16f), table.SortingDescending ? "▼" : "▲");
			Text.Font = GameFont.Small;
		}
		if (over)
		{
			string tip = null;
			try { tip = tipMethod?.Invoke(col, new object[] { table }) as string; } catch { }
			if (!tip.NullOrEmpty())
				TooltipHandler.TipRegion(area, tip);
		}
		if (Widgets.ButtonInvisible(area))
		{
			try { clickMethod?.Invoke(col, new object[] { rect, table }); }
			catch (Exception e) { Log.ErrorOnce("[Steward – Work Split] header click: " + e.Message, 0x5175a2); }
		}
	}

	private static bool IsGroupEnd(WorkTypeDef w, WorkTypeDef parent)
	{
		if (Folded(parent))
			return w == parent;
		List<WorkTypeDef> parts = PartsOf(parent);
		return parts != null && parts.Count > 0 && parts[parts.Count - 1] == w;
	}

	// a folded parent: one click sets every part the same; "2–4" when they differ
	public static void CellPrefix(PawnColumnWorker_WorkPriority __instance, Pawn pawn, out int __state)
	{
		__state = -1;
		WorkTypeDef w = __instance.def?.workType;
		if (w == null || pawn?.workSettings == null || !pawn.workSettings.EverWork || !Folded(w))
			return;
		__state = pawn.workSettings.GetPriority(w);
	}

	public static void CellPostfix(PawnColumnWorker_WorkPriority __instance, Rect rect, Pawn pawn, int __state)
	{
		WorkTypeDef w = __instance.def?.workType;
		if (w == null || !Enabled)
			return;
		if (S.groups && !OtherWorkTab)
		{
			BuildGroups();
			WorkTypeDef parent = groups.ContainsKey(w) ? w : parentOf.TryGetValue(w, out var pp) ? pp : null;
			if (parent != null)
			{
				Color c = GroupColors[colorOf[parent]];
				GUI.color = new Color(c.r, c.g, c.b, 0.5f);
				if (parent == w)
					Widgets.DrawLineVertical(rect.x, rect.y, rect.height);
				if (IsGroupEnd(w, parent))
					Widgets.DrawLineVertical(rect.xMax - 1f, rect.y, rect.height);
				GUI.color = Color.white;
			}
		}
		if (__state < 0 || pawn?.workSettings == null)
			return;
		List<WorkTypeDef> parts = PartsOf(w);
		if (parts == null)
			return;
		int now = pawn.workSettings.GetPriority(w);
		if (now != __state)
		{
			foreach (WorkTypeDef p in parts)
				if (!pawn.WorkTypeIsDisabled(p))
					pawn.workSettings.SetPriority(p, now);
		}
		// mixed?
		int lo = pawn.WorkTypeIsDisabled(w) ? 99 : now, hi = lo == 99 ? -1 : now;
		foreach (WorkTypeDef p in parts)
		{
			if (pawn.WorkTypeIsDisabled(p))
				continue;
			int v = pawn.workSettings.GetPriority(p);
			lo = Math.Min(lo, v);
			hi = Math.Max(hi, v);
		}
		if (hi < 0 || lo == hi)
			return;
		string txt = Find.PlaySettings.useWorkPriorities ? (lo == 0 ? "–" : lo.ToString()) + "–" + (hi == 0 ? "–" : hi.ToString()) : "½";
		Text.Font = GameFont.Tiny;
		GUI.color = new Color(1f, 0.8f, 0.45f);
		Text.Anchor = TextAnchor.LowerRight;
		Widgets.Label(new Rect(rect.x, rect.y, rect.width - 1f, rect.height + 2f), txt);
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
	}
}
