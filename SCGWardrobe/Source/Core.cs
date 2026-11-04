using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SCGWardrobe;

public enum WeaponPref { Any, Ranged, Melee, LongRange, ShortRange, Light }
public enum ArmorWeight { Any, None, Light, Medium, Heavy }
public enum Situation { Work, Hunt, Defense, Caravan }

// one row "role → value" of the tables (weapons, armour)
public sealed class RolePref : IExposable
{
	public string role;
	public int value;
	public RolePref() { }
	public RolePref(string r, int v) { role = r; value = v; }
	public void ExposeData()
	{
		Scribe_Values.Look(ref role, "role");
		Scribe_Values.Look(ref value, "value", 0);
	}
}

public sealed class RoleColor : IExposable
{
	public string role;
	public Color color = Color.white;
	public bool on;
	public RoleColor() { }
	public RoleColor(string r, Color c, bool o) { role = r; color = c; on = o; }
	public void ExposeData()
	{
		Scribe_Values.Look(ref role, "role");
		Scribe_Values.Look(ref color, "color", Color.white);
		Scribe_Values.Look(ref on, "on", false);
	}
}

// work outfit editor: what a worker of this work type wears for it
public sealed class WorkOutfit : IExposable
{
	public string work;
	public List<string> apparel = new List<string>();
	public WorkOutfit() { }
	public WorkOutfit(string w) { work = w; }
	public void ExposeData()
	{
		Scribe_Values.Look(ref work, "work");
		Scribe_Collections.Look(ref apparel, "apparel", LookMode.Value);
		if (apparel == null) apparel = new List<string>();
	}
}

public sealed class WardrobeSettings : ModSettings
{
	public bool recommended = true;
	public bool enabled = true;
	// weapons
	public bool roleWeapons = true;
	public float damagedWeaponBelow = 0.5f;
	public List<RolePref> weaponPrefs;
	// armour
	public bool armorSets = true;
	public int huntArmor = (int)ArmorWeight.Light;
	public int defenseArmor = (int)ArmorWeight.Heavy;
	public int caravanArmor = (int)ArmorWeight.Medium;
	public List<RolePref> armorPrefs;
	// work outfits
	public bool workEditor = true;
	public bool workCustomized;
	public List<WorkOutfit> workOutfits;
	public List<string> workRemoved;
	// care
	public bool care = true;
	public float replaceBelow = 0.5f;
	public bool noCorpse = true;
	public bool retireBills = true;
	public float retireBelow = 0.35f;
	// occasions
	public bool sleepwear = true;
	public int sleepFrom = 22;
	public int sleepTo = 6;
	public bool festive = true;
	public bool festiveOnlyOccasions = true;
	public bool occasionsCustomized;
	public List<string> sleepDefs;
	public List<string> festiveDefs;
	// uniforms
	public bool uniforms = true;
	public List<RoleColor> roleColors;
	// personal wardrobe
	public bool favorites = true;
	public bool favoritesToBedroom = true;
	// tailor
	public bool tailorPlanner = true;
	// advanced
	public int maxDistance = 60;
	public bool verbose;

