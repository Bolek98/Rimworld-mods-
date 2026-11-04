using System;
using RimWorld;
using Verse;

namespace ResearchedLoadout
{
	// 2.18.0: dressing provider. Every dressing decision of Smart Colonist Gear goes through here.
	// Without an add-on the default provider is used and everything works exactly as before.
	// An add-on (Smart Colonist Gear – Wardrobe) registers its own provider and then takes over the
	// decisions: it gets SCG's own answer (score, candidate, skill) and may keep, change or replace it,
	// so its defaults can equal SCG's behaviour. SCG's background ticks keep running but only ask the
	// provider – there is always one decision maker per question.
	public interface IDressingProvider
	{
		string Name { get; }
		// is this piece allowed as a candidate for this pawn at all
		bool AllowApparel(Pawn p, Apparel a);
		// final score of a piece (worn or candidate); scale = 1 for the game's raw apparel score, 15 for SCG's own
		// dresser score; return float.NegativeInfinity to rule the piece out
		float ApparelScore(Pawn p, Apparel a, float scgScore, bool worn, float scale);
		// a worn piece that should be replaced as soon as something else fits (tattered, someone else's favourite …)
		bool KeepWorn(Pawn p, Apparel a);
		// may SCG take this worn piece off now (temperature, stripping, work clothes off …)
		bool MayRemove(Pawn p, Apparel a);
		// final weapon score (SCG's own value in, the provider's value out)
		float WeaponScore(Pawn p, Thing weapon, float scgScore);
		// armour for battle (raid, drafted, hunting); scgDefault = SCG's own choice
		Apparel BattleArmor(Pawn p, float maxDist, bool threat, Func<Apparel> scgDefault);
		// the work garment for a skill; scgDefault = SCG's own choice
		Apparel WorkGarment(Pawn p, Map map, SkillDef skill, float maxDist, Func<Apparel> scgDefault);
		// which skill a piece of apparel is work clothing for (null = none); scgDefault = SCG's own detection
		SkillDef WorkSkillOf(ThingDef def, Func<SkillDef> scgDefault);
		// true = the provider dresses this pawn right now (occasion, sleepwear …): SCG leaves his apparel alone
		bool Hold(Pawn p);
	}

	public sealed class DefaultDressing : IDressingProvider
	{
		public string Name => "Smart Colonist Gear";
		public bool AllowApparel(Pawn p, Apparel a) => true;
		public float ApparelScore(Pawn p, Apparel a, float scgScore, bool worn, float scale) => scgScore;
		public bool KeepWorn(Pawn p, Apparel a) => true;
		public bool MayRemove(Pawn p, Apparel a) => true;
		public float WeaponScore(Pawn p, Thing weapon, float scgScore) => scgScore;
		public Apparel BattleArmor(Pawn p, float maxDist, bool threat, Func<Apparel> scgDefault) => scgDefault();
		public Apparel WorkGarment(Pawn p, Map map, SkillDef skill, float maxDist, Func<Apparel> scgDefault) => scgDefault();
		public SkillDef WorkSkillOf(ThingDef def, Func<SkillDef> scgDefault) => scgDefault();
		public bool Hold(Pawn p) => false;
	}

	public static class Dressing
	{
		public static readonly IDressingProvider Default = new DefaultDressing();
		private static IDressingProvider provider;

		// the add-on that took over the dressing decisions (null = SCG itself)
		public static IDressingProvider External => provider;
		public static IDressingProvider Current => provider ?? Default;

		public static void Register(IDressingProvider p)
		{
			provider = p;
			try { Specialization.ClearWorkSkillCache(); } catch { }
			MLog.Info("[Smart Colonist Gear] dressing is managed by " + (p?.Name ?? "Smart Colonist Gear") + ".");
		}

		public static void Unregister(IDressingProvider p)
		{
			if (provider == p) provider = null;
			try { Specialization.ClearWorkSkillCache(); } catch { }
		}

		private static void Fail(Exception e, int key)
		{
			MLog.WarnOnce("[Smart Colonist Gear] dressing provider " + (provider?.Name ?? "?") + ": " + e.Message, key);
		}

		internal static bool Allow(Pawn p, Apparel a)
		{
			if (provider == null) return true;
			try { return provider.AllowApparel(p, a); } catch (Exception e) { Fail(e, 8180001); return true; }
		}

		internal static float Score(Pawn p, Apparel a, float scg, bool worn, float scale)
		{
			if (provider == null) return scg;
			try { return provider.ApparelScore(p, a, scg, worn, scale); } catch (Exception e) { Fail(e, 8180002); return scg; }
		}

		internal static bool KeepWorn(Pawn p, Apparel a)
		{
			if (provider == null) return true;
			try { return provider.KeepWorn(p, a); } catch (Exception e) { Fail(e, 8180003); return true; }
		}

		internal static bool MayRemove(Pawn p, Apparel a)
		{
			if (provider == null) return true;
			try { return provider.MayRemove(p, a); } catch (Exception e) { Fail(e, 8180004); return true; }
		}

		internal static float Weapon(Pawn p, Thing w, float scg)
		{
			if (provider == null) return scg;
			try { return provider.WeaponScore(p, w, scg); } catch (Exception e) { Fail(e, 8180005); return scg; }
		}

		internal static Apparel Armor(Pawn p, float maxDist, bool threat, Func<Apparel> scg)
		{
			if (provider == null) return scg();
			try { return provider.BattleArmor(p, maxDist, threat, scg); } catch (Exception e) { Fail(e, 8180006); return scg(); }
		}

		internal static Apparel Garment(Pawn p, Map map, SkillDef sk, float maxDist, Func<Apparel> scg)
		{
			if (provider == null) return scg();
			try { return provider.WorkGarment(p, map, sk, maxDist, scg); } catch (Exception e) { Fail(e, 8180007); return scg(); }
		}

		internal static SkillDef WorkSkill(ThingDef def, Func<SkillDef> scg)
		{
			if (provider == null) return scg();
			try { return provider.WorkSkillOf(def, scg); } catch (Exception e) { Fail(e, 8180008); return scg(); }
		}

		public static bool Held(Pawn p)
		{
			if (provider == null || p == null) return false;
			try { return provider.Hold(p); } catch (Exception e) { Fail(e, 8180009); return false; }
		}
	}
}
