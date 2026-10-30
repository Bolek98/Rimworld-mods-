using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.3.0: holidays from around the world, in packs you can switch on and off:
    //  Asia, the Americas, Europe (more) – with a mood, a party and sometimes fireworks or gifts –
    //  and small "days" (Pi Day, Towel Day …) that are only a short message.
    // In the 60-day year they sit on free days of the 5-day months; in a 365-day year on their
    // real date (moving holidays on a typical date).
    // ---------------------------------------------------------------------------
    public static partial class Holidays
    {
        public static readonly string[] Packs = { "Asia", "Americas", "Europe", "Days" };

        public static bool PackOn(string pack)
        {
            if (string.IsNullOrEmpty(pack) || SHMod.S == null) return true;
            switch (pack)
            {
                case "Asia": return SHMod.S.packAsia;
                case "Americas": return SHMod.S.packAmericas;
                case "Europe": return SHMod.S.packEurope;
                case "Days": return SHMod.S.packDays;
            }
            return true;
        }

        private static void AddPacks(List<Holiday> l)
        {
            // Asia
            l.Add(new Holiday { pack = "Asia", id = "LunarNewYear", month = 1, day = 2, realMonth = 2, realDay = 10, thought = "SH_H_LunarNewYear", partyHour = 18, eveningHour = 22, evening = Fireworks });
            l.Add(new Holiday { pack = "Asia", id = "Holi", month = 2, day = 4, realMonth = 3, realDay = 25, thought = "SH_H_Holi", partyHour = 14 });
            l.Add(new Holiday { pack = "Asia", id = "Diwali", month = 9, day = 3, realMonth = 10, realDay = 29, thought = "SH_H_Diwali", partyHour = 19, eveningHour = 21, evening = Fireworks });
            l.Add(new Holiday { pack = "Asia", id = "MidAutumn", month = 8, day = 3, realMonth = 9, realDay = 17, thought = "SH_H_MidAutumn", partyHour = 19 });
            // the Americas
            l.Add(new Holiday { pack = "Americas", id = "DayOfDead", month = 10, day = 2, realMonth = 11, realDay = 1, thought = "SH_H_DayOfDead", partyHour = 18, morning = Remember });
            l.Add(new Holiday { pack = "Americas", id = "FoundersDay", month = 6, day = 1, realMonth = 7, realDay = 4, thought = "SH_H_FoundersDay", partyHour = 17, eveningHour = 22, evening = Fireworks });
            // Europe (more)
            l.Add(new Holiday { pack = "Europe", id = "StPatrick", month = 2, day = 3, realMonth = 3, realDay = 17, thought = "SH_H_StPatrick", partyHour = 18, morning = BeerGift });
            l.Add(new Holiday { pack = "Europe", id = "Oktoberfest", month = 8, day = 5, realMonth = 9, realDay = 20, thought = "SH_H_Oktoberfest", partyHour = 16, morning = BeerGift });
            l.Add(new Holiday { pack = "Europe", id = "StMartin", month = 10, day = 3, realMonth = 11, realDay = 11, thought = "SH_H_StMartin", partyHour = 17 });
            // small days – a message only
            l.Add(new Holiday { pack = "Days", small = true, id = "PiDay", month = 2, day = 2, realMonth = 3, realDay = 14 });
            l.Add(new Holiday { pack = "Days", small = true, id = "EarthDay", month = 3, day = 4, realMonth = 4, realDay = 22 });
            l.Add(new Holiday { pack = "Days", small = true, id = "BookDay", month = 3, day = 3, realMonth = 4, realDay = 23 });
            l.Add(new Holiday { pack = "Days", small = true, id = "TowelDay", month = 4, day = 4, realMonth = 5, realDay = 25 });
            l.Add(new Holiday { pack = "Days", small = true, id = "ChocolateDay", month = 6, day = 3, realMonth = 7, realDay = 7 });
            l.Add(new Holiday { pack = "Days", small = true, id = "CatDay", month = 7, day = 2, realMonth = 8, realDay = 8 });
            l.Add(new Holiday { pack = "Days", small = true, id = "PirateDay", month = 8, day = 2, realMonth = 9, realDay = 19 });
            l.Add(new Holiday { pack = "Days", small = true, id = "SmileDay", month = 9, day = 1, realMonth = 10, realDay = 2 });
        }

        // a few bottles for the evening (St. Patrick, Oktoberfest)
        private static void BeerGift(Map map)
        {
            if (!SHMod.S.gifts) return;
            int n = Colonists(map).Count;
            Drop(map, "SH_GiftBeer", Make("Beer", Mathf.Clamp(n, 2, 12)));
        }
    }
}
