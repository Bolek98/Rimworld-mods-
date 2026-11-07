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
using Verse.AI.Group;

namespace StewardKit;

// ===========================================================================
// Steward – Kit: what every colonist carries in his inventory, by his role.
//   * Roles come from Steward – Roles when it is on, otherwise from the
//     colonist's best skills (doctor, hunter, builder, cook, hauler …).
//   * The kit table (role × item × count) is filled automatically the first
//     time and the player changes it in the settings.
//   * Missing items are picked up from storage nearby (Basic work, short
//     walks only, never over the weight limit); extras are dropped at home.
//   * Caravans pack the kits of their members.
// ===========================================================================

public sealed class KitLine : IExposable
{
	public string role;
	public string item;
	public int count;

	public KitLine() { }
	public KitLine(string role, string item, int count) { this.role = role; this.item = item; this.count = count; }

	public void ExposeData()
	{
		Scribe_Values.Look(ref role, "role");
		Scribe_Values.Look(ref item, "item");
		Scribe_Values.Look(ref count, "count", 1);
	}
}

public static class Kits
{
	// roles of the kit table (Steward – Roles names where they exist)
	public static readonly string[] Roles = { "Doctor", "Hunter", "Builder", "Soldier", "Cook", "Hauler", "Miner", "Farmer", "Crafter", "Researcher", "Handler", "Warden", "Everyone" };

	// item keys: a category or a ThingDef defName
	public const string Medicine = "@Medicine", Meal = "@Meal", Component = "@Component", CombatDrug = "@CombatDrug";
	// 0.2.0: more kinds
	public const string FirstAid = "@FirstAid", PainDrug = "@PainDrug", Kibble = "@Kibble";
	public static readonly string[] Categories = { Medicine, Meal, Component, CombatDrug, FirstAid, PainDrug, Kibble };
	// version of the automatic table; an unchanged table is rebuilt when it grows
	public const int DefaultsVersion = 2;

	public static string RoleLabel(string role) => ("SK_Role_" + role).Translate();

	public static string ItemLabel(string item)
	{
		if (item == null) return "?";
		if (item.StartsWith("@")) return ("SK_Item_" + item.Substring(1)).Translate();
		ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(item);
		return d != null ? (string)d.LabelCap : item;
	}

	public static Texture2D ItemIcon(string item)
	{
		switch (item)
		{
			case Medicine: return Def("MedicineIndustrial")?.uiIcon;
			case Meal: return Def("MealSurvivalPack")?.uiIcon ?? Def("MealSimple")?.uiIcon;
			case Component: return Def("ComponentIndustrial")?.uiIcon;
			case CombatDrug: return Def("GoJuice")?.uiIcon ?? Def("Yayo")?.uiIcon;
			case FirstAid: return FirstAidDefs().FirstOrDefault()?.uiIcon ?? Def("MedicineHerbal")?.uiIcon;
			case PainDrug: return PainDefs().FirstOrDefault()?.uiIcon ?? Def("Penoxycyline")?.uiIcon;
			case Kibble: return Def("Kibble")?.uiIcon;
		}
		return Def(item)?.uiIcon;
	}

	private static ThingDef Def(string n) => n == null ? null : DefDatabase<ThingDef>.GetNamedSilentFail(n);

