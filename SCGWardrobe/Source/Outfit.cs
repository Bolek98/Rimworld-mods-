using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ResearchedLoadout;
using RimWorld;
using Verse;
using Verse.AI;

namespace SCGWardrobe;

// 0.1.1: changing into sleepwear / festive clothes and back without dropping anything on the floor.
// The vanilla Wear job drops every piece it replaces where the pawn stands – every night a pile of clothes
// lay next to the beds. Now the replaced pieces go into the pawn's inventory ("day outfit") and come back
// on in the morning / after the occasion; the occasion pieces then stay in his inventory for the next time.
[DefOf]
public static class WRJobDefOf
{
	public static JobDef WR_ChangeOutfit;
	public static JobDef WR_RestoreOutfit;
	static WRJobDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(WRJobDefOf)); }
}

public static class Outfit
{
	private static WardrobeSettings S => WardrobeMod.Settings;

	public static bool IsOccasion(ThingDef d) => WardrobeProvider.IsSleepwear(d) || WardrobeProvider.IsFestive(d);

	// is this inventory thing one of the clothes Wardrobe keeps there (day outfit or occasion clothes)
	public static bool IsKept(Thing t)
	{
		WardrobeGame g = WardrobeGame.Get();
		return g != null && t is Apparel && g.carried.Contains(t.thingIDNumber);
	}

	public static bool Changing(Pawn p)
	{
		JobDef d = p?.CurJob?.def;
		return d != null && (d == WRJobDefOf.WR_ChangeOutfit || d == WRJobDefOf.WR_RestoreOutfit);
	}

	public static bool HasDay(Pawn p)
	{
		WardrobeGame g = WardrobeGame.Get();
		return g != null && g.dayOf.TryGetValue(p.thingIDNumber, out var l) && l != null && l.Count > 0;
	}

	public static bool WearsOccasion(Pawn p) => p.apparel.WornApparel.Any(a => IsOccasion(a.def));

	private static List<Apparel> Conflicts(Pawn p, ThingDef def)
	{
		var list = new List<Apparel>();
		foreach (Apparel w in p.apparel.WornApparel)
			if (!ApparelUtility.CanWearTogether(def, w.def, p.RaceProps.body)) list.Add(w);
		return list;
	}

	private static bool Stuck(Pawn p, Apparel a) => p.apparel.IsLocked(a) || (p.outfits?.forcedHandler != null && p.outfits.forcedHandler.IsForced(a));

	// an occasion piece in his own inventory first, then one lying in reach
	public static Apparel FindPiece(Pawn p, bool sleep, Func<Pawn, bool, Apparel> onMap)
	{
		Func<ThingDef, bool> isOcc = sleep ? (Func<ThingDef, bool>)WardrobeProvider.IsSleepwear : WardrobeProvider.IsFestive;
		foreach (Thing t in p.inventory.innerContainer)
			if (t is Apparel a && isOcc(a.def) && Classify.Fits(p, a) && !ApparelFlip.Blocked(p, a.def)) return a;
		return onMap(p, sleep);
	}

	// can he change into this piece without dropping anything?
	public static bool CanChange(Pawn p, Apparel piece)
	{
		if (piece == null || piece.Destroyed) return false;
		foreach (Apparel w in Conflicts(p, piece.def))
			if (Stuck(p, w)) return false;
		// the replaced pieces only move from his body into his inventory; only a piece taken from the map adds mass
		bool fromInv = piece.ParentHolder is Pawn_InventoryTracker;
		if (!fromInv && MassUtility.FreeSpace(p) < piece.GetStatValue(StatDefOf.Mass)) return false;
		return true;
	}

	public static int ChangeTicks(Pawn p, Apparel piece)
	{
		if (piece == null) return 60;
		float s = piece.GetStatValue(StatDefOf.EquipDelay);
		foreach (Apparel w in Conflicts(p, piece.def)) s += w.GetStatValue(StatDefOf.EquipDelay);
		return Math.Max(30, (int)(s * 60f));
	}

