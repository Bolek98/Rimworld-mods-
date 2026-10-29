using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AutoTurrets
{
	// 1.5.2: Vanilla Factions Expanded – Security world artillery.
	// Our automatic turrets have no "mannable" part while they fire alone, but VFE Security reads it
	// directly (world target button, aiming over the world map, firing), which threw errors and hid
	// the buttons. Here the part is put back for those moments, and the "Set world target" button
	// works on every artillery of yours: automatic ones fire by themselves, a manned one that nobody
	// mans sends the nearest free colonist who can fight to it.
	public static class VfesArtillery
	{
		private static MethodInfo startTargeting;
		private static Texture2D icon;
		public static bool Active;

		public static void Install(Harmony h)
		{
			try
			{
				Type comp = GenTypes.GetTypeInAnyAssembly("VFESecurity.CompWorldArtillery");
				if (comp == null) return;
				startTargeting = AccessTools.Method(comp, "StartWorldTargeting");
				MethodInfo gizmos = AccessTools.Method(comp, "CompGetGizmosExtra");
				if (gizmos != null && startTargeting != null)
					h.Patch(gizmos, prefix: new HarmonyMethod(typeof(VfesArtillery), nameof(GizmoPrefix)));
				var pre = new HarmonyMethod(typeof(VfesArtillery), nameof(RestorePrefix));
				var fin = new HarmonyMethod(typeof(VfesArtillery), nameof(RestoreFinalizer));
				foreach (MethodInfo m in new[]
				{
					AccessTools.Method(comp, "FinalForcedMissRadius"),
					AccessTools.Method(comp, "StartAttack"),
					AccessTools.Method(GenTypes.GetTypeInAnyAssembly("VFESecurity.Verb_ShootWithWorldTargeting"), "WarmupComplete"),
				})
				{
					if (m != null) h.Patch(m, prefix: pre, finalizer: fin);
				}
				Active = true;
				Log.Message("[Auto Turrets] VFE Security world artillery supported.");
			}
			catch (Exception e)
			{
				Log.Warning("[Auto Turrets] VFE Security artillery: " + e.Message);
			}
		}

		private static Building_TurretGun TurretOf(object inst)
		{
			if (inst is ThingComp c) return c.parent as Building_TurretGun;
			if (inst is Verb v) return v.caster as Building_TurretGun;
			return null;
		}

		public static void RestorePrefix(object __instance, out Building_TurretGun __state)
		{
			__state = null;
			try
			{
				Building_TurretGun t = TurretOf(__instance);
				if (t == null || Auto.mannable(t) != null) return;
				CompMannable c = t.GetComp<CompMannable>();
				if (c == null) return;
				Auto.mannable(t) = c;
				__state = t;
			}
			catch { }
		}

		public static Exception RestoreFinalizer(Exception __exception, Building_TurretGun __state)
		{
			if (__state != null)
			{
				try { Auto.mannable(__state) = null; } catch { }
			}
			return __exception;
		}

		public static bool GizmoPrefix(ThingComp __instance, ref IEnumerable<Gizmo> __result)
		{
			__result = Make(__instance);
			return false;
		}

		private static IEnumerable<Gizmo> Make(ThingComp comp)
		{
			Building_TurretGun t = comp?.parent as Building_TurretGun;
			if (t == null || t.Faction != Faction.OfPlayer || startTargeting == null) yield break;
			if (icon == null)
			{
				try { icon = AccessTools.Field(comp.GetType(), "WorldTargetIcon")?.GetValue(null) as Texture2D; } catch { }
				if (icon == null) icon = ContentFinder<Texture2D>.Get("UI/Commands/Attack", false) ?? BaseContent.BadTex;
			}
			bool auto = Auto.IsAuto(t);
			CompMannable mc = t.GetComp<CompMannable>();
			bool manned = auto || mc == null || mc.ManningPawn != null;
			var cmd = new Command_Action
			{
				defaultLabel = "VFES_SetWorldTarget".Translate(),
				defaultDesc = "VFES_SetWorldTargetDesc".Translate(),
				icon = icon,
			};
			if (auto) cmd.defaultDesc += "\n\n" + "AT_VfesAuto".Translate();
			else if (!manned) cmd.defaultDesc += "\n\n" + "AT_VfesSend".Translate();
			cmd.action = () =>
			{
				if (!manned) SendManner(t);
				try { startTargeting.Invoke(comp, null); }
				catch (Exception ex) { Log.Warning("[Auto Turrets] world target: " + (ex.InnerException ?? ex).Message); }
			};
			yield return cmd;
		}

		private static void SendManner(Building_TurretGun turret)
		{
			try
			{
				Map map = turret.Map;
				if (map == null) return;
				Pawn best = null;
				float bestD = float.MaxValue;
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					if (p.Downed || p.InMentalState || p.WorkTagIsDisabled(WorkTags.Violent) || p.jobs == null) continue;
					float d = (p.Position - turret.Position).LengthHorizontalSquared;
					if (d >= bestD || !p.CanReserveAndReach(turret, PathEndMode.InteractionCell, Danger.Deadly)) continue;
					best = p;
					bestD = d;
				}
				if (best == null)
				{
					Messages.Message("AT_VfesNobody".Translate(), turret, MessageTypeDefOf.RejectInput, false);
					return;
				}
				best.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.ManTurret, turret), JobTag.Misc);
				Messages.Message("AT_VfesGoes".Translate(best.LabelShort), new LookTargets(best, turret), MessageTypeDefOf.TaskCompletion, false);
			}
			catch (Exception ex)
			{
				Log.Warning("[Auto Turrets] artillery crew: " + ex.Message);
			}
		}
	}
}
