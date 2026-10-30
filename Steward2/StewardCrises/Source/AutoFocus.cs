using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.19: Balanced picks a focus by itself.
// While the map uses Balanced (and the setting is on) the steward looks at the
// colony every plan and every ~5000 ticks and picks at most ONE focus style
// (Survival, Farming, Building, Industry, Production, Care, Defense,
// Ranching, Comfort, Research) – or none. That style's table is added at
// reduced strength (×0.6 at the default style strength) on top of the normal
// Balanced logic; the even spreading of work stays.
//
// Every candidate gets a score where 1.0 means "worth a focus". The best one
// at or above 1 is picked. A focus is kept at least one in-game day (hysteresis)
// unless an urgent need (food, the time right after a raid) appears, or its
// own need is gone (score below 0.35 after at least six hours).
// Crises are not duplicated: Defense is never picked while the Raid crisis is
// on (the crisis already handles it) – it is the "repair and re-arm" focus
// for the two days after a raid.
// ---------------------------------------------------------------------------
public static class AutoFocus
{
	public const ForemanStyle None = ForemanStyle.Balanced;

	// part of a focus style's table applied (at the default style strength 2)
	public const float Strength = 0.6f;

	public const int EvalInterval = 5000;      // background re-evaluation
	public const int MinEvalGap = 600;         // plans closer than this reuse the result
	public const int MinHold = 60000;          // one in-game day
	public const int DropHold = 15000;         // six hours – an empty need may end earlier
	public const int RaidMemory = 120000;      // two days after a raid

	private struct Cand
	{
		public ForemanStyle style;
		public float score;
		public string reason;
		public bool urgent;
	}

	public static bool Active(ForemanMapComponent comp)
	{
		CrisesSettings s = CrisesMod.S;
		return comp != null && comp.style == ForemanStyle.Balanced && (s == null || s.balancedAutoFocus);
	}

	public static ForemanStyle Current(ForemanMapComponent comp)
	{
		return Active(comp) ? CS.Of(comp).autoFocus : None;
	}

	// The extra importance for a work type from the current focus.
	public static float Delta(ForemanMapComponent comp, WorkTypeDef w)
	{
		ForemanStyle f = Current(comp);
		if (f == None || w == null)
			return 0f;
		float strength = (ForemanMod.Settings?.styleStrength ?? 2f) / 2f;
		return Styles.Delta(f, w) * Strength * strength;
	}

	// "Balanced → now: Building (32 blueprints)"
	public static string LongLabel(ForemanMapComponent comp)
	{
		if (comp == null)
			return "";
		ForemanStyle f = Current(comp);
		if (f == None)
			return Styles.Label(comp.style);
		return string.IsNullOrEmpty(CS.Of(comp).autoFocusReason)
			? (string)"CF_AutoFocusNowNoReason".Translate(Styles.Label(comp.style), Styles.Label(f))
			: (string)"CF_AutoFocusNow".Translate(Styles.Label(comp.style), Styles.Label(f), CS.Of(comp).autoFocusReason);
	}

	// "Balanced → Building" (for buttons and short lines)
	public static string ShortLabel(ForemanMapComponent comp)
	{
		if (comp == null)
			return "";
		ForemanStyle f = Current(comp);
		return f == None ? Styles.Label(comp.style) : (string)"CF_AutoFocusShort".Translate(Styles.Label(comp.style), Styles.Label(f));
	}

	// Re-evaluates the focus. Returns true when it changed (a re-plan is due).
	public static bool Update(Map map, ForemanMapComponent comp, MapDemand demand, bool force)
	{
		if (map == null || comp == null)
			return false;
		int now = Find.TickManager.TicksGame;
		if (!Active(comp))
		{
			// style switched or setting off: drop the focus silently
			if (CS.Of(comp).autoFocus != None)
			{
				CS.Of(comp).autoFocus = None;
				CS.Of(comp).autoFocusSince = now;
				CS.Of(comp).autoFocusReason = null;
			}
			return false;
		}
		if (!force && now - CS.Of(comp).autoFocusEval < MinEvalGap)
			return false;
		CS.Of(comp).autoFocusEval = now;

		List<Cand> cands;
		try
		{
			cands = Evaluate(map, comp, demand);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] auto focus: " + ex.Message, 0x57e3a10);
			return false;
		}

		Cand best = default;
		best.style = None;
		foreach (Cand c in cands)
			if (c.score >= 1f && (best.style == None || c.score > best.score))
				best = c;

