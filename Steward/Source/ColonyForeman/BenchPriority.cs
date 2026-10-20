using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// 2.36: priority workbenches. A toggle on every workbench with bills marks it as a priority bench.
// Colonists then do bills at marked benches first, before the other benches of the same work type.
// How: for every work type with bill work (Crafting, Smithing, Tailoring, Cooking, Art …), one extra
// WorkGiver is generated. It sits at the top of the work type (highest priorityInType) and scans only marked benches.
public sealed class BenchPriorityComp : MapComponent
{
	public List<Thing> marked = new List<Thing>();

	public BenchPriorityComp(Map map) : base(map) { }

	public static BenchPriorityComp For(Map map) => map?.GetComponent<BenchPriorityComp>();

	public bool IsMarked(Thing t) => t != null && marked.Contains(t);

	public void Toggle(Thing t)
	{
		if (!marked.Remove(t))
			marked.Add(t);
	}

	public override void MapComponentTick()
	{
		// once in a while drop destroyed or moved benches
		if (marked.Count > 0 && Find.TickManager.TicksGame % 2500 == 0)
			marked.RemoveAll(t => t == null || t.Destroyed || t.Map != map);
	}

	public override void MapComponentOnGUI()
	{
		if (marked.Count == 0 || Find.CurrentMap != map || WorldRendererUtility.WorldSelected)
			return;
		try
		{
			for (int i = 0; i < marked.Count; i++)
			{
				Thing t = marked[i];
				if (t == null || !t.Spawned)
					continue;
				Vector2 pos = (t.DrawPos + new Vector3(0f, 0f, 0.6f)).MapToUIPosition();
				GenMapUI.DrawThingLabel(pos, "★ 1", new Color(1f, 0.85f, 0.3f));
			}
		}
		catch
		{
		}
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref marked, "cfPriorityBenches", LookMode.Reference);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			marked ??= new List<Thing>();
			marked.RemoveAll(t => t == null);
		}
	}
}

// the generated WorkGiver: does bills like the vanilla one, but only at marked benches
public class WorkGiver_CFPriorityBench : WorkGiver_DoBill
{
	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		BenchPriorityComp comp = BenchPriorityComp.For(pawn.Map);
		if (comp == null || comp.marked.Count == 0)
			return true;
		List<Thing> list = comp.marked;
		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] is IBillGiver bg && list[i].Spawned && ThingIsUsableBillGiver(list[i]) && bg.BillStack.AnyShouldDoNow)
				return false;
		}
		return true;
	}

	// 2.36.1: who used the bench last – shown on the bench's button, first use also in the log
	public static readonly Dictionary<Thing, string> lastUse = new Dictionary<Thing, string>();

	// 2.36.1: the vanilla request (all bill givers) made the region search pick the NEAREST bench of
	// the work type, marked or not – so marked benches were not preferred at all. Undefined = only our list.
	public override ThingRequest PotentialWorkThingRequest => ThingRequest.ForUndefined();

	public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
	{
		BenchPriorityComp comp = BenchPriorityComp.For(thing?.Map);
		if (comp == null || !comp.IsMarked(thing))
			return null;
		Job j = base.JobOnThing(pawn, thing, forced);
		if (j != null && j.def == JobDefOf.DoBill)
		{
			if (!lastUse.ContainsKey(thing))
				MLog.Info("[Steward] priority bench " + thing.LabelShort + " used by " + pawn.LabelShort + " (" + def.workType.defName + ")");
			lastUse[thing] = pawn.LabelShort + "|" + Find.TickManager.TicksGame;
		}
		return j;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		BenchPriorityComp comp = BenchPriorityComp.For(pawn.Map);
		if (comp == null)
			return Enumerable.Empty<Thing>();
		return comp.marked.Where(t => t != null && t.Spawned && ThingIsUsableBillGiver(t));
	}
}

[StaticConstructorOnStartup]
public static class BenchPriorityDefs
{
	public static readonly HashSet<ThingDef> Benches = new HashSet<ThingDef>();

	public static readonly Texture2D Icon =
		ContentFinder<Texture2D>.Get("UI/ColonyForeman/BenchPriority", reportFailure: false)
		?? TexCommand.GatherSpotActive
		?? BaseContent.WhiteTex;

	static BenchPriorityDefs()
	{
		try
		{
			// only the plain vanilla DoBill workers with fixed benches (no surgery, no special mod logic)
			var sources = DefDatabase<WorkGiverDef>.AllDefsListForReading
				.Where(d => d.giverClass == typeof(WorkGiver_DoBill) && d.workType != null
					&& d.fixedBillGiverDefs != null && d.fixedBillGiverDefs.Count > 0)
				.GroupBy(d => d.workType)
				.ToList();
			foreach (var g in sources)
			{
				WorkTypeDef wt = g.Key;
				WorkGiverDef first = g.First();
				List<ThingDef> benches = g.SelectMany(d => d.fixedBillGiverDefs).Where(x => x != null).Distinct().ToList();
				if (benches.Count == 0)
					continue;
				int top = DefDatabase<WorkGiverDef>.AllDefsListForReading.Where(d => d.workType == wt).Max(d => d.priorityInType);
				WorkGiverDef def = new WorkGiverDef
				{
					defName = "CF_PriorityBench_" + wt.defName,
					label = "CF_BP_Giver".Translate(wt.gerundLabel),
					giverClass = typeof(WorkGiver_CFPriorityBench),
					workType = wt,
					priorityInType = top + 1,
					verb = first.verb,
					gerund = first.gerund,
					requiredCapacities = first.requiredCapacities?.ToList() ?? new List<PawnCapacityDef>(),
					canBeDoneByMechs = g.Any(d => d.canBeDoneByMechs),
					tagToGive = first.tagToGive,
					prioritizeSustains = first.prioritizeSustains,
					directOrderable = false, // otherwise the right-click menu shows every bill twice
					fixedBillGiverDefs = benches,
					modContentPack = ForemanMod.Instance?.Content,
					generated = true
				};
				DefDatabase<WorkGiverDef>.Add(def);
				wt.workGiversByPriority.Insert(0, def);
				foreach (ThingDef b in benches)
					Benches.Add(b);
			}
			MLog.Info("[Steward] Priority benches: " + sources.Count + " work types, " + Benches.Count + " bench types.");
		}
		catch (Exception e)
		{
			Log.Error("[Steward] Priority benches setup failed: " + e);
		}
	}
}

[HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
public static class BenchPriorityGizmoPatch
{
	private static string LastUse(Thing b)
	{
		if (!WorkGiver_CFPriorityBench.lastUse.TryGetValue(b, out string v))
			return "CF_BP_NotUsed".Translate();
		string[] parts = v.Split('|');
		int ago = Find.TickManager.TicksGame - int.Parse(parts[1]);
		return "CF_BP_LastUse".Translate(parts[0], (ago / 2500f).ToString("0.0"));
	}

	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		if (__instance?.Map == null || __instance.Faction != Faction.OfPlayer || !(__instance is IBillGiver)
			|| !BenchPriorityDefs.Benches.Contains(__instance.def))
			yield break;
		BenchPriorityComp comp = BenchPriorityComp.For(__instance.Map);
		if (comp == null)
			yield break;
		Building b = __instance;
		yield return new Command_Toggle
		{
			defaultLabel = "CF_BP_Label".Translate(),
			defaultDesc = "CF_BP_Desc".Translate() + "\n\n" + LastUse(b),
			icon = BenchPriorityDefs.Icon,
			isActive = () => comp.IsMarked(b),
			toggleAction = () => comp.Toggle(b)
		};
	}
}
