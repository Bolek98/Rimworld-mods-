// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakInfo
{
    // ---------------------------------------------------------------------------
    // 0.14.0 Infocentrum: the info card ("i") of a colonist in our look. Overview of all stats in groups
    // with the colony average (green better, orange worse), a colonist to compare with, the chosen stat
    // explained (what it is made of) and the race; the other pages show our character, health, social
    // and gear tabs and the life log. "Původní karta" opens the game's card.
    // ---------------------------------------------------------------------------
    public static class InfoCardSwap
    {
        private static bool bypass;
        private static readonly AccessTools.FieldRef<Dialog_InfoCard, Thing> thingRef = AccessTools.FieldRefAccess<Dialog_InfoCard, Thing>("thing");

        public static void Patch(Harmony h)
        {
            try { h.Patch(AccessTools.Method(typeof(WindowStack), nameof(WindowStack.Add)), prefix: new HarmonyMethod(typeof(InfoCardSwap), nameof(Prefix))); }
            catch (Exception e) { Log.Warning("[Mišák Tabs] info card not available: " + e.Message); }
        }

        public static void Prefix(ref Window window)
        {
            if (bypass) { bypass = false; return; }
            if (window == null || window.GetType() != typeof(Dialog_InfoCard) || MTMod.S == null || !MTMod.S.infoCard) return;
            try
            {
                if (thingRef((Dialog_InfoCard)window) is Pawn p && p.RaceProps.Humanlike && !p.Dead)
                    window = new Dialog_MTInfo(p);
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] info card swap: " + e.Message, 0x51A7B44); }
        }

        public static void OpenOriginal(Thing t)
        {
            bypass = true;
            Find.WindowStack.Add(new Dialog_InfoCard(t));
            bypass = false;
        }

        public static void OpenOriginal(Def d)
        {
            bypass = true;
            Find.WindowStack.Add(new Dialog_InfoCard(d));
            bypass = false;
        }
    }

    public class Dialog_MTInfo : Window
    {
        private readonly Pawn pawn;
        private Pawn compare;
        private int page;          // 0 overview, 1 life story, 2 health, 3 social, 4 gear, 5 history
        private string group = "all";
        private StatDef chosen;
        private Vector2 scrollGroups, scrollStats, scrollExplain, scrollPage;
        private List<Row> rows;
        private readonly Dictionary<StatDef, float> avg = new Dictionary<StatDef, float>();
        private readonly Dictionary<StatDef, float> cmp = new Dictionary<StatDef, float>();
        private List<Pawn> others;

        private class Row
        {
            public StatDef stat;
            public float value;
            public string group;
        }

        private static readonly string[] Groups = { "all", "basic", "combat", "work", "health", "resist", "food", "social", "psy", "mods" };

        // lower is better for these; the neutral ones get no colour
        private static readonly HashSet<string> LowerBetter = new HashSet<string>
        {
            "MentalBreakThreshold", "ComfyTemperatureMin", "FoodPoisonChance", "HungerRateMultiplier", "Flammability",
            "AimingDelayFactor", "RestFallRateFactor", "JoyFallRateFactor", "FilthRate", "ToxicEnvironmentResistance_Negative",
            "MeleeWeapon_CooldownMultiplier", "RangedCooldownFactor", "LifespanFactor_Negative", "ArrestSuccessChance_Negative"
        };
        private static readonly HashSet<string> Neutral = new HashSet<string>
        {
            "PsychicSensitivity", "Fertility", "LifeExpectancy", "Mass", "PawnBeauty", "BodySize_Neutral"
        };

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1200f, UI.screenWidth - 40f), Mathf.Min(720f, UI.screenHeight - 60f));
        protected override float Margin => 0f;

        public Dialog_MTInfo(Pawn p)
        {
            pawn = p;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            forcePause = false;
            preventCameraMotion = false;
            draggable = true;
        }

        // ---------------------------------------------------------------- data

        private void Build()
        {
            rows = new List<Row>();
            StatRequest req = StatRequest.For(pawn);
            foreach (StatDef s in DefDatabase<StatDef>.AllDefsListForReading)
            {
                try
                {
                    if (s.alwaysHide || !s.showOnPawns || !s.showOnHumanlikes) continue;
                    if (!s.Worker.ShouldShowFor(req) || s.Worker.IsDisabledFor(pawn)) continue;
                    float v = pawn.GetStatValue(s, true);
                    if (Mathf.Approximately(v, s.hideAtValue)) continue;
                    rows.Add(new Row { stat = s, value = v, group = GroupOf(s) });
                }
                catch { }
            }
            rows = rows.OrderBy(r => r.stat.category?.displayOrder ?? 9999).ThenByDescending(r => r.stat.displayPriorityInCategory).ThenBy(r => r.stat.label).ToList();
            others = (pawn.MapHeld?.mapPawns.FreeColonists ?? new List<Pawn>()).Where(o => o != pawn && o.RaceProps.Humanlike && !o.Dead).ToList();
            if (chosen == null) chosen = rows.FirstOrDefault(r => r.stat == StatDefOf.MentalBreakThreshold)?.stat ?? rows.FirstOrDefault()?.stat;
        }

        private static string GroupOf(StatDef s)
        {
            if (s.modContentPack != null && !s.modContentPack.IsOfficialMod) return "mods";
            string d = s.defName, c = s.category?.defName ?? "";
            if (d.Contains("Psychic") || d.Contains("Psyfocus") || d.Contains("Neural") || c.Contains("Psy")) return d == "PsychicSensitivity" ? "resist" : "psy";
            if (d.Contains("Toxic") || d.Contains("PainShock") || d.Contains("MentalBreak") || d.Contains("Resist") || d.Contains("Immunity") || c.Contains("Resist")) return "resist";
            if (d.Contains("Hunger") || d.Contains("Food") || d.Contains("Fertility") || d.Contains("Learning") || d.Contains("LifeExpectancy") || d.Contains("Nutrition") || c.Contains("Food")) return "food";
            if (c.Contains("Combat") || d.Contains("Melee") || d.Contains("Shooting") || d.Contains("Aiming") || d.Contains("Armor") || d.Contains("Dodge") || d.Contains("IncomingDamage")) return "combat";
            if (c.Contains("Social") || d.Contains("Negotiation") || d.Contains("Trade") || d.Contains("Social") || d.Contains("Recruit") || d.Contains("Tame") || d.Contains("Beauty")) return "social";
            if (c.Contains("Work") || c.Contains("Crafting") || d.Contains("Speed") && !d.Contains("Move") || d.Contains("Yield") || d.Contains("Success") || d.Contains("Efficiency")) return "work";
            if (c.Contains("Health") || d.Contains("Immunity") || d.Contains("Healing") || d.Contains("Tend") || d.Contains("Medical") || d.Contains("Surgery") || d.Contains("Rest")) return "health";
            return "basic";
        }

        private float Avg(StatDef s)
        {
            if (avg.TryGetValue(s, out float v)) return v;
            float sum = 0f; int n = 0;
            foreach (Pawn o in others)
            {
                try
                {
                    if (!s.Worker.ShouldShowFor(StatRequest.For(o)) || s.Worker.IsDisabledFor(o)) continue;
                    sum += o.GetStatValue(s, true); n++;
                }
                catch { }
            }
            v = n > 0 ? sum / n : float.NaN;
            avg[s] = v;
            return v;
        }

        private float Cmp(StatDef s)
        {
            if (compare == null) return float.NaN;
            if (cmp.TryGetValue(s, out float v)) return v;
            try { v = compare.GetStatValue(s, true); } catch { v = float.NaN; }
            cmp[s] = v;
            return v;
        }

        // +1 better, -1 worse, 0 same or neutral
        private static int Better(StatDef s, float mine, float other)
        {
            if (float.IsNaN(other) || Neutral.Contains(s.defName)) return 0;
            float diff = mine - other;
            float scale = Mathf.Max(Mathf.Abs(other), Mathf.Abs(mine), 0.01f);
            if (Mathf.Abs(diff) / scale < 0.05f) return 0;
            int sign = diff > 0 ? 1 : -1;
            return LowerBetter.Contains(s.defName) ? -sign : sign;
        }

        private static string Fmt(StatDef s, float v)
        {
            try { return s.ValueToString(v, ToStringNumberSense.Absolute); }
            catch { return v.ToString("0.##"); }
        }

        // ---------------------------------------------------------------- window

        public override void DoWindowContents(Rect inRect)
        {
            if (pawn == null || pawn.Destroyed && !pawn.Dead) { Close(); return; }
            if (rows == null) Build();
            Look.Box(inRect, Look.Bg);
            Rect head = new Rect(inRect.x, inRect.y, inRect.width, 64f);
            Header(head);
            Rect body = new Rect(inRect.x, head.yMax, inRect.width, inRect.height - head.height);
            try
            {
                switch (page)
                {
                    case 0: Overview(body); break;
                    case 5: History(body.ContractedBy(12f)); break;
                    default: Embedded(body); break;
                }
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] info card: " + e, 0x51A7B45); }
            Look.ResetGui();
        }

        private void Header(Rect r)
        {
            Look.Box(r, Look.Card);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Look.Line);
            Rect port = new Rect(r.x + 10f, r.y + 6f, 52f, 52f);
            Look.Box(port, Look.Card2, Look.Line);
            try { GUI.DrawTexture(port, PortraitsCache.Get(pawn, new Vector2(52f, 52f), Rot4.South, default, 1.25f)); } catch { }
            float x = port.xMax + 10f;
            Look.Text(new Rect(x, r.y + 6f, 300f, 26f), pawn.LabelShortCap, Color.white, GameFont.Medium);
            var bits = new List<string>();
            try
            {
                if (pawn.story?.TitleCap != null) bits.Add(pawn.story.TitleCap);
                bits.Add(pawn.genes?.Xenotype != null && pawn.genes.Xenotype != XenotypeDefOf.Baseliner ? pawn.genes.XenotypeLabelCap : pawn.def.LabelCap.ToString());
                bits.Add(pawn.ageTracker.CurLifeStage.label + " · " + "MT_H_Age".Translate(pawn.ageTracker.AgeBiologicalYears));
            }
            catch { }
            Look.Text(new Rect(x, r.y + 34f, 360f, 18f), string.Join(" · ", bits).Truncate(360f), new Color32(160, 167, 174, 255), GameFont.Tiny);
            // a warning chip: mental break risk
            try
            {
                var mb = pawn.mindState?.mentalBreaker;
                string risk = mb == null ? null : mb.BreakExtremeIsImminent ? "MT_I_RiskHigh".Translate() : mb.BreakMajorIsImminent ? "MT_I_RiskMid".Translate() : mb.BreakMinorIsImminent ? "MT_I_RiskLow".Translate() : null;
                if (risk != null)
                {
                    Verse.Text.Font = GameFont.Tiny;
                    float w = Verse.Text.CalcSize(risk).x + 18f;
                    Verse.Text.Font = GameFont.Small;
                    Rect cr = new Rect(x + 370f, r.y + 20f, w, 22f);
                    Look.Box(cr, new Color32(60, 34, 30, 255), Look.Red);
                    Look.Text(cr, risk, Look.Red, GameFont.Tiny, TextAnchor.MiddleCenter);
                }
            }
            catch { }
            // pages
            string[] pages = { "MT_I_Overview".Translate(), "MT_I_Life".Translate(), "MT_I_Health".Translate(), "MT_I_Social".Translate(), "MT_I_Gear".Translate(), "MT_I_History".Translate() };
            float px = r.xMax - 40f;
            float total = 0f;
            Verse.Text.Font = GameFont.Small;
            float[] ws = pages.Select(p => Verse.Text.CalcSize(p).x + 24f).ToArray();
            total = ws.Sum() + 6f * (ws.Length - 1);
            px = r.xMax - 40f - 130f - 12f - total;
            for (int i = 0; i < pages.Length; i++)
            {
                if (Look.Chip(new Rect(px, r.y + 20f, ws[i], 24f), pages[i], page == i)) { page = i; scrollPage = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                px += ws[i] + 6f;
            }
            if (Look.Button(new Rect(r.xMax - 40f - 130f, r.y + 18f, 130f, 28f), "MT_I_Original".Translate()))
            {
                Close();
                InfoCardSwap.OpenOriginal(pawn);
            }
        }

        // the other pages: our tabs, their own header cut off
        private void Embedded(Rect r)
        {
            const float cut = 54f;
            GUI.BeginGroup(r);
            TabSize.suppress = true;
            try
            {
                Rect full = new Rect(0f, -cut, r.width, r.height + cut);
                switch (page)
                {
                    case 1: CharacterTab.Draw(full, pawn); break;
                    case 2: HealthTab.DrawFor(full, pawn); break;
                    case 3: SocialTab.Draw(full, pawn); break;
                    case 4: GearTab.Draw(full, pawn); break;
                }
            }
            finally { TabSize.suppress = false; GUI.EndGroup(); }
        }

        private void History(Rect r)
        {
            var entries = LifeLog.For(pawn).OrderByDescending(e => e.tick).ToList();
            if (entries.Count == 0)
            {
                Look.Text(r, "MT_I_NoHistory".Translate(), new Color32(160, 167, 174, 255), GameFont.Small, TextAnchor.MiddleCenter);
                return;
            }
            Rect view = new Rect(0f, 0f, r.width - 16f, entries.Count * 28f);
            Widgets.BeginScrollView(r, ref scrollPage, view);
            for (int i = 0; i < entries.Count; i++)
            {
                var e = entries[i];
                Rect row = new Rect(0f, i * 28f, view.width, 26f);
                if (i % 2 == 0) Widgets.DrawBoxSolid(row, Look.Card);
                string date = GenDate.DateFullStringAt(e.tick, Find.WorldGrid.LongLatOf(pawn.MapHeld?.Tile ?? 0));
                Look.Text(new Rect(row.x + 8f, row.y, 190f, row.height), date, new Color32(160, 167, 174, 255), GameFont.Tiny);
                Look.Text(new Rect(row.x + 200f, row.y, row.width - 200f, row.height), e.text.Truncate(row.width - 204f), Color.white, GameFont.Small);
            }
            Widgets.EndScrollView();
        }

        // ---------------------------------------------------------------- overview

        private void Overview(Rect r)
        {
            Rect left = new Rect(r.x, r.y, 190f, r.height);
            Rect right = new Rect(r.xMax - 310f, r.y, 310f, r.height);
            Rect mid = new Rect(left.xMax, r.y, right.x - left.xMax, r.height).ContractedBy(12f);
            GroupList(left);
            Middle(mid);
            Explain(right);
        }

        private void GroupList(Rect r)
        {
            Look.Box(r, Look.Card);
            const float h = 28f;
            Rect view = new Rect(0f, 0f, r.width - 16f, Groups.Length * h + 12f);
            Widgets.BeginScrollView(r, ref scrollGroups, view);
            float y = 8f;
            foreach (string g in Groups)
            {
                int n = g == "all" ? rows.Count : rows.Count(x => x.group == g);
                if (n == 0 && g != "all") continue;
                Rect rr = new Rect(0f, y, view.width, h);
                bool sel = group == g;
                if (sel) { Widgets.DrawBoxSolid(rr, Look.GreenBg); Widgets.DrawBoxSolid(new Rect(rr.x, rr.y, 3f, rr.height), Look.Green); }
                else if (Mouse.IsOver(rr)) Widgets.DrawBoxSolid(rr, Look.Card2);
                Look.Text(new Rect(rr.x + 12f, rr.y, rr.width - 50f, rr.height), ("MT_I_G_" + g).Translate(), sel ? Look.Green : Color.white);
                Look.Text(new Rect(rr.xMax - 44f, rr.y, 36f, rr.height), n.ToString(), new Color32(160, 167, 174, 255), GameFont.Tiny, TextAnchor.MiddleRight);
                if (Widgets.ButtonInvisible(rr)) { group = g; scrollStats = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                y += h;
            }
            Widgets.EndScrollView();
        }

        private void Middle(Rect r)
        {
            // compare with
            Look.Text(new Rect(r.x, r.y, 110f, 26f), "MT_I_Compare".Translate(), new Color32(160, 167, 174, 255), GameFont.Small);
            Rect cb = new Rect(r.x + 112f, r.y, 220f, 26f);
            if (Look.Button(cb, compare != null ? compare.LabelShortCap : (string)"MT_I_Pick".Translate()))
            {
                var opts = new List<FloatMenuOption> { new FloatMenuOption("MT_I_Average".Translate(), () => { compare = null; cmp.Clear(); }) };
                foreach (Pawn o in others) { Pawn oo = o; opts.Add(new FloatMenuOption(o.LabelShortCap, () => { compare = oo; cmp.Clear(); })); }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            float y = r.y + 34f;
            // four key tiles
            float tw = (r.width - 3 * 8f) / 4f;
            Tile(new Rect(r.x, y, tw, 64f), StatDefOf.MarketValue, ValueNote);
            Tile(new Rect(r.x + (tw + 8f), y, tw, 64f), StatDefOf.MoveSpeed, AvgNote);
            Tile(new Rect(r.x + 2 * (tw + 8f), y, tw, 64f), StatDefOf.CarryingCapacity, RankNote);
            TempTile(new Rect(r.x + 3 * (tw + 8f), y, tw, 64f));
            y += 72f;
            Look.Text(new Rect(r.x, y, r.width, 18f), (compare != null ? "MT_I_LegendCmp".Translate(compare.LabelShortCap) : "MT_I_Legend".Translate()), new Color32(160, 167, 174, 255), GameFont.Tiny);
            y += 22f;
            // the stats in two columns, grouped
            var shown = group == "all" ? rows : rows.Where(x => x.group == group).ToList();
            var byGroup = shown.GroupBy(x => x.group).ToList();
            Rect area = new Rect(r.x, y, r.width, r.yMax - y);
            float colW = (area.width - 16f - 10f) / 2f;
            const float rowH = 24f;
            // layout: fill the shorter column with each group card
            var place = new List<(IGrouping<string, Row> g, float x, float y, float h)>();
            float[] colY = { 0f, 0f };
            foreach (var g in byGroup)
            {
                float hgt = 28f + g.Count() * rowH + 8f;
                int c = colY[0] <= colY[1] ? 0 : 1;
                place.Add((g, c * (colW + 10f), colY[c], hgt));
                colY[c] += hgt + 10f;
            }
            Rect view = new Rect(0f, 0f, area.width - 16f, Mathf.Max(colY[0], colY[1]));
            Widgets.BeginScrollView(area, ref scrollStats, view);
            foreach (var (g, gx, gy, gh) in place)
            {
                Rect card = new Rect(gx, gy, colW, gh);
                if (gy + gh < scrollStats.y || gy > scrollStats.y + area.height) continue;
                Look.Box(card, Look.Card, Look.Line);
                Look.Text(new Rect(card.x + 10f, card.y + 4f, card.width - 20f, 22f), ("MT_I_G_" + g.Key).Translate(), Look.Green, GameFont.Small);
                float ry = card.y + 28f;
                foreach (Row row in g)
                {
                    Rect rr = new Rect(card.x + 4f, ry, card.width - 8f, rowH);
                    if (chosen == row.stat) Widgets.DrawBoxSolid(rr, Look.GreenBg);
                    else if (Mouse.IsOver(rr)) Widgets.DrawBoxSolid(rr, Look.Card2);
                    float other = compare != null ? Cmp(row.stat) : Avg(row.stat);
                    int b = Better(row.stat, row.value, other);
                    Color vc = b > 0 ? Look.Green : b < 0 ? Look.Orange : Color.white;
                    float valW = compare != null ? 150f : 90f;
                    Look.Text(new Rect(rr.x + 6f, rr.y, rr.width - valW - 10f, rr.height), row.stat.LabelCap.ToString().Truncate(rr.width - valW - 10f), new Color32(220, 222, 224, 255), GameFont.Small);
                    string val = Fmt(row.stat, row.value);
                    if (compare != null && !float.IsNaN(other)) val += "  <color=#8a9198>" + Fmt(row.stat, other) + "</color>";
                    Look.Text(new Rect(rr.xMax - valW - 4f, rr.y, valW, rr.height), val, vc, GameFont.Small, TextAnchor.MiddleRight);
                    if (Widgets.ButtonInvisible(rr)) { chosen = row.stat; scrollExplain = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                    ry += rowH;
                }
            }
            Widgets.EndScrollView();
        }

        private string ValueNote(StatDef s, float v)
        {
            if (others.Count == 0) return "";
            int lower = others.Count(o => { try { return o.GetStatValue(s) < v; } catch { return false; } });
            return "MT_I_MoreThan".Translate(Mathf.RoundToInt(100f * lower / others.Count));
        }

        private string AvgNote(StatDef s, float v)
        {
            float a = Avg(s);
            return float.IsNaN(a) ? "" : (string)"MT_I_ColonyAvg".Translate(Fmt(s, a));
        }

        private string RankNote(StatDef s, float v)
        {
            if (others.Count == 0) return "";
            bool most = others.All(o => { try { return o.GetStatValue(s) <= v; } catch { return true; } });
            bool least = others.All(o => { try { return o.GetStatValue(s) >= v; } catch { return true; } });
            return most ? "MT_I_Most".Translate() : least ? "MT_I_Least".Translate() : AvgNote(s, v);
        }

        private void Tile(Rect r, StatDef s, Func<StatDef, float, string> note)
        {
            Look.Box(r, Look.Card, Look.Line);
            float v;
            try { v = pawn.GetStatValue(s); } catch { return; }
            int b = Better(s, v, Avg(s));
            Look.Text(new Rect(r.x + 10f, r.y + 4f, r.width - 20f, 18f), s.LabelCap, new Color32(160, 167, 174, 255), GameFont.Tiny);
            Look.Text(new Rect(r.x + 10f, r.y + 20f, r.width - 20f, 26f), Fmt(s, v), b > 0 ? Look.Green : b < 0 ? Look.Orange : Color.white, GameFont.Medium);
            Look.Text(new Rect(r.x + 10f, r.y + 44f, r.width - 20f, 18f), note(s, v).Truncate(r.width - 20f), new Color32(160, 167, 174, 255), GameFont.Tiny);
            if (Widgets.ButtonInvisible(r)) { chosen = s; scrollExplain = Vector2.zero; }
        }

        private void TempTile(Rect r)
        {
            Look.Box(r, Look.Card, Look.Line);
            float lo, hi;
            try { lo = pawn.GetStatValue(StatDefOf.ComfyTemperatureMin); hi = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax); } catch { return; }
            Look.Text(new Rect(r.x + 10f, r.y + 4f, r.width - 20f, 18f), "MT_I_Temp".Translate(), new Color32(160, 167, 174, 255), GameFont.Tiny);
            Look.Text(new Rect(r.x + 10f, r.y + 20f, r.width - 20f, 26f), lo.ToStringTemperature("F0") + " – " + hi.ToStringTemperature("F0"), Color.white, GameFont.Medium);
            float alo = Avg(StatDefOf.ComfyTemperatureMin), ahi = Avg(StatDefOf.ComfyTemperatureMax);
            string note = "";
            if (!float.IsNaN(alo) && lo < alo - 8f) note = "MT_I_ColdProof".Translate();
            else if (!float.IsNaN(ahi) && hi > ahi + 8f) note = "MT_I_HeatProof".Translate();
            else if (!float.IsNaN(alo) && lo > alo + 8f) note = "MT_I_ColdWeak".Translate();
            Look.Text(new Rect(r.x + 10f, r.y + 44f, r.width - 20f, 18f), note, new Color32(160, 167, 174, 255), GameFont.Tiny);
            if (Widgets.ButtonInvisible(r)) { chosen = StatDefOf.ComfyTemperatureMin; scrollExplain = Vector2.zero; }
        }

        private void Explain(Rect r)
        {
            Look.Box(r, Look.Card);
            Rect inner = r.ContractedBy(12f);
            float y = inner.y;
            if (chosen != null)
            {
                float v = 0f;
                try { v = pawn.GetStatValue(chosen); } catch { }
                Look.Text(new Rect(inner.x, y, inner.width, 18f), "MT_I_Chosen".Translate(), new Color32(160, 167, 174, 255), GameFont.Tiny);
                y += 18f;
                string title = chosen.LabelCap + ": " + Fmt(chosen, v);
                float th = Verse.Text.CalcHeight(title, inner.width);
                Look.Text(new Rect(inner.x, y, inner.width, th), title, Color.white, GameFont.Small, TextAnchor.UpperLeft);
                y += th + 4f;
                Verse.Text.Font = GameFont.Tiny;
                string desc = chosen.description ?? "";
                float a = Avg(chosen);
                if (!float.IsNaN(a)) desc += (desc.Length > 0 ? " " : "") + "MT_I_ColonyAvg".Translate(Fmt(chosen, a)) + ".";
                float dh = Mathf.Min(Verse.Text.CalcHeight(desc, inner.width), 110f);
                GUI.color = new Color32(190, 195, 200, 255);
                Widgets.Label(new Rect(inner.x, y, inner.width, dh), desc.TruncateHeight(inner.width, dh));
                GUI.color = Color.white;
                y += dh + 8f;
                Look.Text(new Rect(inner.x, y, inner.width, 20f), "MT_I_MadeOf".Translate(), Look.Green, GameFont.Small);
                y += 22f;
                string ex;
                try { ex = chosen.Worker.GetExplanationFull(StatRequest.For(pawn), ToStringNumberSense.Absolute, v); } catch { ex = ""; }
                float raceH = 120f;
                Rect er = new Rect(inner.x, y, inner.width, inner.yMax - y - raceH - 8f);
                Verse.Text.Font = GameFont.Tiny;
                float eh = Verse.Text.CalcHeight(ex, er.width - 16f);
                Rect ev = new Rect(0f, 0f, er.width - 16f, eh);
                Look.Box(er, Look.Bg, Look.Line);
                Widgets.BeginScrollView(er.ContractedBy(4f), ref scrollExplain, ev);
                GUI.color = new Color32(210, 214, 218, 255);
                Widgets.Label(ev, ex);
                GUI.color = Color.white;
                Widgets.EndScrollView();
                Verse.Text.Font = GameFont.Small;
                y = er.yMax + 8f;
            }
            // the race
            Rect race = new Rect(inner.x, y, inner.width, inner.yMax - y);
            Look.Text(new Rect(race.x, race.y, race.width, 20f), "MT_I_Race".Translate(), Look.Green, GameFont.Small);
            string what = pawn.genes?.Xenotype != null && pawn.genes.Xenotype != XenotypeDefOf.Baseliner
                ? pawn.genes.XenotypeLabelCap + " – " + pawn.genes.Xenotype.description
                : pawn.def.LabelCap + " – " + pawn.def.description;
            Verse.Text.Font = GameFont.Tiny;
            GUI.color = new Color32(190, 195, 200, 255);
            Rect tr = new Rect(race.x, race.y + 22f, race.width, race.height - 22f - 26f);
            Widgets.Label(tr, (what ?? "").TruncateHeight(tr.width, tr.height));
            GUI.color = Color.white;
            Verse.Text.Font = GameFont.Small;
            if (Look.Button(new Rect(race.x, race.yMax - 24f, 120f, 24f), "MT_I_FullDesc".Translate()))
            {
                Def d = pawn.genes?.Xenotype != null && pawn.genes.Xenotype != XenotypeDefOf.Baseliner ? (Def)pawn.genes.Xenotype : pawn.def;
                InfoCardSwap.OpenOriginal(d);
            }
        }
    }
}
