using System;
using System.Collections.Generic;
using System.Linq;
using ResearchedLoadout;
using RimWorld;
using Verse;
using Verse.AI;
using RimWorld.Planet;

namespace SCGWardrobe;

// Wardrobe's dressing provider: Smart Colonist Gear asks it at every dressing decision.
// Every answer starts from SCG's own answer, so with untouched settings the colonists dress as with SCG alone,
// plus the Wardrobe rules (roles, situations, care, favourites, occasions).
public sealed class WardrobeProvider : IDressingProvider
{
	public static readonly WardrobeProvider Instance = new WardrobeProvider();
	private static WardrobeSettings S => WardrobeMod.Settings;

	// 0.1.2: no translation before the game has loaded its languages (the provider registers in the mod constructor)
	public string Name => LanguageDatabase.activeLanguage != null && "WR_Name".CanTranslate() ? (string)"WR_Name".Translate() : "Smart Colonist Gear – Wardrobe";

	private static bool On(Pawn p)
	{
		if (S == null || !S.enabled || p == null) return false;
		WardrobeGame g = WardrobeGame.Get();
		return g == null || !g.Exempt(p);
	}

	private static float HpPct(Thing t) => t.def.useHitPoints && t.MaxHitPoints > 0 ? (float)t.HitPoints / t.MaxHitPoints : 1f;

	// ---------------- occasion pieces ----------------
	private static HashSet<string> sleepSet, festiveSet;
	private static int setsVersion = -1;
	public static int SetsVersion;

	private static void Sets()
	{
		if (setsVersion == SetsVersion && sleepSet != null) return;
		setsVersion = SetsVersion;
		sleepSet = new HashSet<string>(S.SleepDefs);
		festiveSet = new HashSet<string>(S.FestiveDefs);
	}

	public static bool IsSleepwear(ThingDef d) { Sets(); return d != null && sleepSet.Contains(d.defName); }
	public static bool IsFestive(ThingDef d) { Sets(); return d != null && festiveSet.Contains(d.defName); }

	public static string HoldOf(Pawn p)
	{
		WardrobeGame g = WardrobeGame.Get();
		return g != null && p != null && g.hold.TryGetValue(p.thingIDNumber, out string h) ? h : null;
	}

	// the occasion clothes are candidates only while the occasion lasts
	private static bool OccasionAllowed(Pawn p, ThingDef d)
	{
		string h = HoldOf(p);
		if (S.sleepwear && IsSleepwear(d)) return h == "sleep";
		if (S.festive && S.festiveOnlyOccasions && IsFestive(d)) return h == "festive";
		return true;
	}

	// ---------------- situation and armour weight ----------------
	public static Situation SituationOf(Pawn p)
	{
		if (p.Drafted || (p.Map != null && SteadyThreat.Active(p.Map))) return Situation.Defense;
		Job j = p.CurJob;
		if (j != null && j.def == JobDefOf.Hunt) return Situation.Hunt;
		if (p.GetCaravan() != null || (p.Map == null && p.IsWorldPawn())) return Situation.Caravan;
		return Situation.Work;
	}

	public static ArmorWeight Desired(Pawn p, Situation s)
	{
		switch (s)
		{
			case Situation.Defense: return (ArmorWeight)S.defenseArmor;
			case Situation.Hunt: return (ArmorWeight)S.huntArmor;
			case Situation.Caravan: return (ArmorWeight)S.caravanArmor;
		}
		string role = Roles.Of(p);
		RolePref r = S.ArmorPrefs.FirstOrDefault(x => x.role == role);
		ArmorWeight w = r == null ? ArmorWeight.Any : (ArmorWeight)r.value;
		if (w == ArmorWeight.Any)
		{
			RolePref all = S.ArmorPrefs.FirstOrDefault(x => x.role == "Everyone");
			if (all != null) w = (ArmorWeight)all.value;
		}
		return w;
	}

	// raw-scale bonus of a piece for the wanted weight (only pieces that are armour at all)
	private static float ArmorBonus(Pawn p, Apparel a)
	{
		if (!S.armorSets) return 0f;
		ArmorWeight have = Classify.WeightOf(a);
		if (have == ArmorWeight.None) return 0f;
		ArmorWeight want = Desired(p, SituationOf(p));
		if (want == ArmorWeight.Any) return 0f;
		int diff = (int)have - (int)want;
		if (diff == 0) return 0.3f;
		if (diff > 0) return -0.35f * diff;          // heavier than wanted
		return want == ArmorWeight.Heavy ? -0.2f * -diff : -0.05f * -diff;   // lighter than wanted
	}

	// ---------------- IDressingProvider ----------------
	public bool AllowApparel(Pawn p, Apparel a)
	{
		if (!On(p) || a == null) return true;
		if (S.noCorpse && a.WornByCorpse) return false;
		WardrobeGame g = WardrobeGame.Get();
		if (S.favorites && g != null)
		{
			int owner = g.OwnerOf(a);
			if (owner >= 0 && owner != p.thingIDNumber) return false;
		}
		if (S.care && HpPct(a) < S.replaceBelow) return false;
		return OccasionAllowed(p, a.def);
	}

