using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoTurrets
{
	public class ATSettings : ModSettings
	{
		public bool autoManned = true;
		public bool autoMortars = true;
		public bool mortarSafety = true;
		public float safetyExtra = 4f;
		public bool loadShells = true;
		public bool anyMetal = true;
		public bool allowSilver = false;
		public bool friendlyFire = true;     // 1.1.0: false = your turrets never hit your own people (bullets and blasts)
		public bool fuelWood = false;        // 1.1.0: turrets can also be refilled with wood
		public bool fuelStone = false;       // 1.1.0: … and with stone blocks

		public override void ExposeData()
		{
			Scribe_Values.Look(ref autoManned, "autoManned", true);
			Scribe_Values.Look(ref autoMortars, "autoMortars", true);
			Scribe_Values.Look(ref mortarSafety, "mortarSafety", true);
			Scribe_Values.Look(ref safetyExtra, "safetyExtra", 4f);
			Scribe_Values.Look(ref loadShells, "loadShells", true);
			Scribe_Values.Look(ref anyMetal, "anyMetal", true);
			Scribe_Values.Look(ref allowSilver, "allowSilver", false);
			Scribe_Values.Look(ref friendlyFire, "friendlyFire", true);
			Scribe_Values.Look(ref fuelWood, "fuelWood", false);
			Scribe_Values.Look(ref fuelStone, "fuelStone", false);
		}
	}

	public class ATMod : Mod
	{
		public static ATSettings S;

		public ATMod(ModContentPack content) : base(content)
		{
			S = GetSettings<ATSettings>();
			new Harmony("bolek98.autoturrets").PatchAll();
		}

		public override string SettingsCategory() => "Auto Turrets";

		public override void DoSettingsWindowContents(Rect r)
		{
			var l = new Listing_Standard();
			l.Begin(r);
			l.CheckboxLabeled("AT_AutoManned".Translate(), ref S.autoManned, "AT_AutoManned_Desc".Translate());
			l.CheckboxLabeled("AT_AutoMortars".Translate(), ref S.autoMortars, "AT_AutoMortars_Desc".Translate());
			l.CheckboxLabeled("AT_MortarSafety".Translate(), ref S.mortarSafety, "AT_MortarSafety_Desc".Translate());
			if (S.mortarSafety)
			{
				l.Label("AT_SafetyExtra".Translate(S.safetyExtra.ToString("0")));
				S.safetyExtra = Mathf.Round(l.Slider(S.safetyExtra, 0f, 15f));
			}
			l.CheckboxLabeled("AT_LoadShells".Translate(), ref S.loadShells, "AT_LoadShells_Desc".Translate());
			l.CheckboxLabeled("AT_FriendlyFire".Translate(), ref S.friendlyFire, "AT_FriendlyFire_Desc".Translate());
			l.GapLine();
			l.CheckboxLabeled("AT_AnyMetal".Translate(), ref S.anyMetal, "AT_AnyMetal_Desc".Translate());
			l.CheckboxLabeled("AT_AllowSilver".Translate(), ref S.allowSilver);
			l.CheckboxLabeled("AT_FuelWood".Translate(), ref S.fuelWood, "AT_FuelWood_Desc".Translate());
			l.CheckboxLabeled("AT_FuelStone".Translate(), ref S.fuelStone, "AT_FuelStone_Desc".Translate());
			l.Label("AT_Restart".Translate());
			l.End();
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			if (Current.ProgramState == ProgramState.Playing && Find.Maps != null)
				foreach (Map m in Find.Maps)
					foreach (Building b in m.listerBuildings.allBuildingsColonist.ToList())
						if (b is Building_TurretGun t)
							Auto.Refresh(t);
		}
	}

	public static class Auto
	{
		public static readonly AccessTools.FieldRef<Building_TurretGun, CompMannable> mannable =
			AccessTools.FieldRefAccess<Building_TurretGun, CompMannable>("mannableComp");

		public static bool IsMortar(Building_TurretGun t)
		{
			try
			{
				return t.gun != null && t.gun.TryGetComp<CompChangeableProjectile>() != null;
			}
			catch
			{
				return false;
			}
		}

		// should this turret fire on its own?
		public static bool Wants(Building_TurretGun t)
		{
			if (ATMod.S == null || t.Faction != Faction.OfPlayer || t.GetComp<CompMannable>() == null)
				return false;
			return IsMortar(t) ? ATMod.S.autoMortars : ATMod.S.autoManned;
		}

		// is it one of ours right now (a manned turret that fires alone)?
		public static bool IsAuto(Building_TurretGun t) => mannable(t) == null && t.GetComp<CompMannable>() != null && t.Faction == Faction.OfPlayer;

		public static void Refresh(Building_TurretGun t)
		{
			if (t == null || !t.Spawned)
				return;
			CompMannable c = t.GetComp<CompMannable>();
			if (c == null)
				return;
			mannable(t) = Wants(t) ? null : c;
		}
	}

	[HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.SpawnSetup))]
	public static class Patch_SpawnSetup
	{
		public static void Postfix(Building_TurretGun __instance) => Auto.Refresh(__instance);
	}

	[HarmonyPatch(typeof(Building), nameof(Building.SetFaction))]
	public static class Patch_SetFaction
	{
		public static void Postfix(Thing __instance)
		{
			if (__instance is Building_TurretGun t)
				Auto.Refresh(t);
		}
	}

	// you can still aim them by hand
	[HarmonyPatch(typeof(Building_TurretGun), "CanSetForcedTarget", MethodType.Getter)]
	public static class Patch_CanSetForcedTarget
	{
		public static void Postfix(Building_TurretGun __instance, ref bool __result)
		{
			if (!__result && Auto.IsAuto(__instance))
				__result = true;
		}
	}

	// a mortar alone does not drop shells next to your own people
	[HarmonyPatch(typeof(Building_TurretGun), "IsValidTarget")]
	public static class Patch_IsValidTarget
	{
		public static void Postfix(Building_TurretGun __instance, Thing t, ref bool __result)
		{
			if (!__result || ATMod.S == null || !ATMod.S.mortarSafety || !Auto.IsAuto(__instance))
				return;
			try
			{
				Verb v = __instance.AttackVerb;
				if (v == null || !v.ProjectileFliesOverhead())
					return;
				float radius = v.verbProps.ForcedMissRadius + ATMod.S.safetyExtra + 2f;
				float r2 = radius * radius;
				IntVec3 c = t.Position;
				foreach (Pawn p in __instance.Map.mapPawns.AllPawnsSpawned)
				{
					if (p.Dead || p == t)
						continue;
					if ((p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer) && p.Position.DistanceToSquared(c) <= r2)
					{
						__result = false;
						return;
					}
				}
			}
			catch
			{
			}
		}
	}

	// colonists carry shells to mortars that fire on their own
	public class WorkGiver_LoadShell : WorkGiver_Scanner
	{
		public override PathEndMode PathEndMode => PathEndMode.Touch;

		public override bool ShouldSkip(Pawn pawn, bool forced = false) => ATMod.S == null || !ATMod.S.loadShells;

		public override IEnumerable<Thing> PotentialWorkThingsGlobal(Pawn pawn)
		{
			List<Building> list = pawn.Map.listerBuildings.allBuildingsColonist;
			for (int i = 0; i < list.Count; i++)
				if (list[i] is Building_TurretGun t && Needs(t))
					yield return t;
		}

		public static bool Needs(Building_TurretGun t)
		{
			if (!Auto.IsAuto(t))
				return false;
			CompChangeableProjectile c = t.gun?.TryGetComp<CompChangeableProjectile>();
			return c != null && !c.Loaded;
		}

		public static Thing FindShell(Pawn pawn, Building_TurretGun t)
		{
			StorageSettings allowed = t.gun.TryGetComp<CompChangeableProjectile>().allowedShellsSettings;
			return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.ClosestTouch,
				TraverseParms.For(pawn), 9999f,
				x => !x.IsForbidden(pawn) && pawn.CanReserve(x, 10, 1) && (allowed == null || allowed.AllowedToAccept(x)));
		}

		public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
		{
			if (!(thing is Building_TurretGun t) || !Needs(t))
				return false;
			if (t.IsBurning() || !pawn.CanReserve(t, 1, -1, null, forced))
				return false;
			if (FindShell(pawn, t) == null)
			{
				JobFailReason.Is("AT_NoShell".Translate());
				return false;
			}
			return true;
		}

		public override Job JobOnThing(Pawn pawn, Thing thing, bool forced = false)
		{
			var t = (Building_TurretGun)thing;
			Thing shell = FindShell(pawn, t);
			if (shell == null)
				return null;
			Job job = JobMaker.MakeJob(DefDatabase<JobDef>.GetNamed("AT_LoadShell"), t, shell);
			job.count = 1;
			return job;
		}
	}

	public class JobDriver_LoadShell : JobDriver
	{
		private Building_TurretGun Turret => (Building_TurretGun)job.GetTarget(TargetIndex.A).Thing;

		public override bool TryMakePreToilReservations(bool errorOnFailed)
		{
			return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, -1, null, errorOnFailed)
				&& pawn.Reserve(job.GetTarget(TargetIndex.B), job, 10, 1, null, errorOnFailed);
		}

		protected override IEnumerable<Toil> MakeNewToils()
		{
			this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
			this.FailOn(() => !WorkGiver_LoadShell.Needs(Turret));
			yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.ClosestTouch).FailOnDespawnedNullOrForbidden(TargetIndex.B).FailOnSomeonePhysicallyInteracting(TargetIndex.B);
			yield return Toils_Haul.StartCarryThing(TargetIndex.B);
			yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
			Toil load = ToilMaker.MakeToil("AT_Load");
			load.initAction = () =>
			{
				Thing carried = pawn.carryTracker.CarriedThing;
				CompChangeableProjectile c = Turret.gun?.TryGetComp<CompChangeableProjectile>();
				if (carried == null || c == null || c.Loaded)
					return;
				c.LoadShell(carried.def, 1);
				carried.SplitOff(1).Destroy();
			};
			load.defaultCompleteMode = ToilCompleteMode.Instant;
			yield return load;
		}
	}

	// refuelable turrets that take one metal (steel barrels …) take any metal
	[StaticConstructorOnStartup]
	public static class MetalFuel
	{
		static MetalFuel()
		{
			try
			{
				Run();
			}
			catch (Exception e)
			{
				Log.Error("[Auto Turrets] metal fuel: " + e);
			}
		}

		private static bool Metal(ThingDef d) =>
			d.stuffProps?.categories != null && d.stuffProps.categories.Contains(StuffCategoryDefOf.Metallic) && !d.IsCorpse && !d.destroyOnDrop;

		private static bool Cat(ThingDef d, StuffCategoryDef c) =>
			c != null && d.stuffProps?.categories != null && d.stuffProps.categories.Contains(c) && !d.IsCorpse && !d.destroyOnDrop;

		private static void Run()
		{
			ATSettings s = ATMod.S;
			if (s == null || (!s.anyMetal && !s.fuelWood && !s.fuelStone))
				return;
			// 1.1.0: besides any metal also wood and/or stone blocks, as you choose
			List<ThingDef> metals = DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d.category == ThingCategory.Item && (
					(s.anyMetal && Metal(d) && (s.allowSilver || d != ThingDefOf.Silver))
					|| (s.fuelWood && Cat(d, StuffCategoryDefOf.Woody))
					|| (s.fuelStone && Cat(d, StuffCategoryDefOf.Stony))))
				.ToList();
			int n = 0;
			foreach (ThingDef def in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (def.thingClass == null || !typeof(Building_Turret).IsAssignableFrom(def.thingClass) || def.comps == null)
					continue;
				foreach (CompProperties cp in def.comps)
				{
					if (!(cp is CompProperties_Refuelable rp) || rp.fuelFilter == null)
						continue;
					List<ThingDef> allowed = rp.fuelFilter.AllowedThingDefs.ToList();
					if (allowed.Count == 0 || !allowed.All(Metal))
						continue;
					foreach (ThingDef m in metals)
						rp.fuelFilter.SetAllow(m, true);
					n++;
				}
			}
			Log.Message("[Auto Turrets] " + n + " turrets take any metal (" + metals.Count + " metals).");
		}
	}

	// 1.1.0: friendly fire switch – with it off, bullets from your turrets fly past your own people and allies,
	// and blasts from their shells do not hurt them
	public static class FriendlyFire
	{
		public static bool Off => ATMod.S != null && !ATMod.S.friendlyFire;

		public static bool OurTurret(Thing t) => t is Building_Turret b && b.Faction == Faction.OfPlayer;

		public static bool Friendly(Thing t)
		{
			if (!(t is Pawn p) || p.Dead)
				return false;
			if (p.Faction == Faction.OfPlayer || p.HostFaction == Faction.OfPlayer)
				return true;
			return p.Faction != null && !p.Faction.HostileTo(Faction.OfPlayer) && !p.InMentalState;
		}
	}

	[HarmonyPatch(typeof(Projectile), "CanHit")]
	public static class Patch_ProjectileCanHit
	{
		public static void Postfix(Thing thing, ref bool __result, Thing ___launcher)
		{
			if (__result && FriendlyFire.Off && FriendlyFire.OurTurret(___launcher) && FriendlyFire.Friendly(thing))
				__result = false;
		}
	}

	[HarmonyPatch(typeof(DamageWorker), nameof(DamageWorker.ExplosionAffectCell))]
	public static class Patch_ExplosionFriendly
	{
		public static void Prefix(Explosion explosion, IntVec3 c, ref List<Thing> ignoredThings)
		{
			try
			{
				if (!FriendlyFire.Off || explosion?.Map == null || !FriendlyFire.OurTurret(explosion.instigator))
					return;
				List<Thing> here = c.GetThingList(explosion.Map);
				for (int i = 0; i < here.Count; i++)
				{
					if (!FriendlyFire.Friendly(here[i]))
						continue;
					if (ignoredThings == null)
						ignoredThings = new List<Thing>();
					if (!ignoredThings.Contains(here[i]))
						ignoredThings.Add(here[i]);
				}
			}
			catch
			{
			}
		}
	}
}
