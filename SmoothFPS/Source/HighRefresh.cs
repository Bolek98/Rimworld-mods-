using System;
using System.Reflection;
using UnityEngine;
using RimWorld;
using Verse;

namespace GraphicsTuner
{
    // 1.6.3: high refresh monitors. On RimWorld 1.7 with a 144–240 Hz monitor and the game's own FPS limit
    // ("like the game" = the monitor's rate), dragging with the mouse stutters and the loading screen can
    // freeze; a 60 FPS cap fixes both. While the game loads (or does any long event) and while a mouse
    // button is held, the frame rate is held at 60; afterwards the game's own value comes back. Only when
    // the FPS limit is "like the game" – a limit you chose yourself is never touched.
    public static class HighRefresh
    {
        private const int Cap = 60;
        private static int hz = -1;
        private static bool active;
        private static int saved;

        public static int Hz
        {
            get
            {
                if (hz < 0)
                    hz = Detect();
                return hz;
            }
        }

        private static int Detect()
        {
            try
            {
                Resolution r = Screen.currentResolution;
                object boxed = r;
                // Unity 2022.2+: refreshRateRatio (a RefreshRate with a double "value"); older: refreshRate (int)
                PropertyInfo ratio = typeof(Resolution).GetProperty("refreshRateRatio");
                if (ratio != null)
                {
                    object rr = ratio.GetValue(boxed, null);
                    PropertyInfo val = rr?.GetType().GetProperty("value");
                    if (val != null)
                    {
                        double d = Convert.ToDouble(val.GetValue(rr, null));
                        if (d > 1.0)
                            return (int)Math.Round(d);
                    }
                }
                PropertyInfo old = typeof(Resolution).GetProperty("refreshRate");
                if (old != null)
                    return Convert.ToInt32(old.GetValue(boxed, null));
            }
            catch { }
            return 0;
        }

        public static void Update(GfxSettings s)
        {
            if (s == null || !s.highRefreshCap || s.fpsLimit != -2 || Hz <= 120 || !Application.isFocused)
            {
                Release();
                return;
            }
            bool need = LongEventHandler.AnyEventNowOrWaiting
                || Input.GetMouseButton(0) || Input.GetMouseButton(1) || Input.GetMouseButton(2);
            if (need && !active)
            {
                saved = Application.targetFrameRate;
                if (saved <= 0 || saved > Cap)
                {
                    Application.targetFrameRate = Cap;
                    active = true;
                    Tell(s);
                }
            }
            else if (!need)
                Release();
        }

        private static void Release()
        {
            if (!active)
                return;
            active = false;
            if (Application.targetFrameRate == Cap)
                Application.targetFrameRate = saved;
        }

        private static void Tell(GfxSettings s)
        {
            if (s.highRefreshTold || Current.ProgramState != ProgramState.Playing)
                return;
            s.highRefreshTold = true;
            try { s.Write(); } catch { }
            Messages.Message("GT_HighRefreshMsg".Translate(Hz), MessageTypeDefOf.NeutralEvent, false);
            MLog.Info("[Smooth FPS] high refresh monitor (" + Hz + " Hz): 60 FPS while loading and dragging.");
        }
    }
}
