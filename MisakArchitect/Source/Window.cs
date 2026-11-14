using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakArchitect;

// The architect menu: categories on the left, a search box on top, the buildings in the middle and
// what the hovered one is on the right. Favorites (★) and recently used ones have their own lists.
public class MainTabWindow_MisakArchitect : MainTabWindow_Architect
{
	public static bool dirty;

	private const string Fav = "@fav";
	private const string Recent = "@recent";
	private const float Head = 34f;
	private const float ColW = 168f;
	// 0.3.0: categories in 1–3 columns
	private static int SideCols => Mathf.Clamp(S?.sideCols ?? 2, 1, 3);
	private static float SideW => SideCols * ColW + 16f;
	private const float InfoW = 250f;
	private const float Cell = 75f;
	private const float Gap = 6f;

	private readonly QuickSearchWidget search = new QuickSearchWidget();
	private string cat;
	private Vector2 scrollGrid, scrollSide, scrollInfo;
	private Designator hover;
	private bool focusDone;
	private readonly Dictionary<string, string> subSel = new Dictionary<string, string>();
	private int drag;
	private Vector2 dragStart;
	private Rect dragRect;

	private static MASettings S => MisakArchitectMod.S;

	public override Vector2 RequestedTabSize => new Vector2(Mathf.Clamp(S != null && S.width > 0f ? S.width : 1240f, 640f, UI.screenWidth - 10f), Mathf.Clamp(S?.height ?? 380f, 240f, UI.screenHeight - 120f));

	protected override float Margin => 0f;

	public MainTabWindow_MisakArchitect()
	{
		closeOnAccept = false;
		search.inactiveTextColor = Color.white.ToTransparent(0.4f);
	}

	public override void PreOpen()
	{
		base.PreOpen();
		Catalog.Rebuild();
		Subs.Clear();
		ClearIcons();
		cat ??= S.lastCategory;
		if (!ValidCat(cat))
			cat = S.grouped ? "g:" + (Groups.Order.FirstOrDefault(g => GroupCount(g) > 0 && g != "orders") ?? "build") : Catalog.cats.FirstOrDefault(c => c.Visible)?.defName;
		focusDone = false;
	}

	public override void PostClose()
	{
		base.PostClose();
		S.lastCategory = cat;
		if (dirty) { dirty = false; MisakArchitectMod.Save(); }
	}

	public override void WindowUpdate()
	{
		base.WindowUpdate();
		DesignationCategoryDef c = Catalog.cats.FirstOrDefault(x => x.defName == cat);
		if ((c != null && c.showPowerGrid || cat == "g:power") && !search.filter.Active)
			OverlayDrawHandler.DrawPowerGridOverlayThisFrame();
	}

	public override void ExtraOnGUI()
	{
		// the selected designator's own buttons (rotate, …) above the window
		Designator sel = Find.DesignatorManager.SelectedDesignator;
		if (sel != null)
		{
			try { sel.DoExtraGuiControls(windowRect.x, windowRect.y - 4f); }
			catch { }
		}
	}

	public override void OnCancelKeyPressed()
	{
		if (search.CurrentlyFocused() || search.filter.Active)
		{
			search.Reset();
			search.Unfocus();
			return;
		}
		base.OnCancelKeyPressed();
	}

	public override void Notify_ClickOutsideWindow()
	{
		base.Notify_ClickOutsideWindow();
		search.Unfocus();
	}

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Small;
		hover = null;
		MUI.Fill(inRect, MUI.CardBg);

