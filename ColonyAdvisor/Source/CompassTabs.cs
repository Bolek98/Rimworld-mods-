using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyAdvisor
{
    // 0.3.0: the "Journal" and "Expeditions" tabs of the advisor window.
    public static class CompassTabs
    {
        private static string T(string key, params NamedArgument[] a) => key.Translate(a).Resolve();
        private static readonly Color Red = new Color(0.9f, 0.35f, 0.3f);

        // ---------------- journal ----------------
        public static float DrawJournal(Rect view, Report r, Func<float, float, string, string, float> title)
        {
            float y = title(0f, view.width, T("CA_J_Title"), T("CA_J_Sub"));
            List<StoryLine> lines = Compass.Journal(r);
            if (lines.Count == 0) { Widgets.Label(new Rect(0f, y, view.width, 30f), T("CA_J_None")); return y + 30f; }
            bool shownInactiveHead = false;
            foreach (StoryLine l in lines)
            {
                if (!l.active && !AdvisorMod.S.journalShowInactive) continue;
                if (!l.active && !shownInactiveHead)
                {
                    shownInactiveHead = true;
                    y += 6f;
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(0f, y, view.width, 24f), T("CA_J_Later"));
                    GUI.color = Color.white;
                    y += 26f;
                }
                y = Line(y, view.width, l);
            }
            return y;
        }

        private static float Line(float y, float w, StoryLine l)
        {
            float textW = w - 230f;
            string now = T("CA_J_NowLabel") + " " + l.now;
            float nh = Text.CalcHeight(now, textW);
            float needH = l.needs.Sum(n => Text.CalcHeight("• " + n, textW));
            bool open = l.active;
            float stepsH = open ? l.steps.Count * 24f + 4f : 0f;
            float h = 34f + nh + needH + stepsH + 10f;
            Rect card = new Rect(0f, y, w, h);
            MUI.Card(card, l.active);
            MUI.Fill(new Rect(card.x, card.y, 4f, card.height), l.active ? MUI.Accent : MUI.Muted);
            Text.Font = GameFont.Medium;
            GUI.color = l.active ? MUI.Text1 : MUI.Muted;
            Widgets.Label(new Rect(card.x + 14f, card.y + 4f, textW, 30f), l.title + (l.active ? "  " + T("CA_J_Active") : ""));
            Text.Font = GameFont.Small;
            // progress bar
            Rect bar = new Rect(card.xMax - 210f, card.y + 12f, 140f, 12f);
            Widgets.FillableBar(bar, l.progress);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(bar.xMax + 6f, card.y + 6f, 60f, 24f), l.progress.ToStringPercent());
            float yy = card.y + 34f;
            GUI.color = MUI.Warn;
            Widgets.Label(new Rect(card.x + 14f, yy, textW, nh), now);
            yy += nh;
            GUI.color = MUI.Muted;
            foreach (string n in l.needs)
            {
                float hh = Text.CalcHeight("• " + n, textW);
                Widgets.Label(new Rect(card.x + 24f, yy, textW - 10f, hh), "• " + n);
                yy += hh;
            }
            GUI.color = Color.white;
            if (open)
            {
                yy += 4f;
                Check next = l.steps.FirstOrDefault(s => !s.done);
                foreach (Check c in l.steps)
                {
                    GUI.color = c.done ? MUI.Accent : c == next ? MUI.Warn : MUI.Text1;
                    Rect lr = new Rect(card.x + 24f, yy, textW, 24f);
                    Widgets.Label(lr, (c.done ? "✓" : c == next ? "▶" : "○") + "  " + c.label);
                    if (!c.hint.NullOrEmpty()) TooltipHandler.TipRegion(lr, c.hint);
                    yy += 24f;
                }
                GUI.color = Color.white;
            }
            // buttons on the right
            float by = card.y + 34f;
            if (l.act != null && l.actLabel != null)
            {
                if (Widgets.ButtonText(new Rect(card.xMax - 200f, by, 190f, 28f), l.actLabel)) l.act();
                by += 32f;
            }
            if (l.target.IsValid && Widgets.ButtonText(new Rect(card.xMax - 200f, by, 190f, 28f), T("CA_Btn_Show")))
                CameraJumper.TryJumpAndSelect(l.target);
            return card.yMax + 6f;
        }

        // ---------------- quest details (used by the quests tab) ----------------
        public static string QuestExtra(QuestRating q)
        {
            QuestTravel t = Compass.Travel(q.quest);
            var parts = new List<string>();
            if (t.days >= 0f) parts.Add(T("CA_Q_Dist", t.tiles, Compass.DaysText(t.days), Compass.DaysText(t.days * 2f)));
            else if (!t.target.IsValid) parts.Add(T("CA_Q_Here"));
            if (q.stars > 0) parts.Add(T("CA_Q_Threat", q.stars));
            return string.Join(" · ", parts.ToArray());
        }

        public static QuestRating Recommended(Report r)
        {
            return r.quests.Where(q => q.verdict == 0).OrderByDescending(q => q.value / Mathf.Max(1f, Compass.Travel(q.quest).days + 1f)).FirstOrDefault();
        }

        // ---------------- expeditions ----------------
        private static int pick = -1;
        private static List<Pawn> team;
        private static List<(Pawn, string)> stay;
        private static int teamTick = -99999;

        public static void Reset() { teamTick = -99999; }

        public static float DrawExpeditions(Rect view, Report r, Func<float, float, string, string, float> title)
        {
            Map map = r.map;
            float y = title(0f, view.width, T("CA_X_Title"), T("CA_X_Sub"));
            CompassGame g = CompassGame.Get();
            if (g != null)
            {
                int restless = g.Restless();
                string line = T("CA_X_Home", g.AverageDaysHome().ToString("0"), restless);
                GUI.color = restless > 0 ? MUI.Warn : MUI.Muted;
                float hh = Text.CalcHeight(line, view.width);
                Widgets.Label(new Rect(0f, y, view.width, hh), line);
                GUI.color = Color.white;
                y += hh + 6f;
            }

            // destinations
            y = Sub(y, view.width, T("CA_X_Where"));
            List<Destination> dests = Compass.Destinations(r);
            if (dests.Count == 0) { GUI.color = MUI.Muted; Widgets.Label(new Rect(0f, y, view.width, 24f), T("CA_X_NoDest")); GUI.color = Color.white; y += 26f; }
            if (pick >= dests.Count) pick = -1;
            if (pick < 0 && dests.Count > 0) pick = 0;
            for (int i = 0; i < dests.Count && i < 12; i++)
            {
                Destination d = dests[i];
                Rect row = new Rect(0f, y, view.width, 28f);
                if (i == pick) MUI.Fill(row, MUI.OnBg, 3f); else if (Mouse.IsOver(row)) MUI.Fill(row, MUI.CardBg2, 3f);
                GUI.color = i == pick ? MUI.Accent : MUI.Text1;
                string kind = T("CA_X_K_" + d.kind);
                Widgets.Label(new Rect(row.x + 8f, row.y + 4f, row.width - 330f, 24f), kind + ": " + d.label);
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(row.xMax - 320f, row.y + 4f, 220f, 24f), T("CA_X_Eta", d.tiles, Compass.DaysText(d.days)));
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y + 1f, 84f, 26f), T("CA_Btn_Show"))) CameraJumper.TryJumpAndSelect(d.target);
                if (Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 96f, row.height))) pick = i;
                y += 30f;
            }
            Destination dest = pick >= 0 && pick < dests.Count ? dests[pick] : null;
            y += 6f;

            // team
            if (team == null || Find.TickManager.TicksGame - teamTick > 2500)
            {
                Compass.Team(map, out team, out stay);
                teamTick = Find.TickManager.TicksGame;
            }
            team.RemoveAll(p => p.DestroyedOrNull() || p.Dead);
            y = Sub(y, view.width, T("CA_X_Team", team.Count));
            foreach (Pawn p in team)
            {
                Rect row = new Rect(0f, y, view.width, 26f);
                Widgets.ThingIcon(new Rect(row.x + 4f, row.y + 1f, 24f, 24f), p);
                string weapon = p.equipment?.Primary != null ? p.equipment.Primary.LabelShortCap : T("CA_X_NoWeapon");
                Widgets.Label(new Rect(row.x + 34f, row.y + 3f, 220f, 24f), p.LabelShortCap);
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(row.x + 260f, row.y + 3f, row.width - 360f, 24f), weapon + " · " + T("CA_X_Skills",
                    p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0, p.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0, p.skills?.GetSkill(SkillDefOf.Social)?.Level ?? 0));
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 90f, row.y, 84f, 24f), T("CA_X_Drop"))) { team.Remove(p); break; }
                y += 28f;
            }
            if (stay.Count > 0)
            {
                GUI.color = MUI.Muted;
                string s = T("CA_X_Stay", string.Join(", ", stay.Where(x => x.Item2 != T("CA_X_KeepUnfit")).Select(x => x.Item1.LabelShortCap + " (" + x.Item2 + ")").ToArray()));
                int unfit = stay.Count(x => x.Item2 == T("CA_X_KeepUnfit"));
                if (unfit > 0) s += " " + T("CA_X_Unfit", unfit);
                float hh = Text.CalcHeight(s, view.width);
                Widgets.Label(new Rect(0f, y, view.width, hh), s);
                GUI.color = Color.white;
                y += hh + 4f;
            }
            if (Widgets.ButtonText(new Rect(0f, y, 180f, 26f), T("CA_X_Rebuild"))) { Reset(); }
            y += 34f;

            // supplies and timing
            float oneWay = dest?.days ?? -1f;
            float stayDays = dest != null && dest.kind == "settlement" ? 1f : 2f;
            float total = oneWay >= 0f ? oneWay * 2f + stayDays : 6f;
            Compass.Supplies(map, team, total, out int meals, out int med, out int haveMeals, out int haveMed);
            y = Sub(y, view.width, T("CA_X_Plan"));
            var info = new List<(string, bool)>();
            if (dest != null && oneWay >= 0f) info.Add((T("CA_X_Times", Compass.DaysText(oneWay), Compass.DaysText(total), (GenDate.DaysPassed + total).ToString("0")), true));
            else info.Add((T("CA_X_NoTimes"), true));
            info.Add((T("CA_X_Food", meals, haveMeals), haveMeals >= meals));
            info.Add((T("CA_X_Med", med, haveMed), haveMed >= med));
            int kitMissing = Compass.KitMissing(team, out bool kit);
            if (kit) info.Add((kitMissing == 0 ? T("CA_X_KitOk") : T("CA_X_KitMissing", kitMissing), kitMissing == 0));
            if (dest?.quest != null && dest.quest.TicksUntilExpiry > 0 && oneWay >= 0f && dest.quest.State == QuestState.NotYetAccepted)
                info.Add((T("CA_X_Expiry", Compass.DaysText(dest.quest.TicksUntilExpiry / 60000f)), true));
            foreach (var (text, ok) in info)
            {
                GUI.color = ok ? MUI.Text1 : MUI.Warn;
                float hh = Text.CalcHeight("• " + text, view.width - 10f);
                Widgets.Label(new Rect(10f, y, view.width - 10f, hh), "• " + text);
                y += hh;
            }
            GUI.color = Color.white;
            y += 8f;
            if (team.Count > 0 && Widgets.ButtonText(new Rect(0f, y, 260f, 32f), T("CA_X_Form"))) Compass.FormCaravan(map, team);
            if (dest?.quest != null && Widgets.ButtonText(new Rect(270f, y, 180f, 32f), T("CA_Btn_OpenQuest"))) Advisor.OpenQuest(dest.quest);
            y += 40f;

            // gravship checklist
            if (ModsConfig.OdysseyActive)
            {
                y = Sub(y, view.width, T("CA_G_Title"));
                foreach (Check c in Compass.GravChecklist(map))
                {
                    GUI.color = c.done ? MUI.Accent : MUI.Warn;
                    Rect lr = new Rect(10f, y, view.width - 10f, 24f);
                    Widgets.Label(lr, (c.done ? "✓  " : "○  ") + c.label);
                    if (!c.hint.NullOrEmpty()) TooltipHandler.TipRegion(lr, c.hint);
                    y += 24f;
                }
                GUI.color = Color.white;
            }
            return y + 10f;
        }

        private static float Sub(float y, float w, string text)
        {
            GUI.color = MUI.Accent;
            Widgets.Label(new Rect(0f, y, w, 24f), text);
            GUI.color = Color.white;
            MUI.Fill(new Rect(0f, y + 23f, w, 1f), MUI.Border);
            return y + 28f;
        }
    }
}
