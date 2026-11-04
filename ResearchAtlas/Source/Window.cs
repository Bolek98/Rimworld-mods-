using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ResearchAtlas
{
    static class Look
    {
        public static readonly Color Bg = new Color32(21, 24, 28, 255);
        public static readonly Color Canvas = new Color32(16, 19, 23, 255);
        public static readonly Color Card = new Color32(44, 50, 58, 255);
        public static readonly Color Card2 = new Color32(56, 63, 72, 255);
        public static readonly Color Line = new Color32(58, 65, 72, 255);
        public static readonly Color Fg = new Color32(216, 221, 227, 255);
        public static readonly Color Dim = new Color32(165, 175, 188, 255);
        public static readonly Color Green = new Color32(124, 191, 94, 255);
        public static readonly Color GreenBg = new Color32(31, 42, 34, 255);
        public static readonly Color Gold = new Color32(242, 193, 78, 255);
        public static readonly Color Blue = new Color32(127, 188, 227, 255);
        public static readonly Color Red = new Color32(224, 100, 90, 255);

        // card states
        public static readonly Color DoneBg = new Color32(42, 64, 50, 255), DoneBorder = new Color32(96, 150, 112, 255), DoneText = new Color32(196, 222, 200, 255);
        public static readonly Color AvailBg = new Color32(56, 70, 88, 255), AvailBorder = new Color32(178, 202, 230, 255);
        public static readonly Color LockBg = new Color32(42, 46, 54, 255), LockBorder = new Color32(82, 90, 102, 255), LockText = new Color32(170, 177, 188, 255);
        public static readonly Color ProgBg = new Color32(82, 70, 34, 255);

        public static void Box(Rect r, Color bg, Color? border = null, int thick = 1)
        {
            Widgets.DrawBoxSolid(r, bg);
            if (border.HasValue) { GUI.color = border.Value; Widgets.DrawBox(r, thick); GUI.color = Color.white; }
        }

        public static void Label(Rect r, string s, Color c, GameFont f = GameFont.Small, TextAnchor a = TextAnchor.MiddleLeft)
        {
            Text.Font = f; Text.Anchor = a; GUI.color = c;
            try { Widgets.Label(r, s); }
            finally { GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft; Text.Font = GameFont.Small; }
        }

        public static bool Chip(Rect r, string s, bool on)
        {
            bool over = Mouse.IsOver(r);
            Box(r, on ? GreenBg : (over ? Card2 : Card), on ? Green : Line);
            Label(r, s, on ? Color.white : Fg, GameFont.Small, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        public static float ChipW(string s) { Text.Font = GameFont.Small; float w = Text.CalcSize(s).x + 22f; return w; }
    }

    // state shared by every opening of the window: layouts are kept while the game runs
    public static class AtlasView
    {
        public static readonly Dictionary<string, LayoutResult> mem = new Dictionary<string, LayoutResult>();
        public static void ClearCache()
        {
            mem.Clear();
            try { var d = System.IO.Path.Combine(GenFilePaths.ConfigFolderPath, "ResearchAtlas"); if (System.IO.Directory.Exists(d)) System.IO.Directory.Delete(d, true); } catch { }
        }
    }

    // a line between two projects, in world coordinates, ready to draw
    class Poly
    {
        public int from, to;
        public Vector2[] pts;
        public Rect bb;
        public bool back;
    }

    public class MainTabWindow_Atlas : MainTabWindow_Research
    {
        const float TopH = 40f, TabsH = 30f, QueueH = 72f, HeadH = 24f, SideW = 260f;
        const float MinZoom = 0.2f, MaxZoom = 1.6f;

        static bool vanillaMode;
        static bool failedOnce;

        // filters (kept while the game runs)
        enum TabMode { All, One, Other }
        static TabMode tabMode = TabMode.All;
        static ResearchTabDef tab;
        static bool onlyAvailable, hideFinished;
        static string modFilter;
        static int catFilter = -1, techFilter = -1;
        static int focusId = -1;
        static string searchText = "";
        static Vector2 savedPan = new Vector2(float.NaN, 0);
        static float savedZoom = 1f;
        static string savedFor;

        // view
        List<Node> view = new List<Node>();
        HashSet<int> viewSet = new HashSet<int>();
        HashSet<int> dimmed = new HashSet<int>();
        string viewHash;
        LayoutResult layout;
        LayoutJob job;
        bool jobPlain;
        bool dirty = true;
        Vector2 pan;
        float zoom = 1f;
        bool needHome = true;
        List<Poly> polys = new List<Poly>();
        LayoutResult polysFor;
        Texture2D minimap;
        string minimapFor;

        // search
        List<int> matches = new List<int>();
        HashSet<int> matchSet = new HashSet<int>();
        HashSet<int> pathSet = new HashSet<int>();
        int matchCursor = -1;
        string searchFor;

        // input
        bool dragging, moved;
        Vector2 downPos;
        int downNode = -1;
        int downButton;
        int hover = -1;
        int relFor = -2;
        HashSet<int> rel = new HashSet<int>();
        readonly List<(Rect r, Def d)> iconHits = new List<(Rect, Def)>();
        int iconHitsFor = -1;
        ResearchProjectDef lastSelected;
        int pendingCenter = -1;
        int flash = -1; float flashUntil;

        // state cache
        enum St { Done, Progress, Available, Locked }
        readonly Dictionary<int, St> st = new Dictionary<int, St>();
        readonly Dictionary<int, string> warn = new Dictionary<int, string>();
        int stFrame = -999;
        float rate; float rateAt = -99; int researchers;
        readonly List<(Pawn p, float speed, bool now)> who = new List<(Pawn, float, bool)>();

        // text cache: wrapped names per node, font and width
        readonly Dictionary<long, string> wrapCache = new Dictionary<long, string>();

        public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight - 35f);

        public override void PreOpen()
        {
            try { base.PreOpen(); } catch (Exception e) { Log.Warning("[Research Atlas] vanilla PreOpen: " + e.Message); }
            try { Atlas.Build(); dirty = true; st.Clear(); stFrame = -999; lastSelected = selectedProject; }
            catch (Exception e) { Fail(e); }
        }

        public override void PostClose()
        {
            base.PostClose();
            savedPan = pan; savedZoom = zoom; savedFor = viewHash;
        }

        public override void DoWindowContents(Rect inRect)
        {
            windowRect.width = UI.screenWidth;
            if (vanillaMode || !Atlas.Ready)
            {
                base.DoWindowContents(inRect);
                float leftW = Mathf.Max(270f, inRect.width * 0.22f);
                if (!failedOnce && Widgets.ButtonText(new Rect(leftW - 300f, 2f, 146f, 26f), "Research Atlas")) { vanillaMode = false; dirty = true; }
                return;
            }
            try { Draw(inRect); }
            catch (Exception e) { Fail(e); }
            finally { GUI.color = Color.white; Text.Anchor = TextAnchor.UpperLeft; Text.Font = GameFont.Small; }
        }

        void Fail(Exception e)
        {
            if (!failedOnce) Log.Error("[Research Atlas] the window failed, the vanilla research window is used instead: " + e);
            failedOnce = true;
            vanillaMode = true;
        }

        // ------------------------------------------------------------------ view ----
        static bool Visible(Node n)
        {
            ResearchProjectDef d = n.def;
            try
            {
                if (!Find.Storyteller.difficulty.AllowedBy(d.hideWhen) && !Find.ResearchManager.IsCurrentProject(d)) return false;
                if (d.IsHidden) return false;
            }
            catch { }
            return true;
        }

        bool PassFilters(Node n)
        {
            if (hideFinished && n.def.IsFinished) return false;
            if (onlyAvailable && !n.def.CanStartNow) return false;
            if (modFilter != null && n.mod != modFilter) return false;
            if (catFilter >= 0 && (int)n.cat != catFilter) return false;
            if (techFilter >= 0 && n.band != techFilter) return false;
            return true;
        }

        // the tabs: everything, the main tab, each DLC tab, and one tab for all mod tabs together
        static bool OfficialTab(ResearchTabDef t) => t == null || t == ResearchTabDefOf.Main || (t.modContentPack != null && t.modContentPack.IsOfficialMod);

        static bool InTab(Node n)
        {
            switch (tabMode)
            {
                case TabMode.One: return n.def.tab == tab || (tab == ResearchTabDefOf.Main && n.def.tab == null);
                case TabMode.Other: return !OfficialTab(n.def.tab);
                default: return true;
            }
        }

        void RebuildView()
        {
            dirty = false;
            var set = new List<Node>();
            dimmed.Clear();
            HashSet<int> focus = focusId >= 0 ? Related(focusId) : null;
            foreach (Node n in Atlas.nodes)
            {
                if (!Visible(n)) continue;
                if (focus != null) { if (!focus.Contains(n.id)) continue; }
                else if (!InTab(n)) continue;
                bool pass = PassFilters(n);
                if (!pass && RAMod.S.compactFilters) continue;
                if (!pass) dimmed.Add(n.id);
                set.Add(n);
            }
            view = set;
            viewSet = new HashSet<int>(set.Select(v => v.id));
            bool group = RAMod.S.groupByMod;
            string hash = LayoutCache.Hash(set, group);
            if (hash == viewHash && layout != null) return;
            viewHash = hash;
            needHome = true;
            if (AtlasView.mem.TryGetValue(hash, out LayoutResult r)) { SetLayout(r); job = null; return; }
            bool plain = focusId < 0 && dimmed.Count == 0 && !onlyAvailable && !hideFinished && modFilter == null && catFilter < 0 && techFilter < 0;
            if (plain)
            {
                r = LayoutCache.Load(hash);
                if (r != null) { AtlasView.mem[hash] = r; SetLayout(r); job = null; return; }
            }
            SetLayout(null);
            job = new LayoutJob(hash, set, group);
            if (set.Count < 200) job.RunNow(); else job.Start();
            jobPlain = plain;
        }

        void SetLayout(LayoutResult r)
        {
            layout = r;
            polysFor = null;
            minimapFor = null;
        }

        HashSet<int> Related(int id)
        {
            var res = new HashSet<int> { id };
            var s1 = new Stack<int>(); s1.Push(id);
            while (s1.Count > 0) foreach (int p in Atlas.nodes[s1.Pop()].pre) if (res.Add(p)) s1.Push(p);
            s1.Push(id);
            var down = new HashSet<int>();
            while (s1.Count > 0) foreach (int p in Atlas.nodes[s1.Pop()].post) if (down.Add(p)) s1.Push(p);
            res.UnionWith(down);
            return res;
        }

        void PollJob()
        {
            if (job == null || !job.done) return;
            if (job.error != null) Log.Warning("[Research Atlas] arranging failed: " + job.error);
            AtlasView.mem[job.key] = job.result;
            if (jobPlain && job.error == null) LayoutCache.Save(job.key, job.result);
            SetLayout(job.result);
            job = null;
            needHome = true;
        }

        // ------------------------------------------------------------- geometry ----
        static Rect WorldCard(Vector2 p) => new Rect(Geo.X(p.x), Geo.Top + p.y, Geo.CardW, Geo.CardH);

        Rect ToScreen(Rect w) => new Rect(w.x * zoom - pan.x, w.y * zoom - pan.y, w.width * zoom, w.height * zoom);
        Vector2 ToScreen(Vector2 w) => new Vector2(w.x * zoom - pan.x, w.y * zoom - pan.y);
        Vector2 ToWorld(Vector2 s) => new Vector2((s.x + pan.x) / zoom, (s.y + pan.y) / zoom);

        // lines run right from the prerequisite, turn in a lane in the gap between two columns, and pass
        // columns at the y the layout kept free for them; lines of one prerequisite share their lane (bundles)
        void BuildPolys()
        {
            polysFor = layout;
            polys = new List<Poly>();
            if (layout == null) return;
            var raw = new List<(Poly poly, List<Vector2> pts, List<(int gap, int at, float ya, float yb, int key)> turns)>();
            foreach (EdgeRoute e in layout.edges)
            {
                if (!layout.pos.TryGetValue(e.from, out Vector2 a) || !layout.pos.TryGetValue(e.to, out Vector2 b)) continue;
                var poly = new Poly { from = e.from, to = e.to, back = e.back };
                float ya = Geo.Top + a.y + Geo.CardH / 2f, yb = Geo.Top + b.y + Geo.CardH / 2f;
                if (e.back)
                {
                    // rare: a prerequisite placed to the right – a plain line from its left side
                    poly.pts = new[] { new Vector2(Geo.X(a.x), ya), new Vector2(Geo.X(b.x) + Geo.CardW, yb) };
                    raw.Add((poly, null, null));
                    continue;
                }
                var ys = new List<float> { ya };
                foreach (float y in e.ys) ys.Add(Geo.Top + y);
                ys.Add(yb);
                var turns = new List<(int, int, float, float, int)>();
                int col = (int)a.x;
                for (int k = 0; k < ys.Count - 1; k++)
                {
                    int key = k == 0 ? e.from : (e.from * 7919 + e.to) * 31 + k;
                    turns.Add((col + k, k, ys[k], ys[k + 1], key));
                }
                raw.Add((poly, ys.Select((y, i) => new Vector2(col + i, y)).ToList(), turns));
            }
            // lanes per gap: one per prerequisite (or long line) that turns there
            var lanes = new Dictionary<int, Dictionary<int, float>>();
            var byGap = new Dictionary<int, List<(int key, float y)>>();
            foreach (var r in raw)
            {
                if (r.turns == null) continue;
                foreach (var t in r.turns)
                {
                    if (Mathf.Abs(t.ya - t.yb) < 1f) continue;
                    if (!byGap.TryGetValue(t.gap, out var l)) byGap[t.gap] = l = new List<(int, float)>();
                    l.Add((t.key, Mathf.Min(t.ya, t.yb)));
                }
            }
            foreach (var kv in byGap)
            {
                var keys = kv.Value.GroupBy(v => v.key).Select(g => (g.Key, g.Min(v => v.y))).OrderBy(v => v.Item2).Select(v => v.Key).ToList();
                var d = new Dictionary<int, float>();
                int cnt = keys.Count;
                float gx0 = Geo.X(kv.Key) + Geo.CardW + 10f, gw = Geo.GapX - 20f;
                // only a few lanes – lines far apart vertically can share one
                int laneCount = Mathf.Clamp(cnt, 1, 6);
                for (int i = 0; i < cnt; i++) d[keys[i]] = gx0 + gw * ((i % laneCount) + 0.5f) / laneCount;
                lanes[kv.Key] = d;
            }
            foreach (var r in raw)
            {
                Poly p = r.poly;
                if (r.turns != null)
                {
                    var pts = new List<Vector2>();
                    var ys = r.pts;
                    int col = (int)ys[0].x;
                    pts.Add(new Vector2(Geo.X(col) + Geo.CardW, ys[0].y));
                    for (int k = 0; k < ys.Count - 1; k++)
                    {
                        var t = r.turns[k];
                        float ya = ys[k].y, yb = ys[k + 1].y;
                        int c = col + k;
                        if (Mathf.Abs(ya - yb) >= 1f)
                        {
                            float lx = lanes.TryGetValue(c, out var d) && d.TryGetValue(t.key, out float x) ? x : Geo.X(c) + Geo.CardW + Geo.GapX / 2f;
                            pts.Add(new Vector2(lx, ya));
                            pts.Add(new Vector2(lx, yb));
                        }
                        pts.Add(new Vector2(Geo.X(c + 1), yb));
                        if (k < ys.Count - 2) pts.Add(new Vector2(Geo.X(c + 1) + Geo.CardW, yb));   // passes the column in its free lane
                    }
                    p.pts = pts.ToArray();
                }
                float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
                foreach (Vector2 v in p.pts) { x0 = Mathf.Min(x0, v.x); y0 = Mathf.Min(y0, v.y); x1 = Mathf.Max(x1, v.x); y1 = Mathf.Max(y1, v.y); }
                p.bb = Rect.MinMaxRect(x0 - 2f, y0 - 2f, x1 + 2f, y1 + 2f);
                polys.Add(p);
            }
        }

        float WorldW => layout == null ? 0f : Geo.X(layout.width) + 10f;
        float WorldH => layout == null ? 0f : Geo.Top * 2f + layout.height;

        void ClampPan(Rect canvas)
        {
            if (layout == null) return;
            float ww = WorldW * zoom, hh = WorldH * zoom;
            float mx = Mathf.Max(0f, ww - canvas.width * 0.5f), my = Mathf.Max(0f, hh - canvas.height * 0.5f);
            pan.x = Mathf.Clamp(pan.x, -canvas.width * 0.5f, mx);
            pan.y = Mathf.Clamp(pan.y, -canvas.height * 0.5f, my);
        }

        // where to look first: what is being researched, else the first available project
        void Home(Rect canvas)
        {
            needHome = false;
            if (layout == null || layout.pos.Count == 0) return;
            if (savedFor == viewHash && !float.IsNaN(savedPan.x)) { pan = savedPan; zoom = savedZoom; savedFor = null; return; }
            zoom = 1f;
            int target = -1;
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            if (cur != null && Atlas.byDef.TryGetValue(cur, out Node cn) && layout.pos.ContainsKey(cn.id)) target = cn.id;
            if (target < 0)
            {
                float best = float.MaxValue;
                foreach (Node n in view)
                    if (layout.pos.TryGetValue(n.id, out Vector2 p) && State(n) == St.Available && p.x * 10000f + p.y < best) { best = p.x * 10000f + p.y; target = n.id; }
            }
            if (target >= 0) CenterOn(canvas, target, false);
            else pan = new Vector2(-10f, -10f);
        }

        void CenterOn(Rect canvas, int id, bool keepZoom = true)
        {
            if (layout == null || !layout.pos.TryGetValue(id, out Vector2 p)) return;
            if (!keepZoom || zoom < 0.75f) zoom = 1f;
            Rect w = WorldCard(p);
            pan = w.center * zoom - new Vector2(canvas.width, canvas.height) / 2f;
        }

        // ------------------------------------------------------------------ draw ----
        void Draw(Rect inRect)
        {
            Look.Box(inRect, Look.Bg);
            if (dirty) RebuildView();
            PollJob();
            if (selectedProject != lastSelected)
            {
                // something outside (an info card link, an alert) asked for a project
                lastSelected = selectedProject;
                if (selectedProject != null && Atlas.byDef.TryGetValue(selectedProject, out Node sn))
                {
                    if (!viewSet.Contains(sn.id)) { tabMode = TabMode.All; focusId = -1; ResetFilters(); RebuildView(); }
                    pendingCenter = sn.id;
                }
            }
            if (Time.frameCount - stFrame > 45) { st.Clear(); warn.Clear(); stFrame = Time.frameCount; }
            Rate();

            Rect top = new Rect(inRect.x, inRect.y, inRect.width, TopH);
            Rect tabsR = new Rect(inRect.x, top.yMax, inRect.width, TabsH);
            Rect queueR = new Rect(inRect.x, inRect.yMax - QueueH, inRect.width, QueueH);
            float bodyY = tabsR.yMax + 4f, bodyH = queueR.y - bodyY - 4f;
            bool side = RAMod.S.minimap;
            Rect sideR = side ? new Rect(inRect.xMax - SideW, bodyY, SideW, bodyH) : Rect.zero;
            Rect area = new Rect(inRect.x, bodyY, inRect.width - (side ? SideW + 6f : 0f), bodyH);
            Rect head = new Rect(area.x, area.y, area.width, HeadH);
            Rect canvas = new Rect(area.x, head.yMax, area.width, area.height - HeadH);
            DrawTop(top);
            DrawTabs(tabsR);
            DrawCanvas(head, canvas);
            if (side) DrawSide(sideR, canvas);
            DrawQueue(queueR);
        }

        void ResetFilters() { onlyAvailable = false; hideFinished = false; modFilter = null; catFilter = -1; techFilter = -1; }

        void DrawTop(Rect r)
        {
            // right side first, so the left side knows where to stop
            float rx = r.xMax - 40f;   // room for the close button
            Rect B(float w) { rx -= w + 6f; return new Rect(rx, r.y + 6f, w, 28f); }
            Rect help = B(28f);
            Look.Chip(help, "?", false);
            TooltipHandler.TipRegion(help, "RA_Help".Translate());
            if (Look.Chip(B(Look.ChipW("RA_Vanilla".Translate())), "RA_Vanilla".Translate(), false)) { vanillaMode = true; return; }
            Type rf = Fixes.RFMod.Active ? typeof(Fixes.Dialog_ResearchFixer) : GenTypes.GetTypeInAnyAssembly("ResearchFixer.Dialog_ResearchFixer");
            if (rf != null && Look.Chip(B(Look.ChipW("RA_FixesButton".Translate())), "RA_FixesButton".Translate(), false))
            {
                try { Find.WindowStack.Add((Window)Activator.CreateInstance(rf)); } catch (Exception e) { Log.Warning("[Research Atlas] Research Fixer: " + e.Message); }
            }
            if (Look.Chip(B(Look.ChipW("RA_Panel".Translate())), "RA_Panel".Translate(), RAMod.S.minimap)) { RAMod.S.minimap = !RAMod.S.minimap; RAMod.Instance.WriteSettings(); }
            if (Look.Chip(B(Look.ChipW("RA_Home".Translate())), "RA_Home".Translate(), false)) { needHome = true; savedFor = null; }
            float stop = rx - 6f;

            float x = r.x;
            Look.Label(new Rect(x, r.y, 130f, r.height - 4f), "RA_Title".Translate(), Color.white, GameFont.Medium);
            x += 130f;
            Rect sr = new Rect(x, r.y + 6f, 260f, 28f);
            GUI.SetNextControlName("RA_Search");
            string ns = Widgets.TextField(sr, searchText);
            if (searchText.NullOrEmpty() && GUI.GetNameOfFocusedControl() != "RA_Search")
                Look.Label(sr.ContractedBy(6f, 0f), "RA_SearchHint".Translate(), Look.Dim, GameFont.Tiny);
            if (ns != searchText) { searchText = ns; searchFor = null; }
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && GUI.GetNameOfFocusedControl() == "RA_Search")
            {
                NextMatch(); Event.current.Use();
            }
            x = sr.xMax + 6f;
            bool Chip(string label, bool on)
            {
                float w = Look.ChipW(label);
                if (x + w > stop) return false;
                Rect c = new Rect(x, r.y + 6f, w, 28f); x += w + 6f; return Look.Chip(c, label, on);
            }
            if (!searchText.NullOrEmpty())
            {
                string cnt = matches.Count == 0 ? "RA_NoMatch".Translate().ToString() : (matchCursor + 1) + "/" + matches.Count + " ▸";
                float w = Look.ChipW(cnt);
                Rect cr = new Rect(x, r.y + 6f, w, 28f);
                if (Look.Chip(cr, cnt, false)) NextMatch();
                TooltipHandler.TipRegion(cr, "RA_NextMatchTip".Translate());
                x = cr.xMax + 6f;
            }
            if (Chip("RA_OnlyAvailable".Translate(), onlyAvailable)) { onlyAvailable = !onlyAvailable; dirty = true; }
            if (Chip("RA_HideFinished".Translate(), hideFinished)) { hideFinished = !hideFinished; dirty = true; }
            if (Chip("RA_GroupByMod".Translate(), RAMod.S.groupByMod)) { RAMod.S.groupByMod = !RAMod.S.groupByMod; RAMod.Instance.WriteSettings(); dirty = true; viewHash = null; }
            string ml = modFilter == null ? "RA_ModAll".Translate().ToString() : modFilter;
            if (Chip(("RA_Mod".Translate() + ": " + ml).Truncate(200f) + " ▾", modFilter != null))
                Find.WindowStack.Add(new Dialog_ModFilter(m => { modFilter = m; dirty = true; }));
            string cl = catFilter < 0 ? "RA_All".Translate().ToString() : CatLabel((Cat)catFilter);
            if (Chip("RA_Cat".Translate() + ": " + cl + " ▾", catFilter >= 0))
            {
                var o = new List<FloatMenuOption> { new FloatMenuOption("RA_All".Translate(), () => { catFilter = -1; dirty = true; }) };
                foreach (Cat c in Enum.GetValues(typeof(Cat))) { Cat cc = c; o.Add(new FloatMenuOption(CatLabel(cc) + " (" + Atlas.nodes.Count(n => n.cat == cc) + ")", () => { catFilter = (int)cc; dirty = true; })); }
                Find.WindowStack.Add(new FloatMenu(o));
            }
            string tl = techFilter < 0 ? "RA_All".Translate().ToString() : ((TechLevel)techFilter).ToStringHuman().CapitalizeFirst();
            if (Chip("RA_Era".Translate() + ": " + tl + " ▾", techFilter >= 0))
            {
                var o = new List<FloatMenuOption> { new FloatMenuOption("RA_All".Translate(), () => { techFilter = -1; dirty = true; }) };
                foreach (int b in Atlas.nodes.Select(n => n.band).Distinct().OrderBy(b => b)) { int bb = b; o.Add(new FloatMenuOption(((TechLevel)bb).ToStringHuman().CapitalizeFirst(), () => { techFilter = bb; dirty = true; })); }
                Find.WindowStack.Add(new FloatMenu(o));
            }
            if (focusId >= 0 && Chip(("RA_Focus".Translate(Atlas.nodes[focusId].label) + " ✕").Truncate(260f), true)) { focusId = -1; dirty = true; }
            if (onlyAvailable || hideFinished || modFilter != null || catFilter >= 0 || techFilter >= 0)
                if (Chip("RA_Reset".Translate(), false)) { ResetFilters(); dirty = true; }
        }

        void DrawTabs(Rect r)
        {
            float x = r.x;
            int all = 0, other = 0;
            var counts = new Dictionary<ResearchTabDef, int>();
            foreach (Node n in Atlas.nodes)
            {
                if (!Visible(n)) continue;
                all++;
                ResearchTabDef t = n.def.tab ?? ResearchTabDefOf.Main;
                if (!OfficialTab(t)) { other++; continue; }
                counts.TryGetValue(t, out int c); counts[t] = c + 1;
            }
            bool Tab(string label, bool on)
            {
                Text.Font = GameFont.Small;
                float w = Mathf.Max(80f, Text.CalcSize(label).x + 28f);
                Rect rr = new Rect(x, r.y, w, r.height);
                x += w + 3f;
                if (rr.xMax > r.xMax) return false;
                bool over = Mouse.IsOver(rr);
                Look.Box(rr, on ? Look.Card2 : (over ? Look.Card : Look.Bg), Look.Line);
                if (on) Widgets.DrawBoxSolid(new Rect(rr.x, rr.yMax - 2f, rr.width, 2f), Look.Gold);
                Look.Label(rr, label, on ? Color.white : Look.Dim, GameFont.Small, TextAnchor.MiddleCenter);
                return Widgets.ButtonInvisible(rr);
            }
            bool inFocus = focusId >= 0;
            if (Tab("RA_AllTabs".Translate() + " (" + all + ")", tabMode == TabMode.All && !inFocus)) { tabMode = TabMode.All; focusId = -1; dirty = true; }
            var order = counts.Keys.OrderBy(t => t == ResearchTabDefOf.Main ? 0 : 1).ThenBy(t => t.index).ToList();
            foreach (ResearchTabDef t in order)
                if (Tab(t.LabelCap + " (" + counts[t] + ")", tabMode == TabMode.One && tab == t && !inFocus)) { tabMode = TabMode.One; tab = t; focusId = -1; dirty = true; }
            if (other > 0 && Tab("RA_OtherTabs".Translate() + " (" + other + ")", tabMode == TabMode.Other && !inFocus)) { tabMode = TabMode.Other; focusId = -1; dirty = true; }
        }

        // ---------------------------------------------------------------- canvas ----
        void DrawCanvas(Rect head, Rect canvas)
        {
            Look.Box(head, new Color32(24, 28, 33, 255), Look.Line);
            Look.Box(canvas, Look.Canvas, Look.Line);
            if (layout == null)
            {
                Look.Label(canvas, job != null ? "RA_Arranging".Translate(view.Count).ToString() : "RA_Empty".Translate().ToString(), Look.Dim, GameFont.Medium, TextAnchor.MiddleCenter);
                return;
            }
            if (layout.pos.Count == 0) { Look.Label(canvas, "RA_Empty".Translate(), Look.Dim, GameFont.Medium, TextAnchor.MiddleCenter); return; }
            if (polysFor != layout) BuildPolys();
            if (needHome) Home(canvas);
            if (pendingCenter >= 0) { CenterOn(canvas, pendingCenter); flash = pendingCenter; flashUntil = Time.realtimeSinceStartup + 2.5f; pendingCenter = -1; }
            UpdateSearch();
            HandleInput(canvas);
            ClampPan(canvas);

            // era headers (stay on top while the tree moves up and down)
            GUI.BeginGroup(head);
            try { DrawBandHeads(new Rect(0, 0, head.width, head.height)); }
            finally { GUI.EndGroup(); }

            GUI.BeginGroup(canvas);
            Rect local = new Rect(0, 0, canvas.width, canvas.height);
            try
            {
                DrawBandBacks(local);
                Vector2 mouse = Event.current.mousePosition;
                bool mouseIn = local.Contains(mouse);
                hover = -1;
                Rect viewW = new Rect(pan.x / zoom, pan.y / zoom, local.width / zoom, local.height / zoom);
                var vis = new List<(Node n, Rect r)>();
                foreach (Node n in view)
                {
                    if (!layout.pos.TryGetValue(n.id, out Vector2 p)) continue;
                    Rect w = WorldCard(p);
                    if (!w.Overlaps(viewW)) continue;
                    Rect sr = ToScreen(w);
                    vis.Add((n, sr));
                    if (mouseIn && sr.Contains(mouse)) hover = n.id;
                }
                bool ctrl = Event.current.control;
                if (ctrl && hover >= 0) { if (relFor != hover) { rel = Related(hover); relFor = hover; } }
                else if (relFor != -2) { rel = null; relFor = -2; }
                DrawEdges(local, viewW);
                ResearchQueue q = ResearchQueue.Get;
                if (hover != iconHitsFor) iconHits.Clear();
                foreach (var (n, r) in vis) DrawCard(n, r, q);
                if (hover >= 0)
                {
                    Node hn = Atlas.nodes[hover];
                    Rect hr = vis.First(v => v.n.id == hover).r;
                    bool onIcon = false;
                    foreach (var ih in iconHits)
                        if (ih.r.Contains(mouse)) { onIcon = true; TooltipHandler.TipRegion(ih.r, ih.d.LabelCap + "\n" + "RA_IconClick".Translate().Colorize(ColoredText.SubtleGrayColor)); break; }
                    if (!onIcon) TooltipHandler.TipRegion(hr, new TipSignal(() => Tip(hn), hn.id * 31 + 7));
                }
            }
            finally { GUI.EndGroup(); }
        }

        void DrawBandBacks(Rect local)
        {
            for (int i = 0; i < layout.bands.Count; i++)
            {
                var b = layout.bands[i];
                float x0 = (Geo.X(b.x0) - Geo.GapX / 2f) * zoom - pan.x;
                float x1 = (Geo.X(b.x1) - Geo.GapX / 2f) * zoom - pan.x;
                if (x1 < 0 || x0 > local.width) continue;
                if (i % 2 == 1) Widgets.DrawBoxSolid(new Rect(x0, 0, x1 - x0, local.height), new Color(1, 1, 1, 0.022f));
                Widgets.DrawBoxSolid(new Rect(x0, 0, 1f, local.height), new Color(1, 1, 1, 0.06f));
            }
        }

        void DrawBandHeads(Rect local)
        {
            for (int i = 0; i < layout.bands.Count; i++)
            {
                var b = layout.bands[i];
                float x0 = (Geo.X(b.x0) - Geo.GapX / 2f) * zoom - pan.x;
                float x1 = (Geo.X(b.x1) - Geo.GapX / 2f) * zoom - pan.x;
                if (x1 < 0 || x0 > local.width) continue;
                Rect br = new Rect(x0, 0, x1 - x0, local.height);
                if (i % 2 == 1) Widgets.DrawBoxSolid(br, new Color(1, 1, 1, 0.03f));
                Widgets.DrawBoxSolid(new Rect(x0, 0, 1f, local.height), new Color(1, 1, 1, 0.08f));
                // the label stays visible while its era is on screen
                string label = ((TechLevel)b.band).ToStringHuman().CapitalizeFirst();
                Text.Font = GameFont.Small;
                float lw = Text.CalcSize(label).x + 12f;
                float lx = Mathf.Clamp(Mathf.Max(x0 + 8f, 8f), x0 + 4f, Mathf.Max(x0 + 4f, x1 - lw));
                Look.Label(new Rect(lx, 0, lw, local.height), label, new Color(1, 1, 1, 0.7f), GameFont.Small);
            }
        }

        // ------------------------------------------------------------------ edges ----
        void DrawEdges(Rect local, Rect viewW)
        {
            bool searching = matchSet.Count > 0;
            float t = Mathf.Max(1f, 2f * zoom);
            bool fine = zoom >= 0.5f;
            int drawn = 0;
            for (int pass = 0; pass < 2; pass++)
                foreach (Poly p in polys)
                {
                    if (!p.bb.Overlaps(viewW)) continue;
                    if (!viewSet.Contains(p.from) || !viewSet.Contains(p.to)) continue;
                    bool hi;
                    if (rel != null) hi = rel.Contains(p.from) && rel.Contains(p.to);
                    else hi = (hover >= 0 && (hover == p.from || hover == p.to)) || (searching && pathSet.Contains(p.from) && pathSet.Contains(p.to)) || (focusId >= 0 && (focusId == p.from || focusId == p.to));
                    if ((pass == 1) != hi) continue;
                    if (!hi && zoom < 0.32f && view.Count > 700) continue;   // far out on a huge tree: only the important lines
                    Color c;
                    if (hi) c = Look.Gold;
                    else
                    {
                        St sa = State(Atlas.nodes[p.from]), sb = State(Atlas.nodes[p.to]);
                        if (sb == St.Done) c = new Color(0.33f, 0.47f, 0.37f, 0.55f);
                        else if (sa == St.Done) c = new Color(0.62f, 0.68f, 0.76f, 0.75f);
                        else c = new Color(0.42f, 0.46f, 0.52f, 0.55f);
                        if (dimmed.Contains(p.to) || (searching && !pathSet.Contains(p.to)) || (rel != null)) c.a *= 0.3f;
                    }
                    float w = hi ? Mathf.Max(2f, 3f * zoom) : t;
                    DrawPoly(p, c, w, fine);
                    if (++drawn > 5000) return;
                }
        }

        void DrawPoly(Poly p, Color c, float w, bool fine)
        {
            Vector2[] pts = p.pts;
            if (p.back)
            {
                Widgets.DrawLine(ToScreen(pts[0]), ToScreen(pts[1]), c, w);
                return;
            }
            float r = fine ? 6f * zoom : 0f;   // rounded (cut) corners
            for (int i = 0; i < pts.Length - 1; i++)
            {
                Vector2 a = ToScreen(pts[i]), b = ToScreen(pts[i + 1]);
                // shorten the segment at corners and join with a short diagonal
                Vector2 da = i > 0 ? Corner(pts, i, r) : Vector2.zero;
                Vector2 db = i + 1 < pts.Length - 1 ? Corner(pts, i + 1, r) : Vector2.zero;
                Vector2 dir = (b - a);
                float len = dir.magnitude;
                if (len < 0.01f) continue;
                dir /= len;
                Vector2 a2 = a + dir * Mathf.Min(da.magnitude, len / 2f), b2 = b - dir * Mathf.Min(db.magnitude, len / 2f);
                Seg(a2, b2, c, w);
                if (fine && i + 1 < pts.Length - 1 && db.sqrMagnitude > 0.01f)
                {
                    Vector2 nb = ToScreen(pts[i + 2]);
                    Vector2 nd = (nb - b); float nl = nd.magnitude;
                    if (nl > 0.01f) { nd /= nl; Widgets.DrawLine(b2, b + nd * Mathf.Min(db.magnitude, nl / 2f), c, w); }
                }
            }
            if (fine)
            {
                // a small arrowhead at the project
                Vector2 e = ToScreen(pts[pts.Length - 1]);
                float s = Mathf.Max(4f, 7f * zoom);
                Widgets.DrawLine(e, e + new Vector2(-s, -s * 0.6f), c, w);
                Widgets.DrawLine(e, e + new Vector2(-s, s * 0.6f), c, w);
            }
        }

        Vector2 Corner(Vector2[] pts, int i, float r)
        {
            if (r <= 0f) return Vector2.zero;
            Vector2 a = pts[i - 1], b = pts[i], c = pts[i + 1];
            bool turn = Mathf.Abs((b.x - a.x) * (c.y - b.y) - (b.y - a.y) * (c.x - b.x)) > 0.01f;
            return turn ? new Vector2(r, 0) : Vector2.zero;
        }

        static void Seg(Vector2 a, Vector2 b, Color c, float w)
        {
            if (Mathf.Abs(a.y - b.y) < 0.5f)
                Widgets.DrawBoxSolid(new Rect(Mathf.Min(a.x, b.x), a.y - w / 2f, Mathf.Abs(b.x - a.x), w), c);
            else if (Mathf.Abs(a.x - b.x) < 0.5f)
                Widgets.DrawBoxSolid(new Rect(a.x - w / 2f, Mathf.Min(a.y, b.y), w, Mathf.Abs(b.y - a.y)), c);
            else Widgets.DrawLine(a, b, c, w);
        }

        // ------------------------------------------------------------------ cards ----
        St State(Node n)
        {
            if (st.TryGetValue(n.id, out St s)) return s;
            ResearchProjectDef d = n.def;
            if (d.IsFinished) s = St.Done;
            else if (Find.ResearchManager.IsCurrentProject(d)) s = St.Progress;
            else if (d.CanStartNow) s = St.Available;
            else s = St.Locked;
            st[n.id] = s;
            return s;
        }

        // why a project with all prerequisites done still can't start
        string Warn(Node n)
        {
            if (warn.TryGetValue(n.id, out string w)) return w;
            w = null;
            try
            {
                ResearchProjectDef d = n.def;
                if (!d.IsFinished && d.PrerequisitesCompleted && !d.CanStartNow && n.knowledge == false)
                {
                    var l = new List<string>();
                    if (d.requiredResearchBuilding != null && !d.PlayerHasAnyAppropriateResearchBench) l.Add("RA_TipBench".Translate(d.requiredResearchBuilding.LabelCap));
                    if (!d.TechprintRequirementMet) l.Add("RA_TipTechprints".Translate(d.TechprintsApplied, d.TechprintCount));
                    if (!d.AnalyzedThingsRequirementsMet && d.requiredAnalyzed != null) foreach (var a in d.requiredAnalyzed) l.Add("RA_TipStudy".Translate(a.LabelCap));
                    if (!d.PlayerMechanitorRequirementMet) l.Add("RA_TipMechanitor".Translate());
                    if (l.Count > 0) w = string.Join("\n", l);
                }
            }
            catch { }
            warn[n.id] = w;
            return w;
        }

        string Wrapped(Node n, GameFont f, float width, int lines)
        {
            long key = ((long)n.id << 16) | ((long)f << 12) | ((long)lines << 10) | (long)Mathf.Clamp((int)(width / 4f), 0, 1023);
            if (wrapCache.TryGetValue(key, out string s)) return s;
            Text.Font = f;
            string[] words = n.label.Split(' ');
            var outL = new List<string>();
            string cur = "";
            int wi = 0;
            for (; wi < words.Length; wi++)
            {
                string tryS = cur.Length == 0 ? words[wi] : cur + " " + words[wi];
                if (Text.CalcSize(tryS).x <= width || cur.Length == 0)
                {
                    cur = tryS;
                    continue;
                }
                outL.Add(cur); cur = words[wi];
                if (outL.Count == lines - 1) { wi++; break; }
            }
            if (outL.Count == lines - 1 && wi < words.Length) cur = cur + " " + string.Join(" ", words.Skip(wi));
            outL.Add(cur);
            for (int i = 0; i < outL.Count; i++) if (Text.CalcSize(outL[i]).x > width) outL[i] = outL[i].Truncate(width);
            s = string.Join("\n", outL);
            Text.Font = GameFont.Small;
            wrapCache[key] = s;
            return s;
        }

        void DrawCard(Node n, Rect r, ResearchQueue q)
        {
            St s = State(n);
            bool searching = matchSet.Count > 0;
            bool match = matchSet.Contains(n.id);
            float alpha = 1f;
            if (dimmed.Contains(n.id)) alpha = 0.45f;
            if (searching && !match && !pathSet.Contains(n.id)) alpha = 0.4f;
            if (rel != null && !rel.Contains(n.id)) alpha = 0.35f;
            Color bg, border, text;
            switch (s)
            {
                case St.Done: bg = Look.DoneBg; border = Look.DoneBorder; text = Look.DoneText; break;
                case St.Progress: bg = Look.ProgBg; border = Look.Gold; text = Color.white; break;
                case St.Available: bg = Look.AvailBg; border = Look.AvailBorder; text = Color.white; break;
                default: bg = Look.LockBg; border = Look.LockBorder; text = Look.LockText; break;
            }
            int qi = q?.IndexOf(n.def) ?? -1;
            if (qi >= 0 && s != St.Progress) border = Look.Gold * 0.85f;
            bool flashing = flash == n.id && Time.realtimeSinceStartup < flashUntil;
            if (match || flashing || (rel != null && n.id == relFor)) border = Look.Gold;
            if (hover == n.id) bg = Color.Lerp(bg, Color.white, 0.07f);
            int thick = match || flashing || s == St.Progress || n.id == focusId ? 2 : 1;
            bg.a = alpha; border.a = alpha; text.a = alpha;
            Look.Box(r, bg, border, thick);
            float z = zoom;
            // mod stripe
            Color stripe = n.modColor; stripe.a = alpha * 0.9f;
            Widgets.DrawBoxSolid(new Rect(r.x + 1f, r.y + 1f, Mathf.Max(2f, 3f * z), r.height - 2f), stripe);
            float prog = n.def.ProgressPercent;
            if (s != St.Done && prog > 0f)
            {
                Rect pr = new Rect(r.x + 1f, r.yMax - Mathf.Max(2f, 4f * z) - 1f, (r.width - 2f), Mathf.Max(2f, 4f * z));
                Widgets.DrawBoxSolid(pr, new Color(0.1f, 0.12f, 0.14f, alpha));
                Widgets.DrawBoxSolid(new Rect(pr.x, pr.y, pr.width * prog, pr.height), new Color(Look.Green.r, Look.Green.g, Look.Green.b, alpha));
            }
            if (z < 0.42f)
            {
                if (qi >= 0) Badge(r, qi + 1, alpha);
                return;   // far out: coloured tiles
            }
            // icon
            float isz = (z >= 0.75f ? 38f : 30f) * z;
            Rect ic = new Rect(r.x + 8f * z, r.y + (z >= 0.75f ? 7f : (Geo.CardH - 30f) / 2f) * z, isz, isz);
            Widgets.DrawBoxSolid(ic, new Color(0.13f, 0.15f, 0.18f, alpha));
            Texture2D tex = Atlas.IconOf(n);
            GUI.color = new Color(1, 1, 1, alpha * (s == St.Locked ? 0.8f : 1f));
            if (tex != null) Widgets.DrawTextureFitted(ic.ContractedBy(2f), tex, 1f);
            else Look.Label(ic, ((TechLevel)n.band).ToStringHuman().Substring(0, 1).ToUpper(), new Color(1, 1, 1, alpha * 0.5f), GameFont.Small, TextAnchor.MiddleCenter);
            GUI.color = Color.white;
            float tx = ic.xMax + 6f * z;
            float badgeW = (qi >= 0 || s == St.Locked || s == St.Done) ? 20f * z + 4f : 0f;
            float tw = r.xMax - tx - 6f * z - badgeW;
            GameFont f = z >= 1.05f ? GameFont.Small : GameFont.Tiny;
            if (z >= 1.4f) f = GameFont.Small;
            string name = Wrapped(n, f, tw, 2);
            float nameH = z >= 0.75f ? 40f * z : r.height - 4f;
            Look.Label(new Rect(tx, r.y + 3f * z, tw + 2f, nameH), name, text, f, z >= 0.75f ? TextAnchor.UpperLeft : TextAnchor.MiddleLeft);
            if (s == St.Done) Look.Label(new Rect(r.xMax - 20f * z - 2f, r.y + 2f, 20f * z, 20f * z), "✓", Look.Green, GameFont.Small, TextAnchor.MiddleCenter);
            else if (qi >= 0) Badge(r, qi + 1, alpha);
            else if (s == St.Locked) Look.Label(new Rect(r.xMax - 20f * z - 2f, r.y + 2f, 20f * z, 20f * z), "🔒", new Color(1, 1, 1, 0.45f * alpha), GameFont.Tiny, TextAnchor.MiddleCenter);
            string wn = s != St.Done ? Warn(n) : null;
            if (wn != null)
            {
                Rect wr = new Rect(r.xMax - 20f * z - 2f, r.y + 22f * z, 18f * z, 18f * z);
                Widgets.DrawBoxSolid(wr, new Color(Look.Red.r, Look.Red.g, Look.Red.b, alpha));
                Look.Label(wr, "!", Color.white, GameFont.Tiny, TextAnchor.MiddleCenter);
            }
            if (z < 0.75f) return;
            // bottom row: what it unlocks, the cost
            float us = 18f * z;
            float uy = r.yMax - us - 7f * z;
            float ux = r.x + 8f * z;
            string cost = n.def.CostApparent.ToString("F0");
            Text.Font = GameFont.Tiny;
            float cw = Text.CalcSize(cost).x + 4f;
            float maxX = r.xMax - cw - 8f * z;
            int shown = 0;
            bool rec = hover == n.id;
            if (rec) { iconHits.Clear(); iconHitsFor = n.id; }
            foreach (Def u in n.unlocks)
            {
                if (shown >= 6 || ux + us > maxX - 22f * z) break;
                Texture2D ut = Atlas.UnlockIcon(u);
                if (ut == null) continue;
                Rect ur = new Rect(ux, uy, us, us);
                Widgets.DrawBoxSolid(ur, new Color(0.14f, 0.17f, 0.2f, alpha));
                GUI.color = new Color(1, 1, 1, alpha);
                Widgets.DrawTextureFitted(ur.ContractedBy(1f), ut, 1f);
                GUI.color = Color.white;
                if (rec) iconHits.Add((ur, u));
                ux += us + 3f * z; shown++;
            }
            int more = n.unlocks.Count - shown;
            if (more > 0 && shown > 0) Look.Label(new Rect(ux, uy, 30f * z, us), "+" + more, new Color(Look.Dim.r, Look.Dim.g, Look.Dim.b, alpha), GameFont.Tiny);
            Look.Label(new Rect(r.xMax - cw - 6f * z, uy, cw, us), cost, new Color(Look.Dim.r, Look.Dim.g, Look.Dim.b, alpha), GameFont.Tiny, TextAnchor.MiddleRight);
        }

        void Badge(Rect r, int num, float alpha)
        {
            float d = Mathf.Max(14f, 20f * zoom);
            Rect b = new Rect(r.xMax - d - 3f, r.y + 3f, d, d);
            Widgets.DrawBoxSolid(b, new Color(Look.Gold.r, Look.Gold.g, Look.Gold.b, Mathf.Max(alpha, 0.8f)));
            Look.Label(b, num.ToString(), Color.black, GameFont.Tiny, TextAnchor.MiddleCenter);
        }

        // --------------------------------------------------------------- side panel ----
        void DrawSide(Rect r, Rect canvas)
        {
            Look.Box(r, new Color32(18, 22, 27, 255), Look.Line);
            float y = r.y + 6f;
            // researchers
            Look.Label(new Rect(r.x + 10f, y, r.width - 20f, 22f), "RA_Researchers".Translate(), Color.white, GameFont.Small);
            y += 24f;
            if (who.Count == 0) { Look.Label(new Rect(r.x + 10f, y, r.width - 20f, 40f), "RA_AdvNoResearchers".Translate(), Look.Dim, GameFont.Tiny, TextAnchor.UpperLeft); y += 42f; }
            int shownW = 0;
            foreach (var (p, speed, now) in who.OrderByDescending(w => w.now).ThenByDescending(w => w.speed))
            {
                if (shownW >= 6) { Look.Label(new Rect(r.x + 10f, y, r.width - 20f, 18f), "+" + (who.Count - shownW), Look.Dim, GameFont.Tiny); y += 18f; break; }
                Rect row = new Rect(r.x + 10f, y, r.width - 20f, 20f);
                Widgets.DrawBoxSolid(new Rect(row.x, row.y + 6f, 8f, 8f), now ? Look.Green : Look.Line);
                Look.Label(new Rect(row.x + 14f, row.y, row.width - 70f, 20f), p.LabelShortCap.Truncate(row.width - 74f), now ? Color.white : Look.Fg, GameFont.Tiny);
                Look.Label(new Rect(row.xMax - 56f, row.y, 56f, 20f), speed.ToStringPercent(), Look.Dim, GameFont.Tiny, TextAnchor.MiddleRight);
                TooltipHandler.TipRegion(row, now ? "RA_ResearchingNow".Translate() : "RA_ResearchSpeedTip".Translate());
                y += 20f; shownW++;
            }
            y += 8f;
            // semi-random choices
            ResearchQueue q = ResearchQueue.Get;
            if (RAMod.S.semiRandom && q != null)
            {
                Widgets.DrawLineHorizontal(r.x + 6f, y, r.width - 12f); y += 6f;
                Look.Label(new Rect(r.x + 10f, y, r.width - 20f, 22f), "RA_RandomTitle".Translate(), Color.white, GameFont.Small);
                y += 24f;
                var offers = q.Offers();
                if (offers.Count == 0) { Look.Label(new Rect(r.x + 10f, y, r.width - 20f, 36f), "RA_RandomNone".Translate(), Look.Dim, GameFont.Tiny, TextAnchor.UpperLeft); y += 38f; }
                foreach (ResearchProjectDef d in offers)
                {
                    Rect or = new Rect(r.x + 10f, y, r.width - 20f, 30f);
                    if (Look.Chip(or, ((string)d.LabelCap).Truncate(or.width - 16f), Find.ResearchManager.IsCurrentProject(d))) { Begin(d); q.Pick(d); }
                    TooltipHandler.TipRegion(or, d.description ?? "");
                    y += 34f;
                }
                Rect rr = new Rect(r.x + 10f, y, r.width - 20f, 24f);
                if (offers.Count > 0 && Look.Chip(rr, "RA_RandomReroll".Translate(q.rerolls), false)) q.Reroll();
                y += 30f;
            }
            // minimap at the bottom of the panel
            float mh = Mathf.Min(220f, r.yMax - y - 10f);
            if (mh > 60f) DrawMinimap(new Rect(r.x + 8f, r.yMax - mh - 8f, r.width - 16f, mh), canvas);
        }

        void DrawMinimap(Rect mr, Rect canvas)
        {
            if (layout == null || layout.pos.Count == 0) return;
            float ww = WorldW, hh = WorldH;
            // keep the tree's proportions inside the box
            float sc = Mathf.Min(mr.width / ww, mr.height / hh);
            Rect inner = new Rect(mr.x + (mr.width - ww * sc) / 2f, mr.y + (mr.height - hh * sc) / 2f, ww * sc, hh * sc);
            if (minimapFor != viewHash || minimap == null || Time.frameCount - minimapFrame > 600) BuildMinimap(Mathf.Max(8, (int)inner.width), Mathf.Max(8, (int)inner.height));
            Look.Box(mr, new Color(0.05f, 0.06f, 0.08f, 1f), Look.Line);
            if (minimap != null) GUI.DrawTexture(inner, minimap);
            // search hits as dots
            foreach (int id in matches)
                if (layout.pos.TryGetValue(id, out Vector2 p))
                {
                    Rect w = WorldCard(p);
                    Widgets.DrawBoxSolid(new Rect(inner.x + w.center.x * sc - 2f, inner.y + w.center.y * sc - 2f, 4f, 4f), Look.Gold);
                }
            Rect vr = new Rect(inner.x + pan.x / zoom * sc, inner.y + pan.y / zoom * sc, canvas.width / zoom * sc, canvas.height / zoom * sc);
            vr = Rect.MinMaxRect(Mathf.Max(vr.xMin, mr.xMin), Mathf.Max(vr.yMin, mr.yMin), Mathf.Min(vr.xMax, mr.xMax), Mathf.Min(vr.yMax, mr.yMax));
            if (vr.width > 0 && vr.height > 0) { GUI.color = Look.Gold; Widgets.DrawBox(vr); GUI.color = Color.white; }
            if (Mouse.IsOver(mr) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag) && Event.current.button == 0)
            {
                Vector2 m = Event.current.mousePosition - inner.position;
                Vector2 w = new Vector2(m.x / sc, m.y / sc);
                pan = w * zoom - new Vector2(canvas.width, canvas.height) / 2f;
                Event.current.Use();
            }
            TooltipHandler.TipRegion(mr, "RA_MinimapTip".Translate());
        }

        int minimapFrame;

        void BuildMinimap(int w, int h)
        {
            minimapFor = viewHash;
            minimapFrame = Time.frameCount;
            try
            {
                if (minimap == null || minimap.width != w || minimap.height != h) minimap = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
                float ww = WorldW, hh = WorldH;
                foreach (var kv in layout.pos)
                {
                    Rect wr = WorldCard(kv.Value);
                    int x0 = (int)(wr.x / ww * w), y0 = (int)(wr.y / hh * h);
                    int x1 = Mathf.Max(x0 + 1, (int)(wr.xMax / ww * w)), y1 = Mathf.Max(y0 + 1, (int)(wr.yMax / hh * h));
                    Node n = Atlas.nodes[kv.Key];
                    St s = State(n);
                    Color32 c = s == St.Done ? new Color32(72, 110, 82, 255) : s == St.Available ? new Color32(150, 172, 196, 255) : s == St.Progress ? new Color32(242, 193, 78, 255) : new Color32(70, 76, 86, 255);
                    for (int y = y0; y < y1 && y < h; y++)
                        for (int x = x0; x < x1 && x < w; x++)
                            if (x >= 0 && y >= 0) px[(h - 1 - y) * w + x] = c;
                }
                minimap.SetPixels32(px);
                minimap.Apply();
            }
            catch (Exception e) { Log.Warning("[Research Atlas] minimap: " + e.Message); minimap = null; }
        }

        // -------------------------------------------------------------- input ----
        void HandleInput(Rect canvas)
        {
            Event ev = Event.current;
            Vector2 m = ev.mousePosition;
            bool inside = canvas.Contains(m);
            if (ev.type == EventType.ScrollWheel && inside)
            {
                float old = zoom;
                zoom = Mathf.Clamp(zoom * (ev.delta.y > 0 ? 0.88f : 1.13f), MinZoom, MaxZoom);
                Vector2 local = m - canvas.position;
                pan = (pan + local) * (zoom / old) - local;
                ev.Use();
                return;
            }
            if (ev.type == EventType.MouseDown && inside)
            {
                dragging = true; moved = false; downPos = m; downButton = ev.button;
                downNode = NodeAt(canvas, m);
                ev.Use();
            }
            else if (ev.type == EventType.MouseDrag && dragging)
            {
                if ((m - downPos).sqrMagnitude > 16f) moved = true;
                if (moved) pan -= ev.delta;
                ev.Use();
            }
            else if (ev.type == EventType.MouseUp && dragging)
            {
                dragging = false;
                if (!moved && downNode >= 0 && NodeAt(canvas, m) == downNode)
                {
                    Vector2 local = m - canvas.position;
                    if (downButton == 0 && iconHitsFor == downNode)
                        foreach (var ih in iconHits)
                            if (ih.r.Contains(local)) { Find.WindowStack.Add(new Dialog_InfoCard(ih.d)); ev.Use(); return; }
                    Click(Atlas.nodes[downNode], downButton, ev.shift, ev.control);
                }
                ev.Use();
            }
            else if (ev.type == EventType.KeyDown && ev.keyCode == KeyCode.Home && GUI.GetNameOfFocusedControl() != "RA_Search") { needHome = true; savedFor = null; ev.Use(); }
        }

        int NodeAt(Rect canvas, Vector2 m)
        {
            if (layout == null) return -1;
            Vector2 w = ToWorld(m - canvas.position);
            foreach (Node n in view)
            {
                if (!layout.pos.TryGetValue(n.id, out Vector2 p)) continue;
                if (WorldCard(p).Contains(w)) return n.id;
            }
            return -1;
        }

        void Click(Node n, int button, bool shift, bool ctrl)
        {
            ResearchQueue q = ResearchQueue.Get;
            if (button == 1) { Menu(n); return; }
            if (ctrl) { focusId = focusId == n.id ? -1 : n.id; dirty = true; return; }
            if (n.knowledge || q == null)
            {
                if (n.def.CanStartNow) Begin(n.def);
                return;
            }
            if (n.def.IsFinished) { Messages.Message("RA_AlreadyDone".Translate(n.label), MessageTypeDefOf.RejectInput, false); return; }
            if (shift) q.Add(n.def); else q.QueuePath(n.def);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            st.Clear();
        }

        void Begin(ResearchProjectDef d)
        {
            SoundDefOf.ResearchStart.PlayOneShotOnCamera();
            Find.ResearchManager.SetCurrentProject(d);
            st.Clear();
        }

        void Menu(Node n)
        {
            ResearchQueue q = ResearchQueue.Get;
            var o = new List<FloatMenuOption>();
            if (n.def.CanStartNow && !Find.ResearchManager.IsCurrentProject(n.def)) o.Add(new FloatMenuOption("RA_ResearchNow".Translate(), () => Begin(n.def)));
            if (q != null && !n.def.IsFinished && !n.knowledge)
            {
                o.Add(new FloatMenuOption("RA_QueuePath".Translate(), () => { q.QueuePath(n.def); st.Clear(); }));
                if (q.IndexOf(n.def) < 0) o.Add(new FloatMenuOption("RA_QueueOne".Translate(), () => { q.Add(n.def); st.Clear(); }));
                else o.Add(new FloatMenuOption("RA_Unqueue".Translate(), () => { q.Remove(n.def); st.Clear(); }));
            }
            o.Add(new FloatMenuOption(focusId == n.id ? "RA_Unfocus".Translate() : "RA_FocusOn".Translate(), () => { focusId = focusId == n.id ? -1 : n.id; dirty = true; }));
            o.Add(new FloatMenuOption("RA_InfoCard".Translate(), () => Find.WindowStack.Add(new Dialog_InfoCard(n.def))));
            o.Add(new FloatMenuOption("RA_OpenVanilla".Translate(), () => { vanillaMode = true; Select(n.def); }));
            Find.WindowStack.Add(new FloatMenu(o));
        }

        // -------------------------------------------------------------- search ----
        void UpdateSearch()
        {
            string want = (searchText ?? "").Trim().ToLowerInvariant();
            string key = want + "|" + viewHash;
            if (key == searchFor) return;
            searchFor = key;
            matches.Clear(); matchSet.Clear(); pathSet.Clear(); matchCursor = -1;
            if (want.Length < 2) return;
            foreach (Node n in view) if (n.search.Contains(want)) { matches.Add(n.id); matchSet.Add(n.id); }
            matches.Sort((a, b) =>
            {
                Vector2 pa = layout != null && layout.pos.TryGetValue(a, out var x) ? x : Vector2.zero, pb = layout != null && layout.pos.TryGetValue(b, out var y) ? y : Vector2.zero;
                int c = pa.x.CompareTo(pb.x); return c != 0 ? c : pa.y.CompareTo(pb.y);
            });
            var stack = new Stack<int>();
            foreach (int id in matches) { pathSet.Add(id); stack.Push(id); }
            while (stack.Count > 0) foreach (int p in Atlas.nodes[stack.Pop()].pre) if (viewSet.Contains(p) && pathSet.Add(p)) stack.Push(p);
        }

        void NextMatch()
        {
            if (matches.Count == 0) return;
            matchCursor = (matchCursor + 1) % matches.Count;
            pendingCenter = matches[matchCursor];
        }

        // --------------------------------------------------------------- tooltip ----
        string Tip(Node n)
        {
            var sb = new System.Text.StringBuilder();
            ResearchProjectDef d = n.def;
            sb.AppendLine(n.label.Colorize(ColoredText.TipSectionTitleColor));
            sb.AppendLine(n.mod + " · " + ((TechLevel)n.band).ToStringHuman() + " · " + CatLabel(n.cat));
            if (!d.IsFinished && !n.knowledge)
            {
                float left = d.CostApparent - d.ProgressApparent;
                sb.AppendLine("RA_TipCost".Translate(d.ProgressApparent.ToString("F0"), d.CostApparent.ToString("F0"), Eta(left)));
            }
            string w = Warn(n);
            if (w != null) { sb.AppendLine(); sb.AppendLine(("RA_TipCannotStart".Translate() + "\n" + w).Colorize(ColorLibrary.RedReadable)); }
            if (!d.description.NullOrEmpty()) { sb.AppendLine(); sb.AppendLine(d.description.Length > 600 ? d.description.Substring(0, 600) + "…" : d.description); }
            if (n.unlocks.Count > 0)
            {
                sb.AppendLine(); sb.AppendLine("RA_TipUnlocks".Translate().Colorize(ColoredText.TipSectionTitleColor));
                foreach (Def u in n.unlocks.Take(12)) sb.AppendLine("  " + u.LabelCap);
                if (n.unlocks.Count > 12) sb.AppendLine("  +" + (n.unlocks.Count - 12));
            }
            var pre = (d.prerequisites ?? new List<ResearchProjectDef>()).Where(p => p != null).ToList();
            if (pre.Count > 0)
            {
                sb.AppendLine(); sb.AppendLine("RA_TipNeeds".Translate().Colorize(ColoredText.TipSectionTitleColor));
                foreach (var p in pre) sb.AppendLine("  " + (p.IsFinished ? "✓ " : "✗ ") + (string)p.LabelCap + (p.tab != d.tab && p.tab != null ? " (" + (string)p.tab.LabelCap + ")" : ""));
            }
            var req = new List<string>();
            if (d.requiredResearchBuilding != null) req.Add("RA_TipBench".Translate(d.requiredResearchBuilding.LabelCap));
            if (d.requiredResearchFacilities != null) foreach (var f in d.requiredResearchFacilities) req.Add("RA_TipBench".Translate(f.LabelCap));
            if (d.TechprintCount > 0) req.Add("RA_TipTechprints".Translate(d.TechprintsApplied, d.TechprintCount));
            if (d.requiredAnalyzed != null) foreach (var a in d.requiredAnalyzed) req.Add("RA_TipStudy".Translate(a.LabelCap));
            if (d.requiresMechanitor) req.Add("RA_TipMechanitor".Translate());
            if (req.Count > 0) { sb.AppendLine(); foreach (string s in req) sb.AppendLine(s); }
            sb.AppendLine(); sb.Append("RA_TipClick".Translate().Colorize(ColoredText.SubtleGrayColor));
            return sb.ToString();
        }

        // research points per hour of the colonists who do research (about half of their day is research)
        float Rate()
        {
            if (Time.realtimeSinceStartup - rateAt < 2f) return rate;
            rateAt = Time.realtimeSinceStartup;
            float sum = 0; researchers = 0;
            who.Clear();
            try
            {
                WorkTypeDef wt = WorkTypeDefOf.Research;
                foreach (Pawn p in PawnsFinder.AllMaps_FreeColonistsSpawned)
                {
                    if (p.Downed || p.WorkTypeIsDisabled(wt) || p.workSettings == null || !p.workSettings.WorkIsActive(wt)) continue;
                    float sp = p.GetStatValue(StatDefOf.ResearchSpeed);
                    sum += sp;
                    researchers++;
                    who.Add((p, sp, p.CurJobDef == JobDefOf.Research));
                }
                rate = sum * 0.00825f * Find.Storyteller.difficulty.researchSpeedFactor * 2500f * 0.5f;
            }
            catch { rate = 0; }
            return rate;
        }

        string Eta(float points)
        {
            float r = Rate();
            if (r <= 0.01f) return "RA_EtaNever".Translate();
            float h = points / r;
            if (h < 24f) return "RA_EtaHours".Translate(Mathf.CeilToInt(h));
            return "RA_EtaDays".Translate((h / 24f).ToString("0.#"));
        }

        static string CatLabel(Cat c) => ("RA_Cat_" + c).Translate();

        // ---------------------------------------------------------------- queue ----
        void DrawQueue(Rect r)
        {
            Look.Box(r, new Color32(18, 22, 27, 255), Look.Line);
            ResearchQueue q = ResearchQueue.Get;
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            float x = r.x + 10f;
            Rect cr = new Rect(x, r.y + 6f, 250f, r.height - 12f);
            Look.Box(cr, Look.Card, cur != null ? Look.Gold : Look.Line);
            if (cur != null)
            {
                Look.Label(new Rect(cr.x + 8f, cr.y + 2f, cr.width - 16f, 22f), ("RA_Now".Translate() + ": " + cur.LabelCap).Truncate(cr.width - 16f), Color.white, GameFont.Small);
                Rect pb = new Rect(cr.x + 8f, cr.y + 28f, cr.width - 16f, 8f);
                Widgets.DrawBoxSolid(pb, new Color(0.16f, 0.19f, 0.22f));
                Widgets.DrawBoxSolid(new Rect(pb.x, pb.y, pb.width * cur.ProgressPercent, pb.height), Look.Green);
                Look.Label(new Rect(cr.x + 8f, cr.y + 38f, cr.width - 16f, 18f), cur.ProgressApparentString + " / " + cur.CostApparent.ToString("F0") + " · " + Eta(cur.CostApparent - cur.ProgressApparent), Look.Dim, GameFont.Tiny);
                if (Widgets.ButtonInvisible(cr) && Atlas.byDef.TryGetValue(cur, out Node cn)) Jump(cn);
            }
            else Look.Label(cr.ContractedBy(8f, 0f), "RA_NothingNow".Translate(), Look.Dim, GameFont.Small);
            x = cr.xMax + 10f;
            if (q == null) return;
            float total = q.queue.Where(d => d != cur).Sum(d => Mathf.Max(0f, d.CostApparent - d.ProgressApparent)) + (cur != null ? cur.CostApparent - cur.ProgressApparent : 0f);
            Look.Label(new Rect(x, r.y + 6f, 96f, r.height - 12f), "RA_Queue".Translate() + " (" + q.queue.Count + ")\n" + (q.queue.Count > 0 ? Eta(total) : "–"), Look.Fg, GameFont.Tiny);
            x += 100f;
            Rect hint = new Rect(r.xMax - 330f, r.y + 6f, 320f, r.height - 12f);
            Rect area = new Rect(x, r.y + 6f, hint.x - 10f - x, r.height - 12f);
            const float IW = 180f;
            int fits = Mathf.Max(0, (int)(area.width / (IW + 6f)));
            int group = ReorderableWidget.NewGroup((from, to) => q.Move(from, to), ReorderableDirection.Horizontal, area, 6f);
            for (int i = 0; i < q.queue.Count; i++)
            {
                if (i >= fits) { if (fits > 0) Look.Label(new Rect(x + fits * (IW + 6f) - 46f, area.y, 44f, area.height), "+" + (q.queue.Count - fits), Look.Dim, GameFont.Small, TextAnchor.MiddleRight); break; }
                ResearchProjectDef d = q.queue[i];
                Rect ir = new Rect(x + i * (IW + 6f), area.y, IW, area.height);
                ReorderableWidget.Reorderable(group, ir);
                bool now = d == cur;
                Look.Box(ir, Mouse.IsOver(ir) ? Look.Card2 : Look.Card, now ? Look.Gold : (d.CanStartNow ? Look.Line : new Color(0.4f, 0.3f, 0.3f)));
                Look.Label(new Rect(ir.x + 8f, ir.y + 2f, ir.width - 26f, 22f), ((i + 1) + ". " + d.LabelCap).Truncate(ir.width - 30f), Color.white, GameFont.Small);
                string sub = d.ProgressPercent > 0 ? d.ProgressPercent.ToStringPercent() + " · " : "";
                sub += Eta(d.CostApparent - d.ProgressApparent);
                if (!d.CanStartNow && !now && d.PrerequisitesCompleted) sub = "RA_Blocked".Translate();
                Look.Label(new Rect(ir.x + 8f, ir.y + 26f, ir.width - 12f, 20f), sub, Look.Dim, GameFont.Tiny);
                Rect xr = new Rect(ir.xMax - 20f, ir.y + 4f, 16f, 16f);
                if (Widgets.ButtonText(xr, "×", false)) { q.Remove(d); st.Clear(); break; }
                if (Event.current.type == EventType.MouseUp && Event.current.button == 1 && Mouse.IsOver(ir)) { q.Remove(d); st.Clear(); Event.current.Use(); break; }
                if (Event.current.type == EventType.MouseUp && Event.current.button == 0 && Mouse.IsOver(ir) && !xr.Contains(Event.current.mousePosition) && Atlas.byDef.TryGetValue(d, out Node qn)) Jump(qn);
            }
            if (q.queue.Count == 0) Look.Label(area, "RA_QueueEmpty".Translate(), Look.Dim, GameFont.Small);
            string adv = Advice(cur, q);
            Look.Box(hint, Look.GreenBg, Look.Green * 0.7f);
            Look.Label(hint.ContractedBy(10f, 2f), adv, Look.Fg, GameFont.Tiny);
        }

        void Jump(Node n)
        {
            if (!viewSet.Contains(n.id)) { tabMode = TabMode.All; focusId = -1; ResetFilters(); dirty = true; RebuildView(); }
            pendingCenter = n.id;
        }

        string Advice(ResearchProjectDef cur, ResearchQueue q)
        {
            bool bench = false;
            foreach (Map m in Find.Maps) if (m.IsPlayerHome && m.listerBuildings.ColonistsHaveResearchBench()) { bench = true; break; }
            if (!bench) return "RA_AdvNoBench".Translate();
            if (researchers == 0) return "RA_AdvNoResearchers".Translate();
            if (cur == null && q.queue.Count == 0) return "RA_AdvPick".Translate();
            if (cur == null && q.queue.Count > 0) return "RA_AdvBlocked".Translate();
            return "RA_AdvRate".Translate(researchers, Mathf.RoundToInt(rate * 2f));
        }
    }

    // a long list of mods needs a search, not a float menu
    public class Dialog_ModFilter : Window
    {
        private readonly Action<string> pick;
        private string filter = "";
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(460f, 640f);

        public Dialog_ModFilter(Action<string> pick)
        {
            this.pick = pick;
            doCloseX = true; closeOnClickedOutside = true; absorbInputAroundWindow = true; forcePause = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Look.Label(new Rect(0, 0, inRect.width, 30f), "RA_PickMod".Translate(), Color.white, GameFont.Medium);
            GUI.SetNextControlName("RA_ModSearch");
            filter = Widgets.TextField(new Rect(0, 36f, inRect.width, 28f), filter);
            if (Event.current.type == EventType.Repaint && GUI.GetNameOfFocusedControl() != "RA_ModSearch" && filter.Length == 0) GUI.FocusControl("RA_ModSearch");
            string f = filter.ToLowerInvariant();
            var list = Atlas.mods.Where(m => f.Length == 0 || m.ToLowerInvariant().Contains(f)).ToList();
            Rect outR = new Rect(0, 72f, inRect.width, inRect.height - 72f);
            Rect view = new Rect(0, 0, outR.width - 16f, (list.Count + 1) * 28f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0;
            if (Widgets.ButtonText(new Rect(0, y, view.width, 26f), "RA_ModAll".Translate(), false)) { pick(null); Close(); }
            y += 28f;
            foreach (string m in list)
            {
                Rect r = new Rect(0, y, view.width, 26f);
                if (y + 28f >= scroll.y && y <= scroll.y + outR.height)
                {
                    Widgets.DrawBoxSolid(new Rect(r.x, r.y + 4f, 4f, r.height - 8f), Atlas.nodes.First(n => n.mod == m).modColor);
                    if (Widgets.ButtonText(new Rect(r.x + 8f, r.y, r.width - 8f, r.height), m + "  (" + Atlas.modCount[m] + ")", false)) { pick(m); Close(); }
                }
                y += 28f;
            }
            Widgets.EndScrollView();
        }
    }
}
