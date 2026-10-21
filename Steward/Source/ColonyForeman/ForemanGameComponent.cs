using System.Collections.Generic;
using Verse;

namespace ColonyForeman;

public sealed class ForemanGameComponent : GameComponent
{
	public HashSet<string> lockedPawns = new HashSet<string>();

	public HashSet<string> lockedWork = new HashSet<string>();

	public HashSet<string> lockedCells = new HashSet<string>();

	public HashSet<string> preferences = new HashSet<string>();

	public ForemanGameComponent(Game game)
	{
	}

	// 2.14.1: caches keyed by map are emptied when a game is loaded or
	// started – before, every load kept the old maps alive in memory.
	public override void FinalizeInit()
	{
		ClearCaches();   // 2.17
		CrisisRules.Reset();
		SmartBalance.Reset();
		WorkGiver_StewardHaulProducts.Reset();
		WorkGiver_StewardTidyHome.Reset();
		PickUpAndHaulCompat.Reset();            // 2.18
		MainTabWindow_Supplies.ResetOpenWindow(); // 2.18
	}

	public static ForemanGameComponent Get()
	{
		return (Current.Game == null) ? null : Current.Game.GetComponent<ForemanGameComponent>();
	}

	private static string Key(Pawn p, WorkTypeDef w)
	{
		return p.GetUniqueLoadID() + "|" + w.defName;
	}

	// 2.17: lookups without building strings (the work tab asks for every
	// cell at every GUI event). The saved string sets stay as they are; these
	// runtime caches are filled from them on demand and emptied on load and
	// whenever a lock or preference changes.
	private readonly Dictionary<int, bool> pawnLockCache = new Dictionary<int, bool>();
	private readonly Dictionary<long, bool> cellLockCache = new Dictionary<long, bool>();
	private readonly Dictionary<long, bool> preferCache = new Dictionary<long, bool>();

	private static long CellKey(Pawn p, WorkTypeDef w) => ((long)p.thingIDNumber << 16) | w.index;

	private void ClearCaches()
	{
		pawnLockCache.Clear();
		cellLockCache.Clear();
		preferCache.Clear();
	}

	public bool PawnLocked(Pawn p)
	{
		// 2.17: cached by thingIDNumber
		if (!pawnLockCache.TryGetValue(p.thingIDNumber, out bool v))
			pawnLockCache[p.thingIDNumber] = v = lockedPawns.Contains(p.GetUniqueLoadID());
		return v;
	}

	public bool WorkLocked(WorkTypeDef w)
	{
		return lockedWork.Contains(w.defName);
	}

	public bool CellLocked(Pawn p, WorkTypeDef w)
	{
		// 2.17: cached by (thingIDNumber, work type)
		long k = CellKey(p, w);
		if (!cellLockCache.TryGetValue(k, out bool v))
			cellLockCache[k] = v = lockedCells.Contains(Key(p, w));
		return v;
	}

	public bool AnyLock(Pawn p, WorkTypeDef w)
	{
		return PawnLocked(p) || WorkLocked(w) || CellLocked(p, w);
	}

	public bool Prefers(Pawn p, WorkTypeDef w)
	{
		// 2.17: cached like the locks
		long k = CellKey(p, w);
		if (!preferCache.TryGetValue(k, out bool v))
			preferCache[k] = v = preferences.Contains(Key(p, w));
		return v;
	}

	public void TogglePawn(Pawn p)
	{
		Toggle(lockedPawns, p.GetUniqueLoadID());
	}

	public void ToggleWork(WorkTypeDef w)
	{
		Toggle(lockedWork, w.defName);
	}

	public void ToggleCell(Pawn p, WorkTypeDef w)
	{
		Toggle(lockedCells, Key(p, w));
	}

	// 2.49.6: a priority you change by hand in the Work tab is locked at once,
	// so the steward never overwrites it. Middle click unlocks it again.
	public bool LockCell(Pawn p, WorkTypeDef w)
	{
		if (lockedCells.Add(Key(p, w)))
		{
			ClearCaches();
			return true;
		}
		return false;
	}

	public void TogglePreference(Pawn p, WorkTypeDef w)
	{
		Toggle(preferences, Key(p, w));
	}

	private void Toggle(HashSet<string> set, string key)
	{
		if (!set.Remove(key))
		{
			set.Add(key);
		}
		ClearCaches();   // 2.17
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref lockedPawns, "lockedPawns", LookMode.Value);
		Scribe_Collections.Look(ref lockedWork, "lockedWork", LookMode.Value);
		Scribe_Collections.Look(ref lockedCells, "lockedCells", LookMode.Value);
		Scribe_Collections.Look(ref preferences, "preferences", LookMode.Value);
		ClearCaches();   // 2.17: rebuilt from the loaded sets
		if (lockedPawns == null)
		{
			lockedPawns = new HashSet<string>();
		}
		if (lockedWork == null)
		{
			lockedWork = new HashSet<string>();
		}
		if (lockedCells == null)
		{
			lockedCells = new HashSet<string>();
		}
		if (preferences == null)
		{
			preferences = new HashSet<string>();
		}
	}
}
