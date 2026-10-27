using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;

namespace CaravanHelper;

// ===========================================================================
// Caravan Helper (Karavanní pomocník) – Bolek98
//  1. Supplies: the game's "select travel supplies" packs food only when a route
//     is chosen and never when you leave a site again. Here food for the trip
//     (and the way back), sensible medicine, and the drugs addicts need are
//     packed – also when you reform a caravan without a route.
//  2. Leaving a site: shows what the game counts as an enemy (so you can find
//     it), and – optional – lets you leave while only sleeping, hidden or
//     harmless enemies remain.
// ===========================================================================
public sealed class CHSettings : ModSettings
{
	public bool autoForReform = true;
	public int defaultDays = 4;
	public bool returnTrip = true;
	public int extraDays = 1;
	public int medicinePerColonist = 2;
	public bool preferNormalMedicine = true;
	public bool addictDrugs = true;
	public bool showBlockers = true;
	public bool leavePastSleeping = true;
	// 1.0.3: map exit side when forming, sharing inventory on arrival
	public bool chooseExit = true;
	public bool shareOnArrival = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref autoForReform, "autoForReform", true);
		Scribe_Values.Look(ref defaultDays, "defaultDays", 4);
		Scribe_Values.Look(ref returnTrip, "returnTrip", true);
		Scribe_Values.Look(ref extraDays, "extraDays", 1);
		Scribe_Values.Look(ref medicinePerColonist, "medicinePerColonist", 2);
		Scribe_Values.Look(ref preferNormalMedicine, "preferNormalMedicine", true);
		Scribe_Values.Look(ref addictDrugs, "addictDrugs", true);
		Scribe_Values.Look(ref showBlockers, "showBlockers", true);
		Scribe_Values.Look(ref leavePastSleeping, "leavePastSleeping", true);
		Scribe_Values.Look(ref chooseExit, "chooseExit", true);
		Scribe_Values.Look(ref shareOnArrival, "shareOnArrival", true);
	}
}

public sealed class CaravanHelperMod : Mod
{
	public static CHSettings S;

	public CaravanHelperMod(ModContentPack content) : base(content)
	{
		S = GetSettings<CHSettings>();
		new Harmony("bolek98.caravanhelper").PatchAll(typeof(CaravanHelperMod).Assembly);
	}

	public override string SettingsCategory() => "CH_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect)
	{
		var l = new Listing_Standard();
		l.Begin(inRect);
		l.Label("CH_SecSupplies".Translate());
		l.CheckboxLabeled("CH_AutoReform".Translate(), ref S.autoForReform, "CH_AutoReformTip".Translate());
		l.Label("CH_Days".Translate(S.defaultDays), tooltip: "CH_DaysTip".Translate());
		S.defaultDays = Mathf.RoundToInt(Slider(l.GetRect(22f), S.defaultDays, 1, 20));
		l.CheckboxLabeled("CH_Return".Translate(), ref S.returnTrip, "CH_ReturnTip".Translate());
		l.Label("CH_Extra".Translate(S.extraDays));
		S.extraDays = Mathf.RoundToInt(Slider(l.GetRect(22f), S.extraDays, 0, 10));
		l.Label("CH_Medicine".Translate(S.medicinePerColonist));
		S.medicinePerColonist = Mathf.RoundToInt(Slider(l.GetRect(22f), S.medicinePerColonist, 0, 6));
		l.CheckboxLabeled("CH_NormalMed".Translate(), ref S.preferNormalMedicine, "CH_NormalMedTip".Translate());
		l.CheckboxLabeled("CH_Addicts".Translate(), ref S.addictDrugs, "CH_AddictsTip".Translate());
		l.GapLine();
		l.Label("CH_SecLeave".Translate());
		l.CheckboxLabeled("CH_Blockers".Translate(), ref S.showBlockers, "CH_BlockersTip".Translate());
		l.CheckboxLabeled("CH_LeaveSleeping".Translate(), ref S.leavePastSleeping, "CH_LeaveSleepingTip".Translate());
		// 1.0.3
		l.GapLine();
		l.Label("CH_SecTravel".Translate());
		l.CheckboxLabeled("CH_ChooseExit".Translate(), ref S.chooseExit, "CH_ChooseExitTip".Translate());
		l.CheckboxLabeled("CH_Share".Translate(), ref S.shareOnArrival, "CH_ShareTip".Translate());
		l.End();
	}

	private static float Slider(Rect r, float v, float min, float max)
	{
		float nv = Widgets.HorizontalSlider(r, v, min, max, true);
		return nv == v ? v : Mathf.Round(nv);
	}
}

