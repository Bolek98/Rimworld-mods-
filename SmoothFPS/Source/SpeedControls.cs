using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.5.3: speed controls.
    //  * speed 4 without dev mode – the game itself has speed 4 (TimeSpeed.Ultrafast, ×15), only the
    //    button and the key are hidden outside dev mode. With the setting on, the 4 vanilla buttons move
    //    one button to the left and a 4th speed button is drawn after them; the Ultrafast key and
    //    "faster" from speed 3 also work.
    //  * speed during danger – while TimeSlower.ForcedNormalSpeed is on (raids, fights …) the game
    //    runs at ×1 whatever you chose. Optionally speed 2 (×3) or speed 3 (×6) are kept, never more.
    //    Only TickManager.TickRateMultiplier is changed, the slower itself works as before.
    //  * hidden resource list – ResourceReadout.ResourceReadoutOnGUI is skipped unless the mouse is
    //    over its area.
    // Speed mods that do the same (Smart Speed) win – then the speed parts stay off.
    // ---------------------------------------------------------------------------
    public static class SpeedControls
    {
        public static bool OtherSpeedMod;
        private const float But = 32f;

        private static GfxSettings S => GraphicsTunerMod.Settings;

        public static void Patch(Harmony h)
        {
            try { OtherSpeedMod = ModsConfig.IsActive("sarg.smartspeed"); } catch { OtherSpeedMod = false; }
            if (!OtherSpeedMod)
            {
                try
                {
                    h.Patch(AccessTools.Method(typeof(TimeControls), nameof(TimeControls.DoTimeControlsGUI)),
                        prefix: new HarmonyMethod(typeof(SpeedControls), nameof(ControlsPre)),
                        postfix: new HarmonyMethod(typeof(SpeedControls), nameof(ControlsPost)));
                }
                catch (Exception ex) { MLog.Warn("[Smooth FPS] speed 4 button not available: " + ex.Message); }
                try
                {
                    h.Patch(AccessTools.PropertyGetter(typeof(TickManager), nameof(TickManager.TickRateMultiplier)),
                        postfix: new HarmonyMethod(typeof(SpeedControls), nameof(RatePost)));
                }
                catch (Exception ex) { MLog.Warn("[Smooth FPS] speed during danger not available: " + ex.Message); }
            }
            try
            {
                h.Patch(AccessTools.Method(typeof(ResourceReadout), nameof(ResourceReadout.ResourceReadoutOnGUI)),
                    prefix: new HarmonyMethod(typeof(SpeedControls), nameof(ReadoutPre)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] hidden resource list not available: " + ex.Message); }
        }

        private static bool UltraOn => S != null && S.ultraSpeed && !Prefs.DevMode;   // dev mode: the game's own key works

        // ---------------- speed 4 button ----------------
        public static void ControlsPre(ref Rect timerRect)
        {
            if (UltraOn) timerRect.x -= But;   // room for the 4th speed button on the right
        }

        public static void ControlsPost(Rect timerRect)
        {
            if (!UltraOn) return;
            try
            {
                TickManager tm = Find.TickManager;
                if (tm == null) return;
                Rect b = new Rect(timerRect.x + But * 4f, timerRect.y, But, TimeControls.TimeButSize.y);
                if (Widgets.ButtonImage(b, TexButton.SpeedButtonTextures[(int)TimeSpeed.Ultrafast], true, "GT_UltraButton".Translate()) && !tm.ForcePaused)
                    SetUltra(tm);
                // a small "4" so it is not mistaken for speed 3
                Text.Font = GameFont.Tiny;
                Text.Anchor = TextAnchor.LowerRight;
                GUI.color = new Color(1f, 1f, 1f, 0.85f);
                Widgets.Label(new Rect(b.x, b.y, b.width - 1f, b.height + 2f), "4");
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                if (!tm.ForcePaused && tm.CurTimeSpeed == TimeSpeed.Ultrafast)
                    GUI.DrawTexture(b, TexUI.HighlightTex);
                if (tm.slower.ForcedNormalSpeed && S.dangerSpeed == 0)
                    Widgets.DrawLineHorizontal(b.x, b.y + b.height / 2f, b.width);
                GenUI.AbsorbClicksInRect(b);

                if (Event.current.type == EventType.KeyDown && !Find.WindowStack.WindowsForcePause)
                {
                    if (KeyBindingDefOf.TimeSpeed_Ultrafast.KeyDownEvent
                        || (KeyBindingDefOf.TimeSpeed_Faster.KeyDownEvent && tm.CurTimeSpeed == TimeSpeed.Superfast))
                    {
                        SetUltra(tm);
                        Event.current.Use();
                    }
                }
            }
            catch (Exception ex)
            {
                Text.Anchor = TextAnchor.UpperLeft;
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                MLog.WarnOnce("[Smooth FPS] speed 4 button: " + ex.Message, 0x5f4d21);
            }
        }

        private static void SetUltra(TickManager tm)
        {
            tm.CurTimeSpeed = TimeSpeed.Ultrafast;
            if (tm.CurTimeSpeed == TimeSpeed.Ultrafast)
                SoundDefOf.Clock_Superfast.PlayOneShotOnCamera();
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.TimeControls, KnowledgeAmount.SpecificInteraction);
        }

        // ---------------- speed during danger ----------------
        public static void RatePost(TickManager __instance, ref float __result)
        {
            // the game returns exactly 1 while it forces normal speed – cheap checks first
            if (__result != 1f) return;
            GfxSettings s = S;
            if (s == null || s.dangerSpeed <= 0) return;
            TimeSpeed cs = __instance.CurTimeSpeed;
            if (cs <= TimeSpeed.Normal) return;
            try
            {
                if (!__instance.slower.ForcedNormalSpeed) return;
                __result = cs == TimeSpeed.Fast || s.dangerSpeed == 1 ? 3f : 6f;
            }
            catch { }
        }

        // ---------------- hidden resource list ----------------
        public static bool ReadoutPre()
        {
            GfxSettings s = S;
            if (s == null || !s.hideResources) return true;
            try
            {
                Vector2 m = UI.MousePositionOnUIInverted;
                return m.x <= 140f && m.y <= UI.screenHeight - 180f;
            }
            catch
            {
                return true;
            }
        }
    }
}
