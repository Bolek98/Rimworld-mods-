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
    // 0.2.0 Health tab: an overview on the left (state, care, pain, capacities), the colonist's
    // whole figure in the middle with marks where something is wrong, and the conditions grouped
    // by body part on the right. Operations and the original tab are one click away.
    // ---------------------------------------------------------------------------
    public static class HealthTab
    {
        public static readonly Vector2 Size = new Vector2(820f, 540f);
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        private static AccessTools.FieldRef<bool> opTab;
        public static bool showOriginal;
        private static bool showAll;
        private static string region;          // a mark on the figure clicked: only that region in the list
        private static Vector2 leftScroll, listScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Health), "FillTab"),
                    prefix: new HarmonyMethod(typeof(HealthTab), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(HealthTab), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] health tab not available: " + e.Message); }
            try { opTab = AccessTools.StaticFieldRefAccess<bool>(AccessTools.Field(typeof(HealthCardUtility), "onOperationTab")); }
            catch { opTab = null; }
        }

        public static void TakeOver()
        {
            if (!MTMod.S.health || !MTMod.S.replaceOtherHealthTabs) return;
            try
            {
                var m = AccessTools.Method(typeof(ITab_Pawn_Health), "FillTab");
                Patches info = Harmony.GetPatchInfo(m);
                if (info == null) return;
                foreach (Patch p in info.Prefixes.Where(x => x.owner != MTMod.H.Id).ToList())
                {
                    MTMod.H.Unpatch(m, p.PatchMethod);
                    Log.Message("[Mišák Tabs] the health tab of " + p.owner + " is replaced by Mišák Tabs (switch it back in the settings).");
                }
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] health take over: " + e.Message); }
        }

        public static bool Ours(InspectTabBase t) => t is ITab_Pawn_Health && MTMod.S.health && !showOriginal;

        private static Pawn SelPawn
        {
            get
            {
                Thing t = Find.Selector.SingleSelectedThing;
                if (t is Pawn p) return p;
                if (t is Corpse c) return c.InnerPawn;
                return null;
            }
        }

        public static bool Prefix(ITab_Pawn_Health __instance)
        {
            if (!MTMod.S.health || showOriginal) return true;
            Pawn pawn = SelPawn;
            if (pawn == null) return true;
            sizeRef(__instance) = Size;
            try { Draw(new Rect(0f, 0f, Size.x, Size.y), pawn); }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] health tab: " + e, 0x51A7B12);
                showOriginal = true;
            }
            return false;
        }

        public static void Postfix(ITab_Pawn_Health __instance)
        {
            if (!MTMod.S.health || !showOriginal) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 126f, sz.y - 30f, 116f, 24f), "MT_OurLook".Translate(), true))
            {
                showOriginal = false;
                if (opTab != null) opTab() = false;
            }
        }

        // ---------- drawing ----------
        private static void Draw(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            Header(head, pawn);
            Rect body = new Rect(r.x, head.yMax, r.width, r.height - head.height);
            Rect left = new Rect(body.x, body.y, 250f, body.height);
            Rect mid = new Rect(left.xMax, body.y, 230f, body.height);
            Rect right = new Rect(mid.xMax, body.y, body.width - left.width - mid.width, body.height);
            if (opsMode && OpsTab.Available && CanOperate(pawn))
            {
                OpsTab.Draw(body, pawn);
                return;
            }
            Widgets.DrawBoxSolid(new Rect(left.xMax, body.y, 1f, body.height), Look.Line);
            Widgets.DrawBoxSolid(new Rect(mid.xMax, body.y, 1f, body.height), Look.Line);
            Overview(left.ContractedBy(10f), pawn);
            Figure(mid.ContractedBy(8f), pawn);
            Conditions(right.ContractedBy(10f), pawn);
        }

        private static void Header(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Card);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Look.Line);
            Look.Text(new Rect(r.x + 12f, r.y + 6f, 360f, 24f), pawn.LabelCap, Color.white, GameFont.Medium);
            State(pawn, out string state, out Color sc);
            Look.Text(new Rect(r.x + 12f, r.y + 30f, 420f, 20f), state, sc, GameFont.Tiny);
            bool ops = CanOperate(pawn);
            Rect op = new Rect(r.xMax - 262f, r.y + 13f, 120f, 28f);
            bool inOps = opsMode && ops && OpsTab.Available;
            if (Look.Button(op, inOps ? "MT_O_BackHealth".Translate() : "MT_Operations".Translate(), true, ops || inOps))
            {
                if (OpsTab.Available) opsMode = !inOps;
                else
                {
                    showOriginal = true;
                    if (opTab != null) opTab() = true;
                }
            }
            TooltipHandler.TipRegion(op, ops ? "MT_OperationsTip".Translate() : "MT_OperationsNo".Translate());
            Rect orig = new Rect(r.xMax - 132f, r.y + 13f, 120f, 28f);
            if (Look.Button(orig, "MT_Original".Translate()))
            {
                showOriginal = true;
                if (opTab != null) opTab() = false;
            }
            TooltipHandler.TipRegion(orig, "MT_OriginalTipHealth".Translate());
        }

        // the same rules as the game's tab (simplified): own and guest pawns that have operations
        private static bool CanOperate(Pawn pawn)
        {
            if (pawn.Dead) return false;
            try
            {
                if (!pawn.def.AllRecipes.Any(x => x.AvailableNow && x.AvailableOnNow(pawn))) return false;
            }
            catch { return false; }
            if (pawn.Faction == Faction.OfPlayer || pawn.IsPrisonerOfColony) return true;
            if (pawn.HostFaction == Faction.OfPlayer && !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Moving)) return true;
            if (pawn.RaceProps.IsFlesh && pawn.Faction != null && pawn.Faction.HostileTo(Faction.OfPlayer)) return false;
            return !pawn.RaceProps.Humanlike && pawn.Downed;
        }

        // one line that says how the pawn is – the most urgent thing first
        private static void State(Pawn pawn, out string text, out Color c)
        {
            HediffSet hs = pawn.health.hediffSet;
            if (pawn.Dead) { text = "MT_H_Dead".Translate(); c = Muted; return; }
            float bleed = hs.BleedRateTotal;
            if (bleed > 0.001f)
            {
                int ticks = HealthUtility.TicksUntilDeathDueToBloodLoss(pawn);
                text = ticks < 60000
                    ? "MT_H_BleedDeath".Translate(bleed.ToStringPercent(), ticks.ToStringTicksToPeriod()).ToString()
                    : "MT_H_Bleeding".Translate(bleed.ToStringPercent()).ToString();
                c = Look.Red;
                return;
            }
            Hediff race = Diseases(pawn).OrderByDescending(d => d.Severity - Immunity(d)).FirstOrDefault();
            if (race != null && race.Severity > Immunity(race))
            {
                text = "MT_H_Losing".Translate(race.LabelCap);
                c = Look.Red;
                return;
            }
            if (hs.HasTendableHediff()) { text = "MT_H_NeedsTend".Translate(); c = Look.Orange; return; }
            if (pawn.Downed) { text = "MT_H_Downed".Translate(); c = Look.Orange; return; }
            if (race != null) { text = "MT_H_Winning".Translate(race.LabelCap); c = Look.Blue; return; }
            if (hs.hediffs.Any(h => h.Visible && h is Hediff_Injury)) { text = "MT_H_Healing".Translate(); c = Look.Blue; return; }
            text = "MT_H_Healthy".Translate();
            c = Look.Green;
        }

        private static IEnumerable<Hediff> Diseases(Pawn pawn) =>
            pawn.health.hediffSet.hediffs.Where(h => h.Visible && h.TryGetComp<HediffComp_Immunizable>() != null && !h.FullyImmune());

        private static float Immunity(Hediff h)
        {
            try { return h.TryGetComp<HediffComp_Immunizable>()?.Immunity ?? 0f; } catch { return 0f; }
        }

        // ---------- left: care, pain, capacities ----------
        private static void Overview(Rect r, Pawn pawn)
        {
            float y = r.y;
            bool own = pawn.Faction == Faction.OfPlayer || pawn.HostFaction == Faction.OfPlayer;
            if (!pawn.Dead && pawn.playerSettings != null && own && pawn.RaceProps.IsFlesh && Current.ProgramState == ProgramState.Playing)
            {
                Look.Text(new Rect(r.x, y, r.width, 18f), "MT_H_Care".Translate(), Muted, GameFont.Tiny);
                y += 18f;
                Rect mc = new Rect(r.x, y, r.width, 28f);
                Look.Box(mc, Look.Card2, Look.Line);
                Widgets.DrawHighlightIfMouseover(mc);
                MedicalCareUtility.MedicalCareSelectButton(mc, pawn);
                TooltipHandler.TipRegionByKey(mc, "MedicineQualityDescription");
                y += 32f;
                if (pawn.IsColonist && !pawn.DevelopmentalStage.Baby())
                {
                    Rect st = new Rect(r.x, y, r.width, 24f);
                    bool before = pawn.playerSettings.selfTend;
                    Widgets.CheckboxLabeled(st, "AllowSelfTend".Translate(), ref pawn.playerSettings.selfTend);
                    TooltipHandler.TipRegion(st, "AllowSelfTendTip".Translate(Faction.OfPlayer.def.pawnsPlural, 0.7f.ToStringPercent()).CapitalizeFirst());
                    if (pawn.playerSettings.selfTend && !before && pawn.WorkTypeIsDisabled(WorkTypeDefOf.Doctor))
                    {
                        pawn.playerSettings.selfTend = false;
                        Messages.Message("MessageCannotSelfTendEver".Translate(pawn.LabelShort, pawn), MessageTypeDefOf.RejectInput, false);
                    }
                    y += 28f;
                }
                if (pawn.foodRestriction != null && pawn.foodRestriction.Configurable && pawn.needs?.food != null && !pawn.DevelopmentalStage.Baby())
                {
                    Rect fr = new Rect(r.x, y, r.width, 26f);
                    Look.Text(new Rect(fr.x, fr.y, 70f, fr.height), "AllowFood".Translate(), Color.white, GameFont.Tiny);
                    if (Look.Button(new Rect(fr.x + 74f, fr.y, fr.width - 74f, fr.height), pawn.foodRestriction.CurrentFoodPolicy.label.Truncate(fr.width - 84f)))
                    {
                        var opts = new List<FloatMenuOption>();
                        foreach (FoodPolicy fp in Current.Game.foodRestrictionDatabase.AllFoodRestrictions)
                        {
                            FoodPolicy f = fp;
                            opts.Add(new FloatMenuOption(f.label, () => pawn.foodRestriction.CurrentFoodPolicy = f));
                        }
                        opts.Add(new FloatMenuOption("ManageFoodPolicies".Translate() + "...", () => Find.WindowStack.Add(new Dialog_ManageFoodPolicies(pawn.foodRestriction.CurrentFoodPolicy))));
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    y += 30f;
                }
                Widgets.DrawBoxSolid(new Rect(r.x, y + 2f, r.width, 1f), Look.Line);
                y += 8f;
            }

            // pain and bleeding
            if (pawn.RaceProps.IsFlesh)
            {
                Pair<string, Color> pain = HealthCardUtility.GetPainLabel(pawn);
                Row(new Rect(r.x, y, r.width, 22f), "PainLevel".Translate(), pain.First, pain.Second, pawn.health.hediffSet.PainTotal, HealthCardUtility.GetPainTip(pawn), true);
                y += 26f;
            }
            if (pawn.Dead) return;

            // capacities with bars
            Look.Text(new Rect(r.x, y, r.width, 18f), "MT_H_Capacities".Translate(), Muted, GameFont.Tiny);
            y += 20f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            var caps = Capacities(pawn).ToList();
            Rect view = new Rect(0f, 0f, outR.width - (caps.Count * 28f > outR.height ? 16f : 0f), caps.Count * 28f);
            Widgets.BeginScrollView(outR, ref leftScroll, view);
            float cy = 0f;
            foreach (PawnCapacityDef cap in caps)
            {
                PawnCapacityDef c = cap;
                Pair<string, Color> eff = HealthCardUtility.GetEfficiencyLabel(pawn, c);
                float lvl = pawn.health.capacities.GetLevel(c);
                Row(new Rect(0f, cy, view.width, 24f), c.GetLabelFor(pawn).CapitalizeFirst(), eff.First, eff.Second, lvl, null, false,
                    new TipSignal(() => HealthCardUtility.GetPawnCapacityTip(pawn, c), pawn.thingIDNumber ^ c.index));
                cy += 28f;
            }
            Widgets.EndScrollView();
        }

        private static IEnumerable<PawnCapacityDef> Capacities(Pawn pawn)
        {
            RaceProperties race = pawn.RaceProps;
            IEnumerable<PawnCapacityDef> all = DefDatabase<PawnCapacityDef>.AllDefs;
            IEnumerable<PawnCapacityDef> src = race.Humanlike ? all.Where(x => x.showOnHumanlikes)
                : race.Animal ? all.Where(x => x.showOnAnimals)
                : race.IsMechanoid ? all.Where(x => x.showOnMechanoids)
                : all.Where(x => x.showOnHumanlikes);
            return src.Where(c => PawnCapacityUtility.BodyCanEverDoCapacity(race.body, c)).OrderBy(c => c.listOrder);
        }

        // a label, the value on the right and a thin bar under it
        private static void Row(Rect r, string label, string value, Color c, float pct, string tip, bool reverse, TipSignal? sig = null)
        {
            if (Mouse.IsOver(r)) Widgets.DrawBoxSolid(r, Look.Card);
            Look.Text(new Rect(r.x + 2f, r.y, r.width - 70f, r.height - 4f), label, Color.white, GameFont.Small);
            Look.Text(new Rect(r.xMax - 70f, r.y, 68f, r.height - 4f), value, c, GameFont.Small, TextAnchor.MiddleRight);
            float shown = reverse ? Mathf.Clamp01(pct) : Mathf.Clamp01(pct);
            Look.Bar(new Rect(r.x + 2f, r.yMax - 3f, r.width - 4f, 3f), shown, c);
            if (sig.HasValue) TooltipHandler.TipRegion(r, sig.Value);
            else if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(r, tip);
        }

        // ---------- middle: the figure with marks ----------
        private struct Mark
        {
            public string key;
            public Vector2 pos;
            public Color color;
            public bool bleeding;
            public List<Hediff> list;
        }

        private static void Figure(Rect r, Pawn pawn)
        {
            Rect fig = new Rect(r.x + (r.width - 200f) / 2f, r.y + 6f, 200f, 280f);
            Look.Box(fig, Look.Card, Look.Line);
            // 0.3.0: the colonist's own silhouette coloured by health (any race), or the plain portrait
            Rect box = new Rect(0f, 0f, 1f, 1f);
            bool sil = false;
            if (silhouette)
            {
                try
                {
                    var health = Silhouette.RegionHealth(pawn, RegionOf);
                    Texture2D st = Silhouette.Get(pawn, new Vector2(fig.width, fig.height), health, out box);
                    if (st != null) { GUI.DrawTexture(fig, st); sil = true; }
                }
                catch (Exception e)
                {
                    silhouette = false;
                    Log.Warning("[Mišák Tabs] silhouette: " + e.Message);
                }
            }
            if (!sil)
            {
                try
                {
                    RenderTexture tex = PortraitsCache.Get(pawn, new Vector2(fig.width, fig.height), Rot4.South, default(Vector3), 1f);
                    GUI.DrawTexture(fig, tex);
                }
                catch
                {
                    try { Widgets.ThingIcon(fig.ContractedBy(20f), pawn); } catch { }
                }
            }
            Rect vb = new Rect(fig.xMax - 26f, fig.y + 4f, 22f, 22f);
            if (Widgets.ButtonInvisible(vb)) { silhouette = !silhouette; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
            Widgets.DrawBoxSolid(vb, Mouse.IsOver(vb) ? Look.Card2 : new Color(0f, 0f, 0f, 0.35f));
            Look.Text(vb, silhouette ? "◐" : "◑", Muted, GameFont.Small, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(vb, silhouette ? "MT_H_ToPortrait".Translate() : "MT_H_ToSilhouette".Translate());

            var marks = Marks(pawn);
            foreach (Mark m in marks)
            {
                Vector2 mp = sil ? Silhouette.MapSpot(m.pos) : m.pos;
                if (sil) mp = new Vector2(box.x + mp.x * box.width, box.y + mp.y * box.height);
                Vector2 p = new Vector2(fig.x + mp.x * fig.width, fig.y + mp.y * fig.height);
                float s = m.bleeding ? 12f + Mathf.PingPong(Time.realtimeSinceStartup * 8f, 4f) : 12f;
                Rect mr = new Rect(p.x - s / 2f, p.y - s / 2f, s, s);
                bool sel = region == m.key;
                Widgets.DrawBoxSolid(mr.ExpandedBy(sel ? 3f : 1.5f), sel ? Color.white : new Color(0f, 0f, 0f, 0.85f));
                Widgets.DrawBoxSolid(mr, m.color);
                Rect hit = mr.ExpandedBy(5f);
                if (Mouse.IsOver(hit))
                {
                    string tip = string.Join("\n", m.list.Select(h => (h.Part != null ? h.Part.LabelCap + ": " : "") + h.LabelCap).Distinct().ToArray());
                    TooltipHandler.TipRegion(hit, tip + "\n\n" + "MT_H_MarkTip".Translate());
                }
                if (Widgets.ButtonInvisible(hit))
                {
                    region = sel ? null : m.key;
                    listScroll = Vector2.zero;
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                }
            }

            // legend and the whole-body things
            float y = fig.yMax + 8f;
            int whole = Visible(pawn).Count(h => h.Part == null);
            if (marks.Count == 0 && whole == 0)
                Look.Text(new Rect(r.x, y, r.width, 40f), "MT_H_NothingWrong".Translate(), Look.Green, GameFont.Small, TextAnchor.MiddleCenter);
            else
            {
                Legend(new Rect(r.x, y, r.width, 18f), Look.Red, "MT_H_LegendBad".Translate());
                Legend(new Rect(r.x, y + 18f, r.width, 18f), Look.Orange, "MT_H_LegendHurt".Translate());
                Legend(new Rect(r.x, y + 36f, r.width, 18f), Look.Blue, "MT_H_LegendOther".Translate());
                if (whole > 0)
                    Look.Text(new Rect(r.x, y + 58f, r.width, 18f), "MT_H_Whole".Translate(whole), Muted, GameFont.Tiny);
            }
            if (region != null)
            {
                if (Look.Button(new Rect(r.x + 20f, r.yMax - 30f, r.width - 40f, 26f), "MT_H_ShowAllParts".Translate()))
                    region = null;
            }
        }

        public static bool silhouette = true;
        public static bool opsMode;            // 0.4.0: our own operations view

        public static void FigureForOps(Rect r, Pawn pawn) => Figure(r, pawn);
        public static Silhouette.Region RegionOfPart(BodyPartRecord part) => RegionOf(part);
        private static readonly HashSet<string> ArmParts = new HashSet<string> { "Shoulder", "Clavicle", "Arm", "Humerus", "Radius", "Hand", "Finger" };
        private static readonly HashSet<string> LegParts = new HashSet<string> { "Leg", "Femur", "Tibia", "Foot", "Toe" };

        // which region of the silhouette a body part belongs to
        private static Silhouette.Region RegionOf(BodyPartRecord part)
        {
            int side = Side(part);
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                string n = p.def.defName;
                if (ArmParts.Contains(n)) return side > 0 ? Silhouette.Region.ArmR : Silhouette.Region.ArmL;
                if (LegParts.Contains(n)) return side > 0 ? Silhouette.Region.LegR : Silhouette.Region.LegL;
                if (Spots.TryGetValue(n, out Vector2 v)) return v.y < 0.4f ? Silhouette.Region.Head : Silhouette.Region.Torso;
            }
            // other bodies: by height; limbs of an unknown body by their name
            string d = part.def.defName.ToLowerInvariant();
            if (d.Contains("arm") || d.Contains("hand") || d.Contains("wing")) return side > 0 ? Silhouette.Region.ArmR : Silhouette.Region.ArmL;
            if (d.Contains("leg") || d.Contains("foot") || d.Contains("paw")) return side > 0 ? Silhouette.Region.LegR : Silhouette.Region.LegL;
            return part.height == BodyPartHeight.Top ? Silhouette.Region.Head : Silhouette.Region.Torso;
        }

        private static void Legend(Rect r, Color c, string text)
        {
            Widgets.DrawBoxSolid(new Rect(r.x + 4f, r.y + 5f, 9f, 9f), c);
            Look.Text(new Rect(r.x + 20f, r.y, r.width - 20f, r.height), text, Muted, GameFont.Tiny);
        }

        private static IEnumerable<Hediff> Visible(Pawn pawn)
        {
            HediffSet hs = pawn.health.hediffSet;
            if (showAll)
            {
                foreach (Hediff h in hs.hediffs) yield return h;
                yield break;
            }
            foreach (Hediff_MissingPart m in hs.GetMissingPartsCommonAncestors()) yield return m;
            foreach (Hediff h in hs.hediffs)
                if (!(h is Hediff_MissingPart) && h.Visible) yield return h;
        }

        private static List<Mark> Marks(Pawn pawn)
        {
            var map = new Dictionary<string, Mark>();
            foreach (Hediff h in Visible(pawn))
            {
                if (h.Part == null) continue;
                Vector2 pos = Where(pawn, h.Part, out string key);
                if (!map.TryGetValue(key, out Mark m))
                    m = new Mark { key = key, pos = pos, color = Look.Blue, list = new List<Hediff>() };
                m.list.Add(h);
                Color c = Severity(pawn, h);
                if (Rank(c) > Rank(m.color)) m.color = c;
                if (h.Bleeding) m.bleeding = true;
                map[key] = m;
            }
            return map.Values.ToList();
        }

        private static int Rank(Color c) => c == Look.Red ? 3 : c == Look.Orange ? 2 : 1;

        // red: bleeding, missing or badly damaged part, a disease you are losing; orange: hurt; blue: the rest (scars, implants …)
        private static Color Severity(Pawn pawn, Hediff h)
        {
            if (h.Bleeding || h is Hediff_MissingPart) return Look.Red;
            if (h.Part != null)
            {
                float max = h.Part.def.GetMaxHealth(pawn);
                float hp = pawn.health.hediffSet.GetPartHealth(h.Part);
                if (max > 0f && hp / max < 0.5f) return Look.Red;
                if (h is Hediff_Injury && !h.IsPermanent()) return Look.Orange;
            }
            if (h.TryGetComp<HediffComp_Immunizable>() != null && h.Severity > Immunity(h)) return Look.Red;
            if (h.TendableNow()) return Look.Orange;
            return Look.Blue;
        }

        // where a body part is on the figure (pawn faces us: his left is on our right)
        private static readonly Dictionary<string, Vector2> Spots = new Dictionary<string, Vector2>
        {
            { "Head", new Vector2(0.5f, 0.24f) }, { "Skull", new Vector2(0.5f, 0.2f) }, { "Brain", new Vector2(0.5f, 0.19f) },
            { "Eye", new Vector2(0.44f, 0.25f) }, { "Ear", new Vector2(0.36f, 0.25f) }, { "Nose", new Vector2(0.5f, 0.28f) },
            { "Jaw", new Vector2(0.5f, 0.31f) }, { "Neck", new Vector2(0.5f, 0.37f) },
            { "Torso", new Vector2(0.5f, 0.52f) }, { "Heart", new Vector2(0.55f, 0.47f) }, { "Lung", new Vector2(0.44f, 0.47f) },
            { "Liver", new Vector2(0.45f, 0.56f) }, { "Stomach", new Vector2(0.55f, 0.56f) }, { "Kidney", new Vector2(0.45f, 0.6f) },
            { "Spine", new Vector2(0.5f, 0.55f) }, { "Ribcage", new Vector2(0.5f, 0.48f) }, { "Sternum", new Vector2(0.5f, 0.46f) },
            { "Pelvis", new Vector2(0.5f, 0.65f) }, { "Waist", new Vector2(0.5f, 0.64f) },
            { "Shoulder", new Vector2(0.32f, 0.43f) }, { "Clavicle", new Vector2(0.4f, 0.42f) }, { "Arm", new Vector2(0.28f, 0.52f) },
            { "Humerus", new Vector2(0.28f, 0.49f) }, { "Radius", new Vector2(0.27f, 0.57f) }, { "Hand", new Vector2(0.26f, 0.65f) },
            { "Finger", new Vector2(0.25f, 0.69f) },
            { "Leg", new Vector2(0.43f, 0.78f) }, { "Femur", new Vector2(0.43f, 0.74f) }, { "Tibia", new Vector2(0.43f, 0.83f) },
            { "Foot", new Vector2(0.43f, 0.92f) }, { "Toe", new Vector2(0.42f, 0.95f) }
        };

        private static Vector2 Where(Pawn pawn, BodyPartRecord part, out string key)
        {
            // the nearest part (it or a parent) we know a spot for
            BodyPartRecord p = part;
            Vector2 pos = new Vector2(-1f, -1f);
            while (p != null)
            {
                if (Spots.TryGetValue(p.def.defName, out Vector2 v)) { pos = v; break; }
                p = p.parent;
            }
            if (pos.x < 0f)
            {
                // unknown body (animals, other races): by height on the body
                pos = new Vector2(0.5f, part.height == BodyPartHeight.Top ? 0.3f : part.height == BodyPartHeight.Bottom ? 0.8f : 0.52f);
            }
            // left or right: the first of a pair is the pawn's left (our right)
            int side = Side(part);
            if (side != 0 && Mathf.Abs(pos.x - 0.5f) > 0.01f)
            {
                float d = Mathf.Abs(pos.x - 0.5f);
                pos.x = side < 0 ? 0.5f + d : 0.5f - d;
            }
            else if (side != 0)
                pos.x = 0.5f + (side < 0 ? 0.06f : -0.06f);
            key = (p ?? part).def.defName + ":" + side + ":" + (p == null ? part.height.ToString() : "");
            return pos;
        }

        // -1 = left, 1 = right, 0 = middle
        private static int Side(BodyPartRecord part)
        {
            for (BodyPartRecord p = part; p != null && p.parent != null; p = p.parent)
            {
                List<BodyPartRecord> sib = p.parent.parts.Where(x => x.def == p.def).ToList();
                if (sib.Count == 2) return sib.IndexOf(p) == 0 ? -1 : 1;
            }
            return 0;
        }

        // ---------- right: conditions by body part ----------
        private static void Conditions(Rect r, Pawn pawn)
        {
            Look.Text(new Rect(r.x, r.y, r.width - 140f, 20f), region == null ? "MT_H_Conditions".Translate() : "MT_H_ConditionsHere".Translate(), Muted, GameFont.Tiny);
            if (Look.Chip(new Rect(r.xMax - 130f, r.y, 130f, 20f), "MT_H_ShowAll".Translate(), showAll)) showAll = !showAll;
            var groups = Visible(pawn).GroupBy(h => h.Part)
                .OrderByDescending(g => g.Key == null ? 9999999f : (int)g.Key.height * 10000 + g.Key.coverageAbsWithChildren).ToList();
            if (region != null)
                groups = groups.Where(g => g.Key != null && Key(pawn, g.Key) == region).ToList();

            Rect outR = new Rect(r.x, r.y + 26f, r.width, r.height - 26f);
            float h = 0f;
            foreach (var g in groups) h += 30f + g.Count() * 26f + 8f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(h, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref listScroll, view);
            float y = 0f;
            foreach (var g in groups)
            {
                float gh = 30f + g.Count() * 26f;
                Rect card = new Rect(0f, y, view.width, gh);
                Look.Box(card, Look.Card, Look.Line);
                // part name and its condition
                string part = g.Key == null ? "WholeBody".Translate().ToString() : g.Key.LabelCap;
                Look.Text(new Rect(card.x + 8f, card.y + 4f, card.width - 120f, 22f), part, Color.white, GameFont.Small);
                if (g.Key != null)
                {
                    Pair<string, Color> cond = HealthUtility.GetPartConditionLabel(pawn, g.Key);
                    Look.Text(new Rect(card.xMax - 116f, card.y + 4f, 108f, 22f), cond.First, cond.Second, GameFont.Tiny, TextAnchor.MiddleRight);
                    float max = g.Key.def.GetMaxHealth(pawn);
                    if (max > 0f) Look.Bar(new Rect(card.x + 8f, card.y + 26f, card.width - 16f, 2f), pawn.health.hediffSet.GetPartHealth(g.Key) / max, cond.Second);
                }
                float ry = card.y + 30f;
                foreach (Hediff d in g.OrderByDescending(x => x.Bleeding).ThenByDescending(x => x.Severity))
                {
                    HediffRow(new Rect(card.x + 8f, ry, card.width - 16f, 24f), pawn, d);
                    ry += 26f;
                }
                y += gh + 8f;
            }
            if (groups.Count == 0)
                Look.Text(new Rect(0f, 0f, view.width, 60f), "MT_H_NoConditions".Translate(), Look.Green, GameFont.Small, TextAnchor.MiddleCenter);
            Widgets.EndScrollView();
        }

        private static string Key(Pawn pawn, BodyPartRecord part)
        {
            Where(pawn, part, out string key);
            return key;
        }

        private static void HediffRow(Rect r, Pawn pawn, Hediff d)
        {
            if (Mouse.IsOver(r)) Widgets.DrawBoxSolid(r, Look.Card2);
            Color c = Severity(pawn, d);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y + 8f, 6f, 8f), c);
            float right = 0f;
            // chips: bleeding, tended / needs tending
            var chips = new List<(string, Color)>();
            if (d.Bleeding) chips.Add(("MT_H_ChipBleed".Translate(d.BleedRate.ToStringPercent()), Look.Red));
            if (d.IsTended()) chips.Add(("MT_H_ChipTended".Translate(), Look.Green));
            else if (d.TendableNow()) chips.Add(("MT_H_ChipTend".Translate(), Look.Orange));
            if (d.IsPermanent()) chips.Add(("MT_H_ChipScar".Translate(), Muted));
            foreach (var (text, col) in chips)
            {
                float w = Verse.Text.CalcSize(text).x * 0.8f + 14f;
                right += w + 4f;
                Rect cr = new Rect(r.xMax - right, r.y + 3f, w, 18f);
                Look.Box(cr, Look.Card2, col);
                Look.Text(cr, text, col, GameFont.Tiny, TextAnchor.MiddleCenter);
            }
            // a disease: immunity against the disease
            HediffComp_Immunizable imm = d.TryGetComp<HediffComp_Immunizable>();
            float textW = r.width - right - 16f;
            if (imm != null && !d.FullyImmune())
            {
                float bw = 70f;
                right += bw + 6f;
                Rect br = new Rect(r.xMax - right, r.y + 5f, bw, 14f);
                Look.Bar(new Rect(br.x, br.y, br.width, 5f), Immunity(d), Look.Green);
                Look.Bar(new Rect(br.x, br.y + 8f, br.width, 5f), d.Severity, Look.Red);
                TooltipHandler.TipRegion(br, "MT_H_ImmunityTip".Translate(Immunity(d).ToStringPercent(), d.Severity.ToStringPercent()));
                textW = r.width - right - 16f;
            }
            string label = d.LabelCap;
            Color lc = d.LabelColor;
            if (lc.r > 0.95f && lc.g > 0.95f && lc.b > 0.95f) lc = Color.white;
            Look.Text(new Rect(r.x + 12f, r.y, textW, r.height), label.Truncate(textW), lc, GameFont.Small);
            if (Mouse.IsOver(r))
            {
                Hediff dd = d;
                TooltipHandler.TipRegion(r, new TipSignal(() => dd.GetTooltip(pawn, false), dd.loadID ^ 0x3A1));
            }
        }
    }
}
