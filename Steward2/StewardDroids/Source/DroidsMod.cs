using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ColonyForeman;

// ===========================================================================
// Steward – Droids (add-on of the Steward core)
// Everything that is a machine and works for the colony: Biotech mechanoids (vanilla, DLC and
// modded), androids (Vanilla Races Expanded – Android) and other robot races.
// - Guard duty: combat mechs patrol the edge of the home area, its outer doors and the most
//   valuable rooms, a share of them sweeps the whole map; when enemies come close the nearest
//   ones intercept and the rest hold the side of the base the enemies come from. Afterwards
//   they go back to their posts. Recharging is staggered so the guard never leaves all at once.
// - Care: badly damaged mechs leave the fight, damaged mechs are repaired by the mechanitor,
//   warnings before energy, reactor power or neutroamine run out.
// - Androids that never sleep work around the clock; droids take the firefighting first.
// - "Droids" tab: every machine with its mode, energy, upkeep, health and job.
// Nothing here needs the other mods – they are found by name; a missing one is just skipped.
// ===========================================================================

public enum DroidMode
{
	Auto,
	Guard,
	Work,
	Rest
}

public enum DroidKind
{
	None,
	Mech,
	Android,
	Robot
}

public sealed class DroidsSettings : ModSettings
{
	public bool guard = true;            // guard duty at all
	public bool autoGuardCombat = true;  // combat mechs in "Auto" mode guard
	public float sweepShare = 0.25f;     // share of guards that sweep the whole map
	public int alertRadius = 40;         // enemies this close to the base wake the guard
	public bool guardRooms = true;       // the most valuable rooms get a post too
	public float rechargeAt = 0.35f;     // a guard goes to recharge below this (one at a time)
	public bool staggerCharging = true;
	public bool retreatHurt = true;
	public float retreatAt = 0.4f;
	public bool autoRepair = true;       // damaged mechs get "repair automatically"
	public bool schedule24 = true;       // droids that never sleep work around the clock
	public bool firefightFirst = true;   // droids take firefighting first
	public bool warnings = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref guard, "guard", true);
		Scribe_Values.Look(ref autoGuardCombat, "autoGuardCombat", true);
		Scribe_Values.Look(ref sweepShare, "sweepShare", 0.25f);
		Scribe_Values.Look(ref alertRadius, "alertRadius", 40);
		Scribe_Values.Look(ref guardRooms, "guardRooms", true);
		Scribe_Values.Look(ref rechargeAt, "rechargeAt", 0.35f);
		Scribe_Values.Look(ref staggerCharging, "staggerCharging", true);
		Scribe_Values.Look(ref retreatHurt, "retreatHurt", true);
		Scribe_Values.Look(ref retreatAt, "retreatAt", 0.4f);
		Scribe_Values.Look(ref autoRepair, "autoRepair", true);
		Scribe_Values.Look(ref schedule24, "schedule24", true);
		Scribe_Values.Look(ref firefightFirst, "firefightFirst", true);
		Scribe_Values.Look(ref warnings, "warnings", true);
	}
}

public sealed class DroidsMod : Mod
{
	public const string Version = "0.1.0";
	public static DroidsSettings S;

	public DroidsMod(ModContentPack content) : base(content)
	{
		S = GetSettings<DroidsSettings>();
		try
		{
			var h = new Harmony("misak.steward.droids");
			h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)), postfix: new HarmonyMethod(typeof(DroidGizmo), nameof(DroidGizmo.Postfix)));
			MethodInfo charge = AccessTools.Method(typeof(JobGiver_GetEnergy_Charger), "TryGiveJob");
			if (charge != null)
				h.Patch(charge, prefix: new HarmonyMethod(typeof(GuardBrain), nameof(GuardBrain.ChargePrefix)));
		}
		catch (Exception e) { Log.Warning("[Steward Droids] patches: " + e.Message); }
		try
		{
			StewardApi.Register(new DroidsStep());
			StewardApi.AddSettingsPage(new SettingsPage { label = "SD_Name".Translate(), draw = DrawSettings, write = () => S.Write() });
		}
		catch (Exception e) { Log.Warning("[Steward Droids] steward hooks: " + e.Message); }
		MLog.Info("[Steward] Droids v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "SD_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect) => DrawSettings(inRect);

	private static void DrawSettings(Rect r)
	{
		var l = new Listing_Standard();
		l.Begin(r);
		l.Label("SD_Intro".Translate());
		l.GapLine();
		l.CheckboxLabeled("SD_SetGuard".Translate(), ref S.guard, "SD_SetGuardTip".Translate());
		l.CheckboxLabeled("SD_SetAutoGuard".Translate(), ref S.autoGuardCombat, "SD_SetAutoGuardTip".Translate());
		l.Label("SD_SetSweep".Translate(Mathf.RoundToInt(S.sweepShare * 100f)));
		S.sweepShare = l.Slider(S.sweepShare, 0f, 0.6f);
		l.Label("SD_SetRadius".Translate(S.alertRadius));
		S.alertRadius = Mathf.RoundToInt(l.Slider(S.alertRadius, 15f, 120f));
		l.CheckboxLabeled("SD_SetRooms".Translate(), ref S.guardRooms, "SD_SetRoomsTip".Translate());
		l.CheckboxLabeled("SD_SetStagger".Translate(), ref S.staggerCharging, "SD_SetStaggerTip".Translate());
		l.Label("SD_SetRecharge".Translate(Mathf.RoundToInt(S.rechargeAt * 100f)));
		S.rechargeAt = l.Slider(S.rechargeAt, 0.15f, 0.8f);
		l.CheckboxLabeled("SD_SetRetreat".Translate(), ref S.retreatHurt, "SD_SetRetreatTip".Translate());
		if (S.retreatHurt)
		{
			l.Label("SD_SetRetreatAt".Translate(Mathf.RoundToInt(S.retreatAt * 100f)));
			S.retreatAt = l.Slider(S.retreatAt, 0.15f, 0.7f);
		}
		l.GapLine();
		l.CheckboxLabeled("SD_SetRepair".Translate(), ref S.autoRepair, "SD_SetRepairTip".Translate());
		l.CheckboxLabeled("SD_SetSchedule".Translate(), ref S.schedule24, "SD_SetScheduleTip".Translate());
		l.CheckboxLabeled("SD_SetFire".Translate(), ref S.firefightFirst, "SD_SetFireTip".Translate());
		l.CheckboxLabeled("SD_SetWarn".Translate(), ref S.warnings, "SD_SetWarnTip".Translate());
		l.End();
	}
}

