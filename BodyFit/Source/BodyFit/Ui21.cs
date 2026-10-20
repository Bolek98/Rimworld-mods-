using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.1: settings tabs "Clothes" (your own setting per piece of clothing) and
// "Preview" (a colonist before and after fitting, from all four sides).
public partial class BodyFitMod
{
	private string clothSearch = "";
	private bool clothWornOnly = true;
	private List<ThingDef> clothList;
	private int clothListFrame = -999;

	private List<ThingDef> ClothList()
	{
		// rebuilt about twice a second – the list of worn clothes changes slowly
		if (clothList != null && Time.frameCount - clothListFrame < 30)
			return clothList;
		clothListFrame = Time.frameCount;
		HashSet<ThingDef> worn = null;
		if (clothWornOnly && Current.Game != null && Find.Maps != null)
		{
			worn = new HashSet<ThingDef>();
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
					if (p.RaceProps.Humanlike && p.apparel != null)
						foreach (Apparel a in p.apparel.WornApparel)
							worn.Add(a.def);
		}
		string q = clothSearch.NullOrEmpty() ? null : clothSearch.ToLowerInvariant();
		clothList = DefDatabase<ThingDef>.AllDefsListForReading
			.Where(d => d.IsApparel && (worn == null || worn.Contains(d) || Settings.items.ContainsKey(d.defName)))
			.Where(d => q == null || (d.label ?? "").ToLowerInvariant().Contains(q) || d.defName.ToLowerInvariant().Contains(q))
			.OrderBy(d => Settings.items.ContainsKey(d.defName) ? 0 : 1).ThenBy(d => d.label ?? d.defName)
			.ToList();
		return clothList;
	}

