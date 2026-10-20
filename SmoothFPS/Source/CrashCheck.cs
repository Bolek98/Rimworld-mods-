using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.3.0: crash check. When the last game ended without closing its log (a crash to desktop without
    // an error), Smooth FPS looks at what is left behind – the end of the previous log and the crash
    // record Windows wrote (CrashDumps) – and tells at the next start what kind of crash it was, when,
    // which mods were busy right before it, and (for an endless loop) where on the map it happened.
    public static class CrashCheck
    {
        public static string ReportPath => Path.Combine(GenFilePaths.SaveDataFolderPath, "SmoothFPS_crash.txt");

        public static void Run()
        {
            try
            {
                string prev = PrevLog();
                if (prev == null)
                    return;
                string[] lines = ReadLines(prev);
                if (lines.Length == 0 || lines.Reverse().Take(5).Any(l => l.Contains("Log closed")))
                    return;   // the last game closed normally
                DateTime ended = File.GetLastWriteTime(prev);
                string seenFile = Path.Combine(GenFilePaths.SaveDataFolderPath, "SmoothFPS_crash_seen.txt");
                string stamp = ended.ToString("o");
                if (File.Exists(seenFile) && File.ReadAllText(seenFile).Trim() == stamp)
                    return;   // already told
                File.WriteAllText(seenFile, stamp);

                var sb = new StringBuilder();
                sb.AppendLine("GT_Crash_When".Translate(ended.ToString("d.M. HH:mm")));
                string kind = null, place = null;
                string dump = FindDump(ended);
                if (dump != null)
                    ReadDump(dump, out kind, out place);
                sb.AppendLine(kind != null ? (string)("GT_Crash_Kind_" + kind).Translate() : (string)"GT_Crash_KindUnknown".Translate());
                if (place != null)
                    sb.AppendLine("GT_Crash_Place".Translate(place));
                List<string> mods = BusyMods(lines);
                if (mods.Count > 0)
                    sb.AppendLine("GT_Crash_Mods".Translate(string.Join(", ", mods.Take(5).ToArray())));
                string lastError = lines.Reverse().Take(400).FirstOrDefault(l => l.Contains(" ERROR ") || l.Contains("Exception"));
                if (lastError != null)
                    sb.AppendLine("GT_Crash_LastError".Translate(Trim(lastError, 220)));
                sb.AppendLine();
                sb.AppendLine("GT_Crash_Advice".Translate());
                string text = sb.ToString().TrimEnd();
                try
                {
                    File.WriteAllText(ReportPath, text + "\n\nLog: " + prev + (dump != null ? "\nDump: " + dump : "") + "\n\n--- last lines ---\n" + string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 60)).ToArray()));
                }
                catch { }
                MLog.Warn("[Smooth FPS] the last game crashed: " + text.Replace("\n", " | "));
                // 1.3.1: shown when the main menu is up (a window added during loading was lost)
                pendingText = text;
                try
                {
                    new HarmonyLib.Harmony("misak.smoothfps.crashdialog").Patch(HarmonyLib.AccessTools.Method(typeof(UIRoot), nameof(UIRoot.UIRootOnGUI)),
                        postfix: new HarmonyLib.HarmonyMethod(typeof(CrashCheck), nameof(ShowPending)));
                }
                catch
                {
                    Find.WindowStack?.Add(new Dialog_MessageBox(text, title: "GT_Crash_Title".Translate()));
                    pendingText = null;
                }
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] crash check: " + ex.Message);
            }
        }

        private static string pendingText;

        public static void ShowPending()
        {
            if (pendingText == null || Find.WindowStack == null || LongEventHandler.AnyEventNowOrWaiting)
                return;
            string t = pendingText;
            pendingText = null;
            try { Find.WindowStack.Add(new Dialog_MessageBox(t, title: "GT_Crash_Title".Translate())); }
            catch { }
        }

        private static string Trim(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";

        private static string[] ReadLines(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs))
                return r.ReadToEnd().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        }

        private static string PrevLog()
        {
            string[] tries =
            {
                Path.Combine(GenFilePaths.SaveDataFolderPath, "Logs", "Player-prev.log"),
                Path.Combine(GenFilePaths.SaveDataFolderPath, "Player-prev.log"),
                Path.Combine(Application.persistentDataPath, "Player-prev.log")
            };
            return tries.FirstOrDefault(File.Exists);
        }

        // a crash record of RimWorld written within 15 minutes after the previous log ended
        private static string FindDump(DateTime ended)
        {
            try
            {
                string local = Environment.GetEnvironmentVariable("LOCALAPPDATA");
                if (local.NullOrEmpty())
                    return null;
                string dir = Path.Combine(local, "CrashDumps");
                if (!Directory.Exists(dir))
                    return null;
                return new DirectoryInfo(dir).GetFiles("RimWorld*.dmp")
                    .Where(f => f.LastWriteTime >= ended.AddMinutes(-2) && f.LastWriteTime <= ended.AddMinutes(15))
                    .OrderByDescending(f => f.LastWriteTime).Select(f => f.FullName).FirstOrDefault();
            }
            catch
            {
                return null;
            }
        }

        // minidump: exception code of the crashing thread, and for an endless loop the map cell that
        // repeats on its stack
        private static void ReadDump(string path, out string kind, out string place)
        {
            kind = null;
            place = null;
            byte[] d = File.ReadAllBytes(path);
            if (d.Length < 32 || Encoding.ASCII.GetString(d, 0, 4) != "MDMP")
                return;
            int n = BitConverter.ToInt32(d, 8), rva = BitConverter.ToInt32(d, 12);
            int exRva = -1, thRva = -1;
            for (int i = 0; i < n; i++)
            {
                int t = BitConverter.ToInt32(d, rva + i * 12), r = BitConverter.ToInt32(d, rva + i * 12 + 8);
                if (t == 6) exRva = r;
                if (t == 3) thRva = r;
            }
            if (exRva < 0)
                return;
            int tid = BitConverter.ToInt32(d, exRva);
            uint code = BitConverter.ToUInt32(d, exRva + 8);
            switch (code)
            {
                case 0xC00000FD: kind = "Loop"; break;
                case 0xC0000005: kind = "Memory"; break;
                case 0xC0000017: kind = "OutOfMemory"; break;
                case 0xE0434352: kind = "Managed"; break;
                case 0xC0000409: kind = "Fast"; break;
                default: kind = null; break;
            }
            if (code != 0xC00000FD || thRva < 0)
                return;
            int cnt = BitConverter.ToInt32(d, thRva);
            for (int i = 0; i < cnt; i++)
            {
                int o = thRva + 4 + i * 48;
                if (BitConverter.ToInt32(d, o) != tid)
                    continue;
                int size = BitConverter.ToInt32(d, o + 32), mrva = BitConverter.ToInt32(d, o + 36);
                var pairs = new Dictionary<long, int>();
                long prevV = -1;
                for (int k = mrva; k + 8 <= mrva + size && k + 8 <= d.Length; k += 8)
                {
                    long v = BitConverter.ToInt64(d, k);
                    if (v > 0 && v < 1000 && prevV > 0 && prevV < 1000)
                    {
                        long key = prevV * 10000 + v;
                        pairs.TryGetValue(key, out int c);
                        pairs[key] = c + 1;
                    }
                    prevV = v;
                }
                if (pairs.Count > 0)
                {
                    var best = pairs.OrderByDescending(kv => kv.Value).First();
                    if (best.Value >= 50)
                        place = (best.Key / 10000) + ", " + (best.Key % 10000);
                }
            }
        }

        // mods whose code or log tag appears in the last part of the log
        private static List<string> BusyMods(string[] lines)
        {
            var nsToMod = new Dictionary<string, string>();
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
            {
                if (m.IsOfficialMod)
                    continue;
                foreach (System.Reflection.Assembly a in m.assemblies.loadedAssemblies)
                {
                    try
                    {
                        foreach (Type t in a.GetTypes())
                            if (!t.Namespace.NullOrEmpty() && !nsToMod.ContainsKey(t.Namespace) && t.Namespace != "HarmonyLib")
                                nsToMod[t.Namespace] = m.Name;
                    }
                    catch { }
                }
            }
            var count = new Dictionary<string, int>();
            var rx = new Regex(@"at ([A-Za-z0-9_\.]+)\.[A-Za-z0-9_<>]+[\.:]");
            var tag = new Regex(@"\] \[([^\]]{2,40})\]");
            foreach (string l in lines.Skip(Math.Max(0, lines.Length - 300)))
            {
                foreach (Match mt in rx.Matches(l))
                {
                    string ns = mt.Groups[1].Value;
                    while (!ns.NullOrEmpty())
                    {
                        if (nsToMod.TryGetValue(ns, out string mod))
                        {
                            count.TryGetValue(mod, out int c);
                            count[mod] = c + 1;
                            break;
                        }
                        int dot = ns.LastIndexOf('.');
                        ns = dot > 0 ? ns.Substring(0, dot) : null;
                    }
                }
                Match tm = tag.Match(l);
                if (tm.Success && (l.Contains(" ERROR ") || l.Contains(" WARN ")) && tm.Groups[1].Value != "Verse" && tm.Groups[1].Value != "Smooth FPS")
                {
                    string mod = tm.Groups[1].Value;
                    count.TryGetValue(mod, out int c);
                    count[mod] = c + 1;
                }
            }
            return count.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
        }
    }
}
