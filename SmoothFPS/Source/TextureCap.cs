using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.3.3 (performance roadmap, step 2): oversized mod textures. Some mods ship pictures of 4096 or
    // even 8192 pixels for things that are a few cells big on the screen – they eat video memory
    // (pictures pushed out of the card show up late, blank or as red crosses). A DDS picture already
    // contains its smaller copies (mipmaps); a picture bigger than the limit simply starts at the first
    // copy that fits. Nothing is converted or written, the files stay as they are; turn it off and the
    // next start loads the full size again. The game's own pictures are never touched.
    // 1.5.3: is this RimWorld 1.7 (Waylith engine)? Every 1.7 guard asks here.
    public static class Env
    {
        private static int state = -1;

        public static bool Is17
        {
            get
            {
                if (state < 0) state = Detect() ? 1 : 0;
                return state == 1;
            }
        }

        private static bool Detect()
        {
            try { if (RimWorld.VersionControl.CurrentMinor >= 7 || RimWorld.VersionControl.CurrentMajor > 1) return true; } catch { }
            try
            {
                foreach (System.Reflection.Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string n = a.GetName().Name ?? "";
                    if (n.StartsWith("Waylith") || n == "UnityStubs" || n.StartsWith("RimWorldWaylith")) return true;
                }
            }
            catch { }
            return Type.GetType("Mono.Runtime") == null;
        }
    }

    public static class TextureCap
    {
        public static int Count, SavedMb;
        // 1.5.3: the old check looked for an engine type with GenTypes, which only searches the game's
        // and mods' assemblies – it never found it, so the limit stayed on on 1.7 and doors, icons and
        // furniture went blank. One shared check now (game version, engine assemblies, runtime).
        public static bool Waylith => Env.Is17;
        private static bool patched;

        // 1.4.7: not on 1.7 (Waylith). Its texture system uploads pictures on demand and moves them out of
        // video memory under pressure – it can bring back only pictures it loaded itself. A smaller
        // picture made here was lost after such a move and stayed blank (furniture icons, doors …).
        // Waylith manages video memory on its own, so the limit is not needed there.
        public static bool Blocked => Waylith;
        public static int Cap => Waylith ? 0 : GraphicsTunerMod.Settings?.texCap ?? 0;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ModDdsLoader), nameof(ModDdsLoader.TryLoadDds)), prefix: new HarmonyMethod(typeof(TextureCap), nameof(Prefix)));
                patched = true;
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] texture limit not available: " + ex.Message); }
        }

        private static bool Official(string path)
        {
            string p = path.Replace('\\', '/');
            return p.Contains("/RimWorld/Data/") || p.Contains("/Data/Core/");
        }

        // DDS loaded from now on (Faster Game Loading and others load some later)
        public static bool Prefix(VirtualFile file, ref Texture2D __result)
        {
            try
            {
                int cap = Cap;
                string full = file?.FullPath;
                if (cap <= 0 || string.IsNullOrEmpty(full) || Official(full) || !File.Exists(full))
                    return true;
                Texture2D t = LoadCapped(full, cap, Path.GetFileNameWithoutExtension(file.Name));
                if (t == null)
                    return true;
                if (Count++ == 0)
                    MLog.Info("[Smooth FPS] texture limit " + cap + " px is used (first: " + file.Name + ").");
                __result = t;
                return false;
            }
            catch (Exception ex)
            {
                MLog.WarnOnce("[Smooth FPS] texture limit: " + ex.Message, 0x7e3c01);
                return true;
            }
        }

        // textures the game loaded before Smooth FPS started: swapped for the smaller start
        public static void SweepLoaded()
        {
            int cap = Cap;
            if (cap <= 0)
                return;
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
            {
                if (m.IsOfficialMod)
                    continue;
                ModContentHolder<Texture2D> holder;
                try { holder = m.GetContentHolder<Texture2D>(); } catch { continue; }
                if (holder?.contentList == null)
                    continue;
                foreach (var kv in holder.contentList.ToList())
                {
                    Texture2D old = kv.Value;
                    if (old == null || (old.width <= cap && old.height <= cap))
                        continue;
                    string path = FindDds(m, kv.Key);
                    if (path == null)
                        continue;
                    try
                    {
                        Texture2D t = LoadCapped(path, cap, old.name);
                        if (t == null)
                            continue;
                        holder.contentList[kv.Key] = t;
                        UnityEngine.Object.Destroy(old);
                    }
                    catch (Exception ex) { MLog.WarnOnce("[Smooth FPS] texture limit " + kv.Key + ": " + ex.Message, kv.Key.GetHashCode()); }
                }
            }
            if (Count > 0)
                MLog.Info("[Smooth FPS] texture limit " + cap + " px: " + Count + " oversized mod pictures start smaller, about " + SavedMb + " MB of video memory saved.");
        }

        private static string FindDds(ModContentPack m, string key)
        {
            try
            {
                foreach (string folder in m.foldersToLoadDescendingOrder)
                {
                    string p = Path.Combine(folder, "Textures", key + ".dds");
                    if (File.Exists(p))
                        return p;
                }
                string q = Path.Combine(m.RootDir, "Textures", key + ".dds");
                return File.Exists(q) ? q : null;
            }
            catch { return null; }
        }

        private static int LevelBytes(int w, int h, int block) => Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * block;

        // null = not a case for us (small, no mipmaps, not block compressed, unknown format)
        public static Texture2D LoadCapped(string path, int cap, string name)
        {
            byte[] head = new byte[148];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Read(head, 0, 148) < 128)
                    return null;
            }
            if (BitConverter.ToUInt32(head, 0) != 0x20534444)   // "DDS "
                return null;
            int h = BitConverter.ToInt32(head, 12), w = BitConverter.ToInt32(head, 16);
            int mips = Math.Max(1, BitConverter.ToInt32(head, 28));
            if ((w <= cap && h <= cap) || mips <= 1)
                return null;
            string cc = System.Text.Encoding.ASCII.GetString(head, 84, 4);
            int off = 128, block;
            TextureFormat fmt;
            switch (cc)
            {
                case "DXT1": fmt = TextureFormat.DXT1; block = 8; break;
                case "DXT5": fmt = TextureFormat.DXT5; block = 16; break;
                case "ATI1": case "BC4U": fmt = TextureFormat.BC4; block = 8; break;
                case "ATI2": case "BC5U": fmt = TextureFormat.BC5; block = 16; break;
                case "DX10":
                    off = 148;
                    int dxgi = BitConverter.ToInt32(head, 128);
                    if (dxgi == 71 || dxgi == 72) { fmt = TextureFormat.DXT1; block = 8; }
                    else if (dxgi == 77 || dxgi == 78) { fmt = TextureFormat.DXT5; block = 16; }
                    else if (dxgi == 80 || dxgi == 81) { fmt = TextureFormat.BC4; block = 8; }
                    else if (dxgi == 83 || dxgi == 84) { fmt = TextureFormat.BC5; block = 16; }
                    else if (dxgi == 98 || dxgi == 99) { fmt = TextureFormat.BC7; block = 16; }
                    else return null;
                    break;
                default: return null;
            }
            int skip = 0, skipBytes = 0;
            while ((w >> skip) > cap || (h >> skip) > cap)
            {
                if (skip + 1 >= mips)
                    return null;
                skipBytes += LevelBytes(Math.Max(1, w >> skip), Math.Max(1, h >> skip), block);
                skip++;
            }
            int nw = Math.Max(1, w >> skip), nh = Math.Max(1, h >> skip), nm = mips - skip;
            int restBytes = 0;
            for (int i = 0; i < nm; i++)
                restBytes += LevelBytes(Math.Max(1, nw >> i), Math.Max(1, nh >> i), block);
            byte[] data = new byte[restBytes];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                fs.Seek(off + skipBytes, SeekOrigin.Begin);
                int got = 0;
                while (got < restBytes)
                {
                    int n = fs.Read(data, got, restBytes - got);
                    if (n <= 0)
                        return null;   // file shorter than its header says
                    got += n;
                }
            }
            var t = new Texture2D(nw, nh, fmt, nm, false, true);
            t.name = name;
            t.LoadRawTextureData(data);
            t.filterMode = FilterMode.Trilinear;
            t.anisoLevel = 0;
            // 1.3.2: on 1.7 (Waylith) textures made while the game loads get their video memory only at the
            // end of loading, from their pixels or from the game's texture cache. The game puts its own
            // pictures into that cache first; ours are not in it, so dropping the pixels here left them
            // blank (doors, their icons …). On 1.7 the pixels stay until the picture is on the card –
            // the engine frees them itself after the upload.
            t.Apply(false, !Waylith);
            Count++;
            SavedMb += skipBytes / (1024 * 1024);
            return t;
        }
    }
}
