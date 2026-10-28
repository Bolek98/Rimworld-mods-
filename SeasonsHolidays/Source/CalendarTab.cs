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
            Holiday next = null; int inDays = 99;
            foreach (Holiday h in Holidays.All)
            {
                if (!Holidays.On(h)) continue;
                int d = ((h.month * 5 + h.day - 1) - (month * 5 + day - 1) + 60) % 60;
                if (d < inDays) { inDays = d; next = h; }
            }
            string sub = next == null ? "" : inDays == 0 ? "SH_TabHolidayToday".Translate(next.Label).ToString() : "SH_TabNext".Translate(next.Label, inDays).ToString();
            if (SHMod.S.realDate) sub += "   " + "SH_TabRealDate".Translate();
            GUI.color = Gold;
            Widgets.Label(new Rect(r.x + 10f, r.y + 40f, r.width - 20f, 24f), sub);
            GUI.color = Color.white;

            // 4 × 3 month cards, starting with January
            float top = r.y + 72f;
            float gw = (r.width - 20f - 3 * 10f) / 4f, gh = (r.yMax - top - 10f - 2 * 10f) / 3f;
            var births = BirthdaysByMonth(map);
            for (int m = 0; m < 12; m++)
            {
                Rect c = new Rect(r.x + 10f + (m % 4) * (gw + 10f), top + (m / 4) * (gh + 10f), gw, gh);
                DrawMonth(c, map, m, m == month ? day : 0, lat, births.TryGetValue(m, out var b) ? b : null);
            }
        }

        private static Dictionary<int, List<(int day, Pawn p)>> BirthdaysByMonth(Map map)
        {
            var d = new Dictionary<int, List<(int, Pawn)>>();
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.ageTracker == null) continue;
                int doy = p.ageTracker.BirthDayOfYear;   // 0–59 from Aprimay 1
                int m = Months.MonthOf((Twelfth)(doy / 5));
                if (!d.TryGetValue(m, out var l)) d[m] = l = new List<(int, Pawn)>();
                l.Add((doy % 5 + 1, p));
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
            // five days
            float dx = c.x + 10f, dw = (c.width - 20f - 4 * 4f) / 5f;
            for (int d = 1; d <= 5; d++)
            {
                Rect dr = new Rect(dx + (d - 1) * (dw + 4f), c.y + 56f, dw, 22f);
                bool hol = Holidays.All.Any(h => h.month == m && h.day == d && Holidays.On(h));
                bool bday = births != null && births.Any(b => b.day == d);
                Widgets.DrawBoxSolid(dr, d == today ? Green : hol ? new Color(0.45f, 0.36f, 0.12f) : bday ? new Color(0.18f, 0.30f, 0.45f) : new Color(1f, 1f, 1f, 0.06f));
                GUI.color = d == today ? Color.black : Color.white;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dr, d.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            // holidays and birthdays of this month
            float y = c.y + 84f;
            foreach (Holiday h in Holidays.All.Where(h => h.month == m).OrderBy(h => h.day))
            {
                if (y > c.yMax - 16f) break;
                bool on = Holidays.On(h);
                GUI.color = on ? Gold : Muted;
                Rect lr = new Rect(c.x + 10f, y, c.width - 20f, 18f);
                Widgets.Label(lr, h.day + ".  " + h.Label + (on ? "" : " (" + "SH_TabOff".Translate() + ")"));
                TooltipHandler.TipRegion(lr, h.Desc);
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
}
