using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ResearchedLoadout
{
	// 2.16.1 "Work clothes only while working": an apron, lab coat, mining helmet … (apparel that speeds up one
	// skill) is put on when the specialist starts that work and taken off again about two in-game hours after
	// he last did it; the clothes it replaced are put back on. Never while drafted or in a raid, never strips
	// someone bare in the cold, never touches forced, locked or ideology apparel.
	public static class WorkClothes
	{
		private const int KeepTicks = 5000;       // ~2 in-game hours after the last work of that skill
		private const int ActionCooldown = 2500;  // ~1 hour between two changes of the same colonist
		private const float MaxDistance = 70f;

		// pawn id -> skill defName -> last tick he did that work
		private static readonly Dictionary<int, Dictionary<string, int>> lastWork = new Dictionary<int, Dictionary<string, int>>();
		// pawn id -> earliest tick of the next change
		private static readonly Dictionary<int, int> nextAct = new Dictionary<int, int>();
		// pawn id -> work garment id -> the apparel it replaced (to put back)
		private static readonly Dictionary<int, Dictionary<int, List<Apparel>>> replaced = new Dictionary<int, Dictionary<int, List<Apparel>>>();

		private static ResearchedLoadoutSettings St => ResearchedLoadoutMod.Settings;

		public static bool On
		{
			get
			{
				ResearchedLoadoutSettings s = St;
				return s != null && s.enabled && s.workClothing && s.workClothesOnlyWorking;
			}
		}

		public static void ResetGameState()
		{
			lastWork.Clear();
			nextAct.Clear();
			replaced.Clear();
			GearBeforeWork.ResetGameState();
			ToolBelt.ResetGameState();
		}

		private static int Now => Find.TickManager?.TicksGame ?? 0;

		// the skills of the work he is doing right now
		private static List<SkillDef> CurrentSkills(Pawn p)
		{
			WorkTypeDef wt = p?.CurJob?.workGiverDef?.workType;
			return wt?.relevantSkills;
		}

		// is the pawn doing (or did he just do) work for this skill
		public static bool Active(Pawn p, SkillDef sk)
		{
			if (p == null || sk == null) return false;
			try
			{
				List<SkillDef> cur = CurrentSkills(p);
				if (cur != null && cur.Contains(sk)) return true;
				Dictionary<string, int> d;
				int t;
				return lastWork.TryGetValue(p.thingIDNumber, out d) && d.TryGetValue(sk.defName, out t) && Now - t >= 0 && Now - t < KeepTicks;
			}
			catch { return true; }
		}

		public static void Tick(int now)
		{
			if (!On || Find.Maps == null) return;
			try
			{
				int slot = (now / 60) % 4;
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome) continue;
					bool threat = SteadyThreat.Active(map);
					foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
					{
						if (p == null) continue;
						Note(p, now);
						if ((p.thingIDNumber & 3) != slot) continue;   // each colonist every 4 s
						if (threat) continue;
						TryPawn(p, map, now);
					}
				}
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] work clothes: " + e.Message, 7310121);
			}
		}

		// 2.17.0: he is about to do this work (dressing before the work job starts)
		internal static void NoteWork(Pawn p, SkillDef sk, int now)
		{
			if (p == null || sk == null) return;
			Dictionary<string, int> d;
			if (!lastWork.TryGetValue(p.thingIDNumber, out d)) lastWork[p.thingIDNumber] = d = new Dictionary<string, int>();
			d[sk.defName] = now;
		}

		// 2.17.0: what the work garment replaces, to be put back after the work
		internal static void NoteReplaced(Pawn p, Apparel garment, List<Apparel> gone)
		{
			if (p == null || garment == null) return;
			Dictionary<int, List<Apparel>> r;
			if (!replaced.TryGetValue(p.thingIDNumber, out r)) replaced[p.thingIDNumber] = r = new Dictionary<int, List<Apparel>>();
			r[garment.thingIDNumber] = gone ?? new List<Apparel>();
		}

		// remember which work he is doing now
		private static void Note(Pawn p, int now)
		{
			List<SkillDef> cur = CurrentSkills(p);
			if (cur == null || cur.Count == 0) return;
			Dictionary<string, int> d;
			if (!lastWork.TryGetValue(p.thingIDNumber, out d)) lastWork[p.thingIDNumber] = d = new Dictionary<string, int>();
			for (int i = 0; i < cur.Count; i++) if (cur[i] != null) d[cur[i].defName] = now;
		}

		private static bool Eligible(Pawn p)
		{
			if (p.Drafted || p.Downed || p.Dead || p.InMentalState || p.apparel == null || p.outfits == null) return false;
			if (p.IsPrisoner || p.IsSlave || p.HostFaction != null || !LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) return false;
			SCGBattleComponent c = SCGBattleComponent.Get();
			if (c != null && c.restoreTo.ContainsKey(p.thingIDNumber)) return false;   // battle gear wins
			if (Dressing.Held(p)) return false;   // 2.18.0: the dressing provider dresses him right now
			return true;
		}

		internal static bool Untouchable(Pawn p, Apparel a)
		{
			if (a == null) return true;
			if (p.apparel.IsLocked(a)) return true;
			try { if (p.outfits?.forcedHandler != null && p.outfits.forcedHandler.IsForced(a)) return true; } catch { }
			try { if (GearSense.Required(p, a.def)) return true; } catch { }
			return false;
		}

		private static void TryPawn(Pawn p, Map map, int now)
		{
			if (!Eligible(p)) return;
			int next;
			if (nextAct.TryGetValue(p.thingIDNumber, out next) && now < next && next - now <= ActionCooldown) return;
			Job cur = p.CurJob;
			if (cur != null && (cur.playerForced && cur.workGiverDef == null || cur.def == JobDefOf.Wear || cur.def == JobDefOf.RemoveApparel || cur.def == JobDefOf.LayDown || cur.def == JobDefOf.Ingest)) return;
			if (p.InBed()) return;
			if (PutOn(p, map, now, cur)) return;
			TakeOff(p, map, now, cur);
		}

		// ---------- the work starts: put the work garment on ----------
		private static bool PutOn(Pawn p, Map map, int now, Job cur)
		{
			List<SkillDef> skills = CurrentSkills(p);
			if (skills == null || skills.Count == 0) return false;
			foreach (SkillDef sk in skills)
			{
				if (sk == null || !Specialization.IsSpecialist(p, sk)) continue;
				if (p.apparel.WornApparel.Any(w => Specialization.WorkSkillOf(w.def) == sk)) continue;
				Apparel best = FindGarment(p, map, sk, MaxDistance);
				if (best == null) continue;
				var gone = p.apparel.WornApparel.Where(w => !ApparelUtility.CanWearTogether(best.def, w.def, p.RaceProps.body)).ToList();
				if (gone.Any(w => Untouchable(p, w))) continue;
				Job job = JobMaker.MakeJob(JobDefOf.Wear, best);
				GearWhy.TagJob(job, GearWhy.Reason("RL_WhyWorkOn", sk.label));
				p.jobs.StartJob(job, JobCondition.InterruptForced, null, cur != null, true);
				Dictionary<int, List<Apparel>> r;
				if (!replaced.TryGetValue(p.thingIDNumber, out r)) replaced[p.thingIDNumber] = r = new Dictionary<int, List<Apparel>>();
				r[best.thingIDNumber] = gone;
				nextAct[p.thingIDNumber] = now + ActionCooldown;
				return true;
			}
			return false;
		}

		// 2.18.0: through the dressing provider; SCG's own choice is FindGarmentDefault
		internal static Apparel FindGarment(Pawn p, Map map, SkillDef sk, float maxDistance)
		{
			return Dressing.Garment(p, map, sk, maxDistance, () => FindGarmentDefault(p, map, sk, maxDistance));
		}

		public static Apparel FindGarmentDefault(Pawn p, Map map, SkillDef sk, float maxDistance)
		{
			ApparelPolicy policy = p.outfits?.CurrentApparelPolicy;
			Apparel best = null;
			float bestScore = float.MinValue;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
			{
				Apparel a = t as Apparel;
				if (a == null || !a.Spawned || a.IsForbidden(p) || a.IsBurning() || a.def.apparel == null) continue;
				if (Specialization.WorkSkillOf(a.def) != sk) continue;
				if (a.def.useHitPoints && (float)a.HitPoints / a.MaxHitPoints < 0.5f) continue;
				if (LoadoutFilter.IsManuallyBanned(a.def) || !LoadoutFilter.IsAllowed(a.def)) continue;
				if (policy?.filter != null && !policy.filter.Allows(a)) continue;
				if (!ApparelUtility.HasPartsToWear(p, a.def) || !a.def.apparel.PawnCanWear(p, false) || !LoadoutFilter.StageOk(p, a.def) || ApparelFlip.Blocked(p, a.def)) continue;
				if (ApparelGender.Mismatch(p, a.def)) continue;
				if (a.WornByCorpse && ThoughtUtility.CanGetThought(p, ThoughtDefOf.DeadMansApparel, true)) continue;
				float dist = (a.Position - p.Position).LengthHorizontal;
				if (dist > maxDistance) continue;
				float score = Specialization.WorkClothingBonus(p, a.def) * 10f + a.GetStatValue(StatDefOf.Insulation_Cold) * 0.05f - dist * 0.02f;
				if (score <= bestScore) continue;
				if (!p.CanReserveAndReach(a, PathEndMode.ClosestTouch, Danger.Some)) continue;
				best = a;
				bestScore = score;
			}
			return best;
		}

		// ---------- the work is over: take it off, put the old clothes back on ----------
		private static void TakeOff(Pawn p, Map map, int now, Job cur)
		{
			foreach (Apparel a in p.apparel.WornApparel.ToList())
			{
				SkillDef sk = Specialization.WorkSkillOf(a.def);
				if (sk == null || Active(p, sk) || Untouchable(p, a)) continue;
				if (!SafeToRemove(p, map, a) || !Dressing.MayRemove(p, a)) continue;
				List<Apparel> back = null;
				Dictionary<int, List<Apparel>> r;
				if (replaced.TryGetValue(p.thingIDNumber, out r) && r.TryGetValue(a.thingIDNumber, out back)) r.Remove(a.thingIDNumber);
				back = (back ?? new List<Apparel>()).Where(x => x != null && !x.Destroyed && x.Spawned && !x.IsForbidden(p) && x.Map == map
					&& ApparelUtility.HasPartsToWear(p, x.def) && p.CanReserveAndReach(x, PathEndMode.ClosestTouch, Danger.Some)).ToList();
				// bare torso or legs afterwards and nothing to put back: keep it on
				if (back.Count == 0 && CoversBasic(a.def)) continue;
				Job job = JobMaker.MakeJob(JobDefOf.RemoveApparel, a);
				job.haulDroppedApparel = true;
				GearWhy.TagJob(job, GearWhy.Reason("RL_WhyWorkOff", sk.label));
				ApparelMemory.NoteRemoved(p, a.def);
				p.jobs.StartJob(job, JobCondition.InterruptForced, null, cur != null && cur.def.suspendable, true);
				foreach (Apparel x in back)
				{
					Job w = JobMaker.MakeJob(JobDefOf.Wear, x);
					GearWhy.TagJob(w, GearWhy.Reason("RL_WhyWorkBack"));
					p.jobs.jobQueue.EnqueueFirst(w);
				}
				nextAct[p.thingIDNumber] = now + ActionCooldown;
				return;
			}
		}

		private static bool CoversBasic(ThingDef def)
		{
			var g = def.apparel?.bodyPartGroups;
			return g != null && (g.Contains(BodyPartGroupDefOf.Torso) || g.Contains(BodyPartGroupDefOf.Legs));
		}

		private static bool SafeToRemove(Pawn p, Map map, Apparel a)
		{
			try
			{
				if (LoadoutFilter.FreezingOrOverheating(p)) return false;
				// gear against toxic air stays on while it is needed
				if (a.def.equippedStatOffsets != null && a.def.equippedStatOffsets.Any(m => m.stat == StatDefOf.ToxicResistance || m.stat == StatDefOf.ToxicEnvironmentResistance) && GearSense.Required(p, a.def)) return false;
				float outdoor = map.mapTemperature.OutdoorTemp;
				float cold = a.GetStatValue(StatDefOf.Insulation_Cold), heat = a.GetStatValue(StatDefOf.Insulation_Heat);
				float min = p.GetStatValue(StatDefOf.ComfyTemperatureMin), max = p.GetStatValue(StatDefOf.ComfyTemperatureMax);
				if (cold > 1f && outdoor < min + cold + 2f) return false;
				if (heat > 1f && outdoor > max - heat - 2f) return false;
			}
			catch { return false; }
			return true;
		}
	}
}
