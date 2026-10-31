using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using UnityEngine;
using UnityEngine.Scripting;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.1: "Smoothness" tab – the former Smooth Frames mod, rewritten for 1.7.
    //  * the tick loop with a frame budget (the game at 3x does not eat the frame)
    //  * smooth camera, more TPS when only watching, all time to simulation in background
    //  * fewer map mesh rebuilds per frame, alerts checked less often at high speed
    //  * hitch finder, no full GC in the middle of event generation, benchmark
    // ---------------------------------------------------------------------------
    public static class Smooth
    {
        public static bool OtherSmoothMod;
        public static bool Active => !OtherSmoothMod;

        public static void Patch(Harmony h)
        {
            OtherSmoothMod = ModLister.GetActiveModWithIdentifier("bolek98.smoothframes", true) != null;
            if (OtherSmoothMod)
            {
                MLog.Warn("[Smooth FPS] Smooth Frames is active – the Smoothness tab is off. Turn Smooth Frames off, Smooth FPS contains all of it now.");
                return;
            }
            foreach (Type t in new[] { typeof(TickLoop), typeof(CameraSmooth), typeof(AlertsSlow), typeof(MapRegenCap) })
            {
                try { h.CreateClassProcessor(t).Patch(); }
                catch (Exception ex) { MLog.Warn("[Smooth FPS] " + t.Name + " skipped: " + ex.Message); }
            }
            GcHold.Apply(h);
        }
    }

    // ---------------- the tick loop with a frame budget ----------------
    [HarmonyPatch(typeof(TickManager), nameof(TickManager.TickManagerUpdate))]
    public static class TickLoop
    {
        private static readonly AccessTools.FieldRef<TickManager, float> realTime = AccessTools.FieldRefAccess<TickManager, float>("realTimeToTickThrough");
        private static readonly AccessTools.FieldRef<TickManager, int> ticksThisFrame = AccessTools.FieldRefAccess<TickManager, int>("ticksThisFrame");
        private static readonly AccessTools.FieldRef<TickManager, Stopwatch> clock = AccessTools.FieldRefAccess<TickManager, Stopwatch>("clock");
        private static readonly AccessTools.FieldRef<TickManager, SimpleMovingAverage> rawAvg = AccessTools.FieldRefAccess<TickManager, SimpleMovingAverage>("rawTickTimeAverage");
        private static readonly AccessTools.FieldRef<TickManager, ExponentialMovingAverage> smoothAvg = AccessTools.FieldRefAccess<TickManager, ExponentialMovingAverage>("smoothedTickTimeAverage");

        private static readonly Stopwatch frameClock = Stopwatch.StartNew();
        private static double lastFrameStart;
        public static double lastTickMs;
        public static float renderMs = 8f, simMs, budgetMs, fps;
        public static int mode;     // 0 normal, 1 controlling, 2 watching, 3 background
        public static bool Running;
        private static float lastInteract = -10f;
        private static bool failed;
        // 1.1.3: safety net – if game time does not move although the game is not paused
        // (another mod that also takes over ticking), the game's own tick loop comes back
        private static int watchTicks = -1;
        private static float watchSince;
        private static int watchCalls;
        public static bool StuckFallback;
        private static float stuckUntil = -1f;
        private static int stuckCount;

        private static bool Interacting()
        {
            try
            {
                if (Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) || Input.mouseScrollDelta.y != 0f
                    || Find.DesignatorManager?.SelectedDesignator != null)
                    lastInteract = Time.realtimeSinceStartup;
            }
            catch
            {
            }
            return Time.realtimeSinceStartup - lastInteract < 0.6f;
        }

        // 1.2: the smart target may be lower than the chosen one
        public static int TargetFps(GfxSettings s) { int o = s.TargetOverride(); return o > 0 ? Mathf.Clamp(o, 20, 360) : s.sfAutoTarget ? SmartTarget.Value : Mathf.Clamp(s.sfTarget, 20, 360); }

        // 1.1.4: another mod that also takes over the tick loop (Butter++, Multiplayer …) –
        // two tick loops fight (colonists stop moving). Then ours steps aside by itself.
        public static string OtherTickMod;
        private static bool othersChecked;

        private static void CheckOthers()
        {
            othersChecked = true;
            try
            {
                MethodBase m = AccessTools.Method(typeof(TickManager), nameof(TickManager.TickManagerUpdate));
                HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                if (info == null)
                    return;
                var names = new List<string>();
                foreach (HarmonyLib.Patch p in info.Prefixes.Concat(info.Transpilers))
                {
                    if (p.owner == null || p.owner.StartsWith("misak.graphicstuner"))
                        continue;
                    string name = p.owner;
                    Assembly asm = p.PatchMethod?.DeclaringType?.Assembly;
                    foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                        if (asm != null && mod.assemblies.loadedAssemblies.Contains(asm))
                            name = mod.Name;
                    if (!names.Contains(name))
                        names.Add(name);
                }
                if (Type.GetType("Multiplayer.Client.Multiplayer, Multiplayer") != null || ModLister.GetActiveModWithIdentifier("rwmt.Multiplayer", true) != null)
                    names.Add("Multiplayer");
                if (names.Count > 0)
                {
                    OtherTickMod = string.Join(", ", names.ToArray());
                    MLog.Warn("[Smooth FPS] " + OtherTickMod + " also controls the game's tick loop – the simulation budget steps aside so the two do not fight. Everything else in Smooth FPS keeps working.");
                }
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] tick loop check: " + e.Message);
            }
        }

        public static bool Prefix(TickManager __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null)
                return true;
            if (!othersChecked)
                CheckOthers();
            if (OtherTickMod != null)
            {
                Running = false;
                return true;
            }
            double now = frameClock.Elapsed.TotalMilliseconds;
            double frameMs = now - lastFrameStart;
            lastFrameStart = now;
            FreezeWatch.Beat();
            HitchFinder.FrameDone(frameMs, lastTickMs);
            GcHold.Watchdog();
            // 1.2.1: a stall only pauses the budget for 20 s – after 3 stalls it stays off
            if (stuckUntil > 0f && !failed && Time.realtimeSinceStartup > stuckUntil)
            {
                stuckUntil = -1f;
                StuckFallback = false;
                watchTicks = -1;
                watchCalls = 0;
            }
            if (!s.sfBudget || failed || stuckUntil > 0f)
            {
                Running = false;
                lastTickMs = 0;
                return true;
            }
            try
            {
                if (frameMs > 0 && frameMs < 2000)
                {
                    float r = (float)Math.Max(0.5, frameMs - lastTickMs);
                    renderMs = renderMs * 0.9f + r * 0.1f;
                    fps = fps * 0.9f + (float)(1000.0 / frameMs) * 0.1f;
                }
                TickManager tm = __instance;
                ticksThisFrame(tm) = 0;
                lastTickMs = 0;
                Running = true;
                if (tm.Paused)
                {
                    simMs = 0f;
                    watchSince = Time.realtimeSinceStartup;
                    watchCalls = 0;
                    return false;
                }
                float mult = tm.TickRateMultiplier;
                if (mult <= 0f)
                    return false;
                float perTick = 1f / (60f * mult);
                ref float rt = ref realTime(tm);
                if (Mathf.Abs(Time.deltaTime - perTick) < perTick * 0.1f)
                    rt += perTick;
                else
                    rt += Time.deltaTime;

                bool interacting = Interacting();
                bool background = s.sfBackground && !Application.isFocused;
                bool idle = s.sfIdle && !interacting && mult >= 2f && Time.realtimeSinceStartup - lastInteract > 2f;
                // 1.4.3: turbo – at speed 3 and 4, while you don't touch anything, the picture drops to a few
                // frames a second and all the rest of the time goes to the simulation (game time runs as fast
                // as the PC can). The moment you move the mouse or press a key the picture is smooth again.
                bool turbo = s.turbo && mult > 3.01f && !interacting && !background && Time.realtimeSinceStartup - lastInteract > 1f;
                int target = TargetFps(s);
                int want = interacting ? Math.Max(s.sfMove, target) : background ? 10 : turbo ? Mathf.Clamp(s.turboFps, 5, 30) : idle ? Math.Min(s.sfIdleFps, target) : target;
                mode = interacting ? 1 : background ? 3 : turbo ? 4 : idle ? 2 : 0;
                float maxBudget = idle || background || turbo ? 1000f / want - 2f : 45.45f;
                budgetMs = Mathf.Clamp(1000f / want - renderMs, 3f, Math.Max(45.45f, maxBudget));
                int maxSim = s.MaxSimNow();
                if (maxSim > 0 && !idle && !background)
                    budgetMs = Mathf.Min(budgetMs, maxSim);
                bool catchingUp = (!s.sfSlowdown || turbo) && rt > perTick * 3f;
                float needed = fps > 1f ? 60f * mult / Math.Min(fps, want) + 1f : mult * 2f;
                float maxTicks = Mathf.Clamp(needed * (catchingUp ? 1.5f : 1f), mult * 2f, turbo ? mult * 20f : mult * 8f);
                // 1.7.2: speed 1 and 2 always run at their real speed – the FPS target only
                // decides how smooth they are, never how slow. With a heavy colony one tick costs
                // more than the frame budget of a high FPS target, and the game ran at the speed
                // of the FPS (e.g. 40 instead of 60 ticks a second at speed 1).
                bool realSpeed = s.sfRealNormal && mult <= 3.01f && !background;
                // 1.7.3: at speed 3 and 4 the simulation gets at least as much time as drawing.
                // When the FPS target cannot be reached (drawing alone takes longer than one frame
                // of the target), the old formula left the simulation 3 ms – speed 4 ran barely
                // faster than speed 1.
                if (s.sfFastShare && mult > 3.01f && !background)
                    budgetMs = Math.Max(budgetMs, Math.Min(renderMs, 45.45f));
                if (realSpeed)
                {
                    float due = rt / perTick;
                    float avg = Math.Max(0.05f, rawAvg(tm).GetAverage());
                    // 1.6.7: the debt is paid in small steps – before, one slow frame left a big debt and
                    // the next frame ran up to 66 ms of ticks at once, the next was slow again… At speed 2
                    // (×3) with a heavy colony that was a steady stutter. Now at most twice the normal
                    // number of ticks for one frame and at most 30 ms (a frame never falls below ~30 FPS
                    // because of catching up).
                    budgetMs = Math.Max(budgetMs, Math.Min(due * avg + 1f, 30f));
                    maxTicks = Math.Max(maxTicks, Math.Min(Mathf.Ceil(due), Mathf.Ceil(mult * 60f / Math.Max(20f, Math.Min(fps, want)) * 2f)));
                }

                Stopwatch c = clock(tm);
                c.Reset();
                c.Start();
                int done = 0;
                while (rt > 0f && done < maxTicks)
                {
                    double a = c.Elapsed.TotalMilliseconds;
                    tm.DoSingleTick();
                    double b = c.Elapsed.TotalMilliseconds;
                    rt -= perTick;
                    done++;
                    ticksThisFrame(tm) = done;
                    rawAvg(tm).AddValue((float)(b - a));
                    smoothAvg(tm).AddValue(rawAvg(tm).GetAverage());
                    if (tm.Paused || b >= budgetMs)
                        break;
                }
                lastTickMs = c.Elapsed.TotalMilliseconds;
                int nowTicks = tm.TicksGame;
                // 1.2.1: only when we really ran ticks and game time still did not move (long pauses, hitches
                // and windows no longer switch the budget off by mistake)
                bool moved = nowTicks != watchTicks;
                watchTicks = nowTicks;
                if (moved || tm.Paused)
                {
                    watchCalls = 0;
                    watchSince = Time.realtimeSinceStartup;
                }
                else if ((watchCalls += done) == 0)
                    watchSince = Time.realtimeSinceStartup;
                else if (watchCalls >= 120 && Time.realtimeSinceStartup - watchSince > 3f)
                {
                    StuckFallback = true;
                    Running = false;
                    stuckCount++;
                    if (stuckCount >= 3)
                    {
                        failed = true;
                        MLog.Warn("[Smooth FPS] game time stopped 3 times although the game was not paused – another mod probably controls ticking too. The simulation budget stays off, the game's own tick loop is used.");
                    }
                    else
                    {
                        stuckUntil = Time.realtimeSinceStartup + 20f;
                        MLog.Warn("[Smooth FPS] game time did not move for 3 s although the game is not paused – the game's own tick loop is used for 20 s, then the simulation budget tries again (" + stuckCount + "/3).");
                    }
                    return true;
                }
                simMs = simMs * 0.9f + (float)lastTickMs * 0.1f;
                // ticks that did not fit: vanilla throws them away (the game slows down);
                // "keep speed" does up to 0.3 s of them in the next frames
                if (rt > 0f)
                    rt = s.sfSlowdown && !realSpeed && !turbo ? 0f : Mathf.Min(rt, turbo ? 1f : realSpeed ? 0.15f : 0.3f);   // 1.6.7: smaller debt at speed 1–2
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Smooth FPS] tick loop failed, using the game's own: " + e, 0x6f7a11);
                failed = true;
                Running = false;
                return true;
            }
        }
    }

    // ---------------- smooth camera ----------------
    [HarmonyPatch(typeof(CameraDriver), nameof(CameraDriver.HitchReduceFactor), MethodType.Getter)]
    public static class CameraSmooth
    {
        private static float smoothDt = 1f / 60f;
        private static int lastFrame = -1;

        public static bool Prefix(ref float __result)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.sfCamera)
                return true;
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                __result = 1f;
                return false;
            }
            if (Time.frameCount != lastFrame)
            {
                lastFrame = Time.frameCount;
                smoothDt = Mathf.Lerp(smoothDt, Mathf.Min(dt, 0.1f), 0.25f);
            }
            __result = smoothDt / dt;
            return false;
        }
    }

    // ---------------- alerts: at 3x checked every other frame ----------------
    [HarmonyPatch(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutUpdate))]
    public static class AlertsSlow
    {
        private static readonly AccessTools.FieldRef<AlertsReadout, List<Alert>> activeRef = AccessTools.FieldRefAccess<AlertsReadout, List<Alert>>("activeAlerts");

        public static bool Prefix(AlertsReadout __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.slowAlerts || Find.TickManager == null || Find.TickManager.TickRateMultiplier < 5.5f)   // speed 3 = ×6
                return true;
            int every = Runtime.FastNow(s) ? 4 : 2;
            if (Time.frameCount % every == 0)
                return true;
            // 1.1.1: active alerts must still get their per-frame update – a critical alert
            // otherwise thinks it just appeared and posts its red message (with sound) again
            try
            {
                List<Alert> active = activeRef(__instance);
                for (int i = 0; i < active.Count; i++)
                    active[i].AlertActiveUpdate();
            }
            catch
            {
                return true;
            }
            return false;
        }
    }

    // ---------------- map mesh rebuilds: at most N visible sections per frame ----------------
    // The game rebuilds every changed visible part of the map in one frame (growing plants,
    // building, snow). With many changes at 3x that is a hitch. Here the rest waits a frame.
    [HarmonyPatch(typeof(MapDrawer), nameof(MapDrawer.MapMeshDrawerUpdate_First))]
    public static class MapRegenCap
    {
        private static readonly AccessTools.FieldRef<MapDrawer, Map> mapRef = AccessTools.FieldRefAccess<MapDrawer, Map>("map");
        private static readonly AccessTools.FieldRef<MapDrawer, Section[,]> sectionsRef = AccessTools.FieldRefAccess<MapDrawer, Section[,]>("sections");
        private static readonly AccessTools.FieldRef<MapDrawer, List<MapDrawLayer>> globalRef = AccessTools.FieldRefAccess<MapDrawer, List<MapDrawLayer>>("global");
        private static readonly AccessTools.FieldRef<MapDrawer, ulong> globalFlagsRef = AccessTools.FieldRefAccess<MapDrawer, ulong>("globalDirtyFlags");
        public static int Delayed;
        private static bool failed;

        public static bool Prefix(MapDrawer __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            int cap = s == null ? 0 : s.regenCap > 0 ? s.regenCap : (s.fastRegen && Runtime.FastNow(s) ? 8 : 0);
            if (cap <= 0 || failed)
                return true;
            try
            {
                ulong gflags = globalFlagsRef(__instance);
                if (gflags != 0UL)
                {
                    List<MapDrawLayer> global = globalRef(__instance);
                    for (int i = 0; i < global.Count; i++)
                    {
                        MapDrawLayer l = global[i];
                        l.Dirty = l.Dirty || (gflags & l.relevantChangeTypes) != 0;
                        if (l.Dirty)
                            l.Regenerate();
                    }
                }
                Map map = mapRef(__instance);
                CellRect view = Find.CameraDriver.CurrentViewRect.ExpandedBy(1).ClipInsideMap(map);
                Section[,] sections = sectionsRef(__instance);
                int budget = cap;
                bool any = false;
                int nx = sections.GetLength(0), nz = sections.GetLength(1);
                for (int x = 0; x < nx; x++)
                {
                    for (int z = 0; z < nz; z++)
                    {
                        Section sec = sections[x, z];
                        if (sec.dirtyFlags == 0UL)
                            continue;
                        if (sec.CellRect.Overlaps(view))
                        {
                            if (budget <= 0)
                            {
                                Delayed++;
                                continue;   // stays dirty, next frame
                            }
                            budget--;
                        }
                        if (sec.TryUpdate(view))
                            any = true;
                    }
                }
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                MLog.Warn("[Smooth FPS] map rebuild cap switched off: " + e.Message);
                return true;
            }
        }
    }

    // ---------------- GC hold during event generation ----------------
    public static class GcHold
    {
        private static int depth;
        private static bool held;
        public static bool Held => held;
        private static long startMem;
        private static float since;
        private static GCLatencyMode savedLatency;
        private const long LimitBytes = 1200L * 1024L * 1024L;
        public static int Holds;
        private static readonly bool coreClr = Type.GetType("Mono.Runtime") == null;

        public static void Enter()
        {
            if (!UnityData.IsInMainThread)
                return;
            depth++;
            GfxSettings s = GraphicsTunerMod.Settings;
            if (depth != 1 || held || s == null || !s.gcHold)
                return;
            try
            {
                startMem = GC.GetTotalMemory(false);
                if (coreClr)
                {
                    // 1.7 (.NET): no blocking full collections while this runs
                    savedLatency = GCSettings.LatencyMode;
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                }
                else
                {
                    if (GarbageCollector.GCMode != GarbageCollector.Mode.Enabled)
                        return;
                    GarbageCollector.GCMode = GarbageCollector.Mode.Disabled;
                }
                held = true;
                Holds++;
                since = Time.realtimeSinceStartup;
            }
            catch
            {
            }
        }

        public static void Exit()
        {
            if (!UnityData.IsInMainThread)
                return;
            if (depth > 0)
                depth--;
            if (depth == 0)
                Release();
        }

        public static void Release()
        {
            if (!held)
                return;
            held = false;
            try
            {
                if (coreClr)
                    GCSettings.LatencyMode = savedLatency;
                else
                    GarbageCollector.GCMode = GarbageCollector.Mode.Enabled;
            }
            catch
            {
            }
        }

        public static void Check()
        {
            if (held && GC.GetTotalMemory(false) - startMem > LimitBytes)
                Release();
        }

        public static void Watchdog()
        {
            if (held && (depth == 0 || Time.realtimeSinceStartup - since > 30f))
            {
                depth = 0;
                Release();
            }
        }

        public static void Prefix() => Enter();

        public static Exception Finalizer(Exception __exception)
        {
            Exit();
            return __exception;
        }

        public static void PawnPostfix() => Check();

        public static IEnumerable<MethodBase> Targets()
        {
            yield return AccessTools.Method(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute));
            yield return AccessTools.Method(typeof(QuestGen), nameof(QuestGen.Generate), new[] { typeof(QuestScriptDef), typeof(Slate) });
            yield return AccessTools.Method(typeof(PawnGroupKindWorker), nameof(PawnGroupKindWorker.GeneratePawns),
                new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(bool) });
            foreach (MethodInfo m in AccessTools.GetDeclaredMethods(typeof(MapGenerator)).Where(m => m.Name == nameof(MapGenerator.GenerateMap)))
                yield return m;
        }

        public static void Apply(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(GcHold), nameof(Prefix));
            var fin = new HarmonyMethod(typeof(GcHold), nameof(Finalizer));
            foreach (MethodBase m in Targets())
            {
                if (m == null)
                    continue;
                try { h.Patch(m, prefix: pre, finalizer: fin); }
                catch (Exception e) { MLog.Warn("[Smooth FPS] GC hold: could not patch " + m.Name + ": " + e.Message); }
            }
            try
            {
                h.Patch(AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }),
                    postfix: new HarmonyMethod(typeof(GcHold), nameof(PawnPostfix)));
            }
            catch
            {
            }
        }
    }

    // ---------------- hitch finder ----------------
    [StaticConstructorOnStartup]
    public static class HitchFinder
    {
        private static readonly Dictionary<string, double> sum = new Dictionary<string, double>();
        private static readonly Dictionary<Assembly, string> modOf = new Dictionary<Assembly, string>();
        private static readonly double tickToMs = 1000.0 / Stopwatch.Frequency;
        private static int gcAtStart;
        private static int gen0AtStart, gen1AtStart;
        private static TimeSpan pauseAtStart;
        private static readonly Func<TimeSpan> totalPause = MakePause();

        // .NET 7+: how long the garbage collector stopped the game in total
        private static Func<TimeSpan> MakePause()
        {
            try
            {
                MethodInfo m = typeof(GC).GetMethod("GetTotalPauseDuration", BindingFlags.Public | BindingFlags.Static);
                return m == null ? null : (Func<TimeSpan>)Delegate.CreateDelegate(typeof(Func<TimeSpan>), m);
            }
            catch { return null; }
        }
        public static string LastHitch = "";
        private static float lastLog;
        private static int patched;
        private static bool on;
        public static bool IsOn => on;
        // 1.6.0: rolling totals for the "What slows the tick" window
        private static readonly Dictionary<string, double> roll = new Dictionary<string, double>();
        private static float rollStart = -1f;
        public static bool Rolling;

        public static void RollReset()
        {
            roll.Clear();
            rollStart = Time.realtimeSinceStartup;
            Rolling = true;
        }

        // ms per second of real time, biggest first
        public static List<KeyValuePair<string, double>> RollTake(out float seconds)
        {
            seconds = rollStart < 0f ? 0f : Time.realtimeSinceStartup - rollStart;
            float sec = Math.Max(0.5f, seconds);
            var list = roll.Select(kv => new KeyValuePair<string, double>(kv.Key, kv.Value / sec)).OrderByDescending(kv => kv.Value).Take(40).ToList();
            roll.Clear();
            rollStart = Time.realtimeSinceStartup;
            return list;
        }

        static HitchFinder()
        {
            Start();
        }

        // 1.3.0: can also start while the game runs (switching it on in the settings)
        private static bool started;

        public static void Start()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (started || s == null || !s.hitchFinder || Smooth.OtherSmoothMod)
                return;
            started = true;
            on = true;
            try
            {
                var h = new Harmony("misak.graphicstuner.hitch");
                var pre = new HarmonyMethod(typeof(HitchFinder), nameof(Pre));
                var post = new HarmonyMethod(typeof(HitchFinder), nameof(Post));
                if (s.hitchThings)
                    Try(h, AccessTools.Method(typeof(Thing), nameof(Thing.DoTick)), new HarmonyMethod(typeof(HitchFinder), nameof(PreThing)), new HarmonyMethod(typeof(HitchFinder), nameof(PostThing)));
                Try(h, AccessTools.Method(typeof(Map), nameof(Map.MapPreTick)), pre, post);
                Try(h, AccessTools.Method(typeof(Map), nameof(Map.MapPostTick)), pre, post);
                Try(h, AccessTools.Method(typeof(World), nameof(World.WorldTick)), pre, post);
                Try(h, AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)), pre, post);
                foreach (Type t in GenTypes.AllTypes)
                {
                    if (t.IsAbstract || t.IsGenericTypeDefinition)
                        continue;
                    string[] names = typeof(MapComponent).IsAssignableFrom(t) ? new[] { "MapComponentTick", "MapComponentUpdate", "MapComponentOnGUI" }
                        : typeof(GameComponent).IsAssignableFrom(t) ? new[] { "GameComponentTick", "GameComponentUpdate", "GameComponentOnGUI" }
                        : typeof(WorldComponent).IsAssignableFrom(t) ? new[] { "WorldComponentTick", "WorldComponentUpdate" }
                        : null;
                    if (names == null)
                        continue;
                    foreach (string n in names)
                    {
                        MethodInfo m;
                        try { m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                        catch { continue; }
                        if (m != null && !m.IsAbstract && m.GetMethodBody() != null)
                            Try(h, m, pre, post);
                    }
                }
                // 1.6.0: the parts of the world tick
                Try(h, AccessTools.Method(typeof(WorldPawns), nameof(WorldPawns.WorldPawnsTick)), pre, post);
                Try(h, AccessTools.Method(typeof(FactionManager), nameof(FactionManager.FactionManagerTick)), pre, post);
                Try(h, AccessTools.Method(typeof(WorldObjectsHolder), nameof(WorldObjectsHolder.WorldObjectsHolderTick)), pre, post);
                Try(h, AccessTools.Method(typeof(IdeoManager), nameof(IdeoManager.IdeoManagerTick)), pre, post);
                Try(h, AccessTools.Method(typeof(WorldPathGrid), nameof(WorldPathGrid.WorldPathGridTick)), pre, post);
                Try(h, AccessTools.Method(typeof(World), nameof(World.WorldUpdate)), pre, post);
                // 1.5.0: saving the game is named in the list (autosave stops the game for a moment)
                Try(h, AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) }), pre, post);
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                    foreach (Assembly a in mod.assemblies.loadedAssemblies)
                        modOf[a] = mod.Name;
                MLog.Info("[Smooth FPS] hitch finder on (" + patched + " places watched).");
                if (s.hitchDeep)
                {
                    try
                    {
                        DeepHitch.Install(h, Mod);
                        DeepHitch.On = true;
                    }
                    catch (Exception e)
                    {
                        MLog.Warn("[Smooth FPS] deep hitch finder could not start: " + e.Message);
                    }
                }
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] hitch finder could not start: " + e.Message);
            }
        }

        private static void Try(Harmony h, MethodBase m, HarmonyMethod pre, HarmonyMethod post)
        {
            if (m == null)
                return;
            try
            {
                h.Patch(m, pre, post);
                patched++;
            }
            catch
            {
            }
        }

        private static bool genWatched;

        public static void WatchGeneration()
        {
            if (genWatched || !on)
                return;
            genWatched = true;
            try
            {
                var h = new Harmony("misak.graphicstuner.hitchgen");
                var pre = new HarmonyMethod(typeof(HitchFinder), nameof(Pre));
                var post = new HarmonyMethod(typeof(HitchFinder), nameof(Post));
                int before = patched;
                var heavy = new List<MethodBase>
                {
                    AccessTools.Method(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) }),
                    AccessTools.Method(typeof(PawnGroupKindWorker), nameof(PawnGroupKindWorker.GeneratePawns), new[] { typeof(PawnGroupMakerParms), typeof(PawnGroupMaker), typeof(bool) }),
                    AccessTools.Method(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute)),
                    AccessTools.Method(typeof(QuestGen), nameof(QuestGen.Generate), new[] { typeof(QuestScriptDef), typeof(Slate) }),
                    AccessTools.Method(typeof(PawnApparelGenerator), nameof(PawnApparelGenerator.GenerateStartingApparelFor)),
                    AccessTools.Method(typeof(PawnWeaponGenerator), nameof(PawnWeaponGenerator.TryGenerateWeaponFor))
                };
                Try(h, heavy[2], pre, new HarmonyMethod(typeof(HitchFinder), nameof(PostIncident)));
                Try(h, heavy[3], pre, new HarmonyMethod(typeof(HitchFinder), nameof(PostQuest)));
                Try(h, heavy[0], pre, post);
                Try(h, heavy[1], pre, post);
                Try(h, heavy[4], pre, post);
                Try(h, heavy[5], pre, post);
                var seen = new HashSet<MethodInfo>();
                foreach (MethodBase m in heavy)
                {
                    if (m == null)
                        continue;
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                    if (info == null)
                        continue;
                    foreach (HarmonyLib.Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    {
                        if (p.owner.StartsWith("misak.graphicstuner") || p.PatchMethod == null || !seen.Add(p.PatchMethod))
                            continue;
                        Try(h, p.PatchMethod, pre, post);
                    }
                }
                MLog.Info("[Smooth FPS] hitch finder: event generation watched (" + (patched - before) + " places).");
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] hitch finder (generation) failed: " + e.Message);
            }
        }

        // 1.6.1: drawing – the "unknown" part of a hitch is mostly drawing and windows; also every
        // mod patch on pawn drawing and graphics building (Body Fit, HAR, animation mods …)
        private static bool drawWatched;

        public static void WatchDrawing()
        {
            if (drawWatched || !on)
                return;
            drawWatched = true;
            try
            {
                var h = new Harmony("misak.graphicstuner.hitchdraw");
                var pre = new HarmonyMethod(typeof(HitchFinder), nameof(Pre));
                var post = new HarmonyMethod(typeof(HitchFinder), nameof(Post));
                int before = patched;
                var main = new List<MethodBase>
                {
                    AccessTools.Method(typeof(DynamicDrawManager), nameof(DynamicDrawManager.DrawDynamicThings)),
                    AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.MapMeshDrawerUpdate_First)),
                    AccessTools.Method(typeof(MapDrawer), nameof(MapDrawer.DrawMapMesh)),
                    AccessTools.Method(typeof(RimWorld.ColonistBar), nameof(RimWorld.ColonistBar.ColonistBarOnGUI)),
                    AccessTools.Method(typeof(RimWorld.AlertsReadout), nameof(RimWorld.AlertsReadout.AlertsReadoutOnGUI)),
                    AccessTools.Method(typeof(WindowStack), nameof(WindowStack.WindowStackOnGUI)),
                    AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.EnsureGraphicsInitialized)),
                    AccessTools.Method(typeof(PawnRenderTree), nameof(PawnRenderTree.EnsureInitialized)),
                    // 1.7.0: the parts of drawing things: choosing what to draw, preparing pawns
                    AccessTools.Method(typeof(DynamicDrawManager), "ComputeCulledThings"),
                    AccessTools.Method(typeof(DynamicDrawManager), "PreDrawVisibleThings")
                };
                foreach (MethodBase m in main)
                    Try(h, m, pre, post);
                // other mods' patches on pawn drawing
                var drawRoots = new List<MethodBase>(main)
                {
                    AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.RenderPawnAt)),
                    AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.DynamicDrawPhaseAt)),
                    AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.SetAllGraphicsDirty))
                };
                var seen = new HashSet<MethodInfo>();
                foreach (MethodBase m in drawRoots)
                {
                    if (m == null)
                        continue;
                    HarmonyLib.Patches info = Harmony.GetPatchInfo(m);
                    if (info == null)
                        continue;
                    foreach (HarmonyLib.Patch p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    {
                        if (p.owner.StartsWith("misak.graphicstuner") || p.PatchMethod == null || !seen.Add(p.PatchMethod))
                            continue;
                        Try(h, p.PatchMethod, pre, post);
                    }
                }
                MLog.Info("[Smooth FPS] hitch finder: drawing watched (" + (patched - before) + " places).");
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] hitch finder (drawing) failed: " + e.Message);
            }
        }

        public static void PostIncident(IncidentWorker __instance, long __state)
        {
            FreezeWatch.Pop(null);
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            if (ms >= 1.0)
                Add("GT_HitchIncident".Translate().ToString() + " " + (__instance?.def?.defName ?? "?") + " [" + (__instance?.def?.modContentPack?.Name ?? "?") + "]", ms);
        }

        public static void PostQuest(object[] __args, long __state)
        {
            FreezeWatch.Pop(null);
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            QuestScriptDef d = __args != null && __args.Length > 0 ? __args[0] as QuestScriptDef : null;
            if (ms >= 1.0)
                Add("GT_HitchQuest".Translate().ToString() + " " + (d?.defName ?? "?") + " [" + (d?.modContentPack?.Name ?? "?") + "]", ms);
        }

        public static void Pre(MethodBase __originalMethod, out long __state)
        {
            FreezeWatch.Push(__originalMethod);
            __state = Stopwatch.GetTimestamp();
        }

        public static void PreThing(Thing __instance, out long __state)
        {
            FreezeWatch.Push(__instance);
            __state = Stopwatch.GetTimestamp();
        }

        public static void PostThing(Thing __instance, long __state)
        {
            FreezeWatch.Pop(null);
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            if (ms >= 1.0)
                Add((__instance?.def?.defName ?? "?") + " [" + ModOf(__instance) + "]", ms);
        }

        public static void Post(MethodBase __originalMethod, long __state)
        {
            FreezeWatch.Pop(__originalMethod);
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            if (Rolling && ms > 0.0 && ms < 1.0)
            {
                // 1.6.0: the window wants also the small, frequent costs
                string k = __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + " [" + Mod(__originalMethod.DeclaringType?.Assembly) + "]";
                lock (gate)
                {
                    roll.TryGetValue(k, out double r);
                    roll[k] = r + ms;
                }
            }
            if (ms >= 1.0)
                Add(__originalMethod.DeclaringType?.Name + "." + __originalMethod.Name + " [" + Mod(__originalMethod.DeclaringType?.Assembly) + "]", ms);
        }

        private static string ModOf(Thing t)
        {
            if (t == null)
                return "?";
            string own = t.def?.modContentPack?.Name ?? "?";
            string code = Mod(t.GetType().Assembly);
            return code != own && code != "RimWorld" ? own + ", " + code : own;
        }

        public static string ModName(Assembly a) => Mod(a);

        private static string Mod(Assembly a)
        {
            if (a == null)
                return "?";
            if (modOf.TryGetValue(a, out string n))
                return n;
            return a.GetName().Name == "Assembly-CSharp" ? "RimWorld" : a.GetName().Name;
        }

        private static readonly object gate = new object();

        public static void Add(string what, double ms)
        {
            // 1.6.1: pawns are drawn on several threads – the lists are shared, so one at a time
            lock (gate)
            {
                sum.TryGetValue(what, out double s);
                sum[what] = s + ms;
                if (Rolling)
                {
                    roll.TryGetValue(what, out double r);
                    roll[what] = r + ms;
                }
            }
        }

        public static void FrameDone(double frameMs, double tickMs)
        {
            if (!on)
                return;
            GfxSettings s = GraphicsTunerMod.Settings;
            int gc = GC.CollectionCount(GC.MaxGeneration);
            // 1.6.2: the first frame after the main menu or loading a save measured the whole time
            // since the last game frame (hours) – that is no hitch; a gap over a minute is skipped
            bool log = s != null && frameMs >= s.hitchMs && frameMs < 60000.0 && Time.realtimeSinceStartup - lastLog > 1f;
            lock (gate)
            {
            if (DeepHitch.On)
                DeepHitch.Flush(Add, log);
            if (log)
            {
                lastLog = Time.realtimeSinceStartup;
                // 1.5.0: the main entries first, then up to 8 details from the deep mode
                var top = sum.Where(kv => !kv.Key.StartsWith(DeepHitch.Mark)).OrderByDescending(kv => kv.Value).Take(10)
                    .Concat(sum.Where(kv => kv.Key.StartsWith(DeepHitch.Mark)).OrderByDescending(kv => kv.Value).Take(8)).ToList();
                double known = sum.Where(kv => !kv.Key.StartsWith(DeepHitch.Mark)).Sum(kv => kv.Value);
                string gcText = gc != gcAtStart ? " | full GC " + (gc - gcAtStart) + "x" : "";
                // 1.7.0: also the small clean-ups and how long the collector stopped the game
                int g0 = GC.CollectionCount(0) - gen0AtStart, g1 = GC.CollectionCount(1) - gen1AtStart;
                if (g0 > 0 || g1 > 0)
                    gcText += " | GC gen0 " + g0 + "x, gen1 " + g1 + "x";
                if (totalPause != null)
                {
                    double pms = (totalPause() - pauseAtStart).TotalMilliseconds;
                    if (pms >= 1.0)
                        gcText += " | GC pause " + pms.ToString("0") + " ms";
                }
                string list = top.Count == 0 ? "-" : string.Join("; ", top.Select(kv => kv.Key + " " + kv.Value.ToString("0") + " ms").ToArray());
                LastHitch = frameMs.ToString("0") + " ms: " + (top.Count > 0 ? top[0].Key : "?");
                MLog.Warn("[Smooth FPS] hitch " + frameMs.ToString("0") + " ms (simulation " + tickMs.ToString("0") + " ms, drawing/windows " + (frameMs - tickMs).ToString("0") + " ms" + gcText + "). Top: " + list
                            + (frameMs - known > 100 ? " | unknown: " + (frameMs - known).ToString("0") + " ms" : ""));
            }
            sum.Clear();
            }
            gcAtStart = gc;
            gen0AtStart = GC.CollectionCount(0);
            gen1AtStart = GC.CollectionCount(1);
            if (totalPause != null)
                pauseAtStart = totalPause();
        }
    }

    public class GTGameComp : GameComponent
    {
        public GTGameComp(Game game) { Sim.ResetGame(); RainWash.ResetGame(); }
        public override void FinalizeInit()
        {
            HitchFinder.WatchGeneration();
            HitchFinder.WatchDrawing();
        }
    }

    // ---------------- benchmark: 60 s at 3x ----------------
    public static class Benchmark
    {
        public const float Length = 60f;
        public static bool Running;
        private static float start;
        private static int ticksStart;
        private static readonly List<float> frames = new List<float>();
        private static TimeSpeed savedSpeed, runSpeed;
        private static float maxMult;
        public static string LastResult;

        public static float Remaining => Running ? Mathf.Max(0f, Length - (Time.realtimeSinceStartup - start)) : 0f;

        public static bool CanStart => Current.ProgramState == ProgramState.Playing && Find.TickManager != null && Find.CurrentMap != null && !Profiler.Running && !Profiler.Preparing && !Profiler.Removing;

        public static void Start()
        {
            if (!CanStart || Running)
                return;
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                string n = w.GetType().Name;
                if (n.Contains("ModSettings") || n.Contains("Options") || n.Contains("Dialog_Settings"))
                    Find.WindowStack.TryRemove(w, false);
            }
            TickManager tm = Find.TickManager;
            savedSpeed = tm.CurTimeSpeed;
            // 1.2: at the speed you play (4× with Smart Speed too); paused = 3×
            if (tm.CurTimeSpeed == TimeSpeed.Paused)
                tm.CurTimeSpeed = TimeSpeed.Superfast;
            runSpeed = tm.CurTimeSpeed;
            frames.Clear();
            maxMult = 0f;
            start = Time.realtimeSinceStartup;
            ticksStart = tm.TicksGame;
            Running = true;
            Messages.Message("GT_BenchStarted".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        public static void Update()
        {
            if (!Running)
                return;
            if (Current.ProgramState != ProgramState.Playing || Find.TickManager == null || Find.CurrentMap == null)
            {
                Running = false;
                return;
            }
            frames.Add(Time.unscaledDeltaTime * 1000f);
            if (Find.TickManager.CurTimeSpeed == TimeSpeed.Paused)
                Find.TickManager.CurTimeSpeed = runSpeed;
            maxMult = Mathf.Max(maxMult, Find.TickManager.TickRateMultiplier);
            float t = Time.realtimeSinceStartup - start;
            if (t < Length)
                return;
            Running = false;
            int ticks = Find.TickManager.TicksGame - ticksStart;
            Find.TickManager.CurTimeSpeed = savedSpeed;
            if (frames.Count < 10)
                return;
            List<float> sorted = frames.OrderByDescending(x => x).ToList();
            float avgFps = frames.Count / t;
            int onePct = Mathf.Max(1, sorted.Count / 100);
            float low = 1000f / sorted.Take(onePct).Average();
            float worst = sorted[0];
            float tps = ticks / t;
            int hitches = frames.Count(x => x > 100f);
            int rec = avgFps < 40f || low < 15f ? 0 : avgFps < 75f ? 1 : 2;
            LastResult = "GT_BenchResult".Translate(avgFps.ToString("0"), low.ToString("0"), worst.ToString("0"), tps.ToString("0"), (tps / 60f).ToString("0.0"), hitches,
                ("GT_Prof" + new[] { "Perf", "Bal", "Qual" }[rec]).Translate()).ToString()
                + "\n" + "GT_BenchSpeed".Translate(maxMult.ToString("0.#"));
            Find.WindowStack.Add(new Dialog_MessageBox(LastResult, title: "GT_BenchTitle".Translate()));
            MLog.Info("[Smooth FPS] benchmark: " + LastResult.Replace("\n", " | "));
        }
    }
}
