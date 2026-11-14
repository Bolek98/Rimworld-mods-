using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnyMaterial;

// 0.2.0: buildings with a fixed cost (torch: 20 wood) – a cost item that is a material (wood, steel,
// blocks, cloth, leather…) may be replaced by another material of the same kind when the original is missing.
public class FixedSub : IExposable
{
	public bool off;                                   // the player turned it off for this blueprint
	public List<ThingDef> allowed;                     // null = any of the same kind
	public Dictionary<ThingDef, ThingDef> chosen = new Dictionary<ThingDef, ThingDef>();   // original -> used instead
	private List<ThingDef> ck, cv;

	public void ExposeData()
	{
		Scribe_Values.Look(ref off, "off");
		Scribe_Collections.Look(ref allowed, "allowed", LookMode.Def);
		Scribe_Collections.Look(ref chosen, "chosen", LookMode.Def, LookMode.Def, ref ck, ref cv);
		chosen ??= new Dictionary<ThingDef, ThingDef>();
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
			foreach (ThingDef k in chosen.Keys.ToList())
				if (k == null || chosen[k] == null) chosen.Remove(k);
	}

	public FixedSub Copy() => new FixedSub { off = off, allowed = allowed?.ToList(), chosen = new Dictionary<ThingDef, ThingDef>(chosen) };
}

public static class Fixed
{
	private static readonly Dictionary<ThingDef, List<ThingDef>> subsCache = new Dictionary<ThingDef, List<ThingDef>>();

