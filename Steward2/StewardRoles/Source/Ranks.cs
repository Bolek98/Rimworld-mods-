using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ===========================================================================
// 0.2.0: ranks. The longer a colonist really does the work of his role, the better he gets at it:
// Apprentice → Experienced → Master. Each rank is a visible (good) health condition with small
// bonuses for that role (a doctor tends better, a cook poisons less, a miner digs faster …).
// Experience counts only time spent on the role's own work and is kept per role, so switching
// roles back and forth loses nothing. A letter announces every promotion.
// ===========================================================================
public static class Ranks
{
	public const int TickEvery = 250;
	// game ticks of real role work: 60 000 = one full day of work
	public const int ExperiencedAt = 60000;      // ~4 days of a normal work share
	public const int MasterAt = 240000;          // ~16 days

	private static readonly Dictionary<ColonyRole, HediffDef> defs = new Dictionary<ColonyRole, HediffDef>();

	public static int RankOf(int xp) => xp >= MasterAt ? 2 : xp >= ExperiencedAt ? 1 : 0;

	public static string RankLabel(int rank) => ("SR_Rank" + rank).Translate();

	// progress to the next rank, 0..1 (1 at master)
	public static float Progress(int xp)
	{
		if (xp >= MasterAt) return 1f;
		if (xp >= ExperiencedAt) return (xp - ExperiencedAt) / (float)(MasterAt - ExperiencedAt);
		return xp / (float)ExperiencedAt;
	}

	private struct Bonus
	{
		public string stat;
		public bool factor;
		public float v1, v2;    // experienced, master
		public Bonus(string s, bool f, float a, float b) { stat = s; factor = f; v1 = a; v2 = b; }
	}

	private static IEnumerable<Bonus> Bonuses(ColonyRole r)
	{
		switch (r)
		{
			case ColonyRole.Doctor:
				yield return new Bonus("MedicalTendQuality", false, 0.05f, 0.10f);
				yield return new Bonus("MedicalSurgerySuccessChance", false, 0.04f, 0.08f);
				yield return new Bonus("MedicalTendSpeed", true, 1.10f, 1.20f);
				break;
			case ColonyRole.Warden:
				yield return new Bonus("NegotiationAbility", false, 0.10f, 0.20f);
				yield return new Bonus("SocialImpact", false, 0.05f, 0.10f);
				break;
			case ColonyRole.Cook:
				yield return new Bonus("CookSpeed", true, 1.15f, 1.30f);
				yield return new Bonus("FoodPoisonChance", true, 0.70f, 0.40f);
				break;
			case ColonyRole.Hunter:
				yield return new Bonus("HuntingStealth", false, 0.10f, 0.20f);
				yield return new Bonus("AimingDelayFactor", true, 0.92f, 0.85f);
				break;
			case ColonyRole.Farmer:
				yield return new Bonus("PlantWorkSpeed", true, 1.10f, 1.20f);
				yield return new Bonus("PlantHarvestYield", false, 0.05f, 0.10f);
				break;
			case ColonyRole.Builder:
				yield return new Bonus("ConstructionSpeed", true, 1.10f, 1.20f);
				yield return new Bonus("ConstructSuccessChance", false, 0.03f, 0.06f);
				break;
			case ColonyRole.Miner:
				yield return new Bonus("MiningSpeed", true, 1.15f, 1.30f);
				yield return new Bonus("MiningYield", false, 0.05f, 0.10f);
				break;
			case ColonyRole.Crafter:
				yield return new Bonus("GeneralLaborSpeed", true, 1.10f, 1.20f);
				break;
			case ColonyRole.Researcher:
				yield return new Bonus("ResearchSpeed", true, 1.10f, 1.20f);
				break;
			case ColonyRole.Handler:
				yield return new Bonus("TameAnimalChance", true, 1.15f, 1.30f);
				yield return new Bonus("TrainAnimalChance", true, 1.15f, 1.30f);
				break;
			case ColonyRole.Hauler:
				yield return new Bonus("CarryingCapacity", false, 15f, 30f);
				yield return new Bonus("MoveSpeed", false, 0.15f, 0.30f);
				break;
			case ColonyRole.Soldier:
				yield return new Bonus("AimingDelayFactor", true, 0.92f, 0.85f);
				yield return new Bonus("MeleeHitChance", false, 0.03f, 0.06f);
				yield return new Bonus("PainShockThreshold", false, 0.05f, 0.10f);
				break;
		}
	}

