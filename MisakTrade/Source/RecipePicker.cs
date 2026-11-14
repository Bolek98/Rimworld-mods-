// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTrade
{
    // ---------------------------------------------------------------------------
    // 0.13.0: "Add bill" (work benches) and "Add operation" (health tab) open a picker in the look of the
    // plant picker instead of the game's long menu. The picker is built from the very menu options the game
    // (or another mod) made – picking one runs that option, so the bill is added exactly as by the game.
    // Every bill list in the game goes through BillStack.DoListing: its menu maker is wrapped, and the
    // menu it opens is caught in WindowStack.Add and replaced by our window.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class PickerHook
    {
        private static List<FloatMenuOption> pending;
        private static IBillGiver pendingGiver;
        private static readonly AccessTools.FieldRef<FloatMenu, List<FloatMenuOption>> optionsRef = AccessTools.FieldRefAccess<FloatMenu, List<FloatMenuOption>>("options");
        private static readonly AccessTools.FieldRef<FloatMenuOption, ThingDef> shownRef = AccessTools.FieldRefAccess<FloatMenuOption, ThingDef>("shownItem");
        private static readonly AccessTools.FieldRef<FloatMenuOption, Texture2D> texRef = AccessTools.FieldRefAccess<FloatMenuOption, Texture2D>("iconTex");

        static PickerHook()
        {
            try
            {
                Harmony h = MTMod.H ?? new Harmony("bolek98.misaktabs.pickers");
                h.Patch(AccessTools.Method(typeof(BillStack), nameof(BillStack.DoListing)),
                    prefix: new HarmonyMethod(typeof(PickerHook), nameof(DoListingPrefix)));
                h.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)),
                    prefix: new HarmonyMethod(typeof(PickerHook), nameof(AddPrefix)) { priority = Priority.Last });
            }
            catch (Exception e) { Log.Warning("[Clear Tabs] recipe / operation picker: " + e.Message); }
        }

        public static ThingDef ShownItem(FloatMenuOption o) { try { return shownRef(o); } catch { return null; } }
        public static Texture2D IconTex(FloatMenuOption o) { try { return texRef(o); } catch { return null; } }

        public static void DoListingPrefix(BillStack __instance, ref Func<List<FloatMenuOption>> recipeOptionsMaker)
        {
            if (MTMod.S == null || recipeOptionsMaker == null) return;
            IBillGiver giver = __instance?.billGiver;
            bool medical = giver is Pawn || giver is Corpse;
            if (medical ? !MTMod.S.surgeryPicker : !MTMod.S.recipePicker) return;
            Func<List<FloatMenuOption>> orig = recipeOptionsMaker;
            recipeOptionsMaker = () =>
            {
                List<FloatMenuOption> list = orig();
                pending = list;
                pendingGiver = giver;
                return list;
            };
        }

        public static bool AddPrefix(Window window)
        {
            if (pending == null) return true;
            List<FloatMenuOption> list = pending;
            IBillGiver giver = pendingGiver;
            pending = null;
            pendingGiver = null;
            try
            {
                if (!(window is FloatMenu fm) || optionsRef(fm) != list) return true;
                if (list.Count == 0 || list.All(o => o.action == null && o.Label == "NoneBrackets".Translate().ToString())) return true;
                if (giver is Pawn pawn)
                {
                    if (!SurgeryPicker.Open(pawn, list, null)) return true;
                }
                else if (giver is Thing t)
                {
                    // mech gestators open the mech picker (group C) instead of ours
                    if (!(t is Building_WorkTable wt && MechPickerPatch.TryOpen(wt)))
                        Find.WindowStack.Add(new Dialog_RecipePicker(t, list));
                }
                else return true;
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Clear Tabs] picker: " + e, 0x4d5410);
                return true;
            }
        }
    }

    // the shared look of the pickers (same as the plant picker)
    public static class PickerUI
    {
        public static float Tab(float x, float y, string lab, bool on, Action click)
        {
            Text.Font = GameFont.Small;
            float w = Text.CalcSize(lab).x + 24f;
            Rect b = new Rect(x, y, w, 28f);
            MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
            MUI.Frame(b, on ? Look.Green : Look.Line);
            Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
            if (Widgets.ButtonInvisible(b)) click();
            return w;
        }

        public static string Search(Rect sr, string search, string ctrl, string hint)
        {
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName(ctrl);
            Look.EscUnfocus(ctrl);
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), hint, MUI.Muted);
            return search;
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

        public static void Frame(Window w)
        {
            Rect full = new Rect(0f, 0f, w.windowRect.width, w.windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
        }

        public static void Setup(Window w)
        {
            w.doCloseX = true;
            w.closeOnClickedOutside = true;
            w.absorbInputAroundWindow = true;
            w.draggable = true;
            w.doWindowBackground = false;
            w.drawShadow = true;
            w.forcePause = false;
        }

        public static void Icon(Rect icon, FloatMenuOption o, ThingDef def, Texture2D fallback)
        {
            MUI.Fill(icon, Look.Bg);
            Rect inner = icon.ContractedBy(4f);
            ThingDef shown = PickerHook.ShownItem(o) ?? def;
            Texture2D tex = PickerHook.IconTex(o) ?? fallback;
            if (shown != null) Widgets.DefIcon(inner, shown);
            else if (tex != null) Widgets.DrawTextureFitted(inner, tex, 1f);
        }
    }

    public class Dialog_RecipePicker : Window
    {
        public enum Group { All, Meals, Drugs, Medicine, Weapons, Armor, Apparel, Resources, Buildings, Other }
        private enum Sort { Name, Work, Value, Skill }

        private class Entry
        {
            public FloatMenuOption opt;     // null = locked by research
            public RecipeDef recipe;
            public ThingDef product;
            public Group group;
            public string label, lockReason, mod, needs;
            public float work, value;
            public int skill;
            public bool skillOk, inQueue;
        }

        private readonly Thing table;
        private readonly List<Entry> all = new List<Entry>();
        private Group group = Group.All;
        private Sort sort = Sort.Name;
        private string search = "";
        private bool showLocked;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(1000f, 700f);

        public Dialog_RecipePicker(Thing table, List<FloatMenuOption> options)
        {
            this.table = table;
            PickerUI.Setup(this);
            List<RecipeDef> recipes = table.def.AllRecipes ?? new List<RecipeDef>();
            Map map = table.MapHeld ?? Find.CurrentMap;
            List<Pawn> cols = map?.mapPawns.FreeColonists?.ToList() ?? new List<Pawn>();
            var queued = new HashSet<RecipeDef>();
            try { if (table is IBillGiver g) foreach (Bill b in g.BillStack.Bills) queued.Add(b.recipe); } catch { }
            var byLabel = new Dictionary<string, RecipeDef>();
            foreach (RecipeDef r in recipes)
            {
                string l = r.LabelCap.ToString();
                if (!byLabel.ContainsKey(l)) byLabel[l] = r;
            }
            var used = new HashSet<RecipeDef>();
            foreach (FloatMenuOption o in options)
            {
                RecipeDef rec = null;
                if (!byLabel.TryGetValue(o.Label ?? "", out rec))
                {
                    ThingDef shown = PickerHook.ShownItem(o);
                    rec = recipes.FirstOrDefault(r => shown != null && r.UIIconThing == shown && !used.Contains(r)) ?? recipes.FirstOrDefault(r => shown != null && r.ProducedThingDef == shown);
                }
                if (rec != null) used.Add(rec);
                all.Add(Make(o, rec, o.Label, cols, queued));
            }
            // recipes of this bench that research has not unlocked yet – shown greyed with the reason
            foreach (RecipeDef r in recipes)
            {
                if (used.Contains(r) || r.AvailableNow) continue;
                try { if (!r.AvailableOnNow(table)) continue; } catch { continue; }
                var e = Make(null, r, r.LabelCap, cols, queued);
                e.lockReason = LockReason(r);
                all.Add(e);
            }
        }

        private Entry Make(FloatMenuOption o, RecipeDef rec, string label, List<Pawn> cols, HashSet<RecipeDef> queued)
        {
            var e = new Entry { opt = o, recipe = rec, label = label ?? "" };
            if (rec != null)
            {
                e.product = rec.ProducedThingDef;
                try { e.work = rec.WorkAmountTotal(null); } catch { e.work = rec.workAmount; }
                if (e.product != null)
                {
                    int n = rec.products != null && rec.products.Count > 0 ? rec.products[0].count : 1;
                    try { e.value = e.product.BaseMarketValue * n; } catch { }
                }
                if (rec.skillRequirements != null && rec.skillRequirements.Count > 0)
                    e.skill = rec.skillRequirements.Max(s => s.minLevel);
                try { e.skillOk = cols.Any(p => rec.PawnSatisfiesSkillRequirements(p)); } catch { e.skillOk = true; }
                e.mod = rec.modContentPack?.Name ?? "";
                e.inQueue = queued.Contains(rec);
                try { e.needs = string.Join(", ", rec.ingredients.Select(i => i.Summary).ToArray()); } catch { e.needs = ""; }
            }
            else e.skillOk = true;
            e.group = GroupOf(rec, e.product);
            return e;
        }

        private static string LockReason(RecipeDef r)
        {
            try
            {
                if (r.researchPrerequisite != null && !r.researchPrerequisite.IsFinished)
                    return "MT_PA_NeedsResearch".Translate(r.researchPrerequisite.LabelCap);
                if (r.researchPrerequisites != null)
                    foreach (ResearchProjectDef p in r.researchPrerequisites)
                        if (!p.IsFinished) return "MT_PA_NeedsResearch".Translate(p.LabelCap);
            }
            catch { }
            return "MT_PA_Locked".Translate();
        }

        private static bool InCat(ThingDef d, string cat)
        {
            try { return d.thingCategories != null && d.thingCategories.Any(c => c.defName == cat || c.Parents.Any(p => p.defName == cat)); }
            catch { return false; }
        }

        public static Group GroupOf(RecipeDef rec, ThingDef p)
        {
            if (p == null) return Group.Other;
            try
            {
                if (p.IsDrug && !p.IsMedicine) return Group.Drugs;
                if (p.IsMedicine || InCat(p, "Medicine")) return Group.Medicine;
                if (p.IsIngestible && (InCat(p, "FoodMeals") || (p.ingestible != null && (p.ingestible.foodType & FoodTypeFlags.Meal) != 0))) return Group.Meals;
                if (p.IsWeapon) return Group.Weapons;
                if (p.IsApparel)
                {
                    if (InCat(p, "ApparelArmor") || p.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp) >= 0.3f) return Group.Armor;
                    return Group.Apparel;
                }
                if (p.category == ThingCategory.Building || p.Minifiable || p.IsFrame) return Group.Buildings;
                if (p.IsIngestible) return Group.Meals;
                if (p.IsStuff || InCat(p, "Manufactured") || InCat(p, "ResourcesRaw") || InCat(p, "Items") || p.category == ThingCategory.Item) return Group.Resources;
            }
            catch { }
            return Group.Other;
        }

        public override void DoWindowContents(Rect inRect)
        {
            PickerUI.Frame(this);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), "MT_PA_RecTitle".Translate(table.LabelCap), MUI.Text1, GameFont.Medium);
            y += 38f;
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = g == Group.All ? all.Count : all.Count(e => e.group == g);
                if (n == 0 && g != Group.All) continue;
                Group gg = g;
                x += PickerUI.Tab(x, y, ("MT_PA_G" + g).Translate() + " (" + n + ")", group == g, () => { group = gg; scroll = Vector2.zero; }) + 6f;
                if (x > r.xMax - 80f) { x = r.x; y += 34f; }
            }
            y += 36f;
            search = PickerUI.Search(new Rect(r.x, y, 260f, 28f), search, "MT_RecipeSearch", "MT_PG_Search".Translate());
            x = r.x + 276f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PG_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
            {
                Sort ss = s;
                x += PickerUI.Tab(x, y, ("MT_PA_S" + s).Translate(), sort == s, () => sort = ss) + 4f;
            }
            Rect lr = new Rect(r.xMax - 230f, y, 230f, 28f);
            Widgets.CheckboxLabeled(lr, "MT_PA_ShowLocked".Translate(), ref showLocked);
            y += 38f;

            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => (group == Group.All || e.group == group)
                && (showLocked || e.opt != null)
                && (q.Length == 0 || e.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (e.product != null && e.product.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) || e.mod.NullOrEmpty() == false && e.mod.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (sort)
            {
                case Sort.Work: list = list.OrderBy(e => e.work).ThenBy(e => e.label); break;
                case Sort.Value: list = list.OrderByDescending(e => e.value).ThenBy(e => e.label); break;
                case Sort.Skill: list = list.OrderBy(e => e.skill).ThenBy(e => e.label); break;
                default: list = list.OrderBy(e => e.opt == null).ThenBy(e => e.label); break;
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
                        if (e.opt != null && e.opt.action != null)
                        {
                            Close();
                            e.opt.action();
                            break;
                        }
                        Messages.Message(e.lockReason ?? e.label, MessageTypeDefOf.RejectInput, false);
                    }
                }
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PG_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool locked = e.opt == null || e.opt.action == null;
            bool over = Mouse.IsOver(c);
            MUI.Fill(c, e.inQueue ? Look.GreenBg : over && !locked ? Look.Card2 : Look.Card);
            MUI.Frame(c, e.inQueue ? Look.Green : Look.Line);
            if (locked) GUI.color = new Color(1f, 1f, 1f, 0.5f);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            PickerUI.Icon(icon, e.opt, e.recipe?.UIIconThing ?? e.product, e.recipe?.UIIcon);
            GUI.color = Color.white;
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 30f;
            Look.Text(new Rect(tx, c.y + 4f, tw, 22f), e.label.Truncate(tw), locked ? MUI.Muted : e.inQueue ? Look.Green : MUI.Text1);
            string line2 = locked && e.lockReason != null ? e.lockReason
                : (e.work > 0f ? "MT_PA_Work".Translate(e.work.ToStringWorkAmount()).ToString() : "") + (e.needs.NullOrEmpty() ? "" : " · " + e.needs);
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), line2.Truncate(tw), locked ? Look.Orange : MUI.Muted, GameFont.Tiny);
            float bx = tx;
            if (e.skill > 0) bx = PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PG_Skill".Translate(e.skill), e.skillOk ? Look.Line : Look.Red);
            if (e.value > 0.5f) bx = PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PA_Value".Translate(e.value.ToString("0.#")), Look.Line);
            if (e.inQueue) bx = PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PA_InQueue".Translate(), Look.Green);
            if (!e.mod.NullOrEmpty() && e.mod != "Core") PickerUI.Badge(new Rect(bx, c.y + 43f, 0f, 16f), e.mod.Truncate(110f), Look.Line);
            if (e.recipe != null) Widgets.InfoCardButton(c.xMax - 28f, c.y + 6f, e.recipe);
            var sb = new System.Text.StringBuilder();
            sb.Append(e.label);
            if (e.recipe != null && !e.recipe.description.NullOrEmpty()) sb.Append('\n').Append(e.recipe.description);
            if (!e.needs.NullOrEmpty()) sb.Append("\n\n").Append("MT_PA_Needs".Translate(e.needs).ToString());
            if (e.work > 0f) sb.Append('\n').Append("MT_PA_Work".Translate(e.work.ToStringWorkAmount()).ToString());
            if (e.skill > 0) sb.Append('\n').Append(e.skillOk ? "MT_PA_SkillOk".Translate(e.skill).ToString() : "MT_PA_SkillNo".Translate(e.skill).ToString());
            if (e.lockReason != null) sb.Append("\n\n").Append(e.lockReason);
            if (!e.mod.NullOrEmpty()) sb.Append('\n').Append("MT_PG_Mod".Translate(e.mod).ToString());
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 32f, c.height), sb.ToString());
            return Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 32f, c.height));
        }
    }
}
