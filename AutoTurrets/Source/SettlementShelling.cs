using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace AutoTurrets
{
	// ---------------------------------------------------------------------------
	// 1.4.0: VFE Security world artillery can shell a hostile settlement that has no map yet.
	// The game keeps such a base only as an icon, so VFE refused it ("needs a visible, explored
	// map"). Here the shell flies there as usual and each hit is counted on the settlement:
	//  - when you attack it later, the hits fall on the base while the map is made (fewer
	//    defenders and buildings),
	//  - after enough hits the base is destroyed, as if taken by a caravan.
	// ---------------------------------------------------------------------------
	public class SettlementShellingComp : WorldComponent
	{
		public Dictionary<int, int> hits = new Dictionary<int, int>();   // settlement ID -> hits

		public SettlementShellingComp(World w) : base(w) { }

		public static SettlementShellingComp Get() => Find.World?.GetComponent<SettlementShellingComp>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref hits, "atShellHits", LookMode.Value, LookMode.Value);
			if (hits == null) hits = new Dictionary<int, int>();
		}

		public int HitsOn(MapParent s) => s != null && hits.TryGetValue(s.ID, out int n) ? n : 0;
	}

	public static class SettlementShelling
	{
		public static bool On => ATMod.S == null || ATMod.S.shellSettlements;

		// hits that destroy a base: more for a bigger faction tech level
		public static int Needed(MapParent s)
		{
			int baseN = ATMod.S?.shellHitsToDestroy ?? 40;
			// 1.4.2: a site (camp, field factory…) is smaller than a base – half the hits
			if (s is Site) return Mathf.Max(5, Mathf.RoundToInt(baseN * 0.5f));
			TechLevel t = s?.Faction?.def?.techLevel ?? TechLevel.Industrial;
			float k = t <= TechLevel.Neolithic ? 0.6f : t <= TechLevel.Medieval ? 0.8f : t <= TechLevel.Industrial ? 1f : 1.4f;
			return Mathf.Max(5, Mathf.RoundToInt(baseN * k));
		}

		// a base of another faction, hostile to you, without a map
		// 1.4.2: also a hostile site (e.g. the Wolfein drone factory that sends drone raids)
		public static MapParent Target(PlanetTile tile)
		{
			if (!On || !tile.Valid) return null;
			MapParent s = Find.WorldObjects.SettlementAt(tile);
			if (s == null)
				s = Find.WorldObjects.Sites.FirstOrDefault(x => x.Tile == tile && x.Faction != null);
			if (s == null || s.HasMap || s.Faction == null || s.Faction.IsPlayer || !s.Faction.HostileTo(Faction.OfPlayer)) return null;
			return s;
		}

		public static void Hit(MapParent s, ThingDef shell)
		{
			var c = SettlementShellingComp.Get();
			if (c == null || s == null || s.Destroyed) return;
			c.hits.TryGetValue(s.ID, out int n);
			n++;
			c.hits[s.ID] = n;
			int need = Needed(s);
			if (n >= need)
			{
				c.hits.Remove(s.ID);
				Destroy(s);
				return;
			}
			if (n == 1 || n % 5 == 0 || need - n <= 3)
				Messages.Message("AT_ShellHit".Translate(s.Label, n, need), new GlobalTargetInfo(s), MessageTypeDefOf.NeutralEvent, false);
		}

		private static void Destroy(MapParent s)
		{
			Faction f = s.Faction;
			PlanetTile tile = s.Tile;
			if (s is Site)
			{
				// 1.4.2: the site is simply gone (its quest ends, its raids stop)
				Find.LetterStack.ReceiveLetter("AT_ShellDestroyedLabel".Translate(), "AT_ShellSiteDestroyed".Translate(s.Label, f.Name), LetterDefOf.PositiveEvent, new GlobalTargetInfo(tile), f);
				s.Destroy();
				return;
			}
			var sb = new StringBuilder("AT_ShellDestroyed".Translate(s.Label, f.Name));
			try
			{
				WorldObjectDef dd = tile.LayerDef?.DestroyedSettlementWorldObjectDef ?? WorldObjectDefOf.DestroyedSettlement;
				WorldObject ruin = WorldObjectMaker.MakeWorldObject(dd);
				ruin.Tile = tile;
				ruin.SetFaction(f);
				Find.WorldObjects.Add(ruin);
			}
			catch (Exception e) { Log.Warning("[Auto Turrets] ruin: " + e.Message); }
			bool other = Find.WorldObjects.Settlements.Any(x => x != s && x.Faction == f);
			if (!other)
			{
				f.defeated = true;
				sb.AppendLine().AppendLine().Append("LetterFactionBaseDefeated_FactionDestroyed".Translate(f.Name));
			}
			foreach (Faction other2 in Find.FactionManager.AllFactions)
				if (!other2.Hidden && !other2.IsPlayer && other2 != f && other2.HostileTo(f))
					Faction.OfPlayer.TryAffectGoodwillWith(other2, 20, false, false, HistoryEventDefOf.DestroyedEnemyBase);
			Find.LetterStack.ReceiveLetter("AT_ShellDestroyedLabel".Translate(), sb.ToString(), LetterDefOf.PositiveEvent, new GlobalTargetInfo(tile), f);
			s.Destroy();
		}

		// when the base gets a map (your attack), the counted hits fall on it
		public static void ApplyOnMap(MapParent s)
		{
			var c = SettlementShellingComp.Get();
			int n = c?.HitsOn(s) ?? 0;
			if (n <= 0 || s.Map == null) return;
			c.hits.Remove(s.ID);
			Map map = s.Map;
			var targets = new List<IntVec3>();
			foreach (Building b in map.listerBuildings.allBuildingsNonColonist)
				if (b.Faction == s.Faction) targets.Add(b.Position);
			foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(s.Faction))
				targets.Add(p.Position);
			if (targets.Count == 0) return;
			int blasts = Mathf.Min(n, 60);
			for (int i = 0; i < blasts; i++)
			{
				IntVec3 c0 = targets.RandomElement() + GenRadial.RadialPattern[Rand.Range(0, 20)];
				if (!c0.InBounds(map)) continue;
				try { GenExplosion.DoExplosion(c0, map, 2.9f, DamageDefOf.Bomb, null); } catch { }
			}
			Find.LetterStack.ReceiveLetter("AT_ShellAppliedLabel".Translate(), "AT_ShellApplied".Translate(s.Label, n), LetterDefOf.NeutralEvent, new LookTargets(map.Center, map));
		}
	}

	public static class VfesSettlementPatches
	{
		private static Type compT, projT;
		private static MethodInfo isWithinRange, isValidMode, startAttack, drawHighlight, resetCached;
		private static FieldInfo propsWorldRange, projTargetTile, projDef, worldTargetF;
		private static Type verbT;

		// VFE fires over the map edge only at a base that has a map – a shelled base has none
		public static bool CastPrefix(Verb_LaunchProjectile __instance, ref bool __result)
		{
			try
			{
				Thing caster = __instance.caster;
				if (caster?.Map == null || __instance.CurrentTarget.Cell.InBounds(caster.Map)) return true;
				ThingComp comp = (caster as ThingWithComps)?.AllComps.FirstOrDefault(c => compT.IsInstanceOfType(c));
				if (comp == null) return true;
				GlobalTargetInfo wt = (GlobalTargetInfo)worldTargetF.GetValue(comp);
				if (!wt.IsValid || SettlementShelling.Target(wt.Tile) == null) return true;
				ThingDef projectile = __instance.Projectile;
				if (projectile == null) return true;
				if (__instance.EquipmentSource != null)
				{
					__instance.EquipmentSource.GetComp<CompChangeableProjectile>()?.Notify_ProjectileLaunched();
				}
				ShootLine line;
				if (!__instance.TryFindShootLineFromTo(caster.Position, __instance.CurrentTarget, out line, false))
					line = new ShootLine(caster.Position, __instance.CurrentTarget.Cell);
				Projectile p = (Projectile)GenSpawn.Spawn(projectile, line.Source, caster.Map);
				p.Launch(caster, caster.DrawPos, line.Dest, __instance.CurrentTarget, ProjectileHitFlags.IntendedTarget, __instance.preventFriendlyFire, __instance.EquipmentSource);
				__result = true;
				return false;
			}
			catch (Exception e)
			{
				Log.WarningOnce("[Auto Turrets] shelling shot: " + e.Message, 0x5a71b3);
				return true;
			}
		}

		public static void Install(Harmony h)
		{
			try
			{
				compT = GenTypes.GetTypeInAnyAssembly("VFESecurity.CompWorldArtillery");
				projT = GenTypes.GetTypeInAnyAssembly("VFESecurity.WorldObject_ArtilleryProjectile");
				if (compT == null || projT == null) return;
				isWithinRange = AccessTools.Method(compT, "IsWithinRange");
				isValidMode = AccessTools.Method(compT, "IsValidTargetForFiringMode");
				startAttack = AccessTools.Method(compT, "StartAttack");
				drawHighlight = AccessTools.Method(compT, "DrawTargetHighlightField");
				resetCached = AccessTools.Method(compT, "ResetCachedTile");
				Type propsT = GenTypes.GetTypeInAnyAssembly("VFESecurity.CompProperties_WorldArtillery");
				propsWorldRange = propsT == null ? null : AccessTools.Field(propsT, "worldMapAttackRange");
				projTargetTile = AccessTools.Field(projT, "targetTile");
				projDef = AccessTools.Field(projT, "projectileDef");
				MethodInfo start = AccessTools.Method(compT, "StartWorldTargeting");
				MethodInfo arrive = AccessTools.Method(projT, "OnArrival");
				if (start == null || arrive == null || isWithinRange == null || isValidMode == null || startAttack == null || projTargetTile == null) return;
				h.Patch(start, prefix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(StartPrefix)));
				h.Patch(arrive, prefix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(ArrivePrefix)));
				verbT = GenTypes.GetTypeInAnyAssembly("VFESecurity.Verb_ShootWithWorldTargeting");
				worldTargetF = AccessTools.Field(compT, "worldTarget");
				MethodInfo cast = verbT == null ? null : AccessTools.Method(verbT, "TryCastShot");
				if (cast != null && worldTargetF != null)
					h.Patch(cast, prefix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(CastPrefix)));
				h.Patch(AccessTools.Method(typeof(Settlement), nameof(Settlement.PostMapGenerate)), postfix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(MapPostfix)));
				h.Patch(AccessTools.Method(typeof(Settlement), nameof(Settlement.GetInspectString)), postfix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(InspectPostfix)));
				h.Patch(AccessTools.Method(typeof(Site), nameof(Site.PostMapGenerate)), postfix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(MapPostfix)));
				h.Patch(AccessTools.Method(typeof(Site), nameof(Site.GetInspectString)), postfix: new HarmonyMethod(typeof(VfesSettlementPatches), nameof(InspectPostfix)));
				Log.Message("[Auto Turrets] VFE Security artillery can shell settlements without a map.");
			}
			catch (Exception e) { Log.Warning("[Auto Turrets] settlement shelling: " + e.Message); }
		}

		// our world targeting: the same as VFE's, plus hostile bases without a map
		public static bool StartPrefix(ThingComp __instance)
		{
			if (!SettlementShelling.On) return true;
			try
			{
				ThingComp comp = __instance;
				Building_TurretGun turret = comp.parent as Building_TurretGun;
				if (turret?.Map == null) return true;
				resetCached?.Invoke(comp, null);
				CameraJumper.TryJump(CameraJumper.GetWorldTarget(turret), CameraJumper.MovementMode.Pan);
				Find.WorldSelector.ClearSelection();
				int range = propsWorldRange != null ? (int)propsWorldRange.GetValue(comp.props) : 0;
				Find.WorldTargeter.BeginTargeting(delegate (GlobalTargetInfo t)
				{
					object[] a = { t, null };
					if (!(bool)isWithinRange.Invoke(comp, a))
					{
						Messages.Message((string)a[1], MessageTypeDefOf.RejectInput, false);
						return false;
					}
					MapParent s = SettlementShelling.Target(t.Tile);
					if (s != null)
					{
						startAttack.Invoke(comp, new object[] { t, new LocalTargetInfo(new IntVec3(100, 0, 100)), turret });
						Messages.Message("AT_ShellStart".Translate(s.Label, SettlementShelling.Needed(s) - SettlementShellingComp.Get().HitsOn(s)), turret, MessageTypeDefOf.TaskCompletion, false);
						return true;
					}
					// 1.4.2: a clear reason instead of VFE's "needs a visible, explored map"
					MapParent mp0 = Find.WorldObjects.MapParentAt(t.Tile);
					if (mp0?.Map == null && SettlementShelling.On)
					{
						MapParent other = (MapParent)Find.WorldObjects.SettlementAt(t.Tile) ?? Find.WorldObjects.Sites.FirstOrDefault(x => x.Tile == t.Tile && x.Faction != null);
						if (other != null && other.Faction != null && !other.Faction.IsPlayer && !other.HasMap && !other.Faction.HostileTo(Faction.OfPlayer))
							Messages.Message("AT_ShellNotHostile".Translate(other.Label, other.Faction.Name), MessageTypeDefOf.RejectInput, false);
						else
							Messages.Message("AT_ShellNoTarget".Translate(), MessageTypeDefOf.RejectInput, false);
						return false;
					}
					object[] b = { t, null };
					if (!(bool)isValidMode.Invoke(comp, b))
					{
						Messages.Message((string)b[1], MessageTypeDefOf.RejectInput, false);
						return false;
					}
					MapParent mp = Find.WorldObjects.MapParentAt(t.Tile);
					if (mp?.Map == null) return false;
					Current.Game.CurrentMap = mp.Map;
					var tp = new TargetingParameters { canTargetPawns = true, canTargetBuildings = true, canTargetLocations = true };
					Find.Targeter.BeginTargeting(tp, delegate (LocalTargetInfo lt)
					{
						startAttack.Invoke(comp, new object[] { t, lt, turret });
						Current.Game.CurrentMap = turret.Map;
						Find.CameraDriver.JumpToCurrentMapLoc(turret.Position);
					}, delegate (LocalTargetInfo x)
					{
						if (x.IsValid) GenDraw.DrawTargetHighlight(x);
					}, null, null, null, null, true, null, delegate (LocalTargetInfo x)
					{
						try { drawHighlight?.Invoke(comp, new object[] { x, t }); } catch { }
					});
					return true;
				}, true, null, true, delegate
				{
					if (range > 0 && turret.Map != null) GenDraw.DrawWorldRadiusRing(turret.Map.Tile, range);
				}, delegate (GlobalTargetInfo t)
				{
					MapParent s = SettlementShelling.Target(t.Tile);
					return s == null ? "" : (TaggedString)"AT_ShellLabel".Translate(SettlementShellingComp.Get().HitsOn(s), SettlementShelling.Needed(s));
				});
				return false;
			}
			catch (Exception e)
			{
				Log.Warning("[Auto Turrets] world targeting: " + e.Message);
				return true;
			}
		}

		public static void ArrivePrefix(WorldObject __instance)
		{
			try
			{
				PlanetTile tile = (PlanetTile)projTargetTile.GetValue(__instance);
				if (Find.Maps.Any(m => m.Tile == tile)) return;
				MapParent s = SettlementShelling.Target(tile);
				if (s != null)
					SettlementShelling.Hit(s, projDef?.GetValue(__instance) as ThingDef);
			}
			catch (Exception e) { Log.WarningOnce("[Auto Turrets] shell arrival: " + e.Message, 0x5a71b2); }
		}

		public static void MapPostfix(MapParent __instance)
		{
			try { SettlementShelling.ApplyOnMap(__instance); }
			catch (Exception e) { Log.Warning("[Auto Turrets] shelled base: " + e.Message); }
		}

		public static void InspectPostfix(MapParent __instance, ref string __result)
		{
			try
			{
				int n = SettlementShellingComp.Get()?.HitsOn(__instance) ?? 0;
				if (n > 0)
					__result = (__result.NullOrEmpty() ? "" : __result + "\n") + "AT_ShellLabel".Translate(n, SettlementShelling.Needed(__instance));
			}
			catch { }
		}
	}
}
