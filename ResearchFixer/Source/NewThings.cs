using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchFixer
{
    // 0.2.0: things unlocked by research get a NEW mark until they are made / built the first time
    public class RF_NewThings : GameComponent
    {
        public HashSet<string> recipes = new HashSet<string>();
        public HashSet<string> builds = new HashSet<string>();

        public RF_NewThings(Game game) { }

        public static RF_NewThings Get => Current.Game?.GetComponent<RF_NewThings>();

        public static bool On => RFMod.Settings != null && RFMod.Settings.markNew;

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref recipes, "recipes", LookMode.Value);
            Scribe_Collections.Look(ref builds, "builds", LookMode.Value);
            if (recipes == null) recipes = new HashSet<string>();
            if (builds == null) builds = new HashSet<string>();
        }

        public void Notify_Finished(ResearchProjectDef proj)
        {
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
                if (r.researchPrerequisite == proj || (r.researchPrerequisites != null && r.researchPrerequisites.Contains(proj)))
                    if (r.AvailableNow) recipes.Add(r.defName);
            foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading)
                if (t.BuildableByPlayer && t.researchPrerequisites != null && t.researchPrerequisites.Contains(proj) && t.IsResearchFinished)
                    builds.Add(t.defName);
            foreach (TerrainDef t in DefDatabase<TerrainDef>.AllDefsListForReading)
                if (t.BuildableByPlayer && t.researchPrerequisites != null && t.researchPrerequisites.Contains(proj) && t.IsResearchFinished)
                    builds.Add(t.defName);
        }

        public static void ClearAll()
        {
            RF_NewThings c = Get;
            if (c == null) return;
            c.recipes.Clear();
            c.builds.Clear();
        }

        public const string Green = "#7ED957";
    }

    [HarmonyPatch(typeof(ResearchManager), nameof(ResearchManager.FinishProject))]
    public static class Patch_FinishProject
    {
        public static void Postfix(ResearchProjectDef proj)
        {
            try
            {
                if (!RF_NewThings.On || proj == null) return;
                RF_NewThings.Get?.Notify_Finished(proj);
            }
            catch (Exception e) { Log.WarningOnce("[Research Fixer] new things: " + e.Message, 0x4f11001); }
        }
    }

    // the workbench "Add bill" menu
    [HarmonyPatch(typeof(FloatMenu), MethodType.Constructor, new[] { typeof(List<FloatMenuOption>) })]
    public static class Patch_BillMenu
    {
        public static void Prefix(List<FloatMenuOption> options)
        {
            try
            {
                if (!RF_NewThings.On || options == null) return;
                RF_NewThings c = RF_NewThings.Get;
                if (c == null || c.recipes.Count == 0) return;
                var table = Find.Selector?.SingleSelectedThing as IBillGiver;
                if (table == null) return;
                var labels = new Dictionary<string, bool>();
                foreach (string dn in c.recipes)
                {
                    RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(dn);
                    if (r != null) labels[r.LabelCap.Resolve()] = true;
                }
                string tag = "  <color=" + RF_NewThings.Green + ">" + "RF_New".Translate() + "</color>";
                foreach (FloatMenuOption o in options)
                    if (o != null && o.Label != null && labels.ContainsKey(o.Label))
                        o.Label = "<color=" + RF_NewThings.Green + ">●</color> " + o.Label + tag;
            }
            catch (Exception e) { Log.WarningOnce("[Research Fixer] bill menu: " + e.Message, 0x4f11002); }
        }
    }

    [HarmonyPatch(typeof(Bill_Production), nameof(Bill_Production.Notify_IterationCompleted))]
    public static class Patch_BillDone
    {
        public static void Postfix(Bill_Production __instance)
        {
            try { if (__instance?.recipe != null) RF_NewThings.Get?.recipes.Remove(__instance.recipe.defName); }
            catch { }
        }
    }

    // the Architect menu: a green dot on new buildings
    [HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.GizmoOnGUI))]
    public static class Patch_BuildGizmo
    {
        public static void Postfix(Designator_Build __instance, Vector2 topLeft, float maxWidth)
        {
            try
            {
                if (!RF_NewThings.On || Event.current.type != EventType.Repaint) return;
                RF_NewThings c = RF_NewThings.Get;
                if (c == null || c.builds.Count == 0 || __instance.PlacingDef == null || !c.builds.Contains(__instance.PlacingDef.defName)) return;
                float w = __instance.GetWidth(maxWidth);
                Rect dot = new Rect(topLeft.x + w - 22f, topLeft.y + 1f, 22f, 22f);
                GameFont f = Text.Font;
                TextAnchor an = Text.Anchor;
                Text.Font = GameFont.Medium;
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(dot, "<color=" + RF_NewThings.Green + ">●</color>");
                Text.Anchor = an;
                Text.Font = f;
                TooltipHandler.TipRegion(new Rect(topLeft.x, topLeft.y, w, 75f), "RF_NewTip".Translate());
            }
            catch (Exception e) { Log.WarningOnce("[Research Fixer] build menu: " + e.Message, 0x4f11003); }
        }
    }

    [HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
    public static class Patch_FrameDone
    {
        public static void Prefix(Frame __instance)
        {
            try
            {
                BuildableDef b = __instance?.def?.entityDefToBuild;
                if (b != null) RF_NewThings.Get?.builds.Remove(b.defName);
            }
            catch { }
        }
    }
}

