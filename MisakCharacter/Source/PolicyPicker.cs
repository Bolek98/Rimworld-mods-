// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakCharacter
{
    // ---------------------------------------------------------------------------
    // 0.13.0: Assign tab – outfit, food and drug policy (and the allowed area once there are more
    // than four areas) in our window: search and a short preview of what each policy allows,
    // "Manage …" kept. Dragging over the cells still paints the same policy on other colonists.
    // ---------------------------------------------------------------------------
    public static class PolicyPickerPatch
    {
        public const int AreaThreshold = 4;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PawnColumnWorker_Outfit), nameof(PawnColumnWorker_Outfit.DoCell)), prefix: new HarmonyMethod(typeof(PolicyPickerPatch), nameof(OutfitCell)));
            h.Patch(AccessTools.Method(typeof(PawnColumnWorker_FoodRestriction), nameof(PawnColumnWorker_FoodRestriction.DoCell)), prefix: new HarmonyMethod(typeof(PolicyPickerPatch), nameof(FoodCell)));
            h.Patch(AccessTools.Method(typeof(PawnColumnWorker_DrugPolicy), nameof(PawnColumnWorker_DrugPolicy.DoCell)), prefix: new HarmonyMethod(typeof(PolicyPickerPatch), nameof(DrugCell)));
            h.Patch(AccessTools.Method(typeof(AreaAllowedGUI), nameof(AreaAllowedGUI.DoAllowedAreaSelectors)), prefix: new HarmonyMethod(typeof(PolicyPickerPatch), nameof(AreaCell)));
        }

        private static bool On => MTMod.S != null && MTMod.S.policyPicker;

        public static bool OutfitCell(Rect rect, Pawn pawn)
        {
            if (!On || pawn?.outfits == null || pawn.IsQuestLodger()) return true;
            try
            {
                Rect r = rect.ContractedBy(0f, 2f);
                bool forced = pawn.outfits.forcedHandler.SomethingIsForced;
                Rect left = r, right = default(Rect);
                if (forced) r.SplitVerticallyWithMargin(out left, out right, 4f);
                ApparelPolicy cur = pawn.outfits.CurrentApparelPolicy;
                PolicyCell.Do(left, pawn, PolicyKind.Outfit, cur, cur?.label ?? "-",
                    (p, v) => { if (p.outfits != null && !p.IsQuestLodger()) p.outfits.CurrentApparelPolicy = (ApparelPolicy)v; });
                if (forced)
                {
                    if (Widgets.ButtonText(right, "ClearForcedApparel".Translate())) pawn.outfits.forcedHandler.Reset();
                    if (Mouse.IsOver(right))
                        TooltipHandler.TipRegion(right, "ForcedApparel".Translate() + ":\n\n   " + string.Join("\n   ", pawn.outfits.forcedHandler.ForcedApparel.Select(a => a.LabelCap.ToString())));
                }
                return false;
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] outfit picker: " + e, 0x4d5421); return true; }
        }

        public static bool FoodCell(Rect rect, Pawn pawn)
        {
            if (!On || pawn?.foodRestriction == null) return true;
            try
            {
                FoodPolicy cur = pawn.foodRestriction.CurrentFoodPolicy;
                PolicyCell.Do(rect.ContractedBy(0f, 2f), pawn, PolicyKind.Food, cur, cur?.label ?? "-",
                    (p, v) => { if (p.foodRestriction != null) p.foodRestriction.CurrentFoodPolicy = (FoodPolicy)v; });
                return false;
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] food picker: " + e, 0x4d5422); return true; }
        }

        public static bool DrugCell(Rect rect, Pawn pawn)
        {
            if (!On || pawn?.drugs?.CurrentPolicy == null) return true;
            try
            {
                string text = pawn.drugs.CurrentPolicy.label;
                if (pawn.story?.traits != null)
                {
                    Trait t = pawn.story.traits.GetTrait(TraitDefOf.DrugDesire);
                    Gene_ChemicalDependency gene;
                    if (t != null) text += " (" + t.Label + ")";
                    else if (ModsConfig.BiotechActive && PawnUtility.TryGetChemicalDependencyGene(pawn, out gene)) text += " (" + gene.Label + ")";
                }
                PolicyCell.Do(rect.ContractedBy(0f, 2f), pawn, PolicyKind.Drugs, pawn.drugs.CurrentPolicy, text,
                    (p, v) => { if (p.drugs != null) p.drugs.CurrentPolicy = (DrugPolicy)v; });
                return false;
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] drug picker: " + e, 0x4d5423); return true; }
        }

        public static bool AreaCell(Rect rect, Pawn p)
        {
            if (!On || p?.playerSettings == null || Find.CurrentMap == null) return true;
            try
            {
                int n = Find.CurrentMap.areaManager.AllAreas.Count(a => a.AssignableAsAllowed());
                if (n <= AreaThreshold) return true;   // few areas: the game's coloured boxes are clearer
                Area cur = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                PolicyCell.Do(rect, p, PolicyKind.Area, cur, AreaUtility.AreaAllowedLabel_Area(cur),
                    (q, v) => { if (q.playerSettings != null && q.playerSettings.SupportsAllowedAreas) q.playerSettings.AreaRestrictionInPawnCurrentMap = (Area)v; },
                    cur?.Color);
                if (Mouse.IsOver(rect)) cur?.MarkForDraw();
                return false;
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] area picker: " + e, 0x4d5424); return true; }
        }
    }

    public enum PolicyKind { Outfit, Food, Drugs, Area }

    // a cell in the Assign tab: click → our picker, press and drag over other rows → paint the same value
    public static class PolicyCell
    {
        private static Pawn pressPawn;
        private static PolicyKind pressKind;
        private static object pressValue;
        private static bool dragged;

        public static void Do(Rect r, Pawn pawn, PolicyKind kind, object current, string label, Action<Pawn, object> set, Color? swatch = null)
        {
            Event ev = Event.current;
            bool over = Mouse.IsOver(r);
            MUI.Fill(r, over ? Look.Card2 : Look.Card);
            MUI.Frame(r, pressPawn != null && pressKind == kind && over ? Look.Green : Look.Line);
            float x = r.x + 6f;
            if (swatch.HasValue)
            {
                MUI.Fill(new Rect(x, r.y + (r.height - 12f) / 2f, 12f, 12f), swatch.Value);
                x += 18f;
            }
            Look.Text(new Rect(x, r.y, r.xMax - x - 18f, r.height), label.Truncate(r.xMax - x - 18f), MUI.Text1, GameFont.Small);
            Look.Text(new Rect(r.xMax - 16f, r.y, 14f, r.height), "▾", MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(r, label + "\n\n" + "MT_PB_CellTip".Translate());

            if (over && ev.type == EventType.MouseDown && ev.button == 0)
            {
                pressPawn = pawn;
                pressKind = kind;
                pressValue = current;
                dragged = false;
                ev.Use();
                return;
            }
            if (pressPawn != null && pressKind == kind && over && pawn != pressPawn && Input.GetMouseButton(0))
            {
                if (!ReferenceEquals(current, pressValue))
                {
                    set(pawn, pressValue);
                    SoundDefOf.Designate_DragStandard_Changed_NoCam.PlayOneShotOnCamera();
                }
                dragged = true;
            }
            if (ev.rawType == EventType.MouseUp && ev.button == 0 && pressPawn == pawn && pressKind == kind)
            {
                bool open = !dragged && over;
                pressPawn = null;
                pressValue = null;
                if (open)
                {
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    Find.WindowStack.Add(new Dialog_PolicyPicker(pawn, kind, current, set));
                }
            }
            if (pressPawn != null && !Input.GetMouseButton(0) && ev.type == EventType.Repaint) pressPawn = null;
        }
    }

    public class Dialog_PolicyPicker : Window
    {
        private class Entry
        {
            public object value;
            public string label, line2, tip, search;
            public Color? swatch;
            public int count;
        }

        private readonly Pawn pawn;
        private readonly PolicyKind kind;
        private readonly object current;
        private readonly Action<Pawn, object> set;
        private readonly List<Entry> all = new List<Entry>();
        private string search = "";
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(860f, 600f);

        public Dialog_PolicyPicker(Pawn pawn, PolicyKind kind, object current, Action<Pawn, object> set)
        {
            this.pawn = pawn;
            this.kind = kind;
            this.current = current;
            this.set = set;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
            Build();
        }

        private void Build()
        {
            switch (kind)
            {
                case PolicyKind.Outfit:
                    foreach (ApparelPolicy o in Current.Game.outfitDatabase.AllOutfits) all.Add(FromFilter(o, o.label, o.filter, true));
                    break;
                case PolicyKind.Food:
                    foreach (FoodPolicy f in Current.Game.foodRestrictionDatabase.AllFoodRestrictions) all.Add(FromFilter(f, f.label, f.filter, false));
                    break;
                case PolicyKind.Drugs:
                    foreach (DrugPolicy d in Current.Game.drugPolicyDatabase.AllPolicies) all.Add(FromDrugs(d));
                    break;
                case PolicyKind.Area:
                    all.Add(new Entry { value = null, label = AreaUtility.AreaAllowedLabel_Area(null), line2 = "MT_PB_AreaAll".Translate(), search = "" });
                    Map map = pawn.MapHeld ?? Find.CurrentMap;
                    if (map != null)
                        foreach (Area a in map.areaManager.AllAreas.Where(a => a.AssignableAsAllowed()))
                            all.Add(new Entry { value = a, label = a.Label, swatch = a.Color, count = a.TrueCount, line2 = "MT_PB_AreaCells".Translate(a.TrueCount), search = a.Label });
                    break;
            }
        }

        private static Entry FromFilter(object policy, string label, ThingFilter filter, bool apparel)
        {
            var e = new Entry { value = policy, label = label, search = label };
            try
            {
                var defs = filter.AllowedThingDefs.ToList();
                e.count = defs.Count;
                var names = defs.Select(d => d.label).Where(s => !s.NullOrEmpty()).OrderBy(s => s).ToList();
                e.line2 = "MT_PB_Allows".Translate(defs.Count) + (names.Count > 0 ? ": " + string.Join(", ", names.Take(6)) + (names.Count > 6 ? " …" : "") : "");
                var sb = new System.Text.StringBuilder();
                sb.Append(label).Append("\n\n").Append("MT_PB_Allows".Translate(defs.Count).ToString());
                if (apparel)
                {
                    QualityRange q = filter.AllowedQualityLevels;
                    FloatRange hp = filter.AllowedHitPointsPercents;
                    sb.Append('\n').Append("MT_PB_Quality".Translate(q.min.GetLabel(), q.max.GetLabel()).ToString());
                    sb.Append('\n').Append("MT_PB_HitPoints".Translate(hp.min.ToStringPercent(), hp.max.ToStringPercent()).ToString());
                }
                if (names.Count > 0) sb.Append("\n\n").Append(string.Join(", ", names.Take(40))).Append(names.Count > 40 ? " …" : "");
                e.tip = sb.ToString();
                e.search += " " + string.Join(" ", names);
            }
            catch { e.line2 = ""; e.tip = label; }
            return e;
        }

        private static Entry FromDrugs(DrugPolicy d)
        {
            var e = new Entry { value = d, label = d.label, search = d.label };
            try
            {
                var sched = new List<string>();
                var joy = new List<string>();
                var add = new List<string>();
                for (int i = 0; i < d.Count; i++)
                {
                    DrugPolicyEntry x = d[i];
                    if (x?.drug == null) continue;
                    if (x.allowScheduled) sched.Add(x.drug.label);
                    if (x.allowedForJoy) joy.Add(x.drug.label);
                    if (x.allowedForAddiction) add.Add(x.drug.label);
                }
                e.count = sched.Count + joy.Count;
                e.line2 = "MT_PB_DrugLine".Translate(sched.Count, joy.Count, add.Count);
                var sb = new System.Text.StringBuilder(d.label);
                if (sched.Count > 0) sb.Append("\n\n").Append("MT_PB_DrugSched".Translate().ToString()).Append(": ").Append(string.Join(", ", sched));
                if (joy.Count > 0) sb.Append("\n\n").Append("MT_PB_DrugJoy".Translate().ToString()).Append(": ").Append(string.Join(", ", joy));
                if (add.Count > 0) sb.Append("\n\n").Append("MT_PB_DrugAdd".Translate().ToString()).Append(": ").Append(string.Join(", ", add));
                e.tip = sb.ToString();
                e.search += " " + string.Join(" ", sched.Concat(joy));
            }
            catch { e.line2 = ""; e.tip = d.label; }
            return e;
        }

        public override void DoWindowContents(Rect inRect)
        {
            PickUI.Frame(this);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), ("MT_PB_PT_" + kind).Translate(pawn.LabelShortCap), MUI.Text1, GameFont.Medium);
            y += 38f;
            search = PickUI.Search(new Rect(r.x, y, 300f, 28f), "MT_PB_PolSearch", search, "MT_PG_Search".Translate());
            if (Look.Button(new Rect(r.xMax - 200f, y, 200f, 28f), kind == PolicyKind.Area ? "ManageAreas".Translate() : "AssignTabEdit".Translate() + " …"))
            {
                Close();
                OpenManage();
                return;
            }
            y += 38f;
            string q = (search ?? "").Trim();
            var shown = all.Where(e => PickUI.Matches(q, e.search)).ToList();
            int picked = PickUI.Grid(new Rect(r.x, y, r.width, r.yMax - y), ref scroll, shown.Count, 390f, 60f, (c, i) => DrawCard(c, shown[i]), "MT_PG_None".Translate());
            if (picked >= 0)
            {
                set(pawn, shown[picked].value);
                SoundDefOf.Click.PlayOneShotOnCamera();
                if (kind == PolicyKind.Drugs) PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.DrugPolicies, KnowledgeAmount.Total);
                Close();
            }
        }

        private void OpenManage()
        {
            switch (kind)
            {
                case PolicyKind.Outfit: Find.WindowStack.Add(new Dialog_ManageApparelPolicies(current as ApparelPolicy)); break;
                case PolicyKind.Food: Find.WindowStack.Add(new Dialog_ManageFoodPolicies(current as FoodPolicy)); break;
                case PolicyKind.Drugs: Find.WindowStack.Add(new Dialog_ManageDrugPolicies(current as DrugPolicy)); break;
                case PolicyKind.Area:
                    Map map = pawn.MapHeld ?? Find.CurrentMap;
                    if (map != null) Find.WindowStack.Add(new Dialog_ManageAreas(map));
                    break;
            }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool cur = ReferenceEquals(e.value, current);
            bool over = Mouse.IsOver(c);
            MUI.Fill(c, cur ? Look.GreenBg : over ? Look.Card2 : Look.Card);
            MUI.Frame(c, cur ? Look.Green : Look.Line);
            float tx = c.x + 10f;
            if (e.swatch.HasValue)
            {
                MUI.Fill(new Rect(c.x + 10f, c.y + 10f, 20f, 20f), e.swatch.Value);
                tx += 28f;
            }
            float tw = c.xMax - tx - 8f;
            Look.Text(new Rect(tx, c.y + 4f, tw, 24f), e.label.Truncate(tw), cur ? Look.Green : MUI.Text1);
            Look.Text(new Rect(tx, c.y + 28f, tw, 26f), (e.line2 ?? "").Truncate(tw * 1.9f), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            if (cur) PickUI.Badge(new Rect(c.xMax - 70f, c.y + 6f, 0f, 16f), "MT_PB_Current".Translate(), Look.Green);
            if (e.value is Area a && over) a.MarkForDraw();
            if (!e.tip.NullOrEmpty()) TooltipHandler.TipRegion(c, e.tip);
            return Widgets.ButtonInvisible(c);
        }
    }
}
