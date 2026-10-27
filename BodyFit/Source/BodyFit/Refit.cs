using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 2.0: Refitting. Every piece of body clothing and armour is redrawn so that
// it follows the body the pawn really has – Beautiful Bodies, FemaleBB, alien
// race bodies … The body itself is never changed.
//
//   * The picture of a piece of clothing was painted over one body: the
//     game's own body of that body type (read straight from the game files,
//     so even a body mod that replaces the vanilla bodies cannot hide it),
//     or the modded body it was made for.
//   * Both bodies are measured row by row (left and right edge of every row,
//     top and bottom of the body).
//   * Every row of the clothing is moved and stretched from the old body onto
//     the new one. Coats, capes and skirts below the body follow the last body
//     row, so long clothes stay long. Colour masks are refitted the same way.
//   * The new pictures are made once (outside drawing, a few per frame) and
//     kept for the whole game.
//   * Left alone: clothes a body mod or race mod painted for that body
//     itself, Sized Apparel, race-specific pictures, statues and stands.
// ---------------------------------------------------------------------------
public static class Refit
{
	public const string Prefix = "BodyFit/Refit/";

	private static readonly string[] Dirs = { "north", "east", "south", "west" };

	public sealed class Job
	{
		public string key;
		public string apparelPath;   // <worn>_<body>
		public string refPath;       // body the picture was painted on
		public bool refVanilla;      // read the reference from the game files
		public string targetPath;    // body really drawn
		public bool core;            // 2.1: measure only the trunk (clothes without sleeves)
		public bool head;            // 2.1: hair / beard / headgear on a head – sideways only
	}

	// 2.1: the preview draws the pawn once without any fitting
	public static bool Suspended;

	// key -> fake path (null = nothing to do / failed)
	private static readonly Dictionary<string, string> done = new Dictionary<string, string>();
	private static readonly Dictionary<string, Job> pending = new Dictionary<string, Job>();
	private static int counter;
	public static int made, skipped;

	// review: finished pictures are kept – every setting that changes them is part
	// of the key, so a settings change never rebuilds (and leaks) the same pictures
	public static void Clear()
	{
		lock (BF.Gate)
			pending.Clear();
	}

	public static bool IsRefit(Graphic g) => g?.path != null && g.path.StartsWith(Prefix);

	// fake path when ready, null otherwise (queued if new)
	// 2.5.2: who waits for which picture – when it is ready only those pawns are redrawn
	private static readonly Dictionary<string, HashSet<Pawn>> waiters = new Dictionary<string, HashSet<Pawn>>();

	public static void AddWaiter(Dictionary<string, HashSet<Pawn>> map, string key, Pawn who)
	{
		if (who == null)
			return;
		if (!map.TryGetValue(key, out var set))
			map[key] = set = new HashSet<Pawn>();
		set.Add(who);
	}

	public static void RedrawWaiters(Dictionary<string, HashSet<Pawn>> map, string key)
	{
		HashSet<Pawn> set;
		lock (BF.Gate)
		{
			if (!map.TryGetValue(key, out set))
				return;
			map.Remove(key);
		}
		foreach (Pawn p in set)
			QueueRedraw(p);
	}

	public static string Request(Job j, out bool ready, Pawn who = null)
	{
		BodyFitSettings s = BodyFitMod.Settings;
		j.key += (s.refitSides ? "|s" : "") + (s.refitHeight && !j.head ? "|y" : "") + (Shots.Loose ? "|wide" : "");
		lock (BF.Gate)
		{
			if (done.TryGetValue(j.key, out string p))
			{
				ready = true;
				return p;
			}
			if (!pending.ContainsKey(j.key))
				pending[j.key] = j;
			AddWaiter(waiters, j.key, who);
		}
		ready = false;
		return null;
	}

	// main thread, every frame, outside drawing
	public static int PendingCount { get { lock (BF.Gate) return pending.Count; } }

	public static void WantRedraw() => redrawWanted = true;

	public static Color32[] ReadPixels(Texture2D tex, out int w, out int h) => Read(tex, out w, out h);

