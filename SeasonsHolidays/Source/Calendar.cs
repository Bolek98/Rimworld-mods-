using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // Seasons & Holidays 0.1.0. The RimWorld year (60 days) as 12 months of 5 days: Aprimay is
    // March–May, Jugust June–August, Septober September–November, Decembary December–February
    // (the game's own "twelfths"). Real holidays (Czech and international) with moods, parties,
    // gifts and events, weather by month, new weathers, birthdays, seasonal moods, traditions
    // and a calendar tab. The months are calendar months: in the south Christmas is in summer.
    // ---------------------------------------------------------------------------
    public class SHSettings : ModSettings
    {
        public bool recommended = true;
        public bool calendarDate = true;      // date readout with months
        public int yearMode = 0;              // 0 game years (5500), 1 from year 0, 2 from year 1
        public bool holidays = true;
        public bool parties = true;
        public bool gifts = true;
        public bool events = true;            // fireworks, Halloween fog, white Christmas
        public bool letters = true;           // a letter on the holiday morning (otherwise a message)
        public bool birthdays = true;
        public bool traditions = true;
        public bool seasonalMoods = true;
        public bool winterBlues = true;
        public bool monthWeather = true;
        public float weatherStrength = 1f;
        public bool newWeathers = true;
        public bool seasonTips = true;        // "winter is coming" / "the growing season starts"
        public bool realDate = false;         // holidays by the real date of your computer
        public List<string> off = new List<string>();   // holidays switched off
        public int yearDays = 60;             // 0.2.0: length of the year – 60 (game), 120, 180, 365
        public bool ageWithYear = true;       // colonists age with the calendar year
        public bool plantColors = true;       // 0.3.0: trees and grass change colour with the seasons (looks only)
        public bool packAsia = true;          // 0.3.0: world holiday packs
        public bool packAmericas = true;
        public bool packEurope = true;
        public bool packDays = true;          // small days, a message only

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref calendarDate, "calendarDate", true);
            Scribe_Values.Look(ref yearMode, "yearMode", 0);
            Scribe_Values.Look(ref holidays, "holidays", true);
            Scribe_Values.Look(ref parties, "parties", true);
            Scribe_Values.Look(ref gifts, "gifts", true);
            Scribe_Values.Look(ref events, "events", true);
            Scribe_Values.Look(ref letters, "letters", true);
            Scribe_Values.Look(ref birthdays, "birthdays", true);
            Scribe_Values.Look(ref traditions, "traditions", true);
            Scribe_Values.Look(ref seasonalMoods, "seasonalMoods", true);
            Scribe_Values.Look(ref winterBlues, "winterBlues", true);
            Scribe_Values.Look(ref monthWeather, "monthWeather", true);
            Scribe_Values.Look(ref weatherStrength, "weatherStrength", 1f);
            Scribe_Values.Look(ref newWeathers, "newWeathers", true);
            Scribe_Values.Look(ref seasonTips, "seasonTips", true);
            Scribe_Values.Look(ref realDate, "realDate", false);
            Scribe_Collections.Look(ref off, "off", LookMode.Value);
            Scribe_Values.Look(ref yearDays, "yearDays", 60);
            Scribe_Values.Look(ref ageWithYear, "ageWithYear", true);
            Scribe_Values.Look(ref plantColors, "plantColors", true);
            Scribe_Values.Look(ref packAsia, "packAsia", true);
            Scribe_Values.Look(ref packAmericas, "packAmericas", true);
            Scribe_Values.Look(ref packEurope, "packEurope", true);
            Scribe_Values.Look(ref packDays, "packDays", true);
            if (off == null) off = new List<string>();
        }
    }

    public class SHMod : Mod
    {
        public const string Version = "0.3.0";
        public static SHSettings S;
        private static List<string> recBullets;
        private static Vector2 scroll;

        public SHMod(ModContentPack content) : base(content)
        {
            S = GetSettings<SHSettings>();
            MUI.Init(new SHSettings(), null, "recommended", "yearMode", "off", "realDate", "yearDays", "ageWithYear");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("misak.seasonsholidays");
            try { MUI.PatchWindow(h, typeof(SHMod)); } catch { }
            DatePatches.Patch(h);
            WeatherMonths.Patch(h);
            YearClock.Patch(h);   // 0.2.0
            PlantColors.Patch(h); // 0.3.0
            Log.Message("[Seasons & Holidays] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "SH_ModName".Translate();

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "SH_Rec1".Translate(), "SH_Rec2".Translate(), "SH_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            // the year numbering is always yours
            Rect yr = new Rect(r.x, y, r.width, 30f);
            Widgets.Label(new Rect(yr.x, yr.y + 4f, 220f, 26f), "SH_YearMode".Translate());
            string[] modes = { "SH_Year0".Translate(), "SH_Year1".Translate(), "SH_Year2".Translate() };
            float bx = yr.x + 220f;
            for (int i = 0; i < 3; i++)
            {
                float w = Text.CalcSize(modes[i]).x + 24f;
                Rect b = new Rect(bx, yr.y, w, 28f);
                if (S.yearMode == i) Widgets.DrawHighlightSelected(b);
                if (Widgets.ButtonText(b, modes[i], true, true, S.yearMode != i)) { S.yearMode = i; DatePatches.Reset(); }
                bx += w + 6f;
            }
            TooltipHandler.TipRegion(new Rect(yr.x, yr.y, 220f, 30f), "SH_YearMode_Desc".Translate());
            y += 36f;
            // 0.2.0: the length of the year – always yours too
            Rect yl = new Rect(r.x, y, r.width, 30f);
            Widgets.Label(new Rect(yl.x, yl.y + 4f, 220f, 26f), "SH_YearDays".Translate());
            TooltipHandler.TipRegion(new Rect(yl.x, yl.y, 220f, 30f), "SH_YearDays_Desc".Translate());
            bx = yl.x + 220f;
            foreach (int len in YearClock.Lengths)
            {
                string lbl = "SH_YearDaysN".Translate(len);
                float w = Text.CalcSize(lbl).x + 24f;
                Rect b = new Rect(bx, yl.y, w, 28f);
                if (S.yearDays == len) Widgets.DrawHighlightSelected(b);
                if (Widgets.ButtonText(b, lbl, true, true, S.yearDays != len)) { S.yearDays = len; YearClock.Invalidate(); DatePatches.Reset(); }
                bx += w + 6f;
            }
            y += 34f;
            if (S.yearDays != 60)
            {
                Rect ar = new Rect(r.x, y, Mathf.Min(r.width, 640f), 28f);
                bool ag = S.ageWithYear;
                Widgets.CheckboxLabeled(ar, "SH_AgeWithYear".Translate(), ref ag);
                TooltipHandler.TipRegion(ar, "SH_AgeWithYear_Desc".Translate());
                S.ageWithYear = ag;
                y += 30f;
            }
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, rest.width - 16f, 1200f + Holidays.All.Count * 26f);
            MUI.BeginLock(S.recommended);
            Widgets.BeginScrollView(rest, ref scroll, view);
            try
            {
                var l = new Listing_Standard { maxOneColumn = true };
                l.Begin(view);
                MUI.Section(l, "SH_SecCalendar".Translate());
                Check(l, "SH_CalendarDate", ref S.calendarDate, "calendarDate");
                Check(l, "SH_SeasonTips", ref S.seasonTips, "seasonTips");
                MUI.Section(l, "SH_SecHolidays".Translate());
                Check(l, "SH_Holidays", ref S.holidays, "holidays");
                Check(l, "SH_Parties", ref S.parties, "parties");
                Check(l, "SH_Gifts", ref S.gifts, "gifts");
                Check(l, "SH_Events", ref S.events, "events");
                Check(l, "SH_Letters", ref S.letters, "letters");
                Check(l, "SH_Birthdays", ref S.birthdays, "birthdays");
                Check(l, "SH_Traditions", ref S.traditions, "traditions");
                Check(l, "SH_PackAsia", ref S.packAsia, "packAsia");
                Check(l, "SH_PackAmericas", ref S.packAmericas, "packAmericas");
                Check(l, "SH_PackEurope", ref S.packEurope, "packEurope");
                Check(l, "SH_PackDays", ref S.packDays, "packDays");
                bool rd = S.realDate;
                l.CheckboxLabeled("SH_RealDate".Translate(), ref rd, "SH_RealDate_Desc".Translate());
                S.realDate = rd;
                MUI.Section(l, "SH_SecWeather".Translate());
                Check(l, "SH_MonthWeather", ref S.monthWeather, "monthWeather");
                S.weatherStrength = MUI.Slider(l, "SH_WeatherStrength".Translate(Mathf.RoundToInt(S.weatherStrength * 100f)), S.weatherStrength, 0f, 2f, "SH_WeatherStrength_Desc".Translate(), S, "weatherStrength");
                Check(l, "SH_NewWeathers", ref S.newWeathers, "newWeathers");
                bool pc = S.plantColors;
                Check(l, "SH_PlantColors", ref S.plantColors, "plantColors");
                if (pc != S.plantColors) PlantColors.RefreshAll();
                MUI.Section(l, "SH_SecMoods".Translate());
                Check(l, "SH_SeasonalMoods", ref S.seasonalMoods, "seasonalMoods");
                Check(l, "SH_WinterBlues", ref S.winterBlues, "winterBlues");
                MUI.Section(l, "SH_SecList".Translate());
                foreach (Holiday hd in Holidays.All)
                {
                    bool on = !S.off.Contains(hd.id);
                    Holidays.DateOf(hd, Holidays.YearNow(), out int hm, out int hdd);
                    l.CheckboxLabeled(hd.Label + "  –  " + Months.DayLabel(hm, hdd), ref on, hd.Desc);
                    if (on) S.off.Remove(hd.id);
                    else if (!S.off.Contains(hd.id)) S.off.Add(hd.id);
                }
                l.End();
            }
            finally
            {
                Widgets.EndScrollView();
                MUI.EndLock(rest);
            }
        }

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);
    }

    // ---------- months ----------
    public static class Months
    {
        // Twelfth.First is the start of Aprimay = March
        public static int MonthOf(Twelfth t) => ((int)t + 2) % 12;
        public static Twelfth TwelfthOf(int month) => (Twelfth)((month + 10) % 12);

        public static string Name(int m) => ("SH_M" + m).Translate();
        public static string NameOf(int m) => ("SH_MG" + m).Translate();   // "of December" / "prosince"

        public static string DayLabel(int m, int d) => "SH_DayMonth".Translate(d, NameOf(m));

        // the month of the weather: in the south the seasons are turned around
        public static int ClimateMonth(int m, float latitude) => latitude < 0f ? (m + 6) % 12 : m;

        public static void Now(Map map, out int month, out int day)
        {
            long t = Find.TickManager.TicksAbs;
            float lon = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
            At(t, lon, out month, out day);
        }

        // 0.2.0: the calendar date of a moment for any length of the year
        public static void At(long absTicks, float lon, out int month, out int day)
        {
            YearClock.FromDayOfYear(YearClock.DayOfYear(absTicks, lon), out month, out day);
        }

        public static int YearShown(long absTicks, float lon)
        {
            At(absTicks, lon, out int m, out _);
            int y = GenDate.Year(absTicks, lon) + (m <= 1 ? 1 : 0);   // January and February start the new year
            int mode = SHMod.S?.yearMode ?? 0;
            if (mode == 0) return y;
            // 0.2.2: the year the colony started, counted the same way as now (other mods that change the
            // year numbering – Start Date – and the length of the year are taken into account)
            int start = Find.TickManager != null && Find.TickManager.gameStartAbsTick > 0
                ? GenDate.Year(Find.TickManager.gameStartAbsTick, lon) + (StartMonthEarly(lon) ? 1 : 0)
                : GenDate.DefaultStartingYear;
            return y - start + (mode == 2 ? 1 : 0);
        }

        private static bool StartMonthEarly(float lon)
        {
            At(Find.TickManager.gameStartAbsTick, lon, out int m, out _);
            return m <= 1;
        }

        public static string YearText(int y) => (SHMod.S?.yearMode ?? 0) == 0 ? y.ToString() : "SH_YearN".Translate(y).ToString();
    }

    // ---------- the date everywhere in the game ----------
    public static class DatePatches
    {
        public static void Patch(Harmony h)
        {
            Try(h, "DateReadoutStringAt", nameof(Readout));
            Try(h, "DateFullStringAt", nameof(Full));
            Try(h, "DateShortStringAt", nameof(Readout));
            Try(h, "DateMonthYearStringAt", nameof(MonthYear));
        }

        private static void Try(Harmony h, string method, string prefix)
        {
            try
            {
                var m = AccessTools.Method(typeof(GenDate), method, new[] { typeof(long), typeof(Vector2) });
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(DatePatches), prefix));
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] date " + method + ": " + e.Message); }
        }

        // the date readout caches its text per day – after a change of the year numbering it is rebuilt
        public static void Reset()
        {
            try { AccessTools.Method(typeof(DateReadout), "Reset")?.Invoke(null, null); } catch { }
            try { Traverse.Create(typeof(DateReadout)).Field("dateStringYear").SetValue(-1); } catch { }
        }

        private static bool On => SHMod.S != null && SHMod.S.calendarDate && Find.TickManager != null;

        public static bool Readout(long absTicks, Vector2 location, ref string __result)
        {
            if (!On) return true;
            try
            {
                Months.At(absTicks, location.x, out int m, out int d);
                __result = "SH_Date".Translate(d, Months.NameOf(m), Months.YearText(Months.YearShown(absTicks, location.x)), Months.Name(m));
                return false;
            }
            catch { return true; }
        }

        public static bool Full(long absTicks, Vector2 location, ref string __result)
        {
            if (!On) return true;
            try
            {
                Months.At(absTicks, location.x, out int m, out int d);
                Season s = GenDate.Season(absTicks, location);
                __result = "SH_Date".Translate(d, Months.NameOf(m), Months.YearText(Months.YearShown(absTicks, location.x)), Months.Name(m)) + " (" + s.Label() + ")";
                return false;
            }
            catch { return true; }
        }

        public static bool MonthYear(long absTicks, Vector2 location, ref string __result)
        {
            if (!On) return true;
            try
            {
                Months.At(absTicks, location.x, out int m, out _);
                __result = Months.Name(m) + " " + Months.YearText(Months.YearShown(absTicks, location.x));
                return false;
            }
            catch { return true; }
        }
    }
}
