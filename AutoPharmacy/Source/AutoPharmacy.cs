using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoPharmacy;

// ---------------------------------------------------------------------------
// Auto Pharmacy: doctors give the right drug to whoever needs it.
//   * Every drug in the game (vanilla and any mod) is read once: what its
//     effects really do – less pain, faster immunity, less bleeding, more
//     consciousness/breathing/blood pumping, cures a named illness, rest, mood.
//   * Every colonist is checked: how bad is the pain, will the illness win the
//     race against immunity, bleeding, blood loss, failing organs, mood …
//   * The best drug for the problem is chosen by how serious it is: weak
//     problem → mild drug; addictive drugs only when it is really bad (setting).
//   * Never twice: a drug whose effect is still running is not given again,
//     overdose risk, teetotalers and your blocked drugs are respected.
// ---------------------------------------------------------------------------

public enum Need { Critical, Pain, Immunity, Cure, Bleeding, BloodLoss, Consciousness, Breathing, BloodPumping, BloodFiltration, Moving, Rest, Mood, Manipulation, Sight, Prevention, Contraception, Fertility, Addiction, Hemogen, Healing }

public sealed class DrugInfo
{
	public ThingDef def;
	public readonly Dictionary<Need, float> effects = new Dictionary<Need, float>();
	public readonly HashSet<HediffDef> cures = new HashSet<HediffDef>();
	public readonly HashSet<HediffDef> gives = new HashSet<HediffDef>();
	public float addictiveness;
	public bool overdoses;
	public bool isDrug;
	public bool usable;   // used on the patient (injectors, serums …), not eaten
	// 1.2.0: combat drugs
	public bool combatFlag;      // CompProperties_Drug.isCombatEnhancingDrug
	public float combatScore;    // how much it helps in a fight (moving, manipulation, sight, consciousness, pain)
	public ChemicalDef chemical;
	public float minToleranceToAddict;
	public float largeOverdose;
	public float overdoseMax;   // 1.3.1: how much overdose one dose adds at most
	public bool Combat => isDrug && !usable && (combatFlag || combatScore >= 0.15f);
	// 1.3.0: taken regularly, not for an illness (prevention, contraception, fertility)
	public bool Regimen => effects.ContainsKey(Need.Prevention) || effects.ContainsKey(Need.Contraception) || effects.ContainsKey(Need.Fertility);

	public void Add(Need n, float v)
	{
		if (v <= 0.001f)
			return;
		effects.TryGetValue(n, out float o);
		effects[n] = Mathf.Max(o, v);
	}

	public bool Useful => effects.Count > 0 || cures.Count > 0;

	public string Summary()
	{
		var parts = new List<string>();
		foreach (var kv in effects.OrderByDescending(k => k.Value))
			if (kv.Key != Need.Cure)
				parts.Add(("AP_Need_" + kv.Key).Translate() + " " + (kv.Value * 100f).ToString("0") + "%");
		if (cures.Count > 0)
			parts.Add("AP_Cures".Translate(string.Join(", ", cures.Select(h => h.label ?? h.defName))));
		if (addictiveness > 0f)
			parts.Add("AP_Addictive".Translate((addictiveness * 100f).ToString("0")));
		return string.Join(" · ", parts);
	}
}

public static class Pharmacy
{
	private static List<DrugInfo> drugs;
	public static List<DrugInfo> Drugs
	{
		get
		{
			if (drugs == null)
				Analyse();
			return drugs;
		}
	}

	public static void Reset() => drugs = null;

	public static bool HasFamilyDrugs => Drugs.Any(d => d.effects.ContainsKey(Need.Contraception) || d.effects.ContainsKey(Need.Fertility));

