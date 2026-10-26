using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.6.0: simulation – game size profile, smarter tick, world clean-up.
    //
    // RimWorld 1.6+ updates most things in two parts: Tick() every tick (movement, jobs) and
    // TickInterval(delta) only every few ticks, with the time that passed (needs, health,
    // growth, refuel, trader stock …). The game already does the second part only every
    // 15 ticks for things you cannot see. Here, by the size of the game:
    //  * world: settlements, sites and world pawns far from you – every 30 / 60 ticks,
    //  * other maps (a second base, a quest map) without danger – every 30 / 60 ticks,
    //  * huge games: things off screen on your map – every 30 ticks (never during a fight).
    // Nothing is skipped: the game passes the whole time that went by (delta), so needs,
    // healing, growth and timers come out the same, only in fewer, bigger steps.
    // ---------------------------------------------------------------------------
    public static class Sim
    {
        public enum Size { Small, Normal, Huge }

        // -1 auto, 0 small, 1 normal, 2 huge
        public static Size Current
        {
            get
            {
                GfxSettings s = GraphicsTunerMod.Settings;
                int v = s?.gameSize ?? -1;
                if (v >= 0 && v <= 2) return (Size)v;
                return Detected;
            }
        }

        private static Size? detected;
        private static float detectedAt = -999f;

        // auto: by the number of mods, the size of the current map and the number of things on the world
        public static Size Detected
        {
            get
            {
                if (detected.HasValue && Time.realtimeSinceStartup - detectedAt < 60f)
                    return detected.Value;
                detectedAt = Time.realtimeSinceStartup;
                int mods = LoadedModManager.RunningModsListForReading.Count;
                int area = 0, worldObjects = 0;
                try
                {
                    if (Verse.Current.ProgramState == ProgramState.Playing)
                    {
                        area = Find.Maps.Count == 0 ? 0 : Find.Maps.Max(m => m.Area);
                        worldObjects = Find.WorldObjects?.AllWorldObjects?.Count ?? 0;
                    }
                }
                catch { }
                Size r = Size.Normal;
                if (mods >= 300 || area >= 300 * 300 || worldObjects >= 700)
                    r = Size.Huge;
                else if (mods < 120 && area <= 225 * 225 && worldObjects < 300)
                    r = Size.Small;
                detected = r;
                return r;
            }
        }

        public static int WorldRate => Current == Size.Huge ? 60 : Current == Size.Normal ? 30 : 15;
        public static int OtherMapRate => Current == Size.Huge ? 60 : Current == Size.Normal ? 30 : 15;
        public static int OffscreenRate => Current == Size.Huge ? 30 : 15;
        public static int KeepBattles => Current == Size.Huge ? 4 : Current == Size.Normal ? 8 : 0;

        public static bool On
        {
            get
            {
                GfxSettings s = GraphicsTunerMod.Settings;
                return s != null && s.smartTick && Current != Size.Small;
            }
        }

        // ---------------- smarter tick ----------------
        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(GenTicks), nameof(GenTicks.GetCameraUpdateRate)),
                    postfix: new HarmonyMethod(typeof(Sim), nameof(CameraRatePost)));
                h.Patch(AccessTools.PropertyGetter(typeof(Pawn), nameof(Pawn.UpdateRateTicks)),
                    postfix: new HarmonyMethod(typeof(Sim), nameof(PawnRatePost)));
                h.Patch(AccessTools.PropertyGetter(typeof(Thing), "MaxTickIntervalRate"),
                    postfix: new HarmonyMethod(typeof(Sim), nameof(MaxRatePost)));
                h.Patch(AccessTools.PropertyGetter(typeof(WorldObject), "UpdateRateTicks"),
                    postfix: new HarmonyMethod(typeof(Sim), nameof(WorldObjectRatePost)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] smarter tick could not start: " + ex.Message);
            }
        }

        // danger on a map: there everything keeps the game's own rate
        private static readonly Dictionary<int, bool> danger = new Dictionary<int, bool>();
        private static int dangerTick = -1;

        private static bool Danger(Map m)
        {
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - dangerTick >= 250 || now < dangerTick)
            {
                danger.Clear();
                dangerTick = now;
            }
            if (danger.TryGetValue(m.uniqueID, out bool d))
                return d;
            try { d = GenHostility.AnyHostileActiveThreatToPlayer(m, true) || m.fireWatcher.FireDanger > 0f; }
            catch { d = true; }
            danger[m.uniqueID] = d;
            return d;
        }

        public static int Rate(Thing t, int vanilla)
        {
            if (vanilla < 15 || t == null || !On)
                return vanilla;
            if (t is Pawn p && (p.Drafted || p.IsFighting()))
                return vanilla;
            Map map = t.MapHeld;
            if (map == null)
                return WorldRate;   // world pawns, pawns in caravans and transport pods
            if (Danger(map))
                return vanilla;
            if (map != Find.CurrentMap || !WorldRendererUtility.DrawingMap)
                return OtherMapRate;
            return OffscreenRate;
        }

        public static void CameraRatePost(Thing thing, ref int __result)
        {
            if (__result >= 15)
                __result = Rate(thing, __result);
        }

        public static void PawnRatePost(Pawn __instance, ref int __result)
        {
            if (__result >= 15 && __instance.RaceProps.Animal)
                __result = Rate(__instance, __result);
        }

        // the game caps the step at 15 ticks – raised only where Rate() allows it
        public static void MaxRatePost(Thing __instance, ref int __result)
        {
            if (__result != 15 || !On)
                return;
            int r = Rate(__instance, 15);
            if (r > __result)
                __result = r;
        }

        public static void WorldObjectRatePost(WorldObject __instance, ref int __result)
        {
            if (__result < 15 || !On || __instance == null)
                return;
            if (__instance.Faction == Faction.OfPlayer || __instance is Caravan || (__instance is MapParent mp && mp.HasMap))
                return;
            __result = WorldRate;
        }

        // ---------------- world clean-up ----------------
        // Old battles in the combat log keep every pawn that fought in them (dead raiders too) –
        // the game's own clean-up may not remove them. Only the newest battles are kept, then the
        // game's own world pawn clean-up runs (its own safety rules: colonists, relatives, quests,
        // faction leaders, tales … are always kept).
        public static string CleanWorld(bool manual)
        {
            if (Verse.Current.ProgramState != ProgramState.Playing)
                return "";
            int keep = manual ? Math.Max(KeepBattles, 4) : KeepBattles;
            if (keep <= 0)
                return "";
            WorldPawns wp = Find.WorldPawns;
            int before = wp.AllPawnsAliveOrDead.Count;
            int removedBattles = 0;
            try
            {
                List<Battle> battles = Find.BattleLog.Battles;
                // newest first in the list
                if (battles.Count > keep)
                {
                    removedBattles = battles.Count - keep;
                    battles.RemoveRange(keep, removedBattles);
                }
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] world clean-up (battles): " + ex.Message);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                wp.gc.RunGC();
            }
            catch (Exception ex)
            {
                // 1.4.1: the error comes from the mod that replaced the game's clean-up – name it, once
                string who = ModOf(ex);
                MLog.WarnOnce("[Smooth FPS] world clean-up: the game's world pawn clean-up failed" + (who != null ? " inside " + who : "") + " (" + ex.GetType().Name + ": " + ex.Message + "). Smooth FPS skips it; the mod's author should fix it.", 0x6f7a41);
            }
            int slept = 0;
            try { if (GraphicsTunerMod.Settings?.worldSleep ?? false) slept = WorldSleep.SleepNow(); }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] sleeping world pawns: " + ex.Message); }
            sw.Stop();
            int after = wp.AllPawnsAliveOrDead.Count;
            lastClean = Find.TickManager.TicksGame;
            string text = (string)"GT_SimCleaned".Translate(before - after, after, removedBattles, sw.ElapsedMilliseconds) + (slept > 0 ? " " + (string)"GT_SimSlept".Translate(slept) : "");
            MLog.Info("[Smooth FPS] " + text);
            return text;
        }

        private static int lastClean = -999999;

        private static string ModOf(Exception ex)
        {
            try
            {
                var st = new System.Diagnostics.StackTrace(ex, false);
                foreach (var f in st.GetFrames() ?? new System.Diagnostics.StackFrame[0])
                {
                    System.Reflection.Assembly asm = f.GetMethod()?.DeclaringType?.Assembly;
                    if (asm == null || asm == typeof(Pawn).Assembly || asm == typeof(Sim).Assembly) continue;
                    foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                        if (m.assemblies.loadedAssemblies.Contains(asm)) return m.Name;
                }
            }
            catch { }
            return null;
        }

        // automatically: in a pause of at least 3 s, at most once per 2 in-game days
        public static void AutoClean(bool paused, float pausedFor)
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.worldClean || KeepBattles <= 0 || !paused || pausedFor < 3f)
                return;
            int now = Find.TickManager?.TicksGame ?? 0;
            if (now - lastClean < 120000 && lastClean >= 0)
                return;
            if (lastClean < 0 && now < 2500)
                return;
            string t = CleanWorld(false);
            if (!t.NullOrEmpty())
                Messages.Message(t, MessageTypeDefOf.SilentInput, false);
        }

        public static void ResetGame()
        {
            lastClean = -999999;
            danger.Clear();
            detected = null;
        }

        private static float sleepAt = -10f;
        private static string sleepLine = "";

        public static string WorldLine()
        {
            if (Verse.Current.ProgramState != ProgramState.Playing)
                return "";
            WorldPawns wp = Find.WorldPawns;
            string line = (string)"GT_SimWorldLine".Translate(wp.AllPawnsAlive.Count(), wp.AllPawnsDead.Count(), Find.WorldObjects.AllWorldObjects.Count, Find.BattleLog.Battles.Count);
            // 1.4.6: how many of the living ones sleep, and what keeps the others awake
            if (Time.realtimeSinceStartup - sleepAt > 3f)
            {
                sleepAt = Time.realtimeSinceStartup;
                try
                {
                    WorldSleep.Counts(out int awake, out int asleep, out string top);
                    sleepLine = (string)"GT_SimSleepLine".Translate(awake, asleep, top.NullOrEmpty() ? "–" : top);
                }
                catch { sleepLine = ""; }
            }
            return line + (sleepLine.NullOrEmpty() ? "" : "\n" + sleepLine);
        }
    }

    // ---------------------------------------------------------------------------
    // 1.6.0: "What slows the tick" – a live list of the parts of the world and map tick,
    // ms per second of play, which mod. Needs the hitch finder (it uses its measuring points).
    // ---------------------------------------------------------------------------
    public class Dialog_TickCost : Window
    {
        private Vector2 scroll;
        private List<KeyValuePair<string, double>> rows = new List<KeyValuePair<string, double>>();
        private float nextRefresh;
        private float window;

        public override Vector2 InitialSize => new Vector2(820f, 640f);

        // 1.4.4: the settings and the Esc menu pause the game – the measurement needs it running,
        // so they close and the game goes on at the speed it had
        public static void OpenRunning()
        {
            try
            {
                foreach (Window w in Find.WindowStack.Windows.ToList())
                    if (w.forcePause || w is Dialog_ModSettings || w is Dialog_Options)
                        Find.WindowStack.TryRemove(w, false);
            }
            catch { }
            Find.WindowStack.Add(new Dialog_TickCost());
            if (Verse.Current.ProgramState == ProgramState.Playing)
                Messages.Message("GT_TC_Running".Translate(), MessageTypeDefOf.SilentInput, false);
        }

        public Dialog_TickCost()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            HitchFinder.RollReset();
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "GT_TC_Title".Translate());
            Text.Font = GameFont.Small;
            GfxSettings gs = GraphicsTunerMod.Settings;
            if (!HitchFinder.IsOn && gs != null && gs.hitchFinder)
                GraphicsTunerMod.StartHitchNow();
            if (!HitchFinder.IsOn)
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 60f), "GT_TC_NeedHitch".Translate());
                GUI.color = Color.white;
                return;
            }
            float now = Time.realtimeSinceStartup;
            if (now >= nextRefresh)
            {
                nextRefresh = now + 3f;
                rows = HitchFinder.RollTake(out window);
            }
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 40f), "GT_TC_Sub".Translate(window.ToString("0"), ("GT_Size" + Sim.Current).Translate())
                + " " + "GT_TC_Things".Translate(FastCull.LastCount));
            GUI.color = Color.white;
            Rect outR = new Rect(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Math.Max(30f, rows.Count * 26f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            double top = rows.Count > 0 ? rows[0].Value : 1.0;
            float y = 0f;
            foreach (var kv in rows)
            {
                Rect r = new Rect(0f, y, view.width, 24f);
                float frac = (float)(kv.Value / Math.Max(0.001, top));
                MUI.Fill(new Rect(r.x, r.y + 2f, (r.width - 120f) * frac, r.height - 4f), new Color(MUI.Accent.r, MUI.Accent.g, MUI.Accent.b, 0.18f));
                Widgets.Label(new Rect(r.x + 4f, r.y + 2f, r.width - 130f, 22f), kv.Key.Truncate(r.width - 130f));
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(r.xMax - 120f, r.y + 2f, 116f, 22f), "GT_TC_Ms".Translate(kv.Value.ToString("0.0")));
                Text.Anchor = TextAnchor.UpperLeft;
                y += 26f;
            }
            if (rows.Count == 0)
                Widgets.Label(new Rect(0f, 0f, view.width, 26f), "GT_TC_Wait".Translate());
            Widgets.EndScrollView();
        }
    }
}

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.4.6: sleeping world pawns. The game puts world pawns that need nothing to "sleep"
    // (mothball) and then updates them only every 15000 ticks. A pawn with a condition that
    // has to be updated stays awake – and mods (RJW parts, race genes, quirks …) give such a
    // condition to almost every pawn, so hundreds of pawns far away were updated every tick.
    // A condition that comes from a mod no longer keeps a world pawn awake (pregnancies and
    // the game's own conditions still do). A sleeping pawn wakes up the moment it comes back
    // to a map or a caravan, exactly as in the game.
    // ---------------------------------------------------------------------------
    public static class WorldSleep
    {
        private static readonly System.Reflection.FieldInfo fAlive = AccessTools.Field(typeof(WorldPawns), "pawnsAlive");
        private static readonly System.Reflection.FieldInfo fMoth = AccessTools.Field(typeof(WorldPawns), "pawnsMothballed");
        private static readonly System.Reflection.MethodInfo mShould = AccessTools.Method(typeof(WorldPawns), "ShouldMothball");
        private static readonly System.Reflection.MethodInfo mPrevent = AccessTools.Method(typeof(WorldPawns), "DefPreventingMothball");

        public static void Patch(Harmony h)
        {
            try
            {
                if (mPrevent != null)
                    h.Patch(mPrevent, postfix: new HarmonyMethod(typeof(WorldSleep), nameof(Postfix)));
            }
            catch (Exception ex) { MLog.Warn("[Smooth FPS] sleeping world pawns not available: " + ex.Message); }
        }

        private static bool On => GraphicsTunerMod.Settings?.worldSleep ?? false;

        public static bool ModCondition(HediffDef d) =>
            d != null && d.modContentPack != null && !d.modContentPack.IsOfficialMod
            && (d.hediffClass == null || d.hediffClass.Name.IndexOf("Pregnan", StringComparison.OrdinalIgnoreCase) < 0)
            && d.defName.IndexOf("Pregnan", StringComparison.OrdinalIgnoreCase) < 0;

        public static void Postfix(Pawn p, ref HediffDef __result)
        {
            if (__result == null || !On || !ModCondition(__result)) return;
            try
            {
                // only the game's own conditions may keep it awake
                foreach (Hediff h in p.health.hediffSet.hediffs)
                {
                    HediffDef d = h.def;
                    if (d.AlwaysAllowMothball || h.IsPermanent() || (h is Hediff_MissingPart mp && !mp.Bleeding)) continue;
                    if (!ModCondition(d)) { __result = d; return; }
                }
                __result = null;
            }
            catch { }
        }

        // put to sleep now everyone who may sleep (the game does it only every 15000 ticks)
        public static int SleepNow()
        {
            WorldPawns wp = Find.WorldPawns;
            if (wp == null || fAlive == null || fMoth == null || mShould == null) return 0;
            var alive = fAlive.GetValue(wp) as HashSet<Pawn>;
            var moth = fMoth.GetValue(wp) as HashSet<Pawn>;
            if (alive == null || moth == null) return 0;
            int n = 0;
            foreach (Pawn p in alive.ToList())
            {
                try
                {
                    if ((bool)mShould.Invoke(wp, new object[] { p }))
                    {
                        alive.Remove(p);
                        moth.Add(p);
                        n++;
                    }
                }
                catch { }
            }
            return n;
        }

        public static void Counts(out int awake, out int asleep, out string top)
        {
            awake = asleep = 0;
            top = "";
            WorldPawns wp = Find.WorldPawns;
            if (wp == null || fAlive == null || fMoth == null) return;
            var alive = fAlive.GetValue(wp) as HashSet<Pawn>;
            var moth = fMoth.GetValue(wp) as HashSet<Pawn>;
            awake = alive?.Count ?? 0;
            asleep = moth?.Count ?? 0;
            if (alive == null || mPrevent == null) return;
            var why = new Dictionary<string, int>();
            foreach (Pawn p in alive)
            {
                try
                {
                    HediffDef d = mPrevent.Invoke(wp, new object[] { p }) as HediffDef;
                    string k = d != null ? d.LabelCap.ToString() : p.IsCaravanMember() ? "caravan" : "–";
                    why[k] = why.TryGetValue(k, out int c) ? c + 1 : 1;
                }
                catch { }
            }
            top = string.Join(", ", why.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key + " " + kv.Value).ToArray());
        }
    }
}
