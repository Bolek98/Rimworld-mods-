using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // Smooth FPS – graphics and smoothness settings for RimWorld 1.7 (Waylith).
    //  * Effects: fewer flecks (smoke, sparks, dust), a cap per second, none when
    //    zoomed far out, no weapons in hands when zoomed far out, no rain/snow
    //    overlay, plant sway.
    //  * Display: V-Sync, FPS limit (also unlimited), FPS limit in background,
    //    display mode – read from the new engine by reflection, so the mod also
    //    loads on 1.6 (those rows are then hidden).
    //  * Smoothness: the 1.7 log stores a full stack trace with every message;
    //    with many mods that costs hitches – it can be switched off.
    //  * Advanced: garbage collector mode, written to RimWorld.runtimeconfig.json
    //    (backup first, restart needed).
    //  * Small on-screen display with FPS, frame time and game speed (TPS).
    // ---------------------------------------------------------------------------
    public class GfxSettings : ModSettings
    {
        // profile: 0 Performance, 1 Balanced, 2 Quality, 3 Custom
        public int profile = 1;
        public float effects = 0.7f;
        public int effectsPerSecond = 1500;     // 0 = no cap
        public bool cullEffectsZoomed = true;
        public bool cullWeaponsZoomed = true;
        public float cullZoom = 40f;
        public bool noWeather;
        public bool plantSway = true;
        public bool fastLog = true;
        public bool autoSpeedEffects = true;    // 1.1: fewer effects at speed 3 and more
        public bool adaptiveEffects = true;     // 1.1: fewer effects while FPS is under the target

        // 1.1 smoothness tab (the former Smooth Frames) – not part of the profiles
        public bool sfBudget = true;
        public int sfTarget = 60;
        public int sfMove = 75;
        public bool sfSlowdown = true;          // true = keep the picture smooth, the game may run a bit slower
        public int sfMaxSim;                    // 0 = automatic
        public bool sfCamera = true;
        public bool sfIdle = true;
        public bool sfRealNormal = true;
        public bool sfFastShare = true;
        public bool saveSmart = true;           // 1.7.4: autosave waits for a quiet moment
        public int saveMaxWaitMin = 3;         // 1.7.3: speed 3/4 – simulation gets at least the drawing time        // 1.7.2: speed 1 and 2 never run slower than real
        public int sfIdleFps = 30;
        public bool sfBackground = true;
        public bool slowAlerts = true;
        public int regenCap;                    // 0 = off (experimental)
        public bool overlayBar = true;
        public bool hitchFinder;
        public bool hitchThings;
        public bool hitchDeep;   // 1.5.0
        // 1.6.0: simulation
        public int gameSize = -1;      // -1 automatic, 0 small, 1 normal, 2 huge
        public bool smartTick = true;
        public bool worldClean = true;
        public bool fastCull = true;   // 1.7.0
        public int hitchMs = 250;
        public bool gcHold;
        public int tab;
        // 1.2
        public bool sfAutoTarget = true;        // the target lowers itself when the PC can't keep up
        // 1.2.1: own target FPS / simulation limit for each game speed (1×–4×)
        public List<int> sfTargetBySpeed = new List<int> { 0, 0, 0, 0 };    // 0 = the common target
        public List<int> sfMaxSimBySpeed = new List<int> { -1, -1, -1, -1 }; // -1 = the common limit

        public static int SpeedIdx()
        {
            try
            {
                TickManager tm = Find.TickManager;
                return tm == null ? 0 : Mathf.Clamp((int)tm.CurTimeSpeed - 1, 0, 3);
            }
            catch { return 0; }
        }

        public int TargetOverride()
        {
            int i = SpeedIdx();
            return sfTargetBySpeed != null && i < sfTargetBySpeed.Count ? sfTargetBySpeed[i] : 0;
        }

        public int MaxSimNow()
        {
            int i = SpeedIdx();
            int v = sfMaxSimBySpeed != null && i < sfMaxSimBySpeed.Count ? sfMaxSimBySpeed[i] : -1;
            return v < 0 ? sfMaxSim : v;
        }

        private static List<int> Pad(List<int> l, int def)
        {
            if (l == null) l = new List<int>();
            while (l.Count < 4) l.Add(def);
            return l;
        }
        public bool fastMode = true;            // savings at 4× and faster
        public bool fastWeather = true;         //   no weather drawing at 4×+
        public bool fastRegen;                  //   at most 8 map sections rebuilt per frame at 4×+ (experimental)
        // 1.2: memory & freezes (the former Fewer Freezes)
        public bool memAuto = true;
        public float memSliceMs = 8f;
        public bool memOnPause = true;
        public int memThresholdMb = 400;
        public bool memOnSave = true;
        public bool memUnload;
        public bool memRamWarn = true;
        public bool vramWarn = true;   // 1.3.0
        public bool memLowLatency = true;       // 1.7: no blocking full clean-ups during play
        public bool memCompact;                 // 1.7: compact memory during long pauses
        public int texCap = 2048;               // 1.3.3: oversized mod pictures start smaller (0 = off)
        public bool memHud;


        // display – your own choice, never changed by a profile
        public int vsync = -1;                  // -1 = like the game, else index into the engine's modes
        public int fpsLimit = -2;               // -2 = like the game, -1 = unlimited, else FPS
        public int bgFps;                       // 0 = no change
        public int laptop;                      // 1.4.0: 0 off, 1 always, 2 only on battery
        public int laptopFps = 45;
        public int displayMode = -1;            // -1 = like the game
        public bool overlay;

        // advanced
        public int gcMode;                      // 0 untouched, 1 smooth, 2 server

        public static readonly string[] ProfileFields =
        {
            "effects", "effectsPerSecond", "cullEffectsZoomed", "cullWeaponsZoomed", "cullZoom", "noWeather", "plantSway", "fastLog", "autoSpeedEffects", "adaptiveEffects"
        };

        public static GfxSettings PresetValues(int which)
        {
            var p = new GfxSettings();
            switch (which)
            {
                case 0:
                    p.effects = 0.35f; p.effectsPerSecond = 600; p.cullEffectsZoomed = true; p.cullWeaponsZoomed = true;
                    p.cullZoom = 30f; p.noWeather = true; p.plantSway = false; p.fastLog = true; p.autoSpeedEffects = true; p.adaptiveEffects = true;
                    break;
                case 2:
                    p.effects = 1f; p.effectsPerSecond = 0; p.cullEffectsZoomed = false; p.cullWeaponsZoomed = false;
                    p.cullZoom = 60f; p.noWeather = false; p.plantSway = true; p.fastLog = false; p.autoSpeedEffects = false; p.adaptiveEffects = false;
                    break;
            }
            return p;
        }

        public void Preset(int which)
        {
            GfxSettings p = PresetValues(which);
            foreach (string f in ProfileFields)
            {
                FieldInfo fi = typeof(GfxSettings).GetField(f);
                fi.SetValue(this, fi.GetValue(p));
            }
        }

        public bool Matches(int which)
        {
            GfxSettings p = PresetValues(which);
            foreach (string f in ProfileFields)
            {
                FieldInfo fi = typeof(GfxSettings).GetField(f);
                object a = fi.GetValue(this), b = fi.GetValue(p);
                if (a is float fa && b is float fb ? !Mathf.Approximately(fa, fb) : !Equals(a, b))
                    return false;
            }
            return true;
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref profile, "profile", 1);
            Scribe_Values.Look(ref effects, "effects", 0.7f);
            Scribe_Values.Look(ref effectsPerSecond, "effectsPerSecond", 1500);
            Scribe_Values.Look(ref cullEffectsZoomed, "cullEffectsZoomed", true);
            Scribe_Values.Look(ref cullWeaponsZoomed, "cullWeaponsZoomed", true);
            Scribe_Values.Look(ref cullZoom, "cullZoom", 40f);
            Scribe_Values.Look(ref noWeather, "noWeather", false);
            Scribe_Values.Look(ref plantSway, "plantSway", true);
            Scribe_Values.Look(ref fastLog, "fastLog", true);
            Scribe_Values.Look(ref vsync, "vsync", -1);
            Scribe_Values.Look(ref fpsLimit, "fpsLimit", -2);
            Scribe_Values.Look(ref bgFps, "bgFps", 0);
            Scribe_Values.Look(ref laptop, "laptop", 0);
            Scribe_Values.Look(ref laptopFps, "laptopFps", 45);
            Scribe_Values.Look(ref displayMode, "displayMode", -1);
            Scribe_Values.Look(ref overlay, "overlay", false);
            Scribe_Values.Look(ref gcMode, "gcMode", 0);
            Scribe_Values.Look(ref autoSpeedEffects, "autoSpeedEffects", true);
            Scribe_Values.Look(ref adaptiveEffects, "adaptiveEffects", true);
            Scribe_Values.Look(ref sfBudget, "sfBudget", true);
            Scribe_Values.Look(ref sfTarget, "sfTarget", 60);
            Scribe_Values.Look(ref sfMove, "sfMove", 75);
            Scribe_Values.Look(ref sfSlowdown, "sfSlowdown", true);
            Scribe_Values.Look(ref sfMaxSim, "sfMaxSim", 0);
            Scribe_Values.Look(ref sfCamera, "sfCamera", true);
            Scribe_Values.Look(ref sfIdle, "sfIdle", true);
            Scribe_Values.Look(ref sfRealNormal, "sfRealNormal", true);
            Scribe_Values.Look(ref sfFastShare, "sfFastShare", true);
            Scribe_Values.Look(ref saveSmart, "saveSmart", true);
            Scribe_Values.Look(ref saveMaxWaitMin, "saveMaxWaitMin", 3);
            Scribe_Values.Look(ref sfIdleFps, "sfIdleFps", 30);
            Scribe_Values.Look(ref sfBackground, "sfBackground", true);
            Scribe_Values.Look(ref slowAlerts, "slowAlerts", true);
            Scribe_Values.Look(ref regenCap, "regenCap", 0);
            Scribe_Values.Look(ref overlayBar, "overlayBar", true);
            Scribe_Values.Look(ref hitchFinder, "hitchFinder", false);
            Scribe_Values.Look(ref hitchThings, "hitchThings", false);
            Scribe_Values.Look(ref hitchDeep, "hitchDeep", false);
            Scribe_Values.Look(ref gameSize, "gameSize", -1);
            Scribe_Values.Look(ref smartTick, "smartTick", true);
            Scribe_Values.Look(ref worldClean, "worldClean", true);
            Scribe_Values.Look(ref fastCull, "fastCull", true);
            Scribe_Values.Look(ref hitchMs, "hitchMs", 250);
            Scribe_Values.Look(ref gcHold, "gcHold", false);
            Scribe_Values.Look(ref tab, "tab", 0);
            Scribe_Values.Look(ref sfAutoTarget, "sfAutoTarget", true);
            Scribe_Collections.Look(ref sfTargetBySpeed, "sfTargetBySpeed", LookMode.Value);
            Scribe_Collections.Look(ref sfMaxSimBySpeed, "sfMaxSimBySpeed", LookMode.Value);
            sfTargetBySpeed = Pad(sfTargetBySpeed, 0);
            sfMaxSimBySpeed = Pad(sfMaxSimBySpeed, -1);
            Scribe_Values.Look(ref fastMode, "fastMode", true);
            Scribe_Values.Look(ref fastWeather, "fastWeather", true);
            Scribe_Values.Look(ref fastRegen, "fastRegen", false);
            Scribe_Values.Look(ref memAuto, "memAuto", true);
            Scribe_Values.Look(ref memSliceMs, "memSliceMs", 8f);
            Scribe_Values.Look(ref memOnPause, "memOnPause", true);
            Scribe_Values.Look(ref memThresholdMb, "memThresholdMb", 400);
            Scribe_Values.Look(ref memOnSave, "memOnSave", true);
            Scribe_Values.Look(ref memUnload, "memUnload", false);
            Scribe_Values.Look(ref memRamWarn, "memRamWarn", true);
            Scribe_Values.Look(ref vramWarn, "vramWarn", true);
            Scribe_Values.Look(ref memLowLatency, "memLowLatency", true);
            Scribe_Values.Look(ref memCompact, "memCompact", false);
            Scribe_Values.Look(ref texCap, "texCap", 2048);
            Scribe_Values.Look(ref memHud, "memHud", false);

            if (Scribe.mode == LoadSaveMode.LoadingVars && profile >= 0 && profile <= 2)
                Preset(profile);
        }
    }

    // ---------------- the new engine, by reflection ----------------
    public static class Engine
    {
        private static bool init;
        private static object screen;
        private static MethodInfo applyVSync, applyDisplay;
        private static Type vsyncType, displayType;
        private static PropertyInfo vsyncProp, displayProp;
        public static string[] VSyncNames = new string[0];
        public static string[] DisplayNames = new string[0];
        public static bool IsWaylith;

        private static Type Find(string name)
        {
            foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (Type t in types)
                    if (t != null && t.Name == name)
                        return t;
            }
            return null;
        }

        private static Type FindIn(string assemblyName, string typeName)
        {
            Assembly a = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(x => x.GetName().Name == assemblyName);
            if (a == null) return null;
            try { return a.GetTypes().FirstOrDefault(t => t.Name == typeName); }
            catch (ReflectionTypeLoadException e) { return e.Types.FirstOrDefault(t => t != null && t.Name == typeName); }
            catch { return null; }
        }

        public static void Init()
        {
            if (init) return;
            init = true;
            try
            {
                Type rt = FindIn("UnityStubs", "UnityRuntime") ?? FindIn("Waylith.UnityCompat", "UnityRuntime");
                if (rt == null) return;
                object cur = rt.GetProperty("Current", BindingFlags.Public | BindingFlags.Static)?.GetValue(null, null);
                if (cur == null) { init = false; return; }
                screen = cur.GetType().GetProperty("Screen")?.GetValue(cur, null);
                if (screen == null) return;
                IsWaylith = true;
                vsyncProp = screen.GetType().GetProperty("VSyncMode");
                displayProp = screen.GetType().GetProperty("DisplayMode");
                vsyncType = vsyncProp?.PropertyType;
                displayType = displayProp?.PropertyType;
                applyVSync = screen.GetType().GetMethod("ApplyVSyncMode");
                applyDisplay = screen.GetType().GetMethod("ApplyDisplayMode");
                if (vsyncType != null && vsyncType.IsEnum) VSyncNames = Enum.GetNames(vsyncType);
                if (displayType != null && displayType.IsEnum) DisplayNames = Enum.GetNames(displayType);
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] engine settings not available: " + ex.Message);
            }
        }

        public static string CurrentVSync => vsyncProp?.GetValue(screen, null)?.ToString();
        public static string CurrentDisplay => displayProp?.GetValue(screen, null)?.ToString();

        public static void SetVSync(int index)
        {
            if (applyVSync == null || index < 0 || index >= VSyncNames.Length) return;
            if (CurrentVSync == VSyncNames[index]) return;
            applyVSync.Invoke(screen, new[] { Enum.Parse(vsyncType, VSyncNames[index]) });
        }

        public static void SetDisplay(int index)
        {
            if (applyDisplay == null || index < 0 || index >= DisplayNames.Length) return;
            if (CurrentDisplay == DisplayNames[index]) return;
            applyDisplay.Invoke(screen, new[] { Enum.Parse(displayType, DisplayNames[index]) });
        }

        // 1.7 writes a full stack trace with every log line
        private static bool logInit;
        private static MemberInfo logTraces, fileTraces;

        private static void SetStatic(MemberInfo m, bool v)
        {
            if (m is PropertyInfo p && p.CanWrite) p.SetValue(null, v, null);
            else if (m is FieldInfo f) f.SetValue(null, v);
        }

        private static MemberInfo StaticBool(Type t, string name)
        {
            if (t == null) return null;
            const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            return (MemberInfo)t.GetProperty(name, bf) ?? t.GetField(name, bf);
        }

        public static void SetFastLog(bool fast)
        {
            if (!IsWaylith) return;
            try
            {
                if (!logInit)
                {
                    logInit = true;
                    logTraces = StaticBool(Find("WaylithLogFormatter"), "EnableStackTraces");
                    fileTraces = StaticBool(Find("FileLog"), "CaptureStackTraces");
                }
                SetStatic(logTraces, !fast);
                SetStatic(fileTraces, !fast);
            }
            catch (Exception ex)
            {
                MLog.WarnOnce("[Smooth FPS] fast log: " + ex.Message, 0x6f7a01);
            }
        }

        // ---------------- garbage collector (runtimeconfig.json) ----------------
        public static string ConfigPath
        {
            get
            {
                try
                {
                    string p = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "RimWorld.runtimeconfig.json");
                    return File.Exists(p) ? p : null;
                }
                catch { return null; }
            }
        }

        private static readonly string[] GcKeys = { "System.GC.Concurrent", "System.GC.RetainVM", "System.GC.Server", "System.GC.ConserveMemory" };

        public static int ReadGcMode()
        {
            string p = ConfigPath;
            if (p == null) return -1;
            string s = File.ReadAllText(p);
            if (s.Contains("\"System.GC.Server\": true")) return 2;
            if (s.Contains("\"System.GC.RetainVM\": true")) return 1;
            return 0;
        }

        // returns null on success, otherwise the reason
        public static string WriteGcMode(int mode)
        {
            string p = ConfigPath;
            if (p == null) return "no config";
            try
            {
                string bak = p + ".graphicstuner.bak";
                if (!File.Exists(bak))
                    File.Copy(p, bak);
                List<string> lines = File.ReadAllLines(p).ToList();
                lines.RemoveAll(l => GcKeys.Any(k => l.Contains("\"" + k + "\"")));
                int at = lines.FindIndex(l => l.Contains("\"configProperties\""));
                if (at < 0) return "configProperties not found";
                var add = new List<string>();
                if (mode >= 1)
                {
                    add.Add("      \"System.GC.Concurrent\": true,");
                    add.Add("      \"System.GC.RetainVM\": true,");
                }
                if (mode == 2)
                    add.Add("      \"System.GC.Server\": true,");
                lines.InsertRange(at + 1, add);
                File.WriteAllLines(p, lines.ToArray());
                return null;
            }
            catch (Exception ex)
            {
                return ex.Message;
            }
        }

        public static string RestoreConfig()
        {
            string p = ConfigPath;
            if (p == null) return "no config";
            string bak = p + ".graphicstuner.bak";
            if (!File.Exists(bak)) return null;
            try { File.Copy(bak, p, true); return null; }
            catch (Exception ex) { return ex.Message; }
        }
    }

    // ---------------- runtime: applies settings and measures ----------------
    public static class Runtime
    {
        private static float nextApply;
        public static float Fps, FrameMs, Tps;
        private static int frames;
        private static float frameStart = -1f;
        private static int ticksAt = -1;
        private static float tpsStart;
        public static int EffectsMade, EffectsSkipped;
        private static int effectsThisSecond;
        private static float effectsSecond;
        private static float acc;
        private static bool inBackground;
        private static int savedTarget = -1;
        private static bool lastSway;
        private static bool swayInit, lastPrefsSway;

        public static void Update()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null) return;
            try { Vram.Tick(); } catch { }
            float now = Time.realtimeSinceStartup;
            frames++;
            if (frameStart < 0f) frameStart = now;
            if (now - frameStart >= 1f)
            {
                Fps = frames / (now - frameStart);
                FrameMs = Fps > 0f ? 1000f / Fps : 0f;
                frames = 0;
                frameStart = now;
                int t = Current.ProgramState == ProgramState.Playing && Find.TickManager != null ? Find.TickManager.TicksGame : -1;
                if (t >= 0 && ticksAt >= 0)
                    Tps = (t - ticksAt) / Mathf.Max(0.001f, now - tpsStart);
                else
                    Tps = 0f;
                ticksAt = t;
                tpsStart = now;
            }
            Benchmark.Update();
            Profiler.Update();
            Freezer.Update();
            if (now < nextApply) return;
            nextApply = now + 1f;
            SmartTarget.Update(Fps);
            UpdateAdaptive(s);
            try { Apply(s); }
            catch (Exception ex) { Log.ErrorOnce("[Smooth FPS] " + ex.Message, 0x6f7a02); }
        }

        public static void Apply(GfxSettings s)
        {
            Engine.Init();
            // FPS limit (and a lower one in the background)
            bool lap = Laptop.Active(s);
            int bgFps = s.bgFps > 0 ? s.bgFps : (lap ? 5 : (s.sfBackground && Smooth.Active ? 10 : 0));
            bool background = !Application.isFocused && bgFps > 0;
            if (background && !inBackground)
            {
                inBackground = true;
                savedTarget = Application.targetFrameRate;
            }
            else if (!background && inBackground)
            {
                inBackground = false;
                if (s.fpsLimit == -2)
                    Application.targetFrameRate = savedTarget;   // back to the game's own value
            }
            int want = int.MinValue;
            if (background)
                want = bgFps;
            else if (s.fpsLimit == -1)
                want = -1;
            else if (s.fpsLimit > 0)
                want = s.fpsLimit;
            // 1.4.0: laptop mode caps the FPS (never raises a lower limit)
            if (lap && !background && (want == int.MinValue || want <= 0 || want > s.laptopFps))
                want = s.laptopFps;
            if (want != int.MinValue && Application.targetFrameRate != want)
                Application.targetFrameRate = want;
            if (s.vsync >= 0) Engine.SetVSync(s.vsync);
            if (s.displayMode >= 0) Engine.SetDisplay(s.displayMode);
            Engine.SetFastLog(s.fastLog);
            // 1.3.2: the game's own option and this one are the same switch. At start and whenever you
            // change it in the game options, the game's value wins (before, Smooth FPS turned the
            // swaying back on at every start); a change here goes to the game.
            if (!swayInit || Prefs.PlantWindSway != lastPrefsSway)
            {
                swayInit = true;
                s.plantSway = Prefs.PlantWindSway;
                lastSway = lastPrefsSway = s.plantSway;
            }
            else if (lastSway != s.plantSway)
            {
                lastSway = lastPrefsSway = s.plantSway;
                Prefs.PlantWindSway = s.plantSway;
            }
        }

        // 1.1: effect multiplier from game speed and from FPS under the target
        public static float Adaptive = 1f;
        public static float SpeedFactor = 1f;

        private static void UpdateAdaptive(GfxSettings s)
        {
            float target = Smooth.Active ? TickLoop.TargetFps(s) : 60f;
            if (s.adaptiveEffects && Fps > 1f && Application.isFocused)
            {
                if (Fps < target * 0.9f) Adaptive = Mathf.Max(0.25f, Adaptive - 0.15f);
                else if (Fps > target * 0.97f) Adaptive = Mathf.Min(1f, Adaptive + 0.05f);
            }
            else
                Adaptive = 1f;
            float mult = Current.ProgramState == ProgramState.Playing && Find.TickManager != null ? Find.TickManager.TickRateMultiplier : 1f;
            SpeedFactor = s.autoSpeedEffects && mult >= 5.5f ? 0.5f : 1f;   // speed 3 = ×6
            // 1.2: high-speed mode – at 4× and faster even fewer effects (nobody can follow them anyway)
            if (FastNow(s)) SpeedFactor = Mathf.Min(SpeedFactor, 0.25f);
        }

        // 1.2: 4× (Smart Speed and similar) and faster
        public static bool FastNow(GfxSettings s)
        {
            // speed 3 is ×6 (×12 when nothing happens); the 4th speed (Smart Speed, dev) is ×15 and more
            if (s == null || !s.fastMode || Current.ProgramState != ProgramState.Playing || Find.TickManager == null) return false;
            TickManager tm = Find.TickManager;
            return tm.CurTimeSpeed >= TimeSpeed.Ultrafast || tm.TickRateMultiplier > 12.5f;
        }

        public static bool ZoomedOut(GfxSettings s)
        {
            CameraDriver cd = Find.CameraDriver;
            return cd != null && cd.ZoomRootSize > s.cullZoom;
        }

        // true = make this effect
        public static bool AllowEffect()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null) return true;
            if (s.cullEffectsZoomed && ZoomedOut(s)) { EffectsSkipped++; return false; }
            float now = Time.realtimeSinceStartup;
            if (now - effectsSecond >= 1f)
            {
                effectsSecond = now;
                effectsThisSecond = 0;
            }
            if (s.effectsPerSecond > 0 && effectsThisSecond >= s.effectsPerSecond) { EffectsSkipped++; return false; }
            float share = s.effects * Adaptive * SpeedFactor * Laptop.EffectFactor(s);
            if (share < 0.999f)
            {
                acc += share;
                if (acc < 1f) { EffectsSkipped++; return false; }
                acc -= 1f;
            }
            effectsThisSecond++;
            EffectsMade++;
            return true;
        }

        public static void Overlay()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || Event.current.type != EventType.Repaint) return;
            bool bench = Benchmark.Running;
            if (!s.overlay && !bench && !Profiler.Running) return;
            string t = "GT_Overlay".Translate(Fps.ToString("0"), FrameMs.ToString("0.0"), Tps > 0f ? Tps.ToString("0") : "–");
            if (Smooth.Active && TickLoop.Running && TickLoop.mode != 0)
                t += " · " + ("GT_Mode" + TickLoop.mode).Translate();
            if (bench)
                t += " · " + "GT_BenchRunning".Translate(Benchmark.Remaining.ToString("0"));
            if (Profiler.Running)
                t += " · " + "GT_ProfRunning".Translate(Profiler.Remaining.ToString("0"));
            if (SmartTarget.Lowered)
                t += " · " + "GT_SmartNow".Translate(SmartTarget.Value);
            if (!HitchFinder.LastHitch.NullOrEmpty())
                t += "\n" + "GT_LastHitch".Translate(HitchFinder.LastHitch);
            Text.Font = GameFont.Tiny;
            Vector2 size = Text.CalcSize(t);
            bool bar = s.overlayBar && Smooth.Active && TickLoop.Running;
            float w = Mathf.Max(size.x, bar ? 220f : 0f);
            float h = size.y + 4f + (bar ? 22f : 0f);
            Rect r = new Rect(UI.screenWidth - w - 20f, 6f, w + 12f, h);
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.6f));
            Widgets.Label(new Rect(r.x + 6f, r.y + 2f, w + 4f, size.y), t);
            if (bar)
            {
                // simulation | drawing | reserve, against the frame time of the target FPS
                float frame = 1000f / Mathf.Max(20, TickLoop.TargetFps(s));
                float total = Mathf.Max(frame, TickLoop.simMs + TickLoop.renderMs);
                Rect b = new Rect(r.x + 6f, r.y + size.y + 4f, w, 7f);
                Widgets.DrawBoxSolid(b, new Color(0.25f, 0.27f, 0.3f));
                float ws = b.width * TickLoop.simMs / total, wr = b.width * TickLoop.renderMs / total;
                Widgets.DrawBoxSolid(new Rect(b.x, b.y, ws, b.height), new Color(0.49f, 0.75f, 0.37f));
                Widgets.DrawBoxSolid(new Rect(b.x + ws, b.y, wr, b.height), new Color(0.35f, 0.6f, 0.9f));
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(b.x, b.yMax, w, 14f), "GT_Bar".Translate(TickLoop.simMs.ToString("0.0"), TickLoop.renderMs.ToString("0.0")));
                GUI.color = Color.white;
            }
            Text.Font = GameFont.Small;
        }
    }

    // ---------------- patches ----------------
    [HarmonyPatch(typeof(Root), "Update")]
    public static class RootUpdatePatch
    {
        public static void Postfix()
        {
            Runtime.Update();
            SaveTiming.Update();
        }
    }

    [HarmonyPatch(typeof(UIRoot), nameof(UIRoot.UIRootOnGUI))]
    public static class OverlayPatch
    {
        public static void Postfix()
        {
            Runtime.Overlay();
            Freezer.OnGUI();
        }
    }

    [HarmonyPatch(typeof(FleckManager), nameof(FleckManager.CreateFleck))]
    public static class FleckPatch
    {
        public static bool Prefix() => Runtime.AllowEffect();
    }

    [HarmonyPatch(typeof(WeatherManager), nameof(WeatherManager.DrawAllWeather))]
    public static class WeatherPatch
    {
        public static bool Prefix()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s != null && s.fastWeather && Runtime.FastNow(s)) return false;
            return s == null || !s.noWeather;
        }
    }

    [HarmonyPatch(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAndApparelExtras))]
    public static class WeaponPatch
    {
        public static bool Prefix(PawnRenderFlags flags)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.cullWeaponsZoomed || (flags & PawnRenderFlags.Portrait) != 0) return true;
            return !Runtime.ZoomedOut(s);
        }
    }

    // ---------------- mod and settings window ----------------
    public class GraphicsTunerMod : Mod
    {
        public const string Version = "1.3.0";
        public static GfxSettings Settings;
        private Vector2 scroll;
        private float viewHeight = 900f;
        private static readonly string[] ProfKeys = { "GT_ProfPerf", "GT_ProfBal", "GT_ProfQual", "GT_ProfCustom" };
        private static readonly int[] FpsChoices = { -2, -1, 30, 60, 75, 90, 120, 144, 165, 240 };
        private static readonly int[] BgChoices = { 0, 10, 20, 30, 60 };
        private static string gcMessage;

        public GraphicsTunerMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<GfxSettings>();
            var harmony = new Harmony("misak.graphicstuner");
            MUI.Init(GfxSettings.PresetValues(1), GfxSettings.ProfileFields);
            MUI.PatchWindow(harmony, typeof(GraphicsTunerMod));
            foreach (Type t in new[] { typeof(RootUpdatePatch), typeof(OverlayPatch), typeof(FleckPatch), typeof(WeatherPatch), typeof(WeaponPatch) })
            {
                try { harmony.CreateClassProcessor(t).Patch(); }
                catch (Exception ex) { MLog.Warn("[Smooth FPS] " + t.Name + " skipped: " + ex.Message); }
            }
            Smooth.Patch(harmony);
            Freezer.Patch(harmony);
            LocationGeneratorFast.Patch(harmony);   // 1.3.2
            Sim.Patch(harmony);                     // 1.6.0
            FastCull.Patch(harmony);                // 1.7.0
            WorldPathSpread.Patch(harmony);         // 1.7.0
            SaveTiming.Patch(harmony);              // 1.7.4
            ModEffects.Patch(harmony);              // 1.3.2: effects from mods too
            TextureCap.Patch(harmony);              // 1.3.3: oversized mod textures
            // 1.3.4: no more swapping textures that were loaded before Smooth FPS started – icons and
            // pictures already handed out kept pointing at the destroyed originals (pink X, blank icons).
            // Only textures loaded from now on start smaller.
            MLog.Game("[Smooth FPS] v" + Version + " loaded (messages: Logs\\SmoothFPS.log).");
            // 1.3.0: what happened if the last game crashed
            LongEventHandler.ExecuteWhenFinished(CrashCheck.Run);
        }

        public override string SettingsCategory() => "Smooth FPS";

        // 1.3.0: the hitch finder starts at once when switched on (before it needed a restart)
        public static void StartHitchNow()
        {
            try
            {
                HitchFinder.Start();
                if (HitchFinder.IsOn && Current.ProgramState == ProgramState.Playing)
                {
                    HitchFinder.WatchGeneration();
                    HitchFinder.WatchDrawing();
                }
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] hitch finder start: " + e.Message);
            }
        }

        private static void Muted(Rect r, string text)
        {
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(r, text);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void Note(Listing_Standard l, string text)
        {
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label(text);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            l.Gap(4f);
        }

        private static void Check(Listing_Standard l, string key, ref bool value, string field)
        {
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref value, Settings, field);
        }

        // label on the left, a button with the current value on the right
        private static void Dropdown(Listing_Standard l, string label, string tip, string current, Func<List<FloatMenuOption>> options)
        {
            Rect row = l.GetRect(30f);
            Widgets.DrawHighlightIfMouseover(row);
            if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(row, tip);
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 190f, 24f), label);
            if (Widgets.ButtonText(new Rect(row.xMax - 180f, row.y + 2f, 180f, 26f), current))
                Find.WindowStack.Add(new FloatMenu(options()));
            l.Gap(2f);
        }

        private static string FpsText(int v) => v == -2 ? "GT_Game".Translate().ToString() : v == -1 ? "GT_Unlimited".Translate().ToString() : v.ToString();

        private static string ModeName(string prefix, string engineName)
        {
            string key = prefix + engineName;
            return key.CanTranslate() ? key.Translate().ToString() : GenText.SplitCamelCase(engineName);
        }

        private static readonly string[] TabKeys = { "GT_TabGraphics", "GT_TabSmooth", "GT_TabMemory", "GT_TabSim" };
        private static readonly int[] TargetChoices = { 30, 45, 60, 75, 90, 120, 144, 165 };
        private Vector2 scroll2;
        private float viewHeight2 = 900f;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            GfxSettings s = Settings;
            Engine.Init();
            MUI.Version(inRect, "v" + Version + " · Bolek98");
            // tabs
            float tx = inRect.x;
            for (int i = 0; i < TabKeys.Length; i++)
            {
                string label = TabKeys[i].Translate();
                Text.Font = GameFont.Small;
                float w = Text.CalcSize(label).x + 36f;
                Rect tr = new Rect(tx, inRect.y, w, 30f);
                bool sel = s.tab == i;
                MUI.Fill(tr, sel ? MUI.CardBg2 : MUI.CardBg, 4f);
                if (sel) MUI.Fill(new Rect(tr.x + 6f, tr.yMax - 3f, tr.width - 12f, 3f), MUI.Accent, 1f);
                else Widgets.DrawHighlightIfMouseover(tr);
                GUI.color = sel ? MUI.Text1 : MUI.Muted;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(tr, label);
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(tr)) s.tab = i;
                tx += w + 8f;
            }
            Rect body = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
            if (s.tab == 1) SmoothTab(body, s);
            else if (s.tab == 2) MemoryTab(body, s);
            else if (s.tab == 3) SimTab(body, s);
            else GraphicsTab(body, s);
        }

        private void GraphicsTab(Rect inRect, GfxSettings s)
        {
            Muted(new Rect(inRect.x, inRect.y, inRect.width, 20f), "GT_Profile".Translate());
            float cw = (inRect.width - 36f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 78f);
                if (MUI.ProfileCard(c, ProfKeys[i].Translate(), (ProfKeys[i] + "_Card").Translate(), s.profile == i, i == 1))
                {
                    if (i < 3) s.Preset(i);
                    s.profile = i;
                }
            }
            Muted(new Rect(inRect.x, inRect.y + 104f, inRect.width, 20f), "GT_ProfileHint".Translate());

            Rect outRect = new Rect(inRect.x, inRect.y + 128f, inRect.width, inRect.height - 128f);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(viewHeight, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float colW = (view.width - 28f) / 2f;

            // ---- left: effects and world (profile)
            var l = new Listing_Standard();
            l.Begin(new Rect(0f, 0f, colW, 100000f));
            MUI.Section(l, "GT_SecEffects".Translate());
            s.effects = Mathf.Round(MUI.Slider(l, "GT_Effects".Translate((s.effects * 100f).ToString("0")), s.effects, 0.1f, 1f, "GT_Effects_Desc".Translate(), s, "effects") * 20f) / 20f;
            float cap = MUI.Slider(l, "GT_Cap".Translate(s.effectsPerSecond == 0 ? "GT_Unlimited".Translate().ToString() : s.effectsPerSecond.ToString()), s.effectsPerSecond, 0f, 3000f, "GT_Cap_Desc".Translate(), s, "effectsPerSecond");
            s.effectsPerSecond = Mathf.RoundToInt(cap / 100f) * 100;
            Check(l, "GT_CullEffects", ref s.cullEffectsZoomed, "cullEffectsZoomed");
            Check(l, "GT_CullWeapons", ref s.cullWeaponsZoomed, "cullWeaponsZoomed");
            s.cullZoom = Mathf.Round(MUI.Slider(l, "GT_CullZoom".Translate(s.cullZoom.ToString("0")), s.cullZoom, 15f, 80f, "GT_CullZoom_Desc".Translate(), s, "cullZoom"));
            Check(l, "GT_NoWeather", ref s.noWeather, "noWeather");
            Check(l, "GT_PlantSway", ref s.plantSway, "plantSway");
            Check(l, "GT_AutoSpeed", ref s.autoSpeedEffects, "autoSpeedEffects");
            Check(l, "GT_Adaptive", ref s.adaptiveEffects, "adaptiveEffects");
            MUI.Section(l, "GT_SecSmooth".Translate());
            Check(l, "GT_FastLog", ref s.fastLog, "fastLog");
            Note(l, "GT_FastLog_Note".Translate());
            if (s.profile >= 0 && s.profile <= 2 && !s.Matches(s.profile))
                s.profile = 3;
            float leftH = l.CurHeight;
            l.End();

            // ---- right: display, advanced, now
            var r = new Listing_Standard();
            r.Begin(new Rect(colW + 28f, 0f, colW, 100000f));
            MUI.Section(r, "GT_SecDisplay".Translate());
            if (Engine.IsWaylith && Engine.VSyncNames.Length > 0)
            {
                string cur = s.vsync < 0 || s.vsync >= Engine.VSyncNames.Length ? "GT_Game".Translate().ToString() : ModeName("GT_VSync_", Engine.VSyncNames[s.vsync]);
                Dropdown(r, "GT_VSync".Translate(), "GT_VSync_Desc".Translate(), cur, () =>
                {
                    var list = new List<FloatMenuOption> { new FloatMenuOption("GT_Game".Translate(), () => s.vsync = -1) };
                    for (int i = 0; i < Engine.VSyncNames.Length; i++)
                    {
                        int li = i;
                        list.Add(new FloatMenuOption(ModeName("GT_VSync_", Engine.VSyncNames[i]), () => s.vsync = li));
                    }
                    return list;
                });
            }
            Dropdown(r, "GT_Fps".Translate(), "GT_Fps_Desc".Translate(), FpsText(s.fpsLimit), () =>
                FpsChoices.Select(v => new FloatMenuOption(FpsText(v), () => s.fpsLimit = v)).ToList());
            Dropdown(r, "GT_BgFps".Translate(), "GT_BgFps_Desc".Translate(), s.bgFps == 0 ? "GT_NoChange".Translate().ToString() : s.bgFps.ToString(), () =>
                BgChoices.Select(v => new FloatMenuOption(v == 0 ? "GT_NoChange".Translate().ToString() : v.ToString(), () => s.bgFps = v)).ToList());
            if (Engine.IsWaylith && Engine.DisplayNames.Length > 0)
            {
                string cur = s.displayMode < 0 || s.displayMode >= Engine.DisplayNames.Length ? "GT_Game".Translate().ToString() : ModeName("GT_Display_", Engine.DisplayNames[s.displayMode]);
                Dropdown(r, "GT_Display".Translate(), "GT_Display_Desc".Translate(), cur, () =>
                {
                    var list = new List<FloatMenuOption> { new FloatMenuOption("GT_Game".Translate(), () => s.displayMode = -1) };
                    for (int i = 0; i < Engine.DisplayNames.Length; i++)
                    {
                        int li = i;
                        list.Add(new FloatMenuOption(ModeName("GT_Display_", Engine.DisplayNames[i]), () => s.displayMode = li));
                    }
                    return list;
                });
            }
            MUI.Check(r, "GT_OverlayOn".Translate(), "GT_OverlayOn_Desc".Translate(), ref s.overlay, s, "overlay");
            // 1.4.0: laptop mode
            string[] lapNames = { "GT_LaptopOff".Translate(), "GT_LaptopOn".Translate(), "GT_LaptopBattery".Translate() };
            Dropdown(r, "GT_Laptop".Translate(), "GT_Laptop_Desc".Translate(), lapNames[Mathf.Clamp(s.laptop, 0, 2)], () =>
                Enumerable.Range(0, 3).Select(i => new FloatMenuOption(lapNames[i], () => s.laptop = i)).ToList());
            if (s.laptop > 0)
            {
                int[] lapFps = { 30, 40, 45, 50, 60 };
                Dropdown(r, "GT_LaptopFps".Translate(), "GT_LaptopFps_Desc".Translate(), s.laptopFps.ToString(), () =>
                    lapFps.Select(v => new FloatMenuOption(v.ToString(), () => s.laptopFps = v)).ToList());
                if (s.laptop == 2)
                    Note(r, (Laptop.OnBattery() ? "GT_LaptopNowBattery" : "GT_LaptopNowMains").Translate());
            }
            if (!Engine.IsWaylith)
                Note(r, "GT_Not17".Translate());

            MUI.Section(r, "GT_SecAdvanced".Translate());
            if (Engine.ConfigPath != null)
            {
                int real = Engine.ReadGcMode();
                string[] gcNames = { "GT_Gc0".Translate(), "GT_Gc1".Translate(), "GT_Gc2".Translate() };
                Dropdown(r, "GT_Gc".Translate(), "GT_Gc_Desc".Translate(), gcNames[Mathf.Clamp(real, 0, 2)], () =>
                    Enumerable.Range(0, 3).Select(i => new FloatMenuOption(gcNames[i], () =>
                    {
                        string err = Engine.WriteGcMode(i);
                        s.gcMode = i;
                        gcMessage = err == null ? "GT_GcSaved".Translate().ToString() : "GT_GcError".Translate(err).ToString();
                    })).ToList());
                Note(r, "GT_Gc_Note".Translate());
                if (r.ButtonText("GT_GcRestore".Translate()))
                {
                    string err = Engine.RestoreConfig();
                    s.gcMode = 0;
                    gcMessage = err == null ? "GT_GcRestored".Translate().ToString() : "GT_GcError".Translate(err).ToString();
                }
                if (gcMessage != null)
                {
                    GUI.color = MUI.Warn;
                    r.Label(gcMessage);
                    GUI.color = Color.white;
                }
            }
            else
                Note(r, "GT_GcNone".Translate());

            MUI.Section(r, "GT_SecNow".Translate());
            r.Label("GT_Now".Translate(Runtime.Fps.ToString("0"), Runtime.FrameMs.ToString("0.0"), Runtime.Tps > 0f ? Runtime.Tps.ToString("0") : "–"));
            r.Label("GT_NowEffects".Translate(Runtime.EffectsMade, Runtime.EffectsSkipped));
            if (Runtime.Adaptive < 0.99f || Runtime.SpeedFactor < 0.99f)
                r.Label("GT_NowAuto".Translate((s.effects * Runtime.Adaptive * Runtime.SpeedFactor * 100f).ToString("0")));
            float rightH = r.CurHeight;
            r.End();

            viewHeight = Mathf.Max(leftH, rightH) + 20f;
            Widgets.EndScrollView();
        }

        private static void Plain(Listing_Standard l, string key, ref bool v)
        {
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, Settings, null);
        }

        private void SmoothTab(Rect inRect, GfxSettings s)
        {
            if (Smooth.OtherSmoothMod)
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 60f), "GT_SmoothOther".Translate());
                GUI.color = Color.white;
                return;
            }
            Rect outRect = inRect;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(viewHeight2, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll2, view);
            float colW = (view.width - 28f) / 2f;

            var l = new Listing_Standard();
            l.Begin(new Rect(0f, 0f, colW, 100000f));
            MUI.Section(l, "GT_SecHighSpeed".Translate());
            Plain(l, "GT_Budget", ref s.sfBudget);
            if (TickLoop.OtherTickMod != null)
            {
                GUI.color = MUI.Warn;
                Text.Font = GameFont.Tiny;
                l.Label("GT_BudgetOther".Translate(TickLoop.OtherTickMod));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            else if (TickLoop.StuckFallback)
            {
                GUI.color = MUI.Warn;
                Text.Font = GameFont.Tiny;
                l.Label("GT_BudgetStuck".Translate());
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            Dropdown(l, "GT_Target".Translate(), "GT_Target_Desc".Translate(), s.sfTarget.ToString(), () =>
                TargetChoices.Select(v => new FloatMenuOption(v.ToString(), () => s.sfTarget = v)).ToList());
            Plain(l, "GT_SmartTarget", ref s.sfAutoTarget);
            if (SmartTarget.Lowered)
                Note(l, "GT_SmartLowered".Translate(SmartTarget.Value, s.sfTarget));
            string[] slowNames = { "GT_SlowKeep".Translate(), "GT_SlowPicture".Translate() };
            Dropdown(l, "GT_Slow".Translate(), "GT_Slow_Desc".Translate(), slowNames[s.sfSlowdown ? 1 : 0], () => new List<FloatMenuOption>
            {
                new FloatMenuOption(slowNames[1], () => s.sfSlowdown = true),
                new FloatMenuOption(slowNames[0], () => s.sfSlowdown = false)
            });
            Plain(l, "GT_RealNormal", ref s.sfRealNormal);
            Plain(l, "GT_FastShare", ref s.sfFastShare);
            float ms = MUI.Slider(l, "GT_MaxSim".Translate(s.sfMaxSim == 0 ? "GT_Auto".Translate().ToString() : s.sfMaxSim + " ms"), s.sfMaxSim, 0f, 40f, "GT_MaxSim_Desc".Translate(), s, null);
            s.sfMaxSim = Mathf.RoundToInt(ms);
            s.sfMove = Mathf.RoundToInt(MUI.Slider(l, "GT_Move".Translate(s.sfMove), s.sfMove, 30f, 165f, "GT_Move_Desc".Translate(), s, null) / 5f) * 5;
            Plain(l, "GT_Idle", ref s.sfIdle);
            if (s.sfIdle)
                s.sfIdleFps = Mathf.RoundToInt(MUI.Slider(l, "GT_IdleFps".Translate(s.sfIdleFps), s.sfIdleFps, 15f, 60f, "GT_Idle_Desc".Translate(), s, null) / 5f) * 5;
            Plain(l, "GT_Background", ref s.sfBackground);
            Plain(l, "GT_Camera", ref s.sfCamera);
            Plain(l, "GT_SaveSmart", ref s.saveSmart);
            if (s.saveSmart)
                s.saveMaxWaitMin = Mathf.RoundToInt(MUI.Slider(l, "GT_SaveWait".Translate(s.saveMaxWaitMin), s.saveMaxWaitMin, 1f, 10f, "GT_SaveSmart_Desc".Translate(), s, null));

            // 1.2.1: every speed can have its own target and simulation limit
            MUI.Section(l, "GT_SecBySpeed".Translate());
            Note(l, "GT_BySpeed_Desc".Translate());
            for (int i = 0; i < 4; i++)
            {
                int idx = i;
                string sp = (i + 1).ToString();
                int tv = s.sfTargetBySpeed[idx];
                Dropdown(l, "GT_SpeedTarget".Translate(sp), "GT_SpeedTarget_Desc".Translate(), tv <= 0 ? "GT_Same".Translate().ToString() : tv.ToString(), () =>
                {
                    var opts = new List<FloatMenuOption> { new FloatMenuOption("GT_Same".Translate(), () => s.sfTargetBySpeed[idx] = 0) };
                    opts.AddRange(TargetChoices.Select(v => new FloatMenuOption(v.ToString(), () => s.sfTargetBySpeed[idx] = v)));
                    return opts;
                });
                int mv = s.sfMaxSimBySpeed[idx];
                string ml = mv < 0 ? "GT_Same".Translate().ToString() : mv == 0 ? "GT_Auto".Translate().ToString() : mv + " ms";
                float nv = MUI.Slider(l, "GT_SpeedMaxSim".Translate(sp, ml), mv, -1f, 40f, "GT_SpeedMaxSim_Desc".Translate(), s, null);
                s.sfMaxSimBySpeed[idx] = Mathf.RoundToInt(nv);
            }

            // 1.2: 4× and faster
            MUI.Section(l, "GT_SecFast".Translate());
            Plain(l, "GT_FastMode", ref s.fastMode);
            if (s.fastMode)
            {
                Plain(l, "GT_FastWeather", ref s.fastWeather);
                Plain(l, "GT_FastRegen", ref s.fastRegen);
            }
            Note(l, Runtime.FastNow(s) ? "GT_FastNowOn".Translate().ToString() : "GT_FastNowOff".Translate().ToString());

            MUI.Section(l, "GT_SecHidden".Translate());
            s.regenCap = Mathf.RoundToInt(MUI.Slider(l, "GT_Regen".Translate(s.regenCap == 0 ? "GT_Off".Translate().ToString() : s.regenCap.ToString()), s.regenCap, 0f, 16f, "GT_Regen_Desc".Translate(), s, null));
            Plain(l, "GT_SlowAlerts", ref s.slowAlerts);
            float leftH = l.CurHeight;
            l.End();

            var r = new Listing_Standard();
            r.Begin(new Rect(colW + 28f, 0f, colW, 100000f));
            MUI.Section(r, "GT_SecDiag".Translate());
            MUI.Check(r, "GT_OverlayOn".Translate(), "GT_OverlayOn_Desc".Translate(), ref s.overlay, s, "overlay");
            Plain(r, "GT_OverlayBar", ref s.overlayBar);

            // 1.2: what slows the game
            Rect prow = r.GetRect(32f);
            Widgets.Label(new Rect(prow.x, prow.y + 5f, prow.width - 130f, 26f), "GT_Prof".Translate());
            TooltipHandler.TipRegion(prow, "GT_Prof_Desc".Translate());
            Rect pbtn = new Rect(prow.xMax - 120f, prow.y + 2f, 120f, 28f);
            if (Profiler.Running)
                Widgets.Label(pbtn, "GT_ProfRunning".Translate(Profiler.Remaining.ToString("0")));
            else if (!Profiler.CanStart)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(pbtn.x - 60f, pbtn.y + 3f, 180f, 26f), Profiler.InGame ? "GT_ProfBusy".Translate() : "GT_BenchNoGame".Translate());
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(pbtn, "GT_BenchStart".Translate()))
                Profiler.Start();
            Note(r, "GT_Prof_Note".Translate());
            // 1.4.0: what is expensive on the map
            if (Current.ProgramState == ProgramState.Playing && Find.CurrentMap != null)
            {
                if (r.ButtonText("GT_MC_Open".Translate()))
                    Find.WindowStack.Add(new Dialog_MapCost());
                TooltipHandler.TipRegion(new Rect(0f, r.CurHeight - 30f, colW, 30f), "GT_MC_Open_Desc".Translate());
            }
            if (Profiler.Last != null && r.ButtonText("GT_ProfShow".Translate()))
                Find.WindowStack.Add(new Dialog_SlowReport());
            Rect rrow = r.GetRect(30f);
            float hw = (rrow.width - 8f) / 2f;
            if (Widgets.ButtonText(new Rect(rrow.x, rrow.y, hw, 28f), "GT_ReportCopy".Translate())) Report.Copy();
            if (Widgets.ButtonText(new Rect(rrow.x + hw + 8f, rrow.y, hw, 28f), "GT_ReportSave".Translate())) Report.Save();
            TooltipHandler.TipRegion(rrow, "GT_Report_Desc".Translate());
            r.Gap(8f);

            // benchmark
            Rect row = r.GetRect(32f);
            Widgets.Label(new Rect(row.x, row.y + 5f, row.width - 130f, 26f), "GT_Bench".Translate());
            TooltipHandler.TipRegion(row, "GT_Bench_Desc".Translate());
            Rect btn = new Rect(row.xMax - 120f, row.y + 2f, 120f, 28f);
            if (Benchmark.Running)
                Widgets.Label(btn, "GT_BenchRunning".Translate(Benchmark.Remaining.ToString("0")));
            else if (!Benchmark.CanStart)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(btn.x - 60f, btn.y + 3f, 180f, 26f), "GT_BenchNoGame".Translate());
                GUI.color = Color.white;
            }
            else if (Widgets.ButtonText(btn, "GT_BenchStart".Translate()))
                Benchmark.Start();
            if (!Benchmark.LastResult.NullOrEmpty())
                Note(r, Benchmark.LastResult);
            else
                Note(r, "GT_Bench_Note".Translate());

            Plain(r, "GT_Hitch", ref s.hitchFinder);
            if (s.hitchFinder && !HitchFinder.IsOn)
                StartHitchNow();
            if (s.hitchFinder)
            {
                Plain(r, "GT_HitchThings", ref s.hitchThings);
                Plain(r, "GT_HitchDeep", ref s.hitchDeep);
                s.hitchMs = Mathf.RoundToInt(MUI.Slider(r, "GT_HitchMs".Translate(s.hitchMs), s.hitchMs, 100f, 2000f, "GT_Hitch_Desc".Translate(), s, null) / 50f) * 50;
            }
            Plain(r, "GT_GcHold", ref s.gcHold);

            MUI.Section(r, "GT_SecNow".Translate());
            if (TickLoop.Running)
                r.Label("GT_NowSmooth".Translate(TickLoop.fps.ToString("0"), Runtime.Tps > 0f ? Runtime.Tps.ToString("0") : "–", TickLoop.simMs.ToString("0.0"), TickLoop.renderMs.ToString("0.0"), TickLoop.budgetMs.ToString("0.0")));
            else
                Note(r, "GT_NowSmoothOff".Translate());
            if (MapRegenCap.Delayed > 0)
                Note(r, "GT_NowRegen".Translate(MapRegenCap.Delayed));
            float rightH = r.CurHeight;
            r.End();
            viewHeight2 = Mathf.Max(leftH, rightH) + 20f;
            Widgets.EndScrollView();
        }

        // 1.2: memory & freezes (the former Fewer Freezes)
        // 1.6.0: game size and simulation
        private static readonly string[] SizeKeys = { "GT_SizeAuto", "GT_SizeSmall", "GT_SizeNormal", "GT_SizeHuge" };

        private void SimTab(Rect inRect, GfxSettings s)
        {
            Muted(new Rect(inRect.x, inRect.y, inRect.width, 20f), "GT_SizeHeader".Translate());
            float cw = (inRect.width - 36f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                int val = i - 1;
                Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 84f);
                string desc = (SizeKeys[i] + "_Card").Translate();
                if (val == -1)
                    desc = "GT_SizeAuto_Card".Translate(("GT_Size" + Sim.Detected).Translate());
                if (MUI.ProfileCard(c, SizeKeys[i].Translate(), desc, s.gameSize == val, val == -1))
                    s.gameSize = val;
            }
            var l = new Listing_Standard();
            l.Begin(new Rect(inRect.x, inRect.y + 116f, inRect.width, inRect.height - 116f));
            l.ColumnWidth = (inRect.width - 34f) / 2f;
            MUI.Section(l, "GT_SimTickSec".Translate());
            MUI.Check(l, "GT_SimSmartTick".Translate(), "GT_SimSmartTick_Desc".Translate(), ref s.smartTick, s, null);
            Muted(l.GetRect(48f), "GT_SimRates".Translate(Sim.WorldRate, Sim.OtherMapRate, Sim.OffscreenRate));
            MUI.Check(l, "GT_FastCull".Translate(), "GT_FastCull_Desc".Translate(), ref s.fastCull, s, null);
            l.Gap(6f);
            if (l.ButtonText("GT_TC_Open".Translate()))
                Find.WindowStack.Add(new Dialog_TickCost());
            if (!HitchFinder.IsOn)
                Muted(l.GetRect(40f), "GT_TC_NeedHitch".Translate());
            l.NewColumn();
            MUI.Section(l, "GT_SimCleanSec".Translate());
            MUI.Check(l, "GT_SimClean".Translate(), "GT_SimClean_Desc".Translate(), ref s.worldClean, s, null);
            Muted(l.GetRect(40f), Sim.KeepBattles > 0 ? "GT_SimKeep".Translate(Sim.KeepBattles) : "GT_SimKeepOff".Translate());
            if (Verse.Current.ProgramState == ProgramState.Playing)
            {
                Muted(l.GetRect(44f), Sim.WorldLine());
                if (l.ButtonText("GT_SimCleanNow".Translate()))
                {
                    string t = "";
                    LongEventHandler.QueueLongEvent(() => { t = Sim.CleanWorld(true); }, "GT_SimCleaning", false, null);
                    LongEventHandler.ExecuteWhenFinished(() => Messages.Message(t, MessageTypeDefOf.NeutralEvent, false));
                }
            }
            l.End();
        }

        private void MemoryTab(Rect inRect, GfxSettings s)
        {
            if (Freezer.OtherMod)
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 60f), "GT_MemOther".Translate());
                GUI.color = Color.white;
                return;
            }
            float colW = (inRect.width - 28f) / 2f;
            var l = new Listing_Standard();
            l.Begin(new Rect(inRect.x, inRect.y, colW, inRect.height));
            MUI.Section(l, "GT_SecMem".Translate());
            Note(l, (Freezer.Net ? "GT_MemMode17" : "GT_MemMode16").Translate());
            Plain(l, "GT_MemAuto", ref s.memAuto);
            if (!s.memAuto)
            {
                if (!Freezer.Net)
                    s.memSliceMs = Mathf.Round(MUI.Slider(l, "GT_MemSlice".Translate(s.memSliceMs.ToString("0")), s.memSliceMs, 1f, 20f, "GT_MemSlice_Desc".Translate(), s, null));
                s.memThresholdMb = Mathf.RoundToInt(MUI.Slider(l, "GT_MemThreshold".Translate(s.memThresholdMb), s.memThresholdMb, 100f, 2000f, "GT_MemThreshold_Desc".Translate(), s, null) / 50f) * 50;
            }
            Plain(l, "GT_MemOnPause", ref s.memOnPause);
            Plain(l, "GT_MemOnSave", ref s.memOnSave);
            if (Freezer.Net)
            {
                Plain(l, "GT_MemLowLatency", ref s.memLowLatency);
                Plain(l, "GT_MemCompact", ref s.memCompact);
            }
            if (!Freezer.Net) Plain(l, "GT_MemUnload", ref s.memUnload);
            Plain(l, "GT_MemRamWarn", ref s.memRamWarn);
            if (Vram.Available) Plain(l, "GT_VramWarnSet", ref s.vramWarn);
            // 1.3.3: oversized mod textures
            string capText = s.texCap <= 0 ? "GT_TexCapOff".Translate().ToString() : s.texCap + " px";
            if (l.ButtonTextLabeled("GT_TexCap".Translate(), capText))
            {
                int[] steps = { 0, 4096, 2048, 1024 };
                int i = Array.IndexOf(steps, s.texCap);
                s.texCap = steps[(i + 1) % steps.Length];
            }
            Note(l, "GT_TexCap_Desc".Translate(TextureCap.Count, TextureCap.SavedMb));
            l.End();

            var o = new Listing_Standard();
            o.Begin(new Rect(inRect.x + colW + 28f, inRect.y, colW, inRect.height));
            MUI.Section(o, "GT_SecMemStats".Translate());
            o.Label(Freezer.Net ? "GT_MemNow17".Translate(Freezer.UsedMb).ToString() : "GT_MemNow".Translate(Freezer.UsedMb, Freezer.HeapMb, Freezer.CurrentSliceMs.ToString("0.#")).ToString());
            string ram = Freezer.RamLine();
            if (ram != null)
                o.Label(ram);
            o.Gap(4f);
            o.Label(Freezer.HitchLine());
            GUI.color = MUI.Muted;
            o.Label(Freezer.CleanLine());
            GUI.color = Color.white;
            // 1.3.0: video memory
            string vl = Vram.Line();
            if (vl != null)
            {
                o.Gap(4f);
                o.Label(vl);
                if (o.ButtonText("GT_VramWho".Translate()))
                    Find.WindowStack.Add(new Dialog_MessageBox(Vram.Report(), title: "GT_VramTitle".Translate()));
            }
            o.Gap(6f);
            if (o.ButtonText("GT_MemCollectNow".Translate()))
                Freezer.Collect(true);
            o.Gap(6f);
            Plain(o, "GT_MemHud", ref s.memHud);
            o.Gap(6f);
            Note(o, "GT_MemNote".Translate());
            o.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            try { Runtime.Apply(Settings); } catch { }
        }
    }
}
