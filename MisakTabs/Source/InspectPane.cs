using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakTabs
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
        public static bool TabsPrefix(IInspectPane pane)
        {
            if (!On) return true;
            try
            {
                float top = pane.PaneTopY - 30f;
                float x = InspectPaneUtility.PaneWidthFor(pane) - 72f;
                float leftEdge = 0f;
                bool drewOpen = false;
                IEnumerable tabs = pane.CurTabs;
                if (tabs == null) return false;
                var list = new List<InspectTabBase>();
                foreach (object o in tabs) if (o is InspectTabBase t) list.Add(t);
                foreach (InspectTabBase tab in list)
                {
                    if (!tab.IsVisible) continue;
                    bool open = tab.GetType() == pane.OpenTabType;
                    if (!tab.Hidden)
                    {
                        Rect r = new Rect(x, top, 72f, 30f);
                        leftEdge = x;
                        if (TabButton(r, tab.labelKey.Translate(), open))
                            toggleTab.Invoke(null, new object[] { tab, pane });
                        if (!open && !tab.TutorHighlightTagClosed.NullOrEmpty())
                            UIHighlighter.HighlightOpportunity(r, tab.TutorHighlightTagClosed);
                        x -= 72f;
                    }
                    if (open)
                    {
                        tab.DoTabGUI();
                        pane.RecentHeight = 700f;
                        drewOpen = true;
                    }
                }
                if (drewOpen)
                    MUI.Fill(new Rect(0f, top, leftEdge, 30f), Look.Bg);
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] tab buttons: " + e, 0x4d5401);
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
