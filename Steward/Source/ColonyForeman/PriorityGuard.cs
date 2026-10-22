using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace ColonyForeman;

// 2.53.0: priority guard.
// The game saves every pawn's work priorities only as a list of numbers in the order of the work
// types (DefMap by index). When a mod that adds a work type is added, removed or moved in the mod
// list, every number lands on another work type – cooks get hauling, growers get art …
// Here the order of the work types (by name) and every pawn's priorities by work type name are
// saved too. After loading, when the order is different, each pawn gets his priorities back on the
// right work types.
public sealed class PriorityGuard : GameComponent
{
	private List<string> order = new List<string>();
	private Dictionary<string, string> byPawn = new Dictionary<string, string>();

	public static int LastFixed { get; private set; }

	public PriorityGuard(Game game)
	{
	}

	private static List<string> CurrentOrder()
	{
		List<WorkTypeDef> all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
		var list = new List<string>(all.Count);
		for (int i = 0; i < all.Count; i++)
			list.Add(null);
		foreach (WorkTypeDef w in all)
			if (w.index >= 0 && w.index < list.Count)
				list[w.index] = w.defName;
		return list;
	}

	private static IEnumerable<Pawn> Workers()
	{
		var seen = new HashSet<int>();
		IEnumerable<Pawn> src = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive;
		foreach (Pawn p in src)
		{
			if (p?.workSettings == null || !p.workSettings.EverWork || !seen.Add(p.thingIDNumber))
				continue;
			if (p.Faction != Faction.OfPlayer && !p.IsSlaveOfColony)
				continue;
			yield return p;
		}
	}

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			order = CurrentOrder();
			byPawn = new Dictionary<string, string>();
			try
			{
				foreach (Pawn p in Workers())
				{
					var parts = new List<string>();
					foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
					{
						int v = p.workSettings.GetPriority(w);
						if (v != 0)
							parts.Add(w.defName + ":" + v);
					}
					byPawn[p.GetUniqueLoadID()] = string.Join(",", parts);
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[Steward] priority guard could not save: " + ex.Message);
			}
		}
		Scribe_Collections.Look(ref order, "cfWorkOrder", LookMode.Value);
		Scribe_Collections.Look(ref byPawn, "cfPriorities", LookMode.Value, LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			order ??= new List<string>();
			byPawn ??= new Dictionary<string, string>();
		}
	}

	public override void LoadedGame()
	{
		LastFixed = 0;
		try
		{
			if (order == null || order.Count == 0)
				return;
			List<string> now = CurrentOrder();
			if (now.SequenceEqual(order))
				return;
			int fixedPawns = 0;
			foreach (Pawn p in Workers())
			{
				if (Restore(p))
					fixedPawns++;
			}
			LastFixed = fixedPawns;
			var gone = order.Where(d => d != null && !now.Contains(d)).ToList();
			var added = now.Where(d => d != null && !order.Contains(d)).ToList();
			string msg = "[Steward] The work types changed since the last save (mods added, removed or moved"
				+ (gone.Count > 0 ? "; gone: " + string.Join(", ", gone) : "")
				+ (added.Count > 0 ? "; new: " + string.Join(", ", added) : "")
				+ "). Priorities put back on the right work for " + fixedPawns + " pawns.";
			Log.Message(msg);
			if (fixedPawns > 0)
				Messages.Message("CF_GuardFixed".Translate(fixedPawns), MessageTypeDefOf.NeutralEvent, historical: false);
		}
		catch (Exception ex)
		{
			Log.Error("[Steward] priority guard failed: " + ex);
		}
	}

	private bool Restore(Pawn p)
	{
		var want = new Dictionary<string, int>();
		if (byPawn.TryGetValue(p.GetUniqueLoadID(), out string saved) && saved != null)
		{
			foreach (string part in saved.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
			{
				int cut = part.LastIndexOf(':');
				if (cut > 0 && int.TryParse(part.Substring(cut + 1), out int v))
					want[part.Substring(0, cut)] = v;
			}
		}
		else
			return false;
		bool manual = Find.PlaySettings.useWorkPriorities;
		foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
		{
			if (p.WorkTypeIsDisabled(w))
				continue;
			// not in the saved list = it was off, or the work type is new (a new mod) – off, like the game does
			if (!want.TryGetValue(w.defName, out int v))
				v = 0;
			if (!manual && v > 0)
				v = 3;
			if (p.workSettings.GetPriority(w) != v)
				p.workSettings.SetPriority(w, v);
		}
		return true;
	}
}