// ---------------------------------------------------------------------------
// what kind of machine a pawn is – by the game's own flags first, other mods by name
// ---------------------------------------------------------------------------
public static class DroidUtil
{
	private static bool vreLooked;
	private static MethodInfo vreIsAndroid, vreIsAwakened;
	private static NeedDef reactorNeed, memoryNeed;
	private static HediffDef neutroLoss;
	private static ThingDef neutroamine;
	private static readonly Dictionary<ThingDef, bool> robotRace = new Dictionary<ThingDef, bool>();
	private static readonly Dictionary<int, bool> androidCache = new Dictionary<int, bool>();

	private static void LookVre()
	{
		if (vreLooked)
			return;
		vreLooked = true;
		try
		{
			Type u = AccessTools.TypeByName("VREAndroids.Utils");
			if (u != null)
			{
				vreIsAndroid = u.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "IsAndroid" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Pawn));
				vreIsAwakened = u.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(m => m.Name == "IsAwakened" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Pawn));
			}
		}
		catch { }
		reactorNeed = DefDatabase<NeedDef>.GetNamedSilentFail("VREA_ReactorPower");
		memoryNeed = DefDatabase<NeedDef>.GetNamedSilentFail("VREA_MemorySpace");
		neutroLoss = DefDatabase<HediffDef>.GetNamedSilentFail("VREA_NeutroLoss");
		neutroamine = DefDatabase<ThingDef>.GetNamedSilentFail("Neutroamine");
	}

	public static bool VreActive
	{
		get { LookVre(); return vreIsAndroid != null || reactorNeed != null; }
	}

	public static DroidKind Kind(Pawn p)
	{
		if (p == null || p.RaceProps == null)
			return DroidKind.None;
		if (p.RaceProps.IsMechanoid)
			return DroidKind.Mech;
		if (androidCache.TryGetValue(p.thingIDNumber, out bool isA))
		{
			if (isA)
				return DroidKind.Android;
		}
		else
		{
			isA = p.RaceProps.Humanlike && IsVreAndroid(p);
			androidCache[p.thingIDNumber] = isA;
			if (isA)
				return DroidKind.Android;
		}
		if (IsRobotRace(p.def))
			return DroidKind.Robot;
		return DroidKind.None;
	}

	public static bool IsVreAndroid(Pawn p)
	{
		LookVre();
		if (vreIsAndroid != null)
		{
			try { return (bool)vreIsAndroid.Invoke(null, new object[] { p }); }
			catch { }
		}
		return reactorNeed != null && p.needs?.TryGetNeed(reactorNeed) != null;
	}

	public static bool? Awakened(Pawn p)
	{
		LookVre();
		if (vreIsAwakened == null)
			return null;
		try { return (bool)vreIsAwakened.Invoke(null, new object[] { p }); }
		catch { return null; }
	}

	// other robot races (Android Tiers, Misc. Robots …): mechanical flesh, or no food need and robot-ish names
	private static bool IsRobotRace(ThingDef d)
	{
		if (d?.race == null)
			return false;
		if (robotRace.TryGetValue(d, out bool v))
			return v;
		v = false;
		try
		{
			RaceProperties r = d.race;
			string flesh = r.FleshType?.defName ?? "";
			string n = (d.defName + " " + flesh).ToLowerInvariant();
			bool named = n.Contains("android") || n.Contains("robot") || n.Contains("droid") || n.Contains("synth") || n.Contains("mechanical") || n.Contains("cyborg");
			bool mechFlesh = r.FleshType == FleshTypeDefOf.Mechanoid || flesh.ToLowerInvariant().Contains("mech") || flesh.ToLowerInvariant().Contains("android") || flesh.ToLowerInvariant().Contains("robot");
			bool noFood = !r.EatsFood || (!r.needsRest && r.foodType == FoodTypeFlags.None);
			v = mechFlesh || (named && noFood);
		}
		catch { }
		robotRace[d] = v;
		return v;
	}

	public static bool IsCombatMech(Pawn p) => p.RaceProps.IsMechanoid && !p.RaceProps.IsWorkMech;

	// player's machines on this map
	public static List<Pawn> On(Map map)
	{
		var list = new List<Pawn>();
		if (map?.mapPawns == null)
			return list;
		foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
		{
			if (p.Dead || p.IsPrisoner)
				continue;
			if (Kind(p) != DroidKind.None)
				list.Add(p);
		}
		return list;
	}

	public static float? Energy(Pawn p)
	{
		Need_MechEnergy e = p.needs?.energy;
		if (e != null)
			return e.CurLevelPercentage;
		LookVre();
		if (reactorNeed != null)
		{
			Need n = p.needs?.TryGetNeed(reactorNeed);
			if (n != null)
				return n.CurLevelPercentage;
		}
		return null;
	}

	public static float? Memory(Pawn p)
	{
		LookVre();
		if (memoryNeed == null)
			return null;
		Need n = p.needs?.TryGetNeed(memoryNeed);
		return n?.CurLevelPercentage;
	}

	public static float NeutroLoss(Pawn p)
	{
		LookVre();
		if (neutroLoss == null)
			return 0f;
		return p.health?.hediffSet?.GetFirstHediffOfDef(neutroLoss)?.Severity ?? 0f;
	}

	public static int NeutroamineOnMap(Map map)
	{
		LookVre();
		if (neutroamine == null || map == null)
			return -1;
		int n = 0;
		foreach (Thing t in map.listerThings.ThingsOfDef(neutroamine))
			if (!t.IsForbidden(Faction.OfPlayer))
				n += t.stackCount;
		return n;
	}

	public static float Health(Pawn p) => p.health?.summaryHealth?.SummaryHealthPercent ?? 1f;
}

// ---------------------------------------------------------------------------
// the mode of every machine, saved with the game
// ---------------------------------------------------------------------------
public sealed class DroidsGame : GameComponent
{
	public static DroidsGame Instance;
	public Dictionary<int, DroidMode> modes = new Dictionary<int, DroidMode>();
	public HashSet<int> scheduled = new HashSet<int>();

	public DroidsGame(Game game)
	{
		Instance = this;
	}

