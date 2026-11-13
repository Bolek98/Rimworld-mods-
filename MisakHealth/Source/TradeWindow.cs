// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakHealth
{
    // ---------------------------------------------------------------------------
    // 0.13.0: trading in our look. The game's own trade window (Dialog_Trade) stays the window – it sets
    // up the trade session, closes it and hands out quests – only its contents are drawn by us:
    // categories on the left (food, medicine, weapons, apparel, resources, items, animals, people,
    // buildings), search, sorting, rows with picture, NAME and quality, our count / their count, the
    // prices and the game's own +/- buttons, and on the right a cart with everything that changes.
    // Accept, reset, cancel, gift mode and "what they buy" work exactly like the game's.
    // Caravans (mass and food bar) and royal favor trades keep the game's window; any error falls back
    // to the game's window for that trade.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class TradeWindowPatch
    {
        private class State
        {
            public TradeWindow.Cat cat = TradeWindow.Cat.All;
            public TradeWindow.Sort sort = TradeWindow.Sort.Category;
            public int side;            // 0 all, 1 ours, 2 theirs
            public string search = "";
            public Vector2 scroll, cartScroll;
            public List<Tradeable> list;
            public float builtAt = -99f;
            public string builtKey;
            public bool failed;
        }

        private static readonly ConditionalWeakTable<Dialog_Trade, State> states = new ConditionalWeakTable<Dialog_Trade, State>();
        private static FieldInfo giftsOnlyF;

        static TradeWindowPatch()
        {
            try
            {
                var h = new Harmony("misak.health.trade");
                giftsOnlyF = AccessTools.Field(typeof(Dialog_Trade), "giftsOnly");
                h.Patch(AccessTools.Method(typeof(Dialog_Trade), nameof(Dialog_Trade.DoWindowContents)),
                    prefix: new HarmonyMethod(typeof(TradeWindowPatch), nameof(Prefix)) { priority = Priority.High });
                h.Patch(AccessTools.Method(typeof(Window), "SetInitialSizeAndPosition"),
                    postfix: new HarmonyMethod(typeof(TradeWindowPatch), nameof(SizePostfix)));
            }
            catch (Exception e) { Log.Warning("[Clear Tabs] trade window: " + e.Message); }
        }

        // ours only for a plain trade: not from a caravan (mass/food bar), not for royal favor
        private static bool Ours()
        {
            try
            {
                if (MTMod.S == null || !MTMod.S.tradeWindow) return false;
                if (TradeSession.deal == null || TradeSession.trader == null || TradeSession.playerNegotiator == null) return false;
                if (TradeSession.TradeCurrency == TradeCurrency.Favor) return false;
                if (TradeSession.playerNegotiator.GetCaravan() != null) return false;
                return true;
            }
            catch { return false; }
        }

        public static void SizePostfix(Window __instance)
        {
            if (!(__instance is Dialog_Trade d) || !Ours()) return;
            try
            {
                float w = Mathf.Min(1320f, UI.screenWidth - 40f), hgt = Mathf.Min(900f, UI.screenHeight - 40f);
                d.windowRect = new Rect((UI.screenWidth - w) / 2f, (UI.screenHeight - hgt) / 2f, w, hgt).Rounded();
            }
            catch { }
        }

        public static bool Prefix(Dialog_Trade __instance, Rect inRect)
        {
            if (!Ours()) return true;
            State st = states.GetValue(__instance, _ => new State());
            if (st.failed) return true;
            try
            {
                TradeWindow.Draw(__instance, inRect, st.cat, st.sort, st.side, ref st.search, ref st.scroll, ref st.cartScroll, ref st.list, ref st.builtAt, ref st.builtKey,
                    c => { st.cat = c; st.scroll = Vector2.zero; st.builtAt = -99f; },
                    s => { st.sort = s; st.builtAt = -99f; },
                    sd => { st.side = sd; st.scroll = Vector2.zero; st.builtAt = -99f; },
                    GiftsOnly(__instance));
                return false;
            }
            catch (Exception e)
            {
                st.failed = true;
                Log.ErrorOnce("[Clear Tabs] trade window failed, the game's trade window is used for this trade: " + e, 0x4d5491);
                Look.ResetGui();
                try { Widgets.EndScrollView(); } catch { }
                return true;
            }
        }

        private static bool GiftsOnly(Dialog_Trade d)
        {
            try { return giftsOnlyF != null && (bool)giftsOnlyF.GetValue(d); } catch { return false; }
        }
    }

    public static class TradeWindow
    {
        public enum Cat { All, Food, Medicine, Weapons, Apparel, Resources, Items, Animals, People, Buildings, Other }
        public enum Sort { Category, Name, Value, Count }

        private const float RowH = 34f;
        // 0.15.0: sub-category, wish list / need filter, which window they belong to
        private static string sub, extra;
        private static Dialog_Trade forDlg;
        private static Vector2 sideScroll;
        private static readonly Texture2D SellableIcon = ContentFinder<Texture2D>.Get("UI/Commands/SellableItems");
        private static readonly Texture2D GiftIcon = ContentFinder<Texture2D>.Get("UI/Buttons/GiftMode");
        private static readonly Texture2D TradeIcon = ContentFinder<Texture2D>.Get("UI/Buttons/TradeMode");
        private static readonly Dictionary<ThingDef, Cat> catCache = new Dictionary<ThingDef, Cat>();

        public static Cat CatOf(Tradeable tr)
        {
            Thing t = tr.AnyThing;
            if (t is Pawn p) return p.RaceProps != null && p.RaceProps.Humanlike ? Cat.People : Cat.Animals;
            if (t is MinifiedThing) return Cat.Buildings;
            ThingDef d = tr.ThingDef;
            if (d == null) return Cat.Other;
            if (catCache.TryGetValue(d, out Cat c)) return c;
            c = Cat.Other;
            try
            {
                if (d.IsWeapon) c = Cat.Weapons;
                else if (d.IsApparel) c = Cat.Apparel;
                else if (d.IsMedicine || d.IsDrug || In(d, ThingCategoryDefOf.Medicine) || In(d, ThingCategoryDefOf.Drugs) || In(d, ThingCategoryDefOf.BodyParts)) c = Cat.Medicine;
                else if (d.IsIngestible || In(d, ThingCategoryDefOf.Foods)) c = Cat.Food;
                else if (d.category == ThingCategory.Building || In(d, ThingCategoryDefOf.Buildings)) c = Cat.Buildings;
                else if (In(d, ThingCategoryDefOf.Items) || In(d, ThingCategoryDefOf.Techprints) || In(d, ThingCategoryDefOf.Neurotrainers)) c = Cat.Items;
                else if (d.IsStuff || In(d, ThingCategoryDefOf.ResourcesRaw) || In(d, ThingCategoryDefOf.Manufactured) || In(d, ThingCategoryDefOf.StoneBlocks) || In(d, ThingCategoryDefOf.Leathers) || In(d, ThingCategoryDefOf.Textiles)) c = Cat.Resources;
            }
            catch { }
            catCache[d] = c;
            return c;
        }

        private static bool In(ThingDef d, ThingCategoryDef cat)
        {
            if (cat == null || d.thingCategories == null) return false;
            foreach (ThingCategoryDef tc in d.thingCategories)
                for (ThingCategoryDef x = tc; x != null; x = x.parent)
                    if (x == cat) return true;
            return false;
        }

        private static IEnumerable<Tradeable> Pool()
        {
            bool hide = TradeSession.trader.TraderKind != null && TradeSession.trader.TraderKind.hideThingsNotWillingToTrade;
            return TradeSession.deal.AllTradeables.Where(tr => !tr.IsCurrency && tr.HasAnyThing && (tr.TraderWillTrade || !hide));
        }

        public static void Draw(Dialog_Trade dlg, Rect inRect, Cat cat, Sort sort, int side, ref string search, ref Vector2 scroll, ref Vector2 cartScroll,
            ref List<Tradeable> list, ref float builtAt, ref string builtKey, Action<Cat> setCat, Action<Sort> setSort, Action<int> setSide, bool giftsOnly)
        {
            TradeSession.deal.UpdateCurrencyCount();
            Rect full = new Rect(0f, 0f, dlg.windowRect.width, dlg.windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(full.x, full.y, full.width, 3f), TradeSession.giftMode ? Look.Blue : Look.Green);
            Text.Font = GameFont.Small;
            Tradeable currency = TradeSession.deal.AllTradeables.FirstOrDefault(x => x.IsCurrency && !x.IsFavor);
            Rect r = inRect;

            // ---- header ----
            float y = r.y;
            Faction fac = TradeSession.trader.Faction;
            string trader = TradeSession.trader.TraderName ?? "";
            Look.Text(new Rect(r.x, y, r.width * 0.5f, 30f), trader.Truncate(r.width * 0.5f), MUI.Text1, GameFont.Medium);
            string kind = TradeSession.trader.TraderKind?.LabelCap ?? "";
            string facLine = fac != null ? fac.Name + " · " + kind : kind;
            Look.Text(new Rect(r.x, y + 30f, r.width * 0.5f, 20f), facLine.Truncate(r.width * 0.5f), MUI.Muted, GameFont.Tiny);
            Pawn neg = TradeSession.playerNegotiator;
            string negLine = "MT_TR_Negotiator".Translate(neg.LabelShortCap, neg.GetStatValue(StatDefOf.TradePriceImprovement).ToStringPercent());
            Look.Text(new Rect(r.x, y + 48f, r.width * 0.5f, 20f), negLine, MUI.Muted, GameFont.Tiny);
            // silver: ours ↔ theirs
            if (currency != null)
            {
                Rect sb = new Rect(r.xMax - 470f, y + 4f, 470f, 56f);
                MUI.Fill(sb, Look.Card);
                MUI.Frame(sb, Look.Line);
                Widgets.ThingIcon(new Rect(sb.x + 8f, sb.y + 12f, 32f, 32f), currency.AnyThing);
                int ours = currency.CountPostDealFor(Transactor.Colony), theirs = currency.CountPostDealFor(Transactor.Trader);
                int change = currency.CountPostDealFor(Transactor.Colony) - currency.CountHeldBy(Transactor.Colony);
                bool flash = Time.time - Dialog_Trade.lastCurrencyFlashTime < 1f;
                Look.Text(new Rect(sb.x + 48f, sb.y + 4f, 150f, 20f), "MT_TR_Ours".Translate(), MUI.Muted, GameFont.Tiny);
                Look.Text(new Rect(sb.x + 48f, sb.y + 22f, 150f, 28f), ours.ToStringCached(), ours < 0 ? Look.Red : MUI.Text1, GameFont.Medium);
                Look.Text(new Rect(sb.x + 190f, sb.y + 14f, 100f, 28f), (change > 0 ? "+" : "") + change.ToStringCached(), change > 0 ? Look.Green : change < 0 ? (flash ? Look.Red : Look.Orange) : MUI.Muted, GameFont.Small, TextAnchor.MiddleCenter);
                Look.Text(new Rect(sb.xMax - 160f, sb.y + 4f, 150f, 20f), "MT_TR_Theirs".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
                Look.Text(new Rect(sb.xMax - 160f, sb.y + 22f, 150f, 28f), theirs.ToStringCached(), theirs < 0 || flash ? Look.Red : MUI.Text1, GameFont.Medium, TextAnchor.MiddleRight);
                TooltipHandler.TipRegion(sb, "MT_TR_SilverTip".Translate());
            }
            y += 72f;

            // ---- body: sidebar | list | cart ----
            float bottomH = 52f;
            Rect body = new Rect(r.x, y, r.width, r.yMax - y - bottomH - 8f);
            Rect sideR = new Rect(body.x, body.y, 190f, body.height);
            Rect cart = new Rect(body.xMax - 270f, body.y, 270f, body.height);
            Rect mid = new Rect(sideR.xMax + 10f, body.y, cart.x - sideR.xMax - 20f, body.height);

            // sidebar categories (counts of the current pool)
            var pool = Pool().ToList();
            if (forDlg != dlg) { forDlg = dlg; sub = null; extra = null; sideScroll = Vector2.zero; }
            TradePlus.SessionStart(dlg, pool);
            Map map = TradeSession.playerNegotiator?.MapHeld ?? Find.CurrentMap;
            MUI.Fill(sideR, Look.Card);
            MUI.Frame(sideR, Look.Line);
            var needList = TradePlus.Needs(map);
            Rect sideView = new Rect(0f, 0f, sideR.width - 16f, 31f * 12 + 30f + 29f * 3 + 30f + 31f + 30f + 31f * needList.Count + 40f);
            Widgets.BeginScrollView(sideR, ref sideScroll, sideView);
            Rect sideR0 = sideR;
            sideR = new Rect(0f, 0f, sideView.width + 12f, sideView.height);
            float sy = sideR.y + 6f;
            foreach (Cat c in Enum.GetValues(typeof(Cat)))
            {
                int n = c == Cat.All ? pool.Count : pool.Count(t => CatOf(t) == c);
                if (n == 0 && c != Cat.All && c != cat) continue;
                Rect b = new Rect(sideR.x + 6f, sy, sideR.width - 12f, 28f);
                bool on = cat == c;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                if (on) MUI.Frame(b, Look.Green);
                Look.Text(new Rect(b.x + 8f, b.y, b.width - 50f, b.height), ("MT_TR_C" + c).Translate(), on ? Look.Green : MUI.Text1);
                Look.Text(new Rect(b.xMax - 44f, b.y, 38f, b.height), n.ToStringCached(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
                if (Widgets.ButtonInvisible(b)) { setCat(c); sub = null; extra = null; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                sy += 31f;
            }
            // side filter (all / ours / theirs)
            sy += 8f;
            Look.Text(new Rect(sideR.x + 8f, sy, sideR.width - 16f, 20f), "MT_TR_Show".Translate(), MUI.Muted, GameFont.Tiny);
            sy += 20f;
            for (int i = 0; i < 3; i++)
            {
                Rect b = new Rect(sideR.x + 6f, sy, sideR.width - 12f, 26f);
                bool sel = side == i;
                MUI.Fill(b, sel ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                if (sel) MUI.Frame(b, Look.Green);
                Look.Text(new Rect(b.x + 8f, b.y, b.width - 10f, b.height), ("MT_TR_Side" + i).Translate(), sel ? Look.Green : MUI.Text1, GameFont.Tiny);
                if (Widgets.ButtonInvisible(b)) { setSide(i); SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                sy += 29f;
            }
            // 0.15.0: wish list and what the colony needs
            sy += 8f;
            {
                int wn = pool.Count(t => TradePlus.Wished(t.ThingDef) && t.CountHeldBy(Transactor.Trader) > 0);
                Rect b = new Rect(sideR.x + 6f, sy, sideR.width - 12f, 28f);
                bool on = extra == "wish";
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                if (on) MUI.Frame(b, Look.Green);
                Look.Text(new Rect(b.x + 8f, b.y, b.width - 50f, b.height), "♥ " + "MT_TR_Wishes".Translate(), on ? Look.Green : wn > 0 ? Look.Red : MUI.Text1);
                Look.Text(new Rect(b.xMax - 44f, b.y, 38f, b.height), wn.ToStringCached(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
                TooltipHandler.TipRegion(b, "MT_TR_WishesTip".Translate());
                if (Widgets.ButtonInvisible(b)) { extra = on ? null : "wish"; builtAt = -99f; scroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                sy += 31f;
            }
            if (needList.Count > 0)
            {
                sy += 6f;
                Look.Text(new Rect(sideR.x + 8f, sy, sideR.width - 16f, 20f), "MT_TR_Needs".Translate(), Look.Orange, GameFont.Tiny);
                sy += 20f;
                foreach (var nd in needList)
                {
                    int has = pool.Count(t => t.CountHeldBy(Transactor.Trader) > 0 && t.AnyThing != null && SafeMatch(nd, t.AnyThing));
                    Rect b = new Rect(sideR.x + 6f, sy, sideR.width - 12f, 28f);
                    bool on = extra == "need:" + nd.key;
                    MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                    MUI.Frame(b, on ? Look.Green : Look.Orange.ToTransparent(0.5f));
                    Look.Text(new Rect(b.x + 8f, b.y, b.width - 40f, b.height), nd.label.Truncate(b.width - 44f), on ? Look.Green : MUI.Text1, GameFont.Tiny);
                    Look.Text(new Rect(b.xMax - 34f, b.y, 28f, b.height), has.ToStringCached(), has > 0 ? Look.Green : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
                    TooltipHandler.TipRegion(b, "MT_TR_NeedTip".Translate(nd.label, has));
                    if (Widgets.ButtonInvisible(b)) { extra = on ? null : "need:" + nd.key; builtAt = -99f; scroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                    sy += 31f;
                }
            }
            Widgets.EndScrollView();
            sideR = sideR0;

            // search + sort
            Rect sr = new Rect(mid.x, mid.y, Mathf.Min(280f, mid.width * 0.4f), 28f);
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName("MT_TradeSearch");
            Look.EscUnfocus("MT_TradeSearch");
            string ns = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (ns != search) { search = ns; builtAt = -99f; scroll = Vector2.zero; }
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), "MT_TR_Search".Translate(), MUI.Muted);
            float x = sr.xMax + 14f;
            Look.Text(new Rect(x, mid.y, 60f, 28f), "MT_TR_SortBy".Translate(), MUI.Muted);
            x += 60f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
            {
                string lab = ("MT_TR_S" + s).Translate();
                float w = Text.CalcSize(lab).x + 18f;
                if (x + w > mid.xMax) break;
                Rect b = new Rect(x, mid.y, w, 28f);
                bool on = sort == s;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                MUI.Frame(b, on ? Look.Green : Look.Line);
                Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(b)) setSort(s);
                x += w + 4f;
            }
            {
                string lab = MTMod.S.tradeTiles ? "MT_TR_ViewRows".Translate() : "MT_TR_ViewTiles".Translate();
                float w = Text.CalcSize(lab).x + 18f;
                Rect b = new Rect(mid.xMax - w, mid.y, w, 28f);
                if (b.x > x)
                {
                    MUI.Fill(b, Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                    MUI.Frame(b, Look.Blue);
                    Look.Text(b, lab, Look.Blue, GameFont.Small, TextAnchor.MiddleCenter);
                    if (Widgets.ButtonInvisible(b)) { MTMod.S.tradeTiles = !MTMod.S.tradeTiles; scroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                }
            }

            // rebuild the list now and then (counts change while trading, so cheaply every half second)
            string key = cat + "|" + sort + "|" + side + "|" + search + "|" + TradeSession.giftMode + "|" + sub + "|" + extra;
            if (list == null || key != builtKey || Time.realtimeSinceStartup - builtAt > 0.5f)
            {
                list = Build(pool, cat, sort, side, search);
                if (extra == "wish") list = list.Where(t => TradePlus.Wished(t.ThingDef)).ToList();
                else if (extra != null && extra.StartsWith("need:"))
                {
                    var nd = needList.FirstOrDefault(n => "need:" + n.key == extra);
                    list = nd == null ? list : list.Where(t => t.CountHeldBy(Transactor.Trader) > 0 && t.AnyThing != null && SafeMatch(nd, t.AnyThing)).ToList();
                }
                fullCatList = list;
                if (sub != null) list = list.Where(t => TradePlus.SubOf(t, cat) == sub).ToList();
                builtKey = key;
                builtAt = Time.realtimeSinceStartup;
            }

            // 0.15.0: sub-categories as chips
            float chipsH = 0f;
            if (cat != Cat.All && cat != Cat.People && cat != Cat.Animals && fullCatList != null)
            {
                var groups = fullCatList.GroupBy(t => TradePlus.SubOf(t, cat)).Where(g => g.Key != null).OrderBy(g => g.Key == "other" ? 1 : 0).ToList();
                if (groups.Count > 1)
                {
                    float cx = mid.x;
                    Text.Font = GameFont.Tiny;
                    var chips = new List<(string k, string label)> { (null, "MT_TR_SubAll".Translate() + " " + fullCatList.Count) };
                    chips.AddRange(groups.Select(g => (g.Key, ("MT_TR_Sub_" + g.Key).Translate() + " " + g.Count())));
                    foreach (var (k, label) in chips)
                    {
                        float w = Text.CalcSize(label).x + 16f;
                        if (cx + w > mid.xMax) break;
                        Rect cr = new Rect(cx, mid.y + 34f, w, 22f);
                        bool on = sub == k;
                        MUI.Fill(cr, on ? Look.GreenBg : Mouse.IsOver(cr) ? Look.Card2 : Look.Card);
                        MUI.Frame(cr, on ? Look.Green : Look.Line);
                        Look.Text(cr, label, on ? Look.Green : MUI.Text1, GameFont.Tiny, TextAnchor.MiddleCenter);
                        if (Widgets.ButtonInvisible(cr)) { sub = k; builtAt = -99f; scroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                        cx += w + 4f;
                    }
                    Text.Font = GameFont.Small;
                    chipsH = 28f;
                }
            }
            if (MTMod.S.tradeTiles)
            {
                Tiles(new Rect(mid.x, mid.y + 36f + chipsH, mid.width, mid.height - 36f - chipsH), list, map, ref scroll);
                DrawCart(cart, pool, currency, ref cartScroll, map, ref builtAt);
                DrawBottom(dlg, new Rect(r.x, r.yMax - bottomH, r.width, bottomH), giftsOnly, ref list, ref builtAt);
                Look.ResetGui();
                return;
            }
            // column header
            Rect lr = new Rect(mid.x, mid.y + 36f + chipsH, mid.width, mid.height - 36f - chipsH);
            Rect hdr = new Rect(lr.x, lr.y, lr.width - 16f, 22f);
            Cols(hdr, out Rect cName, out Rect cOurs, out Rect cSell, out Rect cAdj, out Rect cBuy, out Rect cTheirs);
            Look.Text(cName, "MT_TR_HName".Translate(), MUI.Muted, GameFont.Tiny);
            Look.Text(cOurs, "MT_TR_HOurs".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
            Look.Text(cSell, "MT_TR_HSell".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
            Look.Text(cAdj, TradeSession.giftMode ? "MT_TR_HGift".Translate() : "MT_TR_HAdjust".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
            Look.Text(cBuy, "MT_TR_HBuy".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
            Look.Text(cTheirs, "MT_TR_HTheirs".Translate(), MUI.Muted, GameFont.Tiny);
            Rect outR = new Rect(lr.x, lr.y + 24f, lr.width, lr.height - 24f);
            MUI.Fill(outR, Look.Card);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(list.Count * RowH, 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Rect row = new Rect(0f, i * RowH, view.width, RowH);
                    if (row.yMax < scroll.y - RowH || row.y > scroll.y + outR.height + RowH) continue;
                    DrawRow(row, list[i], i);
                }
                if (list.Count == 0) Look.Text(new Rect(8f, 4f, view.width, 26f), "MT_TR_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }

            // cart
            DrawCart(cart, pool, currency, ref cartScroll, map, ref builtAt);

            // ---- bottom buttons ----
            Rect bot = new Rect(r.x, r.yMax - bottomH, r.width, bottomH);
            DrawBottom(dlg, bot, giftsOnly, ref list, ref builtAt);
            Look.ResetGui();
        }

        private static List<Tradeable> fullCatList;

        private static bool SafeMatch(TradePlus.Need n, Thing t)
        {
            try { return n.match(t); } catch { return false; }
        }

        // the extra marks of a thing: wish heart, need, upgrade, price memory
        private static string Badges(Tradeable tr, Map map, out Color col)
        {
            col = MUI.Muted;
            var bits = new List<string>();
            string up = TradePlus.BetterFor(tr, map);
            if (up != null) { bits.Add(up); col = Look.Green; }
            var nd = TradePlus.NeedFor(tr, map);
            if (nd != null) { bits.Add("MT_TR_BadgeNeed".Translate()); if (up == null) col = Look.Orange; }
            if (tr.CountHeldBy(Transactor.Trader) > 0)
            {
                int pm = TradePlus.PriceMark(tr, TradeAction.PlayerBuys);
                if (pm < 0) { bits.Add("MT_TR_Cheap".Translate()); if (up == null && nd == null) col = Look.Green; }
                else if (pm > 0) bits.Add("MT_TR_Dear".Translate());
            }
            if (tr.CountHeldBy(Transactor.Colony) > 0)
            {
                int pm = TradePlus.PriceMark(tr, TradeAction.PlayerSells);
                if (pm < 0) bits.Add("MT_TR_SellGood".Translate());
                if (TradePlus.Junk(tr)) bits.Add("MT_TR_Junk".Translate());
                else if (TradePlus.Surplus(tr) > 0) bits.Add("MT_TR_Surplus".Translate(TradePlus.Surplus(tr)));
            }
            return string.Join(" · ", bits);
        }

        private static void Heart(Rect r, Tradeable tr)
        {
            if (tr.ThingDef == null) return;
            bool w = TradePlus.Wished(tr.ThingDef);
            if (!w && !Mouse.IsOver(r.ExpandedBy(40f, 4f))) return;
            Look.Text(r, "♥", w ? Look.Red : new Color(1f, 1f, 1f, Mouse.IsOver(r) ? 0.8f : 0.3f), GameFont.Small, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(r, w ? "MT_TR_WishRemove".Translate() : "MT_TR_WishAdd".Translate());
            if (Widgets.ButtonInvisible(r)) { TradePlus.ToggleWish(tr.ThingDef); SoundDefOf.Tick_High.PlayOneShotOnCamera(); }
        }

        // ---- tiles view ----
        private static void Tiles(Rect r, List<Tradeable> list, Map map, ref Vector2 scroll)
        {
            MUI.Fill(r, Look.Card);
            const float tw = 196f, th = 150f, gap = 8f;
            int cols = Mathf.Max(1, Mathf.FloorToInt((r.width - 16f + gap) / (tw + gap)));
            float w = (r.width - 16f - gap * (cols - 1)) / cols;
            int rows = Mathf.CeilToInt(list.Count / (float)cols);
            Rect view = new Rect(0f, 0f, r.width - 16f, Mathf.Max(rows * (th + gap), 10f));
            Widgets.BeginScrollView(r, ref scroll, view);
            try
            {
                for (int i = 0; i < list.Count; i++)
                {
                    Rect c = new Rect((i % cols) * (w + gap), (i / cols) * (th + gap), w, th);
                    if (c.yMax < scroll.y - th || c.y > scroll.y + r.height + th) continue;
                    Tile(c, list[i], map, i);
                }
                if (list.Count == 0) Look.Text(new Rect(8f, 4f, view.width, 26f), "MT_TR_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void Tile(Rect c, Tradeable tr, Map map, int index)
        {
            Thing t = tr.AnyThing;
            bool changed = tr.CountToTransfer != 0;
            string badges = Badges(tr, map, out Color bc);
            MUI.Fill(c, changed ? Look.GreenBg : Look.Card2);
            MUI.Frame(c, changed ? Look.Green : bc == Look.Green ? Look.Green.ToTransparent(0.6f) : bc == Look.Orange ? Look.Orange.ToTransparent(0.6f) : Look.Line);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            try { Widgets.ThingIcon(icon, t); } catch { }
            Heart(new Rect(c.xMax - 48f, c.y + 4f, 20f, 20f), tr);
            Widgets.InfoCardButton(c.xMax - 26f, c.y + 4f, t);
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 52f;
            Text.Font = GameFont.Small;
            string label = tr.LabelCap;
            Look.Text(new Rect(tx, c.y + 6f, c.xMax - tx - 8f, 20f), label.Truncate(c.xMax - tx - 52f), tr.TraderWillTrade ? MUI.Text1 : MUI.Muted);
            string subl = "";
            if (t != null && t.TryGetQuality(out QualityCategory qc)) subl = qc.GetLabel().CapitalizeFirst();
            if (t != null && t.def.useHitPoints && t.MaxHitPoints > 0 && t.HitPoints < t.MaxHitPoints) subl += (subl.Length > 0 ? " · " : "") + ((float)t.HitPoints / t.MaxHitPoints).ToStringPercent();
            Look.Text(new Rect(tx, c.y + 26f, c.xMax - tx - 8f, 16f), subl, MUI.Muted, GameFont.Tiny);
            if (badges.Length > 0)
                Look.Text(new Rect(c.x + 8f, c.y + 60f, c.width - 16f, 30f), badges, bc, GameFont.Tiny, TextAnchor.UpperLeft);
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 50f, 58f), () => tr.LabelCap + (tr.TipDescription.NullOrEmpty() ? "" : "\n\n" + tr.TipDescription) + (badges.Length > 0 ? "\n\n" + badges : ""), tr.GetHashCode() * 37);
            // counts and prices
            int ours = tr.CountHeldBy(Transactor.Colony), theirs = tr.CountHeldBy(Transactor.Trader);
            float by = c.yMax - 52f;
            Look.Text(new Rect(c.x + 8f, by, c.width / 2f - 8f, 18f), ours > 0 ? "MT_TR_TileOurs".Translate(ours) : "", MUI.Muted, GameFont.Tiny);
            Look.Text(new Rect(c.center.x, by, c.width / 2f - 8f, 18f), theirs > 0 ? "MT_TR_TileTheirs".Translate(theirs) : "", MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight);
            if (ours > 0) Price(new Rect(c.x + 4f, by + 14f, 70f, 18f), tr, TradeAction.PlayerSells);
            if (theirs > 0 && tr.IsThing) Price(new Rect(c.xMax - 74f, by + 14f, 70f, 18f), tr, TradeAction.PlayerBuys);
            int sel = TradePlus.Selling(tr), buy = TradePlus.Buying(tr);
            string mid = sel > 0 ? "−" + sel : buy > 0 ? "+" + buy : "0";
            Look.Text(new Rect(c.center.x - 30f, by + 14f, 60f, 18f), mid, sel > 0 ? Look.Orange : buy > 0 ? Look.Green : MUI.Muted, GameFont.Small, TextAnchor.MiddleCenter);
            if (!tr.TraderWillTrade) return;
            // buttons: click 1, shift 10, ctrl all; the other side goes back to zero first
            float bw = (c.width - 24f) / 2f;
            Rect sb = new Rect(c.x + 8f, c.yMax - 26f, bw, 20f), bb = new Rect(sb.xMax + 8f, sb.y, bw, 20f);
            int step = Event.current.control ? 100000 : Event.current.shift ? 10 : 1;
            if (ours > 0 && Look.Button(sb, TradeSession.giftMode ? "MT_TR_Give".Translate() : "MT_TR_Sell".Translate()))
            {
                if (buy > 0) TradePlus.SetBuy(tr, 0);
                else if (Event.current.button == 1) TradePlus.SetSell(tr, Math.Max(0, sel - step));
                else TradePlus.SetSell(tr, sel + step);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            if (theirs > 0 && !TradeSession.giftMode && Look.Button(bb, "MT_TR_Buy".Translate()))
            {
                if (sel > 0) TradePlus.SetSell(tr, 0);
                else if (Event.current.button == 1) TradePlus.SetBuy(tr, Math.Max(0, buy - step));
                else TradePlus.SetBuy(tr, buy + step);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
            }
            TooltipHandler.TipRegion(new Rect(c.x, sb.y, c.width, 20f), "MT_TR_TileBtnTip".Translate());
        }

        private static List<Tradeable> Build(List<Tradeable> pool, Cat cat, Sort sort, int side, string search)
        {
            string q = (search ?? "").Trim();
            IEnumerable<Tradeable> l = pool.Where(t =>
                (cat == Cat.All || CatOf(t) == cat)
                && (side == 0 || (side == 1 ? t.CountHeldBy(Transactor.Colony) > 0 : t.CountHeldBy(Transactor.Trader) > 0))
                && (q.Length == 0 || (t.Label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (t.ThingDef?.label ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (sort)
            {
                case Sort.Name: l = l.OrderBy(t => t.Label); break;
                case Sort.Value: l = l.OrderByDescending(t => SafeValue(t)).ThenBy(t => t.Label); break;
                case Sort.Count: l = l.OrderByDescending(t => Math.Max(t.CountHeldBy(Transactor.Colony), t.CountHeldBy(Transactor.Trader))).ThenBy(t => t.Label); break;
                default: l = l.OrderBy(t => t.TraderWillTrade ? 0 : 1).ThenBy(t => (int)CatOf(t)).ThenBy(t => t.ThingDef?.label ?? "").ThenByDescending(t => SafeValue(t)); break;
            }
            return l.ToList();
        }

        private static float SafeValue(Tradeable t)
        {
            try { return t.BaseMarketValue; } catch { return 0f; }
        }

        private static void Cols(Rect row, out Rect name, out Rect ours, out Rect sell, out Rect adj, out Rect buy, out Rect theirs)
        {
            float w = row.width;
            theirs = new Rect(row.xMax - 60f, row.y, 60f, row.height);
            buy = new Rect(theirs.x - 84f, row.y, 84f, row.height);
            adj = new Rect(buy.x - 244f, row.y, 240f, row.height);
            sell = new Rect(adj.x - 84f, row.y, 84f, row.height);
            ours = new Rect(sell.x - 60f, row.y, 56f, row.height);
            name = new Rect(row.x, row.y, Mathf.Max(80f, ours.x - row.x - 6f), row.height);
        }

        private static void DrawRow(Rect row, Tradeable tr, int index)
        {
            bool changed = tr.CountToTransfer != 0;
            MUI.Fill(row, changed ? Look.GreenBg : index % 2 == 1 ? Look.Card2 : Look.Card);
            if (Mouse.IsOver(row)) MUI.Frame(row, Look.Line);
            Cols(row, out Rect cName, out Rect cOurs, out Rect cSell, out Rect cAdj, out Rect cBuy, out Rect cTheirs);
            Color nameCol = tr.TraderWillTrade ? MUI.Text1 : MUI.Muted;
            // picture + name + details
            Thing t = tr.AnyThing;
            Rect icon = new Rect(cName.x + 4f, row.y + 3f, 28f, 28f);
            try { Widgets.ThingIcon(icon, t); } catch { }
            Rect info = new Rect(cName.xMax - 24f, row.y + 5f, 24f, 24f);
            Widgets.InfoCardButton(info.x, info.y, t);
            Heart(new Rect(info.x - 22f, row.y + 7f, 20f, 20f), tr);
            float tx = icon.xMax + 6f, tw = info.x - tx - 26f;
            string label = tr.LabelCap;
            Look.Text(new Rect(tx, row.y + 1f, tw, 18f), label.Truncate(tw), nameCol);
            string sub = ("MT_TR_C" + CatOf(tr)).Translate();
            if (t != null && t.TryGetQuality(out QualityCategory qc)) sub += " · " + qc.GetLabel().CapitalizeFirst();
            if (t != null && t.def.useHitPoints && t.MaxHitPoints > 0 && t.HitPoints < t.MaxHitPoints) sub += " · " + ((float)t.HitPoints / t.MaxHitPoints).ToStringPercent();
            if (t is Apparel ap && ap.WornByCorpse) sub += " · " + "MT_TR_Tainted".Translate();
            if (t is Pawn pp && pp.ageTracker != null) sub += " · " + pp.ageTracker.AgeBiologicalYears;
            string badges = Badges(tr, TradeSession.playerNegotiator?.MapHeld ?? Find.CurrentMap, out Color bc);
            Look.Text(new Rect(tx, row.y + 17f, tw, 16f), sub.Truncate(tw), MUI.Muted, GameFont.Tiny);
            if (badges.Length > 0)
            {
                float sw = Mathf.Min(tw, Text.CalcSize(sub).x + 8f);
                Text.Font = GameFont.Tiny;
                float bwid = tw - sw;
                if (bwid > 40f) Look.Text(new Rect(tx + sw, row.y + 17f, bwid, 16f), ("· " + badges).Truncate(bwid), bc, GameFont.Tiny);
                Text.Font = GameFont.Small;
            }
            TooltipHandler.TipRegion(new Rect(cName.x, row.y, cName.width - 26f, row.height), () => tr.LabelCap + (tr.TipDescription.NullOrEmpty() ? "" : "\n\n" + tr.TipDescription), tr.GetHashCode() * 31);

            // ours / sell price
            int ours = tr.CountHeldBy(Transactor.Colony);
            if (ours != 0)
            {
                Look.Text(cOurs, ours.ToStringCached(), MUI.Text1, GameFont.Small, TextAnchor.MiddleRight);
                Price(cSell, tr, TradeAction.PlayerSells);
            }
            // adjust
            if (!tr.TraderWillTrade)
                Look.Text(cAdj, "TraderWillNotTrade".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
            else if (ModsConfig.IdeologyActive && TransferableUIUtility.TradeIsPlayerSellingToSlavery(tr, TradeSession.trader.Faction)
                && !new HistoryEvent(HistoryEventDefOf.SoldSlave, TradeSession.playerNegotiator.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
            {
                Look.Text(cAdj, "NegotiatorWillNotTradeSlaves".Translate(TradeSession.playerNegotiator), Look.Orange, GameFont.Tiny, TextAnchor.MiddleCenter);
            }
            else
            {
                Text.Font = GameFont.Small;
                TransferableUIUtility.DoCountAdjustInterface(cAdj, tr, index, tr.GetMinimumToTransfer(), tr.GetMaximumToTransfer());
            }
            // buy price / theirs
            int theirs = tr.CountHeldBy(Transactor.Trader);
            if (theirs != 0 && tr.IsThing)
            {
                Price(cBuy, tr, TradeAction.PlayerBuys);
                Look.Text(new Rect(cTheirs.x + 6f, cTheirs.y, cTheirs.width - 6f, cTheirs.height), theirs.ToStringCached(), MUI.Text1);
            }
            Look.ResetGui();
        }

        private static void Price(Rect r, Tradeable tr, TradeAction action)
        {
            if (tr.IsCurrency || !tr.TraderWillTrade) return;
            Color c = MUI.Text1;
            PriceType pt = tr.PriceTypeFor(action);
            bool good = action == TradeAction.PlayerBuys ? pt <= PriceType.Cheap : pt >= PriceType.Expensive;
            bool bad = action == TradeAction.PlayerBuys ? pt >= PriceType.Expensive : pt <= PriceType.Cheap;
            if (good) c = Look.Green; else if (bad) c = Look.Red;
            float p = tr.GetPriceFor(action);
            string s = TradeSession.TradeCurrency == TradeCurrency.Silver ? p.ToStringMoney() : p.ToString("0.#");
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            Look.Text(r, s, c, GameFont.Small, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(r, new TipSignal(() => tr.GetPriceTooltip(action), tr.GetHashCode() * 297 + (int)action));
        }

        private static void DrawCart(Rect cart, List<Tradeable> pool, Tradeable currency, ref Vector2 scroll, Map map, ref float builtAt)
        {
            // 0.15.0: smart buttons and saved carts above the totals
            float toolsH = TradeSession.giftMode ? 0f : 4 * 30f + 8f;
            CartBody(cart, pool, currency, ref scroll, toolsH);
            if (toolsH > 0f)
            {
                Rect tools = new Rect(cart.x + 8f, cart.yMax - 74f - toolsH - 4f, cart.width - 16f, toolsH);
                float half = (tools.width - 6f) / 2f;
                if (Look.Button(new Rect(tools.x, tools.y, half, 26f), "MT_TR_SellSurplus".Translate()))
                {
                    int n = TradePlus.SellSurplus(pool);
                    if (n == 0) Find.WindowStack.Add(new Dialog_TradeKeep(pool));
                    builtAt = -99f;
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
                TooltipHandler.TipRegion(new Rect(tools.x, tools.y, half, 26f), "MT_TR_SellSurplusTip".Translate());
                if (Look.Button(new Rect(tools.x + half + 6f, tools.y, half, 26f), "MT_TR_Keep".Translate()))
                    Find.WindowStack.Add(new Dialog_TradeKeep(pool));
                TooltipHandler.TipRegion(new Rect(tools.x + half + 6f, tools.y, half, 26f), "MT_TR_KeepTip".Translate());
                if (Look.Button(new Rect(tools.x, tools.y + 30f, tools.width, 26f), "MT_TR_Balance".Translate()))
                {
                    bool ok = TradePlus.Balance(pool, currency, map);
                    if (!ok) Messages.Message("MT_TR_BalanceFail".Translate(), MessageTypeDefOf.RejectInput, false);
                    builtAt = -99f;
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
                TooltipHandler.TipRegion(new Rect(tools.x, tools.y + 30f, tools.width, 26f), "MT_TR_BalanceTip".Translate());
                bool any = pool.Any(t => t.CountToTransfer != 0 && !t.IsCurrency);
                if (Look.Button(new Rect(tools.x, tools.y + 60f, half, 26f), "MT_TR_SaveCart".Translate(), false, any))
                {
                    MTMod.S.tradeCarts.Add(TradePlus.CartName(pool) + "\t" + TradePlus.CartToString(pool));
                    if (MTMod.S.tradeCarts.Count > 12) MTMod.S.tradeCarts.RemoveAt(0);
                    MTMod.S.Write();
                    Messages.Message("MT_TR_CartSaved".Translate(), MessageTypeDefOf.TaskCompletion, false);
                }
                if (Look.Button(new Rect(tools.x + half + 6f, tools.y + 60f, half, 26f), "MT_TR_LoadCart".Translate(MTMod.S.tradeCarts.Count), false, MTMod.S.tradeCarts.Count > 0))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (string c in MTMod.S.tradeCarts.ToList())
                    {
                        string cc = c;
                        string[] parts = c.Split('\t');
                        if (parts.Length != 2) continue;
                        opts.Add(new FloatMenuOption(parts[0], () => { TradePlus.ApplyCart(parts[1], pool); }));
                        opts.Add(new FloatMenuOption("   " + "MT_TR_DeleteCart".Translate(parts[0]), () => { MTMod.S.tradeCarts.Remove(cc); MTMod.S.Write(); }));
                    }
                    if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
                }
                Text.Font = GameFont.Tiny;
                Look.Text(new Rect(tools.x, tools.y + 90f, tools.width, 24f), "MT_TR_ToolsHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                Text.Font = GameFont.Small;
            }
        }

        private static void CartBody(Rect cart, List<Tradeable> pool, Tradeable currency, ref Vector2 scroll, float toolsH)
        {
            MUI.Fill(cart, Look.Card);
            MUI.Frame(cart, Look.Line);
            Look.Text(new Rect(cart.x + 10f, cart.y + 6f, cart.width - 20f, 24f), TradeSession.giftMode ? "MT_TR_GiftCart".Translate() : "MT_TR_Cart".Translate(), MUI.Text1, GameFont.Small);
            var buys = new List<Tradeable>();
            var sells = new List<Tradeable>();
            float buyTotal = 0f, sellTotal = 0f;
            foreach (Tradeable t in pool)
            {
                int n = t.CountToTransfer;
                if (n == 0) continue;
                if (t.ActionToDo == TradeAction.PlayerBuys) { buys.Add(t); buyTotal += t.GetPriceFor(TradeAction.PlayerBuys) * Math.Abs(n); }
                else if (t.ActionToDo == TradeAction.PlayerSells) { sells.Add(t); sellTotal += t.GetPriceFor(TradeAction.PlayerSells) * Math.Abs(n); }
            }
            float y = cart.y + 34f;
            Rect totals = new Rect(cart.x + 8f, cart.yMax - 74f, cart.width - 16f, 66f);
            Rect outR = new Rect(cart.x + 4f, y, cart.width - 8f, totals.y - y - 6f - (toolsH > 0f ? toolsH + 4f : 0f));
            int lines = buys.Count + sells.Count + (buys.Count > 0 ? 1 : 0) + (sells.Count > 0 ? 1 : 0);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(lines * 24f, 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                float vy = 0f;
                if (buys.Count > 0)
                {
                    Look.Text(new Rect(4f, vy, view.width, 22f), "MT_TR_Buying".Translate(), Look.Green, GameFont.Tiny);
                    vy += 24f;
                    foreach (Tradeable t in buys) { CartLine(new Rect(0f, vy, view.width, 22f), t); vy += 24f; }
                }
                if (sells.Count > 0)
                {
                    Look.Text(new Rect(4f, vy, view.width, 22f), TradeSession.giftMode ? "MT_TR_Giving".Translate() : "MT_TR_Selling".Translate(), Look.Orange, GameFont.Tiny);
                    vy += 24f;
                    foreach (Tradeable t in sells) { CartLine(new Rect(0f, vy, view.width, 22f), t); vy += 24f; }
                }
                if (lines == 0) Look.Text(new Rect(4f, 0f, view.width, 40f), "MT_TR_Empty".Translate(), MUI.Muted, GameFont.Tiny);
            }
            finally { Widgets.EndScrollView(); }
            // totals
            MUI.Fill(totals, Look.Bg);
            MUI.Frame(totals, Look.Line);
            if (TradeSession.giftMode)
            {
                int gw = 0;
                try { gw = FactionGiftUtility.GetGoodwillChange(TradeSession.deal.AllTradeables, TradeSession.trader.Faction); } catch { }
                Look.Text(new Rect(totals.x + 8f, totals.y + 4f, totals.width - 16f, 28f), "MT_TR_Goodwill".Translate(gw.ToStringWithSign()), gw > 0 ? Look.Green : MUI.Muted);
            }
            else
            {
                string money(float f) => TradeSession.TradeCurrency == TradeCurrency.Silver ? f.ToStringMoney() : f.ToString("0");
                Look.Text(new Rect(totals.x + 8f, totals.y + 2f, totals.width - 16f, 20f), "MT_TR_BuyTotal".Translate(money(buyTotal)), MUI.Muted, GameFont.Tiny);
                Look.Text(new Rect(totals.x + 8f, totals.y + 20f, totals.width - 16f, 20f), "MT_TR_SellTotal".Translate(money(sellTotal)), MUI.Muted, GameFont.Tiny);
                float net = sellTotal - buyTotal;
                if (currency != null) net = currency.CountPostDealFor(Transactor.Colony) - currency.CountHeldBy(Transactor.Colony);
                Look.Text(new Rect(totals.x + 8f, totals.y + 38f, totals.width - 16f, 24f), "MT_TR_Net".Translate((net > 0 ? "+" : "") + money(net)), net >= 0 ? Look.Green : Look.Orange);
            }
        }

        private static void CartLine(Rect r, Tradeable t)
        {
            try { Widgets.ThingIcon(new Rect(r.x + 2f, r.y + 1f, 20f, 20f), t.AnyThing); } catch { }
            int n = Math.Abs(t.CountToTransfer);
            Look.Text(new Rect(r.x + 26f, r.y, r.width - 30f, r.height), (n + "× " + t.LabelCap).Truncate(r.width - 30f), MUI.Text1, GameFont.Tiny);
            if (Widgets.ButtonInvisible(r)) { t.AdjustTo(0); SoundDefOf.Tick_Low.PlayOneShotOnCamera(); }
            TooltipHandler.TipRegion(r, "MT_TR_CartTip".Translate());
        }

        private static void DrawBottom(Dialog_Trade dlg, Rect bot, bool giftsOnly, ref List<Tradeable> list, ref float builtAt)
        {
            float bw = 170f, bh = 40f;
            Rect accept = new Rect(bot.center.x - bw / 2f, bot.y + 6f, bw, bh);
            string acceptLabel = TradeSession.giftMode
                ? "OfferGifts".Translate() + " (" + FactionGiftUtility.GetGoodwillChange(TradeSession.deal.AllTradeables, TradeSession.trader.Faction).ToStringWithSign() + ")"
                : "AcceptButton".Translate().ToString();
            if (Button(accept, acceptLabel, Look.Green))
            {
                Action action = delegate
                {
                    if (TradeSession.deal.TryExecute(out bool actuallyTraded))
                    {
                        if (actuallyTraded)
                        {
                            SoundDefOf.ExecuteTrade.PlayOneShotOnCamera();
                            TradeSession.playerNegotiator.GetCaravan()?.RecacheInventory();
                            dlg.Close(doCloseSound: false);
                        }
                        else dlg.Close();
                    }
                };
                if (TradeSession.deal.DoesTraderHaveEnoughSilver()) action();
                else
                {
                    dlg.FlashSilver();
                    SoundDefOf.ClickReject.PlayOneShotOnCamera();
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("ConfirmTraderShortFunds".Translate(), action));
                }
                Event.current.Use();
            }
            if (Button(new Rect(accept.x - 10f - bw, accept.y, bw, bh), "ResetButton".Translate(), Look.Line))
            {
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                TradeSession.deal.Reset();
                builtAt = -99f;
            }
            if (Button(new Rect(accept.xMax + 10f, accept.y, bw, bh), "CancelButton".Translate(), Look.Line))
            {
                dlg.Close();
                Event.current.Use();
            }
            // what they buy, gift mode
            Rect sell = new Rect(bot.xMax - bh, accept.y, bh, bh);
            if (Widgets.ButtonImageWithBG(sell, SellableIcon, new Vector2(32f, 32f)))
                Find.WindowStack.Add(new Dialog_SellableItems(TradeSession.trader));
            TooltipHandler.TipRegionByKey(sell, "CommandShowSellableItemsDesc");
            Faction faction = TradeSession.trader.Faction;
            if (faction != null && !giftsOnly && !faction.def.permanentEnemy && TradeSession.trader.TradeCurrency != TradeCurrency.Favor)
            {
                Rect g = new Rect(sell.x - bh - 4f, accept.y, bh, bh);
                if (Widgets.ButtonImageWithBG(g, TradeSession.giftMode ? TradeIcon : GiftIcon, new Vector2(32f, 32f)))
                {
                    TradeSession.giftMode = !TradeSession.giftMode;
                    TradeSession.deal.Reset();
                    builtAt = -99f;
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
                if (TradeSession.giftMode) TooltipHandler.TipRegionByKey(g, "TradeModeTip");
                else TooltipHandler.TipRegionByKey(g, "GiftModeTip", faction.Name);
            }
            // vanilla window, just for this trade
            Rect van = new Rect(bot.x, accept.y + 8f, 200f, 24f);
            Look.Text(van, "MT_TR_Hint".Translate(), MUI.Muted, GameFont.Tiny);
        }

        private static bool Button(Rect r, string label, Color accent)
        {
            bool over = Mouse.IsOver(r);
            MUI.Fill(r, over ? Look.Card2 : Look.Card);
            MUI.Frame(r, accent);
            Look.Text(r, label.Truncate(r.width - 8f), accent == Look.Line ? MUI.Text1 : accent, GameFont.Small, TextAnchor.MiddleCenter);
            return Widgets.ButtonInvisible(r);
        }
    }
}
