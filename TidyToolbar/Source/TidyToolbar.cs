using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace TidyToolbar
{
    // ---------------------------------------------------------------------------
    // Tidy Toolbar: the bottom bar with too many buttons from mods.
    // - Buttons are sorted into GROUPS: each group is one button on the bar that opens a menu
    //   with its buttons. By default the game's own buttons stay on the bar and every button
    //   added by a mod goes into the group "Mods".
    // - Right click any button (on the bar or in a group menu): put it on the bar, into another
    //   group or a new group, move it left/right.
    // - Every button gets the width its label needs (no more cut labels), buttons with an icon
    //   stay small, and what still does not fit goes into "More".
    // - Hotkeys of buttons inside groups still work.
    // ---------------------------------------------------------------------------
    public sealed class TTSettings : ModSettings
    {
        public List<string> order = new List<string>();     // user order (defNames); empty = the game's order
        public List<string> placeKeys = new List<string>(); // defName → where it is ("" = bar, else group name)
        public List<string> placeVals = new List<string>();
        public List<string> groups = new List<string>();    // user group names, in bar order
        public bool modsToGroup = true;                     // buttons from mods go into the group "Mods" by default
        public bool autoMove = true;                        // what does not fit goes into "More"
        public bool iconsSmall = true;                      // buttons with an icon take only the icon's width
        // the right side of the screen (alerts, letters, play settings)
        public bool tidyAlerts = true;                      // same alerts once with ×N, mute, the rest folded
        public int alertLimit = 10;                         // at most this many alerts shown (critical and high always)
        public List<string> mutedAlerts = new List<string>();
        public int letterDays = 3;                          // letters older than this go away by themselves (0 = never)
        public int letterMax = 15;                          // at most this many letters on the stack (0 = no limit)
        public bool collapsePlay = true;                    // the play settings icons behind one button
        // 1.1.0: the clock
        public bool clockOn = true;
        public int clockSize = 1;                           // 0 small, 1 medium, 2 large
        public bool clockCompact;
        public bool clockFree;
        public float clockX = -1f, clockY = -1f;
        public bool clockWeather = true;
        public bool clockHoliday = true;
        public bool clockColony;
        public bool alarmOn;
        public int alarmHour = 22, alarmMin;
        public int alarmAction = 1;                         // 0 sound only, 1 normal speed, 2 pause
        public bool duskWarn;
        // 1.1.0: buttons of the selection
        public float gizmoScale = 1f;
        public float gizmoGapX = 5f, gizmoGapY = 14f;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref order, "order", LookMode.Value);
            Scribe_Collections.Look(ref placeKeys, "placeKeys", LookMode.Value);
            Scribe_Collections.Look(ref placeVals, "placeVals", LookMode.Value);
            Scribe_Collections.Look(ref groups, "groups", LookMode.Value);
            Scribe_Values.Look(ref modsToGroup, "modsToGroup", true);
            Scribe_Values.Look(ref autoMove, "autoMove", true);
            Scribe_Values.Look(ref iconsSmall, "iconsSmall", true);
            Scribe_Values.Look(ref tidyAlerts, "tidyAlerts", true);
            Scribe_Values.Look(ref alertLimit, "alertLimit", 10);
            Scribe_Collections.Look(ref mutedAlerts, "mutedAlerts", LookMode.Value);
            Scribe_Values.Look(ref letterDays, "letterDays", 3);
            Scribe_Values.Look(ref letterMax, "letterMax", 15);
            Scribe_Values.Look(ref collapsePlay, "collapsePlay", true);
            Scribe_Values.Look(ref clockOn, "clockOn", true);
            Scribe_Values.Look(ref clockSize, "clockSize", 1);
            Scribe_Values.Look(ref clockCompact, "clockCompact", false);
            Scribe_Values.Look(ref clockFree, "clockFree", false);
            Scribe_Values.Look(ref clockX, "clockX", -1f);
            Scribe_Values.Look(ref clockY, "clockY", -1f);
            Scribe_Values.Look(ref clockWeather, "clockWeather", true);
            Scribe_Values.Look(ref clockHoliday, "clockHoliday", true);
            Scribe_Values.Look(ref clockColony, "clockColony", false);
            Scribe_Values.Look(ref alarmOn, "alarmOn", false);
            Scribe_Values.Look(ref alarmHour, "alarmHour", 22);
            Scribe_Values.Look(ref alarmMin, "alarmMin", 0);
            Scribe_Values.Look(ref alarmAction, "alarmAction", 1);
            Scribe_Values.Look(ref duskWarn, "duskWarn", false);
            Scribe_Values.Look(ref gizmoScale, "gizmoScale", 1f);
            Scribe_Values.Look(ref gizmoGapX, "gizmoGapX", 5f);
            Scribe_Values.Look(ref gizmoGapY, "gizmoGapY", 14f);
            if (mutedAlerts == null) mutedAlerts = new List<string>();
            if (order == null) order = new List<string>();
            if (placeKeys == null || placeVals == null || placeKeys.Count != placeVals.Count) { placeKeys = new List<string>(); placeVals = new List<string>(); }
            if (groups == null) groups = new List<string>();
        }
    }

    public sealed class TidyToolbarMod : Mod
    {
        public const string Version = "1.1.0";
        public static TTSettings S;
        private Vector2 scroll;

        public TidyToolbarMod(ModContentPack content) : base(content)
        {
            S = GetSettings<TTSettings>();
            try
            {
                var h = new Harmony("bolek98.tidytoolbar");
                h.Patch(AccessTools.Method(typeof(MainButtonsRoot), "DoButtons"),
                    prefix: new HarmonyMethod(typeof(Bar), nameof(Bar.Prefix)) { priority = Priority.Low });
                TidyHud.Patch(h);
                Clock.Patch(h);
                Log.Message("[Tidy Toolbar] v" + Version + " loaded.");
            }
            catch (Exception e)
            {
                Log.Error("[Tidy Toolbar] could not patch the toolbar: " + e);
            }
        }

        public override string SettingsCategory() => "Tidy Toolbar";

        // 1.1.0: the clock and the buttons of the selection
        private static void DrawClockSettings(Rect r)
        {
            var l = new Listing_Standard();
            l.Begin(r);
            l.CheckboxLabeled("TT_ClockOn".Translate(), ref S.clockOn, "TT_ClockOnTip".Translate());
            if (l.ButtonTextLabeled("TT_ClockSizeL".Translate(), ("TT_ClockSize" + S.clockSize).Translate()))
                S.clockSize = (S.clockSize + 1) % 3;
            l.CheckboxLabeled("TT_ClockCompactL".Translate(), ref S.clockCompact, "TT_ClockCompactTip".Translate());
            l.CheckboxLabeled("TT_ClockFreeL".Translate(), ref S.clockFree, "TT_ClockFreeTip".Translate());
            l.CheckboxLabeled("TT_ClockWeatherL".Translate(), ref S.clockWeather);
            l.CheckboxLabeled("TT_ClockHolidayL".Translate(), ref S.clockHoliday, "TT_ClockHolidayTip".Translate());
            l.CheckboxLabeled("TT_ClockColonyL".Translate(), ref S.clockColony, "TT_ClockColonyTip".Translate());
            l.GapLine();
            l.CheckboxLabeled("TT_AlarmOnL".Translate(Clock.TimeText(S.alarmHour + S.alarmMin / 60f)), ref S.alarmOn, "TT_AlarmOnTip".Translate());
            if (l.ButtonTextLabeled("TT_AlarmTime".Translate(), Clock.TimeText(S.alarmHour + S.alarmMin / 60f)))
                Find.WindowStack.Add(new Dialog_Alarm());
            l.CheckboxLabeled("TT_DuskWarnL".Translate(), ref S.duskWarn, "TT_DuskWarnTip".Translate());
            l.GapLine();
            l.Label("TT_GizmoTitle".Translate());
            S.gizmoScale = Mathf.Round(l.SliderLabeled("TT_GizmoScale".Translate((S.gizmoScale * 100f).ToString("0")), S.gizmoScale, 0.6f, 1f, tooltip: "TT_GizmoScaleTip".Translate()) * 20f) / 20f;
            S.gizmoGapX = Mathf.Round(l.SliderLabeled("TT_GizmoGapX".Translate(S.gizmoGapX.ToString("0")), S.gizmoGapX, 0f, 20f));
            S.gizmoGapY = Mathf.Round(l.SliderLabeled("TT_GizmoGapY".Translate(S.gizmoGapY.ToString("0")), S.gizmoGapY, 2f, 30f));
            if (l.ButtonText("TT_GizmoReset".Translate())) { S.gizmoScale = 1f; S.gizmoGapX = 5f; S.gizmoGapY = 14f; }
            l.End();
        }

        private static int page;

        public static void OpenPage(int p) => page = p;

        public override void DoSettingsWindowContents(Rect inRect)
        {
            // two pages: the bottom bar and the right side of the screen
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.y, 220f, 30f), "TT_PageBar".Translate(), true, true, page != 0)) page = 0;
            if (Widgets.ButtonText(new Rect(inRect.x + 228f, inRect.y, 260f, 30f), "TT_PageHud".Translate(), true, true, page != 1)) page = 1;
            if (Widgets.ButtonText(new Rect(inRect.x + 496f, inRect.y, 220f, 30f), "TT_PageClock".Translate(), true, true, page != 2)) page = 2;
            inRect.yMin += 40f;
            if (page == 1) { TidyHud.DrawSettings(inRect); return; }
            if (page == 2) { DrawClockSettings(inRect); return; }
            var top = new Listing_Standard();
            top.Begin(new Rect(inRect.x, inRect.y, inRect.width, 90f));
            top.CheckboxLabeled("TT_ModsToGroup".Translate(Bar.ModsGroup), ref S.modsToGroup, "TT_ModsToGroupTip".Translate());
            top.CheckboxLabeled("TT_AutoMove".Translate(), ref S.autoMove, "TT_AutoMoveTip".Translate());
            top.CheckboxLabeled("TT_IconsSmall".Translate(), ref S.iconsSmall, "TT_IconsSmallTip".Translate());
            top.End();
            GUI.color = new Color(0.7f, 0.72f, 0.76f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, inRect.y + 92f, inRect.width - 420f, 40f), "TT_Hint".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(inRect.xMax - 410f, inRect.y + 94f, 200f, 30f), "TT_NewGroup".Translate()))
                Bar.AskNewGroup(null);
            if (Widgets.ButtonText(new Rect(inRect.xMax - 200f, inRect.y + 94f, 200f, 30f), "TT_Reset".Translate()))
            {
                S.order.Clear();
                S.placeKeys.Clear();
                S.placeVals.Clear();
                S.groups.Clear();
            }

            List<MainButtonDef> all = Bar.Ordered(DefDatabase<MainButtonDef>.AllDefsListForReading.Where(d => d.buttonVisible).ToList());
            Rect outR = new Rect(inRect.x, inRect.y + 136f, inRect.width, inRect.height - 136f);
            const float rowH = 30f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, all.Count * rowH);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < all.Count; i++)
            {
                MainButtonDef d = all[i];
                Rect r = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 1) Widgets.DrawLightHighlight(r);
                float x = 4f;
                if (d.Icon != null) GUI.DrawTexture(new Rect(x, r.y + 3f, 24f, 24f), d.Icon);
                x += 30f;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.width - x - 330f, rowH), d.LabelCap + "  <color=#888888>(" + (d.modContentPack?.Name ?? "?") + ")</color>");
                Text.Anchor = TextAnchor.UpperLeft;
                string place = Bar.PlaceOf(d);
                if (Widgets.ButtonText(new Rect(r.xMax - 320f, r.y + 2f, 230f, 26f), place == "" ? "TT_OnBar".Translate().ToString() : "TT_InGroup".Translate(place).ToString()))
                    Find.WindowStack.Add(new FloatMenu(Bar.PlaceOptions(d)));
                if (i > 0 && Widgets.ButtonImage(new Rect(r.xMax - 74f, r.y + 3f, 24f, 24f), TexButton.ReorderUp))
                    Bar.Move(all, i, -1);
                if (i < all.Count - 1 && Widgets.ButtonImage(new Rect(r.xMax - 36f, r.y + 3f, 24f, 24f), TexButton.ReorderDown))
                    Bar.Move(all, i, +1);
            }
            Widgets.EndScrollView();
        }
    }

    public static class Bar
    {
        private static readonly AccessTools.FieldRef<MainButtonsRoot, List<MainButtonDef>> allRef =
            AccessTools.FieldRefAccess<MainButtonsRoot, List<MainButtonDef>>("allButtonsInOrder");

        private const float IconW = 44f;
        private const float MinText = 56f;
        private const float Pad = 22f;
        private static bool failed;

        public static string ModsGroup => "TT_Mods".Translate();
        public static string MoreGroup => "TT_More".Translate();
        private static TTSettings S => TidyToolbarMod.S;

        // ---- where a button lives ----
        public static bool IsGameButton(MainButtonDef d) => d.modContentPack == null || d.modContentPack.IsOfficialMod;

        public static string PlaceOf(MainButtonDef d)
        {
            int i = S.placeKeys.IndexOf(d.defName);
            if (i >= 0) return S.placeVals[i];
            if (S.modsToGroup && !IsGameButton(d) && !IsMenu(d)) return ModsGroup;
            return "";
        }

        public static void SetPlace(MainButtonDef d, string place)
        {
            int i = S.placeKeys.IndexOf(d.defName);
            if (i >= 0) S.placeVals[i] = place;
            else { S.placeKeys.Add(d.defName); S.placeVals.Add(place); }
            if (place != "" && place != ModsGroup && place != MoreGroup && !S.groups.Contains(place)) S.groups.Add(place);
            // groups nobody uses any more disappear
            S.groups.RemoveAll(g => !S.placeVals.Contains(g));
            Save();
        }

        public static List<string> AllGroups()
        {
            var g = new List<string>(S.groups);
            if (!g.Contains(ModsGroup)) g.Add(ModsGroup);
            return g;
        }

        public static List<FloatMenuOption> PlaceOptions(MainButtonDef d)
        {
            var opts = new List<FloatMenuOption>();
            string cur = PlaceOf(d);
            if (cur != "") opts.Add(new FloatMenuOption("TT_ToBar".Translate(), () => SetPlace(d, "")));
            foreach (string g in AllGroups().Concat(new[] { MoreGroup }))
                if (g != cur)
                {
                    string gg = g;
                    opts.Add(new FloatMenuOption("TT_ToGroup".Translate(gg), () => SetPlace(d, gg)));
                }
            opts.Add(new FloatMenuOption("TT_NewGroup".Translate(), () => AskNewGroup(d)));
            return opts;
        }

        public static void AskNewGroup(MainButtonDef d)
        {
            Find.WindowStack.Add(new Dialog_GroupName(name =>
            {
                if (name.NullOrEmpty()) return;
                if (!S.groups.Contains(name)) S.groups.Add(name);
                if (d != null) SetPlace(d, name);
                else Save();
            }));
        }

        // ---- order ----
        // the buttons in the user's order; buttons the user never moved keep their place after the
        // button that comes before them in the game's order
        public static List<MainButtonDef> Ordered(List<MainButtonDef> defs)
        {
            var game = defs.OrderBy(d => d.order).ToList();
            List<string> order = S.order;
            if (order.Count == 0)
                return game;
            var result = new List<MainButtonDef>();
            foreach (string n in order)
            {
                MainButtonDef d = game.FirstOrDefault(x => x.defName == n);
                if (d != null) result.Add(d);
            }
            for (int i = 0; i < game.Count; i++)
            {
                if (result.Contains(game[i])) continue;
                int at = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int k = result.IndexOf(game[j]);
                    if (k >= 0) { at = k + 1; break; }
                }
                result.Insert(at, game[i]);
            }
            return result;
        }

        public static void Move(List<MainButtonDef> list, int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= list.Count) return;
            MainButtonDef t = list[i];
            list[i] = list[j];
            list[j] = t;
            S.order = list.Select(d => d.defName).ToList();
            Save();
        }

        private static void Save()
        {
            try { LoadedModManager.GetMod<TidyToolbarMod>()?.WriteSettings(); } catch { }
        }

        private static bool IsMenu(MainButtonDef d) => d == MainButtonDefOf.Menu;

        private static float Want(MainButtonDef d, bool shortened)
        {
            if (d.Icon != null && (S.iconsSmall || d.minimized))
                return IconW;
            Text.Font = GameFont.Small;
            float w = shortened ? d.ShortenedLabelCapWidth : d.LabelCapWidth;
            return Mathf.Max(MinText, w + Pad);
        }

        private static float GroupWant(string g)
        {
            Text.Font = GameFont.Small;
            return Mathf.Max(MinText, Text.CalcSize(g + " (99)").x + Pad);
        }

        // ---- drawing ----
        public static bool Prefix(MainButtonsRoot __instance)
        {
            if (failed) return true;
            try
            {
                Draw(__instance);
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[Tidy Toolbar] drawing the toolbar failed, the game's toolbar is used instead: " + e);
                return true;
            }
        }

        // one thing on the bar: a button or a group
        private sealed class Item
        {
            public MainButtonDef def;
            public string group;
            public List<MainButtonDef> members;
            public float w;
        }

        private static void Draw(MainButtonsRoot root)
        {
            List<MainButtonDef> all = allRef(root);
            List<MainButtonDef> visible = Ordered(all.Where(d => d.Worker.Visible).ToList());
            var bar = new List<MainButtonDef>();
            var inGroup = new Dictionary<string, List<MainButtonDef>>();
            foreach (MainButtonDef d in visible)
            {
                string p = IsMenu(d) ? "" : PlaceOf(d);
                if (p == "") { bar.Add(d); continue; }
                if (!inGroup.TryGetValue(p, out var l)) inGroup[p] = l = new List<MainButtonDef>();
                l.Add(d);
            }
            if (!inGroup.TryGetValue(MoreGroup, out var more)) more = new List<MainButtonDef>();

            float screen = UI.screenWidth;
            List<string> groupNames = AllGroups().Where(g => inGroup.ContainsKey(g)).ToList();
            bool shortened = false;
            Func<float> need = () => bar.Sum(d => Want(d, shortened)) + groupNames.Sum(GroupWant) + (more.Count > 0 ? GroupWant(MoreGroup) : 0f);
            if (need() > screen) shortened = true;
            if (S.autoMove)
                while (need() > screen && bar.Count > 1)
                {
                    int k = bar.FindLastIndex(d => !IsMenu(d));
                    if (k < 0) break;
                    more.Insert(0, bar[k]);
                    bar.RemoveAt(k);
                }

            // the items in bar order: buttons, then the groups and "More" right before the main menu
            var items = new List<Item>();
            foreach (MainButtonDef d in bar)
                if (!IsMenu(d)) items.Add(new Item { def = d, w = Want(d, shortened) });
            foreach (string g in groupNames)
                items.Add(new Item { group = g, members = inGroup[g], w = GroupWant(g) });
            if (more.Count > 0)
                items.Add(new Item { group = MoreGroup, members = more, w = GroupWant(MoreGroup) });
            foreach (MainButtonDef d in bar)
                if (IsMenu(d)) items.Add(new Item { def = d, w = Want(d, shortened) });

            float used = items.Sum(it => it.w);
            float left = screen - used;
            if (left > 0f)
            {
                var grow = items.Where(it => it.w > IconW).ToList();
                if (grow.Count == 0) grow = items;
                float total = grow.Sum(it => it.w);
                foreach (Item it in grow) it.w += left * it.w / Mathf.Max(1f, total);
            }
            else if (left < 0f)
            {
                float f = screen / Mathf.Max(1f, used);
                foreach (Item it in items) it.w *= f;
            }

            GUI.color = Color.white;
            float y = UI.screenHeight - 35;
            float x = 0f;
            for (int i = 0; i < items.Count; i++)
            {
                Item it = items[i];
                float bw = i == items.Count - 1 ? screen - Mathf.Round(x) : Mathf.Round(x + it.w) - Mathf.Round(x);
                Rect r = new Rect(Mathf.Round(x), y, bw, 36f);
                if (it.def != null)
                {
                    if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(r))
                    {
                        Event.current.Use();
                        Find.WindowStack.Add(new FloatMenu(ButtonMenu(it.def, visible)));
                    }
                    it.def.Worker.DoButton(r);
                }
                else
                    DrawGroup(r, it.group, it.members, visible);
                x += it.w;
            }
        }

        private static void DrawGroup(Rect r, string name, List<MainButtonDef> members, List<MainButtonDef> visible)
        {
            bool open = members.Any(d => Find.MainTabsRoot.OpenTab == d);
            if (open) Widgets.DrawHighlight(r);
            bool right = Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(r);
            if (right) Event.current.Use();
            if (Widgets.ButtonTextSubtle(r, name + " (" + members.Count + ")", 0f, -1f, SoundDefOf.Mouseover_Category) || right)
            {
                var opts = new List<FloatMenuOption>();
                foreach (MainButtonDef d in members)
                {
                    MainButtonDef def = d;
                    string label = def.LabelCap;
                    if (def.Worker.Disabled) { opts.Add(new FloatMenuOption(label, null, def.Icon ?? BaseContent.ClearTex, Color.white)); continue; }
                    opts.Add(new FloatMenuOption(label, () => def.Worker.InterfaceTryActivate(), def.Icon ?? BaseContent.ClearTex, Color.white));
                }
                // 1.1.0: a big group gets a search window
                if (members.Count >= 6)
                    opts.Insert(0, new FloatMenuOption("TT_SearchOpen".Translate(), () => Find.WindowStack.Add(new Window_ButtonSearch(members))));
                if (right)
                {
                    // right click on a group: move its buttons somewhere
                    opts.Clear();
                    foreach (MainButtonDef d in members)
                    {
                        MainButtonDef def = d;
                        opts.Add(new FloatMenuOption("TT_WhereTo".Translate(def.LabelCap), () => Find.WindowStack.Add(new FloatMenu(ButtonMenu(def, visible))), def.Icon ?? BaseContent.ClearTex, Color.white));
                    }
                }
                opts.Add(new FloatMenuOption("TT_Edit".Translate(), OpenSettings));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(r, "TT_GroupTip".Translate(name, string.Join(", ", members.Select(d => d.LabelCap.ToString()).ToArray())));
        }

        private static List<FloatMenuOption> ButtonMenu(MainButtonDef d, List<MainButtonDef> visible)
        {
            var opts = new List<FloatMenuOption>();
            if (!IsMenu(d))
                opts.AddRange(PlaceOptions(d));
            var all = Ordered(DefDatabase<MainButtonDef>.AllDefsListForReading.Where(x => x.buttonVisible).ToList());
            int i = all.IndexOf(d);
            if (i > 0)
                opts.Add(new FloatMenuOption("TT_Left".Translate(), () => MoveVisible(all, d, -1, visible)));
            if (i >= 0 && i < all.Count - 1)
                opts.Add(new FloatMenuOption("TT_Right".Translate(), () => MoveVisible(all, d, +1, visible)));
            opts.Add(new FloatMenuOption("TT_Edit".Translate(), OpenSettings));
            return opts;
        }

        // moves past the next visible button in the same place (bar or the same group)
        private static void MoveVisible(List<MainButtonDef> all, MainButtonDef d, int dir, List<MainButtonDef> visible)
        {
            string place = PlaceOf(d);
            int i = all.IndexOf(d);
            int j = i + dir;
            while (j >= 0 && j < all.Count && (!visible.Contains(all[j]) || PlaceOf(all[j]) != place)) j += dir;
            if (j < 0 || j >= all.Count) return;
            all.RemoveAt(i);
            all.Insert(j, d);
            S.order = all.Select(x => x.defName).ToList();
            Save();
        }

        private static void OpenSettings()
        {
            Mod mod = LoadedModManager.GetMod<TidyToolbarMod>();
            if (mod != null) Find.WindowStack.Add(new Dialog_ModSettings(mod));
        }
    }

    // a small window that asks for the name of a new group
    public sealed class Dialog_GroupName : Window
    {
        private readonly Action<string> done;
        private string name = "";
        private bool focused;

        public Dialog_GroupName(Action<string> done)
        {
            this.done = done;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(360f, 150f);

        public override void DoWindowContents(Rect inRect)
        {
            Widgets.Label(new Rect(0f, 0f, inRect.width, 28f), "TT_GroupNameAsk".Translate());
            GUI.SetNextControlName("TT_GroupName");
            name = Widgets.TextField(new Rect(0f, 32f, inRect.width, 32f), name);
            if (!focused) { GUI.FocusControl("TT_GroupName"); focused = true; }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (Widgets.ButtonText(new Rect(inRect.width - 120f, inRect.height - 34f, 120f, 34f), "OK") || enter)
            {
                if (enter) Event.current.Use();
                string n = name.Trim();
                Close();
                if (n.Length > 0) done(n);
            }
        }
    }

    // 1.1.0: the buttons of a group with a search field (type, Enter opens the first)
    public sealed class Window_ButtonSearch : Window
    {
        private readonly List<MainButtonDef> members;
        private string search = "";
        private Vector2 scroll;
        private bool focused;

        public override Vector2 InitialSize => new Vector2(320f, Mathf.Min(560f, 110f + members.Count * 30f));

        public Window_ButtonSearch(List<MainButtonDef> list)
        {
            members = list.ToList();
            closeOnClickedOutside = true;
            doCloseX = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            layer = WindowLayer.GameUI;
        }

        protected override void SetInitialSizeAndPosition()
        {
            Vector2 s = InitialSize;
            float x = Mathf.Clamp(UI.MousePositionOnUIInverted.x - s.x / 2f, 0f, UI.screenWidth - s.x);
            windowRect = new Rect(x, UI.screenHeight - 35f - s.y, s.x, s.y);
        }

        public override void DoWindowContents(Rect r)
        {
            GUI.SetNextControlName("TT_Search");
            search = Widgets.TextField(new Rect(r.x, r.y, r.width - 30f, 28f), search ?? "");
            if (!focused) { GUI.FocusControl("TT_Search"); focused = true; }
            List<MainButtonDef> list = members.Where(d => search.NullOrEmpty() || d.LabelCap.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            if (Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter) && list.Count > 0)
            {
                Event.current.Use();
                Open(list[0]);
                return;
            }
            Rect outR = new Rect(r.x, r.y + 34f, r.width, r.height - 34f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * 30f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < list.Count; i++)
            {
                MainButtonDef d = list[i];
                Rect row = new Rect(0f, i * 30f, view.width, 28f);
                Widgets.DrawHighlightIfMouseover(row);
                if (d.Icon != null) GUI.DrawTexture(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), d.Icon);
                Text.Anchor = TextAnchor.MiddleLeft;
                GUI.color = d.Worker.Disabled ? Color.gray : Color.white;
                Widgets.Label(new Rect(row.x + 32f, row.y, row.width - 32f, row.height), d.LabelCap);
                GUI.color = Color.white;
                Text.Anchor = TextAnchor.UpperLeft;
                if (!d.Worker.Disabled && Widgets.ButtonInvisible(row)) { Open(d); break; }
            }
            Widgets.EndScrollView();
        }

        private void Open(MainButtonDef d)
        {
            Close();
            d.Worker.InterfaceTryActivate();
        }
    }
}