	public static void ProcessPending()
	{
		if (!BF.Main)
			return;
		List<Job> jobs = null;
		int left;
		lock (BF.Gate)
			left = pending.Count;
		// 2.2.2: redraw the colony once when the queue is done (or every 3 s
		// while a long queue runs) – not every frame. Redrawing all pawns each
		// frame made alien races rebuild their graphics non-stop: bodies
		// vanished, the log filled up and the game nearly stopped.
		// 2.2.3: pawns skipped while busy (RJW / animation) are redrawn once they are free
		if (deferred.Count > 0 && UnityEngine.Time.frameCount % 30 == 0)
			RedrawDeferred();
		if (redrawQueue.Count > 0)
			RedrawSome();
		if (redrawWanted && (left == 0 || UnityEngine.Time.realtimeSinceStartup - lastRedraw > 3f))
		{
			redrawWanted = false;
			lastRedraw = UnityEngine.Time.realtimeSinceStartup;
			RedrawAll();
		}
		// 2.2.4: a time budget instead of 4 jobs a frame – one job can take 50 ms on big pictures,
		// four of them made every frame slow while many new clothes / bodies were being fitted
		var sw = System.Diagnostics.Stopwatch.StartNew();
		jobs = new List<Job>();
		bool anyBuilt = false;
		while (!BF.OverBudget)
		{
			Job next;
			lock (BF.Gate)
			{
				if (pending.Count == 0)
					break;
				next = pending.First().Value;
				pending.Remove(next.key);
			}
			string fakeOne = null;
			try
			{
				fakeOne = Build(next);
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Body Fit] refit " + next.apparelPath + ": " + ex.Message, next.key.GetHashCode());
			}
			lock (BF.Gate)
				done[next.key] = fakeOne;
			bool known;
			lock (BF.Gate)
				known = waiters.ContainsKey(next.key);
			if (fakeOne != null)
			{
				made++;
				// 2.5.2: only the pawns that wear this piece are redrawn; everybody only when nobody is known
				if (known)
					RedrawWaiters(waiters, next.key);
				else
					anyBuilt = true;
			}
			else if (known)
				lock (BF.Gate)
					waiters.Remove(next.key);
			else
				skipped++;
			if (sw.Elapsed.TotalMilliseconds > 6.0 || BF.OverBudget)
				break;
		}
		if (anyBuilt)
			redrawWanted = true;
		return;
		bool any = false;
		foreach (Job j in jobs)
		{
			string fake = null;
			try
			{
				fake = Build(j);
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Body Fit] refit " + j.apparelPath + ": " + ex.Message, j.key.GetHashCode());
			}
			lock (BF.Gate)
				done[j.key] = fake;
			if (fake != null)
			{
				made++;
				any = true;
			}
			else
				skipped++;
		}
		if (any)
			redrawWanted = true;
	}

	private static bool redrawWanted;
	private static float lastRedraw;

	private static void RedrawAll()
	{
		try
		{
			if (Current.Game == null || Find.Maps == null)
				return;
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
					if (p.RaceProps.Humanlike)
					{
						// 2.2.3: never rebuild the graphics of a pawn in the middle of an RJW act / animation –
						// the animation mod lost the pawn and it stayed invisible
						if (Busy(p))
						{
							deferred.Add(p);
							continue;
						}
						// 2.5.1: not all pawns in one frame – a few per frame (big colonies with alien races
						// stopped for 100–500 ms each time all of them rebuilt their graphics at once)
						if (redrawSet.Add(p))
							redrawQueue.Enqueue(p);
					}
		}
		catch
		{
		}
	}

	private static readonly Queue<Pawn> redrawQueue = new Queue<Pawn>();
	private static readonly HashSet<Pawn> redrawSet = new HashSet<Pawn>();

	public static void QueueRedraw(Pawn p)
	{
		if (p != null && redrawSet.Add(p))
			redrawQueue.Enqueue(p);
	}

	// a few pawns per frame, in the order they were queued
	private static void RedrawSome()
	{
		int n = 0;
		while (redrawQueue.Count > 0 && n < 3)
		{
			Pawn p = redrawQueue.Dequeue();
			redrawSet.Remove(p);
			try
			{
				if (p == null || !p.Spawned || p.Destroyed)
					continue;
				if (Busy(p))
				{
					deferred.Add(p);
					continue;
				}
				p.Drawer?.renderer?.SetAllGraphicsDirty();
				PortraitsCache.SetDirty(p);
				n++;
			}
			catch
			{
			}
		}
	}

	private static readonly HashSet<Pawn> deferred = new HashSet<Pawn>();

	public static bool Busy(Pawn p)
	{
		try
		{
			object d = p?.jobs?.curDriver;
			if (d == null)
				return false;
			string ns = d.GetType().Namespace ?? "";
			if (ns.StartsWith("rjw", StringComparison.OrdinalIgnoreCase) || ns.IndexOf("Animation", StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
			string job = p.CurJobDef?.defName ?? "";
			return job.StartsWith("RJW", StringComparison.OrdinalIgnoreCase) || job.IndexOf("Lovin", StringComparison.OrdinalIgnoreCase) >= 0;
		}
		catch
		{
			return false;
		}
	}

	private static void RedrawDeferred()
	{
		try
		{
			List<Pawn> free = null;
			foreach (Pawn p in deferred)
				if (p == null || !p.Spawned || !Busy(p))
					(free ??= new List<Pawn>()).Add(p);
			if (free == null)
				return;
			foreach (Pawn p in free)
			{
				deferred.Remove(p);
				if (p != null && p.Spawned)
				{
					p.Drawer?.renderer?.SetAllGraphicsDirty();
					PortraitsCache.SetDirty(p);
				}
			}
		}
		catch
		{
			deferred.Clear();
		}
	}

	// ---- body measurements --------------------------------------------------
	private sealed class Profile
	{
		public int h;
		public float[] l, r;     // per row, 0..1, NaN = no body in this row
		public float[] cl, cr;   // 2.1: the part of the row around the middle (trunk without arms)
		public float bottom, top; // 0..1
	}

	private static readonly Dictionary<Texture2D, Profile> profiles = new Dictionary<Texture2D, Profile>();

	private static Profile Measure(Texture2D tex)
	{
		if (tex == null)
			return null;
		if (profiles.TryGetValue(tex, out Profile pr))
			return pr;
		pr = null;
		Color32[] px = Read(tex, out int w, out int h);
		if (px != null)
		{
			pr = new Profile { h = h, l = new float[h], r = new float[h], cl = new float[h], cr = new float[h] };
			int[] count = new int[h];
			int total = 0;
			for (int y = 0; y < h; y++)
			{
				int lo = -1, hi = -1, row = y * w, c = 0;
				for (int x = 0; x < w; x++)
				{
					if (px[row + x].a < 60)
						continue;
					if (lo < 0) lo = x;
					hi = x;
					c++;
				}
				count[y] = c;
				total += c;
				if (c >= 2 && hi > lo)
				{
					pr.l[y] = (float)lo / w;
					pr.r[y] = (float)(hi + 1) / w;
					// the run of pixels nearest to the middle: the trunk, arms with a gap are left out
					int mid = w / 2, best = -1;
					for (int d = 0; d < w / 2 && best < 0; d++)
					{
						if (mid - d >= 0 && px[row + mid - d].a >= 60) best = mid - d;
						else if (mid + d < w && px[row + mid + d].a >= 60) best = mid + d;
					}
					int a0 = best, a1 = best;
					while (a0 > 0 && px[row + a0 - 1].a >= 60) a0--;
					while (a1 < w - 1 && px[row + a1 + 1].a >= 60) a1++;
					pr.cl[y] = (float)a0 / w;
					pr.cr[y] = (float)(a1 + 1) / w;
				}
				else
				{
					pr.l[y] = pr.r[y] = pr.cl[y] = pr.cr[y] = float.NaN;
				}
			}
			if (total < 30)
				pr = null;
			else
			{
				int acc = 0, b = -1, t = h - 1;
				for (int y = 0; y < h; y++)
				{
					acc += count[y];
					if (b < 0 && acc >= total * 0.01f) b = y;
					if (acc >= total * 0.99f) { t = y; break; }
				}
				pr.bottom = (float)Mathf.Max(b, 0) / h;
				pr.top = (float)(t + 1) / h;
			}
		}
		profiles[tex] = pr;
		return pr;
	}

	// edges of a row at height v (0..1); false when no body there
	private static bool Edges(Profile p, float v, bool core, out float l, out float r)
	{
		int y = Mathf.Clamp(Mathf.FloorToInt(v * p.h), 0, p.h - 1);
		l = core ? p.cl[y] : p.l[y];
		r = core ? p.cr[y] : p.r[y];
		return !float.IsNaN(l);
	}

	// ---- reading pictures (not readable in the game – through the GPU) -----
	private static Color32[] Read(Texture2D tex, out int w, out int h)
	{
		w = Mathf.Min(tex.width, 1024);
		h = Mathf.Min(tex.height, 1024);
		RenderTexture rt = new RenderTexture(w, h, 0, RenderTextureFormat.ARGB32);
		RenderTexture prev = RenderTexture.active;
		Texture2D copy = null;
		try
		{
			Graphics.Blit(tex, rt);
			RenderTexture.active = rt;
			copy = new Texture2D(w, h, TextureFormat.RGBA32, false);
			copy.ReadPixels(new Rect(0, 0, w, h), 0, 0);
			copy.Apply(false);
			return copy.GetPixels32();
		}
		catch
		{
			return null;
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

	// flipped: the game draws this body's east picture mirrored for west
	private static Texture2D Body(string basePath, string dir, bool vanilla, out bool flipped)
	{
		flipped = false;
		if (basePath.NullOrEmpty())
			return null;
		Texture2D t = Load(basePath + "_" + dir, vanilla);
		if (t == null && dir == "west")
		{
			t = Load(basePath + "_east", vanilla);
			flipped = t != null;
		}
		return t;
	}

	// vanilla: the game's own picture, even if a body mod replaced it; DLC pictures
	// that are not in the game's resources come through the normal lookup
	private static Texture2D Load(string path, bool vanilla)
	{
		Texture2D t = vanilla ? Resources.Load<Texture2D>("Textures/" + path) : null;
		return t ?? ContentFinder<Texture2D>.Get(path, false);
	}

	// ---- the work -----------------------------------------------------------
	private static string Build(Job j)
	{
		BodyFitSettings s = BodyFitMod.Settings;
		string fake = Prefix + (++counter);
		var holder = BodyFitMod.Content?.GetContentHolder<Texture2D>();
		if (holder == null)
			return null;
		bool changed = false;
		var add = new List<KeyValuePair<string, Texture2D>>();
		foreach (string dir in Dirs)
		{
			Texture2D main = ContentFinder<Texture2D>.Get(j.apparelPath + "_" + dir, false);
			if (main == null)
				continue;   // the game falls back (flips east, …) exactly as for the original
			Texture2D mask = ContentFinder<Texture2D>.Get(j.apparelPath + "_" + dir + "m", false);
			bool side = dir == "east" || dir == "west";
			// 2.5: known from an earlier game – no reading back from the graphics card
			string ck = j.key + "|" + dir;
			if (RefitCache.TryLoad(ck, main, out Texture2D cached))
			{
				if (cached == null)
				{
					add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, main));
					if (mask != null)
						add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", mask));
					continue;
				}
				changed = true;
				add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, cached));
				if (mask != null)
				{
					Texture2D cmk = RefitCache.TryLoad(ck + "m", mask, out Texture2D cm2) ? cm2 : null;
					add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", cmk ?? mask));
				}
				continue;
			}
			Texture2D from = Body(j.refPath, dir, j.refVanilla, out bool flipFrom);
			Texture2D to = Body(j.targetPath, dir, false, out bool flipTo);
			RowMap map = null;
			if ((!side || s.refitSides) && from != null && to != null && (from != to || flipFrom != flipTo))
			{
				// review: each body is mirrored on its own – one may have a real west picture, the other not
				map = RowMap.Make(Measure(from), Measure(to), flipFrom, flipTo, s.refitHeight && !j.head, j.core, j.head);
			}
			if (map == null)
			{
				Shots.WhyKey("build: no rowmap " + dir + " " + j.apparelPath + " from=" + (from != null) + " to=" + (to != null) + " | " + j.refPath + " -> " + j.targetPath + (from != null && to != null ? " " + RowMap.Diag(Measure(from), Measure(to)) : ""));
				add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, main));
				if (mask != null)
					add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", mask));
				continue;
			}
			Texture2D wm = Warp(main, map, out Color32[] wpx);
			if (wm == null)
				return null;
			changed = true;
			add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir, wm));
			if (!Shots.Experiment)
				RefitCache.Save(ck, main, wpx, wm.width, wm.height);
			if (mask != null)
			{
				Texture2D wk = Warp(mask, map, out Color32[] kpx);
				add.Add(new KeyValuePair<string, Texture2D>(fake + "_" + dir + "m", wk ?? mask));
				if (wk != null && !Shots.Experiment)
					RefitCache.Save(ck + "m", mask, kpx, wk.width, wk.height);
			}
		}
		if (!changed)
		{
			Shots.WhyKey("build: no change " + j.apparelPath + " | " + j.refPath + " -> " + j.targetPath);
			return null;
		}
		// bodies are the same – keep the original picture
		foreach (var kv in add)
			holder.contentList[kv.Key] = kv.Value;
		// another mod may cache the game's picture lookup – then the new pictures
		// cannot be found and the original is kept (no pink squares)
		if (ContentFinder<Texture2D>.Get(add[0].Key, false) != add[0].Value)
		{
			foreach (var kv in add)
				holder.contentList.Remove(kv.Key);
			MLog.WarnOnce("[Body Fit] refitted pictures cannot be registered (another mod caches textures) – refitting is off.", 0x0b0d2f02);
			return null;
		}
		return fake;
	}

	// For every row v of the new body: where to read in the old picture.
	// 2.2: piecewise – only the part between the body edges is stretched; what
	// lies outside the body (capes, sleeves, loose cloth, hair) is only moved
	// with the nearest edge and keeps its own size. No more "wings".
	private sealed class RowMap
	{
		public int n;
		public float[] srcV, tl, tr, fl, fr;

		public static RowMap Make(Profile from, Profile to, bool mirrorFrom, bool mirrorTo, bool height, bool core, bool head)
		{
			if (from == null || to == null)
				return null;
			int n = 256;
			var m = new RowMap { n = n, srcV = new float[n], tl = new float[n], tr = new float[n], fl = new float[n], fr = new float[n] };
			float vs = 1f, fb = from.bottom, tb = to.bottom;
			// 2.3.5: heads are fitted in height too (a smaller or higher face moves the mask / helmet with it)
			if (head && !BodyFitMod.Simple)
				height = true;   // 2.5: the simple Body Fit fits hats and masks only sideways
			if (height)
			{
				float fh = from.top - from.bottom, th = to.top - to.bottom;
				if (fh > 0.05f && th > 0.05f)
					vs = head ? Mathf.Clamp(fh / th, 0.85f, 1.2f) : (Shots.Loose ? Mathf.Clamp(fh / th, 0.7f, 1.35f) : Mathf.Clamp(fh / th, 0.88f, 1.14f));
				// 2.3.6: heads – centres aligned (a mask follows the face up or down) without squashing it flat
				if (head)
				{
					fb = (from.top + from.bottom) * 0.5f;
					tb = (to.top + to.bottom) * 0.5f;
				}
				else if (Mathf.Abs(vs - 1f) < 0.02f)
				{
					vs = 1f;
					fb = tb = 0f;
				}
			}
			bool[] ok = new bool[n];
			var ratios = new List<float>();
			for (int i = 0; i < n; i++)
			{
				float v = (i + 0.5f) / n;
				float sv = height ? fb + (v - tb) * vs : v;
				m.srcV[i] = Mathf.Clamp01(sv);
				if (Edges(to, v, core, out float a0, out float a1) && Edges(from, m.srcV[i], core, out float b0, out float b1))
				{
					if (mirrorTo) { float t = 1f - a1; a1 = 1f - a0; a0 = t; }
					if (mirrorFrom) { float t = 1f - b1; b1 = 1f - b0; b0 = t; }
					float tw = a1 - a0, fw = b1 - b0;
					if (tw > 0.02f && fw > 0.02f)
					{
						m.tl[i] = a0; m.tr[i] = a1; m.fl[i] = b0; m.fr[i] = b1;
						ok[i] = true;
						ratios.Add(fw / tw);
					}
				}
			}
			if (ratios.Count < 8)
				return null;
			// bodies too different (a centaur, a blob, a mis-measured picture):
			// keep the original picture – the simple fitting still moves and scales it
			ratios.Sort();
			float med = ratios[ratios.Count / 2];
			if (Shots.Loose ? (med < 0.4f || med > 2.4f) : (med < 0.6f || med > 1.65f))
				return null;
			// rows without body (below the feet, above the neck, gaps between legs)
			// take the nearest measured row: long coats stay long, nothing tears
			int last = -1;
			for (int i = 0; i < n; i++)
			{
				if (ok[i]) { last = i; continue; }
				int next = -1;
				for (int k = i + 1; k < n; k++) if (ok[k]) { next = k; break; }
				int src = last < 0 ? next : next < 0 ? last : (i - last <= next - i ? last : next);
				m.tl[i] = m.tl[src]; m.tr[i] = m.tr[src]; m.fl[i] = m.fl[src]; m.fr[i] = m.fr[src];
			}
			// smooth strongly – fingers, hands and single loose pixels must not make zig-zags
			int r = head ? 10 : 8;
			m.tl = Smooth(m.tl, r); m.tr = Smooth(m.tr, r); m.fl = Smooth(m.fl, r); m.fr = Smooth(m.fr, r);
			// limit the stretch of every row (the old body may be measured wrong in places)
			float lo = head ? 0.85f : 0.72f, hi = head ? 1.18f : 1.4f;
			if (Shots.Loose) { lo = head ? 0.6f : 0.45f; hi = head ? 1.5f : 2.0f; }   // test: chibi bodies need much more
			bool differs = vs != 1f;
			for (int i = 0; i < n; i++)
			{
				float tw = m.tr[i] - m.tl[i], fw = m.fr[i] - m.fl[i];
				float k = Mathf.Clamp(fw / tw, lo, hi);
				float fc = (m.fl[i] + m.fr[i]) * 0.5f;
				m.fl[i] = fc - tw * k * 0.5f;
				m.fr[i] = fc + tw * k * 0.5f;
				if (Mathf.Abs(m.tl[i] - m.fl[i]) > 0.004f || Mathf.Abs(m.tr[i] - m.fr[i]) > 0.004f)
					differs = true;
			}
			return differs ? m : null;
		}

		public static string Diag(Profile from, Profile to)
		{
			if (from == null || to == null) return "noprofile";
			return "fromTB=" + from.top.ToString("0.00") + "/" + from.bottom.ToString("0.00") + " toTB=" + to.top.ToString("0.00") + "/" + to.bottom.ToString("0.00");
		}

		// where in the old row to read for position u of the new row
		public float Src(int i, float u)
		{
			float a0 = tl[i], a1 = tr[i], b0 = fl[i], b1 = fr[i];
			if (u <= a0) return b0 - (a0 - u);   // outside on the left: moved only
			if (u >= a1) return b1 + (u - a1);   // outside on the right: moved only
			return b0 + (u - a0) * (b1 - b0) / (a1 - a0);
		}

		private static float[] Smooth(float[] a, int r)
		{
			float[] o = new float[a.Length];
			for (int i = 0; i < a.Length; i++)
			{
				float sum = 0f;
				int c = 0;
				for (int k = Math.Max(0, i - r); k <= Math.Min(a.Length - 1, i + r); k++) { sum += a[k]; c++; }
				o[i] = sum / c;
			}
			return o;
		}
	}

	private static Texture2D Warp(Texture2D src, RowMap m) => Warp(src, m, out _);

	private static Texture2D Warp(Texture2D src, RowMap m, out Color32[] result)
	{
		result = null;
		Color32[] px = Read(src, out int w, out int h);
		if (px == null)
			return null;
		var outPx = new Color32[w * h];
		for (int y = 0; y < h; y++)
		{
			float v = (y + 0.5f) / h;
			int i = Mathf.Clamp(Mathf.FloorToInt(v * m.n), 0, m.n - 1);
			float sy = m.srcV[i] * h - 0.5f;
			for (int x = 0; x < w; x++)
			{
				float su = m.Src(i, (x + 0.5f) / w);
				outPx[y * w + x] = Sample(px, w, h, su * w - 0.5f, sy);
			}
		}
		var tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
		tex.name = "BodyFitRefit_" + src.name;
		tex.filterMode = src.filterMode;
		tex.wrapMode = TextureWrapMode.Clamp;
		tex.anisoLevel = src.anisoLevel;
		tex.SetPixels32(outPx);
		result = outPx;
		tex.Apply(true, true);
		return tex;
	}

	private static Color32 Sample(Color32[] px, int w, int h, float x, float y)
	{
		if (x < -1f || x > w || y < -1f || y > h)
			return new Color32(0, 0, 0, 0);
		int x0 = Mathf.FloorToInt(x), y0 = Mathf.FloorToInt(y);
		float fx = x - x0, fy = y - y0;
		Color32 a = Px(px, w, h, x0, y0), b = Px(px, w, h, x0 + 1, y0), c = Px(px, w, h, x0, y0 + 1), d = Px(px, w, h, x0 + 1, y0 + 1);
		float wa = (1 - fx) * (1 - fy) * a.a, wb = fx * (1 - fy) * b.a, wc = (1 - fx) * fy * c.a, wd = fx * fy * d.a;
		float alpha = wa + wb + wc + wd;   // alpha-weighted: no dark fringes
		if (alpha <= 0.001f)
			return new Color32(0, 0, 0, 0);
		return new Color32(
			(byte)Mathf.Clamp((a.r * wa + b.r * wb + c.r * wc + d.r * wd) / alpha, 0, 255),
			(byte)Mathf.Clamp((a.g * wa + b.g * wb + c.g * wc + d.g * wd) / alpha, 0, 255),
			(byte)Mathf.Clamp((a.b * wa + b.b * wb + c.b * wc + d.b * wd) / alpha, 0, 255),
			(byte)Mathf.Clamp(alpha, 0, 255));
	}

	private static Color32 Px(Color32[] px, int w, int h, int x, int y)
	{
		if (x < 0 || y < 0 || x >= w || y >= h)
			return new Color32(0, 0, 0, 0);
		return px[y * w + x];
	}

	// ---- which mod a picture comes from -------------------------------------
	public static ModContentPack Provider(string path)
	{
		List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;
		for (int i = mods.Count - 1; i >= 0; i--)
			if (mods[i].GetContentHolder<Texture2D>().Get(path) != null)
				return mods[i];
		return null;   // game files
	}
}

