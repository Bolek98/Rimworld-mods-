using System;
using System.Collections.Generic;
using System.Runtime;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using UnityEngine.Scripting;
using Verse;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.2: "Memory & freezes" tab – the former Fewer Freezes mod, with two modes picked by
    // the game version itself:
    //  * 1.6 and older (Mono, Unity's garbage collector): incremental clean-up per frame set by
    //    memory size, a big blocking clean-up while paused / after saving.
    //  * 1.7 and newer (.NET): the clean-up runs in the background – here a blocking full
    //    clean-up is exactly what freezes the game. So: background clean-ups while paused /
    //    after saving, "low latency" mode during play, optional compaction during long pauses.
    // Steps aside when the old Fewer Freezes mod is active.
    // ---------------------------------------------------------------------------
    public static class Freezer
    {
        private const long MB = 1024L * 1024L;
        private const float HitchSec = 0.25f;
        private const float WindowSec = 3600f;

        public static readonly bool Net = Type.GetType("Mono.Runtime") == null;
        public static bool OtherMod;
        public static bool Active => !OtherMod;

        private static float nextCheck;
        private static float pausedSince = -1f;
        private static float lastCollect = -9999f, lastUnload = -9999f, lastCompact = -9999f;
        private static long usedAfterCompact;   // 1.3.2
        private static long usedAfterLast;
        private static bool latencySet;

        private struct Hitch { public float time; public float sec; public bool gc; }
        private static readonly List<Hitch> hitches = new List<Hitch>();
        private static int lastGcCount = -1;
        private static bool ourCollect;
        private static float lastGcStop;
        private static int cleanPause, cleanSave, cleanManual;
        private static readonly float[] graph = new float[90];
        private static int graphPos;
        private static bool warned, lowRam;

        public static void Patch(Harmony h)
        {
            OtherMod = ModLister.GetActiveModWithIdentifier("bolek98.memorytuner", true) != null;
            if (OtherMod)
            {
                MLog.Warn("[Smooth FPS] Fewer Freezes is active – the Memory & freezes tab is off. Turn Fewer Freezes off, Smooth FPS contains all of it now.");
                return;
            }
            try
            {
                var save = AccessTools.Method(typeof(GameDataSaveLoader), nameof(GameDataSaveLoader.SaveGame), new[] { typeof(string) });
                if (save != null)
                    h.Patch(save, postfix: new HarmonyMethod(typeof(Freezer), nameof(AfterSave)));
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] memory: save patch failed: " + e.Message); }
        }

        public static long UsedMb => GC.GetTotalMemory(false) / MB;

        public static long HeapMb
        {
            get
            {
                try { return Net ? UsedMb : UnityEngine.Profiling.Profiler.GetMonoHeapSizeLong() / MB; }
                catch { return UsedMb; }
            }
        }

        public static float CurrentSliceMs
        {
            get
            {
                try { return GarbageCollector.isIncremental ? GarbageCollector.incrementalTimeSliceNanoseconds / 1000000f : 0f; }
                catch { return 0f; }
            }
        }

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
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !Active)
                return;
            float now = Time.realtimeSinceStartup;
            MeasureFrame(now);
            bool playing = Current.ProgramState == ProgramState.Playing && Find.TickManager != null;
            bool paused = playing && Find.TickManager.Paused;
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
                if (s.memRamWarn || Net)
                    CheckRam(s, playing);
                if (Net)
                    Update17(s, now, paused);
                else
                    Update16(s, now, paused);
                // 1.2.1: not on 1.7 – the Waylith engine can drop textures the UI still uses (blank icons)
                if (s.memUnload && !Net && paused && pausedSince >= 0f && now - pausedSince >= 3f && now - lastUnload >= 600f)
                {
                    lastUnload = now;
                    ourCollect = true;
                    Resources.UnloadUnusedAssets();
                }
            }
            catch (Exception ex)
            {
                Log.ErrorOnce("[Smooth FPS] memory: " + ex.Message, 0x6f7a31);
            }
        }

        // 1.6 and older: Unity's incremental collector
        private static void Update16(GfxSettings s, float now, bool paused)
        {
            if (GarbageCollector.isIncremental)
            {
                float ms = s.memAuto ? AutoSliceMs(HeapMb) : s.memSliceMs;
                ulong ns = (ulong)(ms * 1000000f);
                if (GarbageCollector.incrementalTimeSliceNanoseconds != ns)
                    GarbageCollector.incrementalTimeSliceNanoseconds = ns;
            }
            if (s.memOnPause && paused && pausedSince >= 0f && now - pausedSince >= 1.5f && now - lastCollect >= 90f && Grown(s))
            {
                Collect(false);
                cleanPause++;
            }
        }

        // 1.7 and newer: .NET collects in the background – never a blocking full clean-up in play
        private static void Update17(GfxSettings s, float now, bool paused)
        {
            // low latency: the runtime avoids blocking full clean-ups while you play
            bool wantLow = s.memLowLatency && !lowRam;
            try
            {
                if (wantLow && GCSettings.LatencyMode == GCLatencyMode.Interactive)
                {
                    GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
                    latencySet = true;
                }
                else if (!wantLow && latencySet && GCSettings.LatencyMode == GCLatencyMode.SustainedLowLatency && !GcHold.Held)
                {
                    GCSettings.LatencyMode = GCLatencyMode.Interactive;
                    latencySet = false;
                }
            }
            catch { }
            if (s.memOnPause && paused && pausedSince >= 0f && now - pausedSince >= 1.5f && now - lastCollect >= 90f && Grown(s))
            {
                Background();
                cleanPause++;
            }
            // compaction (packs the large objects together) – it does stop the game, so only in a long pause
            // 1.3.2: only after a longer pause (20 s) and only when the memory grew a lot since the last
            // compaction – a 9 s stop for 200 MB was not worth it
            if (s.memCompact && paused && pausedSince >= 0f && now - pausedSince >= 20f && now - lastCompact >= 1800f
                && GC.GetTotalMemory(false) - usedAfterCompact >= 1500L * MB)
            {
                lastCompact = now;
                Collect(false, true);
                usedAfterCompact = GC.GetTotalMemory(false);
            }
        }

        private static bool Grown(GfxSettings s)
        {
            long used = GC.GetTotalMemory(false);
            long grown = (used - usedAfterLast) / MB;
            long limit = s.memAuto ? AutoThresholdMb(used / MB) : s.memThresholdMb;
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
            if (LongEventHandler.AnyEventNowOrWaiting || ourCollect || Profiler.Running)
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
                return "GT_MemHitchNone".Translate();
            return "GT_MemHitches".Translate(all, gc, worst.ToString("0.0#"));
        }

        public static string CleanLine() => "GT_MemCleans".Translate(cleanPause, cleanSave, cleanManual);

        // ---------------- computer memory ----------------
        private static long procMb;
        private static float procAt = -999f;

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
            return "GT_MemRam".Translate(used, total, Mathf.RoundToInt(100f * used / total));
        }

        private static void CheckRam(GfxSettings s, bool playing)
        {
            long total = SystemInfo.systemMemorySize;
            long used = ProcessMb();
            if (total <= 0 || used <= 0)
                return;
            // low on memory: let .NET clean up as usual again
            lowRam = used >= total * 80 / 100;
            if (!lowRam || warned || !s.memRamWarn || !playing)
                return;
            warned = true;
            Find.LetterStack?.ReceiveLetter("GT_MemRamWarn_Title".Translate(), "GT_MemRamWarn_Text".Translate(used, total), LetterDefOf.NeutralEvent);
        }

        // ---------------- clean-ups ----------------
        // 1.7: a full clean-up in the background – the game keeps running
        // 1.5.0: on some .NET set-ups the "background" clean-up still stops the game (3–4 s after
        // every autosave). Measured: once it stops the game, it is only done while paused.
        private static bool bgBlocks;

        private static void Background()
        {
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, false, false);
                sw.Stop();
                HitchFinder.Add("GT_HitchOwnGc".Translate(), sw.Elapsed.TotalMilliseconds);
                if (sw.ElapsedMilliseconds > 300 && !bgBlocks)
                {
                    bgBlocks = true;
                    MLog.Info("[Smooth FPS] the background clean-up stopped the game for " + sw.ElapsedMilliseconds + " ms – from now on only while the game is paused.");
                }
                usedAfterLast = GC.GetTotalMemory(false);
                lastCollect = Time.realtimeSinceStartup;
            }
            catch { }
        }

        public static void Collect(bool manual, bool compact = false)
        {
            long before = GC.GetTotalMemory(false) / MB;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ourCollect = true;
            if (Net)
            {
                if (compact)
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true, compact);
            }
            else
                GC.Collect();
            sw.Stop();
            HitchFinder.Add("GT_HitchOwnGc".Translate(), sw.Elapsed.TotalMilliseconds);
            lastGcCount = GcCount();
            usedAfterLast = GC.GetTotalMemory(false);
            lastCollect = Time.realtimeSinceStartup;
            if (manual)
                cleanManual++;
            string text = "GT_MemDone".Translate(before, usedAfterLast / MB, sw.ElapsedMilliseconds);
            MLog.Info("[Smooth FPS] " + text);
            if (manual || sw.ElapsedMilliseconds > 500)
                Messages.Message(text, MessageTypeDefOf.SilentInput, historical: false);
        }

        // right after a save – the game stops for it anyway
        public static void AfterSave()
        {
            try
            {
                GfxSettings s = GraphicsTunerMod.Settings;
                if (s == null || !Active || !s.memOnSave || Current.ProgramState != ProgramState.Playing)
                    return;
                if (Time.realtimeSinceStartup - lastCollect < 60f || !Grown(s))
                    return;
                long before = GC.GetTotalMemory(false) / MB;
                ourCollect = true;
                if (Net)
                {
                    // 1.5.0: an autosave while you play is not followed by a clean-up that stops the game
                    if (bgBlocks && !(Find.TickManager?.Paused ?? false))
                        return;
                    Background();
                }
                else
                {
                    GC.Collect();
                    usedAfterLast = GC.GetTotalMemory(false);
                    lastCollect = Time.realtimeSinceStartup;
                }
                lastGcCount = GcCount();
                cleanSave++;
                MLog.Info("[Smooth FPS] " + "GT_MemDoneSave".Translate(before, GC.GetTotalMemory(false) / MB));
            }
            catch (Exception ex)
            {
                MLog.WarnOnce("[Smooth FPS] memory: " + ex.Message, 0x6f7a32);
            }
        }

        // ---------------- small display, bottom left ----------------
        public static void OnGUI()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !Active || !s.memHud || Find.CurrentMap == null || Event.current.type != EventType.Repaint)
                return;
            Text.Font = GameFont.Tiny;
            string t = "GT_MemHud_Text".Translate(UsedMb, lastGcStop > 0f ? lastGcStop.ToString("0.0#") : "–");
            Vector2 size = Text.CalcSize(t);
            float w = Mathf.Max(size.x + 8f, 140f);
            Rect r = new Rect(8f, UI.screenHeight - 90f - size.y, w, size.y + 30f);
            Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.5f));
            Widgets.Label(new Rect(r.x + 4f, r.y + 1f, size.x + 4f, size.y), t);
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
}
