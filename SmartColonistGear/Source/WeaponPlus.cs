using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ResearchedLoadout
{
	// ---------------------------------------------------------------------------
	// 2.17.0 weapons: a carried gun goes into the hand, weapons by role, spare guns in storage,
	// explosives only for good shooters, choosing a weapon in the Gear overview, alert about
	// unarmed colonists during a threat.
	// ---------------------------------------------------------------------------
	public static class WeaponPlus
	{
		private static ResearchedLoadoutSettings S => ResearchedLoadoutMod.Settings;

		public static int Shooting(Pawn p)
		{
			SkillRecord sk = p?.skills?.GetSkill(SkillDefOf.Shooting);
			return sk == null || sk.TotallyDisabled ? 0 : sk.Level;
		}

		// ---- a gun in the pockets goes into the hand ----
		public static bool TrySwapCarriedGun(Pawn pawn)
		{
			if (S == null || !S.swapCarriedGun || !ResearchedLoadoutMapComponent.RangedForAll(pawn)) return false;
			ThingWithComps primary = pawn.equipment?.Primary;
			if (primary == null || primary.def.IsRangedWeapon || pawn.inventory == null || pawn.Drafted) return false;
			if (LoadoutFilter.IsPersonaWeapon(primary)) return false;
			// not while fighting: Simple Sidearms may have switched to melee on purpose
			if (pawn.mindState != null && Find.TickManager.TicksGame - pawn.mindState.lastEngageTargetTick < 2500) return false;
			ThingWithComps gun = pawn.inventory.innerContainer.InnerListForReading
				.OfType<ThingWithComps>()
				.Where(t => t.def.IsRangedWeapon && LoadoutFilter.IsRealWeapon(t.def) && !LoadoutFilter.IsBelowMinHp(t) && EquipmentUtility.CanEquip(t, pawn))
				.OrderByDescending(t => ResearchedLoadoutMapComponent.ScoreWeapon(pawn, t))
				.FirstOrDefault();
			if (gun == null) return false;
			try
			{
				if (!pawn.equipment.TryTransferEquipmentToContainer(primary, pawn.inventory.innerContainer)) return false;
				pawn.inventory.innerContainer.Remove(gun);
				pawn.equipment.AddEquipment(gun);
				RL.NotifyWeapon(pawn, gun, primary);
				return true;
			}
			catch (Exception e)
			{
				MLog.Warn("[Smart Colonist Gear] swap to carried gun: " + e.Message);
				return false;
			}
		}

		// ---- weapons by role ----
		public static float RoleFactor(Pawn pawn, Thing thing)
		{
			if (S == null || !S.roleWeapons || thing?.def == null || !thing.def.IsRangedWeapon || pawn?.skills == null) return 1f;
			float range = thing.def.Verbs?.FirstOrDefault()?.range ?? 0f;
			int sh = Shooting(pawn);
			if (range >= 36f)                       // sniper and long rifles: for the best shooters
				return sh >= 12 ? 1.35f : sh < 8 ? 0.7f : 1f;
			if (range > 0f && range <= 20f)         // shotguns, SMGs, pistols: for the weaker ones
				return sh < 8 ? 1.2f : sh >= 12 ? 0.85f : 1f;
			return 1f;
		}

		// ---- spare guns ----
		private static int spares;

		public static void CountSpares(List<Thing> weapons)
		{
			spares = 0;
			if (S == null || S.reserveRanged <= 0 || weapons == null) return;
			for (int i = 0; i < weapons.Count; i++)
			{
				Thing t = weapons[i];
				if (t != null && t.Spawned && t.def.IsRangedWeapon && LoadoutFilter.IsRealWeapon(t.def) && !LoadoutFilter.IsThrownWeapon(t.def) && t.IsInAnyStorage())
					spares++;
			}
		}

		// an armed shooter does not take one of the last spare guns – an unarmed colonist (or one
		// with only a melee weapon who should have a gun) still may
		public static bool ReserveBlocks(Pawn pawn, Thing thing)
		{
			if (S == null || S.reserveRanged <= 0 || !thing.def.IsRangedWeapon || LoadoutFilter.IsThrownWeapon(thing.def)) return false;
			Thing p = pawn.equipment?.Primary;
			if (p == null || !p.def.IsRangedWeapon) return false;
			if (LoadoutFilter.IsBelowMinHp(p)) return false;
			return spares <= S.reserveRanged;
		}

		// ---- explosives ----
		private static readonly Dictionary<ThingDef, bool> explosive = new Dictionary<ThingDef, bool>();

		public static bool IsExplosive(ThingDef d)
		{
			if (d == null) return false;
			if (explosive.TryGetValue(d, out bool b)) return b;
			b = d.Verbs != null && d.Verbs.Any(v => v?.defaultProjectile?.projectile != null && v.defaultProjectile.projectile.explosionRadius > 0.5f);
			explosive[d] = b;
			return b;
		}

		public static bool ExplosiveBlocked(Pawn pawn, ThingDef d)
		{
			return S != null && S.explosivesMinShooting > 0 && IsExplosive(d) && Shooting(pawn) < S.explosivesMinShooting;
		}

		// ---- Gear overview: choose a weapon ----
		public static List<FloatMenuOption> PickMenu(Pawn pawn)
		{
			var opts = new List<FloatMenuOption>();
			Map map = pawn.Map;
			if (map != null)
			{
				var list = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon)
					.Where(t => t.Spawned && t.def.IsWeapon && LoadoutFilter.IsRealWeapon(t.def) && t.IsInAnyStorage() && !t.IsForbidden(pawn) && EquipmentUtility.CanEquip(t, pawn))
					.OrderByDescending(t => t.def.IsRangedWeapon)
					.ThenByDescending(t => ResearchedLoadoutMapComponent.ScoreWeapon(pawn, t))
					.Take(14).ToList();
				foreach (Thing t in list)
				{
					Thing th = t;
					string label = th.LabelCap + " – " + (th.def.IsRangedWeapon ? "RL_GO_Ranged" : "RL_GO_Melee").Translate();
					opts.Add(new FloatMenuOption(label, () =>
					{
						if (!pawn.CanReserveAndReach(th, PathEndMode.ClosestTouch, Danger.Deadly))
						{
							Messages.Message("NoPath".Translate().CapitalizeFirst(), pawn, MessageTypeDefOf.RejectInput, false);
							return;
						}
						Job job = JobMaker.MakeJob(JobDefOf.Equip, th);
						pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
					}, th.def.uiIcon ?? BaseContent.ClearTex, Color.white));
				}
			}
			if (opts.Count == 0) opts.Add(new FloatMenuOption("RL_GO_NoWeaponsStored".Translate(), null));
			return opts;
		}
	}

	// ---- alert: unarmed colonists while enemies are on the map ----
	public class Alert_SCGUnarmed : Alert
	{
		private readonly List<Pawn> culprits = new List<Pawn>();
		private int lastTick = -1;

		public Alert_SCGUnarmed()
		{
			defaultLabel = "RL_AlertUnarmed".Translate();
			defaultPriority = AlertPriority.High;
		}

		private List<Pawn> Culprits()
		{
			int now = Find.TickManager.TicksGame;
			if (now == lastTick) return culprits;
			lastTick = now;
			culprits.Clear();
			ResearchedLoadoutSettings s = ResearchedLoadoutMod.Settings;
			Map map = Find.CurrentMap;
			if (s == null || !s.enabled || !s.unarmedAlert || map == null || !GenHostility.AnyHostileActiveThreatToPlayer(map)) return culprits;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p.Downed || p.WorkTagIsDisabled(WorkTags.Violent) || (p.IsSlave && !s.armSlaves)) continue;
				Thing w = p.equipment?.Primary;
				if (w == null || (!w.def.IsRangedWeapon && ResearchedLoadoutMapComponent.RangedForAll(p))) culprits.Add(p);
			}
			return culprits;
		}

		public override TaggedString GetExplanation()
		{
			return "RL_AlertUnarmed_Desc".Translate(string.Join("\n", culprits.Select(p => "  • " + p.LabelShortCap + (p.equipment?.Primary == null ? "" : " (" + p.equipment.Primary.LabelShort + ")")).ToArray()));
		}

		public override AlertReport GetReport()
		{
			List<Pawn> l = Culprits();
			return l.Count == 0 ? AlertReport.Inactive : AlertReport.CulpritsAre(l);
		}
	}
}
