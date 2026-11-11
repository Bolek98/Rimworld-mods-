using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace BattleInstinct;

public enum AKind { Ignore, Attack, Area, SelfBuff, AllyBuff, Heal, GameAI, Unknown }

public struct Cast
{
	public Ability ability;
	public LocalTargetInfo target;
	public AKind kind;
}

public static class AbilityBrain
{
	private static readonly Dictionary<AbilityDef, AKind> kinds = new Dictionary<AbilityDef, AKind>();

	// never used on their own: social, rituals, summons, map-changing, resurrection, travel
	private static readonly string[] Never =
	{
		"Convert", "Counsel", "StartRitual", "StartTrial", "StartConversion", "StartAnimaLinking", "Reassure", "WordOfLove",
		"SocialInteraction", "ShowTargetIdeoCertainty", "OffsetPrisonerResistance", "ReimplantXenogerm", "Resurrect",
		"Farskip", "RaidEnemy", "RandomAlliedMilitaryAid", "DropMech", "DropPawns", "Infestation", "Neuroquake",
		"PsychicSlaughter", "FleshbeastFromCorpse", "Transmute", "TrainRandomSkill", "GiveInspiration", "PreachHealth",
		"Chunkskip", "Waterskip", "Wallraise", "Spawn", "Teleport", "ConsumeLeap", "BloodfeederBite", "Flashstorm",
		"StopManhunter", "Firefoampop", "TransferEntropy", "Smokepop", "Bombardment", "Skip", "Bloodfeed", "Ritual",
		"Pregnan", "Xenogerm", "Recruit", "Tame", "Gift", "Shuttle", "Portal"
	};
	private static readonly string[] Offensive =
	{
		"Stun", "Explosion", "Burner", "FireBurst", "FireSpew", "LaunchProjectile", "PowerBeam", "GiveMentalState",
		"PutToSleep", "ReleaseGas", "SprayLiquid", "BrainDamageChance", "DeactivateMechanoid", "AnimalRoar",
		"OnlyTargetHostiles", "Damage", "Blast", "Shock", "Lightning", "Push", "Knockback", "Pull"
	};
	private static readonly string[] Healing = { "FixWorstHealthCondition", "UnnaturalHealing", "Coagulate", "Heal", "Restore", "Regenerat" };

	public static AKind KindOf(AbilityDef def)
	{
		if (def == null) return AKind.Ignore;
		if (kinds.TryGetValue(def, out AKind k)) return k;
		try { k = Classify(def); }
		catch { k = AKind.Unknown; }
		kinds[def] = k;
		return k;
	}

	private static bool Has(IEnumerable<string> names, string[] list) => names.Any(n => list.Any(w => n.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0));

	private static AKind Classify(AbilityDef def)
	{
		if (!def.confirmationDialogText.NullOrEmpty()) return AKind.Ignore;   // the game asks first – too risky to fire alone
		VerbProperties vp = def.verbProperties;
		if (vp == null) return AKind.Ignore;
		if (vp.verbClass != null && vp.verbClass.Name.IndexOf("Jump", StringComparison.OrdinalIgnoreCase) >= 0) return AKind.Ignore;
		var names = new List<string>();
		foreach (AbilityCompProperties c in def.comps)
		{
			if (c?.compClass != null) names.Add(c.compClass.Name);
			names.Add(c?.GetType().Name ?? "");
		}
		names.Add(def.abilityClass?.Name ?? "");
		if (Has(names, Never)) return AKind.Ignore;
		bool area = def.EffectRadius > 0.5f;
		TargetingParameters tp = vp.targetParams;
		bool selfOnly = !vp.targetable || (tp != null && tp.canTargetSelf && !tp.canTargetPawns && !tp.canTargetLocations);

		if (Has(names, Healing)) return selfOnly ? AKind.SelfBuff : AKind.Heal;

		var give = def.comps.OfType<CompProperties_AbilityGiveHediff>().FirstOrDefault();
		bool offensive = Has(names, Offensive) || vp.defaultProjectile != null;
		if (!offensive && give?.hediffDef != null)
		{
			if (give.hediffDef.isBad && def.hostile) offensive = true;
			else if (!give.hediffDef.isBad)
				return !HelpsInCombat(give.hediffDef) ? AKind.Ignore : selfOnly || give.onlyApplyToSelf ? AKind.SelfBuff : AKind.AllyBuff;
		}
		if (offensive)
		{
			if (area || selfOnly) return AKind.Area;
			return AKind.Attack;
		}
		if (def.aiCanUse && def.ai_IsOffensive) return AKind.GameAI;
		return AKind.Unknown;
	}