	public static DroidMode Raw(Pawn p) => Instance != null && p != null && Instance.modes.TryGetValue(p.thingIDNumber, out DroidMode m) ? m : DroidMode.Auto;

	// what the machine really does when its mode is "Auto"
	public static DroidMode Of(Pawn p)
	{
		DroidMode m = Raw(p);
		if (m != DroidMode.Auto)
			return m;
		if (DroidsMod.S != null && DroidsMod.S.guard && DroidsMod.S.autoGuardCombat && DroidUtil.IsCombatMech(p))
			return DroidMode.Guard;
		return DroidMode.Work;
	}

	public static void Set(Pawn p, DroidMode m)
	{
		if (Instance == null || p == null)
			return;
		if (m == DroidMode.Auto)
			Instance.modes.Remove(p.thingIDNumber);
		else
			Instance.modes[p.thingIDNumber] = m;
		GuardBrain.Forget(p);
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref modes, "modes", LookMode.Value, LookMode.Value);
		List<int> sc = Scribe.mode == LoadSaveMode.Saving ? scheduled.ToList() : null;
		Scribe_Collections.Look(ref sc, "scheduled", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.LoadingVars)
			scheduled = new HashSet<int>(sc ?? new List<int>());
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			modes ??= new Dictionary<int, DroidMode>();
			scheduled ??= new HashSet<int>();
		}
	}

	public override void GameComponentTick()
	{
		int now = Find.TickManager.TicksGame;
		if (now % GuardBrain.Every != 17 || DroidsMod.S == null)
			return;
		bool care = now - lastCare >= 480;
		foreach (Map map in Find.Maps)
		{
			try { GuardBrain.Tick(map); }
			catch (Exception e) { MLog.WarnOnce("[Steward Droids] guard: " + e, 0x5d0001); }
			if (care)
			{
				try { DroidCare.Tick(map); }
				catch (Exception e) { MLog.WarnOnce("[Steward Droids] care: " + e, 0x5d0002); }
			}
		}
		if (care)
			lastCare = now;
	}

	private int lastCare = -99999;
}

// ---------------------------------------------------------------------------
// the mode button on every machine
// ---------------------------------------------------------------------------
public static class DroidGizmo
{
	private static Texture2D icon;

	public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
	{
		try
		{
			Pawn p = __instance;
			if (p == null || p.Faction != Faction.OfPlayer || !p.Spawned || DroidUtil.Kind(p) == DroidKind.None || DroidsGame.Instance == null)
				return;
			__result = __result.Concat(new[] { Make(p) });
		}
		catch { }
	}

