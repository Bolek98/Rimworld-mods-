using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyForeman;

// 2.45: three modes instead of one big machine.
//  Simple      – priorities from skills and passions, planned once a day; only real
//                danger (fire, raid, wounded …) changes them for a while.
//  Recommended – Simple plus a calm reaction to what the colony needs: plans at most
//                every few hours, no small back-and-forth, nobody is moved twice in
//                half a day, a crisis takes its people and gives them back.
//  Advanced    – everything Steward can do, with every setting (as before 2.45).
public enum StewardMode
{
	Simple,
	Recommended,
	Advanced,
	// 2.52: Steward decides everything – all crises and situations, your switched-off lists and counts are not used
	Autopilot
}

public sealed class CalmCell
{
	public int tick;
	public bool crisis;
}

public static class Modes
{
	public static StewardMode Current
	{
		get
		{
			ForemanSettings s = ForemanMod.Settings;
			if (s == null) return StewardMode.Recommended;
			return (StewardMode)Mathf.Clamp(s.mode, 0, 3);
		}
	}

	public static bool Calm => Current != StewardMode.Advanced;
	public static bool Simple => Current == StewardMode.Simple;
	public static bool Autopilot => Current == StewardMode.Autopilot;

	// Simple: only crises that are real danger to life or the base
	public const Crisis Danger = Crisis.Fire | Crisis.Raid | Crisis.Medical | Crisis.Toxic | Crisis.Manhunters
		| Crisis.PrisonBreak | Crisis.DangerousBreak | Crisis.EntityEscape | Crisis.VacuumBreach | Crisis.ToxGas
		| Crisis.NoFirefighters | Crisis.Siege | Crisis.Infestation;

	public static Crisis Mask(Crisis c) => Simple ? c & Danger : c;

	// hours between the regular plans
	public static int IntervalHours(ForemanSettings s)
	{
		switch (Current)
		{
			case StewardMode.Simple: return 24;
			case StewardMode.Recommended: return s?.intervalHours ?? 6;   // 2.49.8: your value counts
			case StewardMode.Autopilot: return 3;
			default: return s?.intervalHours ?? 4;
		}
	}

	// the earliest re-plan because the work on the map changed (blueprints, bills …)
	public static int ShiftGap => Current == StewardMode.Advanced ? 1250 : Autopilot ? 2500 : 5000;
	public static int SituationGap => Current == StewardMode.Advanced ? 600 : Autopilot ? 1250 : 5000;

	// a cell the steward changed stays as it is at least this long (calm modes)
	public static int HoldTicks => Simple ? 60000 : Autopilot ? 15000 : 30000;

	public static ForemanStyle PlanStyle(ForemanMapComponent comp)
	{
		if (comp == null) return ForemanStyle.Balanced;
		return Simple ? ForemanStyle.Balanced : comp.style;
	}

	// ---------------- preset values ----------------
	// your own choices – never changed by a mode
	private static readonly string[] Skip =
	{
		"mode", "maxPriorityOverride", "usePickUpAndHaul", "recPerStyle", "autoByDefault", "nightShift", "masterBills",
		"autoSchedule", "scheduleCare", "supplyMessages", "noStorageAlert", "skillAlert", "crisisMessages", "includeLodgers",
		"childLearning", "parentCare", "countMechs", "learningSlots", "learningMaxSkill", "useMyRules", "priorityAreaWorkers",
		"quietMessages", "sitToEat", "butcherFirst"
	};

	public static ForemanSettings PresetValues(StewardMode m)
	{
		var s = new ForemanSettings();
		s.autoMessages = false;
		if (m == StewardMode.Simple)
		{
			s.useDemand = false;
			s.useSituations = false;
			s.balancedAutoFocus = false;
			s.autoStaff = false;
			s.intervalHours = 24;
			s.moodPassion = false;
			s.priorityAreas = false;
		}
		else if (m == StewardMode.Autopilot)
		{
			s.intervalHours = 3;
			s.useDemand = true;
			s.useSituations = true;
			s.balancedAutoFocus = true;
			s.autoStaff = true;
			s.useCrisis = true;
		}
		else
		{
			s.intervalHours = 6;
		}
		return s;
	}

	private static ForemanSettings recCache;
	public static ForemanSettings Recommended => recCache ?? (recCache = PresetValues(StewardMode.Recommended));

	public static void InitMarks()
	{
		MUI.Init(Recommended, null, Skip);
	}

	// copies the preset into the settings (bool / int / float / enum fields only;
	// your lists – rules, profiles, switched-off crises – stay)
	public static void ApplyPreset(ForemanSettings target, StewardMode m)
	{
		if (target == null || m == StewardMode.Advanced) return;
		MUI.SetRecommended(PresetValues(m));
		MUI.ApplyAll(target);
		MUI.SetRecommended(Recommended);
	}