	// other materials of the same kind (shares a stuff category: woody, metallic, stony, fabric, leathery…)
	public static List<ThingDef> Subs(ThingDef orig)
	{
		if (orig == null || !orig.IsStuff || orig.stuffProps.categories.NullOrEmpty())
			return null;
		if (!subsCache.TryGetValue(orig, out List<ThingDef> l))
		{
			l = DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d != orig && d.IsStuff && d.stuffProps.categories != null && d.stuffProps.categories.Any(orig.stuffProps.categories.Contains))
				.OrderBy(d => d.label).ToList();
			subsCache[orig] = l;
		}
		return l.Count > 0 ? l : null;
	}

	public static BuildableDef BuildDef(Thing t)
		=> t is Blueprint_Build bp ? bp.def.entityDefToBuild : t is Frame f ? f.def.entityDefToBuild : t?.def;

	public static ThingDef StuffOf(Thing t)
		=> t is Blueprint_Build bp ? bp.stuffToUse : t?.Stuff;

	// the cost items of this blueprint/frame that could be replaced
	public static List<ThingDefCountClass> Replaceable(BuildableDef bd, ThingDef stuff)
	{
		var res = new List<ThingDefCountClass>();
		List<ThingDefCountClass> cost;
		Ctx.paused++;
		try { cost = bd.CostListAdjusted(stuff, false); }
		finally { Ctx.paused--; }
		if (cost == null) return res;
		foreach (ThingDefCountClass c in cost)
			if (c.thingDef != stuff && Subs(c.thingDef) != null)
				res.Add(c);
		return res;
	}

	public static bool Applies(Thing t)
	{
		var s = AnyMaterialMod.S;
		if (s == null || !s.enabled || t == null || t.Faction != Faction.OfPlayer)
			return false;
		BuildableDef bd = BuildDef(t);
		return bd != null && Replaceable(bd, StuffOf(t)).Count > 0;
	}

	public static int Have(Map map, ThingDef d)
	{
		int n = 0;
		List<Thing> l = map.listerThings.ThingsOfDef(d);
		for (int i = 0; i < l.Count; i++)
			if (!l[i].IsForbidden(Faction.OfPlayer))
				n += l[i].stackCount;
		return n;
	}

	private static float Score(ThingDef d, Map map, PickRule rule, int need)
	{
		switch (rule)
		{
			case PickRule.Cheapest: return -d.BaseMarketValue * need;
			case PickRule.Strongest: return d.GetStatValueAbstract(StatDefOf.StuffPower_Armor_Sharp);
			case PickRule.Prettiest: return d.stuffProps?.statFactors?.FirstOrDefault(m => m.stat == StatDefOf.Beauty)?.value ?? 1f;
			default: return Have(map, d);
		}
	}

	// when a builder is about to bring materials: keep the original while there is enough of it,
	// otherwise take another material of the same kind. Once something is delivered it stays.
	public static void Repick(Thing t) => Repick(t, false);

	// drops what was brought of one material next to the frame
	public static void Eject(Frame f, ThingDef d)
	{
		if (f?.Map == null || d == null) return;
		foreach (Thing t in f.resourceContainer.ToList())
			if (t.def == d)
				f.resourceContainer.TryDrop(t, f.Position, f.Map, ThingPlaceMode.Near, out _);
	}

	// drops everything in a frame that is no longer part of its cost
	public static void EjectExtras(Thing t)
	{
		if (!(t is Frame f) || f.Map == null) return;
		var cost = f.TotalMaterialCost();
		foreach (Thing x in f.resourceContainer.ToList())
			if (!cost.Any(c => c.thingDef == x.def))
				f.resourceContainer.TryDrop(x, f.Position, f.Map, ThingPlaceMode.Near, out _);
	}

	public static void Repick(Thing t, bool force)
	{
		var s = AnyMaterialMod.S;
		Map map = t.Map;
		AMMap comp = AMMap.Of(map);
		if (comp == null || !Applies(t))
			return;
		FixedSub fs = comp.GetFixed(t);
		if (fs?.off == true || (fs == null && !s.fixedAuto))
			return;
		Frame frame = t as Frame;
		var rule = (PickRule)s.rule;
		foreach (ThingDefCountClass c in Replaceable(BuildDef(t), StuffOf(t)))
		{
			ThingDef orig = c.thingDef;
			ThingDef cur = fs != null && fs.chosen.TryGetValue(orig, out ThingDef ch) ? ch : orig;
			int delivered = frame != null ? frame.resourceContainer.TotalStackCountOfDef(cur) : 0;
			if (delivered > 0 && Have(map, cur) + delivered >= c.count)
				continue;   // already being built from it and there is enough for the rest
			var cands = new List<ThingDef> { orig };
			cands.AddRange(Subs(orig));
			if (fs?.allowed != null)
				cands = cands.Where(d => d == orig || fs.allowed.Contains(d)).ToList();
			int need = c.count;
			if (delivered == 0 && Have(map, cur) >= need && (fs?.allowed == null || cur == orig || fs.allowed.Contains(cur)) && (cur == orig || !s.preferOriginal || Have(map, orig) < need))
				continue;
			ThingDef best = null;
			if (s.preferOriginal && Have(map, orig) >= need)
				best = orig;
			else
			{
				float bestScore = float.MinValue;
				foreach (ThingDef d in cands)
				{
					if (Have(map, d) < need) continue;
					float sc = Score(d, map, rule, need);
					if (sc > bestScore) { bestScore = sc; best = d; }
				}
			}
			// nothing there in full: take what there is most of, so at least something gets brought
			// (a frame that already has some keeps its material then – nothing better is there)
			if (best == null && delivered > 0)
				continue;
			best ??= cands.OrderByDescending(d => Have(map, d)).First();
			if (best == cur)
				continue;
			fs ??= comp.MakeFixed(t);
			if (frame != null && delivered > 0)
				Eject(frame, cur);
			if (best == orig) fs.chosen.Remove(orig);
			else fs.chosen[orig] = best;
		}
	}

	// the cost list with the replacements (a new list – the game's lists are cached and shared)
	public static List<ThingDefCountClass> Apply(List<ThingDefCountClass> cost, FixedSub fs)
	{
		if (cost == null || fs == null || fs.chosen.Count == 0)
			return cost;
		var res = new List<ThingDefCountClass>(cost.Count);
		foreach (ThingDefCountClass c in cost)
		{
			if (fs.chosen.TryGetValue(c.thingDef, out ThingDef sub) && sub != c.thingDef)
			{
				ThingDefCountClass same = res.FirstOrDefault(x => x.thingDef == sub);
				if (same != null) same.count += c.count;
				else res.Add(new ThingDefCountClass(sub, c.count));
			}
			else
			{
				ThingDefCountClass same = res.FirstOrDefault(x => x.thingDef == c.thingDef);
				if (same != null) same.count += c.count;
				else res.Add(new ThingDefCountClass(c.thingDef, c.count));
			}
		}
		return res;
	}
}

