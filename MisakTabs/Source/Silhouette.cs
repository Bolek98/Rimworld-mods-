using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.3.0 The colonist's own silhouette for the health tab – of any race (HAR, Biotech
    // xenotypes, mods): the game draws the pawn without clothes, we turn the picture into one
    // flat shape and colour each region by the health of its body parts (green → orange → red,
    // dark = missing). Made once and kept until the health changes.
    // ---------------------------------------------------------------------------
    public static class Silhouette
    {
        public enum Region { Head, Torso, ArmL, ArmR, LegL, LegR }   // L/R = the pawn's own side

        private sealed class Entry
        {
            public Texture2D tex;
            public int hash;
            public Rect box;          // the shape inside the picture (0…1, from the top)
            public int used;
        }

        private static readonly Dictionary<int, Entry> cache = new Dictionary<int, Entry>();
        private static int frame;

        public static readonly Color Healthy = new Color32(118, 170, 104, 255);
        public static readonly Color Missing = new Color32(52, 56, 60, 255);
        private static readonly Color Edge = new Color32(214, 222, 208, 255);

        public static Color ForHealth(float f)
        {
            if (f < 0f) return Missing;
            if (f >= 0.999f) return Healthy;
            return f >= 0.5f ? Color.Lerp(Look.Orange, Healthy, (f - 0.5f) * 2f) : Color.Lerp(Look.Red, Look.Orange, f * 2f);
        }

        // the worst health (0…1) of the parts in every region; -1 = a whole limb is missing
        public static Dictionary<Region, float> RegionHealth(Pawn pawn, Func<BodyPartRecord, Region> regionOf)
        {
            var d = new Dictionary<Region, float>();
            foreach (Region r in Enum.GetValues(typeof(Region))) d[r] = 1f;
            HediffSet hs = pawn.health.hediffSet;
            foreach (BodyPartRecord p in pawn.RaceProps.body.AllParts)
            {
                Region r = regionOf(p);
                if (hs.PartIsMissing(p))
                {
                    // only a missing limb or head darkens the region; a lost finger makes it orange
                    bool big = p.coverageAbs >= 0.04f || p.parent == pawn.RaceProps.body.corePart;
                    d[r] = Mathf.Min(d[r], big ? -1f : 0.45f);
                    continue;
                }
                float max = p.def.GetMaxHealth(pawn);
                if (max <= 0f) continue;
                float f = hs.GetPartHealth(p) / max;
                if (d[r] >= 0f) d[r] = Mathf.Min(d[r], f);
            }
            return d;
        }

        public static Texture2D Get(Pawn pawn, Vector2 size, Dictionary<Region, float> health, out Rect box)
        {
            box = new Rect(0f, 0f, 1f, 1f);
            frame++;
            int hash = pawn.thingIDNumber;
            foreach (var kv in health) hash = hash * 31 + Mathf.RoundToInt(kv.Value * 20f);
            hash = hash * 31 + (int)size.x * 7 + (int)size.y;
            hash = hash * 31 + (pawn.story?.bodyType?.defName?.GetHashCode() ?? 0) + (pawn.ageTracker?.CurLifeStageIndex ?? 0);
            if (cache.TryGetValue(pawn.thingIDNumber, out Entry e) && e.hash == hash && e.tex != null)
            {
                e.used = frame;
                box = e.box;
                return e.tex;
            }
            Texture2D tex = Make(pawn, size, health, out box);
            if (tex == null) return null;
            if (e != null && e.tex != null) UnityEngine.Object.Destroy(e.tex);
            cache[pawn.thingIDNumber] = new Entry { tex = tex, hash = hash, box = box, used = frame };
            if (cache.Count > 24)
                foreach (var old in cache.OrderBy(x => x.Value.used).Take(8).ToList())
                {
                    if (old.Value.tex != null) UnityEngine.Object.Destroy(old.Value.tex);
                    cache.Remove(old.Key);
                }
            return tex;
        }

        private static Texture2D Make(Pawn pawn, Vector2 size, Dictionary<Region, float> health, out Rect box)
        {
            box = new Rect(0f, 0f, 1f, 1f);
            RenderTexture rt = PortraitsCache.Get(pawn, size, Rot4.South, default(Vector3), 1f, true, true, false, false, null, null, false, PawnHealthState.Mobile);   // 0.4.1: always standing, even when lying wounded
            if (rt == null) return null;
            int w = rt.width, h = rt.height;
            RenderTexture prev = RenderTexture.active;
            var src = new Texture2D(w, h, TextureFormat.ARGB32, false);
            try
            {
                RenderTexture.active = rt;
                src.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                src.Apply(false);
            }
            finally { RenderTexture.active = prev; }
            Color32[] px = src.GetPixels32();
            UnityEngine.Object.Destroy(src);

            const byte Solid = 90;
            // the shape's box (texture rows go from the bottom)
            int minX = w, maxX = -1, minY = h, maxY = -1;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    if (px[y * w + x].a > Solid)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
            if (maxX < 0) return null;
            float bw = maxX - minX + 1, bh = maxY - minY + 1;
            box = new Rect(minX / (float)w, (h - 1 - maxY) / (float)h, bw / w, bh / h);

            var outPx = new Color32[px.Length];
            for (int y = minY; y <= maxY; y++)
            {
                // the row's own width: arms are the outer parts of the body rows; where the arms stand
                // apart from the body (3+ separate runs in a row) the outer runs are the arms
                int rl = -1, rr = -1, runs = 0, firstEnd = -1, lastStart = -1;
                bool inRun = false;
                for (int x = minX; x <= maxX; x++)
                {
                    bool on = px[y * w + x].a > Solid;
                    if (on) { if (rl < 0) rl = x; rr = x; }
                    if (on && !inRun) { runs++; lastStart = x; }
                    if (!on && inRun && runs == 1) firstEnd = x - 1;
                    inRun = on;
                }
                if (rl < 0) continue;
                float v = (maxY - y) / bh;                 // 0 = top of the head
                for (int x = rl; x <= rr; x++)
                {
                    Color32 c = px[y * w + x];
                    if (c.a <= 20) continue;
                    float rowU = rr > rl ? (x - rl) / (float)(rr - rl) : 0.5f;     // 0 = our left
                    Region reg = RegionAt(v, rowU);
                    if (runs >= 3 && v > 0.47f && v <= 0.86f)
                        reg = x <= firstEnd ? Region.ArmR : x >= lastStart ? Region.ArmL : Region.Torso;
                    Color col = ForHealth(health[reg]);
                    float lum = (c.r * 0.3f + c.g * 0.59f + c.b * 0.11f) / 255f;
                    col *= 0.78f + 0.3f * lum;
                    bool edge = IsEdge(px, w, h, x, y, Solid);
                    if (edge) col = Color.Lerp(col, Edge, 0.55f);
                    col.a = c.a / 255f;
                    // a missing limb: only a faint ghost of it
                    if (health[reg] < 0f) col.a *= edge ? 0.55f : 0.22f;
                    outPx[y * w + x] = col;
                }
            }
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(outPx);
            tex.Apply(false);
            return tex;
        }

        private static bool IsEdge(Color32[] px, int w, int h, int x, int y, byte solid)
        {
            if (px[y * w + x].a <= solid) return false;
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h || px[ny * w + nx].a <= solid) return true;
                }
            return false;
        }

        // RimWorld figures: big head on top, body below; arms are the outer parts of the body,
        // the bottom of the body stands for the legs. The pawn faces us: his left is on our right.
        public static Region RegionAt(float v, float rowU)
        {
            if (v < 0.47f) return Region.Head;
            if (v > 0.86f) return rowU >= 0.5f ? Region.LegL : Region.LegR;
            if (v > 0.53f && rowU < 0.2f) return Region.ArmR;
            if (v > 0.53f && rowU > 0.8f) return Region.ArmL;
            return Region.Torso;
        }

        // the old human-figure spot (x, y of a 200×280 card) → the place on the shape
        public static Vector2 MapSpot(Vector2 spot)
        {
            float v = Piece(spot.y, new[] { 0f, 0.15f, 0.37f, 0.40f, 0.66f, 0.70f, 0.97f, 1f }, new[] { 0f, 0.07f, 0.45f, 0.5f, 0.82f, 0.88f, 0.98f, 1f });
            float u = 0.5f + (spot.x - 0.5f) * (v < 0.47f ? 1.2f : 1.45f);
            return new Vector2(Mathf.Clamp(u, 0.04f, 0.96f), v);
        }

        private static float Piece(float x, float[] xs, float[] ys)
        {
            for (int i = 1; i < xs.Length; i++)
                if (x <= xs[i])
                    return Mathf.Lerp(ys[i - 1], ys[i], (x - xs[i - 1]) / Mathf.Max(0.0001f, xs[i] - xs[i - 1]));
            return ys[ys.Length - 1];
        }
    }
}
