using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace QoLFW
{
    // The "QoL" button in the bottom bar: map search, prisoners, colony actions.
    public class MainTabWindow_QoL : MainTabWindow
    {
        private static int tab;
        private readonly QuickSearchWidget search = new QuickSearchWidget();
        private string lastQuery = null;
        private int lastBuild = -9999;
        private List<Hit> hits = new List<Hit>();
        private Vector2 scroll;

        private class Hit
        {
            public ThingDef def;
            public string label;
            public List<Thing> things = new List<Thing>();
            public int count;
            public int next;
        }

        public override Vector2 RequestedTabSize => new Vector2(760f, 560f);

        public override void PreOpen()
        {
            base.PreOpen();
            lastQuery = null;
            if (tab == 0) search.Focus();
        }

        public override void DoWindowContents(Rect r)
        {
            string[] tabs = { "QFW_TabSearch", "QFW_TabPrisoners", "QFW_TabColony" };
            float bw = 170f;
            for (int i = 0; i < tabs.Length; i++)
            {
                Rect b = new Rect(r.x + i * (bw + 6f), r.y, bw, 30f);
                MUI.Fill(b, tab == i ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(b, tab == i ? MUI.Accent : MUI.Border);
                Widgets.DrawHighlightIfMouseover(b);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = tab == i ? MUI.Accent : MUI.Text1;
                Widgets.Label(b, tabs[i].Translate());
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b) && tab != i)
                {
                    tab = i; scroll = Vector2.zero;
                    SoundDefOf.Click.PlayOneShotOnCamera();
                    if (tab == 0) search.Focus(); else search.Unfocus();
                }
            }
            Rect body = new Rect(r.x, r.y + 40f, r.width, r.height - 40f);
            Map map = Find.CurrentMap;
            if (map == null) return;
            try
            {
                if (tab == 0) SearchTab(body, map);
                else if (tab == 1) PrisonersTab(body, map);
                else ColonyTab(body, map);
            }
            finally
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
        }

        // ---------------- map search ----------------
        private void SearchTab(Rect r, Map map)
        {
            search.OnGUI(new Rect(r.x, r.y, 300f, 24f));
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 312f, r.y + 4f, r.width - 312f, 22f), "QFW_SearchHint".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            string q = Q.Norm(search.filter.Text ?? "").Trim();
            if (q != lastQuery || Find.TickManager.TicksGame - lastBuild > 250)
            {
                Build(map, q);
                lastQuery = q;
                lastBuild = Find.TickManager.TicksGame;
            }
            Rect list = new Rect(r.x, r.y + 32f, r.width, r.height - 32f);
            if (q.Length < 2)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(list, "QFW_SearchType".Translate());
                GUI.color = Color.white;
                return;
            }
            if (hits.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(list, "QFW_SearchNone".Translate());
                GUI.color = Color.white;
                return;
            }
            float rowH = 30f;
            Rect view = new Rect(0f, 0f, list.width - 18f, hits.Count * rowH);
            Widgets.BeginScrollView(list, ref scroll, view);
            try
            {
                for (int i = 0; i < hits.Count; i++)
                {
                    Hit h = hits[i];
                    Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                    if (i % 2 == 0) MUI.Fill(row, MUI.CardBg);
                    Widgets.DrawHighlightIfMouseover(row);
                    Thing first = h.things.FirstOrDefault(t => t != null && t.Spawned);
                    if (first != null) Widgets.ThingIcon(new Rect(row.x + 4f, row.y + 2f, 24f, 24f), first);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Widgets.Label(new Rect(row.x + 36f, row.y, row.width - 260f, row.height), h.label);
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(row.xMax - 220f, row.y, 100f, row.height), "×" + h.count);
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                    string btn = h.things.Count > 1 ? "QFW_ShowNext".Translate(Mathf.Min(h.next + 1, h.things.Count), h.things.Count).ToString() : "QFW_Show".Translate().ToString();
                    if (Widgets.ButtonText(new Rect(row.xMax - 110f, row.y + 2f, 106f, 24f), btn) || (Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 120f, row.height))))
                        Jump(h);
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private void Jump(Hit h)
        {
            h.things.RemoveAll(t => t == null || !t.Spawned);
            if (h.things.Count == 0) return;
            if (h.next >= h.things.Count) h.next = 0;
            Thing t = h.things[h.next];
            h.next = (h.next + 1) % h.things.Count;
            CameraJumper.TryJumpAndSelect(t);
        }

        private void Build(Map map, string q)
        {
            var old = hits.ToDictionary(x => x.label, x => x.next);
            hits.Clear();
            if (q.Length < 2) return;
            var byKey = new Dictionary<string, Hit>();
            List<Thing> all = map.listerThings.AllThings;
            for (int i = 0; i < all.Count; i++)
            {
                Thing t = all[i];
                if (t == null || !t.Spawned) continue;
                ThingCategory c = t.def.category;
                if (c != ThingCategory.Item && c != ThingCategory.Building && c != ThingCategory.Pawn && c != ThingCategory.Plant) continue;
                if (t.Position.Fogged(map)) continue;
                string label = t is Pawn p ? p.LabelShortCap : t.def.LabelCap.ToString();
                if (t.Stuff != null && c == ThingCategory.Building) label = t.LabelNoCount.CapitalizeFirst();
                if (!Q.Norm(label).Contains(q) && !Q.Norm(t.def.defName).Contains(q)) continue;
                Hit h;
                if (!byKey.TryGetValue(label, out h))
                {
                    h = new Hit { def = t.def, label = label };
                    byKey[label] = h;
                }
                h.things.Add(t);
                h.count += c == ThingCategory.Item ? t.stackCount : 1;
            }
            hits = byKey.Values.OrderBy(x => x.def.category == ThingCategory.Item ? 0 : x.def.category == ThingCategory.Pawn ? 1 : x.def.category == ThingCategory.Building ? 2 : 3)
                .ThenBy(x => x.label).Take(300).ToList();
            foreach (Hit h in hits)
            {
                int n;
                if (old.TryGetValue(h.label, out n)) h.next = n;
            }
        }

        // ---------------- prisoners ----------------
        private static bool AnyBloodfeeder(Map map)
        {
            if (!ModsConfig.BiotechActive) return false;
            foreach (Pawn p in map.mapPawns.FreeColonistsAndPrisonersSpawned)
                if (p.IsBloodfeeder()) return true;
            return false;
        }
        private static readonly Func<Pawn, bool> isStudiable = MakeCall<Func<Pawn, bool>>("IsStudiable");

        private static T MakeCall<T>(string name) where T : class
        {
            try
            {
                var m = AccessTools.Method(typeof(ITab_Pawn_Visitor), name);
                return m == null ? null : (T)(object)Delegate.CreateDelegate(typeof(T), m);
            }
            catch { return null; }
        }

        private void PrisonersTab(Rect r, Map map)
        {
            List<Pawn> pris = map.mapPawns.PrisonersOfColonySpawned.ToList();
            List<Pawn> slaves = map.mapPawns.SlavesOfColonySpawned.ToList();
            if (pris.Count == 0 && slaves.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(r, "QFW_NoPrisoners".Translate());
                GUI.color = Color.white;
                return;
            }
            float rowH = 34f, headH = 26f;
            float total = (pris.Count > 0 ? headH + pris.Count * rowH : 0f) + (slaves.Count > 0 ? headH + 8f + slaves.Count * rowH : 0f);
            Rect view = new Rect(0f, 0f, r.width - 18f, total);
            Widgets.BeginScrollView(r, ref scroll, view);
            try
            {
                float y = 0f;
                if (pris.Count > 0)
                {
                    Head(view.width, y, "QFW_PColName", "QFW_PColMode");
                    y += headH;
                    foreach (Pawn p in pris) { Row(p, new Rect(0f, y, view.width, rowH - 2f), false); y += rowH; }
                }
                if (slaves.Count > 0)
                {
                    y += 8f;
                    Head(view.width, y, "QFW_SColName", "QFW_SColAction");
                    y += headH;
                    foreach (Pawn p in slaves) { Row(p, new Rect(0f, y, view.width, rowH - 2f), true); y += rowH; }
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static readonly float[] col = { 0f, 200f, 380f, 470f, 550f, 650f };

        private static void Head(float w, float y, string name, string mode)
        {
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            string[] head = { name, mode, "QFW_PColRes", "QFW_PColWill", "QFW_PColRecruit", "QFW_PColDays" };
            for (int i = 0; i < head.Length; i++)
                Widgets.Label(new Rect(col[i] + 4f, y + 4f, 140f, 20f), head[i].Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void Row(Pawn p, Rect row, bool slave)
        {
            QSettings s = QoLMod.S;
            MUI.Fill(row, MUI.CardBg);
            Widgets.DrawHighlightIfMouseover(row);
            Text.Anchor = TextAnchor.MiddleLeft;
            Rect name = new Rect(row.x + 4f, row.y, col[1] - 8f, row.height);
            Widgets.Label(name, p.LabelShortCap);
            TooltipHandler.TipRegion(name, p.LabelCap + "\n" + (p.def.label ?? "").CapitalizeFirst() + (p.Faction != null ? "\n" + p.Faction.Name : ""));
            if (Widgets.ButtonInvisible(name)) CameraJumper.TryJumpAndSelect(p);

            bool rec = p.guest != null && p.guest.Recruitable;
            Rect act = new Rect(row.x + col[1], row.y + 3f, col[2] - col[1] - 8f, 26f);
            if (!slave)
            {
                PrisonerInteractionModeDef mode = p.guest.ExclusiveInteractionMode;
                if (Widgets.ButtonText(act, mode != null ? mode.LabelCap.ToString() : "-")) ModeMenu(p);
            }
            else if (s.prisoners && s.slaveRecruit && rec)
            {
                TooltipHandler.TipRegion(act, "QFW_MakeColonist_Desc".Translate());
                if (Widgets.ButtonText(act, "QFW_MakeColonist".Translate()))
                {
                    Pawn pawn = p;
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("QFW_MakeColonist_Confirm".Translate(pawn.LabelShort), () =>
                    {
                        try
                        {
                            RecruitUtility.Recruit(pawn, Faction.OfPlayer);
                            if (pawn.needs?.mood != null) pawn.needs.mood.thoughts.memories.TryGainMemory(ThoughtDefOf.WasEnslaved);
                            Messages.Message("QFW_MadeColonist".Translate(pawn.LabelShort), pawn, MessageTypeDefOf.PositiveEvent, false);
                        }
                        catch (Exception e) { Log.Warning("[QoL Faster Work] slave to colonist: " + e.Message); }
                    }));
                }
            }
            else
            {
                GUI.color = MUI.Muted;
                Widgets.Label(act, "-");
                GUI.color = Color.white;
            }
            if (!slave)
            {
                Widgets.Label(new Rect(row.x + col[2] + 4f, row.y, 80f, row.height), p.guest.resistance >= 0f ? p.guest.resistance.ToString("0.0") : "-");
                Widgets.Label(new Rect(row.x + col[3] + 4f, row.y, 80f, row.height), p.guest.will >= 0f ? p.guest.will.ToString("0.0") : "-");
            }
            QoLGameComp gc = QoLGameComp.Instance;
            string rs = rec ? "QFW_Yes".Translate().ToString() : "QFW_No".Translate().ToString();
            if (!rec && gc != null)
            {
                float left = gc.DaysLeft(p);
                if (left >= 0f) rs += " (" + "QFW_InDays".Translate(left.ToString("0.0")) + ")";
            }
            GUI.color = rec ? MUI.Accent : MUI.Warn;
            Widgets.Label(new Rect(row.x + col[4] + 4f, row.y, col[5] - col[4] - 4f, row.height), rs);
            GUI.color = Color.white;
            Widgets.Label(new Rect(row.x + col[5] + 4f, row.y, 90f, row.height), gc != null ? gc.DaysHeld(p).ToString("0.0") : "-");
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static void ModeMenu(Pawn p)
        {
            var opts = new List<FloatMenuOption>();
            bool wild = p.IsWildMan();
            foreach (PrisonerInteractionModeDef m in DefDatabase<PrisonerInteractionModeDef>.AllDefs.Where(x => !x.isNonExclusiveInteraction).OrderBy(x => x.listOrder))
            {
                if (!p.guest.Recruitable && m.hideIfNotRecruitable) continue;
                if (wild && !m.allowOnWildMan) continue;
                if (m.hideIfNoBloodfeeders && p.MapHeld != null && !AnyBloodfeeder(p.MapHeld)) continue;
                if (m.hideOnHemogenicPawns && ModsConfig.BiotechActive && p.genes != null && p.genes.HasActiveGene(GeneDefOf.Hemogenic)) continue;
                if (!m.allowInClassicIdeoMode && Find.IdeoManager.classicMode) continue;
                if (ModsConfig.AnomalyActive)
                {
                    if (m.hideIfNotStudiableAsPrisoner && (isStudiable == null || !isStudiable(p))) continue;
                    if (m.hideIfGrayFleshNotAppeared && !Find.Anomaly.hasSeenGrayFlesh) continue;
                }
                PrisonerInteractionModeDef mode = m;
                opts.Add(new FloatMenuOption(m.LabelCap, () =>
                {
                    if (mode == PrisonerInteractionModeDefOf.Convert && p.guest.ideoForConversion == null)
                        p.guest.ideoForConversion = Faction.OfPlayer.ideos.PrimaryIdeo;
                    p.guest.SetExclusiveInteraction(mode);
                }));
            }
            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }

        // ---------------- colony actions ----------------
        private void ColonyTab(Rect r, Map map)
        {
            float y = r.y;
            Action<string, string, Action> button = (key, desc, act) =>
            {
                Rect b = new Rect(r.x, y, 260f, 34f);
                if (Widgets.ButtonText(b, key.Translate())) act();
                GUI.color = MUI.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x + 272f, y + 2f, r.width - 272f, 34f), desc.Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                y += 44f;
            };
            button("QFW_AllEat", "QFW_AllEat_Desc", () => AllEat(map));
            button("QFW_AllSleep", "QFW_AllSleep_Desc", () => AllSleep(map));
            button("QFW_AllWake", "QFW_AllWake_Desc", () => AllWake(map));
            if (QoLMod.S.pipette)
                button("QFW_PipetteBtn", "QFW_PipetteBtn_Desc", () => { Close(); Pipette.Start(); });
        }

        private static IEnumerable<Pawn> Free(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => !p.Drafted && !p.Downed && !p.InMentalState && p.jobs != null && p.needs != null).ToList();

        private static void AllEat(Map map)
        {
            int go = 0, skip = 0;
            var giver = new JobGiver_GetFood();
            foreach (Pawn p in Free(map))
            {
                if (p.needs.food == null || p.needs.food.CurLevelPercentage > 0.8f) { skip++; continue; }
                Job j = null;
                try { j = giver.TryIssueJobPackage(p, default(JobIssueParams)).Job; } catch { }
                if (j == null) { skip++; continue; }
                p.jobs.TryTakeOrderedJob(j, JobTag.SatisfyingNeeds);
                go++;
            }
            Q.Msg("QFW_AllEat_Done".Translate(go, skip));
        }

        private static void AllSleep(Map map)
        {
            int go = 0, skip = 0;
            var giver = new JobGiver_GetRest();
            foreach (Pawn p in Free(map))
            {
                if (p.needs.rest == null || p.needs.rest.CurLevelPercentage > 0.9f) { skip++; continue; }
                if (p.CurJobDef == JobDefOf.LayDown) { skip++; continue; }
                Job j = null;
                try { j = giver.TryIssueJobPackage(p, default(JobIssueParams)).Job; } catch { }
                if (j == null) { skip++; continue; }
                p.jobs.TryTakeOrderedJob(j, JobTag.SatisfyingNeeds);
                go++;
            }
            Q.Msg("QFW_AllSleep_Done".Translate(go, skip));
        }

        private static void AllWake(Map map)
        {
            int n = 0;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed || p.CurJobDef != JobDefOf.LayDown) continue;
                if (p.health?.hediffSet != null && HealthAIUtility.ShouldSeekMedicalRest(p)) continue;
                p.jobs.EndCurrentJob(JobCondition.InterruptForced);
                n++;
            }
            Q.Msg("QFW_AllWake_Done".Translate(n));
        }
    }
}