	private static Gizmo Make(Pawn p)
	{
		icon ??= ContentFinder<Texture2D>.Get("UI/Commands/Draft", false) ?? BaseContent.BadTex;
		DroidMode raw = DroidsGame.Raw(p), eff = DroidsGame.Of(p);
		string label = "SD_Mode_" + raw;
		return new Command_Action
		{
			defaultLabel = "SD_GizmoLabel".Translate(label.Translate()),
			defaultDesc = "SD_GizmoDesc".Translate(("SD_Mode_" + eff).Translate(), GuardBrain.Status(p)),
			icon = icon,
			action = () =>
			{
				var opts = new List<FloatMenuOption>();
				foreach (DroidMode m in Enum.GetValues(typeof(DroidMode)))
				{
					DroidMode mm = m;
					opts.Add(new FloatMenuOption(("SD_Mode_" + m).Translate() + " – " + ("SD_ModeDesc_" + m).Translate(), () =>
					{
						foreach (Pawn s in Find.Selector.SelectedPawns)
							if (s.Faction == Faction.OfPlayer && DroidUtil.Kind(s) != DroidKind.None)
								DroidsGame.Set(s, mm);
					}));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
		};
	}
}

// ---------------------------------------------------------------------------
// guard duty
// ---------------------------------------------------------------------------
public static class GuardBrain
{
	public const int Every = 120;

	private sealed class MapPosts
	{
		public List<IntVec3> posts = new List<IntVec3>();
		public IntVec3 center = IntVec3.Invalid;
		public int builtAt = -99999;
		public int homeCount = -1;
	}

	private sealed class State
	{
		public int post = -1;
		public bool sweeper;
		public string status = "";
		public int alertUntil;
		public bool watched = true;               // stood watch at the current spot – time to move on
		public IntVec3 spot = IntVec3.Invalid;    // where it is heading now
	}

	private static readonly Dictionary<Map, MapPosts> posts = new Dictionary<Map, MapPosts>();
	private static readonly Dictionary<int, State> states = new Dictionary<int, State>();
	private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Job, object> ours = new System.Runtime.CompilerServices.ConditionalWeakTable<Job, object>();
	private static readonly object mark = new object();
	private static readonly HashSet<JobDef> idleJobs = new HashSet<JobDef>();

	public static void Forget(Pawn p)
	{
		if (p != null)
			states.Remove(p.thingIDNumber);
	}

	private static State St(Pawn p)
	{
		if (!states.TryGetValue(p.thingIDNumber, out State s))
			states[p.thingIDNumber] = s = new State();
		return s;
	}

	public static string Status(Pawn p) => states.TryGetValue(p.thingIDNumber, out State s) && !s.status.NullOrEmpty() ? s.status : "—";

	public static int PostCount(Map map) => posts.TryGetValue(map, out MapPosts mp) ? mp.posts.Count : 0;

	private static bool Ours(Job j) => j != null && ours.TryGetValue(j, out _);

	private static Job Mine(Job j)
	{
		ours.Add(j, mark);
		return j;
	}

	private static bool Replaceable(Pawn p)
	{
		Job j = p.CurJob;
		if (j == null || Ours(j))
			return true;
		if (idleJobs.Count == 0)
			foreach (JobDef d in new[] { JobDefOf.Wait_Wander, JobDefOf.GotoWander, JobDefOf.Wait, JobDefOf.Wait_MaintainPosture })
				if (d != null)
					idleJobs.Add(d);
		return idleJobs.Contains(j.def);
	}

	// may this machine get guard orders right now
	private static bool Usable(Pawn p, State s)
	{
		if (p.Dead || p.Downed || !p.Spawned || p.Drafted || p.InMentalState || p.GetLord() != null)
		{
			s.status = p.Drafted ? "SD_StDrafted".Translate() : "SD_StBusy".Translate();
			return false;
		}
		if (p.RaceProps.IsMechanoid && ModsConfig.BiotechActive)
		{
			if (p.IsGestating())
				return false;
			MechWorkModeDef wm = p.GetMechWorkMode();
			if (wm != null && wm != MechWorkModeDefOf.Work)
			{
				s.status = "SD_StPausedMode".Translate(wm.LabelCap);
				return false;
			}
		}
		return true;
	}

	private static bool Charging(Pawn p) => p.CurJobDef == JobDefOf.MechCharge;

	// ---- the posts: edge of the home area, outer doors, valuable rooms ----
	private static MapPosts Posts(Map map)
	{
		if (!posts.TryGetValue(map, out MapPosts mp))
			posts[map] = mp = new MapPosts();
		Area_Home home = map.areaManager?.Home;
		int hc = home?.TrueCount ?? 0;
		int now = Find.TickManager.TicksGame;
		if (now - mp.builtAt < 2500 && hc == mp.homeCount)
			return mp;
		mp.builtAt = now;
		mp.homeCount = hc;
		mp.posts.Clear();
		try { Build(map, home, mp); }
		catch (Exception e) { MLog.WarnOnce("[Steward Droids] posts: " + e.Message, 0x5d0003); }
		return mp;
	}

	private static void Build(Map map, Area_Home home, MapPosts mp)
	{
		var edge = new List<IntVec3>();
		long sx = 0, sz = 0;
		int n = 0;
		if (home != null && home.TrueCount > 0)
		{
			foreach (IntVec3 c in home.ActiveCells)
			{
				sx += c.x; sz += c.z; n++;
				bool border = false;
				for (int i = 0; i < 4; i++)
				{
					IntVec3 o = c + GenAdj.CardinalDirections[i];
					if (!o.InBounds(map) || !home[o])
					{
						border = true;
						break;
					}
				}
				if (border && c.Standable(map) && !c.Fogged(map))
					edge.Add(c);
			}
		}
		mp.center = n > 0 ? new IntVec3((int)(sx / n), 0, (int)(sz / n)) : map.Center;
		IntVec3 ctr = mp.center;
		// perimeter order: by angle around the middle of the base, then keep cells at least 12 apart
		edge.Sort((a, b) => Angle(a, ctr).CompareTo(Angle(b, ctr)));
		const int spacing = 12;
		foreach (IntVec3 c in edge)
		{
			if (mp.posts.Count >= 40)
				break;
			if (mp.posts.Any(q => q.DistanceToSquared(c) < spacing * spacing))
				continue;
			mp.posts.Add(c);
		}
		// outer doors: the outside cell in front of each
		int doors = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (doors >= 12 || !(b is Building_Door))
				continue;
			foreach (IntVec3 c in GenAdj.CellsAdjacentCardinal(b))
			{
				if (!c.InBounds(map) || !c.Standable(map) || c.Fogged(map))
					continue;
				Room r = c.GetRoom(map);
				if (r != null && r.PsychologicallyOutdoors)
				{
					if (!mp.posts.Any(q => q.DistanceToSquared(c) < 16))
					{
						mp.posts.Add(c);
						doors++;
					}
					break;
				}
			}
		}
		// the most valuable rooms
		if (DroidsMod.S.guardRooms)
		{
			var rooms = new List<KeyValuePair<Room, float>>();
			foreach (Room r in map.regionGrid.AllRooms)
			{
				if (r == null || r.PsychologicallyOutdoors || r.IsDoorway || r.CellCount < 6 || r.CellCount > 600)
					continue;
				IntVec3 any = r.Cells.FirstOrDefault();
				if (home != null && any.IsValid && !home[any])
					continue;
				float w = 0f;
				try { w = r.GetStat(RoomStatDefOf.Wealth); } catch { }
				rooms.Add(new KeyValuePair<Room, float>(r, w));
			}
			foreach (var kv in rooms.OrderByDescending(k => k.Value).Take(2))
			{
				IntVec3 c = kv.Key.Cells.Where(x => x.Standable(map)).OrderBy(x => x.DistanceToSquared(ctr)).FirstOrDefault();
				if (c.IsValid && c != default(IntVec3))
					mp.posts.Add(c);
			}
		}
		if (mp.posts.Count == 0 && ctr.InBounds(map))
			mp.posts.Add(ctr);
	}

	private static float Angle(IntVec3 c, IntVec3 ctr) => Mathf.Atan2(c.z - ctr.z, c.x - ctr.x);

	// ---- every 120 ticks ----
	public static void Tick(Map map)
	{
		if (!DroidsMod.S.guard || DroidsGame.Instance == null)
			return;
		var guards = new List<Pawn>();
		foreach (Pawn p in DroidUtil.On(map))
			if (DroidsGame.Of(p) == DroidMode.Guard)
				guards.Add(p);
			else if (DroidsGame.Of(p) == DroidMode.Rest)
				RestOrder(p);
		if (guards.Count == 0)
			return;
		MapPosts mp = Posts(map);
		if (mp.posts.Count == 0)
			return;
		// who sweeps the whole map: a stable share of the guards
		int sweepers = Mathf.FloorToInt(guards.Count * DroidsMod.S.sweepShare);
		guards.Sort((a, b) => a.thingIDNumber.CompareTo(b.thingIDNumber));
		for (int i = 0; i < guards.Count; i++)
		{
			State s = St(guards[i]);
			s.sweeper = i < sweepers;
			if (s.post < 0)
				s.post = (i * mp.posts.Count / Math.Max(1, guards.Count)) % mp.posts.Count;
		}
		List<Pawn> threats = Threats(map, mp, guards);
		int now = Find.TickManager.TicksGame;
		if (threats.Count > 0)
			Defend(map, mp, guards, threats, now);
		else
			foreach (Pawn g in guards)
				Patrol(map, mp, g, guards, now);
	}

	// enemies that matter: close to the base, or seen by a guard
	private static List<Pawn> Threats(Map map, MapPosts mp, List<Pawn> guards)
	{
		var list = new List<Pawn>();
		int r2 = DroidsMod.S.alertRadius * DroidsMod.S.alertRadius;
		foreach (IAttackTarget t in map.attackTargetsCache.TargetsHostileToColony)
		{
			if (!(t.Thing is Pawn e) || !e.Spawned || e.Downed || e.Fogged() || !GenHostility.IsActiveThreatToPlayer(t))
				continue;
			bool near = false;
			Area_Home home = map.areaManager?.Home;
			if (home != null && home[e.Position])
				near = true;
			for (int i = 0; !near && i < mp.posts.Count; i++)
				if (mp.posts[i].DistanceToSquared(e.Position) <= r2)
					near = true;
			for (int i = 0; !near && i < guards.Count; i++)
				if (guards[i].Position.DistanceToSquared(e.Position) <= 30 * 30 && GenSight.LineOfSight(guards[i].Position, e.Position, map, true))
					near = true;
			if (near)
				list.Add(e);
		}
		return list;
	}

	private static void Defend(Map map, MapPosts mp, List<Pawn> guards, List<Pawn> threats, int now)
	{
		// the enemy closest to the base sets the side
		Pawn main = threats.OrderBy(e => mp.posts.Min(q => q.DistanceToSquared(e.Position))).First();
		var ready = new List<Pawn>();
		foreach (Pawn g in guards)
		{
			State s = St(g);
			if (!Usable(g, s))
				continue;
			s.alertUntil = now + 900;
			s.watched = false;
			if (!s.sweeper && s.post >= 0 && s.post < mp.posts.Count)
				s.spot = mp.posts[s.post];
			if (Hurt(g))
			{
				Retreat(map, mp, g, main, s);
				continue;
			}
			if (Charging(g) && (DroidUtil.Energy(g) ?? 1f) < 0.5f)
			{
				s.status = "SD_StCharging".Translate();
				continue;
			}
			if (!Charging(g) && !Replaceable(g) && !Ours(g.CurJob) && !IsCombatJob(g.CurJob))
				continue;
			ready.Add(g);
		}
		if (ready.Count == 0)
			return;
		ready.Sort((a, b) => a.Position.DistanceToSquared(main.Position).CompareTo(b.Position.DistanceToSquared(main.Position)));
		int interceptors = Mathf.Max(1, Mathf.CeilToInt(ready.Count * 0.6f));
		IntVec3 hold = mp.posts.OrderBy(q => q.DistanceToSquared(main.Position)).First();
		for (int i = 0; i < ready.Count; i++)
		{
			Pawn g = ready[i];
			State s = St(g);
			if (i < interceptors)
			{
				Pawn target = threats.OrderBy(e => e.Position.DistanceToSquared(g.Position)).First();
				Intercept(map, g, target, s);
			}
			else
			{
				s.status = "SD_StHold".Translate();
				GoAndWatch(g, hold, LocomotionUrgency.Sprint, 400);
			}
		}
	}

	private static bool IsCombatJob(Job j) => j != null && (j.def == JobDefOf.AttackMelee || j.def == JobDefOf.AttackStatic || j.def == JobDefOf.Wait_Combat);

	private static void Intercept(Map map, Pawn g, Pawn target, State s)
	{
		s.status = "SD_StIntercept".Translate(target.LabelShortCap);
		Verb v = g.CurrentEffectiveVerb;
		bool ranged = v != null && !v.verbProps.IsMeleeAttack;
		if (!ranged)
		{
			if (g.CurJobDef == JobDefOf.AttackMelee && g.CurJob.targetA.Thing == target)
				return;
			if (!g.CanReach(target, PathEndMode.Touch, Danger.Deadly))
				return;
			Job j = JobMaker.MakeJob(JobDefOf.AttackMelee, target);
			j.expiryInterval = 600;
			j.checkOverrideOnExpire = true;
			j.killIncappedTarget = false;
			Start(g, Mine(j));
			return;
		}
		float range = Mathf.Max(4f, v.verbProps.range * 0.8f);
		float d = g.Position.DistanceTo(target.Position);
		if (d <= range && GenSight.LineOfSight(g.Position, target.Position, map, true))
		{
			if (g.CurJobDef != JobDefOf.Wait_Combat && !IsCombatJob(g.CurJob))
				Watch(g, 300);
			return;
		}
		// a cell on the line from the enemy towards the guard, in shooting range
		Vector3 dir = (g.Position - target.Position).ToVector3();
		dir.y = 0f;
		if (dir.sqrMagnitude < 0.01f)
			dir = Vector3.forward;
		IntVec3 want = (target.Position.ToVector3Shifted() + dir.normalized * Mathf.Min(range, d)).ToIntVec3();
		if (!want.InBounds(map))
			want = target.Position;
		IntVec3 cell = RCellFinder.BestOrderedGotoDestNear(want, g);
		if (!cell.IsValid)
			return;
		Job go = JobMaker.MakeJob(JobDefOf.Goto, cell);
		go.locomotionUrgency = LocomotionUrgency.Sprint;
		go.expiryInterval = 240;
		go.checkOverrideOnExpire = true;
		Start(g, Mine(go));
	}

	private static bool Hurt(Pawn g) => DroidsMod.S.retreatHurt && DroidUtil.Health(g) < DroidsMod.S.retreatAt;

	private static void Retreat(Map map, MapPosts mp, Pawn g, Pawn enemy, State s)
	{
		s.status = "SD_StRetreat".Translate();
		IntVec3 far = mp.center.IsValid ? mp.center : g.Position;
		if (enemy != null && mp.posts.Count > 1)
			far = mp.posts.OrderByDescending(q => q.DistanceToSquared(enemy.Position)).First();
		if (g.CurJobDef == JobDefOf.Goto && Ours(g.CurJob) && g.CurJob.targetA.Cell == far)
			return;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, far);
		j.locomotionUrgency = LocomotionUrgency.Sprint;
		j.expiryInterval = 900;
		Start(g, Mine(j));
	}

