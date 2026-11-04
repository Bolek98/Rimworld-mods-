using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // the year at a glance: 12 month cards with temperature, growing, holidays and birthdays
    public class MainTabWindow_Calendar : MainTabWindow
    {
        private static readonly Color Bg = new Color32(22, 26, 30, 255);
        private static readonly Color Card = new Color32(32, 37, 43, 255);
        private static readonly Color Line = new Color32(58, 65, 72, 255);
        private static readonly Color Green = new Color32(124, 191, 94, 255);
        private static readonly Color Gold = new Color32(240, 190, 70, 255);
        private static readonly Color Blue = new Color32(110, 165, 225, 255);
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        private static readonly Dictionary<Holiday, (int m, int d)> dates = new Dictionary<Holiday, (int m, int d)>();

        public override Vector2 RequestedTabSize => new Vector2(Mathf.Min(1180f, UI.screenWidth - 20f), Mathf.Min(760f, UI.screenHeight - 120f));

        public override void DoWindowContents(Rect r)
        {
            Map map = Find.CurrentMap;
            Widgets.DrawBoxSolid(r, Bg);
            if (map == null) return;
            Months.Now(map, out int month, out int day);
            float lat = Find.WorldGrid.LongLatOf(map.Tile).y;
            long abs = Find.TickManager.TicksAbs;
            float lon = Find.WorldGrid.LongLatOf(map.Tile).x;

            // header: today and the next holiday
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, 34f),
                "SH_TabToday".Translate(day, Months.NameOf(month), Months.YearText(Months.YearShown(abs, lon)), GenLocalDate.Season(map).LabelCap()));
            Text.Font = GameFont.Small;
            Holiday next = null; int inDays = 9999;
            int yearDays = YearClock.Days, gy = GenDate.Year(abs, lon);
            int todayDoy = YearClock.ToDayOfYear(month, day);
            dates.Clear();
            foreach (Holiday h in Holidays.All)
            {
                Holidays.DateOf(h, gy, out int hm, out int hd);
                dates[h] = (hm, hd);
                if (!Holidays.On(h)) continue;
                int d = (YearClock.ToDayOfYear(hm, hd) - todayDoy + yearDays) % yearDays;
                if (d < inDays) { inDays = d; next = h; }
            }
            string sub = next == null ? "" : inDays == 0 ? "SH_TabHolidayToday".Translate(next.Label).ToString() : "SH_TabNext".Translate(next.Label, inDays).ToString();
            // 0.5.0: what comes after it (holidays and birthdays)
            string later = Upcoming(map, todayDoy, yearDays, lon, next);
            if (!later.NullOrEmpty()) sub += "   ·   " + "SH_TabUpcoming".Translate(later);
            if (SHMod.S.realDate) sub += "   " + "SH_TabRealDate".Translate();
            GUI.color = Gold;
            Widgets.Label(new Rect(r.x + 10f, r.y + 40f, r.width - 20f, 24f), sub);
            GUI.color = Color.white;

            // 4 × 3 month cards, starting with January
            float top = r.y + 72f;
            float gw = (r.width - 20f - 3 * 10f) / 4f, gh = (r.yMax - top - 10f - 2 * 10f) / 3f;
            var births = BirthdaysByMonth(map, lon);
            for (int m = 0; m < 12; m++)
            {
                Rect c = new Rect(r.x + 10f + (m % 4) * (gw + 10f), top + (m / 4) * (gh + 10f), gw, gh);
                DrawMonth(c, map, m, m == month ? day : 0, lat, births.TryGetValue(m, out var b) ? b : null);
            }
        }

        private static string Upcoming(Map map, int todayDoy, int yearDays, float lon, Holiday skip)
        {
            var ev = new List<(int d, string t)>();
            foreach (var kv in dates)
            {
                Holiday h = kv.Key;
                if (h == skip || h.small || !Holidays.On(h)) continue;
                int d = (YearClock.ToDayOfYear(kv.Value.m, kv.Value.d) - todayDoy + yearDays) % yearDays;
                if (d > 0) ev.Add((d, h.Label));
            }
            if (SHMod.S.birthdays)
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (p.ageTracker == null) continue;
                    int d = (YearClock.DayOfYear(p.ageTracker.BirthAbsTicks, lon) - todayDoy + yearDays) % yearDays;
                    if (d > 0) ev.Add((d, "SH_TabBirthday".Translate(p.LabelShort).ToString()));
                }
            return string.Join(", ", ev.OrderBy(e => e.d).Take(3).Select(e => "SH_TabInDays".Translate(e.t, e.d).ToString()));
        }

        private static Dictionary<int, List<(int day, Pawn p)>> BirthdaysByMonth(Map map, float lon)
        {
            var d = new Dictionary<int, List<(int, Pawn)>>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.ageTracker == null) continue;
                YearClock.FromDayOfYear(YearClock.DayOfYear(p.ageTracker.BirthAbsTicks, lon), out int m, out int day);
                if (!d.TryGetValue(m, out var l)) d[m] = l = new List<(int, Pawn)>();
                l.Add((day, p));
            }
            return d;
        }

        private void DrawMonth(Rect c, Map map, int m, int today, float lat, List<(int day, Pawn p)> births)
        {
            Widgets.DrawBoxSolid(c, Card);
            GUI.color = today > 0 ? Green : Line;
            Widgets.DrawBox(c, today > 0 ? 2 : 1);
            GUI.color = Color.white;
            float temp = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, Months.TwelfthOf(m));
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(c.x + 10f, c.y + 6f, c.width - 20f, 30f), Months.Name(m));
            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperRight;
            GUI.color = temp < 0f ? Blue : temp > 25f ? Gold : Muted;
            Widgets.Label(new Rect(c.x + 10f, c.y + 10f, c.width - 20f, 24f), temp.ToStringTemperature("F0"));
            Text.Anchor = TextAnchor.UpperLeft;
            // growing: plants grow from about 6 °C (slowly from 0 °C)
            string grow = temp >= 6f ? "SH_TabGrow".Translate().ToString() : temp > 0f ? "SH_TabGrowSlow".Translate().ToString() : "SH_TabNoGrow".Translate().ToString();
            GUI.color = temp >= 6f ? Green : temp > 0f ? Gold : Blue;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(c.x + 10f, c.y + 36f, c.width - 20f, 18f), grow);
            // the days of the month (0.2.0: 5 to 31, in rows)
            int len = YearClock.MonthLen(m);
            int perRow = len <= 10 ? len : len <= 15 ? 8 : 8;
            float gap = len > 10 ? 2f : 4f;
            float dx = c.x + 10f, dw = (c.width - 20f - (perRow - 1) * gap) / perRow, dh = len > 10 ? 15f : 22f;
            int rows = (len + perRow - 1) / perRow;
            if (len > 10) Text.Font = GameFont.Tiny;
            for (int d = 1; d <= len; d++)
            {
                int ri = (d - 1) / perRow, ci = (d - 1) % perRow;
                Rect dr = new Rect(dx + ci * (dw + gap), c.y + 56f + ri * (dh + gap), dw, dh);
                bool hol = Holidays.All.Any(h => Holidays.On(h) && dates.TryGetValue(h, out var hd) && hd.m == m && hd.d == d);
                bool bday = births != null && births.Any(b => b.day == d);
                Widgets.DrawBoxSolid(dr, d == today ? Green : hol ? new Color(0.45f, 0.36f, 0.12f) : bday ? new Color(0.18f, 0.30f, 0.45f) : new Color(1f, 1f, 1f, 0.06f));
                // 0.5.0: click a day for its details
                if (Mouse.IsOver(dr)) Widgets.DrawHighlight(dr);
                if (Widgets.ButtonInvisible(dr))
                {
                    var hs = Holidays.All.Where(h => dates.TryGetValue(h, out var hd1) && hd1.m == m && hd1.d == d).ToList();
                    var bs = births == null ? new List<Pawn>() : births.Where(b => b.day == d).Select(b => b.p).ToList();
                    Find.WindowStack.Add(new Dialog_SHDay(map, m, d, hs, bs, d == today));
                }
                GUI.color = d == today ? Color.black : Color.white;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dr, d.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            Text.Font = GameFont.Tiny;
            // holidays and birthdays of this month
            float y = c.y + 56f + rows * (dh + gap) + 6f;
            foreach (Holiday h in Holidays.All.Where(h => dates.TryGetValue(h, out var hd0) && hd0.m == m).OrderBy(h => dates[h].d))
            {
                if (y > c.yMax - 16f) break;
                bool on = Holidays.On(h);
                GUI.color = on ? Gold : Muted;
                Rect lr = new Rect(c.x + 10f, y, c.width - 20f, 18f);
                // 0.4.0: a "Celebrate" button for holidays with an activity
                bool can = on && Activities.On(h.id);
                Rect cel = new Rect(lr.xMax - 18f, lr.y, 18f, 18f);
                Widgets.Label(can ? new Rect(lr.x, lr.y, lr.width - 20f, lr.height) : lr, dates[h].d + ".  " + h.Label + (on ? "" : " (" + "SH_TabOff".Translate() + ")"));
                TooltipHandler.TipRegion(lr, h.Desc + (can ? "\n\n" + "SH_ActTabTip".Translate(Activities.Label(h.id)) : ""));
                if (can)
                {
                    GUI.color = Mouse.IsOver(cel) ? Color.white : Green;
                    Widgets.Label(cel, "▶");
                    GUI.color = on ? Gold : Muted;
                    if (Widgets.ButtonInvisible(cel))
                    {
                        Holiday hh = h;
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("SH_ActConfirm".Translate(hh.Label, Activities.Label(hh.id)),
                            () => Activities.Run(map, hh.id, true)));
                    }
                }
                y += 17f;
            }
            if (births != null && SHMod.S.birthdays)
                foreach (var b in births.OrderBy(x => x.day))
                {
                    if (y > c.yMax - 16f) break;
                    GUI.color = Blue;
                    Widgets.Label(new Rect(c.x + 10f, y, c.width - 20f, 18f), b.day + ".  " + "SH_TabBirthday".Translate(b.p.LabelShort));
                    y += 17f;
                }
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }
    }

    // 0.5.0: one day of the calendar – its holidays (with ▶ celebrate) and birthdays (throw a party)
    public class Dialog_SHDay : Window
    {
        private readonly Map map;
        private readonly int month, day;
        private readonly List<Holiday> hols;
        private readonly List<Pawn> births;
        private readonly bool today;
        private Vector2 scroll;

        public Dialog_SHDay(Map map, int month, int day, List<Holiday> hols, List<Pawn> births, bool today)
        {
            this.map = map; this.month = month; this.day = day; this.hols = hols; this.births = births; this.today = today;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override Vector2 InitialSize => new Vector2(520f, 420f);

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width - 30f, 34f), Months.DayLabel(month, day).CapitalizeFirst() + (today ? "  (" + "SH_DayToday".Translate() + ")" : ""));
            Text.Font = GameFont.Small;
            float temp = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, Months.TwelfthOf(month));
            GUI.color = new Color(0.63f, 0.65f, 0.68f);
            Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 24f), "SH_DayTemp".Translate(temp.ToStringTemperature("F0")));
            GUI.color = Color.white;
            Rect outR = new Rect(r.x, r.y + 64f, r.width, r.height - 64f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, 40f + hols.Count * 96f + births.Count * 40f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0f;
            if (hols.Count == 0 && births.Count == 0) { Widgets.Label(new Rect(0f, 0f, view.width, 26f), "SH_DayNothing".Translate()); }
            foreach (Holiday h in hols)
            {
                bool on = Holidays.On(h);
                GUI.color = on ? new Color32(240, 190, 70, 255) : new Color(0.63f, 0.65f, 0.68f);
                Widgets.Label(new Rect(0f, y, view.width - 130f, 24f), h.Label + (on ? "" : " (" + "SH_TabOff".Translate() + ")"));
                GUI.color = Color.white;
                if (on && Activities.On(h.id))
                {
                    Holiday hh = h;
                    if (Widgets.ButtonText(new Rect(view.width - 126f, y, 126f, 26f), "SH_DayCelebrate".Translate()))
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("SH_ActConfirm".Translate(hh.Label, Activities.Label(hh.id)), () => Activities.Run(map, hh.id, true)));
                }
                Text.Font = GameFont.Tiny;
                string desc = h.Desc + (Activities.Has(h.id) ? "\n" + "SH_DayActivity".Translate(Activities.Label(h.id)) : "");
                float dh = Text.CalcHeight(desc, view.width);
                Widgets.Label(new Rect(0f, y + 26f, view.width, dh), desc);
                Text.Font = GameFont.Small;
                y += 30f + dh + 10f;
            }
            foreach (Pawn p in births)
            {
                GUI.color = new Color32(110, 165, 225, 255);
                Widgets.Label(new Rect(0f, y + 3f, view.width - 160f, 24f), "SH_TabBirthday".Translate(p.LabelShort) + " (" + (p.ageTracker.AgeBiologicalYears + (today ? 0 : 1)) + ")");
                GUI.color = Color.white;
                bool can = p.Spawned && p.Map == map && !p.Downed && !p.InMentalState && GatheringDefOf.Party?.Worker != null
                    && GatheringDefOf.Party.Worker.CanExecute(map, p) && !Activities.GatheringRunning(map);
                Rect b = new Rect(view.width - 156f, y, 156f, 28f);
                if (Widgets.ButtonText(b, "SH_DayParty".Translate(), true, true, can))
                {
                    try
                    {
                        if (GatheringDefOf.Party.Worker.TryExecute(map, p)) Close();
                    }
                    catch (System.Exception e) { Log.Warning("[Seasons & Holidays] birthday party: " + e.Message); }
                }
                if (!can) TooltipHandler.TipRegion(b, "SH_DayPartyNo".Translate());
                y += 36f;
            }
            Widgets.EndScrollView();
        }
    }
}
