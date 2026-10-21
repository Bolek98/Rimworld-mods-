using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.13: crises as one table. For every crisis: which jobs go up or
// down (urgency), how many more colonists do them (workers ×) and how many
// at least (min). Several crises at once add up. Detection is in Detect();
// fires and raids are also checked every few seconds (Urgent()).
// ---------------------------------------------------------------------------
public static partial class CrisisRules
{
	public struct Effect
	{
		public float urgency;
		public float workers;
		public int min;
		public Effect(float u, float w = 1f, int m = 0) { urgency = u; workers = w; min = m; }
	}

	public static readonly Crisis[] All =
	{
		Crisis.Fire, Crisis.Raid, Crisis.Medical, Crisis.Food, Crisis.Medicine, Crisis.Toxic,
		Crisis.Weather, Crisis.Winter, Crisis.Blight, Crisis.Power, Crisis.Breakdown, Crisis.Mood,
		// 2.29
		Crisis.NoFirefighters, Crisis.DangerousBreak, Crisis.PrisonBreak, Crisis.EntityEscape, Crisis.Manhunters,
		Crisis.Siege, Crisis.Infestation, Crisis.VacuumBreach, Crisis.ToxGas, Crisis.Labor, Crisis.Epidemic,
		Crisis.NoBeds, Crisis.AnimalSick, Crisis.Death, Crisis.IndoorTemp, Crisis.FreezerThaw, Crisis.RoofCollapse,
		Crisis.Darkness, Crisis.QuestDeadline, Crisis.LowAmmo, Crisis.WaterShortage, Crisis.Hygiene
	};

	private static readonly Dictionary<Map, bool> raidOn = new Dictionary<Map, bool>();

	public static void Reset() => raidOn.Clear();
	public static void Forget(Map m) => raidOn.Remove(m);

	public const Crisis UrgentMask = Crisis.Fire | Crisis.Raid;

