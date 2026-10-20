using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// 2.46: colonists sit down to eat and use the toilet.
//  * Eating: the game looks for a chair at a table only within ~32 cells of where the colonist
//    stands when he has the food (food taken from the pockets in the field, a far freezer).
//    When it finds none, it eats standing. Now a colonist at home also looks further (70 cells).
//  * When even that finds no chair, or a colonist has to relieve himself on the floor
//    (Dubs Bad Hygiene), the reason is written to the log and shown once – forbidden or outside
//    the allowed area, reserved, prison cell, not reachable, toilet not working / not allowed …
public static class Comfort
{
	public static bool Enabled => ForemanMod.Settings == null || ForemanMod.Settings.sitToEat;

	private static readonly Dictionary<string, int> lastTold = new Dictionary<string, int>();

	public static void Tell(Pawn p, string key, string reason, bool logOnly = false)
	{
		int now = Find.TickManager?.TicksGame ?? 0;
		string k = key + "|" + reason;
		if (lastTold.TryGetValue(k, out int t) && now - t < 30000)
			return;
		lastTold[k] = now;
		string text = (key == "eat" ? "CF_ComfortEat" : "CF_ComfortToilet").Translate(p.LabelShort, reason);
		MLog.Info("[Steward] " + text);
		if (!logOnly && (ForemanMod.Settings == null || ForemanMod.Settings.supplyMessages))
			Messages.Message(text, p, MessageTypeDefOf.NeutralEvent, historical: false);
	}

	public static bool AtTable(IntVec3 cell, Map map)
	{
		if (!cell.IsValid || !cell.InBounds(map))
			return false;
		Building ed = cell.GetEdifice(map);
		if (ed == null || ed.def.building == null || !ed.def.building.isSittable)
			return false;
		for (int i = 0; i < 4; i++)
		{
			Building e = (cell + GenAdj.CardinalDirections[i]).GetEdifice(map);
			if (e != null && e.def.surfaceType == SurfaceType.Eat)
				return true;
		}
		return false;
	}

	private static bool HasTable(Thing chair, Pawn p, out IntVec3 spot)
	{
		spot = IntVec3.Invalid;
		if (!Toils_Ingest.TryFindFreeSittingSpotOnThing(chair, p, out spot))
			return false;
		return AtTable(spot, chair.Map);
	}

	public static bool GoodChair(Thing t, Pawn p)
	{
		if (t.def.building == null || !t.def.building.isSittable)
			return false;
		if (t.Faction != p.Faction && p.Faction != null && p.Faction.IsPlayer)
			return false;
		if (!HasTable(t, p, out _))
			return false;
		if (t.IsForbidden(p) || (p.IsColonist && t.Position.Fogged(t.Map)) || !p.CanReserve(t) || !t.IsSociallyProper(p) || t.IsBurning() || t.HostileTo(p))
			return false;
		return t.Position.GetDangerFor(p, t.Map) == Danger.None;
	}

	// why the nearest chair at a table can not be used
	public static string WhyNoChair(Pawn p, out bool far)
	{
		far = false;
		Map map = p.Map;
		Thing best = null;
		int bd = int.MaxValue;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (b.def.building == null || !b.def.building.isSittable)
				continue;
			bool table = false;
			foreach (IntVec3 c in b.OccupiedRect())
				if (AtTable(c, map)) { table = true; break; }
			if (!table)
				continue;
			int d = b.Position.DistanceToSquared(p.Position);
			if (d < bd) { bd = d; best = b; }
		}
		if (best == null)
			return "CF_WhyNoTable".Translate();
		string where = " (" + best.LabelShort + ")";
		if (best.IsForbidden(p))
		{
			Area a = p.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
			return a != null && !a[best.Position] ? "CF_WhyArea".Translate(a.Label) + where : "CF_WhyForbidden".Translate() + where;
		}
		if (!best.IsSociallyProper(p))
			return "CF_WhyPrison".Translate() + where;
		if (!p.CanReach(best, PathEndMode.OnCell, Danger.Deadly))
			return "CF_WhyReach".Translate() + where;
		if (!p.CanReserve(best))
		{
			Pawn by = map.reservationManager.FirstRespectedReserver(best, p);
			return "CF_WhyReserved".Translate(by?.LabelShort ?? "?") + where;
		}
		if (!Toils_Ingest.TryFindFreeSittingSpotOnThing(best, p, out _))
			return "CF_WhyFull".Translate() + where;
		if (best.Position.GetDangerFor(p, map) != Danger.None)
			return "CF_WhyDanger".Translate() + where;
		far = true;
		return "CF_WhyFar".Translate(Mathf.RoundToInt(Mathf.Sqrt(bd))) + where;
	}
}

[HarmonyPatch(typeof(Toils_Ingest), nameof(Toils_Ingest.TryFindChairOrSpot))]
public static class Comfort_SitToEat
{
	public static void Postfix(Pawn pawn, Thing ingestible, ref IntVec3 cell, ref bool __result)
	{
		try
		{
			if (!Comfort.Enabled || pawn?.Map == null || !pawn.IsColonist || !pawn.Map.IsPlayerHome || pawn.Drafted)
				return;
			if (__result && Comfort.AtTable(cell, pawn.Map))
				return;
			// only for things eaten at a table (meals, raw food), not drugs or beer
			if (ingestible?.def?.ingestible == null || ingestible.def.ingestible.chairSearchRadius <= 0f)
				return;
			Pawn p = pawn;
			Thing chair = GenClosest.ClosestThingReachable(p.Position, p.Map, ThingRequest.ForGroup(ThingRequestGroup.BuildingArtificial), PathEndMode.OnCell,
				TraverseParms.For(p), 70f, t => Comfort.GoodChair(t, p));
			if (chair != null && Toils_Ingest.TryFindFreeSittingSpotOnThing(chair, p, out IntVec3 spot))
			{
				cell = spot;
				__result = true;
				return;
			}
			string why = Comfort.WhyNoChair(p, out bool far);
			// far away from the base (work in the field, food from the pockets) eating standing is normal – log only
			Comfort.Tell(p, "eat", why, far);
		}
		catch (Exception e)
		{
			MLog.WarnOnce("[Steward] sit to eat: " + e.Message, 0x5c0f001);
		}
	}
}

