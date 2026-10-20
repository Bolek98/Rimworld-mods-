using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// 1.2.3: thread safety. In RimWorld 1.6 pawn offsets and scales are computed
// on worker threads (PawnRenderTree.ParallelPreDraw), and games are loaded in
// a background long event. Textures may only be loaded on the main thread.
//   * every shared cache is guarded by one lock (Gate),
//   * a picture that is not known yet is never loaded off the main thread:
//     the request is queued, the caller gets "not ready" (nothing is cached),
//     and the main thread loads it on the next frame,
//   * NotReady tells the caller not to remember a result built on a guess.
// ---------------------------------------------------------------------------
public static class BF
{
	public static readonly object Gate = new object();

	[ThreadStatic] public static bool NotReady;

	// 1.2.4: reading a picture's outline goes through the GPU (Blit/ReadPixels).
	// Doing that while the game renders a pawn (pawn cache camera, OnPostRender)
	// breaks that render – black squares, garbage outlines. Outlines are therefore
	// only read here, in Update, or from the settings/report.
	[ThreadStatic] public static bool TraceAllowed;

	public static bool Main => UnityData.IsInMainThread;

	// 2.3.0: only the human race is changed by default – alien races (HAR and others) are drawn exactly
	// as their mod draws them, unless you switch a race on in the Races tab
	public static bool Handles(Pawn p) => p != null && Handles(p.def);

	public static bool Handles(ThingDef race)
	{
		if (race == null)
			return false;
		if (race == ThingDefOf.Human || Shots.Experiment)
			return true;
		BodyFitSettings s = BodyFitMod.Settings;
		return s != null && (s.offRaces == null || !s.offRaces.Contains(race.defName));
	}

	private static readonly HashSet<string> pendingTex = new HashSet<string>();
	private static readonly HashSet<(string, int)> pendingOutline = new HashSet<(string, int)>();

	public static void QueueTexture(string path)
	{
		lock (Gate)
			pendingTex.Add(path);
		NotReady = true;
	}

	public static void QueueOutline(string basePath, int rot)
	{
		lock (Gate)
			pendingOutline.Add((basePath, rot));
		NotReady = true;
	}

	// main thread, every frame
	public static int PendingCount { get { lock (Gate) return pendingTex.Count + pendingOutline.Count; } }

	public static void ProcessPending()
	{
		if (!Main)
			return;
		string[] tex;
		(string, int)[] outl;
		lock (Gate)
		{
			if (pendingTex.Count == 0 && pendingOutline.Count == 0)
				return;
			tex = new string[pendingTex.Count];
			pendingTex.CopyTo(tex);
			pendingTex.Clear();
			outl = new (string, int)[pendingOutline.Count];
			pendingOutline.CopyTo(outl);
			pendingOutline.Clear();
		}
		for (int i = 0; i < tex.Length; i++)
			Bodies.TextureExists(tex[i]);
		if (outl.Length == 0)
			return;
		// 2.2.4: at most ~6 ms of outlines per frame, the rest next frame
		TraceAllowed = true;
		var sw = System.Diagnostics.Stopwatch.StartNew();
		int doneCount = 0;
		try
		{
			for (int i = 0; i < outl.Length; i++)
			{
				Silhouette.Of(outl[i].Item1, new Rot4(outl[i].Item2));
				doneCount++;
				if (sw.Elapsed.TotalMilliseconds > 6.0)
					break;
			}
		}
		finally
		{
			TraceAllowed = false;
		}
		if (doneCount < outl.Length)
		{
			lock (Gate)
				for (int i = doneCount; i < outl.Length; i++)
					pendingOutline.Add(outl[i]);
			System.Array.Resize(ref outl, doneCount);
			if (outl.Length == 0)
				return;
		}
		// redraw only the pawns whose own head or body outline was just read
		// (portraits and cached frames too) – cheap, no full-colony redraw
		try
		{
			var done = new HashSet<string>();
			for (int i = 0; i < outl.Length; i++)
				done.Add(outl[i].Item1);
			if (Find.Maps != null)
				foreach (Map m in Find.Maps)
					foreach (Pawn p in m.mapPawns.AllPawnsSpawned)
					{
						if (!p.RaceProps.Humanlike)
							continue;
						string head = HairFix.DrawnHead(p);
						string body = Patch_FitScale.WearerBodyPath(p, p.story?.bodyType);
						if ((head != null && done.Contains(head)) || (body != null && done.Contains(body)))
						{
							PortraitsCache.SetDirty(p);
							// 2.2.1: clothes pictures are chosen again now that the body is measured
							if (body != null && done.Contains(body) && !Refit.Busy(p))
								p.Drawer?.renderer?.SetAllGraphicsDirty();
						}
					}
		}
		catch
		{
		}
	}

	public static void ClearPending()
	{
		lock (Gate)
		{
			pendingTex.Clear();
			pendingOutline.Clear();
		}
	}
}