	private static void Patrol(Map map, MapPosts mp, Pawn g, List<Pawn> guards, int now)
	{
		State s = St(g);
		if (!Usable(g, s))
			return;
		if (Charging(g))
		{
			s.status = "SD_StCharging".Translate();
			return;
		}
		if (!Replaceable(g))
			return;
		// recharge – one guard at a time, unless it is nearly empty
		float? en = DroidUtil.Energy(g);
		if (en.HasValue && g.needs?.energy != null && en.Value < DroidsMod.S.rechargeAt)
		{
			bool another = DroidsMod.S.staggerCharging && guards.Any(o => o != g && Charging(o));
			if (!another || en.Value < 0.15f)
			{
				Building_MechCharger ch = JobGiver_GetEnergy_Charger.GetClosestCharger(g, g, false);
				if (ch != null)
				{
					Job c = JobMaker.MakeJob(JobDefOf.MechCharge, ch);
					c.overrideFacing = Rot4.South;
					s.status = "SD_StCharging".Translate();
					Start(g, c);
					return;
				}
			}
		}
		if (Ours(g.CurJob) && g.CurJobDef != JobDefOf.Wait_Combat)
			return;   // still walking to its post
		if (g.CurJobDef == JobDefOf.Wait_Combat && Ours(g.CurJob))
			return;   // on watch until the job runs out
		// arrived at the spot it was heading to: stand watch there first, then move on
		if (!s.watched && s.spot.IsValid && s.spot.InBounds(map))
		{
			if (g.Position.DistanceToSquared(s.spot) <= 4)
			{
				s.watched = true;
				Watch(g, s.sweeper ? 600 : 1500);
				return;
			}
			if (g.CanReach(s.spot, PathEndMode.OnCell, Danger.Deadly))
			{
				GoAndWatch(g, s.spot, LocomotionUrgency.Walk, s.sweeper ? 600 : 1500);
				return;
			}
		}
		IntVec3 dest;
		if (s.sweeper)
		{
			dest = SweepCell(map, mp, g);
			s.status = "SD_StSweep".Translate();
		}
		else
		{
			s.post = (s.post + 1) % mp.posts.Count;
			dest = mp.posts[s.post];
			s.status = "SD_StPatrol".Translate(s.post + 1, mp.posts.Count);
		}
		if (!dest.IsValid)
			return;
		s.spot = dest;
		s.watched = false;
		GoAndWatch(g, dest, LocomotionUrgency.Walk, s.sweeper ? 600 : 1500);
	}

