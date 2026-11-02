using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ===========================================================================
// 2.6.0 – six additions
//  1. ChildBody     – a race without a child body picture gets one made at start from its adult body
//  2. Dialog_ClothingDoctor – every colonist's missing / borrowed pictures with one-click fixes
//  3. Patch_WeaponScale – weapons sized and held by the race's body size
//  4. Patch_HeadgearScale – headgear follows the size of the head it sits on (experimental, off)
//  5. Patch_HairUnderHelmet – hair hidden under full helmets (or all headgear)
//  6. AuthorReport  – missing pictures grouped by mod, for mod authors
// ===========================================================================

// ---------------------------------------------------------------------------
// 1. A child body for races that have none. Made once at game start (main thread): the race's adult
//    body picture is shrunk the way the game's own child body differs from its adult body (measured
//    on the game's pictures), and registered under the very path the race would use for a child –
//    so the race mod, the game and Body Fit all simply find it. Children keep the Child body type
//    and children's clothes fit as painted.
// ---------------------------------------------------------------------------
[StaticConstructorOnStartup]
public static class ChildBody
{
	public static readonly HashSet<ThingDef> Made = new HashSet<ThingDef>();
	public static readonly List<string> Notes = new List<string>();
	private static readonly string[] Dirs = { "north", "east", "south", "west" };

	static ChildBody()
	{
		LongEventHandler.ExecuteWhenFinished(() =>
		{
			try { MakeAll(); }
			catch (Exception ex) { MLog.Warn("[Body Fit] child bodies: " + ex.Message); }
		});
	}

	public static bool CanUse(ThingDef race) => BodyFitMod.Settings != null && BodyFitMod.Settings.childBody && race != null && Made.Contains(race);