	// a buff that only lifts mood or changes thoughts is no help in a fight
	private static bool HelpsInCombat(HediffDef h)
	{
		if (h.stages != null)
			foreach (HediffStage st in h.stages)
				if (!st.capMods.NullOrEmpty() || !st.statOffsets.NullOrEmpty() || !st.statFactors.NullOrEmpty()
				    || st.painFactor < 0.99f || st.painOffset < -0.001f || st.totalBleedFactor < 0.99f || st.naturalHealingFactor > 1.01f)
					return true;
		if (h.comps != null && h.comps.Any(c => c.compClass != null && (c.compClass.Name.IndexOf("Shield", StringComparison.OrdinalIgnoreCase) >= 0 || c.compClass.Name.IndexOf("Regenerat", StringComparison.OrdinalIgnoreCase) >= 0 || c.compClass.Name.IndexOf("Heal", StringComparison.OrdinalIgnoreCase) >= 0)))
			return true;
		return false;
	}

	public static bool DefaultOn(AKind k) => k != AKind.Ignore && k != AKind.Unknown;

	public static bool Enabled(PawnData d, AbilityDef def)
	{
		var s = BattleInstinctMod.Settings;
		if (s.blocked.Contains(def.defName)) return false;
		AKind k = KindOf(def);
		if (k == AKind.Ignore) return false;
		if (DefaultOn(k)) return !d.abilityOff.Contains(def.defName);
		return d.abilityOn.Contains(def.defName);
	}

	private static bool Ready(Pawn p, Ability a)
	{
		if (a == null || a.def == null || a.verb == null) return false;
		if (!a.CanCast.Accepted || a.OnCooldown || a.Casting) return false;
		if (a.GizmoDisabled(out _)) return false;
		if (a.def.IsPsycast && p.psychicEntropy != null && BattleInstinctMod.Settings.noOverheat
		    && p.psychicEntropy.WouldOverflowEntropy(a.def.EntropyGain))
			return false;
		return true;
	}

	private static HediffDef GivenHediff(AbilityDef def) => def.comps.OfType<CompProperties_AbilityGiveHediff>().FirstOrDefault()?.hediffDef;

	public static Cast Find(Pawn p, PawnData d, List<Thing> enemies, Battlefield f)
	{
		var none = new Cast();
		if (p.abilities == null) return none;
		List<Ability> list = p.abilities.AllAbilitiesForReading;
		if (list == null || list.Count == 0) return none;
		var s = BattleInstinctMod.Settings;
		float nearest = (enemies[0].Position - p.Position).LengthHorizontal;
		List<Thing> ranked = null;
		foreach (Ability a in list)
		{
			if (a?.def == null || !Enabled(d, a.def) || !Ready(p, a)) continue;
			AKind k = KindOf(a.def);
			if (k == AKind.Unknown) k = AKind.Attack;   // the player switched it on – treat it as an attack
			Cast c = new Cast { ability = a, kind = k };
			switch (k)
			{
				case AKind.Attack:
				case AKind.GameAI:
				{
					if (ranked == null) ranked = enemies.Take(12).OrderByDescending(t => Brain.Score(p, t, f)).ToList();
					foreach (Thing t in ranked)
					{
						if (!GoodAttackTarget(p, a, t, f, k == AKind.GameAI)) continue;
						c.target = t;
						return c;
					}
					break;
				}
				case AKind.Area:
				{
					LocalTargetInfo at = BestArea(p, a, enemies, f);
					if (!at.IsValid) break;
					c.target = at;
					return c;
				}
				case AKind.SelfBuff:
				{
					if (nearest > s.buffRange) break;
					HediffDef h = GivenHediff(a.def);
					if (h != null && p.health.hediffSet.HasHediff(h)) break;
					if (h == null && a.def.comps.Any(x => x.GetType().Name.IndexOf("Heal", StringComparison.OrdinalIgnoreCase) >= 0) && !Hurt(p)) break;
					if (!a.CanApplyOn(new LocalTargetInfo(p))) break;
					c.target = p;
					return c;
				}
				case AKind.AllyBuff:
				{
					if (nearest > s.buffRange) break;
					HediffDef h = GivenHediff(a.def);
					Pawn who = null;
					foreach (Pawn o in Allies(p, f, a))
					{
						if (h != null && o.health.hediffSet.HasHediff(h)) continue;
						if (!o.Drafted && o != p) continue;
						who = o;
						break;
					}
					if (who == null) break;
					c.target = who;
					return c;
				}
				case AKind.Heal:
				{
					Pawn who = Allies(p, f, a).Where(Hurt).OrderBy(o => o.health.summaryHealth.SummaryHealthPercent - o.health.hediffSet.BleedRateTotal).FirstOrDefault();
					if (who == null) break;
					if (a.def.aiCanUse && !a.AICanTargetNow(who)) break;
					c.target = who;
					return c;
				}
			}
		}
		return none;
	}

