using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace ResearchedLoadout
{
	// 2.17.0 "Dress and equip before starting work": when a colonist picks up a work job, he first puts on the
	// work garment (apron, lab coat, mining helmet …) and takes the work tool (pickaxe, sickle …) for it, and only
	// then starts the work – instead of noticing in the middle of the job. The work job waits in his queue.
	// Never for emergencies (fires, rescue, bleeding patients), never while drafted or in a raid, never for gear
	// that is far away, forbidden or reserved; a real weapon in his hands is never swapped for a tool.
	public static class GearBeforeWork
	{
		private const int Cooldown = 2500;   // ~1 in-game hour per colonist and work type

		// the dressing job -> the work job that waits for it
		private sealed class Waiting
		{
			public Job work;
			public JobTag? tag;
		}

		private static readonly ConditionalWeakTable<Job, Waiting> pending = new ConditionalWeakTable<Job, Waiting>();
		// pawn id + work type -> earliest tick of the next try
		private static readonly Dictionary<long, int> nextTry = new Dictionary<long, int>();

		private static ResearchedLoadoutSettings St => ResearchedLoadoutMod.Settings;

		public static bool On
		{
			get
			{
				ResearchedLoadoutSettings s = St;
				return s != null && s.enabled && s.workClothing && s.gearBeforeWork;
			}
		}

		public static void ResetGameState()
		{
			nextTry.Clear();
		}

		public static void Patch(Harmony h)
		{
			try
			{
				h.Patch(AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage"),
					postfix: new HarmonyMethod(typeof(GearBeforeWork), nameof(IssuePostfix)));
				h.Patch(AccessTools.Method(typeof(Pawn_JobTracker), "StartJob"),
					postfix: new HarmonyMethod(typeof(GearBeforeWork), nameof(StartJobPostfix)));
			}
			catch (Exception e)
			{
				Log.Warning("[Smart Colonist Gear] dress before work: patch failed – " + e.Message);
			}
		}

		// the work giver chose a job: dress / take the tool first, the work job waits
		public static void IssuePostfix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
		{
			try
			{
				if (!On || __instance == null || __instance.emergency) return;
				Job work = __result.Job;
				if (work == null || pawn == null || work.workGiverDef == null || work.playerForced) return;
				Job first = GearJob(pawn, work);
				if (first == null) return;
				pending.Remove(first);
				pending.Add(first, new Waiting { work = work, tag = __result.Tag });
				__result = new ThinkResult(first, __result.SourceNode, __result.Tag, false);
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] dress before work: " + e.Message, 7310171);
			}
		}

		// the dressing job really started: queue the work job right after it
		public static void StartJobPostfix(Pawn_JobTracker __instance, Job newJob, Pawn ___pawn)
		{
			try
			{
				if (newJob == null) return;
				ToolBelt.OnStart(___pawn, newJob);   // 2.19.0
				Waiting w;
				if (!pending.TryGetValue(newJob, out w)) return;
				pending.Remove(newJob);
				if (___pawn == null || __instance.curJob != newJob || w.work == null) return;
				if (__instance.jobQueue.Contains(w.work)) return;
				__instance.jobQueue.EnqueueFirst(w.work, w.tag);
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] dress before work (queue): " + e.Message, 7310172);
			}
		}

		// the Wear or Equip job to do before this work, or null
		private static Job GearJob(Pawn p, Job work)
		{
			WorkTypeDef wt = work.workGiverDef.workType;
			if (wt == null || wt.relevantSkills == null || wt.relevantSkills.Count == 0) return null;
			if (Emergency(p, work, wt)) return null;
			if (!Eligible(p)) return null;
			Map map = p.Map;
			if (map == null || !map.IsPlayerHome || SteadyThreat.Active(map)) return null;
			int now = Find.TickManager?.TicksGame ?? 0;
			long key = ((long)p.thingIDNumber << 16) ^ (wt.index & 0xFFFF);
			int next;
			if (nextTry.TryGetValue(key, out next) && now < next && next - now <= Cooldown) return null;
			nextTry[key] = now + Cooldown;   // one try per hour, whether something was found or not

			float maxDist = Math.Max(10, St.gearBeforeWorkDistance);
			foreach (SkillDef sk in wt.relevantSkills)
			{
				if (sk == null) continue;
				// 1) the work garment
				Job wear = Garment(p, map, sk, maxDist, now);
				if (wear != null) return wear;
				// 2) the work tool
				Job tool = Tool(p, map, sk, maxDist);
				if (tool != null) return tool;
			}
			return null;
		}

		private static bool Emergency(Pawn p, Job work, WorkTypeDef wt)
		{
			if (work.workGiverDef.emergency) return true;
			if (wt == WorkTypeDefOf.Firefighter) return true;
			string n = wt.defName;
			if (n == "Patient" || n == "PatientBedRest" || n == "BasicWorker" || n == "Hauling" || n == "Cleaning") return true;
			JobDef j = work.def;
			// 2.17.1: pharmacy and kit jobs (Auto Pharmacy, Steward – Kit) are short and often urgent –
			// a doctor with a drug in hand must not first go for his work clothes
			if (j.defName.StartsWith("AP_") || j == JobDefOf.TakeInventory || j == JobDefOf.TendPatient && work.targetA.Thing is Pawn tp && tp.Downed)
				return true;
			if (j == JobDefOf.Rescue || j == JobDefOf.BeatFire || j == JobDefOf.ExtinguishSelf || j == JobDefOf.Wear || j == JobDefOf.Equip || j == JobDefOf.RemoveApparel)
				return true;
			if (j == JobDefOf.TendPatient)
			{
				Pawn patient = work.targetA.Thing as Pawn;
				if (patient == null || patient.health?.hediffSet == null) return true;
				if (patient.health.hediffSet.BleedRateTotal > 0.01f || patient.health.HasHediffsNeedingTendByPlayer(true) && patient.Downed && patient.health.hediffSet.BleedRateTotal > 0f) return true;
			}
			return false;
		}

		private static bool Eligible(Pawn p)
		{
			if (!p.Spawned || p.Drafted || p.Downed || p.Dead || p.InMentalState || p.apparel == null || p.outfits == null) return false;
			if (p.IsPrisoner || p.IsSlave || p.HostFaction != null || !LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) return false;
			if (p.mindState != null && p.mindState.anyCloseHostilesRecently) return false;
			SCGBattleComponent c = SCGBattleComponent.Get();
			if (c != null && c.restoreTo.ContainsKey(p.thingIDNumber)) return false;
			if (Dressing.Held(p)) return false;
			return true;
		}

		private static Job Garment(Pawn p, Map map, SkillDef sk, float maxDist, int now)
		{
			if (!Specialization.IsSpecialist(p, sk)) return null;
			if (p.apparel.WornApparel.Any(w => Specialization.WorkSkillOf(w.def) == sk)) return null;
			// "only while working" decides on the work he is about to do
			WorkClothes.NoteWork(p, sk, now);
			Apparel best = WorkClothes.FindGarment(p, map, sk, maxDist);
			if (best == null) return null;
			var gone = p.apparel.WornApparel.Where(w => !ApparelUtility.CanWearTogether(best.def, w.def, p.RaceProps.body)).ToList();
			if (gone.Any(w => WorkClothes.Untouchable(p, w))) return null;
			Job job = JobMaker.MakeJob(JobDefOf.Wear, best);
			GearWhy.TagJob(job, GearWhy.Reason("RL_WhyWorkOn", sk.label));
			WorkClothes.NoteReplaced(p, best, gone);
			return job;
		}

		private static Job Tool(Pawn p, Map map, SkillDef sk, float maxDist)
		{
			if (p.equipment == null) return null;
			ThingWithComps held = p.equipment.Primary;
			// 2.19.0: tools in the pockets – fetched once, swapped into the hands when the work starts
			if (ToolBelt.On)
			{
				if (ToolBelt.PocketTool(p, sk) != null) return null;
				Job fetch = ToolBelt.FetchJob(p, map, sk, maxDist);
				if (fetch != null) return fetch;
			}
			if (SidearmsCompat.Present && held != null)
			{
				// 2.19.3: with Simple Sidearms the tool goes into the pockets as its sidearm – Simple Sidearms
				// switches to it for the work itself. Before, colonists with a weapon never got any tool.
				if (!ToolBelt.CarryTools || ToolBelt.PocketTool(p, sk) != null || ToolBelt.PocketToolCount(p) >= 2) return null;
				if (LoadoutFilter.ToolNotWeapon(held.def) && Specialization.SkillGain(held.def, sk) > 0.02f) return null;   // the right tool is in hand already
				JobDef second = SidearmsCompat.EquipSecondaryJob;
				if (second == null) return null;
				Thing pick = null;
				float pickScore = 0.03f;
				foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
				{
					if (t == null || !t.Spawned || t.def == null || !LoadoutFilter.ToolNotWeapon(t.def)) continue;
					float gain = Specialization.SkillGain(t.def, sk);
					if (gain <= 0.02f) continue;
					float dist = (t.Position - p.Position).LengthHorizontal;
					if (dist > maxDist) continue;
					float score = gain - dist * 0.0005f;
					if (score <= pickScore) continue;
					if (t.IsForbidden(p) || t.IsBurning()) continue;
					if (t.def.useHitPoints && (float)t.HitPoints / t.MaxHitPoints < 0.4f) continue;
					if (!EquipmentUtility.CanEquip(t, p) || !SidearmsCompat.CanPickup(p, t)) continue;
					if (!p.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some)) continue;
					pick = t;
					pickScore = score;
				}
				if (pick == null) return null;
				Job take = JobMaker.MakeJob(second, pick);
				take.count = 1;
				GearWhy.TagJob(take, GearWhy.Reason("RL_WhyToolPocket", sk.label));
				return take;
			}
			float have = 0f;
			if (held != null)
			{
				// a real weapon in his hands stays there (Simple Sidearms switches tools itself)
				if (!LoadoutFilter.ToolNotWeapon(held.def)) return null;
				have = Specialization.SkillGain(held.def, sk);
			}
			Thing best = null;
			float bestScore = have + 0.01f;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
			{
				if (t == null || !t.Spawned || t.def == null || !LoadoutFilter.ToolNotWeapon(t.def)) continue;
				float gain = Specialization.SkillGain(t.def, sk);
				if (gain <= 0.02f) continue;
				float dist = (t.Position - p.Position).LengthHorizontal;
				if (dist > maxDist) continue;
				float score = gain - dist * 0.0005f;
				if (score <= bestScore) continue;
				if (t.IsForbidden(p) || t.IsBurning()) continue;
				if (t.def.useHitPoints && (float)t.HitPoints / t.MaxHitPoints < 0.4f) continue;
				if (!EquipmentUtility.CanEquip(t, p)) continue;
				if (!p.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some)) continue;
				best = t;
				bestScore = score;
			}
			if (best == null) return null;
			Job job = JobMaker.MakeJob(JobDefOf.Equip, best);
			GearWhy.TagJob(job, GearWhy.Reason("RL_WhyToolOn", sk.label));
			return job;
		}
	}
}
