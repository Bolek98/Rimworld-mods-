using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 4. Fitting borrowed clothes. When a piece of apparel has no picture for the
//    wearer's body and the picture of another body is used (2.), it would sit
//    like a shirt two sizes off. Here the real outline of both bodies is
//    traced from their body pictures (every visible pixel, per direction),
//    and the borrowed picture is stretched and moved so that it covers the
//    wearer's body exactly: same width, same height, same centre.
//    Works for every body that has a body picture – vanilla or modded.
// ---------------------------------------------------------------------------
public static class Silhouette
{
	// visible area of a picture in 0..1 (x, y from bottom-left), or null
	private static readonly Dictionary<string, Rect?> cache = new Dictionary<string, Rect?>();

	public static void Clear() { lock (BF.Gate) { cache.Clear(); fits.Clear(); } AlienFit.Clear(); }

	public static Rect? Of(BodyTypeDef b, Rot4 rot) => b == null ? null : Of(b.bodyNakedGraphicPath, rot);

	public static Rect? Of(string basePath, Rot4 rot)
	{
		if (basePath.NullOrEmpty())
			return null;
		string dir = rot == Rot4.North ? "_north" : rot == Rot4.South ? "_south" : "_east";
		string path = basePath + dir;
		Rect? r;
		lock (BF.Gate)
			if (cache.TryGetValue(path, out r))
				return r;
		// 1.2.3/1.2.4: pictures are read on the main thread only, and never during rendering
		if (!BF.Main || !BF.TraceAllowed)
		{
			BF.QueueOutline(basePath, rot.AsInt);
			return null;
		}
		r = null;
		try
		{
			Texture2D tex = ContentFinder<Texture2D>.Get(path, false);
			if (tex == null && rot.IsHorizontal)
				tex = ContentFinder<Texture2D>.Get(basePath + "_west", false);
			if (tex != null)
				r = Trace(tex);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] outline " + path + ": " + ex.Message, path.GetHashCode());
		}
		lock (BF.Gate)
			cache[path] = r;
		return r;
	}

	// Textures of the game are not readable – copy through the GPU first.
	private static Rect? Trace(Texture2D tex)
	{
		int w = Mathf.Min(tex.width, 256), h = Mathf.Min(tex.height, 256);
		RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
		RenderTexture prev = RenderTexture.active;
		Texture2D copy = null;
		try
		{
			Graphics.Blit(tex, rt);
			RenderTexture.active = rt;
			copy = new Texture2D(w, h, TextureFormat.ARGB32, false);
			copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			copy.Apply(false);
			Color32[] px = copy.GetPixels32();
			// 1.2.1: robust outline. A plain bounding box was thrown off by arms,
			// tails, hair, wings or loose pixels and made clothes far too big.
			// Height: rows holding 3 %–97 % of all visible pixels.
			// Width and centre: the median of the rows of the torso (the middle
			// band of the body), each row measured from its 10th to 90th
			// percentile of visible pixels.
			int[] rowCount = new int[h];
			int total = 0;
			for (int y = 0; y < h; y++)
			{
				int row = y * w, c = 0;
				for (int x = 0; x < w; x++)
					if (px[row + x].a >= 40)
						c++;
				rowCount[y] = c;
				total += c;
			}
			if (total < 20)
				return null;
			int acc = 0, minY = -1, maxY = h - 1;
			for (int y = 0; y < h; y++)
			{
				acc += rowCount[y];
				if (minY < 0 && acc >= total * 0.03f)
					minY = y;
				if (acc >= total * 0.97f)
				{
					maxY = y;
					break;
				}
			}
			if (minY < 0 || maxY <= minY)
				return null;
			int bandLo = minY + Mathf.RoundToInt((maxY - minY) * 0.30f);
			int bandHi = minY + Mathf.RoundToInt((maxY - minY) * 0.70f);
			var widths = new List<float>();
			var centres = new List<float>();
			for (int y = bandLo; y <= bandHi; y++)
			{
				int c = rowCount[y];
				if (c < 2)
					continue;
				int row = y * w, seen = 0, lo = -1, hi = -1;
				int pLo = Mathf.FloorToInt(c * 0.10f), pHi = Mathf.CeilToInt(c * 0.90f) - 1;
				for (int x = 0; x < w; x++)
				{
					if (px[row + x].a < 40)
						continue;
					if (seen == pLo) lo = x;
					if (seen == pHi) { hi = x; break; }
					seen++;
				}
				if (lo < 0 || hi <= lo)
					continue;
				widths.Add(hi - lo + 1);
				centres.Add((lo + hi) * 0.5f);
			}
			if (widths.Count == 0)
				return null;
			widths.Sort();
			centres.Sort();
			float width = widths[widths.Count / 2];
			float centre = centres[centres.Count / 2];
			return new Rect((centre - width * 0.5f) / w, (float)minY / h, width / w, (float)(maxY - minY + 1) / h);
		}
		finally
		{
			RenderTexture.active = prev;
			rt.Release();
			UnityEngine.Object.Destroy(rt);   // 2.3.4: no pooled temporaries – on 1.7 they pile up in video memory
			if (copy != null)
				UnityEngine.Object.Destroy(copy);
		}
	}

	// scale (x, z) and offset in mesh units (0..1) that move the picture made
	// for "from" onto the outline of "to"
	public static bool Fit(BodyTypeDef to, BodyTypeDef from, Rot4 rot, out Vector2 scale, out Vector2 shift)
		=> Fit(to?.bodyNakedGraphicPath, from?.bodyNakedGraphicPath, rot, out scale, out shift);

	private static readonly Dictionary<(string, string, int), (bool ok, Vector2 sc, Vector2 sh)> fits =
		new Dictionary<(string, string, int), (bool, Vector2, Vector2)>();

	public static bool Fit(string toPath, string fromPath, Rot4 rot, out Vector2 scale, out Vector2 shift)
		=> Fit(toPath, fromPath, rot, false, out scale, out shift);

	// 2.2.1: wide = a body that is the same shape but clearly smaller or bigger
	// (chibi races …): allow a bigger change of size
	public static bool Fit(string toPath, string fromPath, Rot4 rot, bool wide, out Vector2 scale, out Vector2 shift)
	{
		scale = Vector2.one;
		shift = Vector2.zero;
		if (toPath.NullOrEmpty() || fromPath.NullOrEmpty() || toPath == fromPath)
			return false;
		var key = (toPath, fromPath, rot.AsInt + (wide ? 8 : 0));
		lock (BF.Gate)
		{
			if (fits.TryGetValue(key, out var f))
			{
				scale = f.sc;
				shift = f.sh;
				return f.ok;
			}
		}
		bool wasNotReady = BF.NotReady;
		BF.NotReady = false;
		bool ok = FitInner(toPath, fromPath, rot, wide, out scale, out shift);
		bool guessed = BF.NotReady;
		BF.NotReady = wasNotReady || guessed;
		if (guessed)
		{
			// an outline is still being loaded – no fitting this frame, nothing remembered
			scale = Vector2.one;
			shift = Vector2.zero;
			return false;
		}
		lock (BF.Gate)
			fits[key] = (ok, scale, shift);
		return ok;
	}

	private static bool FitInner(string toPath, string fromPath, Rot4 rot, bool wide, out Vector2 scale, out Vector2 shift)
	{
		scale = Vector2.one;
		shift = Vector2.zero;
		Rect? a = Of(toPath, rot), b = Of(fromPath, rot);
		if (a == null || b == null)
			return false;
		Rect t = a.Value, f = b.Value;
		if (f.width < 0.01f || f.height < 0.01f)
			return false;
		float lo = wide ? 0.5f : 0.75f, hi = wide ? 1.6f : 1.35f;
		scale = new Vector2(Mathf.Clamp(t.width / f.width, lo, hi), Mathf.Clamp(t.height / f.height, lo, hi));
		// centre of "to" minus the scaled centre of "from", around the mesh centre 0.5
		shift = new Vector2((t.center.x - 0.5f) - (f.center.x - 0.5f) * scale.x,
		                    (t.center.y - 0.5f) - (f.center.y - 0.5f) * scale.y);
		// 1.2.4: safety – a real fit moves a picture a little; a big jump means the
		// outline is not a body/head (odd canvas, effects, translucent sprite) – leave it
		if (Mathf.Abs(shift.x) > 0.12f || Mathf.Abs(shift.y) > 0.12f)
		{
			scale = Vector2.one;
			shift = Vector2.zero;
			return false;
		}
		return true;
	}
}


