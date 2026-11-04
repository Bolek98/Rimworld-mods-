using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchAtlas
{
    public class RASettings : ModSettings
    {
        public bool replaceTab = true;
        public bool minimap = true;
        public bool autoStart = true;
        public bool compactFilters = true;     // filters re-arrange the tree instead of only dimming
        public bool groupByMod = false;        // 0.2.0: projects of one mod stay together in a column
        public bool semiRandom = false;        // 0.2.0: offer three random available projects to pick from
        public Fixes.RFSettings fixes;           // Research Fixer, built in

        public override void ExposeData()
        {
            Scribe_Values.Look(ref replaceTab, "replaceTab", true);
            Scribe_Values.Look(ref minimap, "minimap", true);
            Scribe_Values.Look(ref autoStart, "autoStart", true);
            Scribe_Values.Look(ref compactFilters, "compactFilters", true);
            Scribe_Values.Look(ref groupByMod, "groupByMod", false);
            Scribe_Values.Look(ref semiRandom, "semiRandom", false);
            Scribe_Deep.Look(ref fixes, "fixes");
        }
    }

    public class RAMod : Mod
    {
        public static RASettings S;
        public static RAMod Instance;
        private Vector2 scroll;

        public RAMod(ModContentPack content) : base(content)
        {
            Instance = this;
            S = GetSettings<RASettings>();
            if (S.fixes == null)
            {
                // first start: take over the settings of the separate Research Fixer mod, if there are any
                S.fixes = new Fixes.RFSettings();
                Fixes.RFMod.Migrate(S.fixes);
                WriteSettings();
            }
            try { new Harmony("bolek98.researchatlas").CreateClassProcessor(typeof(Patch_FinishProject)).Patch(); }
            catch (Exception e) { Log.Error("[Research Atlas] patches: " + e); }
            try { Fixes.RFMod.Init(content, S.fixes); }
            catch (Exception e) { Log.Error("[Research Atlas] fixes: " + e); }
        }

        public override string SettingsCategory() => "Research Atlas";

        public override void DoSettingsWindowContents(Rect r)
        {
            var l = new Listing_Standard();
            l.Begin(new Rect(r.x, r.y, r.width * 0.6f, 250f));
            bool before = S.replaceTab;
            l.CheckboxLabeled("RA_SetReplace".Translate(), ref S.replaceTab, "RA_SetReplaceTip".Translate());
            l.CheckboxLabeled("RA_SetAutoStart".Translate(), ref S.autoStart, "RA_SetAutoStartTip".Translate());
            l.CheckboxLabeled("RA_SetCompact".Translate(), ref S.compactFilters, "RA_SetCompactTip".Translate());
            l.CheckboxLabeled("RA_SetMinimap".Translate(), ref S.minimap, "RA_SetMinimapTip".Translate());
            l.CheckboxLabeled("RA_GroupByMod".Translate(), ref S.groupByMod, "RA_GroupByModTip".Translate());
            l.CheckboxLabeled("RA_SetRandom".Translate(), ref S.semiRandom, "RA_SetRandomTip".Translate());
            l.Gap();
            if (l.ButtonText("RA_SetClearCache".Translate())) { AtlasView.ClearCache(); Messages.Message("RA_CacheCleared".Translate(), MessageTypeDefOf.NeutralEvent, false); }
            l.End();
            if (before != S.replaceTab) Startup.ApplyTab();
            // the fixes (Research Fixer) below
            Rect fx = new Rect(r.x, r.y + 256f, r.width, r.height - 256f);
            Widgets.DrawLineHorizontal(fx.x, fx.y - 4f, fx.width);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(fx.x, fx.y, fx.width, 32f), "RA_FixesTitle".Translate());
            Text.Font = GameFont.Small;
            Fixes.RFMod.DrawSettings(new Rect(fx.x, fx.y + 34f, fx.width, Mathf.Max(380f, fx.height - 34f)));
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        private static Type vanillaClass;

        static Startup() { ApplyTab(); }

        // our window is a MainTabWindow_Research, so everything in the game that opens the research tab
        // and casts it (alerts, info card links) keeps working
        public static void ApplyTab()
        {
            try
            {
                MainButtonDef b = MainButtonDefOf.Research;
                if (b == null) return;
                if (vanillaClass == null) vanillaClass = b.tabWindowClass;
                Type want = RAMod.S.replaceTab ? typeof(MainTabWindow_Atlas) : vanillaClass;
                if (b.tabWindowClass == want) return;
                b.tabWindowClass = want;
                // the button creates its window once – drop the old one so the next click makes the new kind
                Traverse.Create(b).Field("tabWindowInt").SetValue(null);
            }
            catch (Exception e) { Log.Warning("[Research Atlas] research tab: " + e.Message); }
        }
    }

    // the research queue of this game
    public class ResearchQueue : GameComponent
    {
        public List<ResearchProjectDef> queue = new List<ResearchProjectDef>();
        // 0.2.0 semi-random mode: three projects offered to choose from, new ones when one is picked/finished
        public List<ResearchProjectDef> offers = new List<ResearchProjectDef>();
        public int rerolls = 2;
        public static ResearchQueue Get => Current.Game?.GetComponent<ResearchQueue>();

        public ResearchQueue(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref queue, "queue", LookMode.Def);
            Scribe_Collections.Look(ref offers, "offers", LookMode.Def);
            Scribe_Values.Look(ref rerolls, "rerolls", 2);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (queue == null) queue = new List<ResearchProjectDef>(); queue.RemoveAll(q => q == null);
                if (offers == null) offers = new List<ResearchProjectDef>(); offers.RemoveAll(q => q == null);
            }
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            Advance();
        }

        public int IndexOf(ResearchProjectDef d) => queue.IndexOf(d);

        // offers stay until one of them is finished; finished or no longer possible ones are replaced
        public List<ResearchProjectDef> Offers()
        {
            offers.RemoveAll(o => o == null || o.IsFinished || (!o.CanStartNow && !Find.ResearchManager.IsCurrentProject(o)));
            bool picked = offers.Any(o => Find.ResearchManager.IsCurrentProject(o));
            if (!picked && offers.Count < 3) Fill();
            return offers;
        }

        private void Fill()
        {
            try
            {
                var pool = DefDatabase<ResearchProjectDef>.AllDefsListForReading
                    .Where(d => d != null && d.knowledgeCategory == null && !d.IsFinished && d.CanStartNow && !offers.Contains(d)).ToList();
                while (offers.Count < 3 && pool.Count > 0)
                {
                    var d = pool.RandomElement();
                    pool.Remove(d);
                    offers.Add(d);
                }
            }
            catch { }
        }

        public void Pick(ResearchProjectDef d) { offers.RemoveAll(o => o != d); }

        public void Reroll()
        {
            if (rerolls <= 0) { Messages.Message("RA_RandomNoRerolls".Translate(), MessageTypeDefOf.RejectInput, false); return; }
            rerolls--;
            offers.Clear();
            Fill();
        }

        public void Notify_Finished()
        {
            if (offers.Count > 0 && offers.All(o => o.IsFinished || !Find.ResearchManager.IsCurrentProject(o))) { offers.RemoveAll(o => o.IsFinished); }
            if (rerolls < 3) rerolls++;
        }

        // the missing prerequisites (also hidden ones) in an order that can be researched, then the project
        public void QueuePath(ResearchProjectDef target)
        {
            var path = new List<ResearchProjectDef>();
            var seen = new HashSet<ResearchProjectDef>();
            void Visit(ResearchProjectDef d, int depth)
            {
                if (d == null || depth > 200 || !seen.Add(d) || d.IsFinished) return;
                if (d.prerequisites != null) foreach (var p in d.prerequisites) Visit(p, depth + 1);
                if (d.hiddenPrerequisites != null) foreach (var p in d.hiddenPrerequisites) Visit(p, depth + 1);
                path.Add(d);
            }
            Visit(target, 0);
            foreach (var d in path) if (!queue.Contains(d) && d.knowledgeCategory == null) queue.Add(d);
            // a queued project must come after its prerequisites even if it was queued earlier
            SortByPrerequisites();
            Advance();
        }

        public void Add(ResearchProjectDef d)
        {
            if (d == null || d.IsFinished || queue.Contains(d) || d.knowledgeCategory != null) return;
            queue.Add(d);
            SortByPrerequisites();
            Advance();
        }

        public void Remove(ResearchProjectDef d) { queue.Remove(d); }

        public void Move(int from, int to)
        {
            if (from < 0 || from >= queue.Count) return;
            var d = queue[from];
            queue.RemoveAt(from);
            if (to > from) to--;
            queue.Insert(Mathf.Clamp(to, 0, queue.Count), d);
            SortByPrerequisites();
        }

        private void SortByPrerequisites()
        {
            // stable: keeps the player's order except where a prerequisite would come after its project
            for (int guard = 0; guard < queue.Count * queue.Count + 1; guard++)
            {
                bool moved = false;
                for (int i = 0; i < queue.Count && !moved; i++)
                {
                    var d = queue[i];
                    var pre = (d.prerequisites ?? new List<ResearchProjectDef>()).Concat(d.hiddenPrerequisites ?? new List<ResearchProjectDef>());
                    foreach (var p in pre)
                    {
                        int pi = queue.IndexOf(p);
                        if (pi > i) { queue.RemoveAt(pi); queue.Insert(i, p); moved = true; break; }
                    }
                }
                if (!moved) break;
            }
        }

        public void Advance()
        {
            queue.RemoveAll(q => q == null || q.IsFinished);
            if (!RAMod.S.autoStart || queue.Count == 0) return;
            ResearchManager rm = Find.ResearchManager;
            ResearchProjectDef cur = rm.GetProject();
            if (cur != null && !cur.IsFinished) return;
            foreach (var q in queue)
                if (q.CanStartNow) { rm.SetCurrentProject(q); return; }
        }
    }

    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class Patch_FinishProject
    {
        public static void Postfix()
        {
            try { ResearchQueue.Get?.Advance(); } catch { }
            try { if (RAMod.S.semiRandom) ResearchQueue.Get?.Notify_Finished(); } catch { }
        }
    }
}
