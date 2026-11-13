// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakPickers
{
    // 0.13.0: every window of Mišák Tabs can be resized by dragging its corner, remembers its size
    // (also after a restart) and always fits on the screen.
    [StaticConstructorOnStartup]
    public static class WindowSizes
    {
        public static Dictionary<string, string> saved = new Dictionary<string, string>();
        private static readonly System.Reflection.FieldInfo resizerF = AccessTools.Field(typeof(Window), "resizer");
        private const float Margin = 20f;

        static WindowSizes()
        {
            try
            {
                var h = new Harmony("misak.pickers.windowsizes");
                h.Patch(AccessTools.Method(typeof(Window), "SetInitialSizeAndPosition"),
                    postfix: new HarmonyMethod(typeof(WindowSizes), nameof(SizePostfix)) { priority = Priority.Last });
                h.Patch(AccessTools.Method(typeof(Window), nameof(Window.PreClose)),
                    postfix: new HarmonyMethod(typeof(WindowSizes), nameof(ClosePostfix)));
                h.Patch(AccessTools.Method(typeof(Window), nameof(Window.PostOpen)),
                    postfix: new HarmonyMethod(typeof(WindowSizes), nameof(OpenPostfix)));
            }
            catch (Exception ex) { Log.Warning("[Mišák Tabs] window sizes: " + ex.Message); }
        }

        // our own windows (Dialog_… in this mod) and the trade window while ours draws it
        public static bool Ours(Window w)
        {
            if (w == null) return false;
            Type t = w.GetType();
            if (t.Assembly == typeof(WindowSizes).Assembly && t.Name.StartsWith("Dialog_")) return true;
            return w is Dialog_Trade && MTMod.S != null && MTMod.S.tradeWindow;
        }

        private static string Key(Window w) => w.GetType().Name;

        public static void OpenPostfix(Window __instance)
        {
            try
            {
                if (!Ours(__instance)) return;
                __instance.resizeable = true;
                if (resizerF != null && resizerF.GetValue(__instance) == null)
                    resizerF.SetValue(__instance, new WindowResizer { minWindowSize = new Vector2(Mathf.Min(480f, UI.screenWidth - Margin), Mathf.Min(340f, UI.screenHeight - Margin)) });
            }
            catch { }
        }

        public static void SizePostfix(Window __instance)
        {
            try
            {
                if (!Ours(__instance)) return;
                Rect r = __instance.windowRect;
                string v;
                if (saved.TryGetValue(Key(__instance), out v))
                {
                    string[] p = v.Split(';');
                    float w, hgt;
                    if (p.Length == 2 && float.TryParse(p[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out w)
                        && float.TryParse(p[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out hgt))
                    {
                        r.width = w;
                        r.height = hgt;
                    }
                }
                // always fits on the screen, centred
                r.width = Mathf.Min(r.width, UI.screenWidth - Margin);
                r.height = Mathf.Min(r.height, UI.screenHeight - Margin);
                r.x = (UI.screenWidth - r.width) / 2f;
                r.y = (UI.screenHeight - r.height) / 2f;
                __instance.windowRect = r.Rounded();
            }
            catch { }
        }

        public static void ClosePostfix(Window __instance)
        {
            try
            {
                if (!Ours(__instance)) return;
                Rect r = __instance.windowRect;
                saved[Key(__instance)] = r.width.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + ";" + r.height.ToString("0", System.Globalization.CultureInfo.InvariantCulture);
                MTMod.S?.Write();
            }
            catch { }
        }

        public static void Expose()
        {
            Scribe_Collections.Look(ref saved, "windowSizes", LookMode.Value, LookMode.Value);
            if (saved == null) saved = new Dictionary<string, string>();
        }

        public static void Reset() => saved.Clear();
    }
}
