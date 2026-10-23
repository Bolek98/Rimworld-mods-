using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ResearchedLoadout
{
	// 2.15.0: guests use the colony's gear. Quest lodgers get everything (like colonists), visitors only
	// warm or light clothes against cold and heat. Their own things they take off go into their backpack;
	// when they leave they put their own things back on and the colony's things stay here.
	public static class GuestWardrobe
	{
		// thing id -> pawn id
		private static readonly Dictionary<int, int> borrowed = new Dictionary<int, int>();
		private static readonly Dictionary<int, int> storedOwn = new Dictionary<int, int>();
		private static readonly Dictionary<int, int> nextTry = new Dictionary<int, int>();
		private static bool restoring;

		public static bool LodgersOn => ResearchedLoadoutMod.Settings != null && ResearchedLoadoutMod.Settings.guestLodgers;

		public static bool VisitorsOn => ResearchedLoadoutMod.Settings != null && ResearchedLoadoutMod.Settings.guestVisitors;

		public static bool IsLodger(Pawn p) => p != null && p.IsQuestLodger();

		public static bool IsVisitor(Pawn p)
		{
			return p != null && p.RaceProps != null && p.RaceProps.Humanlike && p.Faction != null && !p.Faction.IsPlayer && !p.IsPrisoner && !p.IsSlave
				&& !p.HostileTo(Faction.OfPlayer) && p.apparel != null && p.MapHeld != null && p.MapHeld.IsPlayerHome;
		}

		public static bool IsGuest(Pawn p) => (LodgersOn && IsLodger(p)) || (VisitorsOn && IsVisitor(p));

		private static bool IsColonyItem(Thing t, Pawn p)
		{
			return t != null && t.Spawned && t.Map != null && t.Map.IsPlayerHome && !storedOwn.ContainsKey(t.thingIDNumber);
		}

		// a gear job of a guest that takes a colony item: remember it as borrowed
		public static void OnGearJob(Pawn pawn, Job job)
		{
			if (pawn == null || job == null || !IsGuest(pawn))
				return;
			Thing t = job.targetA.Thing;
			if (t == null || !IsColonyItem(t, pawn))
				return;
			if (job.def == JobDefOf.Wear || job.def == JobDefOf.Equip || job.def == JobDefOf.TakeInventory || (SidearmsCompat.EquipSecondaryJob != null && job.def == SidearmsCompat.EquipSecondaryJob))
				borrowed[t.thingIDNumber] = pawn.thingIDNumber;
		}

		public static bool IsBorrowed(Thing t) => t != null && borrowed.ContainsKey(t.thingIDNumber);

		// before a guest puts on a colony item: own clothes that would come off go into the backpack instead of the floor
		public static void BeforeWear(Pawn pawn, Apparel newApparel)
		{
			if (restoring || pawn?.apparel == null || pawn.inventory == null || newApparel == null || !IsGuest(pawn))
				return;
			if (newApparel.Spawned && newApparel.Map != null && newApparel.Map.IsPlayerHome)
				borrowed[newApparel.thingIDNumber] = pawn.thingIDNumber;
			if (!borrowed.ContainsKey(newApparel.thingIDNumber))
				return;
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = worn.Count - 1; i >= 0; i--)
			{
				Apparel a = worn[i];
				if (a == newApparel || borrowed.ContainsKey(a.thingIDNumber) || ApparelUtility.CanWearTogether(newApparel.def, a.def, pawn.RaceProps.body))
					continue;
				Stash(pawn, a);
			}
		}

		public static bool Stash(Pawn pawn, Apparel a)
		{
			if (pawn?.apparel == null || pawn.inventory == null || a == null || pawn.apparel.IsLocked(a))
				return false;
			pawn.apparel.Remove(a);
			if (pawn.inventory.innerContainer.TryAdd(a))
				storedOwn[a.thingIDNumber] = pawn.thingIDNumber;
			else if (pawn.Spawned)
				GenPlace.TryPlaceThing(a, pawn.Position, pawn.Map, ThingPlaceMode.Near);
			return true;
		}

		// a lodger's own weapon goes into the backpack, not on the floor
		public static bool BeforeMakeRoom(Pawn pawn, ThingWithComps eq)
		{
			if (restoring || pawn?.equipment == null || pawn.inventory == null || eq == null || !IsGuest(pawn))
				return true;
			ThingWithComps primary = pawn.equipment.Primary;
			if (primary == null || eq.def.equipmentType != EquipmentType.Primary || borrowed.ContainsKey(primary.thingIDNumber))
				return true;
			pawn.equipment.Remove(primary);
			if (pawn.inventory.innerContainer.TryAdd(primary))
				storedOwn[primary.thingIDNumber] = pawn.thingIDNumber;
			else if (pawn.Spawned)
				GenPlace.TryPlaceThing(primary, pawn.Position, pawn.Map, ThingPlaceMode.Near);
			return false;
		}

		public static bool HasAny(Pawn pawn)
		{
			int id = pawn.thingIDNumber;
			foreach (KeyValuePair<int, int> kv in borrowed)
				if (kv.Value == id) return true;
			foreach (KeyValuePair<int, int> kv in storedOwn)
				if (kv.Value == id) return true;
			return false;
		}

		// the colony's things stay here, the guest's own go back on
		public static void Restore(Pawn pawn)
		{
			if (restoring || pawn == null || !HasAny(pawn))
				return;
			restoring = true;
			try
			{
				IntVec3 pos = pawn.PositionHeld;
				Map map = pawn.MapHeld;
				int id = pawn.thingIDNumber;
				if (pawn.apparel != null)
				{
					List<Apparel> worn = pawn.apparel.WornApparel;
					for (int i = worn.Count - 1; i >= 0; i--)
					{
						Apparel a = worn[i];
						if (!borrowed.ContainsKey(a.thingIDNumber))
							continue;
						borrowed.Remove(a.thingIDNumber);
						pawn.apparel.Remove(a);
						Leave(a, pos, map);
					}
				}
				if (pawn.equipment != null && pawn.equipment.Primary != null && borrowed.ContainsKey(pawn.equipment.Primary.thingIDNumber))
				{
					ThingWithComps eq = pawn.equipment.Primary;
					borrowed.Remove(eq.thingIDNumber);
					pawn.equipment.Remove(eq);
					Leave(eq, pos, map);
				}
				if (pawn.inventory != null)
				{
					ThingOwner<Thing> inv = pawn.inventory.innerContainer;
					for (int i = inv.Count - 1; i >= 0; i--)
					{
						Thing t = inv[i];
						if (borrowed.ContainsKey(t.thingIDNumber))
						{
							borrowed.Remove(t.thingIDNumber);
							inv.Remove(t);
							Leave(t, pos, map);
						}
					}
					for (int i = inv.Count - 1; i >= 0; i--)
					{
						Thing t = inv[i];
						if (!storedOwn.ContainsKey(t.thingIDNumber))
							continue;
						storedOwn.Remove(t.thingIDNumber);
						Apparel ap = t as Apparel;
						ThingWithComps w = t as ThingWithComps;
						if (ap != null && pawn.apparel != null)
						{
							inv.Remove(ap);
							pawn.apparel.Wear(ap, true, false);
						}
						else if (w != null && w.def.IsWeapon && pawn.equipment != null && pawn.equipment.Primary == null)
						{
							inv.Remove(w);
							pawn.equipment.AddEquipment(w);
						}
					}
				}
				// whatever is left for this pawn (lost, destroyed) is forgotten
				Forget(id);
			}
			catch (Exception e)
			{
				MLog.Warn("[Smart Colonist Gear] guest gear return: " + e.Message);
			}
			finally
			{
				restoring = false;
			}
		}

		private static void Leave(Thing t, IntVec3 pos, Map map)
		{
			if (map == null || !pos.IsValid)
				return;
			if (GenPlace.TryPlaceThing(t, pos, map, ThingPlaceMode.Near))
				t.SetForbidden(false, false);
		}

		private static void Forget(int pawnId)
		{
			var a = new List<int>();
			foreach (KeyValuePair<int, int> kv in borrowed)
				if (kv.Value == pawnId) a.Add(kv.Key);
			foreach (int k in a) borrowed.Remove(k);
			a.Clear();
			foreach (KeyValuePair<int, int> kv in storedOwn)
				if (kv.Value == pawnId) a.Add(kv.Key);
			foreach (int k in a) storedOwn.Remove(k);
		}

		// ---------- visitors: only against cold and heat ----------
		public static void VisitorTick(Map map, int now)
		{
			if (!VisitorsOn || map == null || !map.IsPlayerHome)
				return;
			IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
			for (int i = 0; i < all.Count; i++)
			{
				Pawn p = all[i];
				if (!IsVisitor(p) || p.Downed || p.InMentalState || p.jobs == null)
					continue;
				// leaving: give the colony's things back while still in the base, not at the map edge
				Verse.AI.Group.Lord lord = p.GetLord();
				if (lord != null && lord.CurLordToil != null && lord.CurLordToil.GetType().Name.IndexOf("Exit", StringComparison.Ordinal) >= 0)
				{
					if (HasAny(p))
						Restore(p);
					continue;
				}
				int nt;
				if (nextTry.TryGetValue(p.thingIDNumber, out nt) && now < nt)
					continue;
				int cold, heat;
				LoadoutFilter.TemperatureNeed(p, out cold, out heat);
				if (cold == 0 && heat == 0)
				{
					if (HasAny(p))
						Restore(p);
					nextTry[p.thingIDNumber] = now + 1250;
					continue;
				}
				if (heat > 0)
				{
					// take off own warm things into the backpack, give back borrowed warm things
					Apparel warm = null;
					foreach (Apparel a in p.apparel.WornApparel)
						if (!LoadoutFilter.IsArmor(a) && !p.apparel.IsLocked(a) && a.GetStatValue(StatDefOf.Insulation_Cold) >= 6f && (warm == null || a.GetStatValue(StatDefOf.Insulation_Cold) > warm.GetStatValue(StatDefOf.Insulation_Cold)))
							warm = a;
					if (warm != null)
					{
						if (borrowed.ContainsKey(warm.thingIDNumber))
						{
							borrowed.Remove(warm.thingIDNumber);
							p.apparel.Remove(warm);
							Leave(warm, p.Position, p.Map);
						}
						else
							Stash(p, warm);
						nextTry[p.thingIDNumber] = now + 60;
						continue;
					}
					nextTry[p.thingIDNumber] = now + 1250;
					continue;
				}
				// cold: put on the warmest colony clothes that add warmth (own clothes go into the backpack)
				Apparel best = null;
				float bestGain = 2f;
				List<Thing> list = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
				for (int k = 0; k < list.Count; k++)
				{
					Apparel a = list[k] as Apparel;
					if (a == null || !a.Spawned || a.IsForbidden(Faction.OfPlayer) || !a.IsInValidStorage() || a.def.apparel == null || !a.def.apparel.PawnCanWear(p)
						|| !ApparelUtility.HasPartsToWear(p, a.def) || !EquipmentUtility.CanEquip(a, p) || !LoadoutFilter.AllowedFor(p, a))
						continue;
					float gain = a.GetStatValue(StatDefOf.Insulation_Cold);
					foreach (Apparel w in p.apparel.WornApparel)
						if (!ApparelUtility.CanWearTogether(a.def, w.def, p.RaceProps.body))
							gain -= w.GetStatValue(StatDefOf.Insulation_Cold);
					if (gain <= bestGain || !p.CanReserveAndReach(a, PathEndMode.ClosestTouch, Danger.Some))
						continue;
					best = a;
					bestGain = gain;
				}
				if (best == null)
				{
					nextTry[p.thingIDNumber] = now + 2500;
					continue;
				}
				Job job = JobMaker.MakeJob(JobDefOf.Wear, best);
				borrowed[best.thingIDNumber] = p.thingIDNumber;
				GearWhy.TagJob(job, GearWhy.Reason("RL_Why_Temperature"));
				p.jobs.StartJob(job, JobCondition.InterruptForced, null, true, true);
				nextTry[p.thingIDNumber] = now + 400;
			}
		}

		// ---------- save / load ----------
		public static void Expose(ref List<string> data)
		{
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				data = new List<string>();
				foreach (KeyValuePair<int, int> kv in borrowed) data.Add("b:" + kv.Key + ":" + kv.Value);
				foreach (KeyValuePair<int, int> kv in storedOwn) data.Add("o:" + kv.Key + ":" + kv.Value);
			}
			Scribe_Collections.Look(ref data, "guestWardrobe", LookMode.Value);
			if (Scribe.mode == LoadSaveMode.LoadingVars)
			{
				borrowed.Clear();
				storedOwn.Clear();
				nextTry.Clear();
				if (data == null)
					return;
				foreach (string s in data)
				{
					string[] p = s.Split(':');
					int t, w;
					if (p.Length != 3 || !int.TryParse(p[1], out t) || !int.TryParse(p[2], out w))
						continue;
					(p[0] == "b" ? borrowed : storedOwn)[t] = w;
				}
			}
		}

		public static void ResetGameState()
		{
			borrowed.Clear();
			storedOwn.Clear();
			nextTry.Clear();
		}
	}

	[HarmonyPatch(typeof(Pawn_ApparelTracker), "Wear")]
	public static class GuestWearPatch
	{
		[HarmonyPrefix]
		public static void Prefix(Pawn_ApparelTracker __instance, Apparel newApparel)
		{
			try { GuestWardrobe.BeforeWear(__instance.pawn, newApparel); }
			catch (Exception e) { MLog.WarnOnce("[Smart Colonist Gear] guest wear: " + e.Message, 0x5c61a01); }
		}
	}

	[HarmonyPatch(typeof(Pawn_EquipmentTracker), "MakeRoomFor", new Type[] { typeof(ThingWithComps), typeof(ThingWithComps) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Out })]
	public static class GuestMakeRoomPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(Pawn_EquipmentTracker __instance, ThingWithComps eq, ref ThingWithComps dropped)
		{
			try
			{
				if (!GuestWardrobe.BeforeMakeRoom(__instance.pawn, eq))
				{
					dropped = null;
					return false;
				}
			}
			catch (Exception e) { MLog.WarnOnce("[Smart Colonist Gear] guest weapon: " + e.Message, 0x5c61a02); }
			return true;
		}
	}

	[HarmonyPatch(typeof(Pawn), "ExitMap")]
	public static class GuestExitPatch
	{
		[HarmonyPrefix]
		public static void Prefix(Pawn __instance, bool allowedToJoinOrCreateCaravan)
		{
			try
			{
				// a lodger going out with your caravan keeps the gear
				if (__instance.Faction != null && __instance.Faction.IsPlayer && allowedToJoinOrCreateCaravan)
					return;
				GuestWardrobe.Restore(__instance);
			}
			catch { }
		}
	}

	[HarmonyPatch(typeof(Pawn), "DeSpawn")]
	public static class GuestDeSpawnPatch
	{
		[HarmonyPrefix]
		public static void Prefix(Pawn __instance)
		{
			try
			{
				// visitors leaving by shuttle or transport pod; player pawns entering a caravan keep their gear
				if (__instance.Faction != null && !__instance.Faction.IsPlayer && __instance.Spawned && !__instance.Dead)
					GuestWardrobe.Restore(__instance);
			}
			catch { }
		}
	}
}
