using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // 1.9.0 Planning: sowing calendar, harvest forecast, winter crop
    // rotation, greenhouse watch and orchard zones.
    // Everything here reads season averages of the map's world tile and the
    // plant defs as this mod has changed them (hardy plants, grow days,
    // growth speed, day and night growth), so the forecast follows the
    // player's settings. Nothing is computed every frame: the calendar
    // refreshes every few seconds, zones every few hundred ticks, the
    // rotation once an in-game hour.
    // ------------------------------------------------------------------
    public static class Forecast
    {
        public const int MaxDays = 120;

        public struct Result
        {
            public bool growsNow;      // temperature allows growth today
            public bool ripens;        // reaches full growth before the cold stops it
            public float ripenDays;    // days until ripe when sown today (if ripens)
            public float idealDays;    // days under ideal temperature
            public int frostDay;       // first day the plant stops growing (-1 = none ahead)
        }

        // average outdoor temperature of a part of a day, from the season of the map's tile
        public static float Temp(Map map, int absTick)
        {
            float t = GenTemperature.GetTemperatureFromSeasonAtTile(absTick, map.Tile);
            try { t += GenTemperature.OffsetFromSunCycle(absTick, map.Tile); } catch { }
            return t;
        }

        // 4 samples a day for the days ahead, cached per map for an in-game hour
        private static Map tempMap;
        private static int tempHour = -1;
        private static float[] temps;

        public static float[] Temps(Map map)
        {
            int hour = GenTicks.TicksAbs / 2500;
            if (temps != null && tempMap == map && tempHour == hour)
                return temps;
            int now = GenTicks.TicksAbs;
            var a = new float[MaxDays * 3 * 4];
            for (int i = 0; i < a.Length; i++)
                a[i] = Temp(map, now + i * 15000);
            temps = a;
            tempMap = map;
            tempHour = hour;
            return a;
        }

        // share of a day plants grow (they rest at night unless "grow day and night" is on)
        public static float ActiveShare()
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            return s != null && s.growAllDay ? 1f : 0.55f;
        }

        public static float SpeedFactor(ThingDef def)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.fasterGrowth)
                return 1f;
            switch (Kind.Of(def))
            {
                case 1: return s.treeSpeed;
                case 2: return s.cropSpeed;
                default: return s.wildSpeed;
            }
        }

        // a plant sown today outdoors on the given soil: when is it ripe, when does the cold stop it
        public static Result Simulate(ThingDef def, Map map, float fertility = 1f, bool indoors = false)
        {
            var r = new Result { frostDay = -1 };
            PlantProperties pp = def.plant;
            float days = Mathf.Max(0.1f, pp.growDays);
            float perDay = SpeedFactor(def) * ActiveShare() * PlantUtility.GrowthRateFactorFor_Fertility(def, fertility) / days;
            r.idealDays = perDay > 0f ? 1f / perDay : 9999f;
            if (perDay <= 0f)
                return r;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            bool alwaysOk = indoors && s != null && s.greenhouse;
            float[] tt = Temps(map);
            float growth = 0f;
            for (int d = 0; d < MaxDays; d++)
            {
                float f = 0f, avg = 0f;
                for (int k = 0; k < 4; k++)
                {
                    float t = tt[d * 4 + k];
                    avg += t * 0.25f;
                    if (alwaysOk)
                        f += 0.25f;
                    else if (t > pp.minGrowthTemperature && t < pp.maxGrowthTemperature)
                        f += 0.25f * PlantUtility.GrowthRateFactorFor_Temperature(def, t);
                }
                bool season = alwaysOk || (avg > pp.minGrowthTemperature && avg < pp.maxGrowthTemperature);
                if (d == 0)
                    r.growsNow = season;
                if (!season)
                {
                    if (r.frostDay < 0 && (d > 0 || growth > 0f || r.growsNow))
                        r.frostDay = d;
                    if (growth > 0f)
                        break;   // the cold (or heat) stops a growing crop
                    continue;     // not sown yet: waits for the season
                }
                float add = f * perDay;
                if (growth + add >= 1f)
                {
                    r.ripens = true;
                    r.ripenDays = d + (1f - growth) / Mathf.Max(add, 0.0001f);
                    break;
                }
                growth += add;
            }
            if (!r.growsNow)
                r.ripens = false;
            if (r.frostDay < 0)
            {
                // first stop after today, for the season line
                for (int d = 1; d < MaxDays; d++)
                {
                    float avg = 0f;
                    for (int k = 0; k < 4; k++)
                        avg += tt[d * 4 + k] * 0.25f;
                    if (!alwaysOk && (avg <= pp.minGrowthTemperature || avg >= pp.maxGrowthTemperature))
                    {
                        r.frostDay = d;
                        break;
                    }
                }
            }
            return r;
        }

        // first day ahead whose average temperature falls below 'limit' (-1 = none), and the first warm day after it
        public static void Season(Map map, float limit, out int frost, out int warm)
        {
            frost = -1;
            warm = -1;
            float[] tt = Temps(map);
            for (int d = 0; d < MaxDays * 3; d++)
            {
                float avg = 0f;
                for (int k = 0; k < 4; k++)
                    avg += tt[d * 4 + k] * 0.25f;
                if (frost < 0)
                {
                    if (avg < limit)
                        frost = d;
                }
                else if (avg >= limit)
                {
                    warm = d;
                    return;
                }
            }
        }

        public static bool SowableOnGround(ThingDef d)
        {
            return d?.plant != null && d.plant.Sowable && d.plant.sowTags != null && d.plant.sowTags.Contains("Ground");
        }

        public static IEnumerable<ThingDef> Crops()
        {
            return HardyPlantsCore.AllPlants().Where(SowableOnGround);
        }
    }

    // ------------------------------------------------------------------
    // harvest forecast of one grow zone (cached for a few hundred ticks)
    // ------------------------------------------------------------------
    public class ZoneForecast
    {
        public ThingDef plant;
        public int plants, ready, growing, stalled;
        public float days = -1f;      // most of the growing plants ripen in about … days
        public float yield;           // expected product of all plants
        public ThingDef product;

        private static readonly Dictionary<Zone, KeyValuePair<int, ZoneForecast>> cache = new Dictionary<Zone, KeyValuePair<int, ZoneForecast>>();

        public static ZoneForecast For(Zone_Growing z)
        {
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(z, out var kv) && now - kv.Key < 600 && kv.Value.plant == z.GetPlantDefToGrow())
                return kv.Value;
            ZoneForecast f = Make(z);
            if (cache.Count > 200)
                cache.Clear();
            cache[z] = new KeyValuePair<int, ZoneForecast>(now, f);
            return f;
        }

        public static void Forget() => cache.Clear();

        private static ZoneForecast Make(Zone_Growing z)
        {
            var f = new ZoneForecast { plant = z.GetPlantDefToGrow() };
            Map map = z.Map;
            if (f.plant?.plant == null || map == null)
                return f;
            PlantProperties pp = f.plant.plant;
            f.product = pp.harvestedThingDef;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            float yf = 1f;
            if (s != null && s.yieldOn)
                yf = Kind.Of(f.plant) == 1 ? s.woodYield : s.cropYield;
            if (pp.harvestYieldAffectedByDifficulty && Find.Storyteller?.difficulty != null)
                yf *= Find.Storyteller.difficulty.cropYieldFactor;
            var left = new List<float>();
            float share = Forecast.ActiveShare();
            List<IntVec3> cells = z.Cells;
            for (int i = 0; i < cells.Count; i++)
            {
                Plant p = cells[i].GetPlant(map);
                if (p == null || p.def != f.plant || p.Blighted)
                    continue;
                f.plants++;
                float hp = p.MaxHitPoints > 0 ? (float)p.HitPoints / p.MaxHitPoints : 1f;
                f.yield += pp.harvestYield * yf * Mathf.Lerp(0.5f, 1f, hp);
                if (p.LifeStage == PlantLifeStage.Mature)
                {
                    f.ready++;
                    continue;
                }
                float rate = 0f;
                try { rate = p.GrowthRate; } catch { }
                if (rate <= 0f)
                {
                    f.stalled++;
                    continue;
                }
                f.growing++;
                left.Add((1f - p.Growth) * Mathf.Max(0.1f, pp.growDays) / (rate * share));
            }
            if (left.Count > 0)
            {
                left.Sort();
                f.days = left[left.Count / 2];
            }
            return f;
        }

        public string Line()
        {
            if (plants == 0 || product == null)
                return null;
            string prod = product.label;
            string amount = Mathf.RoundToInt(yield).ToString();
            if (growing == 0 && stalled == 0 && ready > 0)
                return "HardyPlants_ZoneReady".Translate(ready, amount, prod);
            if (growing == 0 && stalled > 0)
                return "HardyPlants_ZoneStalled".Translate(stalled);
            string s = "HardyPlants_ZoneForecast".Translate(days.ToString("0.#"), amount, prod);
            if (ready > 0)
                s += " " + "HardyPlants_ZoneReadyToo".Translate(ready).ToString();
            return s;
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.GetInspectString))]
    public static class Patch_ZoneForecastInspect
    {
        public static void Postfix(Zone_Growing __instance, ref string __result)
        {
            try
            {
                string line = ZoneForecast.For(__instance).Line();
                if (string.IsNullOrEmpty(line))
                    return;
                __result = string.IsNullOrEmpty(__result) ? line : __result.TrimEnd() + "\n" + line;
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // per zone: winter crop and orchard (saved with the map)
    // ------------------------------------------------------------------
    public class ZonePlan : IExposable
    {
        public string summer, winter;
        public bool inWinter, orchard;

        public void ExposeData()
        {
            Scribe_Values.Look(ref summer, "summer");
            Scribe_Values.Look(ref winter, "winter");
            Scribe_Values.Look(ref inWinter, "inWinter", false);
            Scribe_Values.Look(ref orchard, "orchard", false);
        }

        public bool Empty => string.IsNullOrEmpty(winter) && !orchard;
    }

    public class PlannerComp : MapComponent
    {
        public Dictionary<int, ZonePlan> plans = new Dictionary<int, ZonePlan>();
        private static bool warned;

        public PlannerComp(Map map) : base(map) { }

        public static PlannerComp For(Map map) => map?.GetComponent<PlannerComp>();

        public ZonePlan Get(Zone z, bool create)
        {
            if (z == null)
                return null;
            if (plans.TryGetValue(z.ID, out ZonePlan p))
                return p;
            if (!create)
                return null;
            p = new ZonePlan();
            plans[z.ID] = p;
            return p;
        }

        public static bool IsOrchard(IntVec3 c, Map map)
        {
            if (!(c.GetZone(map) is Zone_Growing z))
                return false;
            PlannerComp pc = For(map);
            return pc != null && pc.plans.TryGetValue(z.ID, out ZonePlan p) && p.orchard;
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref plans, "evergrowthZonePlans", LookMode.Value, LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                plans ??= new Dictionary<int, ZonePlan>();
        }

        public override void MapComponentTick()
        {
            if (plans.Count == 0)
                return;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.winterRotation)
                return;
            if ((Find.TickManager.TicksGame + map.uniqueID * 131) % 2500 != 0)
                return;
            try
            {
                Rotate();
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[Evergrowth] winter crop rotation failed: " + ex);
                }
            }
        }

        private void Rotate()
        {
            List<int> gone = null;
            foreach (var kv in plans)
            {
                ZonePlan p = kv.Value;
                if (string.IsNullOrEmpty(p.winter))
                    continue;
                Zone_Growing z = map.zoneManager.AllZones.FirstOrDefault(x => x.ID == kv.Key) as Zone_Growing;
                if (z == null)
                {
                    (gone ??= new List<int>()).Add(kv.Key);
                    continue;
                }
                ThingDef winter = DefDatabase<ThingDef>.GetNamedSilentFail(p.winter);
                ThingDef cur = z.GetPlantDefToGrow();
                if (winter == null || cur == null)
                    continue;
                if (!p.inWinter)
                {
                    if (cur != winter)
                        p.summer = cur.defName;
                    ThingDef summer = DefDatabase<ThingDef>.GetNamedSilentFail(p.summer ?? "");
                    if (summer == null || summer == winter)
                        continue;
                    Forecast.Result r = Forecast.Simulate(summer, map);
                    bool frostSoon = !r.growsNow || (r.frostDay >= 0 && r.frostDay <= 3);
                    if (!r.ripens && (frostSoon || !Growing(z, summer, 0.2f)))
                    {
                        z.SetPlantDefToGrow(winter);
                        p.inWinter = true;
                        ZoneForecast.Forget();
                        Messages.Message("HardyPlants_RotToWinter".Translate(z.label, winter.LabelCap), new TargetInfo(z.Position, map), MessageTypeDefOf.NeutralEvent, false);
                    }
                }
                else
                {
                    ThingDef summer = DefDatabase<ThingDef>.GetNamedSilentFail(p.summer ?? "");
                    if (cur != winter || summer == null)
                    {
                        p.inWinter = false;   // changed by hand: the player's choice wins
                        continue;
                    }
                    Forecast.Result r = Forecast.Simulate(summer, map);
                    Forecast.Result w = Forecast.Simulate(winter, map);
                    bool goodSeason = r.ripens && (r.frostDay < 0 || r.ripenDays + 5f < r.frostDay);
                    if (goodSeason && (!Growing(z, winter, 0.2f) || !w.growsNow))
                    {
                        z.SetPlantDefToGrow(summer);
                        p.inWinter = false;
                        ZoneForecast.Forget();
                        Messages.Message("HardyPlants_RotToSummer".Translate(z.label, summer.LabelCap), new TargetInfo(z.Position, map), MessageTypeDefOf.NeutralEvent, false);
                    }
                }
            }
            if (gone != null)
                foreach (int id in gone)
                    plans.Remove(id);
        }

        // plants of this kind in the zone that are well on their way (worth waiting for)
        private bool Growing(Zone_Growing z, ThingDef def, float minGrowth)
        {
            List<IntVec3> cells = z.Cells;
            for (int i = 0; i < cells.Count; i++)
            {
                Plant pl = cells[i].GetPlant(map);
                if (pl != null && pl.def == def && !pl.Blighted && pl.Growth >= minGrowth && pl.LifeStage != PlantLifeStage.Mature)
                    return true;
            }
            return false;
        }
    }

    // ------------------------------------------------------------------
    // gizmos on grow zones and growers: calendar, winter crop, orchard
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.GetGizmos))]
    public static class Patch_PlannerZoneGizmos
    {
        private static Texture2D calIcon;

        public static Texture2D CalIcon => calIcon ??= ContentFinder<Texture2D>.Get("UI/Commands/SetPlantToGrow", false) ?? BaseContent.BadTex;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Zone_Growing __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            Zone_Growing z = __instance;
            Map map = z.Map;
            if (map == null)
                yield break;
            yield return CalendarGizmo(map);
            PlannerComp pc = PlannerComp.For(map);
            if (pc == null)
                yield break;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            ZonePlan plan = pc.Get(z, false);
            if (s != null && s.winterRotation)
            {
                ThingDef w = plan?.winter == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(plan.winter);
                yield return new Command_Action
                {
                    defaultLabel = w == null ? "HardyPlants_WinterGizmo".Translate().ToString() : "HardyPlants_WinterGizmoSet".Translate(w.LabelCap).ToString(),
                    defaultDesc = "HardyPlants_WinterGizmoDesc".Translate(),
                    icon = w?.uiIcon ?? CalIcon,
                    defaultIconColor = w == null ? new Color(0.6f, 0.75f, 1f, 0.7f) : Color.white,
                    action = () => Find.WindowStack.Add(new FloatMenu(WinterOptions(pc, z)))
                };
            }
            ThingDef cur = z.GetPlantDefToGrow();
            if (cur?.plant != null && (cur.plant.IsTree || Kind.Of(cur) == 1))
            {
                yield return new Command_Toggle
                {
                    defaultLabel = "HardyPlants_Orchard".Translate(),
                    defaultDesc = "HardyPlants_OrchardDesc".Translate(),
                    icon = cur.uiIcon ?? CalIcon,
                    isActive = () => pc.Get(z, false)?.orchard ?? false,
                    toggleAction = () =>
                    {
                        ZonePlan p = pc.Get(z, true);
                        p.orchard = !p.orchard;
                        if (p.Empty)
                            pc.plans.Remove(z.ID);
                    }
                };
            }
        }

        public static Command CalendarGizmo(Map map)
        {
            return new Command_Action
            {
                defaultLabel = "HardyPlants_Calendar".Translate(),
                defaultDesc = "HardyPlants_CalendarDesc".Translate(),
                icon = CalIcon,
                defaultIconColor = new Color(1f, 0.92f, 0.6f),
                action = () => Find.WindowStack.Add(new Dialog_SowingCalendar(map))
            };
        }

        private static List<FloatMenuOption> WinterOptions(PlannerComp pc, Zone_Growing z)
        {
            var list = new List<FloatMenuOption>
            {
                new FloatMenuOption("HardyPlants_WinterOff".Translate(), () =>
                {
                    ZonePlan p = pc.Get(z, false);
                    if (p == null)
                        return;
                    if (p.inWinter)
                    {
                        ThingDef summer = DefDatabase<ThingDef>.GetNamedSilentFail(p.summer ?? "");
                        if (summer != null)
                            z.SetPlantDefToGrow(summer);
                    }
                    p.winter = null;
                    p.inWinter = false;
                    if (p.Empty)
                        pc.plans.Remove(z.ID);
                })
            };
            ThingDef cur = z.GetPlantDefToGrow();
            foreach (ThingDef d in Forecast.Crops().Where(d => d != cur && !d.plant.IsTree).OrderBy(d => d.plant.minGrowthTemperature).ThenBy(d => d.label))
            {
                ThingDef dd = d;
                if (!PlantUtility.CanSowOnGrower(dd, z))
                    continue;
                string label = dd.LabelCap + "  (" + "HardyPlants_WinterFrom".Translate(dd.plant.minGrowthTemperature.ToStringTemperature("F0")) + ")";
                list.Add(new FloatMenuOption(label, () =>
                {
                    ZonePlan p = pc.Get(z, true);
                    p.winter = dd.defName;
                    if (!p.inWinter)
                        p.summer = z.GetPlantDefToGrow()?.defName;
                }, dd));
            }
            return list;
        }
    }

    [HarmonyPatch(typeof(Building_PlantGrower), nameof(Building_PlantGrower.GetGizmos))]
    public static class Patch_PlannerGrowerGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building_PlantGrower __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            if (__instance.Faction == Faction.OfPlayer && __instance.Map != null)
                yield return Patch_PlannerZoneGizmos.CalendarGizmo(__instance.Map);
        }
    }

    // ------------------------------------------------------------------
    // orchard zones: a tree that is not fully grown is never cut by sowing
    // (another tree kind chosen for the zone, a wild tree inside it) – only
    // ripe trees are harvested and the cell is sown again as usual.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell))]
    public static class Patch_OrchardSow
    {
        public static void Postfix(Pawn pawn, IntVec3 c, ref Job __result)
        {
            if (__result == null || __result.def != JobDefOf.CutPlant || pawn?.Map == null)
                return;
            try
            {
                if (__result.targetA.Thing is Plant p && p.def.plant != null && p.def.plant.IsTree && p.LifeStage != PlantLifeStage.Mature
                    && !p.Map.designationManager.HasMapDesignationOn(p) && PlannerComp.IsOrchard(c, pawn.Map))
                    __result = null;
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // greenhouse watch: an indoor grow zone or grower whose room is too cold
    // (or too hot) for what grows there – heating or cooling failed
    // ------------------------------------------------------------------
    public class Alert_EvergrowthGreenhouse : Alert
    {
        private readonly List<GlobalTargetInfo> culprits = new List<GlobalTargetInfo>();
        private string lines = "";
        private float next;

        public Alert_EvergrowthGreenhouse()
        {
            defaultLabel = "HardyPlants_AlertGH".Translate();
            defaultPriority = AlertPriority.High;
        }

        public override TaggedString GetExplanation() => "HardyPlants_AlertGHDesc".Translate(lines);

        public override AlertReport GetReport()
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.greenhouseWatch)
                return false;
            if (Time.realtimeSinceStartup >= next)
            {
                next = Time.realtimeSinceStartup + 3f;
                try { Scan(s); } catch { culprits.Clear(); }
            }
            return culprits.Count == 0 ? AlertReport.Inactive : AlertReport.CulpritsAre(culprits);
        }

        private void Scan(HardyPlantsSettings s)
        {
            culprits.Clear();
            var sb = new StringBuilder();
            var seen = new HashSet<Room>();
            foreach (Map map in Find.Maps)
            {
                if (!map.IsPlayerHome)
                    continue;
                foreach (Zone zone in map.zoneManager.AllZones)
                    if (zone is Zone_Growing z && z.Cells.Count > 0)
                        Check(map, z.Cells[z.Cells.Count / 2], z.GetPlantDefToGrow(), z.label, s, seen, sb);
                foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    if (b is Building_PlantGrower g && g.Spawned)
                        Check(map, g.Position, g.GetPlantDefToGrow(), g.LabelCap, s, seen, sb);
            }
            lines = sb.ToString().TrimEnd();
        }

        private void Check(Map map, IntVec3 c, ThingDef def, string name, HardyPlantsSettings s, HashSet<Room> seen, StringBuilder sb)
        {
            if (def?.plant == null || !c.InBounds(map))
                return;
            RoofDef roof = map.roofGrid.RoofAt(c);
            if (roof == null || roof.isNatural && !def.plant.cavePlant)
                return;
            Room room = c.GetRoom(map);
            if (room == null || room.PsychologicallyOutdoors || room.UsesOutdoorTemperature || !seen.Add(room))
                return;
            float t = room.Temperature;
            float min = s.greenhouse ? Mathf.Max(def.plant.minGrowthTemperature, -10f) : def.plant.minGrowthTemperature;
            if (s.greenhouse && def.plant.minGrowthTemperature < -10f)
                min = def.plant.minGrowthTemperature;
            if (t <= min)
            {
                culprits.Add(new GlobalTargetInfo(c, map));
                sb.AppendLine("HardyPlants_AlertGHCold".Translate(name, t.ToStringTemperature("F0"), def.LabelCap, min.ToStringTemperature("F0")));
            }
            else if (t >= def.plant.maxGrowthTemperature)
            {
                culprits.Add(new GlobalTargetInfo(c, map));
                sb.AppendLine("HardyPlants_AlertGHHot".Translate(name, t.ToStringTemperature("F0"), def.LabelCap, def.plant.maxGrowthTemperature.ToStringTemperature("F0")));
            }
        }
    }

    // ------------------------------------------------------------------
    // the sowing calendar window
    // ------------------------------------------------------------------
    public class Dialog_SowingCalendar : Window
    {
        private readonly Map map;
        private Vector2 scroll;
        private float viewH = 600f;
        private float nextRefresh;
        private int frost, warm;
        private readonly List<KeyValuePair<ThingDef, Forecast.Result>> crops = new List<KeyValuePair<ThingDef, Forecast.Result>>();
        private readonly List<KeyValuePair<Zone_Growing, ZoneForecast>> zones = new List<KeyValuePair<Zone_Growing, ZoneForecast>>();
        private readonly Dictionary<ThingDef, float> totals = new Dictionary<ThingDef, float>();
        private string search = "";

        public Dialog_SowingCalendar(Map map)
        {
            this.map = map;
            doCloseX = true;
            doCloseButton = false;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            draggable = true;
            preventCameraMotion = false;
        }

        public override Vector2 InitialSize => new Vector2(760f, Mathf.Min(720f, UI.screenHeight - 60f));

        private void Refresh()
        {
            nextRefresh = Time.realtimeSinceStartup + 6f;
            crops.Clear();
            zones.Clear();
            totals.Clear();
            if (map == null || map.Disposed)
                return;
            Forecast.Season(map, 0f, out frost, out warm);
            foreach (ThingDef d in Forecast.Crops())
            {
                try { crops.Add(new KeyValuePair<ThingDef, Forecast.Result>(d, Forecast.Simulate(d, map))); } catch { }
            }
            crops.Sort((a, b) =>
            {
                int ra = Rank(a.Value), rb = Rank(b.Value);
                if (ra != rb) return ra.CompareTo(rb);
                return string.Compare(a.Key.label, b.Key.label, StringComparison.OrdinalIgnoreCase);
            });
            foreach (Zone z in map.zoneManager.AllZones)
            {
                if (!(z is Zone_Growing g))
                    continue;
                ZoneForecast f = ZoneForecast.For(g);
                zones.Add(new KeyValuePair<Zone_Growing, ZoneForecast>(g, f));
                if (f.product != null && f.yield > 0f)
                    totals[f.product] = (totals.TryGetValue(f.product, out float v) ? v : 0f) + f.yield;
            }
        }

        private static int Rank(Forecast.Result r)
        {
            if (r.ripens && r.ripenDays <= r.idealDays * 1.3f) return 0;
            if (r.ripens) return 1;
            return 2;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Time.realtimeSinceStartup >= nextRefresh)
                Refresh();
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 34f), "HardyPlants_CalTitle".Translate(map?.Parent?.LabelCap ?? ""));
            Text.Font = GameFont.Small;
            float y = inRect.y + 38f;
            if (map == null || map.Disposed)
                return;
            string now = GenDate.DateFullStringAt(GenTicks.TicksAbs, Find.WorldGrid.LongLatOf(map.Tile));
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "HardyPlants_CalNow".Translate(now, map.mapTemperature.OutdoorTemp.ToStringTemperature("F0")));
            y += 24f;
            string season = frost < 0 ? "HardyPlants_CalNoFrost".Translate(Forecast.MaxDays * 3).ToString()
                : frost == 0 ? (warm >= 0 ? "HardyPlants_CalColdNow".Translate(warm).ToString() : "HardyPlants_CalColdAlways".Translate().ToString())
                : "HardyPlants_CalFrost".Translate(frost).ToString() + (warm >= 0 ? " " + "HardyPlants_CalWarm".Translate(warm - frost).ToString() : "");
            GUI.color = frost < 0 || frost > 20 ? MUI.Accent : frost > 7 ? MUI.Warn : new Color(0.95f, 0.45f, 0.4f);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), season);
            GUI.color = Color.white;
            y += 28f;
            search = Widgets.TextField(new Rect(inRect.x, y, 220f, 26f), search ?? "");
            if (string.IsNullOrEmpty(search))
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(inRect.x + 6f, y + 3f, 210f, 24f), "HardyPlants_CalSearch".Translate());
                GUI.color = Color.white;
            }
            if (Widgets.ButtonText(new Rect(inRect.xMax - 130f, y, 130f, 26f), "HardyPlants_CalRefresh".Translate()))
            {
                ZoneForecast.Forget();
                Refresh();
            }
            y += 32f;

            Rect outer = new Rect(inRect.x, y, inRect.width, inRect.yMax - y);
            Rect view = new Rect(0f, 0f, outer.width - 16f, viewH);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float cy = 0f;
            // harvest forecast
            Header(view.width, ref cy, "HardyPlants_CalHarvest".Translate());
            if (zones.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(0f, cy, view.width, 24f), "HardyPlants_CalNoZones".Translate());
                GUI.color = Color.white;
                cy += 26f;
            }
            foreach (var kv in zones)
            {
                Rect row = new Rect(0f, cy, view.width, 26f);
                Widgets.DrawHighlightIfMouseover(row);
                ThingDef d = kv.Value.plant;
                if (d != null)
                    Widgets.DefIcon(new Rect(row.x + 2f, row.y + 1f, 24f, 24f), d);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 32f, row.y, 200f, row.height), kv.Key.label.Truncate(196f));
                string line = kv.Value.Line() ?? "HardyPlants_CalEmptyZone".Translate().ToString();
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(row.x + 236f, row.y, row.width - 236f, row.height), line.Truncate(row.width - 240f));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(row, line);
                if (Widgets.ButtonInvisible(row))
                {
                    CameraJumper.TryJumpAndSelect(new GlobalTargetInfo(kv.Key.Position, map));
                }
                cy += 28f;
            }
            if (totals.Count > 0)
            {
                string tot = string.Join(", ", totals.OrderByDescending(t => t.Value).Select(t => Mathf.RoundToInt(t.Value) + " " + t.Key.label));
                GUI.color = MUI.Accent;
                float h = Text.CalcHeight("HardyPlants_CalTotal".Translate(tot), view.width);
                Widgets.Label(new Rect(0f, cy, view.width, h), "HardyPlants_CalTotal".Translate(tot));
                GUI.color = Color.white;
                cy += h + 6f;
            }
            cy += 8f;
            // crops
            Header(view.width, ref cy, "HardyPlants_CalCrops".Translate());
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            float nh = Text.CalcHeight("HardyPlants_CalNote".Translate(), view.width);
            Widgets.Label(new Rect(0f, cy, view.width, nh), "HardyPlants_CalNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            cy += nh + 4f;
            string q = search?.Trim().ToLowerInvariant() ?? "";
            foreach (var kv in crops)
            {
                ThingDef d = kv.Key;
                if (q.Length > 0 && !d.label.ToLowerInvariant().Contains(q))
                    continue;
                Forecast.Result r = kv.Value;
                Rect row = new Rect(0f, cy, view.width, 26f);
                Widgets.DrawHighlightIfMouseover(row);
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 1f, 24f, 24f), d);
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(row.x + 32f, row.y, 200f, row.height), d.LabelCap.Truncate(196f));
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(row.x + 236f, row.y, 110f, row.height), "HardyPlants_CalNeeds".Translate(r.idealDays.ToString("0.#")));
                string st;
                int rank = Rank(r);
                if (!r.growsNow)
                {
                    st = "HardyPlants_CalNoGrow".Translate(d.plant.minGrowthTemperature.ToStringTemperature("F0"));
                    rank = 2;
                }
                else if (rank == 0)
                    st = "HardyPlants_CalOk".Translate(r.ripenDays.ToString("0.#"));
                else if (rank == 1)
                    st = "HardyPlants_CalSlow".Translate(r.ripenDays.ToString("0.#"));
                else
                    st = r.frostDay >= 0 ? "HardyPlants_CalLate".Translate(r.frostDay).ToString() : "HardyPlants_CalTooLong".Translate(Forecast.MaxDays).ToString();
                GUI.color = rank == 0 ? MUI.Accent : rank == 1 ? MUI.Warn : new Color(0.95f, 0.45f, 0.4f);
                Widgets.Label(new Rect(row.x + 350f, row.y, row.width - 350f, row.height), st.Truncate(row.width - 354f));
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                TooltipHandler.TipRegion(row, d.LabelCap + "\n" + st + "\n" + "HardyPlants_CalTip".Translate(d.plant.minGrowthTemperature.ToStringTemperature("F0"), d.plant.maxGrowthTemperature.ToStringTemperature("F0")));
                cy += 28f;
            }
            viewH = cy + 10f;
            Widgets.EndScrollView();
        }

        private static void Header(float w, ref float y, string text)
        {
            Text.Font = GameFont.Small;
            GUI.color = MUI.Text1;
            Widgets.Label(new Rect(0f, y, w, 24f), text);
            GUI.color = new Color(1f, 1f, 1f, 0.15f);
            Widgets.DrawLineHorizontal(0f, y + 24f, w);
            GUI.color = Color.white;
            y += 30f;
        }
    }
}
