using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 2.20: situations. Finer than crises: each one is a concrete job on the map
// (40 blueprints, 3 patients, a trade caravan, hungry prisoners …). In the
// Balanced style a situation REWRITES the Work tab for the jobs it needs:
// it says "this many colonists on priority N" and the planner raises the
// best-suited colonists (skill, passion, speed) to that level – so a big job
// is not left to one colonist. The number grows with the size of the job.
// When the situation is over, the normal plan comes back.
//
// New values are appended only (the names are saved).
// ---------------------------------------------------------------------------
public enum Situation
{
	BuildBacklog,
	Deconstruct,
	Repairs,
	HaulBacklog,
	RottingOutside,
	FoodSpoiling,
	BigHarvest,
	FrostHarvest,
	CutBacklog,
	MineBacklog,
	LowWood,
	LowSteel,
	LowComponents,
	LowMedicine,
	ColdClothing,
	WornClothing,
	LowWeapons,
	Recruit,
	HungryPrisoners,
	MassCasualties,
	Infection,
	TameTrain,
	AnimalProducts,
	AnimalsHungry,
	DirtyRooms,
	ResearchIdle,
	TradeVisit,
	LowFuel,
	MechThreat,
	MoodSlipping,
	Childcare,
	Pollution,
	Exhausted,
	Corpses,
	// Steward 2.30
	StorageFull,
	SowingBacklog,
	NoBedroom,
	UglyRooms,
	LowJoy,
	Slaughter,
	BarrelsReady,
	TextilePile,
	WornGear,
	CaravanPrep,
	Gathering,
	Guests,
	Snow,
	OrbitalTrade,
	ChunksOnFields,
	IdleBench,
	// Steward 2.33: slaves and prisoners
	SlaveSuppression,
	SlaveRebellionRisk,
	PrisonersHurt,
	PrisonRelease,
	DirtyPrison,
	PrisonBeds,
	// Steward 2.37
	ProductsWaiting,
	// Steward 2.39
	AnimalTraining,
	// Steward 2.51: care for the hurt and the hungry – colonists, slaves and prisoners
	Wounded,
	BedHungry,
	HungryNoMeals,
	// 2.52.3: fresh carcasses wait for the butcher
	ButcherBacklog
}

// one "N colonists on priority L" demand of a situation
public struct SitNeed
{
	public WorkTypeDef work;
	public int level;
	public int workers;
}

public sealed class ActiveSituation
{
	public Situation id;
	public int amount;
	public string reason;
	public readonly List<SitNeed> needs = new List<SitNeed>();
	// Exhausted: the colonists who must not be kept on priority 1
	public List<Pawn> pawns;

	public string Label => SituationRules.Label(id);

	// "Construction ×3 (priority 2), Hauling ×1 (priority 2)"
	public string Staffing()
	{
		if (needs.Count == 0)
			return "";
		return string.Join(", ", needs.Select(n => "CF_SitStaff".Translate(n.work.labelShort.CapitalizeFirst(), n.workers, n.level).ToString()));
	}
}

public static partial class SituationRules
{
	public const int EvalInterval = 2500;

	// Context for one evaluation – the counts several situations share are
	// made once (lazily).
	public sealed class Ctx
	{
		public Map map;
		public Crisis crisis;
		public int n;
		public Area_Home home;

		private List<Thing> haulables;
		public List<Thing> Haulables
		{
			get
			{
				if (haulables == null)
				{
					haulables = new List<Thing>();
					foreach (Thing t in map.listerHaulables.ThingsPotentiallyNeedingHauling())
						if (t != null && t.Spawned && !MapDemand.IsChunk(t) && !ModCompat.NoHaul(t) && !t.IsForbidden(Faction.OfPlayer))
							haulables.Add(t);
				}
				return haulables;
			}
		}

		private int harvestable = -1;
		public int Harvestable
		{
			get
			{
				if (harvestable < 0)
				{
					harvestable = 0;
					foreach (Zone z in map.zoneManager.AllZones)
					{
						if (!(z is Zone_Growing g))
							continue;
						ThingDef want = g.GetPlantDefToGrow();
						foreach (IntVec3 c in g.Cells)
						{
							Plant p = c.GetPlant(map);
							if (p != null && p.HarvestableNow && p.LifeStage == PlantLifeStage.Mature && p.def == want)   // 2.52.3: ripe only, like the growers
								harvestable++;
						}
					}
				}
				return harvestable;
			}
		}

		private float foodDays = -1f;
		public float FoodDays => foodDays >= 0f ? foodDays : (foodDays = MapDemand.FoodDays(map));

		private int patients = -1;
		public int Patients => patients >= 0 ? patients : (patients = MapDemand.CountPatients(map));

		private List<Pawn> colonists;
		public List<Pawn> Colonists => colonists ?? (colonists = map.mapPawns.FreeColonistsSpawned.ToList());

		public int Designations(string defName)
		{
			DesignationDef d = DefDatabase<DesignationDef>.GetNamedSilentFail(defName);
			return d == null ? 0 : map.designationManager.SpawnedDesignationsOfDef(d).Count();
		}

		public void Need(ActiveSituation s, string work, int level, int workers)
		{
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(work);
			Need(s, w, level, workers);
		}

		public void Need(ActiveSituation s, WorkTypeDef w, int level, int workers)
		{
			if (w == null || !w.visible || workers <= 0)
				return;
			s.needs.Add(new SitNeed { work = w, level = level, workers = workers });
		}
	}

	private sealed class Def
	{
		public Situation id;
		public int on = 1;      // starts at this amount …
		public int off = 1;     // … and stays while at least this much is left
		public Func<Ctx, int> measure;
		public Func<Ctx, int, string> reason;
		public Action<Ctx, int, ActiveSituation> staff;
		public bool noRaid;     // not during a raid or a fire
		public bool outdoor;    // also not in toxic air or extreme weather
		public Crisis skipIf;   // a crisis that already covers it
		public bool calm;       // only when nothing else is going on
		public string[] preview;  // "Construction:2" – for the settings tab
	}

	private static int Ceil(float v) => Mathf.CeilToInt(v);

