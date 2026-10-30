using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.29: 22 more crises. Cheap checks run every few seconds; the
// costly ones (rooms, roofs, quests) are cached per map for ~20 seconds.
// ---------------------------------------------------------------------------
public static partial class CrisisRules
{
	private sealed class SlowCache
	{
		public int tick = -99999;
		public bool indoorTemp, freezer, roof, quest, noBeds, water, hygiene, ammo;
	}

	private static readonly Dictionary<Map, SlowCache> slow = new Dictionary<Map, SlowCache>();

	private static void DetectMore(Map map, MapDemand d, ref Crisis c)
	{
		Try(ref c, Crisis.Manhunters, () => Manhunters(map));
		Try(ref c, Crisis.Epidemic, () => Epidemic(map));
		Try(ref c, Crisis.PrisonBreak, () => PrisonBreak(map));
		Try(ref c, Crisis.DangerousBreak, () => DangerousBreak(map));
		Try(ref c, Crisis.Siege, () => Siege(map));
		Try(ref c, Crisis.Infestation, () => Infestation(map));
		Try(ref c, Crisis.Death, () => DeadColonist(map));
		Try(ref c, Crisis.AnimalSick, () => SickAnimals(map) >= 2);
		Try(ref c, Crisis.Labor, () => ModsConfig.BiotechActive && InLabor(map));
		Try(ref c, Crisis.EntityEscape, () => ModsConfig.AnomalyActive && EntityLoose(map));
		Try(ref c, Crisis.Darkness, () => ModsConfig.AnomalyActive && GameCondition_UnnaturalDarkness.UnnaturalDarknessOnMap(map));
		Try(ref c, Crisis.VacuumBreach, () => ModsConfig.OdysseyActive && VacuumAtColonists(map));
		Try(ref c, Crisis.ToxGas, () => ToxGasAtColony(map));
		Try(ref c, Crisis.NoFirefighters, () => FireInHome(map) && Firefighters(map) < 2);
		SlowCache s = Slow(map, d);
		if (s.indoorTemp && Allowed(Crisis.IndoorTemp)) c |= Crisis.IndoorTemp;
		if (s.freezer && Allowed(Crisis.FreezerThaw)) c |= Crisis.FreezerThaw;
		if (s.roof && Allowed(Crisis.RoofCollapse)) c |= Crisis.RoofCollapse;
		if (s.quest && Allowed(Crisis.QuestDeadline)) c |= Crisis.QuestDeadline;
		if (s.noBeds && Allowed(Crisis.NoBeds)) c |= Crisis.NoBeds;
		if (s.water && Allowed(Crisis.WaterShortage)) c |= Crisis.WaterShortage;
		if (s.hygiene && Allowed(Crisis.Hygiene)) c |= Crisis.Hygiene;
		if (s.ammo && Allowed(Crisis.LowAmmo)) c |= Crisis.LowAmmo;
	}

	private static SlowCache Slow(Map map, MapDemand d)
	{
		if (!slow.TryGetValue(map, out SlowCache s))
			slow[map] = s = new SlowCache();
		int now = Find.TickManager.TicksGame;
		if (now - s.tick < 1250)
			return s;
		s.tick = now;
		s.indoorTemp = Safe(() => Allowed(Crisis.IndoorTemp) && BadIndoorTemp(map));
		s.freezer = Safe(() => Allowed(Crisis.FreezerThaw) && FreezerWarm(map));
		s.roof = Safe(() => Allowed(Crisis.RoofCollapse) && RoofAtRisk(map));
		s.quest = Safe(() => Allowed(Crisis.QuestDeadline) && QuestDeadlineSoon());
		s.noBeds = Safe(() => Allowed(Crisis.NoBeds) && NoBedsForPatients(map, d));
		s.water = Safe(() => Allowed(Crisis.WaterShortage) && NeedLow(map, "DBHThirst", 0.2f, 2));
		s.hygiene = Safe(() => Allowed(Crisis.Hygiene) && NeedLow(map, "Hygiene", 0.15f, 3));
		s.ammo = Safe(() => Allowed(Crisis.LowAmmo) && AmmoLow(map));
		return s;
	}

	private static bool Safe(Func<bool> f)
	{
		try { return f(); }
		catch { return false; }
	}

	private static IEnumerable<Pawn> People(Map map) => map.mapPawns.FreeColonistsSpawned.Concat(map.mapPawns.SlavesOfColonySpawned);

	// ---- cheap -------------------------------------------------------------
	private static bool Manhunters(Map map)
	{
		int n = 0;
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
		{
			if (p.Downed || !p.RaceProps.Animal || !p.InMentalState)
				continue;
			MentalStateDef m = p.MentalStateDef;
			if ((m == MentalStateDefOf.Manhunter || m == MentalStateDefOf.ManhunterPermanent) && ++n >= 2)
				return true;
		}
		return false;
	}

	private static readonly HashSet<string> contagious = new HashSet<string> { "Flu", "Plague", "Malaria", "SleepingSickness", "LungRot", "FibrousMechanites", "SensoryMechanites" };

