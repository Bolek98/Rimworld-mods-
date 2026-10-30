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
    // 0.12.0: "what to grow" (growing zone, hydroponics, plant pot) in our own window instead of the
    // game's endless list over the whole screen: groups (food, medicine, drugs, materials, trees,
    // decoration), search, sorting, and for each plant a card with its picture, growing time,
    // yield, value per day and the needed skill (red when no colonist of yours has it).
    // Picking a plant does exactly what the game does (all selected zones, roof and skill warnings).
    // ---------------------------------------------------------------------------
    public static class PlantPickerPatch
    {
        private static FieldInfo settablesF;
        private static MethodInfo warn;

        public static void Patch(Harmony h)
        {
            try
            {
                settablesF = AccessTools.Field(typeof(Command_SetPlantToGrow), "settables");
                warn = AccessTools.Method(typeof(Command_SetPlantToGrow), "WarnAsAppropriate");
                h.Patch(AccessTools.Method(typeof(Command_SetPlantToGrow), nameof(Command_SetPlantToGrow.ProcessInput)),
                    prefix: new HarmonyMethod(typeof(PlantPickerPatch), nameof(Prefix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] plant picker: " + e.Message); }
        }

        public static bool Prefix(Command_SetPlantToGrow __instance, Event ev)
        {
            if (MTMod.S == null || !MTMod.S.plantPicker || settablesF == null) return true;
            try
            {
                var list = settablesF.GetValue(__instance) as List<IPlantToGrowSettable>;
                if (list == null) { list = new List<IPlantToGrowSettable>(); settablesF.SetValue(__instance, list); }
                if (!list.Contains(__instance.settable)) list.Add(__instance.settable);
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                Find.WindowStack.Add(new Dialog_PlantPicker(__instance, list));
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Mišák Tabs] plant picker: " + e, 0x4d5403);
                return true;
            }
        }

        // the same as the game's pick
        public static void Pick(Command_SetPlantToGrow cmd, List<IPlantToGrowSettable> settables, ThingDef plant)
        {
            string tag = cmd.tutorTag + "-" + plant.defName;
            if (!TutorSystem.AllowAction(tag)) return;
            bool roofTold = false;
            foreach (IPlantToGrowSettable s in settables)
            {
                s.SetPlantDefToGrow(plant);
                if (!roofTold && plant.plant.interferesWithRoof)
                    foreach (IntVec3 c in s.Cells)
                        if (c.Roofed(s.Map))
                        {
                            Messages.Message("MessagePlantIncompatibleWithRoof".Translate(Find.ActiveLanguageWorker.Pluralize(plant.LabelCap)), MessageTypeDefOf.CautionInput, false);
                            roofTold = true;
                            break;
                        }
            }
            PlayerKnowledgeDatabase.KnowledgeDemonstrated(ConceptDefOf.SetGrowingZonePlant, KnowledgeAmount.Total);
            try { warn?.Invoke(cmd, new object[] { plant }); } catch { }
            TutorSystem.Notify_Event(tag);
        }
    }

    public class Dialog_PlantPicker : Window
    {
        private enum Group { All, Food, Medicine, Drugs, Materials, Trees, Decor }
        private enum Sort { Name, Days, Value, Skill }

        private class Entry
        {
            public ThingDef def;
            public Group group;
            public float days, yield, valuePerDay, nutritionPerDay;
            public string harvest, mod;
            public bool dupLabel, skillOk;
            public int skill;
        }

        private readonly Command_SetPlantToGrow cmd;
        private readonly List<IPlantToGrowSettable> settables;
        private readonly List<Entry> all = new List<Entry>();
        private readonly ThingDef current;
        private Group group = Group.All;
        private Sort sort = Sort.Name;
        private string search = "";
        private bool onlyUsable;
        private Vector2 scroll;
        private int bestSkill;

        public override Vector2 InitialSize => new Vector2(980f, 680f);

        public Dialog_PlantPicker(Command_SetPlantToGrow cmd, List<IPlantToGrowSettable> settables)
        {
            this.cmd = cmd;
            this.settables = settables;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
            forcePause = false;
            current = settables.Count > 0 ? settables[0].GetPlantDefToGrow() : null;
            Map map = settables.Count > 0 ? settables[0].Map : Find.CurrentMap;
            try
            {
                foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
                    if (!p.Downed && p.skills != null && p.workSettings != null && p.workSettings.WorkIsActive(WorkTypeDefOf.Growing))
                        bestSkill = Math.Max(bestSkill, p.skills.GetSkill(SkillDefOf.Plants).Level);
            }
            catch { }
            var defs = PlantUtility.ValidPlantTypesForGrowers(settables).Where(d => Command_SetPlantToGrow.IsPlantAvailable(d, map)).ToList();
            var labels = defs.GroupBy(d => d.label ?? "").Where(g => g.Count() > 1).Select(g => g.Key);
            var dup = new HashSet<string>(labels);
            foreach (ThingDef d in defs)
            {
                var e = new Entry { def = d, group = GroupOf(d), days = Mathf.Max(0.1f, d.plant.growDays), yield = d.plant.harvestYield, skill = d.plant.sowMinSkill };
                ThingDef h = d.plant.harvestedThingDef;
                e.harvest = h != null ? (string)h.LabelCap : "";
                if (h != null)
                {
                    e.valuePerDay = e.yield * h.BaseMarketValue / e.days;
                    try { if (h.IsNutritionGivingIngestible) e.nutritionPerDay = e.yield * h.GetStatValueAbstract(StatDefOf.Nutrition) / e.days; } catch { }
                }
                e.mod = d.modContentPack?.Name ?? "";
                e.dupLabel = dup.Contains(d.label ?? "");
                e.skillOk = e.skill <= 0 || bestSkill >= e.skill;
                all.Add(e);
            }
        }

        private static Group GroupOf(ThingDef d)
        {
            if (d.plant.IsTree) return Group.Trees;
            ThingDef h = d.plant.harvestedThingDef;
            try
            {
                if (h != null && h.IsDrug) return Group.Drugs;
                if (d.plant.purpose == PlantPurpose.Health || (h != null && h.IsMedicine)) return Group.Medicine;
            }
            catch { }
            if (d.plant.purpose == PlantPurpose.Food || (h != null && h.IsNutritionGivingIngestible)) return Group.Food;
            if (d.plant.purpose == PlantPurpose.Beauty || h == null) return Group.Decor;
            return Group.Materials;
        }

        private static string GroupLabel(Group g) => ("MT_PG_" + g).Translate();

        public override void DoWindowContents(Rect inRect)
        {
            Rect full = new Rect(0f, 0f, windowRect.width, windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
            Rect r = inRect;
            float y = r.y;
            // title
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), "MT_PG_Title".Translate(), MUI.Text1, GameFont.Medium);
            if (current != null)
            {
                Widgets.DrawTextureFitted(new Rect(r.xMax - 300f, y + 4f, 24f, 24f), current.uiIcon, 1f);
                Look.Text(new Rect(r.xMax - 270f, y, 240f, 32f), "MT_PG_Now".Translate(current.LabelCap), MUI.Muted);
            }
            y += 38f;
            // group tabs
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = g == Group.All ? all.Count : all.Count(e => e.group == g);
                if (n == 0 && g != Group.All) continue;
                string lab = GroupLabel(g) + " (" + n + ")";
                Text.Font = GameFont.Small;
                float w = Text.CalcSize(lab).x + 24f;
                Rect b = new Rect(x, y, w, 28f);
                bool on = group == g;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                MUI.Frame(b, on ? Look.Green : Look.Line);
                Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(b)) { group = g; scroll = Vector2.zero; }
                x += w + 6f;
            }
            y += 36f;
            // search, sort, usable
            Rect sr = new Rect(r.x, y, 260f, 28f);
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName("MT_PlantSearch");
            Look.EscUnfocus("MT_PlantSearch");
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), "MT_PG_Search".Translate(), MUI.Muted);
            x = sr.xMax + 16f;
            Look.Text(new Rect(x, y, 70f, 28f), "MT_PG_SortBy".Translate(), MUI.Muted);
            x += 70f;
            foreach (Sort s in Enum.GetValues(typeof(Sort)))
            {
                string lab = ("MT_PG_S" + s).Translate();
                float w = Text.CalcSize(lab).x + 20f;
                Rect b = new Rect(x, y, w, 28f);
                bool on = sort == s;
                MUI.Fill(b, on ? Look.GreenBg : Mouse.IsOver(b) ? Look.Card2 : Look.Card);
                MUI.Frame(b, on ? Look.Green : Look.Line);
                Look.Text(b, lab, on ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
                if (Widgets.ButtonInvisible(b)) sort = s;
                x += w + 4f;
            }
            Rect ur = new Rect(r.xMax - 230f, y, 230f, 28f);
            Widgets.CheckboxLabeled(ur, "MT_PG_Usable".Translate(), ref onlyUsable);
            TooltipHandler.TipRegion(ur, "MT_PG_UsableTip".Translate(bestSkill));
            y += 38f;

            // cards
            string q = (search ?? "").Trim();
            IEnumerable<Entry> list = all.Where(e => (group == Group.All || e.group == group)
                && (!onlyUsable || e.skillOk)
                && (q.Length == 0 || e.def.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.harvest.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || e.mod.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            switch (sort)
            {
                case Sort.Days: list = list.OrderBy(e => e.days).ThenBy(e => e.def.label); break;
                case Sort.Value: list = list.OrderByDescending(e => e.valuePerDay).ThenBy(e => e.def.label); break;
                case Sort.Skill: list = list.OrderBy(e => e.skill).ThenBy(e => e.def.label); break;
                default: list = list.OrderBy(e => e.def.label); break;
            }
            var shown = list.ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            int cols = Mathf.Max(1, Mathf.FloorToInt((outR.width - 16f + 8f) / 300f));
            float cw = (outR.width - 16f - (cols - 1) * 8f) / cols, ch = 64f;
            int rows = Mathf.CeilToInt(shown.Count / (float)cols);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows * (ch + 8f), 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    Rect c = new Rect((i % cols) * (cw + 8f), (i / cols) * (ch + 8f), cw, ch);
                    if (c.yMax < scroll.y - 10f || c.y > scroll.y + outR.height + 10f) continue;
                    if (DrawCard(c, shown[i]))
                    {
                        PlantPickerPatch.Pick(cmd, settables, shown[i].def);
                        Close();
                        break;
                    }
                }
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PG_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        private bool DrawCard(Rect c, Entry e)
        {
            bool cur = e.def == current;
            bool over = Mouse.IsOver(c);
            MUI.Fill(c, cur ? Look.GreenBg : over ? Look.Card2 : Look.Card);
            MUI.Frame(c, cur ? Look.Green : over ? Look.Line : Look.Line);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            MUI.Fill(icon, Look.Bg);
            Widgets.DefIcon(icon.ContractedBy(4f), e.def);
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 30f;
            string name = e.def.LabelCap;
            Look.Text(new Rect(tx, c.y + 4f, tw, 22f), name.Truncate(tw), cur ? Look.Green : MUI.Text1);
            string line2 = "MT_PG_Days".Translate(e.days.ToString("0.#"));
            if (e.yield > 0f && !e.harvest.NullOrEmpty()) line2 += " · " + "MT_PG_Yield".Translate(Mathf.RoundToInt(e.yield), e.harvest);
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), line2.Truncate(tw), MUI.Muted, GameFont.Tiny);
            // badges
            float bx = tx;
            if (e.skill > 0) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PG_Skill".Translate(e.skill), e.skillOk ? Look.Line : Look.Red);
            if (e.valuePerDay > 0.05f) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PG_ValueDay".Translate(e.valuePerDay.ToString("0.#")), Look.Line);
            if (e.def.plant.cavePlant) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PG_Cave".Translate(), Look.Blue);
            if (e.def.plant.interferesWithRoof) bx = Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PG_NoRoof".Translate(), Look.Orange);
            if (e.dupLabel && !e.mod.NullOrEmpty()) Badge(new Rect(bx, c.y + 43f, 0f, 16f), e.mod.Truncate(120f), Look.Line);
            // info card
            Widgets.InfoCardButton(c.xMax - 28f, c.y + 6f, e.def);
            // tooltip
            var sb = new System.Text.StringBuilder();
            sb.Append(e.def.LabelCap.ToString()).Append('\n').Append(e.def.description);
            sb.Append("\n\n").Append("MT_PG_Days".Translate(e.days.ToString("0.#")).ToString());
            if (!e.harvest.NullOrEmpty()) sb.Append('\n').Append("MT_PG_Yield".Translate(Mathf.RoundToInt(e.yield), e.harvest).ToString());
            if (e.nutritionPerDay > 0f) sb.Append('\n').Append("MT_PG_NutDay".Translate(e.nutritionPerDay.ToString("0.00")).ToString());
            if (e.valuePerDay > 0f) sb.Append('\n').Append("MT_PG_ValueDay".Translate(e.valuePerDay.ToString("0.#")).ToString());
            if (e.skill > 0) sb.Append('\n').Append("MT_PG_SkillTip".Translate(e.skill, bestSkill).ToString());
            if (!e.mod.NullOrEmpty()) sb.Append('\n').Append("MT_PG_Mod".Translate(e.mod).ToString());
            string tip = sb.ToString();
            TooltipHandler.TipRegion(new Rect(c.x, c.y, c.width - 32f, c.height), tip);
            return Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 32f, c.height));
        }

        private static float Badge(Rect at, string text, Color border)
        {
            Text.Font = GameFont.Tiny;
            float w = Text.CalcSize(text).x + 10f;
            Rect b = new Rect(at.x, at.y, w, at.height);
            MUI.Fill(b, Look.Bg);
            MUI.Frame(b, border);
            Look.Text(b, text, border == Look.Line ? MUI.Muted : border, GameFont.Tiny, TextAnchor.MiddleCenter);
            Text.Font = GameFont.Small;
            return b.xMax + 4f;
        }
    }
}
