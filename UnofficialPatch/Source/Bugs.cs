using System;
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
        // reach (game over). A pawn in a casket the player cannot eject (ancient casket) no longer blocks the
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

        // the same rule as the game-over check (MapPawns.PlayerEjectablePodHolder): a pawn in a
        // cryptosleep casket the player cannot eject (ancient casket) does not count as reachable
        private static bool InForeignCasket(Pawn p)
        {
            if (p.Spawned)
                return false;
            for (IThingHolder h = p.ParentHolder; h != null; h = h.ParentHolder)
                if (h is Building_CryptosleepCasket c)
                    return c.def?.building != null && !c.def.building.isPlayerEjectable;
            return false;
        }
    
        // ------------------------------------------------------------------
        // 0.1.2: an IncidentDef without a usable workerClass or a WorkGiverDef without a usable
        // giverClass (a mod forgot it, or its class failed to load) – the game creates the worker
        // only when it is first needed and then throws an unhelpful error every time the
        // storyteller or a pawn tries it. Checked once after loading: such a def is logged with its
        // mod and gets a worker that never fires / always skips. (Idea from Performance Fish.)
        // ------------------------------------------------------------------
        public static void PatchMissingWorkers(Harmony h)
        {
            LongEventHandler.ExecuteWhenFinished(FixMissingWorkers);
        }

        private static bool Usable(Type t, Type baseType)
        {
            return t != null && !t.IsAbstract && baseType.IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) != null;
        }

        public static void FixMissingWorkers()
        {
            if (!UnofficialPatchMod.Settings.IsOn("workers"))
                return;
            int n = 0;
            try
            {
                foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefsListForReading)
                {
                    if (Usable(d.workerClass, typeof(IncidentWorker)))
                        continue;
                    Log.Warning("[Unofficial 1.7 Patch] IncidentDef '" + d.defName + "' from mod '" + (d.modContentPack?.Name ?? "?") + "' has no usable workerClass (" + (d.workerClass?.FullName ?? "none") + ") – it will never fire instead of throwing errors.");
                    d.workerClass = typeof(IncidentWorker_NeverFires);
                    n++;
                }
                foreach (WorkGiverDef d in DefDatabase<WorkGiverDef>.AllDefsListForReading)
                {
                    if (Usable(d.giverClass, typeof(WorkGiver)))
                        continue;
                    Log.Warning("[Unofficial 1.7 Patch] WorkGiverDef '" + d.defName + "' from mod '" + (d.modContentPack?.Name ?? "?") + "' (work type " + (d.workType?.defName ?? "?") + ") has no usable giverClass (" + (d.giverClass?.FullName ?? "none") + ") – pawns will skip it instead of throwing errors.");
                    d.giverClass = typeof(WorkGiver_AlwaysSkipped);
                    n++;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Unofficial 1.7 Patch] missing worker check failed: " + e.Message);
            }
            if (n > 0)
                Log.Message("[Unofficial 1.7 Patch] " + n + " defs without a usable worker class were disabled (see warnings above).");
        }
    }

    // 0.1.2: stand-in workers for defs whose class is missing
    public class IncidentWorker_NeverFires : IncidentWorker
    {
        public override float BaseChanceThisGame => 0f;

        public override float ChanceFactorNow(IIncidentTarget target) => 0f;

        protected override bool CanFireNowSub(IncidentParms parms) => false;

        protected override bool TryExecuteWorker(IncidentParms parms) => false;
    }

    public class WorkGiver_AlwaysSkipped : WorkGiver
    {
        public override bool ShouldSkip(Pawn pawn, bool forced = false) => true;
    }
}
