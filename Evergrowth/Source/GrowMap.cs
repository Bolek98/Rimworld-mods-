using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // 1.9.0 "Where does it grow": a map overlay for one plant. Green = grows
    // well, yellow = grows slower, red = does not grow here; hover a cell for
    // the reason. Switched on from a grow zone or a hydroponics basin / planter.
    // The cells are checked a few thousand per frame and again every few seconds –
    // never the whole map in one frame.
    // ------------------------------------------------------------------
    public class GrowMapComp : MapComponent, ICellBoolGiver
    {
        public ThingDef plant;
        private byte[] status;          // 0 = not checked / fogged, otherwise Reason + 1
        private CellBoolDrawer drawer;
        private int cursor;
        private float nextCycle;
        private bool dirtyAfterCycle;
        private static int errors;

        public GrowMapComp(Map map) : base(map) { }

        public enum Reason : byte
        {
            Ok, TempSlow, DarkSlow, SoilSlow,                              // grows, maybe slower
            TooCold, TooHot, NoSoil, NeedsRoof, TooDark, Blocked, OnlyGrower  // does not grow
        }

        public static bool Bad(Reason r) => r >= Reason.TooCold;
        public static bool Slow(Reason r) => r != Reason.Ok && !Bad(r);

        public bool Active => plant != null;

        public void Toggle(ThingDef def)
        {
            if (def == null || plant == def)
            {
                plant = null;
                return;
            }
            plant = def;
            int n = map.cellIndices.NumGridCells;
            if (status == null || status.Length != n)
                status = new byte[n];
            else
                Array.Clear(status, 0, status.Length);
            cursor = 0;
            nextCycle = 0f;
            if (drawer == null)
                drawer = new CellBoolDrawer(this, map.Size.x, map.Size.z, 0.42f);
            drawer.SetDirty();
        }

        // ---- ICellBoolGiver ----
        public Color Color => Color.white;

        public bool GetCellBool(int index)
        {
            return status != null && index < status.Length && status[index] != 0;
        }

        public Color GetCellExtraColor(int index)
        {
            Reason r = (Reason)(status[index] - 1);
            if (Bad(r)) return new Color(0.88f, 0.32f, 0.27f);
            if (Slow(r)) return new Color(0.93f, 0.74f, 0.25f);
            return new Color(0.42f, 0.80f, 0.36f);
        }

        // ---- checking ----
        public static Reason Check(ThingDef def, IntVec3 c, Map map)
        {
            PlantProperties pp = def.plant;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            Building edifice = c.GetEdifice(map);
            Building_PlantGrower grower = edifice as Building_PlantGrower;
            if (edifice != null && grower == null && edifice.def.passability == Traversability.Impassable)
                return Reason.Blocked;
            if (pp.Sowable)
            {
                if (grower != null)
                {
                    string tag = grower.def.building?.sowTag;
                    if (tag != null && !pp.sowTags.Contains(tag))
                        return Reason.OnlyGrower;
                }
                else if (!pp.sowTags.Contains("Ground"))
                    return Reason.OnlyGrower;
            }
            float fert = map.fertilityGrid.FertilityAt(c);
            if (fert < pp.fertilityMin)
                return Reason.NoSoil;
            bool roofed = c.Roofed(map);
            if (pp.cavePlant && !roofed)
                return Reason.NeedsRoof;
            float glow = 1f;
            if (roofed && !pp.cavePlant)
            {
                glow = map.glowGrid.GroundGlowAt(c, false, true);
                if (glow < pp.growMinGlow)
                    return Reason.TooDark;
            }
            if (!PlantUtility.GrowthSeasonNow(c, map, def))
            {
                float t0 = c.GetTemperature(map);
                return t0 > pp.maxGrowthTemperature ? Reason.TooHot : Reason.TooCold;
            }
            float t = c.GetTemperature(map);
            bool greenhouse = s != null && s.greenhouse && roofed;
            if (!greenhouse && (t < pp.minOptimalGrowthTemperature || t > pp.maxOptimalGrowthTemperature))
                return Reason.TempSlow;
            if (roofed && !pp.cavePlant && glow < pp.growOptimalGlow)
                return Reason.DarkSlow;
            if (fert < 1f && pp.fertilitySensitivity > 0.01f && !(s != null && s.ignoreSoil))
                return Reason.SoilSlow;
            return Reason.Ok;
        }

        public override void MapComponentUpdate()
        {
            if (plant == null || status == null || Find.CurrentMap != map)
                return;
            try
            {
                if (cursor == 0 && Time.realtimeSinceStartup < nextCycle)
                {
                    drawer.MarkForDraw();
                    drawer.CellBoolDrawerUpdate();
                    return;
                }
                int n = status.Length;
                int end = Mathf.Min(n, cursor + 5000);
                for (int i = cursor; i < end; i++)
                {
                    IntVec3 c = map.cellIndices.IndexToCell(i);
                    if (c.Fogged(map))
                    {
                        status[i] = 0;
                        continue;
                    }
                    byte v = (byte)(Check(plant, c, map) + 1);
                    if (status[i] != v)
                    {
                        status[i] = v;
                        dirtyAfterCycle = true;
                    }
                }
                cursor = end;
                if (cursor >= n)
                {
                    cursor = 0;
                    nextCycle = Time.realtimeSinceStartup + 4f;
                    if (dirtyAfterCycle)
                        drawer.SetDirty();
                    dirtyAfterCycle = false;
                }
                drawer.MarkForDraw();
                drawer.CellBoolDrawerUpdate();
            }
            catch (Exception e)
            {
                plant = null;
                if (errors++ < 3)
                    Log.Warning("[Evergrowth] grow map switched off: " + e.Message);
            }
        }

        public override void MapComponentOnGUI()
        {
            if (plant == null || status == null || Find.CurrentMap != map || Event.current.type != EventType.Repaint)
                return;
            try
            {
                // banner
                string head = "HardyPlants_GrowMapBanner".Translate(plant.LabelCap);
                Text.Font = GameFont.Small;
                Vector2 hs = Text.CalcSize(head);
                Rect hr = new Rect((UI.screenWidth - hs.x) / 2f - 12f, 40f, hs.x + 24f, 30f);
                Widgets.DrawBoxSolid(hr, new Color(0f, 0f, 0f, 0.7f));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(hr, head);
                Text.Anchor = TextAnchor.UpperLeft;

                IntVec3 mc = UI.MouseCell();
                if (!mc.InBounds(map))
                    return;
                int idx = map.cellIndices.CellToIndex(mc);
                if (status[idx] == 0)
                    return;
                Reason r = (Reason)(status[idx] - 1);
                string text = ReasonText(r, mc);
                Vector2 mp = Event.current.mousePosition;
                float w = 300f;
                float h = Text.CalcHeight(text, w - 16f) + 12f;
                Rect box = new Rect(Mathf.Min(mp.x + 18f, UI.screenWidth - w - 4f), Mathf.Min(mp.y + 18f, UI.screenHeight - h - 4f), w, h);
                Widgets.DrawBoxSolid(box, new Color(0.08f, 0.09f, 0.1f, 0.92f));
                GUI.color = Bad(r) ? new Color(0.95f, 0.45f, 0.4f) : Slow(r) ? new Color(0.98f, 0.8f, 0.35f) : new Color(0.55f, 0.9f, 0.5f);
                Widgets.DrawBox(box);
                GUI.color = Color.white;
                Widgets.Label(box.ContractedBy(6f, 6f), text);
            }
            catch
            {
            }
        }

        private string ReasonText(Reason r, IntVec3 c)
        {
            PlantProperties pp = plant.plant;
            float t = c.GetTemperature(map);
            string temp = t.ToStringTemperature("F0");
            string range = pp.minGrowthTemperature.ToStringTemperature("F0") + " – " + pp.maxGrowthTemperature.ToStringTemperature("F0");
            string opt = pp.minOptimalGrowthTemperature.ToStringTemperature("F0") + " – " + pp.maxOptimalGrowthTemperature.ToStringTemperature("F0");
            float fert = map.fertilityGrid.FertilityAt(c);
            switch (r)
            {
                case Reason.Ok: return "HardyPlants_GM_Ok".Translate();
                case Reason.TempSlow: return "HardyPlants_GM_TempSlow".Translate(temp, opt);
                case Reason.DarkSlow: return "HardyPlants_GM_DarkSlow".Translate();
                case Reason.SoilSlow: return "HardyPlants_GM_SoilSlow".Translate(fert.ToStringPercent());
                case Reason.TooCold: return "HardyPlants_GM_TooCold".Translate(temp, range);
                case Reason.TooHot: return "HardyPlants_GM_TooHot".Translate(temp, range);
                case Reason.NoSoil: return "HardyPlants_GM_NoSoil".Translate(fert.ToStringPercent(), pp.fertilityMin.ToStringPercent());
                case Reason.NeedsRoof: return "HardyPlants_GM_NeedsRoof".Translate();
                case Reason.TooDark: return "HardyPlants_GM_TooDark".Translate();
                case Reason.Blocked: return "HardyPlants_GM_Blocked".Translate();
                default: return "HardyPlants_GM_OnlyGrower".Translate();
            }
        }

        public static GrowMapComp For(Map map) => map?.GetComponent<GrowMapComp>();

        public static Command Gizmo(Map map, ThingDef current)
        {
            GrowMapComp comp = For(map);
            if (comp == null || current == null || current.plant == null)
                return null;
            bool on = comp.plant == current;
            return new GrowMapCommand
            {
                comp = comp,
                defaultLabel = on ? "HardyPlants_GrowMapHide".Translate() : "HardyPlants_GrowMap".Translate(),
                defaultDesc = "HardyPlants_GrowMapDesc".Translate(current.LabelCap),
                icon = current.uiIcon ?? BaseContent.BadTex,
                action = delegate
                {
                    comp.Toggle(current);
                }
            };
        }
    }

    // right click: show another plant
    public class GrowMapCommand : Command_Action
    {
        public GrowMapComp comp;

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                foreach (ThingDef d in HardyPlantsCore.AllPlants().Where(p => p.plant.Sowable))
                {
                    ThingDef dd = d;
                    yield return new FloatMenuOption(d.LabelCap, () => comp.Toggle(dd), d);
                }
            }
        }
    }

    [HarmonyPatch(typeof(Zone_Growing), nameof(Zone_Growing.GetGizmos))]
    public static class Patch_GrowMapZoneGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Zone_Growing __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            Command c = null;
            try { c = GrowMapComp.Gizmo(__instance.Map, __instance.GetPlantDefToGrow()); } catch { }
            if (c != null)
                yield return c;
        }
    }

    [HarmonyPatch(typeof(Building_PlantGrower), nameof(Building_PlantGrower.GetGizmos))]
    public static class Patch_GrowMapGrowerGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building_PlantGrower __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            if (__instance.Faction != Faction.OfPlayer)
                yield break;
            Command c = null;
            try { c = GrowMapComp.Gizmo(__instance.Map, __instance.GetPlantDefToGrow()); } catch { }
            if (c != null)
                yield return c;
        }
    }
}
