using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // 2.3.0: progress per mod. Every mod with texts the language lacks: how much is translated,
    // a switch to leave the mod in English, and an export of its translation as a normal
    // Languages folder (to share, or to send to the mod's author).
    public static class ModsTab
    {
        private sealed class Stat
        {
            public string Mod;
            public int Total, Done;
            public float Pct => Total == 0 ? 1f : (float)Done / Total;
        }

        private static volatile List<Stat> stats;
        private static volatile bool counting;
        private static float countedAt = -100f;
        private static Vector2 scroll;
        private static string query = "";
        private static string note;
        private static volatile bool exporting;

        // ---------------- skipped mods (read by the translator thread too) ----------------
        private static volatile HashSet<string> skipped;

        public static bool IsSkipped(string mod)
        {
            HashSet<string> s = skipped;
            if (s == null) skipped = s = Parse(UniversalRimTranslatorMod.Settings.skippedMods);
            return mod != null && s.Contains(mod);
        }

        private static HashSet<string> Parse(string v) =>
            new HashSet<string>((v ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

        private static void SetSkipped(string mod, bool skip)
        {
            var s = new HashSet<string>(Parse(UniversalRimTranslatorMod.Settings.skippedMods), StringComparer.Ordinal);
            if (skip) s.Add(mod); else s.Remove(mod);
            UniversalRimTranslatorMod.Settings.skippedMods = string.Join("\n", s.OrderBy(x => x).ToArray());
            skipped = s;
            Runtime.Reapply();
            Runtime.Kick();
        }

        // ---------------- drawing ----------------
        public static void Draw(Rect r)
        {
            Store store = Runtime.Store;
            if (!Runtime.Active || store == null)
            {
                Widgets.Label(r, "URT_Status_Inactive".Translate(Runtime.Phase));
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (!counting && now - countedAt > 5f) Count(store);

            float y = r.y;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, 44f), "URT_Mods_Intro".Translate());
            GUI.color = Color.white;
            y += 46f;

            // search + export all + open folder
            float bw = 190f;
            query = Widgets.TextField(new Rect(r.x, y, r.width - 2 * (bw + 8f), 30f), query ?? "");
            if (string.IsNullOrEmpty(query))
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(r.x + 6f, y + 5f, 300f, 24f), "URT_Mods_SearchHint".Translate());
                GUI.color = Color.white;
            }
            Rect exAll = new Rect(r.xMax - 2 * bw - 8f, y, bw, 30f);
            if (Widgets.ButtonText(exAll, exporting ? (string)"URT_Mods_Exporting".Translate() : (string)"URT_Mods_ExportAll".Translate()) && !exporting)
                Export(stats?.Where(s => s.Done > 0).Select(s => s.Mod).ToList());
            TooltipHandler.TipRegion(exAll, "URT_Mods_ExportTip".Translate(ExportRoot()));
            if (Widgets.ButtonText(new Rect(r.xMax - bw, y, bw, 30f), "URT_Mods_OpenFolder".Translate()))
                OpenFolder();
            y += 36f;

            if (!string.IsNullOrEmpty(note))
            {
                GUI.color = MUI.Accent;
                Widgets.Label(new Rect(r.x, y, r.width, 24f), note);
                GUI.color = Color.white;
                y += 26f;
            }

            // 2.5.2: new mods that wait for the player ("don't translate newly added mods automatically")
            HashSet<string> held = Runtime.HeldMods;
            if (UniversalRimTranslatorMod.Settings.holdNewMods && held != null && held.Count > 0)
            {
                string names = string.Join(", ", held.OrderBy(x => x).Take(8).ToArray()) + (held.Count > 8 ? " …" : "");
                float aw = 220f;
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(r.x, y + 4f, r.width - aw - 8f, 26f), ((string)"URT_Mods_NewWaiting".Translate(held.Count, names)).Truncate(r.width - aw - 8f));
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(r.xMax - aw, y, aw, 30f), "URT_Mods_AllowNew".Translate()))
                    Runtime.AllowNewMods();
                y += 36f;
            }

            List<Stat> list = stats;
            if (list == null)
            {
                Widgets.Label(new Rect(r.x, y, r.width, 24f), "URT_Mods_Counting".Translate());
                return;
            }
            string q = (query ?? "").Trim();
            List<Stat> show = q.Length == 0 ? list : list.Where(s => s.Mod.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            int all = list.Sum(s => s.Total), done = list.Sum(s => s.Done);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), "URT_Mods_Sum".Translate(list.Count, done, all, Percent(all == 0 ? 1f : (float)done / all)));
            GUI.color = Color.white;
            y += 26f;

            Rect outer = new Rect(r.x, y, r.width, r.yMax - y);
            const float rowH = 34f;
            Rect view = new Rect(0f, 0f, outer.width - 16f, show.Count * rowH);
            Widgets.BeginScrollView(outer, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1);
            int last = Mathf.Min(show.Count - 1, (int)((scroll.y + outer.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                Stat s = show[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                bool on = !IsSkipped(s.Mod);
                bool was = on;
                Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 5f), ref on);
                TooltipHandler.TipRegion(new Rect(row.x, row.y, 32f, row.height), "URT_Mods_SkipTip".Translate());
                if (on != was) SetSkipped(s.Mod, !on);

                float exW = 120f, barW = Mathf.Min(260f, row.width * 0.3f);
                Rect name = new Rect(row.x + 36f, row.y + 6f, row.width - 36f - barW - exW - 24f, 24f);
                GUI.color = on ? Color.white : MUI.Muted;
                Widgets.Label(name, s.Mod.Truncate(name.width));
                GUI.color = Color.white;

                Rect bar = new Rect(name.xMax + 8f, row.y + 7f, barW, 18f);
                MUI.Fill(bar, MUI.CardBg2);
                MUI.Fill(new Rect(bar.x, bar.y, bar.width * s.Pct, bar.height), on ? MUI.Accent : MUI.Border);
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(bar, s.Done + " / " + s.Total + " · " + Percent(s.Pct));
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;

                Rect ex = new Rect(row.xMax - exW, row.y + 2f, exW, 28f);
                if (s.Done > 0 && Widgets.ButtonText(ex, "URT_Mods_Export".Translate()) && !exporting)
                    Export(new List<string> { s.Mod });
            }
            Widgets.EndScrollView();
        }

        private static string Percent(float p) => Mathf.FloorToInt(p * 100f) + " %";

        // counted in the background: hundreds of thousands of texts
        private static void Count(Store store)
        {
            counting = true;
            countedAt = Time.realtimeSinceStartup;
            Item[] items = Runtime.Items.ToArray();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var d = new Dictionary<string, Stat>();
                    var seen = new HashSet<string>();
                    foreach (Item it in items)
                    {
                        string mod = string.IsNullOrEmpty(it.Mod) ? "?" : it.Mod;
                        // the same text twice in one mod counts once
                        if (!seen.Add(mod + "\u0001" + it.Source)) continue;
                        if (!d.TryGetValue(mod, out Stat s)) d[mod] = s = new Stat { Mod = mod };
                        s.Total++;
                        if (store.TryGet(it.Source, out Rec rc) && rc.Usable) s.Done++;     // without the glossary (fast)
                    }
                    stats = d.Values.OrderBy(s => s.Pct >= 1f).ThenByDescending(s => s.Total - s.Done).ThenBy(s => s.Mod).ToList();
                }
                catch (Exception e) { MLog.Warn("[URT] counting per mod: " + e.Message); }
                finally { counting = false; }
            });
        }

        // ---------------- export ----------------
        public static string ExportRoot() => Path.Combine(Runtime.Folder, "Export");

        private static void OpenFolder()
        {
            try
            {
                Directory.CreateDirectory(ExportRoot());
                Application.OpenURL("file:///" + ExportRoot().Replace('\\', '/'));
            }
            catch (Exception e) { MLog.Warn("[URT] open folder: " + e.Message); }
        }

        private static void Export(List<string> mods)
        {
            if (mods == null || mods.Count == 0) return;
            Store store = Runtime.Store;
            string langFolder = LanguageDatabase.activeLanguage?.folderName ?? Runtime.Target;
            var want = new HashSet<string>(mods, StringComparer.Ordinal);
            Item[] items = Runtime.Items.Where(it => want.Contains(it.Mod ?? "?")).ToArray();
            exporting = true;
            note = null;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                int files = 0, texts = 0;
                string last = null;
                try
                {
                    foreach (var byMod in items.GroupBy(it => it.Mod ?? "?"))
                    {
                        int n = ExportMod(store, byMod.Key, byMod.ToList(), langFolder, ref files, out string dir);
                        texts += n;
                        if (n > 0) last = dir;
                    }
                    int t2 = texts, f2 = files;
                    string l2 = last;
                    bool one = mods.Count == 1 && last != null;
                    Runtime.RunOnMain(() =>
                    {
                        string msg = one ? (string)"URT_Mods_ExportedOne".Translate(t2, l2) : (string)"URT_Mods_ExportedAll".Translate(t2, f2, ExportRoot());
                        note = msg;
                        Messages.Message(msg, RimWorld.MessageTypeDefOf.TaskCompletion, false);
                        MLog.Info("[URT] " + msg);
                    });
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] export failed: " + e);
                    string err = e.Message;
                    Runtime.RunOnMain(() => note = "URT_Mods_ExportFailed".Translate(err));
                }
                finally { exporting = false; }
            });
        }

        private static int ExportMod(Store store, string mod, List<Item> items, string langFolder, ref int files, out string dir)
        {
            dir = Path.Combine(Path.Combine(ExportRoot(), Safe(mod)), Path.Combine("Languages", langFolder));
            string file = "URT_" + Safe(mod) + ".xml";
            int n = 0;

            // Keyed: key → text
            var keyed = new SortedDictionary<string, string>(StringComparer.Ordinal);
            // DefInjected: def type → path → text (or a whole list)
            var defs = new SortedDictionary<string, SortedDictionary<string, object>>(StringComparer.Ordinal);
            var lists = new Dictionary<string, List<Item>>();

            foreach (Item it in items)
            {
                string t = store.Use(it.Source);
                if (it.Kind == ItemKind.DefListItem)
                {
                    string lk = Scan.ListKey(it.DefType, it.Path);
                    if (!lists.TryGetValue(lk, out var l)) lists[lk] = l = new List<Item>();
                    l.Add(it);
                    continue;
                }
                if (t == null) continue;
                if (it.Kind == ItemKind.Keyed) keyed[it.Path] = t;
                else
                {
                    string type = it.DefType?.Name ?? "Def";
                    if (!defs.TryGetValue(type, out var m)) defs[type] = m = new SortedDictionary<string, object>(StringComparer.Ordinal);
                    m[it.Path] = t;
                }
                n++;
            }
            // a list (grammar rules …) is written whole: the original entries with the translated ones
            foreach (var kv in lists)
            {
                Item first = kv.Value[0];
                if (!Scan.OriginalLists.TryGetValue(kv.Key, out List<string> orig)) continue;
                var full = new List<string>(orig);
                int got = 0;
                foreach (Item it in kv.Value)
                {
                    string t = store.Use(it.Source);
                    if (t == null || it.ListIndex < 0 || it.ListIndex >= full.Count) continue;
                    full[it.ListIndex] = t;
                    got++;
                }
                if (got == 0) continue;
                string type = first.DefType?.Name ?? "Def";
                if (!defs.TryGetValue(type, out var m)) defs[type] = m = new SortedDictionary<string, object>(StringComparer.Ordinal);
                m[first.Path] = full;
                n += got;
            }
            if (n == 0) return 0;

            if (keyed.Count > 0)
            {
                Write(Path.Combine(Path.Combine(dir, "Keyed"), file), keyed.Select(kv => new KeyValuePair<string, object>(kv.Key, kv.Value)));
                files++;
            }
            foreach (var kv in defs)
            {
                Write(Path.Combine(Path.Combine(Path.Combine(dir, "DefInjected"), kv.Key), file), kv.Value);
                files++;
            }
            return n;
        }

        private static void Write(string path, IEnumerable<KeyValuePair<string, object>> entries)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var sb = new StringBuilder();
            sb.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
            sb.Append("<!-- Universal RimTranslator " + UrtVersion.Number + " – " + DateTime.Now.ToString("yyyy-MM-dd") + " -->\n");
            sb.Append("<LanguageData>\n");
            foreach (var kv in entries)
            {
                if (!ValidTag(kv.Key)) continue;
                if (kv.Value is List<string> l)
                {
                    sb.Append("  <").Append(kv.Key).Append(">\n");
                    foreach (string e in l) sb.Append("    <li>").Append(Esc(e)).Append("</li>\n");
                    sb.Append("  </").Append(kv.Key).Append(">\n");
                }
                else sb.Append("  <").Append(kv.Key).Append(">").Append(Esc(kv.Value as string)).Append("</").Append(kv.Key).Append(">\n");
            }
            sb.Append("</LanguageData>\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        // a line break is written as \n, like in every language pack
        private static string Esc(string s) => (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\r", "").Replace("\n", "\\n");

        private static bool ValidTag(string k)
        {
            if (string.IsNullOrEmpty(k) || !(char.IsLetter(k[0]) || k[0] == '_')) return false;
            foreach (char c in k)
                if (!(char.IsLetterOrDigit(c) || c == '_' || c == '.' || c == '-')) return false;
            return true;
        }

        private static string Safe(string s)
        {
            var sb = new StringBuilder();
            foreach (char c in s ?? "mod")
                sb.Append(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == ' ' || c == '.' ? c : '_');
            string r = sb.ToString().Trim().TrimEnd('.');
            if (r.Length > 60) r = r.Substring(0, 60).Trim();
            return r.Length == 0 ? "mod" : r;
        }
    }
}