	private static void MakeAll()
	{
		if (BodyFitMod.Settings == null || !BodyFitMod.Settings.childBody || !ModsConfig.BiotechActive)
			return;
		var holder = BodyFitMod.Content?.GetContentHolder<Texture2D>();
		if (holder == null)
			return;
		BodyTypeDef child = BodyTypeDefOf.Child;
		if (child == null || child.bodyNakedGraphicPath.NullOrEmpty())
			return;
		int n = 0;
		foreach (ThingDef race in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (race.race == null || !race.race.Humanlike || !Bodies.IsHar(race))
				continue;
			try
			{
				string basePath = RaceBase(race);
				if (basePath.NullOrEmpty())
					continue;
				foreach (string pre in new[] { "", "Female_", "Male_" })
				{
					string target = basePath + pre + "Naked_Child";
					if (Exists(target))
					{
						if (pre == "") break;   // the race has its own child body
						continue;
					}
					BodyTypeDef adultType = null;
					string adult = null;
					foreach (BodyTypeDef b in new[] { BodyTypeDefOf.Thin, BodyTypeDefOf.Female, BodyTypeDefOf.Male })
					{
						if (b != null && Exists(basePath + pre + "Naked_" + b.defName))
						{
							adultType = b;
							adult = basePath + pre + "Naked_" + b.defName;
							break;
						}
					}
					if (adult == null)
						continue;
					if (Make(holder, adult, adultType, target))
					{
						Made.Add(race);
						n++;
						Notes.Add(race.defName + ": " + target + " ← " + adult);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Body Fit] child body for " + race.defName + ": " + ex.Message);
			}
		}
		if (n > 0)
		{
			Bodies.ForgetTextures();
			MLog.Info("[Body Fit] child bodies made for races without one: " + n + " (" + string.Join(", ", Made.Select(r => r.defName)) + ")");
		}
	}

	public static string RaceBase(ThingDef race)
	{
		object alien = race.GetType().GetField("alienRace")?.GetValue(race);
		object gp = alien?.GetType().GetField("graphicPaths")?.GetValue(alien);
		object body = gp?.GetType().GetField("body")?.GetValue(gp);
		return body as string ?? body?.GetType().GetField("path")?.GetValue(body) as string;
	}

	private static bool Exists(string path) =>
		ContentFinder<Texture2D>.Get(path + "_south", false) != null || ContentFinder<Texture2D>.Get(path + "_north", false) != null || ContentFinder<Texture2D>.Get(path + "_east", false) != null;

	private static Texture2D Load(string path, bool vanilla)
	{
		Texture2D t = vanilla ? Resources.Load<Texture2D>("Textures/" + path) : null;
		return t ?? ContentFinder<Texture2D>.Get(path, false);
	}

	private static bool Make(ModContentHolder<Texture2D> holder, string adult, BodyTypeDef adultType, string target)
	{
		bool any = false;
		foreach (string dir in Dirs)
		{
			Texture2D src = Load(adult + "_" + dir, false);
			if (src == null)
				continue;   // the game mirrors east for west by itself
			Texture2D vAdult = Load(adultType.bodyNakedGraphicPath + "_" + dir, true) ?? Load(adultType.bodyNakedGraphicPath + "_" + (dir == "west" ? "east" : dir), true);
			Texture2D vChild = Load(BodyTypeDefOf.Child.bodyNakedGraphicPath + "_" + dir, true) ?? Load(BodyTypeDefOf.Child.bodyNakedGraphicPath + "_" + (dir == "west" ? "east" : dir), true);
			Texture2D made = Shrink(src, vAdult, vChild);
			if (made == null)
				continue;
			made.name = target + "_" + dir;
			holder.contentList[target + "_" + dir] = made;
			any = true;
			// the colour mask, when the adult body has one
			Texture2D mask = Load(adult + "_" + dir + "m", false);
			if (mask != null)
			{
				Texture2D mm = Shrink(mask, vAdult, vChild);
				if (mm != null)
				{
					mm.name = target + "_" + dir + "m";
					holder.contentList[target + "_" + dir + "m"] = mm;
				}
			}
		}
		return any;
	}

	private struct Box { public float x0, x1, y0, y1; public bool ok; }

	private static Box Bounds(Color32[] px, int w, int h)
	{
		var b = new Box { x0 = 1f, y0 = 1f, x1 = 0f, y1 = 0f };
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
				if (px[y * w + x].a > 40)
				{
					float fx = (float)x / w, fy = (float)y / h;
					if (fx < b.x0) b.x0 = fx;
					if (fx > b.x1) b.x1 = fx;
					if (fy < b.y0) b.y0 = fy;
					if (fy > b.y1) b.y1 = fy;
					b.ok = true;
				}
		return b;
	}

	// the race picture is scaled the way the game's child body is smaller than its adult body:
	// same feet line shift, same width and height ratio, centred where the race figure was
	private static Texture2D Shrink(Texture2D src, Texture2D vAdult, Texture2D vChild)
	{
		Color32[] sp = Refit.ReadPixels(src, out int w, out int h);
		if (sp == null)
			return null;
		float rw = 0.8f, rh = 0.78f, dy = 0f;
		if (vAdult != null && vChild != null)
		{
			Color32[] ap = Refit.ReadPixels(vAdult, out int aw, out int ah);
			Color32[] cp = Refit.ReadPixels(vChild, out int cw, out int ch);
			if (ap != null && cp != null)
			{
				Box a = Bounds(ap, aw, ah), c = Bounds(cp, cw, ch);
				if (a.ok && c.ok && a.x1 > a.x0 && a.y1 > a.y0)
				{
					rw = Mathf.Clamp((c.x1 - c.x0) / (a.x1 - a.x0), 0.5f, 1f);
					rh = Mathf.Clamp((c.y1 - c.y0) / (a.y1 - a.y0), 0.5f, 1f);
					dy = c.y0 - a.y0;
				}
			}
		}
		Box s = Bounds(sp, w, h);
		if (!s.ok)
			return null;
		float cx = (s.x0 + s.x1) / 2f;
		float bottom = s.y0 + dy;
		var outPx = new Color32[w * h];
		for (int y = 0; y < h; y++)
		{
			float fy = (float)y / h;
			float sy = s.y0 + (fy - bottom) / rh;    // back into the source
			if (sy < 0f || sy >= 1f)
				continue;
			for (int x = 0; x < w; x++)
			{
				float fx = (float)x / w;
				float sx = cx + (fx - cx) / rw;
				if (sx < 0f || sx >= 1f)
					continue;
				outPx[y * w + x] = Sample(sp, w, h, sx * w, sy * h);
			}
		}
		var t = new Texture2D(w, h, TextureFormat.RGBA32, true);
		t.SetPixels32(outPx);
		t.filterMode = FilterMode.Trilinear;
		t.wrapMode = TextureWrapMode.Clamp;
		t.Apply(true, true);
		return t;
	}

	private static Color32 Sample(Color32[] p, int w, int h, float x, float y)
	{
		int x0 = Mathf.Clamp((int)x, 0, w - 1), y0 = Mathf.Clamp((int)y, 0, h - 1);
		int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
		float tx = x - x0, ty = y - y0;
		Color a = Color.Lerp(p[y0 * w + x0], p[y0 * w + x1], tx);
		Color b = Color.Lerp(p[y1 * w + x0], p[y1 * w + x1], tx);
		return Color.Lerp(a, b, ty);
	}
}

// ---------------------------------------------------------------------------
// 3. Weapons by race size: a race drawn smaller or bigger than a human (Humanoid Alien Races
//    customDrawSize) holds smaller or bigger weapons, closer to or further from its body.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming))]
public static class Patch_WeaponScale
{
	private static readonly Dictionary<ThingDef, float> sizes = new Dictionary<ThingDef, float>();

