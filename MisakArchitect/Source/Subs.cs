using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace MisakArchitect;

// 0.2.0: sub-categories inside every category (like Better Architect), found from what the buildings are,
// so buildings of any mod fall into them by themselves
public static class Subs
{
	public static readonly string[] Order =
	{
		"walls", "doors", "fences", "floors", "fl_wood", "fl_stone", "fl_carpet", "fl_metal", "fl_fine", "fl_other", "beds", "tables", "seats", "storage", "lights", "decor", "plants",
		"joy", "benches", "research", "medical", "temperature", "power", "conduits", "batteries", "security",
		"traps", "facilities", "comms", "production", "animals", "zones", "orders", "other"
	};

	private static readonly Dictionary<Designator, string> cache = new Dictionary<Designator, string>();

	public static void Clear() => cache.Clear();

	public static string Of(Designator d)
	{
		if (d == null) return "other";
		if (MisakArchitectMod.S.byMod)
			return "mod:" + (Catalog.ModOf(d) ?? "RimWorld");
		string k = Kind(d);
		return k == "floors" ? FloorKind(d) : k;
	}

	// 1.1.2: floors split by what they are made of
	private static string FloorKind(Designator d)
	{
		if (d is Designator_Dropdown dd) d = dd.Elements.FirstOrDefault();
		if (!(d is Designator_Build b) || !(b.PlacingDef is TerrainDef t)) return "fl_other";
		string n = t.defName ?? "";
		var cost = t.CostList ?? new List<ThingDefCountClass>();
		bool Costs(Func<ThingDef, bool> f) => cost.Any(c => c?.thingDef != null && f(c.thingDef));
		if (n.Contains("Sterile") || n.Contains("Silver") || n.Contains("Gold") || n.Contains("Fine") || n.Contains("Royal")) return "fl_fine";
		if (n.Contains("Carpet") || Costs(x => x.IsStuff && x.stuffProps?.categories?.Contains(StuffCategoryDefOf.Fabric) == true)) return "fl_carpet";
		if (n.Contains("Wood") || Costs(x => x == ThingDefOf.WoodLog)) return "fl_wood";
		if (n.Contains("Metal") || n.Contains("Steel") || n.Contains("Plasteel") || Costs(x => x == ThingDefOf.Steel || x == ThingDefOf.Plasteel)) return "fl_metal";
		if (n.Contains("Tile") || n.Contains("Flagstone") || n.Contains("Stone") || n.Contains("Concrete") || n.Contains("Paved") || n.Contains("Brick")
			|| Costs(x => x.IsStuff && x.stuffProps?.categories?.Contains(StuffCategoryDefOf.Stony) == true)) return "fl_stone";
		return "fl_other";
	}

	// what the building is, whatever the setting (0.4.0: also used for the main folders)
	public static string Kind(Designator d)
	{
		if (d == null) return "other";
		if (cache.TryGetValue(d, out string s)) return s;
		try { s = Find(d); } catch { s = "other"; }
		cache[d] = s;
		return s;
	}

	private static bool Has<T>(ThingDef td) where T : CompProperties => td.comps != null && td.comps.Any(c => c is T);
	private static bool CompClass(ThingDef td, string name) => td.comps != null && td.comps.Any(c => c.compClass != null && c.compClass.Name.Contains(name));

