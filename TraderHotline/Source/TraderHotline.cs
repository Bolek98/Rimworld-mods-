using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace TraderHotline
{
    // ---------------------------------------------------------------------------
    // Trader Hotline 0.1.0. Call the orbital trader you want from the comms console – and it
    // really comes: you see what it sells and what it costs, pay from any silver in your stores
    // (no orbital beacons needed), see the countdown in the alerts, choose normal or express
    // delivery, and if it can't come (storyteller limits, another mod) it tries again for two
    // days and otherwise refunds the fee. It can also stay longer than a normal trader.
    // ---------------------------------------------------------------------------
    public class THSettings : ModSettings
    {
        public bool recommended = true;
        public float baseFee = 300f;          // normal call
        public float wealthFee = 0.25f;       // + % of colony wealth (0 = none)
        public float expressMul = 2.5f;       // express = fee × this
        public float delayDays = 1.5f;        // normal: arrives after this many days (± a bit)
        public float expressHours = 4f;       // express: after these hours
        public float cooldownDays = 5f;       // between two calls (0 = none)
        public float stayMul = 1.5f;          // a called trader stays this many times longer
        public bool refund = true;            // fee back when it could not come
        public float caravanMul = 0.6f;       // a caravan costs this part of the fee

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref baseFee, "baseFee", 300f);
            Scribe_Values.Look(ref wealthFee, "wealthFee", 0.25f);
            Scribe_Values.Look(ref expressMul, "expressMul", 2.5f);
            Scribe_Values.Look(ref delayDays, "delayDays", 1.5f);
            Scribe_Values.Look(ref expressHours, "expressHours", 4f);
            Scribe_Values.Look(ref cooldownDays, "cooldownDays", 5f);
            Scribe_Values.Look(ref stayMul, "stayMul", 1.5f);
            Scribe_Values.Look(ref refund, "refund", true);
            Scribe_Values.Look(ref caravanMul, "caravanMul", 0.6f);
        }
    }

    public class THMod : Mod
    {
        public const string Version = "0.2.0";
        public static THSettings S;
        private static List<string> recBullets;

        public THMod(ModContentPack content) : base(content)
        {
            S = GetSettings<THSettings>();
            MUI.Init(new THSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("misak.traderhotline");
            try { MUI.PatchWindow(h, typeof(THMod)); } catch { }
            try
            {
                h.Patch(AccessTools.Method(typeof(Building_CommsConsole), nameof(Building_CommsConsole.GetFloatMenuOptions)),
                    postfix: new HarmonyMethod(typeof(Hotline), nameof(Hotline.ConsoleOptions)));
            }
            catch (Exception e) { Log.Warning("[Trader Hotline] comms console: " + e.Message); }
            Log.Message("[Trader Hotline] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "TH_ModName".Translate();

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "TH_Rec1".Translate(), "TH_Rec2".Translate(), "TH_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                S.baseFee = Mathf.Round(MUI.Slider(l, "TH_BaseFee".Translate(Mathf.RoundToInt(S.baseFee)), S.baseFee, 0f, 2000f, "TH_BaseFee_Desc".Translate(), S, "baseFee") / 10f) * 10f;
                S.wealthFee = Mathf.Round(MUI.Slider(l, "TH_WealthFee".Translate(S.wealthFee.ToString("0.00")), S.wealthFee, 0f, 2f, "TH_WealthFee_Desc".Translate(), S, "wealthFee") * 20f) / 20f;
                S.delayDays = Mathf.Round(MUI.Slider(l, "TH_Delay".Translate(S.delayDays.ToString("0.0")), S.delayDays, 0.25f, 5f, "TH_Delay_Desc".Translate(), S, "delayDays") * 4f) / 4f;
                S.expressHours = Mathf.Round(MUI.Slider(l, "TH_Express".Translate(Mathf.RoundToInt(S.expressHours)), S.expressHours, 1f, 24f, "TH_Express_Desc".Translate(), S, "expressHours"));
                S.expressMul = Mathf.Round(MUI.Slider(l, "TH_ExpressMul".Translate(S.expressMul.ToString("0.0")), S.expressMul, 1f, 5f, "TH_ExpressMul_Desc".Translate(), S, "expressMul") * 10f) / 10f;
                S.cooldownDays = Mathf.Round(MUI.Slider(l, "TH_Cooldown".Translate(S.cooldownDays.ToString("0")), S.cooldownDays, 0f, 30f, "TH_Cooldown_Desc".Translate(), S, "cooldownDays"));
                S.stayMul = Mathf.Round(MUI.Slider(l, "TH_Stay".Translate(S.stayMul.ToString("0.0")), S.stayMul, 1f, 4f, "TH_Stay_Desc".Translate(), S, "stayMul") * 10f) / 10f;
                S.caravanMul = Mathf.Round(MUI.Slider(l, "TH_CaravanMul".Translate(Mathf.RoundToInt(S.caravanMul * 100f)), S.caravanMul, 0.1f, 2f, "TH_CaravanMul_Desc".Translate(), S, "caravanMul") * 20f) / 20f;
                MUI.Check(l, "TH_Refund".Translate(), "TH_Refund_Desc".Translate(), ref S.refund, S, "refund");
                l.End();
            }
            finally { MUI.EndLock(rest); }
        }
    }

    public class PendingCall : IExposable
    {
        public int mapId;
        public TraderKindDef kind;
        public Faction faction;     // caravans: who sends it
        public bool caravan;
        public int fireTick, deadline, fee;
        public bool announced;

        public void ExposeData()
        {
            Scribe_Values.Look(ref mapId, "mapId");
            Scribe_Defs.Look(ref kind, "kind");
            Scribe_Values.Look(ref fireTick, "fireTick");
            Scribe_Values.Look(ref deadline, "deadline");
            Scribe_Values.Look(ref fee, "fee");
            Scribe_Values.Look(ref announced, "announced");
            Scribe_References.Look(ref faction, "faction");
            Scribe_Values.Look(ref caravan, "caravan");
        }
    }

    // one thing you can call: an orbital trade ship or a caravan of one faction
    public class Offer
    {
        public TraderKindDef kind;
        public Faction faction;
        public bool caravan;
        public string Label => caravan ? kind.LabelCap + " – " + faction.Name : kind.LabelCap.ToString();
    }

    public class HotlineComponent : GameComponent
    {
        public List<PendingCall> calls = new List<PendingCall>();
        public int nextCallTick;

        public HotlineComponent(Game g) { }

        public static HotlineComponent Get => Current.Game?.GetComponent<HotlineComponent>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref calls, "calls", LookMode.Deep);
            Scribe_Values.Look(ref nextCallTick, "nextCallTick");
            if (calls == null) calls = new List<PendingCall>();
            calls.RemoveAll(c => c?.kind == null);
        }

        // a called ship that vanishes long before its time was taken by another mod – say so in the log
        private Map watchMap; private PassingShip watchShip; private int watchUntil;

        public void Watch(Map map, PassingShip ship) { watchMap = map; watchShip = ship; watchUntil = Find.TickManager.TicksGame + 5000; }

        public override void GameComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (watchShip != null && now % 250 == 77)
            {
                if (watchMap?.passingShipManager == null || now > watchUntil) watchShip = null;
                else if (!watchMap.passingShipManager.passingShips.Contains(watchShip))
                {
                    Log.Warning("[Trader Hotline] the called ship " + watchShip.name + " left orbit after less than 2 hours – another mod removed it (" + (watchShip.ticksUntilDeparture) + " ticks were left).");
                    watchShip = null;
                }
            }
            if (now % 250 != 31 || calls.Count == 0) return;
            foreach (PendingCall c in calls.ToList())
            {
                if (c.caravan && c.faction == null) { calls.Remove(c); continue; }
                Map map = Find.Maps.FirstOrDefault(m => m.uniqueID == c.mapId);
                if (map == null) { calls.Remove(c); continue; }
                if (now < c.fireTick) continue;
                if (c.caravan ? Hotline.ArriveCaravan(map, c.kind, c.faction) : Hotline.Arrive(map, c.kind))
                {
                    calls.Remove(c);
                    continue;
                }
                if (now >= c.deadline)
                {
                    calls.Remove(c);
                    if (THMod.S.refund && c.fee > 0) Hotline.Refund(map, c.fee);
                    Find.LetterStack.ReceiveLetter("TH_FailTitle".Translate(), "TH_FailText".Translate(c.kind.label, c.fee), LetterDefOf.NegativeEvent);
                }
                else c.fireTick = now + 2500;   // try again in an hour
            }
        }
    }

    public static class Hotline
    {
        public static IEnumerable<TraderKindDef> Orbital =>
            DefDatabase<TraderKindDef>.AllDefsListForReading.Where(k => k.orbital && (k.faction == null || Find.FactionManager.FirstFactionOfDef(k.faction) != null))
                .OrderBy(k => k.label);

        // caravans of every faction that is not hostile and would send that kind of trader
        public static IEnumerable<Offer> Caravans(Map map)
        {
            foreach (Faction f in Find.FactionManager.AllFactionsVisibleInViewOrder)
            {
                if (f.IsPlayer || f.defeated || f.temporary || f.def.caravanTraderKinds.NullOrEmpty() || f.HostileTo(Faction.OfPlayer)) continue;
                foreach (TraderKindDef k in f.def.caravanTraderKinds)
                {
                    if (k.faction != null && k.faction != f.def) continue;
                    if (ModsConfig.IdeologyActive && f.ideos != null && k.category == "Slaver" && f.ideos.AllIdeos.Any(i => !i.IdeoApprovesOfSlavery())) continue;
                    if (k.permitRequiredForTrading != null && !map.mapPawns.FreeColonists.Any(p => p.royalty != null && p.royalty.HasPermit(k.permitRequiredForTrading, f))) continue;
                    yield return new Offer { kind = k, faction = f, caravan = true };
                }
            }
        }

        public static IEnumerable<Offer> Ships() =>
            Orbital.Select(k => new Offer { kind = k, faction = k.faction != null ? Find.FactionManager.FirstFactionOfDef(k.faction) : null });

        public static bool ArriveCaravan(Map map, TraderKindDef kind, Faction fac)
        {
            try
            {
                if (fac == null || fac.defeated || fac.HostileTo(Faction.OfPlayer)) return false;
                var parms = new IncidentParms { target = map, faction = fac, traderKind = kind, forced = true };
                bool ok = IncidentDefOf.TraderCaravanArrival.Worker.TryExecute(parms);
                Log.Message("[Trader Hotline] caravan " + kind.defName + " of " + fac.Name + (ok ? " arrived." : " could not come yet."));
                return ok;
            }
            catch (Exception e)
            {
                Log.Warning("[Trader Hotline] caravan " + kind?.defName + ": " + e);
                return false;
            }
        }

        public static int Fee(Map map, bool express, bool caravan = false)
        {
            float w = map != null ? map.wealthWatcher.WealthTotal : 0f;
            float f = THMod.S.baseFee + w * THMod.S.wealthFee / 100f;
            if (caravan) f *= THMod.S.caravanMul;
            if (express) f *= THMod.S.expressMul;
            return Mathf.RoundToInt(f / 10f) * 10;
        }

        public static int SilverOn(Map map) =>
            map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => !t.IsForbidden(Faction.OfPlayer) && (t.IsInAnyStorage() || map.areaManager.Home[t.Position])).Sum(t => t.stackCount);

        public static bool Pay(Map map, int fee)
        {
            if (fee <= 0) return true;
            var piles = map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => !t.IsForbidden(Faction.OfPlayer) && (t.IsInAnyStorage() || map.areaManager.Home[t.Position])).ToList();
            if (piles.Sum(t => t.stackCount) < fee) return false;
            int left = fee;
            foreach (Thing t in piles)
            {
                int take = Mathf.Min(left, t.stackCount);
                t.SplitOff(take).Destroy();
                left -= take;
                if (left <= 0) break;
            }
            return true;
        }

        public static void Refund(Map map, int fee)
        {
            IntVec3 c = DropCellFinder.TradeDropSpot(map);
            DropPodUtility.DropThingsNear(c, map, new List<Thing> { MakeSilver(fee) }, 110, false, false, false, false);
        }

        private static Thing MakeSilver(int n) { Thing s = ThingMaker.MakeThing(ThingDefOf.Silver); s.stackCount = n; return s; }

        public static string Blocked(Map map)
        {
            HotlineComponent hc = HotlineComponent.Get;
            if (hc == null) return null;
            if (hc.calls.Any(c => c.mapId == map.uniqueID)) return "TH_AlreadyCalled".Translate();
            int now = Find.TickManager.TicksGame;
            if (hc.nextCallTick > now) return "TH_Cooldown_Wait".Translate(((hc.nextCallTick - now) / 60000f).ToString("0.0"));
            return null;
        }

        public static void Call(Map map, Offer o, bool express)
        {
            TraderKindDef kind = o.kind;
            HotlineComponent hc = HotlineComponent.Get;
            int fee = Fee(map, express, o.caravan);
            if (!Pay(map, fee)) { Messages.Message("TH_NoSilver".Translate(fee), MessageTypeDefOf.RejectInput, false); return; }
            int now = Find.TickManager.TicksGame;
            int delay = express ? Mathf.RoundToInt(THMod.S.expressHours * 2500f) : Mathf.RoundToInt(THMod.S.delayDays * 60000f * Rand.Range(0.8f, 1.2f));
            hc.calls.Add(new PendingCall { mapId = map.uniqueID, kind = kind, faction = o.caravan ? o.faction : null, caravan = o.caravan, fee = fee, fireTick = now + delay, deadline = now + delay + 120000 });
            hc.nextCallTick = now + Mathf.RoundToInt(THMod.S.cooldownDays * 60000f);
            Messages.Message("TH_Called".Translate(o.Label, delay.ToStringTicksToPeriod(), fee), MessageTypeDefOf.PositiveEvent, false);
        }

        // the trade ship is made here directly (like the vanilla incident does), so mods that
        // take over or block the orbital trader incident (Trader Ships, vehicle mods …) cannot
        // swallow a paid call. Only if that fails, the vanilla incident is tried.
        public static bool Arrive(Map map, TraderKindDef kind)
        {
            try
            {
                if (map.passingShipManager.passingShips.Count >= 5)
                {
                    // room for the called one: the ship that leaves soonest goes now
                    PassingShip first = map.passingShipManager.passingShips.OrderBy(p => p.ticksUntilDeparture).First();
                    map.passingShipManager.RemoveShip(first);
                }
                Faction fac = null;
                if (kind.faction != null)
                    Find.FactionManager.AllFactions.Where(f => f.def == kind.faction).TryRandomElement(out fac);
                TradeShip ship = new TradeShip(kind, fac);
                bool comms = map.listerBuildings.allBuildingsColonist.Any(b => b.def.IsCommsConsole && (b.GetComp<CompPowerTrader>() == null || b.GetComp<CompPowerTrader>().PowerOn));
                map.passingShipManager.AddShip(ship);
                ship.GenerateThings();
                if (THMod.S.stayMul > 1.01f) ship.ticksUntilDeparture = Mathf.RoundToInt(ship.ticksUntilDeparture * THMod.S.stayMul);
                ship.WasAnnounced = true;
                Find.LetterStack.ReceiveLetter(ship.def.LabelCap,
                    "TH_Arrived".Translate(ship.name, ship.def.label, ship.ticksUntilDeparture.ToStringTicksToPeriod()).ToString() + (comms ? "" : "\n\n" + "TH_NoComms".Translate().ToString()),
                    LetterDefOf.PositiveEvent);
                Log.Message("[Trader Hotline] " + kind.defName + " arrived (" + ship.name + "), leaves in " + ship.ticksUntilDeparture + " ticks; ships in orbit: " + map.passingShipManager.passingShips.Count);
                HotlineComponent.Get?.Watch(map, ship);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Trader Hotline] arrival of " + kind?.defName + ": " + e);
                try
                {
                    var parms = new IncidentParms { target = map, traderKind = kind, forced = true };
                    return IncidentDefOf.OrbitalTraderArrival.Worker.TryExecute(parms);
                }
                catch { return false; }
            }
        }

        public static void ConsoleOptions(Building_CommsConsole __instance, Pawn myPawn, ref IEnumerable<FloatMenuOption> __result)
        {
            var list = __result?.ToList() ?? new List<FloatMenuOption>();
            try
            {
                Map map = __instance.Map;
                if (map == null || myPawn == null) { __result = list; return; }
                string why = !__instance.CanUseCommsNow ? "TH_NoPower".Translate().ToString() : Blocked(map);
                if (why == null && !myPawn.CanReach(__instance, PathEndMode.InteractionCell, Danger.Some)) why = "NoPath".Translate().CapitalizeFirst();
                if (why != null)
                    list.Add(new FloatMenuOption("TH_CallMenu".Translate() + " (" + why + ")", null));
                else
                    list.Add(new FloatMenuOption("TH_CallMenu".Translate(), () => Find.WindowStack.Add(new Dialog_Hotline(map))));
            }
            catch (Exception e) { Log.WarningOnce("[Trader Hotline] menu: " + e.Message, 0x7A11E); }
            __result = list;
        }

        // what a trader kind sells: a few category / item names
        private static readonly Dictionary<TraderKindDef, string> stockCache = new Dictionary<TraderKindDef, string>();

        public static string Stock(TraderKindDef k)
        {
            if (stockCache.TryGetValue(k, out string s)) return s;
            var names = new List<string>();
            try
            {
                var cats = new Dictionary<string, int>();
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                {
                    if (d.category != ThingCategory.Item && !d.Minifiable && d.race == null) continue;
                    if (d.tradeability == Tradeability.None || d.BaseMarketValue <= 0f) continue;
                    bool sells = false;
                    foreach (StockGenerator g in k.stockGenerators)
                        if (g.HandlesThingDef(d)) { sells = true; break; }
                    if (!sells) continue;
                    string c = d.race != null ? "TH_Animals".Translate().ToString() : (d.FirstThingCategory?.LabelCap.ToString() ?? d.LabelCap.ToString());
                    cats.TryGetValue(c, out int n);
                    cats[c] = n + 1;
                }
                names = cats.OrderByDescending(kv => kv.Value).Take(10).Select(kv => kv.Key).ToList();
            }
            catch { }
            s = names.Count == 0 ? "?" : string.Join(", ", names);
            stockCache[k] = s;
            return s;
        }
    }

    public class Dialog_Hotline : Window
    {
        private readonly Map map;
        private Vector2 scroll;
        private Offer sel;
        private bool express;
        private readonly List<Offer> ships, caravans;

        public override Vector2 InitialSize => new Vector2(860f, 620f);

        public Dialog_Hotline(Map map)
        {
            this.map = map;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            ships = Hotline.Ships().ToList();
            caravans = Hotline.Caravans(map).OrderBy(o => o.faction.Name).ThenBy(o => o.kind.label).ToList();
            sel = ships.FirstOrDefault() ?? caravans.FirstOrDefault();
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 34f), "TH_Title".Translate());
            Text.Font = GameFont.Small;
            int silver = Hotline.SilverOn(map);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 24f), "TH_Silver".Translate(silver));
            GUI.color = Color.white;
            // list: headers + offers
            var rows = new List<object> { "TH_SecOrbital".Translate().ToString() };
            rows.AddRange(ships.Cast<object>());
            rows.Add("TH_SecCaravan".Translate().ToString());
            if (caravans.Count == 0) rows.Add(0);
            rows.AddRange(caravans.Cast<object>());
            Rect left = new Rect(r.x, r.y + 64f, 330f, r.height - 64f);
            Rect right = new Rect(left.xMax + 14f, left.y, r.width - left.width - 14f, left.height);
            Rect view = new Rect(0f, 0f, left.width - 16f, rows.Count * 32f);
            Widgets.BeginScrollView(left, ref scroll, view);
            for (int i = 0; i < rows.Count; i++)
            {
                Rect row = new Rect(0f, i * 32f, view.width, 30f);
                if (rows[i] is string h)
                {
                    GUI.color = MUI.Accent;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(row.x + 4f, row.y + 10f, row.width, 20f), h.ToUpper());
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    continue;
                }
                if (rows[i] is int)
                {
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(row.x + 8f, row.y + 5f, row.width, 24f), "TH_NoCaravans".Translate());
                    GUI.color = Color.white;
                    continue;
                }
                Offer o = (Offer)rows[i];
                if (o == sel) Widgets.DrawHighlightSelected(row);
                else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.Label(new Rect(row.x + 8f, row.y + 5f, row.width - 12f, 24f), o.Label.Truncate(row.width - 12f));
                if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, o.Label);
                if (Widgets.ButtonInvisible(row)) sel = o;
            }
            Widgets.EndScrollView();
            if (sel == null) return;
            float y = right.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(right.x, y, right.width, 32f), sel.kind.LabelCap);
            Text.Font = GameFont.Small;
            y += 36f;
            Faction f = sel.faction;
            if (f != null) { Widgets.Label(new Rect(right.x, y, right.width, 24f), "TH_Faction".Translate(f.Name)); y += 26f; }
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(right.x, y, right.width, 24f), (sel.caravan ? "TH_KindCaravan" : "TH_KindShip").Translate());
            GUI.color = Color.white;
            y += 28f;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(right.x, y, right.width, 18f), "TH_Sells".Translate());
            GUI.color = Color.white;
            y += 20f;
            string stock = Hotline.Stock(sel.kind);
            float sh = Text.CalcHeight(stock, right.width);
            Widgets.Label(new Rect(right.x, y, right.width, sh), stock);
            y += sh + 16f;
            // delivery
            Widgets.Label(new Rect(right.x, y, right.width, 24f), "TH_Delivery".Translate());
            y += 26f;
            int feeN = Hotline.Fee(map, false, sel.caravan), feeE = Hotline.Fee(map, true, sel.caravan);
            if (Widgets.RadioButtonLabeled(new Rect(right.x, y, right.width, 26f), "TH_Normal".Translate(THMod.S.delayDays.ToString("0.0"), feeN), !express)) express = false;
            y += 30f;
            if (Widgets.RadioButtonLabeled(new Rect(right.x, y, right.width, 26f), "TH_ExpressOpt".Translate(Mathf.RoundToInt(THMod.S.expressHours), feeE), express)) express = true;
            y += 40f;
            GUI.color = MUI.Muted;
            string note = sel.caravan ? "TH_NoteCaravan".Translate().ToString() : "TH_Note".Translate(Mathf.RoundToInt(THMod.S.stayMul * 100f)).ToString();
            float nh = Text.CalcHeight(note, right.width);
            Widgets.Label(new Rect(right.x, y, right.width, nh), note);
            GUI.color = Color.white;
            int fee = express ? feeE : feeN;
            string why = Hotline.Blocked(map);
            if (why == null && silver < fee) why = "TH_NoSilver".Translate(fee);
            Rect b = new Rect(right.xMax - 260f, right.yMax - 40f, 260f, 38f);
            if (why != null)
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(right.x, b.y - 28f, right.width, 24f), why);
                GUI.color = Color.white;
            }
            if (Widgets.ButtonText(b, "TH_CallBtn".Translate(fee), true, true, why == null))
            {
                Hotline.Call(map, sel, express);
                Close();
            }
        }
    }

    // countdown in the alerts
    public class Alert_TraderComing : Alert
    {
        public Alert_TraderComing() { defaultPriority = AlertPriority.Medium; }

        public override string GetLabel()
        {
            PendingCall c = First();
            if (c == null) return "";
            int t = Mathf.Max(0, c.fireTick - Find.TickManager.TicksGame);
            return "TH_AlertLabel".Translate(c.kind.LabelCap, t.ToStringTicksToPeriod(true, true));
        }

        public override TaggedString GetExplanation() => "TH_AlertText".Translate();

        private PendingCall First()
        {
            var hc = HotlineComponent.Get;
            Map m = Find.CurrentMap;
            if (hc == null || m == null) return null;
            return hc.calls.Where(c => c.mapId == m.uniqueID).OrderBy(c => c.fireTick).FirstOrDefault();
        }

        public override AlertReport GetReport() => First() != null;
    }
}
