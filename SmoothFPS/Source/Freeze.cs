using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.7.2: freeze watch. A total freeze writes nothing into any log (a hitch is only written
    // when its frame ends). A small background thread sees that frames stopped and writes
    // what the game was inside at that moment (the places the hitch finder watches:
    // map/world/game components of every mod, ticks, saving, drawing …).
    public static class FreezeWatch
    {
        private static readonly double freq = Stopwatch.Frequency;
        private static long lastBeat;
        private static bool focused = true;
        private static Thread thread;
        private static int mainId = -1;
        private static int told;
        private static double frozenFor;

        // what the main thread is inside right now (watched places only)
        private static readonly object[] open = new object[64];
        private static int depth;
        private static object lastDone;

        private static readonly int[] marks = { 10, 30, 60, 120, 300 };

        public static void Beat()
        {
            if (mainId < 0)
                mainId = Thread.CurrentThread.ManagedThreadId;
            if (told > 0)
            {
                MLog.Warn("[Smooth FPS] the game runs again after a freeze of " + frozenFor.ToString("0") + " s.");
                told = 0;
            }
            depth = 0;
            focused = Application.isFocused || Application.runInBackground;
            Interlocked.Exchange(ref lastBeat, Stopwatch.GetTimestamp());
            if (thread == null)
            {
                thread = new Thread(Loop) { IsBackground = true, Name = "SmoothFPS freeze watch", Priority = System.Threading.ThreadPriority.BelowNormal };
                thread.Start();
                MLog.Info("[Smooth FPS] freeze watch on (writes what the game is doing when it stops for 10 s or more).");
            }
        }

        public static void Push(object what)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainId)
                return;
            int d = depth;
            if (d < open.Length)
                open[d] = what;
            depth = d + 1;
        }

        public static void Pop(object what)
        {
            if (Thread.CurrentThread.ManagedThreadId != mainId)
                return;
            if (depth > 0)
                depth--;
            if (what != null)
                lastDone = what;
        }

        private static void Loop()
        {
            while (true)
            {
                try
                {
                    Thread.Sleep(1000);
                    long b = Interlocked.Read(ref lastBeat);
                    if (b == 0 || !focused)
                        continue;
                    double s = (Stopwatch.GetTimestamp() - b) / freq;
                    if (told >= marks.Length || s < marks[told])
                        continue;
                    frozenFor = s;
                    told++;
                    MLog.Warn("[Smooth FPS] FREEZE: no frame for " + s.ToString("0") + " s. Inside now: " + Inside() + " | last finished: " + Name(lastDone) + Loading());
                }
                catch
                {
                }
            }
        }

        private static string Loading()
        {
            try
            {
                return LongEventHandler.AnyEventNowOrWaiting ? " | a long event (loading/saving) is running" : "";
            }
            catch
            {
                return "";
            }
        }

        private static string Inside()
        {
            int d = Math.Min(depth, open.Length);
            if (d <= 0)
                return "nothing watched (drawing, windows or code outside the watched places)";
            var sb = new StringBuilder();
            for (int i = 0; i < d; i++)
            {
                if (i > 0)
                    sb.Append(" → ");
                sb.Append(Name(open[i]));
            }
            return sb.ToString();
        }

        private static string Name(object o)
        {
            try
            {
                if (o == null)
                    return "-";
                if (o is MethodBase m)
                    return m.DeclaringType?.Name + "." + m.Name + " [" + HitchFinder.ModName(m.DeclaringType?.Assembly) + "]";
                if (o is Thing t)
                    return (t.def?.defName ?? "?") + " tick [" + (t.def?.modContentPack?.Name ?? "?") + "]";
                return o.ToString();
            }
            catch
            {
                return "?";
            }
        }
    }
}