// Runs after HAR, Sized Apparel and the rest: swaps in the refitted picture.
[HarmonyPatch(typeof(ApparelGraphicRecordGetter), nameof(ApparelGraphicRecordGetter.TryGetGraphicApparel))]
public static class Patch_Refit
{
	[HarmonyPriority(Priority.Last)]
	public static void Postfix(Apparel apparel, BodyTypeDef bodyType, bool forStatue, ref ApparelGraphicRecord rec, ref bool __result)
	{
		Inner(apparel, forStatue, ref rec, ref __result);
		// 2.4 (testing): a piece fully covered by a piece worn over it is cut to that piece's outline
		try
		{
			if (!__result || forStatue || !LayerClip.Active || apparel?.Wearer == null || !BF.Main || !BF.Handles(apparel.Wearer))
				return;
			if (!(rec.graphic is Graphic_Multi g) || g.path == null || g.path.StartsWith(LayerClip.Prefix))
				return;
			Apparel outer = LayerClip.Outer(apparel.Wearer, apparel);
			if (outer == null)
				return;
			string op = LayerClip.OuterPath(outer, bodyType);
			if (op.NullOrEmpty())
				return;
			string fake = LayerClip.Request(g.path, op, out bool ready, apparel.Wearer);
			Shots.Why(apparel, "layer under " + outer.def.defName + (ready ? (fake != null ? " cut" : " nothing to cut") : " waiting"));
			if (!ready || fake == null)
				return;
			rec = new ApparelGraphicRecord(GraphicDatabase.Get<Graphic_Multi>(fake, g.Shader, g.drawSize, g.color, g.colorTwo, g.data, null), apparel);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] layers: " + ex.Message, 0x0b0d2f05);
		}
	}

	private static void Inner(Apparel apparel, bool forStatue, ref ApparelGraphicRecord rec, ref bool __result)
	{
		BodyFitSettings s = BodyFitMod.Settings;
		if (!__result || apparel == null || Refit.Suspended || (apparel.Wearer != null && !BF.Handles(apparel.Wearer)))
			return;
		// 2.1: your own setting for this piece of clothing
		ItemTweak tw = s.Tweak(apparel.def);
		if (tw != null && tw.hide && apparel.Wearer != null && !forStatue)
		{
			__result = false;
			rec = new ApparelGraphicRecord(null, null);
			return;
		}
		if ((!s.refit && !s.refitAliens) || forStatue || !BF.Main || (tw != null && tw.noRefit))
			return;
		try
		{
			Pawn p = apparel.Wearer;
			if (p?.story?.bodyType == null || !p.RaceProps.Humanlike || p.IsMutant)
				{ Shots.Why(apparel, "644"); return; }
			// 2.2: humans and body mods on by default, alien races only when asked for
			if (Bodies.IsHar(p.def) ? !s.refitAliens : !s.refit)
				{ Shots.Why(apparel, "647"); return; }
			if (s.noFitRaces.Contains(p.def.defName))
				{ Shots.Why(apparel, "649"); return; }
			// test 2.3.5: clothes of another race keep that race's picture size (1.4, 1.25 …) – on this wearer back to 1
			if (Shots.Level >= 2 && rec.graphic is Graphic_Multi g0 && g0.drawSize != Vector2.one && !Refit.IsRefit(g0))
			{
				ThingDef h0 = Bodies.HomeRace(apparel.def);
				if (h0 != null && h0 != p.def)
				{
					rec = new ApparelGraphicRecord(GraphicDatabase.Get<Graphic_Multi>(g0.path, g0.Shader, Vector2.one, g0.color, g0.colorTwo, g0.data, null), apparel);
					Shots.Why(apparel, "foreign size " + g0.drawSize.x.ToString("0.##") + " -> 1");
				}
			}
			if (!(rec.graphic is Graphic_Multi g) || g.MatSouth == BaseContent.BadMat || Refit.IsRefit(g))
				{ Shots.Why(apparel, "651"); return; }
			string worn = apparel.WornGraphicPath;
			if (worn.NullOrEmpty() || g.path == null)
				{ Shots.Why(apparel, "654"); return; }
			ThingDef home = Bodies.HomeRace(apparel.def);
			if (home != null && home == p.def)
				{ Shots.Why(apparel, "657 the race's own clothes"); return; }   // the race's own clothes
			Refit.Job job;
			if (g.path == worn)
			{
				// 2.1: hats, helmets, masks – fitted to the head really drawn
				if (!s.refitHeads)
					{ Shots.Why(apparel, "663"); return; }
				ApparelLayerDef last = apparel.def.apparel.LastLayer;
				if (last != ApparelLayerDefOf.Overhead && last != ApparelLayerDefOf.EyeCover)
					{ Shots.Why(apparel, "666 packs and other single pictures"); return; }   // packs and other single pictures
				job = HeadRefit.MakeJob(p, g.path, false, home);
				if (job == null)
					{ Shots.Why(apparel, "669"); return; }
			}
			else
			{
				if (!g.path.StartsWith(worn + "_"))
					{ Shots.Why(apparel, "674 race pictures, Sized Apparel …"); return; }   // race pictures, Sized Apparel …
				BodyTypeDef madeFor = DefDatabase<BodyTypeDef>.GetNamedSilentFail(g.path.Substring(worn.Length + 1));
				if (madeFor == null)
					{ Shots.Why(apparel, "677"); return; }
				BodyTypeDef body = p.story.bodyType;
				// the body really drawn (2.1: or the one you chose for this race)
				if (s.raceBody.TryGetValue(p.def.defName, out string own) && !own.NullOrEmpty())
					body = DefDatabase<BodyTypeDef>.GetNamedSilentFail(own) ?? body;
				bool wasNotReady = BF.NotReady;
				BF.NotReady = false;
				string target = Bodies.IsHar(p.def) ? Bodies.RaceBodyPath(p.def, body) : body.bodyNakedGraphicPath;
				bool guessed = BF.NotReady;
				BF.NotReady = wasNotReady || guessed;
				if (guessed)
					{ Shots.Why(apparel, "688 pictures not checked yet – next time"); return; }   // pictures not checked yet – next time
				if (target.NullOrEmpty())
					target = body.bodyNakedGraphicPath;   // the race uses the game's bodies
				if (target.NullOrEmpty())
					{ Shots.Why(apparel, "692"); return; }
				// the body the picture was painted on
				string from;
				bool vanilla;
				if (home != null)
				{
					bool wnr = BF.NotReady;
					BF.NotReady = false;
					from = Bodies.RaceBodyPath(home, madeFor);
					bool g2 = BF.NotReady;
					BF.NotReady = wnr || g2;
					if (g2)
						{ Shots.Why(apparel, "704"); return; }
					vanilla = false;
					if (from.NullOrEmpty())
					{
						from = madeFor.bodyNakedGraphicPath;
						vanilla = madeFor.modContentPack != null && madeFor.modContentPack.IsOfficialMod;
					}
				}
				else
				{
					from = madeFor.bodyNakedGraphicPath;
					vanilla = madeFor.modContentPack != null && madeFor.modContentPack.IsOfficialMod;
				}
				if (from.NullOrEmpty())
					{ Shots.Why(apparel, "718"); return; }
				// 2.2.1: an alien race's own body is refitted to only when it really is a
				// human-like body; tentacles, tails, chibi bodies … keep the race's own look
				if (AlienFit.OwnBody(p, body, target))
				{
					bool wnr3 = BF.NotReady;
					BF.NotReady = false;
					AlienFit.Trust tr = AlienFit.Of(target, from);
					bool g3 = BF.NotReady;
					BF.NotReady = wnr3 || g3;
					if (g3 || (tr == AlienFit.Trust.None && !Shots.Loose))
						{ Shots.Why(apparel, "729 not measured yet (asked again when the o"); return; }   // not measured yet (asked again when the outline is read) or not safe
				}
				// a body mod that painted this piece of clothing for its own bodies: already fits
				ModContentPack picMod = Refit.Provider(g.path + "_south") ?? Refit.Provider(g.path + "_north");
				ModContentPack bodyMod = Refit.Provider(target + "_south");
				if (picMod != null && bodyMod != null && picMod == bodyMod && !picMod.IsOfficialMod)
					{ Shots.Why(apparel, "735"); return; }
				// 2.1: clothes without sleeves (armour plates, vests, trousers) follow the trunk, not the arms
				bool core = s.refitCore && !CoversArms(apparel.def);
				job = new Refit.Job
				{
					key = g.path + "|" + from + (vanilla ? "|v|" : "|m|") + target + (core ? "|c" : ""),
					apparelPath = g.path,
					refPath = from,
					refVanilla = vanilla,
					targetPath = target,
					core = core
				};
			}
			string fake = Refit.Request(job, out bool ready, apparel.Wearer);
			if (!ready || fake == null)
				{ Shots.Why(apparel, ready ? "750 refit made nothing (outline did not match)" : "750 still waiting"); return; }
			Graphic ng = GraphicDatabase.Get<Graphic_Multi>(fake, g.Shader, g.drawSize, g.color, g.colorTwo, g.data, null);
			rec = new ApparelGraphicRecord(ng, apparel);
			Shots.Why(apparel, "OK refitted");
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Body Fit] refit: " + ex.Message, 0x0b0d2f01);
		}
	}

	private static readonly Dictionary<ThingDef, bool> arms = new Dictionary<ThingDef, bool>();

	public static bool CoversArms(ThingDef d)
	{
		lock (BF.Gate)
			if (arms.TryGetValue(d, out bool v))
				return v;
		bool r = false;
		List<BodyPartGroupDef> groups = d.apparel?.bodyPartGroups;
		if (groups != null)
			foreach (BodyPartGroupDef b in groups)
			{
				string n = b.defName;
				if (n == "Arms" || n == "Shoulders" || n == "Hands" || n == "FullBody" || n.Contains("Arm") || n.Contains("Shoulder"))
				{
					r = true;
					break;
				}
			}
		if (groups == null || groups.Count == 0)
			r = true;   // unknown – measure the whole body as before
		lock (BF.Gate)
			arms[d] = r;
		return r;
	}
}