// While the game asks a blueprint, frame or finished building for its cost, the cost list gets the replacements.
public static class Ctx
{
	public static Thing thing;
	public static Map map;
	public static int paused;

	public static void Enter(Thing t, Map m, out (Thing, Map) old)
	{
		old = (thing, map);
		thing = t;
		map = m ?? t?.MapHeld;
	}

	public static void Exit((Thing, Map) old)
	{
		thing = old.Item1;
		map = old.Item2;
	}
}

[HarmonyPatch(typeof(CostListCalculator), nameof(CostListCalculator.CostListAdjusted), new[] { typeof(BuildableDef), typeof(ThingDef), typeof(bool) })]
public static class Patch_CostList
{
	public static void Postfix(BuildableDef entDef, ref List<ThingDefCountClass> __result)
	{
		Thing t = Ctx.thing;
		if (t == null || Ctx.paused > 0 || __result == null)
			return;
		if (Fixed.BuildDef(t) != entDef)
			return;
		FixedSub fs = AMMap.Of(Ctx.map)?.GetFixed(t);
		if (fs == null || fs.chosen.Count == 0)
			return;
		__result = Fixed.Apply(__result, fs);
	}
}

[HarmonyPatch]
public static class Patch_CtxConstructibles
{
	public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
	{
		yield return AccessTools.Method(typeof(Blueprint_Build), nameof(Blueprint_Build.TotalMaterialCost));
		yield return AccessTools.Method(typeof(Blueprint_Build), nameof(Blueprint_Build.GetInspectString));
		yield return AccessTools.Method(typeof(Blueprint), nameof(Blueprint.ThingCountNeeded));
		yield return AccessTools.Method(typeof(Frame), nameof(Frame.TotalMaterialCost));
		yield return AccessTools.Method(typeof(Frame), nameof(Frame.IsCompleted));
		yield return AccessTools.Method(typeof(Frame), nameof(Frame.ThingCountNeeded));
		yield return AccessTools.Method(typeof(Frame), nameof(Frame.ThingCountNeededWithEnroute));
		yield return AccessTools.Method(typeof(Frame), nameof(Frame.GetInspectString));
	}

	public static void Prefix(Thing __instance, out (Thing, Map) __state) => Ctx.Enter(__instance, null, out __state);

	public static Exception Finalizer(Exception __exception, (Thing, Map) __state)
	{
		Ctx.Exit(__state);
		return __exception;
	}
}

// deconstructing / destroying a finished building gives back what it was really built from
[HarmonyPatch(typeof(GenLeaving), nameof(GenLeaving.DoLeavingsFor), new[] { typeof(Thing), typeof(Map), typeof(DestroyMode), typeof(CellRect), typeof(Predicate<IntVec3>), typeof(List<Thing>) })]
public static class Patch_Leavings
{
	public static void Prefix(Thing diedThing, Map map, out (Thing, Map) __state) => Ctx.Enter(diedThing, map, out __state);

	public static Exception Finalizer(Exception __exception, (Thing, Map) __state)
	{
		Ctx.Exit(__state);
		return __exception;
	}
}

// the choice moves from the blueprint to its frame, and from the frame to the finished building
[HarmonyPatch(typeof(Blueprint), nameof(Blueprint.TryReplaceWithSolidThing))]
public static class Patch_BlueprintToFrame
{
	public static void Prefix(Blueprint __instance, out (Map, FixedSub, AnyStuff) __state)
	{
		Map m = __instance.Map;
		AMMap comp = AMMap.Of(m);
		__state = (m, comp?.GetFixed(__instance), comp?.Get(__instance));
	}

