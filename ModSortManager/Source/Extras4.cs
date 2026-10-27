using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // ---------------- 0.2.2: the mod list of a save ----------------
    // Only the <meta> header of the save is read (the reader stops at </meta>; the game itself is
    // not loaded). The dialog shows what is missing or different and can use the save's list.
    public class SaveModList
    {
        public string name, version;
        public List<string> ids = new List<string>(), names = new List<string>();
        public List<ulong> wids = new List<ulong>();
    }

    public static class SaveMods
    {
        public static List<FileInfo> Files()
        {
            try { return GenFilePaths.AllSavedGameFiles.ToList(); }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] saves: " + e.Message);
                return new List<FileInfo>();
            }
        }

        public static SaveModList Read(FileInfo f)
        {
            try
            {
                var settings = new XmlReaderSettings { IgnoreComments = true, IgnoreWhitespace = true, DtdProcessing = DtdProcessing.Prohibit };
                using (XmlReader reader = XmlReader.Create(f.FullName, settings))
                {
                    // the root (savegame), then the first element in it must be <meta> – like the game's ReadToMetaElement
                    if (!NextElement(reader) || !NextElement(reader) || reader.Name != "meta") return null;
                    var res = new SaveModList { name = Path.GetFileNameWithoutExtension(f.Name) };
                    var steam = new List<string>();
                    using (XmlReader sub = reader.ReadSubtree())
                    {
                        string cur = null;
                        sub.Read();
                        while (!sub.EOF)
                        {
                            if (sub.NodeType == XmlNodeType.Element)
                            {
                                string n = sub.Name;
                                if (n == "gameVersion") { res.version = sub.ReadElementContentAsString(); continue; }
                                if (n == "li" && cur != null)
                                {
                                    string v = sub.ReadElementContentAsString() ?? "";
                                    if (cur == "modIds") res.ids.Add(v.Trim());
                                    else if (cur == "modNames") res.names.Add(v.Trim());
                                    else if (cur == "modSteamIds") steam.Add(v.Trim());
                                    continue;
                                }
                                if (n != "li") cur = n;
                            }
                            sub.Read();
                        }
                    }
                    foreach (string s in steam)
                    {
                        ulong w;
                        res.wids.Add(ulong.TryParse(s, out w) ? w : 0UL);
                    }
                    return res.ids.Count == 0 ? null : res;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] mod list of the save " + f.Name + ": " + e.Message);
                return null;
            }
        }

        private static bool NextElement(XmlReader r)
        {
            do
            {
                if (!r.Read()) return false;
            }
            while (r.NodeType != XmlNodeType.Element);
            return true;
        }
    }

    public class Dialog_SaveMods : Window
    {
        private readonly SaveModList data;
        private readonly Action apply;
        private readonly List<string> missing = new List<string>();
        private readonly List<ModMetaData> turnOn = new List<ModMetaData>(), turnOff = new List<ModMetaData>();
        private readonly bool sameOrder;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        public Dialog_SaveMods(SaveModList d, List<ModMetaData> current, Action applyList)
        {
            data = d;
            apply = applyList;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            var found = new List<ModMetaData>();
            for (int i = 0; i < d.ids.Count; i++)
            {
                ModMetaData m = MSM.FindMod(d.ids[i]);
                if (m == null) missing.Add(i < d.names.Count && !d.names[i].NullOrEmpty() ? d.names[i] : d.ids[i]);
                else if (!found.Contains(m)) found.Add(m);
            }
            foreach (ModMetaData m in found)
                if (!current.Any(c => c == m || c.SamePackageId(m.PackageId, true))) turnOn.Add(m);
            foreach (ModMetaData c in current)
                if (!found.Any(m => m == c || m.SamePackageId(c.PackageId, true)) && !c.IsCoreMod) turnOff.Add(c);
            sameOrder = missing.Count == 0 && turnOn.Count == 0 && turnOff.Count == 0
                        && found.Select(m => m.PackageIdNonUnique).SequenceEqual(current.Select(m => m.PackageIdNonUnique));
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width - 30f, 34f), "MSM_SaveTitle".Translate(data.name).ToString().Truncate(r.width - 30f));
            Text.Font = GameFont.Small;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y + 36f, r.width, 24f), "MSM_SaveSub".Translate(data.ids.Count, data.version ?? "?"));
            GUI.color = Color.white;
            float top = r.y + 66f;
            Rect outR = new Rect(r.x, top, r.width, r.yMax - top - 50f);

            var lines = new List<KeyValuePair<string, Color>>();
            if (missing.Count == 0 && turnOn.Count == 0 && turnOff.Count == 0)
                lines.Add(new KeyValuePair<string, Color>((sameOrder ? "MSM_SaveSame" : "MSM_SaveSameMods").Translate().ToString(), MUI.Accent));
            Section(lines, "MSM_SaveMissing".Translate(missing.Count), missing, new Color32(224, 87, 74, 255));
            Section(lines, "MSM_SaveTurnOn".Translate(turnOn.Count), turnOn.Select(m => m.Name).ToList(), MUI.Accent);
            Section(lines, "MSM_SaveTurnOff".Translate(turnOff.Count), turnOff.Select(m => m.Name).ToList(), MUI.Warn);

            const float lh = 24f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, lines.Count * lh);
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < lines.Count; i++)
                {
                    GUI.color = lines[i].Value;
                    Widgets.Label(new Rect(0f, i * lh, view.width, lh), lines[i].Key.Truncate(view.width));
                }
            }
            finally
            {
                GUI.color = Color.white;
                Widgets.EndScrollView();
            }

            Rect btn = new Rect(r.xMax - 220f, r.yMax - 40f, 220f, 40f);
            if (Widgets.ButtonText(btn, "MSM_SaveApply".Translate()))
            {
                Close();
                try { apply?.Invoke(); }
                catch (Exception e) { Log.Warning("[Mod Sort Manager] apply the list of the save: " + e.Message); }
            }
            if (Widgets.ButtonText(new Rect(btn.x - 170f, btn.y, 160f, 40f), "MSM_Close".Translate())) Close();
        }

        private static void Section(List<KeyValuePair<string, Color>> lines, string title, List<string> items, Color c)
        {
            if (items.Count == 0) return;
            if (lines.Count > 0) lines.Add(new KeyValuePair<string, Color>("", Color.white));
            lines.Add(new KeyValuePair<string, Color>("<b>" + title + "</b>", c));
            foreach (string s in items) lines.Add(new KeyValuePair<string, Color>("   • " + s, MUI.Text1));
        }
    }
}
