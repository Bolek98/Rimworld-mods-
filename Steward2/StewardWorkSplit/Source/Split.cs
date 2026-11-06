using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;

namespace StewardWorkSplit;

// One smaller work type split off a big one: its own WorkTypeDef (Defs/WorkTypes.xml) and the
// work givers (jobs) that move into it from the parent.
public sealed class Part
{
	public readonly string def, parent;
	public readonly string[] givers;
	public readonly bool onByDefault;

	public Part(string def, string parent, bool on, params string[] givers)
	{
		this.def = def;
		this.parent = parent;
		this.givers = givers;
		onByDefault = on;
	}
}

// The game builds its list of work types (and the Work tab columns) once, at start. So the split
// happens there, right before the columns are made: settings changes count after a restart.
// All our work types always exist (hidden when off), so saves stay the same with any settings.
public static class Split
{
	public static readonly List<Part> Parts = new List<Part>
	{
		new Part("SW_Surgery", "Doctor", true, "DoBillsMedicalHumanOperation", "DoBillsMedicalAnimalOperation", "TakeToBedToOperate"),
		new Part("SW_Rescue", "Doctor", false, "DoctorRescue"),
		new Part("SW_FeedPatients", "Doctor", false, "DoctorFeedHumanlikes"),
		new Part("SW_AnimalDoctor", "Doctor", false, "DoctorTendToAnimals", "DoctorFeedAnimals"),

		new Part("SW_Butchering", "Cooking", true, "DoBillsButcherFlesh"),
		new Part("SW_Brewing", "Cooking", false, "DoBillsBrew"),

		new Part("SW_Repair", "Construction", true, "Repair", "FixBrokenDownBuilding"),
		new Part("SW_Deconstruct", "Construction", false, "Deconstruct", "Uninstall"),
		new Part("SW_Smoothing", "Construction", true, "ConstructSmoothFloors", "ConstructSmoothWalls"),
		new Part("SW_Roofing", "Construction", false, "BuildRoofs", "RemoveRoofs"),
		new Part("SW_FloorRemoval", "Construction", false, "ConstructRemoveFloors", "ConstructRemoveFoundations"),

		new Part("SW_Harvesting", "Growing", true, "GrowerHarvest"),
		new Part("SW_TreeCare", "PlantCutting", false, "ExtractTree", "PruneGauranlenTree"),
		new Part("SW_Drilling", "Mining", false, "Drill"),

		new Part("SW_Drugs", "Crafting", true, "DoBillsProduceDrugs"),
		new Part("SW_Stonecutting", "Crafting", false, "DoBillsStonecut"),
		new Part("SW_Smelting", "Crafting", false, "DoBillsSmelter"),
		new Part("SW_Refining", "Crafting", false, "DoBillsRefinery"),

		new Part("SW_Machining", "Smithing", false, "DoBillsMachiningTable"),
		new Part("SW_Fabrication", "Smithing", false, "DoBillsFabricationBench"),

		new Part("SW_Scanning", "Research", true, "LongRangeScan", "GroundPenetratingScan"),
		new Part("SW_Study", "Research", false, "StudyArchotechStructures", "Hack"),

		new Part("SW_Taming", "Handling", false, "Tame"),
		new Part("SW_Training", "Handling", false, "Train"),
		new Part("SW_AnimalProducts", "Handling", false, "Milk", "Shear"),
		new Part("SW_Slaughter", "Handling", false, "Slaughter"),

		new Part("SW_Recruiting", "Warden", false, "ChatWithPrisoner", "ConvertPrisoner", "EnslavePrisoner"),
		new Part("SW_FeedPrisoners", "Warden", false, "FeedPrisoner", "DeliverFoodToPrisoner"),
		new Part("SW_Executions", "Warden", false, "DoExecution", "ExecuteGuiltyColonist", "ExecuteSlave"),
		new Part("SW_Slaves", "Warden", false, "SuppressSlave", "ImprisonSlave", "EmancipateSlave"),

		new Part("SW_Refuel", "Hauling", false, "Refuel"),
		new Part("SW_Rearm", "Hauling", false, "RearmTurrets"),
		new Part("SW_Caravans", "Hauling", false, "HelpGatheringItemsForCaravan", "LoadTransporters", "HaulToPortal", "UnloadCarriers"),
		new Part("SW_Corpses", "Hauling", false, "HaulCorpses", "DoBillsCremate"),
		new Part("SW_Beer", "Hauling", false, "FillFermentingBarrel", "TakeBeerOutOfFermentingBarrel"),

		new Part("SW_Snow", "Cleaning", false, "CleanClearSnow"),
		new Part("SW_Painting", "Art", false, "PaintBuilding", "PaintFloor", "RemovePaintBuilding", "RemovePaintFloor"),
	};

