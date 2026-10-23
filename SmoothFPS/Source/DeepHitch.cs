using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace GraphicsTuner
{
    // ---------------------------------------------------------------------------
    // 1.5.0: deep hitch finder. The normal hitch finder can only say "a Silkiera
    // pawn took 130 ms". With the deep mode on it also says WHAT inside the pawn:
    // which work search (WorkGiver), which AI decision (JobGiver), path search,
    // which thing/hediff component and from which mod. Every call is only added to
    // a counter for the current frame (cheap); the list is written with a hitch.
    // Off by default – it costs a little speed while it is on.
    // ---------------------------------------------------------------------------
    public static class DeepHitch
    {
        public static bool On;
        private static readonly double tickToMs = 1000.0 / Stopwatch.Frequency;
        private static double[] wg = new double[0];
        private static readonly Dictionary<Type, double> byType = new Dictionary<Type, double>();
        private static readonly Dictionary<string, double> byName = new Dictionary<string, double>();
        [ThreadStatic] private static int depthWg;
        private static int patched;
        private static Func<Assembly, string> modName;

        public const string Mark = "↳ ";

        public static void Install(Harmony h, Func<Assembly, string> mod)
        {
            modName = mod;
            int before = patched;
            wg = new double[DefDatabase<WorkGiverDef>.DefCount + 8];
            var wgPre = new HarmonyMethod(typeof(DeepHitch), nameof(WgPre));
            var wgPost = new HarmonyMethod(typeof(DeepHitch), nameof(WgPost));
            var tPre = new HarmonyMethod(typeof(DeepHitch), nameof(Pre));
            var tPost = new HarmonyMethod(typeof(DeepHitch), nameof(TypePost));
            string[] wgNames = { "HasJobOnThing", "JobOnThing", "HasJobOnCell", "JobOnCell", "NonScanJob", "ShouldSkip", "PotentialWorkThingsGlobal", "PotentialWorkCellsGlobal" };
            string[] compNames = { "CompTick", "CompTickInterval", "CompTickRare", "CompTickLong" };
            string[] hcompNames = { "CompPostTick", "CompPostTickInterval" };
            string[] hediffNames = { "Tick", "TickInterval", "PostTick", "PostTickInterval" };
            string[] needNames = { "NeedInterval" };
            foreach (Type t in GenTypes.AllTypes)
            {
                if (t.IsGenericTypeDefinition || t.IsInterface)
                    continue;
                string[] names = null;
                HarmonyMethod pre = tPre, post = tPost;
                if (typeof(WorkGiver).IsAssignableFrom(t)) { names = wgNames; pre = wgPre; post = wgPost; }
                else if (typeof(ThinkNode_JobGiver).IsAssignableFrom(t)) names = new[] { "TryGiveJob" };
                else if (typeof(ThingComp).IsAssignableFrom(t)) names = compNames;
                else if (typeof(HediffComp).IsAssignableFrom(t)) names = hcompNames;
                else if (typeof(Hediff).IsAssignableFrom(t)) names = hediffNames;
                else if (typeof(Need).IsAssignableFrom(t)) names = needNames;
                if (names == null)
                    continue;
                foreach (MethodInfo m in t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (Array.IndexOf(names, m.Name) < 0 || m.IsAbstract || m.IsGenericMethodDefinition)
                        continue;
                    // lazy lists (yield) only build the list here – the work is done later, skip them
                    if (m.GetCustomAttribute<IteratorStateMachineAttribute>() != null)
                        continue;
                    try
                    {
                        if (m.GetMethodBody() == null)
                            continue;
                        h.Patch(m, pre, post);
                        patched++;
                    }
                    catch
                    {
                    }
                }
            }
            // path search right now (the slow kind), the whole job search, pawn sub-ticks
            Named(h, AccessTools.Method(typeof(Pawn_JobTracker), "TryFindAndStartJob"), "GT_DeepJobSearch");
            Named(h, AccessTools.Method(typeof(Pawn_PathFollower), nameof(Pawn_PathFollower.PatherTick)), "GT_DeepPather");
            foreach (MethodInfo m in typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(x => x.Name == "FindPathNow"))
                Named(h, m, "GT_DeepPath");
            MLog.Info("[Smooth FPS] deep hitch finder on (" + (patched - before) + " places watched).");
        }

        private static readonly Dictionary<MethodBase, string> named = new Dictionary<MethodBase, string>();

        private static void Named(Harmony h, MethodBase m, string key)
        {
            if (m == null)
                return;
            try
            {
                named[m] = key;
                h.Patch(m, new HarmonyMethod(typeof(DeepHitch), nameof(Pre)), new HarmonyMethod(typeof(DeepHitch), nameof(NamedPost)));
                patched++;
            }
            catch
            {
            }
        }

        public static void Pre(out long __state) => __state = Stopwatch.GetTimestamp();

        public static void WgPre(out long __state)
        {
            __state = depthWg++ == 0 ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void WgPost(WorkGiver __instance, long __state)
        {
            depthWg--;
            if (depthWg < 0) depthWg = 0;
            if (__state == 0L || __instance?.def == null)
                return;
            int i = __instance.def.index;
            if (i >= 0 && i < wg.Length)
                wg[i] += (Stopwatch.GetTimestamp() - __state) * tickToMs;
        }

        public static void TypePost(object __instance, long __state)
        {
            if (__instance == null)
                return;
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            Type t = __instance.GetType();
            byType.TryGetValue(t, out double v);
            byType[t] = v + ms;
        }

        public static void NamedPost(MethodBase __originalMethod, long __state)
        {
            double ms = (Stopwatch.GetTimestamp() - __state) * tickToMs;
            if (!named.TryGetValue(__originalMethod, out string key))
                return;
            byName.TryGetValue(key, out double v);
            byName[key] = v + ms;
        }

        // the frame is over: hand what took at least 1 ms to the hitch list, then start again
        public static void Flush(Action<string, double> add, bool want)
        {
            if (want)
            {
                List<WorkGiverDef> defs = DefDatabase<WorkGiverDef>.AllDefsListForReading;
                for (int i = 0; i < wg.Length; i++)
                {
                    if (wg[i] < 1.0)
                        continue;
                    WorkGiverDef d = defs.FirstOrDefault(x => x.index == i);
                    if (d != null)
                        add(Mark + "GT_DeepWork".Translate(d.defName, d.workType?.defName ?? "?") + " [" + (d.modContentPack?.Name ?? "?") + "]", wg[i]);
                }
                foreach (var kv in byType)
                    if (kv.Value >= 1.0)
                        add(Mark + Kind(kv.Key) + " " + kv.Key.Name + " [" + modName(kv.Key.Assembly) + "]", kv.Value);
                foreach (var kv in byName)
                    if (kv.Value >= 1.0)
                        add(Mark + kv.Key.Translate(), kv.Value);
            }
            Array.Clear(wg, 0, wg.Length);
            depthWg = 0;
            byType.Clear();
            byName.Clear();
        }

        private static string Kind(Type t)
        {
            if (typeof(ThinkNode_JobGiver).IsAssignableFrom(t)) return "GT_DeepKindJobGiver".Translate();
            if (typeof(ThingComp).IsAssignableFrom(t)) return "GT_DeepKindComp".Translate();
            if (typeof(HediffComp).IsAssignableFrom(t) || typeof(Hediff).IsAssignableFrom(t)) return "GT_DeepKindHediff".Translate();
            if (typeof(Need).IsAssignableFrom(t)) return "GT_DeepKindNeed".Translate();
            return "";
        }
    }
}
