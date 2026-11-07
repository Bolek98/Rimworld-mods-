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

	private static List<WorkTypeDef> ours;

	private static List<WorkTypeDef> Ours()
	{
		if (ours == null)
		{
			ours = new List<WorkTypeDef>();
			foreach (Part part in Split.Parts)
			{
				WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(part.def);
				if (w != null)
					ours.Add(w);
			}
		}
		return ours;
	}

	// 0.1.1: Pawn_WorkSettings.EnableAndInitialize prefix – the game gives a new pawn the 6 work types
	// he is best at; our parts (same skills as the parent) took those slots. Marked "always start
	// active" for this one call they stay out of the ranking, the postfix then sets them properly.
	public static void InitPrefix(out bool __state)
	{
		__state = true;
		foreach (WorkTypeDef w in Ours())
			w.alwaysStartActive = true;
	}

	public static Exception InitFinalizer(Exception __exception, bool __state)
	{
		if (__state)
			foreach (WorkTypeDef w in Ours())
				w.alwaysStartActive = false;
		return __exception;
	}

	// Pawn_WorkSettings.EnableAndInitialize postfix: off parts 0, on parts = their parent
	public static void InitPostfix(Pawn_WorkSettings __instance)
	{
		try
		{
			Pawn p = pawnRef(__instance);
			if (p == null || !__instance.EverWork)
				return;
			foreach (Part part in Split.Parts)
			{
				WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(part.def);
				if (c == null)
					continue;
				if (!Split.Active.TryGetValue(part.def, out string parent) || !Copy(p, __instance, part.def, parent))
				{
					WorkTypeDef par = parent != null ? DefDatabase<WorkTypeDef>.GetNamedSilentFail(parent) : null;
					bool keep = par != null && !p.WorkTypeIsDisabled(par) && !p.WorkTypeIsDisabled(c) && __instance.GetPriority(c) == __instance.GetPriority(par);
					if (!keep && __instance.GetPriority(c) != 0)
						__instance.SetPriority(c, 0);
				}
			}
		}
		catch (Exception e)
		{
			Log.Warning("[Steward – Work Split] new pawn: " + e.Message);
		}
	}

	public static bool Copy(Pawn p, string partName, string parentName) => Copy(p, p?.workSettings, partName, parentName);

	public static bool Copy(Pawn p, Pawn_WorkSettings ws, string partName, string parentName)
	{
		if (p == null || ws == null || !ws.EverWork)
			return false;
		WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(partName);
		WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(parentName);
		if (c == null || parent == null || p.WorkTypeIsDisabled(c) || p.WorkTypeIsDisabled(parent))
			return false;
		int v = ws.GetPriority(parent);
		if (ws.GetPriority(c) == v)
			return false;
		ws.SetPriority(c, v);
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