// ---------------------------------------------------------------------------
// 1. supplies
// ---------------------------------------------------------------------------
[HarmonyPatch(typeof(Dialog_FormCaravan), MethodType.Constructor, new[] { typeof(Map), typeof(bool), typeof(Action), typeof(bool), typeof(IntVec3?) })]
public static class Patch_DialogCtor
{
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, bool> autoRef = AccessTools.FieldRefAccess<Dialog_FormCaravan, bool>("autoSelectTravelSupplies");

	public static void Postfix(Dialog_FormCaravan __instance, bool reform)
	{
		if (reform && CaravanHelperMod.S.autoForReform)
			autoRef(__instance) = true;
	}
}

// 1.0.1: pawns without medical settings (mechanoids, vehicles, some mod pawns) made the game's
// own medicine choice throw every frame – the caravan window broke. They get no medicine.
[HarmonyPatch(typeof(Dialog_FormCaravan), "BestMedicineItemFor")]
public static class Patch_BestMedicineGuard
{
	public static bool Prefix(Pawn pawn, ref Transferable __result)
	{
		if (pawn?.playerSettings != null)
			return true;
		__result = null;
		return false;
	}
}

[HarmonyPatch(typeof(Dialog_FormCaravan), "SelectApproximateBestTravelSupplies")]
public static class Patch_Supplies
{
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, PlanetTile> destRef = AccessTools.FieldRefAccess<Dialog_FormCaravan, PlanetTile>("destinationTile");
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, Map> mapRef = AccessTools.FieldRefAccess<Dialog_FormCaravan, Map>("map");
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, bool> massDirty = AccessTools.FieldRefAccess<Dialog_FormCaravan, bool>("massUsageDirty");
	private static readonly AccessTools.FieldRef<Dialog_FormCaravan, bool> foodDirty = AccessTools.FieldRefAccess<Dialog_FormCaravan, bool>("daysWorthOfFoodDirty");
	private static readonly System.Reflection.PropertyInfo ticksProp = AccessTools.Property(typeof(Dialog_FormCaravan), "TicksToArrive");
	private static readonly System.Reflection.PropertyInfo invProp = AccessTools.Property(typeof(Dialog_FormCaravan), "IgnoreInventoryMode");

