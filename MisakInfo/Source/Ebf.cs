// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Reflection;
using Verse;

namespace MisakInfo
{
    // 1.0.2: Elite Bionics Framework – EBF changes the max HP of body parts; the true value comes only from
    // EBF.EBFEndpoints.GetMaxHealthWithEBF. Calling def.GetMaxHealth directly gave the vanilla max (wrong bars
    // and numbers) and EBF warnings in the log. Read through reflection, so EBF is no dependency.
    public static class Ebf
    {
        private static bool done;
        private static Func<BodyPartRecord, Pawn, bool, float> withEbf;
        private static Func<BodyPartDef, Pawn, float> unmodified;

        private static void Init()
        {
            done = true;
            try
            {
                Type t = GenTypes.GetTypeInAnyAssembly("EBF.EBFEndpoints");
                if (t == null) return;
                MethodInfo m = t.GetMethod("GetMaxHealthWithEBF", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(BodyPartRecord), typeof(Pawn), typeof(bool) }, null);
                if (m != null)
                    withEbf = (Func<BodyPartRecord, Pawn, bool, float>)Delegate.CreateDelegate(typeof(Func<BodyPartRecord, Pawn, bool, float>), m);
                MethodInfo u = t.GetMethod("GetMaxHealthUnmodified", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(BodyPartDef), typeof(Pawn) }, null);
                if (u != null)
                    unmodified = (Func<BodyPartDef, Pawn, float>)Delegate.CreateDelegate(typeof(Func<BodyPartDef, Pawn, float>), u);
            }
            catch (Exception e)
            {
                withEbf = null;
                Log.Warning("[Clear Tabs] Elite Bionics Framework support off: " + e.Message);
            }
        }

        public static float MaxHp(BodyPartRecord part, Pawn pawn)
        {
            if (part == null) return 1f;
            if (!done) Init();
            if (withEbf != null)
            {
                try { return withEbf(part, pawn, true); }
                catch { withEbf = null; }
            }
            return part.def.GetMaxHealth(pawn);
        }
    }
}
