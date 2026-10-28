using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace TraderHotline
{
    // 0.3.0 what kinds of goods a trader deals in
    public enum Cat { Weapons, Apparel, Medicine, Food, Materials, Tech, Art, Slaves, Other }

    // ---------------------------------------------------------------------------
    // 0.3.0 Catalog: what every trader kind sells, sorted into categories (worked out from the
    // trader's stock generators, so traders from mods are sorted too), and special orders.
    // ---------------------------------------------------------------------------
    public static class Catalog
    {
        public sealed class Info
        {
            public Dictionary<Cat, List<ThingDef>> goods = new Dictionary<Cat, List<ThingDef>>();
            public HashSet<Cat> cats = new HashSet<Cat>();     // what it is known for
            public List<ThingDef> sample = new List<ThingDef>(); // a few typical items for the card
            public string search = "";                         // all item labels, for the search box
        }

        private static readonly Dictionary<TraderKindDef, Info> cache = new Dictionary<TraderKindDef, Info>();
        public static readonly Cat[] AllCats = (Cat[])Enum.GetValues(typeof(Cat));

        public static Cat CatOf(ThingDef d)
        {
            if (d.race != null) return d.race.Humanlike ? Cat.Slaves : Cat.Food;
            if (d.IsWeapon) return Cat.Weapons;
            if (d.IsApparel)
            {
                float armor = d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, GenStuff.DefaultStuffFor(d));
                return armor >= 0.4f ? Cat.Weapons : Cat.Apparel;
            }
            if (d.IsMedicine || d.IsDrug) return Cat.Medicine;
            if (d.IsIngestible || d.plant != null) return Cat.Food;
            if (d.IsArt || (d.thingCategories != null && d.thingCategories.Any(c => c.defName.Contains("Art") || c.defName.Contains("Luxury")))) return Cat.Art;
            if (d.techLevel >= TechLevel.Spacer || d.HasComp(typeof(CompTechprint)) || (d.thingCategories != null && d.thingCategories.Any(c => c.defName.StartsWith("BodyParts")))) return Cat.Tech;
            if (d.IsStuff || d.stackLimit > 1) return Cat.Materials;
            return Cat.Other;
        }

        public static Info Of(TraderKindDef k)
        {
            if (cache.TryGetValue(k, out Info info)) return info;
            info = new Info();
            try
            {
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (d.category != ThingCategory.Item && !d.Minifiable && d.race == null) continue;
                    if (d.tradeability == Tradeability.None || d.BaseMarketValue <= 0f || d.uiIcon == null) continue;
                    bool sells = false;
                    foreach (StockGenerator g in k.stockGenerators)
                        if (g.HandlesThingDef(d)) { sells = true; break; }
                    if (!sells) continue;
                    Cat c = CatOf(d);
                    if (!info.goods.TryGetValue(c, out var l)) info.goods[c] = l = new List<ThingDef>();
                    l.Add(d);
                }
                int total = info.goods.Sum(kv => kv.Value.Count);
                foreach (var kv in info.goods)
                    if (kv.Key == Cat.Slaves || kv.Value.Count >= Mathf.Max(3, total * 0.1f)) info.cats.Add(kv.Key);
                if (k.category == "Slaver") info.cats.Add(Cat.Slaves);
                if (info.cats.Count == 0 && info.goods.Count > 0) info.cats.Add(info.goods.OrderByDescending(kv => kv.Value.Count).First().Key);
                foreach (var kv in info.goods)
                    kv.Value.Sort((a, b) => b.BaseMarketValue.CompareTo(a.BaseMarketValue));
                // the card shows the most valuable item of each of its biggest categories
                foreach (var kv in info.goods.OrderByDescending(kv => kv.Value.Count))
                {
                    if (info.sample.Count >= 6) break;
                    info.sample.Add(kv.Value[Mathf.Min(kv.Value.Count - 1, 2)]);
                }
                info.search = string.Join(" ", info.goods.SelectMany(kv => kv.Value).Select(d => d.label).ToArray()).ToLower();
            }
            catch (Exception e) { Log.Warning("[Trader Hotline] catalog of " + k.defName + ": " + e.Message); }
            cache[k] = info;
            return info;
        }

        public static string CatLabel(Cat c) => ("TH_Cat_" + c).Translate();

        private static readonly Dictionary<Cat, Texture2D> icons = new Dictionary<Cat, Texture2D>();

        public static Texture2D CatIcon(Cat c)
        {
            if (icons.TryGetValue(c, out var t)) return t;
            string[] names;
            switch (c)
            {
                case Cat.Weapons: names = new[] { "Gun_AssaultRifle", "MeleeWeapon_LongSword" }; break;
                case Cat.Apparel: names = new[] { "Apparel_Parka", "Apparel_CollarShirt" }; break;
                case Cat.Medicine: names = new[] { "MedicineIndustrial", "MedicineHerbal" }; break;
                case Cat.Food: names = new[] { "MealSimple", "RawPotatoes" }; break;
                case Cat.Materials: names = new[] { "Steel", "WoodLog" }; break;
                case Cat.Tech: names = new[] { "ComponentSpacer", "ComponentIndustrial" }; break;
                case Cat.Art: names = new[] { "SculptureSmall", "Gold" }; break;
                case Cat.Slaves: names = new[] { "Apparel_SlaveBodyStrap", "Apparel_BodyStrap" }; break;
                default: names = new[] { "Silver" }; break;
            }
            t = names.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n)?.uiIcon).FirstOrDefault(x => x != null);
            icons[c] = t;
            return t;
        }

        // goods for a special order: items of that category the trader deals in, worth about requestValue
        public static List<Thing> MakeRequest(TraderKindDef k, Cat c)
        {
            var result = new List<Thing>();
            try
            {
                if (!Of(k).goods.TryGetValue(c, out var defs)) return result;
                var pool = defs.Where(d => d.race == null && d.category == ThingCategory.Item).ToList();
                if (pool.Count == 0) return result;
                float target = THMod.S.requestValue, sum = 0f;
                int guard = 0;
                while (sum < target * 0.9f && guard++ < 60)
                {
                    // cheaper items more often, so an order is not one single expensive thing
                    ThingDef d = pool.RandomElementByWeight(x => 1f / Mathf.Max(5f, x.BaseMarketValue));
                    ThingDef stuff = d.MadeFromStuff ? GenStuff.RandomStuffFor(d) : null;
                    Thing t = ThingMaker.MakeThing(d, stuff);
                    t.TryGetComp<CompQuality>()?.SetQuality(QualityUtility.GenerateQualityTraderItem(), ArtGenerationContext.Outsider);
                    float one = Mathf.Max(1f, t.MarketValue);
                    int n = Mathf.Clamp(Mathf.FloorToInt((target - sum) / one), 1, d.stackLimit);
                    if (d.stackLimit > 1) n = Mathf.Max(1, Mathf.Min(n, Mathf.CeilToInt(target * 0.4f / one)));
                    t.stackCount = n;
                    sum += one * n;
                    result.Add(t);
                }
                Log.Message("[Trader Hotline] special order " + c + " for " + k.defName + ": " + result.Count + " stacks worth " + Mathf.RoundToInt(sum));
            }
            catch (Exception e) { Log.Warning("[Trader Hotline] special order: " + e.Message); }
            return result;
        }

        // what the trader would buy from the colony's stores, and roughly for how much
        public static float SellEstimate(Map map, TraderKindDef k, List<ThingDef> top)
        {
            var byDef = new Dictionary<ThingDef, float>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t.def == ThingDefOf.Silver || !t.IsInAnyStorage() || t.IsForbidden(Faction.OfPlayer)) continue;
                if (!k.WillTrade(t.def)) continue;
                byDef.TryGetValue(t.def, out float v);
                byDef[t.def] = v + t.MarketValue * t.stackCount;
            }
            top.Clear();
            top.AddRange(byDef.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key));
            return byDef.Values.Sum() * 0.5f;    // traders pay about half the market value
        }
    }

    // ---------------------------------------------------------------------------
    // 0.3.0 the call window: categories on the left, trader cards in the middle, the chosen
    // trader on the right (what it sells and buys, special order, delivery, from where).
    // ---------------------------------------------------------------------------
    public class Dialog_Hotline : Window
    {
        private enum View { All, Fav, Recent, Ships, Caravans, Cat }

        private readonly Map map;
        private readonly List<Offer> offers;
        private View view = View.All;
        private Cat cat;
        private string search = "";
        private Offer sel;
        private bool express;
        private int request = -1;
        private int edge = -1;
        private Vector2 scrollCards, scrollDetail;
        private Offer estFor;
        private float est;
        private readonly List<ThingDef> estTop = new List<ThingDef>();

        private static View lastView = View.All;
        private static Cat lastCat;
        private static string lastKey;

        public override Vector2 InitialSize => new Vector2(1080f, 720f);

        public Dialog_Hotline(Map map)
        {
            this.map = map;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            offers = Hotline.Ships().ToList();
            offers.AddRange(Hotline.Caravans(map).OrderBy(o => o.faction.Name).ThenBy(o => o.kind.label));
            view = lastView;
            cat = lastCat;
            sel = offers.FirstOrDefault(o => o.Key == lastKey) ?? offers.FirstOrDefault();
        }

        public override void PreClose()
        {
            base.PreClose();
            lastView = view;
            lastCat = cat;
            lastKey = sel?.Key;
        }

        private IEnumerable<Offer> Filtered()
        {
            IEnumerable<Offer> l = offers;
            switch (view)
            {
                case View.Fav: l = l.Where(o => o.Fav); break;
                case View.Recent:
                    var h = HotlineComponent.Get?.history ?? new List<CallRecord>();
                    l = h.Select(r => offers.FirstOrDefault(o => o.kind == r.kind && o.caravan == r.caravan && (!o.caravan || o.faction == r.faction))).Where(o => o != null).Distinct();
                    break;
                case View.Ships: l = l.Where(o => !o.caravan); break;
                case View.Caravans: l = l.Where(o => o.caravan); break;
                case View.Cat: l = l.Where(o => Catalog.Of(o.kind).cats.Contains(cat)); break;
            }
            if (!search.NullOrEmpty())
            {
                string q = search.ToLower();
                l = l.Where(o => o.Label.ToLower().Contains(q) || Catalog.Of(o.kind).search.Contains(q));
            }
            if (view != View.Recent) l = l.OrderByDescending(o => o.Fav);
            return l;
        }

        public override void DoWindowContents(Rect r)
        {
            // ---- header ----
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, 400f, 34f), "TH_Title".Translate());
            Text.Font = GameFont.Small;
            int silver = Hotline.SilverOn(map);
            GUI.color = MUI.Muted;
            string sub = "TH_Silver".Translate(silver);
            float disc = Hotline.Discount();
            if (disc > 0f) sub += "   ·   " + "TH_LoyaltyNow".Translate(Mathf.RoundToInt(disc * 100f));
            string blocked = Hotline.Blocked(map);
            if (blocked != null) sub += "   ·   " + blocked;
            Widgets.Label(new Rect(r.x, r.y + 34f, r.width - 330f, 24f), sub);
            GUI.color = Color.white;
            Rect sr = new Rect(r.xMax - 300f, r.y + 4f, 270f, 30f);
            GUI.DrawTexture(new Rect(sr.x - 26f, sr.y + 5f, 20f, 20f), TexButton.Search);
            search = Widgets.TextField(sr, search);

            float top = r.y + 64f;
            Rect left = new Rect(r.x, top, 200f, r.yMax - top);
            Rect mid = new Rect(left.xMax + 10f, top, 380f, r.yMax - top);
            Rect right = new Rect(mid.xMax + 12f, top, r.xMax - mid.xMax - 12f, r.yMax - top);
            DrawCategories(left);
            DrawCards(mid);
            DrawDetail(right, silver, blocked);
        }

        // ---- left: categories ----
        private void DrawCategories(Rect r)
        {
            MUI.Fill(r, MUI.CardBg, 4f);
            float y = r.y + 6f;
            Row(r, ref y, "TH_ViewAll".Translate(), null, offers.Count, view == View.All, () => view = View.All);
            Row(r, ref y, "TH_ViewFav".Translate(), ContentFinder<Texture2D>.Get("UI/Icons/Stars", false), offers.Count(o => o.Fav), view == View.Fav, () => view = View.Fav);
            Row(r, ref y, "TH_ViewRecent".Translate(), null, HotlineComponent.Get?.history.Count ?? 0, view == View.Recent, () => view = View.Recent);
            y += 6f;
            Row(r, ref y, "TH_SecOrbital".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("OrbitalTradeBeacon")?.uiIcon, offers.Count(o => !o.caravan), view == View.Ships, () => view = View.Ships);
            Row(r, ref y, "TH_SecCaravan".Translate(), DefDatabase<ThingDef>.GetNamedSilentFail("Muffalo")?.uiIcon, offers.Count(o => o.caravan), view == View.Caravans, () => view = View.Caravans);
            y += 6f;
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 10f, y, r.width - 20f, 18f), "TH_BySells".Translate().ToString().ToUpper());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 20f;
            foreach (Cat c in Catalog.AllCats)
            {
                int n = offers.Count(o => Catalog.Of(o.kind).cats.Contains(c));
                if (n == 0) continue;
                Cat cc = c;
                Row(r, ref y, Catalog.CatLabel(c), Catalog.CatIcon(c), n, view == View.Cat && cat == c, () => { view = View.Cat; cat = cc; });
            }
        }

        private static void Row(Rect area, ref float y, string label, Texture2D icon, int count, bool on, Action click)
        {
            Rect row = new Rect(area.x + 4f, y, area.width - 8f, 28f);
            if (on) Widgets.DrawHighlightSelected(row);
            else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
            if (icon != null) GUI.DrawTexture(new Rect(row.x + 4f, row.y + 4f, 20f, 20f), icon);
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.Label(new Rect(row.x + 30f, row.y, row.width - 64f, row.height), label.Truncate(row.width - 64f));
            GUI.color = MUI.Muted;
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(row.xMax - 34f, row.y, 30f, row.height), count.ToString());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            if (Widgets.ButtonInvisible(row)) click();
            y += 30f;
        }

        // ---- middle: cards ----
        private void DrawCards(Rect r)
        {
            List<Offer> list = Filtered().ToList();
            const float h = 92f;
            Rect view = new Rect(0f, 0f, r.width - 16f, Mathf.Max(list.Count * (h + 6f), 10f));
            if (list.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(r.x + 8f, r.y + 8f, r.width - 16f, 60f), "TH_NoneHere".Translate());
                GUI.color = Color.white;
                return;
            }
            Widgets.BeginScrollView(r, ref scrollCards, view);
            for (int i = 0; i < list.Count; i++)
                Card(new Rect(0f, i * (h + 6f), view.width, h), list[i]);
            Widgets.EndScrollView();
        }

        private void Card(Rect r, Offer o)
        {
            MUI.Card(r, o == sel);
            if (Mouse.IsOver(r) && o != sel) Widgets.DrawHighlight(r);
            Faction f = o.faction;
            Rect ic = new Rect(r.x + 8f, r.y + 8f, 36f, 36f);
            if (f != null && f.def.FactionIcon != null)
            {
                GUI.color = f.Color;
                GUI.DrawTexture(ic, f.def.FactionIcon);
                GUI.color = Color.white;
            }
            else if (Catalog.CatIcon(Catalog.Of(o.kind).cats.FirstOrDefault()) is Texture2D t) GUI.DrawTexture(ic, t);
            Widgets.Label(new Rect(r.x + 52f, r.y + 6f, r.width - 150f, 24f), o.kind.LabelCap.Resolve().Truncate(r.width - 150f));
            Text.Font = GameFont.Tiny;
            string line = o.caravan ? f.Name : "TH_ShipShort".Translate().ToString();
            if (o.caravan && f != null)
            {
                int gw = f.PlayerGoodwill;
                string col = gw >= 25 ? "#8fd17a" : gw >= 0 ? "#e0c060" : "#e07a6a";
                line += "  <color=" + col + ">" + (gw >= 0 ? "+" : "") + gw + "</color>";
            }
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x + 52f, r.y + 28f, r.width - 150f, 18f), line);
            GUI.color = Color.white;
            // price
            int fee = Hotline.Fee(map, false, o.caravan);
            Text.Anchor = TextAnchor.UpperRight;
            Widgets.Label(new Rect(r.xMax - 120f, r.y + 30f, 112f, 18f), "TH_FromPrice".Translate(fee));
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            // favourite star
            Rect star = new Rect(r.xMax - 30f, r.y + 6f, 22f, 22f);
            Texture2D st = ContentFinder<Texture2D>.Get("UI/Icons/Stars", false);
            if (st != null)
            {
                GUI.color = o.Fav ? new Color(1f, 0.82f, 0.3f) : new Color(1f, 1f, 1f, 0.25f);
                GUI.DrawTexture(star, st);
                GUI.color = Color.white;
            }
            TooltipHandler.TipRegion(star, "TH_FavTip".Translate());
            if (Widgets.ButtonInvisible(star))
            {
                if (o.Fav) THMod.S.favorites.Remove(o.Key); else THMod.S.favorites.Add(o.Key);
                try { LoadedModManager.GetMod<THMod>().WriteSettings(); } catch { }
                return;
            }
            // typical goods
            float x = r.x + 52f;
            foreach (ThingDef d in Catalog.Of(o.kind).sample)
            {
                Rect g = new Rect(x, r.y + 54f, 30f, 30f);
                Widgets.DefIcon(g, d);
                TooltipHandler.TipRegion(g, d.LabelCap);
                x += 34f;
            }
            if (Widgets.ButtonInvisible(r))
            {
                if (sel != o) { request = -1; scrollDetail = Vector2.zero; }
                sel = o;
            }
        }

        // ---- right: the chosen trader ----
        private void DrawDetail(Rect r, int silver, string blocked)
        {
            if (sel == null) return;
            MUI.Fill(r, MUI.CardBg, 4f);
            r = r.ContractedBy(10f);
            Catalog.Info info = Catalog.Of(sel.kind);
            float y = r.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, y, r.width, 32f), sel.kind.LabelCap);
            Text.Font = GameFont.Small;
            y += 32f;
            GUI.color = MUI.Muted;
            string kind = (sel.caravan ? "TH_KindCaravan" : "TH_KindShip").Translate();
            if (sel.faction != null) kind = "TH_Faction".Translate(sel.faction.Name) + " · " + kind;
            float kh = Text.CalcHeight(kind, r.width);
            Widgets.Label(new Rect(r.x, y, r.width, kh), kind);
            GUI.color = Color.white;
            y += kh + 6f;

            // the part that scrolls: sells, buys
            float bottomH = 250f;
            Rect sv = new Rect(r.x, y, r.width, r.yMax - y - bottomH);
            float inner = sv.width - 16f;
            float need = 24f + info.goods.Count * 64f + 110f;
            Rect view = new Rect(0f, 0f, inner, need);
            Widgets.BeginScrollView(sv, ref scrollDetail, view);
            float vy = 0f;
            Header(new Rect(0f, vy, inner, 20f), "TH_Sells".Translate());
            vy += 22f;
            foreach (var kv in info.goods.OrderByDescending(kv => kv.Value.Count))
            {
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(0f, vy, inner, 18f), Catalog.CatLabel(kv.Key) + " (" + kv.Value.Count + ")");
                Text.Font = GameFont.Small;
                vy += 18f;
                float x = 0f;
                int shown = 0;
                foreach (ThingDef d in kv.Value)
                {
                    if (x + 30f > inner - 40f) break;
                    Rect g = new Rect(x, vy, 28f, 28f);
                    Widgets.DefIcon(g, d);
                    TooltipHandler.TipRegion(g, d.LabelCap + "\n" + d.BaseMarketValue.ToStringMoney());
                    x += 31f;
                    shown++;
                }
                if (shown < kv.Value.Count)
                {
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(x + 2f, vy + 4f, 40f, 22f), "+" + (kv.Value.Count - shown));
                    GUI.color = Color.white;
                }
                vy += 44f;
            }
            // what it buys from us
            if (estFor != sel) { est = Catalog.SellEstimate(map, sel.kind, estTop); estFor = sel; }
            vy += 4f;
            Header(new Rect(0f, vy, inner, 20f), "TH_Buys".Translate(Mathf.RoundToInt(est)));
            vy += 22f;
            float bx = 0f;
            foreach (ThingDef d in estTop)
            {
                Rect g = new Rect(bx, vy, 28f, 28f);
                Widgets.DefIcon(g, d);
                TooltipHandler.TipRegion(g, d.LabelCap);
                bx += 31f;
            }
            if (estTop.Count == 0) { GUI.color = MUI.Muted; Widgets.Label(new Rect(0f, vy, inner, 24f), "TH_BuysNothing".Translate()); GUI.color = Color.white; }
            vy += 34f;
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0f, vy, inner, 30f), "TH_BuysNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            Widgets.EndScrollView();

            // ---- the order ----
            y = r.yMax - bottomH + 6f;
            Widgets.DrawLineHorizontal(r.x, y - 4f, r.width);
            // special order
            var reqCats = info.goods.Where(kv => kv.Key != Cat.Slaves && kv.Value.Any(d => d.category == ThingCategory.Item && d.race == null)).Select(kv => kv.Key).ToList();
            string reqLabel = request < 0 ? "TH_ReqNone".Translate().ToString() : "TH_ReqSome".Translate(Catalog.CatLabel((Cat)request), Mathf.RoundToInt(THMod.S.requestValue), Mathf.RoundToInt(Hotline.RequestFee() * (1f - Hotline.Discount()))).ToString();
            Widgets.Label(new Rect(r.x, y + 4f, 150f, 24f), "TH_Request".Translate());
            if (Widgets.ButtonText(new Rect(r.x + 150f, y, r.width - 150f, 30f), reqLabel))
            {
                var opts = new List<FloatMenuOption> { new FloatMenuOption("TH_ReqNone".Translate(), () => request = -1) };
                foreach (Cat c in reqCats)
                {
                    Cat cc = c;
                    opts.Add(new FloatMenuOption(Catalog.CatLabel(c), () => request = (int)cc, Catalog.CatIcon(c) ?? BaseContent.ClearTex, Color.white));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(new Rect(r.x, y, r.width, 30f), "TH_Request_Desc".Translate(Mathf.RoundToInt(THMod.S.requestValue)));
            y += 36f;
            // where a caravan comes from
            if (sel.caravan)
            {
                Widgets.Label(new Rect(r.x, y + 4f, 150f, 24f), "TH_Edge".Translate());
                string el = edge < 0 ? "TH_EdgeAny".Translate().ToString() : new Rot4(edge).ToStringHuman().CapitalizeFirst();
                if (Widgets.ButtonText(new Rect(r.x + 150f, y, r.width - 150f, 30f), el))
                {
                    var opts = new List<FloatMenuOption> { new FloatMenuOption("TH_EdgeAny".Translate(), () => edge = -1) };
                    for (int i = 0; i < 4; i++)
                    {
                        int ii = i;
                        opts.Add(new FloatMenuOption(new Rot4(i).ToStringHuman().CapitalizeFirst(), () => edge = ii));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                y += 36f;
            }
            // delivery
            int feeN = Hotline.Fee(map, false, sel.caravan, request), feeE = Hotline.Fee(map, true, sel.caravan, request);
            if (Widgets.RadioButtonLabeled(new Rect(r.x, y, r.width, 26f), "TH_Normal".Translate(THMod.S.delayDays.ToString("0.0"), feeN), !express)) express = false;
            y += 28f;
            if (Widgets.RadioButtonLabeled(new Rect(r.x, y, r.width, 26f), "TH_ExpressOpt".Translate(Mathf.RoundToInt(THMod.S.expressHours), feeE), express)) express = true;
            y += 30f;
            int fee = express ? feeE : feeN;
            // warnings and why it can't be called
            string why = blocked;
            if (why == null && silver < fee) why = "TH_NoSilver".Translate(fee);
            string warn = null;
            if (GenHostility.AnyHostileActiveThreatToPlayer(map)) warn = "TH_WarnDanger".Translate();
            if (sel.caravan && sel.faction != null && sel.faction.PlayerGoodwill < 0) warn = (warn == null ? "" : warn + " ") + "TH_WarnGoodwill".Translate();
            Rect b = new Rect(r.xMax - 280f, r.yMax - 40f, 280f, 40f);
            float ty = b.y - 4f;
            if (why != null)
            {
                GUI.color = MUI.Warn;
                float wh = Text.CalcHeight(why, r.width - 290f);
                Widgets.Label(new Rect(r.x, b.y + 2f, r.width - 290f, Mathf.Max(wh, 36f)), why);
                GUI.color = Color.white;
            }
            else if (warn != null)
            {
                GUI.color = MUI.Warn;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x, b.y, r.width - 290f, 40f), warn);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            float disc = Hotline.Discount();
            if (disc > 0f)
            {
                GUI.color = MUI.Accent;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(r.x, y, r.width, 18f), "TH_LoyaltyNow".Translate(Mathf.RoundToInt(disc * 100f)));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
            }
            if (Widgets.ButtonText(b, "TH_CallBtn".Translate(fee), true, true, why == null))
            {
                Hotline.Call(map, sel, express, request, edge);
                Close();
            }
        }

        private static void Header(Rect r, string text)
        {
            GUI.color = MUI.Accent;
            Text.Font = GameFont.Tiny;
            Widgets.Label(r, text.ToUpper());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }
    }
}