	public List<RolePref> WeaponPrefs => weaponPrefs ??= Defaults.Weapons();
	public List<RolePref> ArmorPrefs => armorPrefs ??= Defaults.Armor();
	public List<RoleColor> RoleColors => roleColors ??= Defaults.Colors();
	public List<WorkOutfit> WorkOutfits
	{
		get
		{
			if (workOutfits == null || (!workCustomized && workOutfits.Count == 0)) workOutfits = Defaults.WorkOutfits();
			return workOutfits;
		}
	}
	public List<string> WorkRemoved => workRemoved ??= new List<string>();
	public List<string> SleepDefs
	{
		get
		{
			if (sleepDefs == null || (!occasionsCustomized && sleepDefs.Count == 0)) sleepDefs = Defaults.Sleepwear();
			return sleepDefs;
		}
	}
	public List<string> FestiveDefs
	{
		get
		{
			if (festiveDefs == null || (!occasionsCustomized && festiveDefs.Count == 0)) festiveDefs = Defaults.Festive();
			return festiveDefs;
		}
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref recommended, "recommended", true);
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref roleWeapons, "roleWeapons", true);
		Scribe_Values.Look(ref damagedWeaponBelow, "damagedWeaponBelow", 0.5f);
		Scribe_Collections.Look(ref weaponPrefs, "weaponPrefs", LookMode.Deep);
		Scribe_Values.Look(ref armorSets, "armorSets", true);
		Scribe_Values.Look(ref huntArmor, "huntArmor", (int)ArmorWeight.Light);
		Scribe_Values.Look(ref defenseArmor, "defenseArmor", (int)ArmorWeight.Heavy);
		Scribe_Values.Look(ref caravanArmor, "caravanArmor", (int)ArmorWeight.Medium);
		Scribe_Collections.Look(ref armorPrefs, "armorPrefs", LookMode.Deep);
		Scribe_Values.Look(ref workEditor, "workEditor", true);
		Scribe_Values.Look(ref workCustomized, "workCustomized", false);
		Scribe_Collections.Look(ref workOutfits, "workOutfits", LookMode.Deep);
		Scribe_Collections.Look(ref workRemoved, "workRemoved", LookMode.Value);
		Scribe_Values.Look(ref care, "care", true);
		Scribe_Values.Look(ref replaceBelow, "replaceBelow", 0.5f);
		Scribe_Values.Look(ref noCorpse, "noCorpse", true);
		Scribe_Values.Look(ref retireBills, "retireBills", true);
		Scribe_Values.Look(ref retireBelow, "retireBelow", 0.35f);
		Scribe_Values.Look(ref sleepwear, "sleepwear", true);
		Scribe_Values.Look(ref sleepFrom, "sleepFrom", 22);
		Scribe_Values.Look(ref sleepTo, "sleepTo", 6);
		Scribe_Values.Look(ref festive, "festive", true);
		Scribe_Values.Look(ref festiveOnlyOccasions, "festiveOnlyOccasions", true);
		Scribe_Values.Look(ref occasionsCustomized, "occasionsCustomized", false);
		Scribe_Collections.Look(ref sleepDefs, "sleepDefs", LookMode.Value);
		Scribe_Collections.Look(ref festiveDefs, "festiveDefs", LookMode.Value);
		Scribe_Values.Look(ref uniforms, "uniforms", true);
		Scribe_Collections.Look(ref roleColors, "roleColors", LookMode.Deep);
		Scribe_Values.Look(ref favorites, "favorites", true);
		Scribe_Values.Look(ref favoritesToBedroom, "favoritesToBedroom", true);
		Scribe_Values.Look(ref tailorPlanner, "tailorPlanner", true);
		Scribe_Values.Look(ref maxDistance, "maxDistance", 60);
		Scribe_Values.Look(ref verbose, "verbose", false);
	}
}

// ---------------- roles ----------------
public static class Roles
{
	public static readonly string[] All = { "Everyone", "Soldier", "Hunter", "Doctor", "Builder", "Miner", "Crafter", "Cook", "Farmer", "Researcher", "Handler", "Warden", "Hauler" };

	public static string Label(string r) => ("WR_Role_" + r).CanTranslate() ? (string)("WR_Role_" + r).Translate() : r;

	private static System.Reflection.MethodInfo rolesOf;
	private static bool looked;

	private static string StewardRole(Pawn p)
	{
		try
		{
			if (!looked)
			{
				looked = true;
				Type t = GenTypes.GetTypeInAnyAssembly("ColonyForeman.RolesGame");
				rolesOf = t == null ? null : AccessTools.Method(t, "Of", new[] { typeof(Pawn) });
			}
			if (rolesOf == null) return null;
			string n = rolesOf.Invoke(null, new object[] { p })?.ToString();
			if (string.IsNullOrEmpty(n) || n == "None") return null;
			return All.Contains(n) ? n : "Everyone";
		}
		catch { rolesOf = null; return null; }
	}

	public static bool StewardRolesOn => ModsConfig.IsActive("misak.steward.roles");

	public static string Of(Pawn p)
	{
		if (p == null) return "Everyone";
		WardrobeGame g = WardrobeGame.Get();
		if (g != null && g.roleOverride.TryGetValue(p.thingIDNumber, out string o) && !string.IsNullOrEmpty(o)) return o;
		return StewardRole(p) ?? SkillRole(p);
	}

	private static int Lv(Pawn p, SkillDef s)
	{
		if (s == null || p.skills == null) return 0;
		SkillRecord r = p.skills.GetSkill(s);
		return r == null || r.TotallyDisabled ? -1 : r.Level;
	}

	private static bool Can(Pawn p, string work)
	{
		WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(work);
		return w != null && p.workSettings != null && p.workSettings.EverWork && p.workSettings.WorkIsActive(w);
	}

	// without Steward – Roles: the strongest skill whose work he does (6+), fighters by their combat skill
	public static string SkillRole(Pawn p)
	{
		if (p?.skills == null) return "Everyone";
		var cand = new (string role, string work, SkillDef skill)[]
		{
			("Doctor", "Doctor", SkillDefOf.Medicine), ("Hunter", "Hunting", SkillDefOf.Shooting), ("Builder", "Construction", SkillDefOf.Construction),
			("Miner", "Mining", SkillDefOf.Mining), ("Crafter", "Crafting", SkillDefOf.Crafting), ("Cook", "Cooking", SkillDefOf.Cooking),
			("Farmer", "Growing", SkillDefOf.Plants), ("Researcher", "Research", SkillDefOf.Intellectual), ("Handler", "Handling", SkillDefOf.Animals),
			("Warden", "Warden", SkillDefOf.Social)
		};
		string best = "Everyone";
		int bestLv = 5;
		foreach (var c in cand)
		{
			int lv = Lv(p, c.skill);
			if (lv > bestLv && Can(p, c.work)) { best = c.role; bestLv = lv; }
		}
		int fight = Math.Max(Lv(p, SkillDefOf.Shooting), Lv(p, SkillDefOf.Melee));
		if (fight >= 10 && fight > bestLv + 2) best = "Soldier";
		if (best == "Everyone" && Can(p, "Hauling") && bestLv <= 5) best = "Hauler";
		return best;
	}
}