namespace ResearchFixer
{
    // 0.2.0: Yet Another Research Tree replaces the research window – the button goes into its bottom bar
    public static class YartButton
    {
        private static Type yart;

        private static bool tried;

        // after all mods are loaded (YART may load after Research Fixer)
        public static void TryPatch()
        {
            if (tried) return;
            tried = true;
            var h = new Harmony("misak.researchfixer.yart");
            try
            {
                yart = AccessTools.TypeByName("YART.MainTabWindow_YART");
                if (yart == null) return;
                System.Reflection.MethodInfo m = AccessTools.Method(yart, "DoWindowContents");
                if (m == null) return;
                h.Patch(m, prefix: new HarmonyMethod(typeof(YartButton), nameof(Prefix)), postfix: new HarmonyMethod(typeof(YartButton), nameof(Postfix)));
                Log.Message("[Research Fixer] button added to Yet Another Research Tree.");
            }
            catch (Exception e) { Log.Warning("[Research Fixer] YART button: " + e.Message); }
        }

        // right of YART's zoom / vanilla switch / settings buttons (they move when its left panel is open)
        private static Rect Btn(object window, Rect inRect)
        {
            float left = 0f;
            try
            {
                object hist = Traverse.Create(window).Field("history").GetValue();
                bool has = hist != null && Traverse.Create(hist).Property("HasCurrent").GetValue<bool>();
                if (has) left = Mathf.Max(300f, inRect.width * 0.25f);
            }
            catch { }
            float x = left + 16f + 80f + 6f + 24f + 6f + 24f + 10f;
            return new Rect(x, inRect.yMax - 40f, 140f, 24f);
        }

        private static bool Show => RFMod.Settings != null && RFMod.Settings.researchButton;

        // the graph canvas uses the mouse first – catch the click before it
        public static void Prefix(object __instance, Rect inRect)
        {
            try
            {
                if (!Show) return;
                Event ev = Event.current;
                if (ev.type == EventType.MouseDown && ev.button == 0 && Mouse.IsOver(Btn(__instance, inRect)))
                {
                    ev.Use();
                    Find.WindowStack.Add(new Dialog_ResearchFixer());
                }
            }
            catch (Exception e) { Log.WarningOnce("[Research Fixer] YART button: " + e.Message, 0x7f31002); }
        }

        public static void Postfix(object __instance, Rect inRect)
        {
            try
            {
                if (!Show) return;
                Rect b = Btn(__instance, inRect);
                GameFont f = Text.Font;
                Text.Font = GameFont.Small;
                Widgets.ButtonText(b, "Research Fixer");
                Text.Font = f;
            }
            catch (Exception e) { Log.WarningOnce("[Research Fixer] YART button: " + e.Message, 0x7f31003); }
        }
    }
}
