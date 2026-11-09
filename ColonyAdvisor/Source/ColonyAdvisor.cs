using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyAdvisor
{
    // ---------------------------------------------------------------------------
    // Colony Advisor 0.1.0 – settings, the "Advisor" tab, the alert and the morning plan.
    // ---------------------------------------------------------------------------
    public class AdvisorSettings : ModSettings
    {
        public bool recommended = true;
        public bool showAlert = true;          // alert on the right when something urgent/important waits
        public bool alertTipsToo = false;      // the alert also for plain tips
        public bool morningPlan = true;        // every morning: the three most important things today
        public bool morningAsLetter = false;   // as a letter instead of a short message
        public string goal = "";               // chosen way to finish the game ("" = the one closest to done)
        // 0.3.0 story compass
        public int journalHours = 4;           // the journal is worked out again every few in-game hours (or when opened)
        public bool journalShowInactive = true;
        public bool questAlerts = true;        // letter 1 day and 6 hours before a quest offer runs out
        public bool questAlertsGoodOnly = false;
        public int expTeamMin = 5, expTeamMax = 8;
        public bool expKeepDoctor = true, expKeepCook = true, expKeepBuilder = false;
        public bool cabinFever = true;         // "restless" after many days in one place
        public int cabinDays = 20;
        public bool sawNewPlaces = true;       // good thought when the colonists see a new place
        public bool newPlaceXp = true;         // a little XP for a new place
        public bool rumors = true;             // a rumor of treasure every 7–15 days
        public int rumorMinDays = 7, rumorMaxDays = 15;
        public bool gentlePush = false;        // a nudge letter when the colony sits at home too long

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref showAlert, "showAlert", true);
            Scribe_Values.Look(ref alertTipsToo, "alertTipsToo", false);
            Scribe_Values.Look(ref morningPlan, "morningPlan", true);
            Scribe_Values.Look(ref morningAsLetter, "morningAsLetter", false);
            Scribe_Values.Look(ref goal, "goal", "");
            Scribe_Values.Look(ref journalHours, "journalHours", 4);
            Scribe_Values.Look(ref journalShowInactive, "journalShowInactive", true);
            Scribe_Values.Look(ref questAlerts, "questAlerts", true);
            Scribe_Values.Look(ref questAlertsGoodOnly, "questAlertsGoodOnly", false);
            Scribe_Values.Look(ref expTeamMin, "expTeamMin", 5);
            Scribe_Values.Look(ref expTeamMax, "expTeamMax", 8);
            Scribe_Values.Look(ref expKeepDoctor, "expKeepDoctor", true);
            Scribe_Values.Look(ref expKeepCook, "expKeepCook", true);
            Scribe_Values.Look(ref expKeepBuilder, "expKeepBuilder", false);
            Scribe_Values.Look(ref cabinFever, "cabinFever", true);
            Scribe_Values.Look(ref cabinDays, "cabinDays", 20);
            Scribe_Values.Look(ref sawNewPlaces, "sawNewPlaces", true);
            Scribe_Values.Look(ref newPlaceXp, "newPlaceXp", true);
            Scribe_Values.Look(ref rumors, "rumors", true);
            Scribe_Values.Look(ref rumorMinDays, "rumorMinDays", 7);
            Scribe_Values.Look(ref rumorMaxDays, "rumorMaxDays", 15);
            Scribe_Values.Look(ref gentlePush, "gentlePush", false);
        }
    }

    public class AdvisorMod : Mod
    {
        public const string Version = "0.4.0";
        public static AdvisorSettings S;
        private static List<string> recBullets;

        public AdvisorMod(ModContentPack content) : base(content)
        {
            S = GetSettings<AdvisorSettings>();
            MUI.Init(new AdvisorSettings(), null, "recommended", "goal");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var harmony = new Harmony("bolek98.colonyadvisor");
            try { MUI.PatchWindow(harmony, typeof(AdvisorMod)); } catch { }
            try { harmony.PatchAll(typeof(AdvisorMod).Assembly); } catch (Exception e) { Log.Warning("[Colony Advisor] " + e.Message); }   // 0.2.0
            Log.Message("[Colony Advisor] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "CA_ModName".Translate();

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        private static int setTab;
        private static Vector2 setScroll;
        private static float setH = 600f;

        private static int IntSlider(Listing_Standard l, string key, int v, int min, int max, string field, string unit = "") =>
            Mathf.RoundToInt(MUI.Slider(l, key.Translate() + ": " + v + unit, v, min, max, (key + "_Desc").Translate(), S, field));

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "CA_Rec1".Translate(), "CA_Rec2".Translate(), "CA_Rec3".Translate(), "CA_Rec4".Translate(), "CA_Rec5".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            // 0.3.0: tabs
            string[] names = { "CA_ST_General".Translate(), "CA_ST_Journal".Translate(), "CA_ST_Quests".Translate(), "CA_ST_Exp".Translate(), "CA_ST_Mood".Translate(), "CA_ST_Rumors".Translate(), "CA_ST_Adv".Translate() };
            float tw = (r.width - 6f * (names.Length - 1)) / names.Length;
            for (int i = 0; i < names.Length; i++)
            {
                Rect b = new Rect(r.x + i * (tw + 6f), y, tw, 28f);
                MUI.Fill(b, setTab == i ? MUI.OnBg : MUI.CardBg2, 4f);
                if (setTab == i) MUI.Frame(b, MUI.Accent, 1f, 4f);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = setTab == i ? MUI.Text1 : MUI.Muted;
                Widgets.Label(b, names[i]);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b)) { setTab = i; setScroll = Vector2.zero; }
            }
            y += 34f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, rest.width - 18f, setH);
            Widgets.BeginScrollView(rest, ref setScroll, view);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(new Rect(0f, 0f, view.width, 99999f));
                switch (setTab)
                {
                    case 0:
                        MUI.Section(l, "CA_SecAlert".Translate());
                        Check(l, "CA_ShowAlert", ref S.showAlert, "showAlert");
                        Check(l, "CA_AlertTips", ref S.alertTipsToo, "alertTipsToo");
                        MUI.Section(l, "CA_SecMorning".Translate());
                        Check(l, "CA_Morning", ref S.morningPlan, "morningPlan");
                        Check(l, "CA_MorningLetter", ref S.morningAsLetter, "morningAsLetter");
                        break;
                    case 1:
                        MUI.Section(l, "CA_ST_Journal".Translate());
                        Check(l, "CA_SJ_Inactive", ref S.journalShowInactive, "journalShowInactive");
                        break;
                    case 2:
                        MUI.Section(l, "CA_ST_Quests".Translate());
                        Check(l, "CA_SQ_Alerts", ref S.questAlerts, "questAlerts");
                        Check(l, "CA_SQ_Good", ref S.questAlertsGoodOnly, "questAlertsGoodOnly");
                        break;
                    case 3:
                        MUI.Section(l, "CA_ST_Exp".Translate());
                        S.expTeamMin = IntSlider(l, "CA_SX_Min", S.expTeamMin, 1, 12, "expTeamMin");
                        S.expTeamMax = Math.Max(S.expTeamMin, IntSlider(l, "CA_SX_Max", S.expTeamMax, 1, 16, "expTeamMax"));
                        Check(l, "CA_SX_Doctor", ref S.expKeepDoctor, "expKeepDoctor");
                        Check(l, "CA_SX_Cook", ref S.expKeepCook, "expKeepCook");
                        Check(l, "CA_SX_Builder", ref S.expKeepBuilder, "expKeepBuilder");
                        break;
                    case 4:
                        MUI.Section(l, "CA_ST_Mood".Translate());
                        Check(l, "CA_SM_Cabin", ref S.cabinFever, "cabinFever");
                        S.cabinDays = IntSlider(l, "CA_SM_Days", S.cabinDays, 5, 60, "cabinDays", " d");
                        Check(l, "CA_SM_Saw", ref S.sawNewPlaces, "sawNewPlaces");
                        Check(l, "CA_SM_Xp", ref S.newPlaceXp, "newPlaceXp");
                        break;
                    case 5:
                        MUI.Section(l, "CA_ST_Rumors".Translate());
                        Check(l, "CA_SR_On", ref S.rumors, "rumors");
                        S.rumorMinDays = IntSlider(l, "CA_SR_Min", S.rumorMinDays, 2, 30, "rumorMinDays", " d");
                        S.rumorMaxDays = Math.Max(S.rumorMinDays, IntSlider(l, "CA_SR_Max", S.rumorMaxDays, 2, 45, "rumorMaxDays", " d"));
                        Check(l, "CA_SR_Push", ref S.gentlePush, "gentlePush");
                        break;
                    case 6:
                        MUI.Section(l, "CA_ST_Adv".Translate());
                        S.journalHours = IntSlider(l, "CA_SA_Hours", S.journalHours, 1, 24, "journalHours", " h");
                        break;
                }
                setH = l.CurHeight + 20f;
                l.End();
            }
            finally { MUI.EndLock(new Rect(0f, 0f, view.width, setH)); }
            Widgets.EndScrollView();
        }
    }

    // ---------------------------------------------------------------------------
    // The alert on the right: the most important thing right now; click opens the advisor.
    // ---------------------------------------------------------------------------
    public class Alert_Advisor : Alert
    {
        private Report last;

        public Alert_Advisor()
        {
            defaultLabel = "CA_ModName".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertPriority Priority => last != null && last.UrgentCount > 0 ? AlertPriority.High : AlertPriority.Medium;

        private IEnumerable<Tip> Shown(Report r) =>
            r.tips.Where(t => t.level == Level.Urgent || t.level == Level.Important || (AdvisorMod.S.alertTipsToo && t.level == Level.Tip));

        public override string GetLabel()
        {
            if (last == null) return defaultLabel;
            int n = Shown(last).Count();
            Tip top = Shown(last).FirstOrDefault();
            string head = top == null ? "" : top.title;
            if (head.Length > 32) head = head.Substring(0, 30) + "…";
            return "CA_AlertLabel".Translate(head, n).Resolve();
        }

        public override TaggedString GetExplanation()
        {
            if (last == null) return "";
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("CA_AlertHead".Translate().Resolve());
            sb.AppendLine();
            foreach (Tip t in Shown(last).Take(8))
                sb.AppendLine((t.level == Level.Urgent ? "‼ " : "• ") + t.title);
            sb.AppendLine();
            sb.Append("CA_AlertClick".Translate().Resolve());
            return sb.ToString();
        }

        public override AlertReport GetReport()
        {
            if (AdvisorMod.S == null || !AdvisorMod.S.showAlert || Find.CurrentMap == null) return false;
            last = Advisor.Get(30f);
            return last != null && Shown(last).Any();
        }

        protected override void OnClick()
        {
            AdvisorWindow.ShowTab(0);
        }
    }

    // ---------------------------------------------------------------------------
    // The morning plan: once a day at 7:00 the three most important things.
    // ---------------------------------------------------------------------------
    public class AdvisorGameComponent : GameComponent
    {
        private int lastDay = -1;

        public AdvisorGameComponent(Game game) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref lastDay, "lastDay", -1);
        }

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 0) return;
            try
            {
                if (AdvisorMod.S == null || !AdvisorMod.S.morningPlan) return;
                Map map = Find.AnyPlayerHomeMap;
                if (map == null) return;
                int hour = GenLocalDate.HourOfDay(map);
                int day = GenDate.DaysPassed;
                if (hour != 7 || day == lastDay) return;
                lastDay = day;
                Advisor.Invalidate();
                Report r = Advisor.Get(0f);
                if (r == null) return;
                List<Tip> top = r.tips.Take(3).ToList();
                Ending goal = Advisor.Goal(r);
                var sb = new System.Text.StringBuilder();
                if (top.Count == 0) sb.AppendLine("CA_Morning_Calm".Translate().Resolve());
                int i = 1;
                foreach (Tip t in top) sb.AppendLine((i++) + ". " + t.title);
                if (goal?.next != null) sb.AppendLine("CA_Morning_Goal".Translate(goal.title, goal.next.label).Resolve());
                string text = sb.ToString().TrimEnd();
                if (AdvisorMod.S.morningAsLetter)
                    Find.LetterStack.ReceiveLetter("CA_Morning_Title".Translate(r.score), text, LetterDefOf.NeutralEvent);
                else
                    Messages.Message("CA_Morning_Title".Translate(r.score) + ": " + string.Join(" · ", top.Select(t => t.title).ToArray()), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e) { Log.WarningOnce("[Colony Advisor] morning plan failed: " + e.Message, 0x5A41E1); }
        }
    }

    // ---------------------------------------------------------------------------
    // The "Advisor" tab
    // ---------------------------------------------------------------------------
    public class AdvisorWindow : MainTabWindow
    {
        public static int tab;
        private Vector2 scroll;
        private float viewH = 1000f;

        public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(980f, UI.screenWidth - 40f), Mathf.Min(760f, UI.screenHeight - 120f));

        public static void ShowTab(int t)
        {
            tab = t;
            try
            {
                MainButtonDef d = DefDatabase<MainButtonDef>.GetNamedSilentFail("ColonyAdvisor");
                if (d != null && Find.MainTabsRoot.OpenTab != d) Find.MainTabsRoot.SetCurrentTab(d);
            }
            catch { }
        }

        public override void PreOpen()
        {
            base.PreOpen();
            Advisor.Invalidate();
            Compass.Invalidate();
            CompassTabs.Reset();
        }

        // 0.3.0: tab ids stay the same (0 = now ... 5 = supplies, 6 = journal, 7 = expeditions); this is the order shown
        private static readonly int[] order = { 0, 6, 4, 7, 3, 1, 2, 5 };

        private static string T(string key, params NamedArgument[] a) => key.Translate(a).Resolve();

        public override void DoWindowContents(Rect inRect)
        {
            Report r = Advisor.Get(3f);
            if (r == null) { Widgets.Label(inRect, T("CA_NoMap")); return; }
            float y = inRect.y;
            y = Header(new Rect(inRect.x, y, inRect.width, 0f), r) + 6f;

            string[] names = { T("CA_Tab_Now"), T("CA_Tab_Make"), T("CA_Tab_Research"), T("CA_Tab_Goal"), T("CA_Tab_Quests"), T("CA_Tab_Supplies"), T("CA_Tab_Journal"), T("CA_Tab_Exp") };
            float tw = Mathf.Min(170f, (inRect.width - 6f * (names.Length - 1)) / names.Length);
            for (int k = 0; k < order.Length; k++)
            {
                int i = order[k];
                Rect b = new Rect(inRect.x + k * (tw + 6f), y, tw, 30f);
                string label = names[i];
                if (i == 0 && r.UrgentCount + r.ImportantCount > 0) label += " (" + (r.UrgentCount + r.ImportantCount) + ")";
                if (i == 4 && r.quests.Count > 0) label += " (" + r.quests.Count + ")";
                MUI.Fill(b, tab == i ? MUI.OnBg : MUI.CardBg2, 4f);
                if (tab == i) MUI.Frame(b, MUI.Accent, 1f, 4f);
                Text.Anchor = TextAnchor.MiddleCenter;
                GUI.color = tab == i ? MUI.Text1 : MUI.Muted;
                Widgets.Label(b, label);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(b)) { tab = i; scroll = Vector2.zero; }
            }
            y += 38f;

            Rect outR = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 18f, viewH);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float h = 0f;
            try
            {
                switch (tab)
                {
                    case 0: h = DrawNow(view, r); break;
                    case 1: h = DrawMake(view, r); break;
                    case 2: h = DrawResearch(view, r); break;
                    case 3: h = DrawGoal(view, r); break;
                    case 4: h = DrawQuests(view, r); break;
                    case 5: h = SuppliesTab.Draw(view, r, (yy, ww, tt, ss) => Title(yy, ww, tt, ss)); break;   // 0.2.0
                    case 6: h = CompassTabs.DrawJournal(view, r, (yy, ww, tt, ss) => Title(yy, ww, tt, ss)); break;   // 0.3.0
                    case 7: h = CompassTabs.DrawExpeditions(view, r, (yy, ww, tt, ss) => Title(yy, ww, tt, ss)); break;
                }
            }
            catch (Exception e) { Log.WarningOnce("[Colony Advisor] drawing failed: " + e, 0x5A41E2); }
            Widgets.EndScrollView();
            viewH = Mathf.Max(h + 10f, outR.height - 4f);
        }

        // score, quick numbers and the next step to the goal
        private float Header(Rect r0, Report r)
        {
            Rect card = new Rect(r0.x, r0.y, r0.width, 76f);
            MUI.Card(card);
            Rect sc = new Rect(card.x + 12f, card.y + 10f, 56f, 56f);
            Color c = r.score >= 70 ? MUI.Accent : r.score >= 40 ? MUI.Warn : new Color(0.85f, 0.35f, 0.3f);
            MUI.Fill(sc, MUI.CardBg2, 28f);
            MUI.Frame(sc, c, 2f, 28f);
            Text.Font = GameFont.Medium; Text.Anchor = TextAnchor.MiddleCenter; GUI.color = c;
            Widgets.Label(sc, r.score.ToString());
            Text.Font = GameFont.Small; Text.Anchor = TextAnchor.UpperLeft; GUI.color = Color.white;
            TooltipHandler.TipRegion(sc, T("CA_ScoreTip") + "\n\n" + string.Join("\n", r.scoreParts.Select(p => p.Item1 + ": " + p.Item2 + " %").ToArray()));

            float x = sc.xMax + 14f;
            Widgets.Label(new Rect(x, card.y + 8f, card.xMax - x - 10f, 24f),
                T("CA_Head", r.colonists, r.foodDays.ToString("0.#"), r.medicine, r.fighters, r.strength));
            Ending goal = Advisor.Goal(r);
            GUI.color = MUI.Accent;
            string line = goal?.next != null ? T("CA_HeadGoal", goal.title, goal.next.label) : T("CA_HeadStage", r.stages.Count > 0 ? r.stages[r.CurrentStage].title : "");
            Widgets.Label(new Rect(x, card.y + 32f, card.xMax - x - 10f, 40f), line);
            GUI.color = Color.white;
            return card.yMax;
        }

        // one card per tip
        private float TipCard(float y, float w, Tip t)
        {
            float textW = w - 30f - (t.actLabel != null ? 150f : 0f) - (t.target.IsValid ? 90f : 0f);
            float dh = t.detail.NullOrEmpty() ? 0f : Text.CalcHeight(t.detail, textW);
            float h = Mathf.Max(52f, 30f + dh + 6f);
            Rect card = new Rect(0f, y, w, h);
            MUI.Card(card, t.level == Level.Urgent);
            Color c = t.level == Level.Urgent ? new Color(0.9f, 0.35f, 0.3f) : t.level == Level.Important ? MUI.Warn : MUI.Muted;
            MUI.Fill(new Rect(card.x, card.y, 4f, card.height), c);
            GUI.color = c;
            Widgets.Label(new Rect(card.x + 14f, card.y + 6f, textW, 24f), t.title);
            GUI.color = MUI.Muted;
            if (dh > 0f) Widgets.Label(new Rect(card.x + 14f, card.y + 28f, textW, dh), t.detail);
            GUI.color = Color.white;
            float bx = card.xMax - 10f;
            if (t.actLabel != null)
            {
                bx -= 140f;
                if (Widgets.ButtonText(new Rect(bx, card.y + (h - 30f) / 2f, 140f, 30f), t.actLabel)) t.act?.Invoke();
                bx -= 8f;
            }
            if (t.target.IsValid)
            {
                bx -= 82f;
                if (Widgets.ButtonText(new Rect(bx, card.y + (h - 30f) / 2f, 82f, 30f), T("CA_Btn_Show"))) CameraJumper.TryJumpAndSelect(t.target);
            }
            return card.yMax + 6f;
        }

        private float Title(float y, float w, string text, string sub = null)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, w, 32f), text);
            Text.Font = GameFont.Small;
            y += 32f;
            if (!sub.NullOrEmpty())
            {
                GUI.color = MUI.Muted;
                float h = Text.CalcHeight(sub, w);
                Widgets.Label(new Rect(0f, y, w, h), sub);
                GUI.color = Color.white;
                y += h + 4f;
            }
            return y;
        }

        private float DrawNow(Rect view, Report r)
        {
            float y = Title(0f, view.width, T("CA_Now_Title"), T("CA_Now_Sub"));
            if (r.tips.Count == 0)
            {
                GUI.color = MUI.Accent;
                Widgets.Label(new Rect(0f, y, view.width, 30f), T("CA_Now_AllGood"));
                GUI.color = Color.white;
                return y + 30f;
            }
            foreach (Tip t in r.tips) y = TipCard(y, view.width, t);
            return y;
        }

        private float DrawMake(Rect view, Report r)
        {
            float y = Title(0f, view.width, T("CA_Make_Title"), T("CA_Make_Sub"));
            if (r.needs.Count == 0) { Widgets.Label(new Rect(0f, y, view.width, 30f), T("CA_Make_None")); return y + 30f; }
            foreach (Need n in r.needs)
            {
                Rect card = new Rect(0f, y, view.width, 64f);
                MUI.Card(card, n.urgent);
                Widgets.ThingIcon(new Rect(card.x + 10f, card.y + 12f, 40f, 40f), n.product);
                GUI.color = n.urgent ? MUI.Warn : MUI.Text1;
                Widgets.Label(new Rect(card.x + 60f, card.y + 6f, card.width - 260f, 24f), n.product.LabelCap + "  " + T("CA_Make_Count", n.have, n.want));
                GUI.color = MUI.Muted;
                string where = n.recipe == null ? T("CA_Make_NoRecipe")
                    : n.existing != null ? T("CA_Make_HasBill", n.bench.LabelCap, n.existing.repeatMode == BillRepeatModeDefOf.TargetCount ? n.existing.targetCount.ToString() : "∞")
                    : n.bench != null ? T("CA_Make_At", n.bench.LabelCap)
                    : T("CA_Make_Build", n.benchToBuild?.LabelCap.Resolve() ?? "?");
                Widgets.Label(new Rect(card.x + 60f, card.y + 28f, card.width - 260f, 34f), n.reason + " · " + where);
                GUI.color = Color.white;
                Rect b = new Rect(card.xMax - 190f, card.y + 17f, 180f, 30f);
                if (n.bench != null && n.recipe != null)
                {
                    if (Widgets.ButtonText(b, n.existing != null ? T("CA_Btn_RaiseBill", n.want) : T("CA_Btn_AddBill"))) Advisor.AddBill(n);
                }
                else if (n.benchToBuild != null && n.benchToBuild.BuildableByPlayer)
                {
                    if (Widgets.ButtonText(b, T("CA_Btn_Build")))
                    {
                        Designator_Build des = BuildCopyCommandUtility.FindAllowedDesignator(n.benchToBuild);
                        if (des != null) Find.DesignatorManager.Select(des);
                        else Advisor.OpenTab("Architect");
                    }
                }
                y = card.yMax + 6f;
            }
            return y;
        }

        private float DrawResearch(Rect view, Report r)
        {
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            float y = Title(0f, view.width, T("CA_Res_Title"), cur != null ? T("CA_Res_Current", cur.LabelCap.Resolve(), cur.ProgressPercent.ToStringPercent()) : T("CA_Res_None"));
            int i = 1;
            foreach (ResearchPick p in r.research)
            {
                float dh = Text.CalcHeight(p.reason, view.width - 220f);
                Rect card = new Rect(0f, y, view.width, Mathf.Max(54f, 30f + dh));
                MUI.Card(card, p.def == cur);
                GUI.color = p.def == cur ? MUI.Accent : MUI.Text1;
                Widgets.Label(new Rect(card.x + 12f, card.y + 6f, card.width - 220f, 24f), i++ + ". " + p.def.LabelCap + "  (" + p.def.CostApparent.ToString("0") + ")");
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(card.x + 12f, card.y + 28f, card.width - 220f, dh), p.reason);
                GUI.color = Color.white;
                if (p.def != cur && Widgets.ButtonText(new Rect(card.xMax - 190f, card.y + (card.height - 30f) / 2f, 180f, 30f), T("CA_Btn_Research")))
                    Advisor.StartResearch(p.def);
                y = card.yMax + 6f;
            }
            return y;
        }

        private float DrawGoal(Rect view, Report r)
        {
            float y = Title(0f, view.width, T("CA_Goal_Title"), T("CA_Goal_Sub"));
            // the stages
            for (int s = 0; s < r.stages.Count; s++)
            {
                Stage st = r.stages[s];
                bool cur = s == r.CurrentStage && !st.Done;
                GUI.color = st.Done ? MUI.Accent : cur ? MUI.Warn : MUI.Muted;
                Widgets.Label(new Rect(0f, y, view.width, 26f), (st.Done ? "✓ " : cur ? "▶ " : "○ ") + T("CA_Goal_Stage", s + 1, st.title, st.DoneCount, st.checks.Count));
                GUI.color = Color.white;
                y += 26f;
                if (!st.Done && (cur || s == r.CurrentStage + 1))
                {
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(20f, y, view.width - 20f, 24f), st.goal);
                    y += 24f;
                    GUI.color = Color.white;
                    foreach (Check c in st.checks) y = CheckLine(y, view.width, c);
                }
                y += 4f;
            }
            y += 10f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, view.width, 32f), T("CA_Goal_Endings"));
            Text.Font = GameFont.Small;
            y += 34f;
            Ending chosen = Advisor.Goal(r);
            foreach (Ending e in r.endings)
            {
                if (!e.available) continue;
                bool sel = e == chosen;
                float dh = Text.CalcHeight(e.desc, view.width - 240f);
                float h = 34f + dh + 8f + (sel ? e.steps.Count * 26f + 6f : 0f);
                Rect card = new Rect(0f, y, view.width, h);
                MUI.Card(card, sel);
                GUI.color = sel ? MUI.Accent : MUI.Text1;
                Widgets.Label(new Rect(card.x + 12f, card.y + 6f, card.width - 240f, 26f), e.title + "  " + e.Progress.ToStringPercent());
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(card.x + 12f, card.y + 32f, card.width - 240f, dh), e.desc);
                GUI.color = Color.white;
                Widgets.FillableBar(new Rect(card.xMax - 220f, card.y + 10f, 120f, 12f), e.Progress);
                if (!sel && Widgets.ButtonText(new Rect(card.xMax - 92f, card.y + 4f, 84f, 26f), T("CA_Btn_Choose")))
                {
                    AdvisorMod.S.goal = e.key;
                    AdvisorMod.S.Write();
                    Advisor.Invalidate();
                }
                if (sel)
                {
                    float sy = card.y + 34f + dh + 8f;
                    foreach (Check c in e.steps) sy = CheckLine(sy, card.width - 12f, c, 12f, c == e.next);
                }
                y = card.yMax + 6f;
            }
            if (!AdvisorMod.S.goal.NullOrEmpty() && Widgets.ButtonText(new Rect(0f, y, 240f, 28f), T("CA_Btn_AutoGoal")))
            {
                AdvisorMod.S.goal = "";
                AdvisorMod.S.Write();
            }
            return y + 34f;
        }

        private float CheckLine(float y, float w, Check c, float indent = 20f, bool next = false)
        {
            GUI.color = c.done ? MUI.Accent : next ? MUI.Warn : MUI.Text1;
            string mark = c.done ? "✓" : next ? "▶" : "○";
            Rect lr = new Rect(indent, y, w - indent, 24f);
            Widgets.Label(lr, mark + "  " + c.label);
            GUI.color = Color.white;
            if (!c.hint.NullOrEmpty()) TooltipHandler.TipRegion(lr, c.hint);
            return y + 26f;
        }

        private float DrawQuests(Rect view, Report r)
        {
            float y = Title(0f, view.width, T("CA_Q_Title"), T("CA_Q_Sub", r.strength));
            if (r.quests.Count == 0) { Widgets.Label(new Rect(0f, y, view.width, 30f), T("CA_Q_None")); return y + 30f; }
            QuestRating rec = CompassTabs.Recommended(r);   // 0.3.0
            foreach (QuestRating q in r.quests)
            {
                string extra = CompassTabs.QuestExtra(q);
                string why = q.why + (extra.NullOrEmpty() ? "" : "\n" + extra);
                float dh = Text.CalcHeight(why, view.width - 220f);
                Rect card = new Rect(0f, y, view.width, Mathf.Max(58f, 32f + dh));
                MUI.Card(card, q.verdict == 0);
                Color c = q.verdict == 0 ? MUI.Accent : q.verdict == 1 ? MUI.Warn : new Color(0.9f, 0.35f, 0.3f);
                MUI.Fill(new Rect(card.x, card.y, 4f, card.height), c);
                GUI.color = c;
                string stars = q.stars > 0 ? new string('★', q.stars) + " " : "";
                string left = q.hoursLeft >= 0f ? "  · " + T("CA_Q_Left", q.hoursLeft < 48f ? q.hoursLeft.ToString("0") + " h" : (q.hoursLeft / 24f).ToString("0.#") + " d") : "";
                if (q.hoursLeft >= 0f && q.hoursLeft < 24f) GUI.color = q.hoursLeft < 6f ? new Color(0.9f, 0.35f, 0.3f) : MUI.Warn;
                Widgets.Label(new Rect(card.x + 14f, card.y + 6f, card.width - 220f, 24f), (q == rec ? T("CA_Q_Rec") + " " : "") + stars + q.quest.name + "  – " + T("CA_Q_V" + q.verdict) + left);
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(card.x + 14f, card.y + 30f, card.width - 220f, dh), why);
                GUI.color = Color.white;
                Quest qq = q.quest;
                if (Widgets.ButtonText(new Rect(card.xMax - 190f, card.y + (card.height - 30f) / 2f, 180f, 30f), T("CA_Btn_OpenQuest"))) Advisor.OpenQuest(qq);
                y = card.yMax + 6f;
            }
            return y;
        }
    }
}
