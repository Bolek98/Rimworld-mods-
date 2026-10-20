using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using Verse;

namespace BodyFit;

// 2.5 (performance): refitted pictures are kept on disk. Fitting a piece of clothing to a body
// reads pictures back from the graphics card and reshapes them – up to half a second when many
// new clothes appear at once. The result is the same every time for the same clothes, bodies and
// settings, so it is saved (Zalohy-free: <save data>\BodyFit_cache) and the next game start only
// loads it. The cache empties itself when the mod list or any mod's version changes.
public static class RefitCache
{
	private const string Format = "bf25";
	private static string dir;
	private static bool ready, broken;
	public static int Hits, Misses, Saved;

	private static void Init()
	{
		if (ready || broken)
			return;
		ready = true;
		try
		{
			dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "BodyFit_cache");
			Directory.CreateDirectory(dir);
			// the mod list with each mod's About.xml time: an updated mod empties the cache
			var sb = new StringBuilder(Format);
			foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
			{
				sb.Append('|').Append(m.PackageId);
				try { sb.Append(':').Append(File.GetLastWriteTimeUtc(Path.Combine(m.RootDir, "About", "About.xml")).Ticks); } catch { }
			}
			string sig = Hash(sb.ToString());
			string sigFile = Path.Combine(dir, "signature.txt");
			string old = File.Exists(sigFile) ? File.ReadAllText(sigFile).Trim() : null;
			if (old != sig)
			{
				int n = 0;
				foreach (string f in Directory.GetFiles(dir, "*.bfc"))
				{
					try { File.Delete(f); n++; } catch { }
				}
				File.WriteAllText(sigFile, sig);
				if (n > 0)
					MLog.Info("[Body Fit] mods changed – " + n + " saved fitted pictures removed, they are made again.");
			}
		}
		catch (Exception ex)
		{
			broken = true;
			MLog.WarnOnce("[Body Fit] picture cache off: " + ex.Message, 0x0b0dcac1);
		}
	}

	private static string Hash(string s)
	{
		using (var sha = SHA1.Create())
		{
			byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
			var sb = new StringBuilder(40);
			foreach (byte x in b) sb.Append(x.ToString("x2"));
			return sb.ToString();
		}
	}

	private static string FileFor(string key, Texture2D src) => Path.Combine(dir, Hash(key + "|" + src.name + "|" + src.width + "x" + src.height) + ".bfc");

	// true = known: tex is the fitted picture, or null when this direction needs no change
	public static bool TryLoad(string key, Texture2D src, out Texture2D tex)
	{
		tex = null;
		Init();
		if (broken || src == null)
			return false;
		string f = FileFor(key, src);
		if (!File.Exists(f))
		{
			Misses++;
			return false;
		}
		try
		{
			using (var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.Read))
			using (var ds = new DeflateStream(fs, CompressionMode.Decompress))
			using (var br = new BinaryReader(ds))
			{
				int w = br.ReadInt32(), h = br.ReadInt32();
				if (w == 0 && h == 0)
				{
					Hits++;
					return true;   // "no change" remembered
				}
				if (w != src.width || h != src.height)
					return false;
				byte[] raw = br.ReadBytes(w * h * 4);
				if (raw.Length != w * h * 4)
					return false;
				var px = new Color32[w * h];
				for (int i = 0, k = 0; i < px.Length; i++, k += 4)
					px[i] = new Color32(raw[k], raw[k + 1], raw[k + 2], raw[k + 3]);
				tex = new Texture2D(w, h, TextureFormat.RGBA32, true);
				tex.name = "BodyFitRefit_" + src.name;
				tex.filterMode = src.filterMode;
				tex.wrapMode = TextureWrapMode.Clamp;
				tex.anisoLevel = src.anisoLevel;
				tex.SetPixels32(px);
				tex.Apply(true, true);
				Hits++;
				return true;
			}
		}
		catch
		{
			try { File.Delete(f); } catch { }
			return false;
		}
	}

	// written in the background – the game does not wait for the disk
	public static void Save(string key, Texture2D src, Color32[] px, int w, int h)
	{
		Init();
		if (broken || src == null)
			return;
		string f = FileFor(key, src);
		byte[] raw = null;
		if (px != null)
		{
			raw = new byte[w * h * 4];
			for (int i = 0, k = 0; i < px.Length; i++, k += 4)
			{
				raw[k] = px[i].r; raw[k + 1] = px[i].g; raw[k + 2] = px[i].b; raw[k + 3] = px[i].a;
			}
		}
		int ww = px == null ? 0 : w, hh = px == null ? 0 : h;
		Task.Run(() =>
		{
			try
			{
				string tmp = f + ".tmp";
				using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
				using (var ds = new DeflateStream(fs, System.IO.Compression.CompressionLevel.Fastest))
				using (var bw = new BinaryWriter(ds))
				{
					bw.Write(ww);
					bw.Write(hh);
					if (raw != null)
						bw.Write(raw);
				}
				if (File.Exists(f)) File.Delete(f);
				File.Move(tmp, f);
				System.Threading.Interlocked.Increment(ref Saved);
			}
			catch { }
		});
	}
}