		ForemanStyle cur = CS.Of(comp).autoFocus;
		Cand curC = default;
		curC.style = None;
		foreach (Cand c in cands)
			if (c.style == cur)
				curC = c;
		float curScore = cur == None ? 0f : curC.score;
		int held = now - CS.Of(comp).autoFocusSince;

		ForemanStyle next = cur;
		string reason = cur == None ? null : (curC.style == cur ? curC.reason : CS.Of(comp).autoFocusReason);
		if (cur == None)
		{
			if (best.style != None)
			{
				next = best.style;
				reason = best.reason;
			}
		}
		else if (best.style != None && best.style != cur && best.urgent && !(curC.urgent && curScore >= 1f))
		{
			// urgent need (food, after a raid) wins at once
			next = best.style;
			reason = best.reason;
		}
		else if (held >= MinHold || (held >= DropHold && curScore < 0.35f))
		{
			if (best.style != None && best.style != cur && (curScore < 0.6f || best.score > curScore * 1.25f))
			{
				next = best.style;
				reason = best.reason;
			}
			else if (curScore < 0.6f && (best.style == None || best.style != cur))
			{
				next = best.style;   // may be None
				reason = best.style == None ? null : best.reason;
			}
		}

		if (next == cur)
		{
			CS.Of(comp).autoFocusReason = reason;   // keep the numbers fresh
			return false;
		}
		CS.Of(comp).autoFocus = next;
		CS.Of(comp).autoFocusSince = now;
		CS.Of(comp).autoFocusReason = reason;
		CrisesSettings s = CrisesMod.S;
		if ((s == null || s.crisisMessages) && map.IsPlayerHome)
		{
			string text = next == None
				? (string)"CF_AutoFocusEnd".Translate(Styles.Label(cur))
				: (string)"CF_AutoFocusMsg".Translate(Styles.Label(next), reason ?? "");
			Messages.Message(text, MessageTypeDefOf.NeutralEvent, historical: false);
		}
		return true;
	}

	private static void Add(List<Cand> list, ForemanStyle style, float score, string reason, bool urgent = false)
	{
		if (score > 0f)
			list.Add(new Cand { style = style, score = score, reason = reason, urgent = urgent });
	}

	private static List<Cand> Evaluate(Map map, ForemanMapComponent comp, MapDemand demand)
	{
		var list = new List<Cand>();
		int n = Math.Max(1, MapDemand.Workers(map));
		int now = Find.TickManager.TicksGame;
		Crisis crisis = comp.CurrentCrisis;
		float foodDays = demand != null ? demand.foodDays : MapDemand.FoodDays(map);

		// Survival – food for less than 3 days (1.0 at 3 days, 3.0 at 0)
		if (foodDays < 3f)
			Add(list, ForemanStyle.Survival, 1f + (3f - foodDays) / 1.5f, "CF_AF_Food".Translate(foodDays.ToString("0.0")), urgent: true);

		// Farming – growing season and ripe fields, or a thin larder with fields to work
		bool hasFields = map.zoneManager.AllZones.Any(z => z is Zone_Growing);
		float temp = map.mapTemperature.OutdoorTemp;
		bool season = temp > 6f && temp < 42f;
		if (hasFields && season)
		{
			int ripe = demand?.harvestable ?? 0;
			float s = ripe / (10f + 5f * n);
			if (foodDays < 7f)
				s += (7f - foodDays) / 5f;
			Add(list, ForemanStyle.Farming, s, ripe > 0 ? "CF_AF_Harvest".Translate(ripe) : "CF_AF_Food".Translate(foodDays.ToString("0.0")));
		}

		// Building – blueprints, frames and construction marks
		int build = map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count
		            + map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count
		            + MapDemand.ConstructionMarks(map);
		Add(list, ForemanStyle.Building, build / (15f + 5f * n), "CF_AF_Build".Translate(build));

		// Mining & industry – steel and components running low, rock to mine
		int steel = map.resourceCounter.GetCount(ThingDefOf.Steel);
		int comps = map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial);
		float steelNeed = 60f + 30f * n;
		float compNeed = 2f + 0.5f * n;
		int mines = DesigCount(map, DesignationDefOf.Mine);
		float ind = Mathf.Clamp01(1f - steel / steelNeed) * 0.9f
		            + Mathf.Clamp01(1f - comps / compNeed) * 0.5f
		            + Mathf.Min(1f, mines / (20f + 5f * n)) * 0.5f;
		if (steel < steelNeed || comps < compNeed)
			Add(list, ForemanStyle.Industry, ind, "CF_AF_Industry".Translate(steel, comps));
		else
			Add(list, ForemanStyle.Industry, ind, "CF_AF_Mine".Translate(mines));

		// Production – many bills waiting at crafting, smithing, tailoring
		if (demand != null)
		{
			float over = 0f;
			foreach (var kv in demand.byType)
			{
				string d = kv.Key.defName;
				if (d == "Crafting" || d == "Smithing" || d == "Tailoring")
					over += Mathf.Max(0f, kv.Value.factor - 1f);
			}
			Add(list, ForemanStyle.Production, over / 1.2f, "CF_AF_Bills".Translate());
		}

		// Care – patients, prisoners, children
		int patients = demand != null ? demand.patients : MapDemand.CountPatients(map);
		int prisoners = map.mapPawns.PrisonersOfColonySpawnedCount;
		int kids = 0;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			if (p.DevelopmentalStage != DevelopmentalStage.Adult)
				kids++;
		float care = patients / (1f + 0.2f * n) * 0.7f + prisoners / (3f + 0.2f * n) + kids / (3f + 0.2f * n) * 0.8f;
		Add(list, ForemanStyle.Care, care, "CF_AF_Care".Translate(patients, prisoners, kids));

		// Defense – the two days after a raid (never during one: the Raid
		// crisis handles that), more when buildings are damaged
		if ((crisis & Crisis.Raid) == 0 && CS.Of(comp).lastRaidTick > 0 && now - CS.Of(comp).lastRaidTick < RaidMemory)
		{
			int since = now - CS.Of(comp).lastRaidTick;
			int repair = map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer).Count;
			float def = 1.2f * (1f - since / (float)RaidMemory) + Mathf.Min(1f, repair / (10f + 3f * n)) * 0.5f;
			Add(list, ForemanStyle.Defense, def, "CF_AF_Defense".Translate(repair), urgent: since < RaidMemory / 2);
		}

		// Ranching – animals to tame or slaughter, full udders / fleece, training
		int tame = DesigCount(map, DesignationDefOf.Tame) + DesigCount(map, DesignationDefOf.Slaughter);
		int ready = 0;
		foreach (Pawn a in map.mapPawns.SpawnedColonyAnimals)
		{
			if (a.Dead || a.Downed)
				continue;
			CompMilkable milk = a.TryGetComp<CompMilkable>();
			if (milk != null && milk.ActiveAndFull)
				ready++;
			CompShearable shear = a.TryGetComp<CompShearable>();
			if (shear != null && shear.ActiveAndFull)
				ready++;
			if (a.training != null && a.training.NextTrainableToTrain() != null)
				ready++;
		}
		Add(list, ForemanStyle.Ranching, (tame * 1.5f + ready) / (6f + n), "CF_AF_Animals".Translate(tame, ready));

		// Comfort – a dirty base
		int filth = map.listerFilthInHomeArea.FilthInHomeArea.Count;
		Add(list, ForemanStyle.Comfort, filth / (60f + 12f * n), "CF_AF_Dirt".Translate(filth));

		// 2.38: Tidy – a lot lying around to haul (and dirt)
		int haul = 0;
		foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
			if (t != null && t.Spawned && !t.IsForbidden(Faction.OfPlayer) && !MapDemand.IsChunk(t))
				haul++;
		Add(list, ForemanStyle.Tidy, (haul + filth * 0.4f) / (100f + 15f * n), "CF_AF_Tidy".Translate(haul, filth));

		// Research – only when nothing else needs a focus and the colony is calm
		// 2.19: and only when a research project is chosen and a research bench is built
		bool project = Find.ResearchManager != null && Find.ResearchManager.GetProject() != null
			&& map.listerBuildings.allBuildingsColonist.Any(b => b is Building_ResearchBench);
		if (project && crisis == Crisis.None && foodDays >= 8f && list.All(c => c.score < 0.5f))
			Add(list, ForemanStyle.Research, 1f, "CF_AF_Calm".Translate());

		return list;
	}

	private static int DesigCount(Map map, DesignationDef def)
	{
		return def == null ? 0 : map.designationManager.SpawnedDesignationsOfDef(def).Count();
	}
}
