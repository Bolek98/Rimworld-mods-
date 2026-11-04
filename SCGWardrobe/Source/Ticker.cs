using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using ResearchedLoadout;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SCGWardrobe;

// One colonist per slice (every 90 ticks), so even a big colony costs almost nothing.
public static class Ticker
{
	private const int Every = 90;
	private static int index;
	private static readonly List<Pawn> pawns = new List<Pawn>();
	private static int listBuilt = -99999;

	private static WardrobeSettings S => WardrobeMod.Settings;

	public static void Tick()
	{
		if (S == null || !S.enabled) return;
		int now = Find.TickManager.TicksGame;
		if (now % Every != 17) return;
		try
		{
			if (now - listBuilt > 2500 || pawns.Count == 0)
			{
				listBuilt = now;
				pawns.Clear();
				foreach (Map m in Find.Maps)
					if (m.IsPlayerHome || m.mapPawns.FreeColonistsSpawnedCount > 0)
						pawns.AddRange(m.mapPawns.FreeColonistsSpawned);
				Retire.Maps();
			}
			if (pawns.Count == 0) return;
			index = (index + 1) % pawns.Count;
			Pawn p = pawns[index];
			if (p == null || !p.Spawned || p.Dead || p.apparel == null) return;
			Handle(p);
		}
		catch (Exception e)
		{
			Log.WarningOnce("[SCG – Wardrobe] tick: " + e, 8190001);
		}
	}

	private static void Log2(Pawn p, string s)
	{
		if (S.verbose) Log.Message("[SCG – Wardrobe] " + p.LabelShort + ": " + s);
	}

	private static void Handle(Pawn p)
	{
		WardrobeGame g = WardrobeGame.Get();
		if (g == null || g.Exempt(p) || !LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) return;
		bool danger = p.Drafted || SteadyThreat.Active(p.Map) || p.Downed || p.InMentalState;
		string hold = WardrobeProvider.HoldOf(p);
		if (danger)
		{
			// 0.1.1: no time to change – day clothes back on at once, SCG takes over (battle gear)
			if (Outfit.WearsOccasion(p) || Outfit.HasDay(p)) { Outfit.InstantRestore(p); Log2(p, "back in day clothes (danger)"); }
			if (hold != null) SetHold(p, g, hold, null);
			return;
		}
		if (Outfit.Changing(p)) return;   // a change is on its way
		string want = WantedHold(p);
		bool started = false;
		if (want != null)
		{
			bool sleep = want == "sleep";
			if (Wears(p, sleep)) { if (hold != want) SetHold(p, g, hold, want); }
			else if (hold != null && hold != want && Outfit.WearsOccasion(p))
			{
				// festive → sleep (or back): first the day clothes, the next slice changes again
				if (Free(p, sleep)) { Outfit.StartRestore(p); started = true; }
			}
			else if (Free(p, sleep) && Outfit.FindPiece(p, sleep, FindPiece) is Apparel piece && Outfit.CanChange(p, piece))
			{
				SetHold(p, g, hold, want);
				Outfit.StartChange(p, piece);
				Log2(p, "changes into " + piece.LabelShort + (sleep ? " for the night" : " for the occasion"));
				started = true;
			}
			else if (hold != null && !Outfit.WearsOccasion(p)) SetHold(p, g, hold, null);
		}
		else if (Outfit.WearsOccasion(p) || Outfit.HasDay(p))
		{
			// the night / occasion is over: day clothes back on (the hold stays until then, so SCG doesn't re-dress meanwhile)
			if (CanRestoreNow(p)) { Outfit.StartRestore(p); Log2(p, "back into his day clothes"); started = true; }
		}
		else if (hold != null) SetHold(p, g, hold, null);
		if (started) return;
		if (!Idle(p)) return;
		if (S.uniforms) Recolor(p);
		if (S.favorites && S.favoritesToBedroom && HaulFavorite(p, g)) return;
	}

	private static bool CanRestoreNow(Pawn p)
	{
		Job j = p.CurJob;
		if (j == null) return true;
		if (p.jobs.curDriver != null && p.jobs.curDriver.asleep) return false;
		return !j.playerForced;
	}

	// ---------------- occasions ----------------
	private static bool NightNow(Pawn p)
	{
		int h = GenLocalDate.HourOfDay(p.Map);
		return S.sleepFrom <= S.sleepTo ? h >= S.sleepFrom && h < S.sleepTo : h >= S.sleepFrom || h < S.sleepTo;
	}