		// 0.4.0: Tab / Shift+Tab goes through the folders
		if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Tab && !search.CurrentlyFocused())
		{
			var ids = SideIds();
			if (ids.Count > 0)
			{
				int at = ids.IndexOf(cat);
				cat = ids[(at + (Event.current.shift ? -1 : 1) + ids.Count) % ids.Count];
				scrollGrid = Vector2.zero;
				SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
			}
			Event.current.Use();
		}
		List<Designator> items = Items(out string emptyText);
		if (KeyBindingDefOf.Accept.KeyDownEvent)
		{
			var shown = items.Where(Catalog.Shown).ToList();
			if (search.filter.Active && shown.Count == 1)
			{
				shown[0].ProcessInput(Event.current);
				Event.current.Use();
			}
			else
			{
				Close();
				Event.current.Use();
			}
		}

		// header
		Rect head = new Rect(inRect.x, inRect.y, inRect.width, Head);
		MUI.Fill(head, MUI.CardBg2);
		GUI.color = MUI.Accent;
		Text.Font = GameFont.Small;
		Text.Anchor = TextAnchor.MiddleLeft;
		Widgets.Label(new Rect(head.x + 10f, head.y, SideW - 10f, head.height), "MA_Title".Translate());
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		Rect sr = new Rect(head.x + SideW, head.y + 5f, Mathf.Min(320f, head.width - SideW - InfoW), 24f);
		search.OnGUI(sr, () => scrollGrid = Vector2.zero);
		if (!focusDone) { UI.UnfocusCurrentControl(); focusDone = true; }
		if (search.filter.Active)
		{
			GUI.color = MUI.Muted;
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(sr.xMax + 10f, head.y, 260f, head.height), "MA_Found".Translate(items.Count(Catalog.Shown)));
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
		}

		// header switches: group by mod, show what is not researched yet
		float tx = head.xMax - 40f;
		tx = Toggle(ref tx, head, "MA_ShowLocked", ref S.showLocked);
		tx = Toggle(ref tx, head, "MA_Afford", ref S.onlyAfford);
		tx = Toggle(ref tx, head, "MA_GroupedShort", ref S.grouped, () => { if (!ValidCat(cat)) cat = S.grouped ? "g:build" : Catalog.cats.FirstOrDefault(x => x.Visible)?.defName; });
		tx = Toggle(ref tx, head, "MA_ByMod", ref S.byMod, () => Subs.Clear());
		ColsButton(ref tx, head);

		Rect body = new Rect(inRect.x, head.yMax, inRect.width, inRect.height - Head);
		float infoW = S.showInfo ? InfoW : 0f;
		Sidebar(new Rect(body.x, body.y, SideW, body.height));
		Rect gridR = new Rect(body.x + SideW, body.y, body.width - SideW - infoW, body.height).ContractedBy(6f);
		Grid(gridR, items, emptyText);
		if (S.showInfo)
			Info(new Rect(body.xMax - infoW, body.y, infoW, body.height));
		Resizer(inRect);
	}

	private float Toggle(ref float right, Rect head, string key, ref bool v, Action changed = null)
	{
		string label = key.Translate();
		Text.Font = GameFont.Tiny;
		float w = Text.CalcSize(label).x + 22f;
		Rect r = new Rect(right - w, head.y + 7f, w, 20f);
		MUI.Fill(r, v ? MUI.OnBg : MUI.CardBg);
		MUI.Frame(r, v ? MUI.Accent : MUI.Border);
		GUI.color = v ? MUI.Accent : MUI.Muted;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(r, label);
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		TooltipHandler.TipRegion(r, (key + "_Desc").Translate());
		if (Widgets.ButtonInvisible(r)) { v = !v; changed?.Invoke(); dirty = true; scrollGrid = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
		right = r.x - 6f;
		return right;
	}

	// 0.2.0: drag the top edge (height), the right edge (width) or the top right corner (both)
	private void Resizer(Rect inRect)
	{
		Rect top = new Rect(inRect.x, inRect.y, inRect.width - 18f, 5f);
		Rect right = new Rect(inRect.xMax - 5f, inRect.y + 18f, 5f, inRect.height - 18f);
		Rect corner = new Rect(inRect.xMax - 16f, inRect.y, 16f, 16f);
		Vector2 m = UI.MousePositionOnUIInverted;
		bool overC = Mouse.IsOver(corner), overT = Mouse.IsOver(top), overR = Mouse.IsOver(right);
		GUI.color = new Color(1f, 1f, 1f, drag == 3 || overC ? 0.9f : 0.35f);
		Widgets.DrawLineHorizontal(corner.x + 4f, corner.y + 3f, 10f);
		Widgets.DrawLineVertical(corner.xMax - 3f, corner.y + 3f, 10f);
		GUI.color = Color.white;
		if (overT || drag == 1) Widgets.DrawBoxSolid(top, MUI.Accent.ToTransparent(0.6f));
		if (overR || drag == 2) Widgets.DrawBoxSolid(right, MUI.Accent.ToTransparent(0.6f));
		TooltipHandler.TipRegion(corner, "MA_ResizeTip".Translate());
		if (drag == 0 && Event.current.type == EventType.MouseDown && Event.current.button == 0 && (overC || overT || overR))
		{
			drag = overC ? 3 : overT ? 1 : 2;
			dragStart = m;
			dragRect = windowRect;
			Event.current.Use();
		}
		if (drag == 0) return;
		if (!Input.GetMouseButton(0))
		{
			drag = 0;
			S.height = windowRect.height;
			S.width = windowRect.width;
			dirty = true;
			return;
		}
		if ((drag & 1) != 0)
		{
			float h = Mathf.Clamp(dragRect.height - (m.y - dragStart.y), 240f, UI.screenHeight - 120f);
			windowRect.y = dragRect.yMax - h;
			windowRect.height = h;
		}
		if ((drag & 2) != 0)
			windowRect.width = Mathf.Clamp(dragRect.width + (m.x - dragStart.x), 640f, UI.screenWidth - 10f);
		if (Event.current.type == EventType.MouseDrag) Event.current.Use();
	}

	// ---------------------------------------------------------------- lists

	private List<Designator> Items(out string emptyText)
	{
		emptyText = null;
		if (search.filter.Active)
		{
			string t = search.filter.Text.ToLowerInvariant();
			emptyText = "MA_NoMatch".Translate();
			return Catalog.flat.Where(e => e.search.Contains(t) || search.filter.Matches(e.des.Label)).Select(e => e.des).ToList();
		}
		if (cat == Fav)
		{
			emptyText = "MA_FavEmpty".Translate();
			return S.favorites.Select(k => Catalog.byKey.TryGetValue(k, out var e) ? e.des : null).Where(d => d != null).ToList();
		}
		if (cat == Recent)
		{
			emptyText = "MA_RecentEmpty".Translate();
			return S.recent.Select(k => Catalog.byKey.TryGetValue(k, out var e) ? e.des : null).Where(d => d != null).ToList();
		}
		emptyText = "NothingAvailableInCategory".Translate();
		if (cat != null && cat.StartsWith("g:"))
			return Catalog.groupItems.TryGetValue(cat.Substring(2), out var gl) ? gl : new List<Designator>();
		DesignationCategoryDef c = Catalog.cats.FirstOrDefault(x => x.defName == cat);
		return c != null && Catalog.grid.TryGetValue(c, out var l) ? l : new List<Designator>();
	}

	// 0.3.0: how many columns the categories use (1 → 2 → 3)
	private void ColsButton(ref float right, Rect head)
	{
		string label = "MA_Cols".Translate(SideCols);
		Text.Font = GameFont.Tiny;
		float w = Text.CalcSize(label).x + 22f;
		Rect r = new Rect(right - w, head.y + 7f, w, 20f);
		MUI.Fill(r, Mouse.IsOver(r) ? MUI.CardBg2 : MUI.CardBg);
		MUI.Frame(r, MUI.Border);
		GUI.color = MUI.Text1;
		Text.Anchor = TextAnchor.MiddleCenter;
		Widgets.Label(r, label);
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		TooltipHandler.TipRegion(r, "MA_Cols_Desc".Translate());
		if (Widgets.ButtonInvisible(r))
		{
			S.sideCols = S.sideCols % 3 + 1;
			dirty = true;
			SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
		}
		right = r.x - 6f;
	}

	// one picture per category: the first thing in it you can build now (or the first at all)
	private static readonly Dictionary<string, Designator> iconOf = new Dictionary<string, Designator>();

	public static void ClearIcons() => iconOf.Clear();

	private static Designator IconFor(DesignationCategoryDef c)
	{
		if (iconOf.TryGetValue(c.defName, out Designator d))
			return d;
		d = null;
		if (Catalog.grid.TryGetValue(c, out var list))
			d = PickIcon(list);
		iconOf[c.defName] = d;
		return d;
	}

	private static void DrawIcon(Rect r, Designator d, float alpha)
	{
		if (d == null) return;
		try
		{
			if (d is Designator_Build b && b.PlacingDef != null)
			{
				ThingDef stuff = b.PlacingDef is ThingDef td && td.MadeFromStuff ? GenStuff.DefaultStuffFor(td) : null;
				GUI.color = new Color(1f, 1f, 1f, alpha);
				Widgets.DefIcon(r, b.PlacingDef, stuff);
			}
			else if (d.icon is Texture2D t)
			{
				Color c = d.defaultIconColor;
				GUI.color = new Color(c.r, c.g, c.b, c.a * alpha);
				Widgets.DrawTextureFitted(r, t, 1f);
			}
		}
		catch { }
		GUI.color = Color.white;
	}

	private static List<string> SideIds() => S.grouped
		? Groups.Order.Where(g => GroupCount(g) > 0).Select(g => "g:" + g).ToList()
		: Catalog.cats.Where(c => c.Visible && Catalog.grid.TryGetValue(c, out var l) && l.Any(Catalog.Shown)).Select(c => c.defName).ToList();

	private static bool ValidCat(string c)
	{
		if (c == null) return false;
		if (c == Fav || c == Recent) return true;
		if (c.StartsWith("g:")) return S.grouped && Groups.Order.Contains(c.Substring(2));
		return !S.grouped && Catalog.cats.Any(x => x.defName == c);
	}

	private static int GroupCount(string g) => Catalog.groupItems.TryGetValue(g, out var l) ? l.Count(Catalog.Shown) : 0;

	// picture of a main folder: a fixed building of the base game, else the first thing in it
	private static void GroupIcon(Rect r, string g, float alpha)
	{
		Def d = Groups.IconDef(g);
		if (d is BuildableDef bd)
		{
			GUI.color = new Color(1f, 1f, 1f, alpha);
			try { Widgets.DefIcon(r, bd, bd is ThingDef td && td.MadeFromStuff ? GenStuff.DefaultStuffFor(td) : null); } catch { }
			GUI.color = Color.white;
			return;
		}
		if (Catalog.groupItems.TryGetValue(g, out var l))
			DrawIcon(r, PickIcon(l), alpha);
	}

	// 0.4.0: never the "delete" or "cancel" order as the picture
	private static Designator PickIcon(IEnumerable<Designator> list)
	{
		var flat = list.SelectMany(x => x is Designator_Dropdown dd ? dd.Elements.AsEnumerable() : new[] { x }).ToList();
		bool Bad(Designator x)
		{
			string n = x.GetType().Name;
			return n.Contains("Delete") || n.Contains("Cancel") || n.Contains("Remove") || n.Contains("Shrink") || n.Contains("Clear") || n.Contains("Deconstruct");
		}
		return flat.FirstOrDefault(x => x is Designator_Build && Catalog.Shown(x))
			?? flat.FirstOrDefault(x => x is Designator_Build)
			?? flat.FirstOrDefault(x => x.icon != null && !Bad(x))
			?? flat.FirstOrDefault(x => x.icon != null);
	}

	private void Sidebar(Rect r)
	{
		MUI.Fill(r, MUI.CardBg2);
		var cats = new List<(string id, string label, int count, bool on, DesignationCategoryDef def)>();
		if (S.grouped)
		{
			// 0.4.0: main folders; empty ones stay hidden until something in them can be built
			bool first = S.seenGroups.Count == 0;
			foreach (string g in Groups.Order)
			{
				int n = GroupCount(g);
				if (n == 0) continue;
				if (first) S.seenGroups.Add("g:" + g);
				cats.Add(("g:" + g, Groups.Label(g), n, true, null));
			}
		}
		else
		foreach (DesignationCategoryDef c in Catalog.cats)
		{
			int n = Catalog.grid.TryGetValue(c, out var l) ? l.Count(Catalog.Shown) : 0;
			cats.Add((c.defName, Catalog.CatLabel(c), n, c.Visible && n > 0, c));
		}
		int cols = SideCols;
		float cw = (r.width - 16f) / cols;
		float h = cols == 1 ? 28f : 30f;
		int topRows = cols == 1 ? 2 : 1;          // favorites + recent: two rows in one column, side by side otherwise
		int catRows = Mathf.CeilToInt(cats.Count / (float)cols);
		Rect view = new Rect(0f, 0f, r.width - 16f, (topRows + catRows) * h + 16f);
		Widgets.BeginScrollView(r, ref scrollSide, view);
		// favorites, recent
		var top = new[]
		{
			(id: Fav, label: (string)("★ " + "MA_Favorites".Translate()), count: S.favorites.Count(k => Catalog.byKey.ContainsKey(k))),
			(id: Recent, label: (string)("↺ " + "MA_Recent".Translate()), count: S.recent.Count(k => Catalog.byKey.ContainsKey(k)))
		};
		float y = 4f;
		for (int i = 0; i < 2; i++)
		{
			Rect rr = cols == 1 ? new Rect(0f, y + i * h, view.width, h) : new Rect(i * view.width / 2f, y, view.width / 2f, h);
			Tile(rr, top[i].id, top[i].label, top[i].count, true, null);
		}
		y += topRows * h + 4f;
		MUI.Fill(new Rect(8f, y - 3f, view.width - 16f, 1f), MUI.Border);
		for (int i = 0; i < cats.Count; i++)
		{
			var c = cats[i];
			Rect rr = new Rect((i % cols) * cw, y + (i / cols) * h, cw, h);
			if (rr.yMax < scrollSide.y || rr.y > scrollSide.y + r.height)
				continue;
			Tile(rr, c.id, c.label, c.count, c.on, c.def);
		}
		Widgets.EndScrollView();
	}

	private void Tile(Rect rr, string id, string label, int count, bool on, DesignationCategoryDef def)
	{
		bool sel = !search.filter.Active && id == cat;
		if (sel) MUI.Fill(rr, MUI.OnBg);
		else if (Mouse.IsOver(rr)) MUI.Fill(rr, MUI.CardBg);
		if (sel) MUI.Fill(new Rect(rr.x, rr.y, 3f, rr.height), MUI.Accent);
		float x = rr.x + 7f;
		bool isGroup = id.StartsWith("g:");
		if (def != null || isGroup)
		{
			Rect ic = new Rect(x, rr.y + (rr.height - 22f) / 2f, 22f, 22f);
			if (isGroup) GroupIcon(ic, id.Substring(2), 1f);
			else DrawIcon(ic, IconFor(def), on ? 1f : 0.4f);
			x = ic.xMax + 5f;
		}
		// 0.4.0: a folder that just got its first building is marked new until opened
		bool isNew = isGroup && !S.seenGroups.Contains(id);
		if (isGroup && sel && !S.seenGroups.Contains(id)) { S.seenGroups.Add(id); dirty = true; }
		bool narrow = SideCols > 1;
		Text.Font = GameFont.Tiny;
		string cnt = count > 0 ? count.ToString() : "";
		float cntW = count > 0 ? Text.CalcSize(cnt).x + 6f : 0f;
		Text.Font = narrow ? GameFont.Tiny : GameFont.Small;
		GUI.color = sel ? MUI.Accent : on ? MUI.Text1 : MUI.Muted;
		Text.Anchor = TextAnchor.MiddleLeft;
		float lw = rr.xMax - x - cntW - 2f;
		if (isNew)
		{
			Text.Font = GameFont.Tiny;
			string nl = "MA_New".Translate();
			float nw = Text.CalcSize(nl).x + 8f;
			Rect pill = new Rect(rr.xMax - cntW - nw - 6f, rr.y + (rr.height - 16f) / 2f, nw, 16f);
			MUI.Fill(pill, MUI.Accent, 4f);
			GUI.color = MUI.CardBg;
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(pill, nl);
			Text.Anchor = TextAnchor.MiddleLeft;
			GUI.color = sel ? MUI.Accent : on ? MUI.Text1 : MUI.Muted;
			Text.Font = SideCols > 1 ? GameFont.Tiny : GameFont.Small;
			lw -= nw + 4f;
		}
		Widgets.Label(new Rect(x, rr.y, lw, rr.height), label.Truncate(lw));
		if (count > 0)
		{
			Text.Font = GameFont.Tiny;
			Text.Anchor = TextAnchor.MiddleRight;
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(rr.xMax - cntW - 4f, rr.y, cntW, rr.height), cnt);
		}
		Text.Font = GameFont.Small;
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		if (narrow && Mouse.IsOver(rr) && label.Truncate(lw) != label)
			TooltipHandler.TipRegion(rr, label);
		if (Widgets.ButtonInvisible(rr))
		{
			if (!on && id != Fav && id != Recent)
				Messages.Message("NothingAvailableInCategory".Translate() + ": " + label, MessageTypeDefOf.RejectInput, false);
			else
			{
				cat = id;
				scrollGrid = Vector2.zero;
				if (search.filter.Active) search.Reset();
				SoundDefOf.ArchitectCategorySelect.PlayOneShotOnCamera();
			}
		}
	}

	// ---------------------------------------------------------------- the buildings

	private static bool Locked(Designator d)
	{
		if (d is Designator_Dropdown dd) d = dd.Elements.FirstOrDefault();
		return d is Designator_Build b && b.PlacingDef != null && !Catalog.Shown(d) && !b.PlacingDef.IsResearchFinished;
	}

	private void Grid(Rect r, List<Designator> items, string emptyText)
	{
		List<Designator> shown = items.Where(d => Catalog.Shown(d) || (S.showLocked && Locked(d))).ToList();
		if (S.onlyAfford) shown = shown.Where(d => !Catalog.Shown(d) || Missing(d) == null).ToList();   // 0.4.0
		// sub-categories as chips above the buildings
		var groups = shown.GroupBy(d => Subs.Of(d)).OrderBy(g => Subs.Rank(g.Key)).ThenBy(g => g.Key).ToList();
		string selKey = (search.filter.Active ? "@search" : cat ?? "") + (S.byMod ? "#mod" : "");
		string sub = subSel.TryGetValue(selKey, out string ss) ? ss : null;
		if (sub != null && !groups.Any(g => g.Key == sub)) sub = null;
		if (groups.Count > 1)
		{
			float x = r.x, y = r.y;
			Text.Font = GameFont.Tiny;
			var chips = new List<(string key, string label)> { (null, "MA_SubAll".Translate() + " " + shown.Count) };
			chips.AddRange(groups.Select(g => (g.Key, Subs.Label(g.Key) + " " + g.Count())));
			foreach (var (key, label) in chips)
			{
				float w = Text.CalcSize(label).x + 16f;
				if (x + w > r.xMax) { x = r.x; y += 24f; }
				Rect cr = new Rect(x, y, w, 21f);
				bool on = sub == key;
				MUI.Fill(cr, on ? MUI.OnBg : Mouse.IsOver(cr) ? MUI.CardBg2 : MUI.CardBg);
				MUI.Frame(cr, on ? MUI.Accent : MUI.Border);
				GUI.color = on ? MUI.Accent : MUI.Text1;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(cr, label);
				Text.Anchor = TextAnchor.UpperLeft;
				GUI.color = Color.white;
				if (Widgets.ButtonInvisible(cr)) { subSel[selKey] = key; scrollGrid = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
				x += w + 4f;
			}
			Text.Font = GameFont.Small;
			float used = y + 26f - r.y;
			r = new Rect(r.x, r.y + used, r.width, r.height - used);
			if (sub != null) shown = shown.Where(d => Subs.Of(d) == sub).ToList();
		}
		if (shown.Count == 0)
		{
			GUI.color = MUI.Muted;
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(r, emptyText ?? "");
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			return;
		}
		int cols = Mathf.Max(1, Mathf.FloorToInt((r.width - 16f + Gap) / (Cell + Gap)));
		// 0.4.0: the base game and DLCs first, things from mods below a line
		var vanilla = shown.Where(d => !Groups.FromMod(d, CatOf(d))).ToList();
		var modded = shown.Where(d => Groups.FromMod(d, CatOf(d))).ToList();
		bool split = vanilla.Count > 0 && modded.Count > 0 && !S.byMod;
		shown = vanilla.Concat(modded).ToList();
		int vRows = Mathf.CeilToInt(vanilla.Count / (float)cols);
		const float SepH = 22f;
		Vector2 Pos(int i)
		{
			if (!split || i < vanilla.Count) return new Vector2((i % cols) * (Cell + Gap), (i / cols) * (Cell + Gap + 2f));
			int j = i - vanilla.Count;
			return new Vector2((j % cols) * (Cell + Gap), vRows * (Cell + Gap + 2f) + SepH + (j / cols) * (Cell + Gap + 2f));
		}
		float totalH = shown.Count == 0 ? 0f : Pos(shown.Count - 1).y + Cell + Gap + 2f;
		Rect view = new Rect(0f, 0f, r.width - 16f, totalH);
		Widgets.BeginScrollView(r, ref scrollGrid, view);
		Text.Font = GameFont.Tiny;
		Gizmo interacted = null, floatMenu = null;
		Event ev = null;
		Designator selected = Find.DesignatorManager.SelectedDesignator;
		if (split)
		{
			float sy = vRows * (Cell + Gap + 2f);
			MUI.Fill(new Rect(0f, sy + SepH / 2f, view.width, 1f), MUI.Border);
			string fm = "MA_FromMods".Translate();
			float fw = Text.CalcSize(fm).x + 12f;
			Rect fr = new Rect(8f, sy + 2f, fw, SepH - 4f);
			MUI.Fill(fr, MUI.CardBg);
			GUI.color = MUI.Muted;
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(fr, fm);
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
		}
		for (int i = 0; i < shown.Count; i++)
		{
			Designator d = shown[i];
			Vector2 pos = Pos(i);
			float x = pos.x;
			float y = pos.y;
			Rect cell = new Rect(x, y, Cell, Cell);
			// skip what is scrolled out of sight
			if (y + Cell < scrollGrid.y - 4f || y > scrollGrid.y + r.height + 4f)
				continue;
			if (!Catalog.Shown(d))
			{
				LockedTile(cell, d);
				continue;
			}
			string key = Catalog.Key(d);
			bool fav = key != null && S.favorites.Contains(key);
			Rect star = new Rect(cell.xMax - 19f, cell.y + 1f, 18f, 18f);
			bool showStar = key != null && (fav || Mouse.IsOver(cell));
			if (showStar && Widgets.ButtonInvisible(star))
			{
				if (fav) S.favorites.Remove(key); else S.favorites.Add(key);
				fav = !fav;
				dirty = true;
				SoundDefOf.Tick_High.PlayOneShotOnCamera();
			}
			GizmoResult res;
			try
			{
				res = d.GizmoOnGUI(new Vector2(x, y), Cell, new GizmoRenderParms
				{
					highLight = selected != null && (selected == d || (d is Designator_Dropdown dd && dd.Elements.Contains(selected))),
					isFirst = i == 0
				});
			}
			catch { continue; }
			if (res.State == GizmoState.Interacted || (res.State == GizmoState.OpenedFloatMenu && !d.RightClickFloatMenuOptions.Any()))
			{
				interacted = d;
				ev = res.InteractEvent;
			}
			else if (res.State == GizmoState.OpenedFloatMenu)
				floatMenu = d;
			if ((int)res.State >= 1)
				hover = d;
			Badges(cell, d);   // 0.4.0
			if (showStar)
			{
				GUI.color = fav ? new Color(1f, 0.82f, 0.3f) : new Color(1f, 1f, 1f, Mouse.IsOver(star) ? 0.9f : 0.45f);
				Text.Font = GameFont.Small;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(star, "★");
				Text.Anchor = TextAnchor.UpperLeft;
				Text.Font = GameFont.Tiny;
				GUI.color = Color.white;
				if (Mouse.IsOver(star))
					TooltipHandler.TipRegion(star, fav ? "MA_FavRemove".Translate() : "MA_FavAdd".Translate());
			}
		}
		Text.Font = GameFont.Small;
		Widgets.EndScrollView();
		if (interacted != null)
		{
			interacted.ProcessInput(ev);
			Event.current.Use();
		}
		else if (floatMenu != null)
		{
			var opts = floatMenu.RightClickFloatMenuOptions.ToList();
			Event.current.Use();
			if (opts.Any()) Find.WindowStack.Add(new FloatMenu(opts));
		}
	}

	private static DesignationCategoryDef CatOf(Designator d) => Catalog.catOf.TryGetValue(d, out var c) ? c : null;

	// 0.4.0: what is missing to build it once (null = there is enough)
	private static string Missing(Designator d)
	{
		if (d is Designator_Dropdown dd) d = dd.Elements.FirstOrDefault();
		if (!(d is Designator_Build b) || Find.CurrentMap == null || b.PlacingDef == null) return null;
		try
		{
			var cost = b.PlacingDef.CostListAdjusted(b.StuffDef);
			var miss = new List<string>();
			foreach (ThingDefCountClass tc in cost)
			{
				int have = Find.CurrentMap.resourceCounter.GetCount(tc.thingDef);
				if (have < tc.count) miss.Add(tc.thingDef.LabelCap + " " + have + "/" + tc.count);
			}
			return miss.Count == 0 ? null : string.Join(", ", miss);
		}
		catch { return null; }
	}

	// 0.4.0: how many are built already (top left), a red mark when material is missing (top right under the star)
	private static void Badges(Rect cell, Designator d)
	{
		Designator one = d is Designator_Dropdown dd ? dd.Elements.FirstOrDefault() : d;
		if (!(one is Designator_Build b) || Find.CurrentMap == null) return;
		Text.Font = GameFont.Tiny;
		if (b.PlacingDef is ThingDef td)
		{
			int n = 0;
			try { n = Find.CurrentMap.listerBuildings.AllBuildingsColonistOfDef(td).Count(); } catch { }
			if (n > 0)
			{
				string t = "×" + n;
				Rect br = new Rect(cell.x + 2f, cell.y + 2f, Text.CalcSize(t).x + 6f, 15f);
				MUI.Fill(br, new Color(0f, 0f, 0f, 0.55f), 3f);
				GUI.color = MUI.Text1;
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(br, t);
				Text.Anchor = TextAnchor.UpperLeft;
				GUI.color = Color.white;
				TooltipHandler.TipRegion(br, "MA_BuiltTip".Translate(n));
			}
		}
		string miss = Missing(b);
		if (miss != null)
		{
			Rect mr = new Rect(cell.xMax - 14f, cell.y + 20f, 11f, 11f);
			MUI.Fill(mr, new Color32(214, 84, 84, 255), 5f);
			if (Mouse.IsOver(cell)) TooltipHandler.TipRegion(cell, "MA_MissingTip".Translate(miss));
		}
	}

	private void LockedTile(Rect cell, Designator d)
	{
		if (d is Designator_Dropdown dd) d = dd.Elements.FirstOrDefault();
		if (!(d is Designator_Build b)) return;
		MUI.Fill(cell, MUI.CardBg);
		MUI.Frame(cell, MUI.Border);
		GUI.color = new Color(1f, 1f, 1f, 0.3f);
		try { Widgets.DefIcon(cell.ContractedBy(12f), b.PlacingDef, b.PlacingDef.MadeFromStuff ? GenStuff.DefaultStuffFor(b.PlacingDef) : null); } catch { }
		GUI.color = MUI.Warn;
		Text.Font = GameFont.Tiny;
		Text.Anchor = TextAnchor.LowerCenter;
		Widgets.Label(new Rect(cell.x, cell.y, cell.width, cell.height - 2f), "MA_Locked".Translate());
		Text.Anchor = TextAnchor.UpperLeft;
		GUI.color = Color.white;
		if (Mouse.IsOver(cell))
		{
			hover = d;
			var res = (b.PlacingDef.researchPrerequisites ?? new List<ResearchProjectDef>()).Where(p => !p.IsFinished).Select(p => p.LabelCap.ToString());
			TooltipHandler.TipRegion(cell, b.PlacingDef.LabelCap + "\n" + "MA_LockedTip".Translate(string.Join(", ", res)));
		}
		if (Widgets.ButtonInvisible(cell))
			Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Research);
	}

	// ---------------------------------------------------------------- what it is

	private void Info(Rect r)
	{
		MUI.Fill(r, MUI.CardBg2);
		Designator d = hover ?? Find.DesignatorManager.SelectedDesignator;
		if (d is Designator_Dropdown dd && dd.Elements.Count > 0)
			d = dd.Elements.Contains(Find.DesignatorManager.SelectedDesignator) ? Find.DesignatorManager.SelectedDesignator : dd.Elements[0];
		Rect inner = r.ContractedBy(10f);
		if (d == null)
		{
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			Widgets.Label(inner, "MA_InfoHint".Translate());
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			return;
		}
		Widgets.BeginGroup(inner);
		float y = 0f;
		Text.Font = GameFont.Small;
		GUI.color = MUI.Text1;
		float th = Text.CalcHeight(d.LabelCap, inner.width);
		Widgets.Label(new Rect(0f, y, inner.width, th), d.LabelCap);
		y += th;
		string mod = Catalog.ModOf(d);
		Text.Font = GameFont.Tiny;
		if (!mod.NullOrEmpty())
		{
			GUI.color = MUI.Accent;
			Widgets.Label(new Rect(0f, y, inner.width, 18f), "MA_FromMod".Translate(mod).Truncate(inner.width));
			y += 18f;
		}
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		y += 4f;
		try { d.DrawPanelReadout(ref y, inner.width); }
		catch { }
		Rect descR = new Rect(0f, y + 4f, inner.width, inner.height - y - 4f);
		if (descR.height > 20f)
		{
			Text.Font = GameFont.Tiny;
			GUI.color = MUI.Muted;
			string desc = d.Desc ?? "";
			Widgets.Label(descR, desc.TruncateHeight(descR.width, descR.height));
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
		}
		Widgets.EndGroup();
	}
}