	public static float RaceSize(ThingDef race)
	{
		if (race == null)
			return 1f;
		lock (BF.Gate)
			if (sizes.TryGetValue(race, out float s))
				return s;
		float v = 1f;
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object gs = alien?.GetType().GetField("generalSettings")?.GetValue(alien);
			object pg = gs?.GetType().GetField("alienPartGenerator")?.GetValue(gs);
			object ds = pg?.GetType().GetField("customDrawSize")?.GetValue(pg);
			if (ds is Vector2 d && d.x > 0.1f && d.y > 0.1f)
				v = Mathf.Clamp((d.x + d.y) / 2f, 0.5f, 1.6f);
		}
		catch { }
		lock (BF.Gate)
			sizes[race] = v;
		return v;
	}

	[HarmonyPriority(Priority.Low)]
	public static bool Prefix(Thing eq, Vector3 drawLoc, float aimAngle)
	{
		try
		{
			if (BodyFitMod.Settings == null || !BodyFitMod.Settings.weaponScale)
				return true;
			if (!(eq?.ParentHolder is Pawn_EquipmentTracker tr) || tr.pawn == null || !tr.pawn.RaceProps.Humanlike)
				return true;
			Pawn p = tr.pawn;
			float s = RaceSize(p.def);
			if (Mathf.Abs(s - 1f) < 0.06f)
				return true;
			// pulled toward the body by the same factor
			Vector3 c = p.DrawPos;
			Vector3 loc = drawLoc;
			loc.x = c.x + (drawLoc.x - c.x) * s;
			loc.z = c.z + (drawLoc.z - c.z) * s;
			Draw(eq, loc, aimAngle, s);
			return false;
		}
		catch
		{
			return true;
		}
	}