	public const string RegistryKey = "Bolek98.WorkSplit.Parents";

	// what this game start really did (the settings window compares against it)
	public static readonly Dictionary<string, string> Active = new Dictionary<string, string>();   // our work type -> parent
	public static readonly Dictionary<string, string> OriginalType = new Dictionary<string, string>(); // work giver -> its work type before us
	public static readonly HashSet<string> AppliedOn = new HashSet<string>();
	public static readonly Dictionary<string, string> AppliedMoves = new Dictionary<string, string>();
	public static bool Applied;

	public static Part PartOf(string def) => Parts.FirstOrDefault(p => p.def == def);

	public static void Prefix(bool hotReload)
	{
		if (hotReload || Applied)
			return;
		try
		{
			Apply();
		}
		catch (Exception e)
		{
			Log.Error("[Steward – Work Split] splitting failed, the work types stay as they were: " + e);
		}
	}

	public static void Postfix(bool hotReload)
	{
		if (hotReload)
			return;
		try
		{
			FixRecipes();
		}
		catch (Exception e)
		{
			Log.Warning("[Steward – Work Split] recipes: " + e.Message);
		}
	}

	private static void Apply()
	{
		Applied = true;
		WorkSplitSettings s = WorkSplitMod.Settings;
		foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
			if (g.workType != null)
				OriginalType[g.defName] = g.workType.defName;

		// 1) our work types look like their parent (same skills, same people unable to do it) and stay hidden
		foreach (Part p in Parts)
		{
			WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.parent);
			if (c == null)
				continue;
			c.visible = false;
			if (parent == null)
				continue;
			c.workTags = parent.workTags;
			c.relevantSkills = new List<SkillDef>(parent.relevantSkills ?? new List<SkillDef>());
			c.requireCapableColonist = false;   // only a switched-on part (Activate)
			c.disabledForSlaves = parent.disabledForSlaves;
			c.visibleOnlyWithChildrenInColony = parent.visibleOnlyWithChildrenInColony;
			c.alwaysStartActive = false;
			c.naturalPriority = parent.naturalPriority;
		}

