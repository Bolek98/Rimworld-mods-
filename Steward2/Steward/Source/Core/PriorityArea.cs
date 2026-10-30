using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.22.0: priority area. The player drags a rectangle (Orders menu) and picks
// what should be done in it. The nearest free colonists drop their usual work
// and do everything of that kind inside the rectangle first; when nothing is
// left the area disappears by itself and everyone goes back to normal work.
// ---------------------------------------------------------------------------
public enum PriorityAreaMode
{
	All,
	Build,
	Mine,
	Plants,
	Haul,
	Clean,
	// 2.35.1: rescue, doctors and firefighting inside the area
	Emergency
}

public sealed class PriorityArea : IExposable
{
	public int id;
	public int minX, minZ, maxX, maxZ;
	public PriorityAreaMode mode;
	public int workers;
	public int createdTick;
	public int lastWorkTick;
	public List<Pawn> assigned = new List<Pawn>();
	// 2.22.2: colonists picked by hand (no automatic filling), interrupt now or wait,
	// the area stays until the work inside is really done
	public bool manual;
	public bool interrupt = true;
	public bool hadWork;
	public int emptyChecks;
	// 2.22.3: work count from the last check (not saved) and per-pawn "nothing found" pause,
	// so a colonist does not scan every work type on every job search while nothing waits
	[Unsaved] public int lastCount = -1;
	[Unsaved] public Dictionary<int, int> idleUntil = new Dictionary<int, int>();

	private List<IntVec3> cellCache;

	public CellRect Rect => CellRect.FromLimits(minX, minZ, maxX, maxZ);

	public bool Contains(IntVec3 c) => c.x >= minX && c.x <= maxX && c.z >= minZ && c.z <= maxZ;

	public List<IntVec3> Cells => cellCache ??= Rect.Cells.ToList();

	public IntVec3 Center => new IntVec3((minX + maxX) / 2, 0, (minZ + maxZ) / 2);

	public void ExposeData()
	{
		Scribe_Values.Look(ref id, "id");
		Scribe_Values.Look(ref minX, "minX");
		Scribe_Values.Look(ref minZ, "minZ");
		Scribe_Values.Look(ref maxX, "maxX");
		Scribe_Values.Look(ref maxZ, "maxZ");
		Scribe_Values.Look(ref mode, "mode", PriorityAreaMode.All);
		Scribe_Values.Look(ref workers, "workers", 3);
		Scribe_Values.Look(ref createdTick, "createdTick");
		Scribe_Values.Look(ref lastWorkTick, "lastWorkTick");
		Scribe_Values.Look(ref manual, "manual");
		Scribe_Values.Look(ref interrupt, "interrupt", true);
		Scribe_Values.Look(ref hadWork, "hadWork");
		Scribe_Values.Look(ref emptyChecks, "emptyChecks");
		Scribe_Collections.Look(ref assigned, "assigned", LookMode.Reference);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			assigned ??= new List<Pawn>();
			assigned.RemoveAll(p => p == null);
		}
	}
}

public sealed class PriorityAreaComp : MapComponent
{
	public List<PriorityArea> areas = new List<PriorityArea>();
	private int nextId = 1;

	public PriorityAreaComp(Map map) : base(map) { }

	public static PriorityAreaComp For(Map m) => m?.GetComponent<PriorityAreaComp>();

	public static bool Enabled => ForemanMod.Settings != null && ForemanMod.Settings.priorityAreas;

	// 2.23.0: critical situations come first
	// 2.35.4: only real danger pauses an area (fire, raid, manhunters, breaks,
	// toxic air …) – food, cold, weather, a birth or one patient no longer do
	// 2.49.4: "toxic air" (pollution, wastepacks lying around) no longer pauses an area – only real
	// gas at your people does (ToxGas)
	public const Crisis CriticalMask = Crisis.Fire | Crisis.Raid | Crisis.Manhunters | Crisis.PrisonBreak
		| Crisis.DangerousBreak | Crisis.EntityEscape | Crisis.VacuumBreach | Crisis.ToxGas | Crisis.Siege;

	// 2.35.1: an area that does rescue / doctors / firefighting is exactly what a
	// fire needs – it only waits for raids and other danger
	public const Crisis RescueCrises = Crisis.Fire | Crisis.Medical | Crisis.NoFirefighters;

	public bool Paused => PauseReason(null) != null;

	public bool PausedFor(PriorityArea a) => PauseReason(a) != null;

	// null = working; otherwise why the area waits (shown on the map)
	// 2.52.3: worked out once a second per area – it was asked every frame and on every job search
	private readonly Dictionary<PriorityArea, KeyValuePair<int, string>> pauseCache = new Dictionary<PriorityArea, KeyValuePair<int, string>>();
	private int pauseFrame = -1;

