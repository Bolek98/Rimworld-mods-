using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.3.2/2.3.3 (testing only): pictures of every humanlike pawn on the maps, drawn by the game itself,
// so the fit of clothes, hair and hats on each race can be checked exactly as it looks in play.
// One PNG per pawn, columns = N, E, S, W; rows:
//   1 = as Body Fit draws it now, 2 = alien fitting on (safe), 3 = alien fitting on (everything), 4 = naked.
public static class Shots
{
	// while true: Body Fit treats every race as switched on (testing only, never saved)
	public static bool Experiment;
	// while true: alien bodies are fitted even when they do not look like a scaled human body
	public static bool Loose;

	private static readonly Dictionary<string, int> why = new Dictionary<string, int>();

	public static void Why(Apparel a, string reason)
	{
		if (!Experiment || state != 2)
			return;
		Pawn p = a?.Wearer;
		WhyKey((p?.def?.defName ?? "?") + "\t" + (a?.def?.defName ?? "?") + "\t" + reason);
	}

	public static void WhyKey(string k)
	{
		if (!Experiment || state != 2)
			return;
		k = "pass" + pass + "\t" + k;
		why.TryGetValue(k, out int n);
		why[k] = n + 1;
	}

	private const int S = 256;
	private const int Rows = 4;

	private static int state;          // 0 off, 1 waiting, 2 warming a pass, 3 done
	private static int startFrame;
	private static int pass;           // 1 = safe, 2 = everything
	private static float passStart;
	private static int quietChecks;
	private static List<Pawn> pawns;
	private static Dictionary<Pawn, Texture2D> sheets;
	private static RenderTexture rt;
	private static Texture2D read;
	private static bool savedAliens, savedHeads;

	public static string Folder => Path.Combine(GenFilePaths.SaveDataFolderPath, "Zalohy_modu", "BodyFit_shots");

	// 2.3.8: pictures only when asked for (file Zalohy_modu\BodyFit_shots\run.txt) – the test passes make many
	// extra textures, which cost memory in a normal game. The file is removed after the pictures are taken.
	public static bool Requested;

	public static string Trigger => Path.Combine(Folder, "run.txt");

	public static void Arm()
	{
		state = 0;
		Requested = false;
		try { if (!File.Exists(Trigger)) return; File.Delete(Trigger); } catch { return; }
		Requested = true;
		state = 1;
		startFrame = Time.frameCount + 300;
	}

	public static void Update()
	{
		if (state == 0 || state == 3 || Find.Maps == null || Find.CurrentMap == null)
			return;
		try
		{
			Step();
		}
		catch (Exception ex)
		{
			MLog.Warn("[Body Fit] pictures: " + ex);
			Finish(false);
		}
	}

	private static void Step()
	{
		if (state == 1)
		{
			if (Time.frameCount < startFrame)
				return;
			pawns = new List<Pawn>();
			foreach (Map m in Find.Maps)
				pawns.AddRange(m.mapPawns.AllPawnsSpawned.Where(x => x.RaceProps.Humanlike && x.story != null && x.Drawer?.renderer?.renderTree != null));
			pawns = pawns.OrderBy(x => x.def.defName).Take(60).ToList();
			rt = new RenderTexture(S, S, 24, RenderTextureFormat.ARGB32);
			read = new Texture2D(S, S, TextureFormat.ARGB32, false);
			sheets = new Dictionary<Pawn, Texture2D>();
			var clear = new Color32[S * 4 * S * Rows];
			foreach (Pawn p in pawns)
			{
				var sh = new Texture2D(S * 4, S * Rows, TextureFormat.ARGB32, false);
				sh.SetPixels32(clear);
				sheets[p] = sh;
			}
			// row 1: as now, row 4: naked
			foreach (Pawn p in pawns)
			{
				Draw(p, 0, true);
				Draw(p, 3, false);
			}
			why.Clear();
			savedAliens = BodyFitMod.Settings.refitAliens;
			savedHeads = BodyFitMod.Settings.refitHeads;
			StartPass(1);
			return;
		}
		if (state == 2)
		{
			if (Time.frameCount % 30 != 0)
				return;
			// draw everything again so every picture it needs gets asked for, then wait for the queues
			foreach (Pawn p in pawns)
				Draw(p, -1, true);
			bool quiet = BF.PendingCount == 0 && Refit.PendingCount == 0 && LayerClip.PendingCount == 0;
			quietChecks = quiet ? quietChecks + 1 : 0;
			if (quietChecks < 3 && Time.realtimeSinceStartup - passStart < 45f)
				return;
			foreach (Pawn p in pawns)
				Draw(p, pass, true);
			if (pass == 1)
				StartPass(2);
			else
				Finish(true);
		}
	}

