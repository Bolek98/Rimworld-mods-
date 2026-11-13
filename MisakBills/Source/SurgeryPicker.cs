// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakBills
{
    // ---------------------------------------------------------------------------
    // 0.13.0: "Add operation" in a picker: groups (implants, bionics and prosthetics, amputation and removal,
    // drugs, other), search, sorting, a card per operation and body part with the needed items, the risk and
    // the reason when it cannot be done now. Built from the game's own menu options – picking one does
    // exactly what the game does (royal implant checks, confirmations, the bill).
    // ---------------------------------------------------------------------------
    public static class SurgeryPicker
    {
        public static bool Open(Pawn pawn, List<FloatMenuOption> options, string title)
        {
            if (pawn == null || options == null || options.Count == 0) return false;
            if (MTMod.S == null || !MTMod.S.surgeryPicker) return false;
            Find.WindowStack.Add(new Dialog_SurgeryPicker(pawn, options, title));
            return true;
        }
    }

    public class Dialog_SurgeryPicker : Window
    {
        private enum Group { All, Implants, Bionics, Removal, Drugs, Other }
        private enum Sort { Name, Part, Risk }

        private class Entry
        {
            public FloatMenuOption opt;
            public RecipeDef recipe;
            public BodyPartRecord part;
            public Group group;
            public string label, reason, needs, mod;
            public float death;
        }

        private readonly Pawn pawn;
        private readonly string title;
        private readonly List<Entry> all = new List<Entry>();
        private Group group = Group.All;
        private Sort sort = Sort.Name;
        private string search = "";
        private bool onlyPossible;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public Dialog_SurgeryPicker(Pawn pawn, List<FloatMenuOption> options, string title)
        {
            this.pawn = pawn;
            this.title = title;
            PickerUI.Setup(this);
            // the labels the game gives each operation (+ body part), to know which recipe an option is
            var cands = new List<KeyValuePair<string, KeyValuePair<RecipeDef, BodyPartRecord>>>();
            try
            {
                foreach (RecipeDef r in pawn.def.AllRecipes)
                {
                    try
                    {
                        if (r.targetsBodyPart)
                        {
                            foreach (BodyPartRecord part in r.Worker.GetPartsToApplyOn(pawn, r))
                            {
                                string l = r.Worker.GetLabelWhenUsedOn(pawn, part).CapitalizeFirst();
                                if (part != null && !r.hideBodyPartNames) l = l + " (" + part.Label + ")";
                                cands.Add(new KeyValuePair<string, KeyValuePair<RecipeDef, BodyPartRecord>>(l, new KeyValuePair<RecipeDef, BodyPartRecord>(r, part)));
                            }
                        }
                        else
                            cands.Add(new KeyValuePair<string, KeyValuePair<RecipeDef, BodyPartRecord>>(r.Worker.GetLabelWhenUsedOn(pawn, null).CapitalizeFirst(), new KeyValuePair<RecipeDef, BodyPartRecord>(r, null)));
                    }
                    catch { }
                }
            }
            catch { }
            cands.Sort((a, b) => b.Key.Length.CompareTo(a.Key.Length));
            foreach (FloatMenuOption o in options)
            {
                string label = o.Label ?? "";
                var e = new Entry { opt = o, label = label };
                foreach (var c in cands)
                    if (label == c.Key || label.StartsWith(c.Key + " (", StringComparison.Ordinal))
                    {
                        e.recipe = c.Value.Key;
                        e.part = c.Value.Value;
                        if (label.Length > c.Key.Length + 2 && o.action == null) e.reason = label.Substring(c.Key.Length + 2).TrimEnd(')');
                        e.label = c.Key;
                        break;
                    }
                if (e.recipe != null)
                {
                    try { e.needs = string.Join(", ", e.recipe.ingredients.Select(i => i.Summary).ToArray()); } catch { e.needs = ""; }
                    e.death = e.recipe.deathOnFailedSurgeryChance;
                    e.mod = e.recipe.modContentPack?.Name ?? "";
                }
                else { e.needs = ""; e.mod = ""; }
                e.group = GroupOf(e.recipe);
                all.Add(e);
            }
        }

        private static Group GroupOf(RecipeDef r)
        {
            if (r == null) return Group.Other;
            try
            {
                RecipeWorker w = r.Worker;
                if (w is Recipe_AdministerIngestible) return Group.Drugs;
                if (w is Recipe_RemoveBodyPart || w is Recipe_RemoveHediff || w is Recipe_RemoveImplant) return Group.Removal;
                if (w is Recipe_InstallArtificialBodyPart) return Group.Bionics;
                HediffDef h = r.addsHediff;
                if (h != null)
                {
                    if (h.addedPartProps != null) return Group.Bionics;
                    return Group.Implants;
                }
                if (w is Recipe_InstallImplant) return Group.Implants;
                if (r.ingredients != null && r.ingredients.Any(i => i.IsFixedIngredient && i.FixedIngredient.IsDrug)) return Group.Drugs;
            }
            catch { }
            return Group.Other;
        }

        public override void DoWindowContents(Rect inRect)
        {
            PickerUI.Frame(this);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), title ?? "MT_PA_OpTitle".Translate(pawn.LabelShortCap).ToString(), MUI.Text1, GameFont.Medium);
            y += 38f;
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = g == Group.All ? all.Count : all.Count(e => e.group == g);
                if (n == 0 && g != Group.All) continue;
                Group gg = g;
                x += PickerUI.Tab(x, y, ("MT_PA_O" + g).Translate() + " (" + n + ")", group == g, () => { group = gg; scroll = Vector2.zero; }) + 6f;
            }
            y += 36f;
            search = PickerUI.Search(new Rect(r.x, y, 260f, 28f), search, "MT_SurgerySearch", "MT_PA_OpSearch".Translate());
            x = r.x + 276f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PG_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
            {
                Sort ss = s;
                x += PickerUI.Tab(x, y, ("MT_PA_OS" + s).Translate(), sort == s, () => sort = ss) + 4f;
            }
            Rect ur = new Rect(r.xMax - 230f, y, 230f, 28f);
            Widgets.CheckboxLabeled(ur, "MT_PA_OnlyPossible".Translate(), ref onlyPossible);
            y += 38f;

            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => (group == Group.All || e.group == group)
                && (!onlyPossible || e.opt.action != null)
                && (q.Length == 0 || e.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (e.part != null && e.part.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) || e.needs.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (sort)
            {
                case Sort.Part: list = list.OrderBy(e => e.part?.Label ?? "").ThenBy(e => e.label); break;
                case Sort.Risk: list = list.OrderBy(e => e.death).ThenBy(e => e.label); break;
                default: list = list.OrderBy(e => e.opt.action == null).ThenBy(e => e.label); break;
            }
            var shown = list.ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            int cols = Mathf.Max(1, Mathf.FloorToInt((outR.width - 16f + 8f) / 300f));
            float cw = (outR.width - 16f - (cols - 1) * 8f) / cols, ch = 64f;
            int rows = Mathf.CeilToInt(shown.Count / (float)cols);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows * (ch + 8f), 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    Rect c = new Rect((i % cols) * (cw + 8f), (i / cols) * (ch + 8f), cw, ch);
                    if (c.yMax < scroll.y - 10f || c.y > scroll.y + outR.height + 10f) continue;
                    if (DrawCard(c, shown[i]))
                    {
                        Entry e = shown[i];
                        if (e.opt.action != null)
                        {
                            Close();
                            SoundDefOf.Click.PlayOneShotOnCamera();
                            e.opt.action();
                            break;
                        }
                        Messages.Message(e.reason ?? e.opt.Label, MessageTypeDefOf.RejectInput, false);
                    }
                }
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PG_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool locked = e.opt.action == null;
            bool over = Mouse.IsOver(c);
            MUI.Fill(c, over && !locked ? Look.Card2 : Look.Card);
            MUI.Frame(c, Look.Line);
            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.5f);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            ThingDef iconDef = null;
            try { if (e.recipe != null && e.recipe.ingredients != null) iconDef = e.recipe.ingredients.Where(i => i.IsFixedIngredient).Select(i => i.FixedIngredient).FirstOrDefault(d => !d.IsMedicine); } catch { }
            PickerUI.Icon(icon, e.opt, iconDef, e.recipe?.UIIcon);
            GUI.color = Color.white;
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 30f;
            Look.Text(new Rect(tx, c.y + 4f, tw, 22f), e.label.Truncate(tw), locked ? MUI.Muted : MUI.Text1);
            string line2 = locked ? (e.reason ?? "MT_PA_CantNow".Translate().ToString()) : e.needs;
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), (line2 ?? "").Truncate(tw), locked ? Look.Orange : MUI.Muted, GameFont.Tiny);
            float bx = tx;
            if (e.part != null) bx = PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), e.part.LabelCap, Look.Blue);
            if (e.death > 0f) bx = PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PA_Death".Translate(e.death.ToStringPercent()), Look.Red);
            if (!e.mod.NullOrEmpty() && e.mod != "Core" && !e.mod.StartsWith("Royalty") && !e.mod.StartsWith("Biotech") && !e.mod.StartsWith("Ideology") && !e.mod.StartsWith("Anomaly") && !e.mod.StartsWith("Odyssey"))
                PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), e.mod.Truncate(110f), Look.Line);
            if (e.recipe != null) Widgets.InfoCardButton(c.xMax - 28f, c.y + 6f, e.recipe);
            var sb = new System.Text.StringBuilder();
            sb.Append(e.opt.Label);
            if (e.recipe != null && !e.recipe.description.NullOrEmpty()) sb.Append('\n').Append(e.recipe.description);
            if (!e.needs.NullOrEmpty()) sb.Append("\n\n").Append("MT_PA_Needs".Translate(e.needs).ToString());
            if (e.death > 0f) sb.Append('\n').Append("MT_PA_Death".Translate(e.death.ToStringPercent()).ToString());
            if (!e.mod.NullOrEmpty()) sb.Append('\n').Append("MT_PG_Mod".Translate(e.mod).ToString());
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 32f, c.height), sb.ToString());
            return Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 32f, c.height));
        }
    }
}