	public static int RestoreTicks(Pawn p)
	{
		float s = 0f;
		foreach (Apparel w in p.apparel.WornApparel) if (IsOccasion(w.def)) s += w.GetStatValue(StatDefOf.EquipDelay);
		foreach (Apparel a in DayItems(p)) s += a.GetStatValue(StatDefOf.EquipDelay);
		return Math.Max(30, (int)(s * 60f));
	}

	public static void StartChange(Pawn p, Apparel piece)
	{
		Job job = JobMaker.MakeJob(WRJobDefOf.WR_ChangeOutfit, piece);
		p.jobs.StartJob(job, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false);
	}

	public static void StartRestore(Pawn p)
	{
		Job job = JobMaker.MakeJob(WRJobDefOf.WR_RestoreOutfit);
		p.jobs.StartJob(job, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false);
	}

	private static IEnumerable<Apparel> DayItems(Pawn p)
	{
		WardrobeGame g = WardrobeGame.Get();
		if (g == null || !g.dayOf.TryGetValue(p.thingIDNumber, out var ids) || ids == null) yield break;
		foreach (Thing t in p.inventory.innerContainer)
			if (t is Apparel a && ids.Contains(a.thingIDNumber)) yield return a;
	}

	// the end of the change job: old pieces into the inventory, the occasion piece on
	public static bool DoChange(Pawn p, Apparel piece)
	{
		WardrobeGame g = WardrobeGame.Get();
		if (g == null || !CanChange(p, piece)) return false;
		ThingOwner inv = p.inventory.innerContainer;
		if (!g.dayOf.TryGetValue(p.thingIDNumber, out var day) || day == null) g.dayOf[p.thingIDNumber] = day = new List<int>();
		foreach (Apparel w in Conflicts(p, piece.def))
		{
			if (IsOccasion(w.def))
			{
				// another occasion piece (e.g. festive → sleep): keep it with the occasion clothes
				p.apparel.Remove(w);
				if (inv.TryAdd(w, false)) g.carried.Add(w.thingIDNumber);
				else Haul(p, w);
				continue;
			}
			p.apparel.Remove(w);
			if (inv.TryAdd(w, false)) { day.Add(w.thingIDNumber); g.carried.Add(w.thingIDNumber); }
			else Haul(p, w);
		}
		if (piece.ParentHolder is Pawn_InventoryTracker) inv.Remove(piece);
		else if (piece.Spawned) piece.DeSpawn();
		g.carried.Remove(piece.thingIDNumber);
		p.apparel.Wear(piece, dropReplacedApparel: false);
		return true;
	}

	// back to the day outfit: occasion pieces into the inventory (kept for next time), day pieces back on
	public static void DoRestore(Pawn p)
	{
		WardrobeGame g = WardrobeGame.Get();
		if (g == null) return;
		ThingOwner inv = p.inventory.innerContainer;
		foreach (Apparel w in p.apparel.WornApparel.ToList())
		{
			if (!IsOccasion(w.def) || Stuck(p, w)) continue;
			p.apparel.Remove(w);
			if (inv.TryAdd(w, false)) g.carried.Add(w.thingIDNumber);
			else Haul(p, w);
		}
		foreach (Apparel a in DayItems(p).ToList())
		{
			g.carried.Remove(a.thingIDNumber);
			bool ok = a.PawnCanWear(p, ignoreGender: true) && ApparelUtility.HasPartsToWear(p, a.def) && !Conflicts(p, a.def).Any();
			if (!ok)
			{
				// something else was put on meanwhile – take this piece to storage instead of leaving it in his pockets
				inv.Remove(a);
				Haul(p, a);
				continue;
			}
			inv.Remove(a);
			p.apparel.Wear(a, dropReplacedApparel: false);
		}
		g.dayOf.Remove(p.thingIDNumber);
	}

