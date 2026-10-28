using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;

// Steward 2.27.0: medical emergency that really reacts. Priorities alone did
// nothing until the doctors finished what they were doing (or went to bed).
// Now, every few seconds, each patient who needs help right now (bleeding,
// needs tending, or lies downed outside a bed) and has nobody coming gets the
// nearest free doctor: ordinary work is dropped and the doctor goes at once.
// Forced orders, drafted colonists, sleeping/eating/fleeing ones are left alone.
public static class MedicalResponse
{
	private static List<WorkGiverDef> givers;
	private static readonly HashSet<string> keepJobs = new HashSet<string>
	{
		"TendPatient", "Rescue", "Capture", "BeatFire", "ExtinguishSelf", "Flee", "FleeAndCower",
		"Ingest", "LayDown", "Lovin", "AttackMelee", "AttackStatic", "Vomit", "DeliverToBed", "Arrest"
	};

	private static List<WorkGiverDef> Givers()
	{
		if (givers == null)
			givers = DefDatabase<WorkGiverDef>.AllDefsListForReading
				.Where(g => g.workType == WorkTypeDefOf.Doctor && g.Worker is WorkGiver_Scanner)
				.OrderByDescending(g => g.emergency).ThenByDescending(g => g.priorityInType).ToList();
		return givers;
	}

	public static bool Enabled => CrisesMod.S != null && CrisesMod.S.medicalResponse;

	private static bool NeedsHelp(Pawn p)
	{
		if (p == null || p.Dead || !p.Spawned || p.health == null)
			return false;
		// 2.52.3: only urgent cases (bleeding, infection, lying hurt outside a bed) – a bruise is left to the
		// doctors' normal work instead of pulling someone off their job
		if (HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(p))
			return true;
		return p.Downed && !p.InBed() && !p.DevelopmentalStage.Baby() && !p.IsPrisonerOfColony && (p.IsColonist || p.IsSlaveOfColony);
	}

	private static bool SomeoneComing(Map map, Pawn patient)
	{
		foreach (Pawn d in map.mapPawns.FreeColonistsSpawned)
		{
			Job j = d.CurJob;
			if (j != null && (j.def == JobDefOf.TendPatient || j.def == JobDefOf.Rescue) && j.targetA.Thing == patient)
				return true;
		}
		return false;
	}

	private static bool Free(Pawn d, Pawn patient)
	{
		if (d == patient || d.Downed || d.Drafted || d.InMentalState || !d.Awake() || d.workSettings == null || !d.workSettings.EverWork)
			return false;
		// 2.35.5: never pull a colonist out of a caravan, ritual or other group duty
		if (d.GetLord() != null)
			return false;
		if (d.WorkTypeIsDisabled(WorkTypeDefOf.Doctor) || d.workSettings.GetPriority(WorkTypeDefOf.Doctor) <= 0)
			return false;
		if (HealthAIUtility.ShouldSeekMedicalRest(d) || (d.needs?.food != null && d.needs.food.Starving))
			return false;
		Job j = d.CurJob;
		if (j == null)
			return true;
		if (j.playerForced || keepJobs.Contains(j.def.defName))
			return false;
		if (j.workGiverDef != null && j.workGiverDef.workType == WorkTypeDefOf.Doctor)
			return false;
		return true;
	}

	public static void Tick(Map map)
	{
		if (!Enabled || map == null || !map.IsPlayerHome)
			return;
		// 2.30.2: a colonist sleeping / resting on the floor goes to his bed when he
		// can use it, otherwise claims a free one; when no bed works the player is
		// told once why (owned by someone else, not reachable, forbidden …)
		FloorSleepers(map);
		List<Pawn> patients = new List<Pawn>();
		foreach (Pawn p in map.mapPawns.FreeColonistsAndPrisonersSpawned.Concat(map.mapPawns.SlavesOfColonySpawned).ToList())
			if (NeedsHelp(p) && !SomeoneComing(map, p))
				patients.Add(p);
		if (patients.Count == 0)
			return;
		var used = new HashSet<Pawn>();
		// worst first: bleeding most, then downed
		foreach (Pawn patient in patients.OrderByDescending(p => p.health.hediffSet.BleedRateTotal).ThenByDescending(p => p.Downed))
		{
			var doctors = map.mapPawns.FreeColonistsSpawned.ToList()
				.Where(d => !used.Contains(d) && Free(d, patient))
				.OrderBy(d => (d.Position - patient.Position).LengthHorizontalSquared / (1f + (d.skills?.GetSkill(SkillDefOf.Medicine)?.Level ?? 0)))
				.Take(4).ToList();
			foreach (Pawn d in doctors)
			{
				Job job = JobFor(d, patient);
				if (job == null)
					continue;
				d.jobs.StartJob(job, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false, cancelBusyStances: true);
				used.Add(d);
				break;
			}
		}
	}

	// 2.30.1: a patient lying outside a bed is first carried to a bed (rescue);
	// only when no bed can be found and he is bleeding is he tended on the spot.
	// Before, the emergency tending came first and patients were treated on the floor.
	private static readonly Dictionary<int, int> floorNext = new Dictionary<int, int>();
	private static readonly Dictionary<int, int> floorToldAt = new Dictionary<int, int>();

	public static void ResetGame()
	{
		floorNext.Clear();
		floorToldAt.Clear();
	}