// ---------------------------------------------------------------------------
// 2.1: hair, beards and headgear follow the head really drawn. They are
// painted for the human head; an alien head (wider, narrower, off-centre) gets
// every row of the picture moved and stretched onto its own outline.
// Human heads are left exactly as the game draws them.
// ---------------------------------------------------------------------------
public static class HeadRefit
{
	public static Refit.Job MakeJob(Pawn p, string picPath, bool hair = false, ThingDef home = null)
	{
		string head = HairFix.DrawnHead(p);
		if (head.NullOrEmpty())
			return null;
		// 2.3.6: a hat / mask made for another race is fitted from that race's head
		string homeHead = home != null && home != p.def ? Bodies.RaceHead(home, p.gender) : null;
		if (!homeHead.NullOrEmpty() && homeHead != head)
			return new Refit.Job { key = picPath + "|hh|" + homeHead + "|" + head, apparelPath = picPath, refPath = homeHead, refVanilla = false, targetPath = head, head = true };
		// 2.3.5: a game head whose pictures a mod replaced (bigger chibi heads …) – hats and helmets are
		// fitted from the game's own head onto it; hair of such heads usually comes with the head mod
		if (head.StartsWith("Things/Pawn/Humanlike/Heads/"))
		{
			ModContentPack m = Refit.Provider(head + "_south");
			if (hair || m == null || m.IsOfficialMod)
				return null;
		}
		string refHead = HairFix.ReferenceHead(p.gender);
		if (refHead.NullOrEmpty())
			return null;
		return new Refit.Job
		{
			key = picPath + "|h|" + refHead + "|" + head,
			apparelPath = picPath,
			refPath = refHead,
			refVanilla = true,
			targetPath = head,
			head = true
		};
	}

