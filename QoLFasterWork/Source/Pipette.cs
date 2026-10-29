using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace QoLFW
{
    [DefOf]
    public static class QFWDefOf
    {
        public static KeyBindingDef QFW_Pipette;
        static QFWDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(QFWDefOf)); }
    }

    // The key is read with the game's own low-priority shortcuts (after windows and
    // text fields had their turn, so typing a "p" somewhere doesn't start it).
    [HarmonyPatch(typeof(MainButtonsRoot), "HandleLowPriorityShortcuts")]
    public static class Patch_PipetteKey
    {
        public static void Postfix()
        {
            try { Pipette.OnGUI(); } catch (Exception e) { Log.WarningOnce("[QoL Faster Work] pipette key: " + e.Message, 81723404); }
        }
    }

    // Pipette: press the key, click anything on the map – you get its build
    // designator in hand (same building, material, style and rotation).
    // Floors give the floor, items offer a bill at a workbench.
    [StaticConstructorOnStartup]
    public static class Pipette
    {
        public static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("QoLFW/Pipette", true);
        private static readonly AccessTools.FieldRef<Designator_Place, Rot4> rotRef =
            AccessTools.FieldRefAccess<Designator_Place, Rot4>("placingRot");

        public static string KeyLabel()
        {
            try { return QFWDefOf.QFW_Pipette.MainKeyLabel; } catch { return "P"; }
        }

        public static void OnGUI()
        {
            if (!QoLMod.S.pipette || Current.ProgramState != ProgramState.Playing || Find.CurrentMap == null) return;
            if (Event.current.type != EventType.KeyDown) return;
            if (RimWorld.Planet.WorldRendererUtility.WorldRendered) return;
            if (QFWDefOf.QFW_Pipette == null || !QFWDefOf.QFW_Pipette.KeyDownEvent) return;
            Event.current.Use();
            if (QoLMod.S.pipetteInstant)
            {
                IntVec3 c = UI.MouseCell();
                if (c.InBounds(Find.CurrentMap)) Pick(new LocalTargetInfo(c), true);
                return;
            }
            Start();
        }

        public static void Start()
        {
            if (Find.CurrentMap == null) return;
            if (Find.Targeter.IsTargeting) { Find.Targeter.StopTargeting(); return; }
            Find.DesignatorManager.Deselect();
            var tp = new TargetingParameters
            {
                canTargetLocations = true,
                canTargetBuildings = true,
                canTargetItems = true,
                canTargetPlants = true,
                canTargetPawns = false,
                canTargetFires = false,
                mapObjectTargetsMustBeAutoAttackable = false,
            };
            Find.Targeter.BeginTargeting(tp, t => Pick(t, false), null, null, Icon, false);
        }

        public static void Pick(LocalTargetInfo t, bool underMouse)
        {
            Map map = Find.CurrentMap;
            if (map == null) return;
            IntVec3 c = t.Cell;
            if (!c.InBounds(map)) return;
            if (c.Fogged(map)) { Q.Msg("QFW_PipFogged".Translate(), null, true); return; }

            var things = new List<Thing>();
            if (t.HasThing) things.Add(t.Thing);
            foreach (Thing th in map.thingGrid.ThingsListAt(c))
                if (!things.Contains(th)) things.Add(th);
            // blueprints and frames first, then buildings, then items
            things = things.OrderBy(Rank).ToList();

            foreach (Thing th in things)
                if (Rank(th) <= 2 && TryBuild(th)) return;
            foreach (Thing th in things)
                if (th.def.category == ThingCategory.Item) { BillMenu(th.def, map); return; }

            TerrainDef td = c.GetTerrain(map);
            if (td != null && td.BuildableByPlayer && TryBuildDef(td, null, null, null, Rot4.North)) return;
            TerrainDef under = map.terrainGrid.UnderTerrainAt(c);
            if (under != null && under.BuildableByPlayer && TryBuildDef(under, null, null, null, Rot4.North)) return;
            Q.Msg("QFW_PipNothing".Translate(), null, true);
        }

        private static int Rank(Thing t)
        {
            if (t is Blueprint_Build || t is Frame) return 0;
            if (t is MinifiedThing) return 2;
            if (t.def.category == ThingCategory.Building) return t.def.building != null && t.def.building.isNaturalRock ? 9 : 1;
            if (t.def.category == ThingCategory.Item) return 3;
            return 8;
        }

        private static bool TryBuild(Thing t)
        {
            if (t is Blueprint_Build bp)
                return TryBuildDef(bp.def.entityDefToBuild, bp.stuffToUse, bp.StyleSourcePrecept as Precept_Building, bp.StyleDef, bp.Rotation);
            if (t is Frame fr)
                return TryBuildDef(fr.def.entityDefToBuild, fr.Stuff, fr.StyleSourcePrecept as Precept_Building, fr.StyleDef, fr.Rotation);
            if (t is MinifiedThing m && m.InnerThing != null)
            {
                Thing inner = m.InnerThing;
                return TryBuildDef(inner.def, inner.Stuff, inner.StyleSourcePrecept as Precept_Building, inner.StyleDef, Rot4.North);
            }
            if (t.def.category == ThingCategory.Building)
            {
                if (!t.def.BuildableByPlayer)
                {
                    Q.Msg("QFW_PipCantBuild".Translate(t.LabelNoCount), t, true);
                    return true;
                }
                return TryBuildDef(t.def, t.Stuff, t.StyleSourcePrecept as Precept_Building, t.StyleDef, t.Rotation);
            }
            return false;
        }

        private static bool TryBuildDef(BuildableDef def, ThingDef stuff, Precept_Building precept, ThingStyleDef style, Rot4 rot)
        {
            if (def == null) return false;
            if (!def.BuildableByPlayer)
            {
                Q.Msg("QFW_PipCantBuild".Translate(def.label), null, true);
                return true;
            }
            Command cmd = BuildCopyCommandUtility.BuildCopyCommand(def, stuff, precept, style, true);
            if (cmd == null)
            {
                Q.Msg("QFW_PipLocked".Translate(def.label), null, true);
                return true;
            }
            if (cmd is Command_Action ca && ca.action != null) ca.action();
            else if (cmd is Designator d) { SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); Find.DesignatorManager.Select(d); }
            else return false;
            try
            {
                if (Find.DesignatorManager.SelectedDesignator is Designator_Place place && def is ThingDef td && td.rotatable)
                    rotRef(place) = rot;
            }
            catch { }
            return true;
        }

        // An item: offer a bill at a workbench that can make it.
        public static void BillMenu(ThingDef def, Map map)
        {
            var recipes = DefDatabase<RecipeDef>.AllDefsListForReading
                .Where(r => r.products != null && r.products.Any(p => p.thingDef == def) && r.AvailableNow).ToList();
            if (recipes.Count == 0)
            {
                Q.Msg("QFW_PipNoRecipe".Translate(def.label), null, true);
                return;
            }
            var opts = new List<FloatMenuOption>();
            var noBench = new List<string>();
            foreach (RecipeDef r in recipes)
            {
                var benches = map.listerBuildings.allBuildingsColonist
                    .Where(b => b is IBillGiver && b.def.AllRecipes != null && b.def.AllRecipes.Contains(r) && b.Spawned)
                    .Take(5).ToList();
                if (benches.Count == 0)
                {
                    if (r.AllRecipeUsers != null)
                        noBench.AddRange(r.AllRecipeUsers.Select(u => u.label));
                    continue;
                }
                foreach (Building b in benches)
                {
                    Building bench = b;
                    RecipeDef rec = r;
                    opts.Add(new FloatMenuOption("QFW_PipAddBill".Translate(rec.LabelCap, bench.LabelCap), () =>
                    {
                        Bill bill = rec.MakeNewBill();
                        ((IBillGiver)bench).BillStack.AddBill(bill);
                        Find.Selector.ClearSelection();
                        Find.Selector.Select(bench);
                        try { InspectPaneUtility.OpenTab(typeof(ITab_Bills)); } catch { }
                        Q.Msg("QFW_PipBillAdded".Translate(rec.LabelCap, bench.LabelCap), bench);
                    }, def));
                }
            }
            if (opts.Count == 0)
            {
                Q.Msg("QFW_PipNoBench".Translate(def.label, string.Join(", ", noBench.Distinct().Take(3).ToArray())), null, true);
                return;
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }
}