	private void DrawClothes(Rect r)
	{
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		string head = "BF_ClothesHead".Translate();
		float hh = Text.CalcHeight(head, r.width);
		Widgets.Label(new Rect(r.x, r.y, r.width, hh), head);
		GUI.color = Color.white;
		float y = r.y + hh + 4f;
		string ns = Widgets.TextField(new Rect(r.x, y, 260f, 26f), clothSearch);
		if (ns != clothSearch)
		{
			clothSearch = ns;
			clothList = null;
		}
		bool wo = clothWornOnly;
		Widgets.CheckboxLabeled(new Rect(r.x + 280f, y, 300f, 26f), "BF_ClothesWornOnly".Translate(), ref wo);
		if (wo != clothWornOnly)
		{
			clothWornOnly = wo;
			clothList = null;
		}
		y += 32f;
		List<ThingDef> list = ClothList();
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		float rowH = 60f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, Mathf.Max(1, list.Count) * rowH);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		if (list.Count == 0)
			Widgets.Label(new Rect(0f, 0f, view.width, 30f), "BF_ClothesNone".Translate());
		for (int i = 0; i < list.Count; i++)
		{
			ThingDef d = list[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (row.yMax < scroll.y || row.y > scroll.y + outRect.height)
				continue;   // only rows on screen are drawn
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 4f, 48f, 48f), d);
			Widgets.Label(new Rect(row.x + 56f, row.y + 2f, 300f, 24f), ((string)d.LabelCap).Truncate(300f));
			GUI.color = new Color(0.7f, 0.7f, 0.7f);
			Widgets.Label(new Rect(row.x + 56f, row.y + 26f, 300f, 24f), (d.modContentPack?.Name ?? "?").Truncate(300f));
			GUI.color = Color.white;
			Settings.items.TryGetValue(d.defName, out ItemTweak t);
			ItemTweak w = t ?? new ItemTweak();
			bool hide = w.hide, refit = !w.noRefit;
			float x = row.x + 360f;
			Widgets.CheckboxLabeled(new Rect(x, row.y + 2f, 150f, 24f), "BF_ItemHide".Translate(), ref hide);
			Widgets.CheckboxLabeled(new Rect(x, row.y + 28f, 150f, 24f), "BF_ItemRefit".Translate(), ref refit);
			TooltipHandler.TipRegion(new Rect(x, row.y + 2f, 150f, 24f), "BF_ItemHide_Desc".Translate());
			TooltipHandler.TipRegion(new Rect(x, row.y + 28f, 150f, 24f), "BF_ItemRefit_Desc".Translate());
			x += 166f;
			Vector2 off = w.offset, sc = w.scale;
			off.x = Stepper(new Rect(x, row.y + 2f, 200f, 24f), "BF_ItemSide".Translate(), off.x);
			off.y = Stepper(new Rect(x, row.y + 28f, 200f, 24f), "BF_ItemUp".Translate(), off.y);
			x += 210f;
			sc.x = Mathf.Clamp(Stepper(new Rect(x, row.y + 2f, 200f, 24f), "BF_ClothesW".Translate(), sc.x), 0.3f, 2f);
			sc.y = Mathf.Clamp(Stepper(new Rect(x, row.y + 28f, 200f, 24f), "BF_ClothesH".Translate(), sc.y), 0.3f, 2f);
			bool changed = hide != w.hide || refit == w.noRefit || off != w.offset || sc != w.scale;
			if (changed)
			{
				w.hide = hide;
				w.noRefit = !refit;
				w.offset = off;
				w.scale = sc;
				if (w.IsDefault)
					Settings.items.Remove(d.defName);
				else
					Settings.items[d.defName] = w;
				Changed();   // the game keeps drawing data per pawn – redraw
			}
			if (t != null && Widgets.ButtonText(new Rect(row.xMax - 80f, row.y + 14f, 76f, 26f), "BF_Reset".Translate()))
			{
				Settings.items.Remove(d.defName);
				Changed();
			}
		}
		Widgets.EndScrollView();
	}

	// ---- preview ----------------------------------------------------------
	private Pawn previewPawn;
	private readonly RenderTexture[] before = new RenderTexture[4];
	private readonly RenderTexture[] after = new RenderTexture[4];
	private bool previewMade;

	private void DrawPreview(Rect r)
	{
		if (Current.Game == null || Find.CurrentMap == null)
		{
			Widgets.Label(r, "BF_PreviewNoGame".Translate());
			return;
		}
		List<Pawn> pawns = Find.CurrentMap.mapPawns.AllPawnsSpawned.Where(p => p.RaceProps.Humanlike && p.story != null).OrderBy(p => p.Faction == Faction.OfPlayer ? 0 : 1).ThenBy(p => p.LabelShort).ToList();
		if (previewPawn == null || !pawns.Contains(previewPawn))
		{
			previewPawn = pawns.FirstOrDefault();
			previewMade = false;
		}
		if (previewPawn == null)
		{
			Widgets.Label(r, "BF_PreviewNoGame".Translate());
			return;
		}
		Rect pick = new Rect(r.x, r.y, 280f, 28f);
		if (Widgets.ButtonText(pick, previewPawn.LabelShortCap + " (" + previewPawn.def.label + ")"))
		{
			var menu = new List<FloatMenuOption>();
			foreach (Pawn p in pawns)
			{
				Pawn lp = p;
				menu.Add(new FloatMenuOption(p.LabelShortCap + " (" + p.def.label + ", " + (p.story.bodyType?.defName ?? "-") + ")", () => { previewPawn = lp; previewMade = false; }));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		if (Widgets.ButtonText(new Rect(r.x + 290f, r.y, 160f, 28f), "BF_PreviewRefresh".Translate()))
			previewMade = false;
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		Widgets.Label(new Rect(r.x + 460f, r.y + 4f, r.width - 460f, 24f), ("BF_PreviewInfo".Translate(previewPawn.story.bodyType?.defName ?? "-", Refit.made)).ToString().Truncate(r.width - 460f));
		GUI.color = Color.white;
		if (!previewMade && Event.current.type == EventType.Repaint)
		{
			MakePreview(previewPawn);
			previewMade = true;
		}
		float size = Mathf.Min((r.width - 30f) / 4f, (r.height - 100f) / 2f);
		string[] names = { "BF_North", "BF_East", "BF_South", "BF_West" };
		int[] order = { 2, 1, 0, 3 };   // front, side, back, other side
		for (int k = 0; k < 4; k++)
		{
			int i = order[k];
			float x = r.x + k * (size + 10f);
			Widgets.Label(new Rect(x, r.y + 36f, size, 22f), names[i].Translate() + " – " + "BF_Before".Translate());
			Rect b = new Rect(x, r.y + 58f, size, size);
			Widgets.DrawMenuSection(b);
			if (before[i] != null) GUI.DrawTexture(b, before[i]);
			Widgets.Label(new Rect(x, b.yMax + 4f, size, 22f), names[i].Translate() + " – " + "BF_After".Translate());
			Rect a = new Rect(x, b.yMax + 26f, size, size);
			Widgets.DrawMenuSection(a);
			if (after[i] != null) GUI.DrawTexture(a, after[i]);
		}
	}

	private void MakePreview(Pawn p)
	{
		try
		{
			for (int i = 0; i < 4; i++)
			{
				if (before[i] == null) before[i] = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
				if (after[i] == null) after[i] = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
			}
			Refit.Suspended = true;
			try
			{
				Render(p, before);
			}
			finally
			{
				Refit.Suspended = false;
			}
			Render(p, after);
		}
		catch (Exception ex)
		{
			Refit.Suspended = false;
			MLog.WarnOnce("[Body Fit] preview: " + ex.Message, 0x0b0d2f03);
		}
		finally
		{
			// the pawn on the map is drawn with the normal pictures again
			p.Drawer?.renderer?.renderTree?.SetDirty();
			PortraitsCache.SetDirty(p);
		}
	}

	private static void Render(Pawn p, RenderTexture[] into)
	{
		p.Drawer.renderer.renderTree.SetDirty();
		for (int i = 0; i < 4; i++)
		{
			RenderTexture prev = RenderTexture.active;
			RenderTexture.active = into[i];
			GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
			RenderTexture.active = prev;
			Find.PawnCacheRenderer.RenderPawn(p, into[i], new Vector3(0f, 0f, 0.1f), 1.05f, 0f, new Rot4(i), true, true, true, false);
		}
	}
}
