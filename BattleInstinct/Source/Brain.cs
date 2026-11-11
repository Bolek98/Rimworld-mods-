using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BattleInstinct;

public static class Brain
{
	private const float SeeRange = 60f;

	public static bool Idle(Pawn p)
	{
		Job cur = p.CurJob;
		return cur == null || cur.def == JobDefOf.Wait_Combat || cur.def == JobDefOf.Wait;
	}

	public static void Think(Pawn p, PawnData d, Battlefield f)
	{
		var s = BattleInstinctMod.Settings;
		Job cur = p.CurJob;
		bool ours = cur != null && cur == d.ourJob;
		if (!ours && !Idle(p)) return;   // the player gave an order – leave it
		if (!ours) d.ourJob = null;
		int now = Find.TickManager.TicksGame;
		if (!d.anchor.IsValid) d.anchor = p.Position;

		// enemies this pawn can know about, nearest first
		List<Thing> near = new List<Thing>();
		foreach (Thing t in f.enemies)
			if (t.Position.InHorDistOf(p.Position, SeeRange))
				near.Add(t);
		if (near.Count == 0) return;
		near.SortBy(t => (t.Position - p.Position).LengthHorizontalSquared);

		// hurt? get out of sight first
		if (s.retreat && d.Stance != Stance.Hold && NeedsRetreat(p))
		{
			if (ours && cur.def == JobDefOf.Goto && now < d.retreatUntil) return;
			if (TryRetreat(p, d, near)) return;
			if (!SeenByAny(p.Position, p.Map, near)) return;   // already safe
		}
		if (ours && cur.def != JobDefOf.AttackStatic) return;   // our own move is still running

		// abilities first (may interrupt our own shooting)
		if (s.useAbilities && now - d.lastCast > 60)
		{
			Cast c = AbilityBrain.Find(p, d, near, f);
			if (c.ability != null)
			{
				AbilityBrain.Do(p, d, c);
				d.lastCast = now;
				return;
			}
		}

		if (ours) return;
		if (p.drafter != null && !p.drafter.FireAtWill) return;   // the player said hold fire
		if (p.WorkTagIsDisabled(WorkTags.Violent)) return;

		// best target
		List<Thing> ranked = near.Take(12).OrderByDescending(t => Score(p, t, f)).ToList();
		Thing best = ranked[0];
		Verb verb = p.TryGetAttackVerb(best, false);
		if (verb == null) return;
		IntVec3 locus = d.Stance == Stance.Aggressive ? p.Position : d.anchor;
		float leash = d.Stance == Stance.Aggressive ? s.aggressiveRadius : s.defensiveRadius;

		if (verb.verbProps.IsMeleeAttack)
		{
			if (d.Stance == Stance.Hold) return;   // the game hits what comes close
			Thing mt = ranked.FirstOrDefault(t => t.Position.InHorDistOf(locus, leash + 2f) && p.CanReach(t, PathEndMode.Touch, Danger.Deadly));
			if (mt == null) return;
			Job j = JobMaker.MakeJob(JobDefOf.AttackMelee, mt);
			j.expiryInterval = 400;
			j.checkOverrideOnExpire = true;
			j.expireRequiresEnemiesNearby = true;
			Issue(p, d, j, mt, mt != best ? null : "BI_TxtCharge");
			return;
		}

		// ranged: shoot from here if it is a good place
		bool coverHere = CoverUtility.CalculateOverallBlockChance(p, best.Position, p.Map) > 0.2f;
		Thing shootNow = null;
		float bestScore = Score(p, best, f);
		foreach (Thing t in ranked)
		{
			if (Score(p, t, f) < bestScore - 30f) break;
			if (!verb.CanHitTarget(t)) continue;
			if (s.avoidFriendlyFire && FriendlyRisk(p, p.Position, t, f)) continue;
			shootNow = t;
			break;
		}
		bool goodSpot = shootNow != null && (coverHere || !s.useCover || d.Stance == Stance.Hold || (shootNow.Position - p.Position).LengthHorizontalSquared < 36);
		if (goodSpot || d.Stance == Stance.Hold)
		{
			if (shootNow != null) Shoot(p, d, shootNow, shootNow == best && f.focus.ContainsKey(shootNow) ? "BI_TxtFocus" : null);
			return;
		}

		// find a better place: in range, with cover, inside the leash
		var req = new CastPositionRequest
		{
			caster = p,
			target = best,
			verb = verb,
			maxRangeFromTarget = verb.verbProps.range,
			wantCoverFromTarget = s.useCover,
			locus = locus,
			maxRangeFromLocus = leash,
			maxRegions = 60,
		};
		if (s.avoidFriendlyFire)
			req.validator = c => !FriendlyRisk(p, c, best, f);
		if (CastPositionFinder.TryFindCastPosition(req, out IntVec3 dest) && dest != p.Position)
		{
			Job g = JobMaker.MakeJob(JobDefOf.Goto, dest);
			g.expiryInterval = 300;
			g.checkOverrideOnExpire = true;
			g.locomotionUrgency = LocomotionUrgency.Sprint;
			Issue(p, d, g, best, "BI_TxtCover");
			return;
		}
		if (shootNow != null) Shoot(p, d, shootNow, null);
	}

