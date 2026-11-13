using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace BattleInstinct;

// 0.2.0: everything the colony now does on its own in a fight
public static class Alarm
{
	// colonists the alarm drafted, per map
	private static readonly Dictionary<Map, HashSet<Pawn>> mine = new Dictionary<Map, HashSet<Pawn>>();
	private static readonly Dictionary<Map, int> lastThreat = new Dictionary<Map, int>();
	// colonists the player undrafted during the attack – left alone until it is over
	private static readonly HashSet<Pawn> released = new HashSet<Pawn>();
	internal static bool drafting;
	private const int Grace = 1250;   // ~20 s without enemies before everyone goes back to work

	public static void Reset()
	{
		mine.Clear();
		lastThreat.Clear();
		released.Clear();
	}

	public static bool CanFight(Pawn p)
	{
		if (p == null || p.Dead || p.Downed || p.InMentalState || p.drafter == null || !p.Spawned) return false;
		if (p.WorkTagIsDisabled(WorkTags.Violent)) return false;
		if (p.DevelopmentalStage.Juvenile()) return false;
		if (p.GetLord() != null) return false;   // caravan forming, ritual …
		if (p.health?.summaryHealth != null && p.health.summaryHealth.SummaryHealthPercent < 0.6f) return false;
		if (p.InBed() && HealthAIUtility.ShouldSeekMedicalRest(p)) return false;
		return true;
	}

	public static void Tick(Map map, BISettings s, int now)
	{
		if (!s.autoAlarm || !map.IsPlayerHome) return;
		if (!mine.TryGetValue(map, out var list)) mine[map] = list = new HashSet<Pawn>();
		bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
		if (threat)
		{
			lastThreat[map] = now;
			var field = new Battlefield(map);
			if (field.enemies.Count == 0) return;
			// one wild animal: two fighters are enough; anything bigger: everybody who can fight
			int want = field.enemies.Count == 1 && field.enemies[0] is Pawn ep && ep.RaceProps.Animal ? 2 : 999;
			int have = map.mapPawns.FreeColonistsSpawned.Count(p => p.Drafted);
			var cands = map.mapPawns.FreeColonistsSpawned
				.Where(p => !p.Drafted && !released.Contains(p) && CanFight(p))
				.OrderByDescending(Strength).ToList();
			foreach (Pawn p in cands)
			{
				if (have >= want) break;
				Draft(p, true);
				list.Add(p);
				have++;
			}
			return;
		}
		if (list.Count == 0 && released.Count == 0) return;
		if (lastThreat.TryGetValue(map, out int last) && now - last < Grace) return;
		foreach (Pawn p in list.ToList())
			if (p.Spawned && p.Map == map && p.Drafted && !p.Downed)
				Draft(p, false);
		list.Clear();
		released.RemoveWhere(p => p.MapHeld == map || p.Dead);
	}

	// better armed and more skilled colonists go first
	private static float Strength(Pawn p)
	{
		float v = 0f;
		ThingWithComps w = p.equipment?.Primary;
		if (w != null) v += w.def.IsRangedWeapon ? 10f : 6f;
		v += p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0;
		v += (p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0) * 0.5f;
		return v;
	}

	private static void Draft(Pawn p, bool on)
	{
		drafting = true;
		try { p.drafter.Drafted = on; }
		catch { }
		finally { drafting = false; }
	}

	// the player undrafted one of ours during the attack: respect it
	public static void PlayerUndrafted(Pawn p)
	{
		foreach (var kv in mine)
			if (kv.Value.Remove(p))
				released.Add(p);
	}
}

public static class Tactics
{
	// a melee enemy close to a shooter: step back a few cells and keep shooting (hit and run)
	public static bool TryKite(Pawn p, PawnData d, List<Thing> near, int now)
	{
		if (!BattleInstinctMod.Settings.kite || now - d.lastKite < 90) return false;
		Pawn threat = null;
		foreach (Thing t in near)
		{
			if ((t.Position - p.Position).LengthHorizontalSquared > 20) break;
			if (t is Pawn e && !e.Downed && e.CurrentEffectiveVerb?.verbProps?.IsMeleeAttack == true)
			{
				threat = e;
				break;
			}
		}
		if (threat == null) return false;
		if (p.pather != null && threat.GetStatValue(StatDefOf.MoveSpeed) > p.GetStatValue(StatDefOf.MoveSpeed) * 1.3f)
			return false;   // it is much faster – running only gets us hit in the back
		Map map = p.Map;
		Vector3 away = (p.Position - threat.Position).ToVector3();
		if (away.sqrMagnitude < 0.01f) return false;
		away.Normalize();
		IntVec3 bestC = IntVec3.Invalid;
		float bestR = -999f;
		for (int dist = 7; dist >= 4; dist--)
			for (int side = -2; side <= 2; side++)
			{
				Vector3 perp = new Vector3(-away.z, 0f, away.x) * side;
				IntVec3 c = (p.Position.ToVector3Shifted() + away * dist + perp).ToIntVec3();
				if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
				float minE = 999f;
				foreach (Thing t in near.Take(8))
					minE = Mathf.Min(minE, (t.Position - c).LengthHorizontal);
				if (minE < 4f) continue;
				float r = minE + CoverUtility.CalculateOverallBlockChance(c, threat.Position, map) * 10f;
				if (r <= bestR) continue;
				if (!p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
				bestR = r;
				bestC = c;
			}
		if (!bestC.IsValid) return false;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, bestC);
		j.locomotionUrgency = LocomotionUrgency.Sprint;
		j.expiryInterval = 150;
		j.checkOverrideOnExpire = true;
		Brain.Issue(p, d, j, threat, "BI_TxtKite");
		d.lastKite = now;
		return true;
	}