	// the automatic kit table
	public static List<KitLine> Defaults()
	{
		// 0.2.0: bigger kits; the weight limit still caps what is really carried
		var l = new List<KitLine>
		{
			new KitLine("Doctor", Medicine, 8),
			new KitLine("Doctor", FirstAid, 2),
			new KitLine("Doctor", PainDrug, 2),
			new KitLine("Doctor", Meal, 1),
			new KitLine("Hunter", Meal, 4),
			new KitLine("Hunter", Medicine, 2),
			new KitLine("Builder", Component, 4),
			new KitLine("Builder", Medicine, 1),
			new KitLine("Builder", Meal, 1),
			new KitLine("Soldier", Medicine, 3),
			new KitLine("Soldier", CombatDrug, 2),
			new KitLine("Soldier", Meal, 2),
			new KitLine("Cook", Meal, 4),
			new KitLine("Hauler", Meal, 4),
			new KitLine("Miner", Meal, 2),
			new KitLine("Miner", Medicine, 1),
			new KitLine("Farmer", Meal, 2),
			new KitLine("Farmer", Medicine, 1),
			new KitLine("Crafter", Meal, 2),
			new KitLine("Crafter", Medicine, 1),
			new KitLine("Researcher", Meal, 2),
			new KitLine("Researcher", Medicine, 1),
			new KitLine("Handler", Meal, 2),
			new KitLine("Handler", Medicine, 1),
			new KitLine("Handler", Kibble, 10),
			new KitLine("Warden", Meal, 3),
			new KitLine("Warden", Medicine, 1),
			new KitLine("Everyone", Meal, 2),
			new KitLine("Everyone", Medicine, 2)
		};
		// kinds this game has nothing for are left out (no empty lines in the table)
		l.RemoveAll(x => (x.item == Kibble && Def("Kibble") == null) || (x.item == FirstAid && !FirstAidDefs().Any()) || (x.item == PainDrug && !PainDefs().Any()));
		return l;
	}

