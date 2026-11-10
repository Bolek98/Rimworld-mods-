using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GraphicsTuner
{
    // 1.7.1: the colonist bar with many colonists. For every colonist, every frame, the game works out
    // the activity icons (sleeping, idle, inspired, ideo role, faction …, each with a translated tooltip),
    // the mood threshold and whether he can break. Here the icon list and those two answers are kept for
    // a quarter of a second per colonist – only while the bar is drawn, nothing in the game itself changes.
    // Clicks, dragging, selection boxes, mood bar height and portraits are still drawn live every frame.
    // When another mod patches the icon drawing, the icon cache stays off (their icons always show).
    public static class BarCache
    {
        private const float Keep = 0.25f;   // real seconds

        private static bool inBar;
        private static FieldInfo tmpField;
        private static FieldInfo fTex, fTip, fCol;
        private static float areaWidth = 74f, maxSize = 20f;
        private static bool iconsOk;
        private static MethodInfo drawIconsM;
        private static string ownId;
        private static bool othersChecked;

        private struct Icon { public Texture2D tex; public string tip; public Color? col; }
        private sealed class Entry { public float at = -99f; public List<Icon> icons = new List<Icon>(); public List<object> raw = new List<object>(); }
        private static readonly Dictionary<int, Entry> icons = new Dictionary<int, Entry>();
        private static readonly Dictionary<int, (float at, MoodThreshold v)> mood = new Dictionary<int, (float, MoodThreshold)>();
        private static readonly Dictionary<int, (float at, bool v)> canBreak = new Dictionary<int, (float, bool)>();
        private static readonly AccessTools.FieldRef<MentalBreaker, Pawn> breakerPawn = AccessTools.FieldRefAccess<MentalBreaker, Pawn>("pawn");

        private static bool On => GraphicsTunerMod.Settings?.barCache ?? false;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ColonistBar), nameof(ColonistBar.ColonistBarOnGUI)),
                    prefix: new HarmonyMethod(typeof(BarCache), nameof(BarPre)), finalizer: new HarmonyMethod(typeof(BarCache), nameof(BarFin)));
                h.Patch(AccessTools.Method(typeof(MoodThresholdExtensions), nameof(MoodThresholdExtensions.CurrentMoodThresholdFor)),
                    prefix: new HarmonyMethod(typeof(BarCache), nameof(MoodPre)), postfix: new HarmonyMethod(typeof(BarCache), nameof(MoodPost)));
                h.Patch(AccessTools.PropertyGetter(typeof(MentalBreaker), nameof(MentalBreaker.CanDoRandomMentalBreaks)),
                    prefix: new HarmonyMethod(typeof(BarCache), nameof(BreakPre)), postfix: new HarmonyMethod(typeof(BarCache), nameof(BreakPost)));

                MethodInfo drawIcons = AccessTools.Method(typeof(ColonistBarColonistDrawer), "DrawIcons");
                tmpField = AccessTools.Field(typeof(ColonistBarColonistDrawer), "tmpIconsToDraw");
                Type callType = AccessTools.Inner(typeof(ColonistBarColonistDrawer), "IconDrawCall");
                if (callType != null)
                {
                    fTex = AccessTools.Field(callType, "texture");
                    fTip = AccessTools.Field(callType, "tooltip");
                    fCol = AccessTools.Field(callType, "color");
                }
                try { areaWidth = (float)AccessTools.Field(typeof(ColonistBarColonistDrawer), "BaseIconAreaWidth").GetValue(null); } catch { }
                try { maxSize = (float)AccessTools.Field(typeof(ColonistBarColonistDrawer), "BaseIconMaxSize").GetValue(null); } catch { }
                bool others = false;
                if (drawIcons != null)
                {
                    var info = Harmony.GetPatchInfo(drawIcons);
                    if (info != null)
                        foreach (var o in info.Owners) if (o != h.Id) others = true;
                }
                iconsOk = drawIcons != null && tmpField != null && fTex != null && fTip != null && fCol != null && !others;
                drawIconsM = drawIcons;
                ownId = h.Id;
                if (iconsOk)
                    h.Patch(drawIcons, prefix: new HarmonyMethod(typeof(BarCache), nameof(IconsPre)), postfix: new HarmonyMethod(typeof(BarCache), nameof(IconsPost)));
                else if (others)
                    MLog.Info("[Smooth FPS] colonist bar: another mod draws the activity icons – their cache stays off.");
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] colonist bar cache not available: " + e.Message); }
        }

        public static void BarPre() { inBar = On && Event.current != null && Event.current.type == EventType.Repaint; }
        public static Exception BarFin(Exception __exception) { inBar = false; return __exception; }

        // ---- mood threshold ----
        public static bool MoodPre(Pawn pawn, ref MoodThreshold __result, out bool __state)
        {
            __state = false;
            if (!inBar || pawn == null) return true;
            if (mood.TryGetValue(pawn.thingIDNumber, out var c) && Time.realtimeSinceStartup - c.at < Keep && Time.realtimeSinceStartup >= c.at)
            {
                __result = c.v;
                return false;
            }
            __state = true;
            return true;
        }

        public static void MoodPost(Pawn pawn, MoodThreshold __result, bool __state)
        {
            if (__state && pawn != null) mood[pawn.thingIDNumber] = (Time.realtimeSinceStartup, __result);
        }

        // ---- can break ----
        public static bool BreakPre(MentalBreaker __instance, ref bool __result, out int __state)
        {
            __state = -1;
            if (!inBar) return true;
            Pawn p = breakerPawn(__instance);
            if (p == null) return true;
            if (canBreak.TryGetValue(p.thingIDNumber, out var c) && Time.realtimeSinceStartup - c.at < Keep && Time.realtimeSinceStartup >= c.at)
            {
                __result = c.v;
                return false;
            }
            __state = p.thingIDNumber;
            return true;
        }

        public static void BreakPost(bool __result, int __state)
        {
            if (__state >= 0) canBreak[__state] = (Time.realtimeSinceStartup, __result);
        }

        // ---- activity icons ----
        public static bool IconsPre(Rect rect, Pawn colonist, out bool __state)
        {
            __state = false;
            if (!inBar || colonist == null || colonist.Dead) return true;
            if (!othersChecked) CheckOthers();
            if (!iconsOk) return true;
            if (!icons.TryGetValue(colonist.thingIDNumber, out Entry e) || Time.realtimeSinceStartup - e.at >= Keep || Time.realtimeSinceStartup < e.at)
            {
                __state = true;   // the game makes the list, the postfix keeps it
                return true;
            }
            // the game's own list gets this colonist's icons, so whatever else reads it sees the right ones
            try
            {
                if (tmpField.GetValue(null) is IList tmp) { tmp.Clear(); foreach (object o in e.raw) tmp.Add(o); }
            }
            catch { }
            if (e.icons.Count == 0) return false;
            float size = Mathf.Min(areaWidth / e.icons.Count, maxSize) * Find.ColonistBar.Scale;
            Vector2 pos = new Vector2(rect.x + 1f, rect.yMax - size - 1f);

            for (int i = 0; i < e.icons.Count; i++)
            {
                Icon ic = e.icons[i];
                GUI.color = ic.col ?? Color.white;
                Rect r = new Rect(pos.x, pos.y, size, size);
                if (ic.tex != null) GUI.DrawTexture(r, ic.tex);
                if (ic.tip != null) TooltipHandler.TipRegion(r, ic.tip);
                pos.x += size;
                GUI.color = Color.white;
            }
            GUI.color = Color.white;   // like the game: white after the icons
            return false;
        }

        public static void IconsPost(Pawn colonist, bool __state)
        {
            if (!__state || colonist == null) return;
            try
            {
                if (!icons.TryGetValue(colonist.thingIDNumber, out Entry e)) { e = new Entry(); icons[colonist.thingIDNumber] = e; }
                e.icons.Clear();
                e.raw.Clear();
                if (tmpField.GetValue(null) is IList list)
                    foreach (object o in list)
                    {
                        e.raw.Add(o);
                        e.icons.Add(new Icon { tex = fTex.GetValue(o) as Texture2D, tip = fTip.GetValue(o) as string, col = (Color?)fCol.GetValue(o) });
                    }
                e.at = Time.realtimeSinceStartup;
                if (icons.Count > 400) icons.Clear();
            }
            catch { iconsOk = false; }
        }

        // 1.7.2: a mod that patches the icon drawing after us (later in the load order) reads the game's
        // icon list in its own patch – with our cache that list still held the last colonist's icons, and
        // they showed on everyone. So once the game runs, look again who patches it; anyone else → cache off.
        private static void CheckOthers()
        {
            othersChecked = true;
            try
            {
                var info = drawIconsM != null ? Harmony.GetPatchInfo(drawIconsM) : null;
                if (info == null) return;
                foreach (var o in info.Owners)
                    if (o != ownId)
                    {
                        iconsOk = false;
                        icons.Clear();
                        MLog.Info("[Smooth FPS] colonist bar: another mod (" + o + ") draws the activity icons – their cache stays off.");
                        return;
                    }
            }
            catch { iconsOk = false; }
        }

        public static void Reset() { icons.Clear(); mood.Clear(); canBreak.Clear(); }
    }

    // 1.7.1: visual effects attached to pawns (PawnRenderer.EffectersTick) are not updated for pawns you
    // cannot see – they only make flecks and sounds nobody sees or hears. Seen pawns as before.
    public static class OffscreenEffecters
    {
        private static readonly AccessTools.FieldRef<PawnRenderer, Pawn> rendererPawn = AccessTools.FieldRefAccess<PawnRenderer, Pawn>("pawn");
        private static CellRect view;
        private static int viewFrame = -1;

        public static void Patch(Harmony h)
        {
            try
            {
                MethodInfo m = AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.EffectersTick));
                if (m != null) h.Patch(m, prefix: new HarmonyMethod(typeof(OffscreenEffecters), nameof(Prefix)));
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] off-screen effects not available: " + e.Message); }
        }

        public static bool Prefix(PawnRenderer __instance)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.offscreenFx) return true;
            try
            {
                Pawn p = rendererPawn(__instance);
                if (p == null || !p.Spawned) return true;
                if (WorldRendererUtility.WorldSelected || p.Map != Find.CurrentMap) return false;
                if (viewFrame != Time.frameCount)
                {
                    viewFrame = Time.frameCount;
                    view = Find.CameraDriver.CurrentViewRect.ExpandedBy(4);
                }
                return view.Contains(p.Position);
            }
            catch { return true; }
        }
    }
}
