using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BattleInstinct;

// 0.3.0 – ideas from Gota's "Drafted AI" (threat map, directional cover, spacing, hazard evasion),
// written again for Battle Instinct.

// cells where our people just got hurt stay "hot" for a while; colonists avoid standing there
public static class Heat
{
	private const int TicksPerUnit = 60;
	private const int MaxUnits = 40;      // a cell stays hot at most ~40 s
	private static readonly Dictionary<Map, int[]> grids = new Dictionary<Map, int[]>();

	public static void Clear() => grids.Clear();

	private static int[] Grid(Map map)
	{
		if (!grids.TryGetValue(map, out int[] g) || g.Length != map.cellIndices.NumGridCells)
			grids[map] = g = new int[map.cellIndices.NumGridCells];
		return g;
	}

	public static void Push(Map map, IntVec3 cell, float amount)
	{
		if (map == null || !cell.InBounds(map) || amount <= 0f) return;
		int now = Find.TickManager.TicksGame;
		int[] g = Grid(map);
		int i = map.cellIndices.CellToIndex(cell);
		g[i] = Math.Min(Math.Max(g[i], now) + (int)(amount * TicksPerUnit), now + MaxUnits * TicksPerUnit);
	}

	// 0 = calm, 1 = hit recently, more = hit a lot
	public static float Get(Map map, IntVec3 cell)
	{
		if (map == null || !cell.InBounds(map) || !grids.TryGetValue(map, out int[] g) || g.Length != map.cellIndices.NumGridCells) return 0f;
		int dt = g[map.cellIndices.CellToIndex(cell)] - Find.TickManager.TicksGame;
		return dt <= 0 ? 0f : dt / (float)TicksPerUnit / 6f;
	}

	public static bool Hot(Map map, IntVec3 cell) => Get(map, cell) > 0.5f;
}

[HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
public static class Patch_Heat
{
	public static void Postfix(Thing __instance, DamageInfo dinfo)
	{
		try
		{
			if (!(__instance is Pawn p) || !p.Spawned || p.Faction != Faction.OfPlayer) return;
			if (dinfo.Def == null || !dinfo.Def.harmsHealth || dinfo.Amount <= 0f) return;
			if (dinfo.Instigator == null || !dinfo.Instigator.HostileTo(Faction.OfPlayer)) return;
			Map map = p.Map;
			float a = Mathf.Clamp(dinfo.Amount / 4f, 1f, 8f);
			Heat.Push(map, p.Position, a);
			foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(p))
				Heat.Push(map, c, a * 0.4f);
		}
		catch
		{
		}
	}
}

public static class Spots
{
	// how well a cell is covered from the enemies that can actually shoot at it (the worst one counts most)
	public static float CoverAgainst(Map map, IntVec3 cell, List<Thing> enemies, out int exposed)
	{
		exposed = 0;
		float worst = 1f, sum = 0f;
		int n = 0, k = 0;
		foreach (Thing e in enemies)
		{
			if (k++ >= 8) break;
			if (e is Pawn ep && ep.CurrentEffectiveVerb?.verbProps?.IsMeleeAttack == true) continue;
			float range = (e as Pawn)?.CurrentEffectiveVerb?.verbProps?.range ?? 30f;
			if (!cell.InHorDistOf(e.Position, Mathf.Max(range, 12f) + 2f)) continue;
			if (!GenSight.LineOfSight(e.Position, cell, map, true)) continue;
			float b = CoverUtility.CalculateOverallBlockChance(cell, e.Position, map);
			if (b < 0.15f) exposed++;
			worst = Mathf.Min(worst, b);
			sum += b;
			n++;
		}
		if (n == 0) return 1f;   // nobody sees it
		return worst * 0.6f + sum / n * 0.4f;
	}

	// keep a step apart, so one grenade does not hit everybody
	public static bool TooClose(Pawn p, IntVec3 c, Battlefield f)
	{
		if (!BattleInstinctMod.Settings.spread) return false;
		foreach (Pawn o in f.friends)
		{
			if (o == p || o.Faction != Faction.OfPlayer || !o.Drafted || o.Downed || o.Map != p.Map) continue;
			if ((o.Position - c).LengthHorizontalSquared <= 2) return true;
			if (o.pather != null && o.pather.Moving && (o.pather.Destination.Cell - c).LengthHorizontalSquared <= 2) return true;
		}
		return false;
	}