	public string PauseReason(PriorityArea a)
	{
		int now = Find.TickManager.TicksGame;
		int frame = Time.frameCount;
		if (a != null && pauseCache.TryGetValue(a, out var kv) && now >= kv.Key && now - kv.Key < 60 && (now != kv.Key || frame - pauseFrame < 30))
			return kv.Value;
		string r = PauseReasonNow(a);
		if (a != null)
		{
			if (pauseCache.Count > 64)
				pauseCache.Clear();
			pauseCache[a] = new KeyValuePair<int, string>(now, r);
			pauseFrame = frame;
		}
		return r;
	}

	private string PauseReasonNow(PriorityArea a)
	{
		if (a != null && !ActiveStyle(a))
			return "CF_PA_PauseStyle".Translate(StyleNeeded(a.mode));
		if (GenHostility.AnyHostileActiveThreatToPlayer(map, countDormantPawnsAsHostile: false))
			return "CF_PA_PauseHostile".Translate();
		ForemanMapComponent fc = ForemanMapComponent.For(map);
		if (fc == null)
			return null;
		Crisis mask = a != null && PriorityAreaWork.HasEmergency(a.mode) ? CriticalMask & ~RescueCrises : CriticalMask;
		Crisis c = fc.CurrentCrisis & mask;
		return c != 0 ? (string)"CF_PA_PauseCrisis".Translate(CrisisText.Label(c)) : null;
	}

	// 2.23.0: priority areas belong to the Balanced style – in any other style
	// they are hidden and wait (not removed) until Balanced is chosen again
	// 2.49.3: auto focus switches the style on its own (Building, Defence …) and the areas
	// then stopped silently – they work in every style now
	public static bool StyleAllows(Map m) => true;

	public bool Hidden => false;

	// 2.49.3: each kind of area works in the colony style made for that work – "everything" in
	// Balanced (and your own style), building in Building, mining in Mining, plants in Fields and
	// forest / Farming, hauling and cleaning in Tidy; rescue works always (it is about lives)
	public static ForemanStyle[] StylesFor(PriorityAreaMode m)
	{
		// 2.49.4: back to every style – switching styles all the time was annoying
		if (true)
			return null;
		switch (m)
		{
		case PriorityAreaMode.Build: return new[] { ForemanStyle.Building };
		case PriorityAreaMode.Mine: return new[] { ForemanStyle.Mining };
		case PriorityAreaMode.Plants: return new[] { ForemanStyle.FieldsForest, ForemanStyle.Farming };
		case PriorityAreaMode.Haul:
		case PriorityAreaMode.Clean: return new[] { ForemanStyle.Tidy };
		case PriorityAreaMode.Emergency: return null;
		default: return new[] { ForemanStyle.Balanced, ForemanStyle.Custom };
		}
	}

	public bool ActiveStyle(PriorityArea a)
	{
		ForemanStyle[] ok = StylesFor(a.mode);
		if (ok == null)
			return true;
		ForemanMapComponent fc = ForemanMapComponent.For(map);
		return fc == null || Array.IndexOf(ok, fc.style) >= 0;
	}

	public static string StyleNeeded(PriorityAreaMode m)
	{
		ForemanStyle[] ok = StylesFor(m);
		return ok == null ? "" : string.Join(" / ", ok.Select(Styles.Label).ToArray());
	}

	public PriorityArea AreaOf(Pawn p)
	{
		for (int i = 0; i < areas.Count; i++)
			if (areas[i].assigned.Contains(p))
				return areas[i];
		return null;
	}

	public void Add(CellRect r, PriorityAreaMode mode, List<Pawn> chosen, int workers, bool now)
	{
		r = r.ClipInsideMap(map);
		if (r.Area <= 0)
			return;
		int tick = Find.TickManager.TicksGame;
		var a = new PriorityArea
		{
			id = nextId++,
			minX = r.minX,
			minZ = r.minZ,
			maxX = r.maxX,
			maxZ = r.maxZ,
			mode = mode,
			workers = Mathf.Max(0, workers),
			createdTick = tick,
			lastWorkTick = tick,
			interrupt = now,
			manual = chosen != null && chosen.Count > 0
		};
		if (a.manual)
		{
			// a colonist works for one area at a time – the newest one wins
			foreach (PriorityArea other in areas)
				other.assigned.RemoveAll(chosen.Contains);
			a.assigned.AddRange(chosen);
			if (now)
				foreach (Pawn p in chosen)
					Interrupt(p, a);
		}
		areas.Add(a);
		Assign(a);
		int work = PriorityAreaWork.CountFor(a, map);
		a.lastCount = work;
		Messages.Message((work > 0 ? "CF_PA_Added" : "CF_PA_AddedEmpty").Translate(PriorityAreaWork.ModeLabel(mode), a.assigned.Count, work), new TargetInfo(a.Center, map), MessageTypeDefOf.SilentInput, historical: false);
	}

	public int RemoveAt(IEnumerable<IntVec3> cells)
	{
		var set = new HashSet<IntVec3>(cells);
		int n = areas.RemoveAll(a => set.Any(a.Contains));
		return n;
	}

