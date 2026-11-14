// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakCharacter
{
    // ---------------------------------------------------------------------------
    // 0.13.0: animals.
    // - "Master" in the animals tab opens our window with colonist cards (handling skill, bond,
    //   why somebody can't be the master) instead of the long menu. Shift+click keeps the game's
    //   menu (and its drag-painting).
    // - An "Animal overview" button in the animals tab: every colony animal as a card in groups
    //   (livestock, work, combat, pets) with quick actions – training, slaughter, release to the wild.
    // ---------------------------------------------------------------------------
    [StaticConstructorOnStartup]
    public static class AnimalPickerPatch
    {
        static AnimalPickerPatch()
        {
            try
            {
                if (MTMod.H == null) return;
                MTMod.H.Patch(AccessTools.Method(typeof(TrainableUtility), nameof(TrainableUtility.MasterSelectButton)),
                    prefix: new HarmonyMethod(typeof(AnimalPickerPatch), nameof(MasterPrefix)));
                MTMod.H.Patch(AccessTools.DeclaredMethod(typeof(MainTabWindow_Animals), nameof(MainTabWindow_Animals.DoWindowContents)),
                    postfix: new HarmonyMethod(typeof(AnimalPickerPatch), nameof(TabPostfix)));
            }
            catch (Exception e) { Log.Warning("[Clear Tabs] animal picker: " + e.Message); }
        }

        public static bool On => MTMod.S != null && MTMod.S.animalPicker;

        public static bool MasterPrefix(Rect rect, Pawn pawn)
        {
            if (!On || pawn?.playerSettings == null || Event.current.shift) return true;
            try
            {
                string label = TrainableUtility.MasterString(pawn);
                if (Widgets.ButtonText(rect, label.Truncate(rect.width)))
                {
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    Find.WindowStack.Add(new Dialog_MasterPicker(pawn));
                }
                TooltipHandler.TipRegion(rect, label + "\n\n" + "MT_PC_MasterTip".Translate());
                return false;
            }
            catch (Exception e)
            {
                Log.ErrorOnce("[Clear Tabs] master picker: " + e, 0x4d5c11);
                return true;
            }
        }

        public static void TabPostfix(Rect rect)
        {
            if (!On || Find.CurrentMap == null) return;
            Rect b = new Rect(rect.x + Mathf.Min(rect.width, 260f) + 8f, rect.y, 220f, 32f);
            if (b.xMax > rect.xMax) return;
            if (Widgets.ButtonText(b, "MT_PC_Overview".Translate()))
                Find.WindowStack.Add(new Dialog_AnimalOverview(Find.CurrentMap));
        }
    }

    public class Dialog_MasterPicker : Window
    {
        private readonly Pawn animal;
        private Vector2 scroll;
        private string search = "";

        public override Vector2 InitialSize => new Vector2(760f, 560f);

        public Dialog_MasterPicker(Pawn animal)
        {
            this.animal = animal;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect full = new Rect(0f, 0f, windowRect.width, windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), "MT_PC_MasterTitle".Translate(animal.LabelShortCap), MUI.Text1, GameFont.Medium);
            y += 38f;
            Rect sr = new Rect(r.x, y, 260f, 28f);
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName("MT_MasterSearch");
            Look.EscUnfocus("MT_MasterSearch");
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), "MT_PC_Search".Translate(), MUI.Muted);
            Rect none = new Rect(sr.xMax + 12f, y, 160f, 28f);
            bool noneOn = animal.playerSettings.Master == null;
            MUI.Fill(none, noneOn ? Look.GreenBg : Mouse.IsOver(none) ? Look.Card2 : Look.Card);
            MUI.Frame(none, noneOn ? Look.Green : Look.Line);
            Look.Text(none, "MT_PC_NoMaster".Translate(), noneOn ? Look.Green : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter);
            if (Widgets.ButtonInvisible(none)) { animal.playerSettings.Master = null; Close(); return; }
            y += 38f;

            int need = TrainableUtility.MinimumHandlingSkill(animal);
            string q = (search ?? "").Trim();
            var pawns = PawnsFinder.AllMaps_FreeColonistsSpawned
                .Where(p => q.Length == 0 || p.LabelShortCap.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(p => TrainableUtility.CanBeMaster(p, animal))
                .ThenByDescending(p => p.relations != null && p.relations.DirectRelationExists(PawnRelationDefOf.Bond, animal))
                .ThenByDescending(p => p.skills?.GetSkill(SkillDefOf.Animals)?.Level ?? 0).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            int cols = Mathf.Max(1, Mathf.FloorToInt((outR.width - 16f + 8f) / 300f));
            float cw = (outR.width - 16f - (cols - 1) * 8f) / cols, ch = 64f;
            int rows = Mathf.CeilToInt(pawns.Count / (float)cols);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows * (ch + 8f), 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < pawns.Count; i++)
                {
                    Pawn p = pawns[i];
                    Rect c = new Rect((i % cols) * (cw + 8f), (i / cols) * (ch + 8f), cw, ch);
                    bool can = TrainableUtility.CanBeMaster(p, animal);
                    bool cur = animal.playerSettings.Master == p;
                    bool over = Mouse.IsOver(c) && can;
                    MUI.Fill(c, cur ? Look.GreenBg : over ? Look.Card2 : Look.Card);
                    MUI.Frame(c, cur ? Look.Green : Look.Line);
                    Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
                    MUI.Fill(icon, Look.Bg);
                    Widgets.ThingIcon(icon.ContractedBy(2f), p);
                    float tx = icon.xMax + 8f, tw = c.xMax - tx - 8f;
                    Look.Text(new Rect(tx, c.y + 4f, tw, 22f), p.LabelShortCap.Truncate(tw), cur ? Look.Green : can ? MUI.Text1 : MUI.Muted);
                    int lvl = p.skills?.GetSkill(SkillDefOf.Animals)?.Level ?? 0;
                    string l2 = "MT_PC_Handling".Translate(lvl);
                    if (!can && lvl < need) l2 += " · " + "MT_PC_SkillLow".Translate(need);
                    Look.Text(new Rect(tx, c.y + 24f, tw, 18f), l2.Truncate(tw), !can && lvl < need ? Look.Red : MUI.Muted, GameFont.Tiny);
                    float bx = tx;
                    if (p.relations != null && p.relations.DirectRelationExists(PawnRelationDefOf.Bond, animal))
                        bx = Dialog_MechPicker.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_Bonded".Translate(), Look.Orange);
                    int pets = PawnsFinder.AllMaps_SpawnedPawnsInFaction(Faction.OfPlayer).Count(a => a.RaceProps.Animal && a.playerSettings?.Master == p);
                    if (pets > 0) Dialog_MechPicker.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_HasAnimals".Translate(pets), Look.Line);
                    if (can && Widgets.ButtonInvisible(c))
                    {
                        animal.playerSettings.Master = p;
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                        Close();
                        break;
                    }
                }
                if (pawns.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PC_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }
    }

    public class Dialog_AnimalOverview : Window
    {
        private enum Group { All, Livestock, Work, Combat, Pets }

        private readonly Map map;
        private Group group = Group.All;
        private string search = "";
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(1040f, 700f);

        public Dialog_AnimalOverview(Map map)
        {
            this.map = map;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            draggable = true;
            doWindowBackground = false;
            drawShadow = true;
        }

        private static Group GroupOf(Pawn a)
        {
            try
            {
                if (a.TryGetComp<CompMilkable>() != null || a.TryGetComp<CompShearable>() != null || a.TryGetComp<CompEggLayer>() != null)
                    return Group.Livestock;
                if (a.training != null && TrainableDefOf.Release != null && a.training.HasLearned(TrainableDefOf.Release))
                    return Group.Combat;
                if (a.training != null && a.training.HasLearned(TrainableDefOf.Obedience) && a.RaceProps.trainability != null && a.RaceProps.baseBodySize >= 1f)
                    return Group.Work;
            }
            catch { }
            return Group.Pets;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Rect full = new Rect(0f, 0f, windowRect.width, windowRect.height);
            MUI.Fill(full, Look.Bg);
            MUI.Frame(full, Look.Line);
            MUI.Fill(new Rect(0f, 0f, full.width, 3f), Look.Green);
            Rect r = inRect;
            float y = r.y;
            Look.Text(new Rect(r.x, y, r.width - 40f, 32f), "MT_PC_OverviewTitle".Translate(), MUI.Text1, GameFont.Medium);
            y += 38f;
            var animals = map.mapPawns.ColonyAnimals.Where(a => a.Spawned && !a.Dead).ToList();
            var groups = animals.ToDictionary(a => a, GroupOf);
            float x = r.x;
            foreach (Group g in Enum.GetValues(typeof(Group)))
            {
                int n = g == Group.All ? animals.Count : groups.Count(kv => kv.Value == g);
                if (n == 0 && g != Group.All) continue;
                string lab = ("MT_PC_AG_" + g).Translate() + " (" + n + ")";
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
            Rect sr = new Rect(r.xMax - 260f, y, 260f, 28f);
            MUI.Fill(sr, Look.Card);
            MUI.Frame(sr, Look.Line);
            GUI.SetNextControlName("MT_AnimalSearch");
            Look.EscUnfocus("MT_AnimalSearch");
            search = Widgets.TextField(new Rect(sr.x + 4f, sr.y + 3f, sr.width - 8f, 22f), search ?? "");
            if (search.NullOrEmpty()) Look.Text(new Rect(sr.x + 8f, sr.y, sr.width - 8f, sr.height), "MT_PC_Search".Translate(), MUI.Muted);
            y += 38f;

            string q = (search ?? "").Trim();
            var shown = animals.Where(a => (group == Group.All || groups[a] == group)
                    && (q.Length == 0 || a.LabelShortCap.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || a.def.label.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0))
                .OrderBy(a => groups[a]).ThenBy(a => a.def.label).ThenBy(a => a.LabelShortCap).ToList();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            int cols = Mathf.Max(1, Mathf.FloorToInt((outR.width - 16f + 8f) / 330f));
            float cw = (outR.width - 16f - (cols - 1) * 8f) / cols, ch = 96f;
            int rows = Mathf.CeilToInt(shown.Count / (float)cols);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows * (ch + 8f), 10f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < shown.Count; i++)
                {
                    Rect c = new Rect((i % cols) * (cw + 8f), (i / cols) * (ch + 8f), cw, ch);
                    if (c.yMax < scroll.y - 10f || c.y > scroll.y + outR.height + 10f) continue;
                    if (DrawCard(c, shown[i], groups[shown[i]])) break;
                }
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_PC_None".Translate(), MUI.Muted);
            }
            finally { Widgets.EndScrollView(); }
        }

        // true when the list changed (stop drawing this frame)
        private bool DrawCard(Rect c, Pawn a, Group g)
        {
            MUI.Fill(c, Mouse.IsOver(c) ? Look.Card2 : Look.Card);
            MUI.Frame(c, Look.Line);
            Rect icon = new Rect(c.x + 8f, c.y + 8f, 48f, 48f);
            MUI.Fill(icon, Look.Bg);
            Widgets.ThingIcon(icon.ContractedBy(2f), a);
            float tx = icon.xMax + 8f, tw = c.xMax - tx - 8f;
            Rect nameR = new Rect(tx, c.y + 4f, tw - 28f, 22f);
            Look.Text(nameR, a.LabelShortCap.Truncate(nameR.width), MUI.Text1);
            if (Widgets.ButtonInvisible(nameR)) { CameraJumper.TryJumpAndSelect(a); }
            Widgets.InfoCardButton(c.xMax - 28f, c.y + 4f, a);
            string l2 = a.def.LabelCap + " · " + ("MT_PC_AG_" + g).Translate();
            string master = a.playerSettings?.Master != null ? a.playerSettings.Master.LabelShortCap : null;
            if (master != null) l2 += " · " + "MT_PC_MasterIs".Translate(master);
            Look.Text(new Rect(tx, c.y + 24f, tw, 18f), l2.Truncate(tw), MUI.Muted, GameFont.Tiny);
            float hp = a.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
            float bx = tx;
            bx = Dialog_MechPicker.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_Health".Translate((hp * 100f).ToString("0")), hp < 0.6f ? Look.Red : Look.Line);
            if (a.ageTracker != null) bx = Dialog_MechPicker.Badge(new Rect(bx, c.y + 43f, 0f, 16f), "MT_PC_Age".Translate(a.ageTracker.AgeBiologicalYears), Look.Line);

            // training toggles
            float ty = c.y + 66f, txx = c.x + 8f;
            if (a.training != null)
            {
                foreach (TrainableDef td in TrainableUtility.TrainableDefsInListOrder)
                {
                    bool visible;
                    AcceptanceReport ok = a.training.CanAssignToTrain(td, out visible);
                    if (!visible || td == TrainableDefOf.Tameness) continue;
                    bool learned = a.training.HasLearned(td);
                    bool wanted = a.training.GetWanted(td);
                    string lab = td.LabelCap;
                    Text.Font = GameFont.Tiny;
                    float w = Text.CalcSize(lab).x + 14f;
                    if (txx + w > c.xMax - 120f) break;
                    Rect b = new Rect(txx, ty, w, 20f);
                    Color col = learned ? Look.Green : wanted ? Look.Blue : Look.Line;
                    MUI.Fill(b, wanted ? Look.GreenBg : Look.Bg);
                    MUI.Frame(b, ok.Accepted ? col : Look.Line);
                    Look.Text(b, lab, ok.Accepted ? (learned ? Look.Green : wanted ? Look.Blue : MUI.Muted) : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                    TooltipHandler.TipRegion(b, ok.Accepted ? (string)"MT_PC_TrainTip".Translate(td.LabelCap) : ok.Reason ?? "");
                    if (ok.Accepted && Widgets.ButtonInvisible(b))
                    {
                        a.training.SetWantedRecursive(td, !wanted);
                        SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                    }
                    txx = b.xMax + 4f;
                }
            }

            // slaughter / release
            DesignationManager dm = a.Map?.designationManager;
            if (dm != null)
            {
                bool slaughter = dm.DesignationOn(a, DesignationDefOf.Slaughter) != null;
                Rect sb = new Rect(c.xMax - 112f, ty, 50f, 20f);
                MUI.Fill(sb, slaughter ? new Color32(60, 30, 28, 255) : Look.Bg);
                MUI.Frame(sb, slaughter ? Look.Red : Look.Line);
                Look.Text(sb, "MT_PC_Slaughter".Translate(), slaughter ? Look.Red : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                TooltipHandler.TipRegion(sb, "MT_PC_SlaughterTip".Translate());
                if (Widgets.ButtonInvisible(sb))
                {
                    if (slaughter) dm.RemoveAllDesignationsOn(a);
                    else { dm.RemoveAllDesignationsOn(a); dm.AddDesignation(new Designation(a, DesignationDefOf.Slaughter)); }
                    SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                }
                if (DesignationDefOf.ReleaseAnimalToWild != null)
                {
                    bool release = dm.DesignationOn(a, DesignationDefOf.ReleaseAnimalToWild) != null;
                    Rect rb = new Rect(c.xMax - 58f, ty, 50f, 20f);
                    MUI.Fill(rb, release ? new Color32(48, 40, 24, 255) : Look.Bg);
                    MUI.Frame(rb, release ? Look.Orange : Look.Line);
                    Look.Text(rb, "MT_PC_Release".Translate(), release ? Look.Orange : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                    TooltipHandler.TipRegion(rb, "MT_PC_ReleaseTip".Translate());
                    if (Widgets.ButtonInvisible(rb))
                    {
                        if (release) dm.RemoveAllDesignationsOn(a);
                        else
                        {
                            Pawn target = a;
                            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MT_PC_ReleaseConfirm".Translate(target.LabelShortCap), () =>
                            {
                                DesignationManager d = target.Map?.designationManager;
                                if (d == null) return;
                                d.RemoveAllDesignationsOn(target);
                                d.AddDesignation(new Designation(target, DesignationDefOf.ReleaseAnimalToWild));
                            }, destructive: true));
                        }
                    }
                }
            }
            return false;
        }
    }
}
