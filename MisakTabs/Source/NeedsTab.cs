using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.11.0 Needs tab in the Mišák look.
    //  Left: every need (also those of mods) as a card – name, value, trend arrow, bar with the
    //  game's threshold marks; the game's tooltip.
    //  Right: mood – a big bar with the break thresholds, current and target mood, what the pawn
    //  expects, the sums of good and bad thoughts and the thoughts themselves (×N when stacked,
    //  tooltip with the description and how long it lasts; long names are never cut off without
    //  the full name in the tooltip).
    //  The original tab is one click away.
    // ---------------------------------------------------------------------------
    public static class NeedsTab
    {
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        private static readonly AccessTools.FieldRef<Need, List<float>> threshRef = AccessTools.FieldRefAccess<Need, List<float>>("threshPercents");
        private static readonly Color Muted = new Color32(160, 167, 174, 255);
        private static readonly Color Yellow = new Color32(230, 210, 90, 255);
        public static bool original;
        private static Vector2 needScroll, thoughtScroll;
        private static readonly List<Need> needs = new List<Need>();
        private static readonly List<Thought> groups = new List<Thought>();
        private static readonly List<Thought> tmp = new List<Thought>();

        public static Vector2 Size => TabSize.Fit(MTMod.S.needsW, MTMod.S.needsH, 520f, 380f);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Needs), "FillTab"),
                    prefix: new HarmonyMethod(typeof(NeedsTab), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(NeedsTab), nameof(Postfix)));
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Needs), "UpdateSize"),
                    postfix: new HarmonyMethod(typeof(NeedsTab), nameof(SizePostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] needs tab not available: " + e.Message); }
        }

        private static Pawn SelPawn => Find.Selector.SingleSelectedThing as Pawn ?? (Find.Selector.SingleSelectedThing as Corpse)?.InnerPawn;

        private static bool Ours(Pawn p) => MTMod.S.needs && !original && p?.needs != null && p.needs.AllNeeds.Count > 0;

        public static void SizePostfix(ITab_Pawn_Needs __instance)
        {
            if (Ours(SelPawn)) sizeRef(__instance) = Size;
        }

        public static bool Prefix(ITab_Pawn_Needs __instance)
        {
            Pawn pawn = SelPawn;
            if (!Ours(pawn)) return true;
            sizeRef(__instance) = Size;
            try { Draw(new Rect(0f, 0f, Size.x, Size.y), pawn); }
            catch (Exception e)
            {
                Look.ResetGui();
                Log.ErrorOnce("[Mišák Tabs] needs tab: " + e, 0x51A7B23);
                original = true;
            }
            return false;
        }

        public static void Postfix(ITab_Pawn_Needs __instance)
        {
            if (!MTMod.S.needs || !original) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 126f, sz.y - 30f, 116f, 24f), "MT_OurLook".Translate(), true)) original = false;
        }

        public static void Draw(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            PawnTabs.Header(head, pawn, ref original, "needs", ref MTMod.S.needsW, ref MTMod.S.needsH, 520f, 380f);
            Rect body = new Rect(r.x, head.yMax, r.width, r.height - head.height);
            bool mood = pawn.needs.mood != null;
            float lw = mood ? Mathf.Clamp(body.width * 0.4f, 230f, 330f) : body.width - 20f;
            Rect left = new Rect(body.x + 10f, body.y + 10f, lw, body.height - 20f);
            Needs(left, pawn);
            if (mood)
                Mood(new Rect(left.xMax + 14f, body.y + 10f, body.xMax - left.xMax - 24f, body.height - 20f), pawn);
        }

        // ---------- needs ----------
        private static Color LevelColor(float p) => p < 0.2f ? Look.Red : p < 0.4f ? Look.Orange : p < 0.7f ? Yellow : Look.Green;

        private static void Needs(Rect r, Pawn pawn)
        {
            needs.Clear();
            foreach (Need n in pawn.needs.AllNeeds)
                if (n != null && !(n is Need_Mood) && n.ShowOnNeedList) needs.Add(n);
            try { PawnNeedsUIUtility.SortInDisplayOrder(needs); } catch { }
            float h = 0f;
            foreach (Need n in needs) h += (n.def.major ? 46f : 38f) + 4f;
            Rect view = new Rect(0f, 0f, r.width - (h > r.height ? 16f : 0f), h);
            Widgets.BeginScrollView(r, ref needScroll, view);
            try
            {
                float y = 0f;
                foreach (Need n in needs)
                {
                    float rh = n.def.major ? 46f : 38f;
                    Row(new Rect(0f, y, view.width, rh), n);
                    y += rh + 4f;
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void Row(Rect r, Need n)
        {
            float p;
            try { p = Mathf.Clamp01(n.CurLevelPercentage); } catch { p = 0f; }
            Color c = LevelColor(p);
            Look.Box(r, Mouse.IsOver(r) ? Look.Card2 : Look.Card, Look.Line);
            Widgets.DrawBoxSolid(new Rect(r.x, r.y, 4f, r.height), c);
            GameFont f = n.def.major ? GameFont.Small : GameFont.Tiny;
            float th = n.def.major ? 22f : 18f;
            Look.Text(new Rect(r.x + 12f, r.y + 3f, r.width - 90f, th), n.LabelCap.ToString().Truncate(r.width - 90f), Color.white, f);
            int arrow = 0;
            try { arrow = n.GUIChangeArrow; } catch { }
            if (arrow != 0)
                Look.Text(new Rect(r.xMax - 76f, r.y + 3f, 18f, th), arrow > 0 ? "▲" : "▼", arrow > 0 ? Look.Green : Look.Red, GameFont.Tiny, TextAnchor.MiddleCenter);
            Look.Text(new Rect(r.xMax - 58f, r.y + 3f, 50f, th), p.ToStringPercent(), c, f, TextAnchor.MiddleRight);
            Rect bar = new Rect(r.x + 12f, r.yMax - (n.def.major ? 15f : 13f), r.width - 22f, n.def.major ? 8f : 6f);
            Look.Bar(bar, p, c);
            List<float> th2 = null;
            try { th2 = threshRef(n); } catch { }
            if (th2 != null)
                foreach (float t in th2)
                    if (t > 0f && t < 1f)
                        Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * t - 1f, bar.y - 2f, 2f, bar.height + 4f), new Color(0f, 0f, 0f, 0.75f));
            if (Mouse.IsOver(r))
                TooltipHandler.TipRegion(r, new TipSignal(() => { try { return n.GetTipString(); } catch { return n.LabelCap; } }, n.GetHashCode() ^ 0x3E3D));
        }

        // ---------- mood ----------
        private static void Mood(Rect r, Pawn pawn)
        {
            Need_Mood m = pawn.needs.mood;
            Look.Box(r, Look.Card, Look.Line);
            Rect inner = r.ContractedBy(10f);
            float y = inner.y;
            float cur = m.CurLevelPercentage;
            Color mc = MoodColor(pawn, cur);
            Look.Text(new Rect(inner.x, y, inner.width - 90f, 26f), "MT_N_Mood".Translate() + ": " + m.MoodString.CapitalizeFirst(), Color.white, GameFont.Medium);
            Look.Text(new Rect(inner.xMax - 90f, y, 90f, 26f), cur.ToStringPercent(), mc, GameFont.Medium, TextAnchor.MiddleRight);
            y += 32f;
            // bar with the break thresholds
            Rect bar = new Rect(inner.x, y, inner.width, 18f);
            Look.Bar(bar, cur, mc);
            void Mark(float t, Color c, string tip)
            {
                if (t <= 0f || t >= 1f) return;
                Rect mr = new Rect(bar.x + bar.width * t - 1.5f, bar.y - 3f, 3f, bar.height + 6f);
                Widgets.DrawBoxSolid(mr, c);
                TooltipHandler.TipRegion(new Rect(mr.x - 4f, mr.y, 11f, mr.height), tip);
            }
            try
            {
                var mb = pawn.mindState.mentalBreaker;
                Mark(mb.BreakThresholdExtreme, Look.Red, "MT_N_BreakExtreme".Translate(mb.BreakThresholdExtreme.ToStringPercent()));
                Mark(mb.BreakThresholdMajor, Look.Orange, "MT_N_BreakMajor".Translate(mb.BreakThresholdMajor.ToStringPercent()));
                Mark(mb.BreakThresholdMinor, Yellow, "MT_N_BreakMinor".Translate(mb.BreakThresholdMinor.ToStringPercent()));
            }
            catch { }
            // target mood
            float target = Mathf.Clamp01(m.CurInstantLevelPercentage);
            Widgets.DrawBoxSolid(new Rect(bar.x + bar.width * target - 1f, bar.yMax, 2f, 6f), Color.white);
            TooltipHandler.TipRegion(bar, new TipSignal(() => { try { return m.GetTipString(); } catch { return ""; } }, 0x3E3E));
            y += 30f;
            string exp = "";
            try { exp = ExpectationsUtility.CurrentExpectationFor(pawn)?.LabelCap ?? ""; } catch { }
            Look.Text(new Rect(inner.x, y, inner.width * 0.5f, 18f), "MT_N_Target".Translate(target.ToStringPercent()), Muted, GameFont.Tiny);
            if (!exp.NullOrEmpty())
                Look.Text(new Rect(inner.x + inner.width * 0.5f, y, inner.width * 0.5f, 18f), "MT_N_Expect".Translate(exp), Muted, GameFont.Tiny, TextAnchor.MiddleRight);
            y += 22f;
            // thoughts
            groups.Clear();
            try { PawnNeedsUIUtility.GetThoughtGroupsInDisplayOrder(m, groups); } catch { }
            var rows = new List<(Thought group, Thought lead, int count, float val, string tip)>();
            float pos = 0f, neg = 0f;
            foreach (Thought g in groups)
            {
                try
                {
                    m.thoughts.GetMoodThoughts(g, tmp);
                    if (tmp.Count == 0) continue;
                    Thought lead = PawnNeedsUIUtility.GetLeadingThoughtInGroup(tmp);
                    if (lead == null || !lead.VisibleInNeedsTab) continue;
                    float v = m.thoughts.MoodOffsetOfGroup(g);
                    if (v > 0f) pos += v; else neg += v;
                    rows.Add((g, lead, tmp.Count, v, Tip(pawn, g, lead, tmp)));
                }
                catch { }
            }
            Look.Text(new Rect(inner.x, y, inner.width, 20f), "MT_N_Thoughts".Translate(rows.Count), Color.white, GameFont.Small);
            Look.Text(new Rect(inner.xMax - 140f, y, 66f, 20f), "+" + pos.ToString("0"), Look.Green, GameFont.Small, TextAnchor.MiddleRight);
            Look.Text(new Rect(inner.xMax - 66f, y, 66f, 20f), neg.ToString("0"), neg < 0f ? Look.Red : Muted, GameFont.Small, TextAnchor.MiddleRight);
            y += 24f;
            Rect outR = new Rect(inner.x, y, inner.width, inner.yMax - y);
            const float rh = 26f;
            Rect view = new Rect(0f, 0f, outR.width - (rows.Count * rh > outR.height ? 16f : 0f), rows.Count * rh);
            Widgets.BeginScrollView(outR, ref thoughtScroll, view);
            try
            {
                float ty = 0f;
                foreach (var (g, lead, count, v, tip) in rows)
                {
                    Rect row = new Rect(0f, ty, view.width, rh - 3f);
                    Color c = v > 0f ? Look.Green : v < 0f ? Look.Red : Muted;
                    Widgets.DrawBoxSolid(row, Mouse.IsOver(row) ? Look.Card2 : Look.Bg);
                    Widgets.DrawBoxSolid(new Rect(row.x, row.y, 4f, row.height), c);
                    float iconW = 0f;
                    try
                    {
                        if (ModsConfig.IdeologyActive && lead.sourcePrecept != null && !Find.IdeoManager.classicMode)
                        {
                            iconW = 22f;
                            IdeoUIUtility.DoIdeoIcon(new Rect(row.xMax - 62f - iconW, row.y + 1f, 20f, 20f), lead.sourcePrecept.ideo, false, () => IdeoUIUtility.OpenIdeoInfo(lead.sourcePrecept.ideo));
                        }
                    }
                    catch { }
                    string label = lead.LabelCap.ToString() + (count > 1 ? " ×" + count : "");
                    float lw = row.width - 74f - iconW;
                    Look.Text(new Rect(row.x + 12f, row.y, lw, row.height), label.Truncate(lw), Color.white, GameFont.Small);
                    Look.Text(new Rect(row.xMax - 58f, row.y, 52f, row.height), v.ToString("+0;-0;0"), c, GameFont.Small, TextAnchor.MiddleRight);
                    if (Mouse.IsOver(row)) TooltipHandler.TipRegion(row, new TipSignal(tip, g.GetHashCode() ^ 0x3E3F));
                    ty += rh;
                }
                if (rows.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 24f), "MT_N_NoThoughts".Translate(), Muted, GameFont.Tiny);
            }
            finally { Widgets.EndScrollView(); }
        }

        private static Color MoodColor(Pawn pawn, float cur)
        {
            try
            {
                var mb = pawn.mindState.mentalBreaker;
                if (cur < mb.BreakThresholdExtreme) return Look.Red;
                if (cur < mb.BreakThresholdMajor) return Look.Orange;
                if (cur < mb.BreakThresholdMinor) return Yellow;
            }
            catch { }
            return Look.Green;
        }

        // the game's tooltip for a thought group
        private static string Tip(Pawn pawn, Thought group, Thought lead, List<Thought> all)
        {
            var sb = new StringBuilder();
            sb.Append(lead.LabelCap.AsTipTitle()).AppendLine().AppendLine();
            try
            {
                if (pawn.DevelopmentalStage.Baby())
                {
                    sb.AppendLine(lead.BabyTalk).AppendLine();
                    sb.AppendTagged(("Translation".Translate() + ": " + lead.Description).Colorize(ColoredText.SubtleGrayColor));
                }
                else sb.Append(lead.Description);
            }
            catch { }
            try
            {
                int dur = group.DurationTicks;
                if (dur > 5 && lead is Thought_Memory tm)
                {
                    sb.AppendLine().AppendLine();
                    if (all.Count == 1) sb.AppendTagged("ThoughtExpiresIn".Translate((dur - tm.age).ToStringTicksToPeriod()));
                    else
                    {
                        int mn = int.MaxValue, mx = int.MinValue;
                        foreach (Thought t in all)
                            if (t is Thought_Memory mm) { mn = Mathf.Min(mn, mm.age); mx = Mathf.Max(mx, mm.age); }
                        sb.AppendTagged("ThoughtStartsExpiringIn".Translate((dur - mx).ToStringTicksToPeriod())).AppendLine();
                        sb.AppendTagged("ThoughtFinishesExpiringIn".Translate((dur - mn).ToStringTicksToPeriod()));
                    }
                }
            }
            catch { }
            var seen = new HashSet<string> { lead.LabelCap };
            bool first = true;
            foreach (Thought t in all)
            {
                if (t == lead || !seen.Add(t.LabelCap)) continue;
                if (first) { sb.AppendLine().AppendLine(); first = false; }
                sb.AppendLine("+ " + t.LabelCap);
            }
            return sb.ToString();
        }
    }
}