	private static void Analyse()
	{
		var list = new List<DrugInfo>();
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			try
			{
				if (d.IsCorpse || d.category != ThingCategory.Item)
					continue;
				if (d.HasComp(typeof(CompUsable)) && !d.comps.NullOrEmpty())
				{
					// injectors, serums, kits … that are used on a pawn
					var u = new DrugInfo { def = d, usable = true, isDrug = d.IsDrug };
					foreach (CompProperties c in d.comps)
						if (c is CompProperties_UseEffect ue)
							ReadUseEffect(u, ue);
					if (u.Useful)
						list.Add(u);
					continue;
				}
				if (d.ingestible == null || d.ingestible.outcomeDoers.NullOrEmpty())
					continue;
				var info = new DrugInfo { def = d, isDrug = d.IsDrug };
				CompProperties_Drug cp = d.GetCompProperties<CompProperties_Drug>();
				if (cp != null)
				{
					info.addictiveness = cp.addictiveness;
					info.overdoses = cp.overdoseSeverityOffset.max > 0f;
					info.combatFlag = cp.isCombatEnhancingDrug;
					info.chemical = cp.chemical;
					info.minToleranceToAddict = cp.minToleranceToAddict;
					info.largeOverdose = cp.largeOverdoseChance;
					info.overdoseMax = cp.overdoseSeverityOffset.max;
				}
				foreach (IngestionOutcomeDoer doer in d.ingestible.outcomeDoers)
					ReadDoer(info, doer);
				// a meal that only lifts mood or rest is food, not a medicine
				if (!info.isDrug && !d.IsMedicine && d.ingestible.CachedNutrition > 0.05f && info.cures.Count == 0
				    && info.effects.Keys.All(k => k == Need.Mood || k == Need.Rest))
					continue;
				// 1.3.3: a meal is never handed out as medicine (unless it cures something) – colonists eat it themselves
				if (d.ingestible.IsMeal && info.cures.Count == 0)
					continue;
				if (info.Useful)
					list.Add(info);
			}
			catch (Exception ex)
			{
				Log.WarningOnce("[Auto Pharmacy] " + d.defName + ": " + ex.Message, d.shortHash ^ 0x4150);
			}
		}
		drugs = list;
		Log.Message("[Auto Pharmacy] " + list.Count + " drugs understood: " + string.Join(", ", list.Take(40).Select(i => i.def.defName)) + (list.Count > 40 ? " …" : ""));
	}

	private static void ReadDoer(DrugInfo info, IngestionOutcomeDoer doer)
	{
		if (doer is IngestionOutcomeDoer_GiveHediff gh)
		{
			if (gh.hediffDef != null)
			{
				info.gives.Add(gh.hediffDef);
				ReadHediff(info, gh.hediffDef);
			}
			return;
		}
		if (doer is IngestionOutcomeDoer_OffsetNeed on)
		{
			if (on.need == NeedDefOf.Rest && on.offset > 0f) info.Add(Need.Rest, Mathf.Clamp01(on.offset));
			else if (on.need != null && on.need.defName == "Mood" && on.offset > 0f) info.Add(Need.Mood, Mathf.Clamp01(on.offset * 2f));
			return;
		}
		// 1.4.0: hemogen packs (Biotech) for sanguophages
		string tn = doer.GetType().Name;
		if (tn.IndexOf("Hemogen", StringComparison.OrdinalIgnoreCase) >= 0)
		{
			info.Add(Need.Hemogen, 1f);
			return;
		}
		// modded outcome doers: read every HediffDef they name
		bool removes = Has(tn, "Remove", "Cure", "Heal", "Reduce", "Clear", "Treat");
		foreach (HediffDef h in HediffsIn(doer))
		{
			if (removes)
			{
				if (h.isBad)
				{
					info.cures.Add(h);
					info.Add(Need.Cure, 1f);
				}
			}
			else
			{
				info.gives.Add(h);
				ReadHediff(info, h);
			}
		}
	}

	private static void ReadUseEffect(DrugInfo info, CompProperties_UseEffect ue)
	{
		Type cls = ue.compClass;
		string tn = ue.GetType().Name + " " + (cls?.Name ?? "");
		if (cls != null && typeof(CompUseEffect_FixWorstHealthCondition).IsAssignableFrom(cls))
		{
			info.Add(Need.Critical, 1f);   // heals the worst condition – kept for life or death
			return;
		}
		if (ue is CompProperties_UseEffectAddHediff ah)
		{
			if (ah.hediffDef != null)
			{
				info.gives.Add(ah.hediffDef);
				ReadHediff(info, ah.hediffDef);
			}
			return;
		}
		if (Has(tn, "Implant", "Skill", "Ability", "Research", "Quest", "Psy", "Artifact", "Mechlink", "Deathrest"))
			return;
		bool removes = Has(tn, "Remove", "Cure", "Heal", "Fix", "Reduce", "Clear", "Treat");
		foreach (HediffDef h in HediffsIn(ue))
		{
			if (removes)
			{
				if (h.isBad) { info.cures.Add(h); info.Add(Need.Cure, 1f); }
			}
			else
			{
				info.gives.Add(h);
				ReadHediff(info, h);
			}
		}
		if (removes && Has(tn, "Worst", "Health", "Injur", "Wound"))
			info.Add(Need.Critical, 0.8f);
	}

	private static bool Has(string s, params string[] w)
	{
		foreach (string x in w)
			if (s.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)
				return true;
		return false;
	}

	private static IEnumerable<HediffDef> HediffsIn(object o)
	{
		if (o == null)
			yield break;
		foreach (FieldInfo f in o.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
		{
			object v;
			try { v = f.GetValue(o); } catch { continue; }
			if (v is HediffDef h)
				yield return h;
			else if (v is IEnumerable e && !(v is string))
				foreach (object x in e)
				{
					if (x is HediffDef hx)
						yield return hx;
					else if (x != null && !(x is Def))
						foreach (FieldInfo f2 in x.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public))
							if (f2.GetValue(x) is HediffDef h2)
								yield return h2;
				}
		}
	}

	// what a hediff given by a drug does for the patient
	private static void ReadHediff(DrugInfo info, HediffDef h)
	{
		// 1.3.0: regular use – contraception, fertility, prevention of illness
		if (h.preventsPregnancy || h.defName.IndexOf("Contracept", StringComparison.OrdinalIgnoreCase) >= 0)
			info.Add(Need.Contraception, 1f);
		if (h.stages != null)
			foreach (HediffStage st in h.stages)
			{
				if (st.fertilityFactor < 0.5f) info.Add(Need.Contraception, 1f);
				else if (st.fertilityFactor > 1.15f) info.Add(Need.Fertility, Mathf.Clamp01(st.fertilityFactor - 1f));
				if (st.makeImmuneTo != null && st.makeImmuneTo.Count > 0) info.Add(Need.Prevention, 1f);
				if (StatDefOf.Fertility != null)
				{
					if (st.statFactors != null)
						foreach (StatModifier m in st.statFactors)
							if (m.stat == StatDefOf.Fertility)
							{
								if (m.value < 0.5f) info.Add(Need.Contraception, 1f);
								else if (m.value > 1.15f) info.Add(Need.Fertility, Mathf.Clamp01(m.value - 1f));
							}
					if (st.statOffsets != null)
						foreach (StatModifier m in st.statOffsets)
							if (m.stat == StatDefOf.Fertility && m.value > 0.1f)
								info.Add(Need.Fertility, Mathf.Clamp01(m.value));
				}
			}
		if (h.stages != null)
		{
			foreach (HediffStage st in h.stages)
			{
				if (st.painFactor < 1f) { info.Add(Need.Pain, 1f - st.painFactor); info.combatScore = Mathf.Max(info.combatScore, (1f - st.painFactor) * 0.5f); }
				if (st.painOffset < 0f) info.Add(Need.Pain, -st.painOffset * 2f);
				if (st.totalBleedFactor < 1f) info.Add(Need.Bleeding, 1f - st.totalBleedFactor);
				if (st.capMods != null)
					foreach (PawnCapacityModifier cm in st.capMods)
					{
						if (cm.capacity == null) continue;
						float v = Mathf.Max(cm.offset, cm.postFactor - 1f);
						if (v <= 0f) continue;
						if (cm.capacity == PawnCapacityDefOf.Moving || cm.capacity == PawnCapacityDefOf.Manipulation || cm.capacity == PawnCapacityDefOf.Sight || cm.capacity == PawnCapacityDefOf.Consciousness)
							info.combatScore = Mathf.Max(info.combatScore, v);
						if (cm.capacity == PawnCapacityDefOf.Consciousness) info.Add(Need.Consciousness, v);
						else if (cm.capacity == PawnCapacityDefOf.Breathing) info.Add(Need.Breathing, v);
						else if (cm.capacity == PawnCapacityDefOf.BloodPumping) info.Add(Need.BloodPumping, v);
						else if (cm.capacity == PawnCapacityDefOf.BloodFiltration) info.Add(Need.BloodFiltration, v);
						else if (cm.capacity == PawnCapacityDefOf.Moving) info.Add(Need.Moving, v);
						else if (cm.capacity == PawnCapacityDefOf.Manipulation) info.Add(Need.Manipulation, v);
						else if (cm.capacity == PawnCapacityDefOf.Sight) info.Add(Need.Sight, v);
					}
				ReadStats(info, st.statOffsets, false);
				ReadStats(info, st.statFactors, true);
				if (st.restFallFactor < 1f) info.Add(Need.Rest, (1f - st.restFallFactor) * 0.5f);
			}
		}
		// 1.4.0: a mood thought that comes with the hediff (calming tea and the like)
		foreach (ThoughtDef th in MoodThoughts)
			if (th.hediff == h && th.stages != null && th.stages.Count > 0 && th.stages[0] != null && th.stages[0].baseMoodEffect > 0f)
				info.Add(Need.Mood, Mathf.Clamp01(th.stages[0].baseMoodEffect / 20f));
		if (h.comps != null)
			foreach (HediffCompProperties cp in h.comps)
			{
				string tn = cp.GetType().Name + " " + (cp.compClass?.Name ?? "");
				if (Has(tn, "Immun"))
					info.Add(Need.Immunity, 0.3f);
				if (Has(tn, "Remove", "Cure", "Heal", "Reduce", "Clear", "Treat"))
					foreach (HediffDef t in HediffsIn(cp))
						if (t.isBad && t != h)
						{
							info.cures.Add(t);
							info.Add(Need.Cure, 1f);
						}
			}
	}

	private static List<ThoughtDef> moodThoughts;
	private static List<ThoughtDef> MoodThoughts => moodThoughts ??= DefDatabase<ThoughtDef>.AllDefsListForReading.Where(x => x.hediff != null).ToList();

	private static void ReadStats(DrugInfo info, List<StatModifier> mods, bool factor)
	{
		if (mods == null)
			return;
		foreach (StatModifier m in mods)
		{
			if (m.stat == null) continue;
			float v = factor ? m.value - 1f : m.value;
			if (v <= 0f) continue;
			string n = m.stat.defName;
			if (Has(n, "ImmunityGain")) info.Add(Need.Immunity, v);
			else if (Has(n, "InjuryHealing")) info.Add(Need.Healing, v);
			else if (Has(n, "Blood") && Has(n, "Regen", "Recover", "Gain")) info.Add(Need.BloodLoss, v);
		}
	}

	// ---- patients -----------------------------------------------------------
	// how badly a pawn needs each kind of help, 0 (fine) … 1 (life in danger)
	public static Dictionary<Need, float> Needs(Pawn p, out List<Hediff> curable)
	{
		var s = AutoPharmacyMod.Settings;
		var n = new Dictionary<Need, float>();
		curable = new List<Hediff>();
		HediffSet hs = p.health.hediffSet;
		float pain = hs.PainTotal;
		if (pain >= s.painFrom)
			n[Need.Pain] = Mathf.Clamp01((pain - s.painFrom * 0.5f) / 0.7f);
		float bleed = hs.BleedRateTotal;
		if (bleed > 0.1f)
			n[Need.Bleeding] = Mathf.Clamp01(bleed / 0.6f);
		Hediff bl = hs.GetFirstHediffOfDef(HediffDefOf.BloodLoss);
		if (bl != null && bl.Severity > 0.25f)
			n[Need.BloodLoss] = Mathf.Clamp01(bl.Severity * 1.3f);
		float imm = 0f;
		List<Hediff> all = hs.hediffs;
		for (int i = 0; i < all.Count; i++)
		{
			Hediff h = all[i];
			if (h == null || !h.def.isBad || !h.Visible)
				continue;
			curable.Add(h);
			HediffComp_Immunizable ci = h.TryGetComp<HediffComp_Immunizable>();
			if (ci != null && !ci.FullyImmune)
			{
				float u = 0.25f + 0.4f * h.Severity;
				var pr = ci.Props;
				if (h.def.lethalSeverity > 0f && pr.severityPerDayNotImmune > 0f && pr.immunityPerDaySick > 0f)
				{
					float toDeath = (h.def.lethalSeverity - h.Severity) / pr.severityPerDayNotImmune;
					float toImmune = (1f - ci.Immunity) / pr.immunityPerDaySick;
					if (toImmune > toDeath * 0.85f) u = 1f;        // losing the race
					else if (toImmune > toDeath * 0.6f) u = 0.75f;  // close
				}
				imm = Mathf.Max(imm, u);
			}
		}
		if (imm > 0f)
			n[Need.Immunity] = imm;
		// life in danger: the precious all-healing things are kept for this
		bool critical = imm >= 1f || (bl != null && bl.Severity > 0.6f) || bleed > 0.8f;
		for (int i = 0; i < all.Count && !critical; i++)
			if (all[i].CurStage != null && all[i].CurStage.lifeThreatening && all[i].Visible)
				critical = true;
		if (critical)
			n[Need.Critical] = 1f;
		Cap(p, n, PawnCapacityDefOf.Consciousness, Need.Consciousness);
		Cap(p, n, PawnCapacityDefOf.Breathing, Need.Breathing);
		Cap(p, n, PawnCapacityDefOf.BloodPumping, Need.BloodPumping);
		Cap(p, n, PawnCapacityDefOf.BloodFiltration, Need.BloodFiltration);
		// 1.3.0: long-term troubles (bad back, cataract …) – drugs that help moving, hands or sight
		bool chronic = false;
		foreach (Hediff h in all)
			if (h != null && h.Visible && h.def.isBad && (h.def.chronic || h.IsPermanent())) { chronic = true; break; }
		if (s.useChronic && chronic)
		{
			Cap(p, n, PawnCapacityDefOf.Moving, Need.Moving);
			Cap(p, n, PawnCapacityDefOf.Manipulation, Need.Manipulation);
			Cap(p, n, PawnCapacityDefOf.Sight, Need.Sight);
		}
		// 1.3.0: regular use
		if (s.usePrevention && (p.IsColonist || p.IsPrisonerOfColony))
			n[Need.Prevention] = 0.3f;
		bool adult = p.ageTracker != null && p.ageTracker.AgeBiologicalYears >= 18;
		bool pregnant = false;
		foreach (Hediff h in all)
			if (h != null && h.def.pregnant) { pregnant = true; break; }
		int plan = Comp.Plan(p);
		if (adult && !pregnant && plan == 1)
			n[Need.Contraception] = 0.6f;
		if (adult && !pregnant && plan == 2)
			n[Need.Fertility] = 0.4f;
		// 1.4.0: fresh wounds (not scars) heal faster with a healing drug
		float wounds = 0f;
		foreach (Hediff h in all)
			if (h is Hediff_Injury inj && !inj.IsPermanent()) wounds += inj.Severity;
		if (wounds > 10f)
			n[Need.Healing] = Mathf.Clamp01(wounds / 40f);
		if (s.useRest && p.needs?.rest != null && p.needs.rest.CurLevel < 0.08f && !p.InBed())
			n[Need.Rest] = 0.4f;
		if (s.useMood && p.needs?.mood != null && p.mindState?.mentalBreaker != null)
		{
			float mood = p.needs.mood.CurLevel;
			MentalBreaker mb = p.mindState.mentalBreaker;
			if (mood < mb.BreakThresholdExtreme) n[Need.Mood] = 0.6f;
			else if (mood < mb.BreakThresholdMajor) n[Need.Mood] = 0.4f;
			// 1.4.0: before the breakdown – a mild drug already near the minor break line
			else if (s.moodEarly && mood < mb.BreakThresholdMinor + 0.03f) n[Need.Mood] = 0.25f;
		}
		// 1.4.0: an addiction is kept fed before withdrawal (luciferium above all – a missed dose kills)
		if (s.maintainAddictions)
		{
			float a = 0f;
			foreach (float u in Addictions(p).Values) a = Mathf.Max(a, u);
			if (a > 0f) n[Need.Addiction] = a;
		}
		// 1.4.0: sanguophages low on hemogen
		if (ModsConfig.BiotechActive && p.genes != null)
		{
			Gene_Hemogen g = p.genes.GetFirstGeneOfType<Gene_Hemogen>();
			if (g != null && g.Active && g.Value < g.Max * 0.25f)
				n[Need.Hemogen] = g.Value < g.Max * 0.1f ? 0.8f : 0.5f;
		}
		return n;
	}

	// chemical → how urgent a dose is (need running low; luciferium earlier and most urgent)
	public static Dictionary<ChemicalDef, float> Addictions(Pawn p)
	{
		var d = new Dictionary<ChemicalDef, float>();
		if (p.health?.hediffSet == null)
			return d;
		foreach (Hediff h in p.health.hediffSet.hediffs)
		{
			if (!(h is Hediff_Addiction a) || a.Chemical == null)
				continue;
			Need_Chemical need = a.Need;
			if (need == null)
				continue;
			bool luci = a.Chemical.defName == "Luciferium";
			float lvl = need.CurLevel;
			if (lvl < (luci ? 0.45f : 0.3f))
				d[a.Chemical] = luci ? 1f : (lvl < 0.1f ? 0.6f : 0.45f);
		}
		return d;
	}

	private static void Cap(Pawn p, Dictionary<Need, float> n, PawnCapacityDef c, Need need)
	{
		if (c == null || !p.health.capacities.CapableOf(c) && c != PawnCapacityDefOf.Consciousness)
			return;
		float lvl = p.health.capacities.GetLevel(c);
		if (lvl < 0.6f)
			n[need] = Mathf.Clamp01((0.6f - lvl) * 2f);
	}

	// ---- choosing ------------------------------------------------------------
	public sealed class Choice
	{
		public DrugInfo drug;
		public Thing thing;
		public string reason;
		public float score;
		public bool minor;   // 1.3.3: only for mood, rest or planning – not urgent
	}

	public static Choice Choose(Pawn doctor, Pawn patient)
	{
		var s = AutoPharmacyMod.Settings;
		Dictionary<Need, float> needs = Needs(patient, out List<Hediff> curable);
		if (needs.Count == 0 && curable.Count == 0)
			return null;
		float worst = needs.Count > 0 ? needs.Values.Max() : 0f;
		float overdoseNow = patient.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.DrugOverdose)?.Severity ?? 0f;
		bool overdoseRisk = overdoseNow > 0.05f;
		bool teetotal = patient.IsTeetotaler();
		// 1.3.1: children and babies – babies get no drugs at all, children and teenagers only
		// medicine that is not addictive, cannot overdose and is not for mood, rest or fighting
		int age = patient.ageTracker?.AgeBiologicalYears ?? 99;
		bool baby = patient.DevelopmentalStage == DevelopmentalStage.Baby || patient.DevelopmentalStage == DevelopmentalStage.Newborn || age < 3;
		bool minor = baby || patient.DevelopmentalStage == DevelopmentalStage.Child || age < 18;
		// 1.3.1: another drug that can overdose is still working – never stack two of them
		bool overdoserRunning = Drugs.Any(o => o.overdoses && Running(patient, o));
		Dictionary<ChemicalDef, float> addicted = s.maintainAddictions ? Addictions(patient) : new Dictionary<ChemicalDef, float>();
		Choice best = null;
		DrugInfo missing = null;
		float missingScore = 0f;
		foreach (DrugInfo d in Drugs)
		{
			if (s.blocked.Contains(d.def.defName))
				continue;
			// 1.4.0: the dose an addiction needs
			bool forAddiction = d.chemical != null && addicted.ContainsKey(d.chemical);
			if (d.isDrug && teetotal && !d.Regimen && !forAddiction)
				continue;
			// 1.4.0: the colonist's ideology forbids drugs (an addiction still gets its dose, like in vanilla)
			if (d.isDrug && !d.Regimen && !forAddiction && !IdeoAllows(patient, d))
				continue;
			if (d.overdoses && (overdoseRisk || overdoserRunning || overdoseNow + d.overdoseMax >= 0.45f))
				continue;
			if (d.largeOverdose > 0f && (minor || worst < 0.9f))
				continue;
			if (baby && (d.isDrug || d.overdoses || d.addictiveness > 0f || d.largeOverdose > 0f))
				continue;
			if (minor && (d.addictiveness > 0f || d.overdoses || d.Combat || d.def.IsNonMedicalDrug
			              || d.effects.Keys.Any(k => k == Need.Mood || k == Need.Rest || k == Need.Contraception || k == Need.Fertility)))
				continue;
			if (Running(patient, d) && !forAddiction)
				continue;
			if (!Comp.CanGive(patient, d.def))
				continue;
			float benefit = 0f;
			string why = null;
			float whyV = 0f;
			if (forAddiction)
			{
				benefit += addicted[d.chemical] * 1.2f;
				whyV = benefit;
				why = "AP_Need_Addiction".Translate();
			}
			foreach (var kv in d.effects)
			{
				if (kv.Key == Need.Cure)
					continue;
				if (!needs.TryGetValue(kv.Key, out float u) || u <= 0f)
					continue;
				float b = u * Mathf.Clamp01(kv.Value * 2f);
				benefit += b;
				if (b > whyV) { whyV = b; why = ("AP_Need_" + kv.Key).Translate(); }
			}
			float local = worst;
			if (d.cures.Count > 0)
				foreach (Hediff h in curable)
					if (d.cures.Contains(h.def))
					{
						float b = 0.5f + 0.5f * Mathf.Clamp01(h.Severity);
						if (h.CurStage != null && h.CurStage.lifeThreatening) b = 1f;
						benefit += b;
						local = Mathf.Max(local, b);
						if (b > whyV) { whyV = b; why = h.LabelCap; }
					}
			if (benefit < 0.15f)
				continue;
			// addictive drugs by how serious it is
			if (d.addictiveness > 0f && !forAddiction)
			{
				// 1.3.0: a mild one (beer, …) for mood or pain is fine while it cannot start an addiction
				bool mildSafe = d.addictiveness <= 0.05f && CombatDrugs.Safe(patient, d);
				if (s.addictive == 0 && !mildSafe) continue;
				if (s.addictive == 1 && local < 0.9f && !mildSafe) continue;
				benefit -= d.addictiveness * (1.2f - local) * 1.5f;
			}
			// for small troubles the mild, cheap drug; for big ones the strongest
			float strength = d.effects.Values.DefaultIfEmpty(0f).Max();
			benefit -= Mathf.Max(0f, strength - local) * 0.2f;
			benefit -= d.def.BaseMarketValue * 0.0004f;
			if (benefit < 0.1f || (best != null && benefit <= best.score))
				continue;
			Thing t = Find(doctor, patient, d.def);
			if (t == null)
			{
				// 1.4.0: remembered for the "missing medicine" message
				if (benefit > missingScore && (forAddiction || local >= 0.5f))
				{
					missingScore = benefit;
					missing = d;
				}
				continue;
			}
			if (d.usable)
			{
				CompUsable cu = t.TryGetComp<CompUsable>();
				if (cu == null || !cu.CanBeUsedBy(patient, false, true).Accepted)
					continue;
			}
			best = new Choice { drug = d, thing = t, reason = why ?? "", score = benefit, minor = local < 0.7f && !forAddiction };
		}
		if (missing != null && (best == null || missingScore > best.score * 1.5f))
			Comp.NoteMissing(missing.def, patient);
		return best;
	}

	public static bool IdeoAllows(Pawn p, DrugInfo d)
	{
		if (!ModsConfig.IdeologyActive || p.Ideo == null)
			return true;
		try
		{
			if (!IdeoUtility.DoerWillingToDo(HistoryEventDefOf.IngestedDrug, p))
				return false;
			if (d.def.IsNonMedicalDrug && !IdeoUtility.DoerWillingToDo(HistoryEventDefOf.IngestedRecreationalDrug, p))
				return false;
			if (d.def.ingestible != null && d.def.ingestible.drugCategory == DrugCategory.Hard && !IdeoUtility.DoerWillingToDo(HistoryEventDefOf.IngestedHardDrug, p))
				return false;
		}
		catch
		{
		}
		return true;
	}

	// the drug's effect is still running
	private static bool Running(Pawn p, DrugInfo d)
	{
		foreach (HediffDef h in d.gives)
		{
			Hediff hd = p.health.hediffSet.GetFirstHediffOfDef(h);
			if (hd == null)
				continue;
			// 1.3.0: a regular dose is given a bit before the last one runs out, so there is no gap
			if (d.Regimen)
			{
				HediffComp_Disappears dis = hd.TryGetComp<HediffComp_Disappears>();
				if (dis != null && dis.ticksToDisappear < 2500)
					continue;
			}
			return true;
		}
		return false;
	}

	private static Thing Find(Pawn doctor, Pawn patient, ThingDef def)
	{
		// 1.4.0: what the doctor carries comes first – no walk to the stockpile
		if (doctor.inventory != null)
			foreach (Thing t in doctor.inventory.innerContainer)
				if (t.def == def)
					return t;
		List<Thing> list = doctor.Map.listerThings.ThingsOfDef(def);
		Thing best = null;
		float bd = float.MaxValue;
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (t.IsForbidden(doctor) || !t.Spawned)
				continue;
			float d = (t.Position - doctor.Position).LengthHorizontalSquared;
			if (d >= bd)
				continue;
			if (!doctor.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some, 1, 1))
				continue;
			bd = d;
			best = t;
		}
		// the doctor's own inventory
		if (best == null && doctor.inventory != null)
			foreach (Thing t in doctor.inventory.innerContainer)
				if (t.def == def)
					return t;
		return best;
	}
}

