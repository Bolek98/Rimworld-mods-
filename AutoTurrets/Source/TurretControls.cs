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
	// ---------------------------------------------------------------------------
	// 1.5.0 buttons on every turret of yours:
	// - "Set forced target" and "Hold fire" on every turret, also the automatic ones.
	// - "Auto rearm" on every turret that is rearmed (the game shows it only on some).
	// - "Auto load shells" on every mortar that fires alone (haulers bring shells).
	// - "Ammo": pick the one shell or material this turret is loaded / rearmed with
	//   (or "anything allowed"). Special shells can also be left out globally.
	// ---------------------------------------------------------------------------
	public class TurretControlsComp : GameComponent
	{
		public Dictionary<int, string> ammo = new Dictionary<int, string>();
		public List<int> noLoad = new List<int>();

		public TurretControlsComp(Game g) { }

		public static TurretControlsComp Get => Current.Game?.GetComponent<TurretControlsComp>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref ammo, "ammo", LookMode.Value, LookMode.Value);
			Scribe_Collections.Look(ref noLoad, "noLoad", LookMode.Value);
			if (ammo == null) ammo = new Dictionary<int, string>();
			if (noLoad == null) noLoad = new List<int>();
		}

		public static ThingDef Chosen(Thing t)
		{
			var c = Get;
			if (c == null || t == null || !c.ammo.TryGetValue(t.thingIDNumber, out string n)) return null;
			return DefDatabase<ThingDef>.GetNamedSilentFail(n);
		}

		public static void Choose(Thing t, ThingDef d)
		{
			var c = Get;
			if (c == null) return;
			if (d == null) c.ammo.Remove(t.thingIDNumber);
			else c.ammo[t.thingIDNumber] = d.defName;
		}

		public static bool AutoLoad(Thing t) => Get == null || !Get.noLoad.Contains(t.thingIDNumber);
	}

	public static class TurretControls
	{
		public static bool IsShellTurret(Building_TurretGun t) => t?.gun?.TryGetComp<CompChangeableProjectile>() != null;

		// the shells this mortar may load (its own shell filter)
		public static List<ThingDef> Shells(Building_TurretGun t)
		{
			CompChangeableProjectile c = t.gun.TryGetComp<CompChangeableProjectile>();
			StorageSettings s = c?.allowedShellsSettings;
			return DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsShell && (s == null || s.filter.Allows(d))).OrderBy(d => d.label).ToList();
		}

		public static List<ThingDef> Fuels(Building_TurretGun t)
		{
			CompRefuelable r = t.GetComp<CompRefuelable>();
			return r?.Props?.fuelFilter == null ? new List<ThingDef>() : r.Props.fuelFilter.AllowedThingDefs.OrderBy(d => d.label).ToList();
		}

		public static IEnumerable<Gizmo> Extra(Building_TurretGun t)
		{
			if (t.Faction != Faction.OfPlayer) yield break;
			bool shells = IsShellTurret(t);
			CompRefuelable fuel = t.GetComp<CompRefuelable>();
			if (shells && Auto.IsAuto(t))
			{
				yield return new Command_Toggle
				{
					defaultLabel = "AT_GizmoAutoLoad".Translate(),
					defaultDesc = "AT_GizmoAutoLoad_Desc".Translate(),
					icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", false) ?? BaseContent.BadTex,
					isActive = () => TurretControlsComp.AutoLoad(t),
					toggleAction = () =>
					{
						var c = TurretControlsComp.Get;
						if (c == null) return;
						if (!c.noLoad.Remove(t.thingIDNumber)) c.noLoad.Add(t.thingIDNumber);
					}
				};
			}
			if (shells || fuel != null)
			{
				ThingDef chosen = TurretControlsComp.Chosen(t);
				yield return new Command_Action
				{
					defaultLabel = "AT_GizmoAmmo".Translate(chosen != null ? chosen.LabelCap.ToString() : "AT_AmmoAny".Translate().ToString()),
					defaultDesc = "AT_GizmoAmmo_Desc".Translate(),
					icon = chosen?.uiIcon ?? (shells ? ThingDefOf.Shell_HighExplosive?.uiIcon : ThingDefOf.Steel.uiIcon) ?? BaseContent.BadTex,
					action = () =>
					{
						var opts = new List<FloatMenuOption> { new FloatMenuOption("AT_AmmoAny".Translate(), () => TurretControlsComp.Choose(t, null)) };
						foreach (ThingDef d in shells ? Shells(t) : Fuels(t))
						{
							ThingDef dd = d;
							opts.Add(new FloatMenuOption(d.LabelCap, () => TurretControlsComp.Choose(t, dd), d.uiIcon ?? BaseContent.ClearTex, Color.white));
						}
						Find.WindowStack.Add(new FloatMenu(opts));
					}
				};
			}
		}
	}

	[HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.GetGizmos))]
	public static class Patch_TurretGizmos
	{
		public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building_TurretGun __instance)
		{
			// 1.5.1: another mod's button that throws must not take ours (and the rest) with it
			IEnumerator<Gizmo> it = null;
			try { it = __result?.GetEnumerator(); } catch (Exception ex) { Log.WarningOnce("[Auto Turrets] buttons of another mod: " + ex.Message, 0x41544732); }
			while (it != null)
			{
				Gizmo cur;
				try
				{
					if (!it.MoveNext()) break;
					cur = it.Current;
				}
				catch (Exception ex)
				{
					Log.WarningOnce("[Auto Turrets] a button of another mod failed on " + __instance.LabelCap + " – skipped: " + ex.Message, 0x41544733 ^ __instance.def.shortHash);
					break;
				}
				yield return cur;
			}
			IEnumerable<Gizmo> extra = null;
			try { extra = TurretControls.Extra(__instance).ToList(); } catch (Exception e) { Log.WarningOnce("[Auto Turrets] buttons: " + e.Message, 0x41544731); }
			if (extra != null) foreach (Gizmo g in extra) yield return g;
		}
	}

	// rearming with the one material you picked
	[HarmonyPatch(typeof(RefuelWorkGiverUtility), "FindBestFuel")]
	public static class Patch_FindBestFuel
	{
		public static bool Prefix(Pawn pawn, Thing refuelable, ref Thing __result)
		{
			ThingDef only = refuelable is Building_TurretGun ? TurretControlsComp.Chosen(refuelable) : null;
			if (only == null) return true;
			__result = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(only), PathEndMode.ClosestTouch, TraverseParms.For(pawn), 9999f,
				x => !x.IsForbidden(pawn) && pawn.CanReserve(x));
			// 1.5.3: the picked ammo is gone – rearm with anything the turret takes instead of staying empty
			return __result == null;
		}
	}

	[HarmonyPatch(typeof(RefuelWorkGiverUtility), "FindAllFuel")]
	public static class Patch_FindAllFuel
	{
		public static bool Prefix(Pawn pawn, Thing refuelable, ref List<Thing> __result)
		{
			ThingDef only = refuelable is Building_TurretGun ? TurretControlsComp.Chosen(refuelable) : null;
			if (only == null) return true;
			int n = refuelable.TryGetComp<CompRefuelable>().GetFuelCountToFullyRefuel();
			__result = RefuelWorkGiverUtility.FindEnoughReservableThings(pawn, refuelable.Position, new IntRange(n, n), x => x.def == only);
			return __result == null;
		}
	}

	// 1.5.0: "Turrets need no ammo" – your turrets always count as loaded and never use it up
	public static class FreeAmmo
	{
		public static bool On(CompRefuelable c) =>
			ATMod.S != null && ATMod.S.freeAmmo && c?.parent is Building_Turret && c.parent.Faction == Faction.OfPlayer;
	}

	[HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.HasFuel), MethodType.Getter)]
	public static class Patch_HasFuel
	{
		public static void Postfix(CompRefuelable __instance, ref bool __result) { if (!__result && FreeAmmo.On(__instance)) __result = true; }
	}

	[HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.IsFull), MethodType.Getter)]
	public static class Patch_IsFull
	{
		public static void Postfix(CompRefuelable __instance, ref bool __result) { if (!__result && FreeAmmo.On(__instance)) __result = true; }
	}

	[HarmonyPatch(typeof(CompRefuelable), nameof(CompRefuelable.ConsumeFuel))]
	public static class Patch_ConsumeFuel
	{
		public static bool Prefix(CompRefuelable __instance) => !FreeAmmo.On(__instance);
	}
}

