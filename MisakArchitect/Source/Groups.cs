using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;

namespace MisakArchitect;

// 0.4.0: a few main folders instead of every category of every mod – each building goes where it belongs
// by what it is (a table from any mod lands in Furniture); things that fit nowhere go to "From mods" at the end
public static class Groups
{
	public static readonly string[] Order =
	{
		"orders", "build", "floors", "furniture", "production", "power", "security", "health", "decor", "vehicles", "map", "mods"
	};

	// preferred picture of each folder (first one that exists)
	private static readonly Dictionary<string, string[]> IconDefs = new Dictionary<string, string[]>
	{
		{ "build", new[] { "Wall" } },
		{ "floors", new[] { "WoodPlankFloor", "Concrete" } },
		{ "furniture", new[] { "Bed", "DiningChair" } },
		{ "production", new[] { "ElectricStove", "FueledStove", "TableButcher" } },
		{ "power", new[] { "SolarGenerator", "WoodFiredGenerator", "Heater" } },
		{ "security", new[] { "Turret_MiniTurret", "Sandbags" } },
		{ "health", new[] { "VitalsMonitor", "HospitalBed", "ToolCabinet" } },
		{ "decor", new[] { "SculptureSmall", "PlantPot" } },
	};

	private static readonly Dictionary<Designator, string> cache = new Dictionary<Designator, string>();

	public static void Clear() => cache.Clear();

	public static string Label(string g) => ("MA_G_" + g).Translate();

	public static string Of(Designator d, DesignationCategoryDef c)
	{
		if (d == null) return "mods";
		if (cache.TryGetValue(d, out string g)) return g;
		try { g = Find(d, c); } catch { g = "mods"; }
		cache[d] = g;
		return g;
	}

	private static string Find(Designator d, DesignationCategoryDef c)
	{
		if (d is Designator_Dropdown dd)
			return dd.Elements.Count > 0 ? Of(dd.Elements[0], c) : ByCategory(c);
		string pkg = c?.modContentPack?.PackageId?.ToLowerInvariant() ?? "";
		string cn = (c?.defName ?? "").ToLowerInvariant();
		if (pkg.Contains("maparchitect") || cn.Contains("maparchitect") || cn.Contains("terraform")) return "map";
		if (!(d is Designator_Build b))
		{
			if (cn == "orders" || cn == "zone" || cn.Contains("zone") || cn.Contains("order")) return "orders";
			string n = d.GetType().Name;
			if (n.Contains("Zone") || n.Contains("Area")) return "orders";
			return ByCategory(c);
		}
		if (b.PlacingDef is TerrainDef) return "floors";
		switch (Subs.Kind(d))
		{
			case "walls": case "doors": case "fences": return "build";
			case "floors": return "floors";
			case "beds": case "tables": case "seats": case "joy": case "lights": case "storage": return "furniture";
			case "benches": case "research": case "production": case "facilities": case "comms": case "plants": case "animals": return "production";
			case "medical": return "health";
			case "temperature": case "power": case "conduits": case "batteries": return "power";
			case "security": case "traps": return "security";
			case "decor": return "decor";
		}
		return ByCategory(c);
	}

	// what the original category most likely is
	private static string ByCategory(DesignationCategoryDef c)
	{
		if (c == null) return "mods";
		string n = (c.defName + " " + c.label).ToLowerInvariant();
		if (n.Contains("vehic") || n.Contains("ship") || n.Contains("aircraft") || n.Contains("gravship") || n.Contains("shuttle")) return "vehicles";
		if (n.Contains("structure") || n.Contains("prefab") || n.Contains("wall") || n.Contains("roof") || n.Contains("floor") && !n.Contains("floors")) return "build";
		if (n.Contains("floor")) return "floors";
		if (n.Contains("furniture") || n.Contains("joy") || n.Contains("recreation")) return "furniture";
		if (n.Contains("production") || n.Contains("factory") || n.Contains("industr") || n.Contains("craft") || n.Contains("kitchen")) return "production";
		if (n.Contains("power") || n.Contains("temperature") || n.Contains("pipe") || n.Contains("electric")) return "power";
		if (n.Contains("security") || n.Contains("defen") || n.Contains("military") || n.Contains("combat")) return "security";
		if (n.Contains("hygiene") || n.Contains("medic") || n.Contains("health") || n.Contains("biotech")) return "health";
		if (n.Contains("misc") || n.Contains("decor") || n.Contains("ideolog") || n.Contains("art")) return "decor";
		bool official = c.modContentPack == null || c.modContentPack.IsOfficialMod;
		return official ? "decor" : "mods";
	}

	public static Def IconDef(string g)
	{
		if (!IconDefs.TryGetValue(g, out var names)) return null;
		foreach (string n in names)
		{
			Def d = (Def)DefDatabase<ThingDef>.GetNamedSilentFail(n) ?? DefDatabase<TerrainDef>.GetNamedSilentFail(n);
			if (d != null) return d;
		}
		return null;
	}

	// built by the base game or a DLC (shown first), or by a mod (shown below a line)
	public static bool FromMod(Designator d, DesignationCategoryDef c)
	{
		if (d is Designator_Dropdown dd) d = dd.Elements.FirstOrDefault() ?? d;
		ModContentPack m = d is Designator_Build b ? b.PlacingDef?.modContentPack : c?.modContentPack;
		return m != null && !m.IsOfficialMod;
	}
}