	public static void Postfix(Blueprint __instance, Thing createdThing, (Map, FixedSub, AnyStuff) __state)
	{
		AMMap comp = AMMap.Of(__state.Item1);
		if (comp == null || createdThing == null) return;
		if (__state.Item2 != null)
		{
			comp.fixedSubs[createdThing.thingIDNumber] = __state.Item2.Copy();
			comp.fixedSubs.Remove(__instance.thingIDNumber);
		}
		if (__state.Item3 != null && createdThing is Frame)   // 0.2.3: "any material" goes on with the frame
		{
			comp.blueprints[createdThing.thingIDNumber] = new AnyStuff { allowed = __state.Item3.allowed?.ToList() };
			comp.blueprints.Remove(__instance.thingIDNumber);
		}
	}
}

[HarmonyPatch(typeof(Frame), nameof(Frame.CompleteConstruction))]
public static class Patch_FrameToBuilding
{
	public static void Prefix(Frame __instance, out (Map, FixedSub, IntVec3, BuildableDef) __state)
	{
		Map m = __instance.Map;
		__state = (m, AMMap.Of(m)?.GetFixed(__instance), __instance.Position, __instance.def.entityDefToBuild);
	}

	public static void Postfix(Frame __instance, (Map, FixedSub, IntVec3, BuildableDef) __state)
	{
		FixedSub fs = __state.Item2;
		AMMap comp = AMMap.Of(__state.Item1);
		if (fs == null || comp == null || fs.chosen.Count == 0)
			return;
		comp.fixedSubs.Remove(__instance.thingIDNumber);
		foreach (Thing t in __state.Item3.GetThingList(__state.Item1))
			if (t.def == __state.Item4)
			{
				comp.fixedSubs[t.thingIDNumber] = new FixedSub { chosen = new Dictionary<ThingDef, ThingDef>(fs.chosen) };
				break;
			}
	}
}

// builders: pick the materials of frames too (blueprints are done in Hook)
[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToFrames), nameof(WorkGiver_ConstructDeliverResourcesToFrames.HasJobOnThing))]
public static class Patch_FrameHasJob
{
	public static void Prefix(Thing t) => Hook.Before(t);
}

[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToFrames), nameof(WorkGiver_ConstructDeliverResourcesToFrames.JobOnThing))]
public static class Patch_FrameJobOn
{
	public static void Prefix(Thing t) => Hook.Before(t);
}

// 0.2.3: the same buttons on frames (building already started) – change the material when it ran out
[HarmonyPatch(typeof(Frame), nameof(Frame.GetGizmos))]
public static class Patch_FrameGizmo
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Frame __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		var s = AnyMaterialMod.S;
		if (s == null || !s.enabled || __instance.Faction != Faction.OfPlayer)
			yield break;
		AMMap comp = AMMap.Of(__instance.Map);
		if (comp == null)
			yield break;
		Frame f = __instance;
		foreach (Gizmo g in Patch_BlueprintGizmo.FixedGizmos(f, comp))
			yield return g;
		if (!(f.def.entityDefToBuild is ThingDef td) || !td.MadeFromStuff)
			yield break;
		yield return new Command_Action
		{
			defaultLabel = "AM_GizmoFrameStuff".Translate(),
			defaultDesc = "AM_GizmoFrameStuff_Desc".Translate(),
			icon = Tex.Any,
			action = () =>
			{
				var opts = new List<FloatMenuOption>
				{
					new FloatMenuOption("AM_MenuAny".Translate(), () =>
					{
						comp.blueprints[f.thingIDNumber] = new AnyStuff();
						Picker.RepickFrame(f, comp.blueprints[f.thingIDNumber]);
					}, Tex.Any, Color.white)
				};
				foreach (ThingDef d in Picker.Stuffs(td).OrderByDescending(d => f.Map.resourceCounter.GetCount(d)))
				{
					ThingDef dd = d;
					int have = f.Map.resourceCounter.GetCount(d);
					if (have <= 0 && d != f.Stuff) continue;
					opts.Add(new FloatMenuOption(d.LabelCap + " (" + have + "/" + Picker.Need(td, d) + ")", () =>
					{
						comp.Clear(f);
						Picker.RepickFrame(f, new AnyStuff(), dd);
					}, d));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
		};
	}
}
