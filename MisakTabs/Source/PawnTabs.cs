using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.9.0 Character and Social tabs in the Mišák look.
    //  Character: a dossier (portrait, backstory, traits, what they can't do) and three pages –
    //  skills, the life timeline (birth, arrival, the game's tales and our own life log:
    //  breakdowns, lost and new body parts, skill levels, partners) and records.
    //  Social: everyone they know on the left (with both opinions), on the right why – every
    //  reason both ways – or an overview with partner, best friend, rival and the last talks.
    //  The original tab is always one click away.
    // ---------------------------------------------------------------------------
    public static class PawnTabs
    {
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        private static readonly Color Muted = new Color32(160, 167, 174, 255);
        public static bool charOriginal, socialOriginal;

        public static Vector2 CharSize => TabSize.Fit(MTMod.S.charW, MTMod.S.charH, 640f, 420f);
        public static Vector2 SocialSize => TabSize.Fit(MTMod.S.socialW, MTMod.S.socialH, 620f, 420f);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Character), "FillTab"),
                    prefix: new HarmonyMethod(typeof(PawnTabs), nameof(CharPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(PawnTabs), nameof(CharPostfix)));
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Character), "UpdateSize"),
                    postfix: new HarmonyMethod(typeof(PawnTabs), nameof(CharSizePostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] character tab not available: " + e.Message); }
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Social), "FillTab"),
                    prefix: new HarmonyMethod(typeof(PawnTabs), nameof(SocialPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(PawnTabs), nameof(SocialPostfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] social tab not available: " + e.Message); }
            LifeHooks.Patch(h);
        }

        public static bool OursChar(InspectTabBase t) => t is ITab_Pawn_Character && MTMod.S.character && !charOriginal;
        public static bool OursSocial(InspectTabBase t) => t is ITab_Pawn_Social && MTMod.S.social && !socialOriginal;

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

        public static void CharSizePostfix(ITab_Pawn_Character __instance)
        {
            if (OursChar(__instance)) sizeRef(__instance) = CharSize;
        }

        public static bool CharPrefix(ITab_Pawn_Character __instance)
        {
            if (!MTMod.S.character || charOriginal) return true;
            Pawn pawn = SelPawn;
            if (pawn?.story == null) return true;
            sizeRef(__instance) = CharSize;
            try { CharacterTab.Draw(new Rect(0f, 0f, CharSize.x, CharSize.y), pawn); }
            catch (Exception e)
            {
                Look.ResetGui();
                Log.ErrorOnce("[Mišák Tabs] character tab: " + e, 0x51A7B21);
                charOriginal = true;
            }
            return false;
        }

        public static void CharPostfix(ITab_Pawn_Character __instance)
        {
            if (!MTMod.S.character || !charOriginal) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 126f, 4f, 116f, 24f), "MT_OurLook".Translate(), true)) charOriginal = false;
        }

        public static bool SocialPrefix(ITab_Pawn_Social __instance)
        {
            if (!MTMod.S.social || socialOriginal) return true;
            Pawn pawn = SelPawn;
            if (pawn?.relations == null || !pawn.RaceProps.Humanlike) return true;
            sizeRef(__instance) = SocialSize;
            try { SocialTab.Draw(new Rect(0f, 0f, SocialSize.x, SocialSize.y), pawn); }
            catch (Exception e)
            {
                Look.ResetGui();
                Log.ErrorOnce("[Mišák Tabs] social tab: " + e, 0x51A7B22);
                socialOriginal = true;
            }
            return false;
        }

        public static void SocialPostfix(ITab_Pawn_Social __instance)
        {
            if (!MTMod.S.social || !socialOriginal) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 126f, sz.y - 30f, 116f, 24f), "MT_OurLook".Translate(), true)) socialOriginal = false;
        }

        // ---------- shared bits ----------
        public static void Header(Rect r, Pawn pawn, ref bool original, string gripId, ref float w, ref float h, float minW, float minH)
        {
            Look.Box(r, Look.Card);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax - 1f, r.width, 1f), Look.Line);
            Look.Text(new Rect(r.x + 12f, r.y + 6f, r.width - 170f, 26f), pawn.LabelCap, Color.white, GameFont.Medium);
            var bits = new List<string>();
            try
            {
                if (pawn.Name is NameTriple nt && !nt.ToStringFull.NullOrEmpty() && nt.ToStringFull != pawn.LabelCap) bits.Add(nt.ToStringFull);
                bits.Add(pawn.def.LabelCap);
                if (pawn.genes?.Xenotype != null && pawn.genes.Xenotype != XenotypeDefOf.Baseliner) bits.Add(pawn.genes.XenotypeLabelCap);
                if (pawn.ageTracker != null) bits.Add("MT_H_Age".Translate(pawn.ageTracker.AgeBiologicalYears));
                if (pawn.gender != Gender.None) bits.Add(pawn.gender.GetLabel(pawn.AnimalOrWildMan()));   // žena / muž
                string title = pawn.royalty?.MostSeniorTitle?.Label;
                if (!title.NullOrEmpty()) bits.Add(title.CapitalizeFirst());
                if (pawn.Faction != null && pawn.Faction != Faction.OfPlayer) bits.Add(pawn.Faction.Name);
            }
            catch { }
            Look.Text(new Rect(r.x + 12f, r.y + 32f, r.width - 170f, 18f), string.Join(" · ", bits).Truncate(r.width - 170f), Muted, GameFont.Tiny);
            Rect orig = new Rect(r.xMax - 140f, r.y + 13f, 120f, 28f);
            if (Look.Button(orig, "MT_Original".Translate())) original = true;
            TabSize.Grip(new Rect(r.x, r.y, r.width, r.height), gripId, ref w, ref h, minW, minH);
        }

        public static float Chips(Rect r, IEnumerable<(string text, Color c, string tip)> chips)
        {
            float x = r.x, y = r.y;
            foreach (var (text, c, tip) in chips)
            {
                Verse.Text.Font = GameFont.Tiny;
                float w = Mathf.Min(r.width, Verse.Text.CalcSize(text).x + 16f);
                Verse.Text.Font = GameFont.Small;
                if (x + w > r.xMax) { x = r.x; y += 24f; }
                Rect cr = new Rect(x, y, w, 20f);
                Look.Box(cr, Look.Card2, c);
                Look.Text(cr, text.Truncate(w - 4f), c, GameFont.Tiny, TextAnchor.MiddleCenter);
                if (!tip.NullOrEmpty()) TooltipHandler.TipRegion(cr, tip);
                x += w + 5f;
            }
            return y + 24f - r.y;
        }

        public static Vector2 Lon()
        {
            try { return Find.CurrentMap != null ? Find.WorldGrid.LongLatOf(Find.CurrentMap.Tile) : Vector2.zero; }
            catch { return Vector2.zero; }
        }
    }

    // ---------------------------------------------------------------------------
    // Character tab
    // ---------------------------------------------------------------------------
    public static class CharacterTab
    {
        private static int page;   // 0 skills, 1 life, 2 records
        private static Vector2 leftScroll, pageScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);

        public static void Draw(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            PawnTabs.Header(head, pawn, ref PawnTabs.charOriginal, "char", ref MTMod.S.charW, ref MTMod.S.charH, 640f, 420f);
            Rect body = new Rect(r.x, head.yMax, r.width, r.height - head.height);
            float lw = Mathf.Clamp(body.width * 0.38f, 250f, 340f);
            Rect left = new Rect(body.x + 10f, body.y + 10f, lw, body.height - 20f);
            Rect right = new Rect(left.xMax + 14f, body.y + 10f, body.xMax - left.xMax - 24f, body.height - 20f);
            Dossier(left, pawn);
            string[] pages = { "MT_C_Skills".Translate(), "MT_C_Life".Translate(), "MT_C_Records".Translate() };
            float x = right.x;
            for (int i = 0; i < pages.Length; i++)
            {
                Verse.Text.Font = GameFont.Small;
                float w = Verse.Text.CalcSize(pages[i]).x + 26f;
                if (Look.Chip(new Rect(x, right.y, w, 24f), pages[i], page == i)) { page = i; pageScroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                x += w + 6f;
            }
            Rect pr = new Rect(right.x, right.y + 32f, right.width, right.height - 32f);
            if (page == 0) Skills(pr, pawn);
            else if (page == 1) Life(pr, pawn);
            else Records(pr, pawn);
        }

        private static void Dossier(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Card, Look.Line);
            Rect inner = r.ContractedBy(10f);
            float need = 600f + (pawn.story?.traits?.allTraits.Count ?? 0) * 10f;
            Rect view = new Rect(0f, 0f, inner.width - 16f, need);
            Widgets.BeginScrollView(inner, ref leftScroll, view);
            try
            {
                float y = 0f;
                // portrait
                Rect pic = new Rect((view.width - 120f) / 2f, y, 120f, 140f);
                Look.Box(pic, Look.Bg, Look.Line);
                try { GUI.DrawTexture(pic, PortraitsCache.Get(pawn, new Vector2(120f, 140f), Rot4.South, default(Vector3), 1.1f)); } catch { }
                y += 148f;
                // backstories
                foreach (BackstoryDef b in new[] { pawn.story?.Childhood, pawn.story?.Adulthood })
                {
                    if (b == null) continue;
                    Rect br = new Rect(0f, y, view.width, 40f);
                    Look.Box(br, Look.Bg);
                    Look.Text(new Rect(br.x + 8f, br.y + 2f, br.width - 16f, 18f), (b == pawn.story.Childhood ? "MT_C_Childhood" : "MT_C_Adulthood").Translate(), Muted, GameFont.Tiny);
                    Look.Text(new Rect(br.x + 8f, br.y + 18f, br.width - 16f, 20f), b.TitleCapFor(pawn.gender).Truncate(br.width - 16f), Color.white, GameFont.Small);
                    BackstoryDef bb = b;
                    TooltipHandler.TipRegion(br, new TipSignal(() => bb.FullDescriptionFor(pawn).Resolve(), bb.GetHashCode()));
                    y += 46f;
                }
                // traits
                if (pawn.story?.traits != null && pawn.story.traits.allTraits.Count > 0)
                {
                    Look.Text(new Rect(0f, y, view.width, 18f), "MT_C_Traits".Translate(), Muted, GameFont.Tiny);
                    y += 20f;
                    y += PawnTabs.Chips(new Rect(0f, y, view.width, 200f), pawn.story.traits.allTraits.Select(t =>
                        (t.LabelCap.ToString(), t.Suppressed ? Muted : (t.Degree < 0 ? Look.Orange : Look.Blue), t.TipString(pawn)))) + 4f;
                }
                // what they will not do
                List<WorkTypeDef> off = null;
                try { off = pawn.GetDisabledWorkTypes(true); } catch { }
                if (off != null && off.Count > 0)
                {
                    Look.Text(new Rect(0f, y, view.width, 18f), "MT_C_Incapable".Translate(), Muted, GameFont.Tiny);
                    y += 20f;
                    y += PawnTabs.Chips(new Rect(0f, y, view.width, 200f), off.Select(w => (w.gerundLabel.CapitalizeFirst(), Look.Red, (string)null))) + 4f;
                }
                // ideo and a few facts
                var facts = new List<string>();
                try
                {
                    if (pawn.Ideo != null) facts.Add("MT_C_Ideo".Translate(pawn.Ideo.name));
                    if (pawn.ageTracker != null) facts.Add("MT_C_Birthday".Translate(GenDate.DateShortStringAt(pawn.ageTracker.BirthAbsTicks, PawnTabs.Lon()), pawn.ageTracker.AgeChronologicalYears));
                }
                catch { }
                foreach (string f in facts)
                {
                    float fh = Verse.Text.CalcHeight(f, view.width);
                    Look.Text(new Rect(0f, y, view.width, fh), f, Muted, GameFont.Tiny);
                    y += fh + 2f;
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void Skills(Rect r, Pawn pawn)
        {
            if (pawn.skills == null) { Look.Text(r, "MT_C_NoSkills".Translate(), Muted, GameFont.Small, TextAnchor.MiddleCenter); return; }
            var list = pawn.skills.skills.OrderByDescending(s => s.def.listOrder).ToList();   // the game's order (Shooting … Intellectual)
            float rh = 34f;
            Rect view = new Rect(0f, 0f, r.width - (list.Count * rh > r.height ? 16f : 0f), list.Count * rh);
            Widgets.BeginScrollView(r, ref pageScroll, view);
            try
            {
                float y = 0f;
                foreach (SkillRecord s in list)
                {
                    Rect row = new Rect(0f, y, view.width, rh - 4f);
                    Widgets.DrawBoxSolid(row, Mouse.IsOver(row) ? Look.Card2 : Look.Card);
                    bool off = s.TotallyDisabled;
                    Look.Text(new Rect(row.x + 10f, row.y, 130f, row.height), s.def.skillLabel.CapitalizeFirst(), off ? Muted : Color.white, GameFont.Small);
                    if (s.passion != Passion.None)
                        GUI.DrawTexture(new Rect(row.x + 140f, row.y + 7f, 16f, 16f), s.passion == Passion.Major ? SkillUI.PassionMajorIcon : SkillUI.PassionMinorIcon);
                    Rect bar = new Rect(row.x + 164f, row.y + 9f, row.width - 220f, 12f);
                    if (off) Look.Text(bar, "-", Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
                    else
                    {
                        Color c = s.Level >= 15 ? Look.Green : s.Level >= 8 ? Look.Blue : Look.Orange;
                        Look.Bar(bar, s.Level / 20f, c);
                        Widgets.DrawBoxSolid(new Rect(bar.x, bar.yMax + 1f, bar.width * Mathf.Clamp01(s.XpProgressPercent), 2f), new Color(1f, 1f, 1f, 0.35f));
                        Look.Text(new Rect(row.xMax - 50f, row.y, 40f, row.height), s.Level.ToString(), c, GameFont.Small, TextAnchor.MiddleRight);
                    }
                    SkillRecord ss = s;
                    TooltipHandler.TipRegion(row, new TipSignal(() => SkillTip(ss), ss.def.index ^ 0x5C11));
                    y += rh;
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static string SkillTip(SkillRecord s)
        {
            try { return (string)AccessTools.Method(typeof(SkillUI), "GetSkillDescription").Invoke(null, new object[] { s }); }
            catch { return s.def.description; }
        }

        private struct LifeEntry
        {
            public long tick;
            public string text;
            public Color c;
        }

        private static List<LifeEntry> cache;
        private static int cachePawn, cacheFrame;

        private static List<LifeEntry> Entries(Pawn pawn)
        {
            if (cache != null && cachePawn == pawn.thingIDNumber && Time.frameCount - cacheFrame < 120) return cache;
            var l = new List<LifeEntry>();
            try
            {
                if (pawn.ageTracker != null) l.Add(new LifeEntry { tick = pawn.ageTracker.BirthAbsTicks, text = "MT_L_Born".Translate(), c = Look.Green });
                if (pawn.Faction == Faction.OfPlayer && pawn.playerSettings != null && pawn.playerSettings.joinTick > 0)
                    l.Add(new LifeEntry { tick = Find.TickManager.gameStartAbsTick + pawn.playerSettings.joinTick, text = "MT_L_Joined".Translate(Faction.OfPlayer.Name), c = Look.Green });
                foreach (Tale t in Find.TaleManager.AllTalesListForReading)
                {
                    if (t?.def == null || !t.Concerns(pawn)) continue;
                    l.Add(new LifeEntry { tick = t.date, text = t.ShortSummary, c = Look.Blue });
                }
                foreach (LifeLog.Entry e in LifeLog.For(pawn))
                    l.Add(new LifeEntry { tick = e.tick, text = e.text, c = KindColor(e.kind) });
            }
            catch (Exception ex) { Log.WarningOnce("[Mišák Tabs] life: " + ex.Message, 0x51A7B23); }
            cache = l.OrderByDescending(x => x.tick).ToList();
            cachePawn = pawn.thingIDNumber;
            cacheFrame = Time.frameCount;
            return cache;
        }

        public static Color KindColor(string k)
        {
            switch (k)
            {
                case "break": case "lost": case "wound": return Look.Red;
                case "part": case "surgery": return new Color32(70, 200, 255, 255);
                case "rel": case "birthday": return new Color32(230, 140, 190, 255);
                case "healed": return Look.Green;
                case "death": return new Color32(120, 125, 130, 255);
                case "honor": return new Color32(240, 200, 90, 255);
                case "research": return Look.Blue;
            }
            return Look.Orange;
        }

        private static void Life(Rect r, Pawn pawn)
        {
            var list = Entries(pawn);
            Vector2 lon = PawnTabs.Lon();
            const float rh = 26f;
            // one header row per year
            var rows = new List<(bool year, string text, Color c, string date)>();
            int lastYear = int.MinValue;
            foreach (LifeEntry e in list)
            {
                int year = GenDate.Year(e.tick, lon.x);
                if (year != lastYear) { rows.Add((true, year.ToString(), Color.white, null)); lastYear = year; }
                rows.Add((false, e.text, e.c, GenDate.DateShortStringAt(e.tick, lon)));
            }
            Rect view = new Rect(0f, 0f, r.width - 16f, Mathf.Max(r.height - 2f, rows.Count * rh + 30f));
            Widgets.BeginScrollView(r, ref pageScroll, view);
            try
            {
                float y = 0f;
                float lineX = 96f;
                Widgets.DrawBoxSolid(new Rect(lineX, 0f, 2f, rows.Count * rh), Look.Line);
                foreach (var row in rows)
                {
                    if (row.year)
                    {
                        Rect yr = new Rect(lineX - 30f, y + 3f, 64f, 20f);
                        Look.Box(yr, Look.Card2, Look.Line);
                        Look.Text(yr, row.text, Color.white, GameFont.Tiny, TextAnchor.MiddleCenter);
                    }
                    else
                    {
                        Look.Text(new Rect(0f, y, lineX - 10f, rh), row.date, Muted, GameFont.Tiny, TextAnchor.MiddleRight);
                        Widgets.DrawBoxSolid(new Rect(lineX - 4f, y + rh / 2f - 5f, 10f, 10f), row.c);
                        Look.Text(new Rect(lineX + 16f, y, view.width - lineX - 20f, rh), row.text.Truncate(view.width - lineX - 20f), Color.white, GameFont.Small);
                        Rect hr = new Rect(lineX + 16f, y, view.width - lineX - 20f, rh);
                        if (Mouse.IsOver(hr)) TooltipHandler.TipRegion(hr, row.text);
                    }
                    y += rh;
                }
                Look.Text(new Rect(0f, y + 4f, view.width, 20f), "MT_L_Note".Translate(), Muted, GameFont.Tiny);
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void Records(Rect r, Pawn pawn)
        {
            if (pawn.records == null) return;
            var items = new List<(string, string)>();
            void Add(RecordDef d)
            {
                if (d == null) return;
                try
                {
                    string v = d.type == RecordType.Time ? pawn.records.GetAsInt(d).ToStringTicksToPeriod() : pawn.records.GetValue(d).ToString("0.#");
                    items.Add((d.LabelCap, v));
                }
                catch { }
            }
            Add(DefDatabase<RecordDef>.GetNamedSilentFail("TimeAsColonistOrColonyAnimal"));
            foreach (RecordDef d in new[] { RecordDefOf.Kills, RecordDefOf.KillsHumanlikes, RecordDefOf.KillsAnimals, RecordDefOf.PawnsDowned, RecordDefOf.DamageTaken,
                RecordDefOf.TimesInMentalState, RecordDefOf.OperationsReceived, RecordDefOf.OperationsPerformed, RecordDefOf.TimesTendedOther, RecordDefOf.PrisonersRecruited,
                RecordDefOf.AnimalsTamed, RecordDefOf.MealsCooked, RecordDefOf.ThingsConstructed, RecordDefOf.ThingsCrafted, RecordDefOf.PlantsHarvested, RecordDefOf.CellsMined,
                RecordDefOf.ResearchPointsResearched, RecordDefOf.MessesCleaned })
                Add(d);
            int cols = r.width > 460f ? 3 : 2;
            float cw = (r.width - (cols - 1) * 10f - 16f) / cols;
            Rect view = new Rect(0f, 0f, r.width - 16f, ((items.Count + cols - 1) / cols) * 54f);
            Widgets.BeginScrollView(r, ref pageScroll, view);
            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    Rect c = new Rect((i % cols) * (cw + 10f), (i / cols) * 54f, cw, 48f);
                    Look.Box(c, Look.Card, Look.Line);
                    Look.Text(new Rect(c.x + 10f, c.y + 4f, c.width - 20f, 18f), items[i].Item1.Truncate(c.width - 20f), Muted, GameFont.Tiny);
                    Look.Text(new Rect(c.x + 10f, c.y + 20f, c.width - 20f, 24f), items[i].Item2, Color.white, GameFont.Medium);
                }
            }
            finally { Widgets.EndScrollView(); }
        }
    }

    // ---------------------------------------------------------------------------
    // Social tab
    // ---------------------------------------------------------------------------
    public static class SocialTab
    {
        private static Pawn selected;
        private static string search = "";
        private static Vector2 listScroll, rightScroll;
        private static readonly Color Muted = new Color32(160, 167, 174, 255);
        private static readonly Color Pink = new Color32(230, 140, 190, 255);

        private struct Other
        {
            public Pawn p;
            public string rel;
            public int mine, theirs;
            public bool hasTheirs;
        }

        private static List<Other> cache;
        private static int cachePawn, cacheFrame;

        private static List<Other> Others(Pawn pawn)
        {
            if (cache != null && cachePawn == pawn.thingIDNumber && Time.frameCount - cacheFrame < 60) return cache;
            var set = new HashSet<Pawn>();
            try { foreach (Pawn o in pawn.relations.RelatedPawns) set.Add(o); } catch { }
            try { foreach (Pawn o in PawnsFinder.AllMaps_FreeColonists) set.Add(o); } catch { }
            if (pawn.MapHeld != null) foreach (Pawn o in pawn.MapHeld.mapPawns.AllHumanlike) set.Add(o);
            set.Remove(pawn);
            var l = new List<Other>();
            foreach (Pawn o in set)
            {
                if (o?.RaceProps == null || !o.RaceProps.Humanlike) continue;
                var x = new Other { p = o };
                try
                {
                    x.rel = pawn.GetMostImportantRelation(o)?.GetGenderSpecificLabelCap(o);
                    x.mine = pawn.relations.OpinionOf(o);
                    if (o.relations != null && !o.Dead) { x.theirs = o.relations.OpinionOf(pawn); x.hasTheirs = true; }
                }
                catch { }
                if (x.rel == null && x.mine == 0 && x.theirs == 0 && !o.IsColonist) continue;
                l.Add(x);
            }
            cache = l.OrderByDescending(x => x.rel != null).ThenByDescending(x => Math.Abs(x.mine) + Math.Abs(x.theirs)).ToList();
            cachePawn = pawn.thingIDNumber;
            cacheFrame = Time.frameCount;
            return cache;
        }

        public static Color OpinionColor(int v) => v >= 20 ? Look.Green : v <= -20 ? Look.Red : v < 0 ? Look.Orange : Muted;

        public static void Draw(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            PawnTabs.Header(head, pawn, ref PawnTabs.socialOriginal, "social", ref MTMod.S.socialW, ref MTMod.S.socialH, 620f, 420f);
            Rect body = new Rect(r.x, head.yMax, r.width, r.height - head.height);
            float lw = Mathf.Clamp(body.width * 0.4f, 240f, 340f);
            Rect left = new Rect(body.x + 10f, body.y + 10f, lw, body.height - 20f);
            Rect right = new Rect(left.xMax + 14f, body.y + 10f, body.xMax - left.xMax - 24f, body.height - 20f);
            if (selected != null && (selected.Destroyed && !selected.Dead || selected == pawn)) selected = null;
            Roster(left, pawn);
            if (selected == null) Overview(right, pawn);
            else Detail(right, pawn, selected);
        }

        private static void Roster(Rect r, Pawn pawn)
        {
            Rect sr = new Rect(r.x, r.y, r.width, 26f);
            Look.Box(sr, Look.Card, Look.Line);
            GUI.SetNextControlName("MT_SocialSearch");
            Look.EscUnfocus("MT_SocialSearch");
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 2f, sr.width - 8f, 22f), search);
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 10f, sr.y, sr.width, sr.height), "MT_S_Search".Translate(), Muted, GameFont.Tiny);
            var list = Others(pawn).Where(o => search.NullOrEmpty() || o.p.LabelShort.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (o.rel ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Rect outR = new Rect(r.x, r.y + 32f, r.width, r.height - 32f);
            const float rh = 40f;
            Rect view = new Rect(0f, 0f, outR.width - (list.Count * rh > outR.height ? 16f : 0f), list.Count * rh);
            Widgets.BeginScrollView(outR, ref listScroll, view);
            try
            {
                float y = 0f;
                foreach (Other o in list)
                {
                    Rect row = new Rect(0f, y, view.width, rh - 4f);
                    bool sel = o.p == selected;
                    Look.Box(row, sel ? Look.GreenBg : Mouse.IsOver(row) ? Look.Card2 : Look.Card, sel ? Look.Green : (Color?)null);
                    try { Widgets.ThingIcon(new Rect(row.x + 4f, row.y + 2f, 32f, 32f), o.p); } catch { }
                    Look.Text(new Rect(row.x + 42f, row.y + 1f, row.width - 120f, 18f), o.p.LabelShort.Truncate(row.width - 120f), o.p.Dead ? Muted : Color.white, GameFont.Small);
                    string sub = o.rel ?? (o.p.Faction == Faction.OfPlayer ? "MT_S_Colonist".Translate().ToString() : o.p.Faction?.Name ?? "");
                    if (o.p.Dead) sub += " (" + "MT_S_Dead".Translate() + ")";
                    Look.Text(new Rect(row.x + 42f, row.y + 18f, row.width - 120f, 16f), sub.Truncate(row.width - 120f), o.rel != null ? Pink : Muted, GameFont.Tiny);
                    Look.Text(new Rect(row.xMax - 76f, row.y + 1f, 36f, 32f), o.mine.ToStringWithSign(), OpinionColor(o.mine), GameFont.Small, TextAnchor.MiddleRight);
                    if (o.hasTheirs) Look.Text(new Rect(row.xMax - 38f, row.y + 1f, 34f, 32f), o.theirs.ToStringWithSign(), OpinionColor(o.theirs), GameFont.Tiny, TextAnchor.MiddleRight);
                    TooltipHandler.TipRegion(row, "MT_S_RowTip".Translate(pawn.LabelShort, o.p.LabelShort));
                    if (Widgets.ButtonInvisible(row)) { selected = sel ? null : o.p; rightScroll = Vector2.zero; SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); }
                    y += rh;
                }
                if (list.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 40f), "MT_S_Nobody".Translate(), Muted, GameFont.Small, TextAnchor.MiddleCenter);
            }
            finally { Widgets.EndScrollView(); }
        }

        // the game's explanation split into lines: (label, value)
        private static List<(string, string, int)> Reasons(Pawn from, Pawn to)
        {
            var l = new List<(string, string, int)>();
            string s;
            try { s = from.relations.OpinionExplanation(to); } catch { return l; }
            foreach (string raw in s.Split('\n').Skip(1))
            {
                string line = raw.Trim();
                if (!line.StartsWith("-")) continue;
                line = line.Substring(1).Trim();
                int c = line.LastIndexOf(':');
                string label = c > 0 ? line.Substring(0, c) : line, val = c > 0 ? line.Substring(c + 1).Trim() : "";
                int.TryParse(val.Replace("+", ""), out int n);
                l.Add((label.StripTags(), val, n));
            }
            return l;
        }

        private static void Detail(Rect r, Pawn pawn, Pawn other)
        {
            Look.Box(r, Look.Card, Look.Line);
            Rect inner = r.ContractedBy(10f);
            var mine = Reasons(pawn, other);
            var theirs = other.relations != null && !other.Dead && other.RaceProps.Humanlike ? Reasons(other, pawn) : new List<(string, string, int)>();
            Rect view = new Rect(0f, 0f, inner.width - 16f, 200f + (mine.Count + theirs.Count) * 24f + 60f);
            Widgets.BeginScrollView(inner, ref rightScroll, view);
            try
            {
                float y = 0f;
                try { Widgets.ThingIcon(new Rect(0f, y, 48f, 48f), other); } catch { }
                Look.Text(new Rect(56f, y, view.width - 60f, 26f), other.LabelCap, Color.white, GameFont.Medium);
                var rels = pawn.GetRelations(other).Select(d => d.GetGenderSpecificLabelCap(other)).ToList();
                Look.Text(new Rect(56f, y + 26f, view.width - 60f, 20f), rels.Count > 0 ? string.Join(", ", rels) : (other.Faction?.Name ?? ""), rels.Count > 0 ? Pink : Muted, GameFont.Tiny);
                y += 56f;
                // compatibility and romance
                try
                {
                    float comp = pawn.relations.CompatibilityWith(other);
                    Look.Text(new Rect(0f, y, 140f, 20f), "MT_S_Compat".Translate(), Muted, GameFont.Tiny);
                    Look.Bar(new Rect(140f, y + 7f, view.width - 200f, 6f), Mathf.InverseLerp(-2.5f, 2.5f, comp), comp >= 0f ? Look.Green : Look.Orange);
                    Look.Text(new Rect(view.width - 56f, y, 56f, 20f), comp.ToString("+0.0;-0.0"), comp >= 0f ? Look.Green : Look.Orange, GameFont.Tiny, TextAnchor.MiddleRight);
                    y += 22f;
                    float rom = pawn.relations.SecondaryRomanceChanceFactor(other);
                    if (rom > 0f)
                    {
                        Look.Text(new Rect(0f, y, 140f, 20f), "MT_S_Romance".Translate(), Muted, GameFont.Tiny);
                        Look.Bar(new Rect(140f, y + 7f, view.width - 200f, 6f), rom, Pink);
                        Look.Text(new Rect(view.width - 56f, y, 56f, 20f), rom.ToStringPercent(), Pink, GameFont.Tiny, TextAnchor.MiddleRight);
                        y += 22f;
                    }
                }
                catch { }
                y += 4f;
                y += RomancePairButton(new Rect(0f, y, view.width, 28f), pawn, other);   // 0.11.0
                y += 4f;
                y = Side(new Rect(0f, y, view.width, 0f), "MT_S_Thinks".Translate(pawn.LabelShort, other.LabelShort), pawn.relations.OpinionOf(other), mine);
                if (other.relations != null && !other.Dead && other.RaceProps.Humanlike)
                    y = Side(new Rect(0f, y + 10f, view.width, 0f), "MT_S_Thinks".Translate(other.LabelShort, pawn.LabelShort), other.relations.OpinionOf(pawn), theirs);
            }
            finally { Widgets.EndScrollView(); }
            if (Look.Button(new Rect(r.xMax - 110f, r.yMax - 32f, 100f, 24f), "MT_S_Back".Translate())) selected = null;
            RomanceMenuButton(new Rect(r.xMax - 226f, r.yMax - 32f, 110f, 24f), pawn);   // 0.11.0
        }

        // ---------- 0.11.0: romance, as on the game's social tab ----------
        private static bool CanRomance(Pawn pawn) =>
            ModsConfig.BiotechActive && pawn.ageTracker != null && pawn.ageTracker.AgeBiologicalYearsFloat >= 16f && pawn.Spawned && pawn.IsFreeColonist;

        private static List<FloatMenuOption> RomanceOptions(Pawn romancer)
        {
            var ok = new List<(float, FloatMenuOption)>();
            var no = new List<FloatMenuOption>();
            foreach (Pawn p in romancer.Map.mapPawns.FreeColonistsSpawned)
            {
                if (RelationsUtility.RomanceOption(romancer, p, out FloatMenuOption opt, out float chance)) ok.Add((chance, opt));
                else if (opt != null) no.Add(opt);
            }
            return ok.OrderByDescending(x => x.Item1).Select(x => x.Item2).Concat(no.OrderBy(o => o.Label)).ToList();
        }

        private static void RomanceMenuButton(Rect r, Pawn pawn)
        {
            try
            {
                if (!CanRomance(pawn)) return;
                bool cooldown = pawn.relations.IsTryRomanceOnCooldown;
                AcceptanceReport rep = RelationsUtility.RomanceEligible(pawn, true, false);
                List<FloatMenuOption> list = rep.Accepted ? RomanceOptions(pawn) : null;
                bool dim = !rep.Accepted || list.NullOrEmpty() || cooldown;
                if (Look.Button(r, "TryRomanceButtonLabel".Translate() + "…", false, true))
                {
                    if (cooldown)
                    {
                        int t = pawn.relations.romanceEnableTick - Find.TickManager.TicksGame;
                        Messages.Message("CantRomanceInitiateMessageCooldown".Translate(pawn, t.ToStringTicksToPeriod()), MessageTypeDefOf.RejectInput, false);
                    }
                    else if (!rep.Accepted)
                    {
                        if (!rep.Reason.NullOrEmpty()) Messages.Message(rep.Reason, MessageTypeDefOf.RejectInput, false);
                    }
                    else if (list.NullOrEmpty()) Messages.Message("TryRomanceNoOptsMessage".Translate(pawn), MessageTypeDefOf.RejectInput, false);
                    else Find.WindowStack.Add(new FloatMenu(list));
                }
                if (dim) Widgets.DrawBoxSolid(r, new Color(0f, 0f, 0f, 0.35f));
                TooltipHandler.TipRegion(r, "MT_S_RomanceTip".Translate(pawn.LabelShort));
            }
            catch (Exception e) { Log.ErrorOnce("[Mišák Tabs] romance button: " + e.Message, 0x51A7B31); }
        }

        // a direct button for the selected pair; returns the height used
        private static float RomancePairButton(Rect r, Pawn pawn, Pawn other)
        {
            try
            {
                if (!CanRomance(pawn) || other.Dead || !other.Spawned || other.Map != pawn.Map || !other.IsFreeColonist) return 0f;
                bool can = RelationsUtility.RomanceOption(pawn, other, out FloatMenuOption opt, out float chance);
                if (opt == null) return 0f;   // not attracted to that gender – the game shows nothing either
                bool cooldown = pawn.relations.IsTryRomanceOnCooldown;
                string label = can ? "MT_S_RomanceThem".Translate(other.LabelShort, chance.ToStringPercent()).ToString() : "MT_S_RomanceThemNo".Translate(other.LabelShort).ToString();
                Rect b = new Rect(r.x, r.y, Mathf.Min(r.width, 300f), 28f);
                bool enabled = can && !cooldown;
                if (Look.Button(b, label, enabled, enabled)) opt.action?.Invoke();
                if (!enabled)
                {
                    string why = cooldown ? "CantRomanceInitiateMessageCooldown".Translate(pawn, (pawn.relations.romanceEnableTick - Find.TickManager.TicksGame).ToStringTicksToPeriod()).ToString() : opt.Label;
                    TooltipHandler.TipRegion(b, why);
                }
                return 34f;
            }
            catch { return 0f; }
        }

        private static float Side(Rect r, string title, int total, List<(string label, string val, int n)> reasons)
        {
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 60f, 22f), title, Color.white, GameFont.Small);
            Look.Text(new Rect(r.xMax - 60f, y, 60f, 22f), total.ToStringWithSign(), OpinionColor(total), GameFont.Medium, TextAnchor.MiddleRight);
            y += 26f;
            if (reasons.Count == 0) { Look.Text(new Rect(r.x + 10f, y, r.width, 20f), "MT_S_NoReasons".Translate(), Muted, GameFont.Tiny); return y + 22f; }
            foreach (var (label, val, n) in reasons)
            {
                Rect row = new Rect(r.x, y, r.width, 22f);
                Widgets.DrawBoxSolid(row, Look.Bg);
                Color c = n > 0 ? Look.Green : n < 0 ? Look.Red : Muted;
                Widgets.DrawBoxSolid(new Rect(row.x, row.y, 4f, row.height), c);
                Look.Text(new Rect(row.x + 12f, row.y, row.width - 80f, row.height), label.Truncate(row.width - 80f), Color.white, GameFont.Small);
                Look.Text(new Rect(row.xMax - 66f, row.y, 60f, row.height), val, c, GameFont.Small, TextAnchor.MiddleRight);
                y += 24f;
            }
            return y;
        }

        private static void Overview(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Card, Look.Line);
            Rect inner = r.ContractedBy(10f);
            var others = Others(pawn);
            float y = inner.y;
            Look.Text(new Rect(inner.x, y, inner.width, 24f), "MT_S_Overview".Translate(), Color.white, GameFont.Medium);
            y += 30f;
            void Card(string label, Other? o, Color c)
            {
                Rect cr = new Rect(inner.x, y, inner.width, 40f);
                Look.Box(cr, Look.Bg);
                Widgets.DrawBoxSolid(new Rect(cr.x, cr.y, 4f, cr.height), c);
                Look.Text(new Rect(cr.x + 12f, cr.y + 2f, 160f, 18f), label, Muted, GameFont.Tiny);
                if (o.HasValue)
                {
                    Look.Text(new Rect(cr.x + 12f, cr.y + 18f, cr.width - 90f, 20f), o.Value.p.LabelShort, Color.white, GameFont.Small);
                    Look.Text(new Rect(cr.xMax - 70f, cr.y, 60f, cr.height), o.Value.mine.ToStringWithSign(), OpinionColor(o.Value.mine), GameFont.Medium, TextAnchor.MiddleRight);
                    Pawn op = o.Value.p;
                    if (Widgets.ButtonInvisible(cr)) { selected = op; rightScroll = Vector2.zero; }
                }
                else Look.Text(new Rect(cr.x + 12f, cr.y + 18f, cr.width - 20f, 20f), "—", Muted, GameFont.Small);
                y += 46f;
            }
            var alive = others.Where(o => !o.p.Dead).ToList();
            Other? partner = alive.Where(o => LovePartnerRelationUtility.LovePartnerRelationExists(pawn, o.p)).Select(o => (Other?)o).FirstOrDefault();
            Other? friend = alive.Where(o => o.mine > 0).OrderByDescending(o => o.mine).Select(o => (Other?)o).FirstOrDefault();
            Other? rival = alive.Where(o => o.mine < 0).OrderBy(o => o.mine).Select(o => (Other?)o).FirstOrDefault();
            Card("MT_S_Partner".Translate(), partner, Pink);
            Card("MT_S_BestFriend".Translate(), friend, Look.Green);
            Card("MT_S_Rival".Translate(), rival, Look.Red);
            int friends = alive.Count(o => o.mine >= 20), rivals = alive.Count(o => o.mine <= -20);
            Look.Text(new Rect(inner.x, y, inner.width, 20f), "MT_S_Counts".Translate(friends, rivals), Muted, GameFont.Small);
            y += 28f;
            // the last talks
            Look.Text(new Rect(inner.x, y, inner.width, 20f), "MT_S_Talks".Translate(), Color.white, GameFont.Small);
            y += 24f;
            var talks = new List<LogEntry>();
            try { talks = Find.PlayLog.AllEntries.Where(e => e.Concerns(pawn)).Take(40).ToList(); } catch { }
            bool rom = CanRomance(pawn);
            Rect outR = new Rect(inner.x, y, inner.width, inner.yMax - y - (rom ? 32f : 0f));
            if (rom) RomanceMenuButton(new Rect(r.xMax - 130f, r.yMax - 32f, 120f, 24f), pawn);   // 0.11.0
            float need = 0f;
            var texts = talks.Select(t => { string s = ""; try { s = t.ToGameStringFromPOV(pawn).StripTags(); } catch { } return (t, s); }).Where(x => !x.s.NullOrEmpty()).ToList();
            foreach (var (_, s) in texts) need += Verse.Text.CalcHeight(s, outR.width - 76f) + 4f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, need);
            Widgets.BeginScrollView(outR, ref rightScroll, view);
            try
            {
                float ty = 0f;
                foreach (var (t, s) in texts)
                {
                    float h = Verse.Text.CalcHeight(s, view.width - 60f);
                    Look.Text(new Rect(0f, ty, 56f, 18f), t.Age.ToStringTicksToPeriod(true, true), Muted, GameFont.Tiny);
                    Verse.Text.Font = GameFont.Small;
                    GUI.color = new Color(0.85f, 0.87f, 0.9f);
                    Widgets.Label(new Rect(60f, ty, view.width - 60f, h), s);
                    GUI.color = Color.white;
                    ty += h + 4f;
                }
                if (texts.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 20f), "MT_S_NoTalks".Translate(), Muted, GameFont.Tiny);
            }
            finally { Widgets.EndScrollView(); }
        }
    }

    // ---------------------------------------------------------------------------
    // The life log: what the game does not keep as a tale
    // ---------------------------------------------------------------------------
    public class LifeLog : GameComponent
    {
        public class Entry : IExposable
        {
            public Pawn pawn;
            public long tick;
            public string kind, text;

            public void ExposeData()
            {
                Scribe_References.Look(ref pawn, "pawn", true);
                Scribe_Values.Look(ref tick, "tick");
                Scribe_Values.Look(ref kind, "kind");
                Scribe_Values.Look(ref text, "text");
            }
        }

        public List<Entry> entries = new List<Entry>();

        public LifeLog(Game g) { }

        public static LifeLog I => Current.Game?.GetComponent<LifeLog>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref entries, "entries", LookMode.Deep);
            if (entries == null) entries = new List<Entry>();
            if (Scribe.mode == LoadSaveMode.PostLoadInit) entries.RemoveAll(e => e?.pawn == null);
        }

        public static IEnumerable<Entry> For(Pawn p)
        {
            LifeLog l = I;
            if (l == null) yield break;
            foreach (Entry e in l.entries) if (e.pawn == p) yield return e;
        }

        public static void Add(Pawn p, string kind, string text)
        {
            try
            {
                if (p == null || !p.RaceProps.Humanlike || (p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony && !p.IsSlaveOfColony)) return;
                LifeLog l = I;
                if (l == null || Current.ProgramState != ProgramState.Playing) return;
                long now = Find.TickManager.TicksAbs;
                // the same thing twice in a short time is one entry
                if (l.entries.Any(e => e.pawn == p && e.text == text && now - e.tick < 2500)) return;
                l.entries.Add(new Entry { pawn = p, tick = now, kind = kind, text = text });
                if (l.entries.Count > 4000) l.entries.RemoveRange(0, 500);
            }
            catch { }
        }
    }

    public static class LifeHooks
    {
        private static readonly AccessTools.FieldRef<SkillRecord, Pawn> skillPawn = AccessTools.FieldRefAccess<SkillRecord, Pawn>("pawn");

        public static void Patch(Harmony h)
        {
            void Try(string what, Action a)
            {
                try { a(); } catch (Exception e) { Log.Warning("[Mišák Tabs] life log (" + what + "): " + e.Message); }
            }
            Try("breaks", () => h.Patch(AccessTools.Method(typeof(MentalStateHandler), nameof(MentalStateHandler.TryStartMentalState)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Break))));
            Try("body", () => h.Patch(AccessTools.Method(typeof(HediffSet), nameof(HediffSet.AddDirect)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Hediff))));
            Try("skills", () => h.Patch(AccessTools.Method(typeof(SkillRecord), nameof(SkillRecord.Learn)),
                prefix: new HarmonyMethod(typeof(LifeHooks), nameof(LearnPre)), postfix: new HarmonyMethod(typeof(LifeHooks), nameof(LearnPost))));
            Try("relations", () => h.Patch(AccessTools.Method(typeof(Pawn_RelationsTracker), nameof(Pawn_RelationsTracker.AddDirectRelation)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Relation))));
            // 0.9.1
            Try("surgery fail", () => h.Patch(AccessTools.Method(typeof(Recipe_Surgery), "CheckSurgeryFail"),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(SurgeryFail))));
            Try("surgery", () => h.Patch(AccessTools.Method(typeof(Bill_Medical), nameof(Bill_Medical.Notify_IterationCompleted)),
                prefix: new HarmonyMethod(typeof(LifeHooks), nameof(SurgeryPre)), postfix: new HarmonyMethod(typeof(LifeHooks), nameof(SurgeryPost))));
            Try("healed", () => h.Patch(AccessTools.Method(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.RemoveHediff)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Removed))));
            Try("death", () => h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.Kill)),
                prefix: new HarmonyMethod(typeof(LifeHooks), nameof(KillPre)), postfix: new HarmonyMethod(typeof(LifeHooks), nameof(KillPost))));
            Try("titles", () => h.Patch(AccessTools.Method(typeof(Pawn_RoyaltyTracker), nameof(Pawn_RoyaltyTracker.SetTitle)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Title))));
            Try("roles", () =>
            {
                foreach (Type t in new[] { typeof(Precept_RoleSingle), typeof(Precept_RoleMulti) })
                    h.Patch(AccessTools.Method(t, "Assign"), postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Role)));
            });
            Try("birthday", () => h.Patch(AccessTools.Method(typeof(Pawn_AgeTracker), "BirthdayBiological"),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Birthday))));
            Try("research", () => h.Patch(AccessTools.Method(typeof(ResearchManager), nameof(ResearchManager.FinishProject)),
                postfix: new HarmonyMethod(typeof(LifeHooks), nameof(Research))));
        }

        // ---- 0.9.1: operations, healing, deaths, titles, roles, birthdays, research, heavy wounds ----
        private static readonly Dictionary<Pawn, int> failed = new Dictionary<Pawn, int>();

        public static void SurgeryFail(bool __result, Pawn patient)
        {
            if (__result && patient != null) failed[patient] = Find.TickManager.TicksGame;
        }

        public static void SurgeryPre(Bill_Medical __instance, out (Pawn, string, BodyPartRecord) __state)
        {
            __state = (null, null, null);
            try { __state = (__instance.GiverPawn, __instance.recipe?.label, __instance.Part); } catch { }
        }

        public static void SurgeryPost(Pawn billDoer, (Pawn patient, string label, BodyPartRecord part) __state)
        {
            try
            {
                Pawn patient = __state.patient;
                if (patient == null || __state.label == null) return;
                bool bad = failed.TryGetValue(patient, out int t) && Find.TickManager.TicksGame - t < 600;
                failed.Remove(patient);
                string what = __state.label.CapitalizeFirst() + (__state.part != null ? " (" + __state.part.Label + ")" : "");
                LifeLog.Add(patient, bad ? "lost" : "surgery", (bad ? "MT_L_SurgeryBad" : "MT_L_Surgery").Translate(what, billDoer?.LabelShort ?? "?"));
                if (billDoer != null && billDoer != patient)
                    LifeLog.Add(billDoer, bad ? "lost" : "surgery", (bad ? "MT_L_OperatedBad" : "MT_L_Operated").Translate(patient.LabelShort, what));
            }
            catch { }
        }

        public static void Removed(Hediff hediff, Pawn ___pawn)
        {
            try
            {
                if (___pawn == null || ___pawn.Dead || hediff == null || Current.ProgramState != ProgramState.Playing) return;
                if (hediff.TryGetComp<HediffComp_Immunizable>() != null && hediff.def.isBad)
                    LifeLog.Add(___pawn, "healed", "MT_L_Healed".Translate(hediff.LabelBase));
            }
            catch { }
        }

        public static void KillPre(Pawn __instance, out bool __state) => __state = !__instance.Dead;

        public static void KillPost(Pawn __instance, bool __state, DamageInfo? dinfo, Hediff exactCulprit)
        {
            try
            {
                if (!__state || !__instance.Dead || !__instance.RaceProps.Humanlike || Current.ProgramState != ProgramState.Playing) return;
                string cause = exactCulprit?.LabelBase ?? dinfo?.Def?.label;
                LifeLog.Add(__instance, "death", cause.NullOrEmpty() ? "MT_L_Died".Translate().ToString() : "MT_L_DiedOf".Translate(cause).ToString());
                foreach (Pawn c in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
                {
                    if (c == __instance || c.relations == null) continue;
                    PawnRelationDef rel = c.GetMostImportantRelation(__instance);
                    int op = 0;
                    try { op = c.relations.OpinionOf(__instance); } catch { }
                    if (rel != null) LifeLog.Add(c, "death", "MT_L_LostRel".Translate(rel.GetGenderSpecificLabel(__instance), __instance.LabelShort));
                    else if (op >= 20) LifeLog.Add(c, "death", "MT_L_LostFriend".Translate(__instance.LabelShort));
                }
            }
            catch { }
        }

        public static void Title(Faction faction, RoyalTitleDef title, Pawn ___pawn)
        {
            try { if (title != null) LifeLog.Add(___pawn, "honor", "MT_L_Title".Translate(title.GetLabelCapFor(___pawn), faction?.Name ?? "")); } catch { }
        }

        public static void Role(Precept_Role __instance, Pawn p)
        {
            try { if (p != null) LifeLog.Add(p, "honor", "MT_L_Role".Translate(__instance.LabelCap)); } catch { }
        }

        public static void Birthday(int birthdayAge, Pawn ___pawn) => LifeLog.Add(___pawn, "birthday", "MT_L_Birthday".Translate(birthdayAge));

        public static void Research(ResearchProjectDef proj, Pawn researcher)
        {
            if (researcher != null && proj != null) LifeLog.Add(researcher, "research", "MT_L_Research".Translate(proj.LabelCap));
        }

        public static void Break(bool __result, MentalStateDef stateDef, Pawn ___pawn)
        {
            if (__result && stateDef != null) LifeLog.Add(___pawn, "break", "MT_L_Break".Translate(stateDef.label));
        }

        public static void Hediff(HediffSet __instance, Hediff hediff)
        {
            try
            {
                Pawn p = __instance.pawn;
                if (hediff?.Part == null || p == null || Current.ProgramState != ProgramState.Playing) return;
                if (hediff is Hediff_MissingPart && (hediff.Part.parent == null || !__instance.PartIsMissing(hediff.Part.parent)) && hediff.Part.coverageAbsWithChildren >= 0.005f)
                    LifeLog.Add(p, "lost", "MT_L_Lost".Translate(hediff.Part.Label));
                else if (hediff is Hediff_AddedPart || hediff is Hediff_Implant)
                    LifeLog.Add(p, "part", "MT_L_Installed".Translate(hediff.LabelBase, hediff.Part.Label));
                else if (hediff is Hediff_Injury && !hediff.IsPermanent())
                {
                    float max = hediff.Part.def.GetMaxHealth(p);
                    if (hediff.Severity >= Mathf.Max(8f, max * 0.5f))
                        LifeLog.Add(p, "wound", "MT_L_Wound".Translate(hediff.LabelBase, hediff.Part.Label));
                }
            }
            catch { }
        }

        public static void LearnPre(SkillRecord __instance, out int __state) => __state = __instance.levelInt;

        public static void LearnPost(SkillRecord __instance, int __state)
        {
            if (__instance.levelInt > __state && __instance.levelInt >= 8)
                LifeLog.Add(skillPawn(__instance), "skill", "MT_L_Skill".Translate(__instance.def.skillLabel, __instance.levelInt));
        }

        public static void Relation(PawnRelationDef def, Pawn otherPawn, Pawn ___pawn)
        {
            try
            {
                if (def == null || otherPawn == null) return;
                if (def == PawnRelationDefOf.Lover || def == PawnRelationDefOf.Fiance || def == PawnRelationDefOf.Spouse)
                    LifeLog.Add(___pawn, "rel", "MT_L_Rel".Translate(def.GetGenderSpecificLabel(otherPawn), otherPawn.LabelShort));
                else if (def == PawnRelationDefOf.Parent && otherPawn != null && ___pawn.ageTracker != null && ___pawn.ageTracker.AgeBiologicalTicks < 60000)
                    LifeLog.Add(otherPawn, "rel", "MT_L_Child".Translate(___pawn.LabelShort));
            }
            catch { }
        }
    }
}
