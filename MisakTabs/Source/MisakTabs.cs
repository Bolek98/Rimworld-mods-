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
        public bool health = true;                 // 0.2.0: our health tab
        public bool replaceOtherHealthTabs = true; // 0.2.0: take over the health tab from other mods
        // 0.6.0: the player's window sizes (drag the corner) and the width of the recipe list
        public float billsW = 820f, billsH = 580f, billsLeft = 300f, healthW = 820f, healthH = 540f;
        public bool character = true, social = true;                                  // 0.9.0
        public float charW = 820f, charH = 560f, socialW = 800f, socialH = 540f;      // 0.9.0

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref bills, "bills", true);
            Scribe_Values.Look(ref replaceOtherBillTabs, "replaceOtherBillTabs", true);
            Scribe_Values.Look(ref health, "health", true);
            Scribe_Values.Look(ref replaceOtherHealthTabs, "replaceOtherHealthTabs", true);
            Scribe_Values.Look(ref billsW, "billsW", 820f);
            Scribe_Values.Look(ref billsH, "billsH", 580f);
            Scribe_Values.Look(ref billsLeft, "billsLeft", 300f);
            Scribe_Values.Look(ref healthW, "healthW", 820f);
            Scribe_Values.Look(ref healthH, "healthH", 540f);
            Scribe_Values.Look(ref character, "character", true);
            Scribe_Values.Look(ref social, "social", true);
            Scribe_Values.Look(ref charW, "charW", 820f);
            Scribe_Values.Look(ref charH, "charH", 560f);
            Scribe_Values.Look(ref socialW, "socialW", 800f);
            Scribe_Values.Look(ref socialH, "socialH", 540f);
        }
    }

    public class MTMod : Mod
    {
        public const string Version = "0.9.2";
        public static MTSettings S;
        public static Harmony H;
        public static ModContentPack Pack;   // 0.7.0
        private static List<string> recBullets;

        public MTMod(ModContentPack content) : base(content)
        {
            S = GetSettings<MTSettings>();
            Pack = content;
            MUI.Init(new MTSettings(), null, "recommended", "billsW", "billsH", "billsLeft", "healthW", "healthH", "charW", "charH", "socialW", "socialH");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            H = new Harmony("bolek98.misaktabs");
            try { MUI.PatchWindow(H, typeof(MTMod)); } catch { }
            BillsTab.Patch(H);
            BillPrio.Patch(H);
            HealthTab.Patch(H);
            PawnTabs.Patch(H);   // 0.9.0
            LongEventHandler.ExecuteWhenFinished(BillsTab.TakeOver);
            LongEventHandler.ExecuteWhenFinished(HealthTab.TakeOver);
            Log.Message("[Mišák Tabs] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Mišák Tabs";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "MT_Rec1".Translate(), "MT_Rec2".Translate(), "MT_Rec3".Translate() };
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
                Check(l, "MT_Health", ref S.health, "health");
                Check(l, "MT_ReplaceOtherHealth", ref S.replaceOtherHealthTabs, "replaceOtherHealthTabs");
                Check(l, "MT_Character", ref S.character, "character");
                Check(l, "MT_Social", ref S.social, "social");
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
            // 0.4.2: the GUI state is always put back, also when the label throws
            try { Widgets.Label(r, s); }
            finally
            {
                GUI.color = Color.white;
                Verse.Text.Anchor = TextAnchor.UpperLeft;
                Verse.Text.Font = GameFont.Small;
            }
        }

        // 0.4.2: puts the GUI state back after a drawing error (used by the tab prefixes)
        public static void ResetGui()
        {
            GUI.color = Color.white;
            Verse.Text.Anchor = TextAnchor.UpperLeft;
            Verse.Text.Font = GameFont.Small;
        }

        // 0.4.2: Esc in a search field only leaves the field (the tab is not stuck in it).
        // Call right before Widgets.TextField with the name given to it via GUI.SetNextControlName.
        public static void EscUnfocus(string control)
        {
            Event e = Event.current;
            if (e != null && e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape && GUI.GetNameOfFocusedControl() == control)
            {
                GUI.FocusControl(null);
                e.Use();
            }
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
            bool drawn = false;
            try
            {
                // 0.4.2: a recipe without any icon draws the fallback instead of nothing
                if (def is RecipeDef rd && rd.UIIconThing == null && rd.UIIcon == null) def = null;
                if (def != null) { Widgets.DefIcon(inner, def); drawn = true; }
                else if (fallback != null) { GUI.DrawTexture(inner, fallback, ScaleMode.ScaleToFit); drawn = true; }
            }
            catch { drawn = false; }
            finally { GUI.color = Color.white; }
            if (!drawn) Text(r, "?", Line, GameFont.Small, TextAnchor.MiddleCenter);
        }
    }

    // ---------------------------------------------------------------------------
    // Bills tab
    // ---------------------------------------------------------------------------
    // 0.6.0: the bills and health tabs can be made bigger – drag the corner (top right) or pick a size
    public static class TabSize
    {
        private static string dragId;
        private static Vector2 startMouse, startSize;

        // the tab grows up and to the right from the bottom left corner; it must stay on the screen
        public static Vector2 Fit(float w, float h, float minW, float minH)
        {
            float maxW = Mathf.Max(minW, UI.screenWidth - 8f);
            float maxH = Mathf.Max(minH, UI.screenHeight - 35f - 165f - 6f);
            return new Vector2(Mathf.Clamp(w, minW, maxW), Mathf.Clamp(h, minH, maxH));
        }

        public static bool Grip(Rect r, string id, ref float w, ref float h, float minW, float minH)
        {
            Rect g = new Rect(r.xMax - 18f, r.y, 18f, 12f);
            bool over = Mouse.IsOver(g), on = dragId == id;
            Color c = over || on ? Look.Green : new Color(1f, 1f, 1f, 0.35f);
            for (int i = 0; i < 3; i++)
                Widgets.DrawBoxSolid(new Rect(g.x + 4f + i * 4f, g.y + 2f + (2 - i) * 3f, 10f - i * 4f + (i == 0 ? 2f : 0f), 1.5f), c);
            if (over) TooltipHandler.TipRegion(g, "MT_GripTip".Translate());
            Vector2 m = UI.MousePositionOnUIInverted;
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && over)
            {
                dragId = id; startMouse = m; startSize = new Vector2(r.width, r.height);
                Event.current.Use();
                return false;
            }
            if (!on) return false;
            if (!Input.GetMouseButton(0)) { dragId = null; MTMod.S.Write(); return true; }
            Vector2 s = Fit(startSize.x + (m.x - startMouse.x), startSize.y + (startMouse.y - m.y), minW, minH);
            w = s.x; h = s.y;
            return false;
        }

        public static void Menu(Action<Vector2> set, Vector2 normal, float normalLeft, Action<float> setLeft)
        {
            Vector2 max = Fit(9999f, 9999f, normal.x, normal.y);
            var big = new Vector2(Mathf.Min(max.x, normal.x * 1.3f), Mathf.Min(max.y, normal.y * 1.3f));
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("MT_SizeNormal".Translate(), () => { set(normal); setLeft?.Invoke(normalLeft); MTMod.S.Write(); }),
                new FloatMenuOption("MT_SizeBig".Translate(), () => { set(big); setLeft?.Invoke(normalLeft * 1.3f); MTMod.S.Write(); }),
                new FloatMenuOption("MT_SizeMax".Translate(), () => { set(max); setLeft?.Invoke(Mathf.Min(max.x * 0.4f, 520f)); MTMod.S.Write(); }),
            };
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }

    public static class BillsTab
    {
        public static Vector2 Size => TabSize.Fit(MTMod.S.billsW, MTMod.S.billsH, 760f, 420f);
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        public static bool showOriginal;
        private static string search = "";
        private static int filter;            // 0 all, 1 available, 2 locked, 3 not in the queue yet (0.6.0)
        private static bool splitDrag;
        private static Vector2 recipeScroll, queueScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Bills), "FillTab"),
                    prefix: new HarmonyMethod(typeof(BillsTab), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(BillsTab), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] bills tab not available: " + e.Message); }
            // 0.1.1: the size lives in the base class (ITab_Bills has no UpdateSize of its own)
            try
            {
                h.Patch(AccessTools.DeclaredMethod(typeof(InspectTabBase), "UpdateSize"),
                    postfix: new HarmonyMethod(typeof(BillsTab), nameof(SizePostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] bills tab size: " + e.Message); }
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
            else if (HealthTab.Ours(__instance)) sizeRef(__instance) = HealthTab.Size;
            else if (PawnTabs.OursSocial(__instance)) sizeRef(__instance) = PawnTabs.SocialSize;
            else if (__instance is ITab_Pawn_Social && sizeRef(__instance) == PawnTabs.SocialSize) sizeRef(__instance) = new Vector2(540f, 510f);
            else if (__instance is ITab_Pawn_Health && sizeRef(__instance) == HealthTab.Size) sizeRef(__instance) = new Vector2(630f, 430f);
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
                Look.ResetGui();
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
            float lw = Mathf.Clamp(MTMod.S.billsLeft, 220f, Mathf.Max(220f, body.width - 500f));
            Rect left = new Rect(body.x, body.y, lw, body.height);
            Rect right = new Rect(left.xMax, body.y, body.width - left.width, body.height);
            // 0.6.0: drag the line between the recipes and the queue
            Rect split = new Rect(left.xMax - 4f, body.y, 8f, body.height);
            bool overSplit = Mouse.IsOver(split);
            Widgets.DrawBoxSolid(new Rect(left.xMax - (overSplit || splitDrag ? 1f : 0f), body.y, overSplit || splitDrag ? 3f : 1f, body.height), overSplit || splitDrag ? Look.Green : Look.Line);
            if (overSplit) TooltipHandler.TipRegion(split, "MT_SplitTip".Translate());
            if (Event.current.type == EventType.MouseDown && Event.current.button == 0 && overSplit) { splitDrag = true; Event.current.Use(); }
            if (splitDrag)
            {
                if (!Input.GetMouseButton(0)) { splitDrag = false; MTMod.S.Write(); }
                else MTMod.S.billsLeft = Mathf.Clamp(Event.current.mousePosition.x - body.x, 220f, Mathf.Max(220f, body.width - 500f));
            }
            Recipes(left.ContractedBy(10f), table);
            Queue(right.ContractedBy(10f), table);
            if (TabSize.Grip(r, "bills", ref MTMod.S.billsW, ref MTMod.S.billsH, 760f, 420f)) { }
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
            // 0.5.0: the workbench menu – copy all bills, paste them here or into several workbenches
            Rect sz = new Rect(r.xMax - 318f, r.y + 13f, 28f, 28f);
            if (Look.Button(sz, "□")) TabSize.Menu(v => { MTMod.S.billsW = v.x; MTMod.S.billsH = v.y; }, new Vector2(820f, 580f), 300f, w => MTMod.S.billsLeft = w);
            TooltipHandler.TipRegion(sz, "MT_SizeTip".Translate());
            Rect bm = new Rect(r.xMax - 284f, r.y + 13f, 28f, 28f);
            if (Look.Button(bm, "…")) BenchMenu(table);
            TooltipHandler.TipRegion(bm, "MT_BenchMenuTip".Translate());
            Rect orig = new Rect(r.xMax - 132f, r.y + 13f, 120f, 28f);
            if (Look.Button(orig, "MT_Original".Translate())) showOriginal = true;
            TooltipHandler.TipRegion(orig, "MT_OriginalTip".Translate());
        }

        // 0.5.0
        private static void BenchMenu(Building_WorkTable table)
        {
            var opts = new List<FloatMenuOption>();
            opts.Add(table.billStack.Count > 0
                ? new FloatMenuOption("MT_CopyAll".Translate(table.billStack.Count), () => BenchClipboard.Copy(table))
                : new FloatMenuOption("MT_CopyAll".Translate(0), null));
            string from = BenchClipboard.From ?? "-";
            opts.Add(BenchClipboard.CanPaste(table)
                ? new FloatMenuOption("MT_PasteAll".Translate(BenchClipboard.Count, from), () =>
                {
                    int n = BenchClipboard.PasteInto(table);
                    Messages.Message("MT_PastedInto".Translate(n, 1), MessageTypeDefOf.SilentInput, false);
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                })
                : new FloatMenuOption("MT_PasteAll".Translate(BenchClipboard.Count, from), null));
            opts.Add(BenchClipboard.Count > 0
                ? new FloatMenuOption("MT_PasteSeveral".Translate() + "…", () => Find.WindowStack.Add(new Dialog_PasteBenches(table)))
                : new FloatMenuOption("MT_PasteSeveral".Translate() + "…", null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // 0.5.0: rename (the game's own bill name) and linked bills
        private static void BillMenu(Bill b, Building_WorkTable table)
        {
            var opts = new List<FloatMenuOption>();
            if (b is Bill_Production bp)
                opts.Add(new FloatMenuOption("MT_Rename".Translate() + "…", () => Find.WindowStack.Add(new Dialog_RenameBill(bp))));
            opts.Add(new FloatMenuOption("MT_LinkWith".Translate() + "…", () =>
            {
                var cands = BillLinks.Candidates(b, table.Map);
                var sub = new List<FloatMenuOption>();
                foreach (Bill o in cands)
                {
                    Bill oo = o;
                    Thing bench = o.billStack?.billGiver as Thing;
                    string where = bench != null ? bench.LabelCap + " (" + bench.Position.x + ", " + bench.Position.z + ")" : "?";
                    sub.Add(new FloatMenuOption(where + ": " + o.LabelCap, () =>
                    {
                        BillLinks.Link(b, oo);
                        Messages.Message("MT_Linked".Translate(b.LabelCap, where), MessageTypeDefOf.SilentInput, false);
                    }));
                }
                if (sub.Count == 0) sub.Add(new FloatMenuOption("MT_NoLinkTargets".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(sub));
            }));
            if (BillLinks.GroupOf(b) != 0)
                opts.Add(new FloatMenuOption("MT_Unlink".Translate(), () => BillLinks.Unlink(b)));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // 0.5.0: − / + next to the count: click ±1, Shift ±10, Ctrl ±100 (like the game)
        private static void CountButtons(Rect r, Bill_Production bp)
        {
            bool repeat = bp.repeatMode == BillRepeatModeDefOf.RepeatCount, target = bp.repeatMode == BillRepeatModeDefOf.TargetCount;
            if (!repeat && !target) return;
            Rect minus = new Rect(r.x, r.y, 27f, r.height), plus = new Rect(r.x + 31f, r.y, 27f, r.height);
            int delta = 0;
            if (Look.Button(minus, "-")) delta = -1;
            if (Look.Button(plus, "+")) delta = 1;
            TooltipHandler.TipRegion(minus, "MT_CountTip".Translate());
            TooltipHandler.TipRegion(plus, "MT_CountTip".Translate());
            if (delta == 0) return;
            int step = delta * GenUI.CurrentAdjustmentMultiplier();
            if (repeat) bp.repeatCount = Mathf.Max(0, bp.repeatCount + step);
            else
            {
                int adj = Mathf.Max(1, bp.recipe.targetCountAdjustment);
                int old = bp.targetCount;
                bp.targetCount = Mathf.Max(1, bp.targetCount + step * adj);
                bp.unpauseWhenYouHave = Mathf.Clamp(bp.unpauseWhenYouHave + (bp.targetCount - old), 0, bp.targetCount - 1);
            }
            (delta > 0 ? SoundDefOf.Tick_High : SoundDefOf.Tick_Low).PlayOneShotOnCamera();
        }

        // 0.5.0: what a "do until you have X" bill counts (the game's options, set in Edit)
        private static string CountInfo(Bill_Production bp)
        {
            var parts = new List<string>();
            if (bp.includeEquipped) parts.Add("MT_InclEquipped".Translate());
            ISlotGroup g = bp.GetIncludeSlotGroup();
            if (g != null)
            {
                string name = g is IRenameable rn ? rn.RenamableLabel : g is SlotGroup sg && sg.parent is IRenameable pr ? pr.RenamableLabel : g.GroupingLabel;
                parts.Add("MT_OnlyIn".Translate(name));
            }
            return parts.Count == 0 ? "" : " · " + string.Join(" · ", parts.ToArray());
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
            Look.EscUnfocus("MT_BillSearch");
            GUI.SetNextControlName("MT_BillSearch");
            search = Widgets.TextField(new Rect(sr.x + 6f, sr.y + 3f, sr.width - 12f, 24f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 10f, sr.y, sr.width, 30f), "MT_Search".Translate(), Muted, GameFont.Tiny);
            y += 36f;
            string[] names = { "MT_All".Translate(), "MT_Available".Translate(), "MT_Locked".Translate(), "MT_NotAdded".Translate() };
            float cx = r.x;
            for (int i = 0; i < names.Length; i++)
            {
                float w = Verse.Text.CalcSize(names[i]).x + 18f;
                if (cx + w > r.xMax && cx > r.x) { cx = r.x; y += 28f; }   // 0.6.0: wraps in a narrow list
                if (Look.Chip(new Rect(cx, y, w, 22f), names[i], filter == i)) filter = i;
                if (i == 3) TooltipHandler.TipRegion(new Rect(cx, y, w, 22f), "MT_NotAddedTip".Translate());
                cx += w + 6f;
            }
            y += 30f;
            var inQueue = new HashSet<RecipeDef>(table.billStack.Bills.Select(b => b.recipe));
            string q = (search ?? "").Trim();
            var list = table.def.AllRecipes.Where(rec => rec.AvailableOnNow(table) && (q.Length == 0 || rec.LabelCap.ToString().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            var rows = new List<(RecipeDef rec, bool locked, string why)>();
            foreach (RecipeDef rec in list)
            {
                bool l = Locked(rec, out string why);
                if (filter == 1 && l || filter == 2 && !l || filter == 3 && (l || inQueue.Contains(rec))) continue;
                rows.Add((rec, l, why));
            }
            rows = rows.OrderBy(x => x.locked).ThenBy(x => x.rec.label).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            const float rowH = 48f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(outR, ref recipeScroll, view);
            try
            {
                int first = Mathf.Max(0, (int)(recipeScroll.y / rowH) - 1), last = Mathf.Min(rows.Count - 1, (int)((recipeScroll.y + outR.height) / rowH) + 1);
                for (int i = first; i <= last; i++)
                {
                    var (rec, locked, why) = rows[i];
                    Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
                    bool over = Mouse.IsOver(row);
                    Look.Box(row, over && !locked ? Look.Card2 : Look.Card);
                    Look.Icon(new Rect(row.x + 6f, row.y + 6f, 32f, 32f), rec.UIIconThing, rec.UIIcon);
                    // 0.2.1: the text ends before the info button (long names ran under the "i")
                    float tw = row.width - 46f - 100f;
                    Look.Text(new Rect(row.x + 46f, row.y + 3f, tw, 20f), rec.LabelCap.ToString().Truncate(tw), locked ? Muted : Color.white);
                    Look.Text(new Rect(row.x + 46f, row.y + 22f, tw, 18f), (locked ? why : Ingredients(rec)).Truncate(tw), locked ? Look.Orange : Muted, GameFont.Tiny);
                    Look.Text(new Rect(row.xMax - 66f, row.y, 60f, row.height), locked ? "MT_LockedShort".Translate() : "MT_Add".Translate(), locked ? Muted : Look.Green, GameFont.Tiny, TextAnchor.MiddleRight);
                    // 0.1.2: what the thing is – a rich tooltip and the game's info card
                    Rect info = new Rect(row.xMax - 92f, row.y + (row.height - 22f) / 2f, 22f, 22f);
                    if (InfoButton(info, rec)) { }
                    else
                    {
                        if (over) TooltipHandler.TipRegion(row, new TipSignal(() => RecipeInfo(rec), rec.GetHashCode()));
                        if (!locked && Widgets.ButtonInvisible(row)) Add(table, rec);
                    }
                }
            }
            finally { Widgets.EndScrollView(); }   // 0.4.2
        }

        private static void OpenInfo(RecipeDef rec)
        {
            ThingDef prod = rec.ProducedThingDef;
            if (prod != null) Find.WindowStack.Add(new Dialog_InfoCard(prod, prod.MadeFromStuff ? GenStuff.DefaultStuffFor(prod) : null));
            else Find.WindowStack.Add(new Dialog_InfoCard(rec));
        }

        private static bool InfoButton(Rect r, RecipeDef rec)
        {
            bool over = Mouse.IsOver(r);
            Look.Box(r, over ? Look.Card2 : Look.Card, Look.Line);
            Look.Text(r, "i", over ? Color.white : Muted, GameFont.Small, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(r, "MT_InfoTip".Translate());
            if (Widgets.ButtonInvisible(r)) { OpenInfo(rec); return true; }
            return over;
        }

        private static readonly Dictionary<RecipeDef, string> infoCache = new Dictionary<RecipeDef, string>();

        // what the recipe makes and what it needs, in a few lines
        public static string RecipeInfo(RecipeDef rec)
        {
            if (infoCache.TryGetValue(rec, out string s)) return s;
            var sb = new System.Text.StringBuilder();
            try
            {
                sb.AppendLine("<b>" + rec.LabelCap + "</b>");
                ThingDef prod = rec.ProducedThingDef;
                ThingDef stuff = prod != null && prod.MadeFromStuff ? GenStuff.DefaultStuffFor(prod) : null;
                string desc = prod?.description ?? rec.description;
                if (!desc.NullOrEmpty()) sb.AppendLine(desc.Length > 400 ? desc.Substring(0, 400) + "…" : desc);
                sb.AppendLine();
                if (prod != null)
                {
                    int count = rec.products != null && rec.products.Count > 0 ? rec.products[0].count : 1;
                    sb.AppendLine("MT_Info_Makes".Translate(prod.LabelCap, count, prod.GetStatValueAbstract(StatDefOf.MarketValue, stuff).ToStringMoney(), prod.GetStatValueAbstract(StatDefOf.Mass, stuff).ToString("0.##")));
                    if (prod.IsApparel)
                        sb.AppendLine("MT_Info_Armor".Translate(prod.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff).ToStringPercent(), prod.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff).ToStringPercent(), prod.GetStatValueAbstract(StatDefOf.Insulation_Cold, stuff).ToString("0"), prod.GetStatValueAbstract(StatDefOf.Insulation_Heat, stuff).ToString("0")));
                    if (prod.IsRangedWeapon && prod.Verbs != null && prod.Verbs.Count > 0)
                    {
                        VerbProperties v = prod.Verbs[0];
                        int dmg = v.defaultProjectile?.projectile != null ? v.defaultProjectile.projectile.GetDamageAmount(null) : 0;
                        sb.AppendLine("MT_Info_Gun".Translate(dmg, v.range.ToString("0"), v.burstShotCount));
                    }
                    else if (prod.IsMeleeWeapon)
                        sb.AppendLine("MT_Info_Melee".Translate(prod.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, stuff).ToString("0.0")));
                }
                sb.AppendLine("MT_Info_Work".Translate(Mathf.RoundToInt(rec.workAmount > 0 ? rec.workAmount : (prod?.GetStatValueAbstract(StatDefOf.WorkToMake, stuff) ?? 0f))));
                if (rec.ingredients != null && rec.ingredients.Count > 0)
                    sb.AppendLine("MT_Info_Needs".Translate(string.Join(", ", rec.ingredients.Select(i => i.SummaryFor(rec)).ToArray())));
                if (rec.skillRequirements != null && rec.skillRequirements.Count > 0)
                    sb.AppendLine("MT_Info_Skill".Translate(string.Join(", ", rec.skillRequirements.Select(r => r.Summary).ToArray())));
                if (rec.workSkill != null) sb.AppendLine("MT_Info_UsesSkill".Translate(rec.workSkill.LabelCap));
            }
            catch (Exception e) { sb.AppendLine(e.Message); }
            s = sb.ToString().TrimEnd();
            infoCache[rec] = s;
            return s;
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
            try
            {
                for (int i = 0; i < bills.Count; i++)
                {
                    Bill b = bills[i];
                    Rect c = new Rect(0f, i * (cardH + 6f), view.width, cardH);
                    Pawn doer = Doer(b, table.Map);
                    bool waiting = !b.suspended && b.ShouldDoNow() && Find.TickManager.TicksGame <= b.nextTickToSearchForIngredients;
                    Color border = doer != null ? Look.Green : waiting ? Look.Orange : Look.Line;
                    Look.Box(c, doer != null ? Look.GreenBg : Look.Card, border);
                    Rect ic = new Rect(c.x + 8f, c.y + 8f, 34f, 34f);
                    Look.Icon(ic, b.recipe.UIIconThing, b.recipe.UIIcon);
                    if (Mouse.IsOver(ic)) TooltipHandler.TipRegion(ic, new TipSignal(() => RecipeInfo(b.recipe) + "\n\n" + "MT_ClickInfo".Translate(), b.recipe.GetHashCode() ^ 0x55));
                    if (Widgets.ButtonInvisible(ic)) OpenInfo(b.recipe);
                    float tx = c.x + 52f, tw = c.width - 52f - 200f;   // 0.5.1: leave room for the P / … column (the − / + were hidden under it)
                    // 0.5.0: linked bills are marked; the tooltip says where the others are
                    List<Bill> linked = BillLinks.GroupOf(b) != 0 ? BillLinks.Others(b) : null;
                    Rect title = new Rect(tx, c.y + 6f, tw, 22f);
                    Look.Text(title, b.LabelCap.ToString().Truncate(tw), b.suspended ? Muted : Color.white);
                    if (linked != null && linked.Count > 0)
                        TooltipHandler.TipRegion(title, "MT_LinkedTip".Translate() + "\n" + string.Join("\n", linked.Select(o => "- " + ((o.billStack?.billGiver as Thing)?.LabelCap ?? "?") + ": " + o.LabelCap).ToArray()));
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
                            repeat += CountInfo(bp);   // 0.5.0
                        }
                    }
                    if (linked != null && linked.Count > 0) repeat += " · " + "MT_LinkedN".Translate(linked.Count);
                    Look.Text(new Rect(tx, c.y + 28f, tw, 18f), status, sc, GameFont.Tiny);
                    // 0.5.0: − / + buttons right after the count line
                    bool hasCount = b is Bill_Production bpc && bpc.repeatMode != BillRepeatModeDefOf.Forever;
                    float rw = hasCount ? tw - 62f : tw;
                    Rect rr = new Rect(tx, c.y + 44f, rw, 18f);
                    Look.Text(rr, repeat.Truncate(rw), Muted, GameFont.Tiny);
                    if (Mouse.IsOver(rr) && !repeat.NullOrEmpty()) TooltipHandler.TipRegion(rr, repeat);
                    if (hasCount) CountButtons(new Rect(tx + rw + 4f, c.y + 40f, 58f, 24f), (Bill_Production)b);
                    if (progress >= 0f) Look.Bar(new Rect(tx, c.y + 63f, tw, 4f), progress, Look.Green);
                    // buttons
                    float bx = c.xMax - 146f, by = c.y + 8f;
                    // 0.1.3: priority 1 (first) … 10 (last)
                    int pr = BillPrio.Get(b);
                    Rect pc = new Rect(bx - 46f, by, 40f, 26f);
                    Color prc = pr <= 2 ? Look.Red : pr <= 4 ? Look.Orange : pr == 5 ? Muted : Look.Blue;
                    Look.Box(pc, Look.Card2, prc);
                    Look.Text(pc, "P" + pr, prc, GameFont.Small, TextAnchor.MiddleCenter);
                    TooltipHandler.TipRegion(pc, "MT_PrioTip".Translate(pr));
                    if (Widgets.ButtonInvisible(pc))
                    {
                        Bill bb = b;
                        var opts = new List<FloatMenuOption>();
                        for (int p = 1; p <= 10; p++)
                        {
                            int pp = p;
                            opts.Add(new FloatMenuOption("MT_PrioOption".Translate(p) + (p == 1 ? " – " + "MT_PrioFirst".Translate() : p == 5 ? " – " + "MT_PrioNormal".Translate() : p == 10 ? " – " + "MT_PrioLast".Translate() : ""), () => BillPrio.Set(bb, pp, table)));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    // 0.5.0: bill menu – rename, link with another bill, unlink
                    Rect mr = new Rect(bx - 46f, by + 32f, 40f, 26f);
                    if (Look.Button(mr, "…")) BillMenu(b, table);
                    TooltipHandler.TipRegion(mr, "MT_BillMenuTip".Translate());
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
            }
            finally { Widgets.EndScrollView(); }   // 0.4.2
            if (toUp != null) { table.billStack.Reorder(toUp, -1); SoundDefOf.Tick_High.PlayOneShotOnCamera(); }
            if (toDown != null) { table.billStack.Reorder(toDown, 1); SoundDefOf.Tick_Low.PlayOneShotOnCamera(); }
            if (toDelete != null) { table.billStack.Delete(toDelete); SoundDefOf.Click.PlayOneShotOnCamera(); }
        }
    }

    // ---------------------------------------------------------------------------
    // 0.1.3: bill priority 1 (do first) … 10 (do last), 5 = normal. Within a workbench the bills are
    // kept in that order; across workbenches colonists go first to the bench with the most urgent bill.
    // ---------------------------------------------------------------------------
    public class BillPrio : GameComponent
    {
        public const int Normal = 5;
        private Dictionary<string, int> prio = new Dictionary<string, int>();
        private static BillPrio I;

        public BillPrio(Game g) { I = this; }

        public override void FinalizeInit() { I = this; }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Prune();
            Scribe_Collections.Look(ref prio, "billPriorities", LookMode.Value, LookMode.Value);
            if (prio == null) prio = new Dictionary<string, int>();
            I = this;
        }

        // forget bills that no longer exist
        private void Prune()
        {
            try
            {
                var alive = new HashSet<string>();
                foreach (Map m in Find.Maps)
                    foreach (Building_WorkTable t in m.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
                        foreach (Bill b in t.billStack.Bills) alive.Add(b.GetUniqueLoadID());
                foreach (string k in prio.Keys.ToList()) if (!alive.Contains(k)) prio.Remove(k);
            }
            catch { }
        }

        public static bool Any => I != null && I.prio.Count > 0;

        public static int Get(Bill b)
        {
            if (I == null || b == null) return Normal;
            return I.prio.TryGetValue(b.GetUniqueLoadID(), out int p) ? p : Normal;
        }

        // 0.5.0: set without re-sorting (bench paste); call Resort once afterwards
        public static void SetRaw(Bill b, int p)
        {
            if (I == null || b == null) return;
            string k = b.GetUniqueLoadID();
            if (p == Normal) I.prio.Remove(k); else I.prio[k] = p;
        }

        public static void Resort(Building_WorkTable table)
        {
            List<Bill> bills = table.billStack.Bills;
            List<Bill> sorted = bills.Select((x, i) => new { x, i }).OrderBy(z => Get(z.x)).ThenBy(z => z.i).Select(z => z.x).ToList();
            bills.Clear();
            bills.AddRange(sorted);
        }

        public static void Set(Bill b, int p, Building_WorkTable table)
        {
            if (I == null || b == null) return;
            string k = b.GetUniqueLoadID();
            if (p == Normal) I.prio.Remove(k); else I.prio[k] = p;
            // keep the bench's list in priority order (stable)
            List<Bill> bills = table.billStack.Bills;
            List<Bill> sorted = bills.Select((x, i) => new { x, i }).OrderBy(z => Get(z.x)).ThenBy(z => z.i).Select(z => z.x).ToList();
            bills.Clear();
            bills.AddRange(sorted);
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
        }

        // most urgent bill a colonist could do at this bench (higher = sooner for the game)
        private static float BenchScore(Thing t)
        {
            if (!(t is IBillGiver g) || g.BillStack == null) return 0f;
            int best = 11;
            foreach (Bill b in g.BillStack.Bills)
                if (!b.suspended && b.ShouldDoNow())
                {
                    int p = Get(b);
                    if (p < best) best = p;
                }
            return best > 10 ? 0f : 11 - best;
        }

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.Prioritized)), postfix: new HarmonyMethod(typeof(BillPrio), nameof(PrioritizedPostfix)));
                h.Patch(AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.GetPriority), new[] { typeof(Pawn), typeof(TargetInfo) }), postfix: new HarmonyMethod(typeof(BillPrio), nameof(GetPriorityPostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] bill priorities: " + e.Message); }
        }

        public static void PrioritizedPostfix(WorkGiver_Scanner __instance, ref bool __result)
        {
            if (!__result && Any && __instance is WorkGiver_DoBill) __result = true;
        }

        public static void GetPriorityPostfix(WorkGiver_Scanner __instance, TargetInfo t, ref float __result)
        {
            if (Any && __instance is WorkGiver_DoBill && t.HasThing) __result = BenchScore(t.Thing);
        }
    }
}
