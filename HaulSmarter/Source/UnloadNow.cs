using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HaulSmarter
{
    // 1.2.0: "Unload now" button on a colonist who carries things picked up by Haul Master
    [StaticConstructorOnStartup]
    public static class UnloadNow
    {
        public static readonly Texture2D Icon = ContentFinder<Texture2D>.Get("HaulSmarter/UnloadNow", false) ?? BaseContent.BadTex;

        public static void Order(Pawn p)
        {
            if (p == null || !HaulUtil.HasTracked(p)) return;
            if (!HaulUtil.CanUnloadHere(p))
            {
                Messages.Message("HS_UnloadNow_Nowhere".Translate(p.LabelShort), p, MessageTypeDefOf.RejectInput, false);
                return;
            }
            Job j = JobMaker.MakeJob(HSDefOf.HaulSmarterUnload);
            j.playerForced = true;
            p.jobs.TryTakeOrderedJob(j, JobTag.Misc);
        }
    }

    public class Command_UnloadNow : Command_Action
    {
        public Pawn pawn;

        public override IEnumerable<FloatMenuOption> RightClickFloatMenuOptions
        {
            get
            {
                Pawn p = pawn;
                yield return new FloatMenuOption("HS_UnloadNow_Drop".Translate(), () => HaulUtil.DropAllTracked(p));
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_PawnUnloadGizmo
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (Gizmo g in gizmos)
                yield return g;
            Pawn p = __instance;
            if (p == null || !p.Spawned || !p.IsColonistPlayerControlled || !HaulUtil.HasTracked(p))
                yield break;
            int n = HaulUtil.TrackedItems(p).Count();
            yield return new Command_UnloadNow
            {
                pawn = p,
                defaultLabel = "HS_UnloadNow".Translate(),
                defaultDesc = "HS_UnloadNow_Desc".Translate(n, HaulUtil.TrackedMass(p).ToString("0.#")),
                icon = UnloadNow.Icon,
                action = () => UnloadNow.Order(p)
            };
        }
    }
}