	public override void MapComponentTick()
	{
		if (areas.Count == 0 || Find.TickManager.TicksGame % 120 != 37)
			return;
		try
		{
			Update();
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] priority area update failed: " + ex.Message);
		}
	}

	private static bool Usable(Pawn p, Map map)
	{
		return p != null && !p.Dead && !p.Downed && p.Spawned && p.Map == map && p.IsColonistPlayerControlled
			&& !p.InMentalState && !p.Drafted && p.workSettings != null && p.workSettings.EverWork;
	}

	private void Update()
	{
		if (!Enabled || Hidden)
			return;
		int now = Find.TickManager.TicksGame;
		for (int i = areas.Count - 1; i >= 0; i--)
		{
			PriorityArea a = areas[i];
			// 2.52.3: a mental break or being carried no longer removes someone for good – only death, leaving or no longer ours
			a.assigned.RemoveAll(p => p == null || p.Dead || p.Destroyed || p.MapHeld != map || p.Faction != Faction.OfPlayer);
			if (PausedFor(a))
			{
				// waiting (wrong style, enemies …) does not count as "nobody worked here"
				a.lastWorkTick = now;
				continue;
			}
			bool busyInside = false;
			foreach (Pawn p in a.assigned)
				if (p.CurJob != null && PriorityAreaWork.JobInArea(p.CurJob, a))
				{
					busyInside = true;
					a.lastWorkTick = now;
				}
			// 2.49.3: nobody did anything inside for two days (work that keeps coming back, like
			// filth or repairs, or unreachable work) – the area ends instead of holding colonists forever
			if (!a.manual && now - a.lastWorkTick > 120000)
			{
				areas.RemoveAt(i);
				Messages.Message("CF_PA_Stale".Translate(PriorityAreaWork.ModeLabel(a.mode)), new TargetInfo(a.Center, map), MessageTypeDefOf.NeutralEvent, historical: false);
				continue;
			}
			// finished only when nothing of the chosen kind is left inside
			int work = PriorityAreaWork.CountFor(a, map);
			a.lastCount = work;
			if (work > 0 || busyInside)
			{
				if (work > 0)
					a.hadWork = true;
				a.emptyChecks = 0;
			}
			else if (a.hadWork && ++a.emptyChecks >= 3)
			{
				areas.RemoveAt(i);
				Messages.Message("CF_PA_Done".Translate(PriorityAreaWork.ModeLabel(a.mode)), new TargetInfo(a.Center, map), MessageTypeDefOf.PositiveEvent, historical: false);
				continue;
			}
			Assign(a);
		}
	}

	private static void Interrupt(Pawn p, PriorityArea a)
	{
		// drop ordinary work at once (not forced orders, not needs like eating)
		Job cur = p.CurJob;
		if (cur != null && cur.workGiverDef != null && !cur.playerForced && !PriorityAreaWork.JobInArea(cur, a))
			p.jobs.EndCurrentJob(JobCondition.InterruptForced);
	}

	private bool Takeable(Pawn p, PriorityArea a)
	{
		PriorityArea cur = AreaOf(p);
		if (cur == null)
			return true;
		if (cur == a || cur.manual)
			return false;
		// only from an older area, or from one that has nothing left for now
		return cur.createdTick < a.createdTick || cur.lastCount == 0 || PausedFor(cur);
	}

	private void Assign(PriorityArea a)
	{
		if (a.manual)
			return;
		int want = a.workers <= 0 ? int.MaxValue : a.workers;
		if (a.assigned.Count >= want)
			return;
		IntVec3 center = a.Center;
		// 2.49.3: a newer area may take colonists from an older automatic one – before, everyone
		// stayed stuck in old areas and a new area got nobody
		var free = map.mapPawns.FreeColonistsSpawned
			.Where(p => Usable(p, map) && Takeable(p, a) && PriorityAreaWork.CanDoAny(p, a.mode))
			.Where(p => p.Awake() && !HealthAIUtility.ShouldSeekMedicalRest(p))
			.OrderBy(p => p.Position.DistanceToSquared(center))
			.ToList();
		foreach (Pawn p in free)
		{
			if (a.assigned.Count >= want)
				break;
			AreaOf(p)?.assigned.Remove(p);
			a.assigned.Add(p);
			if (a.interrupt)
				Interrupt(p, a);
		}
	}

	public override void MapComponentUpdate()
	{
		if (areas.Count == 0 || Find.CurrentMap != map || !Enabled || Hidden)
			return;
		for (int i = 0; i < areas.Count; i++)
			GenDraw.DrawFieldEdges(areas[i].Cells, PausedFor(areas[i]) ? new Color(1f, 0.55f, 0.2f, 0.6f) : new Color(0.3f, 0.85f, 1f, 0.75f));
	}

	// 2.35.4: an orange (waiting) area says why
	public override void MapComponentOnGUI()
	{
		if (areas.Count == 0 || Find.CurrentMap != map || !Enabled || Hidden)
			return;
		try
		{
			for (int i = 0; i < areas.Count; i++)
			{
				string why = PauseReason(areas[i]);
				if (why == null)
					continue;
				Vector2 pos = areas[i].Center.ToVector3Shifted().MapToUIPosition();
				GenMapUI.DrawThingLabel(pos, why, new Color(1f, 0.7f, 0.35f));
			}
		}
		catch
		{
		}
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref areas, "cfPriorityAreas", LookMode.Deep);
		Scribe_Values.Look(ref nextId, "cfPriorityAreaNext", 1);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
			areas ??= new List<PriorityArea>();
	}
}

