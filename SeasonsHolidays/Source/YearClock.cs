using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.2.0: the length of the year (60 / 120 / 180 / 365 days). The game counts its year as
    // 60 days everywhere; here the yearly cycle – twelfths, seasons, the date, the year number and
    // the seasonal temperature – runs k times slower (k = days / 60), so a 365-day year has long
    // real seasons. Day and night stay as they are. Changing the length in a running game keeps
    // today's season: the cycle is anchored to the moment of the change.
    // Optional: colonists age with the calendar year (k times slower), otherwise as in the game.
    // ---------------------------------------------------------------------------
    public class SHClock : GameComponent
    {
        public long anchorAbs = -1;
        public double anchorScaled;
        public double kUsed;

        public SHClock(Game g) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref anchorAbs, "anchorAbs", -1L);
            Scribe_Values.Look(ref anchorScaled, "anchorScaled", 0d);
            Scribe_Values.Look(ref kUsed, "kUsed", 0d);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) YearClock.Invalidate();
        }

        public override void FinalizeInit() => YearClock.Invalidate();
    }

    public static class YearClock
    {
        public static readonly int[] Lengths = { 60, 120, 180, 365 };

        // March … February (the game's year starts with Aprimay = March)
        private static readonly int[] RealMonthLen = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };   // January … December

        public static int Days
        {
            get
            {
                int d = SHMod.S?.yearDays ?? 60;
                return Array.IndexOf(Lengths, d) >= 0 ? d : 60;
            }
        }

        public static double K => Days / 60.0;

        // cached anchor of the running game
        private static Game cachedGame;
        private static long aAbs;
        private static double aScaled, aK = 1.0;
        private static bool identity = true;

        public static void Invalidate() { cachedGame = null; }

        private static void Ensure()
        {
            Game g = Current.Game;
            if (g == null || Find.TickManager == null) { identity = true; cachedGame = null; return; }
            double k = K;
            if (g == cachedGame && Math.Abs(aK - k) < 1e-9) return;
            SHClock c = g.GetComponent<SHClock>();
            if (c == null) { identity = true; return; }
            long now = Find.TickManager.TicksAbs;
            if (c.anchorAbs < 0 || c.kUsed <= 0)
            {
                // a new game keeps its starting season, an old save keeps today's season
                long start = Find.TickManager.gameStartAbsTick;
                long at = Find.TickManager.TicksGame < 2500 && start > 0 ? start : now;
                c.anchorAbs = at;
                c.anchorScaled = at;
                c.kUsed = k;
            }
            else if (Math.Abs(c.kUsed - k) > 1e-9)
            {
                c.anchorScaled = c.anchorScaled + (now - c.anchorAbs) / c.kUsed;
                c.anchorAbs = now;
                c.kUsed = k;
                DatePatches.Reset();
            }
            aAbs = c.anchorAbs;
            aScaled = c.anchorScaled;
            aK = c.kUsed;
            identity = Math.Abs(aK - 1.0) < 1e-9 && Math.Abs(aScaled - aAbs) < 0.5;
            cachedGame = g;
        }

        // the game's yearly clock for a real moment
        public static long Scaled(long abs)
        {
            Ensure();
            if (identity) return abs;
            return (long)Math.Floor(aScaled + (abs - aAbs) / aK);
        }

        // ---------- calendar (day 0 = 1 March) ----------
        public static int MonthLen(int month)   // month 0 = January
        {
            int d = Days;
            if (d == 365) return RealMonthLen[month];
            return d / 12;
        }

        // calendar month (0 = January) and day (1-based) for a day of the year (0 = 1 March)
        public static void FromDayOfYear(int doy, out int month, out int day)
        {
            int m = 2;   // March
            int left = ((doy % Days) + Days) % Days;
            for (int i = 0; i < 12; i++)
            {
                int len = MonthLen(m);
                if (left < len) { month = m; day = left + 1; return; }
                left -= len;
                m = (m + 1) % 12;
            }
            month = 1; day = MonthLen(1);
        }

        public static int ToDayOfYear(int month, int day)
        {
            int doy = 0;
            for (int m = 2; m != month; m = (m + 1) % 12) doy += MonthLen(m);
            return doy + Mathf.Clamp(day, 1, MonthLen(month)) - 1;
        }

        // the calendar day of the year of a real moment (YearPercent runs on the scaled clock)
        public static int DayOfYear(long abs, float longitude)
        {
            float p = GenDate.YearPercent(abs, longitude);
            return Mathf.Clamp(Mathf.FloorToInt(p * Days + 0.0001f), 0, Days - 1);
        }

        // ---------- patches ----------
        public static void Patch(Harmony h)
        {
            var longF = new[] { typeof(long), typeof(float) };
            foreach (string n in new[] { "DayOfTwelfth", "DayOfYear", "Twelfth", "YearPercent", "Year" })
                Try(h, AccessTools.Method(typeof(GenDate), n, longF), nameof(LongPrefix));
            Try(h, AccessTools.Method(typeof(GenTemperature), "OffsetFromSeasonCycle"), nameof(SeasonTempPrefix));
            Try(h, AccessTools.PropertyGetter(typeof(Pawn_AgeTracker), "BiologicalTicksPerTick"), null, nameof(AgePostfix));
        }

        private static void Try(Harmony h, MethodBase m, string prefix, string postfix = null)
        {
            if (m == null) { Log.Warning("[Seasons & Holidays] year length: a game method was not found – part of it is off."); return; }
            try
            {
                h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(YearClock), prefix),
                    postfix: postfix == null ? null : new HarmonyMethod(typeof(YearClock), postfix));
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] year length " + m.Name + ": " + e.Message); }
        }

        public static void LongPrefix(ref long absTicks)
        {
            if (SHMod.S == null) return;
            absTicks = Scaled(absTicks);
        }

        // only real moments (now and the near future): the game also asks for sample days of a
        // made-up year (average of a twelfth, min / max of the year) – those stay as they are
        public static void SeasonTempPrefix(ref int absTick)
        {
            if (SHMod.S == null || Find.TickManager == null || Current.ProgramState != ProgramState.Playing) return;
            long now = Find.TickManager.TicksAbs;
            if (absTick < now - 60000L || absTick > now + 3600000L) return;
            absTick = (int)Scaled(absTick);
        }

        public static void AgePostfix(ref float __result)
        {
            if (SHMod.S == null || !SHMod.S.ageWithYear || Days == 60) return;
            __result /= (float)K;
        }
    }
}
