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
		public bool wallTurrets = false;     // 1.2.0: picked turrets get a version built over walls
		public Dictionary<string, bool> wallPick = new Dictionary<string, bool>();   // only choices that differ from the recommendation
		public bool smartShells = true;      // 1.3.1: auto mortars pick targets that suit the loaded shell, haulers pick shells that suit the enemy
		public bool noHitBuildings = false;  // 1.3.1: true = your turrets' bullets and shell blasts spare your own buildings
		public bool wallTurretsOutward = true; // 1.4.0: turrets in walls only shoot at targets on the outer side of the wall
		public Dictionary<string, bool> autoPick = new Dictionary<string, bool>();   // 1.5.0: turrets that should NOT fire alone (false); missing = yes
		public List<string> fuelExtra = new List<string>();  // 1.5.0: "turretDef|thingDef" – extra materials a turret is rearmed with
		public bool ammoAlsoMetal = false;     // 1.5.0: turrets that need their own ammo also take any metal
		public bool freeAmmo = false;          // 1.5.0: your turrets need no ammo or fuel at all
		public bool specialOnlyPicked = false;   // 1.5.0: haulers bring special shells (EMP, incendiary, firefoam) only to a mortar set to them
		public bool shellsFromMaterial = true;  // 1.6.0: mortars and artillery are loaded with material instead of shells
		public int shellCost = 10;              // 1.6.0: material for one shell

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
			Scribe_Values.Look(ref wallTurrets, "wallTurrets", false);
			Scribe_Values.Look(ref smartShells, "smartShells", true);
			Scribe_Values.Look(ref noHitBuildings, "noHitBuildings", false);
			Scribe_Values.Look(ref wallTurretsOutward, "wallTurretsOutward", true);
			Scribe_Collections.Look(ref wallPick, "wallPick", LookMode.Value, LookMode.Value);
			Scribe_Collections.Look(ref autoPick, "autoPick", LookMode.Value, LookMode.Value);
			Scribe_Collections.Look(ref fuelExtra, "fuelExtra", LookMode.Value);
			Scribe_Values.Look(ref specialOnlyPicked, "specialOnlyPicked", false);
			Scribe_Values.Look(ref ammoAlsoMetal, "ammoAlsoMetal", false);
			Scribe_Values.Look(ref freeAmmo, "freeAmmo", false);
			Scribe_Values.Look(ref shellsFromMaterial, "shellsFromMaterial", true);
			Scribe_Values.Look(ref shellCost, "shellCost", 10);
			if (autoPick == null) autoPick = new Dictionary<string, bool>();
			if (fuelExtra == null) fuelExtra = new List<string>();
			if (Scribe.mode == LoadSaveMode.PostLoadInit && wallPick == null)
				wallPick = new Dictionary<string, bool>();
		}
	}

	public class ATMod : Mod
	{
		public static ATSettings S;

		public ATMod(ModContentPack content) : base(content)
		{
			S = GetSettings<ATSettings>();
			var h = new Harmony("bolek98.autoturrets");
			h.PatchAll();
			LongEventHandler.ExecuteWhenFinished(() => VfesArtillery.Install(h));   // 1.5.2
			LongEventHandler.ExecuteWhenFinished(() => LoopGuard.Install(h));   // 1.6.1
		}

		public override string SettingsCategory() => "Auto Turrets";

		// 1.2.2: real tabs; every page fits on the screen, the turret list scrolls on its own
		private static int tab;

		public override void DoSettingsWindowContents(Rect r)
		{
			Rect body = new Rect(r.x, r.y + 34f, r.width, r.height - 34f);
			var tabs = new List<TabRecord>
			{
				new TabRecord("AT_SecShooting".Translate(), () => tab = 0, tab == 0),
				new TabRecord("AT_SecMaterials".Translate(), () => tab = 1, tab == 1),
				new TabRecord("AT_TabTurrets".Translate(), () => tab = 3, tab == 3),
				new TabRecord("AT_WallTurrets".Translate(), () => tab = 2, tab == 2),
			};
			Widgets.DrawMenuSection(body);
			TabDrawer.DrawTabs(body, tabs);
			Rect inner = body.ContractedBy(12f);
			bool restart = WallTurrets.RestartNeeded();
			if (restart) inner.height -= 36f;

			if (tab == 2)
				WallTurrets.DrawPage(inner);
			else if (tab == 3)
				TurretPage.DrawTurrets(inner);
			else
			{
				var l = new Listing_Standard { maxOneColumn = true };
				l.Begin(inner);
				if (tab == 0)
				{
					l.CheckboxLabeled("AT_AutoManned".Translate(), ref S.autoManned, "AT_AutoManned_Desc".Translate());
					l.CheckboxLabeled("AT_AutoMortars".Translate(), ref S.autoMortars, "AT_AutoMortars_Desc".Translate());
					l.CheckboxLabeled("AT_MortarSafety".Translate(), ref S.mortarSafety, "AT_MortarSafety_Desc".Translate());
					if (S.mortarSafety)
					{
						l.Label("AT_SafetyExtra".Translate(S.safetyExtra.ToString("0")));
						S.safetyExtra = Mathf.Round(l.Slider(S.safetyExtra, 0f, 15f));
					}
					l.CheckboxLabeled("AT_LoadShells".Translate(), ref S.loadShells, "AT_LoadShells_Desc".Translate());
					l.CheckboxLabeled("AT_SmartShells".Translate(), ref S.smartShells, "AT_SmartShells_Desc".Translate());
					l.CheckboxLabeled("AT_SpecialOnlyPicked".Translate(), ref S.specialOnlyPicked, "AT_SpecialOnlyPicked_Desc".Translate());
					l.CheckboxLabeled("AT_FriendlyFire".Translate(), ref S.friendlyFire, "AT_FriendlyFire_Desc".Translate());
					l.CheckboxLabeled("AT_NoHitBuildings".Translate(), ref S.noHitBuildings, "AT_NoHitBuildings_Desc".Translate());
				}
				else
				{
					l.CheckboxLabeled("AT_AnyMetal".Translate(), ref S.anyMetal, "AT_AnyMetal_Desc".Translate());
					l.CheckboxLabeled("AT_AllowSilver".Translate(), ref S.allowSilver);
					l.CheckboxLabeled("AT_FuelWood".Translate(), ref S.fuelWood, "AT_FuelWood_Desc".Translate());
					l.CheckboxLabeled("AT_FuelStone".Translate(), ref S.fuelStone, "AT_FuelStone_Desc".Translate());
					l.CheckboxLabeled("AT_AmmoAlsoMetal".Translate(), ref S.ammoAlsoMetal, "AT_AmmoAlsoMetal_Desc".Translate());
					l.CheckboxLabeled("AT_FreeAmmo".Translate(), ref S.freeAmmo, "AT_FreeAmmo_Desc".Translate());
					// 1.6.0
					bool wasMat = S.shellsFromMaterial;
					int wasCost = S.shellCost;
					l.CheckboxLabeled("AT_ShellsFromMaterial".Translate(), ref S.shellsFromMaterial, "AT_ShellsFromMaterial_Desc".Translate());
					if (S.shellsFromMaterial)
					{
						l.Label("AT_ShellCost".Translate(S.shellCost));
						S.shellCost = Mathf.RoundToInt(l.Slider(S.shellCost, 1f, 50f));
					}
					if (wasMat != S.shellsFromMaterial || wasCost != S.shellCost) ShellMaterial.Apply();
					l.Gap(4f);
				}
				float used = l.CurHeight;
				l.End();
				// 1.5.0: every turret's materials, also turrets that need their own ammo
				if (tab == 1)
					TurretPage.DrawMaterials(new Rect(inner.x, inner.y + used + 4f, inner.width, inner.height - used - 4f));
			}
			if (restart)
			{
				Rect w = new Rect(inner.x, inner.yMax + 6f, inner.width, 30f);
				Widgets.DrawBoxSolid(w, new Color(0.45f, 0.35f, 0.05f, 0.5f));
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(w, "AT_RestartNeeded".Translate());
				Text.Anchor = TextAnchor.UpperLeft;
			}
		}

		private static void Section(Listing_Standard l, string key)
		{
			l.Gap(10f);
			Text.Font = GameFont.Medium;
			l.Label(key.Translate());
			Text.Font = GameFont.Small;
			Rect line = l.GetRect(2f);
			Widgets.DrawBoxSolid(line, new Color(1f, 1f, 1f, 0.15f));
			l.Gap(6f);
		}

		private static void Small(Listing_Standard l, string text)
		{
			Text.Font = GameFont.Tiny;
			GUI.color = new Color(0.75f, 0.75f, 0.75f);
			l.Label(text);
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			try { TurretPage.ApplyFuel(false); } catch (Exception e) { Log.Warning("[Auto Turrets] materials: " + e.Message); }
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
			// 1.5.0: turrets you chose to keep manned
			if (!TurretPage.AutoOn(t.def))
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
			// 1.5.0: every turret of yours can be given a target, also the automatic ones
			if (!__result && __instance.Faction == Faction.OfPlayer && __instance.GetComp<CompInteractable>() == null)
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
			// 1.5.3: every shell turret of yours, also the manned ones (the crew can fire right away)
			if (t.Faction != Faction.OfPlayer || !TurretControlsComp.AutoLoad(t))
				return false;
			CompChangeableProjectile c = t.gun?.TryGetComp<CompChangeableProjectile>();
			return c != null && !c.Loaded;
		}

		public static Thing FindShell(Pawn pawn, Building_TurretGun t)
		{
			// 1.6.0: material instead of shells
			if (ShellMaterial.On) return ShellMaterial.FindMaterial(pawn, t);
			StorageSettings allowed = t.gun.TryGetComp<CompChangeableProjectile>().allowedShellsSettings;
			Predicate<Thing> ok = x => !x.IsForbidden(pawn) && pawn.CanReserve(x, 10, 1) && (allowed == null || allowed.AllowedToAccept(x));
			// 1.5.0: the shell you picked for this mortar, nothing else
			ThingDef only = TurretControlsComp.Chosen(t);
			if (only != null)
			{
				Thing picked = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(only), PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f, x => !x.IsForbidden(pawn) && pawn.CanReserve(x, 10, 1));
				if (picked != null) return picked;
				// 1.5.3: the picked shell is gone – any allowed one is better than an empty mortar
			}
			// 1.5.0: special shells (EMP, incendiary, firefoam …) only when you pick them
			if (ATMod.S.specialOnlyPicked)
			{
				Predicate<Thing> ok0 = ok;
				ok = x => ok0(x) && SmartShells.KindOfShell(x.def) == SmartShells.Kind.HE;
			}
			// 1.3.1: smart shell choice – first the best kind for the enemies on the map, then any useful kind, then anything allowed
			if (SmartShells.On && !ATMod.S.specialOnlyPicked)
			{
				try
				{
					List<Thing> shells = pawn.Map.listerThings.ThingsInGroup(ThingRequestGroup.Shell);
					for (int tier = 0; tier < 2; tier++)
					{
						int want = tier;
						bool any = false;
						for (int i = 0; i < shells.Count && !any; i++)
							if (SmartShells.Tier(shells[i].def, pawn.Map) <= want && (allowed == null || allowed.AllowedToAccept(shells[i])))
								any = true;
						if (!any)
							continue;
						Thing found = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.ClosestTouch,
							TraverseParms.For(pawn), 9999f, x => SmartShells.Tier(x.def, pawn.Map) <= want && ok(x));
						if (found != null)
							return found;
					}
				}
				catch (Exception e)
				{
					Log.WarningOnce("[Auto Turrets] smart shell choice: " + e.Message, 0x41545331);
				}
			}
			Thing anyShell = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.Shell), PathEndMode.ClosestTouch,
				TraverseParms.For(pawn), 9999f, x => ok(x));
			if (anyShell != null || allowed == null) return anyShell;
			// 1.5.3: modded ammo that the game doesn't count as a shell but the turret accepts
			return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), PathEndMode.ClosestTouch,
				TraverseParms.For(pawn), 9999f, x => x.def.projectileWhenLoaded != null && ok(x));
		}

		public override bool HasJobOnThing(Pawn pawn, Thing thing, bool forced = false)
		{
			if (!(thing is Building_TurretGun t) || !Needs(t))
				return false;
			if (t.IsBurning() || !pawn.CanReserve(t, 1, -1, null, forced))
				return false;
			if (FindShell(pawn, t) == null)
			{
				ShellMaterial.Explain(pawn, t);   // 1.6.1: why nothing was found, once per turret in the log
				JobFailReason.Is(ShellMaterial.On ? "AT_NoMaterial".Translate(ShellMaterial.Cost) : "AT_NoShell".Translate());
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
			job.count = shell.def.projectileWhenLoaded != null ? 1 : Mathf.Min(ShellMaterial.Cost, shell.stackCount);
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
				if (carried.def.projectileWhenLoaded != null)
				{
					c.LoadShell(carried.def, 1);
					carried.SplitOff(1).Destroy();
				}
				else
				{
					// 1.6.0: material becomes the shell this gun should fire
					int need = ShellMaterial.Cost;
					if (carried.stackCount < need)
						return;
					c.LoadShell(ShellMaterial.ShellFor(Turret), 1);
					carried.SplitOff(need).Destroy();
				}
				if (pawn.carryTracker.CarriedThing != null)
					pawn.carryTracker.TryDropCarriedThing(pawn.Position, ThingPlaceMode.Near, out Thing _);
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
			TurretPage.ApplyFuel(true);
			ShellMaterial.Apply();   // 1.6.0
		}

		public static bool IsMetal(ThingDef d) => Metal(d);
		public static bool IsCat(ThingDef d, StuffCategoryDef c) => Cat(d, c);
	}

	// 1.1.0: friendly fire switch – with it off, bullets from your turrets fly past your own people and allies,
	// and blasts from their shells do not hurt them
	public static class FriendlyFire
	{
		public static bool Off => ATMod.S != null && !ATMod.S.friendlyFire;

		// 1.3.1: any building of ours that aims and shoots (vanilla, ancient/odyssey and modded turrets – they all search targets)
		public static bool OurTurret(Thing t) => t is Building b && b.Faction == Faction.OfPlayer && (b is Building_Turret || b is IAttackTargetSearcher);

		// 1.3.1: "turrets don't hit our buildings"
		public static bool SpareBuildings => ATMod.S != null && ATMod.S.noHitBuildings;

		public static bool OurBuilding(Thing t) => t is Building b && b.Faction == Faction.OfPlayer;

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
		public static void Postfix(Projectile __instance, Thing thing, ref bool __result, Thing ___launcher)
		{
			if (!__result || !FriendlyFire.OurTurret(___launcher))
				return;
			if (FriendlyFire.Off && FriendlyFire.Friendly(thing))
				__result = false;
			// 1.3.1: bullets fly past our own buildings unless the building itself was aimed at
			else if (FriendlyFire.SpareBuildings && FriendlyFire.OurBuilding(thing) && thing != __instance.intendedTarget.Thing)
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
					// 1.3.1: also our buildings, when that option is on
				bool people = FriendlyFire.Off, buildings = FriendlyFire.SpareBuildings;
				if ((!people && !buildings) || explosion?.Map == null || !FriendlyFire.OurTurret(explosion.instigator))
					return;
				List<Thing> here = c.GetThingList(explosion.Map);
				for (int i = 0; i < here.Count; i++)
				{
					if (!(people && FriendlyFire.Friendly(here[i])) && !(buildings && FriendlyFire.OurBuilding(here[i])))
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

	// ------------------------------------------------------------------
	// 1.3.1 Smart shell choice: an automatic mortar does not waste a shell on a
	// target it can't hurt (EMP on flesh without a shield, firefoam on anything
	// but a fire, incendiary on mechanoids); with firefoam loaded it puts out
	// fires in the home area instead. Haulers bring the kind that suits the
	// enemies on the map (EMP against mostly mechanoids/shields, otherwise HE).
	// ------------------------------------------------------------------
	public static class SmartShells
	{
		public enum Kind { Other, HE, EMP, Foam, Fire }

		public static bool On => ATMod.S != null && ATMod.S.smartShells;

		public static Kind KindOfShell(ThingDef shell)
		{
			DamageDef d = shell?.projectileWhenLoaded?.projectile?.damageDef;
			if (d == null) return Kind.Other;
			if (d == DamageDefOf.EMP) return Kind.EMP;
			if (d == DamageDefOf.Extinguish) return Kind.Foam;
			if (d == DamageDefOf.Flame) return Kind.Fire;
			if (d == DamageDefOf.Bomb) return Kind.HE;
			return Kind.Other;
		}

		public static Kind Loaded(Building_TurretGun t)
		{
			CompChangeableProjectile c = t.gun?.TryGetComp<CompChangeableProjectile>();
			return c != null && c.Loaded ? KindOfShell(c.LoadedShell) : Kind.Other;
		}

		public static bool Shielded(Pawn p)
		{
			if (p.apparel != null)
			{
				List<Apparel> worn = p.apparel.WornApparel;
				for (int i = 0; i < worn.Count; i++)
					if (worn[i].TryGetComp<CompShield>() != null)
						return true;
			}
			return p.TryGetComp<CompShield>() != null || p.TryGetComp<CompProjectileInterceptor>() != null;
		}

		// is this shell kind pointless against this target?
		public static bool Pointless(Kind k, Thing t)
		{
			switch (k)
			{
				case Kind.Foam:
					return true;   // never at enemies – only at fires (see Patch_FindFireTarget)
				case Kind.EMP:
					return t is Pawn p && !p.RaceProps.IsMechanoid && !Shielded(p);
				case Kind.Fire:
					return t is Pawn q && q.RaceProps.IsMechanoid;
				default:
					return false;
			}
		}

		// what the enemies on the map are, refreshed every few seconds
		private static int cacheMap = -1, cacheTick = -9999;
		private static bool cacheMechs, cacheAnyMech;

		private static void Scan(Map map)
		{
			int now = Find.TickManager.TicksGame;
			if (map.uniqueID == cacheMap && now - cacheTick < 250 && now >= cacheTick)
				return;
			cacheMap = map.uniqueID;
			cacheTick = now;
			int all = 0, mechs = 0;
			IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				if (p.Dead || p.Downed || p.Faction == null || !p.HostileTo(Faction.OfPlayer))
					continue;
				all++;
				if (p.RaceProps.IsMechanoid || Shielded(p))
					mechs++;
			}
			cacheAnyMech = mechs > 0;
			cacheMechs = all > 0 && mechs * 2 > all;
		}

		// 0 = best kind now, 1 = still useful, 2 = rather not (firefoam, EMP with no mechanoids around)
		public static int Tier(ThingDef shell, Map map)
		{
			Scan(map);
			Kind k = KindOfShell(shell);
			Kind best = cacheMechs ? Kind.EMP : Kind.HE;
			if (k == best) return 0;
			if (k == Kind.Foam || (k == Kind.EMP && !cacheAnyMech)) return 2;
			return 1;
		}
	}

	[HarmonyPatch(typeof(Building_TurretGun), "IsValidTarget")]
	public static class Patch_SmartShellTarget
	{
		public static void Postfix(Building_TurretGun __instance, Thing t, ref bool __result)
		{
			if (!__result || !SmartShells.On || t == null || !Auto.IsAuto(__instance))
				return;
			try
			{
				if (SmartShells.Pointless(SmartShells.Loaded(__instance), t))
					__result = false;
			}
			catch (Exception e)
			{
				Log.WarningOnce("[Auto Turrets] smart shell target: " + e.Message, 0x41545332);
			}
		}
	}

	// with firefoam loaded and nothing else to do, an automatic mortar puts out fires in the home area
	[HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget))]
	public static class Patch_FindFireTarget
	{
		public static void Postfix(Building_TurretGun __instance, ref LocalTargetInfo __result)
		{
			if (__result.IsValid || !SmartShells.On || !Auto.IsAuto(__instance))
				return;
			try
			{
				if (SmartShells.Loaded(__instance) != SmartShells.Kind.Foam)
					return;
				Map map = __instance.Map;
				Verb v = __instance.AttackVerb;
				Area home = map?.areaManager?.Home;
				if (v == null || home == null)
					return;
				List<Thing> fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire);
				Thing best = null;
				float bestD = float.MaxValue;
				for (int i = 0; i < fires.Count && i < 400; i++)
				{
					Thing f = fires[i];
					if (!f.Spawned || !home[f.Position])
						continue;
					RoofDef roof = map.roofGrid.RoofAt(f.Position);
					if (roof != null && roof.isThickRoof)
						continue;
					float d = f.Position.DistanceToSquared(__instance.Position);
					if (d >= bestD || !v.CanHitTarget(f))
						continue;
					best = f;
					bestD = d;
				}
				if (best != null)
					__result = best;
			}
			catch (Exception e)
			{
				Log.WarningOnce("[Auto Turrets] firefoam target: " + e.Message, 0x41545333);
			}
		}
	}
}

namespace AutoTurrets
{
	// 1.5.0: an automatic turret you unticked on the Turrets page does not pick targets by itself –
	// it fires only at the target you give it
	[HarmonyLib.HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget))]
	[HarmonyLib.HarmonyPriority(HarmonyLib.Priority.First)]
	public static class Patch_HoldFire
	{
		public static bool Prefix(Building_TurretGun __instance, ref LocalTargetInfo __result)
		{
			if (!TurretPage.HoldsFire(__instance)) return true;
			__result = LocalTargetInfo.Invalid;
			return false;
		}
	}
}