	// 2.37: things lying in the home area (not in storage) that some stockpile would take
	private static int WaitingInHome(Ctx c)
	{
		Pawn hauler = c.Colonists.FirstOrDefault(p => !p.Downed && !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling));
		if (hauler == null || c.home == null)
			return 0;
		int n = 0, seen = 0;
		foreach (Thing t in c.Haulables)
		{
			if (++seen > 200)
				break;
			if (t.def.category != ThingCategory.Item || t is Corpse || t.IsInAnyStorage() || !c.home[t.Position])
				continue;
			if (StoreUtility.TryFindBestBetterStorageFor(t, hauler, c.map, StoragePriority.Unstored, Faction.OfPlayer, out _, out _, false))
				n++;
		}
		return n;
	}

	// 2.39: tame animals whose training can be done right now by somebody skilled enough
	private static int TrainingDue(Ctx c)
	{
		WorkTypeDef handling = DefDatabase<WorkTypeDef>.GetNamedSilentFail("Handling");
		int best = -1;
		foreach (Pawn p in c.Colonists)
			if (!p.Downed && handling != null && !p.WorkTypeIsDisabled(handling) && p.skills != null)
				best = Math.Max(best, p.skills.GetSkill(SkillDefOf.Animals).Level);
		if (best < 0)
			return 0;
		int n = 0;
		foreach (Pawn a in c.map.mapPawns.SpawnedColonyAnimals)
		{
			if (a.Dead || a.Downed || a.training == null || a.RaceProps.animalType == AnimalType.Dryad)
				continue;
			if (a.training.NextTrainableToTrain() == null || TrainableUtility.TrainedTooRecently(a))
				continue;
			if (TrainableUtility.MinimumHandlingSkill(a) > best)
				continue;
			n++;
		}
		return n;
	}

	private static readonly List<Def> defs = new List<Def>
	{
		new Def
		{
			id = Situation.AnimalTraining, on = 1, off = 1, noRaid = true, preview = new[] { "Handling:1" },
			measure = TrainingDue,
			reason = (c, a) => "CF_SitR_AnimalTraining".Translate(a),
			staff = (c, a, s) => c.Need(s, "Handling", 1, Math.Max(1, Ceil(a / 5f)))
		},
		new Def
		{
			id = Situation.BuildBacklog, on = 20, off = 8, noRaid = true, preview = new[] { "Construction:2" },
			measure = c => c.map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Count + c.map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Count,
			reason = (c, a) => "CF_SitR_BuildBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, Math.Max(2, Ceil(a / 15f)))
		},
		new Def
		{
			id = Situation.Deconstruct, on = 15, off = 5, noRaid = true, preview = new[] { "Construction:2" },
			measure = c => MapDemand.ConstructionMarks(c.map),
			reason = (c, a) => "CF_SitR_Deconstruct".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, Math.Max(1, Ceil(a / 20f)))
		},
		new Def
		{
			id = Situation.Repairs, on = 6, off = 2, noRaid = true, preview = new[] { "Construction:2" },
			measure = c => c.map.listerBuildingsRepairable.RepairableBuildings(Faction.OfPlayer).Count,
			reason = (c, a) => "CF_SitR_Repairs".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, Math.Max(1, Ceil(a / 10f)))
		},
		new Def
		{
			id = Situation.HaulBacklog, on = 80, off = 30, outdoor = true, preview = new[] { "Hauling:2" },
			measure = c => c.Haulables.Count,
			reason = (c, a) => "CF_SitR_HaulBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, "Hauling", 2, Math.Max(2, Ceil(a / 50f)))
		},
		// 2.37: things in the home area that have a stockpile to go to but lie around –
		// a few haulers get hauling on priority 2 so they are not left lying for days
		new Def
		{
			id = Situation.ProductsWaiting, on = 5, off = 1, preview = new[] { "Hauling:2" },
			measure = WaitingInHome,
			reason = (c, a) => "CF_SitR_ProductsWaiting".Translate(a),
			staff = (c, a, s) => c.Need(s, "Hauling", 2, Math.Max(1, Ceil(a / 15f)))
		},
		new Def
		{
			id = Situation.RottingOutside, on = 8, off = 3, outdoor = true, preview = new[] { "Hauling:1" },
			measure = RottingOutside,
			reason = (c, a) => "CF_SitR_RottingOutside".Translate(a),
			staff = (c, a, s) => c.Need(s, "Hauling", 1, Math.Max(1, Ceil(a / 10f)))
		},
		new Def
		{
			id = Situation.FoodSpoiling, on = 10, off = 4, preview = new[] { "Cooking:1", "Hauling:2" },
			measure = FoodSpoiling,
			reason = (c, a) => "CF_SitR_FoodSpoiling".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Cooking", 1, Math.Max(1, Ceil(a / 30f)));
				c.Need(s, "Hauling", 2, 1);
			}
		},
		new Def
		{
			id = Situation.BigHarvest, on = 40, off = 15, noRaid = true, preview = new[] { "Growing:2" },
			measure = c => c.Harvestable,
			reason = (c, a) => "CF_SitR_BigHarvest".Translate(a),
			staff = (c, a, s) => c.Need(s, "Growing", 2, Math.Max(2, Ceil(a / 40f)))
		},
		new Def
		{
			id = Situation.FrostHarvest, on = 5, off = 1, noRaid = true, preview = new[] { "Growing:1" },
			measure = c => c.map.mapTemperature.OutdoorTemp < 2f ? c.Harvestable : 0,
			reason = (c, a) => "CF_SitR_FrostHarvest".Translate(a, c.map.mapTemperature.OutdoorTemp.ToStringTemperature("F0")),
			staff = (c, a, s) => c.Need(s, "Growing", 1, Math.Max(2, Ceil(a / 25f)))
		},
		new Def
		{
			id = Situation.CutBacklog, on = 30, off = 10, outdoor = true, preview = new[] { "PlantCutting:2" },
			measure = c => c.Designations("CutPlant") + c.Designations("HarvestPlant"),
			reason = (c, a) => "CF_SitR_CutBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, "PlantCutting", 2, Math.Max(1, Ceil(a / 30f)))
		},
		new Def
		{
			id = Situation.MineBacklog, on = 30, off = 10, noRaid = true, preview = new[] { "Mining:2" },
			measure = c => c.Designations("Mine") + c.Designations("MineVein"),
			reason = (c, a) => "CF_SitR_MineBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, "Mining", 2, Math.Max(1, Ceil(a / 30f)))
		},
		new Def
		{
			id = Situation.LowWood, outdoor = true, preview = new[] { "PlantCutting:2" },
			measure = c =>
			{
				int want = 75 + 25 * c.n;
				int wood = c.map.resourceCounter.GetCount(ThingDefOf.WoodLog);
				return wood < want && c.Designations("CutPlant") > 0 ? want - wood : 0;
			},
			reason = (c, a) => "CF_SitR_LowWood".Translate(c.map.resourceCounter.GetCount(ThingDefOf.WoodLog)),
			staff = (c, a, s) => c.Need(s, "PlantCutting", 2, c.n >= 5 ? 2 : 1)
		},
		new Def
		{
			id = Situation.LowSteel, noRaid = true, preview = new[] { "Mining:2" },
			measure = c =>
			{
				int want = 50 + 20 * c.n;
				int steel = c.map.resourceCounter.GetCount(ThingDefOf.Steel);
				return steel < want && c.Designations("Mine") > 0 ? want - steel : 0;
			},
			reason = (c, a) => "CF_SitR_LowSteel".Translate(c.map.resourceCounter.GetCount(ThingDefOf.Steel)),
			staff = (c, a, s) => c.Need(s, "Mining", 2, c.n >= 5 ? 2 : 1)
		},
		new Def
		{
			id = Situation.LowComponents, preview = new[] { "Crafting:2" },
			measure = c =>
			{
				int want = 3 + c.n / 2;
				int have = c.map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial);
				return have < want && BenchFor(c.map, d => d == ThingDefOf.ComponentIndustrial) != null ? want - have : 0;
			},
			reason = (c, a) => "CF_SitR_LowComponents".Translate(c.map.resourceCounter.GetCount(ThingDefOf.ComponentIndustrial)),
			staff = (c, a, s) => c.Need(s, BenchFor(c.map, d => d == ThingDefOf.ComponentIndustrial), 2, 1)
		},
		new Def
		{
			id = Situation.LowMedicine, skipIf = Crisis.Medicine, preview = new[] { "Crafting:2", "Growing:2" },
			measure = c =>
			{
				if (c.n < 2)
					return 0;
				int have = MedicineCount(c.map);
				int want = 2 * c.n;
				if (have >= want)
					return 0;
				bool bench = BenchFor(c.map, d => d.IsMedicine) != null;
				return bench || GrowsHealroot(c.map) ? want - have : 0;
			},
			reason = (c, a) => "CF_SitR_LowMedicine".Translate(MedicineCount(c.map)),
			staff = (c, a, s) =>
			{
				c.Need(s, BenchFor(c.map, d => d.IsMedicine), 2, 1);
				if (GrowsHealroot(c.map))
					c.Need(s, "Growing", 2, 1);
			}
		},
		new Def
		{
			id = Situation.ColdClothing, preview = new[] { "Tailoring:2" },
			measure = c =>
			{
				float t = c.map.mapTemperature.OutdoorTemp;
				if (t > 10f || BenchFor(c.map, d => d.IsApparel) == null)
					return 0;
				int cold = 0;
				foreach (Pawn p in c.Colonists)
					if (p.GetStatValue(StatDefOf.ComfyTemperatureMin) > t)
						cold++;
				return cold;
			},
			reason = (c, a) => "CF_SitR_ColdClothing".Translate(a, c.map.mapTemperature.OutdoorTemp.ToStringTemperature("F0")),
			staff = (c, a, s) => c.Need(s, BenchFor(c.map, d => d.IsApparel), 2, Math.Max(1, Ceil(a / 4f)))
		},
		new Def
		{
			id = Situation.WornClothing, preview = new[] { "Tailoring:2" },
			measure = c =>
			{
				if (BenchFor(c.map, d => d.IsApparel) == null)
					return 0;
				int worn = 0;
				foreach (Pawn p in c.Colonists)
				{
					if (p.apparel == null)
						continue;
					foreach (Apparel ap in p.apparel.WornApparel)
						if (ap.def.useHitPoints && ap.HitPoints * 2 < ap.MaxHitPoints)
						{
							worn++;
							break;
						}
				}
				return worn >= Math.Max(2, Ceil(c.n / 3f)) ? worn : 0;
			},
			reason = (c, a) => "CF_SitR_WornClothing".Translate(a),
			staff = (c, a, s) => c.Need(s, BenchFor(c.map, d => d.IsApparel), 2, 1)
		},
		new Def
		{
			id = Situation.LowWeapons, preview = new[] { "Smithing:2" },
			measure = c =>
			{
				if (c.n < 3 || BenchFor(c.map, d => d.IsWeapon) == null)
					return 0;
				int unarmed = 0;
				foreach (Pawn p in c.Colonists)
					if (p.DevelopmentalStage.Adult() && p.equipment != null && p.equipment.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent))
						unarmed++;
				return unarmed >= Ceil(c.n / 2f) ? unarmed : 0;
			},
			reason = (c, a) => "CF_SitR_LowWeapons".Translate(a),
			staff = (c, a, s) => c.Need(s, BenchFor(c.map, d => d.IsWeapon), 2, a >= 5 ? 2 : 1)
		},
		new Def
		{
			id = Situation.Recruit, preview = new[] { "Warden:2" },
			measure = Recruitable,
			reason = (c, a) => "CF_SitR_Recruit".Translate(a),
			staff = (c, a, s) => c.Need(s, "Warden", 2, Math.Max(1, Ceil(a / 3f)))
		},
		new Def
		{
			id = Situation.HungryPrisoners, preview = new[] { "Warden:1" },
			measure = c =>
			{
				int hungry = 0;
				foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
					if (p.needs?.food != null && p.needs.food.CurLevelPercentage < 0.25f)
						hungry++;
				return hungry;
			},
			reason = (c, a) => "CF_SitR_HungryPrisoners".Translate(a),
			staff = (c, a, s) => c.Need(s, "Warden", 1, Math.Max(1, Ceil(a / 3f)))
		},
		new Def
		{
			id = Situation.MassCasualties, on = 3, off = 2, preview = new[] { "Doctor:1" },
			measure = c => c.Patients,
			reason = (c, a) => "CF_SitR_MassCasualties".Translate(a),
			staff = (c, a, s) => c.Need(s, "Doctor", 1, Math.Max(2, Ceil(a * 2f / 3f)))
		},
		new Def
		{
			id = Situation.Infection, preview = new[] { "Doctor:1" },
			measure = Infected,
			reason = (c, a) => "CF_SitR_Infection".Translate(a),
			staff = (c, a, s) => c.Need(s, "Doctor", 1, Math.Max(1, a))
		},
		new Def
		{
			id = Situation.TameTrain, on = 4, off = 2, noRaid = true, preview = new[] { "Handling:2" },
			measure = c =>
			{
				// 2.52.3: taming only – training is the AnimalTraining situation (it checks "trained too recently")
				return c.Designations("Tame");
			},
			reason = (c, a) => "CF_SitR_TameTrain".Translate(a),
			staff = (c, a, s) => c.Need(s, "Handling", 2, Math.Max(1, Ceil(a / 6f)))
		},
		new Def
		{
			id = Situation.AnimalProducts, on = 5, off = 2, noRaid = true, preview = new[] { "Handling:2" },
			measure = c =>
			{
				int n = c.Designations("Slaughter");
				foreach (Pawn a in c.map.mapPawns.SpawnedColonyAnimals)
				{
					if (a.Dead || a.Downed)
						continue;
					CompMilkable milk = a.TryGetComp<CompMilkable>();
					if (milk != null && milk.ActiveAndFull)
						n++;
					CompShearable shear = a.TryGetComp<CompShearable>();
					if (shear != null && shear.ActiveAndFull)
						n++;
				}
				return n;
			},
			reason = (c, a) => "CF_SitR_AnimalProducts".Translate(a),
			staff = (c, a, s) => c.Need(s, "Handling", 2, Math.Max(1, Ceil(a / 8f)))
		},
		new Def
		{
			id = Situation.AnimalsHungry, on = 2, off = 1, noRaid = true, preview = new[] { "Hauling:2", "Handling:2" },
			measure = c =>
			{
				int n = 0;
				foreach (Pawn a in c.map.mapPawns.SpawnedColonyAnimals)
					if (!a.Dead && a.needs?.food != null && a.needs.food.CurLevelPercentage < 0.15f)
						n++;
				return n;
			},
			reason = (c, a) => "CF_SitR_AnimalsHungry".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Hauling", 2, 1);
				c.Need(s, "Handling", 2, 1);
			}
		},
		new Def
		{
			id = Situation.DirtyRooms, on = 8, off = 3, preview = new[] { "Cleaning:1" },
			measure = DirtyRooms,
			reason = (c, a) => "CF_SitR_DirtyRooms".Translate(a),
			staff = (c, a, s) => c.Need(s, "Cleaning", 1, Math.Max(1, Ceil(a / 20f)))
		},
		new Def
		{
			id = Situation.ResearchIdle, calm = true, preview = new[] { "Research:2" },
			measure = c =>
			{
				if (Find.ResearchManager == null || Find.ResearchManager.GetProject() == null || c.FoodDays < 6f)
					return 0;
				return c.map.listerBuildings.allBuildingsColonist.Any(b => b is Building_ResearchBench) ? 1 : 0;
			},
			reason = (c, a) => "CF_SitR_ResearchIdle".Translate(Find.ResearchManager.GetProject()?.LabelCap ?? ""),
			staff = (c, a, s) => c.Need(s, "Research", 2, Math.Max(1, Ceil(c.n / 4f)))
		},
		new Def
		{
			id = Situation.TradeVisit, noRaid = true, preview = new[] { "Hauling:2" },
			measure = Traders,
			reason = (c, a) => "CF_SitR_TradeVisit".Translate(),
			staff = (c, a, s) => c.Need(s, "Hauling", 2, 2)
		},
		new Def
		{
			id = Situation.LowFuel, on = 2, off = 1, preview = new[] { "Hauling:1" },
			measure = LowFuel,
			reason = (c, a) => "CF_SitR_LowFuel".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("Refuel", "Hauling"), 1, Math.Max(1, Ceil(a / 6f)))
		},
		new Def
		{
			id = Situation.MechThreat, noRaid = true, preview = new[] { "Construction:2", "Smithing:2" },
			measure = Threats,
			reason = (c, a) => "CF_SitR_MechThreat".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Construction", 2, 2);
				c.Need(s, "Smithing", 2, 1);
			}
		},
		new Def
		{
			id = Situation.MoodSlipping, skipIf = Crisis.Mood, preview = new[] { "Cooking:2", "Art:2", "Cleaning:2" },
			measure = c =>
			{
				int low = 0;
				foreach (Pawn p in c.Colonists)
				{
					Need_Mood m = p.needs?.mood;
					if (m == null || p.mindState?.mentalBreaker == null)
						continue;
					if (m.CurLevel < p.mindState.mentalBreaker.BreakThresholdMinor + 0.08f)
						low++;
				}
				return low >= Math.Max(2, Ceil(c.n / 4f)) ? low : 0;
			},
			reason = (c, a) => "CF_SitR_MoodSlipping".Translate(a),
			staff = (c, a, s) =>
			{
				// 2.49: the work that answers the colony's real causes (hunger, dirty rooms, pain …)
				c.Need(s, "Cooking", 2, 1);
				c.Need(s, "Art", 2, 1);
				c.Need(s, "Cleaning", 2, 1);
			}
		},
		new Def
		{
			id = Situation.Childcare, preview = new[] { "Childcare:2" },
			measure = c =>
			{
				if (!ModsConfig.BiotechActive)
					return 0;
				int babies = 0;
				foreach (Pawn p in c.Colonists)
					if (p.DevelopmentalStage.Baby())
						babies++;
				// 2.37: children who need lessons count too
				return babies + ChildLearning.NeedLessons(c.Colonists);
			},
			reason = (c, a) => "CF_SitR_Childcare".Translate(a),
			staff = (c, a, s) =>
			{
				bool hungry = c.Colonists.Any(p => p.DevelopmentalStage.Baby() && p.needs?.food != null && p.needs.food.CurLevelPercentage < 0.3f);
				// 2.37: a child whose learning is very low – a teacher on priority 1 for a while
				bool lessons = c.Colonists.Any(p => ChildLearning.IsChild(p) && p.needs?.learning != null && ChildLearning.Learning(p) < 0.25f);
				c.Need(s, "Childcare", hungry || lessons ? 1 : 2, Math.Max(1, Ceil(a / 2f)));
			}
		},
		new Def
		{
			id = Situation.Pollution, on = 20, off = 5, outdoor = true, preview = new[] { "Cleaning:2" },
			measure = PollutionToClear,
			reason = (c, a) => "CF_SitR_Pollution".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("ClearPollution", "Cleaning"), 2, Math.Max(1, Ceil(a / 150f)))
		},
		new Def
		{
			id = Situation.Exhausted, preview = new string[0],
			measure = c => ExhaustedPawns(c).Count,
			reason = (c, a) => "CF_SitR_Exhausted".Translate(a),
			staff = (c, a, s) => s.pawns = ExhaustedPawns(c)
		},
		new Def
		{
			id = Situation.Corpses, on = 3, off = 1, noRaid = true, preview = new[] { "Hauling:2" },
			measure = Corpses,
			reason = (c, a) => "CF_SitR_Corpses".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("HaulCorpses", "Hauling"), 2, Math.Max(1, Ceil(a / 6f)))
		},
		// ---- 2.30 ----
		new Def
		{
			id = Situation.StorageFull, on = 15, off = 5, noRaid = true, preview = new[] { "Construction:2" },
			measure = StorageFull,
			reason = (c, a) => "CF_SitR_StorageFull".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, Math.Max(1, Ceil(a / 30f)))
		},
		new Def
		{
			id = Situation.SowingBacklog, on = 40, off = 15, noRaid = true, outdoor = true, preview = new[] { "Growing:2" },
			measure = SowingBacklog,
			reason = (c, a) => "CF_SitR_SowingBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, "Growing", 2, Math.Max(2, Ceil(a / 50f)))
		},
		new Def
		{
			id = Situation.NoBedroom, on = 1, off = 1, noRaid = true, preview = new[] { "Construction:2" },
			measure = c => c.Colonists.Count(p => p.ownership != null && p.ownership.OwnedBed == null && !p.IsQuestLodger()),
			reason = (c, a) => "CF_SitR_NoBedroom".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, Math.Max(1, Ceil(a / 3f)))
		},
		new Def
		{
			id = Situation.UglyRooms, on = 2, off = 1, noRaid = true, calm = true, preview = new[] { "Cleaning:2", "Art:2", "Construction:3" },
			measure = UglyRooms,
			reason = (c, a) => "CF_SitR_UglyRooms".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Cleaning", 2, 1);
				c.Need(s, "Art", 2, 1);
				c.Need(s, "Construction", 3, 1);
			}
		},
		new Def
		{
			id = Situation.LowJoy, on = 2, off = 1, noRaid = true, skipIf = Crisis.Mood, preview = new[] { "Construction:2", "Art:2" },
			measure = c => c.Colonists.Count(p => p.needs?.joy != null && p.needs.joy.CurLevelPercentage < 0.3f),
			reason = (c, a) => "CF_SitR_LowJoy".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Construction", 2, 1);
				c.Need(s, "Art", 2, 1);
			}
		},
		new Def
		{
			id = Situation.Slaughter, on = 3, off = 1, noRaid = true, preview = new[] { "Handling:2" },
			measure = c => c.map.designationManager.SpawnedDesignationsOfDef(DesignationDefOf.Slaughter).Count(),
			reason = (c, a) => "CF_SitR_Slaughter".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("Slaughter", "Handling"), 2, Math.Max(1, Ceil(a / 8f)))
		},
		new Def
		{
			id = Situation.BarrelsReady, on = 2, off = 1, noRaid = true, preview = new[] { "Hauling:2" },
			measure = BarrelsReady,
			reason = (c, a) => "CF_SitR_BarrelsReady".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("TakeBeerOutOfFermentingBarrel", "Hauling"), 2, 1)
		},
		new Def
		{
			id = Situation.TextilePile, on = 400, off = 200, calm = true, preview = new[] { "Tailoring:2" },
			measure = TextilePile,
			reason = (c, a) => "CF_SitR_TextilePile".Translate(a),
			staff = (c, a, s) => c.Need(s, "Tailoring", 2, 1)
		},
		new Def
		{
			id = Situation.WornGear, on = 10, off = 4, calm = true, preview = new[] { "Crafting:2" },
			measure = WornGear,
			reason = (c, a) => "CF_SitR_WornGear".Translate(a),
			staff = (c, a, s) => c.Need(s, "Crafting", 2, 1)
		},
		new Def
		{
			id = Situation.CaravanPrep, on = 1, off = 1, preview = new[] { "Hauling:1", "Cooking:2" },
			measure = c => LordsOf<LordJob_FormAndSendCaravan>(c.map),
			reason = (c, a) => "CF_SitR_CaravanPrep".Translate(),
			staff = (c, a, s) =>
			{
				c.Need(s, "Hauling", 1, 2);
				c.Need(s, "Cooking", 2, 1);
			}
		},
		new Def
		{
			id = Situation.Gathering, on = 1, off = 1, noRaid = true, preview = new[] { "Cooking:2", "Cleaning:2" },
			measure = c => LordsOf<LordJob_Joinable_Party>(c.map) + LordsOf<LordJob_Ritual>(c.map),
			reason = (c, a) => "CF_SitR_Gathering".Translate(),
			staff = (c, a, s) =>
			{
				c.Need(s, "Cooking", 2, 1);
				c.Need(s, "Cleaning", 2, 1);
			}
		},
		new Def
		{
			id = Situation.Guests, on = 2, off = 1, noRaid = true, preview = new[] { "Cooking:2", "Cleaning:2" },
			measure = Guests,
			reason = (c, a) => "CF_SitR_Guests".Translate(a),
			staff = (c, a, s) =>
			{
				c.Need(s, "Cooking", 2, 1);
				c.Need(s, "Cleaning", 2, 1);
			}
		},
		new Def
		{
			id = Situation.Snow, on = 60, off = 20, noRaid = true, preview = new[] { "Cleaning:2" },
			measure = SnowCells,
			reason = (c, a) => "CF_SitR_Snow".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("CleanClearSnow", "Cleaning"), 2, Math.Max(1, Ceil(a / 150f)))
		},
		new Def
		{
			id = Situation.OrbitalTrade, on = 1, off = 1, noRaid = true, preview = new[] { "Hauling:2" },
			measure = OrbitalTrade,
			reason = (c, a) => "CF_SitR_OrbitalTrade".Translate(),
			staff = (c, a, s) => c.Need(s, "Hauling", 2, 1)
		},
		new Def
		{
			id = Situation.ChunksOnFields, on = 5, off = 1, noRaid = true, outdoor = true, preview = new[] { "Hauling:2" },
			measure = ChunksOnFields,
			reason = (c, a) => "CF_SitR_ChunksOnFields".Translate(a),
			staff = (c, a, s) => c.Need(s, "Hauling", 2, 1)
		},
		new Def
		{
			id = Situation.IdleBench, on = 3, off = 1, noRaid = true, preview = new[] { "Crafting:2" },
			measure = c => IdleBenches(c.map, out _),
			reason = (c, a) => "CF_SitR_IdleBench".Translate(a),
			staff = (c, a, s) =>
			{
				IdleBenches(c.map, out WorkTypeDef w);
				c.Need(s, w, 2, 1);
			}
		},
		// ---- 2.33: slaves and prisoners ----
		new Def
		{
			id = Situation.SlaveSuppression, on = 1, off = 1, preview = new[] { "Warden:2" },
			measure = c => LowSuppression(c, 0.35f),
			reason = (c, a) => "CF_SitR_SlaveSuppression".Translate(a),
			// a slave close to rebelling gets a warden on priority 1
			staff = (c, a, s) => c.Need(s, "Warden", LowSuppression(c, 0.15f) > 0 ? 1 : 2, Math.Max(1, Ceil(a / 3f)))
		},
		new Def
		{
			id = Situation.SlaveRebellionRisk, on = 1, off = 1, skipIf = Crisis.PrisonBreak, preview = new[] { "Warden:1" },
			measure = RebellionRisk,
			reason = (c, a) => "CF_SitR_SlaveRebellionRisk".Translate(a),
			staff = (c, a, s) => c.Need(s, "Warden", 1, Math.Max(2, Ceil(a / 3f)))
		},
		new Def
		{
			id = Situation.PrisonersHurt, on = 1, off = 1, preview = new[] { "Doctor:2" },
			measure = PrisonersHurt,
			reason = (c, a) => "CF_SitR_PrisonersHurt".Translate(a),
			staff = (c, a, s) => c.Need(s, "Doctor", 2, Math.Max(1, Ceil(a / 3f)))
		},
		// ---- 2.51: care ----
		new Def
		{
			id = Situation.Wounded, on = 1, off = 1, preview = new[] { "Doctor:1" },
			measure = Wounded,
			reason = (c, a) => "CF_SitR_Wounded".Translate(a),
			staff = (c, a, s) => c.Need(s, "Doctor", 1, a >= 2 ? 2 : 1)
		},
		new Def
		{
			id = Situation.BedHungry, on = 1, off = 1, preview = new[] { "Doctor:1", "Warden:1" },
			measure = c => BedHungry(c, out _, out _),
			reason = (c, a) =>
			{
				BedHungry(c, out int col, out int pri);
				return "CF_SitR_BedHungry".Translate(col, pri);
			},
			staff = (c, a, s) =>
			{
				BedHungry(c, out int col, out int pri);
				if (col > 0) c.Need(s, "Doctor", 1, Math.Max(1, Ceil(col / 3f)));
				if (pri > 0) c.Need(s, "Warden", 1, Math.Max(1, Ceil(pri / 3f)));
			}
		},
		new Def
		{
			id = Situation.HungryNoMeals, on = 1, off = 1, preview = new[] { "Cooking:1" },
			measure = HungryNoMeals,
			reason = (c, a) => "CF_SitR_HungryNoMeals".Translate(a),
			staff = (c, a, s) => c.Need(s, "Cooking", 1, Math.Max(1, Ceil(a / 4f)))
		},
		new Def
		{
			id = Situation.ButcherBacklog, on = 2, off = 1, preview = new[] { "Cooking:2" },
			measure = c => Kitchen.ButcherableCarcasses(c.map, 99),
			reason = (c, a) => "CF_SitR_ButcherBacklog".Translate(a),
			staff = (c, a, s) => c.Need(s, GiverWork("DoBillsButcherFlesh", "Cooking"), 2, Math.Max(1, Ceil(a / 8f)))
		},
		new Def
		{
			id = Situation.PrisonRelease, on = 1, off = 1, noRaid = true, preview = new[] { "Warden:2" },
			measure = PrisonRelease,
			reason = (c, a) => "CF_SitR_PrisonRelease".Translate(a),
			staff = (c, a, s) => c.Need(s, "Warden", 2, 1)
		},
		new Def
		{
			id = Situation.DirtyPrison, on = 1, off = 1, noRaid = true, preview = new[] { "Cleaning:2" },
			measure = DirtyPrison,
			reason = (c, a) => "CF_SitR_DirtyPrison".Translate(a),
			staff = (c, a, s) => c.Need(s, "Cleaning", 2, 1)
		},
		new Def
		{
			id = Situation.PrisonBeds, on = 1, off = 1, noRaid = true, preview = new[] { "Construction:2" },
			measure = MissingPrisonBeds,
			reason = (c, a) => "CF_SitR_PrisonBeds".Translate(a),
			staff = (c, a, s) => c.Need(s, "Construction", 2, 1)
		}
	};

	public static IEnumerable<Situation> All => defs.Select(d => d.id);

	public static int Count => defs.Count;

	public static string Label(Situation s) => ("CF_Sit_" + s).Translate();

	public static bool Allowed(Situation s)
	{
		CrisesSettings st = CrisesMod.S;
		return st != null && st.useSituations && (st.situationOff == null || !st.situationOff.Contains(s.ToString()));
	}

	// situations work in Balanced only (focus styles and My style keep their own rules)
	public static bool Active(ForemanMapComponent comp)
	{
		CrisesSettings st = CrisesMod.S;
		return comp != null && st != null && st.useSituations;
	}

	// "Construction (priority 2), Hauling (priority 1)" for the settings tab
	public static string Describe(Situation s)
	{
		Def d = defs.FirstOrDefault(x => x.id == s);
		if (d == null || d.preview == null || d.preview.Length == 0)
			return "CF_SitDoesExhausted".Translate();
		var parts = new List<string>();
		foreach (string p in d.preview)
		{
			string[] kv = p.Split(':');
			WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(kv[0]);
			if (w == null || !w.visible)
				continue;
			parts.Add("CF_SitDoesWork".Translate(w.labelShort.CapitalizeFirst(), kv[1]));
		}
		return parts.Count == 0 ? (string)"CF_SitDoesNothing".Translate() : string.Join(", ", parts);
	}

	// 3.0.2: the same check spread over several ticks (a few situations at a time) – no hitch
	public sealed class Round
	{
		internal Ctx c;
		internal int idx;
		internal ICollection<Situation> before;
		internal readonly List<ActiveSituation> result = new List<ActiveSituation>();
		internal readonly List<object> calm = new List<object>();
		internal bool raid, bad;
		public List<ActiveSituation> Result => result;
	}

	public static Round Start(Map map, Crisis crisis, ICollection<Situation> before)
	{
		return new Round
		{
			c = new Ctx { map = map, crisis = crisis, n = Math.Max(1, MapDemand.Workers(map)), home = map.areaManager.Home },
			before = before,
			raid = (crisis & (Crisis.Raid | Crisis.Fire)) != 0,
			bad = (crisis & (Crisis.Toxic | Crisis.Weather)) != 0
		};
	}

	// true = finished
	public static bool Step(Round r, int count)
	{
		Crisis crisis = r.c.crisis;
		for (int k = 0; k < count && r.idx < defs.Count; k++, r.idx++)
		{
			Def d = defs[r.idx];
			if (!Allowed(d.id))
				continue;
			if ((d.noRaid || d.outdoor || d.calm) && r.raid)
				continue;
			if (d.outdoor && r.bad)
				continue;
			if (d.skipIf != Crisis.None && (crisis & d.skipIf) != 0)
				continue;
			if (d.calm)
			{
				r.calm.Add(d);
				continue;
			}
			TryAdd(d, r.c, r.before, r.result);
		}
		if (r.idx < defs.Count)
			return false;
		if (r.calm.Count > 0 && crisis == Crisis.None && r.result.Count == 0)
			foreach (object o in r.calm)
				TryAdd((Def)o, r.c, r.before, r.result);
		return true;
	}

	public static List<ActiveSituation> Detect(Map map, Crisis crisis, ICollection<Situation> before)
	{
		var result = new List<ActiveSituation>();
		CrisesSettings st = CrisesMod.S;
		if (map == null || st == null || !st.useSituations)
			return result;
		var c = new Ctx
		{
			map = map,
			crisis = crisis,
			n = Math.Max(1, MapDemand.Workers(map)),
			home = map.areaManager.Home
		};
		bool raid = (crisis & (Crisis.Raid | Crisis.Fire)) != 0;
		bool bad = (crisis & (Crisis.Toxic | Crisis.Weather)) != 0;
		var calmDefs = new List<Def>();
		foreach (Def d in defs)
		{
			if (!Allowed(d.id))
				continue;
			if ((d.noRaid || d.outdoor || d.calm) && raid)
				continue;
			if (d.outdoor && bad)
				continue;
			if (d.skipIf != Crisis.None && (crisis & d.skipIf) != 0)
				continue;
			if (d.calm)
			{
				calmDefs.Add(d);
				continue;
			}
			TryAdd(d, c, before, result);
		}
		// research only when the colony is calm: no crisis, no other situation
		// 2.52.3: every calm situation is tried (before only the last one ever was)
		if (calmDefs.Count > 0 && crisis == Crisis.None && result.Count == 0)
			foreach (Def d in calmDefs)
				TryAdd(d, c, before, result);
		return result;
	}

	private static void TryAdd(Def d, Ctx c, ICollection<Situation> before, List<ActiveSituation> result)
	{
		try
		{
			int a = d.measure(c);
			bool was = before != null && before.Contains(d.id);
			if (a <= 0 || a < (was ? d.off : d.on))
				return;
			var s = new ActiveSituation { id = d.id, amount = a, reason = d.reason(c, a) };
			d.staff(c, a, s);
			if (s.needs.Count == 0 && d.id != Situation.Exhausted)
				return;   // nothing anybody could do about it here
			result.Add(s);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] situation " + d.id + ": " + ex.Message, 0x5e7b0000 + (int)d.id);
		}
	}

	// to see whether a re-plan is needed: which situations and how many workers
	public static string Signature(List<ActiveSituation> list)
	{
		if (list == null || list.Count == 0)
			return "";
		var parts = new List<string>();
		foreach (ActiveSituation s in list)
		{
			string p = s.id.ToString();
			foreach (SitNeed n in s.needs)
				p += "|" + n.work.defName + n.level + "x" + n.workers;
			if (s.pawns != null)
				p += "|" + string.Join(",", s.pawns.Select(x => x.thingIDNumber));
			parts.Add(p);
		}
		return string.Join(";", parts);
	}

	// "• Big construction backlog: 42 blueprints → Construction ×3 (priority 2)"
	public static string Summary(List<ActiveSituation> list)
	{
		if (list == null || list.Count == 0)
			return "";
		var lines = new List<string>();
		foreach (ActiveSituation s in list)
		{
			string line = "• " + s.Label.CapitalizeFirst() + ": " + s.reason;
			string staff = s.Staffing();
			if (!staff.NullOrEmpty())
				line += " → " + staff;
			lines.Add(line);
		}
		return "CF_SitHeader".Translate() + "\n" + string.Join("\n", lines);
	}

	// ---------------------------------------------------------------------
	// helpers
	// ---------------------------------------------------------------------

	private static Dictionary<ThingDef, List<WorkTypeDef>> benchWork;

	// The work type of a bench with a bill (that should be done now) whose
	// product matches – or null when there is no such bill.
	public static WorkTypeDef BenchFor(Map map, Predicate<ThingDef> product)
	{
		if (benchWork == null)
		{
			benchWork = new Dictionary<ThingDef, List<WorkTypeDef>>();
			foreach (WorkGiverDef g in DefDatabase<WorkGiverDef>.AllDefsListForReading)
			{
				if (g?.workType == null || g.fixedBillGiverDefs == null)
					continue;
				foreach (ThingDef b in g.fixedBillGiverDefs)
				{
					if (!benchWork.TryGetValue(b, out var l))
						benchWork[b] = l = new List<WorkTypeDef>();
					if (!l.Contains(g.workType))
						l.Add(g.workType);
				}
			}
		}
		foreach (Building b in map.listerBuildings.allBuildingsColonist)
		{
			if (!(b is IBillGiver g) || g.BillStack == null || !benchWork.TryGetValue(b.def, out var works) || works.Count == 0)
				continue;
			foreach (Bill bill in g.BillStack.Bills)
			{
				if (bill == null || bill.suspended || bill.recipe?.products == null)
					continue;
				bool match = false;
				foreach (ThingDefCountClass p in bill.recipe.products)
					if (p?.thingDef != null && product(p.thingDef))
						match = true;
				if (match && bill.ShouldDoNow())
					return works[0];
			}
		}
		return null;
	}

	private static WorkTypeDef GiverWork(string giver, string fallback)
	{
		WorkGiverDef g = DefDatabase<WorkGiverDef>.GetNamedSilentFail(giver);
		return g?.workType ?? DefDatabase<WorkTypeDef>.GetNamedSilentFail(fallback);
	}

	private static int MedicineCount(Map map)
	{
		int count = 0;
		Area_Home home = map.areaManager.Home;
		foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Medicine))
		{
			if (t.IsForbidden(Faction.OfPlayer) || (!t.IsInAnyStorage() && !home[t.Position]))
				continue;
			count += t.stackCount;
		}
		return count;
	}

	private static bool GrowsHealroot(Map map)
	{
		foreach (Zone z in map.zoneManager.AllZones)
			if (z is Zone_Growing g && g.GetPlantDefToGrow()?.defName == "Plant_Healroot")
				return true;
		return false;
	}

	private static int RottingOutside(Ctx c)
	{
		int n = 0;
		foreach (Thing t in c.Haulables)
		{
			if (t is Corpse || t.IsInAnyStorage())
				continue;
			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot == null || rot.Stage != RotStage.Fresh)
				continue;
			if (rot.TicksUntilRotAtCurrentTemp < 5 * GenDate.TicksPerDay)
				n++;
		}
		return n;
	}

	// raw food (that cooking turns into meals) that rots within 1.5 days
	private static int FoodSpoiling(Ctx c) => SpoilingNutrition(c.map);

	public static int SpoilingNutrition(Map map)
	{
		float nutrition = 0f;
		Area_Home home = map.areaManager.Home;
		List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.FoodSourceNotPlantOrTree);
		for (int i = 0; i < list.Count; i++)
		{
			Thing t = list[i];
			if (t is Corpse || !t.Spawned || t.def.ingestible == null || !t.def.ingestible.HumanEdible || t.def.IsDrug)
				continue;
			FoodPreferability pref = t.def.ingestible.preferability;
			if (pref != FoodPreferability.RawBad && pref != FoodPreferability.RawTasty)
				continue;
			if (t.IsForbidden(Faction.OfPlayer) || (!t.IsInAnyStorage() && !home[t.Position]))
				continue;
			CompRottable rot = t.TryGetComp<CompRottable>();
			if (rot == null || rot.Stage != RotStage.Fresh || rot.TicksUntilRotAtCurrentTemp > 90000)
				continue;
			nutrition += t.GetStatValue(StatDefOf.Nutrition) * t.stackCount;
		}
		return Mathf.FloorToInt(nutrition);
	}

	private static int Recruitable(Ctx c)
	{
		int n = 0;
		PrisonerInteractionModeDef convert = DefDatabase<PrisonerInteractionModeDef>.GetNamedSilentFail("Convert");
		foreach (Pawn p in c.map.mapPawns.PrisonersOfColonySpawned)
		{
			if (p.guest == null || p.Downed)
				continue;
			string mode = p.guest.ExclusiveInteractionMode?.defName ?? "";
			bool talk = mode == "AttemptRecruit" || mode == "ReduceResistance" || mode == "Enslave" || mode == "ReduceWill";
			if (!talk && convert != null)
			{
				try { talk = p.guest.IsInteractionEnabled(convert); } catch { }
			}
			if (talk)
				n++;
		}
		return n;
	}

	// a disease or wound that can kill – lethal hediffs that are not immune
	// yet and already serious, or heavy bleeding
	private static int Infected(Ctx c)
	{
		int n = 0;
		var seen = new HashSet<int>();
		void Check(IEnumerable<Pawn> pawns)
		{
			foreach (Pawn p in pawns)
			{
				if (p?.health?.hediffSet == null || !seen.Add(p.thingIDNumber))
					continue;
				bool danger = p.health.hediffSet.BleedRateTotal >= 0.4f;
				if (!danger)
				{
					foreach (Hediff h in p.health.hediffSet.hediffs)
					{
						if (!h.Visible || h.def.lethalSeverity <= 0f || h.FullyImmune() || !(h.TendableNow() || h.TryGetComp<HediffComp_Immunizable>() != null))   // 2.52.3: only what a doctor can treat
							continue;
						if (h.Severity >= h.def.lethalSeverity * 0.3f)
						{
							danger = true;
							break;
						}
					}
				}
				if (danger)
					n++;
			}
		}
		Check(c.map.mapPawns.FreeColonistsAndPrisonersSpawned);
		Check(c.map.mapPawns.SlavesOfColonySpawned);
		return n;
	}

	private static int DirtyRooms(Ctx c)
	{
		RoomRoleDef hospital = DefDatabase<RoomRoleDef>.GetNamedSilentFail("Hospital");
		RoomRoleDef kitchen = DefDatabase<RoomRoleDef>.GetNamedSilentFail("Kitchen");
		int n = 0;
		foreach (Thing f in c.map.listerFilthInHomeArea.FilthInHomeArea)
		{
			if (f == null || !f.Spawned)
				continue;
			Room r = f.GetRoom();
			if (r == null || r.PsychologicallyOutdoors)
				continue;
			RoomRoleDef role = r.Role;
			if (role != null && (role == hospital || role == kitchen))
				n++;
		}
		return n;
	}

	private static int Traders(Ctx c)
	{
		foreach (Lord l in c.map.lordManager.lords)
			if (l?.LordJob is LordJob_TradeWithColony)
				return 1;
		if (c.map.passingShipManager.passingShips.Count > 0 && c.map.listerBuildings.allBuildingsColonist.Any(b => b is Building_OrbitalTradeBeacon))
			return 1;
		return 0;
	}

	private static int LowFuel(Ctx c)
	{
		int n = 0;
		foreach (Building b in c.map.listerBuildings.allBuildingsColonist)
		{
			CompRefuelable r = b.TryGetComp<CompRefuelable>();
			if (r == null || !r.allowAutoRefuel || r.Props.fuelIsMortarBarrel)
				continue;
			if (r.FuelPercentOfMax < 0.25f && !b.IsForbidden(Faction.OfPlayer))
				n++;
		}
		return n;
	}

	private static int Threats(Ctx c)
	{
		int n = 0;
		Faction mechs = Faction.OfMechanoids;
		if (mechs != null)
		{
			foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial))
				if (t.Faction == mechs && t.Spawned)
					n++;
		}
		ThingDef hive = DefDatabase<ThingDef>.GetNamedSilentFail("Hive");
		if (hive != null)
			n += c.map.listerThings.ThingsOfDef(hive).Count;
		return n;
	}

	private static int PollutionToClear(Ctx c)
	{
		if (!ModsConfig.BiotechActive)
			return 0;
		Area area = c.map.areaManager.PollutionClear;
		if (area == null)
			return 0;
		int n = 0;
		foreach (IntVec3 cell in area.ActiveCells)
			if (c.map.pollutionGrid.IsPolluted(cell))
				n++;
		return n;
	}

	private static List<Pawn> ExhaustedPawns(Ctx c)
	{
		var list = new List<Pawn>();
		foreach (Pawn p in c.Colonists)
		{
			Need_Rest rest = p.needs?.rest;
			if (rest == null || p.Downed || (p.jobs?.curDriver != null && p.jobs.curDriver.asleep))
				continue;
			if (rest.CurLevel < 0.15f)
				list.Add(p);
		}
		return list;
	}

	public static bool IsTired(Pawn p)
	{
		Need_Rest rest = p?.needs?.rest;
		return rest != null && rest.CurLevel < 0.2f;
	}

	private static int Corpses(Ctx c)
	{
		int n = 0;
		foreach (Thing t in c.map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse))
		{
			if (!(t is Corpse corpse) || !t.Spawned || t.IsInAnyStorage() || !c.home[t.Position] || t.IsForbidden(Faction.OfPlayer))
				continue;
			if (corpse.InnerPawn != null)
				n++;
		}
		return n;
	}
}
