using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // ---------------- 0.2.1: safe start after a crash ----------------
    // A small file marks a running game; a clean quit removes it. If it is still there at the next
    // start, the last session crashed or froze. When the mod list was changed just before that
    // session, the list from before the change is offered (it opens in Mod Sort Manager to check).
    public static class CrashGuard
    {
        private static string FlagPath => Path.Combine(MSM.DataDir, "session_running.txt");
        private static DateTime crashedStart = DateTime.MinValue;   // start time of the session that did not end
        private static bool checkedOnce;

        public static void SessionStart()
        {
            try
            {
                string f = FlagPath;
                if (File.Exists(f))
                {
                    DateTime t;
                    if (DateTime.TryParse(File.ReadAllText(f).Trim(), null, System.Globalization.DateTimeStyles.RoundtripKind, out t))
                        crashedStart = t;
                    else
                        crashedStart = File.GetLastWriteTime(f);
                }
                File.WriteAllText(f, DateTime.Now.ToString("o"));
                Application.quitting += SessionEnd;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] crash guard: " + e.Message);
            }
        }

        public static void SessionEnd()
        {
            try { if (File.Exists(FlagPath)) File.Delete(FlagPath); } catch { }
        }

        // true when a dialog was shown (the order guard then waits for the next start)
        public static bool Check()
        {
            if (checkedOnce) return false;
            checkedOnce = true;
            if (crashedStart == DateTime.MinValue || Bisect.Active) return false;
            try
            {
                // only a recent crash (a flag left from long ago says nothing about today's list)
                if ((DateTime.Now - crashedStart).TotalDays > 3) return false;
                // the list from before the last change: the newest backup that differs from the current list
                List<string> cur = OrderGuard.Current();
                var curN = cur.Select(OrderGuard.Norm).ToList();
                FileInfo pick = null;
                List<string> old = null;
                foreach (FileInfo f in new DirectoryInfo(MSM.BackupDir).GetFiles("ModsConfig_*.xml").OrderByDescending(x => x.Name).Take(5))
                {
                    List<string> ids = MSM.ReadModsConfigIds(f.FullName);
                    if (ids.Count == 0) continue;
                    if (ids.Select(OrderGuard.Norm).SequenceEqual(curN, StringComparer.OrdinalIgnoreCase)) continue;
                    pick = f;
                    old = ids;
                    break;
                }
                // the change must be from shortly before (or during) the session that crashed
                if (pick == null || pick.LastWriteTime < crashedStart.AddHours(-2)) return false;
                var oldSet = new HashSet<string>(old.Select(OrderGuard.Norm), StringComparer.OrdinalIgnoreCase);
                var curSet = new HashSet<string>(curN, StringComparer.OrdinalIgnoreCase);
                List<string> added = curN.Where(x => !oldSet.Contains(x)).ToList();
                List<string> removed = old.Select(OrderGuard.Norm).Where(x => !curSet.Contains(x)).ToList();
                string changes = (added.Count > 0 ? "MSM_CrashAdded".Translate(added.Count) + " " + Names(added) + "\n" : "")
                    + (removed.Count > 0 ? "MSM_CrashRemoved".Translate(removed.Count) + " " + Names(removed) + "\n" : "")
                    + (added.Count == 0 && removed.Count == 0 ? "MSM_CrashMoved".Translate().ToString() + "\n" : "");
                List<string> restore = old.Where(id => MSM.FindMod(id) != null).ToList();
                Log.Message("[Mod Sort Manager] the last session did not end properly; the mod list was changed at " + pick.LastWriteTime.ToString("g") + " (+" + added.Count + " / −" + removed.Count + ")");
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_CrashText".Translate(pick.LastWriteTime.ToString("g"), changes.TrimEnd()),
                    "MSM_CrashRestore".Translate(), () => Find.WindowStack.Add(new Dialog_ModSortManager(restore)),
                    "MSM_CrashKeep".Translate(), () => OrderGuard.Remember(cur), "MSM_CrashTitle".Translate(), false, null, null));
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] crash guard: " + e.Message);
                return false;
            }
        }

        private static string Names(List<string> ids)
        {
            return string.Join(", ", ids.Take(6).Select(id => MSM.FindMod(id)?.Name ?? id).ToArray()) + (ids.Count > 6 ? ", …" : "");
        }
    }

    // a clean quit (also the restart after saving the mod list) – the session ended properly
    [HarmonyPatch(typeof(Root), nameof(Root.Shutdown))]
    public static class Patch_RootShutdown
    {
        public static void Prefix() => CrashGuard.SessionEnd();
    }

    // ---------------- 0.2.1: known mods that do the same job ----------------
    // Not a guess from the code: a short list of pairs that are known to clash or to do the same
    // thing twice. Bolek98's own mods are listed with their well-known rivals.
    public static class Rivals
    {
        public class Rule
        {
            public string key;            // MSM_Rival_<key>: the explanation
            public string[] a, b;         // package ids; "*text" = any package id containing the text
            public Sev sev;
        }

        public class Hit
        {
            public Rule rule;
            public ModMetaData a, b;
        }

        public static readonly List<Rule> Rules = new List<Rule>
        {
            new Rule { key = "Haul", a = new[] { "misak.haulsmarter" }, b = new[] { "*pickupandhaul" }, sev = Sev.Warn },
            new Rule { key = "HarvestHaul", a = new[] { "misak.haulsmarter" }, b = new[] { "*harvestandhaul" }, sev = Sev.Warn },
            new Rule { key = "Tick", a = new[] { "misak.graphicstuner" }, b = new[] { "bolek98.smoothframes", "*rocketman", "bs.performance", "*performancefish" }, sev = Sev.Warn },
            new Rule { key = "Multiplayer", a = new[] { "misak.graphicstuner" }, b = new[] { "rwmt.multiplayer" }, sev = Sev.Warn },
            new Rule { key = "Freezes", a = new[] { "misak.graphicstuner" }, b = new[] { "bolek98.memorytuner" }, sev = Sev.Warn },
            new Rule { key = "Priorities", a = new[] { "misak.colonyforeman" }, b = new[] { "*workmanager", "*freewill" }, sev = Sev.Warn },
            new Rule { key = "Tabs", a = new[] { "bolek98.misaktabs" }, b = new[] { "*nicebilltab", "*nicehealthtab" }, sev = Sev.Info },
            new Rule { key = "Managers", a = new[] { "misak.modsortmanager" }, b = new[] { "fluffy.modmanager", "fluffy.modmanager.continued" }, sev = Sev.Info },
        };

        private static bool Matches(ModMetaData m, string pat)
        {
            if (m == null || m.PackageId == null || pat.NullOrEmpty()) return false;
            string id = OrderGuard.Norm(m.PackageId).ToLowerInvariant();
            if (pat[0] == '*') return id.Contains(pat.Substring(1));
            return id == pat;
        }

        public static List<Hit> Find(List<ModMetaData> active)
        {
            var res = new List<Hit>();
            try
            {
                foreach (Rule r in Rules)
                {
                    ModMetaData a = active.FirstOrDefault(m => r.a.Any(p => Matches(m, p)));
                    if (a == null) continue;
                    foreach (ModMetaData b in active.Where(m => m != a && r.b.Any(p => Matches(m, p))))
                        res.Add(new Hit { rule = r, a = a, b = b });
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Mod Sort Manager] rival check: " + e.Message, 0x35a1101);
            }
            return res;
        }
    }
}