	// walk to a cell, then stand watch there (shooting at anything that comes in sight)
	private static void GoAndWatch(Pawn g, IntVec3 dest, LocomotionUrgency speed, int watch)
	{
		if (!dest.InBounds(g.Map) || !g.CanReach(dest, PathEndMode.OnCell, Danger.Deadly))
			return;
		if (g.Position.DistanceToSquared(dest) <= 4)
		{
			if (g.CurJobDef == JobDefOf.Wait_Combat && Ours(g.CurJob))
				return;
			Watch(g, watch);
			return;
		}
		if (g.CurJobDef == JobDefOf.Goto && Ours(g.CurJob) && g.CurJob.targetA.Cell == dest)
			return;
		Job j = JobMaker.MakeJob(JobDefOf.Goto, dest);
		j.locomotionUrgency = speed;
		j.expiryInterval = 2500;
		j.checkOverrideOnExpire = true;
		Start(g, Mine(j));
	}

	private static void Watch(Pawn g, int ticks)
	{
		Job w = JobMaker.MakeJob(JobDefOf.Wait_Combat);
		w.expiryInterval = ticks;
		w.checkOverrideOnExpire = true;
		Start(g, Mine(w));
	}

	private static IntVec3 SweepCell(Map map, MapPosts mp, Pawn g)
	{
		IntVec3 best = IntVec3.Invalid;
		float bestD = -1f;
		for (int i = 0; i < 12; i++)
		{
			if (!CellFinder.TryFindRandomCell(map, c => c.Standable(map) && !c.Fogged(map) && !c.GetTerrain(map).IsWater, out IntVec3 c2))
				continue;
			float d = c2.DistanceToSquared(mp.center);
			if (d > bestD && g.CanReach(c2, PathEndMode.OnCell, Danger.Some))
			{
				bestD = d;
				best = c2;
			}
		}
		return best;
	}

	private static void Start(Pawn g, Job j)
	{
		try { g.jobs.StartJob(j, JobCondition.InterruptForced, null, false, true); }
		catch (Exception e) { MLog.WarnOnce("[Steward Droids] order for " + g.LabelShort + ": " + e.Message, g.thingIDNumber ^ 0x5d0010); }
	}

	// "Rest": go to a charger now (mechs); others are left to their own needs
	private static void RestOrder(Pawn p)
	{
		State s = St(p);
		s.status = "SD_StRest".Translate();
		if (p.needs?.energy == null || Charging(p) || p.Drafted || p.Downed || !Replaceable(p))
			return;
		if (p.needs.energy.CurLevelPercentage > 0.98f)
			return;
		Building_MechCharger ch = JobGiver_GetEnergy_Charger.GetClosestCharger(p, p, false);
		if (ch == null)
			return;
		Job c = JobMaker.MakeJob(JobDefOf.MechCharge, ch);
		c.overrideFacing = Rot4.South;
		Start(p, c);
	}

	// the game's own "go recharge": a guard waits while another guard is charging (unless nearly empty)
	public static bool ChargePrefix(Pawn pawn, ref Job __result)
	{
		try
		{
			if (DroidsMod.S == null || !DroidsMod.S.guard || !DroidsMod.S.staggerCharging || pawn?.Map == null || DroidsGame.Of(pawn) != DroidMode.Guard)
				return true;
			float? en = DroidUtil.Energy(pawn);
			if (!en.HasValue || en.Value < 0.15f)
				return true;
			foreach (Pawn o in pawn.Map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer))
				if (o != pawn && o.RaceProps.IsMechanoid && Charging(o) && DroidsGame.Of(o) == DroidMode.Guard)
				{
					__result = null;
					return false;
				}
		}
		catch { }
		return true;
	}
}

// ---------------------------------------------------------------------------
// care: repair, schedules, warnings (every 500 ticks)
// ---------------------------------------------------------------------------
public static class DroidCare
{
	public static readonly Dictionary<Map, List<string>> Problems = new Dictionary<Map, List<string>>();
	public static readonly Dictionary<Map, List<Pawn>> Culprits = new Dictionary<Map, List<Pawn>>();

