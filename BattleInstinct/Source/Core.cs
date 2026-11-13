using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using RimWorld.Planet;

namespace BattleInstinct;

public enum Stance { Aggressive, Defensive, Hold }

// per-pawn choices (saved with the game)
public class PawnData : IExposable
{
	public bool auto = true;
	public int stance = 1;
	public HashSet<string> abilityOff = new HashSet<string>();
	public HashSet<string> abilityOn = new HashSet<string>();   // abilities that are off by default, switched on by the player
	public IntVec3 anchor = IntVec3.Invalid;
	// runtime only
	public Job ourJob;
	public Thing target;
	public int lastCast = -9999;
	public int lastText = -9999;
	public int retreatUntil;
	public bool autoDrafted;   // 0.1.1: drafted by Steward – Roles (soldier) or the alarm, not by the player
	public int lastKite = -9999;
	public int v;              // 0.2.0: data version

	public Stance Stance => (Stance)Mathf.Clamp(stance, 0, 2);

	public void ExposeData()
	{
		Scribe_Values.Look(ref auto, "auto", true);
		Scribe_Values.Look(ref stance, "stance", 1);
		Scribe_Values.Look(ref anchor, "anchor", IntVec3.Invalid);
		Scribe_Values.Look(ref autoDrafted, "autoDrafted", false);
		Scribe_Values.Look(ref v, "v", 0);
		// 0.2.0: attack is the new default – pawns that still had the old default (defend) switch to it
		if (Scribe.mode == LoadSaveMode.PostLoadInit && v < 1)
		{
			if (stance == 1) stance = 0;
			v = 1;
		}
		Scribe_Collections.Look(ref abilityOff, "abilityOff", LookMode.Value);
		Scribe_Collections.Look(ref abilityOn, "abilityOn", LookMode.Value);
		if (abilityOff == null) abilityOff = new HashSet<string>();
		if (abilityOn == null) abilityOn = new HashSet<string>();
	}
}

public class BIComp : GameComponent
{
	private Dictionary<int, PawnData> data = new Dictionary<int, PawnData>();
	private List<int> tmpKeys;
	private List<PawnData> tmpVals;
	internal static bool issuing;
	private static bool errorLogged;
	private static BIComp cached;
	private static Game cachedGame;

	public BIComp(Game game) { Alarm.Reset(); }

	public static BIComp Get()
	{
		Game g = Current.Game;
		if (g == null) return null;
		if (cachedGame != g || cached == null)
		{
			cached = g.GetComponent<BIComp>();
			cachedGame = g;
		}
		return cached;
	}

	public static PawnData For(Pawn p)
	{
		BIComp c = Get();
		if (c == null || p == null) return null;
		if (!c.data.TryGetValue(p.thingIDNumber, out PawnData d))
		{
			var s = BattleInstinctMod.Settings;
			d = new PawnData { auto = s.defaultOn, stance = s.defaultStance, v = 1 };
			c.data[p.thingIDNumber] = d;
		}
		return d;
	}

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			// drop pawns that are gone
			var alive = new HashSet<int>();
			foreach (Map m in Find.Maps)
				foreach (Pawn p in m.mapPawns.AllPawns)
					alive.Add(p.thingIDNumber);
			foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
				alive.Add(p.thingIDNumber);
			foreach (Caravan car in Find.WorldObjects.Caravans)
				foreach (Pawn p in car.PawnsListForReading)
					alive.Add(p.thingIDNumber);
			foreach (int k in data.Keys.ToList())
				if (!alive.Contains(k))
					data.Remove(k);
		}
		Scribe_Collections.Look(ref data, "pawns", LookMode.Value, LookMode.Deep, ref tmpKeys, ref tmpVals);
		if (data == null) data = new Dictionary<int, PawnData>();
	}

	public override void GameComponentTick()
	{
		var s = BattleInstinctMod.Settings;
		if (!s.enabled) return;
		int now = Find.TickManager.TicksGame;
		int interval = Mathf.Clamp(s.interval, 10, 120);
		List<Map> maps = Find.Maps;
		for (int mi = 0; mi < maps.Count; mi++)
		{
			Map map = maps[mi];
			if (now % 60 == 17)
			{
				try { Alarm.Tick(map, s, now); }
				catch (Exception e)
				{
					if (!errorLogged) { errorLogged = true; Log.Error("[Battle Instinct] alarm: " + e); }
				}
			}
			Battlefield field = null;
			IReadOnlyList<Pawn> pawns = map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer);
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				if ((now + p.thingIDNumber) % interval != 0) continue;
				if (p.drafter == null || !p.Drafted || p.Downed || p.Dead || p.InMentalState) continue;
				if (!p.IsColonistPlayerControlled && !(s.mechs && p.IsColonyMechPlayerControlled)) continue;
				PawnData d = For(p);
				if (d == null || !d.auto) continue;
				try
				{
					if (field == null) field = new Battlefield(map);
					if (field.enemies.Count == 0) break;
					Brain.Think(p, d, field);
				}
				catch (Exception e)
				{
					if (!errorLogged)
					{
						errorLogged = true;
						Log.Error("[Battle Instinct] " + e);
					}
				}
			}
		}
	}

	internal IEnumerable<KeyValuePair<int, PawnData>> All => data;
}

