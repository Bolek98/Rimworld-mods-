using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;

// 2.39: fire response. Priorities alone did not put a fire out: vanilla firefighting only looks at
// fires in the home area, one colonist per fire, and only when the colonist looks for new work –
// so during a big fire most colonists walked around and only now and then beat a flame.
// Now, every second, free colonists with firefighting on are sent straight to the nearest
// fire that nobody is beating – also fires in fields and stockpiles and next to your buildings.
public static class FireResponse
{
	private static readonly HashSet<string> keepJobs = new HashSet<string>
	{
		"BeatFire", "ExtinguishSelf", "TendPatient", "Rescue", "Capture", "Arrest", "Flee", "FleeAndCower",
		"Ingest", "LayDown", "Vomit", "AttackMelee", "AttackStatic", "DeliverToBed", "Wait_Combat", "DoBill"
	};

	public static bool Enabled => CrisesMod.S != null && CrisesMod.S.fireResponse;

	private static bool Ours(Fire f, Map map, Area_Home home)
	{
		if (!f.Spawned || f.parent is Pawn)
			return false;
		IntVec3 c = f.Position;
		if ((home != null && home[c]) || map.zoneManager.ZoneAt(c) != null)
			return true;
		// right next to a building of yours
		for (int i = 0; i < 9; i++)
		{
			IntVec3 n = c + GenAdj.AdjacentCellsAndInside[i];
			if (!n.InBounds(map))
				continue;
			Building b = n.GetFirstBuilding(map);
			if (b != null && b.Faction == Faction.OfPlayer)
				return true;
		}
		return false;
	}

	private static bool Free(Pawn p)
	{
		if (p.Downed || p.Drafted || p.InMentalState || !p.Awake() || p.workSettings == null || !p.workSettings.EverWork)
			return false;
		if (p.GetLord() != null || p.WorkTagIsDisabled(WorkTags.Firefighting) || p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter)
		    || p.workSettings.GetPriority(WorkTypeDefOf.Firefighter) <= 0)
			return false;
		if (HealthAIUtility.ShouldSeekMedicalRest(p) || (p.needs?.food != null && p.needs.food.Starving))
			return false;
		Job j = p.CurJob;
		return j == null || (!j.playerForced && !keepJobs.Contains(j.def.defName));
	}

	public static void Tick(Map map)
	{
		if (!Enabled || map == null || !map.IsPlayerHome)
			return;
		// 2.52.3: not during an attack – nobody is sent out to the raiders (the game's own firefighting still works)
		ForemanMapComponent comp = ForemanMapComponent.For(map);
		if (comp != null && ((comp.CurrentCrisis & SafeArea.ThreatMask) != 0 || CS.Of(comp).safeActive))
			return;
		List<Thing> all = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
		if (all.Count == 0)
			return;
		Area_Home home = map.areaManager.Home;
		var fires = new List<Fire>();
		for (int i = 0; i < all.Count; i++)
			if (all[i] is Fire f && Ours(f, map, home))
				fires.Add(f);
		if (fires.Count == 0)
			return;
		var colonists = map.mapPawns.FreeColonistsSpawned;
		int fighting = colonists.Count(p => p.CurJobDef == JobDefOf.BeatFire);
		int want = Mathf.Clamp(Mathf.CeilToInt(fires.Count / 2f), 2, 40);
		if (fighting >= want)
			return;
		// fires already somebody's target
		var taken = new HashSet<Thing>();
		foreach (Pawn p in colonists)
			if (p.CurJobDef == JobDefOf.BeatFire && p.CurJob.targetA.Thing != null)
				taken.Add(p.CurJob.targetA.Thing);
		var free = colonists.Where(Free)
			.Select(p => (p, d: fires.Min(f => f.Position.DistanceToSquared(p.Position))))
			.OrderBy(x => x.d).Select(x => x.p).ToList();
		foreach (Pawn p in free)
		{
			if (fighting >= want)
				break;
			Fire target = null;
			int best = int.MaxValue;
			foreach (Fire f in fires)
			{
				if (taken.Contains(f))
					continue;
				int d = f.Position.DistanceToSquared(p.Position);
				if (d < best && f.Position.InAllowedArea(p) && p.CanReserve(f) && p.CanReach(f, PathEndMode.Touch, Danger.Some))
				{
					best = d;
					target = f;
				}
			}
			if (target == null)
				continue;
			try
			{
				p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.BeatFire, target), JobCondition.InterruptForced);
				taken.Add(target);
				fighting++;
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[Steward] fire response: " + ex.Message, 0x57e3a51);
			}
		}
	}
}
