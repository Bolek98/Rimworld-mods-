#define MISAK_MLOG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ------------------------------------------------------------------
    // "What's new" for Bolek98's mods: after an update, the changelog lines since the version
    // you played last are shown once – one window for all of these mods together.
    // The first install shows nothing. "Don't show again" turns it off for all of them.
    // Remembered in Config/Bolek98_WhatsNew.txt (packageId=version).
    // ------------------------------------------------------------------
    public static class MNews
    {
        private const string Shared = "Bolek98.WhatsNew.v1";
        private const string Registry = "Bolek98.Mods.v1";     // every Bolek98 mod that is running: name, version, packageId
        private const int MaxLines = 6;

        private static bool Cz
        {
            get
            {
                try { return (LanguageDatabase.activeLanguage?.folderName ?? "").StartsWith("Czech", StringComparison.OrdinalIgnoreCase); }
                catch { return false; }
            }
        }

        private static string T(string en, string cz) => Cz ? cz : en;

        private static string FilePath => Path.Combine(GenFilePaths.ConfigFolderPath, "Bolek98_WhatsNew.txt");

        // call from the Mod constructor
        public static void Register(ModContentPack content, string version)
        {
            try
            {
                if (content == null || string.IsNullOrEmpty(version))
                    return;
                string id = content.PackageId ?? content.Name;
                // the overview of all Bolek98 mods
                var reg = AppDomain.CurrentDomain.GetData(Registry) as List<string[]>;
                if (reg == null)
                {
                    reg = new List<string[]>();
                    AppDomain.CurrentDomain.SetData(Registry, reg);
                }
                if (!reg.Any(r => r[2] == id))
                    reg.Add(new[] { content.Name ?? id, version, id, content.FolderName ?? "" });
                Dictionary<string, string> seen = Load();
                string old;
                seen.TryGetValue(id, out old);
                if (old != version)
                {
                    seen[id] = version;
                    Save(seen);
                }
                if (old == null || seen.ContainsKey("off") || Compare(version, old) <= 0)
                    return;
                List<string> items = Changes(Path.Combine(content.RootDir, "CHANGELOG.txt"), old, version);
                if (items.Count == 0)
                    return;
                var list = AppDomain.CurrentDomain.GetData(Shared) as List<string[]>;
                bool first = list == null;
                if (first)
                {
                    list = new List<string[]>();
                    AppDomain.CurrentDomain.SetData(Shared, list);
                }
                list.Add(new[] { content.Name ?? id, old, version, string.Join("\n", items) });
                // the first of the mods shows the window for all of them once the game has loaded
                if (first)
                    LongEventHandler.ExecuteWhenFinished(() =>
                    {
                        try { Find.WindowStack.Add(new Window_News(list)); }
                        catch (Exception e) { Log.Warning("[Bolek98] what's new: " + e.Message); }
                    });
            }
            catch (Exception e)
            {
                Log.Warning("[Bolek98] what's new: " + e.Message);
            }
        }

        private static Dictionary<string, string> Load()
        {
            var d = new Dictionary<string, string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0)
                            d[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
                    }
            }
            catch
            {
            }
            return d;
        }

        private static void Save(Dictionary<string, string> d)
        {
            try { File.WriteAllLines(FilePath, d.OrderBy(kv => kv.Key).Select(kv => kv.Key + "=" + kv.Value).ToArray()); }
            catch { }
        }

        public static void TurnOff()
        {
            Dictionary<string, string> d = Load();
            d["off"] = "1";
            Save(d);
        }

        // "1.2.10" vs "1.2.9"
        public static int Compare(string a, string b)
        {
            string[] x = (a ?? "").Split('.'), y = (b ?? "").Split('.');
            for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            {
                int p = i < x.Length ? Num(x[i]) : 0;
                int q = i < y.Length ? Num(y[i]) : 0;
                if (p != q)
                    return p.CompareTo(q);
            }
            return 0;
        }

        private static int Num(string s)
        {
            int v;
            return int.TryParse(s, out v) ? v : 0;
        }

        // the "- " lines of every version newer than old (up to the current one)
        private static List<string> Changes(string path, string old, string current)
        {
            var res = new List<string>();
            if (!File.Exists(path))
                return res;
            bool take = false;
            int more = 0;
            foreach (string raw in File.ReadAllLines(path))
            {
                string l = raw.Trim();
                if (System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d+(\.\d+)+$"))
                {
                    take = Compare(l, old) > 0 && Compare(l, current) <= 0;
                    continue;
                }
                if (!take || !l.StartsWith("- "))
                    continue;
                if (res.Count < MaxLines)
                    res.Add("• " + l.Substring(2));
                else
                    more++;
            }
            if (more > 0)
                res.Add(T("… and " + more + " more (see the change notes)", "… a dalších " + more + " (viz poznámky ke změnám)"));
            return res;
        }

        // "Bolek98's mods" button next to "Report a bug" in every settings window
        public static void Button()
        {
            try
            {
                Dialog_ModSettings w = Find.WindowStack?.WindowOfType<Dialog_ModSettings>();
                if (w == null || !(AppDomain.CurrentDomain.GetData(Registry) is List<string[]> reg) || reg.Count < 2)
                    return;
                Rect r = new Rect(216f, w.windowRect.height - 55f - Window.StandardMargin, 190f, Window.CloseButSize.y);
                if (Widgets.ButtonText(r, T("Bolek98's mods", "Mody Bolek98")))
                    Find.WindowStack.Add(new Window_Mods(reg));
                TooltipHandler.TipRegion(r, T("All Bolek98 mods you run: versions, settings, and saving all their settings to a file.",
                    "Všechny tvoje mody od Bolek98: verze, nastavení a uložení všech jejich nastavení do souboru."));
            }
            catch
            {
            }
        }

        private static string ExportRoot => Path.Combine(GenFilePaths.ConfigFolderPath, "Bolek98_settings");

        private static IEnumerable<string> SettingsFiles(List<string[]> reg)
        {
            var res = new List<string>();
            try
            {
                foreach (string file in Directory.GetFiles(GenFilePaths.ConfigFolderPath, "Mod_*.xml"))
                {
                    string name = Path.GetFileName(file);
                    if (reg.Any(r => r[3].Length > 0 && name.StartsWith("Mod_" + r[3] + "_", StringComparison.OrdinalIgnoreCase)))
                        res.Add(file);
                }
                string news = FilePath;
                if (File.Exists(news)) res.Add(news);
            }
            catch
            {
            }
            return res;
        }

        private class Window_Mods : Window
        {
            private readonly List<string[]> reg;
            private Vector2 scroll;
            private string note;

            public Window_Mods(List<string[]> reg)
            {
                this.reg = reg.OrderBy(r => r[0]).ToList();
                doCloseX = true;
                closeOnClickedOutside = true;
                absorbInputAroundWindow = true;
            }

            public override Vector2 InitialSize => new Vector2(620f, 600f);

            public override void DoWindowContents(Rect inRect)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), T("Bolek98's mods", "Mody od Bolek98"));
                Text.Font = GameFont.Small;
                Rect outer = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f - 80f);
                const float rowH = 34f;
                Rect view = new Rect(0f, 0f, outer.width - 16f, reg.Count * rowH);
                Widgets.BeginScrollView(outer, ref scroll, view);
                for (int i = 0; i < reg.Count; i++)
                {
                    string[] m = reg[i];
                    Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                    if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(row.x + 6f, row.y, row.width - 260f, row.height), m[0].Truncate(row.width - 260f));
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(row.xMax - 250f, row.y, 100f, row.height), m[1]);
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    Mod mod = null;
                    try { mod = LoadedModManager.ModHandles.FirstOrDefault(x => x.Content?.PackageId == m[2] && !string.IsNullOrEmpty(x.SettingsCategory())); }
                    catch { }
                    if (mod != null && Widgets.ButtonText(new Rect(row.xMax - 140f, row.y + 2f, 136f, row.height - 4f), T("Settings", "Nastavení")))
                    {
                        Find.WindowStack.TryRemove(typeof(Dialog_ModSettings), false);
                        Close();
                        Find.WindowStack.Add(new Dialog_ModSettings(mod));
                    }
                }
                Widgets.EndScrollView();

                float by = inRect.yMax - 72f;
                if (Widgets.ButtonText(new Rect(inRect.x, by, 220f, 32f), T("Save all settings", "Uložit všechna nastavení")))
                {
                    try
                    {
                        string dir = Path.Combine(ExportRoot, DateTime.Now.ToString("yyyy-MM-dd_HH-mm"));
                        Directory.CreateDirectory(dir);
                        int n = 0;
                        foreach (string f in SettingsFiles(reg)) { File.Copy(f, Path.Combine(dir, Path.GetFileName(f)), true); n++; }
                        note = T(n + " settings files saved into " + dir, "Uloženo " + n + " souborů nastavení do " + dir);
                    }
                    catch (Exception e) { note = e.Message; }
                }
                Rect load = new Rect(inRect.x + 230f, by, 220f, 32f);
                if (Widgets.ButtonText(load, T("Load saved settings…", "Načíst uložená nastavení…")))
                {
                    var opts = new List<FloatMenuOption>();
                    try
                    {
                        if (Directory.Exists(ExportRoot))
                            foreach (string d in Directory.GetDirectories(ExportRoot).OrderByDescending(x => x))
                            {
                                string dd = d;
                                opts.Add(new FloatMenuOption(Path.GetFileName(d), () =>
                                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                                        T("Replace the settings of Bolek98's mods with the ones saved " + Path.GetFileName(dd) + "? Restart the game right after, before you open any settings.",
                                          "Nahradit nastavení modů od Bolek98 těmi uloženými " + Path.GetFileName(dd) + "? Hned potom restartuj hru, než otevřeš jakékoli nastavení."),
                                        () =>
                                        {
                                            int n = 0;
                                            foreach (string f in Directory.GetFiles(dd, "*.xml").Concat(Directory.GetFiles(dd, "*.txt")))
                                            { File.Copy(f, Path.Combine(GenFilePaths.ConfigFolderPath, Path.GetFileName(f)), true); n++; }
                                            note = T(n + " files loaded – restart the game now.", "Načteno " + n + " souborů – teď restartuj hru.");
                                        }, destructive: true))));
                            }
                    }
                    catch { }
                    if (opts.Count == 0) opts.Add(new FloatMenuOption(T("Nothing saved yet", "Zatím nic uloženo"), null));
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                TooltipHandler.TipRegion(load, T("For another PC or after reinstalling: copy the folder Config\\Bolek98_settings there first.",
                    "Na jiný počítač nebo po přeinstalaci: nejdřív tam zkopíruj složku Config\\Bolek98_settings."));
                if (!string.IsNullOrEmpty(note))
                {
                    GUI.color = MUI.Accent;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(inRect.x, inRect.yMax - 36f, inRect.width, 36f), note);
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                }
            }
        }

        private class Window_News : Window
        {
            private readonly List<string[]> mods;
            private Vector2 scroll;
            private bool off;

            public Window_News(List<string[]> mods)
            {
                this.mods = mods;
                doCloseX = true;
                closeOnClickedOutside = false;
                absorbInputAroundWindow = true;
            }

            public override Vector2 InitialSize => new Vector2(640f, 560f);

            public override void DoWindowContents(Rect inRect)
            {
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), T("What's new in Bolek98's mods", "Co je nového v módech od Bolek98"));
                Text.Font = GameFont.Small;
                float top = inRect.y + 40f, bottom = inRect.yMax - 40f;
                Rect outer = new Rect(inRect.x, top, inRect.width, bottom - top - 4f);
                float w = outer.width - 20f;
                float h = 0f;
                foreach (string[] m in mods)
                    h += 30f + Text.CalcHeight(m[3], w - 12f) + 12f;
                Rect view = new Rect(0f, 0f, w, Math.Max(h, outer.height));
                Widgets.BeginScrollView(outer, ref scroll, view);
                float y = 0f;
                foreach (string[] m in mods)
                {
                    GUI.color = MUI.Accent;
                    Widgets.Label(new Rect(0f, y, w, 28f), m[0] + "   " + m[1] + " → " + m[2]);
                    GUI.color = Color.white;
                    y += 30f;
                    float th = Text.CalcHeight(m[3], w - 12f);
                    Widgets.Label(new Rect(12f, y, w - 12f, th), m[3]);
                    y += th + 12f;
                }
                Widgets.EndScrollView();
                Widgets.CheckboxLabeled(new Rect(inRect.x, inRect.yMax - 32f, 300f, 28f), T("Don't show this again", "Už neukazovat"), ref off);
                if (Widgets.ButtonText(new Rect(inRect.xMax - 140f, inRect.yMax - 34f, 140f, 34f), T("Close", "Zavřít")))
                    Close();
            }

            public override void PostClose()
            {
                base.PostClose();
                if (off)
                    TurnOff();
            }
        }
    }
}