// what the colony sees on one map this moment
public class Battlefield
{
	public readonly Map map;
	public readonly List<Thing> enemies = new List<Thing>();
	public readonly List<Pawn> friends = new List<Pawn>();
	public readonly Dictionary<Thing, int> focus = new Dictionary<Thing, int>();

	public Battlefield(Map map)
	{
		this.map = map;
		foreach (IAttackTarget t in map.attackTargetsCache.TargetsHostileToColony)
		{
			Thing th = t.Thing;
			if (th == null || !th.Spawned || th.Destroyed) continue;
			if (th.Position.Fogged(map)) continue;
			if (t.ThreatDisabled(null)) continue;
			if (th is Pawn ep && (ep.Downed || ep.IsPsychologicallyInvisible())) continue;
			enemies.Add(th);
		}
		if (enemies.Count == 0) return;
		foreach (Pawn p in map.mapPawns.AllPawnsSpawned)
			if (!p.Dead && !p.HostileTo(Faction.OfPlayer) && (p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.Faction != null || p.RaceProps.Humanlike))
				friends.Add(p);
		BIComp c = BIComp.Get();
		if (c != null)
			foreach (var kv in c.All)
			{
				Thing t = kv.Value.target;
				if (t != null && t.Spawned && kv.Value.ourJob != null)
					focus[t] = focus.TryGetValue(t, out int n) ? n + 1 : 1;
			}
	}
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
public static class Patch_Drafted
{
	public static void Postfix(Pawn_DraftController __instance, bool value)
	{
		Pawn p = __instance?.pawn;
		if (p == null || Current.ProgramState != ProgramState.Playing) return;
		PawnData d = BIComp.For(p);
		if (d == null) return;
		d.ourJob = null;
		d.target = null;
		if (value && p.Spawned) d.anchor = p.Position;
		bool byMod = StewardDrafting() || Alarm.drafting;
		d.autoDrafted = value && byMod;
		if (!value && !byMod)
			Alarm.PlayerUndrafted(p);
	}

	// Steward – Roles drafts soldiers when enemies come; it sets SoldierDuty.busy while doing so
	private static System.Reflection.FieldInfo busy;
	private static bool looked;

	private static bool StewardDrafting()
	{
		if (!looked)
		{
			looked = true;
			busy = AccessTools.TypeByName("StewardRoles.SoldierDuty") is Type t ? AccessTools.Field(t, "busy") : null;
		}
		try { return busy != null && (bool)busy.GetValue(null); }
		catch { return false; }
	}
}

// a move order from the player becomes the new place to defend
[HarmonyPatch(typeof(Pawn_JobTracker), nameof(Pawn_JobTracker.TryTakeOrderedJob))]
public static class Patch_OrderedJob
{
	private static readonly AccessTools.FieldRef<Pawn_JobTracker, Pawn> PawnRef = AccessTools.FieldRefAccess<Pawn_JobTracker, Pawn>("pawn");

	public static void Prefix(Pawn_JobTracker __instance, Job job)
	{
		if (BIComp.issuing || job == null) return;
		Pawn p = PawnRef(__instance);
		if (p == null || !p.Drafted) return;
		PawnData d = BIComp.For(p);
		if (d == null) return;
		d.ourJob = null;
		d.autoDrafted = false;   // the player took over
		if (job.def == JobDefOf.Goto && job.targetA.IsValid)
			d.anchor = job.targetA.Cell;
	}
}
