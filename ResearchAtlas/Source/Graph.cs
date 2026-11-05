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
        public List<Def> also = new List<Def>();    // 0.4.0: made possible indirectly (things made at unlocked benches)
        public string searchDeep = "";      // 0.4.0: descriptions of unlocked things + indirect things
        public List<int> pre = new List<int>();     // shown prerequisites (no self, no duplicates)
        public List<int> post = new List<int>();
        public string label;
        public Texture2D icon;              // resolved on the main thread when first drawn
        public bool iconDone;
        public Def main;                    // 0.3.0: main unlocked thing
        public bool mainDone;
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
                    // 0.4.0: every product, not only the first; recipes without products (bills) count as themselves
                    var prods = new List<Def>();
                    if (rd.IsSurgery || rd.products == null || rd.products.Count == 0) prods.Add(rd);
                    else foreach (ThingDefCountClass pc in rd.products) if (pc?.thingDef != null) prods.Add(pc.thingDef);
                    foreach (Def prod in prods)
                    {
                        Add(rd.researchPrerequisite, prod);
                        if (rd.researchPrerequisites != null) foreach (ResearchProjectDef r in rd.researchPrerequisites) Add(r, prod);
                    }
                }
                foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (td.researchPrerequisites != null) foreach (ResearchProjectDef r in td.researchPrerequisites) Add(r, td);
                    if (td.plant?.sowResearchPrerequisites != null) foreach (ResearchProjectDef r in td.plant.sowResearchPrerequisites) Add(r, td);
                }
                foreach (TerrainDef td in DefDatabase<TerrainDef>.AllDefsListForReading)
                    if (td.researchPrerequisites != null) foreach (ResearchProjectDef r in td.researchPrerequisites) Add(r, td);
                // 0.4.0: psychic rituals and what the project itself links in its description (mods use that a lot)
                foreach (Def pr in DefDatabase<PsychicRitualDef>.AllDefsListForReading)
                    if (pr is PsychicRitualDef prd) Add(prd.researchPrerequisite, prd);
                foreach (Node n in list)
                    if (n.def.descriptionHyperlinks != null)
                        foreach (DefHyperlink h in n.def.descriptionHyperlinks) if (h.def != null && !(h.def is ResearchProjectDef)) Add(n.def, h.def);
            }
            catch (Exception e) { Log.Warning("[Research Atlas] unlock index: " + e.Message); }

            // 0.4.0: what a project makes possible indirectly – things made at a workbench it unlocks
            // (the recipe itself needs no research, e.g. meals at an electric stove). Bench → recipes built
            // in one linear pass instead of RecipeDef.AllRecipeUsers (that scans every ThingDef per recipe).
            try
            {
                var benchRecipes = new Dictionary<ThingDef, List<RecipeDef>>();
                void Bench(ThingDef b, RecipeDef rd)
                {
                    if (b == null || rd == null) return;
                    if (!benchRecipes.TryGetValue(b, out var l)) benchRecipes[b] = l = new List<RecipeDef>();
                    if (!l.Contains(rd)) l.Add(rd);
                }
                foreach (RecipeDef rd in DefDatabase<RecipeDef>.AllDefsListForReading)
                    if (rd.recipeUsers != null) foreach (ThingDef b in rd.recipeUsers) Bench(b, rd);
                foreach (ThingDef td in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (td.recipes != null) foreach (RecipeDef rd in td.recipes) Bench(td, rd);
                foreach (Node n in list)
                {
                    var seen = new HashSet<Def>(n.unlocks);
                    foreach (Def u in n.unlocks)
                    {
                        if (!(u is ThingDef b) || !benchRecipes.TryGetValue(b, out var recs)) continue;
                        foreach (RecipeDef rd in recs)
                        {
                            bool own = rd.researchPrerequisite != null || (rd.researchPrerequisites != null && rd.researchPrerequisites.Count > 0);
                            if (own || rd.IsSurgery || rd.products == null) continue;
                            foreach (ThingDefCountClass pc in rd.products)
                                if (pc?.thingDef != null && seen.Add(pc.thingDef)) n.also.Add(pc.thingDef);
                        }
                    }
                }
            }
            catch (Exception e) { Log.Warning("[Research Atlas] indirect index: " + e.Message); }

            var mc = new Dictionary<string, int>();
            foreach (Node n in list)
            {
                n.unlocks.Sort((a, b) => Score(b).CompareTo(Score(a)));
                n.cat = Guess(n);
                var sb = new StringBuilder();
                sb.Append(n.label).Append(' ').Append(n.def.description).Append(' ').Append(n.mod).Append(' ').Append(n.def.defName);
                foreach (Def u in n.unlocks) sb.Append(' ').Append(u.label).Append(' ').Append(u.defName);
                n.search = sb.ToString().ToLowerInvariant();
                n.also.Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
                var deep = new StringBuilder();
                foreach (Def u in n.unlocks) if (!u.description.NullOrEmpty()) deep.Append(' ').Append(u.description);
                foreach (Def u in n.also) deep.Append(' ').Append(u.label).Append(' ').Append(u.defName);
                n.searchDeep = deep.ToString().ToLowerInvariant();
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

        // 0.3.0: the first unlocked thing that has a picture (for the card and the detail header)
        public static Def MainDef(Node n)
        {
            if (n.mainDone) return n.main;
            n.mainDone = true;
            try { foreach (Def d in n.unlocks) if (UnlockIcon(d) != null) { n.main = d; break; } }
            catch { }
            return n.main;
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
    // 0.2.0: a real left-to-right tree (like Research Tree / ResearchPal):
    //  - eras (tech levels) are consecutive column ranges, inside an era the column is the prerequisite depth,
    //    nothing wraps;
    //  - long edges get "dummy" points in the columns they pass, so lines run in free lanes and never cross cards;
    //  - row order by barycenter sweeps (crossing reduction), then y positions by an order-keeping least-squares
    //    fit to the neighbours (isotonic regression), so chains run straight.
    // Pure data, runs on a worker thread.
    public static class Geo
    {
        public const float CardW = 200f, CardH = 72f, GapX = 76f, GapY = 12f, DummyH = 12f, Left = 24f, Top = 12f;
        public static float X(float layer) => Left + layer * (CardW + GapX);
    }

    public class EdgeRoute
    {
        public int from, to;        // node ids
        public float[] ys;          // y (lane centre) in every column between them
        public bool back;           // prerequisite drawn to the right of its project (eras mixed up by a mod)
    }

    public class LayoutResult
    {
        public Dictionary<int, Vector2> pos = new Dictionary<int, Vector2>();   // node id → (column, top y in px)
        public List<EdgeRoute> edges = new List<EdgeRoute>();
        public List<(int band, float x0, float x1)> bands = new List<(int, float, float)>();   // column ranges
        public float width, height;     // columns, px
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
        private readonly int[] modRank;
        private readonly int[] nameRank;
        private readonly bool groupByMod;

        public LayoutJob(string key, List<Node> viewNodes, bool groupByMod)
        {
            this.key = key;
            this.groupByMod = groupByMod;
            int n = viewNodes.Count;
            ids = new int[n]; pre = new int[n][]; band = new int[n]; modRank = new int[n]; nameRank = new int[n];
            var local = new Dictionary<int, int>();
            for (int i = 0; i < n; i++) local[viewNodes[i].id] = i;
            var mods = viewNodes.Select(v => v.mod).Distinct().OrderBy(m => m, StringComparer.OrdinalIgnoreCase).ToList();
            var mi = new Dictionary<string, int>();
            for (int k = 0; k < mods.Count; k++) mi[mods[k]] = k;
            var byName = Enumerable.Range(0, n).OrderBy(i => viewNodes[i].mod, StringComparer.OrdinalIgnoreCase).ThenBy(i => viewNodes[i].label, StringComparer.OrdinalIgnoreCase).ToList();
            for (int k = 0; k < n; k++) nameRank[byName[k]] = k;
            for (int i = 0; i < n; i++)
            {
                Node v = viewNodes[i];
                ids[i] = v.id; band[i] = v.band; modRank[i] = mi[v.mod];
                var l = new List<int>();
                foreach (int p in v.pre) if (local.TryGetValue(p, out int li) && li != i && !l.Contains(li)) l.Add(li);
                pre[i] = l.ToArray();
            }
        }

        public void Start()
        {
            var th = new Thread(() =>
            {
                try { result = Compute(); }
                catch (Exception e) { error = e.ToString(); result = new LayoutResult(); }
                done = true;
            }) { IsBackground = true, Name = "ResearchAtlasLayout" };
            th.Start();
        }

        public void RunNow()
        {
            try { result = Compute(); } catch (Exception e) { error = e.ToString(); result = new LayoutResult(); }
            done = true;
        }

        // one item per column: a project or a point of a long edge
        private class Item { public int real = -1; public int layer; public float h; public float vis; public List<int> up = new List<int>(); public List<int> down = new List<int>(); public float y; public float rank; public float w = 1f; public int mod; }

        private LayoutResult Compute()
        {
            int n = ids.Length;
            var res = new LayoutResult();
            if (n == 0) return res;

            // edges used for the columns: prerequisite in the same or an earlier era
            var post = new List<int>[n];
            for (int i = 0; i < n; i++) post[i] = new List<int>();
            var fwd = new List<(int p, int v)>();
            var back = new List<(int p, int v)>();
            for (int v = 0; v < n; v++)
                foreach (int p in pre[v])
                {
                    if (band[p] <= band[v]) { fwd.Add((p, v)); post[p].Add(v); }
                    else back.Add((p, v));
                }
            var preF = new List<int>[n];
            for (int i = 0; i < n; i++) preF[i] = new List<int>();
            foreach (var (p, v) in fwd) preF[v].Add(p);

            // topological order (Kahn); what is left in a cycle goes last with its edges ignored
            var indeg = new int[n];
            foreach (var (p, v) in fwd) indeg[v]++;
            var topo = new List<int>(n);
            var q = new Queue<int>();
            for (int i = 0; i < n; i++) if (indeg[i] == 0) q.Enqueue(i);
            var inTopo = new bool[n];
            while (q.Count > 0) { int v = q.Dequeue(); topo.Add(v); inTopo[v] = true; foreach (int w in post[v]) if (--indeg[w] == 0) q.Enqueue(w); }
            for (int i = 0; i < n; i++) if (!inTopo[i]) topo.Add(i);
            var topoPos = new int[n];
            for (int k = 0; k < n; k++) topoPos[topo[k]] = k;

            // columns: eras one after another, depth inside the era
            var layer = new int[n];
            var bandsUsed = band.Distinct().OrderBy(b => b).ToList();
            int start = 0;
            var bandRange = new Dictionary<int, (int s, int e)>();
            foreach (int b in bandsUsed)
            {
                int end = start;
                foreach (int v in topo)
                {
                    if (band[v] != b) continue;
                    int l = start;
                    foreach (int p in preF[v]) if (topoPos[p] < topoPos[v]) l = Math.Max(l, layer[p] + 1);
                    layer[v] = l;
                    end = Math.Max(end, l);
                }
                // a project with nothing before it in its era moves right, next to what it opens
                for (int k = topo.Count - 1; k >= 0; k--)
                {
                    int v = topo[k];
                    if (band[v] != b) continue;
                    bool hasPre = false;
                    foreach (int p in preF[v]) if (band[p] == b) { hasPre = true; break; }
                    if (hasPre) continue;
                    int lim = int.MaxValue;
                    foreach (int w in post[v]) if (band[w] == b && topoPos[w] > topoPos[v]) lim = Math.Min(lim, layer[w] - 1);
                    if (lim != int.MaxValue && lim > layer[v]) layer[v] = lim;
                }
                bandRange[b] = (start, end);
                start = end + 1;
            }
            int layers = start;

            // projects without a line inside their era (none at all, or only from an earlier era): spread over
            // the columns of their era, so the first column of an era doesn't become a tower
            var count = new int[layers];
            var lone = new List<int>();
            for (int i = 0; i < n; i++)
            {
                bool inBand = false;
                foreach (int p in preF[i]) if (band[p] == band[i]) { inBand = true; break; }
                if (!inBand) foreach (int w in post[i]) if (band[w] == band[i]) { inBand = true; break; }
                if (!inBand) lone.Add(i);
                else count[layer[i]]++;
            }
            lone.Sort((a, b2) => band[a] != band[b2] ? band[a].CompareTo(band[b2]) : nameRank[a].CompareTo(nameRank[b2]));
            foreach (int v in lone)
            {
                var (s0, e0) = bandRange[band[v]];
                int best = s0;
                for (int l = s0; l <= e0; l++) if (count[l] < count[best]) best = l;
                layer[v] = best; count[best]++;
            }

            // items: projects + one point per column for long edges
            var items = new List<Item>(n * 2);
            for (int i = 0; i < n; i++) items.Add(new Item { real = i, layer = layer[i], h = Geo.CardH + Geo.GapY, vis = Geo.CardH, w = 1f, mod = modRank[i] });
            var chains = new List<(int p, int v, List<int> pts)>();
            foreach (var (p, v) in fwd)
            {
                if (layer[v] <= layer[p]) continue;       // a cycle left over
                var pts = new List<int>();
                int prev = p;
                for (int l = layer[p] + 1; l < layer[v]; l++)
                {
                    var d = new Item { layer = l, h = Geo.DummyH, vis = 0f, w = 0.35f, mod = modRank[p] };
                    items.Add(d);
                    int di = items.Count - 1;
                    items[prev].down.Add(di); d.up.Add(prev);
                    pts.Add(di); prev = di;
                }
                items[prev].down.Add(v); items[v].up.Add(prev);
                chains.Add((p, v, pts));
            }
            var cols = new List<int>[layers];
            for (int l = 0; l < layers; l++) cols[l] = new List<int>();
            for (int k = 0; k < items.Count; k++) cols[items[k].layer].Add(k);

            // order: first by mod/name, then barycenter sweeps
            float Key(int k) => items[k].real >= 0 ? nameRank[items[k].real] : 1e7f;
            for (int l = 0; l < layers; l++)
            {
                var c = cols[l];
                if (l > 0)
                {
                    foreach (int k in c) { var it = items[k]; it.rank = it.up.Count > 0 ? (float)it.up.Average(u => items[u].rank) : Key(k) / 1e3f; }
                    c.Sort((a, b2) => Cmp(a, b2));
                }
                else c.Sort((a, b2) => { int r = Key(a).CompareTo(Key(b2)); return r != 0 ? r : a.CompareTo(b2); });
                for (int k = 0; k < c.Count; k++) items[c[k]].rank = k;
            }
            int Cmp(int a, int b2)
            {
                if (groupByMod)
                {
                    int ma = items[a].mod, mb = items[b2].mod;
                    if (ma != mb) return ma.CompareTo(mb);
                }
                int r = items[a].rank.CompareTo(items[b2].rank);
                if (r != 0) return r;
                r = Key(a).CompareTo(Key(b2));
                return r != 0 ? r : a.CompareTo(b2);
            }
            var bary = new float[items.Count];
            for (int sweep = 0; sweep < 10; sweep++)
            {
                bool down = sweep % 2 == 0;
                for (int s = 0; s < layers; s++)
                {
                    int l = down ? s : layers - 1 - s;
                    var c = cols[l];
                    if (c.Count < 2) continue;
                    foreach (int k in c)
                    {
                        var nb = down ? items[k].up : items[k].down;
                        bary[k] = nb.Count > 0 ? (float)nb.Average(u => items[u].rank) : items[k].rank;
                    }
                    foreach (int k in c) items[k].rank = bary[k];
                    c.Sort((a, b2) => Cmp(a, b2));
                    for (int k = 0; k < c.Count; k++) items[c[k]].rank = k;
                }
            }

            // y: stack, then pull every item to the middle of its neighbours without changing the order
            for (int l = 0; l < layers; l++) { float y = 0; foreach (int k in cols[l]) { items[k].y = y; y += items[k].h; } }
            var desired = new float[items.Count];
            for (int it = 0; it < 12; it++)
            {
                int mode = it % 3;   // 0: from the left, 1: from the right, 2: both
                for (int s = 0; s < layers; s++)
                {
                    int l = mode == 1 ? layers - 1 - s : s;
                    var c = cols[l];
                    foreach (int k in c)
                    {
                        var me = items[k];
                        float sum = 0; int cnt = 0;
                        if (mode != 1) foreach (int u in me.up) { sum += items[u].y + items[u].vis / 2f; cnt++; }
                        if (mode != 0) foreach (int u in me.down) { sum += items[u].y + items[u].vis / 2f; cnt++; }
                        desired[k] = cnt > 0 ? sum / cnt - me.vis / 2f : me.y;
                    }
                    Fit(c, desired);
                }
            }
            float minY = float.MaxValue, maxY = 0;
            foreach (var it in items) { minY = Mathf.Min(minY, it.y); maxY = Mathf.Max(maxY, it.y + it.h); }
            foreach (var it in items) it.y -= minY;

            for (int i = 0; i < n; i++) res.pos[ids[i]] = new Vector2(layer[i], items[i].y);
            foreach (var (p, v, pts) in chains)
                res.edges.Add(new EdgeRoute { from = ids[p], to = ids[v], ys = pts.Select(d => items[d].y).ToArray() });
            foreach (var (p, v) in back) res.edges.Add(new EdgeRoute { from = ids[p], to = ids[v], ys = new float[0], back = true });
            foreach (int b in bandsUsed) res.bands.Add((b, bandRange[b].s, bandRange[b].e + 1));
            res.width = layers;
            res.height = maxY - minY;
            return res;

            // least squares positions that keep the order and the spacing (pool adjacent violators)
            void Fit(List<int> c, float[] want)
            {
                int m = c.Count;
                if (m == 0) return;
                var off = new float[m];
                for (int k = 1; k < m; k++) off[k] = off[k - 1] + items[c[k - 1]].h;
                var val = new List<float>(); var wt = new List<float>(); var len = new List<int>();
                for (int k = 0; k < m; k++)
                {
                    float t = want[c[k]] - off[k], w = items[c[k]].w;
                    val.Add(t); wt.Add(w); len.Add(1);
                    while (val.Count > 1 && val[val.Count - 2] > val[val.Count - 1])
                    {
                        int a = val.Count - 2, b2 = val.Count - 1;
                        float nw = wt[a] + wt[b2];
                        val[a] = (val[a] * wt[a] + val[b2] * wt[b2]) / nw; wt[a] = nw; len[a] += len[b2];
                        val.RemoveAt(b2); wt.RemoveAt(b2); len.RemoveAt(b2);
                    }
                }
                int idx = 0;
                for (int g = 0; g < val.Count; g++)
                    for (int r = 0; r < len[g]; r++, idx++) items[c[idx]].y = val[g] + off[idx];
            }
        }
    }

    // finished layouts are kept on disk, so a 3000-project tree is arranged once per mod list
    public static class LayoutCache
    {
        private static string Folder => Path.Combine(GenFilePaths.ConfigFolderPath, "ResearchAtlas");
        private static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;

        public static string Hash(List<Node> view, bool groupByMod)
        {
            ulong h = 1469598103934665603;
            void Mix(string s) { foreach (char c in s) { h ^= c; h *= 1099511628211; } h ^= 0xFF; h *= 1099511628211; }
            Mix("v2"); Mix(groupByMod ? "g" : "n");
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
                Node N(string defName) { var d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName); return d != null && Atlas.byDef.TryGetValue(d, out Node n) ? n : null; }
                foreach (string line in File.ReadAllLines(f))
                {
                    string[] p = line.Split('|');
                    if (p.Length < 4) continue;
                    if (p[0] == "#edge")
                    {
                        Node a = N(p[1]), b = N(p[2]);
                        if (a == null || b == null) return null;
                        var ys = p[3].Length == 0 ? new float[0] : p[3].Split(';').Select(s => float.Parse(s, CI)).ToArray();
                        r.edges.Add(new EdgeRoute { from = a.id, to = b.id, ys = ys, back = p.Length > 4 && p[4] == "b" });
                        continue;
                    }
                    float x = float.Parse(p[1], CI), y = float.Parse(p[2], CI), z = float.Parse(p[3], CI);
                    if (p[0] == "#size") { r.width = x; r.height = y; continue; }
                    if (p[0] == "#band") { r.bands.Add(((int)x, y, z)); continue; }
                    Node nd = N(p[0]);
                    if (nd == null) return null;   // stale → arrange again
                    r.pos[nd.id] = new Vector2(x, y);
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
                var sb = new StringBuilder();
                sb.Append("#size|").Append(r.width.ToString(CI)).Append('|').Append(r.height.ToString(CI)).Append("|0\n");
                foreach (var b in r.bands) sb.Append("#band|").Append(b.band).Append('|').Append(b.x0.ToString(CI)).Append('|').Append(b.x1.ToString(CI)).Append('\n');
                foreach (var kv in r.pos)
                    sb.Append(Atlas.nodes[kv.Key].def.defName).Append('|').Append(kv.Value.x.ToString(CI)).Append('|').Append(kv.Value.y.ToString(CI)).Append("|0\n");
                foreach (var e in r.edges)
                    sb.Append("#edge|").Append(Atlas.nodes[e.from].def.defName).Append('|').Append(Atlas.nodes[e.to].def.defName).Append('|')
                      .Append(string.Join(";", e.ys.Select(y => y.ToString("0.#", CI)))).Append('|').Append(e.back ? "b" : "f").Append('\n');
                File.WriteAllText(Path.Combine(Folder, hash + ".txt"), sb.ToString());
                var files = new DirectoryInfo(Folder).GetFiles("*.txt").OrderByDescending(f => f.LastWriteTimeUtc).Skip(40);
                foreach (var f in files) f.Delete();
            }
            catch (Exception e) { Log.Warning("[Research Atlas] layout cache: " + e.Message); }
        }
    }
}
