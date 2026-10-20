using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Scripting;
using Verse;

namespace MemoryTuner
{
    // 1.1.0 "Fewer Freezes – Smart Memory Cleanup" (was Memory Tuner).
    // Class and folder names stay, so settings and saves carry over.
    public sealed class MemoryTunerSettings : ModSettings
    {
        public bool auto = true;            // decides everything by itself
        public float sliceMs = 8f;          // used when auto is off
        public bool collectOnPause = true;  // big clean-up while the game is paused
        public int pauseThresholdMb = 400;  // used when auto is off
        public bool showHud = false;
        // 1.1.0
        public bool collectOnSave = true;   // big clean-up right after saving (the game stops then anyway)
        public bool unloadAssets = false;   // free unused textures and sounds while paused
        public bool ramWarning = true;      // warn when the computer is running out of memory
        public bool recommended = true;     // missing key = off, so old settings stay

        public override void ExposeData()
        {
            Scribe_Values.Look(ref auto, "auto", true);
            Scribe_Values.Look(ref sliceMs, "sliceMs", 8f);
            Scribe_Values.Look(ref collectOnPause, "collectOnPause", true);
            Scribe_Values.Look(ref pauseThresholdMb, "pauseThresholdMb", 400);
            Scribe_Values.Look(ref showHud, "showHud", false);
            Scribe_Values.Look(ref collectOnSave, "collectOnSave", true);
            Scribe_Values.Look(ref unloadAssets, "unloadAssets", false);
            Scribe_Values.Look(ref ramWarning, "ramWarning", true);
            Scribe_Values.Look(ref recommended, "recommended", false);
        }
    }

    public sealed class MemoryTunerMod : Mod
    {
        public const string Version = "1.1.0";
        public static MemoryTunerSettings Settings;
        private static List<string> recBullets;