	// Advanced values are kept while a calm mode runs and come back with Advanced
	private static string Snapshot(ForemanSettings s)
	{
		var sb = new System.Text.StringBuilder();
		foreach (var f in typeof(ForemanSettings).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
		{
			Type t = f.FieldType;
			if (f.Name == "mode" || f.Name == "advancedValues" || (t != typeof(bool) && t != typeof(int) && t != typeof(float)))
				continue;
			object v = f.GetValue(s);
			sb.Append(f.Name).Append('=').Append(v is float fv ? fv.ToString(System.Globalization.CultureInfo.InvariantCulture) : v.ToString()).Append(';');
		}
		return sb.ToString();
	}

	private static void Restore(ForemanSettings s, string snap)
	{
		if (string.IsNullOrEmpty(snap)) return;
		foreach (string part in snap.Split(';'))
		{
			int i = part.IndexOf('=');
			if (i <= 0) continue;
			string name = part.Substring(0, i);
			if (name == "quietMessages" || name == "sitToEat" || name == "butcherFirst") continue;   // 2.46.2: outside the modes
			var f = typeof(ForemanSettings).GetField(name);
			if (f == null) continue;
			string v = part.Substring(i + 1);
			try
			{
				if (f.FieldType == typeof(bool)) f.SetValue(s, bool.Parse(v));
				else if (f.FieldType == typeof(int)) f.SetValue(s, int.Parse(v));
				else if (f.FieldType == typeof(float)) f.SetValue(s, float.Parse(v, System.Globalization.CultureInfo.InvariantCulture));
			}
			catch { }
		}
	}

	public static void Set(StewardMode m)
	{
		ForemanSettings s = ForemanMod.Settings;
		if (s == null) return;
		StewardMode was = Current;
		if (was == m) return;
		if (was == StewardMode.Advanced)
			s.advancedValues = Snapshot(s);
		else if (was == StewardMode.Recommended)
			s.recommendedValues = Snapshot(s);
		s.mode = (int)m;
		if (m == StewardMode.Advanced)
			Restore(s, s.advancedValues);
		else
		{
			ApplyPreset(s, m);
			// 2.49.8: Recommended keeps the changes you made in it
			if (m == StewardMode.Recommended)
				Restore(s, s.recommendedValues);
		}
		ForemanMod.Instance?.WriteSettings();
		if (Verse.Current.ProgramState != ProgramState.Playing || Find.Maps == null) return;
		foreach (Map map in Find.Maps)
		{
			ForemanMapComponent c = ForemanMapComponent.For(map);
			if (c == null || !c.auto || MapDemand.Workers(map) == 0) continue;
			c.calmCells.Clear();
			try { c.Run(manual: true); }
			catch (Exception ex) { Log.ErrorOnce("[Steward] mode switch failed: " + ex, 0x57e3a61); }
		}
	}

	public static string Label(StewardMode m) => ("CF_Mode_" + m).Translate();
	public static string Desc(StewardMode m) => ("CF_Mode_" + m + "_Desc").Translate();

	public static List<FloatMenuOption> Menu()
	{
		var list = new List<FloatMenuOption>();
		foreach (StewardMode m in new[] { StewardMode.Simple, StewardMode.Recommended, StewardMode.Autopilot, StewardMode.Advanced })
		{
			StewardMode local = m;
			var opt = new FloatMenuOption((Current == m ? "● " : "") + Label(m), () => Set(local));
			opt.tooltip = new TipSignal(Desc(m));
			list.Add(opt);
		}
		return list;
	}
}

[StaticConstructorOnStartup]
public static class ModeTex
{
	public static readonly Texture2D Icon =
		ContentFinder<Texture2D>.Get("UI/ColonyForeman/Mode", reportFailure: false)
		?? ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", reportFailure: false)
		?? BaseContent.WhiteTex;
}

// The quick switch: a button on every colonist (all selected colonists share one).
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class ModeGizmoPatch
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		if (__instance == null || !__instance.IsColonistPlayerControlled || __instance.Map == null)
			yield break;
		ForemanMapComponent comp = ForemanMapComponent.For(__instance.Map);
		if (comp == null || !comp.auto)
			yield break;
		yield return new Command_Action
		{
			defaultLabel = "CF_ModeGizmo".Translate(Modes.Label(Modes.Current)),
			defaultDesc = "CF_ModeGizmo_Desc".Translate(Modes.Desc(Modes.Current)),
			icon = ModeTex.Icon,
			groupKey = 0x57e3a62,
			action = () =>
			{
				Find.WindowStack.Add(new FloatMenu(Modes.Menu()));
			}
		};
	}
}
