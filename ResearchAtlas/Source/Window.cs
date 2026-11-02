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
        public static readonly Color Canvas = new Color32(17, 20, 24, 255);
        public static readonly Color Card = new Color32(31, 36, 42, 255);
        public static readonly Color Card2 = new Color32(40, 46, 53, 255);
        public static readonly Color Line = new Color32(58, 65, 72, 255);
        public static readonly Color Fg = new Color32(216, 221, 227, 255);
        public static readonly Color Dim = new Color32(140, 151, 163, 255);
        public static readonly Color Green = new Color32(124, 191, 94, 255);
        public static readonly Color GreenBg = new Color32(31, 42, 34, 255);
        public static readonly Color Gold = new Color32(242, 193, 78, 255);
        public static readonly Color Blue = new Color32(127, 188, 227, 255);
        public static readonly Color Red = new Color32(224, 100, 90, 255);

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

    public class MainTabWindow_Atlas : MainTabWindow_Research
    {
        // world units
        const float ColW = 250f, RowH = 100f, CardW = 210f, CardH = 80f, HeadH = 34f;
        const float TopH = 40f, TabsH = 30f, QueueH = 70f;

        static bool vanillaMode;
        static bool failedOnce;

        // filters (kept while the game runs)
        static ResearchTabDef tab;           // null = all
        static bool allTabs = false;
        static bool onlyAvailable, hideFinished;
        static string modFilter;
        static int catFilter = -1, techFilter = -1;
        static int focusId = -1;
        static string searchText = "";

        // view
        List<Node> view = new List<Node>();
        HashSet<int> viewSet = new HashSet<int>();
        HashSet<int> dimmed = new HashSet<int>();
        string viewHash;
        LayoutResult layout;
        LayoutJob job;
        bool dirty = true;
        Vector2 pan = new Vector2(-20, -10);
        float zoom = 0.75f;
        bool needFit = true;
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
        ResearchProjectDef lastSelected;

        // state cache
        enum St { Done, Progress, Available, Locked }
        readonly Dictionary<int, St> st = new Dictionary<int, St>();
        int stFrame = -999;
        float rate; float rateAt = -99; int researchers;

        public override Vector2 InitialSize => new Vector2(UI.screenWidth, UI.screenHeight - 35f);

        public override void PreOpen()
        {
            try { base.PreOpen(); } catch (Exception e) { Log.Warning("[Research Atlas] vanilla PreOpen: " + e.Message); }
            try { Atlas.Build(); dirty = true; st.Clear(); stFrame = -999; lastSelected = selectedProject; }
            catch (Exception e) { Fail(e); }
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
        bool Visible(Node n)
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

        void RebuildView()
        {
            dirty = false;
            var set = new List<Node>();
            dimmed.Clear();
            HashSet<int> focus = null;
            if (focusId >= 0) focus = Related(focusId);
            foreach (Node n in Atlas.nodes)
            {
                if (!Visible(n)) continue;
                if (focus != null) { if (!focus.Contains(n.id)) continue; }
                else if (!allTabs && n.def.tab != CurTabSafe && !(n.def.tab == null && CurTabSafe == null)) continue;
                bool pass = PassFilters(n);
                if (!pass && RAMod.S.compactFilters) continue;
                if (!pass) dimmed.Add(n.id);
                set.Add(n);
            }
            view = set;
            viewSet = new HashSet<int>(set.Select(v => v.id));
            string hash = LayoutCache.Hash(set);
            if (hash == viewHash && layout != null) return;
            viewHash = hash;
            needFit = true;
            if (AtlasView.mem.TryGetValue(hash, out LayoutResult r)) { layout = r; job = null; return; }
            bool plain = focusId < 0 && dimmed.Count == 0 && !onlyAvailable && !hideFinished && modFilter == null && catFilter < 0 && techFilter < 0;
            if (plain)
            {
                r = LayoutCache.Load(hash);
                if (r != null) { AtlasView.mem[hash] = r; layout = r; job = null; return; }
            }
            layout = null;
            job = new LayoutJob(hash, set);
            if (set.Count < 250) job.RunNow(); else job.Start();
            jobPlain = plain;
        }
        bool jobPlain;

        ResearchTabDef CurTabSafe
        {
            get { if (tab == null) tab = ResearchTabDefOf.Main; return tab; }
        }

        HashSet<int> Related(int id)
        {
            var res = new HashSet<int> { id };
            var st1 = new Stack<int>(); st1.Push(id);
            while (st1.Count > 0) foreach (int p in Atlas.nodes[st1.Pop()].pre) if (res.Add(p)) st1.Push(p);
            st1.Push(id);
            var down = new HashSet<int>();
            while (st1.Count > 0) foreach (int p in Atlas.nodes[st1.Pop()].post) if (down.Add(p)) st1.Push(p);
            res.UnionWith(down);
            return res;
        }

        void PollJob()
        {
            if (job == null || !job.done) return;
            if (job.error != null) Log.Warning("[Research Atlas] arranging failed: " + job.error);
            layout = job.result;
            AtlasView.mem[job.key] = layout;
            if (jobPlain && job.error == null) LayoutCache.Save(job.key, layout);
            job = null;
            needFit = true;
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
                    if (!viewSet.Contains(sn.id)) { allTabs = true; focusId = -1; ResetFilters(); RebuildView(); }
                    pendingCenter = sn.id;
                }
            }
            if (Time.frameCount - stFrame > 45) { st.Clear(); stFrame = Time.frameCount; }

            Rect top = new Rect(inRect.x, inRect.y, inRect.width, TopH);
            Rect tabsR = new Rect(inRect.x, top.yMax, inRect.width, TabsH);
            Rect queueR = new Rect(inRect.x, inRect.yMax - QueueH, inRect.width, QueueH);
            Rect canvas = new Rect(inRect.x, tabsR.yMax + 4f, inRect.width, queueR.y - tabsR.yMax - 8f);
            DrawTop(top);
            DrawTabs(tabsR);
            DrawCanvas(canvas);
            DrawQueue(queueR);
        }

        int pendingCenter = -1;

        void ResetFilters() { onlyAvailable = false; hideFinished = false; modFilter = null; catFilter = -1; techFilter = -1; }

        void DrawTop(Rect r)
        {
            float x = r.x;
            Look.Label(new Rect(x, r.y, 150f, r.height - 6f), "RA_Title".Translate(), Color.white, GameFont.Medium);
            x += 150f;
            Rect sr = new Rect(x, r.y + 4f, 260f, 28f);
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
            if (!searchText.NullOrEmpty())
            {
                string cnt = matches.Count == 0 ? "RA_NoMatch".Translate().ToString() : (matchCursor + 1) + "/" + matches.Count;
                Rect cr = new Rect(x, r.y + 4f, 70f, 28f);
                if (Look.Chip(cr, cnt + " ▸", false)) NextMatch();
                TooltipHandler.TipRegion(cr, "RA_NextMatchTip".Translate());
                x = cr.xMax + 6f;
            }
            bool Chip(string label, bool on, float w)
            {
                Rect c = new Rect(x, r.y + 4f, w, 28f); x += w + 6f; return Look.Chip(c, label, on);
            }
            if (Chip("RA_OnlyAvailable".Translate(), onlyAvailable, 120f)) { onlyAvailable = !onlyAvailable; dirty = true; }
            if (Chip("RA_HideFinished".Translate(), hideFinished, 120f)) { hideFinished = !hideFinished; dirty = true; }
            string ml = modFilter == null ? "RA_ModAll".Translate().ToString() : modFilter;
            if (Chip(("RA_Mod".Translate() + ": " + ml).Truncate(170f) + " ▾", modFilter != null, 180f))
                Find.WindowStack.Add(new Dialog_ModFilter(m => { modFilter = m; dirty = true; }));
            string cl = catFilter < 0 ? "RA_All".Translate().ToString() : CatLabel((Cat)catFilter);
            if (Chip("RA_Cat".Translate() + ": " + cl + " ▾", catFilter >= 0, 150f))
            {
                var o = new List<FloatMenuOption> { new FloatMenuOption("RA_All".Translate(), () => { catFilter = -1; dirty = true; }) };
                foreach (Cat c in Enum.GetValues(typeof(Cat))) { Cat cc = c; o.Add(new FloatMenuOption(CatLabel(cc) + " (" + Atlas.nodes.Count(n => n.cat == cc) + ")", () => { catFilter = (int)cc; dirty = true; })); }
                Find.WindowStack.Add(new FloatMenu(o));
            }
            string tl = techFilter < 0 ? "RA_All".Translate().ToString() : ((TechLevel)techFilter).ToStringHuman().CapitalizeFirst();
            if (Chip("RA_Era".Translate() + ": " + tl + " ▾", techFilter >= 0, 150f))
            {
                var o = new List<FloatMenuOption> { new FloatMenuOption("RA_All".Translate(), () => { techFilter = -1; dirty = true; }) };
                foreach (int b in Atlas.nodes.Select(n => n.band).Distinct().OrderBy(b => b)) { int bb = b; o.Add(new FloatMenuOption(((TechLevel)bb).ToStringHuman().CapitalizeFirst(), () => { techFilter = bb; dirty = true; })); }
                Find.WindowStack.Add(new FloatMenu(o));
            }
            if (focusId >= 0 && Chip("RA_Focus".Translate(Atlas.nodes[focusId].label) + " ✕", true, 200f)) { focusId = -1; dirty = true; }
            if (onlyAvailable || hideFinished || modFilter != null || catFilter >= 0 || techFilter >= 0)
                if (Chip("RA_Reset".Translate(), false, 80f)) { ResetFilters(); dirty = true; }

            // right side
            float rx = r.xMax - 40f;   // room for the close button
            Rect B(float w) { rx -= w + 6f; return new Rect(rx, r.y + 4f, w, 28f); }
            if (Look.Chip(B(110f), "RA_Vanilla".Translate(), false)) { vanillaMode = true; return; }
            Type rf = Fixes.RFMod.Active ? typeof(Fixes.Dialog_ResearchFixer) : GenTypes.GetTypeInAnyAssembly("ResearchFixer.Dialog_ResearchFixer");
            if (rf != null && Look.Chip(B(120f), "RA_FixesButton".Translate(), false))
            {
                try { Find.WindowStack.Add((Window)Activator.CreateInstance(rf)); } catch (Exception e) { Log.Warning("[Research Atlas] Research Fixer: " + e.Message); }
            }
            if (Look.Chip(B(70f), "RA_Fit".Translate(), false)) needFit = true;
        }

        void DrawTabs(Rect r)
        {
            float x = r.x;
            var counts = new Dictionary<ResearchTabDef, int>();
            int all = 0;
            foreach (Node n in Atlas.nodes)
            {
                if (n.def.tab == null) continue;
                counts.TryGetValue(n.def.tab, out int c); counts[n.def.tab] = c + 1; all++;
            }
            bool Tab(string label, bool on, out Rect rr)
            {
                Text.Font = GameFont.Small;
                float w = Mathf.Max(70f, Text.CalcSize(label).x + 24f);
                rr = new Rect(x, r.y, w, r.height);
                x += w + 3f;
                if (rr.xMax > r.xMax - 90f) return false;
                bool over = Mouse.IsOver(rr);
                Look.Box(rr, on ? Look.Card2 : (over ? Look.Card : Look.Bg), Look.Line);
                if (on) Widgets.DrawBoxSolid(new Rect(rr.x, rr.yMax - 2f, rr.width, 2f), Look.Gold);
                Look.Label(rr, label, on ? Color.white : Look.Dim, GameFont.Small, TextAnchor.MiddleCenter);
                return Widgets.ButtonInvisible(rr);
            }
            bool inFocus = focusId >= 0;
            if (Tab("RA_AllTabs".Translate() + " (" + all + ")", allTabs && !inFocus, out _)) { allTabs = true; focusId = -1; dirty = true; }
            var hidden = new List<ResearchTabDef>();
            foreach (ResearchTabDef t in DefDatabase<ResearchTabDef>.AllDefsListForReading)
            {
                if (!counts.TryGetValue(t, out int c) || c == 0) continue;
                if (x > r.xMax - 200f) { hidden.Add(t); continue; }
                if (Tab(t.LabelCap + " (" + c + ")", !allTabs && !inFocus && t == CurTabSafe, out Rect tr)) { tab = t; allTabs = false; focusId = -1; dirty = true; }
            }
            if (hidden.Count > 0)
            {
                Rect mr = new Rect(r.xMax - 86f, r.y, 86f, r.height);
                if (Look.Chip(mr, "RA_More".Translate() + " ▾", hidden.Contains(tab) && !allTabs))
                    Find.WindowStack.Add(new FloatMenu(hidden.Select(t => new FloatMenuOption(t.LabelCap + " (" + counts[t] + ")", () => { tab = t; allTabs = false; focusId = -1; dirty = true; })).ToList()));
            }
        }

        // ---------------------------------------------------------------- canvas ----
        Vector2 World(Vector2 cr) => new Vector2(cr.x * ColW, cr.y * RowH + HeadH);

        Rect CardRect(Rect canvas, Vector2 cr)
        {
            Vector2 w = World(cr);
            return new Rect(canvas.x + w.x * zoom - pan.x, canvas.y + w.y * zoom - pan.y, CardW * zoom, CardH * zoom);
        }

        void Fit(Rect canvas)
        {
            needFit = false;
            if (layout == null || layout.pos.Count == 0) return;
            float ww = layout.width * ColW + 40f, hh = layout.height * RowH + HeadH + 40f;
            zoom = Mathf.Clamp(Mathf.Min(canvas.width / ww, canvas.height / hh), 0.15f, 1f);
            if (zoom < 0.35f && layout.pos.Count > 60) zoom = 0.35f;     // big trees: readable tiles, pan from the left
            pan = new Vector2(-20f, -10f);
        }

        void CenterOn(Rect canvas, int id)
        {
            if (layout == null || !layout.pos.TryGetValue(id, out Vector2 p)) return;
            if (zoom < 0.6f) zoom = 0.75f;
            Vector2 w = World(p) + new Vector2(CardW, CardH) / 2f;
            pan = w * zoom - new Vector2(canvas.width, canvas.height) / 2f;
        }

        void DrawCanvas(Rect canvas)
        {
            Look.Box(canvas, Look.Canvas, Look.Line);
            if (layout == null)
            {
                Look.Label(canvas, job != null ? "RA_Arranging".Translate(view.Count).ToString() : "RA_Empty".Translate().ToString(), Look.Dim, GameFont.Medium, TextAnchor.MiddleCenter);
                return;
            }
            if (layout.pos.Count == 0) { Look.Label(canvas, "RA_Empty".Translate(), Look.Dim, GameFont.Medium, TextAnchor.MiddleCenter); return; }
            if (needFit) Fit(canvas);
            if (pendingCenter >= 0) { CenterOn(canvas, pendingCenter); flash = pendingCenter; flashUntil = Time.realtimeSinceStartup + 2.5f; pendingCenter = -1; }
            UpdateSearch();
            HandleInput(canvas);

            GUI.BeginGroup(canvas);
            Rect local = new Rect(0, 0, canvas.width, canvas.height);
            try
            {
                DrawBands(local);
                hover = -1;
                Vector2 mouse = Event.current.mousePosition;
                // visible cards
                var vis = new List<(Node n, Rect r)>();
                foreach (Node n in view)
                {
                    if (!layout.pos.TryGetValue(n.id, out Vector2 p)) continue;
                    Rect cr = CardRect(local, p);
                    if (cr.xMax < 0 || cr.yMax < 0 || cr.x > local.width || cr.y > local.height) continue;
                    vis.Add((n, cr));
                    if (cr.Contains(mouse) && local.Contains(mouse)) hover = n.id;
                }
                DrawEdges(local);
                ResearchQueue q = ResearchQueue.Get;
                foreach (var (n, r) in vis) DrawCard(n, r, q);
                if (hover >= 0)
                {
                    Node hn = Atlas.nodes[hover];
                    var r = vis.First(v => v.n.id == hover).r;
                    TooltipHandler.TipRegion(r, new TipSignal(() => Tip(hn), hn.id * 31 + 7));
                }
                if (RAMod.S.minimap) DrawMinimap(local);
                Look.Label(new Rect(8f, local.height - 22f, 600f, 20f), "RA_Help".Translate(), new Color(1, 1, 1, 0.35f), GameFont.Tiny);
            }
            finally { GUI.EndGroup(); }
        }

        int flash = -1; float flashUntil;

        void DrawBands(Rect local)
        {
            for (int i = 0; i < layout.bands.Count; i++)
            {
                var b = layout.bands[i];
                float x0 = b.x0 * ColW * zoom - pan.x - 15f * zoom;
                float x1 = (b.x1 * ColW - (ColW - CardW)) * zoom - pan.x + 15f * zoom;
                if (x1 < 0 || x0 > local.width) continue;
                Rect br = new Rect(x0, 0, x1 - x0, local.height);
                Widgets.DrawBoxSolid(br, i % 2 == 0 ? new Color(1, 1, 1, 0.025f) : new Color(1, 1, 1, 0.0f));
                Rect hr = new Rect(Mathf.Max(x0, 4f), Mathf.Max(4f, -pan.y + 4f), Mathf.Max(60f, x1 - Mathf.Max(x0, 4f)), 24f);
                if (hr.y > local.height - 24f) hr.y = 4f;
                Look.Label(hr, ((TechLevel)b.band).ToStringHuman().CapitalizeFirst(), new Color(1, 1, 1, 0.45f), zoom > 0.4f ? GameFont.Small : GameFont.Tiny);
            }
        }

        void DrawEdges(Rect local)
        {
            bool searching = matchSet.Count > 0;
            HashSet<int> hoverRel = null;
            if (hover >= 0) { hoverRel = new HashSet<int>(Atlas.nodes[hover].pre); hoverRel.UnionWith(Atlas.nodes[hover].post); hoverRel.Add(hover); }
            float gap = (ColW - CardW) * zoom;
            int drawn = 0;
            for (int pass = 0; pass < 2; pass++)
                foreach (Node n in view)
                {
                    if (!layout.pos.TryGetValue(n.id, out Vector2 pn)) continue;
                    foreach (int p in n.pre)
                    {
                        if (!viewSet.Contains(p) || !layout.pos.TryGetValue(p, out Vector2 pp)) continue;
                        bool hi = (hover >= 0 && (hover == n.id || hover == p)) || (searching && pathSet.Contains(n.id) && pathSet.Contains(p)) || (focusId >= 0 && (focusId == n.id || focusId == p));
                        if ((pass == 1) != hi) continue;
                        Rect a = CardRect(local, pp), b = CardRect(local, pn);
                        Vector2 s = new Vector2(a.xMax, a.center.y), e = new Vector2(b.x, b.center.y);
                        float minX = Mathf.Min(s.x, e.x), maxX = Mathf.Max(s.x, e.x), minY = Mathf.Min(s.y, e.y), maxY = Mathf.Max(s.y, e.y);
                        if (maxX < 0 || minX > local.width || maxY < 0 || minY > local.height) continue;
                        if (!hi && zoom < 0.3f && view.Count > 800 && hover < 0) continue;    // far out on a huge tree: only the important lines
                        Color c = hi ? Look.Gold : new Color(0.55f, 0.6f, 0.66f, dimmed.Contains(n.id) || (searching && !pathSet.Contains(n.id)) ? 0.12f : 0.35f);
                        float w = hi ? 3f : (zoom > 0.5f ? 2f : 1f);
                        if (e.x > s.x + 2f)
                        {
                            float xm = e.x - gap / 2f;
                            if (xm < s.x) xm = (s.x + e.x) / 2f;
                            Widgets.DrawLine(s, new Vector2(xm, s.y), c, w);
                            Widgets.DrawLine(new Vector2(xm, s.y), new Vector2(xm, e.y), c, w);
                            Widgets.DrawLine(new Vector2(xm, e.y), e, c, w);
                        }
                        else Widgets.DrawLine(s, e, c, w);   // prerequisite placed to the right (mixed-up eras in a mod)
                        if (++drawn > 6000) return;
                    }
                }
        }

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

        void DrawCard(Node n, Rect r, ResearchQueue q)
        {
            St s = State(n);
            bool searching = matchSet.Count > 0;
            bool match = matchSet.Contains(n.id);
            float alpha = 1f;
            if (dimmed.Contains(n.id)) alpha = 0.3f;
            if (searching && !match && !pathSet.Contains(n.id)) alpha = 0.3f;
            if (s == St.Locked) alpha *= 0.6f;
            if (s == St.Done) alpha *= 0.65f;
            Color border = s == St.Progress ? Look.Gold : s == St.Available ? new Color(0.75f, 0.8f, 0.86f) : s == St.Done ? Look.Green * 0.8f : Look.Line;
            int qi = q?.IndexOf(n.def) ?? -1;
            if (qi >= 0) border = Look.Gold * 0.85f;
            if (match || (flash == n.id && Time.realtimeSinceStartup < flashUntil)) border = Look.Gold;
            int thick = match || s == St.Progress || n.id == focusId ? 2 : 1;
            bool over = hover == n.id;
            Color bg = over ? Look.Card2 : Look.Card;
            bg.a = alpha; border.a = Mathf.Max(alpha, match ? 1f : alpha);
            Look.Box(r, bg, border, thick);
            Color stripe = n.modColor; stripe.a = alpha;
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, Mathf.Max(2f, 5f * zoom), r.height), stripe);
            if (zoom < 0.32f)
            {
                return;   // far out: a tile in the mod's colour is enough
            }
            if (zoom < 0.55f)
            {
                // middle: name only
                Look.Label(new Rect(r.x + 6f * zoom + 2f, r.y, r.width - 10f * zoom - 2f, r.height), n.label.Truncate(r.width - 12f), new Color(1, 1, 1, alpha), GameFont.Tiny, TextAnchor.MiddleLeft);
                if (qi >= 0) Badge(r, qi + 1, alpha);
                return;
            }
            float z = zoom;
            Rect ic = new Rect(r.x + 12f * z, r.y + 10f * z, 40f * z, 40f * z);
            Widgets.DrawBoxSolid(ic, new Color(0.17f, 0.2f, 0.24f, alpha));
            Texture2D tex = Atlas.IconOf(n);
            GUI.color = new Color(1, 1, 1, alpha);
            if (tex != null) Widgets.DrawTextureFitted(ic.ContractedBy(2f), tex, 1f);
            else Look.Label(ic, ((TechLevel)n.band).ToStringHuman().Substring(0, 1).ToUpper(), new Color(1, 1, 1, alpha * 0.6f), GameFont.Small, TextAnchor.MiddleCenter);
            GUI.color = Color.white;
            GameFont f = z >= 0.8f ? GameFont.Small : GameFont.Tiny;
            float tx = ic.xMax + 8f * z;
            float tw = r.xMax - tx - 26f * z;
            Text.Font = f;
            string name = n.label.Truncate(tw);
            if (s == St.Done) name += " ✓";
            Look.Label(new Rect(tx, r.y + 6f * z, tw + 20f * z, 22f * z + 4f), name, new Color(1, 1, 1, alpha), f, TextAnchor.UpperLeft);
            Text.Font = GameFont.Tiny;
            string sub = (n.mod + " · " + n.def.CostApparent.ToString("F0")).Truncate(r.xMax - tx - 6f);
            Look.Label(new Rect(tx, r.y + 28f * z, r.xMax - tx - 4f, 18f), sub, new Color(Look.Dim.r, Look.Dim.g, Look.Dim.b, alpha), GameFont.Tiny, TextAnchor.UpperLeft);
            if (z >= 0.8f && n.unlocks.Count > 0)
            {
                float ux = r.x + 12f * z, uy = r.yMax - 22f * z, us = 18f * z;
                int shown = 0;
                foreach (Def u in n.unlocks)
                {
                    if (shown >= 5) break;
                    Texture2D ut = Atlas.UnlockIcon(u);
                    if (ut == null) continue;
                    Rect ur = new Rect(ux, uy, us, us);
                    Widgets.DrawBoxSolid(ur, new Color(0.18f, 0.21f, 0.25f, alpha));
                    GUI.color = new Color(1, 1, 1, alpha);
                    Widgets.DrawTextureFitted(ur.ContractedBy(1f), ut, 1f);
                    GUI.color = Color.white;
                    ux += us + 4f * z; shown++;
                }
                int more = n.unlocks.Count - shown;
                if (more > 0 && shown > 0) Look.Label(new Rect(ux, uy, 40f * z, us), "+" + more, new Color(Look.Dim.r, Look.Dim.g, Look.Dim.b, alpha), GameFont.Tiny);
            }
            float prog = n.def.ProgressPercent;
            if (s != St.Done && prog > 0f)
            {
                Rect pr = new Rect(r.xMax - 78f * z, r.yMax - 12f * z, 70f * z, 6f * z);
                Widgets.DrawBoxSolid(pr, new Color(0.16f, 0.19f, 0.22f, alpha));
                Widgets.DrawBoxSolid(new Rect(pr.x, pr.y, pr.width * prog, pr.height), new Color(Look.Green.r, Look.Green.g, Look.Green.b, alpha));
            }
            if (qi >= 0) Badge(r, qi + 1, alpha);
            else if (s == St.Locked && z >= 0.8f) Look.Label(new Rect(r.xMax - 22f * z, r.y + 4f * z, 20f * z, 20f * z), "🔒", new Color(1, 1, 1, 0.5f), GameFont.Tiny, TextAnchor.MiddleCenter);
        }

        void Badge(Rect r, int num, float alpha)
        {
            float d = Mathf.Max(14f, 20f * zoom);
            Rect b = new Rect(r.xMax - d - 4f, r.y + 4f, d, d);
            Widgets.DrawBoxSolid(b, new Color(Look.Gold.r, Look.Gold.g, Look.Gold.b, Mathf.Max(alpha, 0.8f)));
            Look.Label(b, num.ToString(), Color.black, GameFont.Tiny, TextAnchor.MiddleCenter);
        }

        void DrawMinimap(Rect local)
        {
            if (layout == null || layout.width <= 0) return;
            const float MW = 220f, MH = 130f;
            Rect mr = new Rect(local.width - MW - 12f, local.height - MH - 30f, MW, MH);
            if (minimapFor != viewHash || minimap == null) BuildMinimap((int)MW, (int)MH);
            Look.Box(mr, new Color(0.05f, 0.06f, 0.08f, 0.85f), Look.Line);
            if (minimap != null) GUI.DrawTexture(mr, minimap);
            float ww = layout.width * ColW, hh = layout.height * RowH + HeadH;
            float sx = MW / ww, sy = MH / hh;
            Rect vr = new Rect(mr.x + pan.x / zoom * sx, mr.y + pan.y / zoom * sy, local.width / zoom * sx, local.height / zoom * sy);
            vr = Rect.MinMaxRect(Mathf.Max(vr.xMin, mr.xMin), Mathf.Max(vr.yMin, mr.yMin), Mathf.Min(vr.xMax, mr.xMax), Mathf.Min(vr.yMax, mr.yMax));
            if (vr.width > 0 && vr.height > 0) { GUI.color = Look.Gold; Widgets.DrawBox(vr); GUI.color = Color.white; }
            if (Mouse.IsOver(mr) && (Event.current.type == EventType.MouseDown || Event.current.type == EventType.MouseDrag) && Event.current.button == 0)
            {
                Vector2 m = Event.current.mousePosition - mr.position;
                Vector2 w = new Vector2(m.x / sx, m.y / sy);
                pan = w * zoom - new Vector2(local.width, local.height) / 2f;
                Event.current.Use();
            }
        }

        void BuildMinimap(int w, int h)
        {
            minimapFor = viewHash;
            try
            {
                if (minimap == null) minimap = new Texture2D(w, h, TextureFormat.RGBA32, false);
                var px = new Color32[w * h];
                for (int i = 0; i < px.Length; i++) px[i] = new Color32(0, 0, 0, 0);
                float ww = layout.width * ColW, hh = layout.height * RowH + HeadH;
                foreach (var kv in layout.pos)
                {
                    Vector2 wp = World(kv.Value);
                    int x0 = (int)(wp.x / ww * w), y0 = (int)(wp.y / hh * h);
                    int x1 = Mathf.Max(x0 + 1, (int)((wp.x + CardW) / ww * w)), y1 = Mathf.Max(y0 + 1, (int)((wp.y + CardH) / hh * h));
                    Color32 c = Atlas.nodes[kv.Key].modColor;
                    for (int y = y0; y < y1 && y < h; y++)
                        for (int x = x0; x < x1 && x < w; x++)
                            if (x >= 0 && y >= 0) px[(h - 1 - y) * w + x] = c;
                }
                minimap.SetPixels32(px);
                minimap.Apply();
            }
            catch (Exception e) { Log.Warning("[Research Atlas] minimap: " + e.Message); RAMod.S.minimap = false; minimap = null; }
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
                zoom = Mathf.Clamp(zoom * (ev.delta.y > 0 ? 0.88f : 1.13f), 0.12f, 1.6f);
                Vector2 local = m - canvas.position;
                pan = (pan + local) * (zoom / old) - local;
                ev.Use();
                return;
            }
            if (ev.type == EventType.MouseDown && inside && !OverMinimap(canvas, m))
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
                if (!moved && downNode >= 0 && NodeAt(canvas, m) == downNode) Click(Atlas.nodes[downNode], downButton, ev.shift, ev.control);
                ev.Use();
            }
        }

        bool OverMinimap(Rect canvas, Vector2 m)
        {
            if (!RAMod.S.minimap) return false;
            Rect mr = new Rect(canvas.xMax - 220f - 12f, canvas.yMax - 130f - 30f, 220f, 130f);
            return mr.Contains(m);
        }

        int NodeAt(Rect canvas, Vector2 m)
        {
            if (layout == null) return -1;
            Vector2 local = m - canvas.position;
            // world coordinates under the mouse → column/row → the node there (constant time with a grid)
            float wx = (local.x + pan.x) / zoom, wy = (local.y + pan.y) / zoom - HeadH;
            foreach (Node n in view)
            {
                if (!layout.pos.TryGetValue(n.id, out Vector2 p)) continue;
                float x0 = p.x * ColW, y0 = p.y * RowH;
                if (wx >= x0 && wx <= x0 + CardW && wy >= y0 && wy <= y0 + CardH) return n.id;
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
            // the way to each match: all its prerequisites in this view
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
            if (!d.description.NullOrEmpty()) { sb.AppendLine(); sb.AppendLine(d.description.Truncate(500f * 3f)); }
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
            try
            {
                WorkTypeDef wt = WorkTypeDefOf.Research;
                foreach (Pawn p in PawnsFinder.AllMaps_FreeColonistsSpawned)
                {
                    if (p.Downed || p.WorkTypeIsDisabled(wt) || p.workSettings == null || !p.workSettings.WorkIsActive(wt)) continue;
                    sum += p.GetStatValue(StatDefOf.ResearchSpeed);
                    researchers++;
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
            Rect cr = new Rect(x, r.y + 6f, 230f, r.height - 12f);
            Look.Box(cr, Look.Card, cur != null ? Look.Gold : Look.Line);
            if (cur != null)
            {
                Look.Label(new Rect(cr.x + 8f, cr.y + 2f, cr.width - 16f, 22f), "RA_Now".Translate() + ": " + cur.LabelCap, Color.white, GameFont.Small);
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
            Look.Label(new Rect(x, r.y + 6f, 90f, r.height - 12f), "RA_Queue".Translate() + "\n" + (q.queue.Count > 0 ? Eta(total) : "–"), Look.Fg, GameFont.Tiny);
            x += 92f;
            Rect hint = new Rect(r.xMax - 360f, r.y + 6f, 350f, r.height - 12f);
            float qRight = hint.x - 10f;
            Rect area = new Rect(x, r.y + 6f, qRight - x, r.height - 12f);
            const float IW = 170f;
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
            // advice
            string adv = Advice(cur, q);
            Look.Box(hint, Look.GreenBg, Look.Green * 0.7f);
            Look.Label(hint.ContractedBy(10f, 2f), adv, Look.Fg, GameFont.Tiny);
        }

        void Jump(Node n)
        {
            if (!viewSet.Contains(n.id)) { allTabs = true; focusId = -1; ResetFilters(); dirty = true; RebuildView(); }
            pendingCenter = n.id;
        }

        string Advice(ResearchProjectDef cur, ResearchQueue q)
        {
            Rate();
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