// remembers when each pawn last got each drug (drugs without a lasting effect)
public class Comp : GameComponent
{
	private Dictionary<string, int> last = new Dictionary<string, int>();
	private List<string> k; private List<int> v;
	// 1.2.0: colonists who never take combat drugs (gizmo)
	public HashSet<int> combatOff = new HashSet<int>();
	// 1.3.0: family plan per colonist: 0 not handled, 1 no children (contraception), 2 wants children (fertility)
	public Dictionary<int, int> plans = new Dictionary<int, int>();
	private List<int> pk; private List<int> pv;

	public static int Plan(Pawn p)
	{
		Comp c = Get();
		if (c == null || p == null)
			return 0;
		return c.plans.TryGetValue(p.thingIDNumber, out int v) ? v : 0;
	}

	public static void SetPlan(Pawn p, int v)
	{
		Comp c = Get();
		if (c == null || p == null) return;
		if (v == 0) c.plans.Remove(p.thingIDNumber);
		else c.plans[p.thingIDNumber] = v;
	}

	public Comp(Game game) { }

	// 1.4.0: what was given (for the doctor's own supply and the history)
	public Dictionary<string, int> givenCount = new Dictionary<string, int>();
	private List<string> gk; private List<int> gv;
	public List<string> history = new List<string>();
	private readonly Dictionary<ThingDef, int> missing = new Dictionary<ThingDef, int>();
	private readonly Dictionary<ThingDef, string> missingFor = new Dictionary<ThingDef, string>();
	private readonly Dictionary<ThingDef, int> warned = new Dictionary<ThingDef, int>();

