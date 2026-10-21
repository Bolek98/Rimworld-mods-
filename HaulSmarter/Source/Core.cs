using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using RimWorld.Planet;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------
    public sealed class HSSettings : ModSettings
    {
        // profile: 0 like the game, 1 smart, 2 maximum, 3 custom
        public int profile = 1;

        // hauling (profile)
        public bool haulMany = true;
        public int maxItems = 30;
        public int scanRadius = 14;
        public bool pocketHarvest = true;
        public bool pocketMine = true;
        public float unloadAt = 0.8f;
        public bool smartRoute = true;          // 3.0: shortest route through the items to storage
        public bool opportunistic = true;       // 3.0: take things lying right next to you on the way and after other work (gently)
        public bool buildFromPockets = true;    // 3.0: building uses material from the pockets
        public int prioPerish = 2;              // 3.0: 0 normal, 1 sooner, 2 first
        public int prioWeather = 2;
        public int prioValuable = 1;

        // personal (not in the profiles)
        public float bonusKg = 0f;
        public bool slowdown;                   // 3.0: heavy load slows walking (off by default)
        public bool showWeight = true;          // 3.0: bar above the colonist
        public bool showEta = true;             // 3.0: weight + time in the inspect pane
        public bool overviewButton = true;      // 3.0: "Hauling" button at the bottom
        public bool highlight = true;           // 3.0: mark waiting items while the overview is open

        public static readonly string[] ProfileFields =
        {
            "haulMany", "maxItems", "scanRadius", "pocketHarvest", "pocketMine", "unloadAt", "smartRoute", "opportunistic", "prioPerish", "prioWeather", "prioValuable", "buildFromPockets"
        };

        public static HSSettings PresetValues(int which)
        {
            var p = new HSSettings();
            switch (which)
            {
                case 0:
                    p.smartRoute = false; p.opportunistic = false; p.buildFromPockets = false; p.prioPerish = 0; p.prioWeather = 0; p.prioValuable = 0;
                    p.pocketHarvest = false; p.pocketMine = false;
                    break;
                case 2:
                    p.maxItems = 60; p.scanRadius = 24; p.unloadAt = 0.95f; p.prioValuable = 2;
                    break;
            }
            return p;
        }

        public void Preset(int which)
        {
            HSSettings p = PresetValues(which);
            foreach (string f in ProfileFields)
            {
                FieldInfo fi = typeof(HSSettings).GetField(f);
                fi.SetValue(this, fi.GetValue(p));
            }
        }

        public bool Matches(int which)
        {
            HSSettings p = PresetValues(which);
            foreach (string f in ProfileFields)
            {
                FieldInfo fi = typeof(HSSettings).GetField(f);
                object a = fi.GetValue(this), b = fi.GetValue(p);
                if (a is float fa && b is float fb ? !Mathf.Approximately(fa, fb) : !Equals(a, b))
                    return false;
            }
            return true;
        }

        public bool AnyPriority => prioPerish > 0 || prioWeather > 0 || prioValuable > 0;

        public override void ExposeData()
        {
            // settings from 2.x have no profile – they open as Custom with their own values
            Scribe_Values.Look(ref profile, "profile", 3);
            Scribe_Values.Look(ref haulMany, "haulMany", true);
            Scribe_Values.Look(ref maxItems, "maxItems", 30);
            Scribe_Values.Look(ref scanRadius, "scanRadius2", 14);
            Scribe_Values.Look(ref bonusKg, "bonusKg2", 0f);
            Scribe_Values.Look(ref pocketHarvest, "pocketHarvest", true);
            Scribe_Values.Look(ref pocketMine, "pocketMine", true);
            Scribe_Values.Look(ref unloadAt, "unloadAt", 0.8f);
            Scribe_Values.Look(ref smartRoute, "smartRoute", true);
            Scribe_Values.Look(ref opportunistic, "opportunistic2", true);
            Scribe_Values.Look(ref buildFromPockets, "buildFromPockets", true);
            Scribe_Values.Look(ref prioPerish, "prioPerish", 2);
            Scribe_Values.Look(ref prioWeather, "prioWeather", 2);
            Scribe_Values.Look(ref prioValuable, "prioValuable", 1);
            Scribe_Values.Look(ref slowdown, "slowdown", false);
            Scribe_Values.Look(ref showWeight, "showWeight", true);
            Scribe_Values.Look(ref showEta, "showEta", true);
            Scribe_Values.Look(ref overviewButton, "overviewButton", true);
            Scribe_Values.Look(ref highlight, "highlight", true);
            if (Scribe.mode == LoadSaveMode.LoadingVars && profile >= 0 && profile <= 2)
                Preset(profile);
        }
    }

    public sealed class HSMod : Mod
    {
        public const string Version = "3.0.2";
        public static HSSettings S;
        private Vector2 scroll;
        private float height = 900f;
        private static readonly string[] ProfKeys = { "HS_ProfGame", "HS_ProfSmart", "HS_ProfMax", "HS_ProfCustom" };

        public HSMod(ModContentPack content) : base(content)
        {
            S = GetSettings<HSSettings>();
            Compat.Detect();
            var h = new Harmony("misak.haulsmarter");
            MUI.Init(HSSettings.PresetValues(1), HSSettings.ProfileFields);
            try
            {
                MUI.PatchWindow(h, typeof(HSMod));
            }
            catch
            {
            }
            try
            {
                h.PatchAll();
            }
            catch (Exception e)
            {
                Log.Error("[Haul Smarter] patching failed: " + e);
            }
            Log.Message("[Haul Smarter] v" + Version + " loaded." + Compat.Summary());
        }

        public override string SettingsCategory() => "Haul Smarter";

        private static void Muted(Rect r, string text)
        {
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(r, text);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }

        private static void Check(Listing_Standard l, string key, ref bool v, string field)
        {
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);
        }

        private static string PrioName(int v) => ("HS_Prio" + Mathf.Clamp(v, 0, 2)).Translate();

        private static void Prio(Listing_Standard l, string key, int value, Action<int> set, string field)
        {
            Rect row = l.GetRect(30f);
            Widgets.DrawHighlightIfMouseover(row);
            TooltipHandler.TipRegion(row, (key + "_Desc").Translate());
            bool changed = MUI.Changed(S, field);
            if (changed)
                GUI.color = MUI.Warn;
            Widgets.Label(new Rect(row.x, row.y + 4f, row.width - 130f, 24f), key.Translate());
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(row.xMax - 120f, row.y + 2f, 120f, 26f), PrioName(value)))
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption(PrioName(2), () => set(2)),
                    new FloatMenuOption(PrioName(1), () => set(1)),
                    new FloatMenuOption(PrioName(0), () => set(0))
                }));
            l.Gap(2f);
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            HSSettings s = S;
            MUI.Version(inRect, "v" + Version + " · Bolek98");
            Muted(new Rect(inRect.x, inRect.y, inRect.width, 20f), "HS_Profile".Translate());
            float cw = (inRect.width - 36f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 78f);
                if (MUI.ProfileCard(c, ProfKeys[i].Translate(), (ProfKeys[i] + "_Card").Translate(), s.profile == i, i == 1))
                {
                    if (i < 3) s.Preset(i);
                    s.profile = i;
                }
            }
            float top = 104f;
            if (Compat.OtherHauler)
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(inRect.x, inRect.y + top, inRect.width, 24f), "HS_OtherHauler".Translate(Compat.OtherHaulerName));
                GUI.color = Color.white;
                top += 26f;
            }
            Muted(new Rect(inRect.x, inRect.y + top, inRect.width, 20f), "HS_ProfileHint".Translate());
            top += 24f;

            Rect outRect = new Rect(inRect.x, inRect.y + top, inRect.width, inRect.height - top);
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(height, outRect.height));
            Widgets.BeginScrollView(outRect, ref scroll, view);
            float colW = (view.width - 28f) / 2f;

            var l = new Listing_Standard();
            l.Begin(new Rect(0f, 0f, colW, 100000f));
            MUI.Section(l, "HS_SecHaul".Translate());
            Check(l, "HS_HaulMany", ref s.haulMany, "haulMany");
            s.maxItems = Mathf.RoundToInt(MUI.Slider(l, "HS_MaxItems".Translate(s.maxItems), s.maxItems, 2f, 100f, "HS_MaxItems_Desc".Translate(), s, "maxItems"));
            s.scanRadius = Mathf.RoundToInt(MUI.Slider(l, "HS_Radius".Translate(s.scanRadius), s.scanRadius, 3f, 40f, "HS_Radius_Desc".Translate(), s, "scanRadius"));
            Check(l, "HS_SmartRoute", ref s.smartRoute, "smartRoute");
            Check(l, "HS_Opportunistic", ref s.opportunistic, "opportunistic");
            Check(l, "HS_BuildPockets", ref s.buildFromPockets, "buildFromPockets");
            s.unloadAt = Mathf.Round(MUI.Slider(l, "HS_UnloadAt".Translate(s.unloadAt.ToStringPercent()), s.unloadAt, 0.2f, 1f, "HS_UnloadAt_Desc".Translate(), s, "unloadAt") * 20f) / 20f;
            MUI.Section(l, "HS_SecPrio".Translate());
            Prio(l, "HS_PrioPerish", s.prioPerish, v => s.prioPerish = v, "prioPerish");
            Prio(l, "HS_PrioWeather", s.prioWeather, v => s.prioWeather = v, "prioWeather");
            Prio(l, "HS_PrioValuable", s.prioValuable, v => s.prioValuable = v, "prioValuable");
            MUI.Section(l, "HS_SecPockets".Translate());
            Check(l, "HS_PocketHarvest", ref s.pocketHarvest, "pocketHarvest");
            Check(l, "HS_PocketMine", ref s.pocketMine, "pocketMine");
            if (s.profile >= 0 && s.profile <= 2 && !s.Matches(s.profile))
                s.profile = 3;
            float leftH = l.CurHeight;
            l.End();

            var r = new Listing_Standard();
            r.Begin(new Rect(colW + 28f, 0f, colW, 100000f));
            MUI.Section(r, "HS_SecWeight".Translate());
            s.bonusKg = Mathf.Round(MUI.Slider(r, "HS_BonusKg".Translate(s.bonusKg.ToString("0")), s.bonusKg, 0f, 100f, "HS_BonusKg_Desc".Translate(), s, null));
            Check(r, "HS_Slowdown", ref s.slowdown, null);
            Check(r, "HS_ShowWeight", ref s.showWeight, null);
            MUI.Section(r, "HS_SecInfo".Translate());
            Check(r, "HS_ShowEta", ref s.showEta, null);
            bool before = s.overviewButton;
            Check(r, "HS_Overview", ref s.overviewButton, null);
            if (before != s.overviewButton)
                Overview.SetButton();
            Check(r, "HS_Highlight", ref s.highlight, null);
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            r.Label("HS_InfoNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            float rightH = r.CurHeight;
            r.End();

            height = Mathf.Max(leftH, rightH) + 20f;
            Widgets.EndScrollView();
        }
    }

    // ------------------------------------------------------------------
    // Other mods that do the same job. Our copy of a feature switches
    // itself off while the original mod is active, so nothing is done twice.
    // ------------------------------------------------------------------
    public static class Compat
    {
        public static bool OtherHauler, OtherHarvestHaul;
        public static string OtherHaulerName = "";

        private static bool Active(string id) => ModLister.GetActiveModWithIdentifier(id, true) != null;

        public static void Detect()
        {
            if (Active("mehni.pickupandhaul")) { OtherHauler = true; OtherHaulerName = "Pick Up And Haul"; }
            if (Active("laredson.harvestandhaul")) OtherHarvestHaul = true;
        }

        public static string Summary()
        {
            var off = new List<string>();
            if (OtherHauler) off.Add("hauling (" + OtherHaulerName + " is active)");
            if (OtherHarvestHaul) off.Add("harvest pockets (Harvest And Haul is active)");
            return off.Count == 0 ? "" : " Switched off: " + string.Join("; ", off.ToArray()) + ".";
        }

        public static bool Hauling => HSMod.S != null && HSMod.S.haulMany && !OtherHauler;
        public static bool Pockets(bool mine) => HSMod.S != null && !OtherHauler && (mine ? HSMod.S.pocketMine : HSMod.S.pocketHarvest && !OtherHarvestHaul);
    }

    [DefOf]
    public static class HSDefOf
    {
        public static JobDef HaulSmarterMany;
        public static JobDef HaulSmarterUnload;
        public static MainButtonDef HaulSmarterOverview;

        static HSDefOf() => DefOfHelper.EnsureInitializedInCtor(typeof(HSDefOf));
    }

    // per colonist statistics
    public sealed class HaulStat : IExposable
    {
        public int pawnId;
        public float kg;
        public int trips;
        public float kgToday;
        public int tripsToday;
        public int day = -1;

        public void ExposeData()
        {
            Scribe_Values.Look(ref pawnId, "id", 0);
            Scribe_Values.Look(ref kg, "kg", 0f);
            Scribe_Values.Look(ref trips, "trips", 0);
            Scribe_Values.Look(ref kgToday, "kgToday", 0f);
            Scribe_Values.Look(ref tripsToday, "tripsToday", 0);
            Scribe_Values.Look(ref day, "day", -1);
        }

        public void Roll(int today)
        {
            if (day != today)
            {
                day = today;
                kgToday = 0f;
                tripsToday = 0;
            }
        }
    }

    // ------------------------------------------------------------------
    // Saved data: which inventory items were picked up for hauling,
    // and 3.0 statistics.
    // ------------------------------------------------------------------
    public sealed class HSData : GameComponent
    {
        public static HSData I;
        private HashSet<int> tracked = new HashSet<int>();
        private List<HaulStat> stats = new List<HaulStat>();

        public HSData(Game game) { I = this; }

        public override void FinalizeInit()
        {
            I = this;
            Overview.SetButton();
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
                Prune();
            Scribe_Collections.Look(ref tracked, "tracked", LookMode.Value);
            Scribe_Collections.Look(ref stats, "stats", LookMode.Deep);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (tracked == null) tracked = new HashSet<int>();
                if (stats == null) stats = new List<HaulStat>();
                I = this;
            }
        }

        // forget ids of things that no longer exist
        private void Prune()
        {
            try
            {
                var alive = new HashSet<int>();
                foreach (Map m in Find.Maps)
                {
                    foreach (Thing t in m.listerThings.AllThings)
                        alive.Add(t.thingIDNumber);
                    foreach (Pawn p in m.mapPawns.AllPawns)
                        if (p.inventory != null)
                            foreach (Thing t in p.inventory.innerContainer)
                                alive.Add(t.thingIDNumber);
                }
                foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
                    if (p.inventory != null)
                        foreach (Thing t in p.inventory.innerContainer)
                            alive.Add(t.thingIDNumber);
                foreach (Caravan c in Find.WorldObjects.Caravans)
                    foreach (Pawn p in c.PawnsListForReading)
                        if (p.inventory != null)
                            foreach (Thing t in p.inventory.innerContainer)
                                alive.Add(t.thingIDNumber);
                tracked.IntersectWith(alive);
            }
            catch
            {
            }
        }

        public static bool IsTracked(Thing t) => I != null && t != null && I.tracked.Contains(t.thingIDNumber);
        // 3.0.1: when each item went into the pockets (not saved – after loading everything counts as old)
        private readonly Dictionary<int, int> pickedAt = new Dictionary<int, int>();

        public static void Track(Thing t)
        {
            if (I == null || t == null) return;
            I.tracked.Add(t.thingIDNumber);
            if (!I.pickedAt.ContainsKey(t.thingIDNumber) && Find.TickManager != null)
                I.pickedAt[t.thingIDNumber] = Find.TickManager.TicksGame;
        }

        public static void Untrack(Thing t)
        {
            if (I == null || t == null) return;
            I.tracked.Remove(t.thingIDNumber);
            I.pickedAt.Remove(t.thingIDNumber);
        }

        public static int AgeTicks(Thing t)
        {
            if (I == null || t == null || Find.TickManager == null || !I.pickedAt.TryGetValue(t.thingIDNumber, out int at))
                return int.MaxValue;
            return Find.TickManager.TicksGame - at;
        }

        public static int Today => GenDate.DaysPassed;

        public static HaulStat StatFor(Pawn p, bool create)
        {
            if (I == null || p == null)
                return null;
            HaulStat s = I.stats.FirstOrDefault(x => x.pawnId == p.thingIDNumber);
            if (s == null && create)
            {
                s = new HaulStat { pawnId = p.thingIDNumber };
                I.stats.Add(s);
            }
            s?.Roll(Today);
            return s;
        }

        public static void RecordKg(Pawn p, float kg)
        {
            HaulStat s = StatFor(p, true);
            if (s == null || kg <= 0f) return;
            s.kg += kg;
            s.kgToday += kg;
        }

        public static void RecordTrip(Pawn p)
        {
            HaulStat s = StatFor(p, true);
            if (s == null) return;
            s.trips++;
            s.tripsToday++;
        }

        public static void Totals(out float kgToday, out int tripsToday)
        {
            kgToday = 0f;
            tripsToday = 0;
            if (I == null) return;
            int today = Today;
            foreach (HaulStat s in I.stats)
            {
                s.Roll(today);
                kgToday += s.kgToday;
                tripsToday += s.tripsToday;
            }
        }
    }
}
