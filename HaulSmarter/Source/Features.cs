using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // 3.0 Smart route: nearest neighbour from the colonist, then 2-opt,
    // with the storage cell as the end of the way.
    // ------------------------------------------------------------------
    public static class Route
    {
        private static float D(IntVec3 a, IntVec3 b) => Mathf.Sqrt(a.DistanceToSquared(b));

        public static void Order(IntVec3 start, IntVec3 end, List<LocalTargetInfo> things, List<int> counts)
        {
            int n = things.Count;
            var pts = new List<IntVec3>(n);
            for (int i = 0; i < n; i++) pts.Add(things[i].Cell);
            // nearest neighbour
            var order = new List<int>(n);
            var used = new bool[n];
            IntVec3 at = start;
            for (int k = 0; k < n; k++)
            {
                int bi = -1;
                float bd = float.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    if (used[i]) continue;
                    float d = D(at, pts[i]);
                    if (d < bd) { bd = d; bi = i; }
                }
                used[bi] = true;
                order.Add(bi);
                at = pts[bi];
            }
            // 2-opt with fixed start and end
            bool end2 = end.IsValid;
            for (int pass = 0; pass < 4; pass++)
            {
                bool better = false;
                for (int i = 0; i < n - 1; i++)
                {
                    IntVec3 a = i == 0 ? start : pts[order[i - 1]];
                    IntVec3 b = pts[order[i]];
                    for (int j = i + 1; j < n; j++)
                    {
                        IntVec3 c = pts[order[j]];
                        bool last = j == n - 1;
                        if (last && !end2)
                        {
                            // open end: reversing the tail only changes the first edge
                            if (D(a, c) + 0.01f < D(a, b))
                            {
                                order.Reverse(i, j - i + 1);
                                better = true;
                                b = pts[order[i]];
                            }
                            continue;
                        }
                        IntVec3 dd = last ? end : pts[order[j + 1]];
                        if (D(a, c) + D(b, dd) + 0.01f < D(a, b) + D(c, dd))
                        {
                            order.Reverse(i, j - i + 1);
                            better = true;
                            b = pts[order[i]];
                        }
                    }
                }
                if (!better) break;
            }
            var t2 = order.Select(i => things[i]).ToList();
            var c2 = order.Select(i => counts[i]).ToList();
            things.Clear(); things.AddRange(t2);
            counts.Clear(); counts.AddRange(c2);
        }
    }

    // ------------------------------------------------------------------
    // 3.0 What to haul first
    // ------------------------------------------------------------------
    public static class Priority
    {
        private static readonly Dictionary<ThingDef, bool> deteriorates = new Dictionary<ThingDef, bool>();

        private static bool Deteriorates(ThingDef d)
        {
            if (!deteriorates.TryGetValue(d, out bool v))
            {
                try { v = d.GetStatValueAbstract(StatDefOf.DeteriorationRate) > 0.01f; }
                catch { v = false; }
                deteriorates[d] = v;
            }
            return v;
        }

        public static bool Perishable(Thing t)
        {
            CompRottable rot = t.TryGetComp<CompRottable>();
            return rot != null && t.AmbientTemperature > 0f;
        }

        public static bool Weather(Thing t)
        {
            Map map = t.MapHeld;
            if (map == null) return false;
            if (Deteriorates(t.def) && !t.Position.Roofed(map))
                return true;
            List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
            for (int i = 0; i < fires.Count && i < 60; i++)
                if (fires[i].Position.DistanceToSquared(t.Position) <= 25)
                    return true;
            return false;
        }

        public static bool Valuable(Thing t) => t.def.BaseMarketValue * t.stackCount >= 300f;

        public static float Of(Thing t)
        {
            HSSettings s = HSMod.S;
            if (t == null || s == null || !t.Spawned) return 0f;
            int p = 0;
            if (Cleanup.Marked(t)) return 3f;       // 3.1: after a battle – before everything else
            if (Bought.Urgent(t)) return 3f;        // 3.3: bought things the traders did not carry away
            if (s.prioPerish > p && Perishable(t)) p = s.prioPerish;
            if (s.prioWeather > p && Weather(t)) p = s.prioWeather;
            if (s.prioValuable > p && Valuable(t)) p = s.prioValuable;
            return p;
        }
    }

    // ------------------------------------------------------------------
    // 3.0 Things on the way (gentle): a colonist puts things lying right
    // next to him into his pockets – while walking and after finishing
    // other work. Only adjacent cells, one stack at a time, a pause after
    // every pick-up, never while hurrying, never above half of his
    // capacity. He hauls them when idle or the pockets get full.
    // ------------------------------------------------------------------
    public static class OnTheWay
    {
        private static readonly Dictionary<int, int> lastTake = new Dictionary<int, int>();
        private static readonly HashSet<string> busyJobs = new HashSet<string>
        {
            "BeatFire", "ExtinguishSelf", "TendPatient", "Rescue", "Capture", "Arrest", "Flee", "FleeAndCower", "AttackMelee", "AttackStatic",
            "Wait_Combat", "DeliverToBed", "TakeToBed", "Goto", "GotoWander", "Wait_MaintainPosture", "LayDown", "Carried", "Hunt", "FollowClose"
        };
        public const int Pause = 450;

        public static bool CanTake(Pawn pawn, Job job)
        {
            HSSettings s = HSMod.S;
            if (s == null || !s.opportunistic || !Compat.Hauling)
                return false;
            if (pawn == null || !pawn.Spawned || pawn.Drafted || pawn.Downed || pawn.InMentalState || pawn.Map == null || !pawn.Map.IsPlayerHome)
                return false;
            if (!pawn.IsColonistPlayerControlled || !HaulUtil.CanUsePockets(pawn) || pawn.carryTracker?.CarriedThing != null)
                return false;
            if (pawn.workSettings == null || !pawn.workSettings.WorkIsActive(WorkTypeDefOf.Hauling))
                return false;
            if (job != null)
            {
                if (job.playerForced || job.def == HSDefOf.HaulSmarterMany || job.def == HSDefOf.HaulSmarterUnload || busyJobs.Contains(job.def.defName)
                    || job.locomotionUrgency >= LocomotionUrgency.Jog)
                    return false;
            }
            int now = Find.TickManager.TicksGame;
            if (lastTake.TryGetValue(pawn.thingIDNumber, out int last) && now - last < Pause)
                return false;
            // gentle: never more than half of what he can carry
            return HaulUtil.TrackedMass(pawn) < Load.Capacity(pawn) * 0.5f;
        }

        // at most one stack from the cells right next to him
        public static bool TakeOne(Pawn pawn)
        {
            Map map = pawn.Map;
            for (int i = 0; i < 9; i++)
            {
                IntVec3 c = pawn.Position + GenAdj.AdjacentCellsAndInside[i];
                if (!c.InBounds(map))
                    continue;
                List<Thing> here = c.GetThingList(map);
                for (int k = here.Count - 1; k >= 0; k--)
                {
                    Thing t = here[k];
                    if (t.def.category != ThingCategory.Item || t is Corpse || t.IsForbidden(pawn) || t.IsInValidStorage())
                        continue;
                    // 3.0.1: food, medicine and drugs are needed by cooks, doctors and wardens – never pocketed on the way
                    if (HaulUtil.NeededByOthers(t))
                        continue;
                    if (map.reservationManager.IsReservedByAnyoneOf(t, pawn.Faction))
                        continue;
                    if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, t, false))
                        continue;
                    if (!StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, map, StoreUtility.CurrentStoragePriorityOf(t), pawn.Faction, out _, false))
                        continue;
                    int n = HaulUtil.CountThatFits(t, Mathf.Min(HaulUtil.FreeKg(pawn), Load.Capacity(pawn) * 0.5f - HaulUtil.TrackedMass(pawn)));
                    if (n > 0 && HaulUtil.PickUp(pawn, t, n) > 0)
                    {
                        lastTake[pawn.thingIDNumber] = Find.TickManager.TicksGame;
                        if (HaulUtil.ShouldUnloadNow(pawn) && !pawn.jobs.jobQueue.Any(q => q.job?.def == HSDefOf.HaulSmarterUnload))
                            pawn.jobs.jobQueue.EnqueueLast(JobMaker.MakeJob(HSDefOf.HaulSmarterUnload));
                        return true;
                    }
                }
            }
            return false;
        }

        // called by the map every 60 ticks: colonists who are walking somewhere
        public static void WalkingTick(Map map)
        {
            try
            {
                List<Pawn> list = map.mapPawns.FreeColonistsSpawned;
                for (int i = 0; i < list.Count; i++)
                {
                    Pawn p = list[i];
                    if (p.pather == null || !p.pather.Moving)
                        continue;
                    if (CanTake(p, p.CurJob))
                        TakeOne(p);
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Haul Smarter] on-the-way pickup: " + e.Message, 0x4a51e2);
            }
        }
    }

    [HarmonyPatch(typeof(JobDriver), nameof(JobDriver.Cleanup))]
    public static class Patch_Opportunistic
    {
        public static void Postfix(JobDriver __instance, JobCondition condition)
        {
            try
            {
                if (condition != JobCondition.Succeeded)
                    return;
                Job job = __instance.job;
                if (job == null || job.def == JobDefOf.HaulToCell || job.def == JobDefOf.HaulToContainer)
                    return;
                if (Safe.On("onTheWay") && OnTheWay.CanTake(__instance.pawn, job))
                    OnTheWay.TakeOne(__instance.pawn);
            }
            catch (Exception e)
            {
                Safe.Fail("onTheWay", e);
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.0 Building from the pockets: a colonist who carries the material
    // takes it out of his pockets and brings it to the blueprint instead
    // of walking to the stockpile.
    // ------------------------------------------------------------------
    public static class BuildFromPockets
    {
        [ThreadStatic] public static bool RealJob;

        public static Thing Material(Pawn pawn, IConstructible c, bool forced, out int needed)
        {
            needed = 0;
            foreach (ThingDefCountClass need in c.TotalMaterialCost())
            {
                int num = forced || !(c is IHaulEnroute en) ? c.ThingCountNeeded(need.thingDef) : en.GetSpaceRemainingWithEnroute(need.thingDef, pawn);
                if (num <= 0)
                    continue;
                foreach (Thing t in HaulUtil.TrackedItems(pawn))
                {
                    if (t.def == need.thingDef && t.stackCount > 0)
                    {
                        needed = num;
                        return t;
                    }
                }
            }
            return null;
        }
    }

    [HarmonyPatch]
    public static class Patch_BuildJobOnThing
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(WorkGiver_ConstructDeliverResourcesToFrames), nameof(WorkGiver_Scanner.JobOnThing));
            yield return AccessTools.Method(typeof(WorkGiver_ConstructDeliverResourcesToBlueprints), nameof(WorkGiver_Scanner.JobOnThing));
        }

        public static void Prefix() => BuildFromPockets.RealJob = true;

        public static Exception Finalizer(Exception __exception)
        {
            BuildFromPockets.RealJob = false;
            return __exception;
        }
    }

    [HarmonyPatch(typeof(WorkGiver_ConstructDeliverResources), "ResourceDeliverJobFor")]
    public static class Patch_BuildFromPockets
    {
        public static bool Prefix(Pawn pawn, IConstructible c, bool forced, ref Job __result)
        {
            try
            {
                HSSettings s = HSMod.S;
                if (s == null || !s.buildFromPockets || !Safe.On("buildPockets") || pawn == null || c == null || c is Blueprint_Install || !(c is Thing ct) || !ct.Spawned)
                    return true;
                if (FloatMenuMakerMap.makingFor == pawn || pawn.carryTracker == null || pawn.carryTracker.CarriedThing != null || !HaulUtil.HasTracked(pawn))
                    return true;
                Thing mat = BuildFromPockets.Material(pawn, c, forced, out int needed);
                if (mat == null)
                    return true;
                if (!BuildFromPockets.RealJob)
                {
                    // only a check ("is there work?") – answer yes without touching anything
                    __result = JobMaker.MakeJob(JobDefOf.HaulToContainer, ct, ct, ct);
                    return false;
                }
                int count = Mathf.Min(needed, mat.stackCount);
                pawn.inventory.innerContainer.TryTransferToContainer(mat, pawn.carryTracker.innerContainer, count, out Thing carried, false);
                if (carried == null)
                    return true;
                if (mat.Destroyed || mat.stackCount <= 0 || !pawn.inventory.innerContainer.Contains(mat))
                    HSData.Untrack(mat);
                HSData.Untrack(carried);
                Job job = JobMaker.MakeJob(JobDefOf.HaulToContainer);
                job.targetA = carried;
                job.targetB = ct;
                job.targetC = ct;
                job.count = carried.stackCount;
                job.haulMode = HaulMode.ToContainer;
                __result = job;
                return false;
            }
            catch (Exception e)
            {
                Safe.Fail("buildPockets", e);
                return true;
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.0 Weight
    // ------------------------------------------------------------------
    public static class Load
    {
        public static float Capacity(Pawn p) => MassUtility.Capacity(p) + (HSMod.S?.bonusKg ?? 0f);

        public static void Info(Pawn p, out float carried, out float capacity, out int items)
        {
            carried = 0f;
            items = 0;
            foreach (Thing t in HaulUtil.TrackedItems(p))
            {
                carried += HaulUtil.UnitMass(t) * t.stackCount;
                items += t.stackCount;
            }
            capacity = Capacity(p);
        }
    }

    // heavy load slows walking: from half load down to -15 % at full load
    public class StatPart_HaulLoad : StatPart
    {
        private static float Factor(Pawn p)
        {
            if (HSMod.S == null || !HSMod.S.slowdown || p == null || p.inventory == null || !p.IsColonist)
                return 1f;
            float cap = Load.Capacity(p);
            if (cap <= 0f)
                return 1f;
            float load = HaulUtil.TrackedMass(p) / cap;
            if (load <= 0.5f)
                return 1f;
            return 1f - 0.15f * Mathf.Clamp01((load - 0.5f) / 0.5f);
        }

        public override void TransformValue(StatRequest req, ref float val)
        {
            if (req.HasThing && req.Thing is Pawn p)
                val *= Factor(p);
        }

        public override string ExplanationPart(StatRequest req)
        {
            if (req.HasThing && req.Thing is Pawn p)
            {
                float f = Factor(p);
                if (f < 0.999f)
                    return "HS_StatLoad".Translate() + ": x" + f.ToStringPercent();
            }
            return null;
        }
    }

    [StaticConstructorOnStartup]
    public static class HSStartup
    {
        static HSStartup()
        {
            try
            {
                StatDef move = StatDefOf.MoveSpeed;
                if (move.parts == null) move.parts = new List<StatPart>();
                if (!move.parts.Any(x => x is StatPart_HaulLoad))
                    move.parts.Add(new StatPart_HaulLoad { parentStat = move });
            }
            catch (Exception e)
            {
                Log.Warning("[Haul Smarter] load slowdown not available: " + e.Message);
            }
            Overview.SetButton();
        }
    }

    // ------------------------------------------------------------------
    // 3.0 Time estimate
    // ------------------------------------------------------------------
    public static class Eta
    {
        private static readonly Dictionary<int, KeyValuePair<int, float>> cache = new Dictionary<int, KeyValuePair<int, float>>();

        // game hours until the current haul job is unloaded, -1 = not hauling
        public static float Hours(Pawn pawn, out int stillToTake)
        {
            stillToTake = 0;
            Job job = pawn?.CurJob;
            if (job == null || (job.def != HSDefOf.HaulSmarterMany && job.def != HSDefOf.HaulSmarterUnload))
                return -1f;
            List<LocalTargetInfo> q = job.GetTargetQueue(TargetIndex.A);
            stillToTake = q?.Count ?? 0;
            if (job.targetA.IsValid) stillToTake++;
            int now = Find.TickManager.TicksGame;
            if (cache.TryGetValue(pawn.thingIDNumber, out var c) && now - c.Key < 120)
                return c.Value;
            float cells = 0f;
            IntVec3 at = pawn.Position;
            if (job.targetA.IsValid) { cells += Mathf.Sqrt(at.DistanceToSquared(job.targetA.Cell)); at = job.targetA.Cell; }
            if (q != null)
                foreach (LocalTargetInfo t in q)
                    if (t.IsValid) { cells += Mathf.Sqrt(at.DistanceToSquared(t.Cell)); at = t.Cell; }
            // to storage: the store of the first carried item (or the cell we are walking to)
            IntVec3 store = job.targetB.IsValid ? job.targetB.Cell : IntVec3.Invalid;
            int carriedItems = 0;
            foreach (Thing t in HaulUtil.TrackedItems(pawn))
            {
                carriedItems++;
                if (!store.IsValid && StoreUtility.TryFindBestBetterStoreCellFor(t, pawn, pawn.Map, StoragePriority.Unstored, pawn.Faction, out IntVec3 sc, false))
                    store = sc;
            }
            if (store.IsValid)
                cells += Mathf.Sqrt(at.DistanceToSquared(store));
            float ticksPerCell = pawn.TicksPerMoveCardinal * 1.25f;   // paths are longer than straight lines
            float ticks = cells * ticksPerCell + (carriedItems + stillToTake) * 45f;
            float hours = ticks / 2500f;
            cache[pawn.thingIDNumber] = new KeyValuePair<int, float>(now, hours);
            return hours;
        }

        public static string Text(float hours)
        {
            if (hours < 0.1f)
                return "HS_EtaNow".Translate();
            return "HS_EtaHours".Translate(hours.ToString(hours < 10f ? "0.0" : "0"));
        }
    }

    // inspect pane: weight and time
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
    public static class Patch_Inspect
    {
        public static void Postfix(Pawn __instance, ref string __result)
        {
            try
            {
                if (HSMod.S == null || !HSMod.S.showEta || __instance == null || !__instance.IsColonist || __instance.inventory == null)
                    return;
                Load.Info(__instance, out float kg, out float cap, out int items);
                float h = Eta.Hours(__instance, out int still);
                if (items == 0 && h < 0f)
                    return;
                string line = "HS_Carries".Translate(kg.ToString("0.#"), cap.ToString("0"), items);
                if (h >= 0f)
                    line += " · " + "HS_Eta".Translate(Eta.Text(h));
                if (still > 0 && h >= 0f)
                    line += " · " + "HS_StillTake".Translate(still);
                __result = __result.NullOrEmpty() ? line : __result + "\n" + line;
            }
            catch
            {
            }
        }
    }

    // ------------------------------------------------------------------
    // 3.0 On the map: weight bar above colonists, waiting items marked
    // ------------------------------------------------------------------
    public class HSMapComp : MapComponent
    {
        public HSMapComp(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 60 == 17)
                OnTheWay.WalkingTick(map);
            if (now % 250 == 43)
                UnloadSoon(map);
            if (now % 250 == 121)
                Cleanup.Watch(map);
        }

        // 3.0.1: food, medicine or things carried for over an hour go to storage after the current job
        private void UnloadSoon(Map m)
        {
            try
            {
                if (!m.IsPlayerHome)
                    return;
                foreach (Pawn p in m.mapPawns.FreeColonistsSpawned)
                {
                    if (p.Drafted || p.Downed || p.InMentalState || !HaulUtil.HasTracked(p) || !HaulUtil.MustUnloadSoon(p))
                        continue;
                    Job cur = p.CurJob;
                    if (cur != null && (cur.def == HSDefOf.HaulSmarterMany || cur.def == HSDefOf.HaulSmarterUnload))
                        continue;
                    // in the middle of harvesting or mining the pockets fill up first (unloaded when full or done)
                    if (p.jobs.curDriver is JobDriver_PlantWork || p.jobs.curDriver is JobDriver_Mine)
                        continue;
                    if (p.jobs.jobQueue.Any(q => q.job?.def == HSDefOf.HaulSmarterUnload) || !HaulUtil.CanUnloadHere(p))
                        continue;
                    p.jobs.jobQueue.EnqueueFirst(JobMaker.MakeJob(HSDefOf.HaulSmarterUnload));
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Haul Smarter] unload check: " + e.Message, 0x4a51e4);
            }
        }

        public override void MapComponentOnGUI()
        {
            HSSettings s = HSMod.S;
            if (s == null || !s.showWeight || Event.current.type != EventType.Repaint || Find.CurrentMap != map)
                return;
            if (Find.CameraDriver.CurrentZoom > CameraZoomRange.Middle)
                return;
            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                {
                    if (!HaulUtil.HasTracked(p))
                        continue;
                    Load.Info(p, out float kg, out float cap, out int items);
                    if (kg <= 0f || cap <= 0f)
                        continue;
                    Vector2 pos = GenMapUI.LabelDrawPosFor(p, 0.95f);
                    Rect bar = new Rect(pos.x - 22f, pos.y - 12f, 44f, 5f);
                    Widgets.DrawBoxSolid(bar.ExpandedBy(1f), new Color(0f, 0f, 0f, 0.6f));
                    float f = Mathf.Clamp01(kg / cap);
                    Color col = f < 0.6f ? new Color(0.49f, 0.75f, 0.37f) : f < 0.9f ? new Color(0.88f, 0.64f, 0.29f) : new Color(0.85f, 0.3f, 0.25f);
                    Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * f, bar.height), col);
                    if (Find.CameraDriver.CurrentZoom == CameraZoomRange.Closest)
                    {
                        Text.Font = GameFont.Tiny;
                        Text.Anchor = TextAnchor.LowerCenter;
                        GUI.color = new Color(1f, 1f, 1f, 0.85f);
                        Widgets.Label(new Rect(pos.x - 40f, bar.y - 16f, 80f, 16f), kg.ToString("0") + "/" + cap.ToString("0") + " kg");
                        GUI.color = Color.white;
                        Text.Anchor = TextAnchor.UpperLeft;
                        Text.Font = GameFont.Small;
                    }
                }
            }
            catch
            {
            }
        }

        public override void MapComponentUpdate()
        {
            HSSettings s = HSMod.S;
            if (s == null || !s.highlight || Find.CurrentMap != map || !Overview.Open)
                return;
            Overview.Refresh(map);
            if (Overview.Cells.Count > 0)
                GenDraw.DrawFieldEdges(Overview.Cells, new Color(1f, 0.85f, 0.3f));
            if (Overview.HotCells.Count > 0)
                GenDraw.DrawFieldEdges(Overview.HotCells, new Color(1f, 0.4f, 0.25f));
        }
    }

    // ------------------------------------------------------------------
    // 3.0 Hauling overview (button at the bottom)
    // ------------------------------------------------------------------
    public static class Overview
    {
        public static int Waiting, Perish, Outside, Valuable, Haulers, Busy;
        public static float WaitingKg, EtaHours;
        public static readonly List<IntVec3> Cells = new List<IntVec3>();
        public static readonly List<IntVec3> HotCells = new List<IntVec3>();
        private static int lastFrame = -9999;
        private static Map lastMap;

        public static bool Open => Find.WindowStack != null && Find.WindowStack.IsOpen<MainTabWindow_Haul>();

        public static void SetButton()
        {
            try
            {
                MainButtonDef d = DefDatabase<MainButtonDef>.GetNamedSilentFail("HaulSmarterOverview");
                if (d != null && HSMod.S != null)
                    d.buttonVisible = HSMod.S.overviewButton;
            }
            catch
            {
            }
        }

        public static void Refresh(Map map)
        {
            if (map == null || (map == lastMap && Time.frameCount - lastFrame < 90))
                return;
            lastMap = map;
            lastFrame = Time.frameCount;
            Waiting = Perish = Outside = Valuable = Haulers = Busy = 0;
            WaitingKg = 0f;
            Cells.Clear();
            HotCells.Clear();
            var seen = new HashSet<IntVec3>();
            foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
            {
                if (t == null || !t.Spawned || t.def.category != ThingCategory.Item || t.IsForbidden(Faction.OfPlayer) || t.IsInValidBestStorage())
                    continue;
                Waiting++;
                WaitingKg += HaulUtil.UnitMass(t) * t.stackCount;
                bool hot = false;
                if (Priority.Perishable(t)) { Perish++; hot = true; }
                if (Priority.Weather(t)) { Outside++; hot = true; }
                if (Priority.Valuable(t)) Valuable++;
                if (seen.Add(t.Position))
                    (hot ? HotCells : Cells).Add(t.Position);
            }
            float capSum = 0f, ticksSum = 0f;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
            {
                if (p.Downed || p.workSettings == null || !p.workSettings.WorkIsActive(WorkTypeDefOf.Hauling) || p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
                    continue;
                Haulers++;
                capSum += Mathf.Max(5f, Load.Capacity(p) - MassUtility.GearAndInventoryMass(p) + HaulUtil.TrackedMass(p));
                ticksSum += p.TicksPerMoveCardinal;
                Job j = p.CurJob;
                if (j != null && (j.def == HSDefOf.HaulSmarterMany || j.def == HSDefOf.HaulSmarterUnload || j.def == JobDefOf.HaulToCell))
                    Busy++;
            }
            EtaHours = -1f;
            if (Haulers > 0 && Waiting > 0)
            {
                // rough: trips needed × (way there and back) / haulers working at the same time
                float avgCap = capSum / Haulers;
                float trips = Mathf.Max(1f, Mathf.Ceil(WaitingKg / Mathf.Max(5f, avgCap * (HSMod.S?.unloadAt ?? 0.8f))));
                trips = Mathf.Max(trips, Waiting / (float)Mathf.Max(1, HSMod.S?.maxItems ?? 30));
                float dist = AvgDistanceToStorage(map);
                float tripTicks = dist * 2f * (ticksSum / Haulers) * 1.25f + 45f * Mathf.Min(Waiting / trips, HSMod.S?.maxItems ?? 30);
                EtaHours = trips * tripTicks / Haulers / 2500f;
            }
        }

        private static float AvgDistanceToStorage(Map map)
        {
            float sx = 0f, sz = 0f;
            int n = 0;
            foreach (SlotGroup g in map.haulDestinationManager.AllGroupsListForReading)
            {
                List<IntVec3> cells = g.CellsList;
                for (int i = 0; i < cells.Count; i += 4)
                {
                    sx += cells[i].x;
                    sz += cells[i].z;
                    n++;
                }
            }
            if (n == 0)
                return 30f;
            var center = new IntVec3(Mathf.RoundToInt(sx / n), 0, Mathf.RoundToInt(sz / n));
            float sum = 0f;
            int k = 0;
            foreach (IntVec3 c in Cells.Concat(HotCells))
            {
                sum += Mathf.Sqrt(c.DistanceToSquared(center));
                if (++k >= 400) break;
            }
            return k == 0 ? 30f : sum / k;
        }
    }

    // ------------------------------------------------------------------
    // 3.1 Clean-up after a battle: weapons, apparel and bodies the enemies left
    // behind around the base are allowed and hauled before everything else.
    // ------------------------------------------------------------------
    public static class Cleanup
    {
        private static readonly Dictionary<int, int> marked = new Dictionary<int, int>();   // thing id → until tick
        private static readonly Dictionary<int, bool> wasFight = new Dictionary<int, bool>(); // map id
        private const int KeepTicks = 60000 * 2;

        public static bool Marked(Thing t)
        {
            if (marked.Count == 0 || t == null) return false;
            return marked.TryGetValue(t.thingIDNumber, out int until) && Find.TickManager.TicksGame < until;
        }

        public static bool Any => marked.Count > 0;
        private static CellRect near;

        private static bool Wanted(Thing t, Map map)
        {
            if (t == null || !t.Spawned || t.IsInValidStorage()) return false;
            bool kind = t.def.IsWeapon || t.def.IsApparel || (t is Corpse c && !c.InnerPawn.IsColonist && c.InnerPawn.Faction != Faction.OfPlayer);
            if (!kind) return false;
            if (t is Corpse cc && cc.InnerPawn.RaceProps.Animal && cc.InnerPawn.Faction == null) return false;   // hunting, not a battle
            // near the base: in the home area or around it (its bounds + 20 cells)
            return map.areaManager.Home[t.Position] || near.Contains(t.Position);
        }

        public static List<Thing> Candidates(Map map)
        {
            var l = new List<Thing>();
            if (map == null) return l;
            near = CellRect.Empty;
            List<IntVec3> cells = map.areaManager.Home.ActiveCells.ToList();
            if (cells.Count > 0)
                near = CellRect.FromLimits(cells.Min(c => c.x), cells.Min(c => c.z), cells.Max(c => c.x), cells.Max(c => c.z)).ExpandedBy(20).ClipInsideMap(map);
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                if (Wanted(t, map)) l.Add(t);
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
                if (Wanted(t, map) && !l.Contains(t)) l.Add(t);
            return l;
        }

        public static int Run(Map map)
        {
            int n = 0, until = Find.TickManager.TicksGame + KeepTicks;
            foreach (Thing t in Candidates(map))
            {
                if (t.IsForbidden(Faction.OfPlayer)) t.SetForbidden(false, false);
                marked[t.thingIDNumber] = until;
                n++;
            }
            if (marked.Count > 5000)
            {
                int now = Find.TickManager.TicksGame;
                foreach (int k in marked.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) marked.Remove(k);
            }
            return n;
        }

        // a battle at home just ended → tell the player (or clean up by itself)
        public static void Watch(Map map)
        {
            try
            {
                HSSettings s = HSMod.S;
                if (s == null || !map.IsPlayerHome || (!s.cleanupNotice && !s.cleanupAuto)) return;
                bool fight = GenHostility.AnyHostileActiveThreatToPlayer(map, true);
                bool before = wasFight.TryGetValue(map.uniqueID, out bool b) && b;
                wasFight[map.uniqueID] = fight;
                if (fight || !before) return;
                if (s.cleanupAuto)
                {
                    int n = Run(map);
                    if (n > 0) Messages.Message("HS_CleanupStarted".Translate(n), MessageTypeDefOf.PositiveEvent, false);
                }
                else
                {
                    int n = Candidates(map).Count;
                    if (n > 0) Messages.Message("HS_CleanupNotice".Translate(n), MessageTypeDefOf.NeutralEvent, false);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Haul Smarter] clean-up check: " + e.Message, 0x4a51e5); }
        }
    }

    public class MainTabWindow_Haul : MainTabWindow
    {
        private Vector2 scroll;
        public override Vector2 RequestedTabSize => new Vector2(560f, 520f);

        private static void Line(Listing_Standard l, string text, Color? c = null)
        {
            if (c.HasValue) GUI.color = c.Value;
            l.Label(text);
            GUI.color = Color.white;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;
            Overview.Refresh(map);
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 32f), "HS_OverviewTitle".Translate());
            Text.Font = GameFont.Small;
            var l = new Listing_Standard();
            l.Begin(new Rect(inRect.x, inRect.y + 36f, inRect.width, 190f));
            Line(l, "HS_OvWaiting".Translate(Overview.Waiting, Overview.WaitingKg.ToString("0")));
            Line(l, "HS_OvHot".Translate(Overview.Perish, Overview.Outside, Overview.Valuable), Overview.Perish + Overview.Outside > 0 ? MUI.Warn : (Color?)null);
            Line(l, "HS_OvHaulers".Translate(Overview.Haulers, Overview.Busy));
            Line(l, Overview.EtaHours < 0f ? "HS_OvEtaNone".Translate().ToString() : "HS_OvEta".Translate(Eta.Text(Overview.EtaHours)).ToString());
            // 3.1: clean-up after a battle
            int left = Cleanup.Candidates(map).Count;
            Rect cb = new Rect(inRect.xMax - 220f, inRect.y, 220f, 30f);
            if (Widgets.ButtonText(cb, "HS_CleanupButton".Translate(left), active: left > 0))
            {
                int n = Cleanup.Run(map);
                Messages.Message("HS_CleanupStarted".Translate(n), MessageTypeDefOf.PositiveEvent, false);
            }
            TooltipHandler.TipRegion(cb, "HS_CleanupButton_Desc".Translate());
            HSData.Totals(out float kgToday, out int tripsToday);
            Line(l, "HS_OvToday".Translate(kgToday.ToString("0"), tripsToday));
            if (HSMod.S != null)
            {
                bool hl = HSMod.S.highlight;
                l.CheckboxLabeled("HS_Highlight".Translate(), ref HSMod.S.highlight, "HS_Highlight_Desc".Translate());
                if (hl != HSMod.S.highlight)
                    LoadedModManager.GetMod<HSMod>()?.WriteSettings();
            }
            l.End();

            // per colonist
            Rect table = new Rect(inRect.x, inRect.y + 230f, inRect.width, inRect.height - 230f);
            MUI.Fill(new Rect(table.x, table.y, table.width, 1f), MUI.Border);
            float y = table.y + 6f;
            float[] cols = { 0f, 0.36f, 0.58f, 0.80f };
            string[] heads = { "HS_ColName", "HS_ColCarry", "HS_ColToday", "HS_ColTotal" };
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            for (int i = 0; i < cols.Length; i++)
                Widgets.Label(new Rect(table.x + table.width * cols[i], y, table.width * 0.22f + (i == 0 ? 60f : 0f), 20f), heads[i].Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 22f;
            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.Where(p => !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling)).ToList();
            Rect outR = new Rect(table.x, y, table.width, table.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, pawns.Count * 26f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            float ry = 0f;
            foreach (Pawn p in pawns)
            {
                Rect row = new Rect(0f, ry, view.width, 24f);
                Widgets.DrawHighlightIfMouseover(row);
                if (Widgets.ButtonInvisible(row))
                    CameraJumper.TryJumpAndSelect(p);
                Load.Info(p, out float kg, out float cap, out int items);
                HaulStat st = HSData.StatFor(p, false);
                Widgets.Label(new Rect(row.width * cols[0], ry + 2f, row.width * 0.35f, 22f), p.LabelShortCap);
                Widgets.Label(new Rect(row.width * cols[1], ry + 2f, row.width * 0.22f, 22f), kg.ToString("0") + " / " + cap.ToString("0") + " kg");
                Widgets.Label(new Rect(row.width * cols[2], ry + 2f, row.width * 0.22f, 22f), "HS_KgTrips".Translate((st?.kgToday ?? 0f).ToString("0"), st?.tripsToday ?? 0));
                Widgets.Label(new Rect(row.width * cols[3], ry + 2f, row.width * 0.20f, 22f), "HS_KgTrips".Translate((st?.kg ?? 0f).ToString("0"), st?.trips ?? 0));
                ry += 26f;
            }
            Widgets.EndScrollView();
        }
    }
}
