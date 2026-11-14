// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakHealth
{
    // ---------------------------------------------------------------------------
    // 0.13.0: group B pickers – owner of a bed / throne / spot, area and policies in the
    // Assign tab, research projects. Same look as the plant picker. Patched here on startup
    // (own Harmony id) so nothing else has to be touched.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class PickersB
    {
        static PickersB()
        {
            var h = new Harmony("misak.health.pickersB");
            try { OwnerPickerPatch.Patch(h); } catch (Exception e) { Log.Warning("[Clear Tabs] owner picker: " + e.Message); }
            try { PolicyPickerPatch.Patch(h); } catch (Exception e) { Log.Warning("[Clear Tabs] policy picker: " + e.Message); }
            try { ResearchPickerPatch.Patch(h); } catch (Exception e) { Log.Warning("[Clear Tabs] research picker: " + e.Message); }
        }
    }

    // shared drawing for the group B pickers (the plant picker's look)
    public static class PickUI
    {
        public static void Frame(Window w)
        {
            Rect full = new Rect(0f, 0f, w.windowRect.width, w.windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
        }

        // a chip (group tab or sort button); returns true when clicked
        public static bool Chip(ref float x, float y, string label, bool on, float h = 28f, float pad = 24f)
        {
            Text.Font = GameFont.Small;
            float w = Text.CalcSize(label).x + pad;
            Rect b = new Rect(x, y, w, h);
            MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
            MUI.Frame(b, on ? Look.Green : Look.Line);
            Look.Text(b, label, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
            x += w + 6f;
            return Widgets.ButtonInvisible(b);
        }

        public static string Search(Rect sr, string control, string value, string hint)
        {
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName(control);
            Look.EscUnfocus(control);
            value = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), value ?? "");
            if (value.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), hint, MUI.Muted);
            return value;
        }

        public static float Badge(Rect at, string text, Color border)
        {
            Text.Font = GameFont.Tiny;
            float w = Text.CalcSize(text).x + 10f;
            Rect b = new Rect(at.x, at.y, w, at.height);
            MUI.Fill(b, Look.Bg);
            MUI.Frame(b, border);
            Look.Text(b, text, border == Look.Line ? MUI.Muted : border, GameFont.Tiny, TextAnchor.MiddleCenter);
            Text.Font = GameFont.Small;
            return b.xMax + 4f;
        }

        public static bool Matches(string q, params string[] fields)
        {
            if (q.NullOrEmpty()) return true;
            foreach (string f in fields)
                if (!f.NullOrEmpty() && f.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        // a grid of cards in a scroll view; draw(rect, index) returns true when the card was picked
        public static int Grid(Rect outR, ref Vector2 scroll, int count, float minW, float ch, Func<Rect, int, bool> draw, string empty)
        {
            int cols = Mathf.Max(1, Mathf.FloorToInt((outR.width - 16f + 8f) / minW));
            float cw = (outR.width - 16f - (cols - 1) * 8f) / cols;
            int rows = Mathf.CeilToInt(count / (float)cols);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows * (ch + 8f), 10f));
            int picked = -1;
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < count; i++)
                {
                    Rect c = new Rect((i % cols) * (cw + 8f), (i / cols) * (ch + 8f), cw, ch);
                    if (c.yMax < scroll.y - 10f || c.y > scroll.y + outR.height + 10f) continue;
                    if (draw(c, i)) { picked = i; break; }
                }
                if (count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), empty, MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
            return picked;
        }

        public static void Portrait(Rect r, Pawn p)
        {
            MUI.Fill(r, Look.Bg);
            try
            {
                if (p.RaceProps.Humanlike)
                    GUI.DrawTexture(r, PortraitsCache.Get(p, new Vector2(r.width, r.height), Rot4.South, default(Vector3), 1.25f));
                else
                    Widgets.ThingIcon(r.ContractedBy(3f), p);
            }
            catch { Widgets.ThingIcon(r.ContractedBy(3f), p); }
        }
    }

    // ---------------- B1: owner of a bed, throne, meditation spot … ----------------
    public static class OwnerPickerPatch
    {
        private static FieldInfo assignableF;

        public static void Patch(Harmony h)
        {
            assignableF = AccessTools.Field(typeof(Dialog_AssignBuildingOwner), "assignable");
            h.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)),
                prefix: new HarmonyMethod(typeof(OwnerPickerPatch), nameof(Prefix)));
        }

        // the game's "set owner" window is swapped for ours on its way to the screen
        public static void Prefix(ref Window window)
        {
            if (!(window is Dialog_AssignBuildingOwner) || window.GetType() != typeof(Dialog_AssignBuildingOwner)) return;
            if (MTMod.S == null || !MTMod.S.ownerPicker || assignableF == null) return;
            try
            {
                if (assignableF.GetValue(window) is CompAssignableToPawn comp && comp.parent != null)
                    window = new Dialog_OwnerPicker(comp);
            }
            catch (Exception e) { Log.ErrorOnce("[Clear Tabs] owner picker: " + e, 0x4d5410); }
        }
    }

    public class Dialog_OwnerPicker : Window
    {
        private enum Group { All, Colonists, Slaves, Prisoners, Animals, Other }
        private enum Sort { State, Name }

        private class Entry
        {
            public Pawn pawn;
            public Group group;
            public bool assigned, ok, ideoForbids;
            public string reason, elsewhere, partner;
            public bool partnerHere;
        }

        private readonly CompAssignableToPawn comp;
        private readonly List<Entry> all = new List<Entry>();
        private Group group = Group.All;
        private Sort sort = Sort.State;
        private string search = "";
        private Vector2 scroll;
        private int builtFrame = -1;

        public override Vector2 InitialSize => new Vector2(900f, 640f);

        public Dialog_OwnerPicker(CompAssignableToPawn comp)
        {
            this.comp = comp;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
        }

        private void Build()
        {
            all.Clear();
            var seen = new HashSet<Pawn>();
            foreach (Pawn p in comp.AssignedPawnsForReading.Concat(comp.AssigningCandidates))
            {
                if (p == null || !seen.Add(p)) continue;
                var e = new Entry { pawn = p, assigned = comp.AssignedPawnsForReading.Contains(p), group = GroupOf(p) };
                if (!e.assigned)
                {
                    AcceptanceReport ar = comp.CanAssignTo(p);
                    e.ok = ar.Accepted;
                    e.reason = ar.Accepted ? null : ar.Reason?.StripTags();
                    try { e.ideoForbids = e.ok && !Find.IdeoManager.classicMode && comp.IdeoligionForbids(p); } catch { }
                }
                else e.ok = true;
                try
                {
                    Building_Bed bed = p.ownership?.OwnedBed;
                    if (bed != null && bed != comp.parent && comp.parent is Building_Bed) e.elsewhere = bed.LabelCap;
                    Thing throne = p.ownership?.AssignedThrone;
                    if (throne != null && throne != comp.parent && comp.parent is Building_Throne) e.elsewhere = throne.LabelCap;
                }
                catch { }
                try
                {
                    Pawn partner = LovePartnerRelationUtility.ExistingMostLikedLovePartner(p, false);
                    if (partner != null)
                    {
                        e.partner = partner.LabelShortCap;
                        e.partnerHere = comp.AssignedPawnsForReading.Contains(partner);
                    }
                }
                catch { }
                all.Add(e);
            }
            builtFrame = Time.frameCount;
        }

        private static Group GroupOf(Pawn p)
        {
            if (p.IsPrisonerOfColony) return Group.Prisoners;
            if (p.IsSlaveOfColony) return Group.Slaves;
            if (p.RaceProps.Animal) return Group.Animals;
            if (p.IsColonist) return Group.Colonists;
            return Group.Other;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (comp?.parent == null || comp.parent.Destroyed) { Close(); return; }
            if (builtFrame < 0 || Time.frameCount - builtFrame > 30) Build();
            PickUI.Frame(this);
            Rect r = inRect;
            float y = r.y;
            Widgets.DrawTextureFitted(new Rect(r.x, y + 2f, 28f, 28f), comp.parent.def.uiIcon, 1f);
            Look.Text(new Rect(r.x + 36f, y, r.width - 80f, 32f), "MT_PB_OwnerTitle".Translate(comp.parent.LabelCap), MUI.Text1, GameFont.Medium);
            y += 34f;
            int max = comp.MaxAssignedPawnsCount;
            Look.Text(new Rect(r.x, y, r.width, 20f), "MT_PB_OwnerSlots".Translate(comp.AssignedPawnsForReading.Count, max), MUI.Muted, GameFont.Tiny);
            y += 24f;
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = g == Group.All ? all.Count : all.Count(e => e.group == g);
                if (n == 0 && g != Group.All) continue;
                if (PickUI.Chip(ref x, y, ("MT_PB_OG_" + g).Translate() + " (" + n + ")", group == g)) { group = g; scroll = Vector2.zero; }
            }
            y += 36f;
            search = PickUI.Search(new Rect(r.x, y, 260f, 28f), "MT_PB_OwnerSearch", search, "MT_PG_Search".Translate());
            x = r.x + 276f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PG_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
                if (PickUI.Chip(ref x, y, ("MT_PB_OS_" + s).Translate(), sort == s, 28f, 20f)) sort = s;
            y += 38f;

            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => (group == Group.All || e.group == group) && PickUI.Matches(q, e.pawn.LabelShort, e.pawn.Name?.ToStringFull, e.partner));
            list = sort == Sort.Name ? list.OrderBy(e => e.pawn.LabelShort)
                : list.OrderBy(e => e.assigned ? 0 : e.ok && !e.ideoForbids ? 1 : 2).ThenBy(e => e.pawn.LabelShort);
            var shown = list.ToList();
            int picked = PickUI.Grid(new Rect(r.x, y, r.width, r.yMax - y), ref scroll, shown.Count, 280f, 72f, (c, i) => DrawCard(c, shown[i]), "MT_PB_OwnerNone".Translate());
            if (picked >= 0) Pick(shown[picked]);
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool can = e.assigned || (e.ok && !e.ideoForbids);
            bool over = Mouse.IsOver(c) && can;
            MUI.Fill(c, e.assigned ? Look.GreenBg : over ? Look.Card2 : Look.Card);
            MUI.Frame(c, e.assigned ? Look.Green : Look.Line);
            Rect pic = new Rect(c.x + 6f, c.y + 6f, 60f, 60f);
            if (!can) GUI.color = new Color(1f, 1f, 1f, 0.45f);
            PickUI.Portrait(pic, e.pawn);
            GUI.color = Color.white;
            float tx = pic.xMax + 8f, tw = c.xMax - tx - 6f;
            Look.Text(new Rect(tx, c.y + 4f, tw, 22f), e.pawn.LabelShortCap.Truncate(tw), e.assigned ? Look.Green : can ? MUI.Text1 : MUI.Muted);
            string line2 = e.assigned ? "MT_PB_ClickUnassign".Translate().ToString()
                : !e.ok ? (e.reason ?? "").CapitalizeFirst()
                : e.ideoForbids ? "IdeoligionForbids".Translate().ToString()
                : comp.AssignedAnything(e.pawn) ? "BuildingReassign".Translate().ToString() : "BuildingAssign".Translate().ToString();
            Look.Text(new Rect(tx, c.y + 25f, tw, 18f), line2.Truncate(tw), e.assigned ? Look.Green : can ? MUI.Muted : Look.Red, GameFont.Tiny);
            float bx = tx;
            if (e.elsewhere != null) bx = PickUI.Badge(new Rect(bx, c.y + 47f, 0f, 16f), "MT_PB_HasOther".Translate(e.elsewhere).ToString().Truncate(150f), Look.Orange);
            if (e.partner != null) bx = PickUI.Badge(new Rect(bx, c.y + 47f, 0f, 16f), "MT_PB_Partner".Translate(e.partner).ToString().Truncate(140f), e.partnerHere ? Look.Green : Look.Blue);
            if (e.pawn.IsSlaveOfColony || e.pawn.IsPrisonerOfColony) PickUI.Badge(new Rect(bx, c.y + 47f, 0f, 16f), ("MT_PB_OG_" + e.group).Translate(), Look.Line);
            TooltipHandler.TipRegion(c, e.pawn.LabelCap + (e.reason != null ? "\n\n" + e.reason : ""));
            return can && Widgets.ButtonInvisible(c);
        }

        private void Pick(Entry e)
        {
            if (e.assigned)
            {
                comp.TryUnassignPawn(e.pawn);
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
            else
            {
                comp.TryAssignPawn(e.pawn);
                if (comp.MaxAssignedPawnsCount == 1) { Close(); return; }
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
            builtFrame = -1;
        }
    }
}
