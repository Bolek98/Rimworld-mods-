using System.Collections.Generic;
using RimWorld;

namespace ColonyForeman;

public sealed class WorkPolicy
{
	public float urgency = 0.5f;

	public bool everyone;

	public bool emergency;

	public bool pinned;

	public int minWorkers = 1;

	public float share = -1f;

	public float statWeight = 0.5f;

	public readonly List<StatDef> stats = new List<StatDef>();
}
