using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SeasonsHolidays
{
    // ---------------------------------------------------------------------------
    // 0.3.0: trees and grass change colour with the seasons – LOOKS ONLY. Nothing about growth,
    // temperature, raids or traders changes: only the colour of the drawn plant.
    //  - leafy trees: yellow, orange and red in autumn (each tree a bit earlier or later and in
    //    its own colour), brown at the end of autumn, bare grey-brown in winter, green again in spring
    //  - conifers (pine, spruce, fir, cedar …) stay green, a little darker in winter
    //  - wild grass and bushes turn straw-coloured in autumn and pale in winter
    //  - crops in fields, cacti, palms and other tropical plants keep their colour
    //  - by the climate: on a map with a warm winter (tropics) nothing changes, a mild winter
    //    gives softer colours; in the south the seasons are turned around
    // The map is redrawn only when the colours step on (a few times a month), checked once an hour.
    // ---------------------------------------------------------------------------
    public static class PlantColors
    {
        private enum Kind { None, Leafy, Conifer, Grass }

        private sealed class State
        {
            public float autumn, winter, strength;
            public int sig = int.MinValue;
        }

        private static readonly Dictionary<ThingDef, Kind> kinds = new Dictionary<ThingDef, Kind>();
        private static readonly Dictionary<int, State> states = new Dictionary<int, State>();
        private static readonly Dictionary<int, float> climate = new Dictionary<int, float>();   // tile -> strength
        private static readonly Dictionary<long, Graphic> colored = new Dictionary<long, Graphic>();
        private static readonly Dictionary<Graphic, int> graphicIds = new Dictionary<Graphic, int>();
        private static bool failed;

        private static readonly Color Yellow = new Color(1.45f, 1.25f, 0.45f);
        private static readonly Color Orange = new Color(1.7f, 0.95f, 0.35f);
        private static readonly Color Red = new Color(1.75f, 0.6f, 0.38f);
        private static readonly Color Early = new Color(1.2f, 1.12f, 0.7f);
        private static readonly Color Brown = new Color(1.25f, 0.85f, 0.55f);
        private static readonly Color Bare = new Color(0.95f, 0.82f, 0.72f);
        private static readonly Color Straw = new Color(1.25f, 1.08f, 0.7f);
        private static readonly Color Pale = new Color(1.1f, 0.95f, 0.78f);
        private static readonly Color DarkGreen = new Color(0.9f, 0.96f, 0.95f);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(Plant), nameof(Plant.Graphic)), postfix: new HarmonyMethod(typeof(PlantColors), nameof(GraphicPostfix)));
            }
            catch (Exception e) { Log.Warning("[Seasons & Holidays] plant colours: " + e.Message); }
        }

        private static bool On => SHMod.S != null && SHMod.S.plantColors && !failed;

        // ---------------- what kind of plant ----------------
        private static readonly string[] ConiferWords = { "pine", "spruce", "fir", "conifer", "cypress", "juniper", "cedar", "larch", "yew", "sequoia", "redwood", "hemlock" };
        private static readonly string[] KeepWords = { "palm", "cecropia", "bamboo", "cact", "saguaro", "agave", "aloe", "anima", "gauranlen", "polux", "mushroom", "shroom", "fungus", "glow", "coral", "kelp", "devilstrand", "succulent", "grav", "moss" };

        private static bool Has(string s, string[] words)
        {
            if (string.IsNullOrEmpty(s)) return false;
            s = s.ToLowerInvariant();
            foreach (string w in words) if (s.Contains(w)) return true;
            return false;
        }

        private static Kind KindOf(ThingDef d)
        {
            if (kinds.TryGetValue(d, out Kind k)) return k;
            k = Kind.None;
            PlantProperties p = d.plant;
            if (p != null && !p.cavePlant && !Has(d.defName, KeepWords) && !Has(d.label, KeepWords))
            {
                if (p.IsTree)
                    k = Has(d.defName, ConiferWords) || Has(d.label, ConiferWords) ? Kind.Conifer : Kind.Leafy;
                else if (!p.Sowable)
                    k = Kind.Grass;
            }
            kinds[d] = k;
            return k;
        }

        // ---------------- the season on a map ----------------
        private static float ClimateStrength(Map map)
        {
            int tile = map.Tile;
            if (climate.TryGetValue(tile, out float s)) return s;
            float min = 99f;
            try
            {
                for (int i = 0; i < 12; i++)
                    min = Mathf.Min(min, GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, (Twelfth)i));
            }
            catch { min = 0f; }
            // a winter month of 10 °C or more: no change; 0 °C or less: full colours
            s = Mathf.Clamp01((10f - min) / 10f);
            climate[tile] = s;
            return s;
        }

        private static void Compute(Map map, State st)
        {
            Months.Now(map, out int m, out int d);
            float lat = Find.WorldGrid.LongLatOf(map.Tile).y;
            int c = Months.ClimateMonth(m, lat);
            float f = Mathf.Clamp01((d - 1 + GenLocalDate.DayPercent(map)) / Mathf.Max(1, YearClock.MonthLen(m)));
            // autumn: September → November (by the climate); winter: December fades in, January and
            // February bare, March fades out
            st.autumn = c >= 8 && c <= 10 ? ((c - 8) + f) / 3f : (c == 11 ? 1f : 0f);
            st.winter = c == 11 ? f : (c == 0 || c == 1) ? 1f : c == 2 ? 1f - f : 0f;
            st.strength = ClimateStrength(map);
        }

        private static State StateOf(Map map)
        {
            if (!states.TryGetValue(map.uniqueID, out State st))
            {
                st = new State();
                Compute(map, st);
                st.sig = Sig(st);
                states[map.uniqueID] = st;
            }
            return st;
        }

        private static int Sig(State st) => Mathf.RoundToInt(st.autumn * 24f) * 1000 + Mathf.RoundToInt(st.winter * 12f) * 10 + Mathf.RoundToInt(st.strength * 4f);

        // once an in-game hour from the map component
        public static void Tick(Map map)
        {
            if (map == null || Find.TickManager.TicksGame % 2500 != 123) return;
            if (!On)
                return;
            try
            {
                State st = StateOf(map);
                var fresh = new State();
                Compute(map, fresh);
                int sig = Sig(fresh);
                if (sig == st.sig) return;
                st.autumn = fresh.autumn;
                st.winter = fresh.winter;
                st.strength = fresh.strength;
                st.sig = sig;
                if (st.strength > 0f) map.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Things);
            }
            catch (Exception e) { Log.WarningOnce("[Seasons & Holidays] plant colours: " + e.Message, 0x5E4503); }
        }

        // the setting was switched – every map is drawn again
        public static void RefreshAll()
        {
            states.Clear();
            if (Current.ProgramState != ProgramState.Playing || Find.Maps == null) return;
            foreach (Map m in Find.Maps)
                try { m.mapDrawer.WholeMapChanged(MapMeshFlagDefOf.Things); } catch { }
        }

        // ---------------- the colour of one plant ----------------
        private static float Shift(int id)
        {
            uint h = (uint)id * 2654435761u;
            return ((h >> 8) % 1000) / 1000f;   // 0..1
        }

        private static bool ColorFor(Plant p, State st, Kind k, out Color col)
        {
            col = Color.white;
            float r = Shift(p.thingIDNumber);
            float shift = (r - 0.5f) * 0.3f;
            switch (k)
            {
                case Kind.Leafy:
                {
                    float w = st.winter >= 1f ? 1f : Mathf.Clamp01(st.winter + shift);
                    if (w > 0.5f) { col = Bare; break; }
                    float a = Mathf.Clamp01(st.autumn + shift);
                    if (a < 0.12f) return false;
                    int v = Mathf.FloorToInt(Shift(p.thingIDNumber * 7 + 3) * 3f);
                    Color target = v == 0 ? Yellow : v == 1 ? Orange : Red;
                    col = a < 0.3f ? Early : a < 0.85f ? target : Brown;
                    break;
                }
                case Kind.Conifer:
                    if (st.winter < 0.5f) return false;
                    col = DarkGreen;
                    break;
                case Kind.Grass:
                {
                    float w = Mathf.Clamp01(st.winter + shift);
                    if (w > 0.5f) { col = Pale; break; }
                    float a = Mathf.Clamp01(st.autumn + shift);
                    if (a < 0.4f) return false;
                    col = Straw;
                    break;
                }
                default:
                    return false;
            }
            float s = Mathf.Round(st.strength * 4f) / 4f;
            if (s <= 0f) return false;
            col = Color.Lerp(Color.white, col, s);
            return true;
        }

        public static void GraphicPostfix(Plant __instance, ref Graphic __result)
        {
            if (!On || __result == null) return;
            try
            {
                Map map = __instance.Map;
                if (map == null || !__instance.Spawned) return;
                Kind k = KindOf(__instance.def);
                if (k == Kind.None) return;
                State st = StateOf(map);
                if (st.strength <= 0f) return;
                if (!ColorFor(__instance, st, k, out Color col)) return;
                __result = Tinted(__result, col);
            }
            catch (Exception e)
            {
                failed = true;
                Log.Warning("[Seasons & Holidays] plant colours switched off: " + e.Message);
            }
        }

        private static Graphic Tinted(Graphic g, Color col)
        {
            if (!graphicIds.TryGetValue(g, out int gid))
            {
                gid = graphicIds.Count + 1;
                graphicIds[g] = gid;
            }
            int key = (Mathf.RoundToInt(col.r * 40f) << 16) | (Mathf.RoundToInt(col.g * 40f) << 8) | Mathf.RoundToInt(col.b * 40f);
            long full = ((long)gid << 32) | (uint)key;
            if (colored.TryGetValue(full, out Graphic c)) return c;
            Color baseCol = g.color;
            Color mixed = new Color(baseCol.r * col.r, baseCol.g * col.g, baseCol.b * col.b, baseCol.a);
            try { c = g.GetColoredVersion(g.Shader, mixed, g.colorTwo); }
            catch { c = g; }
            colored[full] = c ?? g;
            return colored[full];
        }
    }
}
