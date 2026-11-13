using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace HaulSmarter
{
    // 3.6: haul to an existing stack first. The game picks the nearest free cell of the best storage, so the same
    // thing ends up in many half stacks. When a storage just as good (or better) already holds a stack of the
    // same thing with room, the hauler goes there instead – for every hauling job of the colony, not only ours.
    public static class StackFirst
    {
        private static bool busy;

        public static void Patch(Harmony h)
        {
            try
            {
                var m = AccessTools.Method(typeof(StoreUtility), nameof(StoreUtility.TryFindBestBetterStoreCellFor),
                    new[] { typeof(Thing), typeof(Pawn), typeof(Map), typeof(StoragePriority), typeof(Faction), typeof(IntVec3).MakeByRefType(), typeof(bool) });
                if (m != null)
                    h.Patch(m, postfix: new HarmonyMethod(typeof(StackFirst), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning("[Haul Master] stack-first skipped: " + e.Message); }
        }

        public static void Postfix(Thing t, Pawn carrier, Map map, Faction faction, ref IntVec3 foundCell, ref bool __result)
        {
            if (!__result || busy || HSMod.S == null || !HSMod.S.stackFirst || !Safe.On("stackFirst"))
                return;
            if (t == null || map == null || t.def.stackLimit <= 1 || carrier == null || carrier.Faction != Faction.OfPlayer)
                return;
            busy = true;
            try
            {
                IntVec3 better = Find(t, carrier, map, faction, foundCell);
                if (better.IsValid)
                    foundCell = better;
            }
            catch (Exception e)
            {
                Safe.Fail("stackFirst", e);
            }
            finally
            {
                busy = false;
            }
        }

        private static StoragePriority PriorityAt(Map map, IntVec3 c)
        {
            ISlotGroupParent g = map.haulDestinationManager.SlotGroupParentAt(c);
            return g?.GetStoreSettings()?.Priority ?? StoragePriority.Unstored;
        }

        public static IntVec3 Find(Thing t, Pawn carrier, Map map, Faction faction, IntVec3 found)
        {
            StoragePriority need = PriorityAt(map, found);
            IntVec3 from = carrier.PositionHeld;
            float limit = from.DistanceTo(found) * 1.5f + 12f;
            IntVec3 best = IntVec3.Invalid;
            float bestD = float.MaxValue;
            List<Thing> same = map.listerThings.ThingsOfDef(t.def);
            for (int i = 0; i < same.Count; i++)
            {
                Thing s = same[i];
                if (s == t || !s.Spawned || s.stackCount >= s.def.stackLimit || s.Position == t.PositionHeld)
                    continue;
                if (!s.IsInAnyStorage() || PriorityAt(map, s.Position) < need)
                    continue;
                if (!s.CanStackWith(t))
                    continue;
                float d = from.DistanceTo(s.Position);
                if (d > limit || d >= bestD)
                    continue;
                if (!StoreUtility.IsGoodStoreCell(s.Position, map, t, carrier, faction))
                    continue;
                best = s.Position;
                bestD = d;
            }
            return best;
        }
    }
}
