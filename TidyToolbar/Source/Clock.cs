using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace TidyToolbar
{
    // ---------------------------------------------------------------------------
    // 1.1.0: the clock. Takes the place of the game's date readout (bottom right):
    //  - game time HH:MM in a big font (small / medium / large), sun or moon,
    //  - a bar of the day: night dark, day light, the line is "now",
    //  - date, season, weather and outdoor temperature,
    //  - today's holiday (Seasons & Holidays), optional colony line (colonists, food, silver),
    //  - tooltip: sunrise, sunset, time to sunset/sunrise, days passed, the quadrums.
    // Left click: compact (time only) / full. Right click: menu. It can also float freely and
    // be dragged anywhere. Alarm at a set time (sound / normal speed / pause) and a warning an
    // hour before dusk.
    // Also here: buttons of the selected pawn/thing (gizmos) smaller and with other spacing.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class Clock
    {
        private static TTSettings S => TidyToolbarMod.S;
        private static bool failed;

        private static readonly Texture2D Disc = MakeDisc(32);
        private static readonly Color SunCol = new Color(1f, 0.85f, 0.35f);
        private static readonly Color MoonCol = new Color(0.82f, 0.86f, 0.95f);
        private static readonly Color NightCol = new Color(0.10f, 0.12f, 0.25f);
        private static readonly Color DayCol = new Color(0.55f, 0.75f, 0.95f);
        private static readonly Color Dim = new Color(0.75f, 0.77f, 0.8f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.4f);

        private static Texture2D MakeDisc(int n)
        {
            var t = new Texture2D(n, n, TextureFormat.ARGB32, false);
            float c = (n - 1) / 2f, r = n / 2f - 0.5f;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c));
                    t.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Clamp01(r - d + 0.5f)));
                }
            t.Apply();
            return t;
        }

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(DateReadout), nameof(DateReadout.Height)), postfix: new HarmonyMethod(typeof(Clock), nameof(HeightPostfix)));
                h.Patch(AccessTools.Method(typeof(DateReadout), nameof(DateReadout.DateOnGUI)), prefix: new HarmonyMethod(typeof(Clock), nameof(DatePrefix)) { priority = Priority.First });
                h.Patch(AccessTools.Method(typeof(MapInterface), nameof(MapInterface.MapInterfaceOnGUI_AfterMainTabs)), postfix: new HarmonyMethod(typeof(Clock), nameof(FreeOnGUI)));
            }
            catch (Exception e) { Log.Warning("[Tidy Toolbar] clock: " + e.Message); }
            try
            {
                MethodInfo grid = AccessTools.Method(typeof(GizmoGridDrawer), nameof(GizmoGridDrawer.DrawGizmoGrid));
                h.Patch(grid, prefix: new HarmonyMethod(typeof(Clock), nameof(GizmoPrefix)), postfix: new HarmonyMethod(typeof(Clock), nameof(GizmoPostfix)),
                    transpiler: new HarmonyMethod(typeof(Clock), nameof(GizmoTranspiler)), finalizer: new HarmonyMethod(typeof(Clock), nameof(GizmoFinalizer)));
            }
            catch (Exception e) { Log.Warning("[Tidy Toolbar] pawn buttons size: " + e.Message); }
        }

        private static bool Active => S != null && S.clockOn && !failed;
        private static bool FreeNow => S.clockFree && !WorldRendererUtility.WorldSelected;

        // ---------------- where it is drawn ----------------
        public static void HeightPostfix(ref float __result)
        {
            if (!Active) return;
            __result = FreeNow ? 0f : Size().y;
        }

        public static bool DatePrefix(Rect dateRect)
        {
            if (!Active) return true;
            if (FreeNow) return false;
            try
            {
                Vector2 sz = Size();
                Rect r = new Rect(dateRect.xMax - sz.x, dateRect.yMax - sz.y, sz.x, sz.y);
                Draw(r, false);
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[Tidy Toolbar] the clock failed, the game's date is shown instead: " + e);
                return true;
            }
        }

        private static bool dragging, dragMoved;
        private static Vector2 dragOffset, dragStart;

        public static void FreeOnGUI()
        {
            if (!Active || !FreeNow || Find.CurrentMap == null) return;
            try
            {
                Vector2 sz = Size();
                if (S.clockX < 0f || S.clockY < 0f) { S.clockX = UI.screenWidth - sz.x - 160f; S.clockY = UI.screenHeight - sz.y - 120f; }
                S.clockX = Mathf.Clamp(S.clockX, 0f, UI.screenWidth - sz.x);
                S.clockY = Mathf.Clamp(S.clockY, 0f, UI.screenHeight - sz.y);
                Rect r = new Rect(S.clockX, S.clockY, sz.x, sz.y);
                Widgets.DrawWindowBackground(r);
                Draw(r.ContractedBy(2f), true);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[Tidy Toolbar] the clock failed, the game's date is shown instead: " + e);
            }
        }

        // ---------------- size ----------------
        private static float TimeRowH => S.clockSize == 0 ? 30f : S.clockSize == 1 ? 40f : 52f;

        private static Vector2 Size()
        {
            float h = TimeRowH;
            if (!S.clockCompact)
            {
                h += 12f;                 // day bar
                h += 24f;                 // date
                if (Find.CurrentMap != null && !WorldRendererUtility.WorldSelected)
                {
                    h += 22f;             // season, weather, temperature
                    if (S.clockHoliday && HolidayToday() != null) h += 20f;
                    if (S.clockColony) h += 20f;
                }
            }
            float w = S.clockCompact ? (S.clockSize == 2 ? 150f : 120f) : (S.clockSize == 0 ? 190f : S.clockSize == 1 ? 220f : 240f);
            return new Vector2(w, h + 4f);
        }

        // ---------------- the widget ----------------
        private static void Location(out Vector2 longLat, out Map map)
        {
            map = null;
            if (WorldRendererUtility.WorldSelected && Find.WorldSelector.SelectedTile.Valid)
                longLat = Find.WorldGrid.LongLatOf(Find.WorldSelector.SelectedTile);
            else if (WorldRendererUtility.WorldSelected && Find.WorldSelector.NumSelectedObjects > 0)
                longLat = Find.WorldGrid.LongLatOf(Find.WorldSelector.FirstSelectedObject.Tile);
            else
            {
                map = Find.CurrentMap;
                longLat = map != null ? Find.WorldGrid.LongLatOf(map.Tile) : Vector2.zero;
            }
        }

        public static string TimeText(float hour)
        {
            int h = Mathf.FloorToInt(hour) % 24;
            int m = Mathf.FloorToInt((hour - Mathf.Floor(hour)) * 60f);
            if (!Prefs.TwelveHourClockMode) return h.ToString("00") + ":" + m.ToString("00");
            int hh = h % 12 == 0 ? 12 : h % 12;
            return hh + ":" + m.ToString("00") + " " + (h >= 12 ? "PM".Translate() : "AM".Translate());
        }

        private static void Draw(Rect r, bool free)
        {
            if (Find.TickManager == null) return;
            Location(out Vector2 ll, out Map map);
            if (map == null && !WorldRendererUtility.WorldSelected) return;
            long abs = Find.TickManager.TicksAbs;
            float hour = GenDate.HourFloat(abs, ll.x);
            float glow = map != null ? GenCelestial.CurCelestialSunGlow(map) : (hour >= 6f && hour < 18f ? 1f : 0f);
            bool day = glow >= 0.5f;

            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            float y = r.y;

            // time + sun / moon
            Rect tr = new Rect(r.x, y, r.width - 7f, TimeRowH);
            string time = TimeText(hour);
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleRight;
            float tw = Text.CalcSize(time).x;
            float k = S.clockSize == 0 ? 1f : S.clockSize == 1 ? 1.3f : 1.7f;
            if (k > 1f)
            {
                Matrix4x4 old = GUI.matrix;
                GUIUtility.ScaleAroundPivot(new Vector2(k, k), new Vector2(tr.xMax, tr.center.y));
                Widgets.Label(new Rect(tr.xMax - tw - 4f, tr.center.y - 16f, tw + 4f, 32f), time);
                GUI.matrix = old;
                tw *= k;
            }
            else Widgets.Label(tr, time);
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            float ds = S.clockSize == 0 ? 16f : 20f;
            Rect sun = new Rect(tr.xMax - tw - ds - 10f, tr.center.y - ds / 2f, ds, ds);
            GUI.color = day ? SunCol : MoonCol;
            GUI.DrawTexture(sun, Disc);
            if (!day)
            {
                GUI.color = new Color(0.08f, 0.09f, 0.12f, 0.95f);
                GUI.DrawTexture(new Rect(sun.x + ds * 0.32f, sun.y - ds * 0.1f, ds, ds), Disc);
            }
            GUI.color = Color.white;
            y += TimeRowH;

            if (!S.clockCompact)
            {
                // the day bar
                Rect bar = new Rect(r.x + 6f, y + 2f, r.width - 13f, 7f);
                DrawDayBar(bar, map, abs, ll.x);
                y += 12f;
                // date
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(r.x, y, r.width - 7f, 24f), GenDate.DateReadoutStringAt(abs, ll));
                y += 24f;
                if (map != null && !WorldRendererUtility.WorldSelected)
                {
                    Text.Font = GameFont.Tiny;
                    GUI.color = Dim;
                    string season = GenDate.Season(abs, ll).LabelCap();
                    string line = season;
                    if (S.clockWeather)
                        line += " · " + map.weatherManager.curWeather.LabelCap + " · " + map.mapTemperature.OutdoorTemp.ToStringTemperature("F0");
                    Widgets.Label(new Rect(r.x, y + 2f, r.width - 7f, 22f), line);
                    y += 22f;
                    if (S.clockHoliday)
                    {
                        string hol = HolidayToday();
                        if (hol != null)
                        {
                            GUI.color = Gold;
                            Widgets.Label(new Rect(r.x, y, r.width - 7f, 20f), hol);
                            y += 20f;
                        }
                    }
                    if (S.clockColony)
                    {
                        GUI.color = Dim;
                        Widgets.Label(new Rect(r.x, y, r.width - 7f, 20f), ColonyLine(map));
                        y += 20f;
                    }
                    GUI.color = Color.white;
                    Text.Font = GameFont.Small;
                }
                Text.Anchor = TextAnchor.UpperLeft;
            }

            // mouse: click = compact, right click = menu, drag = move (free mode)
            Event ev = Event.current;
            if (free)
            {
                if (ev.type == EventType.MouseDown && ev.button == 0 && Mouse.IsOver(r))
                {
                    dragging = true;
                    dragMoved = false;
                    dragStart = ev.mousePosition;
                    dragOffset = ev.mousePosition - new Vector2(S.clockX, S.clockY);
                    ev.Use();
                }
                else if (dragging && ev.type == EventType.MouseDrag)
                {
                    if ((ev.mousePosition - dragStart).sqrMagnitude > 16f) dragMoved = true;
                    if (dragMoved) { S.clockX = ev.mousePosition.x - dragOffset.x; S.clockY = ev.mousePosition.y - dragOffset.y; }
                    ev.Use();
                }
                else if (dragging && (ev.rawType == EventType.MouseUp || ev.type == EventType.MouseUp))
                {
                    dragging = false;
                    if (!dragMoved) S.clockCompact = !S.clockCompact;
                    Save();
                    ev.Use();
                }
            }
            else if (Widgets.ButtonInvisible(r, false))
            {
                if (ev.button == 1) Find.WindowStack.Add(new FloatMenu(Menu()));
                else { S.clockCompact = !S.clockCompact; Save(); }
            }
            if (free && ev.type == EventType.MouseDown && ev.button == 1 && Mouse.IsOver(r))
            {
                Find.WindowStack.Add(new FloatMenu(Menu()));
                ev.Use();
            }
            if (Mouse.IsOver(r))
                TooltipHandler.TipRegion(r, new TipSignal(() => Tooltip(map, ll), 0x71d0c10c));
        }

        private static void DrawDayBar(Rect bar, Map map, long abs, float lon)
        {
            float[] g = Glow(map, abs, lon);
            int n = g.Length;
            float w = bar.width / n;
            for (int i = 0; i < n; i++)
            {
                GUI.color = Color.Lerp(NightCol, DayCol, g[i]);
                GUI.DrawTexture(new Rect(bar.x + i * w, bar.y, w + 0.5f, bar.height), BaseContent.WhiteTex);
            }
            float p = GenDate.DayPercent(abs, lon);
            GUI.color = Color.white;
            GUI.DrawTexture(new Rect(bar.x + p * bar.width - 1f, bar.y - 2f, 2f, bar.height + 4f), BaseContent.WhiteTex);
            GUI.color = Color.white;
        }

        // ---------------- the sun over today ----------------
        private const int Samples = 48;
        private static int glowKey = int.MinValue;
        private static float[] glowCache = new float[Samples];

        private static float[] Glow(Map map, long abs, float lon)
        {
            long dayStart = abs - (long)(GenDate.DayPercent(abs, lon) * 60000f);
            int key = (int)(dayStart / 60000L) * 977 + (map?.uniqueID ?? -7);
            if (key == glowKey) return glowCache;
            glowKey = key;
            for (int i = 0; i < Samples; i++)
            {
                int t = (int)(dayStart + (long)((i + 0.5f) * 60000f / Samples));
                glowCache[i] = map != null ? GenCelestial.CelestialSunGlow(map, t) : (i >= Samples / 4 && i < Samples * 3 / 4 ? 1f : 0f);
            }
            return glowCache;
        }

        // hour of sunrise and sunset today (-1 = none: polar day or night)
        public static void SunTimes(Map map, out float rise, out float set)
        {
            rise = set = -1f;
            if (map == null) return;
            float lon = Find.WorldGrid.LongLatOf(map.Tile).x;
            float[] g = Glow(map, Find.TickManager.TicksAbs, lon);
            for (int i = 0; i < Samples - 1; i++)
            {
                float a = g[i], b = g[i + 1];
                float hour = (i + 0.5f) * 24f / Samples;
                if (a < 0.5f && b >= 0.5f && rise < 0f) rise = hour + (0.5f - a) / Mathf.Max(0.0001f, b - a) * 24f / Samples;
                if (a >= 0.5f && b < 0.5f && set < 0f) set = hour + (a - 0.5f) / Mathf.Max(0.0001f, a - b) * 24f / Samples;
            }
        }

        private static string Tooltip(Map map, Vector2 ll)
        {
            var sb = new System.Text.StringBuilder();
            long abs = Find.TickManager.TicksAbs;
            float hour = GenDate.HourFloat(abs, ll.x);
            if (map != null)
            {
                SunTimes(map, out float rise, out float set);
                sb.AppendLine("TT_ClockRise".Translate(rise < 0f ? "–" : TimeText(rise)) + "    " + "TT_ClockSet".Translate(set < 0f ? "–" : TimeText(set)));
                bool day = GenCelestial.CurCelestialSunGlow(map) >= 0.5f;
                float next = day ? set : rise;
                if (next >= 0f)
                {
                    float left = next - hour;
                    if (left < 0f) left += 24f;
                    sb.AppendLine((day ? "TT_ClockToSet" : "TT_ClockToRise").Translate(Hours(left)));
                }
                sb.AppendLine();
            }
            sb.AppendLine("TT_ClockDays".Translate(GenDate.DaysPassed));
            Season season = GenDate.Season(abs, ll);
            for (int i = 0; i < 4; i++)
            {
                Quadrum q = (Quadrum)i;
                sb.AppendLine(q.Label() + " – " + q.GetSeason(ll.y).LabelCap());
            }
            if (S.alarmOn) sb.AppendLine().AppendLine("TT_ClockAlarmTip".Translate(TimeText(S.alarmHour + S.alarmMin / 60f)));
            sb.AppendLine().Append("TT_ClockHelp".Translate());
            return sb.ToString();
        }

        private static string Hours(float h)
        {
            int hh = Mathf.FloorToInt(h), mm = Mathf.FloorToInt((h - hh) * 60f);
            return hh > 0 ? "TT_ClockHM".Translate(hh, mm).ToString() : "TT_ClockM".Translate(mm).ToString();
        }

        private static string ColonyLine(Map map)
        {
            int people = map.mapPawns.FreeColonistsSpawnedCount;
            float food = map.resourceCounter.TotalHumanEdibleNutrition;
            float days = people > 0 ? food / (people * 1.6f) : 0f;
            return "TT_ClockColony".Translate(people, days.ToString("0.#"), map.resourceCounter.Silver);
        }

        // ---------------- Seasons & Holidays (optional, through reflection) ----------------
        private static MethodInfo shToday;
        private static PropertyInfo shLabel;
        private static bool shLooked;
        private static int holTick = -99999;
        private static string holCache;

        private static string HolidayToday()
        {
            Map map = Find.CurrentMap;
            if (map == null) return null;
            int now = Find.TickManager.TicksGame;
            if (now - holTick < 250 && now >= holTick) return holCache;
            holTick = now;
            holCache = null;
            if (!shLooked)
            {
                shLooked = true;
                Type t = GenTypes.GetTypeInAnyAssembly("SeasonsHolidays.Holidays");
                shToday = t == null ? null : AccessTools.Method(t, "Today", new[] { typeof(Map) });
                Type ht = GenTypes.GetTypeInAnyAssembly("SeasonsHolidays.Holiday");
                shLabel = ht == null ? null : AccessTools.Property(ht, "Label");
            }
            if (shToday == null || shLabel == null) return null;
            try
            {
                object h = shToday.Invoke(null, new object[] { map });
                if (h != null) holCache = "★ " + shLabel.GetValue(h);
            }
            catch { shToday = null; }
            return holCache;
        }

        // ---------------- menu ----------------
        private static List<FloatMenuOption> Menu()
        {
            var m = new List<FloatMenuOption>
            {
                new FloatMenuOption((S.clockCompact ? "TT_ClockFull" : "TT_ClockCompact").Translate(), () => { S.clockCompact = !S.clockCompact; Save(); }),
                new FloatMenuOption("TT_ClockSize".Translate(("TT_ClockSize" + ((S.clockSize + 1) % 3)).Translate()), () => { S.clockSize = (S.clockSize + 1) % 3; Save(); }),
                new FloatMenuOption((S.clockFree ? "TT_ClockDock" : "TT_ClockFloat").Translate(), () => { S.clockFree = !S.clockFree; Save(); }),
                new FloatMenuOption((S.clockWeather ? "TT_ClockHideWeather" : "TT_ClockShowWeather").Translate(), () => { S.clockWeather = !S.clockWeather; Save(); }),
                new FloatMenuOption((S.clockColony ? "TT_ClockHideColony" : "TT_ClockShowColony").Translate(), () => { S.clockColony = !S.clockColony; Save(); }),
                new FloatMenuOption(S.alarmOn ? "TT_ClockAlarmOff".Translate(TimeText(S.alarmHour + S.alarmMin / 60f)).ToString() : "TT_ClockAlarmSet".Translate().ToString(), () =>
                {
                    if (S.alarmOn) { S.alarmOn = false; Save(); }
                    else Find.WindowStack.Add(new Dialog_Alarm());
                }),
                new FloatMenuOption("TT_Edit".Translate(), () =>
                {
                    Mod mod = LoadedModManager.GetMod<TidyToolbarMod>();
                    if (mod != null) { TidyToolbarMod.OpenPage(2); Find.WindowStack.Add(new Dialog_ModSettings(mod)); }
                }),
            };
            return m;
        }

        public static void Save()
        {
            try { LoadedModManager.GetMod<TidyToolbarMod>()?.WriteSettings(); } catch { }
        }

        // ---------------- buttons of the selection (gizmos) ----------------
        private static readonly FieldInfo heightDrawnF = AccessTools.Field(typeof(GizmoGridDrawer), "heightDrawn");
        private static Matrix4x4 gizmoOld;
        private static bool gizmoScaled;

        public static Vector2 Spacing()
        {
            if (S == null) return new Vector2(5f, 14f);
            return new Vector2(S.gizmoGapX, S.gizmoGapY);
        }

        // the game's spacing (static readonly) is swapped for this field, set before every draw
        public static Vector2 gap = new Vector2(5f, 14f);

        public static IEnumerable<CodeInstruction> GizmoTranspiler(IEnumerable<CodeInstruction> code)
        {
            FieldInfo f = AccessTools.Field(typeof(GizmoGridDrawer), "GizmoSpacing");
            FieldInfo mine = AccessTools.Field(typeof(Clock), nameof(gap));
            int n = 0;
            foreach (CodeInstruction ci in code)
            {
                if (f != null && (ci.opcode == OpCodes.Ldsfld || ci.opcode == OpCodes.Ldsflda) && ci.operand is FieldInfo fi && fi == f)
                {
                    ci.operand = mine;
                    n++;
                }
                yield return ci;
            }
            if (n == 0) Log.Warning("[Tidy Toolbar] the spacing of pawn buttons could not be changed (game code differs).");
        }

        public static void GizmoPrefix(float startX)
        {
            gap = Spacing();
            gizmoScaled = false;
            if (S == null || Mathf.Abs(S.gizmoScale - 1f) < 0.01f) return;
            gizmoOld = GUI.matrix;
            GUIUtility.ScaleAroundPivot(new Vector2(S.gizmoScale, S.gizmoScale), new Vector2(startX, UI.screenHeight));
            gizmoScaled = true;
        }

        public static void GizmoPostfix()
        {
            if (!gizmoScaled) return;
            GUI.matrix = gizmoOld;
            gizmoScaled = false;
            try
            {
                if (heightDrawnF != null) heightDrawnF.SetValue(null, (float)heightDrawnF.GetValue(null) * S.gizmoScale);
            }
            catch { }
        }

        public static Exception GizmoFinalizer(Exception __exception)
        {
            if (gizmoScaled) { GUI.matrix = gizmoOld; gizmoScaled = false; }
            return __exception;
        }
    }

    // ---------------- alarm and dusk warning ----------------
    public class ClockAlarms : GameComponent
    {
        private float lastHour = -1f;
        private int lastMapId = -1;
        private int duskWarnedDay = -1;

        public ClockAlarms(Game g) { }

        public override void GameComponentTick()
        {
            TTSettings S = TidyToolbarMod.S;
            if (S == null || Find.TickManager.TicksGame % 30 != 0) return;
            if (!S.alarmOn && !S.duskWarn) return;
            try
            {
                Map map = Find.CurrentMap ?? Find.AnyPlayerHomeMap;
                if (map == null) return;
                long abs = Find.TickManager.TicksAbs;
                float lon = Find.WorldGrid.LongLatOf(map.Tile).x;
                float hour = GenDate.HourFloat(abs, lon);
                if (map.uniqueID != lastMapId) { lastMapId = map.uniqueID; lastHour = hour; return; }
                float prev = lastHour;
                lastHour = hour;
                if (S.alarmOn && Crossed(prev, hour, S.alarmHour + S.alarmMin / 60f))
                {
                    SoundDefOf.TinyBell.PlayOneShotOnCamera();
                    Messages.Message("TT_AlarmRang".Translate(Clock.TimeText(S.alarmHour + S.alarmMin / 60f)), MessageTypeDefOf.CautionInput, false);
                    if (S.alarmAction == 1 && Find.TickManager.CurTimeSpeed > TimeSpeed.Normal) Find.TickManager.CurTimeSpeed = TimeSpeed.Normal;
                    else if (S.alarmAction == 2) Find.TickManager.Pause();
                }
                if (S.duskWarn)
                {
                    Clock.SunTimes(map, out _, out float set);
                    int day = GenDate.DaysPassed;
                    if (set >= 0f && day != duskWarnedDay && Crossed(prev, hour, (set - 1f + 24f) % 24f))
                    {
                        duskWarnedDay = day;
                        Messages.Message("TT_DuskWarn".Translate(Clock.TimeText(set)), MessageTypeDefOf.NeutralEvent, false);
                    }
                }
            }
            catch (Exception e) { Log.WarningOnce("[Tidy Toolbar] alarm: " + e.Message, 0x7d1ea1); }
        }

        // went from prev to now past the moment t (all in hours 0–24)
        private static bool Crossed(float prev, float now, float t)
        {
            if (prev < 0f) return false;
            if (now >= prev) return prev < t && t <= now;
            return t > prev || t <= now;   // past midnight
        }
    }

    public class Dialog_Alarm : Window
    {
        private int hour, min, action;

        public override Vector2 InitialSize => new Vector2(380f, 260f);

        public Dialog_Alarm()
        {
            TTSettings S = TidyToolbarMod.S;
            hour = S.alarmHour;
            min = S.alarmMin;
            action = S.alarmAction;
            doCloseX = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "TT_AlarmTitle".Translate());
            Text.Font = GameFont.Small;
            float y = r.y + 40f;
            Widgets.Label(new Rect(r.x, y + 4f, 120f, 26f), "TT_AlarmTime".Translate());
            if (Widgets.ButtonText(new Rect(r.x + 120f, y, 30f, 28f), "-")) hour = (hour + 23) % 24;
            Text.Font = GameFont.Medium;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(r.x + 152f, y - 2f, 90f, 32f), Clock.TimeText(hour + min / 60f));
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(r.x + 244f, y, 30f, 28f), "+")) hour = (hour + 1) % 24;
            if (Widgets.ButtonText(new Rect(r.x + 280f, y, 60f, 28f), ":" + ((min + 15) % 60).ToString("00"))) min = (min + 15) % 60;
            y += 40f;
            for (int i = 0; i < 3; i++)
                if (Widgets.RadioButtonLabeled(new Rect(r.x, y + i * 28f, r.width, 26f), ("TT_AlarmAction" + i).Translate(), action == i)) action = i;
            if (Widgets.ButtonText(new Rect(r.x, r.yMax - 32f, r.width, 32f), "TT_AlarmOk".Translate()))
            {
                TTSettings S = TidyToolbarMod.S;
                S.alarmHour = hour;
                S.alarmMin = min;
                S.alarmAction = action;
                S.alarmOn = true;
                Clock.Save();
                Close();
            }
        }
    }
}