	public static Graphic Swap(Pawn pawn, Graphic result)
	{
		BodyFitSettings s = BodyFitMod.Settings;
		if (!s.refitAliens || !s.refitHeads || Refit.Suspended || !BF.Main || pawn?.story == null)
			return result;
		if (!(result is Graphic_Multi g) || g.path.NullOrEmpty() || Refit.IsRefit(g) || g.MatSouth == BaseContent.BadMat)
			return result;
		if (s.noFitRaces.Contains(pawn.def.defName))
			return result;
		Refit.Job job = MakeJob(pawn, g.path, true);
		if (job == null)
			return result;
		string fake = Refit.Request(job, out bool ready, pawn);
		if (!ready || fake == null)
			return result;
		return GraphicDatabase.Get<Graphic_Multi>(fake, g.Shader, g.drawSize, g.color, g.colorTwo, g.data, null);
	}
}

[HarmonyPatch(typeof(PawnRenderNode_Hair), nameof(PawnRenderNode_Hair.GraphicFor))]
public static class Patch_HairRefit
{
	[HarmonyPriority(Priority.Last)]
	public static void Postfix(Pawn pawn, ref Graphic __result)
	{
		if (!BF.Handles(pawn)) return;
		try { __result = HeadRefit.Swap(pawn, __result); } catch { }
	}
}

