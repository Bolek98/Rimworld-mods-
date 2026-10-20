using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Matchmaker
{
    public class MMSettings : ModSettings
    {
        public bool onlyMine = true;      // colonists don't start romances on their own
        public bool keepPairs = true;     // chosen pairs never break up
        public bool ignoreOpinion = true; // low opinion doesn't slow chosen pairs down

        public override void ExposeData()
        {
            Scribe_Values.Look(ref onlyMine, "onlyMine", true);
            Scribe_Values.Look(ref keepPairs, "keepPairs", true);
            Scribe_Values.Look(ref ignoreOpinion, "ignoreOpinion", true);
        }
    }

    public class MMMod : Mod
    {
        public static MMSettings S;

        public MMMod(ModContentPack c) : base(c)
        {
            S = GetSettings<MMSettings>();
            var h = new Harmony("bolek98.matchmaker");
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(MMMod).Assembly))
            {
                try { h.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { Log.Warning("[Matchmaker] " + t.Name + ": " + e.Message); }
            }
        }

        public override string SettingsCategory() => "Matchmaker";

        public override void DoSettingsWindowContents(Rect r)
        {
            var l = new Listing_Standard();
            l.Begin(r);
            l.CheckboxLabeled("MM_OnlyMine".Translate(), ref S.onlyMine, "MM_OnlyMineTip".Translate());
            l.CheckboxLabeled("MM_KeepPairs".Translate(), ref S.keepPairs, "MM_KeepPairsTip".Translate());
            l.CheckboxLabeled("MM_IgnoreOpinion".Translate(), ref S.ignoreOpinion, "MM_IgnoreOpinionTip".Translate());
            l.Gap();
            l.Label("<color=#999999>" + "MM_AdultsOnly".Translate() + "</color>");
            l.End();
        }
    }

    // the pairs the player made
    public class MM_Pairs : GameComponent
    {
        public List<Pawn> a = new List<Pawn>();
        public List<Pawn> b = new List<Pawn>();

        public MM_Pairs(Game g) { }

        public static MM_Pairs Get => Current.Game?.GetComponent<MM_Pairs>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref a, "a", LookMode.Reference);
            Scribe_Collections.Look(ref b, "b", LookMode.Reference);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (a == null) a = new List<Pawn>();
                if (b == null) b = new List<Pawn>();
                for (int i = Mathf.Min(a.Count, b.Count) - 1; i >= 0; i--)
                    if (a[i] == null || b[i] == null) { a.RemoveAt(i); b.RemoveAt(i); }
                while (b.Count > a.Count) b.RemoveAt(b.Count - 1);
                while (a.Count > b.Count) a.RemoveAt(a.Count - 1);
            }
        }

        public int IndexOf(Pawn x, Pawn y)
        {
            for (int i = 0; i < a.Count; i++)
                if ((a[i] == x && b[i] == y) || (a[i] == y && b[i] == x)) return i;
            return -1;
        }

        public bool IsPair(Pawn x, Pawn y) => x != null && y != null && IndexOf(x, y) >= 0;

        public static bool Chosen(Pawn x, Pawn y) { MM_Pairs p = Get; return p != null && p.IsPair(x, y); }

        public void Add(Pawn x, Pawn y) { if (!IsPair(x, y)) { a.Add(x); b.Add(y); } }

        public void Remove(Pawn x, Pawn y) { int i = IndexOf(x, y); if (i >= 0) { a.RemoveAt(i); b.RemoveAt(i); } }

        public IEnumerable<Pawn> PartnersOf(Pawn x)
        {
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] == x) yield return b[i];
                else if (b[i] == x) yield return a[i];
            }
        }
    }

    public static class MM
    {
        public static bool Adult(Pawn p) => p != null && p.RaceProps.Humanlike && p.ageTracker != null && p.ageTracker.AgeBiologicalYears >= 18;

        public static bool InLove(Pawn x, Pawn y) => LovePartnerRelationUtility.LovePartnerRelationExists(x, y);

        public static void MakePair(Pawn x, Pawn y)
        {
            if (!Adult(x) || !Adult(y) || x == y) return;
            if (!InLove(x, y)) x.relations.AddDirectRelation(PawnRelationDefOf.Lover, y);
            MM_Pairs.Get?.Add(x, y);
            Messages.Message("MM_Paired".Translate(x.LabelShort, y.LabelShort), new LookTargets(x, y), MessageTypeDefOf.PositiveEvent, false);
        }

        public static void Split(Pawn x, Pawn y)
        {
            MM_Pairs.Get?.Remove(x, y);
            x.relations.TryRemoveDirectRelation(PawnRelationDefOf.Lover, y);
            x.relations.TryRemoveDirectRelation(PawnRelationDefOf.Fiance, y);
            if (x.relations.DirectRelationExists(PawnRelationDefOf.Spouse, y))
            {
                x.relations.RemoveDirectRelation(PawnRelationDefOf.Spouse, y);
                x.relations.AddDirectRelation(PawnRelationDefOf.ExSpouse, y);
            }
            else x.relations.AddDirectRelation(PawnRelationDefOf.ExLover, y);
            // no "broke up with me" mood hit - it was the player's call
            Messages.Message("MM_Split".Translate(x.LabelShort, y.LabelShort), new LookTargets(x, y), MessageTypeDefOf.NeutralEvent, false);
        }
    }

    // gizmo on a selected adult colonist
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
    public static class Patch_Gizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
        {
            foreach (Gizmo g in gizmos) yield return g;
            Pawn p = __instance;
            if (p == null || !p.IsColonist || !MM.Adult(p) || p.Map == null || Find.Selector.NumSelected != 1) yield break;
            yield return new Command_Action
            {
                defaultLabel = "MM_Gizmo".Translate(),
                defaultDesc = "MM_GizmoDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("Things/Mote/Heart", false) ?? BaseContent.BadTex,
                action = () => Menu(p)
            };
        }

        private static void Menu(Pawn p)
        {
            var opts = new List<FloatMenuOption>();
            foreach (Pawn o in PawnsFinder.AllMaps_FreeColonistsSpawned.Where(x => x != p && MM.Adult(x)).OrderBy(x => x.LabelShort))
            {
                Pawn other = o;
                bool chosen = MM_Pairs.Chosen(p, other);
                bool love = MM.InLove(p, other);
                int op = p.relations.OpinionOf(other);
                string extra = " (" + "MM_Opinion".Translate() + " " + op.ToStringWithSign() + ")";
                if (chosen)
                    opts.Add(new FloatMenuOption("MM_SplitOpt".Translate(other.LabelShort) + extra, () => MM.Split(p, other)));
                else if (love)
                    opts.Add(new FloatMenuOption("MM_KeepOpt".Translate(other.LabelShort) + extra, () => MM.MakePair(p, other)));
                else
                    opts.Add(new FloatMenuOption("MM_PairOpt".Translate(other.LabelShort) + extra, () => MM.MakePair(p, other)));
            }
            if (opts.Count == 0) opts.Add(new FloatMenuOption("MM_NoOne".Translate(), null));
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }

    // colonists don't start romances by themselves
    [HarmonyPatch(typeof(InteractionWorker_RomanceAttempt), nameof(InteractionWorker_RomanceAttempt.RandomSelectionWeight))]
    public static class Patch_Romance
    {
        public static void Postfix(Pawn initiator, Pawn recipient, ref float __result)
        {
            if (MMMod.S.onlyMine && initiator != null && (initiator.IsColonist || (recipient != null && recipient.IsColonist))) __result = 0f;
        }
    }

    // chosen pairs don't break up
    [HarmonyPatch(typeof(InteractionWorker_Breakup), nameof(InteractionWorker_Breakup.RandomSelectionWeight))]
    public static class Patch_Breakup
    {
        public static void Postfix(Pawn initiator, Pawn recipient, ref float __result)
        {
            if (MMMod.S.keepPairs && MM_Pairs.Chosen(initiator, recipient)) __result = 0f;
        }
    }

    // low opinion doesn't make chosen pairs spend less time together
    [HarmonyPatch(typeof(LovePartnerRelationUtility), nameof(LovePartnerRelationUtility.GetLovinMtbHours))]
    public static class Patch_Lovin
    {
        public static void Postfix(Pawn pawn, Pawn partner, ref float __result)
        {
            if (__result <= 0f || !MMMod.S.ignoreOpinion || !MM_Pairs.Chosen(pawn, partner)) return;
            float f = GenMath.LerpDouble(-100f, 100f, 1.3f, 0.7f, pawn.relations.OpinionOf(partner))
                    * GenMath.LerpDouble(-100f, 100f, 1.3f, 0.7f, partner.relations.OpinionOf(pawn));
            float best = 0.7f * 0.7f;
            if (f > best) __result *= best / f;
        }
    }
}
