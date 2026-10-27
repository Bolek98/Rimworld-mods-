using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CaravanHelper;

// ---------------------------------------------------------------------------
// 1.0.3: 3. Map exit side and 4. sharing inventory on arrival.
//  The game lets you choose the route (world tile) and the packing spot, but
//  not where on the map the caravan walks out: 1.7 takes the map edge nearest
//  to the pawns, 1.6 the edge toward the route. "Exit" in the form caravan
//  window picks a map edge; the caravan then leaves through the reachable cell
//  of that edge nearest to the pawns (automatic = the game's choice).
//  On arrival on a map the caravan's items are shared out so every colonist
//  carries what his drug policy and inventory stock (medicine …) ask for.
// ---------------------------------------------------------------------------
public static class ExitChoice
{
	// Rot4.Invalid = automatic. One form caravan window at a time.
	public static Rot4 side = Rot4.Invalid;

	// set only while the game looks for the exit spot of the window being sent
	public static Map ctxMap;
	public static List<Pawn> ctxPawns;

	public static string Label(Rot4 r) => r == Rot4.North ? "CH_ExitNorth".Translate() : r == Rot4.East ? "CH_ExitEast".Translate()
		: r == Rot4.South ? "CH_ExitSouth".Translate() : r == Rot4.West ? "CH_ExitWest".Translate() : "CH_ExitAuto".Translate();

	public static bool Active => CaravanHelperMod.S.chooseExit && side != Rot4.Invalid && ctxMap != null && ctxPawns != null;

	// the standable, unfogged cell of the chosen edge nearest to the pawns that
	// every walking colonist can reach (or, failing that, that most can reach)
	public static bool TryFind(out IntVec3 spot)
	{
		spot = IntVec3.Invalid;
		if (!Active)
			return false;
		Map map = ctxMap;
		var walkers = ctxPawns.Where(p => p != null && p.Spawned && p.Map == map && p.IsColonist && !p.Downed).ToList();
		if (walkers.Count == 0)
			return false;
		IntVec3 root = walkers.Aggregate(IntVec3.Zero, (acc, p) => acc + p.Position) / walkers.Count;
		IntVec3 best = IntVec3.Invalid, fallback = IntVec3.Invalid;
		int bestDist = int.MaxValue, fallbackReach = 0, fallbackDist = int.MaxValue;
		// every 2nd cell is enough and keeps the reach checks cheap
		int i = 0;
		foreach (IntVec3 c in CellRect.WholeMap(map).GetEdgeCells(side))
		{
			if (i++ % 2 == 1 || !c.Standable(map) || c.Fogged(map))
				continue;
			int d = c.DistanceToSquared(root);
			if (d >= bestDist)
				continue;
			int reach = walkers.Count(p => p.CanReach(c, PathEndMode.Touch, Danger.Deadly));
			if (reach == walkers.Count)
			{
				best = c;
				bestDist = d;
			}
			else if (reach > fallbackReach || reach == fallbackReach && reach > 0 && d < fallbackDist)
			{
				fallback = c;
				fallbackReach = reach;
				fallbackDist = d;
			}
		}
		spot = best.IsValid ? best : fallback;
		if (!spot.IsValid)
		{
			Messages.Message("CH_ExitNone".Translate(Label(side)), MessageTypeDefOf.RejectInput, historical: false);
			return false;
		}
		return true;
	}
}

[HarmonyPatch]
public static class Patch_ExitCtor
{
	static MethodBase TargetMethod() => AccessTools.Constructor(typeof(Dialog_FormCaravan), new[] { typeof(Map), typeof(bool), typeof(Action), typeof(bool), typeof(IntVec3?) });
	static bool Prepare() => TargetMethod() != null;

	public static void Postfix()
	{
		ExitChoice.side = Rot4.Invalid;
	}
}

// the "Exit" button between Cancel and Reset (only when forming, not reforming)
[HarmonyPatch]
public static class Patch_ExitButton
{
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, bool> reformRef = AccessTools.FieldRefAccess<Dialog_FormCaravan, bool>("reform");