public static class PriorityAreaWork
{
	private static readonly Dictionary<PriorityAreaMode, string[]> Types = new Dictionary<PriorityAreaMode, string[]>
	{
		// 2.35.1: "everything" also means rescue, doctors, firefighting, wardens, animals and hunting
		{ PriorityAreaMode.All, new[] { "Firefighter", "Doctor", "Rescuing", "SAR_FieldRescue", "Construction", "Mining", "PlantCutting", "Growing", "Hauling", "Cleaning", "BasicWorker", "Warden", "Handling", "Hunting" } },
		{ PriorityAreaMode.Emergency, new[] { "Firefighter", "Doctor", "Rescuing", "SAR_FieldRescue" } },
		{ PriorityAreaMode.Build, new[] { "Construction" } },
		{ PriorityAreaMode.Mine, new[] { "Mining" } },
		{ PriorityAreaMode.Plants, new[] { "PlantCutting", "Growing" } },
		{ PriorityAreaMode.Haul, new[] { "Hauling" } },
		{ PriorityAreaMode.Clean, new[] { "Cleaning" } }
	};

	private static readonly Dictionary<PriorityAreaMode, List<WorkGiverDef>> giverCache = new Dictionary<PriorityAreaMode, List<WorkGiverDef>>();

	public static string ModeLabel(PriorityAreaMode m) => ("CF_PA_Mode_" + m).Translate();

	private static readonly Dictionary<PriorityAreaMode, List<WorkGiverDef>> emergencyCache = new Dictionary<PriorityAreaMode, List<WorkGiverDef>>();

	public static bool HasEmergency(PriorityAreaMode m) => m == PriorityAreaMode.All || m == PriorityAreaMode.Emergency;

	// 2.35.1: emergency givers (firefighting, tending, rescue) are included –
	// in the emergency pass only those, in the normal pass all of them
	private static List<WorkGiverDef> Givers(PriorityAreaMode m, bool emergencyOnly = false)
	{
		var dict = emergencyOnly ? emergencyCache : giverCache;
		if (dict.TryGetValue(m, out var list))
			return list;
		var names = new HashSet<string>(Types[m]);
		list = DefDatabase<WorkGiverDef>.AllDefsListForReading
			.Where(g => g.workType != null && names.Contains(g.workType.defName) && g.Worker is WorkGiver_Scanner && (!emergencyOnly || g.emergency))
			.OrderByDescending(g => g.emergency)
			.ThenByDescending(g => g.workType.naturalPriority)
			.ThenByDescending(g => g.priorityInType)
			.ToList();
		dict[m] = list;
		return list;
	}

