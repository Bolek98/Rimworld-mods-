using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Verse;

namespace UnofficialPatch
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