	static MethodBase TargetMethod() => AccessTools.Method(typeof(Dialog_FormCaravan), "DoBottomButtons", new[] { typeof(Rect) });
	static bool Prepare() => TargetMethod() != null;

	public static void Postfix(Dialog_FormCaravan __instance, Rect rect)
	{
		if (!CaravanHelperMod.S.chooseExit)
			return;
		try
		{
			if (reformRef(__instance))
				return;
			Rect b = new Rect(168f, rect.height - 55f - 17f, Mathf.Min(150f, rect.width / 2f - 160f - 8.5f - 176f), 40f);
			if (b.width < 80f)
				return;
			if (Widgets.ButtonText(b, "CH_Exit".Translate(ExitChoice.Label(ExitChoice.side))))
			{
				var menu = new List<FloatMenuOption>();
				foreach (Rot4 r in new[] { Rot4.Invalid, Rot4.North, Rot4.East, Rot4.South, Rot4.West })
				{
					Rot4 local = r;
					menu.Add(new FloatMenuOption(ExitChoice.Label(r), () => ExitChoice.side = local));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			TooltipHandler.TipRegion(b, "CH_ExitTip".Translate());
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] exit button: " + ex.Message, 0x6a7c03);
		}
	}
}

// the window is being sent: remember the map and the pawns for the exit search
[HarmonyPatch]
public static class Patch_ExitSend
{
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, Map> mapRef = AccessTools.FieldRefAccess<Dialog_FormCaravan, Map>("map");

	static MethodBase TargetMethod() => AccessTools.Method(typeof(Dialog_FormCaravan), "TryFormAndSendCaravan");
	static bool Prepare() => TargetMethod() != null;

	public static void Prefix(Dialog_FormCaravan __instance)
	{
		ExitChoice.ctxMap = null;
		ExitChoice.ctxPawns = null;
		if (!CaravanHelperMod.S.chooseExit || ExitChoice.side == Rot4.Invalid)
			return;
		try
		{
			ExitChoice.ctxMap = mapRef(__instance);
			ExitChoice.ctxPawns = TransferableUtility.GetPawnsFromTransferables(__instance.transferables).ToList();
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] exit side: " + ex.Message, 0x6a7c04);
		}
	}

	public static Exception Finalizer(Exception __exception)
	{
		ExitChoice.ctxMap = null;
		ExitChoice.ctxPawns = null;
		return __exception;
	}
}

// 1.7: the game takes the map edge nearest to the pawns
[HarmonyPatch]
public static class Patch_ExitClosestEdge
{
	static MethodBase TargetMethod() => AccessTools.Method(typeof(RCellFinder), "TryFindClosestEdgeCellTo", new[] { typeof(IntVec3), typeof(Map), typeof(IntVec3).MakeByRefType() });
	static bool Prepare() => TargetMethod() != null;

	public static bool Prefix(Map map, ref IntVec3 result, ref bool __result)
	{
		if (!ExitChoice.Active || map != ExitChoice.ctxMap)
			return true;
		try
		{
			if (!ExitChoice.TryFind(out IntVec3 spot))
				return true;
			result = spot;
			__result = true;
			return false;
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] exit side: " + ex.Message, 0x6a7c05);
			return true;
		}
	}
}

// 1.6 (and the 1.7 fallback): the edge toward the route
[HarmonyPatch]
public static class Patch_ExitSpot
{
	static MethodBase TargetMethod() => AccessTools.Method(typeof(Dialog_FormCaravan), "TryFindExitSpot", new[] { typeof(List<Pawn>), typeof(bool), typeof(IntVec3).MakeByRefType() });
	static bool Prepare() => TargetMethod() != null;

	public static bool Prefix(ref IntVec3 spot, ref bool __result)
	{
		if (!ExitChoice.Active)
			return true;
		try
		{
			if (!ExitChoice.TryFind(out IntVec3 s))
				return true;
			spot = s;
			__result = true;
			return false;
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] exit side: " + ex.Message, 0x6a7c06);
			return true;
		}
	}
}

