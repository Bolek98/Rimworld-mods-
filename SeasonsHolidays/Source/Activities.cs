using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.4.0: holiday activities – on the morning of a holiday a letter offers something to do
    // (an egg hunt, a witch bonfire, a maypole …); "Celebrate!" and the colonists do it themselves.
    // A few are quiet and happen by themselves (New Year's resolutions, grave candles, St. Nicholas,
    // the New Year hangover). Every activity can be switched off in the settings, and the calendar
    // has a "Celebrate" button to start one by hand.
    // ---------------------------------------------------------------------------
    public static class Activities
    {
        // holidays that offer an activity in a letter
        private static readonly HashSet<string> Offered = new HashSet<string>
        {
            "Valentine", "Carnival", "Easter", "Witches", "MayDay", "ChildrenDay", "Solstice",
            "Harvest", "Halloween", "Thanksgiving", "Christmas"
        };

        // holidays whose activity runs by itself on the morning
        private static readonly HashSet<string> Quiet = new HashSet<string> { "NewYear", "AllSouls", "StNicholas", "NewYearsEve" };

        public static bool Has(string id) => Offered.Contains(id) || Quiet.Contains(id);

        public static bool On(string id) =>
            SHMod.S != null && SHMod.S.activities && Has(id) && !SHMod.S.actOff.Contains(id);

        public static string Label(string id) => ("SH_Act_" + id).Translate();

        private static SHWorldComponent W => Find.World?.GetComponent<SHWorldComponent>();

        private static int Now => Find.TickManager.TicksGame;

        // ---------- the morning ----------
        public static void Morning(Map map, Holiday h)
        {
            if (SHMod.S.holidayTrader && (h.id == "Christmas" || h.id == "Easter")) Trader(map);
            if (!On(h.id)) return;
            if (Quiet.Contains(h.id)) { Run(map, h.id, false); return; }
            ChoiceLetter_SHActivity l = (ChoiceLetter_SHActivity)LetterMaker.MakeLetter(
                "SH_ActTitle".Translate(h.Label), ("SH_ActOffer_" + h.id).Translate(), DefDatabase<LetterDef>.GetNamed("SH_ActivityLetter"), (LookTargets)null);
            l.map = map;
            l.holiday = h.id;
            l.StartTimeout(60000);
            Find.LetterStack.ReceiveLetter(l);
        }

        // ---------- run an activity (from the letter or from the calendar) ----------
        public static void Run(Map map, string id, bool manual)
        {
            if (map == null || Holidays.Colonists(map).Count == 0) return;
            // the bonfires burn in the evening: accepted in the morning, they are lit at 7 pm
            if (!manual && (id == "Witches" || id == "Solstice") && GenLocalDate.HourOfDay(map) < 18)
            {
                int hour = GenLocalDate.HourOfDay(map);
                W.Schedule(map, Now + (19 - hour) * 2500, "Run", id);
                Messages.Message("SH_ActTonight".Translate(Label(id)), MessageTypeDefOf.PositiveEvent, false);
                return;
            }
            try
            {
                switch (id)
                {
                    case "NewYear": Resolutions(map); break;
                    case "Valentine": Valentine(map); break;
                    case "Carnival": Carnival(map); break;
                    case "Easter": EggHunt(map); break;
                    case "Witches": Witches(map); break;
                    case "MayDay": MayDay(map); break;
                    case "ChildrenDay": ChildrenDay(map); break;
                    case "Solstice": Solstice(map); break;
                    case "Harvest": HarvestFeast(map); break;
                    case "Halloween": Halloween(map); break;
                    case "AllSouls": GraveCandles(map); break;
                    case "Thanksgiving": Thanksgiving(map); break;
                    case "StNicholas": Nicholas(map); break;
                    case "Christmas": Carols(map); break;
                    case "NewYearsEve": NewYearsEve(map, manual); break;
                }
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] activity " + id + ": " + e.Message); }
        }

        // ---------- helpers ----------
        private static void Thought(IEnumerable<Pawn> ps, string def)
        {
            foreach (Pawn p in ps) Holidays.GiveThought(p, def, 0);
        }

        private static IntVec3 Center(Map map)
        {
            var cells = map.areaManager.Home.ActiveCells.ToList();
            if (cells.Count > 0)
            {
                int x = 0, z = 0;
                foreach (IntVec3 c in cells) { x += c.x; z += c.z; }
                IntVec3 mid = new IntVec3(x / cells.Count, 0, z / cells.Count);
                if (mid.InBounds(map)) return mid;
            }
            var ps = Holidays.Colonists(map);
            return ps.Count > 0 ? ps.RandomElement().Position : map.Center;
        }

        private static bool FreeOutdoor(IntVec3 c, Map map) =>
            c.InBounds(map) && c.Standable(map) && !c.Roofed(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null
            && c.GetFirstPawn(map) == null && !c.Fogged(map) && map.reachability.CanReachColony(c);

        private static bool Spot(Map map, IntVec3 near, int radius, out IntVec3 cell, Predicate<IntVec3> extra = null)
        {
            return CellFinder.TryFindRandomCellNear(near, map, radius, c => FreeOutdoor(c, map) && (extra == null || extra(c)), out cell)
                || CellFinder.TryFindRandomCellNear(near, map, radius * 2, c => FreeOutdoor(c, map), out cell);
        }

        private static Thing SpawnDeco(Map map, string def, IntVec3 cell)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null) return null;
            Thing t = ThingMaker.MakeThing(d);
            if (d.CanHaveFaction) t.SetFaction(Faction.OfPlayer);
            return GenSpawn.Spawn(t, cell, map);
        }

        private static Pawn Organizer(Map map) =>
            Holidays.Colonists(map).Where(p => !p.Downed && !p.InMentalState && !p.Drafted && p.Awake() && p.health.capacities.CanBeAwake)
                .RandomElementWithFallback();

        // a party at a spot (next to the decoration); falls back to the game's own party
        public static bool GatheringRunning(Map map) =>
            map.lordManager.lords.Any(l => l.LordJob is LordJob_Joinable_Gathering);

        private static bool Gather(Map map, IntVec3 spot)
        {
            if (GatheringRunning(map)) return false;
            Pawn org = Organizer(map);
            if (org == null) return false;
            try
            {
                if (!spot.IsValid || !spot.Standable(map))
                {
                    if (GatheringDefOf.Party.Worker.CanExecute(map, org)) return GatheringDefOf.Party.Worker.TryExecute(map, org);
                    return false;
                }
                var job = new LordJob_Joinable_Party(spot, org, GatheringDefOf.Party);
                LordMaker.MakeNewLord(org.Faction, job, map, job.OrganizerIsStartingPawn ? new[] { org } : null);
                return true;
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] gathering: " + e.Message); return false; }
        }

        private static IntVec3 Beside(Map map, Thing t)
        {
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(t).InRandomOrder())
                if (c.InBounds(map) && c.Standable(map)) return c;
            return t.Position;
        }

        private static void Letter(string key, LookTargets look, params NamedArgument[] args)
        {
            Find.LetterStack.ReceiveLetter(("SH_ActDoneTitle_" + key).Translate(), ("SH_ActDone_" + key).Translate(args), LetterDefOf.PositiveEvent, look);
        }

        private static List<Pawn> Couples(Map map)
        {
            var ps = Holidays.Colonists(map);
            return ps.Where(p => { Pawn l = LovePartnerRelationUtility.ExistingMostLikedLovePartner(p, false); return l != null && ps.Contains(l); }).ToList();
        }

        private static Faction Friendly(Func<Faction, bool> extra = null) =>
            Find.FactionManager.AllFactionsVisible.Where(f => !f.IsPlayer && !f.defeated && !f.temporary && f.def.humanlikeFaction
                && !f.HostileTo(Faction.OfPlayer) && (extra == null || extra(f))).RandomElementWithFallback();

        // ---------- the activities ----------

        // New Year: every colonist picks one skill (a passion, or his weakest) and learns it faster for a season
        private static void Resolutions(Map map)
        {
            HediffDef d = DefDatabase<HediffDef>.GetNamedSilentFail("SH_ResolutionSkill");
            if (d == null) return;
            int ticks = Mathf.Max(60000, YearClock.Days / 4 * 60000);
            var lines = new List<string>();
            foreach (Pawn p in Holidays.Colonists(map))
            {
                if (p.skills == null || p.health.hediffSet.HasHediff(d)) continue;
                var usable = p.skills.skills.Where(s => !s.TotallyDisabled).ToList();
                if (usable.Count == 0) continue;
                SkillRecord pick = usable.Where(s => s.passion == Passion.Major).RandomElementWithFallback()
                    ?? usable.Where(s => s.passion == Passion.Minor).RandomElementWithFallback()
                    ?? usable.OrderBy(s => s.Level).First();
                var h = (Hediff_SHResolution)HediffMaker.MakeHediff(d, p);
                h.skill = pick.def;
                p.health.AddHediff(h);
                HediffComp_Disappears c = h.TryGetComp<HediffComp_Disappears>();
                if (c != null) { c.ticksToDisappear = ticks; c.disappearsAfterTicks = ticks; }
                lines.Add(p.LabelShort + ": " + pick.def.LabelCap);
            }
            if (lines.Count > 0) Letter("NewYear", null, string.Join("\n", lines).Named("LIST"));
        }

        // Valentine's: a romantic dinner for every couple, and romance is in the air all day
        private static void Valentine(Map map)
        {
            var c = Couples(map);
            Thought(c, "SH_RomanticDinner");
            W.romanceUntil = Now + 60000;
            if (c.Count > 0) Gather(map, IntVec3.Invalid);
            Letter("Valentine", null, (c.Count / 2).Named("N"));
        }

        // Carnival: a costume parade with a feast; tomorrow a short fast
        private static void Carnival(Map map)
        {
            var ps = Holidays.Colonists(map);
            Thought(ps, "SH_CarnivalParade");
            IntVec3 spot = Spot(map, Center(map), 12, out IntVec3 c) ? c : IntVec3.Invalid;
            Gather(map, spot);
            W.Schedule(map, Now + 60000, "Fast", "");
            Letter("Carnival", null);
        }

        // Easter: painted eggs hidden around the colony – haulers find them, colonists eat them for fun
        private static void EggHunt(Map map)
        {
            ThingDef egg = DefDatabase<ThingDef>.GetNamedSilentFail("SH_EasterEgg");
            if (egg == null) return;
            int n = Mathf.Clamp(Holidays.Colonists(map).Count * 3, 6, 40), done = 0;
            IntVec3 mid = Center(map);
            for (int i = 0; i < n * 4 && done < n; i++)
            {
                if (!CellFinder.TryFindRandomCellNear(mid, map, 28, c => c.InBounds(map) && c.Standable(map) && c.GetFirstItem(map) == null
                    && !c.Fogged(map) && map.reachability.CanReachColony(c), out IntVec3 cell)) continue;
                GenSpawn.Spawn(ThingMaker.MakeThing(egg), cell, map);
                done++;
            }
            Letter("Easter", new LookTargets(mid, map), done.Named("N"));
        }

        // Witches' Night: a bonfire with a straw witch, the colony gathers around it
        private static void Witches(Map map)
        {
            if (!Spot(map, Center(map), 14, out IntVec3 cell, c => c.GetTerrain(map).affordances?.Contains(TerrainAffordanceDefOf.Medium) ?? true)) return;
            Thing pyre = SpawnDeco(map, "SH_WitchPyre", cell);
            if (pyre == null) return;
            Thought(Holidays.Colonists(map), "SH_WitchBurning");
            Gather(map, Beside(map, pyre));
            Letter("Witches", pyre);
        }

        // May Day: a maypole in the colony – guard it at night or the neighbours steal it; kisses for couples
        private static void MayDay(Map map)
        {
            Thought(Couples(map), "SH_MayKiss");
            if (!Spot(map, Center(map), 10, out IntVec3 cell)) return;
            Thing pole = SpawnDeco(map, "SH_Maypole", cell);
            if (pole == null) return;
            // the night watch: at 2 o'clock the next night
            int hour = GenLocalDate.HourOfDay(map);
            int wait = ((24 - hour) + 2) * 2500;
            W.Schedule(map, Now + wait, "Maypole", pole.thingIDNumber.ToString());
            Letter("MayDay", pole);
        }

        private static void MaypoleNight(Map map, string arg)
        {
            int.TryParse(arg, out int id);
            Thing pole = map.listerThings.ThingsOfDef(DefDatabase<ThingDef>.GetNamedSilentFail("SH_Maypole") ?? ThingDefOf.WoodLog).FirstOrDefault(t => t.thingIDNumber == id);
            if (pole == null || !pole.Spawned) return;
            var ps = Holidays.Colonists(map);
            bool guarded = ps.Any(p => !p.Downed && (p.Awake() || p.Drafted) && p.Position.DistanceTo(pole.Position) <= 12f);
            Faction thief = Friendly();
            if (!guarded && thief != null && Rand.Chance(0.5f))
            {
                pole.Destroy(DestroyMode.Vanish);
                Thought(ps, "SH_MaypoleStolen");
                Find.LetterStack.ReceiveLetter("SH_ActDoneTitle_MaypoleStolen".Translate(), "SH_ActDone_MaypoleStolen".Translate(thief.Name.Named("FACTION")), LetterDefOf.NeutralEvent);
            }
            else
            {
                Thought(ps, "SH_MaypoleKept");
                Messages.Message("SH_MaypoleKept".Translate(), pole, MessageTypeDefOf.PositiveEvent);
            }
        }

        // Children's Day: toys for the children – or, with no children, a day off for everybody
        private static void ChildrenDay(Map map)
        {
            var ps = Holidays.Colonists(map);
            var kids = ps.Where(p => ModsConfig.BiotechActive && p.DevelopmentalStage.Child()).ToList();
            if (kids.Count > 0)
            {
                Thought(kids, "SH_GotToys");
                foreach (Pawn k in kids) if (k.needs?.joy != null) k.needs.joy.CurLevel = Mathf.Min(1f, k.needs.joy.CurLevel + 0.4f);
                var toys = Holidays.Make("SH_Toy", kids.Count);
                IntVec3 c = DropCellFinder.TradeDropSpot(map);
                DropPodUtility.DropThingsNear(c, map, toys, 110, false, false, false, false);
                Letter("ChildrenDay", new LookTargets(c, map), kids.Count.Named("N"));
                return;
            }
            // a day off: the rest of today is recreation, the schedules come back at midnight
            int hour = GenLocalDate.HourOfDay(map);
            var saved = new List<string>();
            foreach (Pawn p in ps)
            {
                if (p.timetable == null) continue;
                var hours = new List<string>();
                for (int h = hour; h < 24; h++)
                {
                    hours.Add(h + "=" + p.timetable.GetAssignment(h).defName);
                    p.timetable.SetAssignment(h, TimeAssignmentDefOf.Joy);
                }
                saved.Add(p.thingIDNumber + ":" + string.Join(",", hours));
            }
            Thought(ps, "SH_DayOff");
            W.Schedule(map, Now + (24 - hour) * 2500 + 100, "Schedules", string.Join(";", saved));
            Letter("ChildrenDayOff", null);
        }

        private static void RestoreSchedules(Map map, string arg)
        {
            foreach (string part in arg.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string[] pp = part.Split(':');
                if (pp.Length < 2 || !int.TryParse(pp[0], out int id)) continue;
                Pawn p = map.mapPawns.AllPawns.FirstOrDefault(x => x.thingIDNumber == id);
                if (p?.timetable == null) continue;
                foreach (string hv in pp[1].Split(','))
                {
                    string[] kv = hv.Split('=');
                    if (kv.Length < 2 || !int.TryParse(kv[0], out int h)) continue;
                    TimeAssignmentDef d = DefDatabase<TimeAssignmentDef>.GetNamedSilentFail(kv[1]);
                    // only hours still set to recreation by us
                    if (d != null && p.timetable.GetAssignment(h) == TimeAssignmentDefOf.Joy) p.timetable.SetAssignment(h, d);
                }
            }
        }

        // Midsummer: medicinal herbs give more today, and a fire by the water
        private static void Solstice(Map map)
        {
            W.herbUntil = Now + 60000;
            IntVec3 mid = Center(map);
            bool water = Spot(map, mid, 30, out IntVec3 cell, c => GenAdjFast.AdjacentCells8Way(c).Any(a => a.InBounds(map) && a.GetTerrain(map).IsWater));
            if (!water && !Spot(map, mid, 14, out cell)) return;
            Thing fire = SpawnDeco(map, "SH_SolsticeFire", cell);
            if (fire == null) return;
            Thought(Holidays.Colonists(map), "SH_MidsummerFire");
            Gather(map, Beside(map, fire));
            Letter("Solstice", fire);
        }

        // Harvest Festival: a wreath and a feast; crops give more today
        private static void HarvestFeast(Map map)
        {
            W.cropUntil = Now + 60000;
            Thing w = null;
            if (Spot(map, Center(map), 10, out IntVec3 cell)) w = SpawnDeco(map, "SH_HarvestWreath", cell);
            Thought(Holidays.Colonists(map), "SH_HarvestFeast");
            Gather(map, w != null ? Beside(map, w) : IntVec3.Invalid);
            Letter("Harvest", w);
        }

        // Halloween: pumpkin lanterns, and the neighbours' kids come trick-or-treating
        private static void Halloween(Map map)
        {
            IntVec3 mid = Center(map);
            int lit = 0;
            for (int i = 0; i < 6; i++)
                if (Spot(map, mid, 16, out IntVec3 c) && SpawnDeco(map, "SH_PumpkinLantern", c) != null) lit++;
            Faction f = Friendly(x => x.def.pawnGroupMakers != null && x.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Peaceful));
            string extra = "";
            if (f != null)
            {
                try
                {
                    IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                    parms.faction = f;
                    parms.forced = true;
                    if (IncidentDefOf.VisitorGroup.Worker.TryExecute(parms))
                    {
                        int treats = TakeSweets(map, 4);
                        if (treats > 0)
                        {
                            f.TryAffectGoodwillWith(Faction.OfPlayer, Mathf.Min(8, treats * 2), false, false);
                            extra = "SH_TreatsGiven".Translate(treats, f.Name);
                        }
                        else extra = "SH_NoTreats".Translate(f.Name);
                    }
                }
                catch (Exception e) { Log.Warning("[Seasons & Holidays] trick-or-treat: " + e.Message); }
            }
            Find.LetterStack.ReceiveLetter("SH_ActDoneTitle_Halloween".Translate(), "SH_ActDone_Halloween".Translate(lit) + (extra.NullOrEmpty() ? "" : "\n\n" + extra), LetterDefOf.PositiveEvent, new LookTargets(mid, map));
        }

        // sweets from the colony's storage for the trick-or-treaters
        private static int TakeSweets(Map map, int want)
        {
            int got = 0;
            foreach (string def in new[] { "Chocolate", "SH_ChristmasCookies", "SH_EasterEgg", "MealFine", "MealSimple" })
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
                if (d == null) continue;
                foreach (Thing t in map.listerThings.ThingsOfDef(d).Where(x => x.IsInAnyStorage() && !x.IsForbidden(Faction.OfPlayer)).ToList())
                {
                    if (got >= want) return got;
                    int take = Mathf.Min(t.stackCount, want - got);
                    t.SplitOff(take).Destroy(DestroyMode.Vanish);
                    got += take;
                }
            }
            return got;
        }

        // Halloween night: now and then somebody sees a ghost (harmless fun)
        public static void HalloweenNight(Map map)
        {
            if (!On("Halloween") || !Rand.Chance(0.35f)) return;
            Pawn p = Holidays.Colonists(map).RandomElementWithFallback();
            if (p == null) return;
            Holidays.GiveThought(p, "SH_GhostSighting", 0);
            Find.LetterStack.ReceiveLetter("SH_GhostTitle".Translate(), "SH_Ghost".Translate(p.LabelShort), LetterDefOf.NeutralEvent, p);
        }

        // All Souls' Day: a candle beside every grave with somebody in it
        private static void GraveCandles(Map map)
        {
            int n = 0;
            foreach (Building_Grave g in map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>().Where(x => x.HasCorpse).ToList())
            {
                if (n >= 40) break;
                foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(g).InRandomOrder())
                    if (c.InBounds(map) && c.Standable(map) && c.GetEdifice(map) == null && c.GetFirstItem(map) == null)
                    {
                        if (SpawnDeco(map, "SH_GraveCandle", c) != null) n++;
                        break;
                    }
            }
            if (n > 0) Messages.Message("SH_CandlesLit".Translate(n), MessageTypeDefOf.PositiveEvent, false);
        }

        // Thanksgiving: a big feast, and a friendly faction sends food
        private static void Thanksgiving(Map map)
        {
            Thought(Holidays.Colonists(map), "SH_ThanksFeast");
            Gather(map, IntVec3.Invalid);
            Faction f = Friendly();
            if (f == null) { Letter("Thanksgiving", null, "SH_Neighbours".Translate().Named("FACTION")); return; }
            int n = Holidays.Colonists(map).Count;
            var food = new List<Thing>();
            food.AddRange(Holidays.Make("MealFine", Mathf.Clamp(n * 2, 4, 30)));
            food.AddRange(Holidays.Make("Pemmican", 40));
            IntVec3 c = DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(c, map, food, 110, false, false, false, false);
            Letter("Thanksgiving", new LookTargets(c, map), f.Name.Named("FACTION"));
        }

        // St. Nicholas with the angel and the devil: gifts for the good, coal for the naughty
        private static void Nicholas(Map map)
        {
            var ps = Holidays.Colonists(map);
            var naughty = ps.Where(p => p.needs.mood.thoughts.memories.GetFirstMemoryOfDef(ThoughtDefOf.Catharsis) != null).ToList();
            var good = ps.Where(p => !naughty.Contains(p)).ToList();
            var prisoners = map.mapPawns.PrisonersOfColonySpawned.ToList();
            Thought(good, "SH_NicholasGift");
            Thought(naughty, "SH_GotCoal");
            var things = new List<Thing>();
            things.AddRange(Holidays.Make("Beer", good.Count(p => !(ModsConfig.BiotechActive && p.DevelopmentalStage.Child()))));
            things.AddRange(Holidays.Make("SH_Coal", naughty.Count + prisoners.Count));
            if (things.Count > 0)
            {
                IntVec3 c = DropCellFinder.TradeDropSpot(map);
                DropPodUtility.DropThingsNear(c, map, things, 110, false, false, false, false);
            }
            string goodList = good.Count == 0 ? "–" : string.Join(", ", good.Select(p => p.LabelShort));
            string badList = naughty.Count + prisoners.Count == 0 ? "SH_NobodyNaughty".Translate().ToString() : string.Join(", ", naughty.Concat(prisoners).Select(p => p.LabelShort));
            Letter("StNicholas", null, goodList.Named("GOOD"), badList.Named("BAD"));
        }

        // Christmas: carols around the tree (the gifts lie under the tree, see Holidays.ChristmasGifts)
        private static void Carols(Map map)
        {
            Thing tree = ChristmasTree(map);
            Thought(Holidays.Colonists(map), "SH_Carols");
            Gather(map, tree != null ? Beside(map, tree) : IntVec3.Invalid);
            Letter(tree != null ? "Christmas" : "ChristmasNoTree", tree);
        }

        public static Thing ChristmasTree(Map map)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail("SH_ChristmasTree");
            return d == null ? null : map.listerThings.ThingsOfDef(d).FirstOrDefault(t => t.Spawned && t.Faction == Faction.OfPlayer);
        }

        // New Year's Eve: the countdown party is on the evening; tomorrow morning some have a hangover
        private static void NewYearsEve(Map map, bool manual)
        {
            if (manual) Gather(map, IntVec3.Invalid);
            int hour = GenLocalDate.HourOfDay(map);
            W.Schedule(map, Now + ((24 - hour) + 9) * 2500, "Hangover", "");
        }

        private static void Hangover(Map map)
        {
            HediffDef alc = HediffDefOf.AlcoholHigh;
            foreach (Pawn p in Holidays.Colonists(map))
            {
                bool drank = alc != null && p.health.hediffSet.HasHediff(alc);
                bool memory = p.needs.mood.thoughts.memories.Memories.Any(m => m.def.defName.IndexOf("Beer", StringComparison.OrdinalIgnoreCase) >= 0 || m.def.defName.IndexOf("Drunk", StringComparison.OrdinalIgnoreCase) >= 0);
                if (drank || memory || Rand.Chance(0.15f)) Holidays.GiveThought(p, "SH_Hangover", 0);
            }
        }

        // the holiday trader: a caravan with seasonal goods at Christmas and Easter
        private static void Trader(Map map)
        {
            TraderKindDef kind = DefDatabase<TraderKindDef>.GetNamedSilentFail("SH_HolidayTrader");
            Faction f = Friendly(x => x.def.caravanTraderKinds.Count > 0 && x.def.pawnGroupMakers != null && x.def.pawnGroupMakers.Any(g => g.kindDef == PawnGroupKindDefOf.Trader));
            if (kind == null || f == null) return;
            try
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(IncidentCategoryDefOf.Misc, map);
                parms.faction = f;
                parms.traderKind = kind;
                parms.forced = true;
                IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms);
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] holiday trader: " + e.Message); }
        }

        // ---------- the scheduled parts (every 250 ticks from the map component) ----------
        public static void Tick(Map map)
        {
            var w = W;
            if (w == null || w.pending.Count == 0) return;
            int now = Now;
            for (int i = w.pending.Count - 1; i >= 0; i--)
            {
                string[] p = w.pending[i].Split(new[] { '|' }, 4);
                if (p.Length < 4 || !int.TryParse(p[0], out int at)) { w.pending.RemoveAt(i); continue; }
                if (p[1] != map.uniqueID.ToString() || at > now) continue;
                w.pending.RemoveAt(i);
                try
                {
                    switch (p[2])
                    {
                        case "Fast": Thought(Holidays.Colonists(map), "SH_Fasting"); break;
                        case "Maypole": MaypoleNight(map, p[3]); break;
                        case "Schedules": RestoreSchedules(map, p[3]); break;
                        case "Hangover": Hangover(map); break;
                        case "Run": Run(map, p[3], true); break;
                    }
                }
                catch (Exception e) { Log.Warning("[Seasons & Holidays] " + p[2] + ": " + e.Message); }
            }
        }

        // ---------- patches: romance in the air, herbs and crops give more ----------
        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker_RomanceAttempt.RandomSelectionWeight)),
                    postfix: new HarmonyMethod(typeof(Activities), nameof(RomancePostfix)));
                h.Patch(AccessTools.Method(typeof(Plant), nameof(Plant.YieldNow)), postfix: new HarmonyMethod(typeof(Activities), nameof(YieldPostfix)));
                h.Patch(AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn)), prefix: new HarmonyMethod(typeof(Activities), nameof(LearnPrefix)));
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] activities patch: " + e.Message); }
        }

        public static void RomancePostfix(ref float __result)
        {
            var w = W;
            if (w != null && w.romanceUntil > Now) __result *= 2f;
        }

        public static void YieldPostfix(Plant __instance, ref int __result)
        {
            if (__result <= 0) return;
            var w = W;
            if (w == null) return;
            int now = Now;
            ThingDef prod = __instance.def.plant?.harvestedThingDef;
            if (prod == null) return;
            if (w.herbUntil > now && prod.IsMedicine) __result = Mathf.RoundToInt(__result * 1.5f);
            else if (w.cropUntil > now && __instance.def.plant.Sowable && !__instance.def.plant.IsTree && prod.IsNutritionGivingIngestible)
                __result = Mathf.RoundToInt(__result * 1.25f);
        }

        public static void LearnPrefix(SkillRecord __instance, Pawn ___pawn, ref float xp)
        {
            if (xp <= 0f || ___pawn?.health?.hediffSet == null) return;
            foreach (Hediff h in ___pawn.health.hediffSet.hediffs)
                if (h is Hediff_SHResolution r && r.skill == __instance.def) { xp *= 1.5f; return; }
        }
    }

    // the New Year's resolution: one skill learned faster
    public class Hediff_SHResolution : HediffWithComps
    {
        public SkillDef skill;

        public override string LabelInBrackets => skill != null ? skill.LabelCap.ToString() : base.LabelInBrackets;

        public override string Description => base.Description + (skill != null ? "\n\n" + "SH_ResolutionSkill".Translate(skill.LabelCap) : "");

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Defs.Look(ref skill, "skill");
        }
    }

    // the morning letter: "Celebrate!" or "Not this year"
    public class ChoiceLetter_SHActivity : ChoiceLetter
    {
        public Map map;
        public string holiday;

        public override bool CanDismissWithRightClick => true;

        public override IEnumerable<DiaOption> Choices
        {
            get
            {
                if (ArchivedOnly) { yield return Option_Close; yield break; }
                var go = new DiaOption("SH_ActGo".Translate())
                {
                    action = () =>
                    {
                        Activities.Run(map ?? Find.CurrentMap, holiday, false);
                        Find.LetterStack.RemoveLetter(this);
                    },
                    resolveTree = true
                };
                if (map == null || !Find.Maps.Contains(map)) go.Disable("SH_ActNoMap".Translate());
                yield return go;
                yield return new DiaOption("SH_ActSkip".Translate())
                {
                    action = () => Find.LetterStack.RemoveLetter(this),
                    resolveTree = true
                };
            }
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref map, "map");
            Scribe_Values.Look(ref holiday, "holiday");
        }
    }
}
