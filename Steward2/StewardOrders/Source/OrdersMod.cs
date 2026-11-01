using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// ===========================================================================
// Steward – Orders (add-on of the Steward core)
// Right-click with a colonist selected gives orders the game normally refuses:
// - "Order: …" for any work on the clicked thing even when the colonist is not assigned to that
//   work type (he must be capable of it) – including research at a research bench;
// - "Sleep here" on any bed he may use, "Rest here" on a hospital bed – even when not tired;
// - "Relax here" on any joy building (TV, chess table, horseshoes …) – even outside free time.
// The game's own options stay; ours only appear where the game's option is missing or greyed out.
// ===========================================================================

public sealed class OrdersSettings : ModSettings
{
	public bool work = true, research = true, sleep = true, relax = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref work, "work", true);
		Scribe_Values.Look(ref research, "research", true);
		Scribe_Values.Look(ref sleep, "sleep", true);
		Scribe_Values.Look(ref relax, "relax", true);
	}
}

public sealed class OrdersMod : Mod
{
	public const string Version = "0.1.0";
	public static OrdersSettings S;

	public OrdersMod(ModContentPack content) : base(content)
	{
		S = GetSettings<OrdersSettings>();
		try { StewardApi.AddSettingsPage(new SettingsPage { label = "SO_Name".Translate(), draw = Draw, write = () => S.Write() }); } catch { }
		MLog.Info("[Steward] Orders v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "SO_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect) => Draw(inRect);

	private static void Draw(Rect r)
	{
		var l = new Listing_Standard();
		l.Begin(r);
		l.Label("SO_Intro".Translate());
		l.Gap(6f);
		l.CheckboxLabeled("SO_SetWork".Translate(), ref S.work, "SO_SetWorkTip".Translate());
		l.CheckboxLabeled("SO_SetResearch".Translate(), ref S.research, "SO_SetResearchTip".Translate());
		l.CheckboxLabeled("SO_SetSleep".Translate(), ref S.sleep, "SO_SetSleepTip".Translate());
		l.CheckboxLabeled("SO_SetRelax".Translate(), ref S.relax, "SO_SetRelaxTip".Translate());
		l.End();
	}
}

public class FloatMenuOptionProvider_StewardOrders : FloatMenuOptionProvider
{
	protected override bool Drafted => false;
	protected override bool Undrafted => true;
	protected override bool Multiselect => false;
	protected override bool MechanoidCanDo => false;
	protected override bool CanSelfTarget => false;

	private static readonly MethodInfo playJob = AccessTools.Method(typeof(JoyGiver_InteractBuilding), "TryGivePlayJob");

	public override bool SelectedPawnValid(Pawn pawn, FloatMenuContext context)
	{
		return base.SelectedPawnValid(pawn, context) && pawn.IsColonistPlayerControlled && pawn.RaceProps.Humanlike;
	}

	public override IEnumerable<FloatMenuOption> GetOptionsFor(Thing clickedThing, FloatMenuContext context)
	{
		if (OrdersMod.S == null) yield break;
		Pawn pawn = context.FirstSelectedPawn;
		if (pawn == null || clickedThing == null || pawn.Downed) yield break;
		var list = new List<FloatMenuOption>();
		try
		{
			if (OrdersMod.S.work || OrdersMod.S.research) WorkOrders(pawn, clickedThing, context, list);
			if (OrdersMod.S.sleep && clickedThing is Building_Bed bed) BedOrder(pawn, bed, list);
			if (OrdersMod.S.relax) RelaxOrder(pawn, clickedThing, list);
		}
		catch (Exception e) { Log.ErrorOnce("[Steward Orders] menu: " + e, 0x50D3E1); }
		foreach (FloatMenuOption o in list) yield return o;
	}