	private static void Shoot(Pawn p, PawnData d, Thing t, string text)
	{
		if (p.CurJobDef == JobDefOf.AttackStatic && p.CurJob.targetA.Thing == t) return;
		Job j = JobMaker.MakeJob(JobDefOf.AttackStatic, t);
		j.expiryInterval = 300;
		j.checkOverrideOnExpire = true;
		j.endIfCantShootTargetFromCurPos = true;
		Issue(p, d, j, t, text);
	}

	public static void Issue(Pawn p, PawnData d, Job j, Thing target, string textKey)
	{
		BIComp.issuing = true;
		try
		{
			if (p.jobs.TryTakeOrderedJob(j, JobTag.Misc))
			{
				d.ourJob = j;
				d.target = target;
				if (textKey != null) Say(p, d, textKey.Translate());
			}
		}
		finally
		{
			BIComp.issuing = false;
		}
	}

	public static void Say(Pawn p, PawnData d, string text)
	{
		if (!BattleInstinctMod.Settings.showText || p.Map == null) return;
		int now = Find.TickManager.TicksGame;
		if (now - d.lastText < 120) return;
		d.lastText = now;
		MoteMaker.ThrowText(p.DrawPos + new Vector3(0f, 0f, 0.6f), p.Map, text, new Color(0.9f, 0.85f, 0.6f), 2.2f);
	}

	// how much this enemy should be shot first
	public static float Score(Pawn p, Thing t, Battlefield f)
	{
		float dist = (t.Position - p.Position).LengthHorizontal;
		float s = 60f - dist;
		if (dist < 5f) s += 15f;
		if (t is Pawn e)
		{
			Verb ev = e.CurrentEffectiveVerb;
			if (ev != null && ev.verbProps != null)
			{
				if (ev.verbProps.IsMeleeAttack)
				{
					if (dist < 8f) s += 30f;
				}
				else
				{
					ProjectileProperties proj = ev.verbProps.defaultProjectile?.projectile;
					if (proj != null && proj.explosionRadius > 0.9f) s += 40f;   // grenades, rockets
					if (ev.verbProps.range >= 32f) s += 15f;   // snipers
				}
			}
			if (e.mindState?.enemyTarget is Pawn tp && tp.Faction == Faction.OfPlayer) s += 10f;
			float hp = e.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
			s += (1f - hp) * 25f;
			if (e.RaceProps.IsMechanoid && e.RaceProps.baseBodySize > 2f) s += 10f;
			if (e.RaceProps.Animal && e.MentalStateDef == null) s -= 20f;
		}
		else if (t is Building_Turret)
			s += 12f;
		else
			s -= 15f;
		if (BattleInstinctMod.Settings.focusFire && f.focus.TryGetValue(t, out int n))
			s += 12f * Mathf.Min(3, n);
		return s;
	}

