using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTurrets
{
	// ---------------------------------------------------------------------------
	// 1.4.0 ammo types: every turret of yours (also modded ones and mortars) can fire another kind of
	// ammo – armor-piercing, hollow-point, incendiary, EMP, stun, toxic; mortars and artillery also
	// smoke and firefoam. The special kinds need no crafting or hauling (the turret refills itself),
	// unless "Special ammo from storage" is on. A gun may fire a kind it normally has no ammo for.
	// Bullets: the kind changes what a hit does (in TakeDamage, so it also works for projectiles of
	// other mods, Combat Extended included – CE's own selected ammo is never changed by "stun").
	// Explosive guns: the kind picks the shell that is loaded (vanilla shells, so the blast is real).
	// ---------------------------------------------------------------------------
	public enum AmmoKind { Normal = 0, ArmorPiercing = 1, HollowPoint = 2, Incendiary = 3, EMP = 4, Stun = 5, Toxic = 6, Smoke = 7, Firefoam = 8 }

	public static class Ammo
	{
		public static readonly AmmoKind[] BulletKinds = { AmmoKind.Normal, AmmoKind.ArmorPiercing, AmmoKind.HollowPoint, AmmoKind.Incendiary, AmmoKind.EMP, AmmoKind.Stun, AmmoKind.Toxic };
		public static readonly AmmoKind[] ShellKinds = { AmmoKind.Normal, AmmoKind.Incendiary, AmmoKind.EMP, AmmoKind.Smoke, AmmoKind.Firefoam, AmmoKind.Toxic };

		public static bool On => ATMod.S == null || ATMod.S.ammoTypes;

		public static AmmoKind Get(Thing t)
		{
			var c = QuickControlComp.Get;
			if (c == null || t == null || !c.kind.TryGetValue(t.thingIDNumber, out int k)) return AmmoKind.Normal;
			if (!On && k != (int)AmmoKind.Stun) return AmmoKind.Normal;   // stun mode works even with ammo types off
			return (AmmoKind)k;
		}

		// special ammo of this turret needs no hauling (the turret refills itself)
		public static bool FreeSpecial(Thing t) =>
			On && ATMod.S != null && !ATMod.S.specialAmmoFromStorage && t is Building_TurretGun && t.Faction == Faction.OfPlayer && Get(t) != AmmoKind.Normal;

		public static bool Enabled(AmmoKind k)
		{
			var s = ATMod.S;
			if (s == null) return true;
			switch (k)
			{
				case AmmoKind.ArmorPiercing: return s.kAP;
				case AmmoKind.HollowPoint: return s.kHP;
				case AmmoKind.Incendiary: return s.kInc;
				case AmmoKind.EMP: return s.kEmp;
				case AmmoKind.Toxic: return s.kTox;
				case AmmoKind.Smoke: case AmmoKind.Firefoam: return s.kShells;
				default: return true;
			}
		}

		public static ThingDef ShellOf(AmmoKind k)
		{
			string n;
			switch (k)
			{
				case AmmoKind.Incendiary: n = "Shell_Incendiary"; break;
				case AmmoKind.EMP: n = "Shell_EMP"; break;
				case AmmoKind.Smoke: n = "Shell_Smoke"; break;
				case AmmoKind.Firefoam: n = "Shell_Firefoam"; break;
				case AmmoKind.Toxic: n = "Shell_Toxic"; break;
				default: return null;
			}
			return DefDatabase<ThingDef>.GetNamedSilentFail(n);
		}

		// can this turret use the kind (and why not)
		public static string Reason(Building_TurretGun t, AmmoKind k)
		{
			if (!Enabled(k)) return "AT_AM_OffInSettings".Translate();
			bool expl = QuickControl.Explosive(t);
			if (expl)
			{
				if (!ShellKinds.Contains(k)) return "AT_AM_NotForShells".Translate();
				if (k == AmmoKind.Normal) return null;
				if (!TurretControls.IsShellTurret(t)) return "AT_AM_NotForRockets".Translate();
				if (ShellOf(k) == null) return "AT_AM_NoShellDef".Translate();
				return null;
			}
			if (!BulletKinds.Contains(k)) return "AT_AM_OnlyShells".Translate();
			return null;
		}

		public static void Set(Building_TurretGun t, AmmoKind k)
		{
			var c = QuickControlComp.Get;
			if (c == null || t == null) return;
			if (k == AmmoKind.Normal) c.kind.Remove(t.thingIDNumber);
			else c.kind[t.thingIDNumber] = (int)k;
			c.stun.Remove(t.thingIDNumber);   // 1.3.0 stun set is folded into the kinds
			try
			{
				if (QuickControl.Explosive(t))
				{
					if (!TurretControls.IsShellTurret(t)) return;
					ThingDef shell = ShellOf(k);
					TurretControlsComp.Choose(t, shell);
					CompChangeableProjectile cp = t.gun?.TryGetComp<CompChangeableProjectile>();
					if (cp != null && cp.Loaded && shell != null && cp.LoadedShell != shell)
					{
						Thing old = cp.RemoveShell();
						if (old != null)
						{
							bool free = (ATMod.S != null && ATMod.S.freeAmmo) || FreeSpecial(t);
							if (free) old.Destroy();
							else GenPlace.TryPlaceThing(old, t.InteractionCell.IsValid ? t.InteractionCell : t.Position, t.Map, ThingPlaceMode.Near);
						}
					}
					if (shell != null && cp != null && !cp.Loaded && ((ATMod.S != null && ATMod.S.freeAmmo) || FreeSpecial(t)))
						cp.LoadShell(shell, 1);
				}
				else CE.Select(t, k);
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] ammo type: " + e.Message, 0x41544d31); }
		}

		public static void SetAll(Map map, AmmoKind k, ThingDef onlyDef = null)
		{
			int n = 0, skipped = 0;
			foreach (Building_TurretGun t in QuickControl.All(map))
			{
				if (onlyDef != null && t.def != onlyDef) continue;
				if (Reason(t, k) != null) { skipped++; continue; }
				Set(t, k);
				n++;
			}
			string msg = "AT_AM_SetMsg".Translate(n, Label(k));
			if (skipped > 0) msg += " " + "AT_AM_Skipped".Translate(skipped);
			Messages.Message(msg, MessageTypeDefOf.NeutralEvent, false);
		}

		public static string Label(AmmoKind k) => ("AT_AM_K_" + k).Translate();
		public static string Desc(AmmoKind k) => ("AT_AM_K_" + k + "_Desc").Translate();

		public static Texture2D Icon(AmmoKind k)
		{
			ThingDef shell = ShellOf(k);
			if (shell?.uiIcon != null && shell.uiIcon != BaseContent.BadTex) return shell.uiIcon;
			if (k == AmmoKind.Stun) return QuickControl.StunTex;
			return ThingDefOf.Shell_HighExplosive?.uiIcon ?? QuickControl.OnTex;
		}

		public static IEnumerable<FloatMenuOption> Options(Building_TurretGun t)
		{
			bool expl = QuickControl.Explosive(t);
			foreach (AmmoKind k in expl ? ShellKinds : BulletKinds)
			{
				AmmoKind kk = k;
				string why = Reason(t, k);
				string label = Label(k);   // same label on every turret: with several selected, the pick goes to all that can use it
				if (why != null) yield return new FloatMenuOption(label + " (" + why + ")", null);
				else
				{
					var o = new FloatMenuOption(label, () => Set(t, kk), Icon(k), Color.white);
					o.tooltip = Desc(k);
					yield return o;
				}
			}
			Map map = t.Map;
			AmmoKind cur = Get(t);
			ThingDef def = t.def;
			yield return new FloatMenuOption("AT_AM_ApplySame".Translate(def.LabelCap), () => SetAll(map, cur, def));
			yield return new FloatMenuOption("AT_AM_ApplyAll".Translate(), () => SetAll(map, cur));
		}

		public static List<FloatMenuOption> AllOptions(Map map)
		{
			var l = new List<FloatMenuOption>();
			foreach (AmmoKind k in BulletKinds.Concat(new[] { AmmoKind.Smoke, AmmoKind.Firefoam }))
			{
				if (!Enabled(k)) continue;
				AmmoKind kk = k;
				var o = new FloatMenuOption("AT_AM_AllKind".Translate(Label(k)), () => SetAll(map, kk), Icon(k), Color.white);
				o.tooltip = Desc(k);
				l.Add(o);
			}
			return l;
		}

		public static Gizmo Gizmo(Building_TurretGun t)
		{
			AmmoKind k = Get(t);
			return new Command_Action
			{
				defaultLabel = "AT_AM_Gizmo".Translate(Label(k)),
				defaultDesc = "AT_AM_Gizmo_Desc".Translate() + "\n\n" + Label(k) + ": " + Desc(k),
				icon = Icon(k),
				action = () => Find.WindowStack.Add(new FloatMenu(Options(t).ToList()))
			};
		}
	}

	// ---------------------------------------------------------------------------
	// what a hit of a bullet with a special kind does
	// ---------------------------------------------------------------------------
	public struct AmmoHit { public AmmoKind kind; public float amount; public Building_TurretGun turret; }

	[HarmonyPatch(typeof(Thing), nameof(Thing.TakeDamage))]
	public static class Patch_AmmoDamage
	{
		[ThreadStatic] private static bool inner;
		private static readonly AccessTools.StructFieldRef<DamageInfo, float> penRef = AccessTools.StructFieldRefAccess<DamageInfo, float>("armorPenetrationInt");

		public static void Prefix(Thing __instance, ref DamageInfo dinfo, out AmmoHit __state)
		{
			__state = default;
			if (inner) return;
			try
			{
				if (!(dinfo.Instigator is Building_TurretGun t) || t.Faction != Faction.OfPlayer || dinfo.Def == null) return;
				AmmoKind k = Ammo.Get(t);
				if (k == AmmoKind.Normal || QuickControl.Explosive(t) || !Ammo.Enabled(k)) return;
				if (dinfo.Def == DamageDefOf.Stun || dinfo.Def == DamageDefOf.EMP) return;
				var s = ATMod.S;
				float a = dinfo.Amount;
				__state = new AmmoHit { kind = k, amount = a, turret = t };
				switch (k)
				{
					case AmmoKind.Stun:
						if (!(__instance is Pawn)) { dinfo.SetAmount(0f); return; }
						dinfo = new DamageInfo(DamageDefOf.Stun, Mathf.Clamp(a, 8f, 25f), 0f, dinfo.Angle, dinfo.Instigator, null, dinfo.Weapon, dinfo.Category, dinfo.IntendedTarget, dinfo.InstigatorGuilty);
						__state.kind = AmmoKind.Normal;   // nothing after the hit
						return;
					case AmmoKind.ArmorPiercing:
						dinfo.SetAmount(a * 0.8f);
						float p = penRef(ref dinfo);
						penRef(ref dinfo) = p > 0f ? p * (s?.apPen ?? 1.6f) : 0.5f;
						return;
					case AmmoKind.HollowPoint:
						dinfo.SetAmount(a * (s?.hpDmg ?? 1.25f));
						penRef(ref dinfo) = penRef(ref dinfo) * 0.5f;
						return;
					case AmmoKind.EMP:
						dinfo.SetAmount(a * 0.4f);
						return;
					case AmmoKind.Toxic:
						dinfo.SetAmount(a * 0.6f);
						return;
				}
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] ammo hit: " + e.Message, 0x41544d32); }
		}

		public static void Postfix(Thing __instance, AmmoHit __state)
		{
			if (__state.kind == AmmoKind.Normal || __state.turret == null || inner) return;
			inner = true;
			try
			{
				var s = ATMod.S;
				Thing target = __instance;
				if (target == null || target.Destroyed) return;
				switch (__state.kind)
				{
					case AmmoKind.Incendiary:
						target.TakeDamage(new DamageInfo(DamageDefOf.Burn, Mathf.Max(1f, __state.amount * 0.3f), 0f, -1f, __state.turret));
						if (!target.Destroyed && Rand.Chance(s?.incIgnite ?? 0.25f))
						{
							if (target is Pawn || target.FlammableNow) target.TryAttachFire(0.4f, __state.turret);
							else if (target.Spawned) FireUtility.TryStartFireIn(target.Position, target.Map, 0.4f, __state.turret);
						}
						break;
					case AmmoKind.EMP:
						target.TakeDamage(new DamageInfo(DamageDefOf.EMP, Mathf.Max(8f, __state.amount * (s?.empPow ?? 1f)), 0f, -1f, __state.turret));
						break;
					case AmmoKind.Toxic:
						if (target is Pawn pw && !pw.Dead && pw.RaceProps != null && pw.RaceProps.IsFlesh)
						{
							float res = 0f;
							try { res = pw.GetStatValue(StatDefOf.ToxicResistance); } catch { }
							float sev = (s?.toxSev ?? 0.04f) * Mathf.Clamp(__state.amount / 10f, 0.5f, 2.5f) * Mathf.Clamp01(1f - res);
							if (sev > 0f) HealthUtility.AdjustSeverity(pw, HediffDefOf.ToxicBuildup, sev);
						}
						break;
				}
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] ammo effect: " + e.Message, 0x41544d33); }
			finally { inner = false; }
		}
	}

	// ---------------------------------------------------------------------------
	// Combat Extended (soft, reflection only): picking a kind also selects that ammo in the gun's
	// CompAmmoUser when its ammo set has it, and with free special ammo the magazine is refilled.
	// If the set has no such ammo, CE's ammo stays and the kind works through the hit effect above.
	// ---------------------------------------------------------------------------
	public static class CE
	{
		private static bool looked;
		private static Type ammoUser;

		private static bool Ready()
		{
			if (!looked)
			{
				looked = true;
				ammoUser = AccessTools.TypeByName("CombatExtended.CompAmmoUser");
			}
			return ammoUser != null;
		}

		private static ThingComp CompOf(Building_TurretGun t)
		{
			if (!Ready() || t?.gun is not ThingWithComps g) return null;
			foreach (ThingComp c in g.AllComps) if (ammoUser.IsInstanceOfType(c)) return c;
			return null;
		}

		private static string Key(AmmoKind k)
		{
			switch (k)
			{
				case AmmoKind.ArmorPiercing: return "ArmorPiercing";
				case AmmoKind.HollowPoint: return "HollowPoint";
				case AmmoKind.Incendiary: return "Incendiary";
				case AmmoKind.EMP: return "EMP";
				case AmmoKind.Toxic: return "Tox";
				default: return null;
			}
		}

		public static void Select(Building_TurretGun t, AmmoKind k)
		{
			ThingComp c = CompOf(t);
			if (c == null) return;
			try
			{
				object props = AccessTools.Property(c.GetType(), "Props")?.GetValue(c) ?? c.props;
				object set = AccessTools.Field(props.GetType(), "ammoSet")?.GetValue(props);
				IEnumerable types = set == null ? null : AccessTools.Field(set.GetType(), "ammoTypes")?.GetValue(set) as IEnumerable;
				if (types == null) return;
				string key = Key(k);
				Def pick = null, first = null;
				foreach (object link in types)
				{
					Def ammo = AccessTools.Field(link.GetType(), "ammo")?.GetValue(link) as Def;
					if (ammo == null) continue;
					first ??= ammo;
					if (key == null) continue;
					Def cls = AccessTools.Field(ammo.GetType(), "ammoClass")?.GetValue(ammo) as Def;
					string n = (cls?.defName ?? "") + " " + ammo.defName;
					if (n.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0) { pick = ammo; break; }
				}
				if (k == AmmoKind.Normal) pick = first;
				if (pick == null) return;   // no such ammo in the set – the hit effect does the job
				AccessTools.Property(c.GetType(), "SelectedAmmo")?.SetValue(c, pick);
				if (Ammo.FreeSpecial(t) || (ATMod.S != null && ATMod.S.freeAmmo))
				{
					MethodInfo reset = AccessTools.Method(c.GetType(), "ResetAmmoCount");
					if (reset != null)
					{
						var ps = reset.GetParameters();
						reset.Invoke(c, ps.Length == 0 ? null : new object[] { pick });
					}
				}
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] Combat Extended ammo: " + e.Message, 0x41544d34); }
		}

		// free special ammo: keep the CE magazine full
		public static void Refill(Building_TurretGun t)
		{
			ThingComp c = CompOf(t);
			if (c == null) return;
			try
			{
				PropertyInfo cur = AccessTools.Property(c.GetType(), "CurMagCount");
				object props = AccessTools.Property(c.GetType(), "Props")?.GetValue(c) ?? c.props;
				object size = AccessTools.Field(props.GetType(), "magazineSize")?.GetValue(props);
				if (cur != null && cur.CanWrite && size is int max && max > 0 && (int)cur.GetValue(c) < max)
					cur.SetValue(c, max);
			}
			catch { }
		}
	}

	// free special ammo for bullet turrets of other mods that use CompRefuelable as ammo, and CE magazines
	public class SpecialAmmoComp : GameComponent
	{
		public SpecialAmmoComp(Game g) { }

		public override void GameComponentTick()
		{
			if (!Ammo.On || ATMod.S == null || ATMod.S.specialAmmoFromStorage || Find.TickManager.TicksGame % 250 != 0) return;
			var qc = QuickControlComp.Get;
			if (qc == null || qc.kind.Count == 0) return;
			try
			{
				foreach (Map m in Find.Maps)
					foreach (Building b in m.listerBuildings.allBuildingsColonist)
						if (b is Building_TurretGun t && qc.kind.ContainsKey(t.thingIDNumber) && !QuickControl.Explosive(t))
							CE.Refill(t);
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] special ammo refill: " + e.Message, 0x41544d35); }
		}
	}
}