        public MemoryTunerMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MemoryTunerSettings>();
            MUI.Init(new MemoryTunerSettings(), null, "recommended", "showHud");
            if (Settings.recommended)
                MUI.ApplyAll(Settings);
            try
            {
                var h = new Harmony("bolek98.memorytuner");
                MUI.PatchWindow(h, typeof(MemoryTunerMod));
                var save = AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) });
                if (save != null)
                    h.Patch(save, postfix: new HarmonyMethod(typeof(Tuner), nameof(Tuner.AfterSave)));
            }
            catch (Exception ex)
            {
                Log.Warning("[Fewer Freezes] save patch failed: " + ex.Message);
            }
            Log.Message("[Fewer Freezes – Smart Memory Cleanup] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Fewer Freezes – Smart Memory Cleanup";

        private static void Check(Listing_Standard l, string key, ref bool value, string field)
        {
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref value, Settings, field);
        }

        public override void DoSettingsWindowContents(Rect r)
        {
            MemoryTunerSettings s = Settings;
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
            {
                recBullets = new List<string>();
                for (int i = 1; i <= 4; i++)
                    recBullets.Add(("MT_Rec" + i).Translate());
            }
            float y0 = r.y;
            y0 += MUI.RecommendedCard(new Rect(r.x, y0, r.width, 0f), s, s.recommended, v =>
            {
                s.recommended = v;
                if (v) MUI.ApplyAll(s);
            }, recBullets) + 6f;
            if (s.recommended)
                y0 += MUI.LockNote(new Rect(r.x, y0, r.width, 0f)) + 4f;

            float colW = (r.width - 24f) / 2f;
            Rect left = new Rect(r.x, y0, colW, r.yMax - y0);
            Rect right = new Rect(left.xMax + 24f, y0, colW, r.yMax - y0);

            // ---- left: how memory is cleaned (locked by the recommended switch)
            var l = new Listing_Standard();
            Rect lockRect = new Rect(left.x, left.y, left.width, 0f);
            MUI.BeginLock(s.recommended);
            try
            {
                l.Begin(left);
                MUI.Section(l, "MT_SecClean".Translate());
                Check(l, "MT_Auto", ref s.auto, "auto");
                if (!s.auto)
                {
                    s.sliceMs = Mathf.Round(MUI.Slider(l, "MT_Slice".Translate(s.sliceMs.ToString("0")), s.sliceMs, 1f, 20f, "MT_Slice_Desc".Translate(), s, "sliceMs"));
                    s.pauseThresholdMb = Mathf.RoundToInt(MUI.Slider(l, "MT_Threshold".Translate(s.pauseThresholdMb), s.pauseThresholdMb, 100f, 2000f, "MT_Threshold_Desc".Translate(), s, "pauseThresholdMb") / 50f) * 50;
                }
                Check(l, "MT_OnPause", ref s.collectOnPause, "collectOnPause");
                Check(l, "MT_OnSave", ref s.collectOnSave, "collectOnSave");
                Check(l, "MT_Unload", ref s.unloadAssets, "unloadAssets");
                Check(l, "MT_RamWarn", ref s.ramWarning, "ramWarning");
                lockRect.height = l.CurHeight;
                l.End();
            }
            finally
            {
                MUI.EndLock(lockRect);
            }

            // ---- right: what is happening (never locked)
            var o = new Listing_Standard();
            o.Begin(right);
            MUI.Section(o, "MT_SecStats".Translate());
            o.Label("MT_Now".Translate(Tuner.UsedMb, Tuner.HeapMb, Tuner.CurrentSliceMs.ToString("0.#")));
            string ram = Tuner.RamLine();
            if (ram != null)
                o.Label(ram);
            o.Gap(4f);
            o.Label(Tuner.HitchLine());
            GUI.color = MUI.Muted;
            o.Label(Tuner.CleanLine());
            GUI.color = Color.white;
            o.Gap(6f);
            if (o.ButtonText("MT_CollectNow".Translate()))
                Tuner.Collect(true);
            o.Gap(6f);
            MUI.Check(o, "MT_Hud".Translate(), "MT_Hud_Desc".Translate(), ref s.showHud, s, "showHud");
            o.Gap(6f);
            GUI.color = MUI.Muted;
            o.Label("MT_Note".Translate());
            GUI.color = Color.white;
            o.End();
        }
    }

    public static class Tuner
    {
        private const long MB = 1024L * 1024L;
        private const float HitchSec = 0.25f;     // a frame longer than this is a freeze
        private const float WindowSec = 3600f;    // statistics of the last hour

        private static float nextCheck;
        private static float pausedSince = -1f;
        private static float lastCollect = -9999f;
        private static float lastUnload = -9999f;
        private static long usedAfterLast;
        private static ulong defaultSlice;
        private static bool haveDefault;

        // 1.1.0: freeze meter
        private struct Hitch { public float time; public float sec; public bool gc; }
        private static readonly List<Hitch> hitches = new List<Hitch>();
        private static int lastGcCount = -1;
        private static bool ourCollect;           // the long frame was our own clean-up
        private static float lastGcStop;          // how long the last memory clean-up stopped the game
        private static int cleanPause, cleanSave, cleanManual;

        // 1.1.0: small memory graph for the HUD (one sample per second)
        private static readonly float[] graph = new float[90];
        private static int graphPos;
        private static bool warned;

        public static long UsedMb => GC.GetTotalMemory(false) / MB;
        public static long HeapMb
        {
            get
            {
                try { return Profiler.GetMonoHeapSizeLong() / MB; }
                catch { return UsedMb; }
            }
        }

        public static float CurrentSliceMs
        {
            get
            {
                try { return GarbageCollector.incrementalTimeSliceNanoseconds / 1000000f; }
                catch { return 0f; }
            }
        }

        // how much time per frame the game may spend tidying memory, by its size
        private static float AutoSliceMs(long heapMb)
        {
            if (heapMb < 1500) return 3f;
            if (heapMb < 2500) return 5f;
            if (heapMb < 3500) return 8f;
            return 12f;
        }

        private static long AutoThresholdMb(long usedMb) => Math.Max(300L, usedMb * 15 / 100);

        private static int GcCount()
        {
            try { return GC.CollectionCount(GC.MaxGeneration); }
            catch { return 0; }
        }

        // ---------------- every frame ----------------
        public static void Update()
        {
            MemoryTunerSettings s = MemoryTunerMod.Settings;
            if (s == null)
                return;
            float now = Time.realtimeSinceStartup;
            MeasureFrame(now);
            TickManager tm = Find.TickManager;
            bool paused = tm != null && tm.Paused;
            if (!paused)
                pausedSince = -1f;
            else if (pausedSince < 0f)
                pausedSince = now;
            if (now < nextCheck)
                return;
            nextCheck = now + 1f;
            try
            {
                graph[graphPos] = UsedMb;
                graphPos = (graphPos + 1) % graph.Length;
                // 1. incremental clean-up per frame
                if (GarbageCollector.isIncremental)
                {
                    if (!haveDefault)
                    {
                        defaultSlice = GarbageCollector.incrementalTimeSliceNanoseconds;
                        haveDefault = true;
                    }
                    float ms = s.auto ? AutoSliceMs(HeapMb) : s.sliceMs;
                    ulong ns = (ulong)(ms * 1000000f);
                    if (GarbageCollector.incrementalTimeSliceNanoseconds != ns)
                        GarbageCollector.incrementalTimeSliceNanoseconds = ns;
                }
                // 2. the big clean-up while the game is paused anyway
                if (s.collectOnPause && paused && pausedSince >= 0f && now - pausedSince >= 1.5f && now - lastCollect >= 90f)
                {
                    if (Grown(s))
                    {
                        Collect(false);
                        cleanPause++;
                    }
                }
                // 3. 1.1.0: free unused textures and sounds after a longer pause, at most every 10 minutes
                if (s.unloadAssets && paused && pausedSince >= 0f && now - pausedSince >= 3f && now - lastUnload >= 600f)
                {
                    lastUnload = now;
                    ourCollect = true;
                    Resources.UnloadUnusedAssets();
                }
                // 4. 1.1.0: the computer is running out of memory
                if (s.ramWarning && !warned)
                    CheckRam();
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[Fewer Freezes] " + ex.Message, 0x3e3a71);
            }
        }

        private static bool Grown(MemoryTunerSettings s)
        {
            long used = GC.GetTotalMemory(false);
            long grown = (used - usedAfterLast) / MB;
            long limit = s.auto ? AutoThresholdMb(used / MB) : s.pauseThresholdMb;
            return usedAfterLast == 0 || grown >= limit;
        }

        private static void MeasureFrame(float now)
        {
            int gc = GcCount();
            bool gcHappened = lastGcCount >= 0 && gc != lastGcCount;
            lastGcCount = gc;
            float dt = Time.unscaledDeltaTime;
            if (dt < HitchSec)
            {
                ourCollect = false;
                return;
            }
            // loading a map or a save is not a freeze
            if (LongEventHandler.AnyEventNowOrWaiting || ourCollect)
            {
                ourCollect = false;
                return;
            }
            hitches.Add(new Hitch { time = now, sec = dt, gc = gcHappened });
            if (gcHappened)
                lastGcStop = dt;
            while (hitches.Count > 0 && now - hitches[0].time > WindowSec)
                hitches.RemoveAt(0);
        }

        public static string HitchLine()
        {
            float now = Time.realtimeSinceStartup;
            int all = 0, gc = 0;
            float worst = 0f;
            foreach (Hitch h in hitches)
            {
                if (now - h.time > WindowSec)
                    continue;
                all++;
                if (h.gc) gc++;
                if (h.sec > worst) worst = h.sec;
            }
            if (all == 0)
                return "MT_HitchNone".Translate();
            return "MT_Hitches".Translate(all, gc, worst.ToString("0.0#"));
        }

        public static string CleanLine()
        {
            return "MT_Cleans".Translate(cleanPause, cleanSave, cleanManual);
        }

        // ---------------- computer memory ----------------
        private static long procMb;
        private static float procAt = -999f;

        // measured at most once a second (asking Windows is not free)
        private static long ProcessMb()
        {
            float now = Time.realtimeSinceStartup;
            if (now - procAt >= 1f)
            {
                procAt = now;
                try
                {
                    using (var p = System.Diagnostics.Process.GetCurrentProcess())
                        procMb = p.WorkingSet64 / MB;
                }
                catch { procMb = 0L; }
            }
            return procMb;
        }

        public static string RamLine()
        {
            long total = SystemInfo.systemMemorySize;
            long used = ProcessMb();
            if (total <= 0 || used <= 0)
                return null;
            return "MT_Ram".Translate(used, total, Mathf.RoundToInt(100f * used / total));
        }

        private static void CheckRam()
        {
            long total = SystemInfo.systemMemorySize;
            long used = ProcessMb();
            if (total <= 0 || used <= 0 || used < total * 80 / 100)
                return;
            warned = true;
            Find.LetterStack?.ReceiveLetter("MT_RamWarn_Title".Translate(), "MT_RamWarn_Text".Translate(used, total), LetterDefOf.NeutralEvent);
        }

        // ---------------- clean-ups ----------------
        public static void Collect(bool manual)
        {
            long before = GC.GetTotalMemory(false) / MB;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ourCollect = true;
            GC.Collect();
            sw.Stop();
            lastGcCount = GcCount();
            usedAfterLast = GC.GetTotalMemory(false);
            lastCollect = Time.realtimeSinceStartup;
            if (manual)
                cleanManual++;
            string text = "MT_Done".Translate(before, usedAfterLast / MB, sw.ElapsedMilliseconds);
            Log.Message("[Fewer Freezes] " + text);
            if (manual || sw.ElapsedMilliseconds > 500)
                Messages.Message(text, MessageTypeDefOf.SilentInput, historical: false);
        }

        // 1.1.0: right after a save (manual or autosave) – the game stops for it anyway
        public static void AfterSave()
        {
            try
            {
                MemoryTunerSettings s = MemoryTunerMod.Settings;
                if (s == null || !s.collectOnSave || Current.ProgramState != ProgramState.Playing)
                    return;
                if (Time.realtimeSinceStartup - lastCollect < 60f || !Grown(s))
                    return;
                long before = GC.GetTotalMemory(false) / MB;
                ourCollect = true;
                GC.Collect();
                lastGcCount = GcCount();
                usedAfterLast = GC.GetTotalMemory(false);
                lastCollect = Time.realtimeSinceStartup;
                cleanSave++;
                Log.Message("[Fewer Freezes] " + "MT_DoneSave".Translate(before, usedAfterLast / MB));
            }
            catch (Exception ex)
            {
                Log.WarningOnce("[Fewer Freezes] " + ex.Message, 0x3e3a72);
            }
        }

        // ---------------- small display ----------------
        public static void OnGUI()
        {
            MemoryTunerSettings s = MemoryTunerMod.Settings;
            if (s == null || !s.showHud || Find.CurrentMap == null || Event.current.type != EventType.Repaint)
                return;
            Text.Font = GameFont.Tiny;
            string t = "MT_Hud_Text".Translate(UsedMb, CurrentSliceMs.ToString("0.#"), lastGcStop > 0f ? lastGcStop.ToString("0.0#") : "–");
            Vector2 size = Text.CalcSize(t);
            float w = Mathf.Max(size.x + 8f, 140f);
            Rect r = new Rect(8f, UI.screenHeight - 90f - size.y, w, size.y + 30f);
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.5f));
            Widgets.Label(new Rect(r.x + 4f, r.y + 1f, size.x + 4f, size.y), t);
            // memory over the last minute and a half
            Rect g = new Rect(r.x + 4f, r.y + size.y + 3f, r.width - 8f, 24f);
            float max = 1f;
            foreach (float v in graph)
                if (v > max) max = v;
            float bw = g.width / graph.Length;
            for (int i = 0; i < graph.Length; i++)
            {
                float v = graph[(graphPos + i) % graph.Length];
                if (v <= 0f)
                    continue;
                float bh = g.height * v / max;
                Widgets.DrawBoxSolid(new Rect(g.x + i * bw, g.yMax - bh, Mathf.Max(1f, bw), bh), new Color(0.49f, 0.75f, 0.37f, 0.8f));
            }
            Text.Font = GameFont.Small;
        }
    }

    public sealed class MemoryTunerComponent : GameComponent
    {
        public MemoryTunerComponent(Game game)
        {
        }

        public override void GameComponentUpdate() => Tuner.Update();

        public override void GameComponentOnGUI() => Tuner.OnGUI();
    }
}