// ---------------------------------------------------------------------------
// 2.2.1: can clothes be fitted to an alien body at all? Alien races (HAR) draw
// their own bodies, and many of them are not a human body: tentacles, a whale
// tail, wings or a big aura are part of the body picture. Their outline says
// nothing about where a shirt belongs, and fitting to it made clothes huge,
// shifted or squeezed. The race's own look is the safe default.
//   Full  – practically a human body (same size and place in every view):
//           the normal fitting and refitting may run.
//   Scale – the same shape, only clearly smaller or bigger, consistently in
//           every view (chibi races): clothes are only scaled and moved.
//   None  – anything else: clothes stay exactly as the race mod draws them.
// Measured in all three views; one odd view is enough for None.
// ---------------------------------------------------------------------------
public static class AlienFit
{
	public enum Trust { None = 0, Scale = 1, Full = 2 }

	private static readonly Dictionary<(string, string), Trust> trusts = new Dictionary<(string, string), Trust>();

	public static void Clear() { lock (BF.Gate) trusts.Clear(); }

	// real = body picture really drawn, from = body the clothes were painted on
	public static Trust Of(string real, string from)
	{
		if (real.NullOrEmpty() || from.NullOrEmpty())
			return Trust.None;
		if (real == from)
			return Trust.Full;
		Trust t;
		lock (BF.Gate)
			if (trusts.TryGetValue((real, from), out t))
				return t;
		bool wasNotReady = BF.NotReady;
		BF.NotReady = false;
		t = Measure(real, from);
		bool guessed = BF.NotReady;
		BF.NotReady = wasNotReady || guessed;
		if (guessed)
			return Trust.None;   // outlines still loading – nothing fitted, nothing remembered
		lock (BF.Gate)
			trusts[(real, from)] = t;
		return t;
	}