// ---------------- defaults (equal to SCG's own behaviour) ----------------
public static class Defaults
{
	public static List<RolePref> Weapons()
	{
		var l = new List<RolePref>();
		foreach (string r in Roles.All) l.Add(new RolePref(r, (int)WeaponPref.Any));
		Set(l, "Hunter", (int)WeaponPref.LongRange);
		Set(l, "Doctor", (int)WeaponPref.Light);
		Set(l, "Soldier", (int)WeaponPref.Ranged);
		return l;
	}

	public static List<RolePref> Armor()
	{
		var l = new List<RolePref>();
		foreach (string r in Roles.All) l.Add(new RolePref(r, (int)ArmorWeight.Any));
		Set(l, "Soldier", (int)ArmorWeight.Heavy);
		Set(l, "Doctor", (int)ArmorWeight.Light);
		Set(l, "Hunter", (int)ArmorWeight.Light);
		return l;
	}

	private static void Set(List<RolePref> l, string r, int v)
	{
		RolePref x = l.FirstOrDefault(e => e.role == r);
		if (x != null) x.value = v;
	}

	public static List<RoleColor> Colors()
	{
		var l = new List<RoleColor>();
		foreach (string r in Roles.All) l.Add(new RoleColor(r, Color.white, false));
		void C(string r, Color c) { RoleColor x = l.FirstOrDefault(e => e.role == r); if (x != null) x.color = c; }
		C("Doctor", new Color(0.92f, 0.92f, 0.92f));
		C("Soldier", new Color(0.33f, 0.42f, 0.27f));
		C("Hunter", new Color(0.45f, 0.36f, 0.24f));
		C("Builder", new Color(0.85f, 0.6f, 0.2f));
		C("Cook", new Color(0.95f, 0.95f, 0.85f));
		C("Researcher", new Color(0.3f, 0.45f, 0.7f));
		C("Farmer", new Color(0.4f, 0.6f, 0.3f));
		return l;
	}

	// SCG's own work clothing detection, grouped by work type (the editor starts with exactly what SCG does)
	public static List<WorkOutfit> WorkOutfits()
	{
		var res = new List<WorkOutfit>();
		var byWork = new Dictionary<string, WorkOutfit>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (d == null || !d.IsApparel) continue;
			SkillDef sk;
			try { sk = ResearchedLoadout.Specialization.AutoWorkSkillOf(d); } catch { sk = null; }
			if (sk == null) continue;
			WorkTypeDef wt = WorkFor(sk);
			if (wt == null) continue;
			if (!byWork.TryGetValue(wt.defName, out WorkOutfit w)) { byWork[wt.defName] = w = new WorkOutfit(wt.defName); res.Add(w); }
			w.apparel.Add(d.defName);
		}
		return res.OrderBy(w => DefDatabase<WorkTypeDef>.GetNamedSilentFail(w.work)?.naturalPriority ?? 0).Reverse().ToList();
	}

	// the main work type of a skill (the one with that skill first and the highest natural priority)
	public static WorkTypeDef WorkFor(SkillDef sk)
	{
		WorkTypeDef best = null;
		foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
		{
			if (w.relevantSkills == null || w.relevantSkills.Count == 0 || w.relevantSkills[0] != sk || !w.visible) continue;
			if (best == null || w.naturalPriority > best.naturalPriority) best = w;
		}
		return best;
	}

	private static readonly string[] SleepWords = { "pajama", "pyjama", "sleep", "nightgown", "nightwear", "nightdress", "nightshirt", "pyžam", "noční" };
	private static readonly string[] FestiveWords = { "tuxedo", "formal", "gown", "ballgown", "partydress", "festive", "kimono", "yukata", "dirndl", "christmas", "xmas", "santa", "wedding", "slavnost", "svatební" };

	public static List<string> Sleepwear() => ByWords(SleepWords);
	public static List<string> Festive() => ByWords(FestiveWords);

	private static List<string> ByWords(string[] words)
	{
		var l = new List<string>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (d == null || !d.IsApparel || d.apparel == null) continue;
			if (Classify.ArmorOf(d) > 0.25f) continue;   // never armour
			string s = (d.defName + " " + d.label).ToLowerInvariant();
			if (s.Contains("vac") || s.Contains("armor") || s.Contains("armour")) continue;
			if (words.Any(w => s.Contains(w))) l.Add(d.defName);
		}
		return l;
	}
}