// ---------------------------------------------------------------------------
// 4. sharing inventory on arrival
// ---------------------------------------------------------------------------
[HarmonyPatch]
public static class Patch_ShareOnArrival
{
	static MethodBase TargetMethod() => AccessTools.Method(typeof(CaravanEnterMapUtility), "Enter",
		new[] { typeof(Caravan), typeof(Map), typeof(Func<Pawn, IntVec3>), typeof(CaravanDropInventoryMode), typeof(bool) });
	static bool Prepare() => TargetMethod() != null;

	public static void Prefix(Caravan caravan, CaravanDropInventoryMode dropInventoryMode)
	{
		if (!CaravanHelperMod.S.shareOnArrival || caravan == null || dropInventoryMode == CaravanDropInventoryMode.DropInstantly)
			return;
		try
		{
			int moved = Share(caravan.PawnsListForReading.ToList());
			if (moved > 0 && Prefs.DevMode)
				Log.Message("[Caravan Helper] shared " + moved + " items on arrival.");
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] sharing inventory: " + ex.Message, 0x6a7c07);
		}
	}

	// what a colonist wants in his own pockets: drugs from his drug policy
	// ("take to inventory") and the inventory stock (medicine …)
	public static Dictionary<ThingDef, int> Wants(Pawn p)
	{
		var w = new Dictionary<ThingDef, int>();
		if (p == null || p.Dead || p.RaceProps == null || !p.RaceProps.Humanlike || p.Faction != Faction.OfPlayer || p.IsPrisoner)
			return w;
		DrugPolicy pol = p.drugs?.CurrentPolicy;
		if (pol != null)
			for (int i = 0; i < pol.Count; i++)
			{
				DrugPolicyEntry e = pol[i];
				if (e?.drug != null && e.takeToInventory > 0)
					w[e.drug] = Math.Max(w.TryGetValue(e.drug, out int a) ? a : 0, e.takeToInventory);
			}
		if (p.inventoryStock != null)
			foreach (InventoryStockGroupDef g in DefDatabase<InventoryStockGroupDef>.AllDefsListForReading)
			{
				int c = p.inventoryStock.GetDesiredCountForGroup(g);
				ThingDef d = c > 0 ? p.inventoryStock.GetDesiredThingForGroup(g) : null;
				if (d != null)
					w[d] = Math.Max(w.TryGetValue(d, out int a) ? a : 0, c);
			}
		return w;
	}

	public static int Share(List<Pawn> pawns)
	{
		int moved = 0;
		var wants = new Dictionary<Pawn, Dictionary<ThingDef, int>>();
		foreach (Pawn p in pawns)
			if (p?.inventory?.innerContainer != null)
				wants[p] = Wants(p);
		foreach (Pawn p in wants.Keys.ToList())
		{
			ThingOwner mine = p.inventory.innerContainer;
			foreach (KeyValuePair<ThingDef, int> kv in wants[p])
			{
				int need = kv.Value - mine.TotalStackCountOfDef(kv.Key);
				foreach (Pawn other in wants.Keys)
				{
					if (need <= 0)
						break;
					if (other == p)
						continue;
					ThingOwner theirs = other.inventory.innerContainer;
					int keep = wants[other].TryGetValue(kv.Key, out int k) ? k : 0;
					int spare = theirs.TotalStackCountOfDef(kv.Key) - keep;
					int guard = 0;
					while (need > 0 && spare > 0 && guard++ < 20)
					{
						Thing t = theirs.FirstOrDefault(x => x.def == kv.Key);
						if (t == null)
							break;
						int n = theirs.TryTransferToContainer(t, mine, Math.Min(t.stackCount, Math.Min(need, spare)));
						if (n <= 0)
							break;
						need -= n;
						spare -= n;
						moved += n;
					}
				}
			}
		}
		return moved;
	}
}
