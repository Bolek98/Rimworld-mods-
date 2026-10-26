using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // Mišák Tabs – clear, modern inspect tabs in one dark style: bills first (health, info and
    // gear follow). The original tab is always one click away for features other mods add to it.
    // ---------------------------------------------------------------------------
    public class MTSettings : ModSettings
    {
        public bool recommended = true;
        public bool bills = true;
        public bool replaceOtherBillTabs = true;   // take over from Nice Bill Tab & co. on the bills tab

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref bills, "bills", true);
            Scribe_Values.Look(ref replaceOtherBillTabs, "replaceOtherBillTabs", true);
        }
    }

    public class MTMod : Mod
    {
        public const string Version = "0.1.0";
        public static MTSettings S;
        public static Harmony H;
        private static List<string> recBullets;

        public MTMod(ModContentPack content) : base(content)
        {
            S = GetSettings<MTSettings>();
            MUI.Init(new MTSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            H = new Harmony("bolek98.misaktabs");
            try { MUI.PatchWindow(H, typeof(MTMod)); } catch { }
            BillsTab.Patch(H);
            LongEventHandler.ExecuteWhenFinished(BillsTab.TakeOver);
            Log.Message("[Mišák Tabs] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Mišák Tabs";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "MT_Rec1".Translate(), "MT_Rec2".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                Check(l, "MT_Bills", ref S.bills, "bills");
                Check(l, "MT_ReplaceOther", ref S.replaceOtherBillTabs, "replaceOtherBillTabs");
                l.End();
            }
            finally { MUI.EndLock(rest); }
        }
    }

    // shared look
    public static class Look
    {
        public static readonly Color Bg = new Color32(21, 24, 28, 255);
        public static readonly Color Card = new Color32(29, 33, 38, 255);
        public static readonly Color Card2 = new Color32(38, 44, 50, 255);
        public static readonly Color Line = new Color32(58, 65, 72, 255);
        public static readonly Color Green = new Color32(124, 191, 94, 255);
        public static readonly Color GreenBg = new Color32(31, 42, 34, 255);
        public static readonly Color Orange = new Color32(224, 162, 74, 255);
        public static readonly Color Blue = new Color32(127, 188, 227, 255);
        public static readonly Color Red = new Color32(224, 100, 90, 255);

        public static void Box(Rect r, Color bg, Color? border = null)
        {
            Widgets.DrawBoxSolid(r, bg);
            if (border.HasValue) { GUI.color = border.Value; Widgets.DrawBox(r); GUI.color = Color.white; }
        }

        public static void Text(Rect r, string s, Color c, GameFont f = GameFont.Small, TextAnchor a = TextAnchor.MiddleLeft)
        {
            Verse.Text.Font = f;
            Verse.Text.Anchor = a;
            GUI.color = c;
            Widgets.Label(r, s);
            GUI.color = Color.white;
            Verse.Text.Anchor = TextAnchor.UpperLeft;
            Verse.Text.Font = GameFont.Small;
        }

        public static void Bar(Rect r, float pct, Color c)
        {
            Widgets.DrawBoxSolid(r, Card2);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, r.width * Mathf.Clamp01(pct), r.height), c);
        }

        public static bool Button(Rect r, string label, bool accent = false, bool enabled = true)
        {
            bool over = enabled && Mouse.IsOver(r);
            Box(r, accent ? GreenBg : (over ? Card2 : Card), accent ? Green : Line);
            Text(r, label, !enabled ? (Color)new Color32(110, 115, 120, 255) : accent ? Green : Color.white, GameFont.Small, TextAnchor.MiddleCenter);
            return enabled && Widgets.ButtonInvisible(r);
        }

        public static bool Chip(Rect r, string label, bool on)
        {
            Box(r, on ? GreenBg : Card2, on ? Green : (Color?)null);
            Text(r, label, on ? Green : (Color)new Color32(160, 167, 174, 255), GameFont.Tiny, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }

        public static void Icon(Rect r, Def def, Texture2D fallback = null)
        {
            Box(r, Card2, Line);
            Rect inner = r.ContractedBy(3f);
            try
            {
                if (def != null) Widgets.DefIcon(inner, def);
                else if (fallback != null) GUI.DrawTexture(inner, fallback, ScaleMode.ScaleToFit);
            }
            catch { }
        }
    }

    // ---------------------------------------------------------------------------
    // Bills tab
    // ---------------------------------------------------------------------------
    public static class BillsTab
    {
        public static readonly Vector2 Size = new Vector2(820f, 580f);
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        public static bool showOriginal;
        private static string search = "";
        private static int filter;            // 0 all, 1 available, 2 locked
        private static Vector2 recipeScroll, queueScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Bills), "FillTab"),
                    prefix: new HarmonyMethod(typeof(BillsTab), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(BillsTab), nameof(Postfix)));
                h.Patch(AccessTools.Method(typeof(ITab_Bills), "UpdateSize") ?? AccessTools.Method(typeof(InspectTabBase), "UpdateSize"),
                    postfix: new HarmonyMethod(typeof(BillsTab), nameof(SizePostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] bills tab not available: " + e.Message); }
        }

        // other bill tab mods (Nice Bill Tab …) draw their own tab in a prefix – theirs is removed
        // while ours is on, so the two do not fight over the same window
        public static void TakeOver()
        {
            if (!MTMod.S.bills || !MTMod.S.replaceOtherBillTabs) return;
            try
            {
                var m = AccessTools.Method(typeof(ITab_Bills), "FillTab");
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null) return;
                foreach (Patch p in info.Prefixes.Where(x => x.owner != MTMod.H.Id).ToList())
                {
                    MTMod.H.Unpatch(m, p.PatchMethod);
                    Log.Message("[Mišák Tabs] the bills tab of " + p.owner + " is replaced by Mišák Tabs (switch it back in the settings).");
                }
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] take over: " + e.Message); }
        }

        public static void SizePostfix(InspectTabBase __instance)
        {
            if (__instance is ITab_Bills && MTMod.S.bills && !showOriginal) sizeRef(__instance) = Size;
        }

        private static Building_WorkTable Table => Find.Selector.SingleSelectedThing as Building_WorkTable;

        public static bool Prefix(ITab_Bills __instance)
        {
            if (!MTMod.S.bills || showOriginal) return true;
            Building_WorkTable t = Table;
            if (t == null) return true;
            sizeRef(__instance) = Size;
            try { Draw(new Rect(0f, 0f, Size.x, Size.y), t); }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] bills tab: " + e, 0x51A7B11);
                showOriginal = true;
            }
            return false;
        }

        // the switch back from the original tab
        public static void Postfix(ITab_Bills __instance)
        {
            if (!MTMod.S.bills || !showOriginal) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 196f, 4f, 110f, 24f), "MT_OurLook".Translate(), true)) showOriginal = false;
        }

        // ---------- drawing ----------
        private static void Draw(Rect r, Building_WorkTable table)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            Header(head, table);
            Rect body = new Rect(r.x, head.yMax, r.width, r.height - head.height);
            Rect left = new Rect(body.x, body.y, 300f, body.height);
            Rect right = new Rect(left.xMax, body.y, body.width - left.width, body.height);
            Widgets.DrawBoxSolid(new Rect(left.xMax, body.y, 1f, body.height), Look.Line);
            Recipes(left.ContractedBy(10f), table);
            Queue(right.ContractedBy(10f), table);
        }

        private static WorkTypeDef WorkTypeOf(ThingDef bench)
        {
            foreach (WorkGiverDef wg in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                if (wg.fixedBillGiverDefs != null && wg.fixedBillGiverDefs.Contains(bench)) return wg.workType;
            return null;
        }

        private static void Header(Rect r, Building_WorkTable table)
        {
            Look.Box(r, Look.Card);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Look.Line);
            Look.Icon(new Rect(r.x + 10f, r.y + 9f, 36f, 36f), table.def);
            WorkTypeDef wt = WorkTypeOf(table.def);
            int doers = 0;
            if (wt != null && table.Map != null)
                doers = table.Map.mapPawns.FreeColonistsSpawned.Count(p => p.workSettings != null && !p.WorkTypeIsDisabled(wt) && p.workSettings.WorkIsActive(wt));
            int active = table.billStack.Bills.Count(b => !b.suspended && b.ShouldDoNow());
            Look.Text(new Rect(r.x + 56f, r.y + 6f, 400f, 24f), table.LabelCap, Color.white, GameFont.Medium);
            string sub = "MT_HeadSub".Translate(table.billStack.Count, active, doers, wt?.labelShort ?? "-");
            Look.Text(new Rect(r.x + 56f, r.y + 30f, 460f, 20f), sub, Muted, GameFont.Tiny);
            // paste (the game's own clipboard), original look
            Bill clip = BillUtility.Clipboard;
            bool canPaste = clip != null && table.def.AllRecipes.Contains(clip.recipe) && clip.recipe.AvailableNow && clip.recipe.AvailableOnNow(table) && table.billStack.Count < BillStack.MaxCount;
            Rect pr = new Rect(r.xMax - 250f, r.y + 13f, 110f, 28f);
            if (Look.Button(pr, "MT_Paste".Translate(), false, canPaste))
            {
                Bill b = clip.Clone();
                b.InitializeAfterClone();
                table.billStack.AddBill(b);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
            }
            if (clip != null) TooltipHandler.TipRegion(pr, "MT_PasteTip".Translate(clip.LabelCap));
            Rect orig = new Rect(r.xMax - 132f, r.y + 13f, 120f, 28f);
            if (Look.Button(orig, "MT_Original".Translate())) showOriginal = true;
            TooltipHandler.TipRegion(orig, "MT_OriginalTip".Translate());
        }

        private static bool Locked(RecipeDef rec, out string why)
        {
            why = null;
            if (rec.AvailableNow) return false;
            var missing = new List<string>();
            if (rec.researchPrerequisite != null && !rec.researchPrerequisite.IsFinished) missing.Add(rec.researchPrerequisite.LabelCap);
            if (rec.researchPrerequisites != null)
                foreach (ResearchProjectDef p in rec.researchPrerequisites)
                    if (!p.IsFinished) missing.Add(p.LabelCap);
            why = missing.Count > 0 ? "MT_NeedResearch".Translate(string.Join(", ", missing.Distinct().ToArray())).ToString() : "MT_NotAvailable".Translate().ToString();
            return true;
        }

        private static string Ingredients(RecipeDef rec)
        {
            try
            {
                var parts = rec.ingredients.Take(3).Select(i => i.SummaryFor(rec)).ToList();
                return string.Join(" · ", parts.ToArray());
            }
            catch { return ""; }
        }

        private static void Recipes(Rect r, Building_WorkTable table)
        {
            float y = r.y;
            Rect sr = new Rect(r.x, y, r.width, 30f);
            Look.Box(sr, Look.Card2, Look.Line);
            search = Widgets.TextField(new Rect(sr.x + 6f, sr.y + 3f, sr.width - 12f, 24f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 10f, sr.y, sr.width, 30f), "MT_Search".Translate(), Muted, GameFont.Tiny);
            y += 36f;
            string[] names = { "MT_All".Translate(), "MT_Available".Translate(), "MT_Locked".Translate() };
            float cx = r.x;
            for (int i = 0; i < 3; i++)
            {
                float w = Verse.Text.CalcSize(names[i]).x + 18f;
                if (Look.Chip(new Rect(cx, y, w, 22f), names[i], filter == i)) filter = i;
                cx += w + 6f;
            }
            y += 30f;
            string q = (search ?? "").Trim();
            var list = table.def.AllRecipes.Where(rec => rec.AvailableOnNow(table) && (q.Length == 0 || rec.LabelCap.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            var rows = new List<(RecipeDef rec, bool locked, string why)>();
            foreach (RecipeDef rec in list)
            {
                bool l = Locked(rec, out string why);
                if (filter == 1 && l || filter == 2 && !l) continue;
                rows.Add((rec, l, why));
            }
            rows = rows.OrderBy(x => x.locked).ThenBy(x => x.rec.label).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            const float rowH = 48f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(outR, ref recipeScroll, view);
            int first = Mathf.Max(0, (int)(recipeScroll.y / rowH) - 1), last = Mathf.Min(rows.Count - 1, (int)((recipeScroll.y + outR.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                var (rec, locked, why) = rows[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
                bool over = Mouse.IsOver(row);
                Look.Box(row, over && !locked ? Look.Card2 : Look.Card);
                Look.Icon(new Rect(row.x + 6f, row.y + 6f, 32f, 32f), rec.UIIconThing, rec.UIIcon);
                Look.Text(new Rect(row.x + 46f, row.y + 3f, row.width - 110f, 20f), rec.LabelCap.ToString().Truncate(row.width - 110f), locked ? Muted : Color.white);
                Look.Text(new Rect(row.x + 46f, row.y + 22f, row.width - 110f, 18f), (locked ? why : Ingredients(rec)).Truncate(row.width - 110f), locked ? Look.Orange : Muted, GameFont.Tiny);
                Look.Text(new Rect(row.xMax - 66f, row.y, 60f, row.height), locked ? "MT_LockedShort".Translate() : "MT_Add".Translate(), locked ? Muted : Look.Green, GameFont.Tiny, TextAnchor.MiddleRight);
                if (over) TooltipHandler.TipRegion(row, rec.description.NullOrEmpty() ? rec.LabelCap.ToString() : rec.description);
                if (!locked && Widgets.ButtonInvisible(row)) Add(table, rec);
            }
            Widgets.EndScrollView();
        }

        private static void Add(Building_WorkTable table, RecipeDef recipe)
        {
            if (table.billStack.Count >= BillStack.MaxCount)
            {
                Messages.Message("MT_Full".Translate(BillStack.MaxCount), MessageTypeDefOf.RejectInput, false);
                return;
            }
            if (ModsConfig.BiotechActive && recipe.mechanitorOnlyRecipe && !table.Map.mapPawns.FreeColonists.Any(MechanitorUtility.IsMechanitor))
                Find.WindowStack.Add(new Dialog_MessageBox("RecipeRequiresMechanitor".Translate(recipe.LabelCap)));
            else if (!table.Map.mapPawns.FreeColonists.Any(col => recipe.PawnSatisfiesSkillRequirements(col)))
                Bill.CreateNoPawnsWithSkillDialog(recipe);
            Bill bill = recipe.MakeNewBill();
            table.billStack.AddBill(bill);
            if (recipe.conceptLearned != null) PlayerKnowledgeDatabase.KnowledgeDemonstrated(recipe.conceptLearned, KnowledgeAmount.Total);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }

        private static Pawn Doer(Bill b, Map map)
        {
            if (map == null) return null;
            foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                if (p.CurJob != null && p.CurJob.bill == b) return p;
            return null;
        }

        private static void Queue(Rect r, Building_WorkTable table)
        {
            Look.Text(new Rect(r.x, r.y, r.width, 20f), "MT_Queue".Translate(table.billStack.Count, BillStack.MaxCount), Muted, GameFont.Tiny);
            List<Bill> bills = table.billStack.Bills;
            Rect outR = new Rect(r.x, r.y + 24f, r.width, r.height - 24f);
            const float cardH = 74f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(bills.Count * (cardH + 6f), outR.height - 2f));
            Widgets.BeginScrollView(outR, ref queueScroll, view);
            Bill toDelete = null, toUp = null, toDown = null;
            for (int i = 0; i < bills.Count; i++)
            {
                Bill b = bills[i];
                Rect c = new Rect(0f, i * (cardH + 6f), view.width, cardH);
                Pawn doer = Doer(b, table.Map);
                bool waiting = !b.suspended && b.ShouldDoNow() && Find.TickManager.TicksGame <= b.nextTickToSearchForIngredients;
                Color border = doer != null ? Look.Green : waiting ? Look.Orange : Look.Line;
                Look.Box(c, doer != null ? Look.GreenBg : Look.Card, border);
                Look.Icon(new Rect(c.x + 8f, c.y + 8f, 34f, 34f), b.recipe.UIIconThing, b.recipe.UIIcon);
                float tx = c.x + 52f, tw = c.width - 52f - 150f;
                Look.Text(new Rect(tx, c.y + 6f, tw, 22f), b.LabelCap.ToString().Truncate(tw), b.suspended ? Muted : Color.white);
                string status;
                Color sc = Muted;
                float progress = -1f;
                if (b.suspended) status = "MT_Suspended".Translate();
                else if (doer != null) { status = "MT_DoingNow".Translate(doer.LabelShort); sc = Look.Green; }
                else if (waiting) { status = "MT_NoIngredients".Translate(); sc = Look.Orange; }
                else if (!b.ShouldDoNow()) status = "MT_Done".Translate();
                else status = "MT_Waiting".Translate();
                string repeat = "";
                if (b is Bill_Production bp)
                {
                    if (bp.repeatMode == BillRepeatModeDefOf.Forever) repeat = "MT_Forever".Translate();
                    else if (bp.repeatMode == BillRepeatModeDefOf.RepeatCount) repeat = "MT_RepeatLeft".Translate(bp.repeatCount);
                    else if (bp.repeatMode == BillRepeatModeDefOf.TargetCount)
                    {
                        int have = 0;
                        try { have = b.recipe.WorkerCounter.CountProducts(bp); } catch { }
                        repeat = "MT_Target".Translate(have, bp.targetCount).ToString() + (bp.paused ? " · " + "MT_PausedSatisfied".Translate() : "");
                        progress = bp.targetCount > 0 ? (float)have / bp.targetCount : 0f;
                    }
                }
                Look.Text(new Rect(tx, c.y + 28f, tw, 18f), status, sc, GameFont.Tiny);
                Look.Text(new Rect(tx, c.y + 44f, tw, 18f), repeat, Muted, GameFont.Tiny);
                if (progress >= 0f) Look.Bar(new Rect(tx, c.y + 63f, tw, 4f), progress, Look.Green);
                // buttons
                float bx = c.xMax - 146f, by = c.y + 8f;
                if (Look.Button(new Rect(bx, by, 26f, 26f), "▲", false, i > 0)) toUp = b;
                if (Look.Button(new Rect(bx + 30f, by, 26f, 26f), "▼", false, i < bills.Count - 1)) toDown = b;
                if (Look.Button(new Rect(bx + 60f, by, 54f, 26f), b.suspended ? "MT_Resume".Translate() : "MT_Pause".Translate())) b.suspended = !b.suspended;
                if (Look.Button(new Rect(bx + 118f, by, 26f, 26f), "✕")) toDelete = b;
                if (Look.Button(new Rect(bx, by + 32f, 70f, 26f), "MT_Edit".Translate(), true) && b is Bill_Production bp2)
                    Find.WindowStack.Add(new Dialog_BillConfig(bp2, table.Position));
                if (Look.Button(new Rect(bx + 74f, by + 32f, 70f, 26f), "MT_Copy".Translate()))
                {
                    BillUtility.Clipboard = b.Clone();
                    Messages.Message("MT_Copied".Translate(b.LabelCap), MessageTypeDefOf.SilentInput, false);
                }
            }
            if (bills.Count == 0)
                Look.Text(new Rect(0f, 0f, view.width, 60f), "MT_NoBills".Translate(), Muted, GameFont.Small, TextAnchor.MiddleCenter);
            Widgets.EndScrollView();
            if (toUp != null) { table.billStack.Reorder(toUp, -1); SoundDefOf.Tick_High.PlayOneShotOnCamera(); }
            if (toDown != null) { table.billStack.Reorder(toDown, 1); SoundDefOf.Tick_Low.PlayOneShotOnCamera(); }
            if (toDelete != null) { table.billStack.Delete(toDelete); SoundDefOf.Click.PlayOneShotOnCamera(); }
        }
    }
}
