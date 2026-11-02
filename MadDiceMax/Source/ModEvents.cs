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
    // 0.6.0: events from other mods follow the die.
    // Every event another mod adds gets a kind – calm, good, ordinary, threat or mad. Well-known
    // event mods are sorted by hand (★), everything else by its category, letter and name.
    // The roll of the day then makes each kind more or less likely, and on calm and generous days
    // modded threats don't come at all. Everything is looked up by defName: a missing mod is
    // simply not there, nothing depends on it.
    // ---------------------------------------------------------------------------
    public enum MKind { Calm, Good, Ordinary, Threat, Mad }

    public static class ModEvents
    {
        // weight factors by kind and face 1..6 (7 = an ordinary day until the next throw)
        private static readonly float[,] factors =
        {
            //          1     2     3     4     5     6
            /*Calm*/  { 2.0f, 2.0f, 1.0f, 1.2f, 0.5f, 0.7f },
            /*Good*/  { 1.5f, 1.5f, 1.0f, 2.5f, 0.3f, 1.0f },
            /*Ord.*/  { 0.8f, 1.0f, 1.5f, 1.0f, 1.0f, 1.2f },
            /*Threat*/{ 0f,   0.4f, 1.0f, 0f,   2.0f, 1.5f },
            /*Mad*/   { 0f,   0f,   0.3f, 0f,   0.8f, 3.0f },
        };

        private const MKind C = MKind.Calm, G = MKind.Good, O = MKind.Ordinary, T = MKind.Threat, M = MKind.Mad;

        // hand-sorted events of mods that bring their own events
        private static readonly Dictionary<string, MKind> table = new Dictionary<string, MKind>
        {
            // Vanilla Events Expanded
            { "VEE_CropSprout", G }, { "VEE_AnimalPodCrash", G }, { "VEE_ShuttleCrash", G }, { "VEE_Cargopodsweapons", G },
            { "VEE_Cargopodsapparel", G }, { "VEE_CaravanAnimalsWanderIn", G }, { "VEE_BattleAnimalsWanderIn", G },
            { "VEE_Crashlanded", G }, { "VEE_PsychicStimulation", G },
            { "VEE_HuntingParty", O }, { "VEE_WildMenWanderIn", O }, { "VEE_MeteoriteShower", O }, { "VEE_SpaceBattle", O },
            { "VEE_Drought", T }, { "VEE_ManhunterSingleArrival", T }, { "VEE_WandererJoinsTraitor", T },
            { "VEE_PsychicOverdrive", T }, { "VEE_PsychicHum", T }, { "VEE_VisitorGroupRaid", T },
            { "VEE_Earthquake", M }, { "RaidEnemyPurple", M }, { "ManhunterPackPurple", M }, { "InfestationPurple", M },
            { "AnimalInsanityMassPurple", M }, { "VEE_Whiteout", M }, { "VEE_WhiteoutRefugees", M }, { "VEE_Scorch", M },
            { "VEE_LongNight", M }, { "VEE_PsychicBloom", M }, { "VEE_PsychicRain", M },
            // Leo – Events
            { "LE_GauranlenBloom", G }, { "LE_AnimaBloom", G }, { "LE_BlackMarket", G }, { "LE_Gift", G }, { "LE_MassInspiration", G },
            { "LE_NutrientRain", G }, { "LE_RebelPlanExposed", G }, { "LE_ResearchBreakthrough", G }, { "LE_SilverShower", G },
            { "LE_SuddenEpiphany", G }, { "LE_UnexpectedMineralSeams", G }, { "LE_VegetativeSpurt", G }, { "LE_AtmosphericExcitant", G },
            { "LE_GravityLow", G }, { "LE_NutrientSurge", G }, { "LE_DielectricResonance", G }, { "LE_ArtisticRenaissance", G },
            { "LE_FavorableReputation", G }, { "LE_MagneticMiracle", G }, { "LE_PsychicLink", G },
            { "LE_PsychicCalm", C }, { "LE_ForceWeather_Clear", C }, { "LE_ForceWeather_Fog", C }, { "LE_ForceWeather_Rain", C },
            { "LE_ForceWeather_SnowGentle", C }, { "LE_ForceWeather_Windy", C }, { "LE_ForceWeather_Overcast", C },
            { "LE_WashedUpFlotsam", C },
            { "LE_InfrasonicTremor", O }, { "LE_DissipationEMP", O }, { "LE_SubspaceNoise", O }, { "LE_MechanoidOverclock", O },
            { "LE_MechanoidThrottling", O }, { "LE_PsychicEcho", O }, { "LE_PowerTrip", O }, { "LE_PredatorPasses", O },
            { "LE_BiomeExpansion", O }, { "LE_SupplyChainShortage", O }, { "LE_ForceWeather_GrayPall", O },
            { "LE_FeralNeurotoxin", T }, { "LE_MechaniteCloud", T }, { "LE_PsychicDeadzone", T }, { "LE_DimensionalJitter", T },
            { "LE_EmberFallout", T }, { "LE_DrySpell", T }, { "LE_PsychicAggression", T }, { "LE_CargoPodsWastepack", T },
            { "LE_FrayedWires", T }, { "LE_MedicineSpoiled", T }, { "LE_MigratingForests", T }, { "LE_PowerDrain", T },
            { "LE_RatInfestation", T }, { "LE_ResearchSetback", T }, { "LE_FireSupportBase", T }, { "LE_ForwardOperatingBase", T },
            { "LE_RaidEnemy_SanguophageHunter", T }, { "LE_InterdictionPoint", T }, { "LE_Mimic", T }, { "LE_TurretMalfunction", T },
            { "LE_ForceWeather_DryThunderstorm", T }, { "LE_ForceWeather_RainyThunderstorm", T }, { "LE_ForceWeather_FoggyRain", T },
            { "LE_ForceWeather_SnowHard", T }, { "LE_ForceWeather_Sandstorm", T }, { "LE_ForceWeather_BlindFog", T },
            { "LE_ForceWeather_Blizzard", T }, { "LE_ForceWeather_TorrentialRain", T }, { "LE_AtmosphericHallucinogen", T },
            { "LE_GravityHigh", T }, { "LE_SubatomicTurbulence", T }, { "LE_DielectricInterference", T },
            { "LE_EconomicDepression", T }, { "LE_MagneticDisruption", T }, { "LE_ScandalousInsult", T },
            { "LE_RiseTreesInitial", M }, { "LE_RiseTrees", M }, { "LE_ShipCrash", M }, { "LE_ForceWeather_ToxRain", M },
            { "LE_ArchotechPsychosis", M },
            // More Faction Interaction
            { "MFI_HuntersLodge", G }, { "MFI_ReverseTradeRequest", G }, { "MFI_BumperCropRequest", G }, { "MFI_AnnualExpo", G },
            { "MFI_MysticalShaman", G },
            { "MFI_DiplomaticMarriage", O }, { "MFI_WoundedCombatants", O }, { "MFI_RoadWorks", O }, { "MFI_SettlementBaseAttack", O },
            { "MFI_Quest_PeaceTalks", O }, { "MFI_HerdMigration_Ambush", O },
            { "MFI_PirateExtortion", T }, { "MFI_QuestSpreadingPirateCamp", T },
            // Wandering Caravans, Trader Ships, Call a Trader, Hospitality, Vanilla Trading Expanded
            { "WanderingCaravanSpawned", G }, { "WanderingCaravanChase", T }, { "WanderingCaravanReturnDemand", T },
            { "TraderShipsArrival", G }, { "TraderShipsCrash", O },
            { "acat_OrbitalTraderVisitingOffer", G },
            { "HappyGuestJoins", G }, { "VisitorGroupMax", G }, { "VisitorGroupSelectFaction", G },
            { "VisitorGroupSpacerCruise", G }, { "VisitorGroupSpacerLuxury", G },
            { "VTE_CaravanArriveForItems", O }, { "VTE_Collectors", T },
            // Vanilla Expanded animals and plants, VFE, deep drill, gardens
            { "AEXP_ExtintAnimalsPasses", C }, { "VAEWaste_WasteAnimalsWanderIn", O }, { "VPEF_Weeds", O },
            { "BeeHiveSprout", G }, { "VSE_Reinforcements", T }, { "MBDD_Infestation", T },
            // race mods with their own events
            { "Milira_SolarCrystalMining", G }, { "Milira_MeteoriteImpact_SolarCrystal", G }, { "Milira_CaravanArrivalChurchTribute", O },
            { "Milira_Raid", T }, { "Milira_SmallCluster_SingleTurret", M }, { "Milira_MilianCluster", M },
            { "Nivarian_MothershipMooring", G }, { "Nivarian_PowerDistribution", G }, { "NivarianAid", G }, { "NivarianCryoPillAid", G },
            { "NivarianRomanticAid", G }, { "NivarianDropPodReinforcement", G }, { "NivarianKoelimeApologyAid", G }, { "NivarianWanderer", G },
            { "Nivarian_OrbitalFireworks", C }, { "Nivarian_FrostDrakeArrival", O }, { "NivarianPawnLend", O },
            { "NivarianWasteCollection", O }, { "Nivarian_ContiniumMechRaid", M },
            { "VV_TitanicHornetSwarm", T }, { "VV_TitanicHornetAssault", M },
            { "Mugirl_Migration", G }, { "Mugirl_CourierRaid", T },
            { "Kiiro_NightRaid", T }, { "Wolfein_RebelRaid", T }, { "Wolfein_DroneRaid", T }, { "AssassinAssault", T },
            { "Nyaron_Infestation", M },
        };

        // good things a generous day (4) may bring as its gift
        private static readonly string[] gifts =
        {
            "LE_Gift", "LE_SilverShower", "LE_BlackMarket", "LE_NutrientRain", "WanderingCaravanSpawned", "TraderShipsArrival",
            "acat_OrbitalTraderVisitingOffer", "NivarianAid", "NivarianCryoPillAid", "VEE_Cargopodsweapons", "VEE_Cargopodsapparel",
            "VEE_AnimalPodCrash", "VEE_CaravanAnimalsWanderIn", "MFI_HuntersLodge", "Milira_MeteoriteImpact_SolarCrystal"
        };

        private static List<IncidentDef> all;
        private static readonly Dictionary<IncidentDef, MKind> guessed = new Dictionary<IncidentDef, MKind>();

        public static bool On => RTMod.S == null || RTMod.S.modEvents;

        public static bool IsModded(IncidentDef d) =>
            d?.modContentPack != null && !d.modContentPack.IsOfficialMod;

        public static bool HandSorted(IncidentDef d) => d != null && table.ContainsKey(d.defName);

        // all events of other mods that can come on their own (and the hand-sorted ones)
        public static List<IncidentDef> All
        {
            get
            {
                if (all == null)
                {
                    all = DefDatabase<IncidentDef>.AllDefsListForReading
                        .Where(d => IsModded(d) && d.category != null && (d.baseChance > 0f || HandSorted(d))
                            && d.modContentPack.PackageId?.ToLowerInvariant().Contains("maddicemax") != true)
                        .OrderBy(d => d.modContentPack.Name).ThenBy(d => (string)d.LabelCap ?? d.defName)
                        .ToList();
                }
                return all;
            }
        }

        public static MKind Auto(IncidentDef d)
        {
            if (table.TryGetValue(d.defName, out MKind k)) return k;
            if (guessed.TryGetValue(d, out k)) return k;
            k = Guess(d);
            guessed[d] = k;
            return k;
        }

        public static MKind KindOf(IncidentDef d)
        {
            if (RTMod.S?.modKinds != null && RTMod.S.modKinds.TryGetValue(d.defName, out int v) && v >= 0 && v <= 4)
                return (MKind)v;
            return Auto(d);
        }

        private static bool Has(string s, params string[] words)
        {
            foreach (string w in words)
                if (s.IndexOf(w, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // any other mod: sorted by its category, letter and name
        private static MKind Guess(IncidentDef d)
        {
            string n = (d.defName + " " + d.label).ToLowerInvariant();
            string cat = d.category?.defName ?? "";
            string letter = d.letterDef?.defName ?? "";
            if (letter == "PurpleEvent" || n.Contains("purple"))
                return MKind.Mad;
            if (cat == "ThreatBig" && Has(n, "cluster", "earthquake", "infestation", "assault", "swarm", "apocalyp", "horde"))
                return MKind.Mad;
            if (cat == "ThreatBig" || cat == "ThreatSmall" || letter == "ThreatBig" || letter == "ThreatSmall" || letter == "NegativeEvent"
                || Has(n, "raid", "attack", "ambush", "manhunter", "siege", "infest", "plague", "assassin", "extortion"))
                return MKind.Threat;
            if (cat == "OrbitalVisitor" || cat == "FactionArrival" || letter == "PositiveEvent"
                || Has(n, "trader", "caravan", "gift", "aid", "join", "wanderer", "bloom", "visitor", "treasure", "reward", "supply"))
                return MKind.Good;
            return MKind.Ordinary;
        }

        public static int Face(int roll) => roll >= 1 && roll <= 6 ? roll : 3;

        public static float Factor(MKind k, int roll) => factors[(int)k, Face(roll) - 1];

        // how much more (or less) likely this event is today; 0 = it doesn't come today
        public static float Factor(IncidentDef d, int roll)
        {
            if (!On || !IsModded(d)) return 1f;
            if (RTMod.S != null && RTMod.S.modEventsOff.Contains(d.defName)) return 0f;
            return Factor(KindOf(d), roll);
        }

        public static int Today => DiceState.I?.roll ?? 3;

        // the faces where a kind comes more often / never
        public static string Faces(MKind k, bool often)
        {
            var list = new List<string>();
            for (int f = 1; f <= 6; f++)
            {
                float v = factors[(int)k, f - 1];
                if (often ? v >= 1.4f : v <= 0f) list.Add(f.ToString());
            }
            return list.Count == 0 ? "RT_MK_NoFace".Translate().ToString() : string.Join(", ", list);
        }

        public static string KindLabel(MKind k) => ("RT_MK_" + k).Translate();

        // modded good things that can be today's gift
        public static IEnumerable<IncidentDef> Gifts()
        {
            if (!On) yield break;
            foreach (string n in gifts)
            {
                IncidentDef d = DefDatabase<IncidentDef>.GetNamedSilentFail(n);
                if (d != null && IsModded(d) && Factor(d, 4) > 0f && KindOf(d) == MKind.Good) yield return d;
            }
        }

        // weighted random order: likelier events first (Efraimidis–Spirakis)
        public static IEnumerable<IncidentDef> WeightedOrder(IEnumerable<IncidentDef> defs, int roll) =>
            defs.Select(d => new { d, w = Factor(d, roll) })
                .Where(x => x.w > 0f)
                .OrderByDescending(x => Mathf.Pow(Rand.Value, 1f / x.w))
                .Select(x => x.d);

        public static void FireNow(IncidentDef d)
        {
            Map map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
            if (map == null || d == null) return;
            IncidentParms p = StorytellerUtility.DefaultParmsNow(d.category, map);
            p.forced = true;
            try { d.Worker.TryExecute(p); }
            catch (Exception e) { Log.Warning("[Mad Dice Max] " + d.defName + ": " + e.Message); }
        }
    }

    // with Max, a modded event that doesn't fit today's roll (threats on a calm or generous day,
    // mad events on a quiet one) or that you switched off can't start; forced events (quests,
    // dev mode) are not touched
    [HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
    public static class Patch_ModEventsToday
    {
        public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
        {
            try
            {
                if (!__result || parms == null || parms.forced || !ModEvents.On) return;
                IncidentDef d = __instance.def;
                if (!ModEvents.IsModded(d) || DiceState.I == null || !DiceState.DiceActive) return;
                if (ModEvents.Factor(d, ModEvents.Today) <= 0f) __result = false;
            }
            catch { }
        }
    }

    public class Dialog_ModEvents : Window
    {
        private Vector2 scroll;
        private string filter = "";
        public override Vector2 InitialSize => new Vector2(760f, 720f);

        public Dialog_ModEvents()
        {
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "RT_ModEventListTitle".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y + 32f, r.width, 46f), "RT_ModEventListDesc".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            filter = Widgets.TextField(new Rect(r.x, r.y + 80f, 260f, 26f), filter ?? "");
            if (filter.NullOrEmpty())
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(r.x + 6f, r.y + 82f, 250f, 24f), "RT_ModEventSearch".Translate());
                GUI.color = Color.white;
            }
            List<IncidentDef> list = ModEvents.All;
            if (!filter.NullOrEmpty())
            {
                string f = filter.ToLowerInvariant();
                list = list.Where(d => ((string)d.LabelCap ?? "").ToLowerInvariant().Contains(f) || d.defName.ToLowerInvariant().Contains(f)
                    || d.modContentPack.Name.ToLowerInvariant().Contains(f)).ToList();
            }
            Rect outR = new Rect(r.x, r.y + 112f, r.width, r.height - 112f - 50f);
            if (ModEvents.All.Count == 0)
            {
                Widgets.Label(outR, "RT_ModEventsNone".Translate());
                return;
            }
            float rowH = 28f, headH = 30f;
            int mods = list.Select(d => d.modContentPack).Distinct().Count();
            Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * rowH + mods * headH);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0f;
            List<string> off = RTMod.S.modEventsOff;
            ModContentPack last = null;
            bool dev = Prefs.DevMode && Current.Game != null;
            foreach (IncidentDef d in list)
            {
                if (d.modContentPack != last)
                {
                    last = d.modContentPack;
                    GUI.color = MUI.Accent;
                    Widgets.Label(new Rect(0f, y + 5f, view.width, 24f), last.Name);
                    GUI.color = Color.white;
                    y += headH;
                }
                Rect row = new Rect(0f, y, view.width, rowH);
                if (y + rowH >= scroll.y && y <= scroll.y + outR.height)
                {
                    Widgets.DrawHighlightIfMouseover(row);
                    MKind k = ModEvents.KindOf(d);
                    bool custom = RTMod.S.modKinds.ContainsKey(d.defName);
                    float right = 150f + (dev ? 80f : 0f);
                    bool on = !off.Contains(d.defName), was = on;
                    string label = (ModEvents.HandSorted(d) ? "★ " : "") + ((string)d.LabelCap ?? d.defName);
                    Widgets.CheckboxLabeled(new Rect(row.x + 10f, row.y, row.width - right - 16f, rowH), label.Truncate(row.width - right - 60f), ref on);
                    if (on != was) { if (on) off.Remove(d.defName); else off.Add(d.defName); }
                    Rect kb = new Rect(row.xMax - right, row.y + 2f, 146f, 24f);
                    GUI.color = KindColor(k);
                    if (Widgets.ButtonText(kb, KindLabel(k) + (custom ? " *" : "")))
                    {
                        IncidentDef def = d;
                        var opts = new List<FloatMenuOption>();
                        for (int i = 0; i <= 4; i++)
                        {
                            int v = i;
                            opts.Add(new FloatMenuOption(KindLabel((MKind)v), () => RTMod.S.modKinds[def.defName] = v));
                        }
                        opts.Add(new FloatMenuOption("RT_MK_Auto".Translate(KindLabel(ModEvents.Auto(def))), () => RTMod.S.modKinds.Remove(def.defName)));
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(row, "RT_MK_Tip".Translate(d.defName, ModEvents.Faces(k, true), ModEvents.Faces(k, false)));
                    if (dev && Widgets.ButtonText(new Rect(row.xMax - 76f, row.y + 2f, 72f, 24f), "RT_EventTry".Translate()))
                        ModEvents.FireNow(d);
                }
                y += rowH;
            }
            Widgets.EndScrollView();
        }

        private static string KindLabel(MKind k) => ModEvents.KindLabel(k).CapitalizeFirst();

        private static Color KindColor(MKind k)
        {
            switch (k)
            {
                case MKind.Calm: return new Color(0.65f, 0.85f, 1f);
                case MKind.Good: return new Color(0.6f, 1f, 0.6f);
                case MKind.Threat: return new Color(1f, 0.7f, 0.5f);
                case MKind.Mad: return new Color(1f, 0.55f, 0.9f);
                default: return Color.white;
            }
        }
    }
}
