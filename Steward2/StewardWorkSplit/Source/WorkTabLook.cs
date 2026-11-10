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
			h.Patch(AccessTools.Method(typeof(PawnTable), nameof(PawnTable.PawnTableOnGUI)),
				postfix: new HarmonyMethod(typeof(WorkTabLook), nameof(TablePostfix)));
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

	// 0.2.1: labels in a staircase – no rotation (turned text landed in the wrong place on some
	// renderers). Column 1 on the top row, column 2 one row lower …, each label runs to the right over
	// the next columns (whose labels sit lower), with a thin line down to its own column.
	private static int rows = 3;
	private static float lineH = 16f;

	public static void HeaderHeightPostfix(ref int __result)
	{
		if (!OwnHeader)
			return;
		if (headerH < 0f)
		{
			Text.Font = GameFont.Tiny;
			lineH = Text.LineHeight;
			float w = 40f;
			foreach (WorkTypeDef d in DefDatabase<WorkTypeDef>.AllDefsListForReading)
				if (d.visible)
					w = Math.Max(w, Math.Min(MaxLabel, Text.CalcSize(Label(d)).x + 6f));
			Text.Font = GameFont.Small;
			float col = S.narrow ? 28f : 34f;
			rows = Mathf.Clamp(Mathf.CeilToInt(w / col), 2, 5);
			headerH = Mathf.Ceil(Band + 4f + rows * lineH + 8f);
		}
		__result = Math.Max(__result, (int)headerH);
	}

	private struct PendingLabel
	{
		public Rect rect;
		public string text;
		public Color color;
	}

	private static readonly List<PendingLabel> pending = new List<PendingLabel>();
	private static int colIndex;
	private static float lastX = float.MaxValue;

	// after the whole header row: the labels on top of every line
	public static void TablePostfix()
	{
		if (pending.Count == 0)
			return;
		Text.Font = GameFont.Tiny;
		Text.Anchor = TextAnchor.MiddleLeft;
		foreach (PendingLabel l in pending)
		{
			Widgets.DrawBoxSolid(l.rect, new Color(0.11f, 0.12f, 0.13f, 0.92f));
			GUI.color = l.color;
			Widgets.Label(new Rect(l.rect.x + 2f, l.rect.y, l.rect.width - 2f, l.rect.height), l.text);
		}
		GUI.color = Color.white;
		Text.Anchor = TextAnchor.UpperLeft;
		Text.Font = GameFont.Small;
		pending.Clear();
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
		// label (staircase)
		if (rect.x <= lastX)
		{
			colIndex = 0;
			pending.Clear();
		}
		lastX = rect.x;
		int row = colIndex++ % rows;
		Text.Font = GameFont.Tiny;
		string label = Label(w);
		float maxW = Math.Max(rect.width, rows * rect.width - 3f);
		float lw = Math.Min(Text.CalcSize(label).x + 6f, maxW);
		Rect area = new Rect(rect.x, rect.y + Band, rect.width, rect.height - Band);
		bool over = Mouse.IsOver(area);
		if (over)
			Widgets.DrawHighlight(area);
		float ly = rect.y + Band + 4f + row * lineH;
		Color lc = Folded(w) ? new Color(1f, 0.85f, 0.55f) : over ? Color.white : new Color(0.88f, 0.88f, 0.88f);
		GUI.color = new Color(1f, 1f, 1f, over ? 0.6f : 0.25f);
		Widgets.DrawLineVertical(rect.center.x, ly + lineH - 2f, rect.yMax - (ly + lineH - 2f));
		GUI.color = Color.white;
		pending.Add(new PendingLabel { rect = new Rect(rect.x + 1f, ly, lw, lineH), text = label.Truncate(lw - 4f), color = lc });
		Text.Font = GameFont.Small;
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
