using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace UnofficialPatch
{
    public static class Bugs
    {
        // ------------------------------------------------------------------
        // Caravan window: choosing medicine for a pawn without player settings (vehicles and other
        // modded pawns) threw an exception every frame and the window stopped working.
        // Such pawns now simply get no medicine – nothing else changes.
        // ------------------------------------------------------------------
        public static void PatchCaravan(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(Dialog_FormCaravan), "BestMedicineItemFor"),
                prefix: new HarmonyMethod(typeof(Bugs), nameof(MedicinePrefix)));
        }

        public static bool MedicinePrefix(Pawn pawn, ref Transferable __result)
        {
            if (!UnofficialPatchMod.OnCaravan || pawn?.playerSettings != null)
                return true;
            __result = null;
            return false;
        }

        // ------------------------------------------------------------------
        // "Create new wanderers" never came when the last colonist lay in an ancient cryptosleep
        // casket: the game counted them as alive (no new wanderers) but not as a colonist it can
        // reach (game over). A pawn in a casket that is not the player's no longer blocks the
        // offer. Everything else that blocks it in vanilla still does.
        // ------------------------------------------------------------------
        public static void PatchWanderers(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(GameEnder), nameof(GameEnder.CanSpawnNewWanderers)),
                postfix: new HarmonyMethod(typeof(Bugs), nameof(WanderersPostfix)));
        }

        public static void WanderersPostfix(ref bool __result)
        {
            if (__result || !UnofficialPatchMod.OnWanderers)
                return;
            bool anyCasket = false;
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
            {
                if (p.RaceProps.Humanlike && !p.Dead)
                {
                    if (InForeignCasket(p))
                    {
                        anyCasket = true;
                        continue;
                    }
                    return;
                }
                if (ModsConfig.AnomalyActive && (DeathRefusalUtility.HasPlayerControlledDeathRefusal(p) || DeathRefusalUtility.PlayerHasCorpseWithDeathRefusal()))
                    return;
            }
            if (!anyCasket)
                return;   // vanilla said no for another reason – keep it
            if (ModsConfig.AnomalyActive && Find.Anomaly.VoidAwakeningActive())
                return;
            __result = true;
        }

        private static bool InForeignCasket(Pawn p)
        {
            if (p.Spawned)
                return false;
            for (IThingHolder h = p.ParentHolder; h != null; h = h.ParentHolder)
                if (h is Building_Casket c)
                    return c.Faction != Faction.OfPlayer;
            return false;
        }
    }
}