[HarmonyPatch(typeof(PawnRenderNode_Beard), nameof(PawnRenderNode_Beard.GraphicFor))]
public static class Patch_BeardRefit
{
	[HarmonyPriority(Priority.Last)]
	public static void Postfix(Pawn pawn, ref Graphic __result)
	{
		if (!BF.Handles(pawn)) return;
		try { __result = HeadRefit.Swap(pawn, __result); } catch { }
	}
}

// ---------------------------------------------------------------------------
// 2.1: your own setting per piece of clothing (settings, tab Clothes):
// move, resize, hide, no refitting. Applies to every pawn in every save.
// ---------------------------------------------------------------------------
public class ItemTweak : IExposable
{
	public bool hide, noRefit;
	public Vector2 offset = Vector2.zero;   // x sideways, y up
	public Vector2 scale = Vector2.one;

	public bool IsDefault => !hide && !noRefit && offset == Vector2.zero && scale == Vector2.one;

	public void ExposeData()
	{
		Scribe_Values.Look(ref hide, "hide", false);
		Scribe_Values.Look(ref noRefit, "noRefit", false);
		Scribe_Values.Look(ref offset, "offset", Vector2.zero);
		Scribe_Values.Look(ref scale, "scale", Vector2.one);
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.ScaleFor))]
public static class Patch_TweakScale
{
	public static void Postfix(PawnRenderNode node, ref Vector3 __result)
	{
		if (Refit.Suspended || !(node is PawnRenderNode_Apparel a) || a.apparel == null || !BF.Handles(node.tree?.pawn))
			return;
		ItemTweak t = BodyFitMod.Settings.Tweak(a.apparel.def);
		if (t == null || t.scale == Vector2.one)
			return;
		__result.x *= t.scale.x;
		__result.z *= t.scale.y;
	}
}

[HarmonyPatch(typeof(PawnRenderNodeWorker), nameof(PawnRenderNodeWorker.OffsetFor))]
public static class Patch_TweakOffset
{
	public static void Postfix(PawnRenderNode node, PawnDrawParms parms, ref Vector3 __result)
	{
		if (Refit.Suspended || !(node is PawnRenderNode_Apparel a) || a.apparel == null || !BF.Handles(node.tree?.pawn))
			return;
		ItemTweak t = BodyFitMod.Settings.Tweak(a.apparel.def);
		if (t == null || t.offset == Vector2.zero)
			return;
		float x = t.offset.x;
		if (parms.facing == Rot4.West)
			x = -x;
		__result.x += x;
		__result.z += t.offset.y;
	}
}
