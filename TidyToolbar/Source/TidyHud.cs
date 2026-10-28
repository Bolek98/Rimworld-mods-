using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace TidyToolbar
{
    // ---------------------------------------------------------------------------
    // The right side of the screen: alerts, letters and the play settings icons.
    // - Alerts: the same alert many times (one per animal …) is shown once with ×N; critical and
    //   high ones always show, the rest up to a limit and folded into "+N more"; right click an
    //   alert to hide it for a day or for good.
    // - Letters: letters that can be dismissed go away by themselves after some days, and the
    //   stack keeps at most a number of them (they stay in the History tab).
    // - Play settings: the icons (zones, roofs, beauty …, also those of mods) behind one button.
    // ---------------------------------------------------------------------------
    public static class TidyHud
    {
        private static TTSettings S => TidyToolbarMod.S;

        private static readonly AccessTools.FieldRef<AlertsReadout, List<Alert>> activeRef = AccessTools.FieldRefAccess<AlertsReadout, List<Alert>>("activeAlerts");
        private static readonly AccessTools.FieldRef<AlertsReadout, float> lastFinalYRef = AccessTools.FieldRefAccess<AlertsReadout, float>("lastFinalY");
        private static readonly MethodInfo checkAddRemove = AccessTools.Method(typeof(AlertsReadout), "CheckAddOrRemoveAlert");

        private static bool failedAlerts, failedPlay;
        private static float drawnHeight = -1f;
        private static bool expanded;
        private static readonly Dictionary<string, int> mutedUntil = new Dictionary<string, int>();
        private static bool inPopup;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(AlertsReadout), nameof(AlertsReadout.AlertsReadoutOnGUI)), prefix: new HarmonyMethod(typeof(TidyHud), nameof(AlertsPrefix)));
                h.Patch(AccessTools.PropertyGetter(typeof(AlertsReadout), nameof(AlertsReadout.AlertsHeight)), postfix: new HarmonyMethod(typeof(TidyHud), nameof(HeightPostfix)));
            }
            catch (Exception e) { Log.Warning("[Tidy Toolbar] alerts: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(LetterStack), nameof(LetterStack.LetterStackTick)), postfix: new HarmonyMethod(typeof(TidyHud), nameof(LettersTick)));
            }
            catch (Exception e) { Log.Warning("[Tidy Toolbar] letters: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls)), prefix: new HarmonyMethod(typeof(TidyHud), nameof(PlayPrefix)) { priority = Priority.First });
            }
            catch (Exception e) { Log.Warning("[Tidy Toolbar] play settings: " + e.Message); }
        }

        // ---------------- alerts ----------------
        private static string Key(Alert a) => a.GetType().FullName;

        private static bool Muted(Alert a)
        {
            string k = Key(a);
            if (S.mutedAlerts.Contains(k)) return true;
            return mutedUntil.TryGetValue(k, out int until) && Find.TickManager != null && Find.TickManager.TicksGame < until;
        }

        private sealed class Row
        {
            public Alert alert;
            public int count;
        }

        private static List<Row> Rows(List<Alert> active)
        {
            var rows = new List<Row>();
            var byLabel = new Dictionary<string, Row>();
            foreach (AlertPriority prio in new[] { AlertPriority.Critical, AlertPriority.High, AlertPriority.Medium })
                foreach (Alert a in active)
                {
                    if (a.Priority != prio || Muted(a)) continue;
                    string label = a.Label ?? "";
                    string key = (int)prio + "|" + label;
                    if (byLabel.TryGetValue(key, out Row r)) { r.count++; continue; }
                    r = new Row { alert = a, count = 1 };
                    byLabel[key] = r;
                    rows.Add(r);
                }
            return rows;
        }

        public static void HeightPostfix(ref float __result)
        {
            if (S != null && S.tidyAlerts && !failedAlerts && drawnHeight >= 0f) __result = drawnHeight;
        }

        public static bool AlertsPrefix(AlertsReadout __instance)
        {
            if (S == null || !S.tidyAlerts || failedAlerts) return true;
            try
            {
                if (Event.current.type == EventType.Layout || Event.current.type == EventType.MouseDrag) return false;
                List<Alert> active = activeRef(__instance);
                List<Row> rows = Rows(active);
                // what is shown: critical and high always, the rest up to the limit
                var shown = new List<Row>();
                int hidden = 0;
                foreach (Row r in rows)
                {
                    if (expanded || r.alert.Priority != AlertPriority.Medium || shown.Count < Mathf.Max(1, S.alertLimit)) shown.Add(r);
                    else hidden += r.count;
                }
                const float foldH = 24f;
                bool fold = hidden > 0 || expanded && rows.Count > S.alertLimit;
                float height = shown.Sum(r => r.alert.Height) + (fold ? foldH : 0f);
                drawnHeight = height;
                if (shown.Count == 0) { lastFinalYRef(__instance) = 0f; return false; }
                float top = Mathf.Max(0f, Find.LetterStack.LastTopY - height);
                Alert over = null;
                float y = top;
                AlertPriority first = shown[0].alert.Priority;
                foreach (Row r in shown)
                {
                    Rect rr = new Rect(UI.screenWidth - 154f, y, 154f, r.alert.Height);
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(rr) && !Event.current.shift)
                    {
                        Event.current.Use();
                        Find.WindowStack.Add(new FloatMenu(AlertMenu(r.alert)));
                    }
                    Rect drawn = r.alert.DrawAt(y, r.alert.Priority != first);
                    if (r.count > 1)
                    {
                        Text.Font = GameFont.Tiny;
                        GUI.color = new Color(1f, 0.85f, 0.4f);
                        Widgets.Label(new Rect(drawn.x + 4f, drawn.y + 4f, 40f, 20f), "×" + r.count);
                        GUI.color = Color.white;
                        Text.Font = GameFont.Small;
                    }
                    if (Mouse.IsOver(drawn)) over = r.alert;
                    y += drawn.height;
                }
                if (fold)
                {
                    Rect fr = new Rect(UI.screenWidth - 154f, y, 154f, foldH);
                    Widgets.DrawBoxSolid(fr, new Color(0f, 0f, 0f, 0.35f));
                    Text.Font = GameFont.Tiny;
                    Text.Anchor = TextAnchor.MiddleRight;
                    Widgets.Label(new Rect(fr.x, fr.y, fr.width - 6f, fr.height), expanded ? "TT_AlertsFold".Translate() : "TT_AlertsMore".Translate(hidden));
                    Text.Anchor = TextAnchor.UpperLeft;
                    Text.Font = GameFont.Small;
                    if (Mouse.IsOver(fr)) Widgets.DrawHighlight(fr);
                    if (Widgets.ButtonInvisible(fr)) expanded = !expanded;
                    y += foldH;
                }
                lastFinalYRef(__instance) = y;
                if (over != null)
                {
                    over.DrawInfoPane();
                    try { checkAddRemove?.Invoke(__instance, new object[] { over }); } catch { }
                }
                return false;
            }
            catch (Exception e)
            {
                failedAlerts = true;
                drawnHeight = -1f;
                Log.Error("[Tidy Toolbar] tidy alerts failed, the game's alerts are used instead: " + e);
                return true;
            }
        }

        private static List<FloatMenuOption> AlertMenu(Alert a)
        {
            string k = Key(a);
            return new List<FloatMenuOption>
            {
                new FloatMenuOption("TT_AlertHideDay".Translate(), () => mutedUntil[k] = Find.TickManager.TicksGame + 60000),
                new FloatMenuOption("TT_AlertHideForever".Translate(), () => { if (!S.mutedAlerts.Contains(k)) S.mutedAlerts.Add(k); Save(); }),
                new FloatMenuOption("TT_Edit".Translate(), OpenSettings),
            };
        }

        // ---------------- letters ----------------
        public static void LettersTick()
        {
            if (S == null || Find.TickManager.TicksGame % 250 != 17) return;
            try
            {
                List<Letter> letters = Find.LetterStack.LettersListForReading;
                if (letters.Count == 0) return;
                int now = Find.TickManager.TicksGame;
                var remove = new List<Letter>();
                if (S.letterDays > 0)
                    foreach (Letter l in letters)
                        if (Removable(l) && now - l.arrivalTick > S.letterDays * 60000) remove.Add(l);
                if (S.letterMax > 0)
                {
                    int left = letters.Count - remove.Count;
                    foreach (Letter l in letters)       // the oldest first
                    {
                        if (left <= S.letterMax) break;
                        if (remove.Contains(l) || !Removable(l)) continue;
                        remove.Add(l);
                        left--;
                    }
                }
                foreach (Letter l in remove) Find.LetterStack.RemoveLetter(l);
            }
            catch (Exception e) { Log.WarningOnce("[Tidy Toolbar] letters: " + e.Message, 0x7d1e7e); }
        }

        // only letters the player could dismiss himself, and never an offer that waits for an answer
        private static bool Removable(Letter l)
        {
            if (l == null || !l.CanDismissWithRightClick) return false;
            if (l is ChoiceLetter cl && cl.quest != null && cl.quest.State == QuestState.NotYetAccepted) return false;
            if (l.def == LetterDefOf.ThreatBig && Find.TickManager.TicksGame - l.arrivalTick < 60000) return false;
            return true;
        }

        // ---------------- play settings ----------------
        public static bool PlayPrefix(ref WidgetRow row, bool worldView)
        {
            if (S == null || !S.collapsePlay || inPopup || failedPlay || row == null) return true;
            try
            {
                bool open = Find.WindowStack.IsOpen<Window_PlaySettings>();
                if (row.ButtonIcon(TexButton.OpenInspectSettings, "TT_PlayTip".Translate(), open ? (Color?)new Color(1f, 0.85f, 0.4f) : null))
                {
                    if (open) Find.WindowStack.TryRemove(typeof(Window_PlaySettings));
                    else Find.WindowStack.Add(new Window_PlaySettings(worldView));
                }
                // the icons (and those of other mods) are still drawn, far away from the screen, so
                // their hotkeys keep working
                row = new WidgetRow(-20000f, -20000f, UIDirection.RightThenDown, 99999f);
                return true;
            }
            catch (Exception e)
            {
                failedPlay = true;
                Log.Error("[Tidy Toolbar] play settings button failed, the icons are shown as usual: " + e);
                return true;
            }
        }

        public sealed class Window_PlaySettings : Window
        {
            private readonly bool world;
            private float lastH = 120f;

            public Window_PlaySettings(bool worldView)
            {
                world = worldView;
                closeOnClickedOutside = true;
                doCloseX = false;
                preventCameraMotion = false;
                absorbInputAroundWindow = false;
                layer = WindowLayer.GameUI;
                doWindowBackground = true;
            }

            public override Vector2 InitialSize => new Vector2(8 * 28f + 2 * Margin, lastH);

            protected override void SetInitialSizeAndPosition()
            {
                Vector2 s = InitialSize;
                windowRect = new Rect(UI.screenWidth - s.x - 8f, UI.screenHeight - 35f - s.y - 160f, s.x, s.y);
            }

            public override void DoWindowContents(Rect inRect)
            {
                inPopup = true;
                try
                {
                    var r = new WidgetRow(inRect.x, inRect.y, UIDirection.RightThenDown, inRect.width);
                    Find.PlaySettings.DoPlaySettingsGlobalControls(r, world);
                    float need = r.FinalY + 24f - inRect.y + 2 * Margin;
                    if (Mathf.Abs(need - windowRect.height) > 2f && Event.current.type == EventType.Repaint)
                    {
                        lastH = need;
                        float bottom = windowRect.yMax;
                        windowRect.height = need;
                        windowRect.y = bottom - need;
                    }
                }
                finally { inPopup = false; }
            }
        }

        // ---------------- settings page ----------------
        private static Vector2 scroll;

        public static void DrawSettings(Rect r)
        {
            var l = new Listing_Standard();
            l.Begin(new Rect(r.x, r.y, r.width, 300f));
            l.CheckboxLabeled("TT_TidyAlerts".Translate(), ref S.tidyAlerts, "TT_TidyAlertsTip".Translate());
            S.alertLimit = Mathf.RoundToInt(l.SliderLabeled("TT_AlertLimit".Translate(S.alertLimit), S.alertLimit, 3f, 30f));
            l.Gap(6f);
            S.letterDays = Mathf.RoundToInt(l.SliderLabeled(S.letterDays == 0 ? "TT_LetterDaysOff".Translate().ToString() : "TT_LetterDays".Translate(S.letterDays).ToString(), S.letterDays, 0f, 15f, tooltip: "TT_LetterDaysTip".Translate()));
            S.letterMax = Mathf.RoundToInt(l.SliderLabeled(S.letterMax == 0 ? "TT_LetterMaxOff".Translate().ToString() : "TT_LetterMax".Translate(S.letterMax).ToString(), S.letterMax, 0f, 40f, tooltip: "TT_LetterMaxTip".Translate()));
            l.Gap(6f);
            l.CheckboxLabeled("TT_CollapsePlay".Translate(), ref S.collapsePlay, "TT_CollapsePlayTip".Translate());
            l.End();
            float y = r.y + 230f;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), "TT_MutedTitle".Translate(S.mutedAlerts.Count));
            y += 26f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, S.mutedAlerts.Count * 28f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < S.mutedAlerts.Count; i++)
            {
                string k = S.mutedAlerts[i];
                Rect row = new Rect(0f, i * 28f, view.width, 26f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                Widgets.Label(new Rect(4f, row.y + 2f, row.width - 120f, 24f), AlertName(k));
                if (Widgets.ButtonText(new Rect(row.xMax - 110f, row.y, 110f, 26f), "TT_Unmute".Translate()))
                {
                    S.mutedAlerts.RemoveAt(i);
                    Save();
                    break;
                }
            }
            Widgets.EndScrollView();
        }

        private static string AlertName(string typeName)
        {
            try
            {
                Type t = GenTypes.GetTypeInAnyAssembly(typeName);
                if (t != null && Find.Alerts != null)
                {
                    Alert a = activeRef(Find.Alerts)?.FirstOrDefault(x => x.GetType() == t);
                    if (a != null) return a.Label;
                }
            }
            catch { }
            string n = typeName.Substring(typeName.LastIndexOf('.') + 1);
            return n.StartsWith("Alert_") ? n.Substring(6) : n;
        }

        private static void Save()
        {
            try { LoadedModManager.GetMod<TidyToolbarMod>()?.WriteSettings(); } catch { }
        }

        private static void OpenSettings()
        {
            Mod mod = LoadedModManager.GetMod<TidyToolbarMod>();
            if (mod != null) Find.WindowStack.Add(new Dialog_ModSettings(mod));
        }
    }
}
