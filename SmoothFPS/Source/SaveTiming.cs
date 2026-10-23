using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.7.4: smart autosave timing. Saving a big colony stops the game for seconds and cannot run
    // in the background (the whole game state is written, it must not change meanwhile). So the
    // autosave waits for a moment when the stop does not hurt: the game is paused, a window or a
    // main tab (Work, Health …) is open, or nothing was touched for a few seconds. At the latest
    // after the set wait it saves anyway. Works for the game's autosave and for mods that call it
    // (Real Time Autosave …).
    public static class SaveTiming
    {
        private static bool pending;
        private static float pendingSince;
        private static bool bypass;
        private static float lastInput = -100f;
        private static int deferred, savedQuiet, savedLate;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(Autosaver), nameof(Autosaver.DoAutosave));
                if (m == null)
                    return;
                h.Patch(m, prefix: new HarmonyMethod(typeof(SaveTiming), nameof(Prefix)));
            }
            catch (Exception e)
            {
                MLog.Warn("[Smooth FPS] save timing skipped: " + e.Message);
            }
        }

        public static bool Prefix()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (bypass || s == null || !s.saveSmart || Current.Game == null)
                return true;
            if (GoodMoment(s))
            {
                pending = false;
                return true;
            }
            if (!pending)
            {
                pending = true;
                pendingSince = Time.realtimeSinceStartup;
                deferred++;
            }
            return false;
        }

        // every frame (Root.Update)
        public static void Update()
        {
            try
            {
                if (Input.anyKey || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2) || Input.mouseScrollDelta.y != 0f)
                    lastInput = Time.realtimeSinceStartup;
                if (!pending)
                    return;
                GfxSettings s = GraphicsTunerMod.Settings;
                if (s == null || Current.Game == null || Current.ProgramState != ProgramState.Playing)
                {
                    pending = false;
                    return;
                }
                if (LongEventHandler.AnyEventNowOrWaiting)
                    return;
                bool late = Time.realtimeSinceStartup - pendingSince >= Math.Max(1, s.saveMaxWaitMin) * 60f;
                if (!late && !GoodMoment(s))
                    return;
                pending = false;
                if (late) savedLate++; else savedQuiet++;
                LongEventHandler.QueueLongEvent(() =>
                {
                    bypass = true;
                    try { Find.Autosaver.DoAutosave(); }
                    finally { bypass = false; }
                }, "Autosaving", false, null, true);
                if (savedQuiet + savedLate <= 3)
                    MLog.Info("[Smooth FPS] autosave done " + (late ? "after the longest wait" : "at a quiet moment") + " (waited " + (Time.realtimeSinceStartup - pendingSince).ToString("0") + " s).");
            }
            catch (Exception e)
            {
                pending = false;
                MLog.WarnOnce("[Smooth FPS] save timing: " + e.Message, 0x5a7e01);
            }
        }

        private static bool GoodMoment(GfxSettings s)
        {
            try
            {
                if (Find.TickManager != null && Find.TickManager.Paused)
                    return true;
                if (Find.WindowStack != null && Find.WindowStack.WindowsForcePause)
                    return true;
                if (Find.MainTabsRoot?.OpenTab != null)
                    return true;
                return Time.realtimeSinceStartup - lastInput >= 4f;
            }
            catch
            {
                return true;
            }
        }

        public static string Status => pending ? "waiting " + (Time.realtimeSinceStartup - pendingSince).ToString("0") + " s" : "-";
    }
}
