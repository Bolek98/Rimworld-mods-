using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyAdvisor
{
    // ---------------------------------------------------------------------------
    // 0.3.0 "Story compass": the story journal, quest details and expiry alerts, the
    // expedition planner, cabin fever / "saw new places" thoughts and rumors.
    // Everything reads the game; only buttons, letters, thoughts and the optional
    // rumor quests change anything.
    // ---------------------------------------------------------------------------
    public class StoryLine
    {
        public string key, title;
        public bool active;
        public float progress;
        public string now;                                  // what to do now
        public List<string> needs = new List<string>();     // what it takes / where we are
        public List<Check> steps = new List<Check>();
        public GlobalTargetInfo target = GlobalTargetInfo.Invalid;
        public string actLabel;
        public Action act;
        public int order;
    }

    public class QuestTravel
    {
        public int tiles = -1;
        public float days = -1f;      // one way, on foot (caravan estimate)
        public GlobalTargetInfo target = GlobalTargetInfo.Invalid;
        public int tick;
    }

    public class Destination
    {
        public string label;
        public GlobalTargetInfo target;
        public PlanetTile tile;
        public int tiles;
        public float days;
        public Quest quest;
        public string kind;           // "quest", "site", "settlement"
    }

    public static class Compass
    {
        private static string T(string key, params NamedArgument[] a) => key.Translate(a).Resolve();
        private static bool Done(string research) { var d = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(research); return d != null && d.IsFinished; }
        private static ResearchProjectDef Res(string research) => DefDatabase<ResearchProjectDef>.GetNamedSilentFail(research);
        private static int CountBuilt(string def)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null) return 0;
            int n = 0;
            foreach (Map m in Find.Maps) if (m.IsPlayerHome) n += m.listerBuildings.AllBuildingsColonistOfDef(d).Count();
            return n;
        }
        private static Thing FirstBuilt(string def)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(def);
            if (d == null) return null;
            foreach (Map m in Find.Maps) if (m.IsPlayerHome) { var b = m.listerBuildings.AllBuildingsColonistOfDef(d).FirstOrDefault(); if (b != null) return b; }
            return null;
        }
        private static Quest QuestWithRoot(params string[] roots) =>
            Find.QuestManager.QuestsListForReading.FirstOrDefault(q => q.root != null && roots.Contains(q.root.defName) && (q.State == QuestState.NotYetAccepted || q.State == QuestState.Ongoing));

        // ---------------- the story journal ----------------
        private static List<StoryLine> journal;
        private static int journalTick = -999999;

        public static void Invalidate() { journalTick = -999999; }

        public static List<StoryLine> Journal(Report r)
        {
            int now = Find.TickManager.TicksGame;
            int every = Mathf.Max(1, AdvisorMod.S.journalHours) * 2500;
            if (journal != null && now - journalTick < every) return journal;
            var list = new List<StoryLine>();
            try { if (ModsConfig.AnomalyActive) list.Add(Anomaly()); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] journal (anomaly): " + e.Message, 0x5A41F1); }
            try { if (ModsConfig.OdysseyActive) list.Add(Gravship()); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] journal (gravship): " + e.Message, 0x5A41F2); }
            try { if (ModsConfig.BiotechActive) list.Add(Mechanitor(r)); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] journal (biotech): " + e.Message, 0x5A41F3); }
            try
            {
                foreach (Ending e in r.endings)
                {
                    if (!e.available || e.key == "Void" || e.key == "Gravship") continue;
                    list.Add(FromEnding(e));
                }
            }
            catch (Exception e) { Log.WarningOnce("[Colony Advisor] journal: " + e.Message, 0x5A41F4); }
            journal = list.Where(l => l != null)
                .OrderByDescending(l => l.active).ThenBy(l => l.order).ThenByDescending(l => l.progress).ToList();
            journalTick = now;
            return journal;
        }

        private static void Step(StoryLine l, bool done, string key, params NamedArgument[] a) =>
            l.steps.Add(new Check { done = done, label = T("CA_J_" + key, a), hint = T("CA_J_" + key + "_H") });

        private static void Finish(StoryLine l, float partial = 0f)
        {
            int done = l.steps.Count(s => s.done);
            l.progress = l.steps.Count == 0 ? 0f : Mathf.Clamp01((done + Mathf.Clamp01(partial)) / l.steps.Count);
        }

        private static StoryLine Anomaly()
        {
            var l = new StoryLine { key = "Void", title = T("CA_E_Void"), order = 0 };
            GameComponent_Anomaly an = Find.Anomaly;
            int lvl = an.Level;
            Building_VoidMonolith mono = an.monolith;
            var held = new List<Pawn>();
            foreach (Map m in Find.Maps)
                if (m.IsPlayerHome)
                    foreach (Building b in m.listerBuildings.allBuildingsColonist)
                        if (b is Building_HoldingPlatform hp && hp.HeldPawn != null) held.Add(hp.HeldPawn);
            l.active = lvl >= 1 || held.Count > 0 || an.hasBuiltHoldingPlatform;
            if (mono != null && mono.Spawned) l.target = mono;

            Step(l, mono != null || lvl >= 1, "Mono");
            Step(l, lvl >= 1, "Activate");
            Step(l, lvl >= 2, "Level2");
            Step(l, lvl >= 3 || an.QuestlineEnded, "Level3");
            Step(l, an.QuestlineEnded, "VoidEnd");

            float partial = 0f;
            MonolithLevelDef next = an.NextLevelDef;
            if (an.QuestlineEnded) l.now = T("CA_J_Void_Ended");
            else if (lvl == 0)
            {
                l.now = mono != null ? T("CA_J_Void_Now0") : T("CA_J_Void_NoMono");
            }
            else if (next != null && next.entityCatagoryCompletionRequired != null && next.entityCountCompletionRequired > 0)
            {
                int disc = Find.EntityCodex.DiscoveredCount(next.entityCatagoryCompletionRequired);
                int need = next.entityCountCompletionRequired;
                partial = (float)disc / need;
                l.needs.Add(T("CA_J_Void_Disc", disc, need, next.entityCatagoryCompletionRequired.label));
                l.now = disc < need ? T("CA_J_Void_NowDisc", need - disc, next.entityCatagoryCompletionRequired.label)
                                    : T("CA_J_Void_NowActivate", next.level);
            }
            else if (next != null) l.now = T("CA_J_Void_NowActivate", next.level);
            else l.now = T("CA_J_Void_NowFinal");

            if (held.Count > 0)
                l.needs.Add(T("CA_J_Void_Held", string.Join(", ", held.Select(p => p.LabelShortCap).Distinct().Take(6).ToArray())));
            else if (lvl >= 1) l.needs.Add(T("CA_J_Void_NoHeld"));
            try
            {
                ResearchProjectDef b = Find.ResearchManager.GetProject(KnowledgeCategoryDefOf.Basic);
                ResearchProjectDef a = Find.ResearchManager.GetProject(KnowledgeCategoryDefOf.Advanced);
                if (b != null || a != null)
                    l.needs.Add(T("CA_J_Void_Study", string.Join(", ", new[] { b, a }.Where(x => x != null).Select(x => x.LabelCap.Resolve() + " " + x.ProgressPercent.ToStringPercent()).ToArray())));
                else if (lvl >= 1)
                {
                    l.needs.Add(T("CA_J_Void_NoStudy"));
                    l.actLabel = T("CA_Btn_ResearchTab");
                    l.act = () => Advisor.OpenTab("Research");
                }
            }
            catch { }
            Finish(l, partial);
            return l;
        }

        private static StoryLine Gravship()
        {
            var l = new StoryLine { key = "Gravship", title = T("CA_E_Gravship"), order = 1 };
            bool research = Done("BasicGravtech");
            int engine = CountBuilt("GravEngine"), console = CountBuilt("PilotConsole"), tanks = CountBuilt("ChemfuelTank");
            int thrusters = CountBuilt("SmallThruster") + CountBuilt("LargeThruster");
            Quest engineQuest = QuestWithRoot("GravEngine");
            Quest coreQuest = QuestWithRoot("Gravcore_Mechhive");
            bool core = false;
            try
            {
                ThingDef gc = DefDatabase<ThingDef>.GetNamedSilentFail("Gravcore");
                core = gc != null && Find.Maps.Any(m => m.IsPlayerHome && m.listerThings.ThingsOfDef(gc).Any());
            }
            catch { }
            bool flown = Find.Maps.Count(m => m.IsPlayerHome) > 1 || Find.WorldObjects.AllWorldObjects.Any(o => o.GetType().Name.Contains("Gravship"));
            ResearchProjectDef cur = Find.ResearchManager.GetProject();
            l.active = engine > 0 || research || (cur != null && cur.defName == "BasicGravtech") || engineQuest != null;

            Step(l, engine > 0, "GravEngine");
            Step(l, research, "Gravtech");
            Step(l, console > 0, "Console");
            Step(l, thrusters > 0, "Thrusters", thrusters);
            Step(l, tanks > 0, "Tanks", tanks);
            Step(l, flown, "Fly");
            Step(l, core || coreQuest != null, "Gravcore");
            Step(l, false, "Mechhive");

            Thing eng = FirstBuilt("GravEngine");
            if (eng != null) l.target = eng;
            if (engine == 0)
            {
                if (engineQuest != null) { l.now = T("CA_J_Grav_NowQuest", engineQuest.name); l.actLabel = T("CA_Btn_OpenQuest"); Quest q = engineQuest; l.act = () => Advisor.OpenQuest(q); }
                else l.now = T("CA_J_Grav_NowFind");
            }
            else if (!research)
            {
                l.now = T("CA_J_Grav_NowResearch");
                ResearchProjectDef d = Res("BasicGravtech");
                if (d != null && cur != d) { l.actLabel = T("CA_Btn_Research"); l.act = () => Advisor.StartResearch(d); }
            }
            else if (console == 0) l.now = T("CA_J_Grav_NowConsole");
            else if (thrusters == 0 || tanks == 0) l.now = T("CA_J_Grav_NowThrust");
            else if (!flown) l.now = T("CA_J_Grav_NowFly");
            else if (!core && coreQuest == null) l.now = T("CA_J_Grav_NowCore");
            else if (coreQuest != null) { l.now = T("CA_J_Grav_NowCoreQuest", coreQuest.name); l.actLabel = T("CA_Btn_OpenQuest"); Quest q = coreQuest; l.act = () => Advisor.OpenQuest(q); }
            else l.now = T("CA_J_Grav_NowHive");
            if (engine > 0) l.needs.Add(T("CA_J_Grav_Parts", console, thrusters, tanks));
            Finish(l);
            return l;
        }

        private static StoryLine Mechanitor(Report r)
        {
            var l = new StoryLine { key = "Mech", title = T("CA_J_Mech_Title"), order = 5 };
            bool mechanitor = false;
            try { mechanitor = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists.Any(p => MechanitorUtility.IsMechanitor(p)); } catch { }
            l.active = mechanitor;
            Step(l, mechanitor, "Mechanitor");
            Step(l, Done("BasicMechtech"), "BasicMech");
            Step(l, Done("StandardMechtech"), "StandardMech");
            Step(l, Done("HighMechtech"), "HighMech");
            Step(l, Done("UltraMechtech"), "UltraMech");
            Check next = l.steps.FirstOrDefault(s => !s.done);
            l.now = next == null ? T("CA_J_AllDone") : next.hint;
            Finish(l);
            return l;
        }

        private static StoryLine FromEnding(Ending e)
        {
            var l = new StoryLine { key = e.key, title = e.title, order = 3 };
            l.steps.AddRange(e.steps);
            l.active = e.steps.Any(s => s.done) && e.key != "Ship" && e.key != "Journey";
            if (e.key == "Ship") l.active = e.steps.Count(s => s.done) >= 3;
            l.now = e.next == null ? T("CA_J_AllDone") : e.next.label + (e.next.hint.NullOrEmpty() ? "" : " – " + e.next.hint);
            if (e.key == "Journey")
            {
                Quest q = Find.QuestManager.QuestsListForReading.FirstOrDefault(x => x.root?.defName != null && x.root.defName.Contains("Journey") && x.State != QuestState.EndedFailed);
                if (q != null) { l.actLabel = T("CA_Btn_OpenQuest"); l.act = () => Advisor.OpenQuest(q); }
            }
            Finish(l);
            return l;
        }

        // ---------------- quest travel ----------------
        private static readonly Dictionary<int, QuestTravel> travel = new Dictionary<int, QuestTravel>();

        public static PlanetTile HomeTile()
        {
            Map m = Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome ? Find.CurrentMap : Find.AnyPlayerHomeMap;
            return m != null ? m.Tile : PlanetTile.Invalid;
        }

        public static float DaysTo(PlanetTile from, PlanetTile to)
        {
            try { return CaravanArrivalTimeEstimator.EstimatedTicksToArrive(from, to, null) / 60000f; }
            catch { return -1f; }
        }

        public static QuestTravel Travel(Quest q)
        {
            int now = Find.TickManager.TicksGame;
            if (travel.TryGetValue(q.id, out QuestTravel t) && now - t.tick < 15000) return t;
            t = new QuestTravel { tick = now };
            try
            {
                PlanetTile home = HomeTile();
                foreach (GlobalTargetInfo g in q.QuestLookTargets)
                {
                    if (!g.IsValid || !g.IsWorldTarget || !g.Tile.Valid || g.Tile == home) continue;
                    if (g.WorldObject is MapParent mp && mp.HasMap && mp.Map.IsPlayerHome) continue;
                    t.target = g;
                    if (home.Valid)
                    {
                        t.tiles = Mathf.RoundToInt(Find.WorldGrid.ApproxDistanceInTiles(home, g.Tile));
                        t.days = DaysTo(home, g.Tile);
                    }
                    break;
                }
            }
            catch { }
            travel[q.id] = t;
            return t;
        }

        public static string DaysText(float d) => d < 0f ? "?" : d < 1f ? (d * 24f).ToString("0") + " h" : d.ToString("0.#") + " d";

        // ---------------- expeditions ----------------
        public static List<Destination> Destinations(Report r)
        {
            var list = new List<Destination>();
            PlanetTile home = HomeTile();
            if (!home.Valid) return list;
            try
            {
                foreach (QuestRating qr in r.quests)
                {
                    QuestTravel t = Travel(qr.quest);
                    if (!t.target.IsValid) continue;
                    list.Add(new Destination { label = qr.quest.name, target = t.target, tile = t.target.Tile, tiles = t.tiles, days = t.days, quest = qr.quest, kind = "quest" });
                }
                foreach (Quest q in Find.QuestManager.QuestsListForReading.Where(q => q.State == QuestState.Ongoing && !q.hidden))
                {
                    QuestTravel t = Travel(q);
                    if (!t.target.IsValid || list.Any(d => d.tile == t.target.Tile)) continue;
                    list.Add(new Destination { label = q.name, target = t.target, tile = t.target.Tile, tiles = t.tiles, days = t.days, quest = q, kind = "quest" });
                }
                var others = new List<(WorldObject o, float dist, string kind)>();
                foreach (WorldObject o in Find.WorldObjects.AllWorldObjects)
                {
                    if (o.Faction == Faction.OfPlayer || list.Any(d => d.tile == o.Tile)) continue;
                    if (o.Tile.Layer != home.Layer) continue;
                    string kind = o is Site ? "site" : o is Settlement s && s.Faction != null && !s.Faction.HostileTo(Faction.OfPlayer) && s.Visitable ? "settlement" : null;
                    if (kind == null) continue;
                    others.Add((o, Find.WorldGrid.ApproxDistanceInTiles(home, o.Tile), kind));
                }
                foreach (var x in others.Where(x => x.kind == "site").OrderBy(x => x.dist).Take(4).Concat(others.Where(x => x.kind == "settlement").OrderBy(x => x.dist).Take(4)))
                    list.Add(new Destination { label = x.o.LabelCap, target = x.o, tile = x.o.Tile, tiles = Mathf.RoundToInt(x.dist), days = DaysTo(home, x.o.Tile), kind = x.kind });
            }
            catch (Exception e) { Log.WarningOnce("[Colony Advisor] destinations: " + e.Message, 0x5A41F5); }
            return list;
        }

        private static float Skill(Pawn p, SkillDef s) { try { return p.skills?.GetSkill(s)?.Level ?? 0; } catch { return 0f; } }
        private static bool Fit(Pawn p) =>
            !p.Downed && !p.InMentalState && !p.IsPrisoner && !p.IsSlave && p.health.summaryHealth.SummaryHealthPercent > 0.8f && !p.health.HasHediffsNeedingTend()
            && !(p.ageTracker != null && !p.ageTracker.Adult);

        // who goes, who stays (and why)
        public static void Team(Map map, out List<Pawn> teamOut, out List<(Pawn, string)> stayOut)
        {
            var team = new List<Pawn>();
            var stay = new List<(Pawn, string)>();
            teamOut = team; stayOut = stay;
            List<Pawn> cols = map.mapPawns.FreeColonistsSpawned.Where(p => !p.IsQuestLodger()).ToList();
            var kept = new HashSet<Pawn>();
            void Keep(SkillDef s, WorkTypeDef w, string why)
            {
                Pawn best = cols.Where(p => !kept.Contains(p) && (w == null || !p.WorkTypeIsDisabled(w))).OrderByDescending(p => Skill(p, s)).FirstOrDefault();
                if (best != null) { kept.Add(best); stay.Add((best, why)); }
            }
            if (cols.Count >= 4)
            {
                if (AdvisorMod.S.expKeepDoctor) Keep(SkillDefOf.Medicine, WorkTypeDefOf.Doctor, T("CA_X_KeepDoctor"));
                if (AdvisorMod.S.expKeepCook) Keep(SkillDefOf.Cooking, DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking"), T("CA_X_KeepCook"));
                if (AdvisorMod.S.expKeepBuilder) Keep(SkillDefOf.Construction, WorkTypeDefOf.Construction, T("CA_X_KeepBuilder"));
            }
            foreach (Pawn p in cols) if (!kept.Contains(p) && !Fit(p)) { kept.Add(p); stay.Add((p, T("CA_X_KeepUnfit"))); }
            var cand = cols.Where(p => !kept.Contains(p)).ToList();
            int max = Mathf.Clamp(AdvisorMod.S.expTeamMax, 1, 20), min = Mathf.Clamp(AdvisorMod.S.expTeamMin, 1, max);
            // keep at least half of the colony at home
            int size = Mathf.Min(max, Mathf.Max(Mathf.Min(min, cand.Count), Mathf.Min(cand.Count, cols.Count / 2)));
            float Fighter(Pawn p)
            {
                if (p.WorkTagIsDisabled(WorkTags.Violent)) return -5f;
                float s = Skill(p, SkillDefOf.Shooting) + Skill(p, SkillDefOf.Melee) * 0.5f;
                if (p.equipment?.Primary != null) s += p.equipment.Primary.def.IsRangedWeapon ? 6f : 3f;
                try { s += p.GetStatValue(StatDefOf.ArmorRating_Sharp) * 10f; } catch { }
                s += (p.needs?.mood?.CurLevel ?? 0.5f) * 4f;
                return s;
            }
            // one medic and one talker if there is room
            Pawn medic = cand.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Doctor)).OrderByDescending(p => Skill(p, SkillDefOf.Medicine)).FirstOrDefault();
            if (medic != null && size >= 3) team.Add(medic);
            Pawn talker = cand.Where(p => !team.Contains(p) && !p.WorkTagIsDisabled(WorkTags.Social)).OrderByDescending(p => Skill(p, SkillDefOf.Social)).FirstOrDefault();
            if (talker != null && size >= 4) team.Add(talker);
            foreach (Pawn p in cand.Where(p => !team.Contains(p)).OrderByDescending(Fighter))
            {
                if (team.Count >= size) break;
                team.Add(p);
            }
        }

        // rough supplies for days out: food, medicine
        public static void Supplies(Map map, List<Pawn> team, float days, out int meals, out int medicine, out int haveMeals, out int haveMedicine)
        {
            meals = Mathf.CeilToInt(team.Count * Mathf.Max(1f, days) * 1.6f / 0.9f);   // ~1.6 nutrition a day, a meal is 0.9
            medicine = Mathf.Max(2, team.Count / 2 + 1);
            haveMeals = 0; haveMedicine = 0;
            try
            {
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree))
                    if (t.def.IsNutritionGivingIngestible && t.def.ingestible.IsMeal && t.def.GetCompProperties<CompProperties_Rottable>() == null && !t.IsForbidden(Faction.OfPlayer)) haveMeals += t.stackCount;
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine))
                    if (!t.IsForbidden(Faction.OfPlayer)) haveMedicine += t.stackCount;
            }
            catch { }
        }

        // Steward Kit (soft): how many kit items the team still misses
        public static int KitMissing(List<Pawn> team, out bool active)
        {
            active = false;
            try
            {
                if (!ModsConfig.IsActive("misak.steward.kit")) return 0;
                Type kits = GenTypes.GetTypeInAnyAssembly("StewardKit.Kits");
                var wanted = kits?.GetMethod("Wanted", new[] { typeof(Pawn) });
                var missing = kits?.GetMethods().FirstOrDefault(m => m.Name == "Missing" && m.GetParameters().Length == 2);
                if (wanted == null || missing == null) return 0;
                active = true;
                int n = 0;
                foreach (Pawn p in team)
                    if (wanted.Invoke(null, new object[] { p }) is System.Collections.IEnumerable lines)
                        foreach (object line in lines) n += (int)missing.Invoke(null, new[] { p, line });
                return n;
            }
            catch { return 0; }
        }

        // opens the vanilla "form caravan" window with the team already picked
        public static void FormCaravan(Map map, List<Pawn> team)
        {
            try
            {
                var dlg = new Dialog_FormCaravan(map);
                Find.WindowStack.Add(dlg);
                if (dlg.transferables != null)
                {
                    foreach (TransferableOneWay tr in dlg.transferables)
                    {
                        if (!(tr.AnyThing is Pawn p) || !p.RaceProps.Humanlike) continue;
                        if (team.Contains(p)) tr.AdjustTo(tr.GetMaximumToTransfer());
                    }
                    AccessTools.Method(typeof(Dialog_FormCaravan), "Notify_TransferablesChanged")?.Invoke(dlg, null);
                }
                Messages.Message(T("CA_X_PickRoute"), MessageTypeDefOf.NeutralEvent, false);
            }
            catch (Exception e) { Log.Warning("[Colony Advisor] could not open the caravan window: " + e.Message); }
        }

        public static List<Check> GravChecklist(Map map)
        {
            var l = new List<Check>();
            void C(bool done, string key, params NamedArgument[] a) => l.Add(new Check { done = done, label = T("CA_G_" + key, a), hint = T("CA_G_" + key + "_H") });
            int engine = CountBuilt("GravEngine"), console = CountBuilt("PilotConsole"), tanks = CountBuilt("ChemfuelTank");
            int thrusters = CountBuilt("SmallThruster") + CountBuilt("LargeThruster");
            int chem = 0;
            try { ThingDef cf = DefDatabase<ThingDef>.GetNamedSilentFail("Chemfuel"); if (cf != null) chem = map.resourceCounter.GetCount(cf); } catch { }
            C(Done("BasicGravtech"), "Research");
            C(engine > 0, "Engine");
            C(console > 0, "Console");
            C(thrusters > 0, "Thrusters", thrusters);
            C(tanks > 0, "Tanks", tanks);
            C(chem >= 100, "Chem", chem);
            C(CountBuilt("GravFieldExtender") > 0 || engine > 0, "Field");
            return l;
        }

        // ---------------- other mods (names only) ----------------
        public static string FriendMods()
        {
            var names = new List<string>();
            try
            {
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                {
                    string n = m.Name ?? "";
                    if (n.IndexOf("Mad Dice", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("World at Your Level", StringComparison.OrdinalIgnoreCase) >= 0
                        || n.IndexOf("World At Your Level", StringComparison.OrdinalIgnoreCase) >= 0)
                        names.Add(n);
                }
            }
            catch { }
            return string.Join(", ", names.Distinct().ToArray());
        }
    }

    // ---------------------------------------------------------------------------
    // Cabin fever: restless after many days in the same place.
    // ---------------------------------------------------------------------------
    public class ThoughtWorker_CabinFever : ThoughtWorker
    {
        protected override ThoughtState CurrentStateInternal(Pawn p)
        {
            try
            {
                if (AdvisorMod.S == null || !AdvisorMod.S.cabinFever) return ThoughtState.Inactive;
                if (!p.IsColonist || p.Map == null || !p.Map.IsPlayerHome || p.IsQuestLodger() || (p.ageTracker != null && !p.ageTracker.Adult)) return ThoughtState.Inactive;
                CompassGame g = CompassGame.Get();
                if (g == null) return ThoughtState.Inactive;
                float days = g.DaysHome(p);
                int lim = Mathf.Max(5, AdvisorMod.S.cabinDays);
                if (days < lim) return ThoughtState.Inactive;
                return ThoughtState.ActiveAtStage(days < lim * 2 ? 0 : 1);
            }
            catch { return ThoughtState.Inactive; }
        }
    }

    // ---------------------------------------------------------------------------
    // The compass game component: days at home, new places, quest expiry alerts,
    // rumors and the gentle push.
    // ---------------------------------------------------------------------------
    public class CompassGame : GameComponent
    {
        private Dictionary<int, int> sinceTick = new Dictionary<int, int>();      // pawn id -> tick when they arrived where they are
        private Dictionary<int, string> atTile = new Dictionary<int, string>();   // pawn id -> tile they are at
        private HashSet<string> visited = new HashSet<string>();                  // places the colony has seen
        private HashSet<int> warned = new HashSet<int>();                         // quest id * 4 + level
        private int nextRumor = -1, nextPush = -1;
        private static CompassGame inst;

        public CompassGame(Game game) { inst = this; }

        public static CompassGame Get() => inst ?? (inst = Current.Game?.GetComponent<CompassGame>());

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref sinceTick, "caSince", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref atTile, "caAt", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref visited, "caVisited", LookMode.Value);
            Scribe_Collections.Look(ref warned, "caWarned", LookMode.Value);
            Scribe_Values.Look(ref nextRumor, "caNextRumor", -1);
            Scribe_Values.Look(ref nextPush, "caNextPush", -1);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                sinceTick = sinceTick ?? new Dictionary<int, int>();
                atTile = atTile ?? new Dictionary<int, string>();
                visited = visited ?? new HashSet<string>();
                warned = warned ?? new HashSet<int>();
                inst = this;
            }
        }

        public float DaysHome(Pawn p) =>
            sinceTick.TryGetValue(p.thingIDNumber, out int t) ? (Find.TickManager.TicksGame - t) / 60000f : 0f;

        public float AverageDaysHome()
        {
            List<Pawn> cols = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep;
            var home = cols.Where(p => p.Map != null && p.Map.IsPlayerHome).ToList();
            return home.Count == 0 ? 0f : home.Average(DaysHome);
        }

        public int Restless()
        {
            int lim = Mathf.Max(5, AdvisorMod.S.cabinDays);
            return PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep.Count(p => p.Map != null && p.Map.IsPlayerHome && DaysHome(p) >= lim);
        }

        private static string TileKey(PlanetTile t) => t.ToString();

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 250 != 0 || AdvisorMod.S == null) return;
            try { QuestAlerts(); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] quest alerts: " + e.Message, 0x5A41F6); }
            if (now % 2500 != 0) return;
            try { Places(now); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] places: " + e.Message, 0x5A41F7); }
            try { Rumors(now); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] rumors: " + e.Message, 0x5A41F8); }
            try { Push(now); } catch (Exception e) { Log.WarningOnce("[Colony Advisor] push: " + e.Message, 0x5A41F9); }
        }

        private void QuestAlerts()
        {
            if (!AdvisorMod.S.questAlerts) return;
            Report r = AdvisorMod.S.questAlertsGoodOnly ? Advisor.Get(30f) : null;
            foreach (Quest q in Find.QuestManager.QuestsListForReading)
            {
                if (q == null || q.hidden || q.State != QuestState.NotYetAccepted) continue;
                int t = q.TicksUntilExpiry;
                if (t <= 0 || t > 60000) continue;
                if (r != null && !r.quests.Any(x => x.quest == q && x.verdict <= 1)) continue;
                bool six = t <= 15000;
                int k1 = q.id * 4 + 1, k2 = q.id * 4 + 2;
                if (six && !warned.Contains(k2))
                {
                    warned.Add(k1); warned.Add(k2);
                    Send(q, t, LetterDefOf.NegativeEvent);
                }
                else if (!six && !warned.Contains(k1))
                {
                    warned.Add(k1);
                    Send(q, t, LetterDefOf.NeutralEvent);
                }
            }
        }

        private static void Send(Quest q, int ticks, LetterDef def)
        {
            QuestTravel tr = Compass.Travel(q);
            string left = Compass.DaysText(ticks / 60000f);
            string text = "CA_QA_Text".Translate(q.name, left).Resolve();
            if (tr.days >= 0f) text += "\n\n" + "CA_QA_Travel".Translate(tr.tiles, Compass.DaysText(tr.days)).Resolve();
            Find.LetterStack.ReceiveLetter("CA_QA_Label".Translate(left), text, def, LookTargets.Invalid, null, q);
        }

        private void Places(int now)
        {
            ThoughtDef saw = DefDatabase<ThoughtDef>.GetNamedSilentFail("CA_SawNewPlaces");
            var newHere = new List<Pawn>();
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep)
            {
                PlanetTile tile = p.Tile;
                if (!tile.Valid) continue;
                string key = TileKey(tile);
                int id = p.thingIDNumber;
                if (!atTile.TryGetValue(id, out string was) || was != key)
                {
                    atTile[id] = key;
                    sinceTick[id] = now;
                }
                bool away = p.IsCaravanMember() || (p.Map != null && !p.Map.IsPlayerHome);
                if (away) sinceTick[id] = now;
                // a real place: a map we stand on (not home) or a caravan at a site/settlement
                bool place = (p.Map != null && !p.Map.IsPlayerHome)
                    || (p.IsCaravanMember() && Find.WorldObjects.AllWorldObjects.Any(o => o.Tile == tile && (o is Site || o is Settlement) && o.Faction != Faction.OfPlayer));
                if (!place) continue;
                if (!visited.Contains(key)) newHere.Add(p);
            }
            if (newHere.Count == 0) return;
            foreach (var grp in newHere.GroupBy(p => TileKey(p.Tile)))
            {
                visited.Add(grp.Key);
                foreach (Pawn p in grp)
                {
                    if (AdvisorMod.S.sawNewPlaces && saw != null) p.needs?.mood?.thoughts?.memories?.TryGainMemory(saw);
                    if (AdvisorMod.S.newPlaceXp && p.skills != null)
                    {
                        SkillRecord best = p.skills.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => (int)s.passion).ThenBy(s => s.Level).FirstOrDefault();
                        best?.Learn(400f, true);
                    }
                }
            }
        }

        private void Rumors(int now)
        {
            if (!AdvisorMod.S.rumors) return;
            int min = Mathf.Max(1, AdvisorMod.S.rumorMinDays), max = Mathf.Max(min, AdvisorMod.S.rumorMaxDays);
            if (nextRumor < 0) { nextRumor = now + Rand.RangeInclusive(min, max) * 60000; return; }
            if (now < nextRumor) return;
            nextRumor = now + Rand.RangeInclusive(min, max) * 60000;
            Map map = Find.AnyPlayerHomeMap;
            if (map == null) return;
            string friends = Compass.FriendMods();
            string extra = friends.NullOrEmpty() ? "" : "\n\n" + "CA_Rumor_Friends".Translate(friends).Resolve();
            // a real treasure: the vanilla item stash quest
            QuestScriptDef stash = DefDatabase<QuestScriptDef>.GetNamedSilentFail("OpportunitySite_ItemStash");
            float points = StorytellerUtility.DefaultThreatPointsNow(map);
            if (stash != null)
            {
                try
                {
                    if (stash.CanRun(points, map))
                    {
                        Quest q = QuestUtility.GenerateQuestAndMakeAvailable(stash, points);
                        if (q != null)
                        {
                            QuestUtility.SendLetterQuestAvailable(q, "CA_Rumor_How".Translate().Resolve() + extra);
                            return;
                        }
                    }
                }
                catch (Exception e) { Log.Warning("[Colony Advisor] rumor quest failed, sending a plain rumor: " + e.Message); }
            }
            // fallback: a place worth a visit, with a marker
            PlanetTile home = map.Tile;
            WorldObject pick = Find.WorldObjects.AllWorldObjects
                .Where(o => o.Faction != Faction.OfPlayer && o.Tile.Layer == home.Layer && (o is Site || (o is Settlement s && s.Visitable && s.Faction != null && !s.Faction.HostileTo(Faction.OfPlayer))) && !visited.Contains(o.Tile.ToString()))
                .Select(o => (o, d: Find.WorldGrid.ApproxDistanceInTiles(home, o.Tile)))
                .Where(x => x.d >= 3f && x.d <= 60f)
                .OrderBy(x => Rand.Value).Select(x => x.o).FirstOrDefault();
            if (pick == null) return;
            Find.LetterStack.ReceiveLetter("CA_Rumor_Label".Translate(), "CA_Rumor_Text".Translate(pick.LabelCap, Compass.DaysText(Compass.DaysTo(home, pick.Tile))).Resolve() + extra,
                LetterDefOf.PositiveEvent, new LookTargets(pick));
        }

        private void Push(int now)
        {
            if (!AdvisorMod.S.gentlePush) return;
            if (nextPush < 0) { nextPush = now + 5 * 60000; return; }
            if (now < nextPush) return;
            nextPush = now + 10 * 60000;
            bool travelling = Find.WorldObjects.Caravans.Any(c => c.IsPlayerControlled);
            if (travelling) return;
            int restless = Restless();
            if (restless == 0) return;
            Report r = Advisor.Get(30f);
            QuestRating best = r?.quests.FirstOrDefault(q => q.verdict == 0);
            string text = best != null ? "CA_Push_Quest".Translate(restless, best.quest.name).Resolve() : "CA_Push_Any".Translate(restless).Resolve();
            Find.LetterStack.ReceiveLetter("CA_Push_Label".Translate(), text, LetterDefOf.NeutralEvent, LookTargets.Invalid, null, best?.quest);
        }
    }
}