	public static bool BadSpot(Pawn p, IntVec3 c, Battlefield f) =>
		Heat.Hot(p.Map, c) || TooClose(p, c, f) || Hazards.Burning(p.Map, c) || Hazards.Gas(p.Map, c);
}

// fire, toxic gas and incoming grenades / shells: get out of the way
public static class Hazards
{
	private static AccessTools.FieldRef<Projectile, Vector3> destRef;
	private static bool destTried;

	private static Vector3? Destination(Projectile pr)
	{
		if (!destTried)
		{
			destTried = true;
			try { destRef = AccessTools.FieldRefAccess<Projectile, Vector3>("destination"); }
			catch { destRef = null; }
		}
		if (destRef == null) return null;
		try { return destRef(pr); }
		catch { return null; }
	}

	public static bool Burning(Map map, IntVec3 c)
	{
		try { return c.InBounds(map) && c.ContainsStaticFire(map); }
		catch { return false; }
	}

	public static bool Gas(Map map, IntVec3 c)
	{
		try { return c.InBounds(map) && map.gasGrid != null && map.gasGrid.DensityAt(c, GasType.ToxGas) > 0; }
		catch { return false; }
	}

	// incoming explosives on this map: where they land and how big the blast is
	public static List<(IntVec3 at, float radius, Thing launcher)> Incoming(Map map)
	{
		var list = new List<(IntVec3, float, Thing)>();
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Projectile))
		{
			if (!(t is Projectile pr) || pr.def?.projectile == null) continue;
			float r = pr.def.projectile.explosionRadius;
			if (r < 0.9f) continue;
			if (pr.Launcher != null && !pr.Launcher.HostileTo(Faction.OfPlayer)) continue;
			Vector3? d = Destination(pr);
			if (d == null) continue;
			list.Add((d.Value.ToIntVec3(), r, pr.Launcher));
		}
		return list;
	}

	// returns true when it sent the pawn away
	public static bool TryDodge(Pawn p, PawnData d, Battlefield f, List<(IntVec3 at, float radius, Thing launcher)> incoming)
	{
		if (!BattleInstinctMod.Settings.dodge) return false;
		Map map = p.Map;
		IntVec3 danger = IntVec3.Invalid;
		float radius = 0f;
		Thing from = null;
		string text = "BI_TxtDodge";
		if (incoming != null)
			foreach (var x in incoming)
				if ((x.at - p.Position).LengthHorizontal <= x.radius + 1.5f)
				{
					danger = x.at; radius = x.radius; from = x.launcher;
					break;
				}
		if (!danger.IsValid && (Burning(map, p.Position) || Gas(map, p.Position)))
		{
			danger = p.Position;
			radius = 2f;
			text = "BI_TxtHazard";
		}
		if (!danger.IsValid) return false;
		Job cur = p.CurJob;
		if (cur != null && cur == d.ourJob && cur.def == JobDefOf.Goto && cur.targetA.Cell.IsValid
		    && (cur.targetA.Cell - danger).LengthHorizontal > radius + 1f)
			return true;   // already running away
		IntVec3 bestC = IntVec3.Invalid;
		float bestR = float.MinValue;
		int num = GenRadial.NumCellsInRadius(7f);
		for (int i = 1; i < num; i++)
		{
			IntVec3 c = p.Position + GenRadial.RadialPattern[i];
			if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map)) continue;
			float dd = (c - danger).LengthHorizontal;
			if (dd <= radius + 1f) continue;
			if (Burning(map, c) || Gas(map, c)) continue;
			if (!map.pawnDestinationReservationManager.CanReserve(c, p, true)) continue;
			float r = dd * 10f - (c - p.Position).LengthHorizontal * 2f - Heat.Get(map, c) * 10f;
			if (from != null && from.Spawned) r += CoverUtility.CalculateOverallBlockChance(c, from.Position, map) * 25f;
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
		Brain.Issue(p, d, j, null, text);
		return d.ourJob == j;
	}
}
