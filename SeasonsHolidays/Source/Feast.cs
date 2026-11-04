using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.5.0: activities for the world holidays (Lunar New Year, Holi, Diwali, Mid-Autumn, Day of the
    // Dead, Founders' Day, St. Patrick, Oktoberfest, St. Martin) and the two old tribal festivals
    // (spring equinox, Yule).
    // ---------------------------------------------------------------------------
    public static partial class Activities
    {
        public static readonly string[] PackIds =
        {
            "LunarNewYear", "Holi", "Diwali", "MidAutumn", "DayOfDead", "FoundersDay", "StPatrick", "Oktoberfest", "StMartin",
            "SpringEquinox", "WinterSolstice"
        };

        // the ones that start in the evening
        private static readonly HashSet<string> Evening = new HashSet<string> { "Witches", "Solstice", "MidAutumn", "StMartin", "WinterSolstice", "Diwali" };

        private static void RunPack(Map map, string id)
        {
            switch (id)
            {
                case "LunarNewYear": LunarNewYear(map); break;
                case "Holi": Holi(map); break;
                case "Diwali": Diwali(map); break;
                case "MidAutumn": MidAutumn(map); break;
                case "DayOfDead": DayOfDead(map); break;
                case "FoundersDay": FoundersDay(map); break;
                case "StPatrick": StPatrick(map); break;
                case "Oktoberfest": Oktoberfest(map); break;
                case "StMartin": StMartin(map); break;
                case "SpringEquinox": Equinox(map); break;
                case "WinterSolstice": Yule(map); break;
            }
        }

        private static List<Thing> Ring(Map map, string def, int n, int radius)
        {
            var l = new List<Thing>();
            IntVec3 mid = Center(map);
            for (int i = 0; i < n * 3 && l.Count < n; i++)
                if (Spot(map, mid, radius, out IntVec3 c))
                {
                    Thing t = SpawnDeco(map, def, c);
                    if (t != null) l.Add(t);
                }
            return l;
        }

        private static void DropNear(Map map, List<Thing> things, IntVec3 at)
        {
            if (things.Count == 0) return;
            if (!at.IsValid) at = DropCellFinder.TradeDropSpot(map);
            foreach (Thing t in things) GenPlace.TryPlaceThing(t, at, map, ThingPlaceMode.Near);
        }

        // Lunar New Year: red lanterns, and everybody gives a red envelope with a little silver to somebody
        private static void LunarNewYear(Map map)
        {
            var ps = Holidays.Colonists(map);
            var lanterns = Ring(map, "SH_RedLantern", 4, 10);
            var pairs = new List<string>();
            if (ps.Count >= 2)
            {
                var givers = ps.InRandomOrder().ToList();
                for (int i = 0; i < givers.Count; i++)
                {
                    Pawn to = givers[(i + 1) % givers.Count];
                    pairs.Add("SH_EnvelopePair".Translate(givers[i].LabelShort, to.LabelShort));
                }
            }
            Thought(ps, "SH_RedEnvelope");
            DropNear(map, Holidays.Make("Silver", 10 * Mathf.Max(1, ps.Count)), lanterns.Count > 0 ? lanterns[0].Position : IntVec3.Invalid);
            Gather(map, lanterns.Count > 0 ? Beside(map, lanterns[0]) : IntVec3.Invalid);
            Letter("LunarNewYear", lanterns.Count > 0 ? (LookTargets)lanterns[0] : null, (pairs.Count > 0 ? string.Join("\n", pairs) : "–").Named("LIST"));
        }

        // Holi: a colour fight – colourful for half a day, the clothes get a bit stained
        private static void Holi(Map map)
        {
            var ps = Holidays.Colonists(map);
            HediffDef col = DefDatabase<HediffDef>.GetNamedSilentFail("SH_Colourful");
            foreach (Pawn p in ps)
            {
                if (col != null && !p.health.hediffSet.HasHediff(col)) p.health.AddHediff(col);
                if (p.apparel != null)
                    foreach (Apparel a in p.apparel.WornApparel)
                        if (a.def.useHitPoints && a.HitPoints > a.MaxHitPoints / 2) a.HitPoints = Mathf.Max(1, a.HitPoints - Mathf.CeilToInt(a.MaxHitPoints * 0.04f));
            }
            Thought(ps, "SH_HoliColours");
            IntVec3 spot = Spot(map, Center(map), 12, out IntVec3 c) ? c : IntVec3.Invalid;
            Gather(map, spot);
            Letter("Holi", spot.IsValid ? new LookTargets(spot, map) : null);
        }

        // Diwali: little oil lamps around the colony and sweets for everybody
        private static void Diwali(Map map)
        {
            var ps = Holidays.Colonists(map);
            var lamps = Ring(map, "SH_DiyaLamp", 8, 14);
            DropNear(map, Holidays.Make("SH_DiwaliSweets", ps.Count * 2), lamps.Count > 0 ? lamps[0].Position : IntVec3.Invalid);
            Thought(ps, "SH_DiwaliLights");
            Gather(map, lamps.Count > 0 ? Beside(map, lamps[0]) : IntVec3.Invalid);
            Letter("Diwali", lamps.Count > 0 ? (LookTargets)lamps[0] : null, lamps.Count.Named("N"));
        }

        // Mid-Autumn: mooncakes and watching the moon together outside
        private static void MidAutumn(Map map)
        {
            var ps = Holidays.Colonists(map);
            IntVec3 spot = Spot(map, Center(map), 14, out IntVec3 c) ? c : IntVec3.Invalid;
            DropNear(map, Holidays.Make("SH_Mooncake", ps.Count), spot);
            Thought(ps, "SH_MoonWatching");
            Gather(map, spot);
            Letter("MidAutumn", spot.IsValid ? new LookTargets(spot, map) : null);
        }

        // Day of the Dead: an altar with marigolds and candles; everybody remembers one of our dead by name
        private static void DayOfDead(Map map)
        {
            var ps = Holidays.Colonists(map);
            Thing altar = Spot(map, Center(map), 10, out IntVec3 cell) ? SpawnDeco(map, "SH_Ofrenda", cell) : null;
            var dead = DeadColonists(map);
            ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail("SH_RememberedDead");
            var names = new HashSet<string>();
            if (def != null && dead.Count > 0)
                foreach (Pawn p in ps)
                {
                    Pawn who = dead.Where(d => d != p && p.relations != null && p.relations.FamilyByBlood.Contains(d)).RandomElementWithFallback() ?? dead.RandomElement();
                    var t = (Thought_Memory)ThoughtMaker.MakeThought(def);
                    p.needs.mood.thoughts.memories.TryGainMemory(t, who);
                    names.Add(who.LabelShort);
                }
            else Thought(ps, "SH_DayOfDeadAltar");
            if (altar != null) Gather(map, Beside(map, altar));
            Letter(dead.Count > 0 ? "DayOfDead" : "DayOfDeadNone", altar, string.Join(", ", names).Named("LIST"));
        }

        private static List<Pawn> DeadColonists(Map map)
        {
            var l = new List<Pawn>();
            foreach (Building_Grave g in map.listerBuildings.allBuildingsColonist.OfType<Building_Grave>())
            {
                Pawn d = g.Corpse?.InnerPawn;
                if (d != null && d.RaceProps.Humanlike && !l.Contains(d)) l.Add(d);
            }
            try
            {
                foreach (Pawn d in Find.WorldPawns.AllPawnsDead)
                    if (l.Count < 60 && d.RaceProps.Humanlike && d.Faction == Faction.OfPlayer && !l.Contains(d)) l.Add(d);
            }
            catch { }
            return l;
        }

        // Founders' Day: the best speaker gives a speech, then a feast
        private static void FoundersDay(Map map)
        {
            var ps = Holidays.Colonists(map);
            Pawn speaker = ps.Where(p => p.skills != null && !p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled && !p.Downed)
                .OrderByDescending(p => p.skills.GetSkill(SkillDefOf.Social).Level).FirstOrDefault();
            int lvl = speaker?.skills.GetSkill(SkillDefOf.Social).Level ?? 0;
            int stage = lvl >= 12 ? 2 : lvl >= 6 ? 1 : 0;
            foreach (Pawn p in ps) Holidays.GiveThought(p, "SH_FoundersSpeech", stage);
            Gather(map, IntVec3.Invalid);
            int days = Find.TickManager.TicksGame / 60000;
            Letter("FoundersDay", speaker, (speaker?.LabelShort ?? "–").Named("PAWN"), days.Named("DAYS"),
                ("SH_SpeechQ" + stage).Translate().Named("HOW"));
        }

        // St. Patrick: everything green, a party
        private static void StPatrick(Map map)
        {
            Thought(Holidays.Colonists(map), "SH_GreenParty");
            IntVec3 spot = Spot(map, Center(map), 12, out IntVec3 c) ? c : IntVec3.Invalid;
            Gather(map, spot);
            Letter("StPatrick", null);
        }

        private static TraitDef Tough => DefDatabase<TraitDef>.GetNamedSilentFail("Tough");

        // Oktoberfest: a beer contest – needs one beer from the storage per contestant
        private static void Oktoberfest(Map map)
        {
            var ps = Holidays.Colonists(map).Where(p => !(ModsConfig.BiotechActive && p.DevelopmentalStage.Child()) && !p.Downed
                && (p.story?.traits == null || !p.story.traits.HasTrait(TraitDefOf.DrugDesire, -1))).Take(8).ToList();
            ThingDef beer = DefDatabase<ThingDef>.GetNamedSilentFail("Beer");
            var stock = beer == null ? new List<Thing>() : map.listerThings.ThingsOfDef(beer).Where(t => t.IsInAnyStorage() && !t.IsForbidden(Faction.OfPlayer)).ToList();
            int have = stock.Sum(t => t.stackCount);
            if (ps.Count < 2 || have < ps.Count)
            {
                Letter("OktoberfestNoBeer", null, ps.Count.Named("N"));
                return;
            }
            int need = ps.Count;
            foreach (Thing t in stock)
            {
                if (need <= 0) break;
                int take = Mathf.Min(need, t.stackCount);
                t.SplitOff(take).Destroy(DestroyMode.Vanish);
                need -= take;
            }
            Pawn winner = ps.RandomElementByWeight(p => p.BodySize * (p.story?.traits != null && Tough != null && p.story.traits.HasTrait(Tough) ? 2f : 1f) + 0.2f);
            foreach (Pawn p in ps) Holidays.GiveThought(p, p == winner ? "SH_BeerChampion" : "SH_BeerContest", 0);
            Gather(map, IntVec3.Invalid);
            Letter("Oktoberfest", winner, winner.LabelShort.Named("PAWN"), ps.Count.Named("N"));
        }

        // St. Martin: a lantern walk and a goose dinner (raw meat from the storage becomes a feast)
        private static void StMartin(Map map)
        {
            var ps = Holidays.Colonists(map);
            var lanterns = Ring(map, "SH_PaperLantern", 6, 12);
            int need = 6 + ps.Count * 2, got = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver)
                .Where(x => x.def.IsWithinCategory(ThingCategoryDefOf.MeatRaw) && x.IsInAnyStorage() && !x.IsForbidden(Faction.OfPlayer)).ToList())
            {
                if (got >= need) break;
                int take = Mathf.Min(need - got, t.stackCount);
                t.SplitOff(take).Destroy(DestroyMode.Vanish);
                got += take;
            }
            bool goose = got >= need;
            if (goose) DropNear(map, Holidays.Make("MealFine", ps.Count), lanterns.Count > 0 ? lanterns[0].Position : IntVec3.Invalid);
            Thought(ps, goose ? "SH_MartinGoose" : "SH_MartinLanterns");
            Gather(map, lanterns.Count > 0 ? Beside(map, lanterns[0]) : IntVec3.Invalid);
            Letter(goose ? "StMartin" : "StMartinNoGoose", lanterns.Count > 0 ? (LookTargets)lanterns[0] : null);
        }

        // the spring equinox (tribal): blessing the fields
        private static void Equinox(Map map)
        {
            Thing w = Spot(map, Center(map), 10, out IntVec3 cell) ? SpawnDeco(map, "SH_HarvestWreath", cell) : null;
            Thought(Holidays.Colonists(map), "SH_EquinoxBlessing");
            Gather(map, w != null ? Beside(map, w) : IntVec3.Invalid);
            Letter("SpringEquinox", w);
        }

        // Yule (tribal): the longest night with a big fire
        private static void Yule(Map map)
        {
            Thing fire = Spot(map, Center(map), 14, out IntVec3 cell) ? SpawnDeco(map, "SH_SolsticeFire", cell) : null;
            Thought(Holidays.Colonists(map), "SH_YuleFire");
            Gather(map, fire != null ? Beside(map, fire) : IntVec3.Invalid);
            Letter("WinterSolstice", fire);
        }
    }

    // ---------------------------------------------------------------------------
    // 0.5.0: letters or – in quiet mode – only messages
    // ---------------------------------------------------------------------------
    public static class Notify
    {
        public static bool Quiet => SHMod.S != null && SHMod.S.quietMode;

        public static void Send(TaggedString label, TaggedString text, LetterDef def, LookTargets look = null)
        {
            if (!Quiet) { Find.LetterStack.ReceiveLetter(label, text, def, look); return; }
            MessageTypeDef mt = def == LetterDefOf.NegativeEvent || def == LetterDefOf.ThreatSmall ? MessageTypeDefOf.NegativeEvent
                : def == LetterDefOf.PositiveEvent ? MessageTypeDefOf.PositiveEvent : MessageTypeDefOf.NeutralEvent;
            string s = label + ": " + text.ToString().Replace("\n\n", " ").Replace("\n", " ");
            if (look != null && look.IsValid) Messages.Message(s, look, mt, true);
            else Messages.Message(s, mt, true);
        }
    }

    // ---------------------------------------------------------------------------
    // 0.5.0: holiday food – a mood bonus when eaten in its season
    // ---------------------------------------------------------------------------
    public class CompProperties_SHSeasonalFood : CompProperties
    {
        public List<int> months = new List<int>();   // 0 = January

        public CompProperties_SHSeasonalFood() { compClass = typeof(CompSHSeasonalFood); }
    }

    public class CompSHSeasonalFood : ThingComp
    {
        public CompProperties_SHSeasonalFood Props => (CompProperties_SHSeasonalFood)props;

        public override void PostIngested(Pawn ingester)
        {
            try
            {
                if (SHMod.S == null || !SHMod.S.seasonalFood || ingester?.needs?.mood == null) return;
                Map map = ingester.MapHeld ?? Find.CurrentMap;
                if (map == null) return;
                Months.Now(map, out int m, out _);
                if (Props.months.Contains(m)) Holidays.GiveThought(ingester, "SH_SeasonalTreat", 0);
            }
            catch { }
        }

        public override string CompInspectStringExtra()
        {
            if (Props.months.NullOrEmpty()) return null;
            return "SH_SeasonFoodInspect".Translate(string.Join(", ", Props.months.Select(Months.Name)));
        }
    }

    // decorations of a season ("pack it away afterwards")
    public class SHSeasonDeco : DefModExtension
    {
        public List<int> months = new List<int>();
    }

    [StaticConstructorOnStartup]
    public static class FeastStartup
    {
        static FeastStartup()
        {
            // the pumpkin pie: from pumpkins when a mod adds them, otherwise from corn and berries
            try
            {
                RecipeDef pie = DefDatabase<RecipeDef>.GetNamedSilentFail("SH_BakePumpkinPie");
                if (pie == null || pie.ingredients.NullOrEmpty()) return;
                var pumpkins = DefDatabase<ThingDef>.AllDefs.Where(d => !d.defName.StartsWith("SH_") && d.defName.IndexOf("Pumpkin", StringComparison.OrdinalIgnoreCase) >= 0
                    && d.category == ThingCategory.Item && d.IsNutritionGivingIngestible && d.ingestible != null
                    && (d.ingestible.foodType & FoodTypeFlags.VegetableOrFruit) != 0).ToList();
                if (pumpkins.Count == 0) return;
                foreach (ThingFilter f in new[] { pie.ingredients[0].filter, pie.fixedIngredientFilter, pie.defaultIngredientFilter })
                {
                    if (f == null) continue;
                    f.SetDisallowAll();
                    foreach (ThingDef d in pumpkins) f.SetAllow(d, true);
                }
                Log.Message("[Seasons & Holidays] pumpkin pie made from " + string.Join(", ", pumpkins.Select(d => d.defName)) + ".");
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] pumpkin pie: " + e.Message); }
        }
    }

    // looking forward to a holiday enjoyed last year
    public class Thought_SHLookingForward : Thought_Memory
    {
        public string holiday;

        public override string LabelCap
        {
            get
            {
                Holiday h = Holidays.All.FirstOrDefault(x => x.id == holiday);
                return CurStage.label.Formatted(h != null ? h.Label : holiday ?? "?").CapitalizeFirst();
            }
        }

        public override bool GroupsWith(Thought other) => other is Thought_SHLookingForward o && o.holiday == holiday && base.GroupsWith(other);

        public override bool TryMergeWithExistingMemory(out bool showBubble)
        {
            // one per holiday – a second one refreshes the first
            var mem = pawn.needs.mood.thoughts.memories.Memories;
            foreach (Thought_Memory m in mem)
                if (m is Thought_SHLookingForward o && o.holiday == holiday) { o.Renew(); showBubble = false; return true; }
            showBubble = true;
            return false;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref holiday, "holiday");
        }
    }
}
