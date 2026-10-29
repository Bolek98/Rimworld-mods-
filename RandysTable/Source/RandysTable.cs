using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandysTable
{
    // ---------------------------------------------------------------------------
    // Randy's Table – chaos storytellers that still let you see what is coming.
    // 0.1: "The Dice" – every day a d6 sets the mood of the day (calm … mad); an alert
    // shows the roll and today's odds. "Diary" – short comments from the storyteller
    // on events (works with any storyteller).
    // ---------------------------------------------------------------------------
    public class RTSettings : ModSettings
    {
        public bool recommended = true;
        public bool diary = true;            // comments on events
        public bool diaryAll = true;         // also with other storytellers (Randy, Cassandra …)
        public int diaryChance = 35;         // % of events that get a comment
        public bool diceAlert = true;        // the "die: 5 – hard day" alert with today's odds
        public bool diceLetter = false;      // the roll as a letter instead of a short message
        public bool heavyOnlySix = true;     // 0.1.5: infestations, mech clusters … only on a mad day

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref diary, "diary", true);
            Scribe_Values.Look(ref diaryAll, "diaryAll", true);
            Scribe_Values.Look(ref diaryChance, "diaryChance", 35);
            Scribe_Values.Look(ref diceAlert, "diceAlert", true);
            Scribe_Values.Look(ref diceLetter, "diceLetter", false);
            Scribe_Values.Look(ref heavyOnlySix, "heavyOnlySix", true);
        }
    }

    public class RTMod : Mod
    {
        public const string Version = "0.1.5";
        public static RTSettings S;
        private static List<string> recBullets;

        public RTMod(ModContentPack content) : base(content)
        {
            S = GetSettings<RTSettings>();
            MUI.Init(new RTSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("bolek98.randystable");
            try { MUI.PatchWindow(h, typeof(RTMod)); } catch { }
            try { h.PatchAll(); }
            catch (Exception e) { Log.Error("[Randy's Table] patching failed: " + e); }
            Log.Message("[Randy's Table] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Randy's Table";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "RT_Rec1".Translate(), "RT_Rec2".Translate(), "RT_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                MUI.Section(l, "RT_SecDice".Translate());
                Check(l, "RT_DiceAlert", ref S.diceAlert, "diceAlert");
                Check(l, "RT_DiceLetter", ref S.diceLetter, "diceLetter");
                Check(l, "RT_HeavySix", ref S.heavyOnlySix, "heavyOnlySix");
                MUI.Section(l, "RT_SecDiary".Translate());
                Check(l, "RT_Diary", ref S.diary, "diary");
                Check(l, "RT_DiaryAll", ref S.diaryAll, "diaryAll");
                S.diaryChance = Mathf.RoundToInt(MUI.Slider(l, "RT_DiaryChance".Translate(S.diaryChance), S.diaryChance, 5f, 100f, "RT_DiaryChance_Desc".Translate(), S, "diaryChance"));
                l.End();
            }
            finally { MUI.EndLock(rest); }
        }
    }

    // Marks our storytellers; "kind" says which one.
    public class RTStoryteller : DefModExtension
    {
        public string kind = "dice";
    }

    // ---------------------------------------------------------------------------
    // The die: one roll per in-game day.
    // ---------------------------------------------------------------------------
    public sealed class Mood
    {
        public float mtb = 1f;          // multiplies the mean time between events (higher = fewer)
        public float big = 1f, small = 1f, misc = 1f, trade = 1f, visitors = 1f, chunks = 1f;
        public float pointsMin = 1f, pointsMax = 1f;
        public bool forceOverdueThreat = true;

        public float Weight(IncidentCategoryDef cat)
        {
            if (cat == IncidentCategoryDefOf.ThreatBig) return big;
            if (cat == IncidentCategoryDefOf.ThreatSmall) return small;
            if (cat == IncidentCategoryDefOf.Misc) return misc;
            if (cat?.defName == "OrbitalVisitor") return trade;
            if (cat?.defName == "FactionArrival") return visitors;
            if (cat?.defName == "ShipChunkDrop") return chunks;
            return 1f;
        }

        public static Mood For(int roll, int seed)
        {
            switch (roll)
            {
                case 1: return new Mood { mtb = 2.5f, big = 0f, small = 0.25f, visitors = 1.4f, pointsMin = 0.6f, pointsMax = 0.8f, forceOverdueThreat = false };
                case 2: return new Mood { mtb = 1.5f, big = 0.35f, small = 0.6f, misc = 1.3f, visitors = 1.5f, trade = 1.3f, pointsMin = 0.7f, pointsMax = 1f, forceOverdueThreat = false };
                case 4: return new Mood { mtb = 1f, big = 0.5f, small = 0.6f, trade = 2.5f, visitors = 2f, chunks = 3f, pointsMin = 0.6f, pointsMax = 1.1f };
                case 5: return new Mood { mtb = 0.75f, big = 2f, small = 1.8f, misc = 0.8f, pointsMin = 1f, pointsMax = 1.5f };
                case 6:
                    // mad day: every category gets its own random weight (same for the whole day)
                    var r = new System.Random(seed);
                    float R() => 0.3f + (float)r.NextDouble() * 2.2f;
                    return new Mood { mtb = 0.45f, big = R(), small = R(), misc = R(), trade = R(), visitors = R(), chunks = R(), pointsMin = 0.6f, pointsMax = 1.8f };
                default: return new Mood { pointsMin = 0.5f, pointsMax = 1.5f };
            }
        }
    }

    public sealed class DiceState : GameComponent
    {
        public static DiceState I;
        public int day = -1;
        public int roll = 3;
        public int rerollAt = -1;      // 0.1.4: a 7 – the dice are thrown again at this tick

        public DiceState(Game game) { I = this; }

        public override void FinalizeInit() { I = this; }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref day, "day", -1);
            Scribe_Values.Look(ref roll, "roll", 3);
            Scribe_Values.Look(ref rerollAt, "rerollAt", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) I = this;
        }

        public static bool DiceActive => Find.Storyteller?.def?.GetModExtension<RTStoryteller>()?.kind == "dice";

        public Mood Mood => Mood.For(roll, day * 7919 + roll);

        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 250 != 17 || !DiceActive)
                return;
            if (GenDate.DaysPassed != day)
                Roll(true);
            else if (rerollAt > 0 && Find.TickManager.TicksGame >= rerollAt)
            {
                rerollAt = -1;
                Face(Rand.RangeInclusive(1, 6), true);
            }
        }

        public void Ensure()
        {
            if (GenDate.DaysPassed != day)
                Roll(true);
        }

        public void Roll(bool announce)
        {
            day = GenDate.DaysPassed;
            rerollAt = -1;
            // 0.1.4: a seven-sided die – 7 means "throw again" a few hours later (an ordinary day until then)
            Face(Rand.RangeInclusive(1, 7), announce);
        }

        private void Face(int value, bool announce)
        {
            roll = value;
            if (roll == 7)
                rerollAt = Find.TickManager.TicksGame + Rand.Range(5000, 20000);
            if (!announce || Find.TickManager.TicksGame < 2500)
                return;
            try
            {
                string title = "RT_RollTitle".Translate(roll, ("RT_Mood" + roll).Translate());
                string line = Diary.Pick("RT_RollSay" + roll);
                string text = title + (line != null ? "\n\n„" + line + "“" : "") + "\n\n" + Forecast.Text();
                if (RTMod.S.diceLetter)
                    Find.LetterStack.ReceiveLetter("RT_RollLetter".Translate(roll), text,
                        roll >= 5 ? LetterDefOf.ThreatSmall : roll == 4 ? LetterDefOf.PositiveEvent : LetterDefOf.NeutralEvent);
                else
                    Messages.Message(title + (line != null ? " – „" + line + "“" : ""), roll >= 5 ? MessageTypeDefOf.ThreatSmall : MessageTypeDefOf.NeutralEvent, false);
                if (roll == 4)
                    Gift();
                // 0.1.2: the roll always shows – a hard day brings at least one threat, a mad day two
                // random events, both at a random time of the day (on top of the usual events)
                if (roll == 5)
                    Surprise(1, Rand.Chance(0.6f) ? new[] { "ThreatBig" } : new[] { "ThreatSmall" });
                if (roll == 6)
                    Surprise(2, "Misc", "Misc", "Misc", "ThreatSmall", "ThreatBig", "OrbitalVisitor", "FactionArrival", "FactionArrival", "ShipChunkDrop");
                // 0.1.3: quieter days now and then bring something too, and the roll sets the weather
                if (roll == 2 && Rand.Chance(0.5f))
                    Surprise(1, "Misc");
                if (roll == 3 && Rand.Chance(0.5f))
                    Surprise(1, "Misc", "Misc", "ThreatSmall", "OrbitalVisitor", "FactionArrival");
                if (roll != 7)
                    Weather();
            }
            catch (Exception e)
            {
                Log.Warning("[Randy's Table] roll: " + e.Message);
            }
        }

        private static readonly AccessTools.FieldRef<WeatherDecider, int> weatherLeft = AccessTools.FieldRefAccess<WeatherDecider, int>("curWeatherDuration");

        // 1 and 4: fine weather; 5: the worst the biome has; 6: anything (also weather from mods);
        // 2 and 3: now and then a change of weather the biome knows
        private void Weather()
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map?.weatherManager == null || map.weatherDecider == null)
                return;
            if ((roll == 2 || roll == 3) && !Rand.Chance(0.3f))
                return;
            float temp = map.mapTemperature.OutdoorTemp;
            bool Ok(WeatherDef w) => w != null && w.temperatureRange.Includes(temp) && w.minMonolithLevel <= 0 && w != map.weatherManager.curWeather;
            var biome = map.Biome.baseWeatherCommonalities.Where(c => c.commonality > 0f && Ok(c.weather)).Select(c => c.weather).Distinct().ToList();
            WeatherDef pick = null;
            if (roll == 1 || roll == 4)
                pick = biome.Where(w => !w.isBad && w.rainRate < 0.1f).OrderByDescending(w => (int)w.favorability).FirstOrDefault();
            else if (roll == 5)
                pick = biome.Where(w => w.isBad || (int)w.favorability <= 1).InRandomOrder().FirstOrDefault()
                    ?? biome.OrderBy(w => (int)w.favorability).FirstOrDefault();
            else if (roll == 6)
            {
                var any = DefDatabase<WeatherDef>.AllDefsListForReading.Where(w => Ok(w) && w.canOccurAsRandomForcedEvent && !w.doToxicBuildup).ToList();
                pick = any.RandomElementWithFallback();
            }
            else
                pick = biome.RandomElementWithFallback();
            if (pick == null)
                return;
            map.weatherManager.TransitionTo(pick);
            try { weatherLeft(map.weatherDecider) = Rand.Range(20000, 45000); } catch { }
        }

        private void Surprise(int count, params string[] cats)
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null)
                return;
            int queued = 0;
            for (int tries = 0; tries < 12 && queued < count; tries++)
            {
                IncidentCategoryDef cat = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail(cats.RandomElement());
                if (cat == null)
                    continue;
                IncidentParms probe = StorytellerUtility.DefaultParmsNow(cat, map);
                var defs = DefDatabase<IncidentDef>.AllDefsListForReading
                    .Where(d => d.category == cat && d.baseChance > 0f && d.TargetAllowed(map) && Find.Storyteller.def != null)
                    .ToList();
                IncidentDef pick = null;
                foreach (IncidentDef d in defs.InRandomOrder())
                {
                    try { if (d.Worker.CanFireNow(StorytellerUtility.DefaultParmsNow(cat, map))) { pick = d; break; } }
                    catch { }
                }
                if (pick == null)
                    continue;
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(cat, map);
                if (parms.points >= 0f)
                    parms.points *= Rand.Range(Mood.pointsMin, Mood.pointsMax);
                Find.Storyteller.incidentQueue.Add(pick, Find.TickManager.TicksGame + Rand.Range(2500, 50000), parms, 5000);
                queued++;
            }
        }

        // a generous day brings one good thing somewhere during the day
        private static readonly string[] Gifts = { "ResourcePodCrash", "TraderCaravanArrival", "WandererJoin", "OrbitalTraderArrival", "FarmAnimalsWanderIn", "SelfTame" };

        private void Gift()
        {
            Map map = Find.AnyPlayerHomeMap;
            if (map == null)
                return;
            var defs = Gifts.Select(n => DefDatabase<IncidentDef>.GetNamedSilentFail(n)).Where(d => d != null).ToList();
            foreach (IncidentDef def in defs.InRandomOrder())
            {
                IncidentParms parms = StorytellerUtility.DefaultParmsNow(def.category, map);
                if (!def.Worker.CanFireNow(parms))
                    continue;
                // 0.1.1: CanFireNow fills in faction, trader and spawn spot for *now*; the gift comes
                // hours later, so it gets fresh parms and the game picks those again when it fires
                IncidentParms later = StorytellerUtility.DefaultParmsNow(def.category, map);
                Find.Storyteller.incidentQueue.Add(def, Find.TickManager.TicksGame + Rand.Range(2500, 40000), later, 5000);
                return;
            }
        }
    }

    // How likely things are today – the part Randy never tells you.
    public static class Forecast
    {
        public static IncidentCategoryDef Cat(string name) => DefDatabase<IncidentCategoryDef>.GetNamedSilentFail(name);

        public static StorytellerCompProperties_Dice Props =>
            Find.Storyteller?.def?.comps?.OfType<StorytellerCompProperties_Dice>().FirstOrDefault();

        public static float EventsPerDay(Mood m, StorytellerCompProperties_Dice p) => 1f / Mathf.Max(0.1f, p.mtbDays * m.mtb);

        public static float Share(Mood m, StorytellerCompProperties_Dice p, IncidentCategoryDef cat)
        {
            float total = 0f, mine = 0f;
            foreach (IncidentCategoryEntry e in p.categoryWeights)
            {
                float w = e.weight * m.Weight(e.category);
                total += w;
                if (e.category == cat)
                    mine += w;
            }
            return total <= 0f ? 0f : mine / total;
        }

        // chance of at least one event of this kind today (Poisson)
        public static float ChanceToday(Mood m, StorytellerCompProperties_Dice p, IncidentCategoryDef cat) =>
            1f - Mathf.Exp(-EventsPerDay(m, p) * Share(m, p, cat));

        private static string Level(float c) =>
            (c < 0.08f ? "RT_LvlVeryLow" : c < 0.2f ? "RT_LvlLow" : c < 0.4f ? "RT_LvlMid" : c < 0.6f ? "RT_LvlHigh" : "RT_LvlVeryHigh").Translate()
            + " (" + c.ToStringPercent("0") + ")";

        public static string Text()
        {
            DiceState d = DiceState.I;
            StorytellerCompProperties_Dice p = Props;
            if (d == null || p == null)
                return "";
            Mood m = d.Mood;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("RT_FcEvents".Translate(EventsPerDay(m, p).ToString("0.0")));
            sb.AppendLine("RT_FcBig".Translate(Level(m.big <= 0f ? 0f : ChanceToday(m, p, IncidentCategoryDefOf.ThreatBig))));
            sb.AppendLine("RT_FcSmall".Translate(Level(ChanceToday(m, p, IncidentCategoryDefOf.ThreatSmall))));
            sb.AppendLine("RT_FcGood".Translate(Level(1f - (1f - ChanceToday(m, p, Cat("OrbitalVisitor"))) * (1f - ChanceToday(m, p, Cat("FactionArrival"))))));
            sb.AppendLine("RT_FcStrength".Translate(Mathf.RoundToInt(m.pointsMin * 100), Mathf.RoundToInt(m.pointsMax * 100)));
            if (!m.forceOverdueThreat)
                sb.AppendLine("RT_FcNoForced".Translate());
            if (d.roll == 4)
                sb.AppendLine("RT_FcGift".Translate());
            int ticksLeft = d.rerollAt > 0 ? d.rerollAt - Find.TickManager.TicksGame : 60000 - Find.TickManager.TicksGame % 60000;
            sb.Append("RT_FcNext".Translate(ticksLeft.ToStringTicksToPeriod()));
            return sb.ToString();
        }
    }

    public class Alert_DiceDay : Alert
    {
        public Alert_DiceDay()
        {
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel()
        {
            DiceState d = DiceState.I;
            return d == null ? "" : "RT_AlertLabel".Translate(d.roll, ("RT_Mood" + d.roll).Translate());
        }

        public override TaggedString GetExplanation()
        {
            DiceState d = DiceState.I;
            if (d == null)
                return "";
            return ("RT_MoodDesc" + d.roll).Translate() + "\n\n" + Forecast.Text();
        }

        public override AlertReport GetReport()
        {
            if (RTMod.S == null || !RTMod.S.diceAlert || DiceState.I == null || !DiceState.DiceActive || DiceState.I.day < 0)
                return false;
            return true;
        }
    }

    public class StorytellerCompProperties_Dice : StorytellerCompProperties_RandomMain
    {
        public StorytellerCompProperties_Dice()
        {
            compClass = typeof(StorytellerComp_Dice);
        }
    }

    // Randy's main event maker, with today's mood applied to how often things happen,
    // which kinds of events come and how strong threats are.
    public class StorytellerComp_Dice : StorytellerComp
    {
        private StorytellerCompProperties_Dice Props => (StorytellerCompProperties_Dice)props;

        private static Mood Today
        {
            get
            {
                DiceState d = DiceState.I;
                if (d == null)
                    return Mood.For(3, 0);
                d.Ensure();
                return d.Mood;
            }
        }

        public override IEnumerable<FiringIncident> MakeIntervalIncidents(IIncidentTarget target)
        {
            Mood m = Today;
            float mtb = Props.mtbDays * m.mtb;
            bool space = target.Tile.Valid && target.Tile.LayerDef != null && target.Tile.LayerDef.isSpace;
            if (space)
                mtb *= Props.spaceMtbDayFactor;
            if (!Rand.MTBEventOccurs(mtb, 60000f, 1000f))
                yield break;
            if (space && GenTicks.TicksGame - Find.Storyteller.LastIncidentTick < GenDate.DaysToTicks(Props.spaceMinSpacingDays))
                yield break;
            bool beacon = target.IncidentTargetTags().Contains(IncidentTargetTagDefOf.Map_RaidBeacon);
            var skip = new List<IncidentCategoryDef>();
            do
            {
                IncidentCategoryDef cat = Choose(target, skip, m);
                if (cat == null)
                    yield break;
                IncidentParms parms = GenerateParms(cat, target);
                if (TrySelectRandomIncident(UsableIncidentsInCategory(cat, parms), out IncidentDef found, target))
                {
                    if (!(Props.skipThreatBigIfRaidBeacon && beacon) || found.category != IncidentCategoryDefOf.ThreatBig)
                        yield return new FiringIncident(found, this, parms);
                    break;
                }
                skip.Add(cat);
            }
            while (skip.Count < Props.categoryWeights.Count);
        }

        private IncidentCategoryDef Choose(IIncidentTarget target, List<IncidentCategoryDef> skip, Mood m)
        {
            if (m.forceOverdueThreat && !skip.Contains(IncidentCategoryDefOf.ThreatBig)
                && Find.TickManager.TicksGame - target.StoryState.LastThreatBigTick > 60000f * Props.maxThreatBigIntervalDays)
                return IncidentCategoryDefOf.ThreatBig;
            var options = Props.categoryWeights.Where(c => !skip.Contains(c.category) && c.weight * m.Weight(c.category) > 0f).ToList();
            if (options.Count == 0)
                return null;
            return options.RandomElementByWeight(c => c.weight * m.Weight(c.category)).category;
        }

        public override IncidentParms GenerateParms(IncidentCategoryDef incCat, IIncidentTarget target)
        {
            IncidentParms parms = StorytellerUtility.DefaultParmsNow(incCat, target);
            if (parms.points >= 0f)
            {
                Mood m = Today;
                parms.points *= Rand.Range(m.pointsMin, m.pointsMax);
            }
            return parms;
        }
    }

    // 0.1.5: with Dice the Gambler, the heaviest threats (infestations, mech clusters, crashed
    // ship parts) come only on a mad day (6). Forced incidents (quests, dev mode) are not touched.
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
    public static class Patch_HeavyOnlySix
    {
        private static readonly HashSet<string> heavy = new HashSet<string>
        {
            "Infestation", "MechCluster", "PsychicEmanatorShipPartCrash", "DefoliatorShipPartCrash"
        };

        public static bool IsHeavy(IncidentDef d)
        {
            if (d == null) return false;
            if (heavy.Contains(d.defName)) return true;
            string n = d.defName;
            return n.IndexOf("Infestation", StringComparison.OrdinalIgnoreCase) >= 0 && n.IndexOf("DeepDrill", StringComparison.OrdinalIgnoreCase) < 0;
        }

        public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            try
            {
                if (!__result || parms == null || parms.forced || RTMod.S == null || !RTMod.S.heavyOnlySix) return;
                DiceState d = DiceState.I;
                if (d == null || !DiceState.DiceActive || d.roll == 6) return;
                if (IsHeavy(__instance.def)) __result = false;
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------------
    // Diary: the storyteller comments on some events (any storyteller).
    // ---------------------------------------------------------------------------
    public static class Diary
    {
        private static int lastTick = -999999;

        public static string Pick(string prefix)
        {
            var found = new List<string>();
            for (int i = 1; i <= 12; i++)
            {
                string key = prefix + "_" + i;
                if (key.CanTranslate())
                    found.Add(key.Translate());
            }
            return found.Count == 0 ? null : found.RandomElement();
        }

        public static string Group(IncidentDef def)
        {
            IncidentCategoryDef c = def.category;
            if (c == null) return null;
            switch (def.defName)
            {
                case "RaidEnemy": return "Raid";
                case "ManhunterPack": return "Manhunter";
                case "Infestation": return "Infestation";
                case "ResourcePodCrash": return "Pod";
                case "WandererJoin": return "Wanderer";
                case "Eclipse": case "SolarFlare": return "Dark";
                case "ToxicFallout": case "VolcanicWinter": case "HeatWave": case "ColdSnap": return "Weather";
            }
            if (c == IncidentCategoryDefOf.ThreatBig) return "Big";
            if (c == IncidentCategoryDefOf.ThreatSmall) return "Small";
            if (c.defName == "OrbitalVisitor") return "Trade";
            if (c.defName == "FactionArrival") return "Visitors";
            if (c.defName == "ShipChunkDrop") return "Chunk";
            if (c.defName == "DiseaseHuman" || c.defName == "DiseaseAnimal") return "Disease";
            if (c == IncidentCategoryDefOf.Misc) return "Misc";
            return null;
        }

        public static void After(IncidentWorker worker, IncidentParms parms)
        {
            RTSettings s = RTMod.S;
            if (s == null || !s.diary || worker?.def == null || Find.Storyteller?.def == null)
                return;
            if (!s.diaryAll && Find.Storyteller.def.GetModExtension<RTStoryteller>() == null)
                return;
            if (parms?.target != null && !(parms.target is Map m && m.IsPlayerHome))
                return;
            int now = Find.TickManager.TicksGame;
            if (now - lastTick < 15000 || !Rand.Chance(s.diaryChance / 100f))
                return;
            string group = Group(worker.def);
            string line = group == null ? null : (Pick("RT_D_" + group) ?? Pick("RT_D_Misc"));
            if (line == null)
                return;
            lastTick = now;
            Messages.Message("„" + line + "“ – " + Find.Storyteller.def.LabelCap, MessageTypeDefOf.SilentInput, false);
        }
    }

    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
    public static class Patch_Diary
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, bool __result)
        {
            if (!__result)
                return;
            try
            {
                Diary.After(__instance, parms);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Randy's Table] diary: " + e.Message, 0x52d1a7);
            }
        }
    }
}