	public static void Tick(Map map)
	{
		List<Pawn> all = DroidUtil.On(map);
		var issues = new List<string>();
		var who = new List<Pawn>();
		int androids = 0;
		foreach (Pawn p in all)
		{
			DroidKind k = DroidUtil.Kind(p);
			// damaged mechs: the mechanitor repairs them
			if (k == DroidKind.Mech && DroidsMod.S.autoRepair && DroidUtil.Health(p) < 0.999f)
			{
				CompMechRepairable rep = p.GetComp<CompMechRepairable>();
				if (rep != null && !rep.autoRepair)
					rep.autoRepair = true;
			}
			// never sleeps: work around the clock (once – your own changes stay)
			if (k != DroidKind.Mech && DroidsMod.S.schedule24 && p.timetable != null && p.needs?.rest == null && DroidsGame.Instance != null && !DroidsGame.Instance.scheduled.Contains(p.thingIDNumber))
			{
				for (int h = 0; h < 24; h++)
					p.timetable.SetAssignment(h, TimeAssignmentDefOf.Work);
				DroidsGame.Instance.scheduled.Add(p.thingIDNumber);
			}
			if (k == DroidKind.Android)
				androids++;
			if (!DroidsMod.S.warnings)
				continue;
			float? en = DroidUtil.Energy(p);
			if (k == DroidKind.Mech && en.HasValue && en.Value < 0.15f && p.CurJobDef != JobDefOf.MechCharge && JobGiver_GetEnergy_Charger.GetClosestCharger(p, p, false) == null)
			{
				issues.Add("SD_WarnMechEnergy".Translate(p.LabelShortCap));
				who.Add(p);
			}
			if (k == DroidKind.Android && en.HasValue && en.Value < 0.1f)
			{
				issues.Add("SD_WarnReactor".Translate(p.LabelShortCap));
				who.Add(p);
			}
		}
		if (DroidsMod.S.warnings && androids > 0)
		{
			int neutro = DroidUtil.NeutroamineOnMap(map);
			bool losing = all.Any(p => DroidUtil.NeutroLoss(p) > 0.2f);
			if (neutro >= 0 && (neutro < androids * 5 || (losing && neutro == 0)))
				issues.Add("SD_WarnNeutro".Translate(neutro, androids));
		}
		Problems[map] = issues;
		Culprits[map] = who;
	}
}

public sealed class Alert_DroidCare : Alert
{
	public Alert_DroidCare()
	{
		defaultLabel = "SD_AlertLabel".Translate();
		defaultPriority = AlertPriority.High;
	}

	public override TaggedString GetExplanation()
	{
		Map m = Find.CurrentMap;
		if (m == null || !DroidCare.Problems.TryGetValue(m, out var l) || l.Count == 0)
			return "";
		return "SD_AlertIntro".Translate() + "\n\n" + string.Join("\n", l.Select(x => "- " + x));
	}

	public override AlertReport GetReport()
	{
		if (DroidsMod.S == null || !DroidsMod.S.warnings)
			return false;
		Map m = Find.CurrentMap;
		if (m == null || !DroidCare.Problems.TryGetValue(m, out var l) || l.Count == 0)
			return false;
		if (DroidCare.Culprits.TryGetValue(m, out var who) && who.Count > 0)
			return AlertReport.CulpritsAre(who.Where(p => p.Spawned).ToList());
		return true;
	}
}

// ---------------------------------------------------------------------------
// plan step: droids take the firefighting first
// ---------------------------------------------------------------------------
public sealed class DroidsStep : IPlanStep
{
	public int Order => 860;
	public string Id => "droids";

	public void Apply(PlanContext ctx)
	{
		if (DroidsMod.S == null || !DroidsMod.S.firefightFirst)
			return;
		WorkTypeDef fire = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Firefighter");
		if (fire == null || !ctx.types.Contains(fire))
			return;
		int droids = 0;
		foreach (Pawn p in ctx.pawns)
			if (DroidUtil.Kind(p) != DroidKind.None && ctx.CanDo(p, fire))
				if (ctx.Set(p, fire, 1, "SD_ReasonFire", null, null, pin: true))
					droids++;
		if (droids < 2)
			return;
		// enough machines for the fires: people one step lower (only where nothing else decided it)
		foreach (Pawn p in ctx.pawns)
			if (DroidUtil.Kind(p) == DroidKind.None && ctx.Get(p, fire) == 1 && !ctx.Pinned(p, fire) && !ctx.Locked(p, fire))
				ctx.Set(p, fire, Math.Min(2, ctx.Levels), "SD_ReasonFireHuman", droids);
	}
}

// ---------------------------------------------------------------------------
// the Droids tab
// ---------------------------------------------------------------------------
public sealed class MainButtonWorker_Droids : MainButtonWorker_ToggleTab
{
	private static int lookedAt = -9999;
	private static bool any;

	public override bool Visible
	{
		get
		{
			if (!base.Visible || Find.CurrentMap == null)
				return false;
			int now = Find.TickManager.TicksGame;
			if (now - lookedAt > 600 || now < lookedAt)
			{
				lookedAt = now;
				any = DroidUtil.On(Find.CurrentMap).Count > 0;
			}
			return any;
		}
	}
}

public sealed class MainTabWindow_Droids : MainTabWindow
{
	private Vector2 scroll;

	public override Vector2 RequestedTabSize => new Vector2(1060f, 600f);

