using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoTurrets
{
	// 1.6.0: mortars and artillery are loaded with plain material (steel and other metals) instead of shells.
	// - turrets with a shell slot (mortar, VFE artillery, field gun …): a hauler brings N material and the
	//   gun is loaded with the shell you picked for it, otherwise a high-explosive shell
	// - VFE automated mortar (shells as a second fuel): its shell gauge is refilled with material, N per shell
	public static class ShellMaterial
	{
		public static bool On => ATMod.S != null && ATMod.S.shellsFromMaterial;
		public static int Cost => Mathf.Max(1, ATMod.S?.shellCost ?? 10);

		public static bool IsMaterial(ThingDef d) =>
			d != null && d.category == ThingCategory.Item && MetalFuel.IsMetal(d) && (ATMod.S.allowSilver || d != ThingDefOf.Silver);

		// the shell a load of material becomes in this gun
		public static ThingDef ShellFor(Building_TurretGun t)
		{
			StorageSettings s = t.gun?.TryGetComp<CompChangeableProjectile>()?.allowedShellsSettings;
			ThingDef chosen = TurretControlsComp.Chosen(t);
			if (chosen?.projectileWhenLoaded != null) return chosen;
			ThingDef he = ThingDefOf.Shell_HighExplosive;
			if (he != null && (s == null || s.AllowedToAccept(he))) return he;
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
				if (d.projectileWhenLoaded != null && (s == null || s.AllowedToAccept(d)))
					return d;
			return he;
		}

		public static Thing FindMaterial(Pawn pawn, Building_TurretGun t)
		{
			int need = Cost;
			Predicate<Thing> ok = x => IsMaterial(x.def) && x.stackCount >= need && !x.IsForbidden(pawn) && pawn.CanReserve(x, 10, need);
			// steel first, then any other metal
			Thing steel = GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForDef(ThingDefOf.Steel), PathEndMode.ClosestTouch,
				TraverseParms.For(pawn), 9999f, ok);
			if (steel != null) return steel;
			return GenClosest.ClosestThingReachable(pawn.Position, pawn.Map, ThingRequest.ForGroup(ThingRequestGroup.HaulableEver), PathEndMode.ClosestTouch,
				TraverseParms.For(pawn), 9999f, ok);
		}

		// ---------------- VFE automated mortar: shells as the second fuel ----------------
		private class Orig
		{
			public List<ThingDef> defs;
			public float mult;
		}

		private static readonly Dictionary<object, Orig> orig = new Dictionary<object, Orig>();
		private static Type dualType;
		private static FieldInfo filterField, multField;
		private static bool looked;

		public static void Apply()
		{
			try
			{
				if (!looked)
				{
					looked = true;
					dualType = GenTypes.GetTypeInAnyAssembly("VEF.Buildings.CompProperties_Refuelable_DualFuel");
					if (dualType != null)
					{
						filterField = AccessTools.Field(dualType, "secondaryFuelFilter");
						multField = AccessTools.Field(dualType, "secondaryFuelMultiplier");
					}
				}
				if (dualType == null || filterField == null || multField == null || ATMod.S == null) return;
				int n = 0;
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
				{
					if (d.comps == null) continue;
					foreach (CompProperties p in d.comps)
					{
						if (p == null || !dualType.IsInstanceOfType(p)) continue;
						ThingFilter f = filterField.GetValue(p) as ThingFilter;
						if (f == null) continue;
						if (!orig.TryGetValue(p, out Orig o))
						{
							o = new Orig { defs = f.AllowedThingDefs.ToList(), mult = (float)multField.GetValue(p) };
							orig[p] = o;
						}
						if (!o.defs.Any(x => x.projectileWhenLoaded != null)) continue;   // not a shell gauge
						f.SetDisallowAll();
						if (On)
						{
							foreach (ThingDef m in DefDatabase<ThingDef>.AllDefsListForReading)
								if (IsMaterial(m)) f.SetAllow(m, true);
							multField.SetValue(p, o.mult / Cost);
							n++;
						}
						else
						{
							foreach (ThingDef x in o.defs) f.SetAllow(x, true);
							multField.SetValue(p, o.mult);
						}
					}
				}
				if (n > 0) Log.Message("[Auto Turrets] " + n + " turrets with a shell gauge are refilled with material (" + Cost + " per shell).");
			}
			catch (Exception e)
			{
				Log.Warning("[Auto Turrets] shells from material: " + e.Message);
			}
		}
	}
}
