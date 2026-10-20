using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.4: settings tab "Wardrobe" – a mannequin copied from any pawn (any race, body, head, hair) tries
// on any piece of clothing, armour or headgear in the game, drawn from all four sides exactly as in
// play (with Body Fit). Whatever does not sit right is fixed right there: hide, move, resize or
// no refitting – the same per-item settings as the Clothes tab, for every pawn in every save.
public partial class BodyFitMod
{
	private Pawn wModel;          // the pawn copied
	private Pawn wMannequin;      // the copy that tries things on (never spawned)
	private ThingDef wSelected;
	private string wSearch = "";
	private List<ThingDef> wList;
	private Vector2 wScroll;
	private readonly RenderTexture[] wTex = new RenderTexture[4];
	private int wRenderedFrame = -999;
	private bool wDirty = true;

	private List<ThingDef> WardrobeList()
	{
		if (wList != null)
			return wList;
		string q = wSearch.NullOrEmpty() ? null : wSearch.ToLowerInvariant();
		wList = DefDatabase<ThingDef>.AllDefsListForReading
			.Where(d => d.IsApparel && d.apparel != null && !d.apparel.wornGraphicPath.NullOrEmpty())
			.Where(d => q == null || (d.label ?? "").ToLowerInvariant().Contains(q) || d.defName.ToLowerInvariant().Contains(q) || (d.modContentPack?.Name ?? "").ToLowerInvariant().Contains(q))
			.OrderBy(d => d.apparel.LastLayer?.drawOrder ?? 0).ThenBy(d => d.label ?? d.defName)
			.ToList();
		return wList;
	}

	private void WardrobeClose()
	{
		try
		{
			if (wMannequin != null && !wMannequin.Discarded)
			{
				wMannequin.apparel?.DestroyAll();
				wMannequin.equipment?.DestroyAllEquipment();
				wMannequin.Discard(true);
			}
		}
		catch
		{
		}
		wMannequin = null;
		wModel = null;
	}

	private Pawn MakeMannequin(Pawn model)
	{
		var req = new PawnGenerationRequest(model.kindDef, null, PawnGenerationContext.NonPlayer, forceGenerateNewPawn: true,
			canGeneratePawnRelations: false, mustBeCapableOfViolence: false, allowFood: false, allowAddictions: false,
			fixedGender: model.gender, fixedBiologicalAge: model.ageTracker.AgeBiologicalYearsFloat,
			fixedChronologicalAge: model.ageTracker.AgeChronologicalYearsFloat, forbidAnyTitle: true);
		Pawn m = PawnGenerator.GeneratePawn(req);
		m.apparel?.DestroyAll();
		m.equipment?.DestroyAllEquipment();
		m.inventory?.DestroyAll();
		if (m.story != null && model.story != null)
		{
			m.story.bodyType = model.story.bodyType;
			m.story.headType = model.story.headType;
			m.story.hairDef = model.story.hairDef;
			m.story.HairColor = model.story.HairColor;
			m.story.skinColorOverride = model.story.SkinColor;
			if (m.style != null && model.style != null)
			{
				m.style.beardDef = model.style.beardDef;
				m.style.FaceTattoo = model.style.FaceTattoo;
				m.style.BodyTattoo = model.style.BodyTattoo;
			}
		}
		// the model's own clothes to start with
		if (model.apparel != null)
			foreach (Apparel a in model.apparel.WornApparel)
				TryOn(m, a.def, a.Stuff);
		return m;
	}

