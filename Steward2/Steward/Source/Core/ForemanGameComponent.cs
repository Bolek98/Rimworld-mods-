using System.Collections.Generic;
using Verse;

namespace ColonyForeman;

// Your locks (middle click) and preferences – for the whole game.
public sealed class ForemanGameComponent : GameComponent
{
	public HashSet<string> lockedPawns = new HashSet<string>();
	public HashSet<string> lockedWork = new HashSet<string>();
	public HashSet<string> lockedCells = new HashSet<string>();
	public HashSet<string> preferences = new HashSet<string>();

	private readonly Dictionary<int, bool> pawnLockCache = new Dictionary<int, bool>();
	private readonly Dictionary<long, bool> cellLockCache = new Dictionary<long, bool>();
	private readonly Dictionary<long, bool> preferCache = new Dictionary<long, bool>();

	public ForemanGameComponent(Game game)
	{
	}

	public override void FinalizeInit()
	{
		ClearCaches();
		WorkCatalog.Reset();
		ForemanMapComponent.ResetStatics();
		StewardApi.RaiseGameStarted();
	}

	public static ForemanGameComponent Get() => Current.Game?.GetComponent<ForemanGameComponent>();

	private static string Key(Pawn p, WorkTypeDef w) => p.GetUniqueLoadID() + "|" + w.defName;

	private static long CellKey(Pawn p, WorkTypeDef w) => ((long)p.thingIDNumber << 16) | (uint)w.index;

	public void ClearCaches()
	{
		pawnLockCache.Clear();
		cellLockCache.Clear();
		preferCache.Clear();
	}

	public bool PawnLocked(Pawn p)
	{
		if (!pawnLockCache.TryGetValue(p.thingIDNumber, out bool v))
			pawnLockCache[p.thingIDNumber] = v = lockedPawns.Contains(p.GetUniqueLoadID());
		return v;
	}

	public bool WorkLocked(WorkTypeDef w) => lockedWork.Contains(w.defName);

	public bool CellLocked(Pawn p, WorkTypeDef w)
	{
		long k = CellKey(p, w);
		if (!cellLockCache.TryGetValue(k, out bool v))
			cellLockCache[k] = v = lockedCells.Contains(Key(p, w));
		return v;
	}

	public bool AnyLock(Pawn p, WorkTypeDef w) => PawnLocked(p) || WorkLocked(w) || CellLocked(p, w);

	public bool Prefers(Pawn p, WorkTypeDef w)
	{
		long k = CellKey(p, w);
		if (!preferCache.TryGetValue(k, out bool v))
			preferCache[k] = v = preferences.Contains(Key(p, w));
		return v;
	}

	public void TogglePawn(Pawn p) => Toggle(lockedPawns, p.GetUniqueLoadID());
	public void ToggleWork(WorkTypeDef w) => Toggle(lockedWork, w.defName);
	public void ToggleCell(Pawn p, WorkTypeDef w) => Toggle(lockedCells, Key(p, w));
	public void TogglePreference(Pawn p, WorkTypeDef w) => Toggle(preferences, Key(p, w));

	private void Toggle(HashSet<string> set, string key)
	{
		if (!set.Remove(key))
			set.Add(key);
		ClearCaches();
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref lockedPawns, "lockedPawns", LookMode.Value);
		Scribe_Collections.Look(ref lockedWork, "lockedWork", LookMode.Value);
		Scribe_Collections.Look(ref lockedCells, "lockedCells", LookMode.Value);
		Scribe_Collections.Look(ref preferences, "preferences", LookMode.Value);
		ClearCaches();
		lockedPawns ??= new HashSet<string>();
		lockedWork ??= new HashSet<string>();
		lockedCells ??= new HashSet<string>();
		preferences ??= new HashSet<string>();
		lockedCells.RemoveWhere(k => k != null && k.Split('|').Length > 2);
	}
}