	// what is still waiting inside the area: [build, mine, plants, haul, clean, other]
	public static int[] CountWork(PriorityArea a, Map map)
	{
		var n = new int[7];
		// 2.35.1: [6] = fires and hurt / downed people and animals in the area
		foreach (Thing f in map.listerThings.ThingsOfDef(ThingDefOf.Fire))
			if (a.Contains(f.Position))
				n[6]++;
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
		{
			if (!a.Contains(p.Position) || p.Dead || p.health == null)
				continue;
			bool ours = p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony || (p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer));
			if (ours && ((p.Downed && !p.InBed() && !p.DevelopmentalStage.Baby()) || p.health.HasHediffsNeedingTend()))   // 2.52.3: babies/patients in bed are not work
				n[6]++;
		}
		foreach (Designation d in map.designationManager.AllDesignations)
		{
			IntVec3 c = d.target.HasThing ? (d.target.Thing.Spawned ? d.target.Thing.Position : IntVec3.Invalid) : d.target.Cell;
			if (!c.IsValid || !a.Contains(c))
				continue;
			string dn = d.def.defName;
			if (dn == "Mine" || dn == "MineVein")
				n[1]++;
			else if (dn == "CutPlant" || dn == "HarvestPlant" || dn == "ExtractTree")
				n[2]++;
			else if (dn == "Deconstruct" || dn == "Uninstall" || dn.StartsWith("Smooth") || dn.StartsWith("Remove"))
				n[0]++;
			else if (dn == "Haul")
				n[3]++;
			else if (dn == "Strip" || dn == "Flick" || dn == "Open")
				n[5]++;
		}
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame))
			if (t.Faction == Faction.OfPlayer && a.Contains(t.Position))
				n[0]++;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint))
			if (t.Faction == Faction.OfPlayer && a.Contains(t.Position))
				n[0]++;
		foreach (Building b in map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer))
			if (a.Contains(b.Position) && map.areaManager.Home[b.Position])
				n[0]++;
		foreach (IntVec3 c in a.Cells)
		{
			if (map.zoneManager.ZoneAt(c) is Zone_Growing z)
			{
				Plant pl = c.GetPlant(map);
				if (pl != null && pl.HarvestableNow && pl.def == z.GetPlantDefToGrow() && z.allowCut)
					n[2]++;
			}
		}
		int checkedHaul = 0;
		// a colonist as the carrier – storage mods patch this search and expect one
		Pawn carrier = map.mapPawns.FreeColonistsSpawned.FirstOrDefault();
		foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
		{
			if (t == null || !t.Spawned || !a.Contains(t.Position) || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (checkedHaul++ > 40)
			{
				n[3]++;
				continue;
			}
			// only things that really have somewhere to go
			try
			{
				if (carrier != null && StoreUtility.TryFindBestBetterStoreCellFor(t, carrier, map, StoreUtility.CurrentStoragePriorityOf(t), Faction.OfPlayer, out _, needAccurateResult: false))
					n[3]++;
			}
			catch { }
		}
		foreach (Thing f in map.listerFilthInHomeArea.FilthInHomeArea)
			if (a.Contains(f.Position))
				n[4]++;
		return n;
	}

	public static int CountFor(PriorityArea a, Map map)
	{
		int[] n = CountWork(a, map);
		switch (a.mode)
		{
		case PriorityAreaMode.Build: return n[0];
		case PriorityAreaMode.Mine: return n[1];
		case PriorityAreaMode.Plants: return n[2];
		case PriorityAreaMode.Haul: return n[3];
		case PriorityAreaMode.Clean: return n[4];
		case PriorityAreaMode.Emergency: return n[6];
		default: return n[0] + n[1] + n[2] + n[3] + n[4] + n[5] + n[6];
		}
	}

	public static bool CanDoAny(Pawn p, PriorityAreaMode m)
	{
		foreach (string n in Types[m])
		{
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(n);
			if (w != null && !p.WorkTypeIsDisabled(w))
				return true;
		}
		return false;
	}

	public static bool JobInArea(Job job, PriorityArea a)
	{
		if (job == null)
			return false;
		return TargetIn(job.targetA, a) || TargetIn(job.targetB, a) || (job.targetQueueA != null && job.targetQueueA.Any(t => TargetIn(t, a)));
	}

	private static bool TargetIn(LocalTargetInfo t, PriorityArea a)
	{
		if (!t.IsValid)
			return false;
		return a.Contains(t.Cell);
	}

	public static Job TryFind(Pawn pawn, PriorityArea a, bool emergencyOnly = false)
	{
		Map map = pawn.Map;
		foreach (WorkGiverDef g in Givers(a.mode, emergencyOnly))
		{
			if (pawn.WorkTypeIsDisabled(g.workType) || pawn.WorkTagIsDisabled(g.workTags))
				continue;
			// 2.52.3: only work switched on in the Work tab (before: e.g. surgery by someone with Doctor off)
			if (!emergencyOnly && !pawn.workSettings.WorkIsActive(g.workType))
				continue;
			var scanner = (WorkGiver_Scanner)g.Worker;
			try
			{
				if (scanner.MissingRequiredCapacity(pawn) != null || scanner.ShouldSkip(pawn))
					continue;
				Job j = FromThings(pawn, scanner, a, map) ?? FromCells(pawn, scanner, a);
				if (j != null)
				{
					j.workGiverDef = g;
					return j;
				}
			}
			catch (Exception ex)
			{
				if (Prefs.DevMode)
					MLog.WarnOnce("[Steward] priority area: " + g.defName + " failed: " + ex, 0x1a7e0000 ^ g.index);
			}
		}
		return null;
	}

	private static Job FromThings(Pawn pawn, WorkGiver_Scanner scanner, PriorityArea a, Map map)
	{
		var found = new List<Thing>();
		IEnumerable<Thing> global = scanner.PotentialWorkThingsGlobal(pawn);
		if (global != null)
		{
			foreach (Thing t in global)
				if (t != null && t.Spawned && a.Contains(t.Position))
					found.Add(t);
		}
		else
		{
			ThingRequest req = scanner.PotentialWorkThingRequest;
			if (req.IsUndefined)
				return null;
			// 2.52.3: the shorter way – the game's list of matching things, or the area's cells (no slow Contains)
			List<Thing> all = map.listerThings.ThingsMatching(req);
			if (all.Count <= a.Cells.Count)
			{
				for (int i = 0; i < all.Count; i++)
					if (all[i].Spawned && a.Contains(all[i].Position))
						found.Add(all[i]);
			}
			else
			{
				var seen = new HashSet<Thing>();
				foreach (IntVec3 c in a.Cells)
				{
					List<Thing> at = map.thingGrid.ThingsListAtFast(c);
					for (int i = 0; i < at.Count; i++)
						if (req.Accepts(at[i]) && seen.Add(at[i]))
							found.Add(at[i]);
				}
			}
		}
		if (found.Count == 0)
			return null;
		IntVec3 from = pawn.Position;
		found.Sort((x, y) => x.Position.DistanceToSquared(from).CompareTo(y.Position.DistanceToSquared(from)));
		int tries = 0;
		foreach (Thing t in found)
		{
			if (tries++ > 25)
				break;
			if (t.IsForbidden(pawn) || !pawn.CanReach(t, scanner.PathEndMode, Danger.Deadly))
				continue;
			if (!scanner.HasJobOnThing(pawn, t))
				continue;
			Job j = scanner.JobOnThing(pawn, t);
			if (j != null)
				return j;
		}
		return null;
	}

	private static Job FromCells(Pawn pawn, WorkGiver_Scanner scanner, PriorityArea a)
	{
		IEnumerable<IntVec3> global = scanner.PotentialWorkCellsGlobal(pawn);
		if (global == null)
			return null;
		IntVec3 from = pawn.Position;
		var cells = global.Where(a.Contains).OrderBy(c => c.DistanceToSquared(from)).Take(25).ToList();
		foreach (IntVec3 c in cells)
		{
			if (c.IsForbidden(pawn) || !pawn.CanReach(c, scanner.PathEndMode, Danger.Deadly))
				continue;
			if (!scanner.HasJobOnCell(pawn, c))
				continue;
			Job j = scanner.JobOnCell(pawn, c);
			if (j != null)
				return j;
		}
		return null;
	}
}

