using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ResearchedLoadout
{
	// 2.19.0 "Tools in the pockets": a colonist keeps the tool for the work he does (pickaxe, sickle, axe,
	// hammer …) in his inventory. Before that work he swaps it into his hands at once (no walk), the real
	// weapon goes into the pockets; when he is drafted, a fight starts or he turns to other work, the weapon
	// comes back into his hands and the tool back into the pockets. The tool is fetched only once – before
	// the first work that wants it – and stays with him. At most 2 tools, within a mass limit.
	public static class ToolBelt
	{
		private const int MaxTools = 2;
		private const int SwapCooldown = 30;
		private const int IdleBack = 2500;   // an in-game hour without tool work = weapon back

		private sealed class Hold { public int weapon = -1; public int lastWork; public SkillDef skill; }
		private static readonly Dictionary<int, Hold> holds = new Dictionary<int, Hold>();
		private static readonly Dictionary<int, int> lastSwap = new Dictionary<int, int>();

		private static ResearchedLoadoutSettings S => ResearchedLoadoutMod.Settings;
		public static bool On
		{
			get
			{
				ResearchedLoadoutSettings s = S;
				return s != null && s.enabled && s.toolBelt && s.toolsNotWeapons && s.workClothing && s.gearBeforeWork && !SidearmsCompat.Present;
			}
		}

		private static int Now => Find.TickManager?.TicksGame ?? 0;

		public static void ResetGameState() { holds.Clear(); lastSwap.Clear(); }

		public static void Patch(Harmony h)
		{
			try
			{
				var drafted = AccessTools.PropertySetter(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted));
				if (drafted != null)
					h.Patch(drafted, postfix: new HarmonyMethod(typeof(ToolBelt), nameof(DraftedPostfix)));
			}
			catch (Exception e) { MLog.Warn("[Smart Colonist Gear] tools in the pockets: " + e.Message); }
		}

		public static bool IsTool(Thing t) => t?.def != null && LoadoutFilter.ToolNotWeapon(t.def);

		// carried tools do not count as weapons for the sidearm limits (they would be dropped as extra weapons)
		public static bool IgnoreInInventory(ThingDef d) => d != null && On && LoadoutFilter.ToolNotWeapon(d);

		// he works with a tool from the pockets right now (the real weapon waits in the inventory)
		public static bool Working(Pawn p) => p != null && holds.TryGetValue(p.thingIDNumber, out Hold h) && h.weapon >= 0 && IsTool(p.equipment?.Primary);

		private static IEnumerable<Thing> PocketTools(Pawn p)
		{
			if (p.inventory == null) yield break;
			var l = p.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < l.Count; i++)
				if (IsTool(l[i])) yield return l[i];
		}

		public static Thing PocketTool(Pawn p, SkillDef sk)
		{
			Thing best = null;
			float bestGain = 0.02f;
			foreach (Thing t in PocketTools(p))
			{
				float g = Specialization.SkillGain(t.def, sk);
				if (g > bestGain) { bestGain = g; best = t; }
			}
			return best;
		}

		private static float PocketToolMass(Pawn p)
		{
			float m = 0f;
			foreach (Thing t in PocketTools(p)) m += t.GetStatValue(StatDefOf.Mass) * t.stackCount;
			return m;
		}

		// before the first work that wants a tool: take one from storage into the pockets
		public static Job FetchJob(Pawn p, Map map, SkillDef sk, float maxDist)
		{
			if (!On || p.inventory == null || PocketTool(p, sk) != null) return null;
			if (PocketTools(p).Count() >= MaxTools) return null;
			ThingWithComps held = p.equipment?.Primary;
			float have = held != null && IsTool(held) ? Specialization.SkillGain(held.def, sk) : 0f;
			float limit = Mathf.Max(0.5f, S.toolBeltMass);
			float used = PocketToolMass(p);
			Thing best = null;
			float bestScore = have + 0.01f;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
			{
				if (t == null || !t.Spawned || !IsTool(t)) continue;
				float gain = Specialization.SkillGain(t.def, sk);
				if (gain <= 0.02f) continue;
				float dist = (t.Position - p.Position).LengthHorizontal;
				if (dist > maxDist) continue;
				float score = gain - dist * 0.0005f;
				if (score <= bestScore) continue;
				float mass = t.GetStatValue(StatDefOf.Mass);
				if (used + mass > limit + 0.001f || MassUtility.FreeSpace(p) < mass) continue;
				if (t.IsForbidden(p) || t.IsBurning() || !t.IsInAnyStorage()) continue;
				if (t.def.useHitPoints && (float)t.HitPoints / t.MaxHitPoints < 0.4f) continue;
				if (!EquipmentUtility.CanEquip(t, p)) continue;
				if (!p.CanReserveAndReach(t, PathEndMode.ClosestTouch, Danger.Some)) continue;
				best = t;
				bestScore = score;
			}
			if (best == null) return null;
			Job job = JobMaker.MakeJob(JobDefOf.TakeInventory, best);
			job.count = 1;
			GearWhy.TagJob(job, GearWhy.Reason("RL_WhyToolPocket", sk.label));
			return job;
		}

		// every job start: tool into the hands for tool work, weapon back otherwise
		public static void OnStart(Pawn p, Job job)
		{
			if (!On || p == null || job == null || p.equipment == null || p.inventory == null || !p.Spawned) return;
			bool holding = holds.TryGetValue(p.thingIDNumber, out Hold h0) && h0.weapon >= 0;
			if (!holding && (job.workGiverDef == null || !PocketTools(p).Any())) return;   // cheap way out for most job starts
			if (!LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) return;
			int now = Now;
			if (p.Drafted || p.Downed || p.InMentalState || SteadyThreat.Active(p.Map) || (p.mindState != null && p.mindState.anyCloseHostilesRecently))
			{
				Back(p);
				return;
			}
			WorkTypeDef wt = job.workGiverDef?.workType;
			if (wt?.relevantSkills != null && wt.relevantSkills.Count > 0 && !job.workGiverDef.emergency && wt != WorkTypeDefOf.Firefighter)
			{
				foreach (SkillDef sk in wt.relevantSkills)
				{
					if (sk == null) continue;
					Thing tool = PocketTool(p, sk);
					ThingWithComps held = p.equipment.Primary;
					float heldGain = held != null && IsTool(held) ? Specialization.SkillGain(held.def, sk) : 0f;
					if (tool != null && Specialization.SkillGain(tool.def, sk) > heldGain + 0.01f)
					{
						if (ToHands(p, tool, now))
						{
							Hold hh = HoldOf(p);
							hh.lastWork = now;
							hh.skill = sk;
						}
						return;
					}
					if (held != null && IsTool(held) && heldGain > 0.02f)
					{
						HoldOf(p).lastWork = now;   // still the right tool
						return;
					}
				}
				// work that wants no tool: the weapon comes back
				Back(p);
				return;
			}
			// no work job (meals, sleep, joy …): weapon back only after an hour without tool work
			if (holds.TryGetValue(p.thingIDNumber, out Hold h) && h.weapon >= 0 && now - h.lastWork > IdleBack)
				Back(p);
		}

		private static Hold HoldOf(Pawn p)
		{
			if (!holds.TryGetValue(p.thingIDNumber, out Hold h)) holds[p.thingIDNumber] = h = new Hold();
			return h;
		}

		private static bool ToHands(Pawn p, Thing tool, int now)
		{
			if (lastSwap.TryGetValue(p.thingIDNumber, out int ls) && now - ls < SwapCooldown && now >= ls) return false;
			try
			{
				ThingWithComps held = p.equipment.Primary;
				Hold h = HoldOf(p);
				if (held != null)
				{
					bool weapon = !IsTool(held);
					if (!p.equipment.TryTransferEquipmentToContainer(held, p.inventory.innerContainer)) return false;
					if (weapon) h.weapon = held.thingIDNumber;
				}
				Thing one = tool.stackCount > 1 ? tool.SplitOff(1) : tool;
				if (one == tool) p.inventory.innerContainer.Remove(tool);
				if (one is ThingWithComps twc)
				{
					p.equipment.AddEquipment(twc);
					lastSwap[p.thingIDNumber] = now;
					RL.Debug(p.LabelShort + " bere z kapsy " + twc.def.defName);
					return true;
				}
				p.inventory.innerContainer.TryAdd(one, true);
			}
			catch (Exception e) { MLog.WarnOnce("[Smart Colonist Gear] tool into hands: " + e.Message, 7319201); }
			return false;
		}

		// the real weapon back into the hands, the tool back into the pockets
		public static void Back(Pawn p)
		{
			if (p?.equipment == null || p.inventory == null || !holds.TryGetValue(p.thingIDNumber, out Hold h) || h.weapon < 0) return;
			try
			{
				Thing weapon = p.inventory.innerContainer.InnerListForReading.FirstOrDefault(t => t != null && t.thingIDNumber == h.weapon);
				h.weapon = -1;
				if (weapon == null) return;   // he lost or dropped it meanwhile – SCG finds him a weapon as usual
				ThingWithComps held = p.equipment.Primary;
				if (held != null)
				{
					if (!IsTool(held)) return;   // he holds a real weapon already
					if (!p.equipment.TryTransferEquipmentToContainer(held, p.inventory.innerContainer)) return;
				}
				p.inventory.innerContainer.Remove(weapon);
				if (weapon is ThingWithComps w) p.equipment.AddEquipment(w);
				else p.inventory.innerContainer.TryAdd(weapon, true);
				lastSwap[p.thingIDNumber] = Now;
				RL.Debug(p.LabelShort + " vraci zbran do ruky " + weapon.def.defName);
			}
			catch (Exception e) { MLog.WarnOnce("[Smart Colonist Gear] weapon back into hands: " + e.Message, 7319202); }
		}

		public static void DraftedPostfix(Pawn_DraftController __instance, bool value)
		{
			try { if (value && On) Back(__instance.pawn); } catch { }
		}
	}
}