namespace AutoTurrets
{
	// 1.6.2: "Turrets need no ammo" also for mortars, artillery and field guns (a shell is always loaded and
	// never used up) and for the VFE automated mortar's shell gauge – nobody has to bring anything.
	public class FreeShellsComp : GameComponent
	{
		private static System.Reflection.FieldInfo secFuel, secCap;
		private static System.Type dualProps;
		private static bool looked;

		public FreeShellsComp(Game g) { }

		public override void GameComponentTick()
		{
			if (ATMod.S == null || !ATMod.S.freeAmmo || Find.TickManager.TicksGame % 120 != 0)
				return;
			try
			{
				if (!looked)
				{
					looked = true;
					System.Type comp = GenTypes.GetTypeInAnyAssembly("VEF.Buildings.CompRefuelable_DualFuel");
					dualProps = GenTypes.GetTypeInAnyAssembly("VEF.Buildings.CompProperties_Refuelable_DualFuel");
					if (comp != null) secFuel = HarmonyLib.AccessTools.Field(comp, "secondaryFuel");
					if (dualProps != null) secCap = HarmonyLib.AccessTools.Field(dualProps, "secondaryFuelCapacity");
				}
				foreach (Map m in Find.Maps)
				{
					List<Building> list = m.listerBuildings.allBuildingsColonist;
					for (int i = 0; i < list.Count; i++)
					{
						if (!(list[i] is Building_TurretGun t))
							continue;
						CompChangeableProjectile c = t.gun?.TryGetComp<CompChangeableProjectile>();
						if (c != null && !c.Loaded)
							c.LoadShell(ShellMaterial.ShellFor(t), 1);
						if (secFuel != null && secCap != null)
							foreach (ThingComp tc in t.AllComps)
								if (secFuel.DeclaringType.IsInstanceOfType(tc) && dualProps.IsInstanceOfType(tc.props))
									secFuel.SetValue(tc, System.Convert.ToSingle(secCap.GetValue(tc.props)));
					}
				}
			}
			catch (System.Exception e)
			{
				Log.WarningOnce("[Auto Turrets] free shells: " + e.Message, 0x41545336);
			}
		}
	}

	[HarmonyPatch(typeof(CompChangeableProjectile), nameof(CompChangeableProjectile.Notify_ProjectileLaunched))]
	public static class Patch_FreeShellLaunched
	{
		public static bool Prefix(CompChangeableProjectile __instance)
		{
			if (ATMod.S == null || !ATMod.S.freeAmmo) return true;
			Thing caster = __instance.parent?.TryGetComp<CompEquippable>()?.PrimaryVerb?.caster;
			return !(caster is Building_TurretGun && caster.Faction == Faction.OfPlayer);   // only your own guns keep their shell
		}
	}
}
