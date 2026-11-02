using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace GraphicsTuner
{
    // 1.6.1: one broken pawn or corpse no longer hides everyone else.
    // The game prepares all moving things of a map in one loop before drawing them. When one of them
    // throws there (e.g. a corpse whose body parts another mod cannot draw), the whole loop stops every
    // frame and the pawns after it in the list are not drawn at all – only their names stay.
    // The guard catches the error for that one thing, writes it to the log once and leaves that thing
    // out of drawing for a while (it tries again after 10 seconds), so the rest of the map draws.
    public static class DrawGuard
    {
        private static readonly Dictionary<int, int> skipUntil = new Dictionary<int, int>();
        private static readonly HashSet<int> logged = new HashSet<int>();
        private static readonly object gate = new object();
        private static volatile int count;
        private const int RetryFrames = 10000; // milliseconds
        private static int frame => Environment.TickCount;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.DynamicDrawPhase)),
                    prefix: new HarmonyMethod(typeof(DrawGuard), nameof(Prefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(DrawGuard), nameof(Finalizer)));
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] draw guard not active: " + ex.Message);
            }
        }

        public static bool Prefix(Thing __instance)
        {
            if (count == 0 || __instance == null)
                return true;
            lock (gate)
            {
                int until;
                if (!skipUntil.TryGetValue(__instance.thingIDNumber, out until))
                    return true;
                if (frame < until)
                    return false;
                skipUntil.Remove(__instance.thingIDNumber);
                count = skipUntil.Count;
                return true;
            }
        }

        public static Exception Finalizer(Exception __exception, Thing __instance)
        {
            if (__exception == null || __instance == null)
                return __exception;
            try
            {
                int id = __instance.thingIDNumber;
                bool first;
                lock (gate)
                {
                    skipUntil[id] = frame + RetryFrames;
                    count = skipUntil.Count;
                    first = logged.Add(id);
                }
                if (first)
                {
                    string what = __instance.LabelCap + " (" + __instance.def?.defName + ")";
                    Log.Warning("[Smooth FPS] " + what + " could not be drawn and is left out for now, so the rest of the map still draws. "
                        + "Cause: " + __exception.GetType().Name + ": " + __exception.Message + "\n" + __exception.StackTrace);
                    MLog.Warn("[Smooth FPS] draw guard: " + what + " – " + __exception.GetType().Name + ": " + __exception.Message);
                }
            }
            catch { }
            return null;
        }
    }
}
