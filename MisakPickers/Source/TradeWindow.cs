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

namespace MisakPickers
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
                var h = new Harmony("misak.pickers.trade");
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
            MUI.Fill(sideR, Look.Card);
            MUI.Frame(sideR, Look.Line);
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
                if (Widgets.ButtonInvisible(b)) { setCat(c); SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
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

            // rebuild the list now and then (counts change while trading, so cheaply every half second)
            string key = cat + "|" + sort + "|" + side + "|" + search + "|" + TradeSession.giftMode;
            if (list == null || key != builtKey || Time.realtimeSinceStartup - builtAt > 0.5f)
            {
                list = Build(pool, cat, sort, side, search);
                builtKey = key;
                builtAt = Time.realtimeSinceStartup;
            }

            // column header
            Rect lr = new Rect(mid.x, mid.y + 36f, mid.width, mid.height - 36f);
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
            DrawCart(cart, pool, currency, ref cartScroll);

            // ---- bottom buttons ----
            Rect bot = new Rect(r.x, r.yMax - bottomH, r.width, bottomH);
            DrawBottom(dlg, bot, giftsOnly, ref list, ref builtAt);
            Look.ResetGui();
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
            float tx = icon.xMax + 6f, tw = info.x - tx - 4f;
            string label = tr.LabelCap;
            Look.Text(new Rect(tx, row.y + 1f, tw, 18f), label.Truncate(tw), nameCol);
            string sub = ("MT_TR_C" + CatOf(tr)).Translate();
            if (t != null && t.TryGetQuality(out QualityCategory qc)) sub += " · " + qc.GetLabel().CapitalizeFirst();
            if (t != null && t.def.useHitPoints && t.MaxHitPoints > 0 && t.HitPoints < t.MaxHitPoints) sub += " · " + ((float)t.HitPoints / t.MaxHitPoints).ToStringPercent();
            if (t is Apparel ap && ap.WornByCorpse) sub += " · " + "MT_TR_Tainted".Translate();
            if (t is Pawn pp && pp.ageTracker != null) sub += " · " + pp.ageTracker.AgeBiologicalYears;
            Look.Text(new Rect(tx, row.y + 17f, tw, 16f), sub.Truncate(tw), MUI.Muted, GameFont.Tiny);
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

        private static void DrawCart(Rect cart, List<Tradeable> pool, Tradeable currency, ref Vector2 scroll)
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
            Rect outR = new Rect(cart.x + 4f, y, cart.width - 8f, totals.y - y - 6f);
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
