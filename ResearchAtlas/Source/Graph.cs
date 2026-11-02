using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchAtlas
{
    public enum Cat { Weapons, Apparel, Medical, Production, Buildings, Food, Power, Other }

    // One research project with everything the window needs, worked out once.
    public class Node
    {
        public int id;
        public ResearchProjectDef def;
        public string mod = "?";
        public string modId = "";
        public Color modColor = Color.gray;
        public int band;                    // tech level as int
        public Cat cat = Cat.Other;
        public string search = "";          // lower-case label, description, mod, unlocked things
        public List<Def> unlocks = new List<Def>();
        public List<int> pre = new List<int>();     // shown prerequisites (no self, no duplicates)
        public List<int> post = new List<int>();
        public string label;
        public Texture2D icon;              // resolved on the main thread when first drawn
        public bool iconDone;
        public bool knowledge;              // Anomaly knowledge project – handled the vanilla way
    }

    // All projects of the game in one graph. Built once on the main thread (defs only, cheap single pass).
    public static class Atlas
    {
        public static List<Node> nodes;
        public static Dictionary<ResearchProjectDef, Node> byDef;
        public static List<string> mods;                        // mod names with projects, sorted
        public static Dictionary<string, int> modCount;
        public static bool Ready => nodes != null;

        public static void Build()
        {
            if (nodes != null) return;
            var t0 = DateTime.Now;
            var list = new List<Node>();
            var map = new Dictionary<ResearchProjectDef, Node>();
            foreach (ResearchProjectDef d in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
            {
                if (d == null || map.ContainsKey(d)) continue;
                var n = new Node { id = list.Count, def = d, band = (int)d.techLevel, knowledge = d.knowledgeCategory != null };
                n.label = SafeLabel(d);
                ModContentPack m = d.modContentPack;
                n.mod = m?.Name ?? "?";
                n.modId = m?.PackageIdPlayerFacing ?? n.mod;
                n.modColor = ModColor(n.modId, m != null && m.IsOfficialMod);
                list.Add(n);
                map[d] = n;
            }
            foreach (Node n in list)
            {
                var seen = new HashSet<int>();
                if (n.def.prerequisites != null)
                    foreach (ResearchProjectDef p in n.def.prerequisites)
                        if (p != null && p != n.def && map.TryGetValue(p, out Node pn) && seen.Add(pn.id)) { n.pre.Add(pn.id); pn.post.Add(n.id); }
            }
            // what each project unlocks – one pass over the defs instead of ResearchProjectDef.UnlockedDefs
            // per project (that one scans every def again for each of thousands of projects)
            void Add(ResearchProjectDef r, Def what) { if (r != null && what != null && map.TryGetValue(r, out Node n) && !n.unlocks.Contains(what)) n.unlocks.Add(what); }
            try
            {
                foreach (RecipeDef rd in DefDatabase<RecipeDef>.AllDefsListForReading)
                {
                    Def prod = rd.IsSurgery ? (Def)rd : (rd.products != null && rd.products.Count > 0 ? rd.products[0].thingDef : null);
                    if (prod == null) continue;
                    Add(rd.researchPrerequisite, prod);
                    if (rd.researchPrerequisites != null) foreach (ResearchProjectDef r in rd.researchPrerequisites) Add(r, prod);
                }
                foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (td.researchPrerequisites != null) foreach (ResearchProjectDef r in td.researchPrerequisites) Add(r, td);
                    if (td.plant?.sowResearchPrerequisites != null) foreach (ResearchProjectDef r in td.plant.sowResearchPrerequisites) Add(r, td);
                }
                foreach (TerrainDef td in DefDatabase<TerrainDef>.AllDefsListForReading)
                    if (td.researchPrerequisites != null) foreach (ResearchProjectDef r in td.researchPrerequisites) Add(r, td);
            }
            catch (Exception e) { Log.Warning("[Research Atlas] unlock index: " + e.Message); }

            var mc = new Dictionary<string, int>();
            foreach (Node n in list)
            {
                n.unlocks.Sort((a, b) => Score(b).CompareTo(Score(a)));
                n.cat = Guess(n);
                var sb = new StringBuilder();
                sb.Append(n.label).Append(' ').Append(n.def.description).Append(' ').Append(n.mod).Append(' ').Append(n.def.defName);
                foreach (Def u in n.unlocks) sb.Append(' ').Append(u.label);
                n.search = sb.ToString().ToLowerInvariant();
                mc.TryGetValue(n.mod, out int c); mc[n.mod] = c + 1;
            }
            modCount = mc;
            mods = mc.Keys.OrderBy(k => k).ToList();
            byDef = map;
            nodes = list;
            Log.Message("[Research Atlas] " + list.Count + " research projects from " + mc.Count + " mods indexed in " + (int)(DateTime.Now - t0).TotalMilliseconds + " ms.");
        }

        private static string SafeLabel(ResearchProjectDef d)
        {
            try { string s = d.LabelCap; if (!s.NullOrEmpty()) return s; } catch { }
            return d.label ?? d.defName;
        }

        // the main unlocked thing first: buildings and weapons over recipes over terrain
        private static int Score(Def d)
        {
            if (d is ThingDef t)
            {
                if (t.IsWeapon) return 6;
                if (t.IsApparel) return 5;
                if (t.building != null) return 4;
                return 3;
            }
            if (d is RecipeDef) return 2;
            return 1;
        }

        private static Cat Guess(Node n)
        {
            var c = new int[8];
            foreach (Def d in n.unlocks)
            {
                if (d is RecipeDef r) { if (r.IsSurgery) c[(int)Cat.Medical] += 2; continue; }
                if (d is TerrainDef) { c[(int)Cat.Buildings]++; continue; }
                if (!(d is ThingDef t)) continue;
                if (t.IsWeapon) c[(int)Cat.Weapons] += 2;
                else if (t.IsApparel) c[(int)Cat.Apparel] += 2;
                else if (t.IsMedicine || t.IsDrug) c[(int)Cat.Medical] += 2;
                else if (t.plant != null || (t.ingestible != null && t.IsNutritionGivingIngestible)) c[(int)Cat.Food] += 2;
                else if (t.building != null)
                {
                    if (t.comps != null && t.comps.Any(cp => cp is CompProperties_Power pw && pw.PowerConsumption < 0f || cp?.compClass != null && cp.compClass.Name.Contains("Battery")))
                        c[(int)Cat.Power] += 2;
                    else if (t.IsWorkTable) c[(int)Cat.Production] += 2;
                    else if (t.building.bed_humanlike || t.IsBed || t.building.isSittable) c[(int)Cat.Buildings] += 1;
                    else c[(int)Cat.Buildings] += 1;
                }
                else c[(int)Cat.Production]++;
            }
            string s = (n.def.defName + " " + n.label).ToLowerInvariant();
            if (s.Contains("medic") || s.Contains("surg")) c[(int)Cat.Medical]++;
            if (s.Contains("power") || s.Contains("electric") || s.Contains("solar") || s.Contains("generator")) c[(int)Cat.Power]++;
            int best = (int)Cat.Other, bv = 0;
            for (int i = 0; i < 7; i++) if (c[i] > bv) { bv = c[i]; best = i; }
            return (Cat)best;
        }

        // the same colour for a mod every time; official content grey
        public static Color ModColor(string id, bool official)
        {
            if (official) return new Color(0.55f, 0.57f, 0.60f);
            uint h = 2166136261;
            foreach (char ch in id ?? "") { h ^= ch; h *= 16777619; }
            float hue = (h % 360) / 360f;
            return Color.HSVToRGB(hue, 0.55f, 0.85f);
        }

        public static Texture2D IconOf(Node n)
        {
            if (n.iconDone) return n.icon;
            n.iconDone = true;
            try
            {
                foreach (Def d in n.unlocks)
                {
                    Texture2D t = UnlockIcon(d);
                    if (t != null) { n.icon = t; break; }
                }
            }
            catch { }
            return n.icon;
        }

        public static Texture2D UnlockIcon(Def d)
        {
            Texture2D t = null;
            if (d is BuildableDef b) t = b.uiIcon;
            else if (d is RecipeDef r) t = r.ProducedThingDef?.uiIcon;
            if (t == null || t == BaseContent.BadTex) return null;
            return t;
        }
    }

    // ---------------------------------------------------------------- layout ----
    // Layered layout of a set of projects, pure data (runs on a worker thread):
    // tech level bands left to right, inside a band the depth by prerequisites, crossing reduction by
    // barycenters, tall columns wrapped so a band with 400 root projects stays on screen.
    public class LayoutResult
    {
        public Dictionary<int, Vector2> pos = new Dictionary<int, Vector2>();   // node id → (column, row)
        public List<(int band, float x0, float x1)> bands = new List<(int, float, float)>();
        public float width, height;
    }

    public class LayoutJob
    {
        public readonly string key;
        public volatile bool done;
        public LayoutResult result;
        public string error;

        private readonly int[] ids;
        private readonly int[][] pre;
        private readonly int[] band;
        private readonly int[] order;

        public LayoutJob(string key, List<Node> viewNodes)
        {
            this.key = key;
            int n = viewNodes.Count;
            ids = new int[n];
            pre = new int[n][];
            band = new int[n];
            order = new int[n];
            var local = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) local[viewNodes[i].id] = i;
            // initial order: mod, then name – projects of one mod stay together
            var sorted = Enumerable.Range(0, n).OrderBy(i => viewNodes[i].mod).ThenBy(i => viewNodes[i].label).ToList();
            for (int k = 0; k < n; k++) order[sorted[k]] = k;
            for (int i = 0; i < n; i++)
            {
                Node v = viewNodes[i];
                ids[i] = v.id;
                band[i] = v.band;
                var l = new List<int>();
                foreach (int p in v.pre) if (local.TryGetValue(p, out int li)) l.Add(li);
                pre[i] = l.ToArray();
            }
        }

        public void Start()
        {
            var th = new Thread(() =>
            {
                try { result = Compute(); }
                catch (Exception e) { error = e.Message; result = new LayoutResult(); }
                done = true;
            }) { IsBackground = true, Name = "ResearchAtlasLayout" };
            th.Start();
        }

        public void RunNow()
        {
            try { result = Compute(); } catch (Exception e) { error = e.Message; result = new LayoutResult(); }
            done = true;
        }

        private LayoutResult Compute()
        {
            int n = ids.Length;
            var res = new LayoutResult();
            if (n == 0) return res;
            // depth inside the band; a prerequisite from a lower band starts a new chain; cycles ignored
            var depth = new int[n];
            var state = new byte[n];   // 0 new, 1 on stack, 2 done
            for (int s = 0; s < n; s++)
            {
                if (state[s] != 0) continue;
                var stack = new Stack<(int node, int idx)>();
                stack.Push((s, 0)); state[s] = 1;
                while (stack.Count > 0)
                {
                    var (v, idx) = stack.Pop();
                    if (idx < pre[v].Length)
                    {
                        stack.Push((v, idx + 1));
                        int p = pre[v][idx];
                        if (state[p] == 0) { state[p] = 1; stack.Push((p, 0)); }
                        continue;
                    }
                    int d = 0;
                    foreach (int p in pre[v])
                        if (state[p] == 2 && band[p] == band[v]) d = Math.Max(d, depth[p] + 1);
                    depth[v] = d; state[v] = 2;
                }
            }
            var post = new List<int>[n];
            for (int i = 0; i < n; i++) post[i] = new List<int>();
            for (int i = 0; i < n; i++) foreach (int p in pre[i]) post[p].Add(i);

            // logical columns: (band, depth)
            var bandsUsed = band.Distinct().OrderBy(b => b).ToList();
            var cols = new List<List<int>>();
            var colBand = new List<int>();
            var colOf = new int[n];
            foreach (int b in bandsUsed)
            {
                int maxD = 0;
                for (int i = 0; i < n; i++) if (band[i] == b) maxD = Math.Max(maxD, depth[i]);
                int first = cols.Count;
                for (int d = 0; d <= maxD; d++) { cols.Add(new List<int>()); colBand.Add(b); }
                for (int i = 0; i < n; i++) if (band[i] == b) { colOf[i] = first + depth[i]; cols[first + depth[i]].Add(i); }
            }
            var rank = new float[n];
            foreach (var c in cols) { c.Sort((a, b2) => order[a].CompareTo(order[b2])); for (int k = 0; k < c.Count; k++) rank[c[k]] = k; }
            // barycenter sweeps, normalised by column size so columns of different heights line up
            float Norm(int i) { int cnt = cols[colOf[i]].Count; return cnt <= 1 ? 0.5f : rank[i] / (cnt - 1); }
            for (int sweep = 0; sweep < 6; sweep++)
            {
                bool forward = sweep % 2 == 0;
                for (int ci = forward ? 0 : cols.Count - 1; forward ? ci < cols.Count : ci >= 0; ci += forward ? 1 : -1)
                {
                    var c = cols[ci];
                    if (c.Count < 2) continue;
                    var bary = new Dictionary<int, float>();
                    foreach (int v in c)
                    {
                        float sum = 0; int cnt = 0;
                        foreach (int w in forward ? (IEnumerable<int>)pre[v] : post[v])
                            if (forward ? colOf[w] < ci : colOf[w] > ci) { sum += Norm(w); cnt++; }
                        bary[v] = cnt > 0 ? sum / cnt : Norm(v);
                    }
                    var sorted = c.OrderBy(v => bary[v]).ThenBy(v => rank[v]).ToList();
                    c.Clear(); c.AddRange(sorted);
                    for (int k = 0; k < c.Count; k++) rank[c[k]] = k;
                }
            }
            // physical columns: a column taller than the limit wraps into several
            int limit = Mathf.Clamp(Mathf.RoundToInt(Mathf.Sqrt(n) * 1.2f), 14, 60);
            float x = 0;
            int curBand = int.MinValue;
            float bandStart = 0;
            float maxRow = 0;
            for (int ci = 0; ci < cols.Count; ci++)
            {
                if (colBand[ci] != curBand)
                {
                    if (curBand != int.MinValue) { res.bands.Add((curBand, bandStart, x)); x += 0.5f; }
                    curBand = colBand[ci]; bandStart = x;
                }
                var c = cols[ci];
                if (c.Count == 0) continue;
                if (c.Count <= limit)
                {
                    // try to sit next to the prerequisites, keep the order, never past the limit
                    float last = -1;
                    for (int k = 0; k < c.Count; k++)
                    {
                        int v = c[k];
                        float want = last + 1;
                        float sum = 0; int cnt = 0;
                        foreach (int p in pre[v]) if (res.pos.TryGetValue(ids[p], out Vector2 pp) && pp.x < x) { sum += pp.y; cnt++; }
                        if (cnt > 0) want = Mathf.Max(last + 1, Mathf.Round(sum / cnt));
                        float room = limit - (c.Count - k);
                        want = Mathf.Min(want, Mathf.Max(last + 1, room));
                        res.pos[ids[v]] = new Vector2(x, want);
                        last = want;
                        maxRow = Mathf.Max(maxRow, want);
                    }
                    x += 1;
                }
                else
                {
                    for (int k = 0; k < c.Count; k++)
                    {
                        res.pos[ids[c[k]]] = new Vector2(x + k / limit, k % limit);
                        maxRow = Mathf.Max(maxRow, k % limit);
                    }
                    x += (c.Count + limit - 1) / limit;
                }
            }
            res.bands.Add((curBand, bandStart, x));
            res.width = x;
            res.height = maxRow + 1;
            return res;
        }
    }

    // finished layouts of whole tabs are kept on disk, so a 3000-project tree is arranged once per mod list
    public static class LayoutCache
    {
        private static string Folder => Path.Combine(GenFilePaths.ConfigFolderPath, "ResearchAtlas");

        public static string Hash(List<Node> view)
        {
            ulong h = 1469598103934665603;
            void Mix(string s) { foreach (char c in s) { h ^= c; h *= 1099511628211; } h ^= 0xFF; h *= 1099511628211; }
            Mix("v1");
            foreach (Node n in view.OrderBy(v => v.def.defName, StringComparer.Ordinal))
            {
                Mix(n.def.defName); Mix(n.band.ToString());
                foreach (int p in n.pre) Mix(Atlas.nodes[p].def.defName);
            }
            return h.ToString("x16");
        }

        public static LayoutResult Load(string hash)
        {
            try
            {
                string f = Path.Combine(Folder, hash + ".txt");
                if (!File.Exists(f)) return null;
                var r = new LayoutResult();
                foreach (string line in File.ReadAllLines(f))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 4) continue;
                    float a = float.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture);
                    float b = float.Parse(p[2], System.Globalization.CultureInfo.InvariantCulture);
                    float c = float.Parse(p[3], System.Globalization.CultureInfo.InvariantCulture);
                    if (p[0] == "#size") { r.width = a; r.height = b; continue; }
                    if (p[0] == "#band") { r.bands.Add(((int)a, b, c)); continue; }
                    ResearchProjectDef d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(p[0]);
                    if (d == null || !Atlas.byDef.TryGetValue(d, out Node n)) return null;   // stale → arrange again
                    r.pos[n.id] = new Vector2(a, b);
                }
                return r.pos.Count > 0 ? r : null;
            }
            catch { return null; }
        }

        public static void Save(string hash, LayoutResult r)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var sb = new StringBuilder();
                sb.Append("#size|").Append(r.width.ToString(ci)).Append('|').Append(r.height.ToString(ci)).Append("|0\n");
                foreach (var b in r.bands) sb.Append("#band|").Append(b.band).Append('|').Append(b.x0.ToString(ci)).Append('|').Append(b.x1.ToString(ci)).Append('\n');
                foreach (var kv in r.pos)
                    sb.Append(Atlas.nodes[kv.Key].def.defName).Append('|').Append(kv.Value.x.ToString(ci)).Append('|').Append(kv.Value.y.ToString(ci)).Append("|0\n");
                File.WriteAllText(Path.Combine(Folder, hash + ".txt"), sb.ToString());
                // keep the folder small: only the newest 40 layouts
                var files = new DirectoryInfo(Folder).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(40);
                foreach (var f in files) f.Delete();
            }
            catch (Exception e) { Log.Warning("[Research Atlas] layout cache: " + e.Message); }
        }
    }
}