// When an assigned colonist looks for work, work inside its area comes first.
[HarmonyPatch(typeof(JobGiver_Work), nameof(JobGiver_Work.TryIssueJobPackage))]
public static class PriorityAreaWorkPatch
{
	public static bool Prefix(JobGiver_Work __instance, Pawn pawn, ref ThinkResult __result)
	{
		try
		{
			if (!PriorityAreaComp.Enabled || pawn?.Map == null)
				return true;
			PriorityAreaComp comp = PriorityAreaComp.For(pawn.Map);
			if (comp == null || comp.areas.Count == 0)
				return true;
			PriorityArea a = comp.AreaOf(pawn);
			if (a == null)
				return true;
			// own area can be worked now (has work, right style, no danger)
			bool ownOk = a.lastCount != 0 && !comp.PausedFor(a);
			// 2.35.1: the emergency pass (fire, tending, rescue) also looks in the area first
			if (__instance.emergency)
			{
				if (!ownOk || !PriorityAreaWork.HasEmergency(a.mode))
					return true;
				Job ej = PriorityAreaWork.TryFind(pawn, a, emergencyOnly: true);
				if (ej == null)
					return true;
				a.lastWorkTick = Find.TickManager.TicksGame;
				__result = new ThinkResult(ej, __instance, ej.workGiverDef?.tagToGive);
				return false;
			}
			int now = Find.TickManager.TicksGame;
			if (a.idleUntil.TryGetValue(pawn.thingIDNumber, out int until) && now < until)
				return true;
			Job j = ownOk ? PriorityAreaWork.TryFind(pawn, a) : null;
			PriorityArea used = a;
			// 2.49.3: nothing this colonist can do in its own area (done, waiting for its style,
			// out of reach, no material) – it helps in another working area instead
			for (int i = comp.areas.Count - 1; i >= 0 && j == null; i--)
			{
				PriorityArea o = comp.areas[i];
				if (o == a || o.lastCount == 0 || comp.PausedFor(o))
					continue;
				j = PriorityAreaWork.TryFind(pawn, o);
				if (j != null)
					used = o;
			}
			if (j == null)
			{
				// nothing for this colonist right now – ordinary work, look again in a while
				a.idleUntil[pawn.thingIDNumber] = now + 300;
				return true;
			}
			a.idleUntil.Remove(pawn.thingIDNumber);
			used.lastWorkTick = now;
			__result = new ThinkResult(j, __instance, j.workGiverDef?.tagToGive);
			return false;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] priority area job failed: " + ex, 0x1a7e1234);
			return true;
		}
	}
}

[StaticConstructorOnStartup]
public static class PriorityAreaTex
{
	public static readonly Texture2D Add =
		ContentFinder<Texture2D>.Get("UI/Designators/HomeAreaOn", reportFailure: false) ?? BaseContent.WhiteTex;
	public static readonly Texture2D Remove =
		ContentFinder<Texture2D>.Get("UI/Designators/HomeAreaOff", reportFailure: false) ?? BaseContent.WhiteTex;
}

public class Designator_PriorityArea : Designator
{
	public Designator_PriorityArea()
	{
		defaultLabel = "CF_PA_Label".Translate();
		defaultDesc = "CF_PA_Desc".Translate() + "\n\n" + "CF_PA_OnlyBalanced".Translate();
		icon = PriorityAreaTex.Add;
		defaultIconColor = new Color(0.55f, 0.9f, 1f);
		soundDragSustain = SoundDefOf.Designate_DragStandard;
		soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
		soundSucceeded = SoundDefOf.Designate_ZoneAdd;
		useMouseIcon = true;
	}

