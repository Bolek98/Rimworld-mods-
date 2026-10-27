using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.4.0 Operations in our own look: the planned operations on the left (order, pause,
    // remove, what is missing, who will operate), the silhouette in the middle, and every
    // operation that can be done on the right – searchable, by kind and by body region.
    // Adding goes through the game's own code (confirmations, violations, xenogerms stay).
    // ---------------------------------------------------------------------------
    public static class OpsTab
    {
        private enum Kind { All, Implant, Remove, Drug, Other }
        private enum Area { All, Head, Torso, Arms, Legs, Whole }

        private sealed class Op
        {
            public RecipeDef recipe;
            public BodyPartRecord part;
            public FloatMenuOption option;
            public string label, reason;
            public List<ThingDef> missing;      // 0.4.2: kept for the right-click menu on the figure
            public AcceptanceReport report;
            public Kind kind;
            public Area area;
        }

        private static readonly MethodInfo generate = AccessTools.Method(typeof(HealthCardUtility), "GenerateSurgeryOption");
        private static List<Op> ops = new List<Op>();
        private static int opsPawn = -1, opsBills = -1;
        private static float opsAt = -10f;
        private static string search = "";
        private static Kind kind = Kind.All;
        private static Area area = Area.All;
        private static Vector2 queueScroll, listScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        public static bool Available => generate != null && generate.GetParameters().Length == 7;

        public static void Draw(Rect body, Pawn pawn)
        {
            Rect left = new Rect(body.x, body.y, 290f, body.height);
            Rect mid = new Rect(left.xMax, body.y, 210f, body.height);
            Rect right = new Rect(mid.xMax, body.y, body.width - left.width - mid.width, body.height);
            Widgets.DrawBoxSolid(new Rect(left.xMax, body.y, 1f, body.height), Look.Line);
            Widgets.DrawBoxSolid(new Rect(mid.xMax, body.y, 1f, body.height), Look.Line);
            Queue(left.ContractedBy(10f), pawn);
            HealthTab.FigureForOps(mid.ContractedBy(6f), pawn);
            List(right.ContractedBy(10f), pawn);
        }

        // ---------- left: what is planned ----------
        private static void Queue(Rect r, Pawn pawn)
        {
            Look.Text(new Rect(r.x, r.y, r.width, 22f), "MT_O_Planned".Translate(), Muted, GameFont.Tiny);
            float y = r.y + 24f;
            List<Bill> bills = pawn.BillStack?.Bills ?? new List<Bill>();
            Rect doc = new Rect(r.x, r.yMax - 92f, r.width, 92f);
            Rect outR = new Rect(r.x, y, r.width, doc.y - y - 8f);
            if (bills.Count == 0)
            {
                Look.Text(new Rect(outR.x, outR.y, outR.width, 60f), "MT_O_NonePlanned".Translate(), Muted, GameFont.Small, TextAnchor.UpperLeft);
            }
            else
            {
                const float rowH = 62f;
                Rect view = new Rect(0f, 0f, outR.width - 16f, bills.Count * rowH);
                Widgets.BeginScrollView(outR, ref queueScroll, view);
                Bill del = null;
                Bill up = null, down = null;
                try
                {
                    for (int i = 0; i < bills.Count; i++)
                    {
                        Bill b = bills[i];
                        Rect row = new Rect(0f, i * rowH, view.width, rowH - 6f);
                        Look.Box(row, b.suspended ? Look.Bg : Look.Card, Look.Line);
                        Look.Icon(new Rect(row.x + 6f, row.y + 6f, 30f, 30f), (Def)b.recipe.UIIconThing ?? b.recipe, b.recipe.UIIcon);
                        Look.Text(new Rect(row.x + 42f, row.y + 2f, row.width - 110f, 20f), (i + 1) + ". " + b.LabelCap.ToString().Truncate(row.width - 120f), b.suspended ? Muted : Color.white);
                        string part = (b as Bill_Medical)?.Part?.LabelCap ?? "MT_O_WholeBody".Translate().ToString();
                        var missing = Missing(b.recipe, pawn);
                        string sub = b.suspended ? "MT_O_Paused".Translate().ToString()
                                   : missing.Count > 0 ? "MT_O_Missing".Translate(string.Join(", ", missing.Select(m => m.label).ToArray())).ToString()
                                   : "MT_O_Ready".Translate().ToString();
                        Look.Text(new Rect(row.x + 42f, row.y + 20f, row.width - 110f, 16f), part.Truncate(row.width - 120f), Muted, GameFont.Tiny);
                        Look.Text(new Rect(row.x + 42f, row.y + 36f, row.width - 110f, 16f), sub.Truncate(row.width - 120f), b.suspended ? Muted : missing.Count > 0 ? Look.Orange : Look.Green, GameFont.Tiny);
                        // buttons: up, down, pause, remove
                        float bx = row.xMax - 66f;
                        if (Small(new Rect(bx, row.y + 6f, 20f, 20f), "▲", i > 0)) up = b;
                        if (Small(new Rect(bx + 22f, row.y + 6f, 20f, 20f), "▼", i < bills.Count - 1)) down = b;
                        if (Small(new Rect(bx + 44f, row.y + 6f, 20f, 20f), "✕", true)) del = b;
                        Rect pr = new Rect(bx, row.y + 30f, 64f, 20f);
                        if (Small(pr, b.suspended ? "MT_O_Resume".Translate() : "MT_O_Pause".Translate(), true)) b.suspended = !b.suspended;
                        TooltipHandler.TipRegion(new Rect(row.x, row.y, row.width - 70f, row.height), b.recipe.description ?? b.LabelCap.ToString());
                    }
                }
                finally { Widgets.EndScrollView(); }   // 0.4.2
                if (up != null) { pawn.BillStack.Reorder(up, -1); SoundDefOf.Tick_High.PlayOneShotOnCamera(); }
                if (down != null) { pawn.BillStack.Reorder(down, 1); SoundDefOf.Tick_Low.PlayOneShotOnCamera(); }
                if (del != null) { pawn.BillStack.Delete(del); SoundDefOf.Click.PlayOneShotOnCamera(); opsAt = -10f; }
            }

            // who will operate
            Look.Box(doc, Look.Card, Look.Line);
            Look.Text(new Rect(doc.x + 8f, doc.y + 4f, doc.width - 16f, 18f), "MT_O_Doctor".Translate(), Muted, GameFont.Tiny);
            Pawn best = BestDoctor(pawn, out int skill);
            Look.Text(new Rect(doc.x + 8f, doc.y + 22f, doc.width - 16f, 22f),
                best == null ? "MT_O_NoDoctor".Translate().ToString() : "MT_O_DoctorIs".Translate(best.LabelShortCap, skill).ToString(),
                best == null ? Look.Red : skill < 6 ? Look.Orange : Look.Green);
            string care = pawn.playerSettings != null ? "MT_O_Care".Translate(pawn.playerSettings.medCare.GetLabel()).ToString() : "";
            Look.Text(new Rect(doc.x + 8f, doc.y + 46f, doc.width - 16f, 18f), care, Muted, GameFont.Tiny);
            Look.Text(new Rect(doc.x + 8f, doc.y + 64f, doc.width - 16f, 18f), "MT_O_DoctorTip".Translate(), Muted, GameFont.Tiny);
        }

        private static bool Small(Rect r, string label, bool enabled)
        {
            bool over = enabled && Mouse.IsOver(r);
            Widgets.DrawBoxSolid(r, over ? Look.Card2 : Look.Bg);
            Look.Text(r, label, enabled ? (over ? Color.white : Muted) : Look.Line, GameFont.Tiny, TextAnchor.MiddleCenter);
            return enabled && Widgets.ButtonInvisible(r);
        }

        private static List<ThingDef> Missing(RecipeDef recipe, Pawn pawn)
        {
            try { return recipe.PotentiallyMissingIngredients(null, pawn.MapHeld).ToList(); }
            catch { return new List<ThingDef>(); }
        }

        private static Pawn BestDoctor(Pawn patient, out int skill)
        {
            skill = 0;
            Map map = patient.MapHeld;
            if (map == null) return null;
            Pawn best = null;
            foreach (Pawn d in map.mapPawns.FreeColonistsSpawned)
            {
                if (d == patient || d.Downed || d.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) || d.workSettings == null || !d.workSettings.WorkIsActive(WorkTypeDefOf.Doctor)) continue;
                int s = d.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0;
                if (best == null || s > skill) { best = d; skill = s; }
            }
            return best;
        }

        // ---------- right: what can be done ----------
        private static void Refresh(Pawn pawn)
        {
            int bills = pawn.BillStack?.Count ?? 0;
            if (opsPawn == pawn.thingIDNumber && opsBills == bills && Time.realtimeSinceStartup - opsAt < 3f) return;
            opsPawn = pawn.thingIDNumber;
            opsBills = bills;
            opsAt = Time.realtimeSinceStartup;
            var list = new List<Op>();
            int index = 0;
            try
            {
                foreach (RecipeDef rec in pawn.def.AllRecipes)
                {
                    if (!rec.AvailableNow) continue;
                    AcceptanceReport report = rec.Worker.AvailableReport(pawn);
                    if (!report.Accepted && report.Reason.NullOrEmpty()) continue;
                    List<ThingDef> missing = rec.PotentiallyMissingIngredients(null, pawn.MapHeld).ToList();
                    if (missing.Any(x => x.isTechHediff) || missing.Any(x => x.IsDrug) || (missing.Any() && rec.dontShowIfAnyIngredientMissing)) continue;
                    if (rec.targetsBodyPart)
                    {
                        foreach (BodyPartRecord part in rec.Worker.GetPartsToApplyOn(pawn, rec))
                            if (rec.AvailableOnNow(pawn, part))
                                list.Add(Make(pawn, rec, missing, report, index++, part));
                    }
                    else if (!pawn.health.hediffSet.HasHediff(rec.addsHediff))
                        list.Add(Make(pawn, rec, missing, report, index++, null));
                }
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] operations: " + e.Message, 0x51A7B13); }
            ops = list.Where(o => o != null).ToList();
        }

        private static Op Make(Pawn pawn, RecipeDef rec, List<ThingDef> missing, AcceptanceReport report, int index, BodyPartRecord part)
        {
            var opt = (FloatMenuOption)generate.Invoke(null, new object[] { pawn, pawn, rec, missing, report, index, part });
            var o = new Op { recipe = rec, part = part, option = opt, label = rec.Worker.GetLabelWhenUsedOn(pawn, part).CapitalizeFirst(), missing = missing, report = report };
            if (!report.Reason.NullOrEmpty()) o.reason = report.Reason;
            else if (missing.Count > 0) o.reason = "MT_O_Missing".Translate(string.Join(", ", missing.Select(m => m.label).ToArray()));
            o.kind = KindOf(rec);
            o.area = part == null ? Area.Whole : AreaOf(HealthTab.RegionOfPart(part));
            return o;
        }

        private static Kind KindOf(RecipeDef r)
        {
            RecipeWorker w = r.Worker;
            if (w is Recipe_AdministerIngestible) return Kind.Drug;
            if (w is Recipe_RemoveBodyPart || w is Recipe_RemoveImplant || w is Recipe_RemoveHediff) return Kind.Remove;
            if (w is Recipe_InstallImplant || w is Recipe_InstallArtificialBodyPart || w is Recipe_InstallNaturalBodyPart
                || (r.addsHediff != null && (r.addsHediff.addedPartProps != null || r.addsHediff.spawnThingOnRemoved != null))) return Kind.Implant;
            return Kind.Other;
        }

        private static Area AreaOf(Silhouette.Region r)
        {
            switch (r)
            {
                case Silhouette.Region.Head: return Area.Head;
                case Silhouette.Region.ArmL:
                case Silhouette.Region.ArmR: return Area.Arms;
                case Silhouette.Region.LegL:
                case Silhouette.Region.LegR: return Area.Legs;
                default: return Area.Torso;
            }
        }

        // 0.4.2: switch to the operations view filtered to the body region of this part
        public static void ShowFor(BodyPartRecord part)
        {
            area = part == null ? Area.Whole : AreaOf(HealthTab.RegionOfPart(part));
            kind = Kind.All;
            search = "";
            listScroll = Vector2.zero;
            HealthTab.opsMode = true;
        }

        // 0.4.2: the operations that can be done on these body parts, as fresh menu options
        // (the game's own GenerateSurgeryOption, like the list on the right)
        public static List<FloatMenuOption> OptionsFor(Pawn pawn, ICollection<BodyPartRecord> parts)
        {
            var res = new List<FloatMenuOption>();
            if (!Available || parts == null || parts.Count == 0) return res;
            try
            {
                Refresh(pawn);
                int index = 0;
                foreach (Op o in ops.Where(x => x.part != null && parts.Contains(x.part)).OrderBy(x => x.reason != null).ThenBy(x => x.label).ThenBy(x => x.part.Label))
                {
                    var opt = (FloatMenuOption)generate.Invoke(null, new object[] { pawn, pawn, o.recipe, o.missing ?? new List<ThingDef>(), o.report, index++, o.part });
                    if (opt == null) continue;
                    Action act = opt.action;
                    if (act != null) opt.action = () => { act(); opsAt = -10f; };
                    res.Add(opt);
                }
            }
            catch (Exception e) { Log.WarningOnce("[Mišák Tabs] operations menu: " + e.Message, 0x51A7B14); }
            return res;
        }

        private static void List(Rect r, Pawn pawn)
        {
            Refresh(pawn);
            Look.Text(new Rect(r.x, r.y, r.width, 22f), "MT_O_Possible".Translate(ops.Count), Muted, GameFont.Tiny);
            float y = r.y + 24f;
            Look.EscUnfocus("MT_OpsSearch");          // 0.4.2
            GUI.SetNextControlName("MT_OpsSearch");
            search = Widgets.TextField(new Rect(r.x, y, r.width, 26f), search ?? "");
            if (search.NullOrEmpty())
                Look.Text(new Rect(r.x + 6f, y, r.width, 26f), "MT_O_Search".Translate(), Look.Line, GameFont.Small);
            y += 32f;
            float cx = r.x;
            foreach (Kind k in Enum.GetValues(typeof(Kind)))
            {
                string lab = ("MT_O_K_" + k).Translate();
                float w = Text.CalcSize(lab).x + 18f;
                if (cx + w > r.xMax && cx > r.x) { cx = r.x; y += 26f; }     // wrap to the next line
                if (Look.Chip(new Rect(cx, y, w, 22f), lab, kind == k)) kind = k;
                cx += w + 5f;
            }
            y += 28f;
            cx = r.x;
            foreach (Area a in Enum.GetValues(typeof(Area)))
            {
                string lab = ("MT_O_A_" + a).Translate();
                float w = Text.CalcSize(lab).x + 18f;
                if (cx + w > r.xMax && cx > r.x) { cx = r.x; y += 26f; }     // wrap to the next line
                if (Look.Chip(new Rect(cx, y, w, 22f), lab, area == a)) area = a;
                cx += w + 5f;
            }
            y += 30f;

            string q = (search ?? "").Trim();
            var shown = ops.Where(o => (kind == Kind.All || o.kind == kind) && (area == Area.All || o.area == area)
                                     && (q.Length == 0 || o.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                                         || (o.part != null && o.part.Label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)))
                           .OrderBy(o => o.reason != null).ThenBy(o => o.label).ThenBy(o => o.part?.Label).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            if (shown.Count == 0)
            {
                Look.Text(new Rect(outR.x, outR.y, outR.width, 40f), "MT_O_NoneFound".Translate(), Muted, GameFont.Small, TextAnchor.UpperLeft);
                return;
            }
            const float rowH = 44f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, shown.Count * rowH);
            Widgets.BeginScrollView(outR, ref listScroll, view);
            Op clicked = null;
            try
            {
                int first = Mathf.Max(0, (int)(listScroll.y / rowH) - 1), last = Mathf.Min(shown.Count - 1, (int)((listScroll.y + outR.height) / rowH) + 1);
                for (int i = first; i <= last; i++)
                {
                    Op o = shown[i];
                    bool ok = o.reason == null && o.option?.action != null && !o.option.Disabled;
                    Rect row = new Rect(0f, i * rowH, view.width, rowH - 4f);
                    bool over = Mouse.IsOver(row);
                    Look.Box(row, over && ok ? Look.Card2 : Look.Card);
                    Def icon = o.recipe.Worker is Recipe_RemoveBodyPart && o.part?.def.spawnThingOnRemoved != null ? (Def)o.part.def.spawnThingOnRemoved : (Def)o.recipe.UIIconThing ?? o.recipe;
                    Look.Icon(new Rect(row.x + 6f, row.y + 5f, 30f, 30f), icon, o.recipe.UIIcon);
                    float tw = row.width - 42f - 70f;
                    Look.Text(new Rect(row.x + 42f, row.y + 2f, tw, 20f), o.label.Truncate(tw), ok ? Color.white : Muted);
                    string sub = (o.part != null && !o.recipe.hideBodyPartNames ? o.part.LabelCap.ToString() : "MT_O_WholeBody".Translate().ToString())
                                 + (o.reason != null ? " · " + o.reason : "");
                    Look.Text(new Rect(row.x + 42f, row.y + 21f, tw, 16f), sub.Truncate(tw), o.reason != null ? Look.Orange : Muted, GameFont.Tiny);
                    Look.Text(new Rect(row.xMax - 66f, row.y, 60f, row.height), ok ? "MT_Add".Translate() : "", Look.Green, GameFont.Tiny, TextAnchor.MiddleRight);
                    if (over) TooltipHandler.TipRegion(row, new TipSignal(() => o.recipe.description.NullOrEmpty() ? o.label : o.label + "\n\n" + o.recipe.description, o.recipe.GetHashCode() ^ (o.part?.Index ?? -1)));
                    if (ok && Widgets.ButtonInvisible(row)) clicked = o;
                }
            }
            finally { Widgets.EndScrollView(); }   // 0.4.2
            if (clicked != null)
            {
                try { clicked.option.action(); } catch (Exception e) { Log.Warning("[Mišák Tabs] add operation: " + e.Message); }
                SoundDefOf.Click.PlayOneShotOnCamera();
                opsAt = -10f;
            }
        }
    }
}
