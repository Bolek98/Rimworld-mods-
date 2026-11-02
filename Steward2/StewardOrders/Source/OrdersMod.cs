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
	public bool oneClick = true;   // 0.2.0: right-click on a bed / bench / joy building does it at once

	public override void ExposeData()
	{
		Scribe_Values.Look(ref work, "work", true);
		Scribe_Values.Look(ref research, "research", true);
		Scribe_Values.Look(ref sleep, "sleep", true);
		Scribe_Values.Look(ref relax, "relax", true);
		Scribe_Values.Look(ref oneClick, "oneClick", true);
	}
}

public sealed class OrdersMod : Mod
{
	public const string Version = "0.2.0";
	public static OrdersSettings S;

	public OrdersMod(ModContentPack content) : base(content)
	{
		S = GetSettings<OrdersSettings>();
		try { OneClick.Patch(new Harmony("misak.steward.orders")); } catch (Exception e) { Log.Warning("[Steward Orders] one click: " + e.Message); }
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
		l.CheckboxLabeled("SO_SetOneClick".Translate(), ref S.oneClick, "SO_SetOneClickTip".Translate());
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
			if (OrdersMod.S.oneClick && OneClick.primary == null) OneClick.primary = Primary(pawn, clickedThing);
			if (OrdersMod.S.work || OrdersMod.S.research) WorkOrders(pawn, clickedThing, context, list);
			if (OrdersMod.S.sleep && clickedThing is Building_Bed bed) BedOrder(pawn, bed, list);
			if (OrdersMod.S.relax) RelaxOrder(pawn, clickedThing, list);
		}
		catch (Exception e) { Log.ErrorOnce("[Steward Orders] menu: " + e, 0x50D3E1); }
		foreach (FloatMenuOption o in list) yield return o;
	}

	// ---------- 0.2.0: the one thing a right-click on this does at once ----------
	private static FloatMenuOption Primary(Pawn pawn, Thing t)
	{
		if (t is Building_Bed bed)
		{
			if (!OrdersMod.S.sleep) return null;
			var tmp = new List<FloatMenuOption>();
			BedOrder(pawn, bed, tmp);
			return tmp.FirstOrDefault(o => !o.Disabled);
		}
		// 0.3.0: anything – joy buildings, then weapons/clothes to put on, then any work on the thing
		// (bills, research, build, repair, harvest, mine, haul, clean, tend, feed, tame …)
		if (OrdersMod.S.relax && t is Building && t.def.building != null && t.def.building.joyKind != null)
		{
			var tmp = new List<FloatMenuOption>();
			RelaxOrder(pawn, t, tmp);
			FloatMenuOption r = tmp.FirstOrDefault(o => !o.Disabled);
			if (r != null) return r;
		}
		if (t.def.category == ThingCategory.Item && t.Spawned && !t.IsForbidden(pawn))
		{
			FloatMenuOption gear = GearOrder(pawn, t);
			if (gear != null) return gear;
		}
		FloatMenuOption w = WorkHere(pawn, t);
		if (w != null) return w;
		if (OrdersMod.S.relax && t is Building)
		{
			var tmp = new List<FloatMenuOption>();
			RelaxOrder(pawn, t, tmp);
			return tmp.FirstOrDefault(o => !o.Disabled);
		}
		return null;
	}

	// a clicked colonist, prisoner or animal: tend, feed, chat, tame, train … whatever work there is
	public override IEnumerable<FloatMenuOption> GetOptionsFor(Pawn clickedPawn, FloatMenuContext context)
	{
		try
		{
			Pawn pawn = context.FirstSelectedPawn;
			if (OrdersMod.S != null && OrdersMod.S.oneClick && OneClick.primary == null && pawn != null && clickedPawn != null && clickedPawn != pawn
				&& !clickedPawn.HostileTo(Faction.OfPlayer))
				OneClick.primary = WorkHere(pawn, clickedPawn);
		}
		catch (Exception e) { Log.ErrorOnce("[Steward Orders] pawn: " + e.Message, 0x50D3E2); }
		yield break;
	}

	// weapon → equip, clothes → wear (only when he can and it is not the same he has)
	private static FloatMenuOption GearOrder(Pawn pawn, Thing t)
	{
		if (!pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Deadly)) return null;
		if (t.def.IsWeapon && pawn.equipment != null && !pawn.WorkTagIsDisabled(WorkTags.Violent) && pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)
			&& EquipmentUtility.CanEquip(t, pawn))
		{
			return new FloatMenuOption("SO_EquipNow".Translate(t.LabelShort), () =>
			{
				Job j = JobMaker.MakeJob(JobDefOf.Equip, t);
				pawn.jobs.TryTakeOrderedJob(j, JobTag.Misc);
			});
		}
		if (t is Apparel ap && pawn.apparel != null && ApparelUtility.HasPartsToWear(pawn, ap.def) && ap.PawnCanWear(pawn, ignoreGender: false))
		{
			return new FloatMenuOption("SO_WearNow".Translate(t.LabelShort), () =>
			{
				Job j = JobMaker.MakeJob(JobDefOf.Wear, t);
				pawn.jobs.TryTakeOrderedJob(j, JobTag.Misc);
			});
		}
		return null;
	}

	// the first work the colonist can do right here (bills at a bench, research at a research bench) –
	// assigned or not, as long as he is capable of it
	private static FloatMenuOption WorkHere(Pawn pawn, Thing t)
	{
		if (pawn.thinker?.TryGetMainTreeThinkNode<JobGiver_Work>() == null) return null;
		foreach (WorkTypeDef wt in DefDatabase<WorkTypeDef>.AllDefsListForReading.OrderBy(w => pawn.workSettings != null && pawn.workSettings.GetPriority(w) == 0 ? 1 : 0))
		{
			if (pawn.WorkTypeIsDisabled(wt)) continue;
			bool notAssigned = pawn.workSettings != null && pawn.workSettings.GetPriority(wt) == 0;
			if (notAssigned && !(t is Building_ResearchBench ? OrdersMod.S.research : OrdersMod.S.work)) continue;
			if (t is Pawn && wt == WorkTypeDefOf.Hauling) continue;   // no carrying people around by a click
			foreach (WorkGiverDef wg in wt.workGiversByPriority)
			{
				if (!(wg.Worker is WorkGiver_Scanner sc) || !wg.directOrderable) continue;
				if (pawn.WorkTagIsDisabled(wg.workTags) || sc.MissingRequiredCapacity(pawn) != null) continue;
				if (!sc.PotentialWorkThingRequest.Accepts(t))
				{
					IEnumerable<Thing> glob = null;
					try { glob = sc.PotentialWorkThingsGlobal(pawn); } catch { }
					if (glob == null || !glob.Contains(t)) continue;
				}
				Job job = null;
				try
				{
					if (!sc.ShouldSkip(pawn, forced: true) && sc.HasJobOnThing(pawn, t, forced: true))
						job = sc.JobOnThing(pawn, t, forced: true);
				}
				catch { job = null; }
				if (job == null || !pawn.CanReach(t, sc.PathEndMode, Danger.Deadly)) continue;
				job.workGiverDef = wg;
				Job local = job;
				WorkGiver_Scanner localSc = sc;
				string ger;
				try { ger = sc.PostProcessedGerund(job); } catch { ger = wg.label; }
				return new FloatMenuOption("SO_Order".Translate(ger, t.LabelShort), () =>
				{
					local.playerForced = true;
					pawn.jobs.TryTakeOrderedJobPrioritizedWork(local, localSc, t.Position);
				});
			}
		}
		return null;
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

// 0.2.0: a right-click on a bed, a work bench, a research bench or a joy building does the obvious thing
// at once (sleep, work, research, relax) instead of opening the menu. Ctrl + right-click opens the menu.
public static class OneClick
{
	public static FloatMenuOption primary;

	public static void Patch(Harmony h)
	{
		h.Patch(AccessTools.Method(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetOptions)),
			prefix: new HarmonyMethod(typeof(OneClick), nameof(Clear)));
		h.Patch(AccessTools.Method(typeof(FloatMenuMakerMap), nameof(FloatMenuMakerMap.GetAutoTakeOption)),
			postfix: new HarmonyMethod(typeof(OneClick), nameof(Take)));
	}

	public static void Clear() => primary = null;

	public static void Take(ref FloatMenuOption __result)
	{
		FloatMenuOption p = primary;
		primary = null;
		if (__result != null || p == null || p.Disabled || OrdersMod.S == null || !OrdersMod.S.oneClick) return;
		if (Event.current != null && (Event.current.control || Event.current.command)) return;
		__result = p;
	}
}