	// 2.35.4: always shown (Orders and Zones); outside Balanced greyed out with the reason
	public override bool Visible => PriorityAreaComp.Enabled && base.Visible;

	public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
	{
		bool ok = PriorityAreaComp.StyleAllows(Find.CurrentMap);
		disabled = !ok;
		disabledReason = ok ? null : (string)"CF_PA_OnlyBalanced".Translate();
		return base.GizmoOnGUI(topLeft, maxWidth, parms);
	}

	public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

	public override bool DragDrawMeasurements => true;

	public override AcceptanceReport CanDesignateCell(IntVec3 c) => c.InBounds(Map);

	public override void DesignateSingleCell(IntVec3 c) => DesignateMultiCell(new[] { c });

	public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
	{
		var list = cells.ToList();
		if (list.Count == 0)
			return;
		CellRect r = CellRect.FromLimits(list.Min(c => c.x), list.Min(c => c.z), list.Max(c => c.x), list.Max(c => c.z));
		Find.WindowStack.Add(new Dialog_PriorityArea(Map, r));
	}

	public override void SelectedUpdate()
	{
		GenUI.RenderMouseoverBracket();
	}
}

// 2.22.2: after dragging – what to do, now or later, and who
public class Dialog_PriorityArea : Window
{
	private readonly Map map;
	private readonly CellRect rect;
	private readonly PriorityArea probe;
	private PriorityAreaMode mode = PriorityAreaMode.All;
	private bool now = true;
	private bool byHand;
	private int workers;
	private readonly HashSet<Pawn> chosen = new HashSet<Pawn>();
	private Vector2 scroll;
	private int[] counts;

	public override Vector2 InitialSize => new Vector2(620f, 640f);

	public Dialog_PriorityArea(Map map, CellRect rect)
	{
		this.map = map;
		this.rect = rect.ClipInsideMap(map);
		probe = new PriorityArea { minX = this.rect.minX, minZ = this.rect.minZ, maxX = this.rect.maxX, maxZ = this.rect.maxZ };
		workers = Mathf.Max(0, ForemanMod.Settings.priorityAreaWorkers);
		doCloseX = true;
		absorbInputAroundWindow = true;
		closeOnClickedOutside = false;
		forcePause = true;
		try
		{
			counts = PriorityAreaWork.CountWork(probe, map);
		}
		catch
		{
			counts = new int[7];
		}
		// suggestion: the nearest colonists who can do something here
		IntVec3 c = probe.Center;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(p => PriorityAreaWork.CanDoAny(p, mode)).OrderBy(p => p.Position.DistanceToSquared(c)).Take(Mathf.Max(1, workers == 0 ? 3 : workers)))
			chosen.Add(p);
	}

	public override void DoWindowContents(Rect inRect)
	{
		float y = inRect.y;
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 34f), "CF_PA_DlgTitle".Translate(rect.Width, rect.Height));
		Text.Font = GameFont.Small;
		y += 36f;
		GUI.color = new Color(0.75f, 0.75f, 0.75f);
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "CF_PA_DlgWork".Translate(counts[0], counts[1], counts[2], counts[3], counts[4]));
		GUI.color = Color.white;
		y += 30f;

		// what
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "CF_PA_DlgWhat".Translate());
		y += 26f;
		var modes = (PriorityAreaMode[])Enum.GetValues(typeof(PriorityAreaMode));
		float bw = (inRect.width - (modes.Length - 1) * 4f) / modes.Length;
		for (int i = 0; i < modes.Length; i++)
		{
			Rect b = new Rect(inRect.x + i * (bw + 4f), y, bw, 28f);
			GUI.color = mode == modes[i] ? new Color(0.55f, 1f, 0.55f) : Color.white;
			if (Widgets.ButtonText(b, PriorityAreaWork.ModeLabel(modes[i]).CapitalizeFirst()))
				mode = modes[i];
		}
		GUI.color = Color.white;
		y += 36f;

		// when
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "CF_PA_DlgWhen".Translate());
		y += 26f;
		if (Widgets.RadioButtonLabeled(new Rect(inRect.x, y, inRect.width, 26f), "CF_PA_DlgNow".Translate(), now))
			now = true;
		y += 26f;
		if (Widgets.RadioButtonLabeled(new Rect(inRect.x, y, inRect.width, 26f), "CF_PA_DlgLater".Translate(), !now))
			now = false;
		y += 34f;

		// who
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "CF_PA_DlgWho".Translate());
		y += 26f;
		string autoLabel = workers <= 0 ? (string)"CF_PA_DlgAutoAll".Translate() : (string)"CF_PA_DlgAuto".Translate(workers);
		Rect autoRow = new Rect(inRect.x, y, inRect.width, 26f);
		if (Widgets.RadioButtonLabeled(new Rect(autoRow.x, autoRow.y, autoRow.width - 90f, 26f), autoLabel, !byHand))
			byHand = false;
		if (!byHand)
		{
			if (Widgets.ButtonText(new Rect(autoRow.xMax - 82f, autoRow.y, 38f, 24f), "−"))
				workers = Mathf.Max(0, workers - 1);
			if (Widgets.ButtonText(new Rect(autoRow.xMax - 40f, autoRow.y, 38f, 24f), "+"))
				workers = Mathf.Min(20, workers + 1);
		}
		y += 26f;
		if (Widgets.RadioButtonLabeled(new Rect(inRect.x, y, inRect.width, 26f), "CF_PA_DlgHand".Translate(chosen.Count), byHand))
			byHand = true;
		y += 30f;

		Rect listOuter = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 44f);
		List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.Position.DistanceToSquared(probe.Center)).ToList();
		Rect view = new Rect(0f, 0f, listOuter.width - 18f, pawns.Count * 28f);
		Widgets.DrawMenuSection(listOuter);
		Widgets.BeginScrollView(listOuter.ContractedBy(2f), ref scroll, view);
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			Rect row = new Rect(0f, i * 28f, view.width, 28f);
			if (i % 2 == 1)
				Widgets.DrawLightHighlight(row);
			bool can = PriorityAreaWork.CanDoAny(p, mode);
			bool on = chosen.Contains(p);
			GUI.color = !byHand ? new Color(1f, 1f, 1f, 0.45f) : (can ? Color.white : new Color(1f, 0.5f, 0.5f));
			Widgets.Label(new Rect(row.x + 6f, row.y + 4f, 170f, 24f), p.LabelShortCap);
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(row.x + 180f, row.y + 6f, row.width - 220f, 22f), Skills(p));
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			if (byHand && can)
			{
				bool v = on;
				Widgets.Checkbox(new Vector2(row.xMax - 28f, row.y + 2f), ref v);
				if (v != on)
				{
					if (v) chosen.Add(p);
					else chosen.Remove(p);
				}
			}
		}
		Widgets.EndScrollView();

		Rect ok = new Rect(inRect.xMax - 160f, inRect.yMax - 36f, 160f, 34f);
		Rect cancel = new Rect(inRect.xMax - 330f, inRect.yMax - 36f, 160f, 34f);
		if (Widgets.ButtonText(cancel, "CancelButton".Translate()))
			Close();
		bool valid = !byHand || chosen.Count > 0;
		if (Widgets.ButtonText(ok, "CF_PA_DlgCreate".Translate(), active: valid) && valid)
		{
			PriorityAreaComp.For(map)?.Add(rect, mode, byHand ? chosen.Where(p => p.Spawned && !p.Dead).ToList() : null, workers, now);
			Close();
		}
	}

	private static string Skills(Pawn p)
	{
		if (p.skills == null)
			return string.Empty;
		string S(SkillDef d) => p.skills.GetSkill(d).TotallyDisabled ? "–" : p.skills.GetSkill(d).Level.ToString();
		return "CF_PA_DlgSkills".Translate(S(SkillDefOf.Construction), S(SkillDefOf.Mining), S(SkillDefOf.Plants));
	}
}