	// the game's own drawing (PawnRenderUtility.DrawEquipmentAiming) with a size factor
	private static void Draw(Thing eq, Vector3 drawLoc, float aimAngle, float scale)
	{
		float num = aimAngle - 90f;
		Mesh mesh;
		if (aimAngle > 20f && aimAngle < 160f) { mesh = MeshPool.plane10; num += eq.def.equippedAngleOffset; }
		else if (aimAngle > 200f && aimAngle < 340f) { mesh = MeshPool.plane10Flip; num -= 180f; num -= eq.def.equippedAngleOffset; }
		else { mesh = MeshPool.plane10; num += eq.def.equippedAngleOffset; }
		num %= 360f;
		CompEquippable ce = eq.TryGetComp<CompEquippable>();
		if (ce != null)
		{
			EquipmentUtility.Recoil(eq.def, EquipmentUtility.GetRecoilVerb(ce.AllVerbs), out Vector3 off, out float ang, aimAngle);
			drawLoc += off * scale;
			num += ang;
		}
		Material mat = eq.Graphic is Graphic_StackCount gs ? gs.SubGraphicForStackCount(1, eq.def).MatSingleFor(eq) : eq.Graphic.MatSingleFor(eq);
		Matrix4x4 m = Matrix4x4.TRS(drawLoc, Quaternion.AngleAxis(num, Vector3.up), new Vector3(eq.Graphic.drawSize.x * scale, 0f, eq.Graphic.drawSize.y * scale));
		Graphics.DrawMesh(mesh, m, mat, 0);
	}
}

// ---------------------------------------------------------------------------
// 4. (experimental, off) Headgear follows the size of the head it sits on: when a race draws its head
//    bigger or smaller than its hats, the hat gets the head's size.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.ScaleFor))]
public static class Patch_HeadgearScale
{
	[ThreadStatic] private static bool inside;

	public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Vector3 __result)
	{
		if (inside || BodyFitMod.Settings == null || !BodyFitMod.Settings.headgearAlign)
			return;
		try
		{
			if (!(node is PawnRenderNode_Apparel a) || a.apparel == null || !Bodies.IsHar(parms.pawn?.def))
				return;
			ApparelLayerDef last = a.apparel.def.apparel?.LastLayer;
			if (last != ApparelLayerDefOf.Overhead && last != ApparelLayerDefOf.EyeCover)
				return;
			PawnRenderNode head = node.parent;
			while (head != null && !(head is PawnRenderNode_Head))
				head = head.parent;
			if (head == null || head.Worker == null)
				return;
			inside = true;
			Vector3 hs = head.Worker.ScaleFor(head, parms);
			inside = false;
			// only when the hat is clearly a different size from the head
			if (Mathf.Abs(hs.x - __result.x) > 0.05f || Mathf.Abs(hs.z - __result.z) > 0.05f)
			{
				__result.x = hs.x;
				__result.z = hs.z;
			}
		}
		catch { }
		finally { inside = false; }
	}
}

// ---------------------------------------------------------------------------
// 5. Hair under helmets: hair pokes through closed helmets. Mode 1 hides it under headgear that covers
//    the full head, mode 2 under any headgear. Only while the headgear is really drawn.
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.CanDrawNow))]
public static class Patch_HairUnderHelmet
{
	private static readonly Dictionary<ThingDef, bool> full = new Dictionary<ThingDef, bool>();

	public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref bool __result)
	{
		if (!__result || !(node is PawnRenderNode_Hair))
			return;
		try
		{
			int mode = BodyFitMod.Settings?.hairUnderHelmet ?? 0;
			if (mode == 0 || parms.pawn?.apparel == null || !PawnRenderNodeWorker_Apparel_Head.HeadgearVisible(parms))
				return;
			List<Apparel> worn = parms.pawn.apparel.WornApparel;
			for (int i = 0; i < worn.Count; i++)
			{
				ThingDef d = worn[i].def;
				if (d.apparel == null || !d.apparel.layers.Contains(ApparelLayerDefOf.Overhead))
					continue;
				if (mode == 2 || FullHead(d))
				{
					__result = false;
					return;
				}
			}
		}
		catch { }
	}

	public static bool FullHead(ThingDef d)
	{
		bool f;
		lock (BF.Gate)
			if (full.TryGetValue(d, out f))
				return f;
		f = d.apparel?.bodyPartGroups != null && d.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.FullHead);
		lock (BF.Gate)
			full[d] = f;
		return f;
	}
}

// ---------------------------------------------------------------------------
// 2. The clothing doctor: every colonist on the map and each picture of him that is missing (red cross)
//    or borrowed from another body type – with one-click fixes.
// ---------------------------------------------------------------------------
public class Dialog_ClothingDoctor : Window
{
	private Vector2 scroll;
	private bool onlyProblems = true;
	private static readonly FieldInfo nodeGraphicF = AccessTools.Field(typeof(PawnRenderNode), "primaryGraphic");