	public static void NoteMissing(ThingDef d, Pawn patient)
	{
		Comp c = Get();
		if (c == null || d == null) return;
		c.missing[d] = Find.TickManager.TicksGame;
		c.missingFor[d] = patient?.LabelShort ?? "";
	}

	public static void Remember(Pawn doctor, Pawn patient, ThingDef d, string why)
	{
		Comp c = Get();
		if (c == null) return;
		c.givenCount[d.defName] = c.givenCount.TryGetValue(d.defName, out int n) ? n + 1 : 1;
		string when = patient?.MapHeld != null ? GenDate.DateFullStringAt(Find.TickManager.TicksAbs, Find.WorldGrid.LongLatOf(patient.MapHeld.Tile)) : "";
		c.history.Add(when + ": " + (doctor?.LabelShort ?? "?") + " → " + (patient?.LabelShort ?? "?") + ": " + d.LabelCap + (string.IsNullOrEmpty(why) ? "" : " (" + why + ")"));
		while (c.history.Count > 60) c.history.RemoveAt(0);
	}

	// the drugs given most often (the doctor carries a few of them)
	public static List<ThingDef> MostGiven(int n)
	{
		Comp c = Get();
		if (c == null) return new List<ThingDef>();
		return c.givenCount.OrderByDescending(kv => kv.Value).Select(kv => DefDatabase<ThingDef>.GetNamedSilentFail(kv.Key)).Where(x => x != null).Take(n).ToList();
	}

	private void WarnMissing()
	{
		int now = Find.TickManager.TicksGame;
		foreach (var kv in missing.ToList())
		{
			if (now - kv.Value > 2500) { missing.Remove(kv.Key); continue; }
			if (warned.TryGetValue(kv.Key, out int w) && now - w < 60000) continue;
			warned[kv.Key] = now;
			missingFor.TryGetValue(kv.Key, out string who);
			Messages.Message("AP_Missing".Translate(kv.Key.LabelCap, who ?? ""), MessageTypeDefOf.CautionInput, false);
		}
	}

	public static Comp Get() => Current.Game?.GetComponent<Comp>();

	private static string Key(Pawn p, ThingDef d) => p.thingIDNumber + "|" + d.defName;

	public static bool CanGive(Pawn p, ThingDef d)
	{
		Comp c = Get();
		if (c == null)
			return true;
		int now = Find.TickManager.TicksGame;
		if (c.last.TryGetValue("any|" + p.thingIDNumber, out int any) && now - any < 1250)
			return false;   // one dose at a time – let it work
		return !c.last.TryGetValue(Key(p, d), out int t) || now - t > AutoPharmacyMod.Settings.minHoursBetween * 2500;
	}

	public static void Given(Pawn p, ThingDef d)
	{
		Comp c = Get();
		if (c == null)
			return;
		int now = Find.TickManager.TicksGame;
		c.last[Key(p, d)] = now;
		c.last["any|" + p.thingIDNumber] = now;
	}