	private static string WantedHold(Pawn p)
	{
		if (S.festive && FestiveNow(p) && S.FestiveDefs.Count > 0) return "festive";
		if (S.sleepwear && S.SleepDefs.Count > 0 && NightNow(p))
		{
			Job j = p.CurJob;
			if (j != null && j.def == JobDefOf.LayDown) return "sleep";
			if (WardrobeProvider.HoldOf(p) == "sleep") return "sleep";   // keep it on through the night
		}
		return null;
	}

	private static MethodInfoCache holidayToday = new MethodInfoCache("SeasonsHolidays.Holidays", "Today", typeof(Map));
	private static MethodInfoCache holidayOn = new MethodInfoCache("SeasonsHolidays.Holidays", "On", null);

	public static bool FestiveNow(Pawn p)
	{
		Lord lord = p.GetLord();
		if (lord?.LordJob is LordJob_Joinable_Party || lord?.LordJob is LordJob_Ritual) return true;
		if (!ModsConfig.IsActive("misak.seasonsholidays") || p.Map == null) return false;
		object h = holidayToday.Call(null, p.Map);
		if (h == null) return false;
		object on = holidayOn.Call(null, h);
		return on == null || (on is bool b && b);
	}

	// he may stop what he does to change: before sleep (in bed, not asleep yet), or idle / at a party
	private static bool Free(Pawn p, bool sleep)
	{
		Job j = p.CurJob;
		if (j == null) return true;
		if (p.jobs.curDriver != null && p.jobs.curDriver.asleep) return false;
		if (sleep) return j.def == JobDefOf.LayDown || Idle(p);
		if (j.playerForced) return false;
		return Idle(p) || j.def.joyKind != null || p.GetLord() != null && !(p.mindState?.duty?.def == DutyDefOf.Spectate);
	}

	private static bool Idle(Pawn p)
	{
		Job j = p.CurJob;
		if (j == null) return true;
		if (j.playerForced) return false;
		JobDef d = j.def;
		return d == JobDefOf.Wait || d == JobDefOf.Wait_Wander || d == JobDefOf.GotoWander || d == JobDefOf.Wait_MaintainPosture || d.joyKind != null;
	}

	private static void SetHold(Pawn p, WardrobeGame g, string hold, string want)
	{
		if (want == null) g.hold.Remove(p.thingIDNumber); else g.hold[p.thingIDNumber] = want;
		// let the game re-dress him soon (back to work clothes after the occasion)
		p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame + 30;
		Log2(p, "hold " + (hold ?? "-") + " → " + (want ?? "-"));
	}

	private static bool Wears(Pawn p, bool sleep)
	{
		Func<ThingDef, bool> isOcc = sleep ? (Func<ThingDef, bool>)WardrobeProvider.IsSleepwear : WardrobeProvider.IsFestive;
		return p.apparel.WornApparel.Any(a => isOcc(a.def));
	}