	// first-aid items from mods (bandages, first-aid kits, tourniquets …)
	private static List<ThingDef> firstAid;
	public static List<ThingDef> FirstAidDefs()
	{
		if (firstAid != null) return firstAid;
		firstAid = new List<ThingDef>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (d.category != ThingCategory.Item || d.IsCorpse) continue;
			string n = d.defName.ToLowerInvariant();
			if (!(n.Contains("bandage") || n.Contains("firstaid") || n.Contains("medkit") || n.Contains("tourniquet") || n.Contains("gauze"))) continue;
			if (d.GetStatValueAbstract(StatDefOf.Mass) > 1.5f) continue;
			firstAid.Add(d);
		}
		return firstAid;
	}

	// drugs of the pharmacy list that help against pain or blood loss / bleeding (not addictive)
	private static List<ThingDef> pain;
	public static List<ThingDef> PainDefs()
	{
		if (pain != null) return pain;
		pain = new List<ThingDef>();
		try
		{
			foreach (AutoPharmacy.DrugInfo i in AutoPharmacy.Pharmacy.Drugs)
			{
				if (i?.def == null || i.addictiveness > 0f || i.Combat) continue;
				if (i.effects.ContainsKey(AutoPharmacy.Need.Pain) || i.effects.ContainsKey(AutoPharmacy.Need.BloodLoss) || i.effects.ContainsKey(AutoPharmacy.Need.Bleeding))
					pain.Add(i.def);
			}
		}
		catch { }
		return pain;
	}

	// which candidate a colonist should rather take (higher = better)
	public static float Preference(string item, ThingDef d)
	{
		if (d == null) return 0f;
		switch (item)
		{
			case Meal:
				if (d.defName == "Pemmican") return 3f;
				if (d.defName.Contains("Survival") || d.defName.Contains("Packaged")) return 2.5f;
				return 1f + (int)d.ingestible.preferability * 0.01f;
			case Medicine: return d.GetStatValueAbstract(StatDefOf.MedicalPotency);
		}
		return 1f;
	}

	// ---------------- roles ----------------
	private static MethodInfo rolesOf;
	private static bool rolesLooked;

	private static string StewardRole(Pawn p)
	{
		try
		{
			if (!rolesLooked)
			{
				rolesLooked = true;
				Type t = GenTypes.GetTypeInAnyAssembly("ColonyForeman.RolesGame");
				rolesOf = t == null ? null : AccessTools.Method(t, "Of", new[] { typeof(Pawn) });
			}
			if (rolesOf == null) return null;
			object r = rolesOf.Invoke(null, new object[] { p });
			string n = r?.ToString();
			if (string.IsNullOrEmpty(n) || n == "None") return null;
			switch (n)
			{
				case "Doctor": case "Hunter": case "Builder": case "Soldier": case "Cook": case "Hauler":
				case "Miner": case "Farmer": case "Crafter": case "Researcher": case "Handler": case "Warden": return n;
				default: return "Everyone";
			}
		}
		catch { rolesOf = null; return null; }
	}

	public static bool RolesModOn => ModsConfig.IsActive("misak.steward.roles");

	// the role whose kit this colonist carries (null = no kit)
	public static string RoleOf(Pawn p)
	{
		KitGame g = KitGame.Get();
		if (g != null && g.overrides.TryGetValue(p.thingIDNumber, out string o))
			return o == "Off" ? null : o;
		string r = StewardRole(p);
		if (r != null) return r;
		return SkillRole(p);
	}

	private static int Skill(Pawn p, SkillDef s) => s == null || p.skills == null ? 0 : (p.skills.GetSkill(s)?.TotallyDisabled == true ? -1 : p.skills.GetSkill(s)?.Level ?? 0);

	private static bool Can(Pawn p, WorkTypeDef w) => w != null && p.workSettings != null && p.workSettings.EverWork && p.workSettings.WorkIsActive(w);

	// without Steward – Roles: the colonist's strongest active work decides
	public static string SkillRole(Pawn p)
	{
		if (p.skills == null) return "Everyone";
		int med = Skill(p, SkillDefOf.Medicine), shoot = Skill(p, SkillDefOf.Shooting), build = Skill(p, SkillDefOf.Construction), cook = Skill(p, SkillDefOf.Cooking);
		string best = "Everyone";
		int bestLv = 5;
		if (Can(p, WorkTypeDefOf.Doctor) && med >= 6 && med > bestLv) { best = "Doctor"; bestLv = med; }
		if (Can(p, WorkTypeDefOf.Hunting) && shoot >= 6 && shoot > bestLv) { best = "Hunter"; bestLv = shoot; }
		if (Can(p, WorkTypeDefOf.Construction) && build >= 6 && build > bestLv) { best = "Builder"; bestLv = build; }
		WorkTypeDef cooking = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Cooking");
		if (Can(p, cooking) && cook >= 6 && cook > bestLv) { best = "Cook"; bestLv = cook; }
		return best;
	}

	// ---------------- what a colonist should carry ----------------
	// 0.2.2: the wanted list is asked for very often (every job search, every item the game
	// thinks of dropping) – it is kept for a few seconds per colonist instead of rebuilt each time
	private static readonly Dictionary<int, (int tick, List<KitLine> list)> wantedCache = new Dictionary<int, (int, List<KitLine>)>();
	private static int settingsStamp;
	public static void ClearCache() { wantedCache.Clear(); settingsStamp++; }

	public static List<KitLine> Wanted(Pawn p)
	{
		if (p == null) return new List<KitLine>();
		int now = Find.TickManager?.TicksGame ?? 0;
		if (wantedCache.TryGetValue(p.thingIDNumber, out var c) && now - c.tick < 250 && now >= c.tick)
			return c.list;
		List<KitLine> list = BuildWanted(p);
		if (wantedCache.Count > 2000) wantedCache.Clear();
		wantedCache[p.thingIDNumber] = (now, list);
		return list;
	}

	private static List<KitLine> BuildWanted(Pawn p)
	{
		var res = new List<KitLine>();
		KitSettings s = KitMod.Settings;
		if (s == null || !s.kitsOn || p == null) return res;
		string role = RoleOf(p);
		if (role == null) return res;
		var sum = new Dictionary<string, int>();
		foreach (KitLine l in s.Lines)
		{
			if (l == null || l.count <= 0 || l.item == null) continue;
			if (l.role == role || (l.role == "Everyone" && s.everyonePack))
			{
				if (l.item == CombatDrug && !s.combatDrugKit) continue;
				sum.TryGetValue(l.item, out int n);
				sum[l.item] = Math.Max(n, 0) + l.count;
			}
		}
		foreach (var kv in sum)
			res.Add(new KitLine(role, kv.Key, kv.Value));
		return res;
	}

	// does this thing count for the item key (for this colonist)
	public static bool Matches(Pawn p, string item, ThingDef d)
	{
		if (d == null || item == null) return false;
		switch (item)
		{
			case Medicine: return d.IsMedicine && MedicineOk(p, d);
			case Meal: return IsKitMeal(d);
			case Component: return d == ThingDefOf.ComponentIndustrial;
			case CombatDrug: return IsCombatDrug(d);
			case FirstAid: return FirstAidDefs().Contains(d);
			case PainDrug: return PainDefs().Contains(d) && !AutoPharmacy.AutoPharmacyMod.Settings.blocked.Contains(d.defName);
			case Kibble: return d.defName == "Kibble";
		}
		return d.defName == item;
	}

	// a doctor carries the medicine his skill should use (no glitterworld for a beginner)
	private static bool MedicineOk(Pawn p, ThingDef d)
	{
		float pot = d.GetStatValueAbstract(StatDefOf.MedicalPotency);
		int med = Math.Max(0, Skill(p, SkillDefOf.Medicine));
		float max = med >= 12 ? 99f : med >= 6 ? 1.0f : 0.65f;
		return pot <= max + 0.001f;
	}

	public static bool IsKitMeal(ThingDef d)
	{
		if (d?.ingestible == null || d.IsCorpse || d.IsDrug) return false;
		if (d.ingestible.IsMeal) return d.ingestible.preferability >= FoodPreferability.MealSimple || d.defName.Contains("Survival");
		return d.defName == "Pemmican";
	}

	private static HashSet<ThingDef> combat;
	public static bool IsCombatDrug(ThingDef d)
	{
		if (combat == null)
		{
			combat = new HashSet<ThingDef>();
			try
			{
				foreach (AutoPharmacy.DrugInfo i in AutoPharmacy.Pharmacy.Drugs)
					if (i.Combat) combat.Add(i.def);
			}
			catch { }
			foreach (string n in new[] { "GoJuice", "WakeUp" })
			{
				ThingDef x = DefDatabase<ThingDef>.GetNamedSilentFail(n);
				if (x != null) combat.Add(x);
			}
		}
		return d != null && combat.Contains(d) && !AutoPharmacy.AutoPharmacyMod.Settings.blocked.Contains(d.defName);
	}

	// things on the map that can be this item (cheap lists, not the whole map)
	public static IEnumerable<Thing> Candidates(Map map, string item)
	{
		switch (item)
		{
			case Medicine: return map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine);
			case Meal: return map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
			case Component: return map.listerThings.ThingsOfDef(ThingDefOf.ComponentIndustrial);
			case CombatDrug: return map.listerThings.ThingsInGroup(ThingRequestGroup.Drug);
			case PainDrug: return map.listerThings.ThingsInGroup(ThingRequestGroup.Drug).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine));
			case FirstAid: return FirstAidDefs().SelectMany(x => map.listerThings.ThingsOfDef(x));
			case Kibble: { ThingDef k = Def("Kibble"); return k == null ? Enumerable.Empty<Thing>() : (IEnumerable<Thing>)map.listerThings.ThingsOfDef(k); }
		}
		ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(item);
		return d == null ? Enumerable.Empty<Thing>() : (IEnumerable<Thing>)map.listerThings.ThingsOfDef(d);
	}

	public static int Have(Pawn p, string item)
	{
		int n = 0;
		ThingOwner<Thing> inv = p.inventory?.innerContainer;
		if (inv == null) return 0;
		for (int i = 0; i < inv.Count; i++)
			if (Matches(p, item, inv[i].def)) n += inv[i].stackCount;
		return n;
	}

	public static int Missing(Pawn p, KitLine want) => Math.Max(0, want.count - Have(p, want.item));

	// is this inventory thing part of his kit (so the game must not drop it)
	public static bool InKit(Pawn p, Thing t)
	{
		if (p == null || t == null) return false;
		foreach (KitLine w in Wanted(p))
			if (Matches(p, w.item, t.def)) return true;
		// 0.2.3: the doctor's pharmacy stock counts too – Steward – Supplies asks here and used to
		// unload those drugs, the pharmacy took them back, again and again
		try { if (AutoPharmacy.PharmacyCarry.Keeps(p, t.def)) return true; } catch { }
		return false;
	}

	// mass the kit may use
	public static float FreeKitMass(Pawn p)
	{
		float cap = MassUtility.Capacity(p);
		float limit = cap * Mathf.Clamp(KitMod.Settings.weightLimit, 0.05f, 1f);
		float used = MassUtility.InventoryMass(p);
		// 0.2.1: clothes in the inventory (Wardrobe keeps the day outfit / sleepwear there) don't count against the kit
		float clothes = 0f;
		foreach (Thing t in p.inventory.innerContainer)
			if (t is Apparel) clothes += t.GetStatValue(StatDefOf.Mass) * t.stackCount;
		return Mathf.Min(limit - (used - clothes), MassUtility.FreeSpace(p) + clothes);
	}

	public static bool Eligible(Pawn p)
	{
		if (p == null || !p.Spawned || p.Dead || p.Downed || p.Drafted || p.InMentalState || p.inventory == null) return false;
		if (!p.IsColonist || p.IsPrisoner || p.IsSlave && !KitMod.Settings.slaves || p.IsQuestLodger()) return false;
		if (p.DevelopmentalStage.Juvenile()) return false;
		return true;
	}
}

