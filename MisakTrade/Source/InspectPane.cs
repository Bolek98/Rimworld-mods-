// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakTrade
{
    // ---------------------------------------------------------------------------
    // 0.12.0: the bottom-left info pane of a selected thing (colonist, animal, building) in our look:
    //  - dark card instead of the brown window,
    //  - the tab buttons above it (Health, Needs, Gear …) as our dark buttons, the open one green,
    //    labels never cut off ("Dynam…") – a smaller font when a label is long,
    //  - health, mood, schedule and area as rounded bars with colours by state,
    //  - the text below in our colours.
    // Everything still does what the game does (click on area = area menu, tabs open the same tabs).
    // ---------------------------------------------------------------------------
    public static class InspectPaneLook
    {
        private static MethodInfo toggleTab;
        private static Vector2 textScroll;
        private static Texture2D vanillaFill;
        private static Texture2D VanillaFill => vanillaFill ?? (vanillaFill = SolidColorMaterials.NewSolidColorTexture(new Color(0.07450981f, 0.08627451f, 9f / 85f, 1f)));

        public static bool On => MTMod.S != null && MTMod.S.paneLook;

        public static void Patch(Harmony h)
        {
            try
            {
                toggleTab = AccessTools.Method(typeof(InspectPaneUtility), "InterfaceToggleTab");
                h.Patch(AccessTools.Method(typeof(InspectPaneUtility), nameof(InspectPaneUtility.InspectPaneOnGUI)),
                    prefix: new HarmonyMethod(typeof(InspectPaneLook), nameof(PanePrefix)));
                h.Patch(AccessTools.Method(typeof(InspectPaneFiller), nameof(InspectPaneFiller.DoPaneContentsFor)),
                    prefix: new HarmonyMethod(typeof(InspectPaneLook), nameof(ContentsPrefix)));
                MethodInfo tabs = AccessTools.Method(typeof(InspectPaneUtility), "DoTabs");
                if (tabs != null && toggleTab != null)
                    h.Patch(tabs, prefix: new HarmonyMethod(typeof(InspectPaneLook), nameof(TabsPrefix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] info pane look: " + e.Message); }
        }

        // ---------------- the card behind the pane ----------------
        public static void PanePrefix(Rect inRect, IInspectPane pane)
        {
            if (!On || pane == null || !pane.AnythingSelected) return;
            try
            {
                MUI.Fill(inRect, Look.Bg);
                MUI.Frame(inRect, Look.Line);
                MUI.Fill(new Rect(inRect.x, inRect.y, inRect.width, 2f), Look.Green);
            }
            catch { }
        }

        // ---------------- tab buttons ----------------
        // 0.12.1: too many tabs for one row (many race mods add their own) – the ones that do not fit
        // go behind a "More ▾" button at the left end; the open tab always stays visible.
        // Without our look the vanilla buttons are kept and only an overflow is handled here.
        public static bool TabsPrefix(IInspectPane pane)
        {
            try
            {
                IEnumerable tabs = pane.CurTabs;
                if (tabs == null) return On ? false : true;
                var all = new List<InspectTabBase>();
                foreach (object o in tabs) if (o is InspectTabBase t && t.IsVisible) all.Add(t);
                var shown = all.Where(t => !t.Hidden).ToList();
                float paneW = InspectPaneUtility.PaneWidthFor(pane);
                int capacity = Math.Max(2, (int)(paneW / 72f));
                bool overflow = shown.Count > capacity;
                if (!On && !overflow) return true;   // vanilla draws as usual

                var row = shown;
                var rest = new List<InspectTabBase>();
                if (overflow)
                {
                    // keep capacity-1 buttons, the last slot is "More"; the open tab is always among the kept ones
                    row = shown.Take(capacity - 1).ToList();
                    InspectTabBase open = shown.FirstOrDefault(t => t.GetType() == pane.OpenTabType);
                    if (open != null && !row.Contains(open))
                        row[row.Count - 1] = open;
                    rest = shown.Where(t => !row.Contains(t)).ToList();
                }

                float top = pane.PaneTopY - 30f;
                float x = paneW - 72f;
                float leftEdge = 0f;
                foreach (InspectTabBase tab in row)
                {
                    bool open = tab.GetType() == pane.OpenTabType;
                    Rect r = new Rect(x, top, 72f, 30f);
                    leftEdge = x;
                    bool clicked;
                    if (On) clicked = TabButton(r, tab.labelKey.Translate(), open);
                    else { Text.Font = GameFont.Small; clicked = Widgets.ButtonText(r, tab.labelKey.Translate()); }
                    if (clicked)
                        toggleTab.Invoke(null, new object[] { tab, pane });
                    if (!open && !tab.TutorHighlightTagClosed.NullOrEmpty())
                        UIHighlighter.HighlightOpportunity(r, tab.TutorHighlightTagClosed);
                    x -= 72f;
                }
                if (rest.Count > 0)
                {
                    Rect r = new Rect(x, top, 72f, 30f);
                    leftEdge = x;
                    string label = "MT_TabsMore".Translate();
                    bool clicked;
                    if (On) clicked = TabButton(r, label, false);
                    else { Text.Font = GameFont.Small; clicked = Widgets.ButtonText(r, label); }
                    TooltipHandler.TipRegion(r, "MT_TabsMoreTip".Translate(rest.Count));
                    if (clicked)
                    {
                        var opts = new List<FloatMenuOption>();
                        foreach (InspectTabBase t in rest)
                        {
                            InspectTabBase tab = t;
                            opts.Add(new FloatMenuOption(tab.labelKey.Translate(), () => toggleTab.Invoke(null, new object[] { tab, pane })));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                }

                bool drewOpen = false;
                foreach (InspectTabBase tab in all)
                {
                    if (tab.GetType() != pane.OpenTabType) continue;
                    tab.DoTabGUI();
                    pane.RecentHeight = 700f;
                    drewOpen = true;
                    break;
                }
                if (drewOpen)
                {
                    if (On) MUI.Fill(new Rect(0f, top, leftEdge, 30f), Look.Bg);
                    else GUI.DrawTexture(new Rect(0f, top, leftEdge, 30f), VanillaFill);
                }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] tab buttons: " + e, 0x4d5401);
                return !On;
            }
            return false;
        }

        private static bool TabButton(Rect r, string label, bool open)
        {
            Rect b = r.ContractedBy(1f);
            bool over = Mouse.IsOver(b);
            MUI.Fill(b, open ? Look.GreenBg : over ? Look.Card2 : Look.Card);
            MUI.Frame(b, open ? Look.Green : over ? Look.Line : Look.Line);
            if (open) MUI.Fill(new Rect(b.x, b.yMax - 3f, b.width, 3f), Look.Green);
            // a long label gets the small font instead of being cut off
            Text.Font = GameFont.Small;
            if (Text.CalcSize(label).x > b.width - 6f) Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = open ? Look.Green : over ? Color.white : MUI.Text1;
            Widgets.Label(new Rect(b.x + 2f, b.y, b.width - 4f, b.height - (open ? 2f : 0f)), label.Truncate(b.width - 4f));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (over) Verse.Sound.MouseoverSounds.DoRegion(b);
            return Widgets.ButtonInvisible(b);
        }

        // ---------------- bars and text ----------------
        private struct Bar
        {
            public string label, tip;
            public float pct;
            public Color fill;
            public Action click;
            public Action hover;
        }

        public static bool ContentsPrefix(ISelectable sel, Rect rect)
        {
            if (!On) return true;
            Thing thing = sel as Thing;
            if (thing == null || thing.def.onlyShowInspectString) return true;
            try
            {
                Widgets.BeginGroup(rect);
                try
                {
                    var bars = Bars(thing);
                    float y = 3f;
                    if (bars.Count > 0)
                    {
                        float gap = 6f;
                        float w = Mathf.Min(120f, (rect.width - gap * (bars.Count - 1)) / bars.Count);
                        for (int i = 0; i < bars.Count; i++)
                            DrawBar(new Rect(i * (w + gap), y, w, 20f), bars[i]);
                        y += 26f;
                    }
                    Rect tr = new Rect(0f, y, rect.width, rect.height - y);
                    DrawText(sel, tr);
                }
                finally { Widgets.EndGroup(); }
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] info pane: " + e, 0x4d5402);
            }
            return false;
        }

        private static List<Bar> Bars(Thing t)
        {
            var list = new List<Bar>();
            Pawn p = t as Pawn;
            if (p == null)
            {
                if (t.def.useHitPoints && t.MaxHitPoints > 0)
                {
                    float hp = (float)t.HitPoints / t.MaxHitPoints;
                    list.Add(new Bar { pct = hp, label = t.HitPoints + " / " + t.MaxHitPoints, fill = HealthColor(hp), tip = "HitPointsBasic".Translate().CapitalizeFirst() + ": " + hp.ToStringPercent() });
                }
                return list;
            }
            float h = p.health.summaryHealth.SummaryHealthPercent;
            list.Add(new Bar { pct = h, label = HealthUtility.GetGeneralConditionLabel(p, true), fill = HealthColor(h), tip = "Health".Translate().CapitalizeFirst() + ": " + h.ToStringPercent() });
            if (p.IsGhoul && p.needs?.food != null)
                list.Add(new Bar { pct = p.needs.food.CurLevelPercentage, label = p.needs.food.CurCategory.GetLabel(), fill = Look.Orange, tip = p.needs.food.LabelCap + ": " + p.needs.food.CurLevelPercentage.ToStringPercent() });
            else if (p.needs?.mood != null)
            {
                float m = p.needs.mood.CurLevelPercentage;
                list.Add(new Bar { pct = m, label = p.needs.mood.MoodString.CapitalizeFirst(), fill = MoodColor(p, m), tip = p.needs.mood.LabelCap + ": " + m.ToStringPercent() });
            }
            if (p.timetable != null && !p.IsPrisonerOfColony)
            {
                TimeAssignmentDef a = p.timetable.CurrentAssignment;
                Color c = a.color;
                c.a = 1f;
                list.Add(new Bar { pct = 1f, label = a.LabelCap, fill = Color.Lerp(c, Look.Card, 0.45f), tip = a.description.NullOrEmpty() ? (string)a.LabelCap : a.LabelCap + "\n" + a.description });
            }
            if (p.playerSettings != null && p.playerSettings.SupportsAllowedAreas && p.Faction == Faction.OfPlayer && p.HostFaction == null && !(p.IsMutant && !p.mutant.Def.respectsAllowedArea))
            {
                Area area = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                Color c = area != null ? area.Color : Look.Line;
                c.a = 1f;
                Pawn pp = p;
                list.Add(new Bar
                {
                    pct = 1f,
                    label = AreaUtility.AreaAllowedLabel(p),
                    fill = Color.Lerp(c, Look.Card, area != null ? 0.45f : 0.2f),
                    tip = "AllowedArea".Translate().CapitalizeFirst(),
                    hover = () => pp.playerSettings?.AreaRestrictionInPawnCurrentMap?.MarkForDraw(),
                    click = () => AreaUtility.MakeAllowedAreaListFloatMenu(a => pp.playerSettings.AreaRestrictionInPawnCurrentMap = a, true, true, pp.MapHeld)
                });
            }
            if (p.needs?.energy != null)
                list.Add(new Bar { pct = p.needs.energy.CurLevelPercentage, label = "Energy".Translate(), fill = Look.Blue, tip = "Energy".Translate() + ": " + p.needs.energy.CurLevelPercentage.ToStringPercent() });
            return list;
        }

        private static Color HealthColor(float f)
        {
            if (f >= 0.85f) return Color.Lerp(Look.Green, Look.Card, 0.35f);
            if (f >= 0.5f) return Color.Lerp(Look.Orange, Look.Card, 0.3f);
            return Color.Lerp(Look.Red, Look.Card, 0.25f);
        }

        private static Color MoodColor(Pawn p, float m)
        {
            float minor = 0.35f, major = 0.2f;
            try
            {
                var b = p.mindState?.mentalBreaker;
                if (b != null) { minor = b.BreakThresholdMinor; major = b.BreakThresholdMajor; }
            }
            catch { }
            if (m < major) return Color.Lerp(Look.Red, Look.Card, 0.25f);
            if (m < minor) return Color.Lerp(Look.Orange, Look.Card, 0.3f);
            return Color.Lerp(Look.Green, Look.Card, 0.35f);
        }

        private static void DrawBar(Rect r, Bar b)
        {
            bool over = Mouse.IsOver(r);
            MUI.Fill(r, Look.Card, 5f);
            float pct = Mathf.Clamp01(b.pct);
            if (pct > 0f) MUI.Fill(new Rect(r.x, r.y, Mathf.Max(8f, r.width * pct), r.height), b.fill, 5f);
            if (over && b.click != null) MUI.Fill(r, new Color(1f, 1f, 1f, 0.08f), 5f);
            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.MiddleCenter;
            GUI.color = MUI.Text1;
            Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 8f, r.height), (b.label ?? "").Truncate(r.width - 8f));
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            if (!b.tip.NullOrEmpty()) TooltipHandler.TipRegion(r, b.tip);
            if (over) b.hover?.Invoke();
            if (b.click != null && Widgets.ButtonInvisible(r)) b.click();
        }

        private static void DrawText(ISelectable sel, Rect r)
        {
            string text;
            try
            {
                text = sel.GetInspectString();
                if (sel is Thing th)
                {
                    string low = th.GetInspectStringLowPriority();
                    if (!low.NullOrEmpty())
                    {
                        if (!text.NullOrEmpty()) text = text.TrimEndNewlines() + "\n";
                        text += low;
                    }
                }
            }
            catch (Exception e) { text = "GetInspectString exception on " + sel + ":\n" + e.Message; }
            if (text.NullOrEmpty()) return;
            GUI.color = MUI.Text1;
            Text.Font = GameFont.Small;
            Widgets.LabelScrollable(r, text.TrimEndNewlines(), ref textScroll, true);
            GUI.color = Color.white;
        }
    }
}

namespace MisakTrade
{
    // ---------------------------------------------------------------------------
    // 0.12.0: inside our windows the game's own buttons, drop-downs ("Any worker") and boxes
    // (the ingredient filter) are drawn in our look too – only the look, they work as before.
    // ---------------------------------------------------------------------------
    public static class OurLookScope
    {
        public static int depth;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawButtonGraphic)), prefix: new HarmonyMethod(typeof(OurLookScope), nameof(ButtonPrefix)));
                h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.DrawMenuSection)), prefix: new HarmonyMethod(typeof(OurLookScope), nameof(SectionPrefix)));
                // 0.12.0: storage settings (stockpiles, shelves, fridges …) in our look
                MethodInfo fill = AccessTools.Method(typeof(ITab_Storage), "FillTab");
                if (fill != null)
                    h.Patch(fill, prefix: new HarmonyMethod(typeof(OurLookScope), nameof(StoragePrefix)),
                        finalizer: new HarmonyMethod(typeof(OurLookScope), nameof(StorageFinalizer)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] our look in windows: " + e.Message); }
        }

        public static void StoragePrefix(ITab_Storage __instance, out bool __state)
        {
            __state = false;
            if (MTMod.S == null || !MTMod.S.paneLook) return;
            __state = true;
            depth++;
            try
            {
                Vector2 size = Traverse.Create(__instance).Field("size").GetValue<Vector2>();
                Rect r = new Rect(0f, 0f, size.x, size.y);
                MUI.Fill(r, Look.Bg);
                MUI.Frame(r, Look.Line);
                MUI.Fill(new Rect(0f, 0f, size.x, 2f), Look.Green);
                // the close cross was drawn before – put it back on top
                GUI.DrawTexture(new Rect(r.width - 22f, 4f, 18f, 18f), TexButton.CloseXSmall);
            }
            catch { }
        }

        public static Exception StorageFinalizer(Exception __exception, bool __state)
        {
            if (__state) depth--;
            return __exception;
        }

        public static bool ButtonPrefix(Rect rect)
        {
            if (depth <= 0) return true;
            bool over = Mouse.IsOver(rect);
            bool down = over && Input.GetMouseButton(0);
            MUI.Fill(rect, down ? Look.GreenBg : over ? Look.Line : Look.Card2);
            MUI.Frame(rect, over ? Look.Green : Look.Line);
            return false;
        }

        public static bool SectionPrefix(Rect rect)
        {
            if (depth <= 0) return true;
            MUI.Fill(rect, Look.Bg);
            MUI.Frame(rect, Look.Line);
            GUI.color = Color.white;
            return false;
        }
    }
}