	private static bool Hurt(Pawn o) => o.health.summaryHealth.SummaryHealthPercent < 0.8f || o.health.hediffSet.BleedRateTotal > 0.1f;

	private static IEnumerable<Pawn> Allies(Pawn p, Battlefield f, Ability a)
	{
		float range = a.verb.verbProps.range;
		yield return p;
		foreach (Pawn o in f.friends)
		{
			if (o == p || o.Faction != Faction.OfPlayer || o.Dead || !o.RaceProps.Humanlike && !o.IsColonyMech) continue;
			if (!o.Position.InHorDistOf(p.Position, range)) continue;
			var t = new LocalTargetInfo(o);
			if (!a.CanApplyOn(t) || !a.verb.CanHitTarget(t)) continue;
			yield return o;
		}
	}

	private static bool GoodAttackTarget(Pawn p, Ability a, Thing t, Battlefield f, bool gameAI)
	{
		var lt = new LocalTargetInfo(t);
		if (!t.Position.InHorDistOf(p.Position, a.verb.verbProps.range + 0.5f)) return false;
		if (!a.CanApplyOn(lt)) return false;
		if ((gameAI || a.def.aiCanUse) && !a.AICanTargetNow(lt)) return false;
		if (!a.verb.CanHitTarget(lt)) return false;
		if (t is Pawn e)
		{
			if (e.stances?.stunner != null && e.stances.stunner.Stunned) return false;
			if (e.InMentalState && a.def.comps.Any(x => x is CompProperties_AbilityGiveMentalState)) return false;
			HediffDef h = GivenHediff(a.def);
			if (h != null && e.health.hediffSet.HasHediff(h)) return false;
			// not worth it on a weak animal
			if (e.RaceProps.Animal && e.RaceProps.baseBodySize < 0.6f) return false;
		}
		if (a.verb.verbProps.defaultProjectile != null && BattleInstinctMod.Settings.avoidFriendlyFire && Brain.FriendlyRisk(p, p.Position, t, f)) return false;
		if (a.def.EffectRadius > 0.5f && FriendsIn(p, t.Position, a.def.EffectRadius + 1f, f, a) > 0) return false;
		return true;
	}

	private static int FriendsIn(Pawn caster, IntVec3 c, float r, Battlefield f, Ability a)
	{
		int n = 0;
		float r2 = r * r;
		foreach (Pawn o in f.friends)
		{
			if (o == caster && !(a.def.comps.OfType<CompProperties_AbilityGiveHediff>().Any(g => g.applyToSelf))) continue;
			if (o.Spawned && (o.Position - c).LengthHorizontalSquared <= r2) n++;
		}
		return n;
	}

	private static LocalTargetInfo BestArea(Pawn p, Ability a, List<Thing> enemies, Battlefield f)
	{
		var s = BattleInstinctMod.Settings;
		float r = Mathf.Max(a.def.EffectRadius, 1.5f);
		VerbProperties vp = a.verb.verbProps;
		bool selfCentered = !vp.targetable;
		var centers = new List<LocalTargetInfo>();
		if (selfCentered) centers.Add(p);
		else foreach (Thing e in enemies.Take(15)) if (e.Position.InHorDistOf(p.Position, vp.range)) centers.Add(e);
		LocalTargetInfo best = LocalTargetInfo.Invalid;
		int bestN = s.aoeMinEnemies - 1;
		foreach (LocalTargetInfo c in centers)
		{
			IntVec3 cell = c.Cell;
			int n = 0;
			foreach (Thing e in enemies)
				if ((e.Position - cell).LengthHorizontalSquared <= r * r) n++;
			if (n <= bestN) continue;
			if (FriendsIn(p, cell, r + 1f, f, a) > 0) continue;
			if (!a.CanApplyOn(c)) continue;
			if (!selfCentered && !a.verb.CanHitTarget(c)) continue;
			if (a.def.aiCanUse && !a.AICanTargetNow(c)) continue;
			best = c;
			bestN = n;
		}
		return best;
	}

	public static void Do(Pawn p, PawnData d, Cast c)
	{
		Ability a = c.ability;
		if (a.verb.verbProps.nonInterruptingSelfCast)
			a.verb.TryStartCastOn(a.verb.Caster);
		else
		{
			Job j = a.GetJob(c.target, c.target);
			Brain.Issue(p, d, j, c.target.Thing, null);
		}
		Brain.Say(p, d, a.def.LabelCap);
	}
}