	private static bool Epidemic(Map map)
	{
		int n = 0;
		foreach (Pawn p in People(map).Concat(map.mapPawns.PrisonersOfColonySpawned))
		{
			List<Hediff> hs = p.health?.hediffSet?.hediffs;
			if (hs == null)
				continue;
			for (int i = 0; i < hs.Count; i++)
			{
				HediffDef h = hs[i].def;
				if (contagious.Contains(h.defName) || (h.makesSickThought && h.lethalSeverity > 0f && h.tendable && !h.chronic && h != HediffDefOf.WoundInfection))
				{
					n++;
					break;
				}
			}
			if (n >= 2)
				return true;
		}
		return false;
	}

	private static bool PrisonBreak(Map map)
	{
		foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
			if (PrisonBreakUtility.IsPrisonBreaking(p))
				return true;
		if (ModsConfig.IdeologyActive)
			foreach (Pawn p in map.mapPawns.SlavesOfColonySpawned)
				if (SlaveRebellionUtility.IsRebelling(p))
					return true;
		return false;
	}

	private static bool DangerousBreak(Map map)
	{
		foreach (Pawn p in People(map))
		{
			if (!p.InMentalState || p.MentalStateDef == null)
				continue;
			MentalStateDef m = p.MentalStateDef;
			string n = m.defName;
			if (m.IsAggro || n.Contains("Berserk") || n.Contains("FireStarting") || n.Contains("Murderous") || n.Contains("Tantrum") || n.Contains("Sadistic") || n.Contains("Jailbreaker"))
				return true;
		}
		return false;
	}

	private static bool Siege(Map map)
	{
		foreach (Lord l in map.lordManager.lords)
			if (l?.LordJob is LordJob_Siege && l.faction != null && l.faction.HostileTo(Faction.OfPlayer) && l.ownedPawns.Count > 0)
				return true;
		return false;
	}

	private static bool Infestation(Map map)
	{
		if (ThingDefOf.Hive == null)
			return false;
		Area_Home home = map.areaManager.Home;
		foreach (Thing h in map.listerThings.ThingsOfDef(ThingDefOf.Hive))
			if (h.Spawned && (home[h.Position] || h.Position.Roofed(map)))
				return true;
		return false;
	}

	private static bool DeadColonist(Map map)
	{
		Area_Home home = map.areaManager.Home;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
		{
			if (!(t is Corpse c) || !c.Spawned || c.InnerPawn == null || !home[c.Position])
				continue;
			// 2.39.1: a body already put away in a stockpile / shelf (kept, waiting for a grave
			// or excluded from the crematorium) is not an emergency – before it kept the crisis
			// "death in the colony" on forever and pushed construction and hauling up in every style
			if (c.IsInAnyStorage() || c.IsForbidden(Faction.OfPlayer))
				continue;
			Pawn p = c.InnerPawn;
			if (p.RaceProps.Humanlike && (p.Faction == Faction.OfPlayer || p.IsSlaveOfColony))
				return true;
		}
		return false;
	}

	private static int SickAnimals(Map map)
	{
		int n = 0;
		foreach (Pawn p in map.mapPawns.SpawnedColonyAnimals.ToList())
			if (HealthAIUtility.ShouldBeTendedNowByPlayer(p))
				n++;
		return n;
	}

	private static bool InLabor(Map map)
	{
		foreach (Pawn p in People(map).Concat(map.mapPawns.PrisonersOfColonySpawned))
		{
			List<Hediff> hs = p.health?.hediffSet?.hediffs;
			if (hs == null)
				continue;
			for (int i = 0; i < hs.Count; i++)
				if (hs[i].def.defName == "PregnancyLabor" || hs[i].def.defName == "PregnancyLaborPushing")
					return true;
		}
		return false;
	}

	private static bool EntityLoose(Map map)
	{
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			if (p.RaceProps.IsAnomalyEntity && !p.Downed && !p.IsOnHoldingPlatform && p.HostileTo(Faction.OfPlayer) && map.areaManager.Home[p.Position])
				return true;
		return false;
	}

	private static bool VacuumAtColonists(Map map)
	{
		foreach (Pawn p in People(map))
		{
			Room r = p.GetRoom();
			if (r != null && !r.UsesOutdoorTemperature && r.Vacuum > 0.5f)
				return true;
		}
		return false;
	}

	private static bool ToxGasAtColony(Map map)
	{
		if (map.gasGrid == null)
			return false;
		foreach (Pawn p in People(map))
			if (map.gasGrid.DensityAt(p.Position, GasType.ToxGas) > 40)
				return true;
		return false;
	}

	private static int Firefighters(Map map)
	{
		int n = 0;
		foreach (Pawn p in People(map))
			if (!p.Downed && !p.InMentalState && p.workSettings != null && !p.WorkTypeIsDisabled(WorkTypeDefOf.Firefighter) && p.workSettings.GetPriority(WorkTypeDefOf.Firefighter) > 0)
				n++;
		return n;
	}

