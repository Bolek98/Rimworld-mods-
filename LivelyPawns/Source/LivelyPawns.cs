using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace LivelyPawns
{
    // ===========================================================================
    // Lively Pawns – Animations & Effects (Bolek98)
    // Pawns are flat sprites; the whole pawn (body, head, clothes, carried weapon) is moved, tilted and
    // squashed a little through ONE matrix per pawn (PawnDrawParms.matrix), so the cost is a few sines
    // per visible pawn – nothing per body part, nothing for pawns outside the camera view.
    // Effects (muzzle glow, flare, smoke, shell casings, dust and snow under the feet) are flecks.
    // Settings only – nothing is stored in saves, so the mod can be added or removed any time.
    // ===========================================================================

    public class LPSettings : ModSettings
    {
        public bool recommended = true;
        public bool motion = true;          // master switch for pawn movement
        public bool walk = true, work = true, combat = true, rest = true, idle = true, animals = true;
        public float strength = 1f;         // 0.2 – 2
        public bool effects = true;         // master switch for effects
        public bool muzzle = true, smoke = true, shells = true, footsteps = true;
        public float effectSize = 1f;       // 0.5 – 2
        public int perf = 1;                // 0 low, 1 medium, 2 high

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", false);
            Scribe_Values.Look(ref motion, "motion", true);
            Scribe_Values.Look(ref walk, "walk", true);
            Scribe_Values.Look(ref work, "work", true);
            Scribe_Values.Look(ref combat, "combat", true);
            Scribe_Values.Look(ref rest, "rest", true);
            Scribe_Values.Look(ref idle, "idle", true);
            Scribe_Values.Look(ref animals, "animals", true);
            Scribe_Values.Look(ref strength, "strength", 1f);
            Scribe_Values.Look(ref effects, "effects", true);
            Scribe_Values.Look(ref muzzle, "muzzle", true);
            Scribe_Values.Look(ref smoke, "smoke", true);
            Scribe_Values.Look(ref shells, "shells", true);
            Scribe_Values.Look(ref footsteps, "footsteps", true);
            Scribe_Values.Look(ref effectSize, "effectSize", 1f);
            Scribe_Values.Look(ref perf, "perf", 1);
        }
    }

    public class LivelyPawnsMod : Mod
    {
        public const string Version = "0.1.1";
        public static LPSettings S;
        public static bool Yayo;            // Yayo's Animation is active: our pawn movement stays off
        private static List<string> recBullets;

        public LivelyPawnsMod(ModContentPack content) : base(content)
        {
            S = GetSettings<LPSettings>();
            MNews.Register(content, Version);
            MUI.Init(new LPSettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            try
            {
                Yayo = LoadedModManager.RunningModsListForReading.Any(m =>
                    (m.PackageIdPlayerFacing ?? "").ToLowerInvariant().Contains("yayo") && (m.Name ?? "").ToLowerInvariant().Contains("anim"));
                if (Yayo)
                    Log.Warning("[Lively Pawns] Yayo's Animation is active – Lively Pawns leaves pawn movement to it and only adds its effects. Do not run both animation mods; switch one of them off.");
            }
            catch { }
            try
            {
                var h = new Harmony("bolek98.livelypawns");
                MUI.PatchWindow(h, typeof(LivelyPawnsMod));
                Hooks.Patch(h);
                Log.Message("[Lively Pawns] v" + Version + " loaded.");
            }
            catch (Exception e) { Log.Error("[Lively Pawns] patch failed: " + e); }
        }

        public override string SettingsCategory() => "Lively Pawns";

        private static void Check(Listing_Standard l, string key, ref bool v, string field)
            => MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            LPSettings s = S;
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
            {
                recBullets = new List<string>();
                for (int i = 1; i <= 3; i++) recBullets.Add(("LP_Rec" + i).Translate());
            }
            float y0 = r.y;
            if (Yayo)
            {
                GUI.color = new Color(1f, 0.75f, 0.35f);
                float hh = Text.CalcHeight("LP_YayoWarn".Translate(), r.width);
                Widgets.Label(new Rect(r.x, y0, r.width, hh), "LP_YayoWarn".Translate());
                GUI.color = Color.white;
                y0 += hh + 6f;
            }
            y0 += MUI.RecommendedCard(new Rect(r.x, y0, r.width, 0f), s, s.recommended, v =>
            {
                s.recommended = v;
                if (v) MUI.ApplyAll(s);
            }, recBullets) + 6f;
            if (s.recommended) y0 += MUI.LockNote(new Rect(r.x, y0, r.width, 0f)) + 4f;
            Rect top = new Rect(r.x, y0, r.width, Mathf.Max(200f, r.yMax - y0 - 30f));
            var l = new Listing_Standard();
            MUI.BeginLock(s.recommended);
            try
            {
                l.Begin(top);
                l.ColumnWidth = (top.width - 24f) / 2f;
                MUI.Section(l, "LP_SecMotion".Translate());
                Check(l, "LP_Motion", ref s.motion, "motion");
                Check(l, "LP_Walk", ref s.walk, "walk");
                Check(l, "LP_Work", ref s.work, "work");
                Check(l, "LP_Combat", ref s.combat, "combat");
                Check(l, "LP_Rest", ref s.rest, "rest");
                Check(l, "LP_Idle", ref s.idle, "idle");
                Check(l, "LP_Animals", ref s.animals, "animals");
                s.strength = Mathf.Round(MUI.Slider(l, "LP_Strength".Translate(Mathf.RoundToInt(s.strength * 100f)), s.strength, 0.2f, 2f, "LP_Strength_Desc".Translate(), s, "strength") * 20f) / 20f;
                l.NewColumn();
                MUI.Section(l, "LP_SecEffects".Translate());
                Check(l, "LP_Effects", ref s.effects, "effects");
                Check(l, "LP_Muzzle", ref s.muzzle, "muzzle");
                Check(l, "LP_Smoke", ref s.smoke, "smoke");
                Check(l, "LP_Shells", ref s.shells, "shells");
                Check(l, "LP_Footsteps", ref s.footsteps, "footsteps");
                s.effectSize = Mathf.Round(MUI.Slider(l, "LP_EffectSize".Translate(Mathf.RoundToInt(s.effectSize * 100f)), s.effectSize, 0.5f, 2f, "LP_EffectSize_Desc".Translate(), s, "effectSize") * 20f) / 20f;
                MUI.Section(l, "LP_SecPerf".Translate());
                MUI.RowLabel(l, "LP_Perf".Translate(("LP_Perf" + s.perf).Translate()), "LP_Perf_Desc".Translate(), s, "perf");
                s.perf = Mathf.RoundToInt(MUI.SliderBar(l.GetRect(22f), s.perf, 0f, 2f, 1f));
                l.End();
            }
            finally { MUI.EndLock(top); }
        }
    }

    // what a pawn is doing, decided a few times a second on the main thread
    public enum Motion { None, Hammer, Mine, Stir, Bend, Nod, Sweep, Social, Eat, Idle }

    public class PawnAnim
    {
        public float seed;
        public float moveSpeed = 4.6f;
        public bool limp, tired;
        public Motion motion;
        public float nextJob, nextStats;
        public float shotT = -99f, shotAngle;
        public float meleeT = -99f; public Vector3 meleeDir;
        public float hitT = -99f; public Vector3 hitDir;
        public float nextStep;
        public bool melee;      // warming up a melee attack
    }

    public static class Anim
    {
        public static readonly ConcurrentDictionary<int, PawnAnim> data = new ConcurrentDictionary<int, PawnAnim>();
        // set on the main thread every frame, read by the parallel pre-draw
        public static float T;              // animation clock in seconds (game speed, stops when paused)
        public static CellRect view;
        public static Map viewMap;
        public static float zoom;
        public static bool active;          // motion allowed this frame
        public static int fleckBudget;
        private static float lastReal;
        // pawns whose drawing threw – left alone for a while (milliseconds, Environment.TickCount)
        private static readonly ConcurrentDictionary<int, int> skip = new ConcurrentDictionary<int, int>();

        public static PawnAnim For(Pawn p) => data.GetOrAdd(p.thingIDNumber, id => new PawnAnim { seed = (id * 0.6180339f) % 1f * 100f });

        public static bool InView(Pawn p)
        {
            Map m = viewMap;
            return m != null && p.MapHeld == m && view.Contains(p.PositionHeld);
        }

        // ---------- every frame, main thread ----------
        public static void FrameUpdate(Map map)
        {
            try
            {
                if (map == null || map != Find.CurrentMap) return;
                float real = Time.realtimeSinceStartup;
                float dt = Mathf.Clamp(real - lastReal, 0f, 0.1f);
                lastReal = real;
                TickManager tm = Find.TickManager;
                if (!tm.Paused) T += dt * Mathf.Min(tm.TickRateMultiplier, 3f);
                CameraDriver cam = Find.CameraDriver;
                zoom = cam.ZoomRootSize;
                view = cam.CurrentViewRect.ExpandedBy(2);
                viewMap = map;
                LPSettings s = LivelyPawnsMod.S;
                active = s != null && s.motion && !LivelyPawnsMod.Yayo;
                fleckBudget = s == null ? 0 : s.perf == 0 ? 6 : s.perf == 1 ? 14 : 30;
                Refresh(map, s);
            }
            catch (Exception e) { Log.ErrorOnce("[Lively Pawns] frame: " + e.Message, 0x1A7E01); }
        }

        // effects are skipped when the camera is far away (Low 20, Medium 28, High 40)
        public static bool EffectsVisible(Vector3 at)
        {
            LPSettings s = LivelyPawnsMod.S;
            if (s == null || !s.effects || viewMap == null) return false;
            float max = s.perf == 0 ? 20f : s.perf == 1 ? 28f : 40f;
            return zoom <= max && view.Contains(at.ToIntVec3());
        }

        private static readonly List<Pawn> tmp = new List<Pawn>();

        private static void Refresh(Map map, LPSettings s)
        {
            if (s == null) return;
            tmp.Clear();
            tmp.AddRange(map.mapPawns.AllPawnsSpawned);
            for (int i = 0; i < tmp.Count; i++)
            {
                Pawn p = tmp[i];
                if (p == null || !view.Contains(p.Position)) continue;
                PawnAnim a = For(p);
                if (T >= a.nextStats)
                {
                    a.nextStats = T + 3f + (a.seed % 1f);
                    try
                    {
                        a.moveSpeed = Mathf.Max(0.5f, p.GetStatValue(StatDefOf.MoveSpeed));
                        a.limp = p.health?.capacities != null && p.health.capacities.GetLevel(PawnCapacityDefOf.Moving) < 0.85f;
                        a.tired = p.needs?.rest != null && p.needs.rest.CurLevel < 0.2f;
                    }
                    catch { }
                }
                if (T >= a.nextJob)
                {
                    a.nextJob = T + 0.3f;
                    try { a.motion = MotionFor(p); } catch { a.motion = Motion.None; }
                    try
                    {
                        var st = p.stances?.curStance as Stance_Warmup;
                        a.melee = st != null && st.verb is Verb_MeleeAttack;
                    }
                    catch { a.melee = false; }
                }
                if (s.effects && s.footsteps) Effects.Footstep(p, a, s);
            }
            // forget pawns that left long ago
            if (data.Count > 600)
                foreach (int id in data.Keys.ToList())
                    if (!tmp.Any(x => x.thingIDNumber == id)) data.TryRemove(id, out _);
        }

        public static Motion MotionFor(Pawn p)
        {
            Job job = p.CurJob;
            if (job == null) return Motion.Idle;
            string d = job.def.defName;
            if (d == "Mine") return Motion.Mine;
            if (d == "Ingest") return Motion.Eat;
            if (d == "Research" || d.Contains("Read") || d.Contains("Study") || d.Contains("Meditate") || d == "Pray") return Motion.Nod;
            if (d == "Clean" || d.Contains("Sweep")) return Motion.Sweep;
            if (d == "Sow" || d.StartsWith("Harvest") || d.StartsWith("CutPlant") || d == "PlantSeed" || d == "ExtractTree") return Motion.Bend;
            if (d == "FinishFrame" || d == "Repair" || d == "Deconstruct" || d == "Uninstall" || d.StartsWith("Smooth") || d == "BuildRoof" || d == "RemoveRoof" || d == "FixBrokenDownBuilding") return Motion.Hammer;
            if (d == "SocialRelax" || d.Contains("Chitchat") || d.Contains("Social") || d == "StandAndBeSociallyActive" || d == "SpectateCeremony" || d == "GiveSpeech") return Motion.Social;
            if (d == "DoBill")
            {
                SkillDef sk = (job.bill as Bill)?.recipe?.workSkill;
                if (sk == null) return Motion.Hammer;
                string n = sk.defName;
                if (n == "Cooking") return Motion.Stir;
                if (n == "Plants") return Motion.Bend;
                if (n == "Intellectual" || n == "Medicine") return Motion.Nod;
                return Motion.Hammer;
            }
            if (d.StartsWith("Wait") || d == "GotoWander" || d == "Wander") return Motion.Idle;
            return Motion.None;
        }

        // ---------- the pawn's extra transform (parallel pre-draw: read-only, no Unity calls) ----------
        public static bool Compute(Pawn p, Rot4 facing, PawnPosture posture, out Vector3 off, out float angle, out float scaleZ)
        {
            off = Vector3.zero; angle = 0f; scaleZ = 1f;
            LPSettings s = LivelyPawnsMod.S;
            if (!data.TryGetValue(p.thingIDNumber, out PawnAnim a)) return false;
            if (skip.TryGetValue(p.thingIDNumber, out int until))
            {
                if (Environment.TickCount < until) return false;
                skip.TryRemove(p.thingIDNumber, out _);
            }
            float k = s.strength;
            float t = T + a.seed;
            bool human = p.RaceProps.Humanlike;
            if (!human && (!s.animals || s.perf == 0)) return false;
            bool side = facing == Rot4.East || facing == Rot4.West;
            float fwd = facing == Rot4.West ? -1f : 1f;   // tilt sign that leans "forward" when seen from the side

            if (posture != PawnPosture.Standing)
            {
                if (!s.rest) return false;
                // breathing; asleep in bed now and then turns a little
                bool asleep = p.needs?.rest != null && !p.Awake();
                scaleZ = 1f + Mathf.Sin(t * (asleep ? 1.1f : 1.6f)) * (asleep ? 0.016f : 0.008f) * k;
                if (asleep && p.InBed())
                {
                    float c = (t + a.seed * 3f) % 45f;
                    if (c < 4f) angle = Mathf.Sin(c / 4f * Mathf.PI) * 5f * k;
                }
                return true;
            }

            bool moving = p.pather != null && p.pather.Moving;
            if (moving)
            {
                if (!s.walk) goto combat;
                float f = 1.7f * (a.moveSpeed / 4.6f);
                float amp = human ? 0.065f : 0.04f;   // 0.1.1: about twice as visible
                if (a.tired) { f *= 0.75f; amp *= 0.6f; }
                if (p.carryTracker?.CarriedThing != null) { f *= 0.85f; amp *= 1.4f; }
                float ph = t * f * Mathf.PI;
                float step = Mathf.Abs(Mathf.Sin(ph));
                if (a.limp) step *= Mathf.Sin(ph) > 0f ? 1.8f : 0.5f;
                off.z += step * amp * k;
                if (side) angle += Mathf.Sin(ph) * (a.limp ? 9f : 5f) * k;
                else { off.x += Mathf.Sin(ph) * 0.03f * k; angle += Mathf.Sin(ph) * 3f * k; }
                if (human && p.Drafted && side) angle += 8f * fwd * k;
                if (a.limp && side) angle += 3f * fwd * k;
            }
            else if (human && s.work && a.motion != Motion.None && a.motion != Motion.Idle)
            {
                Work(a.motion, t, side, fwd, k * 1.7f, ref off, ref angle);   // 0.1.1: stronger
            }
            else if (!human && s.animals && a.motion == Motion.Eat)
            {
                float pulse = Mathf.Max(0f, Mathf.Sin(t * 2.2f));
                off.z -= pulse * 0.025f * k;
                if (side) angle += pulse * 6f * fwd * k;
            }
            else if (human && s.idle)
            {
                // shift weight, now and then a glance aside
                off.x += Mathf.Sin(t * 0.35f) * 0.014f * k;
                float c = (t * 1.3f) % 9f;
                if (c < 1.2f) angle += Mathf.Sin(c / 1.2f * Mathf.PI) * 4f * k;
            }
            if (human && !moving && s.rest) scaleZ = 1f + Mathf.Sin(t * 1.6f) * 0.006f * k;   // breathing

            combat:
            if (s.combat)
            {
                float now = T;
                float ds = now - a.shotT;
                if (ds >= 0f && ds < 0.18f)
                {
                    float q = 1f - ds / 0.18f;
                    off -= Dir(a.shotAngle) * 0.05f * q * k;
                }
                float dm = now - a.meleeT;
                if (dm >= 0f && dm < 0.25f)
                    off += a.meleeDir * 0.13f * Mathf.Sin(dm / 0.25f * Mathf.PI) * k;
                else if (a.melee)
                {
                    // wind-up: lean away from the target, slowly
                    off -= a.meleeDir * 0.03f * k;
                    if (side) angle -= 4f * fwd * k;
                }
                float dh = now - a.hitT;
                if (dh >= 0f && dh < 0.25f)
                    off += a.hitDir * 0.05f * (1f - dh / 0.25f) * k;
            }
            return off != Vector3.zero || angle != 0f || scaleZ != 1f;
        }

        private static void Work(Motion m, float t, bool side, float fwd, float k, ref Vector3 off, ref float angle)
        {
            switch (m)
            {
                case Motion.Hammer:
                {
                    float pulse = Mathf.Pow(Mathf.Max(0f, Mathf.Sin(t * 10.5f)), 4f);
                    off.z -= pulse * 0.03f * k;
                    if (side) angle += pulse * 6f * fwd * k;
                    break;
                }
                case Motion.Mine:
                {
                    float sw = Mathf.Sin(t * 5.7f);
                    if (side) angle += sw * 8f * fwd * k;
                    else off.z += sw * 0.03f * k;
                    break;
                }
                case Motion.Stir:
                    off.x += Mathf.Cos(t * 5.2f) * 0.02f * k;
                    off.z += Mathf.Sin(t * 5.2f) * 0.012f * k;
                    break;
                case Motion.Bend:
                {
                    float pulse = Mathf.Max(0f, Mathf.Sin(t * 3.9f));
                    if (side) angle += (6f + pulse * 3f) * fwd * k;
                    off.z -= (0.01f + pulse * 0.02f) * k;
                    break;
                }
                case Motion.Nod:
                    off.z -= Mathf.Max(0f, Mathf.Sin(t * 2.5f)) * 0.012f * k;
                    if (side) angle += Mathf.Sin(t * 1.2f) * 2f * fwd * k;
                    break;
                case Motion.Sweep:
                    off.x += Mathf.Sin(t * 7f) * 0.04f * k;
                    angle += Mathf.Sin(t * 7f) * 3f * k;
                    break;
                case Motion.Social:
                    angle += Mathf.Sin(t * 1.7f) * 2.5f * k;
                    off.z += Mathf.Max(0f, Mathf.Sin(t * 3.1f)) * 0.008f * k;
                    break;
                case Motion.Eat:
                {
                    float pulse = Mathf.Max(0f, Mathf.Sin(t * 4.8f));
                    off.z -= pulse * 0.015f * k;
                    break;
                }
            }
        }

        public static Vector3 Dir(float angle) => new Vector3(0f, 0f, 1f).RotatedBy(angle);

        public static void Skip(Pawn p, Exception e)
        {
            skip[p.thingIDNumber] = Environment.TickCount + 10000;
            Log.WarningOnce("[Lively Pawns] " + p.LabelShort + " could not be animated and is drawn without animation for now: " + e.Message, p.thingIDNumber ^ 0x1A7E02);
        }
    }

    public static class Hooks
    {
        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(PawnRenderer), "GetDrawParms"),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(DrawParmsPostfix)));
            // 0.1.1: at normal zoom (above 18) the game draws people from a cached picture and ignores the
            // draw matrix – the motion is added to the cached picture's position and angle instead (cheap)
            CachedMotion.Init();
            if (CachedMotion.ok)
                h.Patch(AccessTools.Method(typeof(PawnRenderer), "ParallelPreRenderPawnAt"),
                    postfix: new HarmonyMethod(typeof(CachedMotion), nameof(CachedMotion.Postfix)));
            h.Patch(AccessTools.Method(typeof(Map), nameof(Map.MapUpdate)),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(MapUpdatePostfix)));
            h.Patch(AccessTools.Method(typeof(PawnRenderUtility), nameof(PawnRenderUtility.DrawEquipmentAiming)),
                prefix: new HarmonyMethod(typeof(Hooks), nameof(AimingPrefix)));
            h.Patch(AccessTools.Method(typeof(Verb_LaunchProjectile), "TryCastShot"),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(ShotPostfix)));
            h.Patch(AccessTools.Method(typeof(Verb_MeleeAttack), "TryCastShot"),
                prefix: new HarmonyMethod(typeof(Hooks), nameof(MeleePrefix)));
            h.Patch(AccessTools.Method(typeof(PawnRenderer), nameof(PawnRenderer.Notify_DamageApplied)),
                postfix: new HarmonyMethod(typeof(Hooks), nameof(DamagePostfix)));
        }

        public static void MapUpdatePostfix(Map __instance) => Anim.FrameUpdate(__instance);

        // the whole pawn moves through its root matrix – runs in the game's parallel pre-draw
        public static void DrawParmsPostfix(ref PawnDrawParms __result, Pawn ___pawn)
        {
            Pawn p = ___pawn;
            try
            {
                if (!Anim.active || p == null || p.Dead) return;
                PawnRenderFlags f = __result.flags;
                if ((f & (PawnRenderFlags.Portrait | PawnRenderFlags.Cache | PawnRenderFlags.Statue)) != 0) return;
                if (!Anim.InView(p)) return;
                if (!Anim.Compute(p, __result.facing, __result.posture, out Vector3 off, out float ang, out float sz)) return;
                __result.matrix = __result.matrix * Matrix4x4.TRS(off, Quaternion.AngleAxis(ang, Vector3.up), new Vector3(1f, 1f, sz));
            }
            catch (Exception e) { if (p != null) Anim.Skip(p, e); }
        }

        // the weapon kicks back along the aim line after a shot
        public static void AimingPrefix(Thing eq, ref Vector3 drawLoc, float aimAngle)
        {
            try
            {
                if (!Anim.active || !LivelyPawnsMod.S.combat) return;
                if (!(eq?.ParentHolder is Pawn_EquipmentTracker tr) || tr.pawn == null) return;
                if (!Anim.data.TryGetValue(tr.pawn.thingIDNumber, out PawnAnim a)) return;
                float ds = Anim.T - a.shotT;
                if (ds < 0f || ds >= 0.14f) return;
                drawLoc -= Anim.Dir(aimAngle) * 0.09f * (1f - ds / 0.14f) * LivelyPawnsMod.S.strength;
            }
            catch { }
        }

        public static void ShotPostfix(Verb_LaunchProjectile __instance, bool __result)
        {
            if (!__result) return;
            try
            {
                if (!(__instance.caster is Pawn p) || !p.Spawned) return;
                Vector3 target = __instance.CurrentTarget.HasThing ? __instance.CurrentTarget.Thing.DrawPos : __instance.CurrentTarget.Cell.ToVector3Shifted();
                float angle = (target - p.DrawPos).AngleFlat();
                PawnAnim a = Anim.For(p);
                a.shotT = Anim.T;
                a.shotAngle = angle;
                Effects.Shot(p, __instance, angle);
            }
            catch (Exception e) { Log.ErrorOnce("[Lively Pawns] shot: " + e.Message, 0x1A7E03); }
        }

        public static void MeleePrefix(Verb_MeleeAttack __instance)
        {
            try
            {
                if (!(__instance.caster is Pawn p) || !__instance.CurrentTarget.IsValid) return;
                Vector3 target = __instance.CurrentTarget.HasThing ? __instance.CurrentTarget.Thing.DrawPos : __instance.CurrentTarget.Cell.ToVector3Shifted();
                Vector3 d = target - p.DrawPos;
                d.y = 0f;
                PawnAnim a = Anim.For(p);
                a.meleeDir = d.sqrMagnitude > 0.0001f ? d.normalized : Vector3.zero;
                a.meleeT = Anim.T;
            }
            catch { }
        }

        public static void DamagePostfix(DamageInfo dam, Pawn ___pawn)
        {
            try
            {
                if (___pawn == null) return;
                PawnAnim a = Anim.For(___pawn);
                a.hitT = Anim.T;
                a.hitDir = Anim.Dir(dam.Angle);
            }
            catch { }
        }
    }

    [DefOf]
    public static class LPFleckDefOf
    {
        public static FleckDef LP_MuzzleGlow, LP_MuzzleFlare, LP_GunSmoke, LP_Shell, LP_Dust, LP_Snow;
        static LPFleckDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(LPFleckDefOf)); }
    }

    public static class Effects
    {
        private static readonly string[] energy = { "charge", "laser", "plasma", "ion", "energy", "beam", "pulse", "lance", "emp", "zeus", "fusion", "gauss", "rail" };
        private static readonly Dictionary<ThingDef, int> kindCache = new Dictionary<ThingDef, int>();

        // 0 = no flash (bows, thrown, natural attacks), 1 = gun, 2 = energy, 3 = explosive launcher
        private static int Kind(Pawn p, Verb_LaunchProjectile v)
        {
            ThingDef w = v.EquipmentSource?.def;
            ThingDef proj = v.Projectile;
            ThingDef key = w ?? proj;
            if (key == null) return 0;
            if (kindCache.TryGetValue(key, out int k)) return k;
            k = 1;
            string n = ((w?.defName ?? "") + " " + (proj?.defName ?? "") + " " + (w?.label ?? "")).ToLowerInvariant();
            if (w == null || w.techLevel <= TechLevel.Medieval || n.Contains("bow") || n.Contains("pila") || n.Contains("javelin") || n.Contains("spear") || n.Contains("sling")) k = 0;
            else if (energy.Any(e => n.Contains(e))) k = 2;
            else if ((proj?.projectile != null && proj.projectile.explosionRadius > 0.5f) || n.Contains("launcher") || n.Contains("rocket") || n.Contains("grenade") || n.Contains("mortar")) k = 3;
            kindCache[key] = k;
            return k;
        }

        private static bool Budget()
        {
            if (Anim.fleckBudget <= 0) return false;
            Anim.fleckBudget--;
            return true;
        }

        public static void Shot(Pawn p, Verb_LaunchProjectile v, float angle)
        {
            LPSettings s = LivelyPawnsMod.S;
            Map map = p.Map;
            if (s == null || !s.effects || map == null || !Anim.EffectsVisible(p.DrawPos)) return;
            int kind = Kind(p, v);
            if (kind == 0) return;
            float size = s.effectSize;
            ThingDef w = v.EquipmentSource?.def;
            float reach = 0.4f + (w?.equippedDistanceOffset ?? 0f) + Mathf.Clamp((w?.graphicData?.drawSize.x ?? 1f) * 0.42f, 0.2f, 0.7f);
            Vector3 dir = Anim.Dir(angle);
            Vector3 muzzle = p.DrawPos + dir * reach;
            muzzle.y = AltitudeLayer.MoteOverhead.AltitudeFor();
            if (!muzzle.ShouldSpawnMotesAt(map)) return;
            Color c = kind == 2 ? new Color(0.45f, 0.85f, 1f) : kind == 3 ? new Color(1f, 0.55f, 0.2f) : new Color(1f, 0.78f, 0.35f);
            if (s.muzzle && Budget())
            {
                var g = FleckMaker.GetDataStatic(muzzle, map, LPFleckDefOf.LP_MuzzleGlow, (kind == 3 ? 1.6f : 1f) * size);
                g.instanceColor = c;
                map.flecks.CreateFleck(g);
                if (Budget())
                {
                    var f = FleckMaker.GetDataStatic(muzzle, map, LPFleckDefOf.LP_MuzzleFlare, Rand.Range(0.8f, 1.1f) * size);
                    f.rotation = angle + Rand.Range(-15f, 15f);
                    f.instanceColor = Color.Lerp(c, Color.white, 0.5f);
                    map.flecks.CreateFleck(f);
                }
            }
            if (s.smoke && kind != 2 && Budget())
            {
                var sm = FleckMaker.GetDataStatic(muzzle + dir * 0.1f, map, LPFleckDefOf.LP_GunSmoke, Rand.Range(0.7f, 1f) * size * (kind == 3 ? 2f : 1f));
                sm.velocityAngle = angle + Rand.Range(-25f, 25f);
                sm.velocitySpeed = Rand.Range(0.25f, 0.5f);
                sm.rotationRate = Rand.Range(-40f, 40f);
                map.flecks.CreateFleck(sm);
            }
            if (s.shells && kind == 1 && s.perf > 0 && Budget())
            {
                Vector3 at = p.DrawPos + dir * 0.25f;
                at.y = AltitudeLayer.MoteOverhead.AltitudeFor();
                var sh = FleckMaker.GetDataStatic(at, map, LPFleckDefOf.LP_Shell, size);
                float sideAngle = angle + (Rand.Bool ? 90f : -90f);
                sh.velocityAngle = sideAngle + Rand.Range(-20f, 20f);
                sh.velocitySpeed = Rand.Range(1.1f, 1.6f);
                sh.rotation = Rand.Range(0f, 360f);
                sh.rotationRate = Rand.Range(-600f, 600f);
                map.flecks.CreateFleck(sh);
            }
        }

        // dust on soil and sand, snow puffs on snow – on running steps
        public static void Footstep(Pawn p, PawnAnim a, LPSettings s)
        {
            if (s.perf == 0 || p.pather == null || !p.pather.Moving || Anim.T < a.nextStep) return;
            bool running = p.Drafted || p.MentalStateDef != null || (p.CurJob != null && p.CurJob.locomotionUrgency >= LocomotionUrgency.Jog) || !p.RaceProps.Humanlike && p.BodySize >= 1f;
            if (s.perf == 1 && !running) return;
            a.nextStep = Anim.T + 0.32f / Mathf.Max(0.5f, a.moveSpeed / 4.6f);
            Map map = p.Map;
            IntVec3 cell = p.Position;
            if (!cell.InBounds(map) || !Anim.EffectsVisible(p.DrawPos)) return;
            FleckDef def = null;
            if (map.snowGrid.GetDepth(cell) > 0.15f) def = LPFleckDefOf.LP_Snow;
            else
            {
                TerrainDef t = cell.GetTerrain(map);
                string n = t?.defName ?? "";
                if (t != null && t.natural && (n.Contains("Soil") || n.Contains("Sand") || n.Contains("Gravel") || n.Contains("Dirt") || n.Contains("Mud") || n.Contains("Dust"))) def = LPFleckDefOf.LP_Dust;
            }
            if (def == null || !Budget()) return;
            Vector3 at = p.DrawPos + new Vector3(Rand.Range(-0.12f, 0.12f), 0f, -0.32f);
            at.y = AltitudeLayer.MoteLow.AltitudeFor();
            if (!at.ShouldSpawnMotesAt(map)) return;
            var d = FleckMaker.GetDataStatic(at, map, def, Rand.Range(0.7f, 1f) * s.effectSize * Mathf.Clamp(p.BodySize, 0.6f, 1.6f));
            d.velocityAngle = Rand.Range(0f, 360f);
            d.velocitySpeed = Rand.Range(0.1f, 0.25f);
            d.rotationRate = Rand.Range(-30f, 30f);
            map.flecks.CreateFleck(d);
        }
    }
}