	private static void TryOn(Pawn m, ThingDef def, ThingDef stuff = null)
	{
		if (m?.apparel == null || def == null)
			return;
		try
		{
			if (def.MadeFromStuff && stuff == null)
				stuff = GenStuff.DefaultStuffFor(def);
			var ap = (Apparel)ThingMaker.MakeThing(def, def.MadeFromStuff ? stuff : null);
			// what it would replace goes away (the mannequin keeps nothing in storage)
			foreach (Apparel w in m.apparel.WornApparel.ToList())
				if (!ApparelUtility.CanWearTogether(def, w.def, m.RaceProps.body))
				{
					m.apparel.Remove(w);
					w.Destroy();
				}
			m.apparel.Wear(ap, false, true);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] wardrobe: " + def.defName + ": " + ex.Message, def.shortHash ^ 0x5a7d);
		}
	}

	private static bool Wears(Pawn m, ThingDef d) => m?.apparel != null && m.apparel.WornApparel.Any(a => a.def == d);

	private static void TakeOff(Pawn m, ThingDef d)
	{
		foreach (Apparel a in m.apparel.WornApparel.Where(a => a.def == d).ToList())
		{
			m.apparel.Remove(a);
			a.Destroy();
		}
	}

	private void DrawWardrobe(Rect r)
	{
		if (Current.Game == null || Find.CurrentMap == null)
		{
			Widgets.Label(r, "BF_PreviewNoGame".Translate());
			return;
		}
		List<Pawn> pawns = Find.Maps.SelectMany(mp => mp.mapPawns.AllPawnsSpawned).Where(p => p.RaceProps.Humanlike && p.story != null && p.kindDef != null)
			.OrderBy(p => p.Faction == Faction.OfPlayer ? 0 : 1).ThenBy(p => p.def.label).ThenBy(p => p.LabelShort).ToList();
		if (wModel == null || wMannequin == null || wMannequin.Discarded)
		{
			Pawn first = pawns.FirstOrDefault();
			if (first == null)
			{
				Widgets.Label(r, "BF_PreviewNoGame".Translate());
				return;
			}
			SetModel(first);
		}
		// ---- top bar: model --------------------------------------------------
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "BF_WardrobeHead".Translate());
		GUI.color = Color.white;
		float y = r.y + 26f;
		if (Widgets.ButtonText(new Rect(r.x, y, 300f, 28f), "BF_WardrobeModel".Translate(wModel.LabelShortCap, wModel.def.label, wModel.story.bodyType?.defName ?? "-")))
		{
			var menu = new List<FloatMenuOption>();
			foreach (Pawn p in pawns)
			{
				Pawn lp = p;
				menu.Add(new FloatMenuOption(p.LabelShortCap + " (" + p.def.label + ", " + (p.story.bodyType?.defName ?? "-") + ")", () => SetModel(lp)));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		if (Widgets.ButtonText(new Rect(r.x + 310f, y, 150f, 28f), "BF_WardrobeNaked".Translate()))
		{
			wMannequin.apparel?.DestroyAll();
			wDirty = true;
		}
		if (Widgets.ButtonText(new Rect(r.x + 470f, y, 150f, 28f), "BF_WardrobeReset".Translate()))
			SetModel(wModel);
		y += 34f;
		// ---- left: every piece of clothing -----------------------------------
		Rect left = new Rect(r.x, y, 330f, r.yMax - y);
		string ns = Widgets.TextField(new Rect(left.x, left.y, left.width, 26f), wSearch);
		if (ns != wSearch)
		{
			wSearch = ns;
			wList = null;
		}
		List<ThingDef> list = WardrobeList();
		Rect outRect = new Rect(left.x, left.y + 30f, left.width, left.height - 30f);
		const float rowH = 34f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, Mathf.Max(1, list.Count) * rowH);
		Widgets.BeginScrollView(outRect, ref wScroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < wScroll.y || row.y > wScroll.y + outRect.height)
				continue;
			ThingDef d = list[i];
			bool on = Wears(wMannequin, d);
			if (d == wSelected)
				Widgets.DrawHighlightSelected(row);
			else if (on)
				Widgets.DrawLightHighlight(row);
			Widgets.DrawHighlightIfMouseover(row);
			Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 1f, 30f, 30f), d);
			string label = ((string)d.LabelCap).Truncate(row.width - 70f);
			if (Settings.items.ContainsKey(d.defName))
				label = "* " + label;
			Widgets.Label(new Rect(row.x + 36f, row.y + 6f, row.width - 66f, 24f), label);
			TooltipHandler.TipRegion(row, d.LabelCap + "\n" + (d.modContentPack?.Name ?? "?") + "\n" + string.Join(", ", d.apparel.layers.Select(l => l.label)));
			Widgets.Checkbox(new Vector2(row.xMax - 26f, row.y + 4f), ref on, 24f);
			if (on != Wears(wMannequin, d))
			{
				if (on) TryOn(wMannequin, d);
				else TakeOff(wMannequin, d);
				wSelected = d;
				wDirty = true;
			}
			else if (Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 30f, row.height)))
			{
				wSelected = d;
				if (!Wears(wMannequin, d))
				{
					TryOn(wMannequin, d);
					wDirty = true;
				}
			}
		}
		Widgets.EndScrollView();
		// ---- right: the mannequin from all sides ------------------------------
		Rect right = new Rect(left.xMax + 12f, y, r.xMax - left.xMax - 12f, r.yMax - y);
		float tweakH = wSelected != null ? 84f : 0f;
		float size = Mathf.Min((right.width - 30f) / 4f, right.height - tweakH - 30f);
		if (Event.current.type == EventType.Repaint && (wDirty || Time.frameCount - wRenderedFrame > 45))
		{
			// pictures fitted in the background show up on the next redraw
			WardrobeRender();
			wDirty = false;
			wRenderedFrame = Time.frameCount;
		}
		string[] names = { "BF_North", "BF_East", "BF_South", "BF_West" };
		int[] order = { 2, 1, 0, 3 };
		for (int k = 0; k < 4; k++)
		{
			int i = order[k];
			float x = right.x + k * (size + 10f);
			Widgets.Label(new Rect(x, right.y, size, 22f), names[i].Translate());
			Rect b = new Rect(x, right.y + 22f, size, size);
			Widgets.DrawMenuSection(b);
			if (wTex[i] != null)
				GUI.DrawTexture(b, wTex[i]);
		}
		if (wSelected != null)
			DrawItemTweak(new Rect(right.x, right.y + 30f + size, right.width, tweakH), wSelected);
	}

	private void SetModel(Pawn p)
	{
		WardrobeClose();
		wModel = p;
		try
		{
			wMannequin = MakeMannequin(p);
		}
		catch (Exception ex)
		{
			MLog.Warn("[Body Fit] wardrobe: could not copy " + p.LabelShort + ": " + ex.Message);
			wMannequin = null;
		}
		wDirty = true;
	}

	private void WardrobeRender()
	{
		if (wMannequin == null)
			return;
		try
		{
			for (int i = 0; i < 4; i++)
				if (wTex[i] == null)
					wTex[i] = new RenderTexture(256, 256, 24, RenderTextureFormat.ARGB32);
			wMannequin.Drawer.renderer.renderTree.SetDirty();
			for (int i = 0; i < 4; i++)
			{
				RenderTexture prev = RenderTexture.active;
				RenderTexture.active = wTex[i];
				GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
				RenderTexture.active = prev;
				Find.PawnCacheRenderer.RenderPawn(wMannequin, wTex[i], new Vector3(0f, 0f, 0.1f), 1.05f, 0f, new Rot4(i), true, true, true, false);
			}
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] wardrobe picture: " + ex.Message, 0x0b0d2f06);
		}
	}

	// the per-item settings (same as the Clothes tab) for one piece
	private void DrawItemTweak(Rect r, ThingDef d)
	{
		Widgets.DrawMenuSection(r);
		Rect row = r.ContractedBy(6f);
		Widgets.ThingIcon(new Rect(row.x, row.y, 40f, 40f), d);
		Widgets.Label(new Rect(row.x + 46f, row.y, 260f, 24f), ((string)d.LabelCap).Truncate(260f));
		GUI.color = new Color(0.7f, 0.7f, 0.7f);
		Widgets.Label(new Rect(row.x + 46f, row.y + 24f, 260f, 24f), (d.modContentPack?.Name ?? "?").Truncate(260f));
		GUI.color = Color.white;
		Settings.items.TryGetValue(d.defName, out ItemTweak t);
		ItemTweak w = t ?? new ItemTweak();
		bool hide = w.hide, refit = !w.noRefit;
		float x = row.x + 312f;
		Widgets.CheckboxLabeled(new Rect(x, row.y, 150f, 24f), "BF_ItemHide".Translate(), ref hide);
		Widgets.CheckboxLabeled(new Rect(x, row.y + 28f, 150f, 24f), "BF_ItemRefit".Translate(), ref refit);
		x += 166f;
		Vector2 off = w.offset, sc = w.scale;
		off.x = Stepper(new Rect(x, row.y, 200f, 24f), "BF_ItemSide".Translate(), off.x);
		off.y = Stepper(new Rect(x, row.y + 28f, 200f, 24f), "BF_ItemUp".Translate(), off.y);
		x += 210f;
		sc.x = Mathf.Clamp(Stepper(new Rect(x, row.y, 200f, 24f), "BF_ClothesW".Translate(), sc.x), 0.3f, 2f);
		sc.y = Mathf.Clamp(Stepper(new Rect(x, row.y + 28f, 200f, 24f), "BF_ClothesH".Translate(), sc.y), 0.3f, 2f);
		if (hide != w.hide || refit == w.noRefit || off != w.offset || sc != w.scale)
		{
			w.hide = hide;
			w.noRefit = !refit;
			w.offset = off;
			w.scale = sc;
			if (w.IsDefault)
				Settings.items.Remove(d.defName);
			else
				Settings.items[d.defName] = w;
			Changed();
			wMannequin?.Drawer?.renderer?.SetAllGraphicsDirty();
			wDirty = true;
		}
		if (t != null && Widgets.ButtonText(new Rect(row.xMax - 80f, row.y + 10f, 76f, 26f), "BF_Reset".Translate()))
		{
			Settings.items.Remove(d.defName);
			Changed();
			wMannequin?.Drawer?.renderer?.SetAllGraphicsDirty();
			wDirty = true;
		}
	}

	public override void WriteSettings()
	{
		base.WriteSettings();
		WardrobeClose();   // the settings window was closed
	}
}