	private static void FloorSleepers(Map map)
	{
		int now = Find.TickManager.TicksGame;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
		{
			try
			{
				Job cur = p.CurJob;
				if (p.Downed || p.Drafted || p.GetLord() != null || cur == null || cur.def != JobDefOf.LayDown || cur.targetA.Thing is Building_Bed || cur.playerForced || p.ownership == null)
					continue;
				if (floorNext.TryGetValue(p.thingIDNumber, out int t) && now < t)
					continue;
				floorNext[p.thingIDNumber] = now + 1250;
				Building_Bed bed = HealthAIUtility.ShouldSeekMedicalRest(p) ? RestUtility.FindPatientBedFor(p) : null;
				if (bed == null)
				{
					Building_Bed own = p.ownership.OwnedBed;
					if (own != null && RestUtility.IsValidBedFor(own, p, p, false, false, false, p.GuestStatus))
						bed = own;
				}
				if (bed == null)
				{
					bed = RestUtility.FindBedFor(p);
					if (bed != null && !bed.Medical && p.ownership.OwnedBed == null)
						p.ownership.ClaimBedIfNonMedical(bed);
				}
				if (bed != null)
				{
					p.jobs.StartJob(JobMaker.MakeJob(JobDefOf.LayDown, bed), JobCondition.InterruptForced);
					continue;
				}
				if (floorToldAt.TryGetValue(p.thingIDNumber, out int told) && now - told < 30000)
					continue;
				floorToldAt[p.thingIDNumber] = now;
				Messages.Message("CF_FloorSleep".Translate(p.LabelShort, WhyNoBed(p)), p, MessageTypeDefOf.CautionInput, historical: false);
			}
			catch
			{
			}
		}
	}

	// the first reason why the colonist's own bed (or any free bed) does not work
	private static string WhyNoBed(Pawn p)
	{
		Building_Bed own = p.ownership.OwnedBed;
		if (own != null)
			return Reason(p, own, true);
		Building_Bed any = null;
		foreach (Building b in p.Map.listerBuildings.allBuildingsColonist)
		{
			if (b is Building_Bed bb && bb.def.building.bed_humanlike && !bb.Medical && bb.ForColonists)
			{
				any = bb;
				if (bb.AnyUnownedSleepingSlot)
					return Reason(p, bb, false);
			}
		}
		return any == null ? "CF_FloorNoBeds".Translate().ToString() : "CF_FloorAllOwned".Translate().ToString();
	}

	private static string Reason(Pawn p, Building_Bed b, bool own)
	{
		string which = (own ? "CF_FloorOwnBed" : "CF_FloorFreeBed").Translate(b.LabelShort).ToString();
		string why;
		if (b.IsForbidden(p))
			why = "CF_FloorForbidden".Translate();
		else if (p.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap != null && !p.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap[b.Position])
			why = "CF_FloorArea".Translate(p.playerSettings.EffectiveAreaRestrictionInPawnCurrentMap.Label);
		else if (!p.CanReach(b, PathEndMode.OnCell, Danger.Deadly))
			why = "CF_FloorUnreachable".Translate();
		else if (!p.CanReserve(b, b.SleepingSlotsCount, 0))
			why = "CF_FloorReserved".Translate();
		else if (!RestUtility.CanUseBedNow(b, p, false, false, p.GuestStatus))
			why = "CF_FloorCantUse".Translate();
		else if (!p.CanReach(b, PathEndMode.OnCell, Danger.Some))
			why = "CF_FloorDanger".Translate();
		else
			why = "CF_FloorUnknown".Translate();
		return which + " " + why;
	}

	// 1.0.7: only rescue or tending – other doctor jobs from mods (giving drugs, feeding …) are never
	// started for an emergency; a colonist bleeding fast on the floor is stabilised (tended) first and
	// then carried to bed, a slow bleeder is carried first
	private static bool IsRescueGiver(WorkGiverDef g) => g.defName.IndexOf("Rescue", StringComparison.OrdinalIgnoreCase) >= 0;

	private static bool IsTendJob(Job j) => j != null && (j.def == JobDefOf.TendPatient || j.def.defName.IndexOf("Tend", StringComparison.OrdinalIgnoreCase) >= 0);

	private static Job JobFor(Pawn doctor, Pawn patient)
	{
		bool onFloor = patient.Downed && !patient.InBed();
		float bleed = patient.health.hediffSet.BleedRateTotal;
		if (onFloor)
		{
			if (bleed >= 0.5f)
			{
				Job stop = TryGivers(doctor, patient, g => !IsRescueGiver(g), IsTendJob);
				if (stop != null)
					return stop;
			}
			Job rescue = TryGivers(doctor, patient, IsRescueGiver, j => j.def == JobDefOf.Rescue || j.def.defName.IndexOf("Rescue", StringComparison.OrdinalIgnoreCase) >= 0);
			if (rescue != null)
				return rescue;
			if (bleed <= 0.01f)
				return null;
		}
		return TryGivers(doctor, patient, g => !IsRescueGiver(g), IsTendJob);
	}

	private static Job TryGivers(Pawn doctor, Pawn patient, Predicate<WorkGiverDef> which, Predicate<Job> accept = null)
	{
		foreach (WorkGiverDef g in Givers())
		{
			if (!which(g))
				continue;
			try
			{
				if (!(g.Worker is WorkGiver_Scanner s) || s.ShouldSkip(doctor))
					continue;
				if (!s.HasJobOnThing(doctor, patient))
					continue;
				Job j = s.JobOnThing(doctor, patient);
				if (j != null && (accept == null || accept(j)))
				{
					j.workGiverDef = g;
					return j;
				}
			}
			catch
			{
			}
		}
		return null;
	}
}
