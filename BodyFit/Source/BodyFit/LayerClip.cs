using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.4 (testing first): layers do not poke out. A piece of clothing that is fully covered by a piece
// worn over it (the outer one covers the same body parts or more, e.g. a shirt under a parka) is cut
// to the outline of the outer piece, so its edges do not stick out from under it. Pieces that only
// partly overlap (trousers under a jacket, a vest over a shirt with sleeves) are left alone.
public static class LayerClip
{
	public const string Prefix = "BodyFit/Clip/";
	private static readonly string[] Dirs = { "north", "east", "south", "west" };
	private static readonly Dictionary<string, string> done = new Dictionary<string, string>();
	private static readonly Dictionary<string, (string inner, string outer)> pending = new Dictionary<string, (string, string)>();
	private static int counter;
	[ThreadStatic] private static int depth;

	public static bool Active => Shots.Loose;   // testing only for now

	public static int PendingCount { get { lock (BF.Gate) return pending.Count; } }

	public static void Clear()
	{
		lock (BF.Gate)
		{
			done.Clear();
			pending.Clear();
		}
	}

	// the piece worn over this one that covers everything this one covers (topmost), or null
	public static Apparel Outer(Pawn p, Apparel a)
	{
		if (p?.apparel == null || a?.def?.apparel == null)
			return null;
		var mine = a.def.apparel.bodyPartGroups;
		if (mine == null || mine.Count == 0 || a.def.apparel.LastLayer.IsUtilityLayer || IsHeadLayer(a.def.apparel.LastLayer))
			return null;
		Apparel best = null;
		foreach (Apparel o in p.apparel.WornApparel)
		{
			if (o == a || o.def.apparel == null)
				continue;
			ApparelLayerDef ol = o.def.apparel.LastLayer;
			if (ol.IsUtilityLayer || IsHeadLayer(ol) || ol.drawOrder <= a.def.apparel.LastLayer.drawOrder)
				continue;
			var theirs = o.def.apparel.bodyPartGroups;
			if (theirs == null || !mine.All(g => theirs.Contains(g)))
				continue;
			if (best == null || ol.drawOrder > best.def.apparel.LastLayer.drawOrder)
				best = o;
		}
		return best;
	}

	private static bool IsHeadLayer(ApparelLayerDef l) => l == ApparelLayerDefOf.Overhead || l == ApparelLayerDefOf.EyeCover;

	// path of the outer piece as it is really drawn (refitted or not), or null
	public static string OuterPath(Apparel outer, BodyTypeDef body)
	{
		if (depth > 3)
			return null;
		depth++;
		try
		{
			return ApparelGraphicRecordGetter.TryGetGraphicApparel(outer, body, false, out ApparelGraphicRecord r) ? r.graphic?.path : null;
		}
		finally
		{
			depth--;
		}
	}

	public static string Request(string inner, string outer, out bool ready)
	{
		string key = inner + "|" + outer;
		lock (BF.Gate)
		{
			if (done.TryGetValue(key, out string f))
			{
				ready = true;
				return f;
			}
			if (!pending.ContainsKey(key))
				pending[key] = (inner, outer);
		}
		ready = false;
		return null;
	}

	public static void ProcessPending()
	{
		if (!BF.Main)
			return;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		while (sw.ElapsedMilliseconds < 6)
		{
			string key;
			(string inner, string outer) job;
			lock (BF.Gate)
			{
				if (pending.Count == 0)
					return;
				var kv = pending.First();
				key = kv.Key;
				job = kv.Value;
				pending.Remove(key);
			}
			string fake = null;
			try
			{
				fake = Build(job.inner, job.outer);
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Body Fit] layers " + job.inner + ": " + ex.Message, key.GetHashCode());
			}
			lock (BF.Gate)
				done[key] = fake;
			if (fake != null)
				Refit.WantRedraw();
		}
	}

	private static string Build(string inner, string outer)
	{
		var holder = BodyFitMod.Content?.GetContentHolder<Texture2D>();
		if (holder == null)
			return null;
		string fake = Prefix + (++counter);
		var add = new List<KeyValuePair<string, Texture2D>>();
		bool changed = false;
		foreach (string dir in Dirs)
		{
			Texture2D main = ContentFinder<Texture2D>.Get(inner + "_" + dir, false);
			if (main == null)
				continue;
			Texture2D mask = ContentFinder<Texture2D>.Get(inner + "_" + dir + "m", false);
			Texture2D cover = ContentFinder<Texture2D>.Get(outer + "_" + dir, false);
			bool flip = false;
			if (cover == null && dir == "west")
			{
				cover = ContentFinder<Texture2D>.Get(outer + "_east", false);
				flip = true;
			}
			if (cover == null)
			{
				add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, main));
				if (mask != null) add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", mask));
				continue;
			}
			Texture2D cm = Cut(main, cover, flip, out bool ch);
			if (cm == null)
				return null;
			changed |= ch;
			add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, cm));
			if (mask != null)
				add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", Cut(mask, cover, flip, out _) ?? mask));
		}
		if (!changed || add.Count == 0)
			return null;
		foreach (var kv in add)
			holder.contentList[kv.Key] = kv.Value;
		if (ContentFinder<Texture2D>.Get(add[0].Key, false) != add[0].Value)
		{
			foreach (var kv in add)
				holder.contentList.Remove(kv.Key);
			return null;
		}
		return fake;
	}

	// pixels of src outside the (slightly widened) outline of cover become transparent
	private static Texture2D Cut(Texture2D src, Texture2D cover, bool flip, out bool changed)
	{
		changed = false;
		Color32[] px = Refit.ReadPixels(src, out int w, out int h);
		Color32[] cp = Refit.ReadPixels(cover, out int cw, out int ch);
		if (px == null || cp == null)
			return null;
		// widened cover outline in cover pixels
		const int R = 2;
		var solid = new bool[cw * ch];
		for (int y = 0; y < ch; y++)
			for (int x = 0; x < cw; x++)
			{
				if (cp[y * cw + x].a < 40)
					continue;
				for (int dy = -R; dy <= R; dy++)
					for (int dx = -R; dx <= R; dx++)
					{
						int xx = x + dx, yy = y + dy;
						if (xx >= 0 && yy >= 0 && xx < cw && yy < ch)
							solid[yy * cw + xx] = true;
					}
			}
		var outPx = new Color32[w * h];
		for (int y = 0; y < h; y++)
			for (int x = 0; x < w; x++)
			{
				Color32 c = px[y * w + x];
				if (c.a > 0)
				{
					float u = (x + 0.5f) / w;
					if (flip) u = 1f - u;
					int cx = Mathf.Clamp((int)(u * cw), 0, cw - 1), cy = Mathf.Clamp((int)((y + 0.5f) / h * ch), 0, ch - 1);
					if (!solid[cy * cw + cx])
					{
						c.a = 0;
						changed = true;
					}
				}
				outPx[y * w + x] = c;
			}
		var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
		tex.name = "BodyFitClip_" + src.name;
		tex.filterMode = src.filterMode;
		tex.wrapMode = TextureWrapMode.Clamp;
		tex.SetPixels32(outPx);
		tex.Apply(true, true);
		return tex;
	}
}
