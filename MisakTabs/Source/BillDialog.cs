using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.11.0 The bill window in the Mišák look. Opened instead of the game's bill window
    // (everywhere – a swap in WindowStack.Add), "Původní okno" opens the game's one.
    //  Header: icon, name (rename), running / paused, priority P1–P10, linked bills.
    //  Left: what the recipe needs and makes, work, skills, workbench.
    //  Middle: repeat mode (× / ≥ / ∞) with − / + steps, have X / target with a bar and all the
    //  game's target-count options, where to put the products, who may do it.
    //  Right: the game's ingredient filter and search radius (unchanged, so every special
    //  filter of other mods keeps working).
    // It derives from the game's window, so the radius on the map, the ingredient pane and the
    // worker list are the game's own code.
    // ---------------------------------------------------------------------------
    public class Dialog_MTBillConfig : Dialog_BillConfig
    {
        private readonly IntVec3 pos;
        private string repeatBuf, targetBuf, unpauseBuf;
        private Vector2 infoScroll, midScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);
        private static readonly MethodInfo fillOutput = AccessTools.Method(typeof(Dialog_BillConfig), "FillOutputDropdownOptions");

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1060f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 40f));

        public Dialog_MTBillConfig(Bill_Production bill, IntVec3 billGiverPos) : base(bill, billGiverPos)
        {
            pos = billGiverPos;
            doCloseButton = false;
        }

        protected override void LateWindowOnGUI(Rect inRect) { }

        public override void DoWindowContents(Rect inRect)
        {
            OurLookScope.depth++;
            try { Draw(inRect); }
            catch (Exception e)
            {
                Look.ResetGui();
                Log.ErrorOnce("[Mišák Tabs] bill window: " + e, 0x51A7B41);
                Close();
                BillDialogSwap.OpenOriginal(bill, pos);
            }
            finally { OurLookScope.depth--; }
        }

        private Building_WorkTable Table => bill.billStack?.billGiver as Building_WorkTable;

        private void Draw(Rect r)
        {
            Widgets.DrawBoxSolid(new Rect(r.x - 18f, r.y - 18f, r.width + 36f, r.height + 36f), Look.Bg);
            // ---- header ----
            Rect head = new Rect(r.x, r.y, r.width - 30f, 46f);
            Look.Icon(new Rect(head.x, head.y + 2f, 42f, 42f), bill.recipe);
            float tx = head.x + 52f;
            Look.Text(new Rect(tx, head.y, head.width - 420f, 28f), bill.LabelCap.ToString().Truncate(head.width - 420f), Color.white, GameFont.Medium);
            string sub = (bill.billStack?.billGiver as Thing)?.LabelCap ?? "";
            List<Bill> linked = BillLinks.GroupOf(bill) != 0 ? BillLinks.Others(bill) : null;
            if (linked != null && linked.Count > 0) sub += " · " + "MT_LinkedN".Translate(linked.Count);
            Look.Text(new Rect(tx, head.y + 28f, head.width - 420f, 18f), sub, Muted, GameFont.Tiny);
            if (linked != null && linked.Count > 0)
                TooltipHandler.TipRegion(new Rect(tx, head.y + 28f, head.width - 420f, 18f), "MT_LinkedTip".Translate() + "\n" + string.Join("\n", linked.Select(o => "- " + ((o.billStack?.billGiver as Thing)?.LabelCap ?? "?") + ": " + o.LabelCap).ToArray()));
            float bx = head.xMax;
            bx -= 120f;
            if (Look.Button(new Rect(bx, head.y + 8f, 120f, 28f), "MT_BD_Original".Translate()))
            {
                Close();
                BillDialogSwap.OpenOriginal(bill, pos);
                return;
            }
            bx -= 34f;
            if (Widgets.ButtonImage(new Rect(bx, head.y + 10f, 26f, 26f), TexButton.Rename)) Find.WindowStack.Add(new Dialog_RenameBill(bill));
            TooltipHandler.TipRegion(new Rect(bx, head.y + 10f, 26f, 26f), "MT_Rename".Translate());
            if (bill.recipe.products.Count == 1)
            {
                bx -= 30f;
                ThingDef pd = bill.recipe.products[0].thingDef;
                Widgets.InfoCardButton(bx, head.y + 10f, pd, GenStuff.DefaultStuffFor(pd));
            }
            // priority
            bx -= 50f;
            int pr = BillPrio.Get(bill);
            Rect pc = new Rect(bx, head.y + 8f, 44f, 28f);
            Color prc = pr <= 2 ? Look.Red : pr <= 4 ? Look.Orange : pr == 5 ? Muted : Look.Blue;
            Look.Box(pc, Look.Card2, prc);
            Look.Text(pc, "P" + pr, prc, GameFont.Small, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(pc, "MT_PrioTip".Translate(pr));
            if (Widgets.ButtonInvisible(pc) && Table != null)
            {
                var opts = new List<FloatMenuOption>();
                for (int p = 1; p <= 10; p++)
                {
                    int pp = p;
                    opts.Add(new FloatMenuOption("MT_PrioOption".Translate(p), () => BillPrio.Set(bill, pp, Table)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            // running / paused
            bx -= 130f;
            if (Look.Button(new Rect(bx, head.y + 8f, 124f, 28f), bill.suspended ? "MT_Resume".Translate() : "MT_Pause".Translate(), !bill.suspended))
            {
                bill.suspended = !bill.suspended;
                SoundDefOf.Click.PlayOneShotOnCamera();
            }
            Widgets.DrawBoxSolid(new Rect(r.x, head.yMax + 6f, r.width, 1f), Look.Line);
            // ---- columns ----
            float top = head.yMax + 14f;
            float h = r.yMax - top;
            float lw = Mathf.Floor(r.width * 0.27f), mw = Mathf.Floor(r.width * 0.36f);
            Rect left = new Rect(r.x, top, lw, h);
            Rect mid = new Rect(left.xMax + 12f, top, mw, h);
            Rect right = new Rect(mid.xMax + 12f, top, r.xMax - mid.xMax - 12f, h);
            Info(left);
            Settings(mid);
            Look.Box(right, Look.Card, Look.Line);
            Rect ir = right.ContractedBy(6f);
            float y = ir.y;
            Look.Text(new Rect(ir.x, y, ir.width, 20f), "MT_BD_Ingredients".Translate(), Color.white, GameFont.Small);
            y += 22f;
            DoIngredientConfigPane(ir.x, ref y, ir.width, ir.yMax - y);
        }

        // ---------- left: about the recipe ----------
        private void Info(Rect r)
        {
            Look.Box(r, Look.Card, Look.Line);
            Rect inner = r.ContractedBy(8f);
            RecipeDef rec = bill.recipe;
            var parts = new List<(string title, string text)>();
            if (!rec.description.NullOrEmpty()) parts.Add((null, rec.description));
            parts.Add(("WorkAmount".Translate(), rec.WorkAmountTotal(null).ToStringWorkAmount()));
            var ing = new StringBuilder();
            try
            {
                foreach (IngredientCount ic in rec.ingredients.OrderByDescending(i => i.CountFor(rec)))
                    if (!ic.filter.Summary.NullOrEmpty()) ing.AppendLine("• " + rec.IngredientValueGetter.BillRequirementsDescription(rec, ic));
                string extra = rec.IngredientValueGetter.ExtraDescriptionLine(rec);
                if (extra != null) ing.AppendLine(extra);
            }
            catch { }
            if (ing.Length > 0) parts.Add(("BillRequires".Translate(), ing.ToString().TrimEnd()));
            if (!rec.skillRequirements.NullOrEmpty()) parts.Add(("MinimumSkills".Translate(), rec.MinSkillString.TrimEnd()));
            if (rec.workSkill != null) parts.Add(("MT_BD_Skill".Translate(), rec.workSkill.LabelCap));
            Verse.Text.Font = GameFont.Small;
            float need = 60f;
            foreach (var (t, x) in parts) need += (t != null ? 20f : 0f) + Verse.Text.CalcHeight(x, inner.width - 16f) + 8f;
            need += rec.products.Count * 30f + 24f;
            Rect view = new Rect(0f, 0f, inner.width - 16f, need);
            Widgets.BeginScrollView(inner, ref infoScroll, view);
            try
            {
                float y = 0f;
                // products with icons
                if (rec.products.Count > 0)
                {
                    Look.Text(new Rect(0f, y, view.width, 20f), "MT_BD_Makes".Translate(), Muted, GameFont.Tiny);
                    y += 20f;
                    foreach (ThingDefCountClass p in rec.products)
                    {
                        Look.Icon(new Rect(0f, y, 26f, 26f), p.thingDef);
                        Look.Text(new Rect(32f, y, view.width - 32f, 26f), p.thingDef.LabelCap + " ×" + p.count, Color.white, GameFont.Small);
                        y += 30f;
                    }
                    y += 4f;
                }
                foreach (var (t, x) in parts)
                {
                    if (t != null)
                    {
                        Look.Text(new Rect(0f, y, view.width, 20f), t, Muted, GameFont.Tiny);
                        y += 20f;
                    }
                    float th = Verse.Text.CalcHeight(x, view.width);
                    Verse.Text.Font = GameFont.Small;
                    GUI.color = new Color(0.88f, 0.89f, 0.9f);
                    Widgets.Label(new Rect(0f, y, view.width, th), x);
                    GUI.color = Color.white;
                    y += th + 8f;
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        // ---------- middle: repeat, store, worker ----------
        private void Settings(Rect r)
        {
            Rect view = new Rect(0f, 0f, r.width - 16f, Mathf.Max(r.height, lastMidH));
            Widgets.BeginScrollView(r, ref midScroll, view);
            try
            {
                float y = 0f;
                y = RepeatCard(new Rect(0f, y, view.width, 0f)) + 10f;
                y = StoreCard(new Rect(0f, y, view.width, 0f)) + 10f;
                y = WorkerCard(new Rect(0f, y, view.width, 0f)) + 10f;
                lastMidH = y;
            }
            finally { Widgets.EndScrollView(); }
        }

        private float lastMidH = 600f;

        private static float CardTop(Rect r, string title, float h)
        {
            Look.Box(new Rect(r.x, r.y, r.width, h), Look.Card, Look.Line);
            Look.Text(new Rect(r.x + 10f, r.y + 6f, r.width - 20f, 20f), title, Color.white, GameFont.Small);
            return r.y + 30f;
        }

        // ×N / ≥X / ∞ and anything a mod adds
        private float RepeatCard(Rect r)
        {
            float h = RepeatHeight();
            float y = CardTop(r, "MT_BD_Repeat".Translate(), h);
            float x0 = r.x + 10f, w = r.width - 20f;
            BillRepeatModeDef[] defs = { BillRepeatModeDefOf.RepeatCount, BillRepeatModeDefOf.TargetCount, BillRepeatModeDefOf.Forever };
            string[] lab = { "MT_BD_ModeRepeat", "MT_BD_ModeTarget", "MT_BD_ModeForever" };
            bool other = !defs.Contains(bill.repeatMode);
            float bw = (w - 36f - 6f) / 3f;
            for (int i = 0; i < 3; i++)
            {
                Rect br = new Rect(x0 + i * (bw + 3f), y, bw, 28f);
                bool on = bill.repeatMode == defs[i];
                bool can = i != 1 || bill.recipe.WorkerCounter.CanCountProducts(bill);
                if (on) Look.Box(br, Look.GreenBg, Look.Green);
                if (Look.Button(br, lab[i].Translate(), false, can && !on))
                {
                    bill.repeatMode = defs[i];
                    if (i == 0 && bill.repeatCount < 1) bill.repeatCount = 1;
                    if (bill.paused && i != 1) bill.paused = false;
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
                if (on) Look.Text(br, lab[i].Translate(), Look.Green, GameFont.Small, TextAnchor.MiddleCenter);
            }
            Rect more = new Rect(x0 + w - 32f, y, 32f, 28f);
            if (Look.Button(more, "…", other)) BillRepeatModeUtility.MakeConfigFloatMenu(bill);
            TooltipHandler.TipRegion(more, bill.repeatMode.LabelCap);
            y += 36f;
            if (bill.repeatMode == BillRepeatModeDefOf.RepeatCount)
            {
                Look.Text(new Rect(x0, y, w, 20f), "RepeatCount".Translate(bill.repeatCount), Muted, GameFont.Small);
                y += 24f;
                int v = bill.repeatCount;
                Steps(new Rect(x0, y, w, 28f), ref v, ref repeatBuf, 1, 1);
                bill.repeatCount = Mathf.Max(1, v);
                y += 34f;
            }
            else if (bill.repeatMode == BillRepeatModeDefOf.TargetCount)
            {
                int have = 0;
                try { have = bill.recipe.WorkerCounter.CountProducts(bill); } catch { }
                string tgt = bill.targetCount < 999999 ? bill.targetCount.ToString() : "Infinite".Translate().ToLower().ToString();
                Look.Text(new Rect(x0, y, w, 20f), "MT_BD_Have".Translate(have, tgt), Color.white, GameFont.Small);
                y += 22f;
                Look.Bar(new Rect(x0, y, w, 6f), bill.targetCount > 0 ? (float)have / bill.targetCount : 0f, Look.Green);
                y += 10f;
                string counted = "";
                try { counted = bill.recipe.WorkerCounter.ProductsDescription(bill); } catch { }
                if (!counted.NullOrEmpty())
                {
                    string cl = "CountingProducts".Translate() + ": " + counted.CapitalizeFirst();
                    float ch = Verse.Text.CalcHeight(cl, w);
                    Look.Text(new Rect(x0, y, w, ch), cl, Muted, GameFont.Tiny, TextAnchor.UpperLeft);
                    y += Mathf.Max(18f, ch);
                }
                int old = bill.targetCount, v = bill.targetCount;
                Steps(new Rect(x0, y, w, 28f), ref v, ref targetBuf, bill.recipe.targetCountAdjustment, 0);
                bill.targetCount = v;
                bill.unpauseWhenYouHave = Mathf.Max(0, bill.unpauseWhenYouHave + (bill.targetCount - old));
                y += 34f;
                ThingDef prod = bill.recipe.ProducedThingDef;
                if (prod != null)
                {
                    if (prod.IsWeapon || prod.IsApparel) y = Check(x0, y, w, "IncludeEquipped".Translate(), ref bill.includeEquipped);
                    if (prod.IsApparel && prod.apparel.careIfWornByCorpse) y = Check(x0, y, w, "IncludeTainted".Translate(), ref bill.includeTainted);
                    string inc = bill.GetIncludeSlotGroup() == null ? "IncludeFromAll".Translate().ToString() : "IncludeSpecific".Translate(SlotGroup.GetGroupLabel(bill.GetIncludeSlotGroup())).ToString();
                    if (Look.Button(new Rect(x0, y, w, 26f), inc))
                    {
                        var opts = new List<FloatMenuOption> { new FloatMenuOption("IncludeFromAll".Translate(), () => bill.SetIncludeGroup(null)) };
                        FillOutput(opts, "IncludeSpecific".Translate(), g => bill.SetIncludeGroup(g));
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    y += 32f;
                    if (bill.recipe.products.Any(p => p.thingDef.useHitPoints))
                    {
                        Widgets.FloatRange(new Rect(x0, y, w, 32f), 975643279, ref bill.hpRange, 0f, 1f, "HitPoints", ToStringStyle.PercentZero);
                        bill.hpRange.min = Mathf.Round(bill.hpRange.min * 100f) / 100f;
                        bill.hpRange.max = Mathf.Round(bill.hpRange.max * 100f) / 100f;
                        y += 36f;
                    }
                    if (prod.HasComp(typeof(CompQuality)))
                    {
                        Widgets.QualityRange(new Rect(x0, y, w, 32f), 1098906561, ref bill.qualityRange);
                        y += 36f;
                    }
                    if (prod.MadeFromStuff) y = Check(x0, y, w, "LimitToAllowedStuff".Translate(), ref bill.limitToAllowedStuff);
                }
                y = Check(x0, y, w, "PauseWhenSatisfied".Translate(), ref bill.pauseWhenSatisfied);
                if (bill.pauseWhenSatisfied)
                {
                    Look.Text(new Rect(x0, y, w, 20f), "UnpauseWhenYouHave".Translate() + ": " + bill.unpauseWhenYouHave.ToString("F0"), Muted, GameFont.Small);
                    y += 22f;
                    int u = bill.unpauseWhenYouHave;
                    Steps(new Rect(x0, y, w, 28f), ref u, ref unpauseBuf, bill.recipe.targetCountAdjustment, 0);
                    bill.unpauseWhenYouHave = u;
                    if (bill.unpauseWhenYouHave >= bill.targetCount)
                    {
                        bill.unpauseWhenYouHave = bill.targetCount - 1;
                        unpauseBuf = bill.unpauseWhenYouHave.ToStringCached();
                    }
                    y += 34f;
                }
            }
            else
            {
                Look.Text(new Rect(x0, y, w, 20f), bill.repeatMode.LabelCap, Muted, GameFont.Small);
                y += 24f;
            }
            measuredRepeat = y - r.y + 6f;
            return r.y + h;
        }

        private float measuredRepeat = 120f;

        private float RepeatHeight() => Mathf.Max(80f, measuredRepeat);

        private static float Check(float x, float y, float w, string label, ref bool v)
        {
            Widgets.CheckboxLabeled(new Rect(x, y, w, 24f), label, ref v);
            return y + 26f;
        }

        // −100 −10 −1 [field] +1 +10 +100 (times the recipe's step)
        private static void Steps(Rect r, ref int v, ref string buf, int mult, int min)
        {
            int[] st = { -100, -10, -1, 1, 10, 100 };
            float field = Mathf.Max(60f, r.width * 0.22f);
            float bw = (r.width - field - 8f) / 6f;
            float x = r.x;
            for (int i = 0; i < 6; i++)
            {
                if (i == 3)
                {
                    Widgets.TextFieldNumeric(new Rect(x + 4f, r.y, field, r.height), ref v, ref buf, min, 999999);
                    x += field + 8f;
                }
                int d = st[i] * Mathf.Max(1, mult);
                if (Look.Button(new Rect(x, r.y, bw - 2f, r.height), (d > 0 ? "+" : "") + d))
                {
                    v = Mathf.Clamp(v + d, min, 999999);
                    buf = v.ToStringCached();
                    SoundDefOf.DragSlider.PlayOneShotOnCamera();
                }
                x += bw;
            }
        }

        private void FillOutput(List<FloatMenuOption> opts, string prefix, Action<ISlotGroup> selected)
        {
            try
            {
                object[] a = { opts, prefix, selected };
                fillOutput.Invoke(this, a);
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] stockpile list: " + e.Message, 0x51A7B42); }
        }

        private float StoreCard(Rect r)
        {
            float y = CardTop(r, "MT_BD_Store".Translate(), 70f);
            float x0 = r.x + 10f, w = r.width - 20f;
            string text = string.Format(bill.GetStoreMode().LabelCap, bill.GetSlotGroup() != null ? SlotGroup.GetGroupLabel(bill.GetSlotGroup()) : "");
            bool bad = bill.GetSlotGroup() != null && !bill.recipe.WorkerCounter.CanPossiblyStore(bill, bill.GetSlotGroup());
            if (bad) text += " (" + "IncompatibleLower".Translate() + ")";
            if (Look.Button(new Rect(x0, y, w, 28f), text.Truncate(w - 10f)))
            {
                var opts = new List<FloatMenuOption>();
                foreach (BillStoreModeDef sm in DefDatabase<BillStoreModeDef>.AllDefs.OrderBy(b => b.listOrder))
                {
                    if (sm == BillStoreModeDefOf.SpecificStockpile)
                    {
                        FillOutput(opts, BillStoreModeDefOf.SpecificStockpile.LabelCap, g => bill.SetStoreMode(BillStoreModeDefOf.SpecificStockpile, g));
                        continue;
                    }
                    BillStoreModeDef local = sm;
                    opts.Add(new FloatMenuOption(local.LabelCap, () => bill.SetStoreMode(local)));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            if (bad) Look.Box(new Rect(x0, y, 4f, 28f), Look.Orange);
            return r.y + 70f;
        }

        private float WorkerCard(Rect r)
        {
            bool skill = bill.PawnRestriction == null && bill.recipe.workSkill != null && !bill.MechsOnly;
            float h = skill ? 120f : 70f;
            float y = CardTop(r, "MT_BD_Worker".Translate(), h);
            float x0 = r.x + 10f, w = r.width - 20f;
            string label = bill.PawnRestriction != null ? bill.PawnRestriction.LabelShortCap
                : ModsConfig.IdeologyActive && bill.SlavesOnly ? "AnySlave".Translate().ToString()
                : ModsConfig.BiotechActive && bill.recipe.mechanitorOnlyRecipe ? "AnyMechanitor".Translate().ToString()
                : ModsConfig.BiotechActive && bill.MechsOnly ? "AnyMech".Translate().ToString()
                : ModsConfig.BiotechActive && bill.NonMechsOnly ? "AnyNonMech".Translate().ToString()
                : "AnyWorker".Translate().ToString();
            Widgets.Dropdown(new Rect(x0, y, w, 28f), bill, b => b.PawnRestriction, b => GeneratePawnRestrictionOptions(), label);
            y += 34f;
            if (skill)
            {
                Look.Text(new Rect(x0, y, w, 20f), "AllowedSkillRange".Translate(bill.recipe.workSkill.label) + ":", Muted, GameFont.Small);
                y += 22f;
                Widgets.IntRange(new Rect(x0, y, w, 28f), 0x4D54, ref bill.allowedSkillRange, 0, 20);
            }
            return r.y + h;
        }
    }

    // the swap: the game's bill window -> ours (setting, and once off for "Původní okno")
    public static class BillDialogSwap
    {
        private static bool bypass;
        private static readonly AccessTools.FieldRef<Dialog_BillConfig, IntVec3> posRef = AccessTools.FieldRefAccess<Dialog_BillConfig, IntVec3>("billGiverPos");
        private static readonly AccessTools.FieldRef<Dialog_BillConfig, Bill_Production> billRef = AccessTools.FieldRefAccess<Dialog_BillConfig, Bill_Production>("bill");

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), prefix: new HarmonyMethod(typeof(BillDialogSwap), nameof(Prefix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] bill window not available: " + e.Message); }
        }

        public static void Prefix(ref Window window)
        {
            if (bypass) { bypass = false; return; }
            if (window == null || window.GetType() != typeof(Dialog_BillConfig) || MTMod.S == null || !MTMod.S.billDialog) return;
            try
            {
                var d = (Dialog_BillConfig)window;
                Bill_Production b = billRef(d);
                if (b == null) return;
                window = new Dialog_MTBillConfig(b, posRef(d));
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] bill window swap: " + e.Message, 0x51A7B43); }
        }

        public static void OpenOriginal(Bill_Production b, IntVec3 pos)
        {
            bypass = true;
            Find.WindowStack.Add(new Dialog_BillConfig(b, pos));
            bypass = false;
        }
    }
}
