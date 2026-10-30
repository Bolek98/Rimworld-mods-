using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AutoTurrets
{
	// 1.6.1: a turret that calls itself again and again (seen with the VFE Pirates field gun and a forced target)
	// used to fill the stack until the whole game closed. Every turret and gun method below counts how deep it
	// is nested; past 40 the call is skipped (the turret just doesn't shoot this time) and the log gets the
	// chain once, so the mod that loops can be found.
	public static class LoopGuard
	{
		private const int Limit = 40;
		[ThreadStatic] private static Dictionary<MethodBase, int> depth;
		private static readonly HashSet<MethodBase> reported = new HashSet<MethodBase>();

		public static void Install(Harmony h)
		{
			var pre = new HarmonyMethod(typeof(LoopGuard), nameof(Prefix)) { priority = Priority.First + 1 };
			var fin = new HarmonyMethod(typeof(LoopGuard), nameof(Finalizer));
			int n = 0;
			foreach (MethodBase m in Targets())
			{
				try
				{
					if (m == null || m.IsAbstract || m.GetMethodBody() == null) continue;
					h.Patch(m, prefix: pre, finalizer: fin);
					n++;
				}
				catch (Exception e)
				{
					Log.Warning("[Auto Turrets] loop guard on " + m?.DeclaringType?.Name + "." + m?.Name + ": " + e.Message);
				}
			}
			Log.Message("[Auto Turrets] loop guard on " + n + " turret and gun methods.");
		}

		private static IEnumerable<MethodBase> Targets()
		{
			Type tg = typeof(Building_TurretGun);
			foreach (string s in new[] { "OrderAttack", "TryStartShootSomething", "BeginBurst", "TryFindNewTarget", "ResetForcedTarget", "BurstComplete", "IsValidTarget" })
				yield return AccessTools.DeclaredMethod(tg, s);
			Type v = typeof(Verb);
			foreach (string s in new[] { "CanHitTarget", "CanHitTargetFrom", "Available", "TryCastNextBurstShot", "WarmupComplete", "OrderForceTarget" })
				yield return AccessTools.DeclaredMethod(v, s);
			foreach (MethodInfo m in v.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
				if (m.Name == "TryStartCastOn")
					yield return m;
			Type cone = GenTypes.GetTypeInAnyAssembly("VEF.Weapons.Verb_ShootCone");
			if (cone != null)
				yield return AccessTools.DeclaredMethod(cone, "CanHitTarget");
		}

		public static bool Prefix(MethodBase __originalMethod)
		{
			if (depth == null) depth = new Dictionary<MethodBase, int>();
			depth.TryGetValue(__originalMethod, out int d);
			depth[__originalMethod] = ++d;
			if (d <= Limit) return true;
			if (d == Limit + 1 && reported.Add(__originalMethod))
			{
				string st = Environment.StackTrace;
				if (st.Length > 6000) st = st.Substring(0, 6000);
				Log.Error("[Auto Turrets] endless loop stopped in " + __originalMethod.DeclaringType?.Name + "." + __originalMethod.Name
					+ " – the game would have crashed. Please send this to Bolek98:\n" + st);
			}
			return false;
		}

		public static Exception Finalizer(MethodBase __originalMethod, Exception __exception)
		{
			if (depth != null && depth.TryGetValue(__originalMethod, out int d))
				depth[__originalMethod] = d - 1;
			return __exception;
		}
	}
}
