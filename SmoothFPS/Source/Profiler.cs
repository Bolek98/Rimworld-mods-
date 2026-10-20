using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.2: "What slows the game" – measures for N seconds at the current speed (also 4×+)
    // which mods, things and components take the time. Patches only while measuring,
    // everything is removed again at the end. Times are exclusive (a mod's patch that runs
    // inside a colonist's tick counts for the mod, not twice).
    // ---------------------------------------------------------------------------
    public static class Profiler
    {
        private const string HarmonyId = "misak.graphicstuner.profiler";
        public static bool Running, Preparing, Removing;
        public const float Length = 30f;
        private static float start;
        private static int ticksStart, beginFrame;
        private static readonly List<float> frames = new List<float>();
        private static TimeSpeed savedSpeed;
        private static float speedMult;
        private static Harmony harmony;
        private static int patched, patchedMods;
        private static readonly double tickToMs = 1000.0 / Stopwatch.Frequency;

        // everything for the hot path is prepared before measuring – no strings, no Translate there
        private class Info
        {
            public string item, mod;
            public long ticks;
        }

        private static readonly Dictionary<MethodBase, Info> infoOf = new Dictionary<MethodBase, Info>();
        private static readonly Dictionary<ThingDef, Info> thingInfo = new Dictionary<ThingDef, Info>();
        private static readonly List<MethodBase> ours = new List<MethodBase>();
        private static readonly Dictionary<Assembly, string> modOf = new Dictionary<Assembly, string>();

        // exclusive time: every level remembers how much its children took (incl. our bookkeeping)
        private static readonly long[] childTicks = new long[512];
        private static int depth;

        public static Result Last;

        public class Result
        {
            public float seconds, avgFps, lowFps, worstMs, tps, speed;
            public int hitches, places, modPlaces;
            public List<KeyValuePair<string, double>> mods, items;
            public string when;
        }

        public static float Remaining => Running ? Mathf.Max(0f, Length - (Time.realtimeSinceStartup - start)) : 0f;

        public static bool Busy => Running || Preparing || Removing || Benchmark.Running;

        public static bool InGame => Current.ProgramState == ProgramState.Playing && Find.TickManager != null && Find.CurrentMap != null;

        public static bool CanStart => InGame && !Busy;

        public static void Start()
        {
            if (!CanStart)
                return;
            foreach (Window w in Find.WindowStack.Windows.ToList())
            {
                string n = w.GetType().Name;
                if (n.Contains("ModSettings") || n.Contains("Options") || n.Contains("Dialog_Settings"))
                    Find.WindowStack.TryRemove(w, false);
            }
            Preparing = true;
            LongEventHandler.QueueLongEvent(() =>
            {
                try { PatchAll(); }
                catch (Exception e) { MLog.Warn("[Smooth FPS] what slows the game: " + e.Message); }
                Preparing = false;
                Begin();
            }, "GT_ProfPreparing", false, null);
        }

        private static void Begin()
        {
            if (!InGame)
            {
                Unpatch();
                return;
            }
            TickManager tm = Find.TickManager;
            savedSpeed = tm.CurTimeSpeed;
            // the speed you play at; paused = 3
            if (tm.CurTimeSpeed == TimeSpeed.Paused)
                tm.CurTimeSpeed = TimeSpeed.Superfast;
            frames.Clear();
            foreach (Info i in infoOf.Values) i.ticks = 0;
            foreach (Info i in thingInfo.Values) i.ticks = 0;
            depth = 0;
            beginFrame = Time.frameCount;
            start = Time.realtimeSinceStartup;
            ticksStart = tm.TicksGame;
            speedMult = 0f;
            Running = true;
            Messages.Message("GT_ProfStarted".Translate(Length.ToString("0")), MessageTypeDefOf.NeutralEvent, false);
        }

        // ---------------- what gets measured ----------------
        private static void PatchAll()
        {
            harmony = new Harmony(HarmonyId);
            infoOf.Clear();
            thingInfo.Clear();
            ours.Clear();
            patched = patchedMods = skipped = 0;
            modOf.Clear();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                foreach (Assembly a in mod.assemblies.loadedAssemblies)
                    modOf[a] = mod.Name;

            // 1) mods' Harmony patches – read the list before we add our own
            var patchMethods = new List<KeyValuePair<MethodInfo, Info>>();
            foreach (MethodBase original in Harmony.GetAllPatchedMethods().ToList())
            {
                HarmonyLib.Patches p;
                try { p = Harmony.GetPatchInfo(original); }
                catch { continue; }
                if (p == null)
                    continue;
                foreach (HarmonyLib.Patch x in p.Prefixes.Concat(p.Postfixes).Concat(p.Finalizers))
                {
                    try
                    {
                        MethodInfo pm = x.PatchMethod;
                        if (pm == null || x.owner == null || x.owner.StartsWith("misak.graphicstuner") || !Safe(pm))
                            continue;
                        string mod = Mod(pm.DeclaringType?.Assembly);
                        if (mod == "RimWorld")
                            continue;
                        string orig = (original.DeclaringType?.Name ?? "?") + "." + original.Name;
                        patchMethods.Add(new KeyValuePair<MethodInfo, Info>(pm, new Info { item = "GT_ProfPatch".Translate(orig).ToString(), mod = mod }));
                    }
                    catch { }
                }
            }

            // 2) ticks of things, components, comps and hediffs
            MethodBase doTick = AccessTools.Method(typeof(Thing), nameof(Thing.DoTick));
            if (doTick != null)
            {
                try
                {
                    harmony.Patch(doTick, prefix: pre, finalizer: finThing);
                    ours.Add(doTick);
                    patched++;
                }
                catch { }
            }
            Watch(AccessTools.Method(typeof(Map), nameof(Map.MapPreTick)), "Map.MapPreTick", "RimWorld");
            Watch(AccessTools.Method(typeof(Map), nameof(Map.MapPostTick)), "Map.MapPostTick", "RimWorld");
            Watch(AccessTools.Method(typeof(World), nameof(World.WorldTick)), "World.WorldTick", "RimWorld");
            Watch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)), "Map.MapUpdate", "RimWorld");
            foreach (Type t in GenTypes.AllTypes)
            {
                // a type of a mod whose dependency is missing must not stop the whole measurement
                try { WatchType(t); }
                catch (Exception e) { skipped++; if (skipped <= 3) MLog.Info("[Smooth FPS] what slows the game: skipped " + t?.FullName + ": " + e.Message); }
            }

            // 3) the patches themselves (methods of mods)
            foreach (var kv in patchMethods)
            {
                try
                {
                    if (Watch(kv.Key, kv.Value.item, kv.Value.mod))
                        patchedMods++;
                }
                catch { }
            }
            MLog.Info("[Smooth FPS] what slows the game: watching " + patched + " places (" + patchedMods + " of " + patchMethods.Count + " mod patches, " + skipped + " types skipped).");
        }

        private static int skipped;

        private static void WatchType(Type t)
        {
            {
                if (t.IsAbstract || t.IsGenericTypeDefinition || t.ContainsGenericParameters)
                    return;
                string[] names =
                    typeof(MapComponent).IsAssignableFrom(t) ? new[] { "MapComponentTick", "MapComponentUpdate", "MapComponentOnGUI" }
                    : typeof(GameComponent).IsAssignableFrom(t) ? new[] { "GameComponentTick", "GameComponentUpdate", "GameComponentOnGUI" }
                    : typeof(WorldComponent).IsAssignableFrom(t) ? new[] { "WorldComponentTick", "WorldComponentUpdate" }
                    : typeof(ThingComp).IsAssignableFrom(t) ? new[] { "CompTick", "CompTickRare", "CompTickLong", "CompTickInterval" }
                    : typeof(HediffComp).IsAssignableFrom(t) ? new[] { "CompPostTick", "CompPostTickInterval" }
                    : typeof(Hediff).IsAssignableFrom(t) ? new[] { "Tick", "PostTick", "TickInterval", "PostTickInterval" }
                    : null;
                if (names == null)
                    return;
                foreach (string n in names)
                {
                    MethodInfo m;
                    try { m = t.GetMethod(n, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                    catch { continue; }
                    if (m != null && !m.IsAbstract && m.GetMethodBody() != null)
                        Watch(m, t.Name + "." + n, Mod(t.Assembly));
                }
            }
        }

        // plain static methods only – generic, instance, compiler-generated or body-less ones are skipped
        private static bool Safe(MethodInfo m)
        {
            try
            {
                Type d = m.DeclaringType;
                return m.IsStatic && !m.IsGenericMethod && !m.ContainsGenericParameters && d != null && !d.IsGenericType && !d.ContainsGenericParameters
                       && !d.Name.Contains("<") && !m.Name.Contains("<") && m.GetMethodBody() != null;
            }
            catch { return false; }
        }

        private static readonly HarmonyMethod pre = new HarmonyMethod(typeof(Profiler), nameof(Pre));
        private static readonly HarmonyMethod fin = new HarmonyMethod(typeof(Profiler), nameof(Fin));
        private static readonly HarmonyMethod finThing = new HarmonyMethod(typeof(Profiler), nameof(FinThing));

        private static bool Watch(MethodBase m, string item, string mod)
        {
            if (m == null || infoOf.ContainsKey(m))
                return false;
            try
            {
                infoOf[m] = new Info { item = item, mod = mod };
                harmony.Patch(m, prefix: pre, finalizer: fin);
                ours.Add(m);
                patched++;
                return true;
            }
            catch
            {
                infoOf.Remove(m);
                return false;
            }
        }

        private static string Mod(Assembly a)
        {
            if (a == null)
                return "?";
            if (modOf.TryGetValue(a, out string n))
                return n;
            string an = a.GetName().Name;
            return an == "Assembly-CSharp" || an.StartsWith("Unity") || an == "0Harmony" ? "RimWorld" : an;
        }

        public static void Pre(out long __state)
        {
            __state = 0;
            if (!Running || !UnityData.IsInMainThread)
                return;
            __state = Stopwatch.GetTimestamp();
            if (depth < childTicks.Length)
                childTicks[depth] = 0;
            depth++;
        }

        public static Exception Fin(Exception __exception, MethodBase __originalMethod, long __state)
        {
            if (__state != 0)
            {
                Info info;
                infoOf.TryGetValue(__originalMethod, out info);
                Done(info, __state);
            }
            return __exception;
        }

        public static Exception FinThing(Exception __exception, Thing __instance, long __state)
        {
            if (__state != 0)
            {
                Info info = null;
                ThingDef d = __instance?.def;
                if (d != null && !thingInfo.TryGetValue(d, out info))
                {
                    bool official = d.modContentPack == null || d.modContentPack.IsOfficialMod;
                    thingInfo[d] = info = new Info { item = d.defName, mod = official ? "RimWorld" : d.modContentPack.Name };
                }
                Done(info, __state);
            }
            return __exception;
        }

        private static void Done(Info info, long state)
        {
            long now = Stopwatch.GetTimestamp();
            if (depth > 0)
                depth--;
            long excl = now - state - (depth < childTicks.Length ? childTicks[depth] : 0);
            if (info != null)
                info.ticks += excl;
            // the parent also leaves out our own bookkeeping
            if (depth > 0 && depth - 1 < childTicks.Length)
                childTicks[depth - 1] += Stopwatch.GetTimestamp() - state;
        }

        // ---------------- every frame ----------------
        public static void Update()
        {
            if (!Running)
                return;
            depth = 0;
            if (!InGame)
            {
                Finish(false);
                return;
            }
            TickManager tm = Find.TickManager;
            // the first frames still carry the patching pause – start counting after them
            if (Time.frameCount <= beginFrame + 2)
            {
                start = Time.realtimeSinceStartup;
                ticksStart = tm.TicksGame;
                return;
            }
            frames.Add(Time.unscaledDeltaTime * 1000f);
            if (tm.CurTimeSpeed == TimeSpeed.Paused)
                tm.CurTimeSpeed = savedSpeed == TimeSpeed.Paused ? TimeSpeed.Superfast : savedSpeed;
            speedMult = Mathf.Max(speedMult, tm.TickRateMultiplier);
            if (Time.realtimeSinceStartup - start >= Length)
                Finish(true);
        }

        private static void Finish(bool show)
        {
            Running = false;
            float t = Mathf.Max(0.1f, Time.realtimeSinceStartup - start);
            if (InGame && savedSpeed == TimeSpeed.Paused)
                Find.TickManager.CurTimeSpeed = TimeSpeed.Paused;
            if (show && frames.Count >= 10)
            {
                int ticks = Find.TickManager.TicksGame - ticksStart;
                var byItem = new Dictionary<string, double>();
                var byMod = new Dictionary<string, double>();
                Action<Info, string> add = (i, label) =>
                {
                    if (i.ticks <= 0) return;
                    double ms = i.ticks * tickToMs;
                    string k = label + " [" + i.mod + "]";
                    byItem.TryGetValue(k, out double a);
                    byItem[k] = a + ms;
                    byMod.TryGetValue(i.mod, out double b);
                    byMod[i.mod] = b + ms;
                };
                foreach (Info i in infoOf.Values) add(i, i.item);
                foreach (var kv in thingInfo) add(kv.Value, "GT_ProfThing".Translate(kv.Key.LabelCap.ToString() + " · " + kv.Key.defName).ToString());
                List<float> sorted = frames.OrderByDescending(x => x).ToList();
                int onePct = Mathf.Max(1, sorted.Count / 100);
                Last = new Result
                {
                    seconds = t,
                    avgFps = frames.Count / t,
                    lowFps = 1000f / sorted.Take(onePct).Average(),
                    worstMs = sorted[0],
                    tps = ticks / t,
                    speed = speedMult,
                    hitches = frames.Count(x => x > 100f),
                    places = patched,
                    modPlaces = patchedMods,
                    mods = byMod.Where(kv => kv.Key != "RimWorld").OrderByDescending(kv => kv.Value).Take(10).ToList(),
                    items = byItem.OrderByDescending(kv => kv.Value).Take(10).ToList(),
                    when = DateTime.Now.ToString("d.M.yyyy HH:mm")
                };
                Last.mods.Insert(0, new KeyValuePair<string, double>("GT_ProfVanilla".Translate().ToString(), byMod.TryGetValue("RimWorld", out double v) ? v : 0.0));
                MLog.Info("[Smooth FPS] what slows the game:\n" + Report.Build());
                Find.WindowStack.Add(new Dialog_SlowReport());
            }
            // removing thousands of patches takes a moment – behind the loading screen, one rebuild per method
            Removing = true;
            if (InGame)
                LongEventHandler.QueueLongEvent(Unpatch, "GT_ProfRemoving", false, null);
            else
                Unpatch();
        }

        private static void Unpatch()
        {
            try
            {
                foreach (MethodBase m in ours)
                {
                    try { harmony.Unpatch(m, HarmonyPatchType.All, HarmonyId); }
                    catch { }
                }
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] what slows the game: unpatch " + e.Message); }
            ours.Clear();
            infoOf.Clear();
            thingInfo.Clear();
            Removing = false;
            Preparing = false;
        }
    }

    // ---------------- the result window ----------------
    public class Dialog_SlowReport : Window
    {
        public override Vector2 InitialSize => new Vector2(Mathf.Min(1100f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 40f));

        public Dialog_SlowReport()
        {
            doCloseX = true;
            doCloseButton = false;
            absorbInputAroundWindow = true;
            forcePause = true;
            closeOnClickedOutside = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Profiler.Result r = Profiler.Last;
            if (r == null)
            {
                Close();
                return;
            }
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "GT_ProfTitle".Translate());
            Text.Font = GameFont.Small;
            float y = inRect.y + 38f;
            string sum = "GT_ProfSummary".Translate(r.avgFps.ToString("0"), r.lowFps.ToString("0"), r.tps.ToString("0"), (r.tps / 60f).ToString("0.0"), r.hitches, r.seconds.ToString("0"));
            float sh = Text.CalcHeight(sum, inRect.width);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, sh), sum);
            GUI.color = Color.white;
            y += sh + 10f;

            float colW = (inRect.width - 20f) / 2f;
            float listH = inRect.yMax - y - 90f;
            DrawList(new Rect(inRect.x, y, colW, listH), "GT_ProfMods".Translate(), r.mods, r.seconds);
            DrawList(new Rect(inRect.x + colW + 20f, y, colW, listH), "GT_ProfItems".Translate(), r.items, r.seconds);

            float by = inRect.yMax - 80f;
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, by, inRect.width, 40f), "GT_ProfNote".Translate(r.places, r.modPlaces));
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            float bw = 220f;
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 36f, bw, 34f), "GT_ReportCopy".Translate()))
                Report.Copy();
            if (Widgets.ButtonText(new Rect(inRect.x + bw + 10f, inRect.yMax - 36f, bw, 34f), "GT_ReportSave".Translate()))
                Report.Save();
            if (Widgets.ButtonText(new Rect(inRect.xMax - 160f, inRect.yMax - 36f, 160f, 34f), "CloseButton".Translate()))
                Close();
        }

        private static void DrawList(Rect r, string title, List<KeyValuePair<string, double>> list, float seconds)
        {
            MUI.Card(r);
            Rect c = r.ContractedBy(12f);
            Widgets.Label(new Rect(c.x, c.y, c.width, 24f), title);
            float y = c.y + 30f;
            double max = list.Count > 0 ? list.Max(kv => kv.Value) : 1.0;
            for (int i = 0; i < list.Count; i++)
            {
                if (y + 34f > c.yMax)
                    break;
                var kv = list[i];
                double perSec = kv.Value / Math.Max(0.1, seconds);
                string v = "GT_ProfMsPerSec".Translate(perSec.ToString(perSec < 10 ? "0.0" : "0"));
                float vw = 110f;
                Rect row = new Rect(c.x, y, c.width, 30f);
                Widgets.DrawBoxSolid(new Rect(row.x, row.yMax - 5f, (row.width - vw) * (float)(kv.Value / Math.Max(0.001, max)), 3f), i == 0 ? new Color(0.88f, 0.55f, 0.25f) : new Color(0.49f, 0.75f, 0.37f));
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(row.x, row.y, row.width - vw, 26f), (i + 1) + ". " + kv.Key.Truncate(row.width - vw - 10f));
                TooltipHandler.TipRegion(row, kv.Key);
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(row.xMax - vw, row.y, vw, 26f), v);
                Text.Anchor = TextAnchor.UpperLeft;
                y += 34f;
            }
            if (list.Count == 0)
                Widgets.Label(new Rect(c.x, y, c.width, 24f), "–");
        }
    }

    // ---------------- one-click performance report ----------------
    public static class Report
    {
        public static string Build()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            var sb = new StringBuilder();
            sb.AppendLine("Smooth FPS " + GraphicsTunerMod.Version + " – performance report – " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
            sb.AppendLine("RimWorld " + VersionControl.CurrentVersionStringWithRev + " · active mods: " + ModsConfig.ActiveModsInLoadOrder.Count());
            try { sb.AppendLine("PC: " + SystemInfo.processorType + " · " + SystemInfo.processorCount + " threads · " + SystemInfo.systemMemorySize + " MB RAM · " + SystemInfo.graphicsDeviceName + " " + SystemInfo.graphicsMemorySize + " MB"); }
            catch { }
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
                sb.AppendLine("Map: " + Find.CurrentMap.Size.x + "x" + Find.CurrentMap.Size.z + " · pawns " + Find.CurrentMap.mapPawns.AllPawnsSpawnedCount + " · things " + Find.CurrentMap.listerThings.AllThings.Count);
            sb.AppendLine("Now: FPS " + Runtime.Fps.ToString("0") + " · TPS " + Runtime.Tps.ToString("0") + " · simulation " + TickLoop.simMs.ToString("0.0") + " ms · drawing " + TickLoop.renderMs.ToString("0.0") + " ms");
            if (s != null)
                sb.AppendLine("Settings: profile " + s.profile + " · effects " + (s.effects * 100f).ToString("0") + "% · budget " + (s.sfBudget ? "on" : "off") + " · target " + s.sfTarget
                              + (s.sfAutoTarget ? " (smart, now " + SmartTarget.Value + ")" : "") + " · high-speed mode " + (s.fastMode ? "on" : "off") + " · keep speed " + (!s.sfSlowdown ? "yes" : "no"));
            if (TickLoop.OtherTickMod != null)
                sb.AppendLine("Other tick-loop mod: " + TickLoop.OtherTickMod);
            if (!Benchmark.LastResult.NullOrEmpty())
                sb.AppendLine("Benchmark: " + Benchmark.LastResult.Replace("\n", " | "));
            Profiler.Result r = Profiler.Last;
            if (r != null)
            {
                sb.AppendLine();
                sb.AppendLine("What slows the game (" + r.when + ", " + r.seconds.ToString("0") + " s at up to " + r.speed.ToString("0.#") + "x): avg " + r.avgFps.ToString("0") + " FPS, 1% low " + r.lowFps.ToString("0")
                              + " FPS, TPS " + r.tps.ToString("0") + ", hitches " + r.hitches);
                sb.AppendLine("Mods (ms per second):");
                foreach (var kv in r.mods)
                    sb.AppendLine("  " + kv.Key + ": " + (kv.Value / r.seconds).ToString("0.0"));
                sb.AppendLine("Things / parts (ms per second):");
                foreach (var kv in r.items)
                    sb.AppendLine("  " + kv.Key + ": " + (kv.Value / r.seconds).ToString("0.0"));
            }
            if (!HitchFinder.LastHitch.NullOrEmpty())
                sb.AppendLine("Last hitch: " + HitchFinder.LastHitch);
            return sb.ToString();
        }

        public static void Copy()
        {
            GUIUtility.systemCopyBuffer = Build();
            Messages.Message("GT_ReportCopied".Translate(), MessageTypeDefOf.TaskCompletion, false);
        }

        public static void Save()
        {
            try
            {
                string dir = Path.Combine(GenFilePaths.SaveDataFolderPath, "SmoothFPS");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "report_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".txt");
                File.WriteAllText(path, Build());
                Messages.Message("GT_ReportSaved".Translate(path), MessageTypeDefOf.TaskCompletion, false);
                Application.OpenURL(new Uri(dir).AbsoluteUri);
            }
            catch (Exception e)
            {
                Messages.Message(e.Message, MessageTypeDefOf.RejectInput, false);
            }
        }
    }

    // ---------------- 1.2: smart FPS target ----------------
    // the target lowers itself when the PC can't keep up and climbs back when there is room
    public static class SmartTarget
    {
        public static readonly int[] Steps = { 30, 45, 60, 75, 90, 120, 144, 165 };
        private static int current = -1;
        private static int low, high;
        private static float noUpUntil, lastUp = -999f;

        public static int Value
        {
            get
            {
                GfxSettings s = GraphicsTunerMod.Settings;
                if (s == null) return 60;
                int max = Mathf.Clamp(s.sfTarget, 20, 360);
                if (!s.sfAutoTarget) return max;
                if (current < 0 || current > max) current = max;
                return current;
            }
        }

        public static bool Lowered
        {
            get { GfxSettings s = GraphicsTunerMod.Settings; return s != null && s.sfAutoTarget && Value < Mathf.Clamp(s.sfTarget, 20, 360); }
        }

        // once a second
        public static void Update(float fps)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.sfAutoTarget || !TickLoop.Running || TickLoop.mode != 0 || !Application.isFocused
                || Current.ProgramState != ProgramState.Playing || Find.TickManager == null || Find.TickManager.Paused || Profiler.Running || Benchmark.Running)
            {
                low = high = 0;
                return;
            }
            int cur = Value;
            int max = Mathf.Clamp(s.sfTarget, 20, 360);
            // an FPS limit below the target: the target can never be reached above it
            if (Application.targetFrameRate > 0)
                max = Mathf.Min(max, Mathf.Max(30, Application.targetFrameRate));
            float now = Time.realtimeSinceStartup;
            if (fps < cur * 0.85f) { low++; high = 0; }
            else if (cur < max && now > noUpUntil && fps >= cur * 0.95f && TickLoop.renderMs < 1000f / Up(cur, max) * 0.75f) { high++; low = 0; }
            else { low = 0; high = 0; }
            if (low >= 8)
            {
                int down = Steps.Where(x => x < cur).DefaultIfEmpty(30).Max();
                current = Mathf.Max(30, down);
                low = 0;
                // a step up that did not hold – do not try again for 5 minutes
                if (now - lastUp < 40f)
                    noUpUntil = now + 300f;
            }
            else if (high >= 20)
            {
                current = Up(cur, max);
                high = 0;
                lastUp = now;
            }
        }

        private static int Up(int cur, int max) => Mathf.Min(max, Steps.Where(x => x > cur).DefaultIfEmpty(max).Min());
    }
}