// ---------------- classification ----------------
public static class Classify
{
	public static float ArmorOf(ThingDef d)
	{
		if (d == null) return 0f;
		try { return d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, GenStuff.DefaultStuffFor(d)) + 0.5f * d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, GenStuff.DefaultStuffFor(d)); }
		catch { return 0f; }
	}

	public static float ArmorOf(Apparel a)
	{
		if (a == null) return 0f;
		try { return a.GetStatValue(StatDefOf.ArmorRating_Sharp) + 0.5f * a.GetStatValue(StatDefOf.ArmorRating_Blunt); }
		catch { return 0f; }
	}

	// None (clothes) < Light < Medium < Heavy
	public static ArmorWeight WeightOf(Apparel a)
	{
		float v = ArmorOf(a);
		if (v < 0.2f) return ArmorWeight.None;
		if (v < 0.5f) return ArmorWeight.Light;
		if (v < 0.9f) return ArmorWeight.Medium;
		return ArmorWeight.Heavy;
	}

	public static bool Fits(Pawn p, Apparel a)
	{
		try
		{
			return a.def.apparel != null && a.def.apparel.PawnCanWear(p) && ApparelUtility.HasPartsToWear(p, a.def) && ResearchedLoadout.LoadoutFilter.StageOk(p, a.def);
		}
		catch { return false; }
	}
}

// ---------------- per save: favourites, overrides, holds ----------------
public sealed class WardrobeGame : GameComponent
{
	public Dictionary<int, List<int>> favorites = new Dictionary<int, List<int>>();
	public Dictionary<int, string> roleOverride = new Dictionary<int, string>();
	public List<int> exempt = new List<int>();
	public List<int> retireBillMaps = new List<int>();
	// runtime only
	public readonly Dictionary<int, string> hold = new Dictionary<int, string>();
	public readonly Dictionary<int, int> occasionItem = new Dictionary<int, int>();
	private Dictionary<int, int> ownerOf;

	private static WardrobeGame cached;
	private static Game cachedFor;

	public WardrobeGame(Game g) { }

	public static WardrobeGame Get()
	{
		Game g = Current.Game;
		if (g == null) return null;
		if (cachedFor != g || cached == null) { cached = g.GetComponent<WardrobeGame>(); cachedFor = g; }
		return cached;
	}

	public bool Exempt(Pawn p) => p != null && exempt.Contains(p.thingIDNumber);

	// thing id → owner pawn id (who has it as a favourite)
	public int OwnerOf(Thing t)
	{
		if (t == null) return -1;
		if (ownerOf == null)
		{
			ownerOf = new Dictionary<int, int>();
			foreach (var kv in favorites)
				if (kv.Value != null)
					foreach (int id in kv.Value) ownerOf[id] = kv.Key;
		}
		return ownerOf.TryGetValue(t.thingIDNumber, out int o) ? o : -1;
	}

	public void SetFavorites(Pawn p, IEnumerable<Thing> things)
	{
		favorites[p.thingIDNumber] = things.Where(t => t != null).Select(t => t.thingIDNumber).Distinct().ToList();
		ownerOf = null;
	}

	public void ClearFavorites(Pawn p)
	{
		favorites.Remove(p.thingIDNumber);
		ownerOf = null;
	}

	public int FavoriteCount(Pawn p) => favorites.TryGetValue(p.thingIDNumber, out var l) && l != null ? l.Count : 0;

	public override void GameComponentTick()
	{
		Ticker.Tick();
	}

	public override void ExposeData()
	{
		var favKeys = favorites.Keys.ToList();
		var favVals = favorites.Values.Select(v => string.Join(",", v ?? new List<int>())).ToList();
		Scribe_Collections.Look(ref favKeys, "favKeys", LookMode.Value);
		Scribe_Collections.Look(ref favVals, "favVals", LookMode.Value);
		Scribe_Collections.Look(ref roleOverride, "roleOverride", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref exempt, "exempt", LookMode.Value);
		Scribe_Collections.Look(ref retireBillMaps, "retireBillMaps", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			favorites = new Dictionary<int, List<int>>();
			if (favKeys != null && favVals != null)
				for (int i = 0; i < favKeys.Count && i < favVals.Count; i++)
					favorites[favKeys[i]] = (favVals[i] ?? "").Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out int n) ? n : -1).Where(n => n >= 0).ToList();
			roleOverride ??= new Dictionary<int, string>();
			exempt ??= new List<int>();
			retireBillMaps ??= new List<int>();
			ownerOf = null;
		}
	}
}
