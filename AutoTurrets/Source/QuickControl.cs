using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace AutoTurrets
{
	// ---------------------------------------------------------------------------
	// 1.3.0 quick control: switch every turret of yours off / on with one button (on a turret or in
	// the play settings bottom right), switch one turret off, and a "Stun only" fire mode – bullets of
	// such a turret only stun (no wounds), and it skips pawns that are already stunned or down.
	// Explosive guns (mortars, rockets, shells flying overhead) cannot use stun mode.
	// Saved per turret (by thing ID) in a game component, so old saves keep working.
	// ---------------------------------------------------------------------------
	public class QuickControlComp : GameComponent
	{
		public HashSet<int> off = new HashSet<int>();
		public HashSet<int> stun = new HashSet<int>();
		public Dictionary<int, int> kind = new Dictionary<int, int>();   // 1.4.0 ammo type per turret

		public QuickControlComp(Game g) { }

		public static QuickControlComp Get => Current.Game?.GetComponent<QuickControlComp>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref off, "atOff", LookMode.Value);
			Scribe_Collections.Look(ref stun, "atStun", LookMode.Value);
			if (off == null) off = new HashSet<int>();
			if (stun == null) stun = new HashSet<int>();
			Scribe_Collections.Look(ref kind, "atAmmoKind", LookMode.Value, LookMode.Value);
			if (kind == null) kind = new Dictionary<int, int>();
			if (Scribe.mode == LoadSaveMode.PostLoadInit && stun.Count > 0)
			{
				foreach (int id in stun) if (!kind.ContainsKey(id)) kind[id] = (int)AmmoKind.Stun;   // 1.3.0 saves
				stun.Clear();
			}
		}
	}

	public static class QuickControl
	{
		private static readonly Dictionary<ThingDef, bool> explosive = new Dictionary<ThingDef, bool>();
		private static Action<Building_TurretGun> resetTarget;

		public static bool Gizmos => ATMod.S == null || ATMod.S.quickGizmos;
		public static bool GlobalButton => ATMod.S == null || ATMod.S.quickGlobalButton;

		public static bool Mine(Thing t) => t is Building_TurretGun && t.Faction == Faction.OfPlayer && t.Spawned;

		public static bool IsOff(Thing t)
		{
			var c = QuickControlComp.Get;
			return c != null && t != null && c.off.Contains(t.thingIDNumber);
		}

		public static bool IsStun(Thing t)
		{
			var c = QuickControlComp.Get;
			return c != null && t is Building_TurretGun g && c.kind.TryGetValue(t.thingIDNumber, out int k) && k == (int)AmmoKind.Stun && !Explosive(g);
		}

		public static bool Explosive(Building_TurretGun t)
		{
			ThingDef def = t?.def;
			if (def == null) return true;
			if (explosive.TryGetValue(def, out bool v)) return v;
			v = false;
			try
			{
				if (def.building != null && def.building.IsMortar) v = true;
				else if (TurretControls.IsShellTurret(t)) v = true;
				else
				{
					Verb verb = t.AttackVerb;
					ThingDef proj = verb?.GetProjectile();
					if (proj?.projectile != null && (proj.projectile.explosionRadius > 0.5f || proj.projectile.flyOverhead)) v = true;
					if (verb != null && verb.ProjectileFliesOverhead()) v = true;
				}
			}
			catch { v = true; }
			explosive[def] = v;
			return v;
		}

		public static IEnumerable<Building_TurretGun> All(Map map) =>
			map == null ? Enumerable.Empty<Building_TurretGun>() : map.listerBuildings.allBuildingsColonist.OfType<Building_TurretGun>().Where(Mine).ToList();

		public static void SetOff(Building_TurretGun t, bool value)
		{
			var c = QuickControlComp.Get;
			if (c == null || t == null) return;
			if (value)
			{
				c.off.Add(t.thingIDNumber);
				try
				{
					resetTarget ??= AccessTools.MethodDelegate<Action<Building_TurretGun>>(AccessTools.Method(typeof(Building_TurretGun), "ResetCurrentTarget"));
					resetTarget(t);
				}
				catch { }
			}
			else c.off.Remove(t.thingIDNumber);
		}

		public static void SetStun(Building_TurretGun t, bool value)
		{
			var c = QuickControlComp.Get;
			if (c == null || t == null) return;
			if (value && Explosive(t)) return;
			Ammo.Set(t, value ? AmmoKind.Stun : AmmoKind.Normal);
		}

		public static void AllOff(Map map, bool value)
		{
			int n = 0;
			foreach (Building_TurretGun t in All(map)) { SetOff(t, value); n++; }
			Messages.Message((value ? "AT_QC_AllOffMsg" : "AT_QC_AllOnMsg").Translate(n), MessageTypeDefOf.NeutralEvent, false);
		}

		public static void AllStun(Map map, bool value)
		{
			int n = 0, skipped = 0;
			foreach (Building_TurretGun t in All(map))
			{
				if (value && Explosive(t)) { skipped++; continue; }
				SetStun(t, value);
				n++;
			}
			string msg = (value ? "AT_QC_AllStunMsg" : "AT_QC_AllLethalMsg").Translate(n);
			if (skipped > 0) msg += " " + "AT_QC_SkippedExplosive".Translate(skipped);
			Messages.Message(msg, MessageTypeDefOf.NeutralEvent, false);
		}

		private static Texture2D Tex(string path, Texture2D fallback = null) => ContentFinder<Texture2D>.Get(path, false) ?? fallback ?? BaseContent.BadTex;
		public static Texture2D OffTex => Tex("UI/Commands/HoldFire");
		public static Texture2D OnTex => Tex("UI/Commands/Attack", OffTex);
		public static Texture2D StunTex => ContentFinder<Texture2D>.Get("UI/Icons/Medical/Downed", false) ?? ContentFinder<Texture2D>.Get("Things/Mote/StunAnimation", false) ?? OffTex;

		public static IEnumerable<Gizmo> Gizmo(Building_TurretGun t)
		{
			if (!Gizmos || !Mine(t)) yield break;
			Map map = t.Map;
			bool off = IsOff(t);
			yield return new Command_Toggle
			{
				defaultLabel = "AT_QC_This".Translate(),
				defaultDesc = "AT_QC_This_Desc".Translate(),
				icon = off ? OffTex : OnTex,
				isActive = () => !IsOff(t),
				toggleAction = () => SetOff(t, !IsOff(t))
			};
			yield return new Command_Action
			{
				defaultLabel = "AT_QC_All".Translate(),
				defaultDesc = "AT_QC_All_Desc".Translate(),
				icon = OffTex,
				action = () => Find.WindowStack.Add(new FloatMenu(GlobalOptions(map)))
			};
			if (Ammo.On) { yield return Ammo.Gizmo(t); yield break; }
			bool expl = Explosive(t);
			var mode = new Command_Action
			{
				defaultLabel = (IsStun(t) ? "AT_QC_ModeStun" : "AT_QC_ModeLethal").Translate(),
				defaultDesc = "AT_QC_Mode_Desc".Translate(),
				icon = IsStun(t) ? StunTex : OnTex,
				action = () =>
				{
					var opts = new List<FloatMenuOption>
					{
						new FloatMenuOption("AT_QC_SetLethal".Translate(), () => SetStun(t, false)),
						new FloatMenuOption("AT_QC_SetStun".Translate(), () => SetStun(t, true)),
						new FloatMenuOption("AT_QC_AllLethal".Translate(), () => AllStun(map, false)),
						new FloatMenuOption("AT_QC_AllStun".Translate(), () => AllStun(map, true))
					};
					Find.WindowStack.Add(new FloatMenu(opts));
				}
			};
			if (expl) mode.Disable("AT_QC_Explosive".Translate());
			yield return mode;
		}

		public static List<FloatMenuOption> GlobalOptions(Map map)
		{
			var l = new List<FloatMenuOption>
			{
				new FloatMenuOption("AT_QC_AllOff".Translate(), () => AllOff(map, true)),
				new FloatMenuOption("AT_QC_AllOn".Translate(), () => AllOff(map, false)),
				new FloatMenuOption("AT_QC_AllStun".Translate(), () => AllStun(map, true)),
				new FloatMenuOption("AT_QC_AllLethal".Translate(), () => AllStun(map, false))
			};
			if (Ammo.On) l.Add(new FloatMenuOption("AT_AM_AllMenu".Translate(), () => Find.WindowStack.Add(new FloatMenu(Ammo.AllOptions(map)))));
			return l;
		}
	}

	// off turrets do not start shooting at all (also not at a forced target)
	[HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryStartShootSomething))]
	public static class Patch_QC_Off
	{
		public static bool Prefix(Building_TurretGun __instance)
		{
			if (!QuickControl.IsOff(__instance)) return true;
			QuickControl.SetOff(__instance, true);   // also drops the current target
			return false;
		}
	}

	[HarmonyPatch(typeof(Building_TurretGun), "BeginBurst")]
	public static class Patch_QC_OffBurst
	{
		public static bool Prefix(Building_TurretGun __instance) => !QuickControl.IsOff(__instance);
	}

	// stun mode: skip pawns that are already stunned or down
	[HarmonyPatch(typeof(Building_TurretGun), "IsValidTarget")]
	public static class Patch_QC_StunTarget
	{
		public static void Postfix(Building_TurretGun __instance, Thing t, ref bool __result)
		{
			if (!__result || !(t is Pawn p) || !QuickControl.IsStun(__instance)) return;
			if (p.Downed || (p.stances?.stunner != null && p.stances.stunner.Stunned)) __result = false;
		}
	}

	// stun mode damage: see Patch_AmmoDamage (AmmoTypes.cs)

	// small button bottom right (next to the play settings)
	[HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
	public static class Patch_QC_PlaySettings
	{
		public static void Postfix(WidgetRow row, bool worldView)
		{
			try
			{
				if (worldView || row == null || !QuickControl.GlobalButton) return;
				Map map = Find.CurrentMap;
				if (map == null || !QuickControl.All(map).Any()) return;
				if (row.ButtonIcon(QuickControl.OffTex, "AT_QC_Global_Tip".Translate()))
					Find.WindowStack.Add(new FloatMenu(QuickControl.GlobalOptions(map)));
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] play settings button: " + e.Message, 0x41545145); }
		}
	}
}