	public static Apparel FindPiece(Pawn p, bool sleep)
	{
		Func<ThingDef, bool> isOcc = sleep ? (Func<ThingDef, bool>)WardrobeProvider.IsSleepwear : WardrobeProvider.IsFestive;
		Apparel best = null;
		float bestD = float.MaxValue;
		WardrobeGame g = WardrobeGame.Get();
		foreach (Thing t in p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
		{
			if (!(t is Apparel a) || !isOcc(a.def) || a.IsForbidden(p) || !t.Spawned) continue;
			if (S.noCorpse && a.WornByCorpse) continue;
			if (g != null && S.favorites) { int o = g.OwnerOf(a); if (o >= 0 && o != p.thingIDNumber) continue; }
			if (!Classify.Fits(p, a) || ApparelFlip.Blocked(p, a.def)) continue;
			if (p.outfits?.CurrentApparelPolicy != null && !p.outfits.CurrentApparelPolicy.filter.Allows(a)) continue;
			float d = (t.Position - p.Position).LengthHorizontalSquared;
			if (d > S.maxDistance * S.maxDistance || d >= bestD) continue;
			if (!p.CanReserveAndReach(a, PathEndMode.ClosestTouch, Danger.Some)) continue;
			best = a; bestD = d;
		}
		return best;
	}

	private static void Wear(Pawn p, Apparel a, bool sleep)
	{
		Job job = JobMaker.MakeJob(JobDefOf.Wear, a);
		p.jobs.StartJob(job, JobCondition.InterruptForced, null, resumeCurJobAfterwards: false);
		Log2(p, "puts on " + a.LabelShort + (sleep ? " for the night" : " for the occasion"));
	}

	// ---------------- uniforms (Ideology dye) ----------------
	private static void Recolor(Pawn p)
	{
		if (!ModsConfig.IdeologyActive) return;
		string role = Roles.Of(p);
		RoleColor rc = S.RoleColors.FirstOrDefault(c => c.role == role && c.on) ?? S.RoleColors.FirstOrDefault(c => c.role == "Everyone" && c.on);
		if (rc == null) return;
		bool changed = false;
		foreach (Apparel a in p.apparel.WornApparel)
		{
			if (a.TryGetComp<CompColorable>() == null) continue;
			if (WardrobeProvider.IsFestive(a.def) || WardrobeProvider.IsSleepwear(a.def)) continue;
			if (p.apparel.IsLocked(a)) continue;
			Color have = a.DesiredColor ?? a.DrawColor;
			if (Close(have, rc.color)) continue;
			a.DesiredColor = rc.color;
			changed = true;
		}
		if (changed && JobGiver_OptimizeApparel.TryCreateRecolorJob(p, out Job job))
		{
			p.jobs.StartJob(job, JobCondition.InterruptOptional, null, resumeCurJobAfterwards: false);
			Log2(p, "dyes his clothes in the " + Roles.Label(role) + " colour");
		}
	}

	private static bool Close(Color a, Color b) => Mathf.Abs(a.r - b.r) + Mathf.Abs(a.g - b.g) + Mathf.Abs(a.b - b.b) < 0.06f;

	// ---------------- personal wardrobe ----------------
	private static bool HaulFavorite(Pawn p, WardrobeGame g)
	{
		if (!g.favorites.TryGetValue(p.thingIDNumber, out List<int> ids) || ids == null || ids.Count == 0) return false;
		Room room = p.ownership?.OwnedRoom;
		if (room == null || room.PsychologicallyOutdoors) return false;
		List<Building_Storage> stores = room.ContainedAndAdjacentThings.OfType<Building_Storage>().Where(b => b.GetRoom() == room).ToList();
		if (stores.Count == 0) return false;
		foreach (Thing t in p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
		{
			if (!ids.Contains(t.thingIDNumber) || !t.Spawned || t.IsForbidden(p)) continue;
			if (t.GetRoom() == room) continue;
			if ((t.Position - p.Position).LengthHorizontalSquared > S.maxDistance * S.maxDistance * 4) continue;
			foreach (Building_Storage b in stores)
			{
				foreach (IntVec3 c in b.AllSlotCells())
				{
					if (!StoreUtility.IsGoodStoreCell(c, p.Map, t, p, p.Faction)) continue;
					if (!p.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None)) return false;
					Job job = HaulAIUtility.HaulToCellStorageJob(p, t, c, false);
					if (job == null) continue;
					p.jobs.StartJob(job, JobCondition.InterruptOptional, null, resumeCurJobAfterwards: false);
					Log2(p, "takes his " + t.LabelShort + " to his bedroom");
					return true;
				}
			}
		}
		return false;
	}
}

// soft link helper (reflection, looked up once)
public sealed class MethodInfoCache
{
	private readonly string type, name;
	private readonly Type arg;
	private bool looked;
	private System.Reflection.MethodInfo m;

	public MethodInfoCache(string type, string name, Type arg) { this.type = type; this.name = name; this.arg = arg; }

	public object Call(object inst, object a)
	{
		try
		{
			if (!looked)
			{
				looked = true;
				Type t = GenTypes.GetTypeInAnyAssembly(type);
				if (t != null) m = arg != null ? AccessTools.Method(t, name, new[] { arg }) : AccessTools.Method(t, name);
			}
			return m?.Invoke(inst, new[] { a });
		}
		catch { m = null; return null; }
	}
}

// ---------------- retire tattered pieces: one smelt/burn bill per map ----------------
public static class Retire
{
	public static void Maps()
	{
		WardrobeSettings S = WardrobeMod.Settings;
		WardrobeGame g = WardrobeGame.Get();
		if (S == null || !S.care || !S.retireBills || g == null) return;
		foreach (Map m in Find.Maps)
		{
			if (!m.IsPlayerHome || g.retireBillMaps.Contains(m.uniqueID)) continue;
			if (Add(m, "SmeltApparel") || Add(m, "BurnApparel"))
				g.retireBillMaps.Add(m.uniqueID);
		}
	}

	private static bool Add(Map m, string recipeName)
	{
		RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(recipeName);
		if (r == null) return false;
		foreach (Building b in m.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver giver) || b.def.AllRecipes == null || !b.def.AllRecipes.Contains(r)) continue;
			if (giver.BillStack.Bills.Any(x => x.recipe == r)) return true;   // the player has one already
			var bill = new Bill_Production(r) { repeatMode = BillRepeatModeDefOf.Forever, hpRange = new FloatRange(0f, WardrobeMod.Settings.retireBelow) };
			giver.BillStack.AddBill(bill);
			Messages.Message("WR_RetireBill".Translate(b.LabelCap), b, MessageTypeDefOf.SilentInput, false);
			return true;
		}
		return false;
	}
}