	// medical care level by how serious it is (bandages, herbal, industrial, glitterworld)
	// – only when Pharmacist is not installed, which does the same
	public override void GameComponentTick()
	{
		var s = AutoPharmacyMod.Settings;
		if (s.enabled && s.warnMissing && Find.TickManager.TicksGame % 2500 == 1250)
		{
			try { WarnMissing(); }
			catch (Exception ex) { Log.ErrorOnce("[Auto Pharmacy] missing medicine: " + ex, 0x41506e); }
		}
		if (s.enabled && s.combatDrugs && Find.TickManager.TicksGame % 120 == 37)
		{
			foreach (Map m in Find.Maps)
			{
				try { CombatDrugs.Tick(m); }
				catch (Exception ex) { Log.ErrorOnce("[Auto Pharmacy] combat drugs: " + ex, 0x41506d); }
			}
		}
		if (!s.enabled || !s.autoCare || Find.TickManager.TicksGame % 600 != 0 || AutoPharmacyMod.PharmacistActive)
			return;
		foreach (Map m in Find.Maps)
		{
			// 1.4.3: colonists and slaves (as before), plus prisoners, guests and
			// animals – each group with its own highest medicine level (or off)
			foreach (Pawn p in m.mapPawns.FreeColonistsSpawned.ToList())
				ApplyCare(p, p.IsSlave ? s.careSlaves : s.careColonists);
			if (s.carePrisoners >= 0)
				foreach (Pawn p in m.mapPawns.PrisonersOfColonySpawned.ToList())
					ApplyCare(p, s.carePrisoners);
			if (s.careAnimals >= 0)
				foreach (Pawn p in m.mapPawns.SpawnedColonyAnimals.ToList())
					ApplyCare(p, s.careAnimals);
			if (s.careGuests >= 0)
				foreach (Pawn p in m.mapPawns.AllPawnsSpawned.ToList())
					if (IsGuest(p))
						ApplyCare(p, s.careGuests);
		}
	}

	// 1.4.3: a guest – a humanlike staying with the colony (quest lodger, guest)
	// who is neither ours nor a prisoner
	private static bool IsGuest(Pawn p)
	{
		return p != null && p.RaceProps != null && p.RaceProps.Humanlike && p.Faction != Faction.OfPlayer
		       && p.HostFaction == Faction.OfPlayer && !p.IsPrisoner && !p.IsSlave;
	}

	// 1.4.3: cap < 0 = this group is not managed. "No care" chosen by hand is kept.
	private static void ApplyCare(Pawn p, int cap)
	{
		if (cap < 0 || p?.playerSettings == null || p.health?.hediffSet == null || p.Dead)
			return;
		try
		{
			MedicalCareCategory want = CareFor(p);
			if ((int)want > cap)
				want = (MedicalCareCategory)Mathf.Clamp(cap, 0, (int)MedicalCareCategory.Best);
			if (p.playerSettings.medCare != want && p.playerSettings.medCare != MedicalCareCategory.NoCare)
				p.playerSettings.medCare = want;
		}
		catch (Exception ex) { Log.WarningOnce("[Auto Pharmacy] medical care for " + p.LabelShort + ": " + ex.Message, 0x41506f); }
	}

	// 1.4.2: Biotech / Anomaly rot diseases, looked up by name so a missing DLC is no problem
	private static HediffDef lungRot, bloodRot;
	private static bool rotLooked;
	private static bool IsRot(HediffDef d)
	{
		if (!rotLooked)
		{
			rotLooked = true;
			lungRot = DefDatabase<HediffDef>.GetNamedSilentFail("LungRot");
			bloodRot = DefDatabase<HediffDef>.GetNamedSilentFail("BloodRot");
		}
		return d != null && (d == lungRot || d == bloodRot);
	}

	private static bool LosingRace(Hediff h)
	{
		HediffComp_Immunizable ci = h.TryGetComp<HediffComp_Immunizable>();
		if (ci == null || ci.FullyImmune)
			return false;
		var pr = ci.Props;
		if (h.def.lethalSeverity <= 0f || pr.severityPerDayNotImmune <= 0f || pr.immunityPerDaySick <= 0f)
			return false;
		float toDeath = (h.def.lethalSeverity - h.Severity) / pr.severityPerDayNotImmune;
		float toImmune = (1f - ci.Immunity) / pr.immunityPerDaySick;
		return toImmune > toDeath * 0.6f;
	}

	public static MedicalCareCategory CareFor(Pawn p)
	{
		HediffSet hs = p.health.hediffSet;
		bool life = hs.BleedRateTotal > 0.4f;
		float injuries = 0f;
		bool sick = false;
		foreach (Hediff h in hs.hediffs)
		{
			if (h == null || !h.def.isBad || !h.Visible)
				continue;
			if (h.CurStage != null && h.CurStage.lifeThreatening)
				life = true;
			if (h is Hediff_Injury && !h.IsPermanent())
				injuries += h.Severity;
			if (h.TryGetComp<HediffComp_Immunizable>() != null || h.def.tendable && !(h is Hediff_Injury))
				sick = true;
			// 1.4.2: lung rot / blood rot are deadly races against immunity (tend quality
			// decides them) – industrial medicine from the start, the best once they grow
			// or once immunity is losing the race.
			if (IsRot(h.def))
			{
				sick = true;
				if (h.Severity >= 0.35f || LosingRace(h))
					life = true;
			}
		}
		if (life || injuries > 25f)
			return MedicalCareCategory.Best;
		if (sick || injuries > 8f)
			return MedicalCareCategory.NormalOrWorse;
		return MedicalCareCategory.HerbalOrWorse;
	}

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			int now = Find.TickManager.TicksGame;
			last = last.Where(kv => now - kv.Value < 60000 * 3).ToDictionary(kv => kv.Key, kv => kv.Value);
		}
		Scribe_Collections.Look(ref last, "apLast", LookMode.Value, LookMode.Value, ref k, ref v);
		if (last == null)
			last = new Dictionary<string, int>();
		Scribe_Collections.Look(ref combatOff, "apCombatOff", LookMode.Value);
		if (combatOff == null)
			combatOff = new HashSet<int>();
		Scribe_Collections.Look(ref plans, "apPlans", LookMode.Value, LookMode.Value, ref pk, ref pv);
		if (plans == null)
			plans = new Dictionary<int, int>();
		Scribe_Collections.Look(ref givenCount, "apGiven", LookMode.Value, LookMode.Value, ref gk, ref gv);
		if (givenCount == null)
			givenCount = new Dictionary<string, int>();
		Scribe_Collections.Look(ref history, "apHistory", LookMode.Value);
		if (history == null)
			history = new List<string>();
	}
}

public class WorkGiver_GiveDrug : WorkGiver_Scanner
{
	public override PathEndMode PathEndMode => PathEndMode.Touch;

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		var s = AutoPharmacyMod.Settings;
		// 1.3.4: a copy – the game's own list changes while the work search runs ("Collection was modified")
		var list = new List<Thing>(pawn.Map.mapPawns.FreeColonistsSpawned);
		if (s.prisoners)
			list.AddRange(pawn.Map.mapPawns.PrisonersOfColonySpawned);
		return list;
	}

	public override bool ShouldSkip(Pawn pawn, bool forced = false) => !AutoPharmacyMod.Settings.enabled;

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		Pawn patient = t as Pawn;
		if (patient == null || !Ok(patient))
			return false;
		// 1.4.4: a colonist lying hurt outside a bed is rescued and tended first – drugs only after that
		// (before, a doctor sometimes ran to the downed colonist with a drug instead of carrying him to bed)
		if (!forced && patient != pawn && ((patient.Downed && !patient.InBed()) || HealthAIUtility.ShouldBeTendedNowByPlayerUrgent(patient)))
			return false;
		if (patient != pawn && !pawn.CanReserveAndReach(patient, PathEndMode.Touch, Danger.Some, 1, -1, null, forced))
			return false;
		Pharmacy.Choice c = Pharmacy.Choose(pawn, patient);
		if (c == null)
			return false;
		// 1.3.3: something that is not urgent (mood, rest, planning) – only one colonist on the map
		// runs around with it at a time, the others keep working
		if (c.minor && patient != pawn && !forced && OthersGiving(pawn))
			return false;
		return true;
	}

	private static bool OthersGiving(Pawn pawn)
	{
		foreach (Pawn o in pawn.Map.mapPawns.FreeColonistsSpawned.ToList())
			if (o != pawn && o.CurJobDef == AP_DefOf.AP_GiveDrug)
				return true;
		return false;
	}

	public static bool Ok(Pawn patient)
	{
		if (patient.Dead || !patient.Spawned || !patient.RaceProps.Humanlike || patient.health?.hediffSet == null)
			return false;
		if (!patient.Downed && (patient.Drafted || patient.InMentalState))
			return false;
		return true;
	}

	public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		Pawn patient = t as Pawn;
		if (patient == null)
			return null;
		Pharmacy.Choice c = Pharmacy.Choose(pawn, patient);
		if (c == null)
			return null;
		Job job = JobMaker.MakeJob(AP_DefOf.AP_GiveDrug, patient, c.thing);
		job.count = 1;
		JobDriver_GiveDrug.reasons[pawn.thingIDNumber] = c.reason;
		return job;
	}
}