		// 2) the switched-on parts take their jobs from the parent (only jobs still in the parent –
		//    another mod may have moved them already)
		foreach (Part p in Parts)
		{
			if (!s.IsOn(p))
				continue;
			WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.parent);
			if (c == null || parent == null)
				continue;
			int moved = 0;
			foreach (string gn in p.givers)
			{
				WorkGiverDef g = DefDatabase<WorkGiverDef>.GetNamedSilentFail(gn);
				if (g != null && g.workType == parent)
				{
					g.workType = c;
					moved++;
				}
			}
			AppliedOn.Add(p.def);
			if (moved > 0)
				Activate(c, p.parent);
		}

		// 3) advanced: any job (also from other mods) into any work type
		foreach (var kv in s.moves)
		{
			WorkGiverDef g = DefDatabase<WorkGiverDef>.GetNamedSilentFail(kv.Key);
			WorkTypeDef t = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Value);
			if (g == null || t == null || g.workType == t)
				continue;
			Part p = PartOf(t.defName);
			if (p != null && !Active.ContainsKey(t.defName))
			{
				if (DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.parent) == null)
					continue;
				Activate(t, p.parent);
			}
			g.workType = t;
			AppliedMoves[kv.Key] = kv.Value;
		}

		// 4) columns right after the parent, in our order
		var count = new Dictionary<string, int>();
		foreach (Part p in Parts)
		{
			if (!Active.ContainsKey(p.def))
				continue;
			WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.parent);
			count.TryGetValue(p.parent, out int k);
			count[p.parent] = ++k;
			c.naturalPriority = Math.Max(0, parent.naturalPriority - k);
		}

		// 5) whoever may or may not do the parent: the same for the parts
		foreach (var kv in Active)
		{
			WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key);
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Value);
			foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				RaceProperties r = t.race;
				if (r == null)
					continue;
				if (r.mechEnabledWorkTypes != null && r.mechEnabledWorkTypes.Contains(parent) && !r.mechEnabledWorkTypes.Contains(c))
					r.mechEnabledWorkTypes.Add(c);
				if (r.mechWorkTypePriorities != null)
				{
					MechWorkTypePriority mp = r.mechWorkTypePriorities.FirstOrDefault(x => x.def == parent);
					if (mp != null && !r.mechWorkTypePriorities.Any(x => x.def == c))
						r.mechWorkTypePriorities.Add(new MechWorkTypePriority { def = c, priority = mp.priority });
				}
			}
			foreach (TraitDef t in DefDatabase<TraitDef>.AllDefsListForReading)
				if (t.disabledWorkTypes != null && t.disabledWorkTypes.Contains(parent) && !t.disabledWorkTypes.Contains(c))
					t.disabledWorkTypes.Add(c);
			if (ModsConfig.IdeologyActive)
				foreach (PreceptDef pr in DefDatabase<PreceptDef>.AllDefsListForReading)
					if (pr.opposedWorkTypes != null && pr.opposedWorkTypes.Contains(parent) && !pr.opposedWorkTypes.Contains(c))
						pr.opposedWorkTypes.Add(c);
		}

		// 6) our work types to the end of the list: a save that had no Work Split keeps its priorities
		//    (the game stores them by position)
		var remove = AccessTools.Method(typeof(DefDatabase<WorkTypeDef>), "Remove");
		if (remove != null)
			foreach (Part p in Parts)
			{
				WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def);
				if (c == null)
					continue;
				remove.Invoke(null, new object[] { c });
				DefDatabase<WorkTypeDef>.Add(c);
			}

		AppDomain.CurrentDomain.SetData(RegistryKey, new Dictionary<string, string>(Active));
		if (Active.Count > 0)
			Log.Message("[Steward – Work Split] " + Active.Count + " smaller work types: " + string.Join(", ", Active.Keys));
	}

	private static void Activate(WorkTypeDef c, string parent)
	{
		c.visible = true;
		c.requireCapableColonist = DefDatabase<WorkTypeDef>.GetNamedSilentFail(parent)?.requireCapableColonist ?? false;
		Active[c.defName] = parent;
	}

	// A recipe that may be made only by one work type follows its bench into the part.
	private static void FixRecipes()
	{
		if (Active.Count == 0)
			return;
		foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
		{
			WorkTypeDef need = r.requiredGiverWorkType;
			if (need == null || !Active.ContainsValue(need.defName))
				continue;
			var benches = new HashSet<ThingDef>();
			if (r.recipeUsers != null)
				benches.AddRange(r.recipeUsers);
			foreach (ThingDef t in DefDatabase<ThingDef>.AllDefsListForReading)
				if (t.recipes != null && t.recipes.Contains(r))
					benches.Add(t);
			if (benches.Count == 0)
				continue;
			bool parentHas = false;
			WorkTypeDef part = null;
			foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
			{
				if (g.fixedBillGiverDefs == null || !g.fixedBillGiverDefs.Any(benches.Contains))
					continue;
				if (g.workType == need)
					parentHas = true;
				else if (g.workType != null && Active.TryGetValue(g.workType.defName, out string par) && par == need.defName)
					part = g.workType;
			}
			if (!parentHas && part != null)
				r.requiredGiverWorkType = part;
		}
	}
}
