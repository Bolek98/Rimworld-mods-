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
	private const float SideW = 178f;
	private const float InfoW = 250f;
	private const float Cell = 75f;
	private const float Gap = 6f;

	private readonly QuickSearchWidget search = new QuickSearchWidget();
	private string cat;
	private Vector2 scrollGrid, scrollSide, scrollInfo;
	private Designator hover;
	private bool focusDone;

	private static MASettings S => MisakArchitectMod.S;

	public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(UI.screenWidth - 10f, 1240f), Mathf.Clamp(S?.height ?? 380f, 240f, UI.screenHeight - 120f));

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
		cat ??= S.lastCategory;
		if (cat == null || (cat != Fav && cat != Recent && !Catalog.cats.Any(c => c.defName == cat)))
			cat = Catalog.cats.FirstOrDefault(c => c.Visible)?.defName;
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
		if (c != null && c.showPowerGrid && !search.filter.Active)
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

		Rect body = new Rect(inRect.x, head.yMax, inRect.width, inRect.height - Head);
		float infoW = S.showInfo ? InfoW : 0f;
		Sidebar(new Rect(body.x, body.y, SideW, body.height));
		Rect gridR = new Rect(body.x + SideW, body.y, body.width - SideW - infoW, body.height).ContractedBy(6f);
		Grid(gridR, items, emptyText);
		if (S.showInfo)
			Info(new Rect(body.xMax - infoW, body.y, infoW, body.height));
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
		DesignationCategoryDef c = Catalog.cats.FirstOrDefault(x => x.defName == cat);
		emptyText = "NothingAvailableInCategory".Translate();
		return c != null && Catalog.grid.TryGetValue(c, out var l) ? l : new List<Designator>();
	}

	private void Sidebar(Rect r)
	{
		MUI.Fill(r, MUI.CardBg2);
		var rows = new List<(string id, string label, int count, bool on)>
		{
			(Fav, "MA_Favorites".Translate(), S.favorites.Count(k => Catalog.byKey.ContainsKey(k)), true),
			(Recent, "MA_Recent".Translate(), S.recent.Count(k => Catalog.byKey.ContainsKey(k)), true),
		};
		foreach (DesignationCategoryDef c in Catalog.cats)
		{
			int n = Catalog.grid.TryGetValue(c, out var l) ? l.Count(Catalog.Shown) : 0;
			rows.Add((c.defName, c.LabelCap, n, c.Visible && n > 0));
		}
		const float h = 26f;
		Rect view = new Rect(0f, 0f, r.width - 16f, rows.Count * h + 10f);
		Widgets.BeginScrollView(r, ref scrollSide, view);
		float y = 4f;
		for (int i = 0; i < rows.Count; i++)
		{
			var row = rows[i];
			Rect rr = new Rect(0f, y, view.width, h);
			bool sel = !search.filter.Active && row.id == cat;
			if (i == 2)
			{
				MUI.Fill(new Rect(8f, y - 2f, view.width - 16f, 1f), MUI.Border);
				y += 4f;
				rr.y = y;
			}
			if (sel) MUI.Fill(rr, MUI.OnBg);
			else if (Mouse.IsOver(rr)) MUI.Fill(rr, MUI.CardBg);
			if (sel) MUI.Fill(new Rect(rr.x, rr.y, 3f, rr.height), MUI.Accent);
			GUI.color = sel ? MUI.Accent : row.on ? MUI.Text1 : MUI.Muted;
			Text.Anchor = TextAnchor.MiddleLeft;
			string icon = row.id == Fav ? "★ " : row.id == Recent ? "↺ " : "";
			Widgets.Label(new Rect(rr.x + 10f, rr.y, rr.width - 46f, rr.height), (icon + row.label).Truncate(rr.width - 46f));
			Text.Anchor = TextAnchor.MiddleRight;
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			if (row.count > 0) Widgets.Label(new Rect(rr.xMax - 40f, rr.y, 34f, rr.height), row.count.ToString());
			Text.Font = GameFont.Small;
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			if (Widgets.ButtonInvisible(rr))
			{
				if (!row.on && row.id != Fav && row.id != Recent)
					Messages.Message("NothingAvailableInCategory".Translate() + ": " + row.label, MessageTypeDefOf.RejectInput, false);
				else
				{
					cat = row.id;
					scrollGrid = Vector2.zero;
					if (search.filter.Active) search.Reset();
					SoundDefOf.ArchitectCategorySelect.PlayOneShotOnCamera();
				}
			}
			y += h;
		}
		Widgets.EndScrollView();
	}

	// ---------------------------------------------------------------- the buildings

	private void Grid(Rect r, List<Designator> items, string emptyText)
	{
		List<Designator> shown = items.Where(Catalog.Shown).ToList();
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
		int rowsN = Mathf.CeilToInt(shown.Count / (float)cols);
		Rect view = new Rect(0f, 0f, r.width - 16f, rowsN * (Cell + Gap + 2f));
		Widgets.BeginScrollView(r, ref scrollGrid, view);
		Text.Font = GameFont.Tiny;
		Gizmo interacted = null, floatMenu = null;
		Event ev = null;
		Designator selected = Find.DesignatorManager.SelectedDesignator;
		for (int i = 0; i < shown.Count; i++)
		{
			Designator d = shown[i];
			float x = (i % cols) * (Cell + Gap);
			float y = (i / cols) * (Cell + Gap + 2f);
			Rect cell = new Rect(x, y, Cell, Cell);
			// skip what is scrolled out of sight
			if (y + Cell < scrollGrid.y - 4f || y > scrollGrid.y + r.height + 4f)
				continue;
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