[DefOf]
public static class AP_DefOf
{
	public static JobDef AP_GiveDrug;
	static AP_DefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(AP_DefOf)); }
}

public class JobDriver_GiveDrug : JobDriver
{
	public static readonly Dictionary<int, string> reasons = new Dictionary<int, string>();

	private Pawn Patient => job.GetTarget(TargetIndex.A).Pawn;
	private Thing Drug => job.GetTarget(TargetIndex.B).Thing;

	public override bool TryMakePreToilReservations(bool errorOnFailed)
	{
		if (Patient != pawn && !pawn.Reserve(Patient, job, 1, -1, null, errorOnFailed))
			return false;
		if (Drug.Spawned && !pawn.Reserve(Drug, job, 1, 1, null, errorOnFailed))
			return false;
		return true;
	}

	protected override IEnumerable<Toil> MakeNewToils()
	{
		this.FailOnDespawnedOrNull(TargetIndex.A);
		this.FailOn(() => Drug == null || Drug.Destroyed);
		if (Drug.Spawned)
		{
			yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
			yield return Toils_Haul.StartCarryThing(TargetIndex.B, false, false, false, true);
		}
		else
		{
			// from the doctor's inventory
			Toil take = ToilMaker.MakeToil("AP_TakeFromInventory");
			take.initAction = delegate
			{
				Thing d = Drug;
				if (d.stackCount > 1)
					d = d.SplitOff(1);
				else
					pawn.inventory.innerContainer.Remove(d);
				pawn.carryTracker.TryStartCarry(d);
				job.SetTarget(TargetIndex.B, d);
			};
			yield return take;
		}
		if (Patient != pawn)
			yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
		Toil give = Toils_General.Wait(90);
		give.WithProgressBarToilDelay(TargetIndex.A);
		give.AddFinishAction(delegate
		{
			Thing d = pawn.carryTracker?.CarriedThing;
			Pawn p = Patient;
			if (d == null || p == null || d.Destroyed || p.Dead || ticksLeftThisToil > 0)
				return;
			if (d.stackCount > 1)
				d = d.SplitOff(1);
			else
				pawn.carryTracker.innerContainer.Remove(d);
			string label = d.LabelNoCount;
			CompUsable cu = d.TryGetComp<CompUsable>();
			if (cu != null && d.def.ingestible == null)
			{
				cu.UsedBy(p);   // single-use things destroy themselves, kits stay
			}
			else
				d.Ingested(p, 0f);
			Comp.Given(p, d.def);
			reasons.TryGetValue(pawn.thingIDNumber, out string why);
			Comp.Remember(pawn, p, d.def, why);
			if (AutoPharmacyMod.Settings.notify)
				Messages.Message("AP_Given".Translate(pawn.LabelShort, label, p.LabelShort, why ?? ""), p, MessageTypeDefOf.SilentInput, false);
		});
		yield return give;
	}
}

// ---- 1.2.0: combat drugs for drafted fighters --------------------------------
// A drafted colonist with enemies close takes ONE combat drug (Go-juice, Wake-up,
// Yayo, modded ones …) – from his inventory, or from a stack a few steps away
// when no enemy is near. Never with a chance of addiction (tolerance below the
// addiction level), never twice in the set hours, never while the effect runs,
// never for teetotalers, children or against the colonist's ideology.
public static class CombatDrugs
{
	public static bool AllowedFor(Pawn p)
	{
		Comp c = Comp.Get();
		return c == null || !c.combatOff.Contains(p.thingIDNumber);
	}

	public static void Tick(Map map)
	{
		var s = AutoPharmacyMod.Settings;
		if (!s.combatDrugs || !GenHostility.AnyHostileActiveThreatToPlayer(map))
			return;
		List<Pawn> hostiles = null;
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
		{
			if (!p.Drafted || p.Downed || p.InMentalState || p.health?.hediffSet == null || p.inventory == null)
				continue;
			if (p.DevelopmentalStage != DevelopmentalStage.Adult || !AllowedFor(p) || p.IsTeetotaler())
				continue;
			if (p.CurJobDef == JobDefOf.Ingest)
				continue;
			int now = Find.TickManager.TicksGame;
			if (now - p.mindState.lastTakeCombatEnhancingDrugTick < s.combatHours * 2500)
				continue;
			if (hostiles == null)
				hostiles = map.mapPawns.AllPawnsSpawned.Where(h => !h.Dead && !h.Downed && h.HostileTo(Faction.OfPlayer)).ToList();
			float near = float.MaxValue;
			foreach (Pawn h in hostiles)
				near = Mathf.Min(near, (h.Position - p.Position).LengthHorizontalSquared);
			if (near > 35f * 35f)
				continue;
			Thing t = Pick(p, near);
			if (t == null)
				continue;
			Job job = JobMaker.MakeJob(JobDefOf.Ingest, t);
			job.count = 1;
			if (p.jobs.TryTakeOrderedJob(job, JobTag.Misc))
			{
				p.mindState.lastTakeCombatEnhancingDrugTick = now;
				if (s.notify)
					Messages.Message("AP_CombatTaken".Translate(p.LabelShort, t.def.label), p, MessageTypeDefOf.SilentInput, false);
			}
		}
	}

	private static Thing Pick(Pawn p, float nearSq)
	{
		var s = AutoPharmacyMod.Settings;
		HediffSet hs = p.health.hediffSet;
		if ((hs.GetFirstHediffOfDef(HediffDefOf.DrugOverdose)?.Severity ?? 0f) > 0.01f)
			return null;
		float pain = hs.PainTotal;
		Thing best = null;
		float bestScore = 0f;
		foreach (DrugInfo d in Pharmacy.Drugs)
		{
			if (!d.Combat || s.blocked.Contains(d.def.defName) || d.largeOverdose > 0.02f)
				continue;
			if (d.gives.Any(h => hs.HasHediff(h)))
				continue;   // still working
			if (!Safe(p, d) || !Willing(p, d))
				continue;
			float score = Mathf.Max(d.combatScore, d.combatFlag ? 0.3f : 0f);
			if (d.effects.TryGetValue(Need.Pain, out float pr) && pain >= s.painFrom)
				score += pr * pain;
			score -= d.addictiveness;
			if (score <= bestScore)
				continue;
			Thing t = Source(p, d.def, nearSq);
			if (t == null)
				continue;
			best = t;
			bestScore = score;
		}
		return best;
	}

	// no chance of a new addiction: tolerance below the level where addiction can start
	internal static bool Safe(Pawn p, DrugInfo d)
	{
		if (d.addictiveness <= 0f || d.chemical == null)
			return true;
		if (AddictionUtility.FindAddictionHediff(p, d.chemical) != null)
			return true;   // already addicted – a dose only feeds it, no new addiction
		if (d.minToleranceToAddict <= 0f)
			return false;
		float tol = AddictionUtility.FindToleranceHediff(p, d.chemical)?.Severity ?? 0f;
		return tol < d.minToleranceToAddict;
	}