	private class Row
	{
		public Pawn pawn;
		public Apparel apparel;     // null = body / head / hair
		public string what, path, note;
		public bool bad, borrowed;
	}

	public override Vector2 InitialSize => new Vector2(Mathf.Min(1000f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 40f));

	public Dialog_ClothingDoctor()
	{
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
		draggable = true;
		resizeable = true;
	}

	private List<Row> Collect()
	{
		var rows = new List<Row>();
		Map map = Find.CurrentMap;
		if (map == null)
			return rows;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.OrderBy(x => x.LabelShort))
		{
			var tree = p.Drawer?.renderer?.renderTree;
			if (tree?.rootNode == null)
				continue;
			Walk(p, tree.rootNode, rows, 0);
		}
		return rows;
	}

	private void Walk(Pawn p, PawnRenderNode n, List<Row> rows, int depth)
	{
		if (depth > 8 || n == null)
			return;
		try
		{
			Graphic g = nodeGraphicF?.GetValue(n) as Graphic;
			if (g == null) { try { g = n.GraphicFor(p); } catch { } }
			if (g != null && !g.path.NullOrEmpty())
			{
				bool bad = false;
				try { bad = g.MatSouth == BaseContent.BadMat; } catch { }
				BodyTypeDef sub = n.apparel != null ? Patch_Apparel.Substitute(n.apparel, p.story?.bodyType) : null;
				string what = n.apparel != null ? n.apparel.LabelCap : n is PawnRenderNode_Body ? "BF_DocBody".Translate().ToString()
					: n is PawnRenderNode_Head ? "BF_DocHead".Translate().ToString() : n is PawnRenderNode_Hair ? "BF_DocHair".Translate().ToString() : null;
				if (what != null && (bad || sub != null || !onlyProblems))
					rows.Add(new Row
					{
						pawn = p, apparel = n.apparel, what = what, path = g.path, bad = bad, borrowed = sub != null,
						note = bad ? "BF_DocMissing".Translate().ToString() : sub != null ? "BF_DocBorrowed".Translate(sub.defName).ToString() : "BF_DocOk".Translate().ToString()
					});
			}
		}
		catch { }
		if (n.children != null)
			foreach (PawnRenderNode c in n.children)
				Walk(p, c, rows, depth + 1);
	}

	public override void DoWindowContents(Rect r)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width - 40f, 32f), "BF_DocTitle".Translate());
		Text.Font = GameFont.Small;
		float y = r.y + 36f;
		GUI.color = new Color(0.8f, 0.8f, 0.8f);
		Widgets.Label(new Rect(r.x, y, r.width, 40f), "BF_DocIntro".Translate());
		GUI.color = Color.white;
		y += 42f;
		Widgets.CheckboxLabeled(new Rect(r.x, y, 260f, 24f), "BF_DocOnlyProblems".Translate(), ref onlyProblems);
		if (Widgets.ButtonText(new Rect(r.xMax - 220f, y - 2f, 220f, 28f), "BF_AuthorReport".Translate()))
			AuthorReport.Run();
		y += 30f;
		List<Row> rows = Collect();
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		const float rowH = 52f;
		Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows.Count * rowH, 40f));
		Widgets.BeginScrollView(outR, ref scroll, view);
		if (rows.Count == 0)
			Widgets.Label(new Rect(0f, 0f, view.width, 30f), "BF_DocNothing".Translate());
		for (int i = 0; i < rows.Count; i++)
		{
			Row row = rows[i];
			Rect rr = new Rect(0f, i * rowH, view.width, rowH - 4f);
			if (i % 2 == 0) Widgets.DrawBoxSolid(rr, new Color(1f, 1f, 1f, 0.04f));
			Widgets.DrawBoxSolid(new Rect(rr.x, rr.y, 4f, rr.height), row.bad ? new Color(0.9f, 0.3f, 0.25f) : row.borrowed ? new Color(0.95f, 0.75f, 0.3f) : new Color(0.4f, 0.75f, 0.45f));
			float tw = rr.width - 380f;
			Widgets.Label(new Rect(rr.x + 10f, rr.y + 2f, tw, 24f), (row.pawn.LabelShort + " – " + row.what).Truncate(tw));
			Text.Font = GameFont.Tiny;
			GUI.color = new Color(0.75f, 0.75f, 0.75f);
			Widgets.Label(new Rect(rr.x + 10f, rr.y + 25f, tw, 20f), (row.note + " · " + row.path).Truncate(tw));
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			float bx = rr.xMax - 370f;
			Pawn p = row.pawn;
			// body type of the pawn
			if (Widgets.ButtonText(new Rect(bx, rr.y + 10f, 120f, 28f), "BF_DocBodyBtn".Translate()))
			{
				var opts = new List<FloatMenuOption>();
				foreach (BodyTypeDef b in DefDatabase<BodyTypeDef>.AllDefsListForReading.Where(b => Bodies.BodyUsable(p, b)))
				{
					BodyTypeDef bb = b;
					opts.Add(new FloatMenuOption((p.story?.bodyType == b ? "● " : "") + b.defName, () =>
					{
						p.story.bodyType = bb;
						p.Drawer?.renderer?.SetAllGraphicsDirty();
						MLog.Info("[Body Fit] doctor: " + p.LabelShortCap + " body → " + bb.defName);
					}));
				}
				if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
			}
			if (row.apparel != null)
			{
				ThingDef d = row.apparel.def;
				bool hidden = BodyFitMod.Settings.doctorHide.Contains(d.defName);
				if (Widgets.ButtonText(new Rect(bx + 124f, rr.y + 10f, 110f, 28f), hidden ? "BF_DocShow".Translate() : "BF_DocHide".Translate()))
				{
					if (hidden) BodyFitMod.Settings.doctorHide.Remove(d.defName); else BodyFitMod.Settings.doctorHide.Add(d.defName);
					BodyFitMod.Settings.Write();
					BodyFitMod.Changed();
				}
				if (Widgets.ButtonText(new Rect(bx + 238f, rr.y + 10f, 130f, 28f), "BF_DocPicture".Translate()))
				{
					string worn = row.apparel.WornGraphicPath;
					var opts = new List<FloatMenuOption>();
					opts.Add(new FloatMenuOption("BF_DocAuto".Translate(), () =>
					{
						BodyFitMod.Settings.apparelBody.Remove(d.defName);
						BodyFitMod.Settings.Write();
						BodyFitMod.Changed();
					}));
					if (!worn.NullOrEmpty())
						foreach (BodyTypeDef b in DefDatabase<BodyTypeDef>.AllDefsListForReading.Where(b => Bodies.TextureExists(worn + "_" + b.defName)))
						{
							BodyTypeDef bb = b;
							bool cur = BodyFitMod.Settings.apparelBody.TryGetValue(d.defName, out string c) && c == b.defName;
							opts.Add(new FloatMenuOption((cur ? "● " : "") + b.defName, () =>
							{
								BodyFitMod.Settings.apparelBody[d.defName] = bb.defName;
								BodyFitMod.Settings.Write();
								BodyFitMod.Changed();
							}));
						}
					Find.WindowStack.Add(new FloatMenu(opts));
				}
			}
		}
		Widgets.EndScrollView();
	}
}

