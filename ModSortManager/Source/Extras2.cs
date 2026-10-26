using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // ---------------------------------------------------------------------------
    // 0.2.0: share a mod list as a text code, what changed in updated mods,
    // and how much each mod adds to the loading time.
    // ---------------------------------------------------------------------------
    public class ShareEntry { public string id, name; public ulong wid; }

    public static class ShareCode
    {
        public const string Prefix = "MSM1:";

        public static ulong WorkshopIdOf(ModMetaData m)
        {
            ulong id;
            return m != null && m.OnSteamWorkshop && ulong.TryParse(m.RootDir.Name, out id) ? id : 0;
        }

        public static string Make(IEnumerable<ModMetaData> mods)
        {
            var sb = new StringBuilder();
            foreach (ModMetaData m in mods)
            {
                if (m == null) continue;
                sb.Append(m.PackageId).Append('\t').Append(WorkshopIdOf(m)).Append('\t')
                  .Append((m.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
            }
            byte[] raw = Encoding.UTF8.GetBytes(sb.ToString());
            using (var ms = new MemoryStream())
            {
                using (var z = new DeflateStream(ms, CompressionMode.Compress, true))
                    z.Write(raw, 0, raw.Length);
                return Prefix + Convert.ToBase64String(ms.ToArray());
            }
        }

        public static List<ShareEntry> Parse(string code)
        {
            try
            {
                if (code == null) return null;
                code = new string(code.Where(c => !char.IsWhiteSpace(c)).ToArray());
                int at = code.IndexOf(Prefix, StringComparison.Ordinal);
                if (at < 0) return null;
                byte[] data = Convert.FromBase64String(code.Substring(at + Prefix.Length));
                string text;
                using (var ms = new MemoryStream(data))
                using (var z = new DeflateStream(ms, CompressionMode.Decompress))
                using (var r = new StreamReader(z, Encoding.UTF8))
                    text = r.ReadToEnd();
                var list = new List<ShareEntry>();
                foreach (string line in text.Split('\n'))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 3 || p[0].Length == 0) continue;
                    ulong w;
                    ulong.TryParse(p[1], out w);
                    list.Add(new ShareEntry { id = p[0], wid = w, name = p[2] });
                }
                return list.Count > 0 ? list : null;
            }
            catch
            {
                return null;
            }
        }
    }

    // mods whose version or files changed since the last game start
    public class ModChange { public ModMetaData mod; public string from, to; }

    public static class ModChanges
    {
        private static List<ModChange> changed;
        private static string FilePath => Path.Combine(MSM.DataDir, "known_versions.txt");

        public static List<ModChange> Changed
        {
            get
            {
                if (changed == null) Refresh();
                return changed;
            }
        }

        private static string Stamp(ModMetaData m)
        {
            try
            {
                string about = Path.Combine(Path.Combine(m.RootDir.FullName, "About"), "About.xml");
                return File.Exists(about) ? File.GetLastWriteTimeUtc(about).Ticks.ToString() : "0";
            }
            catch { return "0"; }
        }

        // compared once per game start; the file then holds the new state
        public static void Refresh()
        {
            changed = new List<ModChange>();
            try
            {
                var old = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                bool first = !File.Exists(FilePath);
                if (!first)
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length >= 3) old[p[0]] = p;
                    }
                var lines = new List<string>();
                foreach (ModMetaData m in ModLister.AllInstalledMods)
                {
                    if (m.Official) continue;
                    string key = m.RootDir.FullName, ver = m.ModVersion ?? "", stamp = Stamp(m);
                    lines.Add(key + "\t" + ver + "\t" + stamp);
                    string[] o;
                    if (!first && old.TryGetValue(key, out o) && (o[1] != ver || o[2] != stamp))
                        changed.Add(new ModChange { mod = m, from = o[1], to = ver });
                }
                File.WriteAllLines(FilePath, lines.ToArray());
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] updated mods: " + e.Message);
            }
        }

        public static ModChange Of(ModMetaData m) => Changed.FirstOrDefault(c => c.mod == m);

        // the top of the mod's own change log, if it has one
        public static string Changelog(ModMetaData m, int maxLines = 60)
        {
            try
            {
                var dirs = new[] { m.RootDir.FullName, Path.Combine(m.RootDir.FullName, "About") };
                foreach (string d in dirs)
                {
                    if (!Directory.Exists(d)) continue;
                    foreach (string f in Directory.GetFiles(d))
                    {
                        string n = Path.GetFileName(f).ToLowerInvariant();
                        if (!(n.StartsWith("changelog") || n.StartsWith("change_log") || n.StartsWith("changes"))) continue;
                        if (!(n.EndsWith(".txt") || n.EndsWith(".md") || !n.Contains("."))) continue;
                        string[] all = File.ReadAllLines(f);
                        return string.Join("\n", all.Take(maxLines).ToArray()) + (all.Length > maxLines ? "\n…" : "");
                    }
                }
            }
            catch { }
            return null;
        }
    }

    public class Dialog_Changes : Window
    {
        private Vector2 scroll;
        private readonly Dictionary<ModMetaData, string> text = new Dictionary<ModMetaData, string>();
        private readonly HashSet<ModMetaData> open = new HashSet<ModMetaData>();

        public override Vector2 InitialSize => new Vector2(860f, 680f);

        public Dialog_Changes(ModMetaData only = null)
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            foreach (ModChange c in ModChanges.Changed)
            {
                string t = ModChanges.Changelog(c.mod);
                text[c.mod] = t ?? "MSM_NoChangelog".Translate().ToString();
                if (only == null ? ModChanges.Changed.Count <= 3 : c.mod == only) open.Add(c.mod);
            }
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "MSM_ChangesTitle".Translate(ModChanges.Changed.Count));
            Text.Font = GameFont.Small;
            List<ModChange> list = ModChanges.Changed;
            float w = r.width - 16f;
            float total = 0f;
            foreach (ModChange c in list)
                total += 32f + (open.Contains(c.mod) ? Text.CalcHeight(text[c.mod], w - 20f) + 10f : 0f);
            Rect outR = new Rect(r.x, r.y + 40f, r.width, r.height - 40f);
            Rect view = new Rect(0f, 0f, w, Mathf.Max(total, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                ModChange c = list[i];
                Rect head = new Rect(0f, y, w, 30f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(head);
                bool isOpen = open.Contains(c.mod);
                string ver = c.from == c.to || c.to.NullOrEmpty() ? "" : "   " + (c.from.NullOrEmpty() ? "?" : c.from) + " → " + c.to;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(head.x + 8f, head.y, head.width - 150f, 30f), (isOpen ? "▼ " : "► ") + c.mod.Name + ver);
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(new Rect(head.x, head.y, head.width - 150f, 30f)))
                {
                    if (isOpen) open.Remove(c.mod); else open.Add(c.mod);
                }
                if (MSM.CanOpenPage(c.mod) && Widgets.ButtonText(new Rect(head.xMax - 140f, head.y + 2f, 136f, 26f), c.mod.OnSteamWorkshop ? "MSM_Workshop".Translate() : "MSM_Web".Translate()))
                    MSM.OpenPage(c.mod);
                y += 32f;
                if (isOpen)
                {
                    string t = text[c.mod];
                    float h = Text.CalcHeight(t, w - 20f);
                    GUI.color = new Color32(200, 204, 208, 255);
                    Widgets.Label(new Rect(20f, y, w - 20f, h), t);
                    GUI.color = Color.white;
                    y += h + 10f;
                }
            }
            Widgets.EndScrollView();
        }
    }

    // how much a mod brings to load: textures, code, defs (sizes on disk – a good guide to load time)
    public class Weight { public long tex, code, defs, sound; public long Total => tex + code + defs + sound; }

    public static class LoadWeight
    {
        private static readonly Dictionary<string, Weight> done = new Dictionary<string, Weight>();
        private static readonly HashSet<string> busy = new HashSet<string>();
        private static readonly object sync = new object();

        public static Weight Get(ModMetaData m)
        {
            if (m == null) return null;
            string dir = m.RootDir.FullName;
            lock (sync)
            {
                Weight w;
                if (done.TryGetValue(dir, out w)) return w;
                if (busy.Contains(dir)) return null;
                busy.Add(dir);
            }
            ThreadPool.QueueUserWorkItem(_ =>
            {
                Weight w = new Weight();
                try
                {
                    foreach (string f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        string p = f.Replace('\\', '/');
                        if (p.Contains("/.git/") || p.Contains("/Source/")) continue;
                        string ext = Path.GetExtension(f).ToLowerInvariant();
                        long len;
                        try { len = new FileInfo(f).Length; } catch { continue; }
                        if (ext == ".png" || ext == ".dds" || ext == ".jpg" || ext == ".jpeg" || ext == ".psd") { if (!p.Contains("/About/")) w.tex += len; }
                        else if (ext == ".dll") w.code += len;
                        else if (ext == ".xml") { if (!p.Contains("/About/") && !p.Contains("/Languages/")) w.defs += len; }
                        else if (ext == ".ogg" || ext == ".wav" || ext == ".mp3") w.sound += len;
                        else if (p.Contains("/AssetBundles/")) w.tex += len;
                    }
                }
                catch { }
                lock (sync) { done[dir] = w; busy.Remove(dir); }
            });
            return null;
        }

        public static string Mb(long b) => b < 1024 * 1024 ? (b / 1024L) + " kB" : (b / 1048576f).ToString("0.0") + " MB";

        public static string Line(ModMetaData m)
        {
            Weight w = Get(m);
            if (w == null) return "MSM_WeightCalc".Translate();
            return "MSM_Weight".Translate(Mb(w.tex), Mb(w.code), Mb(w.defs), Mb(w.sound));
        }
    }

    public class Dialog_LoadWeight : Window
    {
        private readonly List<ModMetaData> mods;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(760f, 680f);

        public Dialog_LoadWeight(List<ModMetaData> active)
        {
            mods = active.Where(m => !m.Official).ToList();
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            foreach (ModMetaData m in mods) LoadWeight.Get(m);
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "MSM_WeightTitle".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 36f), "MSM_WeightHint".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            var rows = mods.Select(m => new KeyValuePair<ModMetaData, Weight>(m, LoadWeight.Get(m))).ToList();
            int waiting = rows.Count(x => x.Value == null);
            rows = rows.Where(x => x.Value != null).OrderByDescending(x => x.Value.Total).ToList();
            long all = rows.Sum(x => x.Value.Total), max = rows.Count > 0 ? Math.Max(1L, rows[0].Value.Total) : 1L;
            Widgets.Label(new Rect(r.x, r.y + 72f, r.width, 24f), "MSM_WeightSum".Translate(LoadWeight.Mb(all), rows.Count) + (waiting > 0 ? "  (" + "MSM_WeightCalc".Translate() + " " + waiting + ")" : ""));
            Rect outR = new Rect(r.x, r.y + 100f, r.width, r.height - 100f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows.Count * 28f, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < rows.Count; i++)
            {
                Weight w = rows[i].Value;
                Rect row = new Rect(0f, i * 28f, view.width, 26f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                float bar = (view.width * 0.35f) * w.Total / max;
                Widgets.DrawBoxSolid(new Rect(row.x + view.width * 0.45f, row.y + 8f, bar, 10f), i < 5 ? MUI.Warn : MUI.Accent);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 6f, row.y, view.width * 0.44f, 26f), rows[i].Key.Name.Truncate(view.width * 0.44f));
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.Label(new Rect(row.xMax - view.width * 0.2f, row.y, view.width * 0.2f - 6f, 26f), LoadWeight.Mb(w.Total));
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(row, LoadWeight.Line(rows[i].Key));
            }
            Widgets.EndScrollView();
        }
    }
}