	private static bool Willing(Pawn p, DrugInfo d)
	{
		if (!ModsConfig.IdeologyActive || p.Ideo == null)
			return true;
		try
		{
			if (!new HistoryEvent(HistoryEventDefOf.IngestedDrug, p.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
				return false;
			if (d.def.ingestible != null && d.def.ingestible.drugCategory == DrugCategory.Hard
			    && !new HistoryEvent(HistoryEventDefOf.IngestedHardDrug, p.Named(HistoryEventArgsNames.Doer)).DoerWillingToDo())
				return false;
		}
		catch
		{
		}
		return true;
	}

	// own inventory first; a stack a few steps away only when no enemy is close
	private static Thing Source(Pawn p, ThingDef def, float nearSq)
	{
		foreach (Thing t in p.inventory.innerContainer)
			if (t.def == def)
				return t;
		if (nearSq < 18f * 18f)
			return null;
		List<Thing> list = p.Map.listerThings.ThingsOfDef(def);
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (!t.Spawned || t.IsForbidden(p) || (t.Position - p.Position).LengthHorizontalSquared > 12f * 12f)
				continue;
			if (p.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some, 1, 1))
				return t;
		}
		return null;
	}
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class CombatDrugGizmo
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		if (__instance == null || !__instance.IsColonistPlayerControlled)
			yield break;
		Pawn p = __instance;
		// 1.3.0: family plan – only when a contraceptive or fertility drug exists
		if (!p.Drafted && AutoPharmacyMod.Settings.enabled && Pharmacy.HasFamilyDrugs && p.ageTracker != null && p.ageTracker.AgeBiologicalYears >= 18 && Find.Selector.NumSelected == 1)
		{
			int plan = Comp.Plan(p);
			yield return new Command_Action
			{
				defaultLabel = ("AP_Plan" + plan).Translate(),
				defaultDesc = "AP_Plan_Desc".Translate(),
				icon = DefDatabase<ThingDef>.GetNamedSilentFail("MedicineIndustrial")?.uiIcon ?? BaseContent.BadTex,
				action = () => Comp.SetPlan(p, (Comp.Plan(p) + 1) % 3)
			};
		}
		if (!p.Drafted || !AutoPharmacyMod.Settings.combatDrugs)
			yield break;
		yield return new Command_Toggle
		{
			defaultLabel = "AP_CombatGizmo".Translate(),
			defaultDesc = "AP_CombatGizmo_Desc".Translate(),
			icon = DefDatabase<ThingDef>.GetNamedSilentFail("GoJuice")?.uiIcon ?? TexCommand.Draft,
			isActive = () => CombatDrugs.AllowedFor(p),
			toggleAction = () =>
			{
				Comp c = Comp.Get();
				if (c == null) return;
				if (!c.combatOff.Remove(p.thingIDNumber))
					c.combatOff.Add(p.thingIDNumber);
			}
		};
	}
}

// ---- settings ----------------------------------------------------------------
public class AutoPharmacySettings : ModSettings
{
	public bool enabled = true;
	public bool notify = true;
	public bool prisoners;
	public float painFrom = 0.2f;
	public int addictive = 1;   // 0 never, 1 only when life is in danger, 2 by how serious it is
	public bool useMood = true;
	public bool useRest;
	public bool autoCare = true;
	public int minHoursBetween = 12;
	// 1.2.0: combat drugs for drafted fighters
	public bool combatDrugs = true;
	// 1.3.0
	public bool usePrevention = true;
	public bool useChronic = true;
	public int combatHours = 24;
	// 1.4.0
	public bool maintainAddictions = true;
	public bool moodEarly = true;
	public bool doctorCarry = true;
	public bool warnMissing = true;
	// 1.4.3: highest medical care per group for the automatic care level
	// (-1 = not managed; 0..4 = MedicalCareCategory). Defaults = the old behaviour.
	public int careColonists = (int)MedicalCareCategory.Best;
	public int careSlaves = (int)MedicalCareCategory.Best;
	public int carePrisoners = -1;
	public int careGuests = -1;
	public int careAnimals = -1;
	// 1.2.0: recommended settings switch (missing key = off, so old settings stay)
	public bool recommended = true;
	public HashSet<string> blocked = new HashSet<string>();

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref notify, "notify", true);
		Scribe_Values.Look(ref prisoners, "prisoners", false);
		Scribe_Values.Look(ref painFrom, "painFrom", 0.2f);
		Scribe_Values.Look(ref addictive, "addictive", 1);
		Scribe_Values.Look(ref useMood, "useMood", true);
		Scribe_Values.Look(ref useRest, "useRest", false);
		Scribe_Values.Look(ref autoCare, "autoCare", true);
		Scribe_Values.Look(ref minHoursBetween, "minHoursBetween", 12);
		Scribe_Values.Look(ref combatDrugs, "combatDrugs", true);
		Scribe_Values.Look(ref usePrevention, "usePrevention", true);
		Scribe_Values.Look(ref useChronic, "useChronic", true);
		Scribe_Values.Look(ref combatHours, "combatHours", 24);
		Scribe_Values.Look(ref maintainAddictions, "maintainAddictions", true);
		Scribe_Values.Look(ref moodEarly, "moodEarly", true);
		Scribe_Values.Look(ref doctorCarry, "doctorCarry", true);
		Scribe_Values.Look(ref warnMissing, "warnMissing", true);
		Scribe_Values.Look(ref careColonists, "careColonists", (int)MedicalCareCategory.Best);
		Scribe_Values.Look(ref careSlaves, "careSlaves", (int)MedicalCareCategory.Best);
		Scribe_Values.Look(ref carePrisoners, "carePrisoners", -1);
		Scribe_Values.Look(ref careGuests, "careGuests", -1);
		Scribe_Values.Look(ref careAnimals, "careAnimals", -1);
		Scribe_Values.Look(ref recommended, "recommended", false);
		Scribe_Collections.Look(ref blocked, "blocked", LookMode.Value);
		if (blocked == null)
			blocked = new HashSet<string>();
	}
}

public class AutoPharmacyMod : Mod
{
	public static AutoPharmacySettings Settings;
	private Vector2 scroll;
	private static string search = "";
	public static bool PharmacistActive => ModsConfig.IsActive("fluffy.pharmacist");
	public const string Version = "1.4.4";
	private static List<string> recBullets;

	private static void Check(Listing_Standard l, string key, ref bool value, string field)
	{
		string tip = key + "_Desc";
		MUI.Check(l, key.Translate(), tip.CanTranslate() ? tip.Translate().ToString() : null, ref value, Settings, field);
	}