namespace LivelyPawns
{
    using System.Reflection;

    public static class CachedMotion
    {
        public static bool ok;
        private static FieldInfo resultsF, useCachedF, bodyPosF, bodyAngleF, facingF, postureF, parmsF, pawnF;

        public static void Init()
        {
            try
            {
                resultsF = AccessTools.Field(typeof(PawnRenderer), "results");
                pawnF = AccessTools.Field(typeof(PawnRenderer), "pawn");
                System.Type t = resultsF.FieldType;
                useCachedF = AccessTools.Field(t, "useCached");
                bodyPosF = AccessTools.Field(t, "bodyPos");
                bodyAngleF = AccessTools.Field(t, "bodyAngle");
                postureF = AccessTools.Field(t, "posture");
                parmsF = AccessTools.Field(t, "parms");
                ok = resultsF != null && pawnF != null && useCachedF != null && bodyPosF != null && bodyAngleF != null && postureF != null && parmsF != null;
            }
            catch { ok = false; }
            if (!ok) Log.Warning("[Lively Pawns] motion at normal zoom not available (game code changed).");
        }

        public static void Postfix(PawnRenderer __instance)
        {
            Pawn p = null;
            try
            {
                if (!Anim.active) return;
                object r = resultsF.GetValue(__instance);
                if (!(bool)useCachedF.GetValue(r)) return;
                p = (Pawn)pawnF.GetValue(__instance);
                if (p == null || p.Dead || !Anim.InView(p)) return;
                PawnDrawParms parms = (PawnDrawParms)parmsF.GetValue(r);
                PawnPosture posture = (PawnPosture)postureF.GetValue(r);
                if (!Anim.Compute(p, parms.facing, posture, out Vector3 off, out float ang, out float sz)) return;
                float angle = (float)bodyAngleF.GetValue(r);
                Vector3 pos = (Vector3)bodyPosF.GetValue(r);
                bodyPosF.SetValue(r, pos + Quaternion.AngleAxis(angle, Vector3.up) * off);
                bodyAngleF.SetValue(r, angle + ang);
                resultsF.SetValue(__instance, r);
            }
            catch (Exception e) { if (p != null) Anim.Skip(p, e); }
        }
    }
}