// overrides per colonist ("Off" or a role name) – saved in the game
public sealed class KitGame : GameComponent
{
	public Dictionary<int, string> overrides = new Dictionary<int, string>();
	private static KitGame cached;

	public KitGame(Game g) { cached = this; }

	public static KitGame Get()
	{
		if (cached == null || Current.Game == null) cached = Current.Game?.GetComponent<KitGame>();
		return cached;
	}

	public override void GameComponentTick()
	{
		KitTidy.Tick();
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref overrides, "skOverrides", LookMode.Value, LookMode.Value);
		if (overrides == null) overrides = new Dictionary<int, string>();
	}
}

// picks missing kit items from storage nearby – Basic work, short walks only
public class WorkGiver_KitRefill : WorkGiver_Scanner
{
	private static readonly Dictionary<int, int> nextCheck = new Dictionary<int, int>();

	public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

	// 0.2.0: pemmican and packaged meals before cooked ones, better medicine first
	public override bool Prioritized => true;

	public override float GetPriority(Pawn pawn, TargetInfo t)
	{
		Thing th = t.Thing;
		if (th == null) return 0f;
		float best = 0f;
		foreach (KitLine w in Kits.Wanted(pawn))
			if (Kits.Matches(pawn, w.item, th.def)) best = Math.Max(best, Kits.Preference(w.item, th.def));
		return best;
	}

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		KitSettings s = KitMod.Settings;
		if (s == null || !s.kitsOn || !s.refill || !Kits.Eligible(pawn) || pawn.Map == null || !pawn.Map.IsPlayerHome) return true;
		int now = Find.TickManager.TicksGame;
		if (!forced && nextCheck.TryGetValue(pawn.thingIDNumber, out int n) && now < n) return true;
		bool missing = Kits.Wanted(pawn).Any(w => Kits.Missing(pawn, w) > 0);
		if (!missing)
		{
			nextCheck[pawn.thingIDNumber] = now + Mathf.Max(1, s.checkHours) * GenDate.TicksPerHour;
			return true;
		}
		// 0.2.2: something is missing – look once now, then not again for an in-game hour unless a
		// pick-up was found (JobOnThing clears this). Before, a colonist whose missing item was not
		// in storage (or who had no room for it) searched the whole map at every job search.
		if (!forced)
			nextCheck[pawn.thingIDNumber] = now + GenDate.TicksPerHour;
		return Kits.FreeKitMass(pawn) <= 0.05f;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		var wanted = Kits.Wanted(pawn).Where(w => Kits.Missing(pawn, w) > 0).ToList();
		if (wanted.Count == 0) yield break;
		float maxD = KitMod.Settings.maxDistance;
		float maxD2 = maxD * maxD;
		var seen = new HashSet<Thing>();
		// 0.2.2: cheap checks first, and only the nearest few per item (each candidate costs a reach check)
		var found = new List<Thing>();
		foreach (KitLine w in wanted)
		{
			found.Clear();
			foreach (Thing t in Kits.Candidates(pawn.Map, w.item))
			{
				if (t == null || !t.Spawned || (t.Position - pawn.Position).LengthHorizontalSquared > maxD2) continue;
				if (seen.Contains(t) || !t.IsInAnyStorage() || t.IsForbidden(pawn) || !Kits.Matches(pawn, w.item, t.def)) continue;
				found.Add(t);
			}
			found.Sort((a, b) => (a.Position - pawn.Position).LengthHorizontalSquared.CompareTo((b.Position - pawn.Position).LengthHorizontalSquared));
			for (int i = 0; i < found.Count && i < 8; i++)
				if (seen.Add(found[i])) yield return found[i];
		}
	}

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		if (t.IsForbidden(pawn) || !t.IsInAnyStorage() || !pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.None))
			return false;
		return Count(pawn, t) > 0;
	}

	private static int Count(Pawn pawn, Thing t)
	{
		int need = 0;
		foreach (KitLine w in Kits.Wanted(pawn))
			if (Kits.Matches(pawn, w.item, t.def)) need = Math.Max(need, Kits.Missing(pawn, w));
		if (need <= 0) return 0;
		float mass = t.GetStatValue(StatDefOf.Mass);
		float free = Kits.FreeKitMass(pawn);
		int byMass = mass <= 0.0001f ? need : Mathf.FloorToInt(free / mass);
		return Mathf.Min(need, byMass, t.stackCount);
	}

	public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		int c = Count(pawn, t);
		if (c <= 0) return null;
		Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, t);
		job.count = c;
		nextCheck.Remove(pawn.thingIDNumber);
		return job;
	}
}

