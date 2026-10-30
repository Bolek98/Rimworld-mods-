using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace ColonyForeman;

// 2.37: things lying around that nobody can haul because no stockpile takes them
// (filter, priority or full). The steward tells you once per kind (again after a day).
public static class NoStorage
{
	private static readonly Dictionary<ThingDef, int> told = new Dictionary<ThingDef, int>();

	public static void Check(Map map)
	{
		if (SuppliesMod.S == null || !SuppliesMod.S.noStorageAlert || !map.IsPlayerHome)
			return;
		Pawn hauler = map.mapPawns.FreeColonistsSpawned.FirstOrDefault(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
		if (hauler == null)
			return;
		var stuck = new Dictionary<ThingDef, (int n, Thing first)>();
		int seen = 0;
		foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling().ToList())
		{
			if (++seen > 300)
				break;
			if (t == null || !t.Spawned || t.def.category != ThingCategory.Item || t is Corpse || t.IsForbidden(Faction.OfPlayer) || t.IsInAnyStorage())
				continue;
			if (!map.areaManager.Home[t.Position])
				continue;
			if (StoreUtility.TryFindBestBetterStorageFor(t, hauler, map, StoragePriority.Unstored, Faction.OfPlayer, out _, out _, false))
				continue;
			stuck.TryGetValue(t.def, out var e);
			stuck[t.def] = (e.n + 1, e.first ?? t);
		}
		int now = Find.TickManager.TicksGame;
		foreach (var kv in stuck)
		{
			if (told.TryGetValue(kv.Key, out int when) && now - when < 60000)
				continue;
			told[kv.Key] = now;
			Messages.Message("CF_NoStorage".Translate(kv.Key.LabelCap, kv.Value.n), kv.Value.first, MessageTypeDefOf.CautionInput, false);
		}
	}
}

// 2.37.1: diagnostics – the order of the hauling work givers (which one a colonist tries first)
[StaticConstructorOnStartup]
public static class HaulOrderLog
{
	static HaulOrderLog()
	{
		try
		{
			WorkTypeDef h = WorkTypeDefOf.Hauling;
			MLog.Info("[Steward] hauling order: " + string.Join(", ", h.workGiversByPriority.Select(g => g.defName + "(" + g.priorityInType + ", " + (g.modContentPack?.PackageId ?? "?") + ")")));
		}
		catch
		{
		}
	}
}