	// no time for a job (drafted, raid, downed): swap at once
	public static void InstantRestore(Pawn p)
	{
		try { if (WearsOccasion(p) || HasDay(p)) DoRestore(p); }
		catch (Exception e) { Log.WarningOnce("[SCG – Wardrobe] restore: " + e.Message, 8190011); }
	}

	// a piece that can't go into the inventory or back on: to storage at once (never left on the floor)
	private static void Haul(Pawn p, Thing t)
	{
		if (p.Map == null) { p.inventory.innerContainer.TryAdd(t, true); return; }
		if (!GenPlace.TryPlaceThing(t, p.PositionHeld, p.Map, ThingPlaceMode.Near)) return;
		t.SetForbidden(false, false);
		if (StoreUtility.TryFindBestBetterStorageFor(t, p, p.Map, StoreUtility.CurrentStoragePriorityOf(t), p.Faction, out IntVec3 cell, out IHaulDestination dest))
		{
			Job job = HaulAIUtility.HaulToCellStorageJob(p, t, cell, false);
			if (job != null) p.jobs.jobQueue.EnqueueFirst(job);
		}
	}
}

public class JobDriver_WRChangeOutfit : JobDriver
{
	private int duration;
	private Apparel Piece => job.GetTarget(TargetIndex.A).Thing as Apparel;
	private bool InInventory => Piece != null && !Piece.Spawned && Piece.ParentHolder is Pawn_InventoryTracker t && t.pawn == pawn;

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref duration, "duration", 0);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) => InInventory || pawn.Reserve(Piece, job, 1, -1, null, errorOnFailed);

	public override void Notify_Starting()
	{
		base.Notify_Starting();
		duration = Outfit.ChangeTicks(pawn, Piece);
	}

	protected override IEnumerable<Toil> MakeNewToils()
	{
		this.FailOn(() => Piece == null || Piece.Destroyed || pawn.Drafted || !Outfit.CanChange(pawn, Piece));
		bool inv = InInventory;
		if (!inv) yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.A);
		Toil wait = Toils_General.Wait(duration);
		if (!inv) wait.WithProgressBarToilDelay(TargetIndex.A);
		wait.FailOn(() => !inv && (Piece == null || !Piece.Spawned));
		yield return wait;
		yield return Toils_General.Do(() => Outfit.DoChange(pawn, Piece));
	}
}

public class JobDriver_WRRestoreOutfit : JobDriver
{
	private int duration;

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref duration, "duration", 0);
	}

	public override bool TryMakePreToilReservations(bool errorOnFailed) => true;

	public override void Notify_Starting()
	{
		base.Notify_Starting();
		duration = Outfit.RestoreTicks(pawn);
	}

	protected override IEnumerable<Toil> MakeNewToils()
	{
		yield return Toils_General.Wait(duration);
		yield return Toils_General.Do(() =>
		{
			Outfit.DoRestore(pawn);
			WardrobeGame.Get()?.hold.Remove(pawn.thingIDNumber);
			pawn.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 30;
		});
	}
}

// the game must not unload the kept clothes to storage (it only would after a caravan, "unload everything")
[HarmonyPatch(typeof(Pawn_InventoryTracker), nameof(Pawn_InventoryTracker.FirstUnloadableThing), MethodType.Getter)]
public static class Patch_KeepWardrobeItems
{
	public static void Postfix(Pawn_InventoryTracker __instance, ref ThingCount __result)
	{
		try
		{
			if (__result.Thing == null || !Outfit.IsKept(__result.Thing)) return;
			foreach (Thing t in __instance.innerContainer)
			{
				if (Outfit.IsKept(t) || t.def.IsIngestible) continue;
				__result = new ThingCount(t, t.stackCount);
				return;
			}
			__result = default(ThingCount);
		}
		catch { }
	}
}