// drops extra kit things at home (more than the kit wants, e.g. after a role change)
public static class KitTidy
{
	public static void Tick()
	{
		KitSettings s = KitMod.Settings;
		if (s == null || !s.kitsOn || !s.dropExtras) return;
		int now = Find.TickManager.TicksGame;
		if (now % 2500 != 0) return;
		foreach (Map m in Find.Maps)
		{
			if (!m.IsPlayerHome) continue;
			foreach (Pawn p in m.mapPawns.FreeColonistsSpawned.ToList())
			{
				try { Tidy(p, m); }
				catch (Exception e) { Log.WarningOnce("[Steward – Kit] tidy: " + e.Message, 0x5C17A1); }
			}
		}
	}

	private static void Tidy(Pawn p, Map m)
	{
		if (!Kits.Eligible(p) || !m.areaManager.Home[p.Position] || p.CurJob?.def == JobDefOf.TakeInventory) return;
		if (p.GetLord() != null) return;   // caravan forming, rituals …
		List<KitLine> want = Kits.Wanted(p);
		ThingOwner<Thing> inv = p.inventory.innerContainer;
		foreach (string cat in Kits.Categories)
		{
			KitLine w = want.FirstOrDefault(x => x.item == cat);
			int keep = w?.count ?? -1;
			if (keep < 0) continue;   // not a kit item for him – the game decides as before
			int have = Kits.Have(p, cat);
			int extra = have - keep - Math.Max(0, KitMod.Settings.spare);
			for (int i = inv.Count - 1; i >= 0 && extra > 0; i--)
			{
				Thing t = inv[i];
				if (!Kits.Matches(p, cat, t.def)) continue;
				// 0.2.3: the doctor's pharmacy stock stays (it was dropped here and taken back by the pharmacy)
				if (AutoPharmacy.PharmacyCarry.Keeps(p, t.def)) continue;
				int n = Math.Min(extra, t.stackCount);
				if (inv.TryDrop(t, p.Position, m, ThingPlaceMode.Near, n, out Thing _))
					extra -= n;
			}
		}
	}
}