	public static void Postfix(Dialog_FormCaravan __instance)
	{
		try
		{
			Improve(__instance);
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Caravan Helper] supplies: " + ex, 0x6a7c01);
		}
	}

	private static void Dirty(Dialog_FormCaravan d)
	{
		massDirty(d) = true;
		foodDirty(d) = true;
	}

	private static void Improve(Dialog_FormCaravan d)
	{
		CHSettings s = CaravanHelperMod.S;
		List<TransferableOneWay> tr = d.transferables;
		if (tr == null)
			return;
		List<Pawn> pawns = TransferableUtility.GetPawnsFromTransferables(tr);
		if (pawns.Count == 0)
			return;
		var people = pawns.Where(p => p.RaceProps.Humanlike && !p.IsPrisoner && !(ModsConfig.BiotechActive && p.IsColonyMech)).ToList();
		PlanetTile dest = destRef(d);
		Map map = mapRef(d);

		// ---- how many days of food ----
		float trip = s.defaultDays;
		if (dest.Valid)
		{
			try { trip = (int)ticksProp.GetValue(d) / 60000f; }
			catch { trip = s.defaultDays; }
		}
		bool goingHome = dest.Valid && Find.WorldObjects.SettlementAt(dest)?.Faction == Faction.OfPlayer;
		float want = trip * (s.returnTrip && !goingHome ? 2f : 1f) + s.extraDays;

		// ---- medicine: replace the game's choice (it takes the strongest, glitterworld first) ----
		var meds = tr.Where(t => t.HasAnyThing && t.ThingDef.IsMedicine).ToList();
		foreach (TransferableOneWay t in meds)
			t.AdjustTo(0);
		int medWanted = people.Count(p => !p.AnimalOrWildMan()) * s.medicinePerColonist;
		for (int i = 0; i < medWanted; i++)
		{
			Pawn forPawn = people.Count > 0 ? people[i % people.Count] : null;
			TransferableOneWay best = null;
			float bestScore = float.MinValue;
			foreach (TransferableOneWay t in meds)
			{
				if (!t.CanAdjustBy(1).Accepted)
					continue;
				if (forPawn?.playerSettings != null && !forPawn.playerSettings.medCare.AllowsMedicine(t.ThingDef))
					continue;
				float pot = t.AnyThing.GetStatValue(StatDefOf.MedicalPotency);
				// normal medicine first, then herbal, the precious ones last
				float score = s.preferNormalMedicine ? -Mathf.Abs(pot - 1f) : pot;
				if (score > bestScore)
				{
					bestScore = score;
					best = t;
				}
			}
			if (best == null || !AddIfMassAllows(d, best, 1))
				break;
		}

		// ---- drugs for addicts (luciferium, go-juice …) ----
		if (s.addictDrugs)
		{
			foreach (Pawn p in people)
			{
				if (p.health?.hediffSet == null)
					continue;
				foreach (Hediff h in p.health.hediffSet.hediffs)
				{
					if (!(h is Hediff_Addiction add) || add.Chemical == null)
						continue;
					float fall = add.Need?.def?.fallPerDay ?? 0.3f;
					var drugs = tr.Where(t => t.HasAnyThing && t.ThingDef.GetCompProperties<CompProperties_Drug>()?.chemical == add.Chemical).ToList();
					if (drugs.Count == 0)
						continue;
					foreach (TransferableOneWay t in drugs.OrderByDescending(t => t.ThingDef.GetCompProperties<CompProperties_Drug>().needLevelOffset))
					{
						float per = Math.Max(0.1f, t.ThingDef.GetCompProperties<CompProperties_Drug>().needLevelOffset);
						int count = Mathf.CeilToInt(want * fall / per) + 1;
						int have = t.CountToTransfer;
						if (have < count)
							AddIfMassAllows(d, t, count - have);
						break;
					}
				}
			}
		}

		// ---- food up to the wanted days ----
		IgnorePawnsInventoryMode inv = IgnorePawnsInventoryMode.IgnoreIfAssignedToUnload;
		try { inv = (IgnorePawnsInventoryMode)invProp.GetValue(d); }
		catch { }
		float days = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(tr, map.Tile, inv, Faction.OfPlayer);
		if (days >= want)
		{
			Dirty(d);
			return;
		}
		int wantTicks = (int)(want * 60000f);
		var foods = tr.Where(t => t.HasAnyThing && t.ThingDef.IsNutritionGivingIngestible && !t.ThingDef.IsDrug && !(t.AnyThing is Corpse)
			&& t.ThingDef.ingestible.HumanEdible && pawns.Any(p => p.RaceProps.Humanlike && CaravanPawnsNeedsUtility.CanEatForNutritionEver(t.ThingDef, p)))
			.OrderByDescending(t => FoodScore(t, wantTicks)).ToList();
		int guard = 0;
		foreach (TransferableOneWay t in foods)
		{
			while (days < want && t.CanAdjustBy(1).Accepted && guard++ < 2000)
			{
				int step = Math.Max(1, Mathf.CeilToInt(t.GetMaximumToTransfer() / 20f));
				if (!AddIfMassAllows(d, t, step))
					break;
				days = DaysWorthOfFoodCalculator.ApproxDaysWorthOfFood(tr, map.Tile, inv, Faction.OfPlayer);
			}
			if (days >= want)
				break;
		}
		Dirty(d);
	}

	// survival meals and pemmican first, cooked meals while they keep, raw food last; light for its nutrition
	private static float FoodScore(TransferableOneWay t, int wantTicks)
	{
		ThingDef def = t.ThingDef;
		float score = 0f;
		CompProperties_Rottable rot = def.GetCompProperties<CompProperties_Rottable>();
		if (rot == null)
			score += 3f;
		else if (rot.TicksToRotStart >= wantTicks)
			score += 1.5f;
		else
			score -= 2f;
		switch (def.ingestible.preferability)
		{
			case FoodPreferability.MealLavish: score += 2f; break;
			case FoodPreferability.MealFine: score += 1.6f; break;
			case FoodPreferability.MealSimple: score += 1.2f; break;
			case FoodPreferability.MealAwful: score += 0.3f; break;
			case FoodPreferability.RawTasty: score += 0.4f; break;
			case FoodPreferability.RawBad: score -= 0.5f; break;
			case FoodPreferability.DesperateOnly:
			case FoodPreferability.DesperateOnlyForHumanlikes: score -= 3f; break;
		}
		float nut = def.GetStatValueAbstract(StatDefOf.Nutrition);
		float mass = Math.Max(0.01f, def.BaseMass);
		score += Mathf.Clamp(nut / mass, 0f, 4f) * 0.3f;
		return score;
	}

	private static bool AddIfMassAllows(Dialog_FormCaravan d, TransferableOneWay t, int count)
	{
		massDirty(d) = true;
		float free = d.MassCapacity - d.MassUsage;
		int byMass = t.ThingDef.BaseMass <= 0f ? count : Mathf.FloorToInt(free / t.ThingDef.BaseMass);
		int n = Math.Min(count, Math.Min(byMass, t.GetMaximumToTransfer() - t.CountToTransfer));
		if (n <= 0 || !t.CanAdjustBy(n).Accepted)
			return false;
		t.AdjustBy(n);
		massDirty(d) = true;
		foodDirty(d) = true;
		return true;
	}
}

