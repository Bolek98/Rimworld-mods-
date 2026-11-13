using System;
using System.Collections.Generic;
using HarmonyLib;
using Verse;

namespace GraphicsTuner
{
    // 1.7.2: the game's pawn texture atlas cleanup (PawnTextureAtlas.GC) asks every pawn it holds whether it is
    // spawned. A pawn whose map is already gone (a quest map closed while flying home) then logs a red
    // "Thing … is associated with invalid map index" error. Harmless, but scary – we free such pawns first.
    public static class AtlasGuard
    {
        private static readonly AccessTools.FieldRef<PawnTextureAtlas, Dictionary<Pawn, PawnTextureAtlasFrameSet>> assigned =
            AccessTools.FieldRefAccess<PawnTextureAtlas, Dictionary<Pawn, PawnTextureAtlasFrameSet>>("frameAssignments");
        private static readonly AccessTools.FieldRef<PawnTextureAtlas, List<PawnTextureAtlasFrameSet>> free =
            AccessTools.FieldRefAccess<PawnTextureAtlas, List<PawnTextureAtlasFrameSet>>("freeFrameSets");
        private static readonly AccessTools.FieldRef<Thing, sbyte> mapIndex = AccessTools.FieldRefAccess<Thing, sbyte>("mapIndexOrState");
        private static readonly List<Pawn> gone = new List<Pawn>();
        private static bool failed;

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(PawnTextureAtlas), nameof(PawnTextureAtlas.GC)),
                    prefix: new HarmonyMethod(typeof(AtlasGuard), nameof(Prefix)));
            }
            catch (Exception e) { MLog.Warn("[Smooth FPS] atlas guard skipped: " + e.Message); }
        }

        public static void Prefix(PawnTextureAtlas __instance)
        {
            if (failed)
                return;
            try
            {
                int maps = Find.Maps?.Count ?? 0;
                var dict = assigned(__instance);
                foreach (Pawn p in dict.Keys)
                    if (p != null && mapIndex(p) >= maps)
                        gone.Add(p);
                if (gone.Count == 0)
                    return;
                var freeList = free(__instance);
                foreach (Pawn p in gone)
                {
                    freeList.Add(dict[p]);
                    dict.Remove(p);
                }
            }
            catch (Exception e)
            {
                failed = true;
                MLog.Warn("[Smooth FPS] atlas guard off: " + e.Message);
            }
            finally
            {
                gone.Clear();
            }
        }
    }
}