[StaticConstructorOnStartup]
public static class KitPatches
{
	public static void Apply(Harmony h)
	{
		Try(h, AccessTools.Method(typeof(JobGiver_DropUnusedInventory), nameof(JobGiver_DropUnusedInventory.ShouldKeepDrugInInventory)), postfix: nameof(KeepDrug));
		Try(h, AccessTools.Method(typeof(CaravanFormingUtility), nameof(CaravanFormingUtility.StartFormingCaravan)), prefix: nameof(CaravanPrefix));
		// 1.0.1: the game drops raw food (pemmican, kibble) from pockets at home – kit food stays
		Try(h, AccessTools.Method(typeof(JobGiver_DropUnusedInventory), "Drop", new[] { typeof(Pawn), typeof(Thing) }), prefix: nameof(KeepKitThing));
	}

	private static bool KeepKitThing(Pawn pawn, Thing thing)
	{
		try { return !(pawn != null && thing != null && Kits.InKit(pawn, thing)); }
		catch { return true; }
	}

	private static void Try(Harmony h, MethodBase m, string prefix = null, string postfix = null)
	{
		if (m == null) { Log.Warning("[Steward – Kit] a game method was not found (" + (prefix ?? postfix) + ")"); return; }
		try
		{
			h.Patch(m, prefix: prefix == null ? null : new HarmonyMethod(typeof(KitPatches), prefix), postfix: postfix == null ? null : new HarmonyMethod(typeof(KitPatches), postfix));
		}
		catch (Exception e) { Log.Warning("[Steward – Kit] patch " + m.Name + ": " + e.Message); }
	}

