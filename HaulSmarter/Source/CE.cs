using System;
using System.Reflection;
using RimWorld;
using Verse;

namespace HaulSmarter
{
    // 1.1.1: Combat Extended – besides weight, CE limits the inventory by bulk. Read through reflection,
    // so CE is no dependency; without CE every limit here is "no limit".
    public static class CE
    {
        private static bool done, active;
        public static bool Active { get { if (!done) { done = true; Init(); } return active; } }
        private static Type compType;
        private static PropertyInfo availBulkProp;
        private static FieldInfo availBulkField;
        private static MethodInfo updateInv;
        private static StatDef bulkStat;

        private static void Init()
        {
            try
            {
                compType = GenTypes.GetTypeInAnyAssembly("CombatExtended.CompInventory");
                bulkStat = DefDatabase<StatDef>.GetNamedSilentFail("Bulk");
                if (compType == null || bulkStat == null)
                    return;
                const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                availBulkProp = compType.GetProperty("availableBulk", F);
                if (availBulkProp == null)
                    availBulkField = compType.GetField("availableBulk", F);
                updateInv = compType.GetMethod("UpdateInventory", F, null, Type.EmptyTypes, null);
                active = availBulkProp != null || availBulkField != null;
                Log.Message(active ? "[Haul Master] Combat Extended found – bulk limit is used."
                                   : "[Haul Master] Combat Extended found, but its inventory could not be read – only weight is used.");
            }
            catch (Exception e)
            {
                active = false;
                Log.Warning("[Haul Master] Combat Extended support off: " + e.Message);
            }
        }

        private static ThingComp Comp(Pawn pawn)
        {
            if (pawn?.AllComps == null) return null;
            foreach (ThingComp c in pawn.AllComps)
                if (compType.IsInstanceOfType(c))
                    return c;
            return null;
        }

        // free bulk of the pawn's inventory; float.MaxValue without CE (or a pawn without CE inventory)
        public static float FreeBulk(Pawn pawn)
        {
            if (!Active) return float.MaxValue;
            try
            {
                ThingComp c = Comp(pawn);
                if (c == null) return float.MaxValue;
                try { updateInv?.Invoke(c, null); } catch { }
                object v = availBulkProp != null ? availBulkProp.GetValue(c, null) : availBulkField.GetValue(c);
                return Convert.ToSingle(v);
            }
            catch
            {
                return float.MaxValue;
            }
        }

        public static float UnitBulk(Thing t)
        {
            if (!Active || t == null) return 0f;
            try
            {
                float b = t.GetStatValue(bulkStat);
                return b > 0f ? b : 0f;
            }
            catch
            {
                return 0f;
            }
        }
    }
}