	private static void StartPass(int n)
	{
		pass = n;
		Experiment = true;
		Loose = n == 2;
		BodyFitMod.Settings.refitAliens = true;
		BodyFitMod.Settings.refitHeads = true;
		AlienFit.Clear();
		if (n == 2)
		{
			HeadSeat.Clear();
			foreach (Pawn p in pawns.Where(x => x.def == ThingDefOf.Human).Concat(pawns.Where(x => x.def != ThingDefOf.Human)))
				try { HeadSeat.Learn(p); } catch (Exception ex) { HeadSeat.Notes.Add("error " + p.LabelShort + ": " + ex.Message); }
		}
		passStart = Time.realtimeSinceStartup;
		quietChecks = 0;
		state = 2;
	}

	private static void Finish(bool write)
	{
		Experiment = false;
		Loose = false;
		if (state == 2)
		{
			BodyFitMod.Settings.refitAliens = savedAliens;
			BodyFitMod.Settings.refitHeads = savedHeads;
		}
		AlienFit.Clear();
		state = 3;
		int n = 0;
		try
		{
			if (write && sheets != null)
			{
				Directory.CreateDirectory(Folder);
				foreach (string f in Directory.GetFiles(Folder, "*.png"))
				{
					try { File.Delete(f); } catch { }
				}
				var seen = new HashSet<string>();
				foreach (Pawn p in pawns)
				{
					Texture2D sh = sheets[p];
					sh.Apply();
					string name = p.def.defName + "_" + GenText.SanitizeFilename(p.LabelShort);
					if (!seen.Add(name))
						name += "_" + p.thingIDNumber;
					File.WriteAllBytes(Path.Combine(Folder, name + ".png"), sh.EncodeToPNG());
					n++;
				}
				File.WriteAllLines(Path.Combine(Folder, "headfix.txt"), HeadSeat.Notes);
				File.WriteAllLines(Path.Combine(Folder, "why.txt"), why.OrderBy(kv => kv.Key).Select(kv => kv.Key + "\t" + kv.Value));
				MLog.Info("[Body Fit] " + n + " pawn pictures written: " + Folder);
			}
		}
		finally
		{
			if (sheets != null)
				foreach (Texture2D t in sheets.Values)
					UnityEngine.Object.Destroy(t);
			sheets = null;
			if (rt != null) { rt.Release(); UnityEngine.Object.Destroy(rt); rt = null; }
			if (read != null) { UnityEngine.Object.Destroy(read); read = null; }
			if (pawns != null)
				foreach (Pawn p in pawns)
				{
					p.Drawer?.renderer?.renderTree?.SetDirty();
					PortraitsCache.SetDirty(p);
				}
			pawns = null;
		}
	}

	// row -1: only draw (to ask for pictures), nothing kept
	private static void Draw(Pawn p, int row, bool clothes)
	{
		if (p.Destroyed || p.Drawer?.renderer?.renderTree == null)
			return;
		p.Drawer.renderer.renderTree.SetDirty();
		for (int r = 0; r < 4; r++)
		{
			RenderTexture prev = RenderTexture.active;
			RenderTexture.active = rt;
			GL.Clear(true, true, new Color(0f, 0f, 0f, 0f));
			RenderTexture.active = prev;
			Find.PawnCacheRenderer.RenderPawn(p, rt, new Vector3(0f, 0f, 0.1f), 1.05f, 0f, new Rot4(r), true, clothes, clothes, false);
			if (row < 0)
				continue;
			RenderTexture.active = rt;
			read.ReadPixels(new Rect(0, 0, S, S), 0, 0);
			read.Apply();
			RenderTexture.active = prev;
			// Texture2D y goes up: row 0 sits at the top
			sheets[p].SetPixels(r * S, (Rows - 1 - row) * S, S, S, read.GetPixels());
		}
	}
}