	public static readonly Dictionary<Crisis, Dictionary<string, Effect>> Table = new Dictionary<Crisis, Dictionary<string, Effect>>
	{
		[Crisis.Medical] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.5f, 2.5f, 3), ["Cleaning"] = new Effect(0.05f),
			["Art"] = new Effect(-0.1f), ["Research"] = new Effect(-0.05f)
		},
		[Crisis.Food] = new Dictionary<string, Effect>
		{
			["Cooking"] = new Effect(0.2f, 1.5f), ["Hunting"] = new Effect(0.25f, 1.5f, 1), ["Fishing"] = new Effect(0.25f, 1.5f, 1),
			["Growing"] = new Effect(0.15f, 1.3f), ["PlantCutting"] = new Effect(0.1f),
			["Art"] = new Effect(-0.2f), ["DarkStudy"] = new Effect(-0.2f), ["Research"] = new Effect(-0.15f)
		},
		[Crisis.Winter] = new Dictionary<string, Effect>
		{
			["Growing"] = new Effect(0.25f, 1.5f), ["PlantCutting"] = new Effect(0.1f), ["Hunting"] = new Effect(0.05f)
		},
		[Crisis.Fire] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.1f), ["Hauling"] = new Effect(-0.1f), ["Cleaning"] = new Effect(-0.2f),
			["Art"] = new Effect(-0.3f), ["Research"] = new Effect(-0.2f), ["DarkStudy"] = new Effect(-0.2f), ["Hunting"] = new Effect(-0.2f)
		},
		[Crisis.Raid] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.3f, 2f, 2), ["Construction"] = new Effect(0.15f, 1.3f), ["Warden"] = new Effect(0.1f),
			["Hunting"] = new Effect(-0.3f), ["PlantCutting"] = new Effect(-0.2f), ["Growing"] = new Effect(-0.15f),
			["Mining"] = new Effect(-0.15f), ["Hauling"] = new Effect(-0.2f), ["Cleaning"] = new Effect(-0.3f),
			["Art"] = new Effect(-0.3f), ["Research"] = new Effect(-0.2f), ["DarkStudy"] = new Effect(-0.2f)
		},
		[Crisis.Weather] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.15f), ["Growing"] = new Effect(0.15f, 1.3f), ["Doctor"] = new Effect(0.1f),
			["Cooking"] = new Effect(0.05f), ["Hunting"] = new Effect(-0.15f), ["Mining"] = new Effect(-0.05f)
		},
		[Crisis.Toxic] = new Dictionary<string, Effect>
		{
			["Growing"] = new Effect(0.2f, 1.5f), ["Doctor"] = new Effect(0.1f), ["Research"] = new Effect(0.1f),
			["Crafting"] = new Effect(0.1f), ["Tailoring"] = new Effect(0.05f), ["Smithing"] = new Effect(0.05f),
			["Hunting"] = new Effect(-0.3f), ["PlantCutting"] = new Effect(-0.1f), ["Mining"] = new Effect(-0.1f)
		},
		[Crisis.Blight] = new Dictionary<string, Effect>
		{
			["PlantCutting"] = new Effect(0.35f, 2f, 2), ["Growing"] = new Effect(0.15f)
		},
		[Crisis.Mood] = new Dictionary<string, Effect>
		{
			["Cleaning"] = new Effect(0.2f, 1.3f), ["Cooking"] = new Effect(0.15f), ["Art"] = new Effect(0.15f),
			["Construction"] = new Effect(0.05f), ["Mining"] = new Effect(-0.1f), ["Hauling"] = new Effect(-0.05f)
		},
		[Crisis.Power] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.25f, 1.5f, 1), ["Hauling"] = new Effect(0.1f), ["PlantCutting"] = new Effect(0.1f),
			["Research"] = new Effect(-0.2f), ["Smithing"] = new Effect(-0.1f), ["Crafting"] = new Effect(-0.1f), ["Tailoring"] = new Effect(-0.1f)
		},
		[Crisis.Medicine] = new Dictionary<string, Effect>
		{
			["Crafting"] = new Effect(0.2f, 1.3f), ["Growing"] = new Effect(0.1f), ["Doctor"] = new Effect(0.05f)
		},
		[Crisis.Breakdown] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.3f, 1.3f, 1), ["Crafting"] = new Effect(0.1f)
		},
		// ---- 2.29 ----
		[Crisis.Manhunters] = new Dictionary<string, Effect>
		{
			["Growing"] = new Effect(-0.4f), ["PlantCutting"] = new Effect(-0.4f), ["Hunting"] = new Effect(-0.5f),
			["Mining"] = new Effect(-0.2f), ["Hauling"] = new Effect(-0.2f), ["Handling"] = new Effect(-0.3f),
			["Doctor"] = new Effect(0.15f), ["Crafting"] = new Effect(0.1f), ["Cleaning"] = new Effect(0.05f)
		},
		[Crisis.Epidemic] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.4f, 2f, 2), ["Cooking"] = new Effect(0.1f), ["Cleaning"] = new Effect(0.1f),
			["Art"] = new Effect(-0.2f), ["Research"] = new Effect(-0.1f)
		},
		[Crisis.PrisonBreak] = new Dictionary<string, Effect>
		{
			["Warden"] = new Effect(0.4f, 2f, 2), ["Doctor"] = new Effect(0.2f),
			["Art"] = new Effect(-0.3f), ["Research"] = new Effect(-0.2f), ["Growing"] = new Effect(-0.1f)
		},
		[Crisis.DangerousBreak] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.25f, 1.5f, 1), ["Warden"] = new Effect(0.1f),
			["Art"] = new Effect(-0.2f), ["Hauling"] = new Effect(-0.1f)
		},
		[Crisis.IndoorTemp] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.35f, 1.5f, 1), ["PlantCutting"] = new Effect(0.15f), ["Hauling"] = new Effect(0.15f),
			["Art"] = new Effect(-0.2f)
		},
		[Crisis.NoBeds] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.4f, 1.5f, 1), ["Doctor"] = new Effect(0.1f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.FreezerThaw] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.35f, 1.3f, 1), ["Cooking"] = new Effect(0.25f, 1.5f), ["Art"] = new Effect(-0.1f)
		},
		[Crisis.Siege] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.35f, 1.5f, 1), ["Doctor"] = new Effect(0.15f), ["Crafting"] = new Effect(0.1f),
			["Hunting"] = new Effect(-0.4f), ["PlantCutting"] = new Effect(-0.2f), ["Growing"] = new Effect(-0.2f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.Infestation] = new Dictionary<string, Effect>
		{
			["Mining"] = new Effect(-0.5f), ["Doctor"] = new Effect(0.2f), ["Hauling"] = new Effect(-0.1f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.RoofCollapse] = new Dictionary<string, Effect>
		{
			["Mining"] = new Effect(-0.5f), ["Construction"] = new Effect(0.3f, 1.3f, 1)
		},
		[Crisis.Death] = new Dictionary<string, Effect>
		{
			["Hauling"] = new Effect(0.3f, 1.3f, 1), ["Construction"] = new Effect(0.1f)
		},
		[Crisis.QuestDeadline] = new Dictionary<string, Effect>
		{
			["Crafting"] = new Effect(0.2f, 1.3f), ["Smithing"] = new Effect(0.15f), ["Tailoring"] = new Effect(0.15f),
			["Hauling"] = new Effect(0.15f), ["Art"] = new Effect(-0.1f)
		},
		[Crisis.AnimalSick] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.25f, 1.5f, 1), ["Handling"] = new Effect(0.2f)
		},
		[Crisis.Labor] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.5f, 1.5f, 1), ["Childcare"] = new Effect(0.2f)
		},
		[Crisis.EntityEscape] = new Dictionary<string, Effect>
		{
			["Warden"] = new Effect(0.3f, 1.5f, 1), ["Doctor"] = new Effect(0.2f),
			["DarkStudy"] = new Effect(-0.4f), ["Art"] = new Effect(-0.2f), ["Research"] = new Effect(-0.1f)
		},
		[Crisis.Darkness] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.25f, 1.3f), ["Hauling"] = new Effect(0.2f), ["PlantCutting"] = new Effect(0.1f)
		},
		[Crisis.VacuumBreach] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.5f, 2f, 2), ["Doctor"] = new Effect(0.1f), ["Art"] = new Effect(-0.3f), ["Research"] = new Effect(-0.2f)
		},
		[Crisis.ToxGas] = new Dictionary<string, Effect>
		{
			["Doctor"] = new Effect(0.2f), ["Cleaning"] = new Effect(0.15f), ["Construction"] = new Effect(0.1f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.LowAmmo] = new Dictionary<string, Effect>
		{
			["Crafting"] = new Effect(0.35f, 1.5f, 1), ["Smithing"] = new Effect(0.2f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.WaterShortage] = new Dictionary<string, Effect>
		{
			["Construction"] = new Effect(0.35f, 1.5f, 1), ["Hauling"] = new Effect(0.1f), ["Art"] = new Effect(-0.2f)
		},
		[Crisis.Hygiene] = new Dictionary<string, Effect>
		{
			["Cleaning"] = new Effect(0.25f, 1.5f), ["Hauling"] = new Effect(0.2f), ["Construction"] = new Effect(0.1f)
		},
		[Crisis.NoFirefighters] = new Dictionary<string, Effect>
		{
			["Firefighter"] = new Effect(0.5f, 3f, 99)
		}
	};

	public static bool Allowed(Crisis c)
	{
		ForemanSettings s = ForemanMod.Settings;
		if (s != null && Modes.Autopilot) return true;   // 2.52: Autopilot uses every crisis
		return s != null && s.useCrisis && (s.crisisOff & (long)c) == 0;
	}

	public static string Label(Crisis c) => ("CF_Crisis_" + c).Translate();

	// "Doctor ↑↑, Cooking ↑, Art ↓" for the settings tab
	public static string Describe(Crisis c)
	{
		if (!Table.TryGetValue(c, out var t))
			return "";
		var parts = new List<string>();
		foreach (var kv in t.OrderByDescending(x => x.Value.urgency))
		{
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv.Key);
			if (w == null)
				continue;
			float u = kv.Value.urgency;
			string arrows = u >= 0.25f ? "↑↑" : u > 0f ? "↑" : u <= -0.2f ? "↓↓" : "↓";
			string extra = kv.Value.workers > 1.01f ? " ×" + kv.Value.workers.ToString("0.#") : "";
			parts.Add(w.labelShort.CapitalizeFirst() + " " + arrows + extra);
		}
		return string.Join(", ", parts);
	}

	// ---------------------------------------------------------------------
	// detection
	// ---------------------------------------------------------------------

	// Fires in the home area and raids – cheap, checked every 250 ticks.
	public static Crisis Urgent(Map map)
	{
		Crisis c = Crisis.None;
		if (Allowed(Crisis.Fire) && FireInHome(map))
			c |= Crisis.Fire;
		bool raid = false;
		if (Allowed(Crisis.Raid))
		{
			// once a raid started it lasts while any active enemy is left, so
			// it does not flicker when the number of attackers goes 3 → 2 → 3
			raidOn.TryGetValue(map, out bool was);
			raid = was ? GenHostility.AnyHostileActiveThreatToPlayer(map) : UnderAttack(map);
		}
		raidOn[map] = raid;
		if (raid)
			c |= Crisis.Raid;
		return c;
	}

	public static Crisis Detect(Map map, MapDemand d)
	{
		Crisis c = Urgent(map);
		int colonists = map.mapPawns.FreeColonistsSpawnedCount;
		if (Allowed(Crisis.Medical) && (d.patients >= 2 || (d.patients >= 1 && colonists <= 3)))
			c |= Crisis.Medical;
		if (Allowed(Crisis.Food) && colonists > 0 && d.foodDays < 2f)
			c |= Crisis.Food;
		if (Allowed(Crisis.Winter) && WinterComing(map, d))
			c |= Crisis.Winter;
		Try(ref c, Crisis.Weather, () => ExtremeWeather(map));
		Try(ref c, Crisis.Toxic, () => ToxicAir(map));
		Try(ref c, Crisis.Blight, () => BlightedPlants(map) >= 3);
		Try(ref c, Crisis.Mood, () => LowMood(map));
		Try(ref c, Crisis.Power, () => PowerOut(map));
		Try(ref c, Crisis.Medicine, () => NoMedicine(map, colonists));
		Try(ref c, Crisis.Breakdown, () => BrokenDown(map) > 0);
		DetectMore(map, d, ref c);   // 2.29
		return c;
	}

	private static void Try(ref Crisis c, Crisis flag, Func<bool> test)
	{
		if (!Allowed(flag))
			return;
		try
		{
			if (test())
				c |= flag;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] crisis " + flag + ": " + ex.Message, 0x5e7a0000 + flag.GetHashCode());
		}
	}

	private static bool WinterComing(Map map, MapDemand d)
	{
		try
		{
			Twelfth twelfth = GenLocalDate.Twelfth(map);
			float t = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, (Twelfth)((int)(twelfth + 1) % 12));
			// 2.15: also with empty fields – then the stock-up is hunting and
			// wood. Before, winter preparation needed 15 ripe plants.
			return t < 0f && (d.harvestable >= 5 || d.foodDays < 15f || map.resourceCounter.GetCount(ThingDefOf.WoodLog) < 300);
		}
		catch
		{
			return false;
		}
	}

	// 2.15: fire in the home area or in any zone of the colony (fields,
	// stockpiles), not only in the home area
	private static bool FireInHome(Map map)
	{
		Area_Home home = map.areaManager.Home;
		List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
		for (int i = 0; i < fires.Count; i++)
		{
			Thing f = fires[i];
			if (!f.Spawned)
				continue;
			if (home[f.Position] || map.zoneManager.ZoneAt(f.Position) != null)
				return true;
		}
		return false;
	}

	// 2.15: any human or mechanoid attacker, or at least two hostile animals
	// (one angry squirrel still does not count).
	private static bool UnderAttack(Map map)
	{
		if (!GenHostility.AnyHostileActiveThreatToPlayer(map))
			return false;
		int threats = 0;
		IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			if (p.Downed || p.Faction == Faction.OfPlayer || !p.HostileTo(Faction.OfPlayer))
				continue;
			if (!GenHostility.IsActiveThreatToPlayer(p))
				continue;
			if (!p.RaceProps.Animal)
				return true;
			if (++threats >= 2)
				return true;
		}
		return false;
	}

	private static bool ExtremeWeather(Map map)
	{
		GameConditionManager gm = map.gameConditionManager;
		if (gm.ConditionIsActive(GameConditionDefOf.ColdSnap) || gm.ConditionIsActive(GameConditionDefOf.HeatWave))
			return true;
		float t = map.mapTemperature.OutdoorTemp;
		return t < -25f || t > 45f;
	}

	// 2.15: toxic weather of any name (conditions that make the air or rain
	// harmful – by name or by what they do), and polluted ground or
	// wastepacks in the home area (Biotech)
	private static bool ToxicAir(Map map)
	{
		foreach (GameCondition gc in map.gameConditionManager.ActiveConditions)
		{
			string n = gc.def.defName.ToLowerInvariant();
			if (n.Contains("toxic") || n.Contains("noxious") || n.Contains("acid") || n.Contains("poison") || n.Contains("pollut") || n.Contains("smog") || n.Contains("fallout"))
				return true;
		}
		if (ModsConfig.BiotechActive)
		{
			Area_Home home = map.areaManager.Home;
			int cells = 0, polluted = 0;
			foreach (IntVec3 c in home.ActiveCells)
			{
				cells++;
				if (map.pollutionGrid.IsPolluted(c))
					polluted++;
			}
			if (cells > 0 && polluted * 10 >= cells)
				return true;   // a tenth of the home area is polluted
			ThingDef waste = DefDatabase<ThingDef>.GetNamedSilentFail("Wastepack");
			if (waste != null)
			{
				int n = 0;
				foreach (Thing t in map.listerThings.ThingsOfDef(waste))
					if (t.Spawned && home[t.Position] && !t.IsInAnyStorage())
						n += t.stackCount;
				if (n >= 5)
					return true;
			}
		}
		return false;
	}

	private static int BlightedPlants(Map map)
	{
		int n = 0;
		List<Thing> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);
		for (int i = 0; i < plants.Count; i++)
		{
			if (plants[i] is Plant p && p.Blighted && ++n >= 3)
				break;
		}
		return n;
	}

	// a third of the colonists (at least 2) near a mental break
	private static bool LowMood(Map map)
	{
		List<Pawn> list = map.mapPawns.FreeColonistsSpawned.ToList();
		if (list.Count == 0)
			return false;
		int low = 0;
		foreach (Pawn p in list)
		{
			Need_Mood mood = p.needs?.mood;
			if (mood == null || p.mindState?.mentalBreaker == null)
				continue;
			if (mood.CurLevel < p.mindState.mentalBreaker.BreakThresholdMinor)
				low++;
		}
		return low >= Math.Max(2, Mathf.CeilToInt(list.Count / 3f));
	}

	// A net that had power (generator or battery) but now has none, or whose
	// batteries are empty and keep draining. Solar flare counts too.
	private static bool PowerOut(Map map)
	{
		GameConditionDef flare = DefDatabase<GameConditionDef>.GetNamedSilentFail("SolarFlare");
		if (flare != null && map.gameConditionManager.ConditionIsActive(flare))
			return true;
		foreach (PowerNet net in map.powerNetManager.AllNetsListForReading)
		{
			if (net.powerComps == null || net.powerComps.Count == 0)
				continue;
			bool generator = false, consumer = false;
			foreach (CompPowerTrader pc in net.powerComps)
			{
				if (pc.Props.PowerConsumption < 0f) generator = true;
				else if (pc.Props.PowerConsumption > 0f) consumer = true;
			}
			bool battery = net.batteryComps != null && net.batteryComps.Count > 0;
			if (!consumer || (!generator && !battery))
				continue;
			if (!net.HasActivePowerSource)
				return true;
			if (battery && net.CurrentStoredEnergy() < 1f && net.CurrentEnergyGainRate() < 0f)
				return true;
		}
		return false;
	}

	private static bool NoMedicine(Map map, int colonists)
	{
		if (colonists < 2)
			return false;
		int count = 0;
		Area_Home home = map.areaManager.Home;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine))
		{
			if (t.IsForbidden(Faction.OfPlayer) || (!t.IsInAnyStorage() && !home[t.Position]))
				continue;
			count += t.stackCount;
		}
		return count < Math.Max(2, colonists / 2);
	}

	private static int BrokenDown(Map map)
	{
		int n = 0;
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
			if (b.IsBrokenDown())
				n++;
		return n;
	}
}
