using System;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // runs the calendar on every player home map: holidays, birthdays, seasonal moods, tips
    public class SHMapComponent : MapComponent
    {
        public Fireworks fireworks = new Fireworks();

        public SHMapComponent(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            try { fireworks.Tick(map); } catch { }
            if (Find.TickManager.TicksGame % 250 != 17 || !map.IsPlayerHome || SHMod.S == null) return;
            try { Run(); }
            catch (Exception e) { Log.WarningOnce("[Seasons & Holidays] " + e, 0x5E4501); }
        }

        private SHWorldComponent W => Find.World.GetComponent<SHWorldComponent>();

        private bool Once(string key) => W != null && W.Mark(key + "|" + map.uniqueID);

        private void Run()
        {
            if (Holidays.Colonists(map).Count == 0) return;
            long abs = Find.TickManager.TicksAbs;
            int absDay = (int)(abs / 60000L);
            int hour = GenLocalDate.HourOfDay(map);
            Months.Now(map, out int month, out int day);
            float lat = Find.WorldGrid.LongLatOf(map.Tile).y;
            int climate = Months.ClimateMonth(month, lat);

            // ---- holidays ----
            if (SHMod.S.holidays)
            {
                DateTime now = DateTime.Now;
                foreach (Holiday h in Holidays.All)
                {
                    if (!Holidays.On(h)) continue;
                    bool today; string when;
                    if (SHMod.S.realDate) { today = Holidays.IsRealToday(h, now); when = "R" + now.ToString("yyyyMMdd"); }
                    else
                    {
                        Holidays.DateOf(h, GenDate.Year(abs, Find.WorldGrid.LongLatOf(map.Tile).x), out int hm, out int hd);
                        today = hm == month && hd == day; when = absDay.ToString();
                    }
                    if (!today) continue;
                    if (hour >= 8 && Once("M|" + h.id + "|" + when)) Morning(h, when);
                    if (h.partyHour >= 0 && hour >= h.partyHour && hour < h.partyHour + 3 && SHMod.S.parties && Once("P|" + h.id + "|" + when)) Party(null);
                    if (h.evening != null && hour >= h.eveningHour && Once("E|" + h.id + "|" + when)) h.evening(map);
                }
            }

            // ---- birthdays ----
            if (SHMod.S.birthdays && hour >= 9)
            {
                float lon0 = Find.WorldGrid.LongLatOf(map.Tile).x;
                int doy = YearClock.DayOfYear(abs, lon0);
                foreach (Pawn p in Holidays.Colonists(map))
                {
                    if (p.ageTracker == null || YearClock.DayOfYear(p.ageTracker.BirthAbsTicks, lon0) != doy) continue;
                    if (!Once("B|" + p.thingIDNumber + "|" + absDay)) continue;
                    Holidays.GiveThought(p, "SH_Birthday", 0);
                    Messages.Message("SH_BirthdayMsg".Translate(p.LabelShort, p.ageTracker.AgeBiologicalYears), p, MessageTypeDefOf.PositiveEvent);
                    if (SHMod.S.parties && hour < 20 && p.Awake() && !p.Downed && Rand.Chance(0.7f)) Party(p);
                }
            }

            // ---- seasonal moods (the first day of the month, by the climate: south is turned around) ----
            if (SHMod.S.seasonalMoods && day == 1 && hour >= 9)
            {
                string thought = climate == 3 ? "SH_SpringMood" : climate == 6 ? "SH_SummerEvenings" : (climate == 0 && SHMod.S.winterBlues ? "SH_WinterBlues" : null);
                if (thought != null && Once("S|" + thought + "|" + absDay))
                    foreach (Pawn p in Holidays.Colonists(map)) Holidays.GiveThought(p, thought, 0);
            }

            // ---- the first snow of the year ----
            if (SHMod.S.seasonalMoods && map.weatherManager.curWeather != null && map.weatherManager.curWeather.snowRate > 0.1f)
            {
                int y = GenDate.Year(abs, Find.WorldGrid.LongLatOf(map.Tile).x);
                if (Once("Snow|" + y))
                {
                    foreach (Pawn p in Holidays.Colonists(map)) Holidays.GiveThought(p, "SH_FirstSnow", 0);
                    Messages.Message("SH_FirstSnowMsg".Translate(), MessageTypeDefOf.PositiveEvent);
                }
            }

            // ---- tips: winter is coming / the growing season starts ----
            if (SHMod.S.seasonTips && day == 3 && hour >= 10)
            {
                if (climate == 10 && Avg(month + 1) < 5f && Avg(month + 2) < 5f && Once("TW|" + absDay))
                    Find.LetterStack.ReceiveLetter("SH_TipWinterTitle".Translate(), "SH_TipWinter".Translate(Months.Name((month + 1) % 12), Avg(month + 2).ToStringTemperature("F0")), LetterDefOf.NeutralEvent);
                if (climate == 2 && Avg(month + 1) > 6f && Once("TS|" + absDay))
                    Find.LetterStack.ReceiveLetter("SH_TipSpringTitle".Translate(), "SH_TipSpring".Translate(Months.Name((month + 1) % 12)), LetterDefOf.NeutralEvent);
            }
        }

        private float Avg(int month) => GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, Months.TwelfthOf(((month % 12) + 12) % 12));

        private void Morning(Holiday h, string when)
        {
            var w = W;
            if (w != null && w.Mark("C|" + h.id + "|" + when))
                w.celebrated[h.id] = w.Count(h.id) + 1;
            int stage = Tradition.Stage(h.id);
            if (h.thought != null)
                foreach (Pawn p in Holidays.Colonists(map))
                    if (h.who == null || h.who(p)) Holidays.GiveThought(p, h.thought, stage);
            try { h.morning?.Invoke(map); } catch (Exception e) { Log.Warning("[Seasons & Holidays] " + h.id + ": " + e.Message); }
            string text = ("SH_HL_" + h.id).Translate();
            int n = Tradition.Count(h.id);
            if (SHMod.S.traditions && n >= 2) text += "\n\n" + "SH_TraditionNote".Translate(n);
            if (SHMod.S.letters) Find.LetterStack.ReceiveLetter(h.Label, text, LetterDefOf.PositiveEvent);
            else Messages.Message(h.Label + ": " + text, MessageTypeDefOf.PositiveEvent);
        }

        private void Party(Pawn organizer)
        {
            try
            {
                GatheringDef party = GatheringDefOf.Party;
                if (party?.Worker == null) return;
                if (organizer != null && party.Worker.CanExecute(map, organizer)) party.Worker.TryExecute(map, organizer);
                else if (party.Worker.CanExecute(map)) party.Worker.TryExecute(map);
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] party: " + e.Message); }
        }
    }
}
