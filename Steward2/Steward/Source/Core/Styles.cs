using System;
using System.Collections.Generic;
using System.Linq;
using Verse;

namespace ColonyForeman;

// Colony styles: each one shifts the importance of some work types up or
// down. The steward then gives those jobs to more (or fewer) colonists and
// to better (or worse) suited ones. Work types that a game or a mod does not
// have are simply ignored.
public static class Styles
{
	// 2.12: reworked. Each style has one clear focus (+0.3 to +0.4), a few
	// helpers (+0.08 to +0.25) and pushes back what does not fit (-0.1 to
	// -0.3). Firefighting, patients and basic work are never touched.
	// Balanced and Custom have no table: Balanced spreads work evenly and only
	// reacts to shortages, Custom uses your own settings.
	// 2.42: every style is small and clear – one main work (↑↑, +0.4 or more),
	// at most two or three helpers (↑). With "a style plans only its own work"
	// everything else goes to the lowest priority. No minus values any more:
	// what a style does not list is simply not its business.
	private static Dictionary<string, float> D(params object[] kv)
	{
		var d = new Dictionary<string, float>();
		for (int i = 0; i + 1 < kv.Length; i += 2)
			d[(string)kv[i]] = (float)kv[i + 1];
		return d;
	}

	private static readonly Dictionary<ForemanStyle, Dictionary<string, float>> Table =
		new Dictionary<ForemanStyle, Dictionary<string, float>>
		{
			[ForemanStyle.Balanced] = new Dictionary<string, float>(),
			[ForemanStyle.Custom] = new Dictionary<string, float>(),
			// food and shelter first
			[ForemanStyle.Survival] = D("Cooking", 0.4f, "Growing", 0.3f, "Hunting", 0.3f, "Fishing", 0.25f, "Construction", 0.15f),
			// fields
			[ForemanStyle.Farming] = D("Growing", 0.45f, "PlantCutting", 0.25f, "Cooking", 0.15f),
			[ForemanStyle.FieldsForest] = D("Growing", 0.4f, "PlantCutting", 0.4f),
			// animals
			[ForemanStyle.Ranching] = D("Handling", 0.45f, "Growing", 0.15f),
			// food from the wild
			[ForemanStyle.HuntFish] = D("Hunting", 0.45f, "Fishing", 0.35f, "Cooking", 0.15f),
			// the kitchen: meals and what goes into them
			[ForemanStyle.Kitchen] = D("Cooking", 0.5f, "Hunting", 0.15f, "Fishing", 0.15f),
			// base
			[ForemanStyle.Building] = D("Construction", 0.45f, "Mining", 0.2f, "Hauling", 0.12f),
			[ForemanStyle.Mining] = D("Mining", 0.5f, "Hauling", 0.15f),
			[ForemanStyle.Industry] = D("Mining", 0.4f, "Smithing", 0.3f, "Crafting", 0.25f),
			// making things
			[ForemanStyle.Production] = D("Crafting", 0.4f, "Smithing", 0.4f, "Tailoring", 0.4f),
			[ForemanStyle.Smithing] = D("Smithing", 0.5f, "Crafting", 0.15f),
			[ForemanStyle.Tailoring] = D("Tailoring", 0.5f),
			[ForemanStyle.Art] = D("Art", 0.5f),
			[ForemanStyle.Trade] = D("Art", 0.4f, "Tailoring", 0.3f, "Crafting", 0.25f),
			// knowledge
			[ForemanStyle.Research] = D("Research", 0.5f, "DarkStudy", 0.25f),
			// people
			[ForemanStyle.Defense] = D("Construction", 0.4f, "Smithing", 0.3f, "Doctor", 0.2f),
			[ForemanStyle.Care] = D("Doctor", 0.45f, "Childcare", 0.3f, "Warden", 0.25f, "Cleaning", 0.15f),
			[ForemanStyle.Prison] = D("Warden", 0.5f, "Doctor", 0.15f),
			// home
			[ForemanStyle.Comfort] = D("Cleaning", 0.4f, "Art", 0.3f, "Construction", 0.15f),
			[ForemanStyle.Tidy] = D("Hauling", 0.5f, "Cleaning", 0.4f, "KAU_UrgentHaul", 0.3f, "HaulSmarterUrgent", 0.3f)
		};

	// The order in the menu.
	public static readonly ForemanStyle[] All =
	{
		ForemanStyle.Balanced, ForemanStyle.Custom, ForemanStyle.Survival,
		ForemanStyle.Farming, ForemanStyle.FieldsForest, ForemanStyle.Ranching, ForemanStyle.HuntFish, ForemanStyle.Kitchen,
		ForemanStyle.Building, ForemanStyle.Mining, ForemanStyle.Industry,
		ForemanStyle.Production, ForemanStyle.Smithing, ForemanStyle.Tailoring, ForemanStyle.Art, ForemanStyle.Trade,
		ForemanStyle.Research, ForemanStyle.Defense, ForemanStyle.Care, ForemanStyle.Prison,
		ForemanStyle.Comfort, ForemanStyle.Tidy
	};

	public static float Delta(ForemanStyle style, WorkTypeDef w)
	{
		Dictionary<string, float> t;
		float d;
		return w != null && Table.TryGetValue(style, out t) && t.TryGetValue(w.defName, out d) ? d : 0f;
	}

	public static string Label(ForemanStyle style)
	{
		return ("CF_Style_" + style).Translate();
	}

	// "Growing ↑↑, Cooking ↑, Art ↓" – shown as the menu tooltip.
	public static string Describe(ForemanStyle style)
	{
		string head = ("CF_StyleDesc_" + style).Translate();
		Dictionary<string, float> t;
		if (!Table.TryGetValue(style, out t) || t.Count == 0)
			return head;
		var parts = new List<string>();
		foreach (var kv in t.OrderByDescending(x => x.Value))
		{
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key);
			if (w == null)
				continue;
			string arrows = kv.Value >= 0.4f ? "↑↑↑" : kv.Value >= 0.25f ? "↑↑" : kv.Value > 0f ? "↑" : kv.Value <= -0.2f ? "↓↓" : "↓";
			parts.Add(w.labelShort.CapitalizeFirst() + " " + arrows);
		}
		string text = head + "\n\n" + string.Join(", ", parts);
		ForemanSettings st = ForemanMod.Settings;
		if (st != null && st.styleStrict)
			text += "\n\n" + (st.styleOthersOff ? "CF_StyleStrictNoteOff" : "CF_StyleStrictNote").Translate();
		return text;
	}
}