public class Designator_PriorityAreaClear : Designator
{
	public Designator_PriorityAreaClear()
	{
		defaultLabel = "CF_PA_ClearLabel".Translate();
		defaultDesc = "CF_PA_ClearDesc".Translate() + "\n\n" + "CF_PA_OnlyBalanced".Translate();
		icon = PriorityAreaTex.Remove;
		defaultIconColor = new Color(0.55f, 0.9f, 1f);
		soundDragSustain = SoundDefOf.Designate_DragStandard;
		soundDragChanged = SoundDefOf.Designate_DragStandard_Changed;
		soundSucceeded = SoundDefOf.Designate_ZoneDelete;
		useMouseIcon = true;
	}

	// 2.35.4: always shown (Orders and Zones); outside Balanced greyed out with the reason
	public override bool Visible => PriorityAreaComp.Enabled && base.Visible;

	public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
	{
		bool ok = PriorityAreaComp.StyleAllows(Find.CurrentMap);
		disabled = !ok;
		disabledReason = ok ? null : (string)"CF_PA_OnlyBalanced".Translate();
		return base.GizmoOnGUI(topLeft, maxWidth, parms);
	}

	public override DrawStyleCategoryDef DrawStyleCategory => DrawStyleCategoryDefOf.FilledRectangle;

	public override AcceptanceReport CanDesignateCell(IntVec3 c)
	{
		var comp = PriorityAreaComp.For(Map);
		return c.InBounds(Map) && comp != null && comp.areas.Any(a => a.Contains(c));
	}

	public override void DesignateSingleCell(IntVec3 c) => DesignateMultiCell(new[] { c });

	public override void DesignateMultiCell(IEnumerable<IntVec3> cells)
	{
		var comp = PriorityAreaComp.For(Map);
		int n = comp?.RemoveAt(cells) ?? 0;
		if (n > 0)
			Messages.Message("CF_PA_Removed".Translate(n), MessageTypeDefOf.SilentInput, historical: false);
	}

	public override void SelectedUpdate()
	{
		GenUI.RenderMouseoverBracket();
	}
}