// Dubs Bad Hygiene: when a colonist would relieve himself on the floor, tell why no toilet works
[StaticConstructorOnStartup]
public static class Comfort_Toilet
{
	private static Type fixtureType;
	private static MethodInfo pawnAllowed, working;

	static Comfort_Toilet()
	{
		try
		{
			Type giver = AccessTools.TypeByName("DubsBadHygiene.JobGiver_UseToilet");
			if (giver == null)
				return;
			fixtureType = AccessTools.TypeByName("DubsBadHygiene.Building_AssignableFixture");
			pawnAllowed = fixtureType == null ? null : AccessTools.Method(fixtureType, "PawnAllowed");
			working = fixtureType == null ? null : AccessTools.Method(fixtureType, "Working");
			new Harmony("misak.colonyforeman.dbh").Patch(AccessTools.Method(giver, "TryGiveJob"), postfix: new HarmonyMethod(typeof(Comfort_Toilet), nameof(Postfix)));
		}
		catch (Exception e)
		{
			MLog.Warn("[Steward] toilet check not available: " + e.Message);
		}
	}

	private static string Report(object r)
	{
		if (r is AcceptanceReport a)
			return a.Accepted ? null : (a.Reason.NullOrEmpty() ? "-" : a.Reason);
		return null;
	}

	public static void Postfix(Pawn pawn, Job __result)
	{
		try
		{
			if (!Comfort.Enabled || __result == null || pawn?.Map == null || !pawn.IsColonist || !pawn.Map.IsPlayerHome)
				return;
			if (__result.def.defName != "haveWildPoo")
				return;
			Map map = pawn.Map;
			Thing best = null;
			int bd = int.MaxValue;
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (fixtureType == null || !fixtureType.IsInstanceOfType(b) || b.def.defName.IndexOf("toilet", StringComparison.OrdinalIgnoreCase) < 0 && b.def.defName.IndexOf("latrine", StringComparison.OrdinalIgnoreCase) < 0)
					continue;
				int d = b.Position.DistanceToSquared(pawn.Position);
				if (d < bd) { bd = d; best = b; }
			}
			string why;
			if (best == null)
				why = "CF_WhyNoToilet".Translate();
			else
			{
				string where = " (" + best.LabelShort + ")";
				string w = working != null ? Report(working.Invoke(best, null)) : null;
				string a = pawnAllowed != null ? Report(pawnAllowed.Invoke(best, new object[] { pawn })) : null;
				Area area = pawn.playerSettings?.EffectiveAreaRestrictionInPawnCurrentMap;
				if (w != null) why = "CF_WhyBroken".Translate(w) + where;
				else if (a != null) why = "CF_WhyNotAllowed".Translate(a) + where;
				else if (best.IsForbidden(pawn)) why = (area != null && !area[best.Position] ? "CF_WhyArea".Translate(area.Label) : "CF_WhyForbidden".Translate()) + where;
				else if (!best.IsSociallyProper(pawn)) why = "CF_WhyPrison".Translate() + where;
				else if (!pawn.CanReach(best, PathEndMode.Touch, Danger.Deadly)) why = "CF_WhyReach".Translate() + where;
				else if (!pawn.CanReserve(best, 2)) why = "CF_WhyReserved".Translate(map.reservationManager.FirstRespectedReserver(best, pawn)?.LabelShort ?? "?") + where;
				else why = "CF_WhyBusy".Translate() + where;
			}
			Comfort.Tell(pawn, "toilet", why);
		}
		catch (Exception e)
		{
			MLog.WarnOnce("[Steward] toilet check: " + e.Message, 0x5c0f002);
		}
	}
}

public static class HaulSmarterCompat
{
	private static bool init;
	private static MethodInfo isTracked;

	public static bool Tracked(Thing t)
	{
		if (!init)
		{
			init = true;
			Type d = AccessTools.TypeByName("HaulSmarter.HSData");
			isTracked = d == null ? null : AccessTools.Method(d, "IsTracked", new[] { typeof(Thing) });
		}
		if (isTracked == null)
			return false;
		try { return (bool)isTracked.Invoke(null, new object[] { t }); }
		catch { return false; }
	}
}

// 2.46.1: messages top left – only the important ones (threats, deaths, health, bad events, warnings,
// "can't do that"). Routine notices are not shown but still go to the message history.
[HarmonyPatch(typeof(Messages), nameof(Messages.Message), new[] { typeof(Message), typeof(bool) })]
public static class QuietMessages
{
	private static HashSet<MessageTypeDef> quiet;

	public static bool Prefix(Message msg, bool historical)
	{
		try
		{
			if (msg?.def == null || ForemanMod.Settings == null || !ForemanMod.Settings.quietMessages)
				return true;
			if (quiet == null)
				quiet = new HashSet<MessageTypeDef> { MessageTypeDefOf.NeutralEvent, MessageTypeDefOf.PositiveEvent, MessageTypeDefOf.TaskCompletion,
					MessageTypeDefOf.SituationResolved, MessageTypeDefOf.SilentInput };
			if (!quiet.Contains(msg.def))
				return true;
			if (historical && Find.Archive != null)
				Find.Archive.Add(msg);
			return false;
		}
		catch
		{
			return true;
		}
	}
}
