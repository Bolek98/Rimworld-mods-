// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakGear
{
    // ---------------------------------------------------------------------------
    // 0.15.0 Clear Trade extras: sub-categories, what the colony needs, upgrades for colonists, a memory
    // of prices, selling the surplus, balancing with goods, a wish list (with a letter when a trader
    // brings something from it) and saved carts.
    // ---------------------------------------------------------------------------
    public static class TradePlus
    {
        // ---------------- buy / sell helpers (the count direction differs in gift mode) ----------------
        public static int Selling(Tradeable t) => Math.Max(0, t.CountToTransferToDestination);
        public static int Buying(Tradeable t) => Math.Max(0, -t.CountToTransferToDestination);

        public static void SetSell(Tradeable t, int n) => SetDest(t, Mathf.Clamp(n, 0, t.CountHeldBy(Transactor.Colony)));
        public static void SetBuy(Tradeable t, int n) => SetDest(t, -Mathf.Clamp(n, 0, t.CountHeldBy(Transactor.Trader)));

        private static void SetDest(Tradeable t, int dest)
        {
            int v = t.PositiveCountDirection == TransferablePositiveCountDirection.Destination ? dest : -dest;
            v = Mathf.Clamp(v, t.GetMinimumToTransfer(), t.GetMaximumToTransfer());
            if (t.CanAdjustTo(v).Accepted) t.AdjustTo(v);
        }

        // ---------------- sub-categories ----------------
        public static string SubOf(Tradeable tr, TradeWindow.Cat cat)
        {
            ThingDef d = tr.ThingDef;
            Thing t = tr.AnyThing;
            if (d == null) return "other";
            try
            {
                switch (cat)
                {
                    case TradeWindow.Cat.Food:
                        if (d.defName.Contains("Kibble") || d.defName.Contains("Hay") || d.defName.Contains("Feed")) return "feed";
                        if (d.ingestible != null && d.ingestible.preferability >= FoodPreferability.MealAwful) return "meals";
                        if (d.ingestible != null && d.ingestible.preferability <= FoodPreferability.RawTasty && d.ingestible.preferability >= FoodPreferability.RawBad) return "raw";
                        return "other";
                    case TradeWindow.Cat.Medicine:
                        if (d.IsMedicine) return "medicine";
                        if (d.IsDrug) return "drugs";
                        return "parts";
                    case TradeWindow.Cat.Weapons:
                        if (d.weaponTags != null && d.weaponTags.Any(x => x.Contains("Grenade"))) return "grenades";
                        return d.IsRangedWeapon ? "ranged" : "melee";
                    case TradeWindow.Cat.Apparel:
                        var ap = d.apparel;
                        if (ap == null) return "clothes";
                        if (ap.layers != null && ap.layers.Contains(ApparelLayerDefOf.Belt)) return "belt";
                        if (ap.layers != null && ap.layers.Contains(ApparelLayerDefOf.Overhead) || ap.bodyPartGroups != null && ap.bodyPartGroups.Any(g => g == BodyPartGroupDefOf.FullHead || g == BodyPartGroupDefOf.UpperHead)) return "head";
                        if (t != null && t.GetStatValue(StatDefOf.ArmorRating_Sharp) >= 0.4f) return "armor";
                        return "clothes";
                    case TradeWindow.Cat.Resources:
                        var cats = d.stuffProps?.categories;
                        if (cats != null && cats.Contains(StuffCategoryDefOf.Metallic)) return "metal";
                        if (cats != null && (cats.Contains(StuffCategoryDefOf.Fabric) || cats.Contains(StuffCategoryDefOf.Leathery))) return "textile";
                        if (cats != null && cats.Contains(StuffCategoryDefOf.Stony)) return "stone";
                        if (cats != null && cats.Contains(StuffCategoryDefOf.Woody)) return "wood";
                        return "other";
                    case TradeWindow.Cat.Items:
                        if (d.thingCategories != null && d.thingCategories.Contains(ThingCategoryDefOf.Techprints)) return "techprint";
                        if (d.thingCategories != null && d.thingCategories.Contains(ThingCategoryDefOf.Neurotrainers)) return "neuro";
                        return "other";
                }
            }
            catch { }
            return null;
        }

        // ---------------- what the colony needs ----------------
        public class Need
        {
            public string key, label;
            public Func<Thing, bool> match;
        }

        private static List<Need> needs;
        private static int needsAt = -99999;

        public static List<Need> Needs(Map map)
        {
            if (map == null) return new List<Need>();
            if (needs != null && Find.TickManager.TicksGame - needsAt < 600) return needs;
            needsAt = Find.TickManager.TicksGame;
            needs = new List<Need>();
            try
            {
                var col = map.mapPawns.FreeColonists.Where(p => p.RaceProps.Humanlike).ToList();
                int n = Math.Max(1, col.Count);
                var rc = map.resourceCounter;
                int med = DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsMedicine).Sum(d => rc.GetCount(d));
                if (med < n * 4) needs.Add(new Need { key = "medicine", label = "MT_TR_NeedMedicine".Translate(med, n * 4), match = t => t.def.IsMedicine });
                float days = rc.TotalHumanEdibleNutrition / (n * 1.6f);
                if (days < 15f) needs.Add(new Need { key = "food", label = "MT_TR_NeedFood".Translate(days.ToString("0")), match = t => t.def.IsNutritionGivingIngestible && t.def.ingestible.HumanEdible && !t.def.IsDrug });
                int comp = rc.GetCount(ThingDefOf.ComponentIndustrial);
                if (comp < 10) needs.Add(new Need { key = "components", label = "MT_TR_NeedComp".Translate(comp), match = t => t.def == ThingDefOf.ComponentIndustrial || t.def == ThingDefOf.ComponentSpacer });
                int steel = rc.GetCount(ThingDefOf.Steel);
                if (steel < 200) needs.Add(new Need { key = "steel", label = "MT_TR_NeedSteel".Translate(steel), match = t => t.def == ThingDefOf.Steel });
                int unarmed = col.Count(p => !p.WorkTagIsDisabled(WorkTags.Violent) && p.equipment?.Primary == null);
                if (unarmed > 0) needs.Add(new Need { key = "weapons", label = "MT_TR_NeedWeapons".Translate(unarmed), match = t => t.def.IsWeapon });
                // cold: the coldest part of the year against what the colonists can stand
                float coldest = 99f;
                for (int i = 0; i < 12; i++) coldest = Mathf.Min(coldest, GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, (Twelfth)i));
                int cold = col.Count(p => p.GetStatValue(StatDefOf.ComfyTemperatureMin) > coldest + 5f);
                if (coldest < 0f && cold > 0) needs.Add(new Need { key = "warm", label = "MT_TR_NeedWarm".Translate(cold, coldest.ToStringTemperature("F0")), match = t => t.def.IsApparel && t.GetStatValue(StatDefOf.Insulation_Cold) >= 15f });
            }
            catch { }
            return needs;
        }

        public static Need NeedFor(Tradeable tr, Map map)
        {
            Thing t = tr.AnyThing;
            if (t == null || tr.CountHeldBy(Transactor.Trader) <= 0) return null;
            foreach (Need n in Needs(map)) { try { if (n.match(t)) return n; } catch { } }
            return null;
        }

        // ---------------- an upgrade for one of the colonists ----------------
        private static readonly Dictionary<Tradeable, string> better = new Dictionary<Tradeable, string>();
        private static int betterAt = -99999;

        public static string BetterFor(Tradeable tr, Map map)
        {
            if (Find.TickManager.TicksGame - betterAt > 600) { better.Clear(); betterAt = Find.TickManager.TicksGame; }
            if (better.TryGetValue(tr, out string s)) return s;
            s = null;
            try { s = Compute(tr, map); } catch { }
            better[tr] = s;
            return s;
        }

        public static float WeaponScore(Thing w)
        {
            if (w == null) return 0f;
            VerbProperties v = w.def.Verbs?.FirstOrDefault(x => x.isPrimary) ?? w.def.Verbs?.FirstOrDefault();
            if (w.def.IsRangedWeapon && v != null)
            {
                float dmg = v.defaultProjectile?.projectile != null ? v.defaultProjectile.projectile.GetDamageAmount(w) : 0f;
                float cycle = v.warmupTime + w.GetStatValue(StatDefOf.RangedWeapon_Cooldown) + (v.burstShotCount - 1) * v.ticksBetweenBurstShots / 60f;
                float acc = (w.GetStatValue(StatDefOf.AccuracyShort) + w.GetStatValue(StatDefOf.AccuracyMedium)) / 2f;
                return cycle > 0f ? dmg * v.burstShotCount / cycle * Mathf.Max(0.3f, acc) : 0f;
            }
            return w.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
        }

        private static float ApparelScore(Thing a)
        {
            float armor = a.GetStatValue(StatDefOf.ArmorRating_Sharp) + 0.5f * a.GetStatValue(StatDefOf.ArmorRating_Blunt) + 0.2f * a.GetStatValue(StatDefOf.ArmorRating_Heat);
            float warm = (a.GetStatValue(StatDefOf.Insulation_Cold) + 0.5f * a.GetStatValue(StatDefOf.Insulation_Heat)) / 100f;
            float hp = a.def.useHitPoints && a.MaxHitPoints > 0 ? Mathf.Lerp(0.5f, 1f, (float)a.HitPoints / a.MaxHitPoints) : 1f;
            return (armor + warm) * hp;
        }

        private static string Compute(Tradeable tr, Map map)
        {
            Thing t = tr.AnyThing;
            if (t == null || map == null || tr.CountHeldBy(Transactor.Trader) <= 0) return null;
            if (!t.def.IsWeapon && !t.def.IsApparel) return null;
            Pawn best = null;
            float gain = 0f;
            bool fresh = false;
            foreach (Pawn p in map.mapPawns.FreeColonists)
            {
                if (!p.RaceProps.Humanlike) continue;
                if (t.def.IsWeapon)
                {
                    if (p.WorkTagIsDisabled(WorkTags.Violent)) continue;
                    ThingWithComps cur = p.equipment?.Primary;
                    if (cur != null && cur.def.IsRangedWeapon != t.def.IsRangedWeapon) continue;
                    float now = WeaponScore(cur), neu = WeaponScore(t);
                    float g = now <= 0f ? 1f : (neu - now) / now;
                    if (g > 0.1f && g > gain) { gain = g; best = p; fresh = now <= 0f; }
                }
                else if (t is Apparel ap)
                {
                    if (!ApparelUtility.HasPartsToWear(p, ap.def) || !ap.PawnCanWear(p)) continue;
                    var worn = p.apparel?.WornApparel?.Where(w => !ApparelUtility.CanWearTogether(w.def, ap.def, p.RaceProps.body)).ToList() ?? new List<Apparel>();
                    float now = worn.Sum(ApparelScore), neu = ApparelScore(ap);
                    if (neu <= 0f) continue;
                    float g = now <= 0f ? 1f : (neu - now) / now;
                    if (g > 0.1f && g > gain) { gain = g; best = p; fresh = now <= 0f; }
                }
            }
            if (best == null) return null;
            return fresh ? "MT_TR_BetterNew".Translate(best.LabelShortCap) : "MT_TR_Better".Translate(best.LabelShortCap, gain.ToStringPercent("0"));
        }

        // ---------------- memory of prices (ratio to the base value, per thing) ----------------
        private static Dialog_Trade seenFor;
        private static readonly Dictionary<ThingDef, float> buyAvg = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<ThingDef, float> sellAvg = new Dictionary<ThingDef, float>();

        public static void SessionStart(Dialog_Trade dlg, List<Tradeable> pool)
        {
            if (seenFor == dlg) return;
            seenFor = dlg;
            buyAvg.Clear(); sellAvg.Clear();
            better.Clear(); betterAt = -99999; needs = null;
            var mem = ClearTradeMemory.Get;
            if (mem == null) return;
            foreach (Tradeable t in pool)
            {
                ThingDef d = t.ThingDef;
                if (d == null || !t.TraderWillTrade) continue;
                float bv;
                try { bv = t.BaseMarketValue; } catch { continue; }
                if (bv <= 0.01f) continue;
                try
                {
                    if (t.CountHeldBy(Transactor.Trader) > 0)
                    {
                        if (mem.Avg(d, true, out float a)) buyAvg[d] = a;
                        mem.Add(d, true, t.GetPriceFor(TradeAction.PlayerBuys) / bv);
                    }
                    if (t.CountHeldBy(Transactor.Colony) > 0)
                    {
                        if (mem.Avg(d, false, out float a)) sellAvg[d] = a;
                        mem.Add(d, false, t.GetPriceFor(TradeAction.PlayerSells) / bv);
                    }
                }
                catch { }
            }
        }

        // -1 cheaper than usual (good), +1 dearer (bad), 0 normal or unknown
        public static int PriceMark(Tradeable t, TradeAction action)
        {
            ThingDef d = t.ThingDef;
            if (d == null) return 0;
            var dict = action == TradeAction.PlayerBuys ? buyAvg : sellAvg;
            if (!dict.TryGetValue(d, out float avg) || avg <= 0f) return 0;
            float bv;
            try { bv = t.BaseMarketValue; } catch { return 0; }
            if (bv <= 0.01f) return 0;
            float r = t.GetPriceFor(action) / bv / avg;
            if (action == TradeAction.PlayerBuys) return r < 0.85f ? -1 : r > 1.15f ? 1 : 0;
            return r > 1.15f ? -1 : r < 0.85f ? 1 : 0;
        }

        // ---------------- surplus / junk and balancing ----------------
        public static bool Junk(Tradeable tr)
        {
            Thing t = tr.AnyThing;
            if (t == null || t is Pawn || tr.CountHeldBy(Transactor.Colony) <= 0 || !tr.TraderWillTrade) return false;
            if (t is Apparel ap && ap.WornByCorpse) return true;
            if (!t.def.IsWeapon && !t.def.IsApparel) return false;
            if (t.def.useHitPoints && t.MaxHitPoints > 0 && t.HitPoints < t.MaxHitPoints * 0.5f) return true;
            return t.TryGetQuality(out QualityCategory q) && q <= QualityCategory.Poor;
        }

        public static int Surplus(Tradeable tr)
        {
            ThingDef d = tr.ThingDef;
            if (d == null || !tr.TraderWillTrade) return 0;
            if (Junk(tr)) return tr.CountHeldBy(Transactor.Colony);
            if (MTMod.S.tradeKeep == null || !MTMod.S.tradeKeep.TryGetValue(d.defName, out int keep)) return 0;
            int held = TotalHeld(d);
            int extra = held - keep;
            return Mathf.Clamp(extra, 0, tr.CountHeldBy(Transactor.Colony));
        }

        private static int TotalHeld(ThingDef d) =>
            TradeSession.deal.AllTradeables.Where(x => x.ThingDef == d).Sum(x => x.CountHeldBy(Transactor.Colony));

        public static int SellSurplus(List<Tradeable> pool)
        {
            int n = 0;
            foreach (Tradeable t in pool)
            {
                if (t.IsCurrency) continue;
                int s = Surplus(t);
                if (s > Selling(t)) { SetSell(t, s); n++; }
            }
            return n;
        }

        // sells the least needed things until the colony's silver is not below zero
        public static bool Balance(List<Tradeable> pool, Tradeable currency, Map map)
        {
            if (currency == null) return false;
            TradeSession.deal.UpdateCurrencyCount();
            int missing = -currency.CountPostDealFor(Transactor.Colony);
            if (missing <= 0) return true;
            var ndefs = Needs(map);
            bool Needed(Tradeable t) { Thing x = t.AnyThing; return x != null && ndefs.Any(n => { try { return n.match(x); } catch { return false; } }); }
            var cands = pool.Where(t => !t.IsCurrency && t.TraderWillTrade && !(t.AnyThing is Pawn) && t.CountHeldBy(Transactor.Colony) - Selling(t) > 0 && Buying(t) == 0)
                .OrderByDescending(t => Junk(t) ? 2 : Surplus(t) > 0 ? 1 : 0)
                .ThenBy(t => Needed(t) ? 1 : 0)
                .ThenByDescending(t => (t.CountHeldBy(Transactor.Colony) - Selling(t)) * SafePrice(t))
                .ToList();
            foreach (Tradeable t in cands)
            {
                if (missing <= 0) break;
                float unit = SafePrice(t);
                if (unit <= 0.01f) continue;
                int can = t.CountHeldBy(Transactor.Colony) - Selling(t);
                int take = Mathf.Min(can, Mathf.CeilToInt(missing / unit));
                SetSell(t, Selling(t) + take);
                TradeSession.deal.UpdateCurrencyCount();
                missing = -currency.CountPostDealFor(Transactor.Colony);
            }
            return missing <= 0;
        }

        private static float SafePrice(Tradeable t)
        {
            try { return t.GetPriceFor(TradeAction.PlayerSells); } catch { return 0f; }
        }

        // ---------------- wish list ----------------
        public static bool Wished(ThingDef d) => d != null && MTMod.S.tradeWishes != null && MTMod.S.tradeWishes.Contains(d.defName);

        public static void ToggleWish(ThingDef d)
        {
            if (d == null) return;
            MTMod.S.tradeWishes ??= new List<string>();
            if (!MTMod.S.tradeWishes.Remove(d.defName)) MTMod.S.tradeWishes.Add(d.defName);
            MTMod.S.Write();
        }

        // ---------------- saved carts ----------------
        public static string CartToString(List<Tradeable> pool)
        {
            var parts = new List<string>();
            foreach (Tradeable t in pool)
            {
                if (t.ThingDef == null || t.IsCurrency) continue;
                if (Buying(t) > 0) parts.Add(t.ThingDef.defName + ":" + Buying(t));
                else if (Selling(t) > 0) parts.Add(t.ThingDef.defName + ":-" + Selling(t));
            }
            return string.Join(";", parts);
        }

        public static string CartName(List<Tradeable> pool)
        {
            var labels = pool.Where(t => t.CountToTransfer != 0 && !t.IsCurrency && t.ThingDef != null).Select(t => t.ThingDef.label).Distinct().Take(3).ToList();
            return labels.Count == 0 ? "?" : string.Join(", ", labels);
        }

        public static void ApplyCart(string cart, List<Tradeable> pool)
        {
            foreach (string part in cart.Split(';'))
            {
                string[] kv = part.Split(':');
                if (kv.Length != 2 || !int.TryParse(kv[1], out int n)) continue;
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(kv[0]);
                if (d == null) continue;
                int left = Math.Abs(n);
                foreach (Tradeable t in pool.Where(x => x.ThingDef == d && x.TraderWillTrade).OrderByDescending(x => n > 0 ? x.CountHeldBy(Transactor.Trader) : x.CountHeldBy(Transactor.Colony)))
                {
                    if (left <= 0) break;
                    if (n > 0) { int take = Mathf.Min(left, t.CountHeldBy(Transactor.Trader)); SetBuy(t, take); left -= take; }
                    else { int take = Mathf.Min(left, t.CountHeldBy(Transactor.Colony)); SetSell(t, take); left -= take; }
                }
            }
        }
    }

    // per game: what prices were seen, which traders were already announced
    public class ClearTradeMemory : GameComponent
    {
        public Dictionary<string, Vector2> buy = new Dictionary<string, Vector2>();    // x = sum of ratios, y = count
        public Dictionary<string, Vector2> sell = new Dictionary<string, Vector2>();
        public List<string> announced = new List<string>();
        private List<string> k1, k2; private List<Vector2> v1, v2;

        public ClearTradeMemory(Game g) { }
        public static ClearTradeMemory Get => Current.Game?.GetComponent<ClearTradeMemory>();

        public bool Avg(ThingDef d, bool isBuy, out float avg)
        {
            avg = 0f;
            if (!(isBuy ? buy : sell).TryGetValue(d.defName, out Vector2 v) || v.y < 1f) return false;
            avg = v.x / v.y;
            return true;
        }

        public void Add(ThingDef d, bool isBuy, float ratio)
        {
            if (float.IsNaN(ratio) || ratio <= 0f) return;
            var dict = isBuy ? buy : sell;
            dict.TryGetValue(d.defName, out Vector2 v);
            if (v.y >= 20f) { v.x = v.x / v.y * 19f; v.y = 19f; }   // the last ~20 traders count
            dict[d.defName] = new Vector2(v.x + ratio, v.y + 1f);
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref buy, "buy", LookMode.Value, LookMode.Value, ref k1, ref v1);
            Scribe_Collections.Look(ref sell, "sell", LookMode.Value, LookMode.Value, ref k2, ref v2);
            Scribe_Collections.Look(ref announced, "announced", LookMode.Value);
            buy ??= new Dictionary<string, Vector2>();
            sell ??= new Dictionary<string, Vector2>();
            announced ??= new List<string>();
        }

        // a trader with something from the wish list: one letter
        public override void GameComponentTick()
        {
            if (Find.TickManager.TicksGame % 1250 != 77) return;
            var wishes = MTMod.S?.tradeWishes;
            if (wishes == null || wishes.Count == 0 || MTMod.S == null || !MTMod.S.tradeWindow) return;
            try
            {
                foreach (Map map in Find.Maps)
                {
                    if (!map.IsPlayerHome) continue;
                    foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
                    {
                        if (p.trader?.traderKind == null || p.Faction == null || p.Faction.IsPlayer) continue;
                        Check(p.GetUniqueLoadID(), p.trader.Goods, p.LabelShortCap, p, wishes);
                    }
                    foreach (PassingShip s in map.passingShipManager.passingShips)
                        if (s is TradeShip ts)
                            Check(ts.GetUniqueLoadID(), ts.Goods, ts.TraderName, null, wishes);
                }
                if (announced.Count > 200) announced.RemoveRange(0, 100);
            }
            catch { }
        }

        private void Check(string id, IEnumerable<Thing> goods, string who, Thing look, List<string> wishes)
        {
            if (announced.Contains(id)) return;
            var found = goods.Where(g => g?.def != null && wishes.Contains(g.def.defName)).Select(g => g.def.label).Distinct().ToList();
            if (found.Count == 0) return;
            announced.Add(id);
            Find.LetterStack.ReceiveLetter("MT_TR_WishLetter".Translate(), "MT_TR_WishLetterText".Translate(who, string.Join(", ", found)),
                LetterDefOf.PositiveEvent, look != null ? new LookTargets(look) : null);
        }
    }

    // stock to keep: what is above it counts as surplus
    public class Dialog_TradeKeep : Window
    {
        private readonly List<ThingDef> defs;
        private readonly Dictionary<ThingDef, int> held;
        private readonly Dictionary<ThingDef, string> buf = new Dictionary<ThingDef, string>();
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(560f, 620f);

        public Dialog_TradeKeep(IEnumerable<Tradeable> pool)
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            var list = pool.Where(t => !t.IsCurrency && t.ThingDef != null && t.CountHeldBy(Transactor.Colony) > 0 && t.ThingDef.stackLimit > 1).ToList();
            held = list.GroupBy(t => t.ThingDef).ToDictionary(g => g.Key, g => g.Sum(t => t.CountHeldBy(Transactor.Colony)));
            defs = held.Keys.OrderBy(d => d.label).ToList();
        }

        public override void DoWindowContents(Rect inRect)
        {
            MTMod.S.tradeKeep ??= new Dictionary<string, int>();
            Look.Text(new Rect(inRect.x, inRect.y, inRect.width, 28f), "MT_TR_KeepTitle".Translate(), MUI.Text1, GameFont.Medium);
            Look.Text(new Rect(inRect.x, inRect.y + 30f, inRect.width, 36f), "MT_TR_KeepDesc".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            Rect outR = new Rect(inRect.x, inRect.y + 70f, inRect.width, inRect.height - 70f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, defs.Count * 30f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < defs.Count; i++)
            {
                ThingDef d = defs[i];
                Rect row = new Rect(0f, i * 30f, view.width, 28f);
                if (i % 2 == 0) MUI.Fill(row, Look.Card);
                Widgets.DefIcon(new Rect(row.x + 4f, row.y + 2f, 24f, 24f), d);
                Look.Text(new Rect(row.x + 34f, row.y, row.width - 260f, row.height), d.LabelCap.ToString().Truncate(row.width - 260f), MUI.Text1);
                Look.Text(new Rect(row.xMax - 220f, row.y, 90f, row.height), "MT_TR_KeepHave".Translate(held[d]), MUI.Muted, GameFont.Tiny);
                bool has = MTMod.S.tradeKeep.TryGetValue(d.defName, out int keep);
                if (!buf.ContainsKey(d)) buf[d] = has ? keep.ToString() : "";
                string s = Widgets.TextField(new Rect(row.xMax - 120f, row.y + 2f, 80f, 24f), buf[d]);
                if (s != buf[d])
                {
                    buf[d] = s;
                    if (int.TryParse(s, out int v) && v >= 0) MTMod.S.tradeKeep[d.defName] = v;
                    else if (s.Trim().Length == 0) MTMod.S.tradeKeep.Remove(d.defName);
                }
            }
            Widgets.EndScrollView();
        }

        public override void PostClose()
        {
            base.PostClose();
            MTMod.S.Write();
        }
    }
}