	// the rank conditions are made in code (one per role), so a stat missing in some game version or
	// mod setup is simply left out instead of breaking the defs
	public static void MakeDefs()
	{
		foreach (ColonyRole r in RoleWork.All)
		{
			if (r == ColonyRole.None) continue;
			try
			{
				var def = new HediffDef
				{
					defName = "SR_Rank_" + r,
					label = RoleWork.Label(r).ToLower(),
					description = "SR_RankHediffDesc".Translate(RoleWork.Label(r)),
					hediffClass = typeof(Hediff),
					isBad = false,
					everCurableByItem = false,
					initialSeverity = 1f,
					maxSeverity = 2f,
					defaultLabelColor = new Color(0.55f, 0.85f, 1f),
					stages = new List<HediffStage>()
				};
				for (int rank = 1; rank <= 2; rank++)
				{
					var st = new HediffStage { minSeverity = rank == 1 ? 0.5f : 1.5f, label = RankLabel(rank).ToLower(), becomeVisible = true };
					foreach (Bonus b in Bonuses(r))
					{
						StatDef sd = DefDatabase<StatDef>.GetNamedSilentFail(b.stat);
						if (sd == null) continue;
						float v = rank == 1 ? b.v1 : b.v2;
						if (b.factor) (st.statFactors ??= new List<StatModifier>()).Add(new StatModifier { stat = sd, value = v });
						else (st.statOffsets ??= new List<StatModifier>()).Add(new StatModifier { stat = sd, value = v });
					}
					def.stages.Add(st);
				}
				// stage 0 (below 0.5) is never used, but the list must start at 0
				def.stages.Insert(0, new HediffStage { minSeverity = 0f, becomeVisible = false });
				def.shortHash = (ushort)(GenText.StableStringHash(def.defName) & 0xFFFF);
				def.modContentPack = RolesMod.Pack;
				DefDatabase<HediffDef>.Add(def);
				defs[r] = def;
			}
			catch (Exception e) { Log.Warning("[Steward Roles] rank of " + r + ": " + e.Message); }
		}
	}

	public static HediffDef Def(ColonyRole r) => defs.TryGetValue(r, out HediffDef d) ? d : null;

	// is the pawn doing the work of his role right now
	public static bool DoingRole(Pawn p, ColonyRole r)
	{
		if (r == ColonyRole.Soldier)
		{
			if (p.Drafted) return true;
			JobDef j = p.CurJobDef;
			return j == JobDefOf.AttackMelee || j == JobDefOf.AttackStatic || j == JobDefOf.Wait_Combat;
		}
		WorkTypeDef w = p.CurJob?.workGiverDef?.workType;
		return w != null && RoleWork.Main(r).Contains(w);
	}

	// the right rank condition on the pawn: only the one of his current role
	public static void Sync(Pawn p, ColonyRole r, int xp)
	{
		if (p?.health?.hediffSet == null) return;
		int rank = r == ColonyRole.None ? 0 : RankOf(xp);
		HediffDef want = rank > 0 ? Def(r) : null;
		List<Hediff> list = p.health.hediffSet.hediffs;
		for (int i = list.Count - 1; i >= 0; i--)
		{
			Hediff h = list[i];
			if (h.def.defName.StartsWith("SR_Rank_", StringComparison.Ordinal) && h.def != want)
				p.health.RemoveHediff(h);
		}
		if (want == null) return;
		Hediff have = p.health.hediffSet.GetFirstHediffOfDef(want);
		if (have == null)
		{
			have = HediffMaker.MakeHediff(want, p);
			have.Severity = rank;
			p.health.AddHediff(have);
		}
		else if (Mathf.Abs(have.Severity - rank) > 0.01f)
			have.Severity = rank;
	}

	// a short text of what the rank gives, for letters and tooltips
	public static string BonusText(ColonyRole r, int rank)
	{
		if (rank <= 0) return "SR_RankNoBonus".Translate();
		var parts = new List<string>();
		foreach (Bonus b in Bonuses(r))
		{
			StatDef sd = DefDatabase<StatDef>.GetNamedSilentFail(b.stat);
			if (sd == null) continue;
			float v = rank == 1 ? b.v1 : b.v2;
			string val;
			if (b.factor) val = (v >= 1f ? "+" : "−") + Mathf.RoundToInt(Mathf.Abs(v - 1f) * 100f) + " %";
			else if (sd.toStringStyle == ToStringStyle.PercentZero || sd.toStringStyle == ToStringStyle.PercentOne || sd.toStringStyle == ToStringStyle.PercentTwo)
				val = "+" + Mathf.RoundToInt(v * 100f) + " %";
			else val = "+" + v.ToString("0.##");
			parts.Add(sd.LabelCap + " " + val);
		}
		return string.Join(", ", parts);
	}
}