	public AutoPharmacyMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<AutoPharmacySettings>();
		MUI.Init(new AutoPharmacySettings(), null, "recommended");
		if (Settings.recommended)
			MUI.ApplyAll(Settings);
		var harmony = new Harmony("misak.autopharmacy");
		harmony.PatchAll();
		MUI.PatchWindow(harmony, typeof(AutoPharmacyMod));
		MNews.Register(content, Version);
		Log.Message("[Auto Pharmacy] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Auto Pharmacy";

	// 1.4.3: "not managed" or the highest medical care level for one group
	private static string CareLabel(int v) => v < 0 ? (string)"AP_CareOff".Translate() : ((MedicalCareCategory)Mathf.Clamp(v, 0, 4)).GetLabel().CapitalizeFirst();

	private static void CareButton(Listing_Standard l, string key, Func<int> get, Action<int> set)
	{
		if (l.ButtonTextLabeled(key.Translate(), CareLabel(get())))
		{
			var menu = new List<FloatMenuOption> { new FloatMenuOption(CareLabel(-1), () => set(-1)) };
			for (int i = (int)MedicalCareCategory.NoMeds; i <= (int)MedicalCareCategory.Best; i++)
			{
				int li = i;
				menu.Add(new FloatMenuOption(CareLabel(li), () => set(li)));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
	}

	public override void DoSettingsWindowContents(Rect r)
	{
		MUI.Version(r, "v" + Version + " · Bolek98");
		if (recBullets == null)
		{
			recBullets = new List<string>();
			for (int i = 1; i <= 4; i++)
				recBullets.Add(("AP_Rec" + i).Translate());
		}
		float y0 = r.y;
		y0 += MUI.RecommendedCard(new Rect(r.x, y0, r.width, 0f), Settings, Settings.recommended, v =>
		{
			Settings.recommended = v;
			if (v) MUI.ApplyAll(Settings);
		}, recBullets) + 6f;
		if (Settings.recommended)
			y0 += MUI.LockNote(new Rect(r.x, y0, r.width, 0f)) + 4f;
		var l = new Listing_Standard();
		Rect top = new Rect(r.x, y0, r.width, Settings.autoCare && !PharmacistActive ? 490f : 420f);   // 1.4.3: care per group
		MUI.BeginLock(Settings.recommended);
		try
		{
		l.Begin(top);
		l.ColumnWidth = (top.width - 24f) / 2f;
		Check(l, "AP_Enabled", ref Settings.enabled, "enabled");
		Check(l, "AP_Notify", ref Settings.notify, "notify");
		Check(l, "AP_Prisoners", ref Settings.prisoners, "prisoners");
		Check(l, "AP_UseMood", ref Settings.useMood, "useMood");
		Check(l, "AP_UseRest", ref Settings.useRest, "useRest");
		Check(l, "AP_UsePrevention", ref Settings.usePrevention, "usePrevention");
		Check(l, "AP_UseChronic", ref Settings.useChronic, "useChronic");
		Check(l, "AP_Addictions", ref Settings.maintainAddictions, "maintainAddictions");
		Check(l, "AP_MoodEarly", ref Settings.moodEarly, "moodEarly");
		if (PharmacistActive)
			l.Label("AP_CarePharmacist".Translate());
		else
			Check(l, "AP_AutoCare", ref Settings.autoCare, "autoCare");
		Check(l, "AP_CombatDrugs", ref Settings.combatDrugs, "combatDrugs");
		if (Settings.combatDrugs)
			Settings.combatHours = Mathf.RoundToInt(MUI.Slider(l, "AP_CombatHours".Translate(Settings.combatHours), Settings.combatHours, 6f, 72f, "AP_CombatHours_Desc".Translate(), Settings, "combatHours"));
		l.NewColumn();
		Settings.painFrom = MUI.Slider(l, "AP_PainFrom".Translate((Settings.painFrom * 100f).ToString("0")), Settings.painFrom, 0.05f, 0.6f, "AP_PainFrom_Desc".Translate(), Settings, "painFrom");
		Settings.minHoursBetween = Mathf.RoundToInt(MUI.Slider(l, "AP_MinHours".Translate(Settings.minHoursBetween), Settings.minHoursBetween, 1f, 48f, "AP_MinHours_Desc".Translate(), Settings, "minHoursBetween"));
		if (l.ButtonTextLabeled("AP_Addictive_Label".Translate(), ("AP_AddictiveMode" + Settings.addictive).Translate()))
		{
			var menu = new List<FloatMenuOption>();
			for (int i = 0; i < 3; i++)
			{
				int li = i;
				menu.Add(new FloatMenuOption(("AP_AddictiveMode" + i).Translate(), () => Settings.addictive = li));
			}
			Find.WindowStack.Add(new FloatMenu(menu));
		}
		GUI.color = MUI.Muted;
		l.Label("AP_AddictiveHelp".Translate());
		GUI.color = Color.white;
		Check(l, "AP_DoctorCarry", ref Settings.doctorCarry, "doctorCarry");
		Check(l, "AP_WarnMissing", ref Settings.warnMissing, "warnMissing");
		// 1.4.3: highest medicine per group for the automatic care level
		if (Settings.autoCare && !PharmacistActive)
		{
			l.Gap(4f);
			l.Label("AP_CareGroups".Translate(), -1f, "AP_CareGroups_Desc".Translate());
			CareButton(l, "AP_CareColonists", () => Settings.careColonists, v => Settings.careColonists = v);
			CareButton(l, "AP_CareSlaves", () => Settings.careSlaves, v => Settings.careSlaves = v);
			CareButton(l, "AP_CarePrisoners", () => Settings.carePrisoners, v => Settings.carePrisoners = v);
			CareButton(l, "AP_CareGuests", () => Settings.careGuests, v => Settings.careGuests = v);
			CareButton(l, "AP_CareAnimals", () => Settings.careAnimals, v => Settings.careAnimals = v);
		}
		l.End();
		}
		finally
		{
			MUI.EndLock(top);
		}

		// every drug in the game and what it does
		float y = top.yMax + 6f;
		Widgets.Label(new Rect(r.x, y + 3f, 260f, 24f), "AP_ListHead".Translate(Pharmacy.Drugs.Count));
		// 1.4.0: search in the list (name, what it does, mod)
		search = Widgets.TextField(new Rect(r.x + 270f, y, r.width - 270f - 190f, 28f), search ?? "");
		if (string.IsNullOrEmpty(search))
		{
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(r.x + 276f, y + 4f, 300f, 24f), "AP_Search".Translate());
			GUI.color = Color.white;
		}
		if (Widgets.ButtonText(new Rect(r.xMax - 180f, y, 180f, 28f), "AP_HistoryButton".Translate(Comp.Get()?.history.Count ?? 0)))
			Find.WindowStack.Add(new Dialog_History());
		y += 32f;
		Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
		string q = (search ?? "").Trim();
		List<DrugInfo> list = q.Length == 0 ? Pharmacy.Drugs : Pharmacy.Drugs.Where(d =>
			((string)d.def.LabelCap).IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			d.def.defName.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			d.Summary().IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
			(d.def.modContentPack?.Name ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
		float rowH = 30f;
		Rect view = new Rect(0f, 0f, outRect.width - 18f, list.Count * rowH);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outRect.height)
				continue;
			DrugInfo d = list[i];
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			bool on = !Settings.blocked.Contains(d.def.defName);
			bool was = on;
			Widgets.Checkbox(new Vector2(row.x + 2f, row.y + 2f), ref on);
			if (on != was)
			{
				if (on) Settings.blocked.Remove(d.def.defName);
				else Settings.blocked.Add(d.def.defName);
			}
			Widgets.ThingIcon(new Rect(row.x + 32f, row.y + 1f, 26f, 26f), d.def);
			Widgets.Label(new Rect(row.x + 64f, row.y + 3f, 220f, 24f), ((string)d.def.LabelCap).Truncate(220f));
			string sum = d.Summary();
			Rect sr = new Rect(row.x + 290f, row.y + 3f, row.width - 294f, 24f);
			Widgets.Label(sr, sum.Truncate(sr.width));
			TooltipHandler.TipRegion(row, d.def.LabelCap + " (" + (d.def.modContentPack?.Name ?? "?") + ")\n" + sum);
		}
		Widgets.EndScrollView();
	}
}


// 1.4.0: who got what and why (the last 60)
public class Dialog_History : Window
{
	private Vector2 scroll;

	public Dialog_History()
	{
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
	}

	public override Vector2 InitialSize => new Vector2(640f, 520f);

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "AP_HistoryTitle".Translate());
		Text.Font = GameFont.Small;
		List<string> lines = (Comp.Get()?.history ?? new List<string>()).AsEnumerable().Reverse().ToList();
		Rect outer = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 40f);
		float h = lines.Sum(l => Text.CalcHeight(l, outer.width - 20f) + 4f);
		Rect view = new Rect(0f, 0f, outer.width - 16f, Math.Max(h, outer.height));
		Widgets.BeginScrollView(outer, ref scroll, view);
		float y = 0f;
		foreach (string l in lines)
		{
			float lh = Text.CalcHeight(l, view.width);
			Widgets.Label(new Rect(0f, y, view.width, lh), l);
			y += lh + 4f;
		}
		if (lines.Count == 0)
			Widgets.Label(new Rect(0f, 0f, view.width, 30f), "AP_HistoryEmpty".Translate());
		Widgets.EndScrollView();
	}
}

// 1.4.0: a doctor carries two doses of the drugs given most often – no walk to the stockpile
// (only safe ones: not addictive, cannot overdose)
public class WorkGiver_StockDrugs : WorkGiver_Scanner
{
	private const int Carry = 2, Kinds = 3;

	public override PathEndMode PathEndMode => PathEndMode.ClosestTouch;

	public override bool ShouldSkip(Pawn pawn, bool forced = false)
	{
		var s = AutoPharmacyMod.Settings;
		return !s.enabled || !s.doctorCarry || pawn.inventory == null || !pawn.IsColonist || Wanted(pawn).Count == 0;
	}

	private static List<ThingDef> Wanted(Pawn pawn)
	{
		var list = new List<ThingDef>();
		foreach (ThingDef d in Comp.MostGiven(Kinds * 2))
		{
			DrugInfo info = Pharmacy.Drugs.FirstOrDefault(x => x.def == d);
			if (info == null || info.addictiveness > 0f || info.overdoses || info.Combat || AutoPharmacyMod.Settings.blocked.Contains(d.defName))
				continue;
			if (pawn.inventory.innerContainer.TotalStackCountOfDef(d) < Carry)
				list.Add(d);
			if (list.Count >= Kinds)
				break;
		}
		return list;
	}

	public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
	{
		foreach (ThingDef d in Wanted(pawn))
			foreach (Thing t in pawn.Map.listerThings.ThingsOfDef(d))
				yield return t;
	}

	public override bool HasJobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		if (t.IsForbidden(pawn) || !t.Spawned || !pawn.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some))
			return false;
		// only from a stockpile or shelf – not a dose somebody dropped in a fight
		return t.IsInAnyStorage() && pawn.inventory.innerContainer.TotalStackCountOfDef(t.def) < Carry;
	}

	public override Job JobOnThing(Pawn pawn, Thing t, bool forced = false)
	{
		Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, t);
		job.count = Math.Min(t.stackCount, Carry - pawn.inventory.innerContainer.TotalStackCountOfDef(t.def));
		return job.count > 0 ? job : null;
	}
}


// 1.4.0: a drug that lowers a condition right away (the antitoxin lowers toxic buildup)
public class IngestionOutcomeDoer_ReduceHediff : IngestionOutcomeDoer
{
	public HediffDef hediffDef;
	public float amount = 0.3f;

	protected override void DoIngestionOutcomeSpecial(Pawn pawn, Thing ingested, int ingestedCount)
	{
		Hediff h = hediffDef == null ? null : pawn.health?.hediffSet?.GetFirstHediffOfDef(hediffDef);
		if (h == null)
			return;
		h.Severity -= amount;
		if (h.Severity <= 0.001f)
			pawn.health.RemoveHediff(h);
	}
}