	public override void DoWindowContents(Rect inRect)
	{
		Map map = Find.CurrentMap;
		List<Pawn> list = DroidUtil.On(map);
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, 500f, 32f), "SD_TabTitle".Translate(list.Count));
		Text.Font = GameFont.Small;
		float bx = inRect.xMax;
		foreach (DroidMode m in new[] { DroidMode.Auto, DroidMode.Work, DroidMode.Guard })
		{
			bx -= 190f;
			DroidMode mm = m;
			if (Widgets.ButtonText(new Rect(bx, inRect.y, 182f, 28f), "SD_AllMechs".Translate(("SD_Mode_" + m).Translate())))
				foreach (Pawn p in list)
					if (DroidUtil.IsCombatMech(p))
						DroidsGame.Set(p, mm);
		}
		float y = inRect.y + 36f;
		GUI.color = Color.gray;
		Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "SD_TabInfo".Translate(GuardBrain.PostCount(map)));
		GUI.color = Color.white;
		y += 26f;
		float[] w = { 190f, 80f, 130f, 80f, 90f, 70f, 0f };
		string[] head = { "SD_ColName", "SD_ColKind", "SD_ColMode", "SD_ColEnergy", "SD_ColUpkeep", "SD_ColHealth", "SD_ColNow" };
		w[6] = inRect.width - 16f - w.Take(6).Sum();
		float x = inRect.x;
		Text.Font = GameFont.Tiny;
		for (int i = 0; i < head.Length; i++)
		{
			Widgets.Label(new Rect(x + 4f, y, w[i] - 4f, 20f), head[i].Translate());
			x += w[i];
		}
		Text.Font = GameFont.Small;
		y += 22f;
		Widgets.DrawLineHorizontal(inRect.x, y, inRect.width);
		float extraH = 90f;
		Rect outR = new Rect(inRect.x, y + 2f, inRect.width, inRect.yMax - y - 2f - extraH);
		Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(outR.height, list.Count * 30f));
		Widgets.BeginScrollView(outR, ref scroll, view);
		float ry = 0f;
		foreach (Pawn p in list.OrderBy(q => DroidUtil.Kind(q)).ThenBy(q => q.LabelShort))
		{
			Row(new Rect(0f, ry, view.width, 28f), p, w);
			ry += 30f;
		}
		Widgets.EndScrollView();
		DrawAdvice(new Rect(inRect.x, inRect.yMax - extraH + 4f, inRect.width, extraH - 4f), map, list);
	}

	private static void Row(Rect r, Pawn p, float[] w)
	{
		if (Mouse.IsOver(r))
			Widgets.DrawHighlight(r);
		float x = r.x;
		Rect c = new Rect(x + 4f, r.y + 3f, w[0] - 8f, 24f);
		if (Widgets.ButtonInvisible(c))
			CameraJumper.TryJumpAndSelect(p);
		Widgets.Label(c, p.LabelShortCap.Truncate(c.width));
		x += w[0];
		DroidKind k = DroidUtil.Kind(p);
		string kind = ("SD_Kind_" + k).Translate();
		if (k == DroidKind.Mech)
			kind = DroidUtil.IsCombatMech(p) ? "SD_KindCombat".Translate() : "SD_KindWorker".Translate();
		Widgets.Label(new Rect(x + 4f, r.y + 3f, w[1] - 8f, 24f), kind.Truncate(w[1] - 8f));
		x += w[1];
		DroidMode raw = DroidsGame.Raw(p), eff = DroidsGame.Of(p);
		string ml = raw == DroidMode.Auto ? "SD_Mode_Auto".Translate() + " → " + ("SD_Mode_" + eff).Translate() : ("SD_Mode_" + raw).Translate();
		if (Widgets.ButtonText(new Rect(x + 2f, r.y + 2f, w[2] - 6f, 24f), ml.Truncate(w[2] - 12f)))
		{
			var opts = new List<FloatMenuOption>();
			foreach (DroidMode m in Enum.GetValues(typeof(DroidMode)))
			{
				DroidMode mm = m;
				opts.Add(new FloatMenuOption(("SD_Mode_" + m).Translate() + " – " + ("SD_ModeDesc_" + m).Translate(), () => DroidsGame.Set(p, mm)));
			}
			Find.WindowStack.Add(new FloatMenu(opts));
		}
		x += w[2];
		float? en = DroidUtil.Energy(p);
		Widgets.Label(new Rect(x + 4f, r.y + 3f, w[3] - 8f, 24f), en.HasValue ? en.Value.ToStringPercent() : "—");
		x += w[3];
		string upkeep = "—";
		float? mem = DroidUtil.Memory(p);
		if (mem.HasValue)
			upkeep = "SD_Memory".Translate(mem.Value.ToStringPercent());
		float nl = DroidUtil.NeutroLoss(p);
		if (nl > 0.01f)
			upkeep += " · " + "SD_Neutro".Translate(nl.ToStringPercent());
		Rect ur = new Rect(x + 4f, r.y + 3f, w[4] - 8f, 24f);
		Widgets.Label(ur, upkeep.Truncate(ur.width));
		TooltipHandler.TipRegion(ur, upkeep);
		x += w[4];
		Widgets.Label(new Rect(x + 4f, r.y + 3f, w[5] - 8f, 24f), DroidUtil.Health(p).ToStringPercent());
		x += w[5];
		string now = p.CurJob?.GetReport(p)?.CapitalizeFirst() ?? "";
		if (eff == DroidMode.Guard || eff == DroidMode.Rest)
			now = GuardBrain.Status(p) + (now.NullOrEmpty() ? "" : " · " + now);
		bool? aw = k == DroidKind.Android ? DroidUtil.Awakened(p) : null;
		if (aw == true)
			now = "SD_Awakened".Translate() + " · " + now;
		else if (aw == false && (p.needs?.mood?.CurLevelPercentage ?? 1f) < 0.25f)
			now = "SD_MayAwaken".Translate() + " · " + now;
		if (p.RaceProps.IsMechanoid && ModsConfig.BiotechActive && p.GetOverseer() != null && !MechanitorUtility.InMechanitorCommandRange(p, p.Position))
			now = "SD_OutOfRange".Translate() + " · " + now;
		Rect nr = new Rect(x + 4f, r.y + 3f, w[6] - 8f, 24f);
		Widgets.Label(nr, now.Truncate(nr.width));
		TooltipHandler.TipRegion(nr, now);
	}

	// what the colony lacks: skills no colonist is good at → subroutine ideas for androids
	private static void DrawAdvice(Rect r, Map map, List<Pawn> droids)
	{
		Widgets.DrawLineHorizontal(r.x, r.y, r.width);
		var lines = new List<string>();
		if (DroidCare.Problems.TryGetValue(map, out var probs))
			lines.AddRange(probs);
		if (droids.Any(p => DroidUtil.Kind(p) == DroidKind.Android))
		{
			var weak = new List<string>();
			foreach (SkillDef sk in DefDatabase<SkillDef>.AllDefsListForReading)
			{
				int best = 0;
				foreach (Pawn c in map.mapPawns.FreeColonistsSpawned)
				{
					SkillRecord s = c.skills?.GetSkill(sk);
					if (s != null && !s.TotallyDisabled && s.Level > best)
						best = s.Level;
				}
				if (best < 6)
					weak.Add(sk.LabelCap);
			}
			if (weak.Count > 0)
				lines.Add("SD_Advice".Translate(string.Join(", ", weak)));
		}
		if (lines.Count == 0)
			lines.Add("SD_AllGood".Translate());
		Widgets.Label(new Rect(r.x + 4f, r.y + 4f, r.width - 8f, r.height - 4f), string.Join("\n", lines.Take(4)));
	}
}
