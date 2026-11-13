// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakCharacter
{
    // ---------------------------------------------------------------------------
    // 0.13.0: research tab – a "Find a project" button opens a searchable list of all projects,
    // grouped by research tab and sorted by name, cost, era or progress, with cost, era,
    // prerequisites, bench and techprints on every card. Picking selects the project in the
    // research tab and starts it the game's way (the same checks and warnings).
    // ---------------------------------------------------------------------------
    public static class ResearchPickerPatch
    {
        private static FieldInfo selectedF;
        private static MethodInfo attemptM;
        private static PropertyInfo curTabP;

        public static void Patch(Harmony h)
        {
            selectedF = AccessTools.Field(typeof(MainTabWindow_Research), "selectedProject");
            attemptM = AccessTools.Method(typeof(MainTabWindow_Research), "AttemptBeginResearch");
            curTabP = AccessTools.Property(typeof(MainTabWindow_Research), "CurTab");
            h.Patch(AccessTools.Method(typeof(MainTabWindow_Research), nameof(MainTabWindow_Research.DoWindowContents)),
                postfix: new HarmonyMethod(typeof(ResearchPickerPatch), nameof(Postfix)));
        }

        public static void Postfix(MainTabWindow_Research __instance, Rect inRect)
        {
            if (MTMod.S == null || !MTMod.S.researchPicker) return;
            try
            {
                // under the game's search box (top right)
                Rect b = new Rect(inRect.xMax - 200f, 27f, 200f, 24f);
                if (Look.Button(b, "MT_PB_ResFind".Translate()))
                {
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    Find.WindowStack.Add(new Dialog_ResearchPicker(__instance));
                }
                TooltipHandler.TipRegion(b, "MT_PB_ResFindTip".Translate());
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] research picker: " + e, 0x4d5430); }
        }

        public static void Pick(MainTabWindow_Research tab, ResearchProjectDef p)
        {
            try
            {
                if (tab != null)
                {
                    if (curTabP != null && p.tab != null && curTabP.CanWrite) curTabP.SetValue(tab, p.tab);
                    selectedF?.SetValue(tab, p);
                }
                if (p.knowledgeCategory == null && p.CanStartNow && !Find.ResearchManager.IsCurrentProject(p))
                {
                    if (tab != null && attemptM != null) attemptM.Invoke(tab, new object[] { p });
                    else
                    {
                        SoundDefOf.ResearchStart.PlayOneShotOnCamera();
                        Find.ResearchManager.SetCurrentProject(p);
                    }
                }
                else SoundDefOf.Click.PlayOneShotOnCamera();
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] research pick: " + e, 0x4d5431); }
        }
    }

    public class Dialog_ResearchPicker : Window
    {
        private enum Sort { Name, Cost, Era, Progress }

        private class Entry
        {
            public ResearchProjectDef def;
            public ResearchTabDef tab;
            public Def icon;
            public bool done, canStart, prereqs, bench, techprint, current;
            public float cost, progress;
            public string unlocks, missing, mod;
        }

        private readonly MainTabWindow_Research tab;
        private readonly List<Entry> all = new List<Entry>();
        private readonly List<ResearchTabDef> tabs = new List<ResearchTabDef>();
        private ResearchTabDef group;
        private Sort sort = Sort.Era;
        private string search = "";
        private bool onlyAvailable = true;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(1020f, 700f);

        public Dialog_ResearchPicker(MainTabWindow_Research tab)
        {
            this.tab = tab;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
            Build();
        }

        private void Build()
        {
            foreach (ResearchProjectDef p in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
            {
                try
                {
                    if (p.IsHidden) continue;
                    var e = new Entry { def = p, tab = p.tab, done = p.IsFinished, cost = p.CostApparent, progress = p.ProgressPercent, mod = p.modContentPack?.Name ?? "" };
                    e.canStart = p.CanStartNow;
                    e.prereqs = p.PrerequisitesCompleted;
                    e.bench = p.requiredResearchBuilding == null || p.PlayerHasAnyAppropriateResearchBench;
                    e.techprint = p.TechprintRequirementMet;
                    e.current = Find.ResearchManager.IsCurrentProject(p);
                    var unl = p.UnlockedDefs;
                    e.icon = unl?.FirstOrDefault(d => d is ThingDef td && td.uiIcon != null && td.uiIcon != BaseContent.BadTex) ?? unl?.FirstOrDefault(d => d is BuildableDef);
                    e.unlocks = unl == null ? "" : string.Join(", ", unl.Select(d => d.label).Where(s => !s.NullOrEmpty()).Distinct().Take(8));
                    var miss = new List<string>();
                    if (p.prerequisites != null) miss.AddRange(p.prerequisites.Where(x => !x.IsFinished).Select(x => x.LabelCap.ToString()));
                    if (p.hiddenPrerequisites != null) miss.AddRange(p.hiddenPrerequisites.Where(x => !x.IsFinished).Select(x => x.LabelCap.ToString()));
                    e.missing = string.Join(", ", miss);
                    all.Add(e);
                }
                catch { }
            }
            foreach (ResearchTabDef t in DefDatabase<ResearchTabDef>.AllDefsListForReading)
                if (all.Any(e => e.tab == t)) tabs.Add(t);
            group = null;
        }

        public override void DoWindowContents(Rect inRect)
        {
            PickUI.Frame(this);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 340f, 32f), "MT_PB_ResTitle".Translate(), MUI.Text1, GameFont.Medium);
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            if (cur != null) Look.Text(new Rect(r.xMax - 330f, y, 300f, 32f), "MT_PB_ResNow".Translate(cur.LabelCap, cur.ProgressPercent.ToStringPercent()).ToString().Truncate(300f), MUI.Muted);
            y += 38f;
            float x = r.x;
            if (PickUI.Chip(ref x, y, "MT_PG_All".Translate() + " (" + all.Count + ")", group == null)) { group = null; scroll = Vector2.zero; }
            foreach (ResearchTabDef t in tabs)
            {
                string lab = t.LabelCap + " (" + all.Count(e => e.tab == t) + ")";
                Text.Font = GameFont.Small;
                if (x + Text.CalcSize(lab).x + 24f > r.xMax) { x = r.x; y += 34f; }
                if (PickUI.Chip(ref x, y, lab, group == t)) { group = t; scroll = Vector2.zero; }
            }
            y += 36f;
            search = PickUI.Search(new Rect(r.x, y, 260f, 28f), "MT_PB_ResSearch", search, "MT_PG_Search".Translate());
            x = r.x + 276f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PG_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
                if (PickUI.Chip(ref x, y, ("MT_PB_RS_" + s).Translate(), sort == s, 28f, 20f)) sort = s;
            Rect ur = new Rect(r.xMax - 210f, y, 210f, 28f);
            Widgets.CheckboxLabeled(ur, "MT_PB_ResOnly".Translate(), ref onlyAvailable);
            TooltipHandler.TipRegion(ur, "MT_PB_ResOnlyTip".Translate());
            y += 38f;

            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => (group == null || e.tab == group)
                && (!onlyAvailable || (!e.done && e.prereqs) || e.current)
                && PickUI.Matches(q, e.def.label, e.unlocks, e.mod, e.def.description));
            switch (sort)
            {
                case Sort.Cost: list = list.OrderBy(e => e.cost).ThenBy(e => e.def.label); break;
                case Sort.Era: list = list.OrderBy(e => (int)e.def.techLevel).ThenBy(e => e.cost).ThenBy(e => e.def.label); break;
                case Sort.Progress: list = list.OrderByDescending(e => e.done ? -1f : e.progress).ThenBy(e => e.def.label); break;
                default: list = list.OrderBy(e => e.def.label); break;
            }
            var shown = list.ToList();
            int picked = PickUI.Grid(new Rect(r.x, y, r.width, r.yMax - y), ref scroll, shown.Count, 320f, 70f, (c, i) => DrawCard(c, shown[i]), "MT_PG_None".Translate());
            if (picked >= 0)
            {
                ResearchPickerPatch.Pick(tab, shown[picked].def);
                Close();
            }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool over = Mouse.IsOver(c);
            Color edge = e.current ? Look.Green : e.done ? Look.Blue : Look.Line;
            MUI.Fill(c, e.current ? Look.GreenBg : over ? Look.Card2 : Look.Card);
            MUI.Frame(c, edge);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            MUI.Fill(icon, Look.Bg);
            if (e.icon != null) { try { Widgets.DefIcon(icon.ContractedBy(4f), e.icon); } catch { } }
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 30f;
            Color nameC = e.current ? Look.Green : e.done ? Look.Blue : e.canStart ? MUI.Text1 : MUI.Muted;
            Look.Text(new Rect(tx, c.y + 3f, tw, 22f), e.def.LabelCap.ToString().Truncate(tw), nameC);
            string line2 = e.done ? "MT_PB_ResDone".Translate().ToString()
                : "MT_PB_ResCost".Translate(Mathf.RoundToInt(e.cost)) + (e.progress > 0f ? " · " + e.progress.ToStringPercent() : "");
            if (!e.unlocks.NullOrEmpty()) line2 += " · " + e.unlocks;
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), line2.Truncate(tw), MUI.Muted, GameFont.Tiny);
            if (!e.done && e.progress > 0f) Look.Bar(new Rect(tx, c.y + 42f, tw, 3f), e.progress, Look.Green);
            float bx = tx;
            float by = c.y + 48f;
            bx = PickUI.Badge(new Rect(bx, by, 0f, 16f), e.def.techLevel.ToStringHuman().CapitalizeFirst(), Look.Line);
            if (!e.done && !e.prereqs) bx = PickUI.Badge(new Rect(bx, by, 0f, 16f), "MT_PB_ResPrereq".Translate(), Look.Red);
            if (!e.done && !e.bench && e.def.requiredResearchBuilding != null) bx = PickUI.Badge(new Rect(bx, by, 0f, 16f), e.def.requiredResearchBuilding.LabelCap.ToString().Truncate(110f), Look.Orange);
            if (!e.done && !e.techprint) bx = PickUI.Badge(new Rect(bx, by, 0f, 16f), "MT_PB_ResTechprint".Translate(), Look.Orange);
            if (e.def.knowledgeCategory != null) bx = PickUI.Badge(new Rect(bx, by, 0f, 16f), e.def.knowledgeCategory.LabelCap.ToString().Truncate(90f), Look.Blue);
            if (e.current) PickUI.Badge(new Rect(bx, by, 0f, 16f), "MT_PB_Current".Translate(), Look.Green);
            Widgets.InfoCardButton(c.xMax - 28f, c.y + 6f, e.def);
            var sb = new System.Text.StringBuilder();
            sb.Append(e.def.LabelCap.ToString()).Append('\n').Append(e.def.description);
            sb.Append("\n\n").Append("MT_PB_ResCost".Translate(Mathf.RoundToInt(e.cost)).ToString());
            if (!e.missing.NullOrEmpty()) sb.Append('\n').Append("MT_PB_ResMissing".Translate(e.missing).ToString());
            if (e.def.requiredResearchBuilding != null) sb.Append('\n').Append("MT_PB_ResBench".Translate(e.def.requiredResearchBuilding.LabelCap).ToString());
            if (!e.unlocks.NullOrEmpty()) sb.Append('\n').Append("MT_PB_ResUnlocks".Translate(e.unlocks).ToString());
            if (!e.mod.NullOrEmpty()) sb.Append('\n').Append("MT_PG_Mod".Translate(e.mod).ToString());
            if (!e.canStart && !e.done) sb.Append("\n\n").Append("MT_PB_ResSelectOnly".Translate().ToString());
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 32f, c.height), sb.ToString());
            return Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 32f, c.height));
        }
    }
}