	private static string Find(Designator d)
	{
		if (d is Designator_Dropdown dd)
			return dd.Elements.Count > 0 ? Kind(dd.Elements[0]) : "other";
		if (!(d is Designator_Build b))
		{
			string n = d.GetType().Name;
			if (n.Contains("Zone") || n.Contains("Area")) return "zones";
			return "orders";
		}
		if (b.PlacingDef is TerrainDef) return "floors";
		if (!(b.PlacingDef is ThingDef td)) return "other";
		var bp = td.building;
		string cls = td.thingClass?.Name ?? "";
		if (td.IsDoor) return "doors";
		if (td.IsWall || (bp != null && bp.isPlaceOverableWall)) return "walls";
		if (td.IsFence || (bp != null && bp.isFence)) return "fences";
		if (bp != null && bp.isTrap) return "traps";
		if (typeof(Building_Turret).IsAssignableFrom(td.thingClass)) return "security";
		if (bp != null && bp.isPowerConduit) return "conduits";
		if (td.IsBed)
			return td.building?.bed_humanlike == false ? "animals" : (td.statBases?.Any(s => s.stat == StatDefOf.MedicalTendQualityOffset) == true ? "medical" : "beds");
		if (typeof(Building_ResearchBench).IsAssignableFrom(td.thingClass) || cls.Contains("Research")) return "research";
		if (td.IsCommsConsole) return "comms";
		if (td.IsWorkTable || (td.AllRecipes != null && td.AllRecipes.Count > 0)) return "benches";
		if (Has<CompProperties_Battery>(td)) return "batteries";
		if (Has<CompProperties_Power>(td))
		{
			var pc = td.comps.OfType<CompProperties_Power>().FirstOrDefault();
			if (pc != null && (pc.PowerConsumption < 0f || CompClass(td, "PowerPlant"))) return "power";
		}
		if (Has<CompProperties_TempControl>(td) || cls.Contains("Heater") || cls.Contains("Cooler") || cls.Contains("Vent") || Has<CompProperties_HeatPusher>(td) && !Has<CompProperties_Glower>(td)) return "temperature";
		if (Has<CompProperties_Facility>(td)) return td.defName.Contains("Vitals") || td.defName.Contains("Medic") ? "medical" : "facilities";
		if (td.IsTable) return "tables";
		if (bp != null && bp.isSittable) return "seats";
		if (bp != null && bp.joyKind != null) return "joy";
		// 1.1.1: animal things before storage (troughs and feeders are often storage buildings)
		if (CompClass(td, "AnimalPenMarker") || CompClass(td, "EggBox") || Animalish(td.defName)) return "animals";
		if (typeof(Building_Storage).IsAssignableFrom(td.thingClass) || bp?.fixedStorageSettings != null || bp?.defaultStorageSettings != null && !td.IsWorkTable) return "storage";
		if (typeof(Building_PlantGrower).IsAssignableFrom(td.thingClass) || bp != null && !bp.sowTag.NullOrEmpty()) return "plants";
		if (Has<CompProperties_Glower>(td)) return "lights";
		if (td.IsArt || td.defName.Contains("Statue") || td.defName.Contains("Sculpture") || td.defName.Contains("Decor")) return "decor";
		if (cls.Contains("Animal") || td.defName.Contains("Pen") || td.defName.Contains("Animal") || td.defName.Contains("Hay") || td.defName.Contains("Trough")) return "animals";
		if (td.fillPercent >= 0.4f && td.defName.Contains("Sandbag") || td.defName.Contains("Barricade") || td.defName.Contains("Embrasure")) return "security";
		if (Has<CompProperties_Spawner>(td) || Has<CompProperties_Refuelable>(td) && Has<CompProperties_Power>(td)) return "production";
		return "other";
	}

	private static readonly string[] AnimalWords = { "Animal", "PenMarker", "Trough", "Feeder", "Kennel", "Hutch", "Coop", "Nest", "EggBox", "Hive", "Hay", "Milking", "Shearing", "PetBed", "Doghouse", "Stable" };
	private static bool Animalish(string n) => n != null && AnimalWords.Any(w => n.IndexOf(w, StringComparison.Ordinal) >= 0);

	public static string Label(string key)
	{
		if (key.StartsWith("mod:")) return key.Substring(4);
		return ("MA_Sub_" + key).Translate();
	}

	public static int Rank(string key)
	{
		if (key.StartsWith("mod:")) return key == "mod:RimWorld" ? -1 : 0;
		int i = Array.IndexOf(Order, key);
		return i < 0 ? 999 : i;
	}
}
