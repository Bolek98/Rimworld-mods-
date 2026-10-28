using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyAdvisor
{
    // ---------------------------------------------------------------------------
    // 0.1.0: the advisor. Looks at the current home map and says what matters now:
    // dangers, what to make, what to research, which quests are worth it and how far
    // the colony is on its way through the game. Read-only – nothing changes until the
    // player presses a button (add a bill, start a research project, open a quest).
    // ---------------------------------------------------------------------------
    public enum Level { Urgent = 0, Important = 1, Tip = 2 }

    public class Tip
    {
        public Level level;
        public string title, detail;
        public GlobalTargetInfo target = GlobalTargetInfo.Invalid;
        public string actLabel;
        public Action act;
        public string group;    // "danger", "food", "health", "work", "build", "research", "quest", "progress"
    }

    public class Need
    {
        public ThingDef product;
        public int have, want;
        public string reason;
        public RecipeDef recipe;
        public Building_WorkTable bench;     // a bench on the map that can make it
        public ThingDef benchToBuild;        // no bench yet: build this one
        public Bill_Production existing;     // a bill for it already exists
        public bool urgent;
    }

    public class ResearchPick
    {
        public ResearchProjectDef def;
        public float score;
        public string reason;
    }

    public class QuestRating
    {
        public Quest quest;
        public float value;
        public int stars;
        public int verdict;    // 0 take it, 1 think about it, 2 better not
        public string why;
        public float hoursLeft = -1f;
    }

    public class Check
    {
        public string label;
        public bool done;
        public string hint;
    }

    public class Stage
    {
        public string title, goal;
        public List<Check> checks = new List<Check>();
        public bool Done => checks.All(c => c.done);
        public int DoneCount => checks.Count(c => c.done);
    }

    public class Ending
    {
        public string key, title, desc;
        public Check next;
        public List<Check> steps = new List<Check>();
        public bool available = true;
        public float Progress => steps.Count == 0 ? 0f : (float)steps.Count(s => s.done) / steps.Count;
    }

    public class Report
    {
        public Map map;
        public int colonists, fighters, strength = 1;
        public float foodDays, nutrition;
        public int medicine, meals;
        public int score;                       // 0..100
        public List<(string, int)> scoreParts = new List<(string, int)>();
        public List<Tip> tips = new List<Tip>();
        public List<Need> needs = new List<Need>();
        public List<ResearchPick> research = new List<ResearchPick>();
        public List<QuestRating> quests = new List<QuestRating>();
        public List<Stage> stages = new List<Stage>();
        public List<Ending> endings = new List<Ending>();
        public int CurrentStage => Math.Max(0, stages.FindIndex(s => !s.Done) is int i && i >= 0 ? i : stages.Count - 1);
        public int UrgentCount => tips.Count(t => t.level == Level.Urgent);
        public int ImportantCount => tips.Count(t => t.level == Level.Important);
    }

    public static class Advisor
    {
        private static Report cached;
        private static float cachedAt = -999f;
        private static int cachedTick = -1;
        private static Map cachedMap;

        public static void Invalidate() { cachedAt = -999f; }

        // computed at most every few seconds (window) or every 600 ticks (alert)
        public static Report Get(float maxAgeSeconds = 3f)
        {
            Map map = Find.CurrentMap;
            if (map == null || !map.IsPlayerHome)
                map = Find.Maps?.FirstOrDefault(m => m.IsPlayerHome);
            if (map == null) return null;
            int tick = Find.TickManager.TicksGame;
            if (cached != null && cachedMap == map && Time.realtimeSinceStartup - cachedAt < maxAgeSeconds && Math.Abs(tick - cachedTick) < 2500)
                return cached;
            try
            {
                cached = Build(map);
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Colony Advisor] analysis failed: " + e, 0x5A41D7);
                cached = cached ?? new Report { map = map };
            }
            cachedAt = Time.realtimeSinceStartup;
            cachedTick = tick;
            cachedMap = map;
            return cached;
        }

        private static string T(string key, params NamedArgument[] args) => key.Translate(args).Resolve();

        // ---------------------------------------------------------------------------
        private static Report Build(Map map)
        {
            var r = new Report { map = map };
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned.Where(p => !p.Dead).ToList();
            r.colonists = cols.Count;
            r.fighters = cols.Count(p => !p.WorkTagIsDisabled(WorkTags.Violent) && !p.Downed);

            Safe(() => Dangers(map, cols, r));
            Safe(() => FoodAndHealth(map, cols, r));
            Safe(() => Work(map, cols, r));
            Safe(() => Living(map, cols, r));
            Safe(() => Needs(map, cols, r));
            Safe(() => Research(map, cols, r));
            Safe(() => r.strength = Strength(map, cols));
            Safe(() => Quests(map, cols, r));
            Safe(() => Stages(map, cols, r));
            Safe(() => Endings(map, r));
            Safe(() => Score(map, cols, r));

            // the first research pick and the next step of the current stage are tips
            Safe(() =>
            {
                if (r.research.Count > 0 && Find.ResearchManager.GetProject() == null)
                {
                    ResearchPick rp = r.research[0];
                    r.tips.Add(new Tip
                    {
                        level = Level.Important, group = "research",
                        title = T("CA_T_NoResearch"),
                        detail = T("CA_T_NoResearchD", rp.def.LabelCap.Resolve(), rp.reason),
                        actLabel = T("CA_Btn_Research"),
                        act = () => StartResearch(rp.def)
                    });
                }
                if (r.stages.Count > 0)
                {
                    Stage st = r.stages[r.CurrentStage];
                    Check next = st.checks.FirstOrDefault(c => !c.done);
                    if (next != null)
                        r.tips.Add(new Tip { level = Level.Tip, group = "progress", title = T("CA_T_NextStep", st.title), detail = next.label + (next.hint.NullOrEmpty() ? "" : " – " + next.hint) });
                }
                Ending goal = Goal(r);
                if (goal?.next != null)
                    r.tips.Add(new Tip { level = Level.Important, group = "goal", title = T("CA_T_Goal", goal.title, goal.next.label), detail = goal.next.hint, actLabel = T("CA_Btn_Goal"), act = () => AdvisorWindow.ShowTab(3) });
                foreach (QuestRating q in r.quests.Where(q => q.verdict == 0 && q.hoursLeft >= 0f && q.hoursLeft < 24f).Take(2))
                {
                    Quest qq = q.quest;
                    r.tips.Add(new Tip { level = Level.Important, group = "quest", title = T("CA_T_QuestExpiring", qq.name), detail = q.why, actLabel = T("CA_Btn_OpenQuest"), act = () => OpenQuest(qq) });
                }
            });
            r.tips = r.tips.OrderBy(t => (int)t.level).ToList();
            return r;
        }

        private static void Safe(Action a)
        {
            try { a(); }
            catch (Exception e) { Log.WarningOnce("[Colony Advisor] part of the analysis failed: " + e.Message, e.Message.GetHashCode() ^ 0x5A41); }
        }

        // ---------------- dangers ----------------
        private static void Dangers(Map map, List<Pawn> cols, Report r)
        {
            if (GenHostility.AnyHostileActiveThreatToPlayer(map))
            {
                Pawn enemy = map.mapPawns.AllPawnsSpawned.FirstOrDefault(p => p.HostileTo(Faction.OfPlayer) && !p.Downed && !p.Fogged() && GenHostility.IsActiveThreatToPlayer(p));
                int n = map.mapPawns.AllPawnsSpawned.Count(p => p.HostileTo(Faction.OfPlayer) && !p.Downed && !p.Fogged());
                int drafted = cols.Count(p => p.Drafted);
                r.tips.Add(new Tip
                {
                    level = Level.Urgent, group = "danger",
                    title = T("CA_T_Enemies", n),
                    detail = T("CA_T_EnemiesD", drafted, r.fighters),
                    target = enemy != null ? new GlobalTargetInfo(enemy) : GlobalTargetInfo.Invalid
                });
            }
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Where(f => map.areaManager.Home[f.Position]).ToList();
            if (fires.Count > 0)
                r.tips.Add(new Tip { level = Level.Urgent, group = "danger", title = T("CA_T_Fire", fires.Count), detail = T("CA_T_FireD"), target = new GlobalTargetInfo(fires[0]) });

            foreach (Pawn p in cols)
            {
                if (p.health?.hediffSet == null) continue;
                float bleed = p.health.hediffSet.BleedRateTotal;
                if (p.Downed || bleed > 0.3f)
                    r.tips.Add(new Tip { level = Level.Urgent, group = "health", title = T("CA_T_Downed", p.LabelShort), detail = bleed > 0.1f ? T("CA_T_Bleeding", bleed.ToStringPercent()) : T("CA_T_DownedD"), target = new GlobalTargetInfo(p) });
                Hediff cold = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Hypothermia);
                Hediff hot = p.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.Heatstroke);
                if ((cold != null && cold.Severity > 0.2f) || (hot != null && hot.Severity > 0.2f))
                    r.tips.Add(new Tip { level = Level.Urgent, group = "health", title = T(cold != null ? "CA_T_Cold" : "CA_T_Hot", p.LabelShort), detail = T(cold != null ? "CA_T_ColdD" : "CA_T_HotD"), target = new GlobalTargetInfo(p) });
                if (p.needs?.food != null && p.needs.food.Starving)
                    r.tips.Add(new Tip { level = Level.Urgent, group = "food", title = T("CA_T_Starving", p.LabelShort), detail = T("CA_T_StarvingD"), target = new GlobalTargetInfo(p) });
                var mb = p.mindState?.mentalBreaker;
                if (mb != null && !p.InMentalState && (mb.BreakMajorIsImminent || mb.BreakExtremeIsImminent))
                    r.tips.Add(new Tip { level = Level.Urgent, group = "mood", title = T("CA_T_Break", p.LabelShort), detail = T("CA_T_BreakD"), target = new GlobalTargetInfo(p) });
                else if (mb != null && !p.InMentalState && mb.BreakMinorIsImminent)
                    r.tips.Add(new Tip { level = Level.Important, group = "mood", title = T("CA_T_Mood", p.LabelShort), detail = T("CA_T_MoodD"), target = new GlobalTargetInfo(p) });
            }
        }

        // ---------------- food and medicine ----------------
        private static readonly string[] MealNames = { "MealSimple", "MealFine", "MealLavish", "MealSurvivalPack", "MealNutrientPaste", "MealFine_Veg", "MealFine_Meat", "MealLavish_Veg", "MealLavish_Meat", "Pemmican" };

        private static void FoodAndHealth(Map map, List<Pawn> cols, Report r)
        {
            int n = Math.Max(1, cols.Count);
            r.nutrition = map.resourceCounter.TotalHumanEdibleNutrition;
            r.foodDays = r.nutrition / (n * 1.6f);
            r.meals = MealNames.Select(s => DefDatabase<ThingDef>.GetNamedSilentFail(s)).Where(d => d != null).Sum(d => map.resourceCounter.GetCount(d));
            r.medicine = map.resourceCounter.GetCountIn(ThingCategoryDefOf.Medicine);

            if (cols.Count > 0 && r.foodDays < 2f)
                r.tips.Add(new Tip { level = Level.Urgent, group = "food", title = T("CA_T_FoodLow", r.foodDays.ToString("0.#")), detail = T("CA_T_FoodLowD") });
            else if (cols.Count > 0 && r.foodDays < 6f)
                r.tips.Add(new Tip { level = Level.Important, group = "food", title = T("CA_T_FoodSome", r.foodDays.ToString("0.#")), detail = T("CA_T_FoodSomeD") });

            int needTend = cols.Count(p => p.health.HasHediffsNeedingTend());
            if (needTend > 0 && r.medicine == 0)
                r.tips.Add(new Tip { level = Level.Urgent, group = "health", title = T("CA_T_NoMeds", needTend), detail = T("CA_T_NoMedsD") });
            else if (cols.Count > 0 && r.medicine < cols.Count)
                r.tips.Add(new Tip { level = Level.Important, group = "health", title = T("CA_T_FewMeds", r.medicine), detail = T("CA_T_FewMedsD", cols.Count * 2) });

            // raw food that rots in the warm: a freezer would save it
            int warmRot = 0;
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
            {
                if (!t.Spawned || t.IsForbidden(Faction.OfPlayer)) continue;
                var rot = t.TryGetComp<CompRottable>();
                if (rot == null || rot.PropsRot == null || rot.PropsRot.daysToRotStart > 20f) continue;
                if (t.Position.GetSlotGroup(map) == null) continue;
                if (GenTemperature.GetTemperatureForCell(t.Position, map) > 0f) warmRot += t.stackCount;
            }
            if (warmRot >= 40)
                r.tips.Add(new Tip { level = Level.Important, group = "build", title = T("CA_T_Freezer", warmRot), detail = T("CA_T_FreezerD") });
        }

        // ---------------- who does the work ----------------
        private static void Work(Map map, List<Pawn> cols, Report r)
        {
            if (cols.Count == 0) return;
            void NoOne(WorkTypeDef wt, string key, Level lvl)
            {
                if (wt == null) return;
                if (!cols.Any(p => p.workSettings != null && p.workSettings.EverWork && !p.WorkTypeIsDisabled(wt) && p.workSettings.WorkIsActive(wt)))
                    r.tips.Add(new Tip { level = lvl, group = "work", title = T("CA_T_NoWorker", wt.labelShort ?? wt.label), detail = T(key), actLabel = T("CA_Btn_Work"), act = () => OpenTab("Work") });
            }
            NoOne(DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking"), "CA_W_Cooking", Level.Important);
            NoOne(WorkTypeDefOf.Doctor, "CA_W_Doctor", Level.Important);
            NoOne(WorkTypeDefOf.Firefighter, "CA_W_Fire", Level.Important);
            NoOne(WorkTypeDefOf.Hauling, "CA_W_Hauling", Level.Tip);
            NoOne(WorkTypeDefOf.Growing, "CA_W_Growing", Level.Tip);
            if (map.listerBuildings.allBuildingsColonist.Any(b => b.def.building != null && b.def.thingClass != null && typeof(Building_ResearchBench).IsAssignableFrom(b.def.thingClass)))
                NoOne(WorkTypeDefOf.Research, "CA_W_Research", Level.Tip);
        }

        // ---------------- beds, power, defence ----------------
        private static void Living(Map map, List<Pawn> cols, Report r)
        {
            int noBed = cols.Count(p => p.ownership != null && p.ownership.OwnedBed == null);
            if (noBed > 0)
                r.tips.Add(new Tip { level = Level.Important, group = "build", title = T("CA_T_NoBed", noBed), detail = T("CA_T_NoBedD"), actLabel = T("CA_Btn_Architect"), act = () => OpenTab("Architect") });
            int outside = cols.Count(p => p.ownership?.OwnedBed != null && !p.ownership.OwnedBed.Position.Roofed(map));
            if (outside > 0)
                r.tips.Add(new Tip { level = Level.Important, group = "build", title = T("CA_T_BedOutside", outside), detail = T("CA_T_BedOutsideD") });

            // power: devices that want to run but have no power
            int unpowered = 0;
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                var pt = b.TryGetComp<CompPowerTrader>();
                if (pt == null || pt.PowerNet == null || pt.PowerOn) continue;
                var fl = b.TryGetComp<CompFlickable>();
                if (fl != null && !fl.SwitchIsOn) continue;
                if (pt.PowerOutput >= 0f) continue;
                unpowered++;
            }
            if (unpowered > 0)
                r.tips.Add(new Tip { level = Level.Important, group = "build", title = T("CA_T_Power", unpowered), detail = T("CA_T_PowerD") });

            // defence: after the first two weeks there should be something
            int days = GenDate.DaysPassed;
            int turrets = map.listerBuildings.allBuildingsColonist.Count(b => b is Building_Turret);
            if (days >= 15 && turrets == 0 && cols.Count >= 3)
                r.tips.Add(new Tip { level = Level.Tip, group = "build", title = T("CA_T_Defence"), detail = T("CA_T_DefenceD") });

            // fighters without a weapon
            int unarmed = cols.Count(p => !p.WorkTagIsDisabled(WorkTags.Violent) && p.equipment != null && p.equipment.Primary == null);
            if (unarmed > 0)
                r.tips.Add(new Tip { level = days >= 5 ? Level.Important : Level.Tip, group = "build", title = T("CA_T_Unarmed", unarmed), detail = T("CA_T_UnarmedD") });
        }

        // ---------------- what to make ----------------
        private static void Needs(Map map, List<Pawn> cols, Report r)
        {
            int n = Math.Max(1, cols.Count);

            // meals: about a day and a half ahead
            ThingDef fine = DefDatabase<ThingDef>.GetNamedSilentFail("MealFine");
            bool goodCook = cols.Any(p => p.skills != null && p.skills.GetSkill(SkillDefOf.Cooking).Level >= 6 && !p.WorkTypeIsDisabled(DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking") ?? WorkTypeDefOf.Hauling));
            ThingDef meal = goodCook && fine != null && Recipe(map, fine) != null ? fine : ThingDefOf.MealSimple;
            AddNeed(map, r, meal, r.meals, n * 3, T("CA_N_Meals", n), r.meals < n);

            // medicine
            if (RecipesFor(ThingDefOf.MedicineIndustrial).Any(d => d.AvailableNow))
                AddNeed(map, r, ThingDefOf.MedicineIndustrial, r.medicine, n * 2, T("CA_N_Medicine", n * 2), r.medicine < n);

            // clothes: torn or missing
            int torn = cols.Count(p => p.apparel != null && p.apparel.WornApparel.Any(a => a.def.useHitPoints && a.HitPoints < a.MaxHitPoints * 0.5f));
            int noShirt = cols.Count(p => p.apparel != null && !p.apparel.WornApparel.Any(a => a.def.apparel.CoversBodyPart(p.RaceProps.body.corePart)));
            float winter = map.mapTemperature.SeasonalTemp;
            try { winter = Mathf.Min(winter, Find.World.tileTemperatures.GetOutdoorTemp(map.Tile)); } catch { }
            if (winter < -8f)
            {
                ThingDef parka = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Parka");
                int parkas = cols.Count(p => p.apparel != null && p.apparel.WornApparel.Any(a => a.def == parka));
                if (parka != null && parkas < cols.Count)
                    AddNeed(map, r, parka, parkas, cols.Count, T("CA_N_Parka", winter.ToStringTemperature()), winter < -20f);
            }
            if (torn + noShirt > 0)
            {
                ThingDef shirt = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt") ?? DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_TribalA");
                if (shirt != null && Recipe(map, shirt) == null) shirt = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_TribalA") ?? shirt;
                if (shirt != null)
                    AddNeed(map, r, shirt, map.resourceCounter.GetCount(shirt), torn + noShirt, T("CA_N_Clothes", torn, noShirt), noShirt > 0);
            }

            // weapons for fighters without one
            int unarmed = cols.Count(p => !p.WorkTagIsDisabled(WorkTags.Violent) && p.equipment != null && p.equipment.Primary == null);
            if (unarmed > 0)
            {
                ThingDef best = null; float bestV = -1f;
                foreach (RecipeDef rd in DefDatabase<RecipeDef>.AllDefs)
                {
                    if (!rd.AvailableNow || rd.products == null || rd.products.Count != 1) continue;
                    ThingDef w = rd.products[0].thingDef;
                    if (w == null || !w.IsWeapon || w.IsStuff || w.destroyOnDrop) continue;
                    if (rd.recipeUsers == null || !rd.recipeUsers.Any(u => u.IsResearchFinished)) continue;
                    float v = w.BaseMarketValue;
                    if (w.IsRangedWeapon) v *= 1.3f;
                    if (v > bestV) { bestV = v; best = w; }
                }
                if (best != null)
                    AddNeed(map, r, best, 0, unarmed, T("CA_N_Weapons", unarmed), GenDate.DaysPassed >= 5);
            }

            // components once fabrication is known
            ResearchProjectDef fab = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("Fabrication");
            if (fab != null && fab.IsFinished)
                AddNeed(map, r, ThingDefOf.ComponentIndustrial, map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial), 10, T("CA_N_Components"), false);

            // chemfuel for generators that burn it
            int chemUsers = map.listerBuildings.allBuildingsColonist.Count(b => b.TryGetComp<CompRefuelable>()?.Props?.fuelFilter?.Allows(ThingDefOf.Chemfuel) == true && !b.TryGetComp<CompRefuelable>().Props.fuelFilter.Allows(ThingDefOf.WoodLog));
            if (chemUsers > 0)
                AddNeed(map, r, ThingDefOf.Chemfuel, map.resourceCounter.GetCount(ThingDefOf.Chemfuel), 40 * chemUsers, T("CA_N_Chemfuel", chemUsers), false);

            r.needs = r.needs.Where(x => x.have < x.want).OrderByDescending(x => x.urgent).ThenBy(x => (float)x.have / Math.Max(1, x.want)).ToList();
            foreach (Need nd in r.needs.Where(x => x.urgent && x.existing == null && x.bench != null).Take(2))
            {
                Need cap = nd;
                r.tips.Add(new Tip { level = Level.Important, group = "make", title = T("CA_T_Make", cap.product.LabelCap.Resolve(), cap.want - cap.have), detail = cap.reason, actLabel = T("CA_Btn_AddBill"), act = () => AddBill(cap) });
            }
        }

        // 0.1.0: recipes by product, built once (big mod lists have thousands of recipes)
        private static Dictionary<ThingDef, List<RecipeDef>> byProduct;
        private static List<RecipeDef> RecipesFor(ThingDef product)
        {
            if (byProduct == null)
            {
                byProduct = new Dictionary<ThingDef, List<RecipeDef>>();
                foreach (RecipeDef rd in DefDatabase<RecipeDef>.AllDefs)
                {
                    if (rd.products == null) continue;
                    foreach (ThingDefCountClass p in rd.products)
                    {
                        if (p?.thingDef == null) continue;
                        if (!byProduct.TryGetValue(p.thingDef, out List<RecipeDef> l)) byProduct[p.thingDef] = l = new List<RecipeDef>();
                        l.Add(rd);
                    }
                }
            }
            return byProduct.TryGetValue(product, out List<RecipeDef> res) ? res : new List<RecipeDef>();
        }

        public static RecipeDef Recipe(Map map, ThingDef product)
        {
            RecipeDef best = null;
            foreach (RecipeDef rd in RecipesFor(product))
            {
                if (!rd.AvailableNow) continue;
                if (rd.recipeUsers == null || rd.recipeUsers.Count == 0) continue;
                // prefer one we can make here, then single (not bulk) recipes
                bool here = Benches(map, rd).Any();
                if (best == null || (here && !Benches(map, best).Any()) || (here == Benches(map, best).Any() && rd.products[0].count < best.products[0].count))
                    best = rd;
            }
            return best;
        }

        public static IEnumerable<Building_WorkTable> Benches(Map map, RecipeDef rd)
        {
            foreach (Building b in map.listerBuildings.allBuildingsColonist)
            {
                if (b is Building_WorkTable wt && b.Spawned && wt.def.AllRecipes != null && wt.def.AllRecipes.Contains(rd))
                    yield return wt;
            }
        }

        private static void AddNeed(Map map, Report r, ThingDef product, int have, int want, string reason, bool urgent)
        {
            if (product == null || want <= 0) return;
            var nd = new Need { product = product, have = have, want = want, reason = reason, urgent = urgent };
            nd.recipe = Recipe(map, product);
            if (nd.recipe != null)
            {
                foreach (Building_WorkTable b in Benches(map, nd.recipe))
                {
                    if (nd.bench == null) nd.bench = b;
                    Bill_Production ex = b.BillStack?.Bills?.OfType<Bill_Production>().FirstOrDefault(x => x.recipe == nd.recipe && !x.suspended);
                    if (ex != null) { nd.existing = ex; nd.bench = b; break; }
                }
                if (nd.bench == null)
                    nd.benchToBuild = nd.recipe.recipeUsers.FirstOrDefault(u => u.BuildableByPlayer && u.IsResearchFinished) ?? nd.recipe.recipeUsers.FirstOrDefault();
            }
            r.needs.Add(nd);
        }

        public static void AddBill(Need nd)
        {
            try
            {
                if (nd.bench == null || nd.recipe == null) return;
                if (nd.existing != null)
                {
                    nd.existing.repeatMode = BillRepeatModeDefOf.TargetCount;
                    nd.existing.targetCount = Math.Max(nd.existing.targetCount, nd.want);
                }
                else
                {
                    var bill = new Bill_Production(nd.recipe) { repeatMode = BillRepeatModeDefOf.TargetCount, targetCount = nd.want };
                    nd.bench.BillStack.AddBill(bill);
                    nd.existing = bill;
                }
                Messages.Message("CA_M_BillAdded".Translate(nd.product.LabelCap, nd.want, nd.bench.LabelCap), new LookTargets(nd.bench), MessageTypeDefOf.PositiveEvent, false);
                Invalidate();
            }
            catch (Exception e) { Log.Warning("[Colony Advisor] could not add the bill: " + e.Message); }
        }

        // ---------------- research ----------------
        // defName, base score, condition → extra score and reason key
        private static void Research(Map map, List<Pawn> cols, Report r)
        {
            var picks = new Dictionary<ResearchProjectDef, ResearchPick>();
            void Want(string defName, float score, string reasonKey)
            {
                ResearchProjectDef d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(defName);
                if (d == null || d.IsFinished) return;
                // not startable yet: recommend the first missing prerequisite instead
                int guard = 0;
                while (d != null && !d.CanStartNow && guard++ < 6)
                {
                    ResearchProjectDef pre = d.prerequisites?.FirstOrDefault(p => !p.IsFinished) ?? d.hiddenPrerequisites?.FirstOrDefault(p => !p.IsFinished);
                    if (pre == null) break;
                    reasonKey = reasonKey + "|" + d.LabelCap.Resolve();
                    d = pre;
                    score -= 5f;
                }
                if (d == null || d.IsFinished || !d.CanStartNow || d.knowledgeCategory != null) return;
                string reason = reasonKey.Contains("|") ? T(reasonKey.Split('|')[0]) + " " + T("CA_R_First", reasonKey.Split('|').Last()) : T(reasonKey);
                if (picks.TryGetValue(d, out ResearchPick have))
                {
                    if (have.score >= score) return;
                }
                picks[d] = new ResearchPick { def = d, score = score, reason = reason };
            }

            bool hasPower = map.listerBuildings.allBuildingsColonist.Any(b => b.TryGetComp<CompPowerPlant>() != null);
            float hot = map.mapTemperature.SeasonalTemp;
            int n = cols.Count;
            int days = GenDate.DaysPassed;

            // the basics of every colony
            Want("Electricity", hasPower ? 40f : 90f, "CA_R_Electricity");
            Want("Batteries", hasPower ? 60f : 30f, "CA_R_Batteries");
            Want("AirConditioning", r.foodDays > 4f ? 70f : 55f, "CA_R_Cooling");
            Want("PassiveCooler", 45f, "CA_R_PassiveCooler");
            Want("Stonecutting", 50f, "CA_R_Stonecutting");
            Want("ComplexFurniture", 35f, "CA_R_Furniture");
            Want("Smithing", 40f, "CA_R_Smithing");
            Want("ComplexClothing", hot < 0f ? 65f : 30f, "CA_R_Clothing");
            Want("Brewing", 25f, "CA_R_Brewing");
            Want("PsychoidBrewing", 15f, "CA_R_Psychoid");
            Want("SolarPanels", 45f, "CA_R_Solar");
            Want("WatermillGenerator", 35f, "CA_R_Watermill");
            Want("NutrientPaste", r.foodDays < 5f ? 45f : 15f, "CA_R_Paste");
            Want("Hydroponics", hot < 0f ? 55f : 25f, "CA_R_Hydroponics");
            Want("PackagedSurvivalMeal", 20f, "CA_R_Survival");
            Want("MicroelectronicsBasics", days > 20 ? 80f : 50f, "CA_R_Micro");
            Want("Machining", days > 15 ? 70f : 45f, "CA_R_Machining");
            Want("Gunsmithing", r.fighters > 0 ? 55f : 30f, "CA_R_Guns");
            Want("GunTurrets", days > 15 ? 75f : 45f, "CA_R_Turrets");
            Want("Mortars", 30f, "CA_R_Mortars");
            Want("DrugProduction", 30f, "CA_R_Drugs");
            Want("MedicineProduction", r.medicine < n * 2 ? 70f : 40f, "CA_R_Medicine");
            Want("Fabrication", days > 40 ? 75f : 40f, "CA_R_Fabrication");
            Want("MultiAnalyzer", days > 40 ? 65f : 30f, "CA_R_MultiAnalyzer");
            Want("FlakArmor", 50f, "CA_R_Flak");
            Want("Prosthetics", 30f, "CA_R_Prosthetics");
            Want("GeothermalPower", 35f, "CA_R_Geothermal");
            Want("TransportPod", 20f, "CA_R_Pods");
            Want("ShipBasics", days > 60 ? 45f : 10f, "CA_R_Ship");
            Want("Refining", 30f, "CA_R_Refining");

            // anything else that can be started: cheap ones first, a little
            foreach (ResearchProjectDef d in DefDatabase<ResearchProjectDef>.AllDefs)
            {
                if (picks.ContainsKey(d) || d.IsFinished || !d.CanStartNow || d.knowledgeCategory != null) continue;
                float s = 20f - Mathf.Min(18f, d.CostApparent / 400f);
                picks[d] = new ResearchPick { def = d, score = s, reason = T("CA_R_Cheap", d.CostApparent.ToString("0")) };
            }
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            r.research = picks.Values.OrderByDescending(p => p.score + (p.def == cur ? 5f : 0f)).Take(12).ToList();
        }

        public static void StartResearch(ResearchProjectDef d)
        {
            try
            {
                Find.ResearchManager.SetCurrentProject(d);
                Messages.Message("CA_M_Research".Translate(d.LabelCap), MessageTypeDefOf.NeutralEvent, false);
                Invalidate();
            }
            catch (Exception e) { Log.Warning("[Colony Advisor] could not start the research: " + e.Message); }
        }

        // ---------------- quests ----------------
        private static void Quests(Map map, List<Pawn> cols, Report r)
        {
            int strength = r.strength;
            foreach (Quest q in Find.QuestManager.QuestsListForReading)
            {
                if (q == null || q.hidden || q.State != QuestState.NotYetAccepted) continue;
                var qr = new QuestRating { quest = q, stars = Math.Max(0, q.challengeRating) };
                foreach (QuestPart_Choice ch in q.PartsListForReading.OfType<QuestPart_Choice>())
                    foreach (var c in ch.choices)
                        qr.value = Mathf.Max(qr.value, c.rewards.Sum(rw => { try { return rw.TotalMarketValue; } catch { return 0f; } }));
                try { int t = q.TicksUntilExpiry; if (t > 0) qr.hoursLeft = t / 2500f; } catch { }
                int diff = qr.stars - strength;
                if (qr.stars <= 0) { qr.verdict = qr.value > 0 ? 0 : 1; qr.why = T("CA_Q_Safe"); }
                else if (diff <= 0) { qr.verdict = 0; qr.why = T("CA_Q_Ok", qr.stars, strength); }
                else if (diff == 1) { qr.verdict = 1; qr.why = T("CA_Q_Maybe", qr.stars, strength); }
                else { qr.verdict = 2; qr.why = T("CA_Q_No", qr.stars, strength); }
                if (qr.value > 0) qr.why += " " + T("CA_Q_Value", qr.value.ToStringMoney());
                r.quests.Add(qr);
            }
            r.quests = r.quests.OrderBy(x => x.verdict).ThenByDescending(x => x.value).ToList();
        }

        // 1 = weak, 2 = fair, 3 = strong (same scale as the quest stars)
        public static int Strength(Map map, List<Pawn> cols)
        {
            float s = 0f;
            foreach (Pawn p in cols)
            {
                if (p.WorkTagIsDisabled(WorkTags.Violent) || p.Downed) continue;
                float w = p.equipment?.Primary == null ? 0.3f : (p.equipment.Primary.def.IsRangedWeapon ? 1f : 0.7f);
                float armor = 0f;
                try { armor = p.GetStatValue(StatDefOf.ArmorRating_Sharp); } catch { }
                s += w * (1f + Mathf.Min(1f, armor));
            }
            s += map.listerBuildings.allBuildingsColonist.Count(b => b is Building_Turret) * 0.7f;
            return s >= 9f ? 3 : s >= 4f ? 2 : 1;
        }

        public static void OpenQuest(Quest q)
        {
            try
            {
                Find.MainTabsRoot.SetCurrentTab(MainButtonDefOf.Quests);
                (MainButtonDefOf.Quests.TabWindow as MainTabWindow_Quests)?.Select(q);
            }
            catch (Exception e) { Log.Warning("[Colony Advisor] could not open the quest: " + e.Message); }
        }

        public static void OpenTab(string defName)
        {
            try
            {
                MainButtonDef d = DefDatabase<MainButtonDef>.GetNamedSilentFail(defName);
                if (d != null) Find.MainTabsRoot.SetCurrentTab(d);
            }
            catch { }
        }

        // ---------------- the way through the game ----------------
        private static bool Done(string research) { var d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(research); return d == null || d.IsFinished; }
        private static bool Has(string research) => DefDatabase<ResearchProjectDef>.GetNamedSilentFail(research) != null;
        private static bool Built(Map map, string thingDef) { var d = DefDatabase<ThingDef>.GetNamedSilentFail(thingDef); return d != null && map.listerBuildings.AllBuildingsColonistOfDef(d).Any(); }

        private static void Stages(Map map, List<Pawn> cols, Report r)
        {
            int n = cols.Count;
            List<Building> all = map.listerBuildings.allBuildingsColonist;
            Stage S(string key) { var s = new Stage { title = T("CA_S_" + key), goal = T("CA_S_" + key + "_Goal") }; r.stages.Add(s); return s; }
            void C(Stage s, bool done, string key, params NamedArgument[] a) => s.checks.Add(new Check { done = done, label = T("CA_C_" + key, a), hint = T("CA_C_" + key + "_H") });

            Stage s1 = S("Survive");
            C(s1, n > 0 && cols.All(p => p.ownership?.OwnedBed != null), "Beds");
            C(s1, n > 0 && cols.All(p => p.ownership?.OwnedBed == null || p.ownership.OwnedBed.Position.Roofed(map)), "Roof");
            C(s1, r.foodDays >= 5f, "Food5");
            C(s1, all.Any(b => b.def.AllRecipes != null && b.def.AllRecipes.Any(rd => rd.products.Any(p => p.thingDef == ThingDefOf.MealSimple))), "Stove");
            C(s1, Find.ResearchManager.GetProject() != null || all.Any(b => b is Building_ResearchBench), "Research");

            Stage s2 = S("Stable");
            bool freezer = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree).Any(t => t.Spawned && t.Position.GetSlotGroup(map) != null && GenTemperature.GetTemperatureForCell(t.Position, map) <= 0f && t.Position.Roofed(map));
            C(s2, freezer, "Freezer");
            C(s2, all.Any(b => b.TryGetComp<CompPowerPlant>() != null), "Power");
            C(s2, r.medicine >= Math.Max(2, n * 2), "Medicine", Math.Max(2, n * 2));
            C(s2, all.Any(b => b is Building_Bed bed && bed.Medical), "Hospital");
            C(s2, all.Any(b => b is Building_Turret) || all.Count(b => b.def.defName == "Sandbags" || b.def.defName.StartsWith("Barricade")) >= 8, "Defence");

            Stage s3 = S("Grow");
            C(s3, n >= 6, "Six", n);
            C(s3, map.zoneManager.AllZones.Any(z => z is Zone_Growing), "Fields");
            C(s3, Done("MicroelectronicsBasics"), "Micro");
            C(s3, Built(map, "CommsConsole"), "Comms");
            C(s3, r.fighters > 0 && cols.Where(p => !p.WorkTagIsDisabled(WorkTags.Violent)).All(p => p.equipment?.Primary != null), "Armed");

            Stage s4 = S("Strong");
            C(s4, Done("Fabrication"), "Fabrication");
            C(s4, Done("MultiAnalyzer"), "MultiAnalyzer");
            C(s4, all.Count(b => b is Building_Turret) >= 3, "Turrets");
            int armored = cols.Count(p => { try { return p.GetStatValue(StatDefOf.ArmorRating_Sharp) >= 0.35f; } catch { return false; } });
            C(s4, r.fighters > 0 && armored * 2 >= r.fighters, "Armor");
            C(s4, map.wealthWatcher.WealthTotal >= 150000f, "Wealth", map.wealthWatcher.WealthTotal.ToStringMoney());
        }

        // 0.1.0: the ways to finish the game. Every step says what to do next; the chosen goal
        // (settings or the "Goal" tab) gives the "next step" shown on top of the advisor.
        private static bool WorldHas(string defName) { try { return Find.WorldObjects.AllWorldObjects.Any(o => o.def?.defName == defName); } catch { return false; } }

        private static void Endings(Map map, Report r)
        {
            Ending E(string key, bool avail) { var e = new Ending { key = key, title = T("CA_E_" + key), desc = T("CA_E_" + key + "_D"), available = avail }; r.endings.Add(e); return e; }
            void Step(Ending e, bool done, string key, params NamedArgument[] a) => e.steps.Add(new Check { done = done, label = T("CA_ES_" + key, a), hint = T("CA_ES_" + key + "_H") });
            float wealth = map.wealthWatcher.WealthTotal;
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned.ToList();

            // spaceship (vanilla)
            Ending ship = E("Ship", Has("ShipBasics"));
            Step(ship, Done("Electricity") && Done("MicroelectronicsBasics"), "ShipBase");
            Step(ship, Done("Fabrication") && Done("MultiAnalyzer"), "ShipFab");
            foreach (string rs in new[] { "ShipBasics", "ShipCryptosleep", "ShipReactor", "ShipEngine", "ShipComputerCore", "ShipSensorCluster" })
            {
                var d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(rs);
                if (d != null) Step(ship, d.IsFinished, "Research", d.LabelCap.Resolve());
            }
            Step(ship, map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial) >= 20 || Built(map, "Ship_Reactor"), "Components");
            Step(ship, Built(map, "Ship_Beam") && Built(map, "Ship_CryptosleepCasket"), "Hull");
            Step(ship, Built(map, "Ship_ComputerCore") && Built(map, "Ship_SensorCluster"), "Core");
            Step(ship, Built(map, "Ship_Engine"), "Engine");
            Step(ship, Built(map, "Ship_Reactor"), "Reactor");
            Step(ship, false, "Launch");

            // journey offer: an old ship somewhere in the world
            Ending journey = E("Journey", true);
            bool found = WorldHas("EscapeShip");
            Step(journey, found || Find.QuestManager.QuestsListForReading.Any(q => q.root?.defName != null && q.root.defName.Contains("Journey")), "Offer");
            Step(journey, Done("PackagedSurvivalMeal") || map.resourceCounter.GetCount(DefDatabase<ThingDef>.GetNamedSilentFail("MealSurvivalPack") ?? ThingDefOf.MealSimple) >= 20, "Supplies");
            Step(journey, cols.Count(p => p.equipment?.Primary != null) >= Math.Min(4, Math.Max(1, cols.Count)), "Arms");
            Step(journey, found, "FindShip");
            Step(journey, false, "Defend");

            if (ModsConfig.RoyaltyActive)
            {
                Ending roy = E("Royal", true);
                bool titled = false, high = false;
                try
                {
                    titled = cols.Any(p => p.royalty != null && p.royalty.AllTitlesForReading.Count > 0);
                    high = cols.Any(p => p.royalty != null && p.royalty.AllTitlesForReading.Any(t => t.def != null && t.def.seniority >= 600));
                }
                catch { }
                Step(roy, titled, "Title");
                Step(roy, Built(map, "Throne") || Built(map, "GrandThrone"), "Throne");
                Step(roy, high, "HighTitle");
                Step(roy, Find.QuestManager.QuestsListForReading.Any(q => q.root?.defName != null && q.root.defName.Contains("Ascent")), "Ascent");
                Step(roy, false, "Stellarch");
            }
            if (ModsConfig.IdeologyActive)
            {
                Ending ide = E("Archonexus", true);
                Step(ide, wealth >= 175000f, "Wealth175", wealth.ToStringMoney());
                Step(ide, wealth >= 350000f, "Wealth350", wealth.ToStringMoney());
                Step(ide, WorldHas("Settlement_SecondArchonexusCycle") || WorldHas("Settlement_ThirdArchonexusCycle"), "Cycle2");
                Step(ide, WorldHas("Settlement_ThirdArchonexusCycle"), "Cycle3");
                Step(ide, false, "ArchCore");
            }
            if (ModsConfig.AnomalyActive)
            {
                Ending an = E("Void", true);
                int lvl = 0;
                try { lvl = Find.Anomaly.Level; } catch { }
                Step(an, lvl >= 1, "Activate");
                Step(an, Done("BasicPsychicRituals") || lvl >= 2, "Study");
                Step(an, lvl >= 2, "Level2");
                Step(an, lvl >= 3, "Level3");
                Step(an, false, "VoidEnd");
            }
            if (ModsConfig.OdysseyActive)
            {
                Ending od = E("Gravship", true);
                Step(od, Built(map, "GravEngine"), "GravEngine");
                Step(od, Built(map, "PilotConsole") || Built(map, "GravshipPilotConsole"), "Console");
                Step(od, WorldHas("Gravship") || WorldHas("GravshipLaunch"), "Fly");
                Step(od, false, "Mechhive");
            }
            foreach (Ending e in r.endings)
            {
                Check next = e.steps.FirstOrDefault(c => !c.done);
                e.next = next;
            }
        }

        public static Ending Goal(Report r)
        {
            string key = AdvisorMod.S?.goal ?? "";
            Ending e = r.endings.FirstOrDefault(x => x.key == key && x.available);
            return e ?? r.endings.Where(x => x.available).OrderByDescending(x => x.Progress).FirstOrDefault();
        }

        // ---------------- colony score 0..100 ----------------
        private static void Score(Map map, List<Pawn> cols, Report r)
        {
            int Part(string key, float v01, int weight) { int pts = Mathf.RoundToInt(Mathf.Clamp01(v01) * weight); r.scoreParts.Add((T(key), pts * 100 / Math.Max(1, weight))); return pts; }
            int total = 0;
            total += Part("CA_Sc_Food", r.foodDays / 10f, 20);
            float mood = cols.Count == 0 ? 0.5f : cols.Average(p => p.needs?.mood?.CurLevel ?? 0.5f);
            total += Part("CA_Sc_Mood", (mood - 0.25f) / 0.5f, 20);
            float hurt = cols.Count == 0 ? 0f : cols.Count(p => p.health.HasHediffsNeedingTend() || p.Downed) / (float)cols.Count;
            total += Part("CA_Sc_Health", 1f - hurt + Mathf.Min(0.3f, r.medicine / Math.Max(1f, cols.Count * 3f)) - 0.3f, 20);
            total += Part("CA_Sc_Safety", r.strength / 3f, 20);
            int doneChecks = r.stages.Sum(s => s.DoneCount), allChecks = Math.Max(1, r.stages.Sum(s => s.checks.Count));
            total += Part("CA_Sc_Progress", doneChecks / (float)allChecks, 20);
            r.score = Mathf.Clamp(total - r.UrgentCount * 5, 0, 100);
        }
    }
}
