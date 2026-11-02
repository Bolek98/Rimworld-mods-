using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace EvergrowthWild
{
    // ------------------------------------------------------------------
    // 1.2.0 extras: berries and herbs, pasture grass, protected plants,
    // presets / share code and "Where it grows" for wild plants.
    // ------------------------------------------------------------------
    public static class Extras
    {
        // ---------- what a plant is ----------
        private static readonly Dictionary<ThingDef, byte> kinds = new Dictionary<ThingDef, byte>();   // 0 other, 1 forage, 2 grass

        private static byte KindByte(ThingDef t)
        {
            if (t?.plant == null) return 0;
            if (kinds.TryGetValue(t, out byte k)) return k;
            k = 0;
            try
            {
                if (!t.plant.IsTree && !t.IsDeadPlant && !t.plant.isStump && !t.plant.cavePlant)
                {
                    ThingDef h = t.plant.harvestedThingDef;
                    if (h != null && ((h.IsNutritionGivingIngestible && h.ingestible != null && h.ingestible.HumanEdible) || h.IsMedicine))
                        k = 1;
                    else if (h == null && t.ingestible != null && (t.ingestible.foodType & FoodTypeFlags.Plant) != 0)
                        k = 2;
                }
            }
            catch { }
            kinds[t] = k;
            return k;
        }

        public static bool IsForage(ThingDef t) => KindByte(t) == 1;
        public static bool IsGrass(ThingDef t) => KindByte(t) == 2;

        public static List<ThingDef> ForagePlants()
        {
            WP.Snapshot();
            return WP.Plants.Where(t => IsForage(t) && WP.KindOf(t) == WP.Kind.Normal).ToList();
        }

        public static List<ThingDef> GrassPlants()
        {
            WP.Snapshot();
            return WP.Plants.Where(t => IsGrass(t) && WP.KindOf(t) == WP.Kind.Normal).ToList();
        }

        // ---------- protected plants ----------
        private static HashSet<ThingDef> prot;
        private static int protCount = -1;

        public static bool Protected(ThingDef t)
        {
            WPSettings s = WP.S;
            if (s == null || t == null || s.protectedPlants.Count == 0) return false;
            if (prot == null || protCount != s.protectedPlants.Count)
            {
                prot = new HashSet<ThingDef>(s.protectedPlants.Select(n => DefDatabase<ThingDef>.GetNamedSilentFail(n)).Where(d => d != null));
                protCount = s.protectedPlants.Count;
            }
            return prot.Contains(t);
        }

        public static void ProtectedChanged() { protCount = -1; }

        // a wild (not sown) plant of a protected kind
        public static bool ProtectedWild(Thing t)
        {
            return t is Plant p && !p.sown && Protected(p.def);
        }

        // ---------- presets ----------
        public static void Preset(int which)
        {
            WPSettings s = WP.S;
            WP.Snapshot();
            if (which == 0)
            {
                s.lists.Clear(); s.density.Clear(); s.regrow.Clear(); s.cave.Clear();
                s.forageCommon = 1f; s.forageRegrow = 1f; s.grassRegrow = 1f;
                return;
            }
            s.recommended = false;
            float dens = which == 1 ? 1.8f : 0.45f;
            float reg = which == 1 ? 0.5f : 1.8f;
            foreach (BiomeDef b in WP.Biomes)
            {
                float od = WP.OrigDensity(b);
                if (od > 0f) s.density[b.defName] = Mathf.Min(od * dens, Mathf.Max(3f, od * 2f));
                else s.density.Remove(b.defName);
                s.regrow[b.defName] = Mathf.Clamp(WP.OrigRegrow(b) * reg, 1f, Mathf.Max(100f, WP.OrigRegrow(b) * 2f));
            }
            s.grassRegrow = which == 1 ? 1.5f : 0.75f;
        }

        // ---------- share code ----------
        private const string CodeHead = "EWP1:";

        private static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        public static string Export()
        {
            WPSettings s = WP.S;
            var sb = new StringBuilder();
            foreach (var kv in s.lists) sb.Append("L\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            foreach (var kv in s.density) sb.Append("D\t").Append(kv.Key).Append('\t').Append(F(kv.Value)).Append('\n');
            foreach (var kv in s.regrow) sb.Append("R\t").Append(kv.Key).Append('\t').Append(F(kv.Value)).Append('\n');
            foreach (var kv in s.cave) sb.Append("C\t").Append(kv.Key).Append('\t').Append(F(kv.Value)).Append('\n');
            sb.Append("F\tforage\t").Append(F(s.forageCommon)).Append('\n');
            sb.Append("F\tforageRegrow\t").Append(F(s.forageRegrow)).Append('\n');
            sb.Append("F\tgrass\t").Append(F(s.grassRegrow)).Append('\n');
            sb.Append("F\tpensOnly\t").Append(s.grassPensOnly ? "1" : "0").Append('\n');
            sb.Append("P\tprotected\t").Append(string.Join(",", s.protectedPlants)).Append('\n');
            return CodeHead + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        // returns the number of entries read, or -1 when the text is not a code of this mod
        public static int Import(string code)
        {
            if (code == null) return -1;
            code = code.Trim();
            if (!code.StartsWith(CodeHead)) return -1;
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(code.Substring(CodeHead.Length))); }
            catch { return -1; }
            WPSettings s = WP.S;
            var lists = new Dictionary<string, string>();
            var dens = new Dictionary<string, float>();
            var reg = new Dictionary<string, float>();
            var cave = new Dictionary<string, float>();
            int n = 0;
            float fc = 1f, fr = 1f, gr = 1f;
            bool pens = false;
            var protList = new List<string>();
            foreach (string line in text.Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v);
                switch (p[0])
                {
                    case "L": lists[p[1]] = p[2]; n++; break;
                    case "D": dens[p[1]] = v; n++; break;
                    case "R": reg[p[1]] = v; n++; break;
                    case "C": cave[p[1]] = v; n++; break;
                    case "F":
                        if (p[1] == "forage") fc = v;
                        else if (p[1] == "forageRegrow") fr = v;
                        else if (p[1] == "grass") gr = v;
                        else if (p[1] == "pensOnly") pens = p[2] == "1";
                        n++;
                        break;
                    case "P":
                        protList = p[2].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
                        n++;
                        break;
                }
            }
            if (n == 0) return -1;
            s.lists = lists; s.density = dens; s.regrow = reg; s.cave = cave;
            s.forageCommon = Mathf.Clamp(fc, 0f, 5f);
            s.forageRegrow = Mathf.Clamp(fr, 0.25f, 5f);
            s.grassRegrow = Mathf.Clamp(gr, 0.25f, 5f);
            s.grassPensOnly = pens;
            s.protectedPlants = protList;
            s.recommended = false;
            ProtectedChanged();
            return n;
        }

        // ---------- animal pens ----------
        private class PenGrid { public int tick = -99999; public bool[] cells; }
        private static readonly Dictionary<Map, PenGrid> pens = new Dictionary<Map, PenGrid>();

        public static bool InPen(Map map, IntVec3 c)
        {
            if (map == null) return false;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (!pens.TryGetValue(map, out PenGrid g))
                pens[map] = g = new PenGrid();
            if (g.cells == null || now - g.tick > 2500 || now < g.tick)
            {
                g.tick = now;
                g.cells = new bool[map.cellIndices.NumGridCells];
                try
                {
                    foreach (Building b in map.listerBuildings.allBuildingsColonist)
                    {
                        CompAnimalPenMarker m = b.TryGetComp<CompAnimalPenMarker>();
                        if (m == null) continue;
                        PenMarkerState st = m.PenState;
                        if (st == null || !st.Enclosed) continue;
                        foreach (Region r in st.ConnectedRegions)
                            foreach (IntVec3 cell in r.Cells)
                                g.cells[map.cellIndices.CellToIndex(cell)] = true;
                    }
                }
                catch { }
            }
            return c.InBounds(map) && g.cells[map.cellIndices.CellToIndex(c)];
        }

        public static void ForgetMaps() { pens.Clear(); }
    }

    // berries / herbs regrow after harvest and grass regrows at their own speed (wild plants only)
    [HarmonyPatch(typeof(Plant), nameof(Plant.GrowthRate), MethodType.Getter)]
    public static class Patch_GrowthRate
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            WPSettings s = WP.S;
            if (s == null || __result <= 0f || __instance.sown)
                return;
            if (s.forageRegrow != 1f && Extras.IsForage(__instance.def))
                __result *= s.forageRegrow;
            else if (s.grassRegrow != 1f && Extras.IsGrass(__instance.def)
                && (!s.grassPensOnly || Extras.InPen(__instance.MapHeld, __instance.PositionHeld)))
                __result *= s.grassRegrow;
        }
    }

    // protected wild plants: never cut by a job (sowing, building, clearing), unless that plant
    // itself has a cut / harvest order
    [HarmonyPatch(typeof(PlantUtility), nameof(PlantUtility.PawnWillingToCutPlant_Job))]
    public static class Patch_WillingToCut
    {
        public static void Postfix(Thing plant, ref bool __result)
        {
            if (!__result || !Extras.ProtectedWild(plant))
                return;
            Map m = plant.MapHeld;
            if (m != null && (m.designationManager.DesignationOn(plant, DesignationDefOf.CutPlant) != null
                || m.designationManager.DesignationOn(plant, DesignationDefOf.HarvestPlant) != null))
                return;
            __result = false;
        }
    }

    // dragging a cut / chop order over an area skips protected plants; clicking one plant still works
    [HarmonyPatch]
    public static class Patch_DesignateProtected
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            foreach (Type t in new[] { typeof(Designator_PlantsCut), typeof(Designator_PlantsHarvestWood) })
            {
                var m = AccessTools.Method(t, "CanDesignateThing", new[] { typeof(Thing) });
                if (m != null && m.DeclaringType == t)
                    yield return m;
            }
        }

        public static void Postfix(Thing t, ref AcceptanceReport __result)
        {
            if (!__result.Accepted || !Extras.ProtectedWild(t))
                return;
            DesignationDragger d = Find.DesignatorManager?.Dragger;
            if (d != null && d.Dragging && d.DragCells.Count > 1)
                __result = "EWP_ProtectedSkip".Translate(t.LabelShort);
        }
    }

    // berries and herbs: how common they are among the wild plants of every biome
    public static class ForageApply
    {
        public static float Mult(ThingDef t)
        {
            WPSettings s = WP.S;
            return s != null && s.forageCommon != 1f && Extras.IsForage(t) ? s.forageCommon : 1f;
        }
    }

    // ------------------------------------------------------------------
    // "Where it grows" – green / yellow / red cells for one wild plant on
    // the current map, a few thousand cells per frame, the reason under the mouse.
    // ------------------------------------------------------------------
    public static class WhereGrows
    {
        public static ThingDef plant;
        public static Map map;
        private static byte[] shown, work;
        private static int pos;
        private static float nextPass;
        private static CellBoolDrawer drawer;
        private static bool inBiome;
        private static readonly Color Green = new Color(0.30f, 0.85f, 0.35f), Yellow = new Color(0.95f, 0.85f, 0.25f), Red = new Color(0.90f, 0.25f, 0.20f);
        public static bool Active => plant != null && map != null;

        public static void Start(Map m, ThingDef t)
        {
            if (m == null || t?.plant == null) return;
            map = m;
            plant = t;
            int n = m.cellIndices.NumGridCells;
            shown = new byte[n];
            work = new byte[n];
            pos = 0;
            nextPass = 0f;
            inBiome = WP.ActiveList(m.Biome).TryGetValue(t, out float cv) && cv > 0f;
            drawer = new CellBoolDrawer(i => shown != null && i < shown.Length && shown[i] != 0, () => Color.white,
                i => shown[i] == 1 ? Green : shown[i] == 2 ? Yellow : Red, m.Size.x, m.Size.z, 0.4f);
        }

        public static void Stop()
        {
            plant = null;
            map = null;
            shown = work = null;
            drawer = null;
        }

        public static void Update()
        {
            if (!Active) return;
            if (Find.CurrentMap != map || map.Disposed)
            {
                if (map.Disposed) Stop();
                return;
            }
            if (Time.realtimeSinceStartup >= nextPass)
            {
                int n = work.Length;
                int end = Math.Min(n, pos + 4000);
                for (int i = pos; i < end; i++)
                    work[i] = Eval(map.cellIndices.IndexToCell(i), out _);
                pos = end;
                if (pos >= n)
                {
                    byte[] t = shown; shown = work; work = t;
                    pos = 0;
                    nextPass = Time.realtimeSinceStartup + 3f;
                    drawer.SetDirty();
                }
            }
            drawer.MarkForDraw();
            drawer.CellBoolDrawerUpdate();
        }

        // 0 = unknown (fog), 1 = grows well, 2 = slower / not now, 3 = never
        public static byte Eval(IntVec3 c, out string why)
        {
            why = null;
            ThingDef t = plant;
            if (!c.InBounds(map) || c.Fogged(map)) return 0;
            RoofDef roof = c.GetRoof(map);
            if (t.plant.cavePlant)
            {
                if (roof == null || !roof.isNatural) { why = "EWP_WG_NeedsRoof".Translate(); return 3; }
            }
            else if (roof != null && !roof.isNatural)
            {
                why = "EWP_WG_BuiltRoof".Translate();
                return 3;
            }
            Building ed = c.GetEdifice(map);
            if (ed != null && ed.def.passability == Traversability.Impassable) { why = "EWP_WG_Built".Translate(); return 3; }
            TerrainDef ter = c.GetTerrain(map);
            if (t.terrainAffordanceNeeded != null && ter != null && !ter.affordances.Contains(t.terrainAffordanceNeeded))
            {
                why = "EWP_WG_Terrain".Translate(ter.LabelCap);
                return 3;
            }
            float fert = map.fertilityGrid.FertilityAt(c);
            if (fert < t.plant.fertilityMin)
            {
                why = "EWP_WG_Soil".Translate(fert.ToStringPercent(), t.plant.fertilityMin.ToStringPercent());
                return 3;
            }
            float ff = PlantUtility.GrowthRateFactorFor_Fertility(t, fert);
            float lf = 1f;
            if (roof != null && !t.plant.cavePlant && t.plant.growMinGlow > 0f)
            {
                lf = PlantUtility.GrowthRateFactorFor_Light(t, map.glowGrid.GroundGlowAt(c, false, true));
                if (lf <= 0.001f) { why = "EWP_WG_Dark".Translate(); return 3; }
            }
            float tf = 1f;
            if (GenTemperature.TryGetTemperatureForCell(c, map, out float temp))
                tf = PlantUtility.GrowthRateFactorFor_Temperature(t, temp);
            if (tf <= 0.001f)
            {
                why = (temp < t.plant.minGrowthTemperature ? "EWP_WG_Cold" : "EWP_WG_Hot").Translate(temp.ToStringTemperature("F0"));
                return 2;
            }
            float all = ff * lf * tf;
            if (all < 0.75f)
            {
                why = "EWP_WG_Slow".Translate(all.ToStringPercent(), ff.ToStringPercent(), lf.ToStringPercent(), tf.ToStringPercent());
                return 2;
            }
            why = "EWP_WG_Good".Translate(all.ToStringPercent());
            return 1;
        }

        public static void OnGUI()
        {
            if (!Active || Find.CurrentMap != map) return;
            // the bar at the top: what is shown, close and pick another plant
            float w = 560f;
            Rect bar = new Rect((UI.screenWidth - w) / 2f, 60f, w, 54f);
            Widgets.DrawWindowBackground(bar);
            U.Label(new Rect(bar.x + 10f, bar.y + 4f, w - 230f, 24f), "EWP_WG_Showing".Translate(WP.Label(plant)), MUI.Text1);
            U.Label(new Rect(bar.x + 10f, bar.y + 26f, w - 230f, 24f), inBiome ? "EWP_WG_InBiome".Translate() : "EWP_WG_NotInBiome".Translate(),
                inBiome ? MUI.Muted : MUI.Warn, GameFont.Tiny);
            if (Widgets.ButtonText(new Rect(bar.xMax - 216f, bar.y + 13f, 110f, 28f), "EWP_WG_Other".Translate()))
                Find.WindowStack.Add(new FloatMenu(PlantOptions(map)));
            if (Widgets.ButtonText(new Rect(bar.xMax - 100f, bar.y + 13f, 90f, 28f), "EWP_WG_Close".Translate()))
            {
                Stop();
                return;
            }
            // the reason under the mouse
            if (Mouse.IsOver(bar) || Find.WindowStack.GetWindowAt(UI.MousePosUIInvertedUseEventIfCan) != null)
                return;
            IntVec3 c = UI.MouseCell();
            if (!c.InBounds(map)) return;
            byte st = Eval(c, out string why);
            if (st == 0 || why == null) return;
            Vector2 mp = Event.current.mousePosition;
            float tw = Mathf.Min(360f, Text.CalcSize(why).x + 16f);
            float th = Text.CalcHeight(why, tw - 12f) + 8f;
            Rect tip = new Rect(mp.x + 18f, mp.y + 18f, tw, th);
            Widgets.DrawBoxSolid(tip, new Color(0.08f, 0.08f, 0.1f, 0.9f));
            U.Label(tip.ContractedBy(6f, 4f), why, st == 1 ? Green : st == 2 ? Yellow : Red, GameFont.Small, TextAnchor.UpperLeft);
        }

        // plants of the map's biome first, then "another plant…"
        public static List<FloatMenuOption> PlantOptions(Map m)
        {
            var opts = new List<FloatMenuOption>();
            foreach (ThingDef t in WP.ActiveList(m.Biome).Where(kv => kv.Value > 0f).Select(kv => kv.Key).OrderBy(x => WP.Label(x)))
            {
                ThingDef tt = t;
                opts.Add(new FloatMenuOption(WP.Label(tt), () => Start(m, tt), tt));
            }
            opts.Add(new FloatMenuOption("EWP_WG_AnyPlant".Translate(), () =>
                Find.WindowStack.Add(new Dialog_PickPlant(new Dictionary<ThingDef, float>(), t =>
                {
                    Start(m, t);
                    Find.WindowStack.TryRemove(typeof(Dialog_PickPlant));
                }))));
            return opts;
        }
    }
}
