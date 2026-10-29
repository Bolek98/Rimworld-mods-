using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.7.0 The anatomy figure of the health tab: a human figure (by body type) with three views –
    // body parts by their state (healthy → hurt → missing, prosthesis, bionic, archotech), the
    // organs and the bones (broken ones red). Race features (ears, tails, horns, wings, halo …) come
    // from the race's own body add-ons and genes, so races from any mod get theirs.
    // The pictures are in the mod folder "Anatomy" (one folder per body type); without them the
    // tab keeps the colonist's own silhouette.
    // ---------------------------------------------------------------------------
    public static class Anatomy
    {
        public enum View { Parts, Organs, Bones }
        public static View view = View.Parts;

        // ids in the pictures (red channel / 10)
        // parts: 1 head 2 neck 3 torso 4/5/6 right upper arm/forearm/hand 7/8/9 left … 10/11/12 right thigh/shin/foot 13/14/15 left …
        // organs: 1 brain 2/3 right/left eye 4/5 lungs 6 heart 7 liver 8 stomach 9/10 kidneys
        // bones: 1 skull 2 spine 3 ribs 4/5 clavicles 6 pelvis 7/8/9 right humerus/radius/hand 10/11/12 left … 13/14/15 right femur/tibia/foot 16/17/18 left …
        private const int NParts = 16, NOrgans = 11, NBones = 19;

        private enum St { Ok, Missing, Prosthetic, Bionic, Archo, Implant, Broken }

        private struct Look2
        {
            public St st;
            public float hp;       // 0…1
        }

        private sealed class Kit
        {
            public int w, h;
            public byte[] part, organ, bone;          // ids
            public byte[] organA, boneA;              // alpha
            public Color32[] basePx, linePx;
            public byte[] lineRegion;                 // the body part a pixel of the outline belongs to
            public Texture2D featTex;                   // 0.8.0: all race features in one atlas
            public Dictionary<string, (Rect uv, Rect at)> feats = new Dictionary<string, (Rect, Rect)>();   // at: 0…1 of the frame
            public Vector2[] partC = new Vector2[NParts], organC = new Vector2[NOrgans], boneC = new Vector2[NBones];   // centres 0…1 from the top
        }

        private static readonly Dictionary<string, Kit> kits = new Dictionary<string, Kit>();
        private static bool? available;
        private static string root;

        public static bool Available
        {
            get
            {
                if (available == null)
                {
                    try
                    {
                        root = Path.Combine(MTMod.Pack.RootDir, "Anatomy");
                        available = Directory.Exists(Path.Combine(root, "Male"));
                    }
                    catch { available = false; }
                }
                return available.Value;
            }
        }

        private static Texture2D LoadPng(string file)
        {
            if (!File.Exists(file)) return null;
            var t = new Texture2D(2, 2, TextureFormat.ARGB32, false);
            t.LoadImage(File.ReadAllBytes(file));
            t.filterMode = FilterMode.Bilinear;
            t.wrapMode = TextureWrapMode.Clamp;
            return t;
        }

        // texture rows go from the bottom – we keep everything top-down
        private static Color32[] TopDown(Texture2D t)
        {
            Color32[] px = t.GetPixels32();
            int w = t.width, h = t.height;
            var o = new Color32[px.Length];
            for (int y = 0; y < h; y++) Array.Copy(px, (h - 1 - y) * w, o, y * w, w);
            return o;
        }

        private static Kit KitFor(string kind)
        {
            if (kits.TryGetValue(kind, out Kit k)) return k;
            k = null;
            try
            {
                string d = Path.Combine(root, kind);
                Texture2D b = LoadPng(Path.Combine(d, "base.png")), l = LoadPng(Path.Combine(d, "line.png")),
                          p = LoadPng(Path.Combine(d, "parts.png")), o = LoadPng(Path.Combine(d, "organs.png")), bo = LoadPng(Path.Combine(d, "bones.png"));
                if (b != null && l != null && p != null && o != null && bo != null)
                {
                    k = new Kit { w = b.width, h = b.height, basePx = TopDown(b), linePx = TopDown(l) };
                    Color32[] pp = TopDown(p), op = TopDown(o), bp = TopDown(bo);
                    int n = k.w * k.h;
                    k.part = new byte[n]; k.organ = new byte[n]; k.bone = new byte[n]; k.organA = new byte[n]; k.boneA = new byte[n];
                    for (int i = 0; i < n; i++)
                    {
                        k.part[i] = pp[i].a > 100 ? (byte)Mathf.RoundToInt(pp[i].r / 10f) : (byte)0;
                        k.organ[i] = (byte)Mathf.RoundToInt(op[i].r / 10f); k.organA[i] = op[i].a;
                        k.bone[i] = (byte)Mathf.RoundToInt(bp[i].r / 10f); k.boneA[i] = bp[i].a;
                    }
                    Centres(k.part, null, k.w, k.h, k.partC);
                    Centres(k.organ, k.organA, k.w, k.h, k.organC);
                    Centres(k.bone, k.boneA, k.w, k.h, k.boneC);
                    // the region of every outline pixel: the nearest part within a few pixels
                    k.lineRegion = new byte[n];
                    for (int y = 0; y < k.h; y++)
                        for (int x = 0; x < k.w; x++)
                        {
                            int i = y * k.w + x;
                            if (k.linePx[i].a < 8) continue;
                            byte best = k.part[i];
                            for (int r = 1; r <= 7 && best == 0; r++)
                                for (int dy = -r; dy <= r && best == 0; dy++)
                                    for (int dx = -r; dx <= r; dx++)
                                    {
                                        int nx = x + dx, ny = y + dy;
                                        if (nx < 0 || ny < 0 || nx >= k.w || ny >= k.h) continue;
                                        byte v = k.part[ny * k.w + nx];
                                        if (v != 0) { best = v; break; }
                                    }
                            k.lineRegion[i] = best;
                        }
                    k.featTex = LoadPng(Path.Combine(d, "features.png"));
                    string idx = Path.Combine(d, "features.txt");
                    if (k.featTex != null && File.Exists(idx))
                    {
                        float fw = k.w, fh = k.h, aw = k.featTex.width, ah = k.featTex.height;
                        foreach (string line in File.ReadAllLines(idx))
                        {
                            string[] a = line.Split(' ');
                            if (a.Length < 7) continue;
                            float x = float.Parse(a[1]), y = float.Parse(a[2]), w = float.Parse(a[3]), h = float.Parse(a[4]), ox = float.Parse(a[5]), oy = float.Parse(a[6]);
                            k.feats[a[0]] = (new Rect(x / aw, 1f - (y + h) / ah, w / aw, h / ah), new Rect(ox / fw, oy / fh, w / fw, h / fh));
                        }
                    }
                }
                foreach (Texture2D t in new[] { b, l, p, o, bo }) if (t != null) UnityEngine.Object.Destroy(t);
            }
            catch (Exception e)
            {
                Log.Warning("[Mišák Tabs] anatomy pictures " + kind + ": " + e.Message);
                k = null;
            }
            kits[kind] = k;
            return k;
        }

        private static void Centres(byte[] ids, byte[] alpha, int w, int h, Vector2[] outC)
        {
            int m = outC.Length;
            var sx = new double[m]; var sy = new double[m]; var c = new double[m];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x, id = ids[i];
                    if (id <= 0 || id >= m || (alpha != null && alpha[i] < 128)) continue;
                    sx[id] += x; sy[id] += y; c[id]++;
                }
            for (int i = 0; i < m; i++) outC[i] = c[i] > 0 ? new Vector2((float)(sx[i] / c[i] / w), (float)(sy[i] / c[i] / h)) : new Vector2(-1f, -1f);
        }

        // ---------- which picture fits the pawn ----------
        // 0.10.0: the race's own figure (Anatomy/<Race>_F, _M, _Kid; humans Clovek_F/M/Kid/Fat/Hulk/Thin)
        // when there is one, otherwise the body-type figure with the race features drawn over it
        private static readonly (string, string)[] RaceKits =
        {
            ("Human", "Clovek"), ("Wolfein_Race", "Wolfein"), ("Alien_Miho", "Miho"), ("Maru", "Maru"), ("Yuran_Race", "Yuran"),
            ("VV_Vivi", "Vivi"), ("HAR_Saclean", "Saclean"), ("HAR_Outerm", "Outerm"), ("HAR_Nearmaere", "Nearmare"), ("HAR_Idhale", "Idhale"),
            ("NivarianRace_Pawn", "Nivarian"), ("Mugirl", "Mugirl"), ("TailinRace", "Tailin"), ("MP_Mariponica", "Mariponica")
        };
        private static readonly Dictionary<string, bool> kitExists = new Dictionary<string, bool>();
        private static bool KitExists(string name)
        {
            if (!kitExists.TryGetValue(name, out bool e))
            {
                try { e = Directory.Exists(Path.Combine(root, name)); } catch { e = false; }
                kitExists[name] = e;
            }
            return e;
        }

        private static string RacePrefix(ThingDef race)
        {
            string dn = race?.defName;
            if (dn == null) return null;
            foreach (var (def, pre) in RaceKits)
                if (dn == def || (def != "Human" && def != "Maru" && def != "Mugirl" && dn.StartsWith(def))) return pre;
            return null;
        }

        public static bool IsRaceKit(string kind) => kind != null && kind.IndexOf('_') > 0 && !kind.StartsWith("Clovek_");

        public static string KindOf(Pawn pawn)
        {
            string std = StdKind(pawn);
            if (std == null || !Available) return std;
            try
            {
                string pre = RacePrefix(pawn.def);
                if (pre == null) return std;
                bool kid = pawn.DevelopmentalStage.Juvenile() || pawn.DevelopmentalStage.Baby();
                var tries = new List<string>();
                if (pre == "Clovek")
                {
                    string bt = pawn.story.bodyType?.defName ?? "";
                    if (kid) tries.Add("Clovek_Kid");
                    else if (bt == "Fat") tries.Add("Clovek_Fat");
                    else if (bt == "Hulk") tries.Add("Clovek_Hulk");
                    else if (bt == "Thin") tries.Add("Clovek_Thin");
                    tries.Add(pawn.gender == Gender.Female ? "Clovek_F" : "Clovek_M");
                }
                else
                {
                    if (kid) tries.Add(pre + "_Kid");
                    if (pawn.gender == Gender.Male) tries.Add(pre + "_M");
                    tries.Add(pre + "_F");
                    tries.Add(pre + "_M");
                }
                foreach (string t in tries)
                    if (KitExists(t)) return t;
            }
            catch { }
            return std;
        }

        public static string StdKind(Pawn pawn)
        {
            if (pawn?.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.story == null) return null;
            if (pawn.DevelopmentalStage.Juvenile() || pawn.DevelopmentalStage.Baby()) return "Kid";
            string bt = pawn.story.bodyType?.defName ?? "";
            if (bt == "Fat") return "Fat";
            if (bt == "Hulk") return "Hulk";
            if (pawn.RaceProps.baseBodySize < 0.85f) return "Kid";   // small races (Ratkin, Rabbie …)
            if (bt == "Male") return "Male";
            if (bt == "Female") return "Female";
            return pawn.gender == Gender.Female ? "Female" : "Male";
        }

        public static bool CanDraw(Pawn pawn) => Available && KindOf(pawn) != null && KitFor(KindOf(pawn)) != null;

        // ---------- the state of every part ----------
        private static readonly string[] HeadParts = { "Head", "Skull", "Brain", "Eye", "Ear", "Nose", "Jaw", "Tongue", "Teeth", "Mouth", "Horn", "Antenna" };

        // regions of the parts view a body part covers (side: -1 = the pawn's left)
        private static IEnumerable<int> RegionsOf(BodyPartRecord part, int side)
        {
            bool left = side < 0;
            for (BodyPartRecord p = part; p != null; p = p.parent)
            {
                string n = p.def.defName;
                if (n == "Shoulder" || n == "Clavicle") { yield return left ? 7 : 4; if (p == part) { yield return left ? 8 : 5; yield return left ? 9 : 6; } yield break; }
                if (n == "Arm") { yield return left ? 7 : 4; yield return left ? 8 : 5; if (p == part) yield return left ? 9 : 6; yield break; }
                if (n == "Humerus") { yield return left ? 7 : 4; yield break; }
                if (n == "Radius") { yield return left ? 8 : 5; yield break; }
                if (n == "Hand" || n == "Finger") { yield return left ? 9 : 6; yield break; }
                if (n == "Leg") { yield return left ? 13 : 10; yield return left ? 14 : 11; if (p == part) yield return left ? 15 : 12; yield break; }
                if (n == "Femur") { yield return left ? 13 : 10; yield break; }
                if (n == "Tibia") { yield return left ? 14 : 11; yield break; }
                if (n == "Foot" || n == "Toe") { yield return left ? 15 : 12; yield break; }
                if (n == "Neck") { yield return 2; yield break; }
                if (HeadParts.Contains(n)) { yield return 1; yield break; }
                if (n == "Torso") { yield return 3; yield break; }
            }
            // other bodies: by name, then by height
            string d = part.def.defName.ToLowerInvariant();
            if (d.Contains("hand") || d.Contains("finger")) { yield return left ? 9 : 6; yield break; }
            if (d.Contains("arm") || d.Contains("wing")) { yield return left ? 7 : 4; yield return left ? 8 : 5; yield break; }
            if (d.Contains("foot") || d.Contains("toe") || d.Contains("paw")) { yield return left ? 15 : 12; yield break; }
            if (d.Contains("leg")) { yield return left ? 13 : 10; yield return left ? 14 : 11; yield break; }
            yield return part.height == BodyPartHeight.Top ? 1 : 3;
        }

        private static int OrganOf(BodyPartRecord part, int side)
        {
            string n = part.def.defName;
            bool left = side < 0;
            switch (n)
            {
                case "Brain": return 1;
                case "Eye": return left ? 3 : 2;
                case "Lung": return left ? 5 : 4;
                case "Heart": return 6;
                case "Liver": return 7;
                case "Stomach": return 8;
                case "Kidney": return left ? 10 : 9;
            }
            return 0;
        }

        private static int BoneOf(BodyPartRecord part, int side)
        {
            bool left = side < 0;
            switch (part.def.defName)
            {
                case "Skull": return 1;
                case "Spine": return 2;
                case "Ribcage": case "Sternum": return 3;
                case "Clavicle": return left ? 5 : 4;
                case "Pelvis": return 6;
                case "Humerus": return left ? 10 : 7;
                case "Radius": return left ? 11 : 8;
                case "Femur": return left ? 16 : 13;
                case "Tibia": return left ? 17 : 14;
            }
            return 0;
        }

        private static St Replacement(Hediff h)
        {
            string dn = h.def.defName.ToLowerInvariant();
            if (dn.Contains("archotech") || dn.Contains("archo")) return St.Archo;
            TechLevel t = h.def.spawnThingOnRemoved?.techLevel ?? TechLevel.Undefined;
            if (t == TechLevel.Archotech) return St.Archo;
            if (t >= TechLevel.Spacer) return St.Bionic;
            if (t == TechLevel.Undefined) return dn.Contains("bionic") ? St.Bionic : St.Prosthetic;
            return t <= TechLevel.Industrial ? St.Prosthetic : St.Bionic;
        }

        private sealed class State
        {
            public Look2[] parts = new Look2[NParts], organs = new Look2[NOrgans], bones = new Look2[NBones];
            public Dictionary<int, List<BodyPartRecord>> partsOf = new Dictionary<int, List<BodyPartRecord>>(), organsOf = new Dictionary<int, List<BodyPartRecord>>(), bonesOf = new Dictionary<int, List<BodyPartRecord>>();
            public int hash;
        }

        private static void Worse(ref Look2 l, St st, float hp)
        {
            int Rank(St s) => s == St.Missing ? 5 : s == St.Archo ? 4 : s == St.Bionic ? 3 : s == St.Prosthetic ? 2 : s == St.Broken ? 6 : 0;
            if (st == St.Ok)
            {
                if (l.st == St.Ok) l.hp = Mathf.Min(l.hp, hp);
                return;
            }
            if (Rank(st) >= Rank(l.st)) { l.st = st; l.hp = hp; }
        }

        private static State Compute(Pawn pawn)
        {
            var s = new State();
            for (int i = 0; i < NParts; i++) s.parts[i].hp = 1f;
            for (int i = 0; i < NOrgans; i++) s.organs[i].hp = 1f;
            for (int i = 0; i < NBones; i++) s.bones[i].hp = 1f;
            HediffSet hs = pawn.health.hediffSet;
            int hash = 17;
            var added = new Dictionary<BodyPartRecord, St>();
            foreach (Hediff h in hs.hediffs)
            {
                if (h is Hediff_AddedPart && h.Part != null) added[h.Part] = Replacement(h);
                hash = hash * 31 + h.def.shortHash + (h.Part?.Index ?? 0) * 7 + Mathf.RoundToInt(h.Severity * 4f);
            }
            s.hash = hash;
            foreach (BodyPartRecord p in pawn.RaceProps.body.AllParts)
            {
                int side = Side(p);
                // replaced by an added part (it or a parent)
                St rep = St.Ok;
                for (BodyPartRecord q = p; q != null; q = q.parent)
                    if (added.TryGetValue(q, out St a)) { rep = a; break; }
                bool missing = rep == St.Ok && hs.PartIsMissing(p);
                float max = p.def.GetMaxHealth(pawn);
                float hp = missing || max <= 0f ? 0f : hs.GetPartHealth(p) / max;
                bool big = p.coverageAbs >= 0.04f || p.parent == pawn.RaceProps.body.corePart || p.def.defName == "Hand" || p.def.defName == "Foot";
                St st = rep != St.Ok ? rep : missing ? (big ? St.Missing : St.Ok) : St.Ok;
                if (missing && !big) hp = 0.45f;
                foreach (int r in RegionsOf(p, side))
                {
                    if (r <= 0 || r >= NParts) continue;
                    Worse(ref s.parts[r], st, hp);
                    if (!s.partsOf.TryGetValue(r, out var l)) s.partsOf[r] = l = new List<BodyPartRecord>();
                    l.Add(p);
                }
                int o = OrganOf(p, side);
                if (o > 0)
                {
                    St os = rep != St.Ok ? rep : missing ? St.Missing : St.Ok;
                    if (os == St.Ok && hs.hediffs.Any(h => h.Part == p && h is Hediff_Implant && !(h is Hediff_AddedPart))) os = St.Implant;
                    Worse(ref s.organs[o], os, hp);
                    if (!s.organsOf.TryGetValue(o, out var l)) s.organsOf[o] = l = new List<BodyPartRecord>();
                    l.Add(p);
                }
                int b = BoneOf(p, side);
                if (b > 0)
                {
                    bool cracked = hs.hediffs.Any(h => h.Part == p && h is Hediff_Injury && (h.def.defName == "Crack" || h.def.defName.Contains("Fracture") || h.def.defName.Contains("Broken")));
                    St bs = rep != St.Ok ? rep : missing ? St.Missing : cracked || hp < 0.5f ? St.Broken : St.Ok;
                    Worse(ref s.bones[b], bs, hp);
                    if (!s.bonesOf.TryGetValue(b, out var l)) s.bonesOf[b] = l = new List<BodyPartRecord>();
                    l.Add(p);
                }
            }
            // hands and feet bones follow the hand / foot part
            CopyBone(s, 6, 9); CopyBone(s, 9, 12); CopyBone(s, 12, 15); CopyBone(s, 15, 18);
            // limb bones of a replaced or missing limb follow the limb
            int[][] limb = { new[] { 4, 7 }, new[] { 5, 8 }, new[] { 7, 10 }, new[] { 8, 11 }, new[] { 10, 13 }, new[] { 11, 14 }, new[] { 13, 16 }, new[] { 14, 17 } };
            foreach (int[] pb in limb)
            {
                Look2 lp = s.parts[pb[0]];
                if (lp.st == St.Missing || lp.st == St.Bionic || lp.st == St.Archo || lp.st == St.Prosthetic) s.bones[pb[1]] = lp;
            }
            return s;
        }

        private static void CopyBone(State s, int part, int bone)
        {
            s.bones[bone] = s.parts[part];
            if (s.bones[bone].st == St.Ok && s.bones[bone].hp < 0.5f) s.bones[bone].st = St.Broken;
            if (s.partsOf.TryGetValue(part, out var l)) s.bonesOf[bone] = l;
        }

        public static int Side(BodyPartRecord part)
        {
            for (BodyPartRecord p = part; p != null && p.parent != null; p = p.parent)
            {
                List<BodyPartRecord> sib = p.parent.parts.Where(x => x.def == p.def).ToList();
                if (sib.Count == 2) return sib.IndexOf(p) == 0 ? -1 : 1;
            }
            return 0;
        }

        // ---------- colours ----------
        private static readonly Color Ok = new Color32(92, 190, 112, 255), Yellow = new Color32(240, 190, 70, 255), Red = new Color32(235, 70, 60, 255);
        private static readonly Color CBionic = new Color32(70, 200, 255, 255), CArcho = new Color32(190, 120, 255, 255), CProst = new Color32(176, 124, 72, 255);
        private static readonly Color Bone = new Color32(236, 230, 214, 255);
        private static readonly Color[] OrganNatural =
        {
            Color.clear, new Color32(232, 160, 170, 255), new Color32(240, 240, 240, 255), new Color32(240, 240, 240, 255),
            new Color32(232, 130, 140, 255), new Color32(232, 130, 140, 255), new Color32(214, 60, 70, 255), new Color32(150, 60, 50, 255),
            new Color32(214, 150, 120, 255), new Color32(170, 70, 70, 255), new Color32(170, 70, 70, 255)
        };

        public static Color HpColor(float hp)
        {
            float t = 1f - Mathf.Clamp01(hp);
            return t < 0.5f ? Color.Lerp(Ok, Yellow, t * 2f) : Color.Lerp(Yellow, Red, t * 2f - 1f);
        }

        private static Color StColor(Look2 l, Color healthy)
        {
            switch (l.st)
            {
                case St.Bionic: case St.Implant: return CBionic;
                case St.Archo: return CArcho;
                case St.Prosthetic: return CProst;
                case St.Broken: case St.Missing: return Red;
            }
            return l.hp >= 0.999f ? healthy : HpColor(l.hp);
        }

        // ---------- making the picture ----------
        private sealed class Pic
        {
            public Texture2D tex;
            public int hash, used;
            public State state;
        }

        private static readonly Dictionary<int, Pic> pics = new Dictionary<int, Pic>();
        private static int frame;

        private static Pic Picture(Pawn pawn, Kit k)
        {
            frame++;
            int key = pawn.thingIDNumber * 3 + (int)view;
            State st = null;
            pics.TryGetValue(key, out Pic pic);
            // the state is checked twice a second, not every frame
            if (pic != null && pic.tex != null && (frame - pic.used < 30 || Compute(pawn).hash == pic.hash))
            {
                if (frame - pic.used >= 30) pic.used = frame;
                return pic;
            }
            st = Compute(pawn);
            Texture2D tex = Make(k, st);
            if (pic?.tex != null) UnityEngine.Object.Destroy(pic.tex);
            pic = new Pic { tex = tex, hash = st.hash, used = frame, state = st };
            pics[key] = pic;
            if (pics.Count > 30)
                foreach (var old in pics.OrderBy(x => x.Value.used).Take(10).ToList())
                {
                    if (old.Value.tex != null) UnityEngine.Object.Destroy(old.Value.tex);
                    pics.Remove(old.Key);
                }
            return pic;
        }

        private static Color Over(Color under, Color c, float a)
        {
            float oa = a + under.a * (1f - a);
            if (oa <= 0.0001f) return new Color(0f, 0f, 0f, 0f);
            float ub = under.a * (1f - a);
            return new Color((c.r * a + under.r * ub) / oa, (c.g * a + under.g * ub) / oa, (c.b * a + under.b * ub) / oa, oa);
        }

        private static Texture2D Make(Kit k, State s)
        {
            int w = k.w, h = k.h, n = w * h;
            var px = new Color32[n];
            float dim = view == View.Parts ? 1f : view == View.Organs ? 0.8f : 0.6f;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    Color c = k.basePx[i];
                    c.r *= dim; c.g *= dim; c.b *= dim;
                    int pid = k.part[i];
                    bool hatch = ((x + y) % 7) < 1;
                    bool circuit = (x % 9 == 0 && y % 18 < 12) || (y % 9 == 0 && x % 18 < 11);
                    if (view == View.Parts && pid > 0)
                    {
                        Look2 l = s.parts[pid];
                        Color sc = StColor(l, Ok);
                        switch (l.st)
                        {
                            case St.Missing:
                                c.a = 0f;
                                if (hatch) c = new Color(sc.r, sc.g, sc.b, 0.45f);
                                break;
                            case St.Bionic: case St.Archo:
                                c = Over(c, sc, 0.55f);
                                if (circuit) c = Over(c, Color.Lerp(sc, Color.white, 0.35f), 0.75f);
                                break;
                            case St.Prosthetic:
                                c = Over(c, sc, 0.6f);
                                if (hatch) c = Over(c, Color.Lerp(sc, Color.white, 0.3f), 0.6f);
                                break;
                            default:
                                c = Over(c, sc, 0.62f);
                                break;
                        }
                    }
                    else if (view == View.Organs && k.organA[i] > 0 && k.organ[i] > 0)
                    {
                        int o = k.organ[i];
                        Look2 l = s.organs[o];
                        float a = k.organA[i] / 255f;
                        bool edge = Edge(k.organ, w, h, x, y);
                        if (l.st == St.Missing)
                        {
                            if (edge && ((x + y) / 3) % 2 == 0) c = Over(c, Red, a);
                            else if (hatch) c = Over(c, Red, 0.35f * a);
                        }
                        else
                        {
                            Color oc = StColor(l, OrganNatural[o]);
                            c = Over(c, oc, 0.92f * a);
                            if (edge) c = Over(c, l.st == St.Ok ? new Color(1f, 1f, 1f) : Color.Lerp(oc, Color.white, 0.6f), 0.7f * a);
                            if ((l.st == St.Bionic || l.st == St.Archo || l.st == St.Implant) && circuit) c = Over(c, Color.white, 0.45f * a);
                        }
                    }
                    else if (view == View.Bones && k.boneA[i] > 0 && k.bone[i] > 0)
                    {
                        int b = k.bone[i];
                        Look2 l = s.bones[b];
                        float a = k.boneA[i] / 255f;
                        if (l.st != St.Missing)
                        {
                            Color bc = l.st == St.Ok ? (l.hp >= 0.999f ? Bone : Color.Lerp(Bone, HpColor(l.hp), 0.7f)) : StColor(l, Bone);
                            c = Over(c, bc, a);
                        }
                    }
                    // the outline – not around a missing limb
                    Color32 lp = k.linePx[i];
                    if (lp.a > 0)
                    {
                        int r = k.lineRegion[i];
                        float la = lp.a / 255f * (view == View.Parts ? 1f : 0.55f);
                        if (r > 0 && view == View.Parts && s.parts[r].st == St.Missing)
                        {
                            // dashed red where the limb was
                            la = ((x + y) / 5) % 2 == 0 && lp.a > 150 ? 0.9f : 0f;
                            c = Over(c, Red, la);
                        }
                        else c = Over(c, new Color(0.88f, 0.91f, 0.94f), la);
                    }
                    px[(h - 1 - y) * w + x] = c;
                }
            var tex = new Texture2D(w, h, TextureFormat.ARGB32, false) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            tex.SetPixels32(px);
            tex.Apply(false, true);
            return tex;
        }

        private static bool Edge(byte[] ids, int w, int h, int x, int y)
        {
            byte v = ids[y * w + x];
            if (x <= 1 || y <= 1 || x >= w - 2 || y >= h - 2) return true;
            return ids[y * w + x - 2] != v || ids[y * w + x + 2] != v || ids[(y - 2) * w + x] != v || ids[(y + 2) * w + x] != v;
        }

        // ---------- race features ----------
        private static readonly Dictionary<string, List<string>> featCache = new Dictionary<string, List<string>>();

        // known races (defName, lower case, contains) → features
        private static readonly (string, string[])[] Known =
        {
            ("ratkin", new[] { "mouseEars", "thinTail" }), ("rabbie", new[] { "rabbitEars", "rabbitTail" }), ("bun", new[] { "rabbitEars", "rabbitTail" }),
            ("nyaron", new[] { "catEars", "catTail" }), ("maru", new[] { "catEars", "catTail" }), ("kurin", new[] { "foxEars", "foxTail" }),
            ("miho", new[] { "foxEars", "foxTail" }), ("silkiera", new[] { "foxEars", "foxTail" }), ("yuran", new[] { "foxEars", "foxTail" }),
            ("kiiro", new[] { "foxEars", "foxTail" }), ("wolfein", new[] { "wolfEars", "wolfTail" }), ("tailin", new[] { "catEars", "dragonTail" }),
            ("moyo", new[] { "antennae", "fishTail" }), ("axolotl", new[] { "gills", "fishTail" }), ("moelotl", new[] { "gills", "fishTail" }),
            ("xenoorca", new[] { "fishTail" }), ("milira", new[] { "halo", "featherWings" }), ("outerm", new[] { "halo", "featherWings" }),
            ("mariponica", new[] { "halo", "horns", "wings", "thinTail" }), ("nearmaere", new[] { "horns", "batWings" }), ("neclose", new[] { "horns", "batWings" }),
            ("littluna", new[] { "smallHorns", "wings" }), ("solark", new[] { "horns", "wings" }), ("saclean", new[] { "horns", "wings" }),
            ("qualeela", new[] { "smallHorns", "featherWings" }), ("zoichor", new[] { "horns", "batWings" }), ("vv_vivi", new[] { "antennae", "mothWings" }),
            ("idhale", new[] { "wisp" }), ("kijin", new[] { "horns" }), ("venarium", new[] { "horns", "dragonTail" }), ("nivarian", new[] { "horns", "dragonTail" }),
            ("mugirl", new[] { "horns", "catTail" }), ("dragonian", new[] { "smallHorns", "dragonTail" })
        };

        private static readonly (string, string)[] EarWords = { ("rabbit", "rabbitEars"), ("bunny", "rabbitEars"), ("usagi", "rabbitEars"), ("fox", "foxEars"), ("kitsune", "foxEars"),
            ("wolf", "wolfEars"), ("dog", "wolfEars"), ("inu", "wolfEars"), ("mouse", "mouseEars"), ("rat", "mouseEars"), ("elf", "elfEars"), ("point", "elfEars"), ("cat", "catEars"), ("neko", "catEars") };
        private static readonly (string, string)[] TailWords = { ("fox", "foxTail"), ("fluff", "foxTail"), ("wolf", "wolfTail"), ("dog", "wolfTail"), ("rabbit", "rabbitTail"), ("bunny", "rabbitTail"),
            ("dragon", "dragonTail"), ("lizard", "dragonTail"), ("fish", "fishTail"), ("mermaid", "fishTail"), ("orca", "fishTail"), ("rat", "thinTail"), ("mouse", "thinTail"), ("cat", "catTail") };

        public static List<string> Features(Pawn pawn)
        {
            string key = pawn.def.defName;
            try
            {
                if (pawn.genes != null) key += "|" + string.Join(",", pawn.genes.GenesListForReading.Where(g => g.Active).Select(g => g.def.defName).Where(n => n.StartsWith("Ears_") || n.StartsWith("Tail_") || n.Contains("Horn") || n.Contains("Wing") || n.Contains("Antenna")).ToArray());
            }
            catch { }
            if (featCache.TryGetValue(key, out var f)) return f;
            f = new List<string>();
            string dn = pawn.def.defName.ToLowerInvariant();
            foreach (var (word, tags) in Known)
                if (dn.Contains(word)) { f.AddRange(tags); break; }
            if (f.Count == 0)
            {
                // the race's own body add-ons (Humanoid Alien Races)
                string race = dn + " " + (pawn.def.label ?? "").ToLowerInvariant();
                foreach (string path in AddonPaths(pawn.def))
                {
                    string p = path.ToLowerInvariant();
                    string leaf = p.Substring(p.LastIndexOf('/') + 1);
                    if (leaf.Contains("ear")) Add(f, Pick(race + " " + p, EarWords, "catEars"), "Ears");
                    else if (leaf.Contains("tail")) Add(f, Pick(race + " " + p, TailWords, "catTail"), "Tail");
                    else if (leaf.Contains("horn")) Add(f, "horns", "horns");
                    else if (leaf.Contains("antler")) Add(f, "antlers", "horns");
                    else if (leaf.Contains("halo")) Add(f, "halo", "halo");
                    else if (leaf.Contains("antenna") || leaf.Contains("feeler")) Add(f, "antennae", "antennae");
                    else if (leaf.Contains("gill")) Add(f, "gills", "gills");
                    else if (leaf.Contains("wing"))
                        Add(f, race.Contains("bat") || race.Contains("demon") || race.Contains("dragon") || p.Contains("bat") ? "batWings"
                             : race.Contains("moth") || race.Contains("bee") || race.Contains("insect") || p.Contains("moth") ? "mothWings"
                             : p.Contains("feather") || race.Contains("angel") || race.Contains("bird") ? "featherWings" : "wings", "Wings");
                    else if (leaf.Contains("fin")) Add(f, "fishTail", "Tail");
                }
            }
            // genes (Biotech)
            try
            {
                if (pawn.genes != null)
                    foreach (Gene g in pawn.genes.GenesListForReading.Where(x => x.Active))
                    {
                        string n = g.def.defName;
                        if (n == "Ears_Cat") Add(f, "catEars", "Ears");
                        else if (n == "Ears_Pointed") Add(f, "elfEars", "Ears");
                        else if (n == "Tail_Furry") Add(f, "foxTail", "Tail");
                        else if (n == "Tail_Smooth") Add(f, "thinTail", "Tail");
                        else if (n.Contains("MiniHorn")) Add(f, "smallHorns", "horns");
                        else if (n.Contains("Horn")) Add(f, "horns", "horns");
                        else if (n.Contains("Wing")) Add(f, "wings", "Wings");
                        else if (n.Contains("Antenna")) Add(f, "antennae", "antennae");
                    }
            }
            catch { }
            featCache[key] = f;
            return f;
        }

        private static string Pick(string text, (string, string)[] words, string fallback)
        {
            foreach (var (w, t) in words) if (text.Contains(w)) return t;
            return fallback;
        }

        // one of each kind (ears, tail, wings …)
        private static void Add(List<string> f, string tag, string group)
        {
            string g = group.ToLowerInvariant();
            if (f.Any(x => x.ToLowerInvariant().Contains(g) || (g == "horns" && (x == "horns" || x == "smallHorns" || x == "antlers")))) return;
            f.Add(tag);
        }

        private static IEnumerable<string> AddonPaths(ThingDef def)
        {
            var list = new List<string>();
            try
            {
                object ar = Traverse.Create(def).Field("alienRace").GetValue();
                if (ar == null) return list;
                object gen = Traverse.Create(ar).Field("generalSettings").Field("alienPartGenerator").GetValue();
                if (gen == null) return list;
                if (Traverse.Create(gen).Field("bodyAddons").GetValue() is IEnumerable addons)
                    foreach (object a in addons)
                    {
                        if (a == null) continue;
                        string p = Traverse.Create(a).Field("path").GetValue() as string;
                        string name = Traverse.Create(a).Field("name").GetValue() as string;
                        if (!p.NullOrEmpty()) list.Add(p);
                        else if (!name.NullOrEmpty()) list.Add(name);
                    }
            }
            catch { }
            return list;
        }

        // ---------- drawing ----------
        // the figure fitted into r; returns the rect it was drawn in. hover: the parts under the mouse
        public static Rect Draw(Rect r, Pawn pawn, out List<BodyPartRecord> hover, out Vector2[] centres)
        {
            hover = null; centres = null;
            Kit k = KitFor(KindOf(pawn));
            lastKit = k;
            if (k == null) return Rect.zero;
            float scale = Mathf.Min(r.width / k.w, r.height / k.h);
            Rect fr = new Rect(r.x + (r.width - k.w * scale) / 2f, r.y + (r.height - k.h * scale) / 2f, k.w * scale, k.h * scale);
            // 0.10.0: a race's own figure has its ears, tails and wings drawn in; a human figure still gets
            // gene features (xenotypes) from the body-type figure's pictures
            string kind = KindOf(pawn);
            Kit fk = IsRaceKit(kind) ? null : kind.StartsWith("Clovek_") ? KitFor(StdKind(pawn)) : k;
            List<string> feats = fk == null ? new List<string>() : Features(pawn);
            Color dimF = view == View.Parts ? Color.white : new Color(0.75f, 0.75f, 0.78f, 0.8f);
            foreach (string t in feats) DrawFeat(fk, fr, t + "_back", dimF);
            GUI.color = Color.white;
            Pic pic = Picture(pawn, k);
            GUI.DrawTexture(fr, pic.tex);
            foreach (string t in feats) DrawFeat(fk, fr, t + "_front", dimF);
            GUI.color = Color.white;
            State state = pic.state;
            centres = view == View.Parts ? k.partC : view == View.Organs ? k.organC : k.boneC;
            if (Mouse.IsOver(fr))
            {
                Vector2 m = Event.current.mousePosition;
                int x = Mathf.Clamp((int)((m.x - fr.x) / scale), 0, k.w - 1), y = Mathf.Clamp((int)((m.y - fr.y) / scale), 0, k.h - 1);
                int i = y * k.w + x;
                if (view == View.Organs && k.organ[i] > 0 && k.organA[i] > 100) state.organsOf.TryGetValue(k.organ[i], out hover);
                else if (view == View.Bones && k.bone[i] > 0 && k.boneA[i] > 100) state.bonesOf.TryGetValue(k.bone[i], out hover);
                if (hover == null && k.part[i] > 0) state.partsOf.TryGetValue(k.part[i], out hover);
            }
            return fr;
        }

        private static void DrawFeat(Kit k, Rect fr, string name, Color c)
        {
            if (k?.featTex == null || !k.feats.TryGetValue(name, out var f)) return;
            GUI.color = c;
            GUI.DrawTextureWithTexCoords(new Rect(fr.x + f.at.x * fr.width, fr.y + f.at.y * fr.height, f.at.width * fr.width, f.at.height * fr.height), k.featTex, f.uv);
            GUI.color = Color.white;
        }

        // 0.8.0: the colour and kind of a replacement part, for the list
        public static Color ReplacementColor(Hediff h, out string kind)
        {
            St st = Replacement(h);
            kind = st == St.Archo ? "archo" : st == St.Bionic ? "bionic" : "prosthetic";
            return st == St.Archo ? CArcho : st == St.Bionic ? CBionic : CProst;
        }

        public static Color Implant => CBionic;
        public static Color Missing => Red;

        public static Vector2 SpotOf(BodyPartRecord part, Vector2[] centres)
        {
            int side = Side(part);
            int id = view == View.Organs ? OrganOf(part, side) : view == View.Bones ? BoneOf(part, side) : 0;
            if (id <= 0 || centres[id].x < 0f)
            {
                int best = -1;
                foreach (int r in RegionsOf(part, side)) { best = r; if (r == 6 || r == 9 || r == 12 || r == 15) break; }
                return best > 0 && best < NParts && lastKit != null ? lastKit.partC[best] : new Vector2(0.5f, 0.5f);
            }
            return centres[id];
        }

        private static Kit lastKit;

        // the small legend under the figure
        public static float Legend(Rect r)
        {
            var items = new List<(Color, string)> { (Ok, "MT_A_Healthy".Translate()), (Yellow, "MT_A_Hurt".Translate()), (Red, view == View.Bones ? "MT_A_Broken".Translate() : "MT_A_Missing".Translate()),
                (CProst, "MT_A_Prosthetic".Translate()), (CBionic, view == View.Organs ? "MT_A_Implant".Translate() : "MT_A_Bionic".Translate()), (CArcho, "MT_A_Archo".Translate()) };
            float colW = r.width / 2f, y = r.y;
            for (int i = 0; i < items.Count; i++)
            {
                Rect c = new Rect(r.x + (i % 2) * colW, y + (i / 2) * 16f, colW, 16f);
                Widgets.DrawBoxSolid(new Rect(c.x + 3f, c.y + 4f, 8f, 8f), items[i].Item1);
                MisakTabs.Look.Text(new Rect(c.x + 15f, c.y, c.width - 15f, 16f), items[i].Item2, new Color32(160, 167, 174, 255), GameFont.Tiny);
            }
            return (items.Count + 1) / 2 * 16f;
        }
    }
}
