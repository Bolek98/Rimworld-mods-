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
			foreach (Gizmo g in __result) yield return g;
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
			return false;
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
			return false;
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