	// ---------- work the game does not let you prioritize ----------
	private static void WorkOrders(Pawn pawn, Thing t, FloatMenuContext context, List<FloatMenuOption> list)
	{
		if (pawn.thinker?.TryGetMainTreeThinkNode<JobGiver_Work>() == null) return;
		var used = new HashSet<string>();
		foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefsListForReading)
		{
			bool notAssigned = pawn.workSettings != null && pawn.workSettings.GetPriority(wt) == 0;
			if (pawn.WorkTypeIsDisabled(wt)) continue;           // truly incapable – no order can change that
			foreach (WorkGiverDef wg in wt.workGiversByPriority)
			{
				if (!(wg.Worker is WorkGiver_Scanner sc) || !wg.directOrderable) continue;
				bool research = t is Building_ResearchBench && wg.workType == WorkTypeDefOf.Research;
				// ours only where the game's own option is greyed out: not assigned, or research
				if (!notAssigned && !research) continue;
				if (research ? !OrdersMod.S.research : !OrdersMod.S.work) continue;
				if (pawn.WorkTagIsDisabled(wg.workTags)) continue;
				if (sc.MissingRequiredCapacity(pawn) != null) continue;
				if (!sc.PotentialWorkThingRequest.Accepts(t))
				{
					IEnumerable<Thing> glob = null;
					try { glob = sc.PotentialWorkThingsGlobal(pawn); } catch { }
					if (glob == null || !glob.Contains(t)) continue;
				}
				Job job = null;
				JobFailReason.Clear();
				try
				{
					if (!sc.ShouldSkip(pawn, forced: true) && sc.HasJobOnThing(pawn, t, forced: true))
						job = sc.JobOnThing(pawn, t, forced: true);
				}
				catch { job = null; }
				string what = wg.verb.NullOrEmpty() ? wg.label : wg.verb;
				if (job == null)
				{
					// worth showing only with a reason (e.g. "no research project chosen")
					if (!JobFailReason.HaveReason) continue;
					string lab0 = "SO_Order".Translate(what, t.LabelShort) + ": " + JobFailReason.Reason.CapitalizeFirst();
					if (used.Add(lab0)) list.Add(new FloatMenuOption(lab0, null));
					continue;
				}
				string gerund;
				try { gerund = sc.PostProcessedGerund(job); } catch { gerund = what; }
				string lab = "SO_Order".Translate(gerund, t.LabelShort);
				if (!used.Add(lab)) continue;
				if (!pawn.CanReach(t, sc.PathEndMode, Danger.Deadly))
				{
					list.Add(new FloatMenuOption(lab + ": " + "NoPath".Translate().CapitalizeFirst(), null));
					continue;
				}
				if (notAssigned) lab += " " + "SO_NotAssigned".Translate(wt.gerundLabel);
				Job local = job;
				WorkGiver_Scanner localSc = sc;
				job.workGiverDef = wg;
				list.Add(FloatMenuUtility.DecoratePrioritizedTask(new FloatMenuOption(lab, () =>
				{
					local.playerForced = true;
					pawn.jobs.TryTakeOrderedJobPrioritizedWork(local, localSc, t.Position);
				}, MenuOptionPriority.High), pawn, t));
			}
		}
	}

	// ---------- sleep / rest in this bed now ----------
	private static void BedOrder(Pawn pawn, Building_Bed bed, List<FloatMenuOption> list)
	{
		if (pawn.needs?.rest == null) return;
		if (!RestUtility.CanUseBedEver(pawn, bed.def)) return;
		bool medical = bed.Medical;
		string lab = (medical ? "SO_RestHere" : "SO_SleepHere").Translate(bed.LabelShort);
		if (bed.ForPrisoners && !pawn.IsPrisoner) { list.Add(new FloatMenuOption(lab + ": " + "SO_BedPrisoners".Translate(), null)); return; }
		if (!bed.AnyUnoccupiedSleepingSlot) { list.Add(new FloatMenuOption(lab + ": " + "SO_BedFull".Translate(), null)); return; }
		if (!pawn.CanReserveAndReach(bed, PathEndMode.OnCell, Danger.Deadly, bed.SleepingSlotsCount, 0))
		{
			list.Add(new FloatMenuOption(lab + ": " + "NoPath".Translate().CapitalizeFirst(), null));
			return;
		}
		Pawn owner = bed.OwnersForReading?.FirstOrDefault();
		if (owner != null && owner != pawn && !medical) lab += " " + "SO_BedOf".Translate(owner.LabelShort);
		list.Add(new FloatMenuOption(lab, () =>
		{
			Job j = JobMaker.MakeJob(JobDefOf.LayDown, bed);
			j.forceSleep = !medical;
			pawn.jobs.TryTakeOrderedJob(j, JobTag.SatisfyingNeeds);
		}, MenuOptionPriority.High));
	}

	// ---------- relax on this joy building now ----------
	private static void RelaxOrder(Pawn pawn, Thing t, List<FloatMenuOption> list)
	{
		if (pawn.needs?.joy == null || playJob == null) return;
		foreach (JoyGiverDef jg in DefDatabase<JoyGiverDef>.AllDefsListForReading)
		{
			if (jg.thingDefs == null || !jg.thingDefs.Contains(t.def)) continue;
			if (!(jg.Worker is JoyGiver_InteractBuilding ib)) continue;
			string lab = "SO_RelaxHere".Translate(t.LabelShort);
			Job job = null;
			try { job = (Job)playJob.Invoke(ib, new object[] { pawn, t }); } catch { job = null; }
			if (job == null)
			{
				list.Add(new FloatMenuOption(lab + ": " + "SO_RelaxNo".Translate(), null));
				return;
			}
			if (!pawn.CanReach(t, PathEndMode.Touch, Danger.Deadly))
			{
				list.Add(new FloatMenuOption(lab + ": " + "NoPath".Translate().CapitalizeFirst(), null));
				return;
			}
			job.ignoreJoyTimeAssignment = true;
			list.Add(new FloatMenuOption(lab, () => pawn.jobs.TryTakeOrderedJob(job, JobTag.SatisfyingNeeds), MenuOptionPriority.High));
			return;
		}
	}
}