	// the game drops drugs nobody's drug policy keeps – kit drugs and the doctor's pharmacy stock stay
	private static void KeepDrug(Pawn pawn, Thing drug, ref bool __result)
	{
		if (__result || pawn == null || drug == null) return;
		try
		{
			if (Kits.InKit(pawn, drug) || AutoPharmacy.PharmacyCarry.Keeps(pawn, drug.def))
				__result = true;
		}
		catch { }
	}

	// caravans: the kit items of every member are packed too (when there are some in storage)
	private static void CaravanPrefix(List<Pawn> pawns, List<TransferableOneWay> transferables)
	{
		KitSettings s = KitMod.Settings;
		if (s == null || !s.kitsOn || !s.caravanKits || pawns == null || transferables == null) return;
		try
		{
			Map map = pawns.FirstOrDefault(p => p?.Map != null)?.Map;
			if (map == null) return;
			var need = new Dictionary<string, int>();   // item → count over all members (what they don't carry yet)
			var forPawn = new Dictionary<string, Pawn>();
			foreach (Pawn p in pawns)
			{
				if (p == null || !p.IsColonist) continue;
				foreach (KitLine w in Kits.Wanted(p))
				{
					int miss = Kits.Missing(p, w);
					if (miss <= 0) continue;
					need.TryGetValue(w.item, out int n);
					need[w.item] = n + miss;
					if (!forPawn.ContainsKey(w.item)) forPawn[w.item] = p;
				}
			}
			foreach (var kv in need)
			{
				int left = kv.Value;
				Pawn who = forPawn[kv.Key];
				foreach (Thing t in Kits.Candidates(map, kv.Key).ToList())
				{
					if (left <= 0) break;
					if (!t.Spawned || t.IsForbidden(Faction.OfPlayer) || !t.IsInAnyStorage() || !Kits.Matches(who, kv.Key, t.def)) continue;
					TransferableOneWay tr = TransferableUtility.TransferableMatching(t, transferables, TransferAsOneMode.PodsOrCaravanPacking);
					if (tr == null)
					{
						tr = new TransferableOneWay();
						transferables.Add(tr);
					}
					if (!tr.things.Contains(t)) tr.things.Add(t);
					int target = Math.Min(tr.MaxCount, tr.CountToTransfer + left);
					int add = target - tr.CountToTransfer;
					if (add <= 0) continue;
					if (tr.CanAdjustTo(target).Accepted)
					{
						tr.AdjustTo(target);
						left -= add;
					}
				}
			}
		}
		catch (Exception e) { Log.WarningOnce("[Steward – Kit] caravan kits: " + e.Message, 0x5C17A2); }
	}
}
