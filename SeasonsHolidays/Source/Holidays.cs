using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    public class Holiday
    {
        public string id;
        public int month, day;              // game calendar (day 1–5)
        public int realMonth, realDay;      // real date (realMonth 0 = Easter, computed)
        public string thought;              // ThoughtDef for everyone (or the right people)
        public int partyHour = -1;          // a party at this hour
        public Func<Pawn, bool> who;        // who gets the thought (null = all)
        public Action<Map> morning, evening;
        public int eveningHour = 20;

        public string Label => ("SH_H_" + id).Translate();
        public string Desc => ("SH_HD_" + id).Translate();
    }

    public static class Holidays
    {
        private static List<Holiday> all;

        private static bool Child(Pawn p) => ModsConfig.BiotechActive && p.DevelopmentalStage.Child();
        private static bool Adult(Pawn p) => !ModsConfig.BiotechActive || p.DevelopmentalStage.Adult();
        private static bool Lover(Pawn p) => LovePartnerRelationUtility.ExistingMostLikedLovePartner(p, false) != null;

        public static List<Holiday> All
        {
            get
            {
                if (all == null)
                    all = new List<Holiday>
                    {
                        new Holiday { id = "NewYear", month = 0, day = 1, realMonth = 1, realDay = 1, thought = "SH_H_NewYear", partyHour = 17, morning = Resolutions },
                        new Holiday { id = "Valentine", month = 1, day = 3, realMonth = 2, realDay = 14, thought = "SH_H_Valentine", who = Lover },
                        new Holiday { id = "Carnival", month = 1, day = 5, realMonth = 2, realDay = 25, thought = "SH_H_Carnival", partyHour = 16 },
                        new Holiday { id = "Easter", month = 3, day = 2, realMonth = 0, realDay = 0, thought = "SH_H_Easter" },
                        new Holiday { id = "Witches", month = 3, day = 5, realMonth = 4, realDay = 30, thought = "SH_H_Witches", partyHour = 19 },
                        new Holiday { id = "MayDay", month = 4, day = 1, realMonth = 5, realDay = 1, thought = "SH_H_MayDay", who = Lover },
                        new Holiday { id = "ChildrenDay", month = 5, day = 1, realMonth = 6, realDay = 1, thought = "SH_H_ChildrenDay", who = Child, morning = ChildrenDayGifts },
                        new Holiday { id = "Solstice", month = 5, day = 4, realMonth = 6, realDay = 21, thought = "SH_H_Solstice", partyHour = 18 },
                        new Holiday { id = "Harvest", month = 8, day = 4, realMonth = 9, realDay = 22, thought = null, partyHour = 17, morning = HarvestMoods },
                        new Holiday { id = "Halloween", month = 9, day = 5, realMonth = 10, realDay = 31, thought = "SH_H_Halloween", eveningHour = 19, evening = HalloweenFog },
                        new Holiday { id = "AllSouls", month = 10, day = 1, realMonth = 11, realDay = 2, thought = "SH_H_AllSouls", morning = Remember },
                        new Holiday { id = "Thanksgiving", month = 10, day = 4, realMonth = 11, realDay = 26, thought = "SH_H_Thanksgiving", partyHour = 17 },
                        new Holiday { id = "StNicholas", month = 11, day = 2, realMonth = 12, realDay = 5, thought = "SH_H_StNicholas", who = p => !Child(p), morning = NicholasGifts },
                        new Holiday { id = "Christmas", month = 11, day = 4, realMonth = 12, realDay = 24, thought = "SH_H_Christmas", partyHour = 17, morning = ChristmasGifts },
                        new Holiday { id = "NewYearsEve", month = 11, day = 5, realMonth = 12, realDay = 31, thought = "SH_H_NewYearsEve", partyHour = 20, eveningHour = 23, evening = Fireworks },
                    };
                return all;
            }
        }

        public static bool On(Holiday h) => SHMod.S.holidays && !SHMod.S.off.Contains(h.id);

        public static Holiday Today(Map map)
        {
            Months.Now(map, out int m, out int d);
            return All.FirstOrDefault(h => h.month == m && h.day == d);
        }

        // Easter Sunday (Gregorian, Meeus/Jones/Butcher)
        public static DateTime Easter(int y)
        {
            int a = y % 19, b = y / 100, c = y % 100, d = b / 4, e = b % 4, f = (b + 8) / 25, g = (b - f + 1) / 3;
            int h = (19 * a + b - d - g + 15) % 30, i = c / 4, k = c % 4, l = (32 + 2 * e + 2 * i - h - k) % 7, m = (a + 11 * h + 22 * l) / 451;
            int month = (h + l - 7 * m + 114) / 31, day = (h + l - 7 * m + 114) % 31 + 1;
            return new DateTime(y, month, day);
        }

        public static bool IsRealToday(Holiday h, DateTime now)
        {
            if (h.realMonth == 0) { DateTime e = Easter(now.Year); return now.Date == e.Date || now.Date == e.Date.AddDays(1); }
            return now.Month == h.realMonth && now.Day == h.realDay;
        }

        public static List<Pawn> Colonists(Map map) =>
            map.mapPawns.FreeColonistsSpawned.Where(p => p.needs?.mood != null && !p.Dead).ToList();

        public static void GiveThought(Pawn p, string defName, int stage)
        {
            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail(defName);
            if (def == null || p.needs?.mood?.thoughts?.memories == null) return;
            stage = Mathf.Clamp(stage, 0, def.stages.Count - 1);
            Thought_Memory t = ThoughtMaker.MakeThought(def, stage);
            p.needs.mood.thoughts.memories.TryGainMemory(t);
        }

        // ---------- holiday extras ----------
        private static void Resolutions(Map map)
        {
            HediffDef d = DefDatabase<HediffDef>.GetNamedSilentFail("SH_Resolution");
            if (d == null) return;
            foreach (Pawn p in Colonists(map))
                if (!p.health.hediffSet.HasHediff(d)) p.health.AddHediff(d);
        }

        private static void HarvestMoods(Map map)
        {
            var ps = Colonists(map);
            float need = Mathf.Max(1, ps.Count) * 1.6f;   // nutrition a day
            float days = map.resourceCounter.TotalHumanEdibleNutrition / need;
            int stage = days >= 20f ? 2 : days >= 8f ? 1 : 0;
            foreach (Pawn p in ps) GiveThought(p, "SH_H_Harvest", stage);
        }

        private static void ChildrenDayGifts(Map map)
        {
            var kids = Colonists(map).Where(Child).ToList();
            foreach (Pawn p in Colonists(map).Where(p => !Child(p)))
                if (kids.Count > 0) GiveThought(p, "SH_H_ChildrenDayAdult", Tradition.Stage("ChildrenDay"));
            if (kids.Count > 0 && SHMod.S.gifts) Drop(map, "SH_GiftChildren", Make("Chocolate", kids.Count * 2));
        }

        private static void NicholasGifts(Map map)
        {
            var ps = Colonists(map);
            var kids = ps.Where(Child).ToList();
            foreach (Pawn k in kids) GiveThought(k, "SH_H_StNicholasChild", Tradition.Stage("StNicholas"));
            if (SHMod.S.gifts) Drop(map, "SH_GiftNicholas", Make("Chocolate", kids.Count * 3 + ps.Count));
        }

        private static void ChristmasGifts(Map map)
        {
            if (!SHMod.S.gifts) return;
            int n = Colonists(map).Count;
            var things = new List<Thing>();
            things.AddRange(Make("SH_ChristmasCookies", n * 2));
            things.AddRange(Make("Silver", 25 * Mathf.Clamp(n, 1, 12)));
            things.AddRange(Make("MedicineHerbal", Mathf.Clamp(n / 2, 2, 6)));
            things.AddRange(Make("Beer", Mathf.Clamp(n, 2, 10)));
            Drop(map, "SH_GiftChristmas", things);
            // white Christmas: snow when it is cold enough
            if (SHMod.S.events && map.mapTemperature.OutdoorTemp < 0f && Rand.Chance(0.6f))
            {
                WeatherDef snow = DefDatabase<WeatherDef>.GetNamedSilentFail("SnowGentle");
                if (snow != null && map.weatherManager.curWeather != snow) map.weatherManager.TransitionTo(snow);
            }
        }

        // All Souls' Day: the grief for the dead becomes easier – death memories are half way through
        private static void Remember(Map map)
        {
            foreach (Pawn p in Colonists(map))
            {
                var mem = p.needs.mood.thoughts.memories.Memories;
                for (int i = 0; i < mem.Count; i++)
                {
                    Thought_Memory t = mem[i];
                    if (t.def.defName.IndexOf("Died", StringComparison.OrdinalIgnoreCase) < 0 && t.def.defName.IndexOf("Death", StringComparison.OrdinalIgnoreCase) < 0) continue;
                    if (t.MoodOffset() >= 0f) continue;
                    int half = Mathf.RoundToInt(t.DurationTicks * 0.5f);
                    if (t.age < half) t.age = half;
                }
            }
        }

        private static void HalloweenFog(Map map)
        {
            if (!SHMod.S.events) return;
            WeatherDef fog = DefDatabase<WeatherDef>.GetNamedSilentFail("Fog");
            if (fog != null && map.weatherManager.curWeather != fog && map.weatherManager.curWeather.rainRate < 0.1f) map.weatherManager.TransitionTo(fog);
        }

        private static void Fireworks(Map map)
        {
            if (!SHMod.S.events) return;
            Fireworks f = map.GetComponent<SHMapComponent>()?.fireworks;
            if (f != null) f.Start(map);
        }

        public static List<Thing> Make(string def, int count)
        {
            var l = new List<Thing>();
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null || count <= 0) return l;
            while (count > 0)
            {
                Thing t = ThingMaker.MakeThing(d);
                t.stackCount = Mathf.Min(count, d.stackLimit);
                count -= t.stackCount;
                l.Add(t);
            }
            return l;
        }

        private static void Drop(Map map, string key, List<Thing> things)
        {
            if (things.Count == 0) return;
            IntVec3 c = DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(c, map, things, 110, false, false, false, false);
            Messages.Message(key.Translate(), new TargetInfo(c, map), MessageTypeDefOf.PositiveEvent);
        }
    }

    // how many times the colony celebrated a holiday (a tradition gives more mood)
    public static class Tradition
    {
        public static int Count(string id) => Find.World?.GetComponent<SHWorldComponent>()?.Count(id) ?? 0;

        // the stage of the holiday thought: 0 the first time, up to 3 after three years
        public static int Stage(string id) => SHMod.S.traditions ? Mathf.Clamp(Count(id) - 1, 0, 3) : 0;
    }

    public class SHWorldComponent : WorldComponent
    {
        public Dictionary<string, int> celebrated = new Dictionary<string, int>();
        public List<string> done = new List<string>();   // "<holiday>|<year>|<map>" already done

        public SHWorldComponent(World world) : base(world) { }

        public int Count(string id) => celebrated.TryGetValue(id, out int n) ? n : 0;

        public bool Mark(string key)
        {
            if (done.Contains(key)) return false;
            done.Add(key);
            if (done.Count > 400) done.RemoveRange(0, done.Count - 400);
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref celebrated, "celebrated", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref done, "done", LookMode.Value);
            if (celebrated == null) celebrated = new Dictionary<string, int>();
            if (done == null) done = new List<string>();
        }
    }

    // New Year's Eve fireworks: bursts of light over the colony for an in-game hour
    public class Fireworks
    {
        private int until = -1;

        public void Start(Map map) { until = Find.TickManager.TicksGame + 2500; }

        public void Tick(Map map)
        {
            if (until < 0 || Find.TickManager.TicksGame > until) { until = -1; return; }
            if (Find.TickManager.TicksGame % 20 != 0 || !Rand.Chance(0.6f)) return;
            IntVec3 center = map.mapPawns.FreeColonistsSpawned.Count > 0 ? map.mapPawns.FreeColonistsSpawned.RandomElement().Position : map.Center;
            IntVec3 c = center + new IntVec3(Rand.Range(-18, 19), 0, Rand.Range(-18, 19));
            if (!c.InBounds(map) || c.Roofed(map)) return;
            Vector3 v = c.ToVector3Shifted();
            FleckMaker.Static(v, map, FleckDefOf.ExplosionFlash, Rand.Range(3f, 6f));
            FleckMaker.ThrowLightningGlow(v, map, Rand.Range(2f, 4f));
            for (int i = 0; i < 4; i++) FleckMaker.ThrowMicroSparks(v + new Vector3(Rand.Range(-1f, 1f), 0f, Rand.Range(-1f, 1f)), map);
        }
    }
}