// ---------------------------------------------------------------------------
// 6. A report for mod authors: which pictures are missing, grouped by the mod they belong to.
// ---------------------------------------------------------------------------
public static class AuthorReport
{
	public static void Run()
	{
		try
		{
			string text = Build();
			string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "Logs");
			System.IO.Directory.CreateDirectory(dir);
			string file = System.IO.Path.Combine(dir, "BodyFit_report.txt");
			System.IO.File.WriteAllText(file, text, Encoding.UTF8);
			GUIUtility.systemCopyBuffer = text;
			Find.WindowStack.Add(new Dialog_MessageBox("BF_AuthorDone".Translate(file) + "\n\n" + (text.Length > 3000 ? text.Substring(0, 3000) + "\n…" : text)));
		}
		catch (Exception ex)
		{
			Messages.Message("[Body Fit] " + ex.Message, MessageTypeDefOf.RejectInput, false);
		}
	}

	private static string Build()
	{
		var sb = new StringBuilder();
		sb.AppendLine("Body Fit – missing pictures by mod (" + DateTime.Now.ToString("yyyy-MM-dd HH:mm") + ")");
		sb.AppendLine("Body types used by humanlike pawns on the loaded maps are checked. \"<path>_<BodyType>\" is the picture the game looks for.");
		sb.AppendLine();
		// body types in use, and per race
		var used = new HashSet<BodyTypeDef>();
		var raceBodies = new Dictionary<ThingDef, HashSet<BodyTypeDef>>();
		if (Current.Game != null && Find.Maps != null)
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
				{
					if (!p.RaceProps.Humanlike || p.story?.bodyType == null) continue;
					used.Add(p.story.bodyType);
					if (!raceBodies.TryGetValue(p.def, out var set)) raceBodies[p.def] = set = new HashSet<BodyTypeDef>();
					set.Add(p.story.bodyType);
				}
		if (used.Count == 0)
			foreach (BodyTypeDef b in new[] { BodyTypeDefOf.Male, BodyTypeDefOf.Female, BodyTypeDefOf.Thin, BodyTypeDefOf.Fat, BodyTypeDefOf.Hulk, BodyTypeDefOf.Child })
				if (b != null) used.Add(b);
		var byMod = new SortedDictionary<string, List<string>>();
		void Add(string mod, string line)
		{
			if (!byMod.TryGetValue(mod, out var l)) byMod[mod] = l = new List<string>();
			l.Add(line);
		}
		// clothes
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			ApparelProperties ap = d.apparel;
			if (ap == null || ap.wornGraphicPath.NullOrEmpty() || ap.LastLayer == ApparelLayerDefOf.Overhead || ap.LastLayer == ApparelLayerDefOf.EyeCover || ap.wornGraphicPath == BaseContent.PlaceholderImagePath)
				continue;
			try
			{
				var missing = used.Where(b => !Bodies.TextureExists(ap.wornGraphicPath + "_" + b.defName)).Select(b => b.defName).ToList();
				if (missing.Count == 0 || missing.Count == used.Count) continue;   // all missing = drawn some other way (pack, belt …)
				Add(d.modContentPack?.Name ?? "?", "clothing " + d.defName + " (" + ap.wornGraphicPath + "): no picture for " + string.Join(", ", missing));
			}
			catch { }
		}
		// race bodies
		foreach (var kv in raceBodies)
		{
			if (!Bodies.IsHar(kv.Key)) continue;
			foreach (BodyTypeDef b in kv.Value)
				if (!Bodies.RaceHasBody(kv.Key, b))
					Add(kv.Key.modContentPack?.Name ?? "?", "race " + kv.Key.defName + ": no body picture for " + b.defName + (b == BodyTypeDefOf.Child && ChildBody.CanUse(kv.Key) ? " (Body Fit made one)" : ""));
		}
		// heads, hair and anything else drawn as a red cross right now
		if (Current.Game != null && Find.Maps != null)
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike))
				{
					try
					{
						Graphic h = p.Drawer?.renderer?.HeadGraphic;
						if (h != null && h.MatSouth == BaseContent.BadMat)
							Add(p.def.modContentPack?.Name ?? "?", "head of " + p.LabelShort + " (" + p.def.defName + "): picture missing " + h.path);
					}
					catch { }
				}
		if (byMod.Count == 0)
			sb.AppendLine("Nothing missing. 🎉");
		foreach (var kv in byMod)
		{
			sb.AppendLine("== " + kv.Key + " (" + kv.Value.Count + ")");
			foreach (string l in kv.Value.Distinct()) sb.AppendLine("  - " + l);
			sb.AppendLine();
		}
		return sb.ToString();
	}
}
