// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakGear
{
    // ---------------------------------------------------------------------------
    // 0.13.0: mech gestation picker (Biotech). A "Gestate mech…" button on every mech gestator opens
    // our window: mechs grouped by weight class and by role (work / combat), search, sorting by
    // bandwidth, gestation cycles and cost; a click adds the bill exactly like the game does.
    // Other pickers (the generic recipe picker) can ask Handles(table) / TryOpen(table) so the
    // mech gestator always gets this window.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class MechPickerPatch
    {
        private static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false) ?? BaseContent.BadTex;

        static MechPickerPatch()
        {
            try
            {
                if (MTMod.H == null || !ModsConfig.BiotechActive) return;
                MTMod.H.Patch(AccessTools.DeclaredMethod(typeof(Building_MechGestator), nameof(Building_MechGestator.GetGizmos)),
                    postfix: new HarmonyMethod(typeof(MechPickerPatch), nameof(GizmoPostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] mech picker: " + e.Message); }
        }

        public static bool On => MTMod.S != null && MTMod.S.mechPicker && ModsConfig.BiotechActive;

        // the table is a mech gestator (or any table whose recipes all make mechs)
        public static bool Handles(Building_WorkTable table)
        {
            if (!On || table == null) return false;
            if (table is Building_MechGestator) return true;
            var recs = table.def.AllRecipes;
            return recs != null && recs.Count > 0 && recs.All(r => IsMechRecipe(r));
        }

        public static bool IsMechRecipe(RecipeDef r) => r?.ProducedThingDef?.race != null && r.ProducedThingDef.race.IsMechanoid;

        public static bool TryOpen(Building_WorkTable table)
        {
            if (!Handles(table)) return false;
            SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
            Find.WindowStack.Add(new Dialog_MechPicker(table));
            return true;
        }

        public static IEnumerable<Gizmo> GizmoPostfix(IEnumerable<Gizmo> __result, Building_MechGestator __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!On || __instance == null || __instance.Faction != Faction.OfPlayer) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MT_PC_MechGizmo".Translate(),
                defaultDesc = "MT_PC_MechGizmoDesc".Translate(),
                icon = Icon,
                action = () => TryOpen(__instance)
            };
        }

        // the same as the game's "add bill"
        public static void Add(Building_WorkTable table, RecipeDef recipe)
        {
            if (table.billStack.Count >= BillStack.MaxCount)
            {
                Messages.Message("MT_PC_Full".Translate(BillStack.MaxCount), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (recipe.mechanitorOnlyRecipe && !table.Map.mapPawns.FreeColonists.Any(MechanitorUtility.IsMechanitor))
                Find.WindowStack.Add(new Dialog_MessageBox("RecipeRequiresMechanitor".Translate(recipe.LabelCap)));
            else if (!table.Map.mapPawns.FreeColonists.Any(col => recipe.PawnSatisfiesSkillRequirements(col)))
                Bill.CreateNoPawnsWithSkillDialog(recipe);
            Bill bill = recipe.MakeNewBill();
            table.billStack.AddBill(bill);
            if (recipe.conceptLearned != null) PlayerKnowledgeDatabase.KnowledgeDemonstrated(recipe.conceptLearned, KnowledgeAmount.Total);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }
    }

    public class Dialog_MechPicker : Window
    {
        private enum Group { All, Light, Medium, Heavy, Ultra, Work, Combat }
        private enum Sort { Name, Bandwidth, Cycles, Cost }

        private class Entry
        {
            public RecipeDef recipe;
            public ThingDef mech;
            public string weight = "", mod = "", works = "", needs = "";
            public int weightIdx;
            public bool work;
            public float bandwidth, cost;
            public int cycles;
        }

        private readonly Building_WorkTable table;
        private readonly List<Entry> all = new List<Entry>();
        private Group group = Group.All;
        private Sort sort = Sort.Name;
        private string search = "";
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(980f, 680f);

        public Dialog_MechPicker(Building_WorkTable table)
        {
            this.table = table;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
            forcePause = false;
            foreach (RecipeDef r in table.def.AllRecipes)
            {
                try
                {
                    if (!MechPickerPatch.IsMechRecipe(r) || !r.AvailableNow || !r.AvailableOnNow(table)) continue;
                    ThingDef m = r.ProducedThingDef;
                    var e = new Entry { recipe = r, mech = m, cycles = r.gestationCycles, mod = m.modContentPack?.Name ?? "" };
                    MechWeightClassDef wc = m.race.mechWeightClass;
                    e.weight = wc != null ? (string)wc.LabelCap : "";
                    e.weightIdx = WeightIndex(wc);
                    e.work = m.race.IsWorkMech;
                    if (e.work) e.works = string.Join(", ", m.race.mechEnabledWorkTypes.Select(w => w.gerundLabel ?? w.label).ToArray());
                    try { e.bandwidth = m.GetStatValueAbstract(StatDefOf.BandwidthCost); } catch { }
                    if (r.ingredients != null)
                    {
                        var parts = new List<string>();
                        foreach (IngredientCount ic in r.ingredients)
                        {
                            float n = ic.GetBaseCount();
                            ThingDef d = ic.IsFixedIngredient ? ic.FixedIngredient : ic.filter.AllowedThingDefs.OrderBy(x => x.BaseMarketValue).FirstOrDefault();
                            if (d != null) e.cost += n * d.BaseMarketValue;
                            parts.Add(ic.SummaryFor(r));
                        }
                        e.needs = string.Join(", ", parts.ToArray());
                    }
                    all.Add(e);
                }
                catch (Exception ex) { Log.WarningOnce("[Mišák Tabs] mech picker: " + r?.defName + ": " + ex.Message, (r?.defName ?? "").GetHashCode() ^ 0x3a17); }
            }
        }

        private static int WeightIndex(MechWeightClassDef wc)
        {
            if (wc == null) return 1;
            string n = wc.defName ?? "";
            if (n.IndexOf("Ultra", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (n.IndexOf("Heavy", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (n.IndexOf("Medium", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            if (n.IndexOf("Light", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            return 1;
        }

        private bool In(Entry e, Group g)
        {
            switch (g)
            {
                case Group.Light: return e.weightIdx == 0;
                case Group.Medium: return e.weightIdx == 1;
                case Group.Heavy: return e.weightIdx == 2;
                case Group.Ultra: return e.weightIdx == 3;
                case Group.Work: return e.work;
                case Group.Combat: return !e.work;
                default: return true;
            }
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect full = new Rect(0f, 0f, windowRect.width, windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), "MT_PC_MechTitle".Translate(), MUI.Text1, GameFont.Medium);
            Look.Text(new Rect(r.xMax - 300f, y, 260f, 32f), table.LabelCap, MUI.Muted, GameFont.Small, TextAnchor.MiddleRight);
            y += 38f;
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = all.Count(e => In(e, g));
                if (n == 0 && g != Group.All) continue;
                string lab = ("MT_PC_MG_" + g).Translate() + " (" + n + ")";
                Text.Font = GameFont.Small;
                float w = Text.CalcSize(lab).x + 24f;
                Rect b = new Rect(x, y, w, 28f);
                bool on = group == g;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                MUI.Frame(b, on ? Look.Green : Look.Line);
                Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(b)) { group = g; scroll = Vector2.zero; }
                x += w + 6f;
            }
            y += 36f;
            Rect sr = new Rect(r.x, y, 260f, 28f);
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName("MT_MechSearch");
            Look.EscUnfocus("MT_MechSearch");
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), "MT_PC_Search".Translate(), MUI.Muted);
            x = sr.xMax + 16f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PC_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
            {
                string lab = ("MT_PC_MS_" + s).Translate();
                float w = Text.CalcSize(lab).x + 20f;
                Rect b = new Rect(x, y, w, 28f);
                bool on = sort == s;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                MUI.Frame(b, on ? Look.Green : Look.Line);
                Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(b)) sort = s;
                x += w + 4f;
            }
            y += 38f;

            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => In(e, group)
                && (q.Length == 0 || e.mech.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.works.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.mod.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (sort)
            {
                case Sort.Bandwidth: list = list.OrderBy(e => e.bandwidth).ThenBy(e => e.mech.label); break;
                case Sort.Cycles: list = list.OrderBy(e => e.cycles).ThenBy(e => e.mech.label); break;
                case Sort.Cost: list = list.OrderBy(e => e.cost).ThenBy(e => e.mech.label); break;
                default: list = list.OrderBy(e => e.weightIdx).ThenBy(e => e.mech.label); break;
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
                        MechPickerPatch.Add(table, shown[i].recipe);
                        Close();
                        break;
                    }
                }
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PC_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool over = Mouse.IsOver(c);
            MUI.Fill(c, over ? Look.Card2 : Look.Card);
            MUI.Frame(c, Look.Line);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            MUI.Fill(icon, Look.Bg);
            Widgets.DefIcon(icon.ContractedBy(4f), e.mech);
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 30f;
            Look.Text(new Rect(tx, c.y + 4f, tw, 22f), e.mech.LabelCap.ToString().Truncate(tw), MUI.Text1);
            string line2 = e.work ? e.works : (string)"MT_PC_Combat".Translate();
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), line2.Truncate(tw), MUI.Muted, GameFont.Tiny);
            float bx = tx;
            if (!e.weight.NullOrEmpty()) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), e.weight, e.weightIdx >= 2 ? Look.Orange : Look.Line);
            if (e.bandwidth > 0f) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_Bandwidth".Translate(e.bandwidth.ToString("0.#")), Look.Blue);
            if (e.cycles > 0) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_Cycles".Translate(e.cycles), Look.Line);
            if (e.cost > 0f) Badge(new Rect(bx, c.y + 43f, 0f, 16f), "$" + e.cost.ToString("0"), Look.Line);
            Widgets.InfoCardButton(c.xMax - 28f, c.y + 6f, e.mech);
            var sb = new System.Text.StringBuilder();
            sb.Append(e.mech.LabelCap.ToString()).Append('\n').Append(e.mech.description);
            if (!e.weight.NullOrEmpty()) sb.Append("\n\n").Append("MT_PC_Weight".Translate(e.weight).ToString());
            if (e.bandwidth > 0f) sb.Append('\n').Append("MT_PC_Bandwidth".Translate(e.bandwidth.ToString("0.#")).ToString());
            if (e.cycles > 0) sb.Append('\n').Append("MT_PC_Cycles".Translate(e.cycles).ToString());
            if (!e.needs.NullOrEmpty()) sb.Append('\n').Append("MT_PC_Needs".Translate(e.needs).ToString());
            if (!e.mod.NullOrEmpty()) sb.Append('\n').Append("MT_PC_Mod".Translate(e.mod).ToString());
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 32f, c.height), sb.ToString());
            return Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 32f, c.height));
        }

        internal static float Badge(Rect at, string text, Color border)
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
    }
}