// ---------------------------------------------------------------------------
// 2. leaving a site
// ---------------------------------------------------------------------------
public static class Threats
{
	// everything the game counts as an enemy that keeps you from reforming
	public static List<Thing> Blocking(Map map)
	{
		var list = new List<Thing>();
		foreach (IAttackTarget t in map.attackTargetsCache.TargetsHostileToFaction(Faction.OfPlayer))
		{
			if (GenHostility.IsActiveThreatTo(t, Faction.OfPlayer, true, true))
				list.Add(t.Thing);
			else if (t.Thing is Pawn p && t.Thing.HostileTo(Faction.OfPlayer) && !t.ThreatDisabled(null))
			{
				CompCanBeDormant c = p.GetComp<CompCanBeDormant>();
				if (c != null && !c.Awake)
					list.Add(p);
			}
		}
		return list;
	}

	// enemies that can really hurt you right now: awake, visible, standing pawns
	public static bool Real(Thing t)
	{
		if (!(t is Pawn p) || !p.Spawned || p.Downed || p.Dead || p.Fogged() || !p.Awake())
			return false;
		CompCanBeDormant c = p.GetComp<CompCanBeDormant>();
		if (c != null && !c.Awake)
			return false;
		if (p.IsPrisoner || p.MentalStateDef == MentalStateDefOf.PanicFlee)
			return false;
		return p.HostileTo(Faction.OfPlayer);
	}

	public static bool AnyReal(Map map)
	{
		// 1.0.2: the gizmos and the game's threat check ask every frame while the
		// site is selected – the answer is kept for half a second of game time
		// (and at most one real second while paused).
		int tick = Find.TickManager.TicksGame;
		float now = Time.realtimeSinceStartup;
		if (cacheMap == map && tick - cacheTick < 30 && tick >= cacheTick && now - cacheTime < 1f)
			return cacheReal;
		cacheReal = Blocking(map).Any(Real);
		cacheMap = map;
		cacheTick = tick;
		cacheTime = now;
		return cacheReal;
	}

	// 1.0.2: same short cache for the "who blocks" button
	public static List<Thing> BlockingCached(Map map)
	{
		int tick = Find.TickManager.TicksGame;
		float now = Time.realtimeSinceStartup;
		if (listMap == map && listCache != null && tick - listTick < 30 && tick >= listTick && now - listTime < 1f)
			return listCache;
		listCache = Blocking(map);
		listMap = map;
		listTick = tick;
		listTime = now;
		return listCache;
	}

	private static Map listMap;
	private static List<Thing> listCache;
	private static int listTick;
	private static float listTime;
	private static Map cacheMap;
	private static int cacheTick;
	private static float cacheTime;
	private static bool cacheReal;
}

[HarmonyPatch(typeof(FormCaravanComp), "AnyActiveThreatNow", MethodType.Getter)]
public static class Patch_ThreatNow
{
	public static void Postfix(FormCaravanComp __instance, ref bool __result)
	{
		if (!__result || !CaravanHelperMod.S.leavePastSleeping || !__instance.Reform)
			return;
		try
		{
			MapParent mp = (MapParent)__instance.parent;
			if (mp.HasMap && !Threats.AnyReal(mp.Map))
				__result = false;
		}
		catch
		{
		}
	}
}

[HarmonyPatch(typeof(FormCaravanComp), nameof(FormCaravanComp.GetGizmos))]
public static class Patch_Gizmos
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, FormCaravanComp __instance)
	{
		MapParent mp = __instance.parent as MapParent;
		Map map = mp != null && mp.HasMap ? mp.Map : null;
		bool realThreat = map != null && Threats.AnyReal(map);
		foreach (Gizmo g in gizmos)
		{
			// the game greys out "reform" for any enemy, sleeping ones too
			if (g is Command c && CaravanHelperMod.S.leavePastSleeping && c.Disabled && !realThreat && c.defaultLabel == "CommandReformCaravan".Translate())
			{
				c.Disabled = false;
				c.disabledReason = null;
			}
			yield return g;
		}
		if (map == null || !__instance.Reform || !CaravanHelperMod.S.showBlockers)
			yield break;
		List<Thing> blocking;
		try { blocking = Threats.BlockingCached(map); }
		catch { yield break; }
		if (blocking.Count == 0)
			yield break;
		var cmd = new Command_Action
		{
			defaultLabel = "CH_WhoBlocks".Translate(blocking.Count),
			defaultDesc = "CH_WhoBlocksDesc".Translate(),
			icon = TexCommand.Attack,
			action = () =>
			{
				var opts = new List<FloatMenuOption>();
				foreach (Thing t in blocking.Take(30))
				{
					Thing local = t;
					string state = Threats.Real(t) ? "CH_StateActive".Translate() : t.Fogged() ? "CH_StateHidden".Translate() : "CH_StateSleeping".Translate();
					opts.Add(new FloatMenuOption(t.LabelCap + " (" + state + ")", () => CameraJumper.TryJumpAndSelect(local)));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
		};
		yield return cmd;
	}
}
