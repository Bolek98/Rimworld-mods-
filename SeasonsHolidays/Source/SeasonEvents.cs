using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.5.0: the year around the holidays – spring thaw, the autumn harvest market, cozy winter
    // evenings at the fire, summer insects; holiday reminders, "looking forward to …", holiday
    // visitors with gifts and packing the decorations away after their season.
    // Called from the map component every 250 ticks; the heavier parts run every 2500 ticks.
    // ---------------------------------------------------------------------------
    public static class SeasonEvents
    {
        private static SHWorldComponent W => Find.World?.GetComponent<SHWorldComponent>();

        public static void Tick(Map map, int month, int day, int hour, int climate, int absDay, Func<string, bool> once)
        {
            var S = SHMod.S;
            int now = Find.TickManager.TicksGame;
            int year = GenDate.Year(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile).x);

            // ---- reminders and "looking forward" (once a day) ----
            if (hour >= 9 && S.holidays)
            {
                int todayDoy = YearClock.ToDayOfYear(month, day);
                int gy = Holidays.YearNow();
                int lead = Mathf.Max(1, Mathf.RoundToInt(YearClock.Days / 30f));
                foreach (Holiday h in Holidays.All)
                {
                    if (h.small || !Holidays.On(h)) continue;
                    Holidays.DateOf(h, gy, out int hm, out int hd);
                    int inDays = (YearClock.ToDayOfYear(hm, hd) - todayDoy + YearClock.Days) % YearClock.Days;
                    if (S.remindDays > 0 && inDays == S.remindDays && once("RM|" + h.id + "|" + absDay))
                        Messages.Message("SH_Remind".Translate(h.Label, inDays), MessageTypeDefOf.NeutralEvent, false);
                    if (S.lookForward && S.traditions && inDays == lead && once("LF|" + h.id + "|" + absDay)) LookingForward(map, h);
                }
            }

            // ---- decorations after their season ----
            if (S.packAway > 0 && day == 1 && hour >= 10 && once("PA|" + absDay)) PackAway(map, month);

            if (!S.seasonalEvents) return;

            // ---- spring thaw: after a frosty winter, the first days of spring are wet and muddy by the water ----
            if (S.springThaw && (climate == 2 || climate == 3) && day == 1 && hour >= 8 && Avg(map, month - 1) < 0f && Avg(map, month) > 0f
                && once("TH|" + year) && Rand.Chance(0.6f)) Thaw(map);
            if (S.springThaw && now % 500 == 267 % 500) Muddy(map);

            // ---- autumn harvest market ----
            if (S.harvestMarket && climate == 8 && day == 2 && hour >= 10 && once("HM|" + year) && Rand.Chance(0.7f)) Market(map);

            if (now % 2500 != 1267) return;
            // ---- cozy evenings at the fire in winter ----
            if (S.cozyEvenings && (climate == 11 || climate <= 1) && hour >= 17) Cozy(map);
            // ---- summer insects in hot humid places ----
            if (S.summerInsects && climate >= 5 && climate <= 7 && hour >= 7 && hour <= 21) Insects(map);
        }

        private static float Avg(Map map, int m) => GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, Months.TwelfthOf(((m % 12) + 12) % 12));

        // ---------- traditions: looking forward ----------
        private static void LookingForward(Map map, Holiday h)
        {
            var w = W;
            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail("SH_LookingForward");
            if (w == null || def == null) return;
            int n = 0;
            foreach (Pawn p in Holidays.Colonists(map))
            {
                if (!w.Enjoyed(p, h.id)) continue;
                var t = (Thought_SHLookingForward)ThoughtMaker.MakeThought(def);
                t.holiday = h.id;
                p.needs.mood.thoughts.memories.TryGainMemory(t);
                n++;
            }
            if (n > 0) Messages.Message("SH_LookingForwardMsg".Translate(n, h.Label), MessageTypeDefOf.PositiveEvent, false);
        }

        // ---------- holiday visitors (from the morning of a holiday) ----------
        public static void Visitors(Map map, Holiday h)
        {
            var w = W;
            if (SHMod.S == null || !SHMod.S.holidayVisitors || w == null || h.small) return;
            int now = Find.TickManager.TicksGame;
            if (w.lastVisitors > 0 && now - w.lastVisitors < 15 * 60000) return;
            if (!Rand.Chance(0.25f)) return;
            Faction f = Find.FactionManager.AllFactionsVisible.Where(x => !x.IsPlayer && !x.defeated && !x.temporary && x.def.humanlikeFaction
                && !x.HostileTo(Faction.OfPlayer) && x.GoodwillWith(Faction.OfPlayer) >= 0
                && x.def.pawnGroupMakers != null && x.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Peaceful)).RandomElementWithFallback();
            if (f == null) return;
            try
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                parms.faction = f;
                parms.forced = true;
                if (!IncidentDefOf.VisitorGroup.Worker.TryExecute(parms)) return;
                w.lastVisitors = now;
                var gifts = new List<Thing>();
                gifts.AddRange(Holidays.Make("Silver", Rand.RangeInclusive(30, 90)));
                gifts.AddRange(Holidays.Make(GiftFood(h.id), Mathf.Clamp(Holidays.Colonists(map).Count, 3, 10)));
                IntVec3 c = DropCellFinder.TradeDropSpot(map);
                foreach (Thing t in gifts) GenPlace.TryPlaceThing(t, c, map, ThingPlaceMode.Near);
                Notify.Send("SH_VisitorsTitle".Translate(), "SH_Visitors".Translate(f.Name, h.Label), LetterDefOf.PositiveEvent, new LookTargets(c, map));
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] holiday visitors: " + e.Message); }
        }

        private static string GiftFood(string id)
        {
            switch (id)
            {
                case "Christmas": case "StNicholas": return "SH_ChristmasCookies";
                case "Easter": return "SH_EasterEgg";
                case "Diwali": return "SH_DiwaliSweets";
                case "MidAutumn": return "SH_Mooncake";
                case "Halloween": case "Thanksgiving": case "Harvest": return "SH_PumpkinPie";
                case "NewYear": case "NewYearsEve": return "SH_NewYearPunch";
            }
            return "Beer";
        }

        // ---------- decorations: pack them away after their season ----------
        private static void PackAway(Map map, int month)
        {
            int prev = (month + 11) % 12;
            var list = new List<Thing>();
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                var ext = b.def.GetModExtension<SHSeasonDeco>();
                if (ext == null || ext.months.NullOrEmpty() || ext.months.Contains(month) || !ext.months.Contains(prev)) continue;
                list.Add(b);
            }
            if (list.Count == 0) return;
            int marked = 0;
            if (SHMod.S.packAway == 2)
                foreach (Thing t in list)
                    if (t.def.Minifiable && map.designationManager.DesignationOn(t, DesignationDefOf.Uninstall) == null)
                    {
                        map.designationManager.AddDesignation(new Designation(t, DesignationDefOf.Uninstall));
                        marked++;
                    }
            Messages.Message((marked > 0 ? "SH_PackAwayMarked" : "SH_PackAwayHint").Translate(list.Count), new LookTargets(list), MessageTypeDefOf.NeutralEvent, false);
        }

        // ---------- spring thaw ----------
        private static void Thaw(Map map)
        {
            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail("SH_SpringThaw");
            if (def == null || map.gameConditionManager.ConditionIsActive(def)) return;
            if (!HasWater(map)) return;
            map.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(def, 2 * 60000));
            WeatherDef rain = DefDatabase<WeatherDef>.GetNamedSilentFail("Rain");
            if (rain != null && map.mapTemperature.OutdoorTemp > 1f) map.weatherManager.TransitionTo(rain);
            Notify.Send("SH_ThawTitle".Translate(), "SH_Thaw".Translate(), LetterDefOf.NeutralEvent);
        }

        private static bool HasWater(Map map)
        {
            for (int i = 0; i < 300; i++)
            {
                IntVec3 c = CellFinder.RandomCell(map);
                if (c.GetTerrain(map).IsWater) return true;
            }
            return false;
        }

        private static bool NearWater(IntVec3 c, Map map)
        {
            if (!c.InBounds(map)) return false;
            if (Wet(c.GetTerrain(map))) return true;
            for (int i = 0; i < 8; i++)
            {
                IntVec3 a = c + GenAdj.AdjacentCells[i];
                if (a.InBounds(map) && Wet(a.GetTerrain(map))) return true;
            }
            return false;
        }

        private static bool Wet(TerrainDef t) => t != null && (t.IsWater || t.defName.IndexOf("Mud", StringComparison.OrdinalIgnoreCase) >= 0
            || t.defName.IndexOf("Marsh", StringComparison.OrdinalIgnoreCase) >= 0);

        private static void Muddy(Map map)
        {
            GameConditionDef def = DefDatabase<GameConditionDef>.GetNamedSilentFail("SH_SpringThaw");
            HediffDef mud = DefDatabase<HediffDef>.GetNamedSilentFail("SH_Muddy");
            if (def == null || mud == null || !map.gameConditionManager.ConditionIsActive(def)) return;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Position.Roofed(map) || !NearWater(p.Position, map)) continue;
                Hediff h = p.health.hediffSet.GetFirstHediffOfDef(mud);
                if (h == null) { h = HediffMaker.MakeHediff(mud, p); p.health.AddHediff(h); }
                HediffComp_Disappears c = h.TryGetComp<HediffComp_Disappears>();
                if (c != null) c.ticksToDisappear = Mathf.Max(c.ticksToDisappear, 2500);
            }
        }

        // ---------- harvest market ----------
        private static void Market(Map map)
        {
            TraderKindDef kind = DefDatabase<TraderKindDef>.GetNamedSilentFail("SH_HarvestMarket");
            Faction f = Find.FactionManager.AllFactionsVisible.Where(x => !x.IsPlayer && !x.defeated && !x.temporary && x.def.humanlikeFaction
                && !x.HostileTo(Faction.OfPlayer) && x.def.caravanTraderKinds.Count > 0 && x.def.pawnGroupMakers != null
                && x.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Trader)).RandomElementWithFallback();
            if (kind == null || f == null) return;
            try
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                parms.faction = f;
                parms.traderKind = kind;
                parms.forced = true;
                if (IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms))
                    Messages.Message("SH_MarketMsg".Translate(f.Name), MessageTypeDefOf.PositiveEvent, false);
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] harvest market: " + e.Message); }
        }

        // ---------- cozy evenings ----------
        private static List<ThingDef> fires;

        private static List<ThingDef> Fires
        {
            get
            {
                if (fires == null)
                    fires = DefDatabase<ThingDef>.AllDefs.Where(d => d.category == ThingCategory.Building && d.HasComp(typeof(CompHeatPusher))
                        && (d.defName.IndexOf("Fireplace", StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf("Campfire", StringComparison.OrdinalIgnoreCase) >= 0
                            || d.defName.IndexOf("Hearth", StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf("Brazier", StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                return fires;
            }
        }

        private static bool Lit(Thing t)
        {
            CompRefuelable r = t.TryGetComp<CompRefuelable>();
            if (r != null && !r.HasFuel) return false;
            CompFlickable fl = t.TryGetComp<CompFlickable>();
            return fl == null || fl.SwitchIsOn;
        }

        private static void Cozy(Map map)
        {
            var lit = new List<Thing>();
            foreach (ThingDef d in Fires)
                foreach (Thing t in map.listerThings.ThingsOfDef(d))
                    if (t.Faction == Faction.OfPlayer && Lit(t)) lit.Add(t);
            if (lit.Count == 0) return;
            foreach (Pawn p in Holidays.Colonists(map))
            {
                if (!p.Awake() || p.Drafted || p.Downed) continue;
                Room room = p.GetRoom();
                if (lit.Any(t => t.Position.DistanceToSquared(p.Position) <= 49 && t.GetRoom() == room))
                    Holidays.GiveThought(p, "SH_CozyEvening", 0);
            }
        }

        // ---------- summer insects ----------
        private static void Insects(Map map)
        {
            if (map.TileInfo.rainfall < 1300f || map.mapTemperature.OutdoorTemp < 22f) return;
            foreach (Pawn p in Holidays.Colonists(map))
                if (p.Awake() && !p.Position.Roofed(map) && Rand.Chance(0.5f)) Holidays.GiveThought(p, "SH_Mosquitoes", 0);
        }
    }

    // ---------------------------------------------------------------------------
    // 0.5.0: culture – which holidays the colony keeps (auto by its tech level and beliefs)
    // ---------------------------------------------------------------------------
    public static class Culture
    {
        private static int at = -99999;
        private static bool tribal;

        public static bool Tribal
        {
            get
            {
                if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null || Faction.OfPlayerSilentFail == null) return false;
                int now = Find.TickManager.TicksGame;
                if (now - at < 2500 && now >= at) return tribal;
                at = now;
                try
                {
                    Faction f = Faction.OfPlayer;
                    tribal = f.def.techLevel <= TechLevel.Neolithic;
                    if (!tribal && ModsConfig.IdeologyActive && f.ideos?.PrimaryIdeo != null)
                        tribal = f.ideos.PrimaryIdeo.memes.Any(m => m.defName == "Animist" || m.defName == "NaturePrimacy") && f.def.techLevel <= TechLevel.Medieval;
                }
                catch { tribal = false; }
                return tribal;
            }
        }

        public static bool Allows(Holiday h)
        {
            if (string.IsNullOrEmpty(h.culture) || Current.ProgramState != ProgramState.Playing) return true;
            return h.culture == "tribal" ? Tribal : !Tribal;
        }
    }
}