	// melee fighters: enemies that reached one of our shooters come first
	public static float ScreenBonus(Thing t, Battlefield f)
	{
		if (!BattleInstinctMod.Settings.screen) return 0f;
		foreach (Pawn o in f.friends)
		{
			if (o.Faction != Faction.OfPlayer || !o.Drafted || o.Downed) continue;
			Verb v = o.CurrentEffectiveVerb;
			if (v == null || v.verbProps.IsMeleeAttack) continue;
			if ((o.Position - t.Position).LengthHorizontalSquared <= 36) return 35f;
		}
		return 0f;
	}

	// melee fighter with nothing to hit: stand two steps in front of the nearest shooter, towards the enemy
	public static bool TryScreenPosition(Pawn p, PawnData d, Thing enemy, Battlefield f)
	{
		if (!BattleInstinctMod.Settings.screen) return false;
		Pawn shooter = null;
		float bd = 999f;
		foreach (Pawn o in f.friends)
		{
			if (o == p || o.Faction != Faction.OfPlayer || !o.Drafted || o.Downed) continue;
			Verb v = o.CurrentEffectiveVerb;
			if (v == null || v.verbProps.IsMeleeAttack) continue;
			float dd = (o.Position - p.Position).LengthHorizontal;
			if (dd < bd) { bd = dd; shooter = o; }
		}
		if (shooter == null || bd > 25f) return false;
		Vector3 dir = (enemy.Position - shooter.Position).ToVector3();
		if (dir.sqrMagnitude < 4f) return false;
		dir.Normalize();
		IntVec3 c = (shooter.Position.ToVector3Shifted() + dir * 2.5f).ToIntVec3();
		Map map = p.Map;
		if (!c.InBounds(map)) return false;
		if (!c.Standable(map))
		{
			if (!CellFinder.TryFindRandomReachableNearbyCell(c, map, 2f, TraverseParms.For(p, Danger.Deadly), x => x.Standable(map), null, out c))
				return false;
		}
		if (c == p.Position || (c - p.Position).LengthHorizontalSquared <= 2) return false;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, c);
		j.expiryInterval = 300;
		j.checkOverrideOnExpire = true;
		Brain.Issue(p, d, j, enemy, "BI_TxtScreen");
		return true;
	}

	// defend: nothing in range – take the best covered spot near the place to hold, facing the enemy
	public static bool TryHoldCover(Pawn p, PawnData d, Thing enemy, float leash)
	{
		if (!BattleInstinctMod.Settings.useCover) return false;
		Map map = p.Map;
		float Rate(IntVec3 c) => CoverUtility.CalculateOverallBlockChance(c, enemy.Position, map) * 40f - (c - enemy.Position).LengthHorizontal * 0.3f;
		float here = Rate(p.Position);
		IntVec3 bestC = IntVec3.Invalid;
		float bestR = here + 5f;
		int num = GenRadial.NumCellsInRadius(Mathf.Min(leash, 12f));
		for (int i = 1; i < num; i++)
		{
			IntVec3 c = d.anchor + GenRadial.RadialPattern[i];
			if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
			float r = Rate(c);
			if (r <= bestR) continue;
			if (!map.pawnDestinationReservationManager.CanReserve(c, p, true)) continue;
			if (!p.CanReach(c, PathEndMode.OnCell, Danger.Deadly)) continue;
			bestR = r;
			bestC = c;
		}
		if (!bestC.IsValid || bestC == p.Position) return false;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, bestC);
		j.expiryInterval = 400;
		j.checkOverrideOnExpire = true;
		Brain.Issue(p, d, j, enemy, "BI_TxtCover");
		return true;
	}

	// don't run ahead of the others: wait when much closer to the enemy than the rest of the group
	public static bool TooFarAhead(Pawn p, Thing enemy, Battlefield f)
	{
		if (!BattleInstinctMod.Settings.cohesion) return false;
		var ds = new List<float>();
		foreach (Pawn o in f.friends)
			if (o.Faction == Faction.OfPlayer && o.Drafted && !o.Downed && o.Map == p.Map)
				ds.Add((o.Position - enemy.Position).LengthHorizontal);
		if (ds.Count < 3) return false;
		ds.Sort();
		float median = ds[ds.Count / 2];
		return (p.Position - enemy.Position).LengthHorizontal < median - 12f;
	}

	// a downed colonist lies where no enemy is close: carry him to a bed
	public static bool TryRescue(Pawn p, PawnData d, List<Thing> near)
	{
		if (!BattleInstinctMod.Settings.rescue || p.WorkTagIsDisabled(WorkTags.Caring)) return false;
		if (near.Count > 0 && (near[0].Position - p.Position).LengthHorizontalSquared < 15 * 15) return false;
		Map map = p.Map;
		foreach (Pawn hurt in map.mapPawns.FreeColonistsSpawned)
		{
			if (!hurt.Downed || hurt.InBed() || hurt == p || hurt.ParentHolder is Pawn_CarryTracker) continue;
			if ((hurt.Position - p.Position).LengthHorizontalSquared > 30 * 30) continue;
			bool safe = true;
			foreach (Thing t in near.Take(10))
				if ((t.Position - hurt.Position).LengthHorizontalSquared < 12 * 12) { safe = false; break; }
			if (!safe || !p.CanReserveAndReach(hurt, PathEndMode.OnCell, Danger.Deadly)) continue;
			Building_Bed bed = RestUtility.FindPatientBedFor(hurt) ?? RestUtility.FindBedFor(hurt, p, false);
			if (bed == null) continue;
			Job j = JobMaker.MakeJob(JobDefOf.Rescue, hurt, bed);
			j.count = 1;
			Brain.Issue(p, d, j, null, "BI_TxtRescue");
			return d.ourJob == j;
		}
		return false;
	}
}