	private static readonly Rot4[] Views = { Rot4.South, Rot4.East, Rot4.North };

	private static Trust Measure(string real, string from)
	{
		// ask for every outline first – all of them load in the same frame
		var a = new Rect?[3];
		var b = new Rect?[3];
		for (int i = 0; i < 3; i++)
		{
			a[i] = Silhouette.Of(real, Views[i]);
			b[i] = Silhouette.Of(from, Views[i]);
		}
		bool full = true;
		float sMin = float.MaxValue, sMax = 0f;
		for (int i = 0; i < 3; i++)
		{
			if (a[i] == null || b[i] == null)
				return Trust.None;
			Rect t = a[i].Value, f = b[i].Value;
			if (f.width < 0.01f || f.height < 0.01f || t.width < 0.01f || t.height < 0.01f)
				return Trust.None;
			float rw = t.width / f.width, rh = t.height / f.height;
			float shape = rw / rh, size = Mathf.Sqrt(rw * rh);
			// another shape (much wider than tall: tentacles, tails, wings …) or absurd size
			if (shape < 0.75f || shape > 1.35f || size < 0.5f || size > 1.6f)
				return Trust.None;
			float sx = (t.center.x - 0.5f) - (f.center.x - 0.5f) * rw;
			float sy = (t.center.y - 0.5f) - (f.center.y - 0.5f) * rh;
			if (Mathf.Abs(sx) > 0.12f || Mathf.Abs(sy) > 0.12f)
				return Trust.None;
			if (rw < 0.8f || rw > 1.25f || rh < 0.8f || rh > 1.25f || Mathf.Abs(sx) > 0.06f || Mathf.Abs(sy) > 0.06f)
				full = false;
			sMin = Mathf.Min(sMin, size);
			sMax = Mathf.Max(sMax, size);
		}
		// the views must agree – a body that is "bigger" only from one side is not a scaled human
		if (sMax / sMin > 1.25f)
			return Trust.None;
		return full ? Trust.Full : Trust.Scale;
	}

	// a pawn of an alien race that draws its own body picture (not a game/body-mod body)
	public static bool OwnBody(Pawn p, BodyTypeDef body, string real)
		=> p != null && body != null && !real.NullOrEmpty() && Bodies.IsHar(p.def) && real != body.bodyNakedGraphicPath;
}

// ---------------------------------------------------------------------------
// 1.2: every piece of body apparel is fitted to the body the pawn really has.
// The picture of a piece of clothing is made for one body picture (the
// vanilla body of its body type, or the borrowed body). If the pawn's real
// body picture is different – alien races, modded bodies, FemaleBB … – the
// clothing is stretched and moved onto the real outline. Pictures made
// especially for a race (HAR race clothing) are left alone. Vanilla humans
// in vanilla clothes: nothing changes.
// ---------------------------------------------------------------------------
public static class ApparelFit
{
	private sealed class Entry
	{
		public Graphic graphic;
		public string body;
		public int rot;
		public bool ok;
		public Vector2 scale, shift;
	}

	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PawnRenderNode, Entry> cache =
		new System.Runtime.CompilerServices.ConditionalWeakTable<PawnRenderNode, Entry>();