	public float ApparelScore(Pawn p, Apparel a, float scg, bool worn, float scale)
	{
		if (!On(p) || a == null || float.IsNegativeInfinity(scg)) return scg;
		float bonus = ArmorBonus(p, a);
		WardrobeGame g = WardrobeGame.Get();
		if (S.favorites && g != null && g.OwnerOf(a) == p.thingIDNumber) bonus += 0.5f;
		string h = HoldOf(p);
		if (h == "sleep" && IsSleepwear(a.def)) bonus += 2f;
		if (h == "festive" && IsFestive(a.def)) bonus += 2f;
		return scg + bonus * scale;
	}

	public bool KeepWorn(Pawn p, Apparel a)
	{
		if (!On(p) || a == null) return true;
		if (S.care && HpPct(a) < S.replaceBelow) return false;
		if (S.noCorpse && a.WornByCorpse) return false;
		WardrobeGame g = WardrobeGame.Get();
		if (S.favorites && g != null)
		{
			int owner = g.OwnerOf(a);
			if (owner >= 0 && owner != p.thingIDNumber) return false;
		}
		return OccasionAllowed(p, a.def);
	}

	public bool MayRemove(Pawn p, Apparel a)
	{
		if (!On(p) || a == null) return true;
		string h = HoldOf(p);
		if (h == "sleep" && IsSleepwear(a.def)) return false;
		if (h == "festive" && IsFestive(a.def)) return false;
		return true;
	}

	public static WeaponPref KindOf(ThingDef d)
	{
		if (d == null) return WeaponPref.Any;
		if (d.IsMeleeWeapon) return WeaponPref.Melee;
		if (!d.IsRangedWeapon) return WeaponPref.Any;
		float range = d.Verbs != null && d.Verbs.Count > 0 && d.Verbs[0] != null ? d.Verbs[0].range : 0f;
		if (range >= 30f) return WeaponPref.LongRange;
		if (range < 20f) return WeaponPref.ShortRange;
		return WeaponPref.Ranged;
	}

	public static bool Matches(ThingDef d, WeaponPref want)
	{
		if (want == WeaponPref.Any) return true;
		WeaponPref k = KindOf(d);
		switch (want)
		{
			case WeaponPref.Melee: return k == WeaponPref.Melee;
			case WeaponPref.Ranged: return d.IsRangedWeapon;
			case WeaponPref.LongRange: return k == WeaponPref.LongRange;
			case WeaponPref.ShortRange: return k == WeaponPref.ShortRange;
			case WeaponPref.Light:
				float m = 0f;
				try { m = d.GetStatValueAbstract(StatDefOf.Mass); } catch { }
				return m <= 3f;
		}
		return true;
	}

	public float WeaponScore(Pawn p, Thing w, float scg)
	{
		if (!On(p) || w == null || scg <= 0f) return scg;
		float f = 1f;
		if (S.roleWeapons)
		{
			string role = Roles.Of(p);
			RolePref r = S.WeaponPrefs.FirstOrDefault(x => x.role == role);
			WeaponPref want = r == null ? WeaponPref.Any : (WeaponPref)r.value;
			if (want != WeaponPref.Any) f *= Matches(w.def, want) ? 1.35f : 0.75f;
		}
		if (S.care && HpPct(w) < S.damagedWeaponBelow) f *= 0.6f;
		return scg * f;
	}

	public Apparel BattleArmor(Pawn p, float maxDist, bool threat, Func<Apparel> scgDefault)
	{
		Apparel d = scgDefault();
		if (!On(p) || d == null || !S.armorSets) return d;
		if (!AllowApparel(p, d)) return null;
		if (threat) return d;
		ArmorWeight want = Desired(p, SituationOf(p) == Situation.Defense ? Situation.Defense : Situation.Hunt);
		if (want == ArmorWeight.None) return null;
		if (want != ArmorWeight.Any && (int)Classify.WeightOf(d) > (int)want) return null;
		return d;
	}

	public Apparel WorkGarment(Pawn p, Map map, SkillDef skill, float maxDist, Func<Apparel> scgDefault)
	{
		Apparel d = scgDefault();
		if (!On(p) || d == null) return d;
		return AllowApparel(p, d) ? d : null;
	}

	// ---------------- work outfit editor ----------------
	private static Dictionary<string, SkillDef> editorMap;
	public static int EditorVersion;
	private static int editorBuilt = -1;

	public SkillDef WorkSkillOf(ThingDef def, Func<SkillDef> scgDefault)
	{
		if (S == null || !S.enabled || !S.workEditor || def == null) return scgDefault();
		if (editorMap == null || editorBuilt != EditorVersion)
		{
			editorBuilt = EditorVersion;
			editorMap = new Dictionary<string, SkillDef>();
			foreach (WorkOutfit w in S.WorkOutfits)
			{
				WorkTypeDef wt = DefDatabase<WorkTypeDef>.GetNamedSilentFail(w.work);
				SkillDef sk = wt?.relevantSkills?.FirstOrDefault();
				if (sk == null) continue;
				foreach (string a in w.apparel) if (!editorMap.ContainsKey(a)) editorMap[a] = sk;
			}
		}
		return editorMap.TryGetValue(def.defName, out SkillDef s) ? s : null;
	}

	public static void EditorChanged()
	{
		EditorVersion++;
		try { Specialization.ClearWorkSkillCache(); } catch { }
	}

	public bool Hold(Pawn p)
	{
		if (!On(p)) return false;
		if (HoldOf(p) == null) return false;
		if (p.Drafted || (p.Map != null && SteadyThreat.Active(p.Map))) return false;
		return true;
	}
}