	// ---- costly (cached) ---------------------------------------------------
	private static bool BadIndoorTemp(Map map)
	{
		var seen = new HashSet<Room>();
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is Building_Bed bed) || !bed.def.building.bed_humanlike || bed.ForPrisoners)
				continue;
			Room r = bed.GetRoom();
			if (r == null || r.UsesOutdoorTemperature || !seen.Add(r))
				continue;
			float t = r.Temperature;
			if (t < -5f || t > 42f)
				return true;
		}
		return false;
	}

	private static bool FreezerWarm(Map map)
	{
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			CompTempControl tc = b.GetComp<CompTempControl>();
			if (tc == null || tc.Props.energyPerSecond >= 0f || tc.targetTemperature > 0f)
				continue;
			IntVec3 cold = b.Position + IntVec3.South.RotatedBy(b.Rotation);
			if (!cold.InBounds(map))
				continue;
			Room r = cold.GetRoom(map);
			if (r != null && !r.UsesOutdoorTemperature && r.Temperature > 2f)
				return true;
		}
		return false;
	}

	// a thin roof that loses its last support when a marked rock is mined
	private static bool RoofAtRisk(Map map)
	{
		List<Designation> mines = map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Mine).ToList();
		if (mines.Count == 0)
			return false;
		var mined = new HashSet<IntVec3>(mines.Select(d => d.target.Cell));
		const float reach = 6.9f;
		int radial = GenRadial.NumCellsInRadius(reach);
		foreach (IntVec3 m in mined)
		{
			for (int i = 0; i < radial; i++)
			{
				IntVec3 r = m + GenRadial.RadialPattern[i];
				if (!r.InBounds(map))
					continue;
				RoofDef roof = r.GetRoof(map);
				if (roof == null || roof.isThickRoof || !map.areaManager.Home[r])
					continue;
				bool held = false;
				for (int k = 0; k < radial && !held; k++)
				{
					IntVec3 s = r + GenRadial.RadialPattern[k];
					if (!s.InBounds(map) || mined.Contains(s))
						continue;
					Building e = s.GetEdifice(map);
					if (e != null && e.def.holdsRoof)
						held = true;
				}
				if (!held)
					return true;
			}
		}
		return false;
	}

	private static bool QuestDeadlineSoon()
	{
		foreach (Quest q in Find.QuestManager.QuestsListForReading)
		{
			if (q.State != QuestState.Ongoing || q.hidden)
				continue;
			foreach (QuestPart part in q.PartsListForReading)
				if (part is QuestPart_Delay d && d.State == QuestPartState.Enabled && !string.IsNullOrEmpty(d.expiryInfoPart) && d.TicksLeft > 0 && d.TicksLeft < 60000)
					return true;
		}
		return false;
	}

	private static bool NoBedsForPatients(Map map, MapDemand d)
	{
		int patients = 0;
		foreach (Pawn p in People(map))
			if (p.Downed && !p.InBed())
				patients++;
		if (patients == 0)
			return false;
		int free = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
			if (b is Building_Bed bed && bed.def.building.bed_humanlike && !bed.ForPrisoners && bed.Medical && !bed.AnyOccupants)
				free++;
		if (free == 0)
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
				if (b is Building_Bed bed && bed.def.building.bed_humanlike && !bed.ForPrisoners && !bed.AnyOccupants && bed.OwnersForReading.Count == 0)
					free++;
		return free < patients;
	}

	private static bool NeedLow(Map map, string needDef, float level, int count)
	{
		NeedDef nd = DefDatabase<NeedDef>.GetNamedSilentFail(needDef);
		if (nd == null)
			return false;
		int n = 0;
		foreach (Pawn p in People(map))
		{
			Need need = p.needs?.TryGetNeed(nd);
			if (need != null && need.CurLevelPercentage < level && ++n >= count)
				return true;
		}
		return false;
	}

	private static bool? yayoAmmoOn;
	private static FieldInfo yayoAmmoField;
	private static List<ThingDef> ammoDefs;

	private static bool AmmoLow(Map map)
	{
		if (yayoAmmoOn == null)
		{
			Type t = GenTypes.GetTypeInAnyAssembly("yayoCombat.yayoCombat");
			yayoAmmoField = t == null ? null : AccessTools.Field(t, "ammo");
			ammoDefs = DefDatabase<ThingDef>.AllDefsListForReading.Where(x => x.defName.StartsWith("yy_ammo_", StringComparison.OrdinalIgnoreCase)).ToList();
			yayoAmmoOn = yayoAmmoField != null && ammoDefs.Count > 0;
		}
		if (yayoAmmoOn != true || !(yayoAmmoField.GetValue(null) is bool on) || !on)
			return false;
		int total = 0;
		foreach (ThingDef d in ammoDefs)
			foreach (Thing t in map.listerThings.ThingsOfDef(d))
				if (t.Spawned && !t.IsForbidden(Faction.OfPlayer))
					total += t.stackCount;
		int people = map.mapPawns.FreeColonistsSpawnedCount;
		return people > 0 && total < people * 30;
	}
}
