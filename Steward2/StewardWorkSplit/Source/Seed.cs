using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StewardWorkSplit;

// A smaller work type starts with the priority its parent has, so nobody stops operating or
// harvesting because the work was split: new colonists right away, a save the first time a part
// is on in it (and again after it was off).
public static class Seed
{
	private static readonly AccessTools.FieldRef<Pawn_WorkSettings, Pawn> pawnRef = AccessTools.FieldRefAccess<Pawn_WorkSettings, Pawn>("pawn");

	// Pawn_WorkSettings.EnableAndInitialize postfix
	public static void InitPostfix(Pawn_WorkSettings __instance)
	{
		try
		{
			Pawn p = pawnRef(__instance);
			foreach (var kv in Split.Active)
				Copy(p, kv.Key, kv.Value);
		}
		catch (Exception e)
		{
			Log.Warning("[Steward – Work Split] new pawn: " + e.Message);
		}
	}

	public static bool Copy(Pawn p, string partName, string parentName)
	{
		if (p?.workSettings == null || !p.workSettings.EverWork)
			return false;
		WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(partName);
		WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(parentName);
		if (c == null || parent == null || p.WorkTypeIsDisabled(c) || p.WorkTypeIsDisabled(parent))
			return false;
		int v = p.workSettings.GetPriority(parent);
		if (p.workSettings.GetPriority(c) == v)
			return false;
		p.workSettings.SetPriority(c, v);
		return true;
	}
}

public sealed class WorkSplitGame : GameComponent
{
	private HashSet<string> seeded = new HashSet<string>();

	public WorkSplitGame(Game game)
	{
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref seeded, "seeded", LookMode.Value);
		if (seeded == null)
			seeded = new HashSet<string>();
	}

	public override void StartedNewGame() => Run();

	public override void LoadedGame() => Run();

	private void Run()
	{
		try
		{
			int n = 0, parts = 0;
			foreach (var kv in Split.Active)
			{
				if (seeded.Contains(kv.Key))
					continue;
				parts++;
				foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
					if (Seed.Copy(p, kv.Key, kv.Value))
						n++;
				seeded.Add(kv.Key);
			}
			// a part that is off now starts again from its parent when it is switched back on
			seeded.RemoveWhere(d => !Split.Active.ContainsKey(d));
			if (parts > 0)
				Log.Message("[Steward – Work Split] " + parts + " new smaller work types got their parent's priorities (" + n + " changes).");
		}
		catch (Exception e)
		{
			Log.Warning("[Steward – Work Split] priorities: " + e.Message);
		}
	}
}
