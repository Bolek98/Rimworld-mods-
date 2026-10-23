using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // 0.1.3: check a changed mod list before it is saved – the game itself only finds out after the restart
    public static class PreSave
    {
        public class Dep { public ModMetaData mod; public string name; public ModMetaData installed; }

        public class Ref { public ModMetaData mod; public string def; public string missing; public bool crash; }

        public const int GroupSize = 8, ManyNew = 10;

        public static string Id(ModMetaData m) => (m.PackageId ?? "").ToLowerInvariant().Replace("_steam", "");

        public static List<ModMetaData> NewMods(List<ModMetaData> active, List<ModMetaData> opened)
        {
            var had = new HashSet<string>(opened.Select(Id));
            return active.Where(m => !m.Official && !had.Contains(Id(m))).ToList();
        }

        public static List<Dep> Deps(List<ModMetaData> active)
        {
            var list = new List<Dep>();
            var ids = new HashSet<string>(active.Select(Id));
            foreach (ModMetaData m in active)
                foreach (ModDependency d in m.Dependencies)
                {
                    if (d == null || d.packageId.NullOrEmpty()) continue;
                    var alts = new List<string> { d.packageId };
                    if (d.alternativePackageIds != null) alts.AddRange(d.alternativePackageIds);
                    if (alts.Any(a => ids.Contains(a.ToLowerInvariant().Replace("_steam", "")))) continue;
                    ModMetaData inst = alts.Select(a => ModLister.GetModWithIdentifier(a, true)).FirstOrDefault(x => x != null);
                    list.Add(new Dep { mod = m, name = d.displayName ?? d.packageId, installed = inst });
                }
            return list;
        }

        public static List<ModMetaData> Old(List<ModMetaData> newMods) => newMods.Where(m => !m.Official && !m.VersionCompatible).ToList();

        // groups of about 8; a new mod goes together with the new mods it needs
        public static List<List<ModMetaData>> Groups(List<ModMetaData> newMods)
        {
            var left = new List<ModMetaData>(newMods);
            var groups = new List<List<ModMetaData>>();
            var cur = new List<ModMetaData>();
            while (left.Count > 0)
            {
                var closure = new List<ModMetaData>();
                var stack = new Stack<ModMetaData>();
                stack.Push(left[0]);
                while (stack.Count > 0)
                {
                    ModMetaData m = stack.Pop();
                    if (closure.Contains(m) || !left.Contains(m)) continue;
                    closure.Add(m);
                    foreach (ModDependency d in m.Dependencies)
                        foreach (ModMetaData o in left)
                            if (d != null && !d.packageId.NullOrEmpty() && o.SamePackageId(d.packageId, true)) stack.Push(o);
                }
                foreach (ModMetaData m in closure) left.Remove(m);
                if (cur.Count > 0 && cur.Count + closure.Count > GroupSize) { groups.Add(cur); cur = new List<ModMetaData>(); }
                // keep the order of the active list inside a group
                cur.AddRange(newMods.Where(closure.Contains));
            }
            if (cur.Count > 0) groups.Add(cur);
            return groups;
        }

        // ---------- the folders the game would load for a mod ----------
        private static bool ShouldLoad(LoadFolder f, HashSet<string> ids)
        {
            Func<string, bool> on = x => ids.Contains(x.ToLowerInvariant().Replace("_steam", ""));
            if (f.requiredAnyOfPackageIds != null && f.requiredAnyOfPackageIds.Count > 0 && !f.requiredAnyOfPackageIds.Any(on)) return false;
            if (f.requiredAllOfPackageIds != null && f.requiredAllOfPackageIds.Count > 0 && !f.requiredAllOfPackageIds.All(on)) return false;
            if (f.disallowedAnyOfPackageIds != null && f.disallowedAnyOfPackageIds.Any(on)) return false;
            return true;
        }

        public static List<string> Folders(ModMetaData m, HashSet<string> ids)
        {
            var res = new List<string>();
            string root = m.RootDir.FullName;
            try
            {
                if (m.loadFolders != null && m.loadFolders.DefinedVersions().Count > 0)
                {
                    List<LoadFolder> lf = m.LoadFoldersForVersion(VersionControl.CurrentVersionString);
                    if (lf == null || lf.Count == 0)
                    {
                        string best = m.loadFolders.DefinedVersions().Where(x =>
                        {
                            if (x == "default" || x.NullOrEmpty() || !x.Contains(".")) return false;
                            try { return VersionControl.VersionFromString(x) <= VersionControl.CurrentVersion; } catch { return false; }
                        }).OrderByDescending(x => x).FirstOrDefault();
                        lf = best != null ? m.LoadFoldersForVersion(best) : null;
                        if (lf == null) lf = m.LoadFoldersForVersion("default");
                    }
                    if (lf != null)
                    {
                        foreach (LoadFolder f in lf)
                            if (ShouldLoad(f, ids)) res.Add(Path.Combine(root, f.folderName.TrimStart('/', '\\')));
                        if (res.Count > 0) return res;
                    }
                }
            }
            catch { }
            string exact = Path.Combine(root, VersionControl.CurrentVersionStringWithoutBuild);
            if (Directory.Exists(exact)) res.Add(exact);
            else
            {
                Version bestV = new Version(0, 0);
                try
                {
                    foreach (DirectoryInfo d in m.RootDir.GetDirectories())
                    {
                        Version v;
                        if (VersionControl.TryParseVersionString(d.Name, out v) && v <= VersionControl.CurrentVersion && v > bestV) bestV = v;
                    }
                }
                catch { }
                if (bestV.Major > 0) res.Add(Path.Combine(root, bestV.ToString()));
            }
            string common = Path.Combine(root, "Common");
            if (Directory.Exists(common)) res.Add(common);
            res.Add(root);
            return res;
        }

        private static IEnumerable<string> Xmls(string folder, string sub)
        {
            string d = Path.Combine(folder, sub);
            if (!Directory.Exists(d)) return Enumerable.Empty<string>();
            try { return Directory.GetFiles(d, "*.xml", SearchOption.AllDirectories); } catch { return Enumerable.Empty<string>(); }
        }

        private static readonly Regex DefNameRx = new Regex(@"<defName>\s*([^<\s]+)\s*</defName>", RegexOptions.Compiled);

        private static readonly string[] Generated = { "Meat_", "Corpse_", "Blueprint_", "Frame_", "Minified", "Chunk", "Leather_", "Egg" };

        // ---------- a background scan: things new mods need in their build cost / recipe ----------
        public class Scan
        {
            public volatile bool done;
            public volatile int progress, total;
            public List<Ref> refs = new List<Ref>();
            public string error;

            public void Run(List<ModMetaData> active, List<ModMetaData> check)
            {
                var ids = new HashSet<string>(active.Select(Id));
                var folders = active.Select(m => new KeyValuePair<ModMetaData, List<string>>(m, Folders(m, ids))).ToList();
                total = active.Count + check.Count;
                var t = new Thread(() =>
                {
                    try
                    {
                        var defined = new HashSet<string>();
                        foreach (KeyValuePair<ModMetaData, List<string>> kv in folders)
                        {
                            foreach (string f in kv.Value)
                                foreach (string sub in new[] { "Defs", "Patches" })
                                    foreach (string file in Xmls(f, sub))
                                    {
                                        string txt;
                                        try { txt = File.ReadAllText(file); } catch { continue; }
                                        foreach (Match mt in DefNameRx.Matches(txt)) defined.Add(mt.Groups[1].Value);
                                    }
                            progress++;
                        }
                        foreach (ModMetaData m in check)
                        {
                            List<string> fl = folders.FirstOrDefault(kv => kv.Key == m).Value ?? Folders(m, ids);
                            var seen = new HashSet<string>();
                            foreach (string f in fl)
                                foreach (string file in Xmls(f, "Defs"))
                                {
                                    var doc = new XmlDocument();
                                    try { doc.Load(file); } catch { continue; }
                                    XmlNodeList costs = doc.SelectNodes("//costList");
                                    if (costs == null) continue;
                                    foreach (XmlNode cl in costs)
                                    {
                                        XmlNode def = cl;
                                        while (def.ParentNode != null && def.ParentNode != doc.DocumentElement) def = def.ParentNode;
                                        string defName = def["defName"]?.InnerText?.Trim() ?? (def.Attributes?["Name"]?.Value ?? def.Name);
                                        bool recipe = def["recipeMaker"] != null;
                                        if (MayRequireUnmet(cl, ids) || MayRequireUnmet(def, ids)) continue;
                                        foreach (XmlNode c in cl.ChildNodes)
                                        {
                                            if (c.NodeType != XmlNodeType.Element) continue;
                                            if (c.Attributes != null && c.Attributes.Cast<XmlAttribute>().Any(a => a.Name.StartsWith("MayRequire"))) continue;
                                            string n = c.Name;
                                            if (n == "li" || defined.Contains(n) || Generated.Any(g => n.StartsWith(g))) continue;
                                            if (!seen.Add(defName + "|" + n)) continue;
                                            refs.Add(new Ref { mod = m, def = defName, missing = n, crash = recipe });
                                        }
                                    }
                                }
                            progress++;
                        }
                    }
                    catch (Exception e) { error = e.Message; }
                    done = true;
                });
                t.IsBackground = true;
                t.Start();
            }

            private static bool MayRequireUnmet(XmlNode n, HashSet<string> ids)
            {
                XmlAttribute a = n.Attributes?["MayRequire"];
                if (a == null) return false;
                return a.Value.Split(',').Any(x => !ids.Contains(x.Trim().ToLowerInvariant().Replace("_steam", "")));
            }
        }

        // ---------- groups left for later ----------
        private static string QueuePath => Path.Combine(MSM.DataDir, "pending_groups.txt");

        public static void SaveQueue(List<string> order, List<List<ModMetaData>> later)
        {
            try
            {
                var lines = new List<string> { "ORDER " + string.Join(" ", order.ToArray()) };
                foreach (List<ModMetaData> g in later) lines.Add("GROUP " + string.Join(" ", g.Select(Id).ToArray()));
                File.WriteAllLines(QueuePath, lines.ToArray());
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] pending groups: " + e.Message); }
        }

        public static bool LoadQueue(out List<string> order, out List<List<string>> groups)
        {
            order = new List<string>();
            groups = new List<List<string>>();
            try
            {
                if (!File.Exists(QueuePath)) return false;
                foreach (string l in File.ReadAllLines(QueuePath))
                {
                    if (l.StartsWith("ORDER ")) order = l.Substring(6).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                    else if (l.StartsWith("GROUP ")) groups.Add(l.Substring(6).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList());
                }
                return groups.Count > 0;
            }
            catch { return false; }
        }

        public static void DropQueue() { try { if (File.Exists(QueuePath)) File.Delete(QueuePath); } catch { } }

        public static void WriteQueue(List<string> order, List<List<string>> groups)
        {
            if (groups.Count == 0) { DropQueue(); return; }
            try
            {
                var lines = new List<string> { "ORDER " + string.Join(" ", order.ToArray()) };
                foreach (List<string> g in groups) lines.Add("GROUP " + string.Join(" ", g.ToArray()));
                File.WriteAllLines(QueuePath, lines.ToArray());
            }
            catch { }
        }

        // put a mod right after the nearest mod before it in the planned order
        public static void Insert(List<ModMetaData> active, ModMetaData m, List<string> order)
        {
            int at = order.IndexOf(Id(m));
            for (int i = at - 1; i >= 0; i--)
            {
                int j = active.FindIndex(x => Id(x) == order[i]);
                if (j >= 0) { active.Insert(j + 1, m); return; }
            }
            active.Add(m);
        }
    }

    public class Dialog_PreSave : Window
    {
        private readonly Dialog_ModSortManager owner;
        private readonly List<ModMetaData> newMods;
        private readonly List<PreSave.Dep> deps;
        private readonly List<ModMetaData> old;
        private readonly List<List<ModMetaData>> groups;
        private readonly PreSave.Scan scan = new PreSave.Scan();
        private Vector2 scroll;
        private bool autoContinued;

        public override Vector2 InitialSize => new Vector2(720f, 620f);

        public Dialog_PreSave(Dialog_ModSortManager owner)
        {
            this.owner = owner;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            doCloseX = true;
            List<ModMetaData> active = owner.ActiveList;
            newMods = PreSave.NewMods(active, owner.OpenedList);
            deps = PreSave.Deps(active);
            old = PreSave.Old(newMods);
            groups = newMods.Count >= PreSave.ManyNew ? PreSave.Groups(newMods) : new List<List<ModMetaData>>();
            // a removed mod can break mods that stay: check those that named it too
            var removed = new HashSet<string>(owner.OpenedList.Where(m => !active.Any(a => PreSave.Id(a) == PreSave.Id(m))).Select(PreSave.Id));
            var check = new List<ModMetaData>(newMods);
            if (removed.Count > 0)
                foreach (ModMetaData m in active)
                    if (!check.Contains(m) && !m.Official && (m.Dependencies.Any(d => d != null && d.packageId != null && removed.Contains(d.packageId.ToLowerInvariant()))
                        || (m.LoadAfter != null && m.LoadAfter.Any(x => removed.Contains(x.ToLowerInvariant())))))
                        check.Add(m);
            scan.Run(active, check);
        }

        private bool HasProblems => deps.Count > 0 || old.Count > 0 || groups.Count > 1 || (scan.done && (scan.refs.Count > 0 || scan.error != null));

        public override void DoWindowContents(Rect inRect)
        {
            if (scan.done && !HasProblems && !autoContinued)
            {
                autoContinued = true;
                Close();
                owner.ContinueSave();
                return;
            }
            MUI.Fill(inRect, new Color32(24, 27, 31, 255));
            Rect r = inRect.ContractedBy(14f);
            float y = r.y;
            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 30f), "MSM_PS_Title".Translate(), MUI.Text1, GameFont.Medium, TextAnchor.MiddleLeft, false);
            y += 34f;
            if (!scan.done)
            {
                Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 22f), "MSM_PS_Checking".Translate(scan.progress, scan.total), MUI.Muted, GameFont.Small, TextAnchor.MiddleLeft, false);
                y += 24f;
            }

            var sb = new List<KeyValuePair<Color, string>>();
            Color err = new Color32(232, 104, 96, 255), warn = MUI.Warn, txt = new Color32(200, 204, 208, 255);
            if (deps.Count > 0)
            {
                sb.Add(new KeyValuePair<Color, string>(err, "MSM_PS_Deps".Translate()));
                foreach (PreSave.Dep d in deps)
                    sb.Add(new KeyValuePair<Color, string>(txt, "• " + "MSM_PS_DepLine".Translate(d.mod.Name, d.name, d.installed != null ? "MSM_PS_DepOff".Translate() : "MSM_PS_DepNone".Translate())));
            }
            if (scan.done && scan.refs.Count > 0)
            {
                sb.Add(new KeyValuePair<Color, string>(err, "MSM_PS_Refs".Translate()));
                foreach (PreSave.Ref f in scan.refs.Take(40))
                    sb.Add(new KeyValuePair<Color, string>(txt, "• " + "MSM_PS_RefLine".Translate(f.mod.Name, f.def, f.missing) + (f.crash ? "MSM_PS_RefCrash".Translate().ToString() : "")));
                if (scan.refs.Count > 40) sb.Add(new KeyValuePair<Color, string>(txt, "MSM_PS_MoreLines".Translate(scan.refs.Count - 40)));
            }
            if (scan.error != null) sb.Add(new KeyValuePair<Color, string>(warn, scan.error));
            if (old.Count > 0)
            {
                sb.Add(new KeyValuePair<Color, string>(warn, "MSM_PS_Old".Translate()));
                foreach (ModMetaData m in old)
                    sb.Add(new KeyValuePair<Color, string>(txt, "• " + "MSM_PS_OldLine".Translate(m.Name, MSM.Versions(m))));
            }
            if (groups.Count > 1)
            {
                sb.Add(new KeyValuePair<Color, string>(warn, "MSM_PS_Many".Translate(newMods.Count)));
                sb.Add(new KeyValuePair<Color, string>(txt, "MSM_PS_ManyText".Translate(groups.Count)));
                for (int i = 0; i < groups.Count; i++)
                    sb.Add(new KeyValuePair<Color, string>(txt, (i + 1) + ") " + string.Join(", ", groups[i].Select(m => m.Name).ToArray())));
            }

            Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 90f);
            float w = outR.width - 16f;
            float h = 0f;
            Text.Font = GameFont.Small;
            foreach (var kv in sb) h += Text.CalcHeight(kv.Value, w) + 4f;
            Rect view = new Rect(0f, 0f, w, h);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float yy = 0f;
            foreach (var kv in sb)
            {
                float lh = Text.CalcHeight(kv.Value, w);
                Dialog_ModSortManager.Label(new Rect(0f, yy, w, lh + 2f), kv.Value, kv.Key, GameFont.Small, TextAnchor.UpperLeft);
                yy += lh + 4f;
            }
            Widgets.EndScrollView();

            // buttons
            float by = r.yMax - 80f, bw = (r.width - 16f) / 3f;
            List<ModMetaData> fixable = deps.Where(d => d.installed != null).Select(d => d.installed).Distinct().ToList();
            var bad = new HashSet<ModMetaData>(deps.Where(d => d.installed == null && newMods.Contains(d.mod)).Select(d => d.mod));
            if (scan.done) foreach (PreSave.Ref f in scan.refs) if (newMods.Contains(f.mod)) bad.Add(f.mod);
            foreach (ModMetaData m in old) bad.Add(m);
            if (Dialog_ModSortManager.Button(new Rect(r.x, by, bw, 34f), "MSM_PS_FixDeps".Translate(fixable.Count), false, fixable.Count > 0))
            {
                foreach (ModMetaData m in fixable) owner.ActivateMod(m);
                Close();
            }
            if (Dialog_ModSortManager.Button(new Rect(r.x + bw + 8f, by, bw, 34f), "MSM_PS_OffBad".Translate(bad.Count), false, bad.Count > 0))
            {
                foreach (ModMetaData m in bad) owner.DeactivateMod(m);
                Close();
            }
            if (Dialog_ModSortManager.Button(new Rect(r.x + 2 * (bw + 8f), by, bw, 34f), "MSM_PS_FirstGroup".Translate(groups.Count > 1 ? groups[0].Count : 0), true, groups.Count > 1))
            {
                List<string> order = owner.ActiveList.Select(PreSave.Id).ToList();
                for (int i = 1; i < groups.Count; i++)
                    foreach (ModMetaData m in groups[i]) owner.DeactivateMod(m);
                owner.SetQueueAfterSave(order, groups.Skip(1).Select(g => g.Select(PreSave.Id).ToList()).ToList());
                Close();
                owner.ContinueSave();
                return;
            }
            float by2 = r.yMax - 36f;
            if (Dialog_ModSortManager.Button(new Rect(r.x, by2, (r.width - 8f) / 2f, 34f), "MSM_PS_Back".Translate())) Close();
            if (Dialog_ModSortManager.Button(new Rect(r.x + (r.width + 8f) / 2f, by2, (r.width - 8f) / 2f, 34f), "MSM_PS_SaveAnyway".Translate()))
            {
                Close();
                owner.ContinueSave();
            }
        }
    }
}