	// would a shot from 'from' to 't' risk hitting one of ours?
	public static bool FriendlyRisk(Pawn shooter, IntVec3 from, Thing t, Battlefield f)
	{
		IntVec3 to = t.Position;
		float lenSq = (to - from).LengthHorizontalSquared;
		if (lenSq < 4f) return false;
		for (int i = 0; i < f.friends.Count; i++)
		{
			Pawn o = f.friends[i];
			if (o == shooter || !o.Spawned || o.Map != f.map) continue;
			IntVec3 c = o.Position;
			// right next to the target – a miss can hit them
			if ((c - to).LengthHorizontalSquared <= 2 && !o.Downed) return true;
			// on the line of fire, not right next to the shooter
			if ((c - from).LengthHorizontalSquared <= 2) continue;
			if (DistToSegmentSq(c, from, to) <= 0.6f && (c - from).LengthHorizontalSquared < lenSq) return true;
		}
		return false;
	}

	private static float DistToSegmentSq(IntVec3 c, IntVec3 a, IntVec3 b)
	{
		float ax = a.x, az = a.z, bx = b.x, bz = b.z, px = c.x, pz = c.z;
		float dx = bx - ax, dz = bz - az;
		float l = dx * dx + dz * dz;
		float t = l <= 0f ? 0f : Mathf.Clamp01(((px - ax) * dx + (pz - az) * dz) / l);
		float qx = ax + t * dx - px, qz = az + t * dz - pz;
		return qx * qx + qz * qz;
	}

	public static bool NeedsRetreat(Pawn p)
	{
		var s = BattleInstinctMod.Settings;
		if (p.health?.summaryHealth == null) return false;
		if (p.health.summaryHealth.SummaryHealthPercent < s.retreatHp) return true;
		if (p.RaceProps.IsFlesh && p.health.hediffSet.BleedRateTotal > 0.05f
		    && HealthUtility.TicksUntilDeathDueToBloodLoss(p) < s.bleedHours * GenDate.TicksPerHour)
			return true;
		return false;
	}

	private static bool SeenByAny(IntVec3 c, Map map, List<Thing> enemies)
	{
		int n = 0;
		foreach (Thing e in enemies)
		{
			if (n++ > 8) break;
			if (!c.InHorDistOf(e.Position, 45f)) continue;
			if (GenSight.LineOfSight(e.Position, c, map, true)) return true;
		}
		return false;
	}

	private static bool TryRetreat(Pawn p, PawnData d, List<Thing> enemies)
	{
		Map map = p.Map;
		IntVec3 from = p.Position;
		float Rate(IntVec3 c)
		{
			float minD = 999f;
			int k = 0;
			foreach (Thing e in enemies)
			{
				if (k++ > 6) break;
				minD = Mathf.Min(minD, (e.Position - c).LengthHorizontal);
			}
			float r = Mathf.Min(minD, 40f);
			if (!SeenByAny(c, map, enemies)) r += 30f;
			r -= (c - from).LengthHorizontal * 0.4f;
			return r;
		}
		float here = Rate(from);
		IntVec3 bestC = IntVec3.Invalid;
		float bestR = here + 6f;
		int num = GenRadial.NumCellsInRadius(22f);
		for (int i = 9; i < num; i += 5)
		{
			IntVec3 c = from + GenRadial.RadialPattern[i];
			if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
			if (c.GetDangerFor(p, map) == Danger.Deadly) continue;
			if (!map.pawnDestinationReservationManager.CanReserve(c, p, true)) continue;
			float r = Rate(c);
			if (r <= bestR) continue;
			if (!p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
			bestR = r;
			bestC = c;
		}
		if (!bestC.IsValid) return false;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, bestC);
		j.locomotionUrgency = LocomotionUrgency.Sprint;
		j.expiryInterval = 600;
		j.checkOverrideOnExpire = true;
		Issue(p, d, j, null, "BI_TxtRetreat");
		d.retreatUntil = Find.TickManager.TicksGame + 600;
		return true;
	}
}