	public static bool Get(PawnRenderNode_Apparel node, Pawn pawn, Rot4 rot, out Vector2 scale, out Vector2 shift)
	{
		scale = Vector2.one;
		shift = Vector2.zero;
		BodyFitSettings s = BodyFitMod.Settings;
		if (!s.fitApparel || Refit.Suspended || node.apparel == null || pawn?.story?.bodyType == null)
			return false;
		if (s.noFitRaces.Contains(pawn.def.defName))
			return false;
		Graphic g = node.PrimaryGraphic;
		if (Refit.IsRefit(g))
			return false;   // 2.0: already redrawn for this body
		string real = Patch_FitScale.WearerBodyPath(pawn, pawn.story.bodyType);
		Entry e;
		lock (BF.Gate)
		{
			e = cache.GetValue(node, _ => new Entry());
			if (e.graphic == g && e.body == real && e.rot == rot.AsInt)
			{
				scale = e.scale;
				shift = e.shift;
				return e.ok;
			}
		}
		bool wasNotReady = BF.NotReady;
		BF.NotReady = false;
		bool ok = false;
		Vector2 sc = Vector2.one, sh = Vector2.zero;
		BodyTypeDef body = pawn.story.bodyType;
		BodyTypeDef madeFor = Patch_Apparel.Substitute(node.apparel, body) ?? body;
		string worn = node.apparel.WornGraphicPath;
		// only pictures of the form <worn>_<body>; race-specific pictures are made for that race
		if (g != null && !worn.NullOrEmpty() && g.path == worn + "_" + madeFor.defName)
		{
			// 1.2.2: clothing from a race mod is drawn on that race's body
			ThingDef home = Bodies.HomeRace(node.apparel.def);
			bool foreign = home != null && home != pawn.def;
			string from = foreign ? Bodies.RaceBodyPath(home, madeFor) : null;
			if (from.NullOrEmpty())
				from = madeFor.bodyNakedGraphicPath;
			if (foreign || madeFor != body || s.fitAll)
			{
				// 2.2.1: alien bodies only when the body really is a (scaled) human body
				bool wide = false, allowed = true;
				if (AlienFit.OwnBody(pawn, body, real))
				{
					AlienFit.Trust tr = AlienFit.Of(real, from);
					allowed = tr != AlienFit.Trust.None || Shots.Loose;
					wide = tr == AlienFit.Trust.Scale;
				}
				if (allowed)
					ok = Silhouette.Fit(real, from, rot, wide, out sc, out sh);
			}
		}
		bool guessed = BF.NotReady;
		BF.NotReady = wasNotReady || guessed;
		if (guessed)
			return false;   // a picture is still being loaded – try again next frame
		lock (BF.Gate)
		{
			e.graphic = g;
			e.body = real;
			e.rot = rot.AsInt;
			e.ok = ok;
			e.scale = sc;
			e.shift = sh;
		}
		scale = sc;
		shift = sh;
		return ok;
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker_Apparel_Body), nameof(PawnRenderNodeWorker_Apparel_Body.ScaleFor))]
public static class Patch_FitScale
{
	// the body picture really drawn (alien races draw their own)
	public static string WearerBodyPath(Pawn p, BodyTypeDef body)
	{
		string path = null;
		try { path = p?.Drawer?.renderer?.BodyGraphic?.path; } catch { }
		return path.NullOrEmpty() ? body?.bodyNakedGraphicPath : path;
	}

	public static void Postfix(PawnRenderNode n, PawnDrawParms parms, ref Vector3 __result)
	{
		if (!(n is PawnRenderNode_Apparel node) || !BF.Handles(parms.pawn))
			return;
		try
		{
			Pawn pawn = parms.pawn;
			if (ApparelFit.Get(node, pawn, parms.facing, out Vector2 sc, out _))
			{
				__result.x *= sc.x;
				__result.z *= sc.y;
			}
			// your own size of clothes for this race
			Vector2 own = BodyFitMod.Settings.ClothesSize(pawn?.def);
			__result.x *= own.x;
			__result.z *= own.y;
		}
		catch
		{
		}
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker_Apparel_Body), nameof(PawnRenderNodeWorker_Apparel_Body.OffsetFor))]
public static class Patch_FitOffset
{
	public static void Postfix(PawnRenderNode n, PawnDrawParms parms, ref Vector3 __result)
	{
		if (!(n is PawnRenderNode_Apparel node) || !BF.Handles(parms.pawn))
			return;
		try
		{
			Pawn pawn = parms.pawn;
			if (!ApparelFit.Get(node, pawn, parms.facing, out _, out Vector2 sh))
				return;
			float size = HumanlikeMeshPoolUtility.HumanlikeBodyWidthForPawn(pawn);
			float x = sh.x * size;
			if (parms.facing == Rot4.West)
				x = -x;   // west is the mirrored east picture
			__result.x += x;
			__result.z += sh.y * size;
		}
		catch
		{
		}
	}
}
