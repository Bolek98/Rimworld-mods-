using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // Shared "Report a bug" button of Bolek98's mods (Mišák Core).
    // Shown in the bottom-left corner of the mod's settings window (drawn by MUI.Version).
    // Builds two reports from what the game already knows:
    //   short – fits a Steam Workshop comment (Steam allows about 1000 characters),
    //   full  – saved as a text file for GitHub Issues (no limit).
    // Nothing is sent anywhere; the player copies and pastes it themselves.
    // The Windows user name is replaced by <user> in every path.
    // ------------------------------------------------------------------
    public static class MReport
    {
        public const string IssuesUrl = "https://github.com/Bolek98/Rimworld-mods-/issues/new?template=bug_report.md";
        private const int ShortLimit = 950;

        private static readonly AccessTools.FieldRef<Dialog_ModSettings, Mod> modRef =
            AccessTools.FieldRefAccess<Dialog_ModSettings, Mod>("mod");

        public static bool Cz
        {
            get
            {
                try { return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Czech", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        public static string T(string en, string cz) => Cz ? cz : en;

        // Called from MUI.Version while the settings window draws.
        public static void Button(string versionText)
        {
            try
            {
                Dialog_ModSettings w = Find.WindowStack?.WindowOfType<Dialog_ModSettings>();
                if (w == null)
                    return;
                Mod mod = modRef(w);
                if (mod == null)
                    return;
                // same row as the game's Close button (window y = height - 55, content starts at the margin)
                Rect r = new Rect(0f, w.windowRect.height - 55f - Window.StandardMargin, 210f, Window.CloseButSize.y);
                if (Widgets.ButtonText(r, T("Report a bug", "Nahlásit chybu")))
                    Find.WindowStack.Add(new Dialog_Report(mod, versionText));
                TooltipHandler.TipRegion(r, T(
                    "Builds a short report for a Workshop comment and a full one for GitHub. Nothing is sent anywhere.",
                    "Připraví krátké hlášení do komentáře na Workshopu a celé hlášení pro GitHub. Nic se nikam neodesílá."));
            }
            catch
            {
            }
        }

        // ---------------- building the report ----------------
        public class Report
        {
            public string Short;
            public string Full;
            public string FilePath;
            public int OwnErrors;
        }

        public static Report Build(Mod mod, string versionText)
        {
            string ns = typeof(MReport).Namespace;
            ModContentPack pack = mod.Content;
            string name = pack?.Name ?? mod.GetType().Name;
            List<ModContentPack> mods = LoadedModManager.RunningModsListForReading;

            // errors and warnings from the game log that point into this mod's code
            var own = new List<LogMessage>();
            var other = new List<LogMessage>();
            try
            {
                foreach (LogMessage m in Log.Messages.ToList())
                {
                    if (m == null || m.type == LogMessageType.Message)
                        continue;
                    string all = (m.text ?? "") + "\n" + (m.StackTrace ?? "");
                    if (all.Contains(ns + ".") || all.Contains("[" + name + "]"))
                        own.Add(m);
                    else if (m.type == LogMessageType.Error)
                        other.Add(m);
                }
            }
            catch
            {
            }

            List<KeyValuePair<string, int>> shared = SharedPatches(ns);

            var full = new StringBuilder();
            full.AppendLine("=== Bug report: " + name + " " + versionText + " ===");
            full.AppendLine("Created: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            full.AppendLine("RimWorld: " + VersionControl.CurrentVersionStringWithRev);
            full.AppendLine("System: " + SystemInfo.operatingSystem + " | " + SystemInfo.graphicsDeviceName + " | RAM " + SystemInfo.systemMemorySize + " MB");
            full.AppendLine("Language: " + (LanguageDatabase.activeLanguage?.folderName ?? "?"));
            full.AppendLine("Active mods: " + mods.Count);
            full.AppendLine();
            full.AppendLine("--- Errors and warnings from this mod (" + own.Count + ") ---");
            if (own.Count == 0)
                full.AppendLine("none in the game log");
            foreach (LogMessage m in own.AsEnumerable().Reverse().Take(15).Reverse())
                AppendMessage(full, m, 12);
            full.AppendLine();
            full.AppendLine("--- Other errors in the game log (last 8 of " + other.Count + ") ---");
            foreach (LogMessage m in other.AsEnumerable().Reverse().Take(8).Reverse())
                AppendMessage(full, m, 6);
            full.AppendLine();
            full.AppendLine("--- Other mods that change the same game code (Harmony owner: methods) ---");
            if (shared.Count == 0)
                full.AppendLine("none");
            foreach (var kv in shared)
                full.AppendLine(kv.Key + ": " + kv.Value);
            full.AppendLine();
            full.AppendLine("--- Mod list (load order) ---");
            for (int i = 0; i < mods.Count; i++)
                full.AppendLine((i + 1) + ". " + mods[i].Name + " [" + mods[i].PackageIdPlayerFacing + "]");
#if MISAK_MLOG
            try
            {
                string lp = MLog.Path;
                if (File.Exists(lp))
                {
                    full.AppendLine();
                    full.AppendLine("--- Own log of the mod (last 80 lines) ---");
                    foreach (string line in ReadShared(lp).Split('\n').Reverse().Take(80).Reverse())
                        full.AppendLine(line.TrimEnd('\r'));
                }
            }
            catch
            {
            }
#endif

            var rep = new Report { OwnErrors = own.Count };
            rep.Full = Clean(full.ToString());

            // short version for a Workshop comment
            var sb = new StringBuilder();
            sb.AppendLine("Bug report – " + name + " " + versionText);
            sb.AppendLine("RimWorld " + VersionControl.CurrentVersionStringWithRev + " | " + mods.Count + " mods");
            sb.AppendLine("Errors from this mod: " + own.Count);
            if (own.Count > 0)
            {
                LogMessage first = own[own.Count - 1];
                sb.AppendLine("Last: " + Cut(FirstLine(first.text), 260));
                string where = FirstOwnFrame(first.StackTrace, ns);
                if (where != null)
                    sb.AppendLine("At: " + Cut(where, 160));
            }
            if (shared.Count > 0)
                sb.AppendLine("Same code also changed by: " + string.Join(", ", shared.Take(6).Select(k => k.Key)));
            sb.Append("What happened: ");
            rep.Short = Cut(Clean(sb.ToString()), ShortLimit);

            try
            {
                string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "BugReports");
                Directory.CreateDirectory(dir);
                string safe = new string(name.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == '-').ToArray()).Trim().Replace(' ', '_');
                if (safe.Length > 40)
                    safe = safe.Substring(0, 40);
                rep.FilePath = Path.Combine(dir, safe + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm") + ".txt");
                File.WriteAllText(rep.FilePath, rep.Full, Encoding.UTF8);
            }
            catch
            {
                rep.FilePath = null;
            }
            return rep;
        }

        // Harmony owners that patch the same methods as this mod (count of shared methods)
        private static List<KeyValuePair<string, int>> SharedPatches(string ns)
        {
            var count = new Dictionary<string, int>();
            try
            {
                foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
                {
                    HarmonyLib.Patches p = Harmony.GetPatchInfo(m);
                    if (p == null)
                        continue;
                    var all = p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers).ToList();
                    if (!all.Any(x => InNs(x.PatchMethod, ns)))
                        continue;
                    foreach (string owner in all.Where(x => !InNs(x.PatchMethod, ns)).Select(x => x.owner).Distinct())
                    {
                        if (string.IsNullOrEmpty(owner))
                            continue;
                        count.TryGetValue(owner, out int c);
                        count[owner] = c + 1;
                    }
                }
            }
            catch
            {
            }
            return count.OrderByDescending(k => k.Value).ThenBy(k => k.Key).ToList();
        }

        private static bool InNs(MethodInfo m, string ns)
        {
            string n = m?.DeclaringType?.Namespace;
            return n != null && (n == ns || n.StartsWith(ns + "."));
        }

        private static void AppendMessage(StringBuilder sb, LogMessage m, int stackLines)
        {
            sb.AppendLine("[" + m.type + (m.repeats > 1 ? " x" + m.repeats : "") + "] " + Cut(m.text ?? "", 1500));
            string st = m.StackTrace;
            if (!string.IsNullOrEmpty(st))
                foreach (string line in st.Split('\n').Where(l => l.Trim().Length > 0).Take(stackLines))
                    sb.AppendLine("    " + line.Trim());
        }

        private static string FirstOwnFrame(string stack, string ns)
        {
            if (string.IsNullOrEmpty(stack))
                return null;
            foreach (string line in stack.Split('\n'))
                if (line.Contains(ns + "."))
                    return line.Trim();
            return null;
        }

        private static string FirstLine(string s)
        {
            if (string.IsNullOrEmpty(s))
                return "";
            int i = s.IndexOf('\n');
            return (i < 0 ? s : s.Substring(0, i)).Trim();
        }

        private static string Cut(string s, int max) => s.Length <= max ? s : s.Substring(0, max - 1) + "…";

        private static string Clean(string s)
        {
            try
            {
                string u = Environment.UserName;
                if (!string.IsNullOrEmpty(u) && u.Length > 1)
                {
                    s = s.Replace("\\" + u + "\\", "\\<user>\\").Replace("/" + u + "/", "/<user>/");
                }
            }
            catch
            {
            }
            return s;
        }

        private static string ReadShared(string path)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var r = new StreamReader(fs))
                return r.ReadToEnd();
        }
    }

    public class Dialog_Report : Window
    {
        private readonly MReport.Report rep;
        private readonly string modName;
        private string copied;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(760f, 600f);

        public Dialog_Report(Mod mod, string versionText)
        {
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            modName = mod.Content?.Name ?? mod.GetType().Name;
            rep = MReport.Build(mod, versionText);
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, inRect.width, 34f), MReport.T("Report a bug", "Nahlásit chybu") + " – " + modName);
            Text.Font = GameFont.Small;
            y += 38f;

            string intro = MReport.T(
                "1. Copy the short report and paste it into a comment on the mod's Workshop page. Add one sentence about what happened.\n" +
                "2. Longer problem? Open GitHub Issues and paste the full report there (it has no length limit).\n" +
                "Errors found from this mod: " + rep.OwnErrors + ". Nothing is sent anywhere by itself.",
                "1. Zkopíruj krátké hlášení a vlož ho do komentáře na stránce modu na Workshopu. Připiš jednu větu, co se stalo.\n" +
                "2. Delší problém? Otevři GitHub Issues a vlož tam celé hlášení (nemá omezenou délku).\n" +
                "Nalezené chyby tohoto modu: " + rep.OwnErrors + ". Nic se samo nikam neodesílá.");
            float ih = Text.CalcHeight(intro, inRect.width);
            Widgets.Label(new Rect(0f, y, inRect.width, ih), intro);
            y += ih + 10f;

            float bw = (inRect.width - 30f) / 4f;
            if (Widgets.ButtonText(new Rect(0f, y, bw, 34f), MReport.T("Copy short report", "Kopírovat krátké")))
            {
                GUIUtility.systemCopyBuffer = rep.Short;
                copied = MReport.T("Short report copied – paste it into a Workshop comment.", "Krátké hlášení zkopírováno – vlož ho do komentáře na Workshopu.");
            }
            if (Widgets.ButtonText(new Rect(bw + 10f, y, bw, 34f), MReport.T("Copy full report", "Kopírovat celé")))
            {
                GUIUtility.systemCopyBuffer = rep.Full;
                copied = MReport.T("Full report copied – paste it into GitHub Issues.", "Celé hlášení zkopírováno – vlož ho do GitHub Issues.");
            }
            if (Widgets.ButtonText(new Rect((bw + 10f) * 2f, y, bw, 34f), "GitHub Issues"))
                Application.OpenURL(MReport.IssuesUrl);
            if (rep.FilePath != null && Widgets.ButtonText(new Rect((bw + 10f) * 3f, y, bw, 34f), MReport.T("Open folder", "Otevřít složku")))
                Application.OpenURL("file:///" + Path.GetDirectoryName(rep.FilePath).Replace('\\', '/'));
            y += 40f;

            if (copied != null)
            {
                GUI.color = MUI.Accent;
                Widgets.Label(new Rect(0f, y, inRect.width, 24f), copied);
                GUI.color = Color.white;
            }
            y += 26f;

            Rect box = new Rect(0f, y, inRect.width, inRect.height - y - CloseButSize.y - 8f);
            Widgets.Label(new Rect(0f, y, inRect.width, 22f), MReport.T("Short report (preview):", "Krátké hlášení (náhled):"));
            box.y += 24f;
            box.height -= 24f;
            Widgets.DrawBoxSolid(box, MUI.CardBg);
            Rect view = new Rect(0f, 0f, box.width - 20f, Text.CalcHeight(rep.Short, box.width - 30f) + 10f);
            Widgets.BeginScrollView(box.ContractedBy(4f), ref scroll, view);
            Widgets.Label(new Rect(4f, 4f, view.width - 8f, view.height), rep.Short);
            Widgets.EndScrollView();
        }
    }
}
