using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace ResearchedLoadout
{
	// 2.18.0 "Battle and work":
	// - battle outfits: every outfit can have a battle outfit; drafted colonists (and, if wanted, everyone
	//   during a raid) use it, and go back to their work outfit afterwards – the dresser then takes the
	//   battle pieces off again and puts the work clothes back on.
	// - arming on draft: a drafted colonist fetches armor lying nearby and walks back to where he was drafted.
	// - clothes for the coming weather and for caravans (temperature forecast).
	// - worn gear is swapped for a good piece from storage before it falls apart.
	public sealed class SCGBattleComponent : GameComponent
	{
		// work outfit id -> battle outfit id
		public Dictionary<int, int> battleOf = new Dictionary<int, int>();
		// pawn id -> work outfit id while he wears the battle outfit
		public Dictionary<int, int> restoreTo = new Dictionary<int, int>();
		// bills added by the gear planner (load ids)
		public HashSet<string> plannerBills = new HashSet<string>();
		private List<int> k1, v1, k2, v2;

		public SCGBattleComponent(Game game) { }

		public static SCGBattleComponent Get() => Current.Game?.GetComponent<SCGBattleComponent>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref battleOf, "battleOf", LookMode.Value, LookMode.Value, ref k1, ref v1);
			Scribe_Collections.Look(ref restoreTo, "restoreTo", LookMode.Value, LookMode.Value, ref k2, ref v2);
			Scribe_Collections.Look(ref plannerBills, "plannerBills", LookMode.Value);
			if (battleOf == null) battleOf = new Dictionary<int, int>();
			if (restoreTo == null) restoreTo = new Dictionary<int, int>();
			if (plannerBills == null) plannerBills = new HashSet<string>();
		}

		public override void FinalizeInit()
		{
			base.FinalizeInit();
			BattleGear.ResetGameState();
		}

		public override void GameComponentTick()
		{
			int now = Find.TickManager.TicksGame;
			if (now % 60 == 23) BattleGear.Tick(now);
			if (now % 2500 == 1234) WornSwap.Tick(now);
			if (now % 30000 == 4321) GearPlanner.AutoTick();
		}
	}

	public static class BattleGear
	{
		private sealed class Arming
		{
			public IntVec3 spot;
			public int until;
			public int count;
			public bool moved;
		}

		private static readonly Dictionary<int, Arming> arming = new Dictionary<int, Arming>();
		private static readonly HashSet<Job> ours = new HashSet<Job>();
		private static readonly List<Apparel> unforce = new List<Apparel>();

		private static ResearchedLoadoutSettings St => ResearchedLoadoutMod.Settings;

		public static void ResetGameState()
		{
			arming.Clear();
			ours.Clear();
			unforce.Clear();
			tempCache.Clear();
		}

		public static void Patch(Harmony h)
		{
			try
			{
				h.Patch(AccessTools.PropertySetter(typeof(Pawn_DraftController), "Drafted"),
					postfix: new HarmonyMethod(typeof(BattleGear), nameof(DraftedPostfix)));
			}
			catch (Exception e)
			{
				Log.Error("[Smart Colonist Gear] draft patch failed: " + e);
			}
		}

		// ---------------- outfits ----------------
		public static ApparelPolicy ById(int id)
		{
			List<ApparelPolicy> all = Current.Game?.outfitDatabase?.AllOutfits;
			if (all == null) return null;
			for (int i = 0; i < all.Count; i++) if (all[i].id == id) return all[i];
			return null;
		}

		public static ApparelPolicy BattleFor(ApparelPolicy work)
		{
			SCGBattleComponent c = SCGBattleComponent.Get();
			int id;
			if (c == null || work == null || !c.battleOf.TryGetValue(work.id, out id)) return null;
			ApparelPolicy b = ById(id);
			return b == work ? null : b;
		}

		public static void SetBattle(ApparelPolicy work, ApparelPolicy battle)
		{
			SCGBattleComponent c = SCGBattleComponent.Get();
			if (c == null || work == null) return;
			if (battle == null || battle == work) c.battleOf.Remove(work.id);
			else c.battleOf[work.id] = battle.id;
		}

		// a new outfit: everything the work outfit allows, plus all armor and armored headgear
		public static ApparelPolicy CreateBattle(ApparelPolicy work)
		{
			OutfitDatabase db = Current.Game?.outfitDatabase;
			if (db == null || work == null) return null;
			ApparelPolicy b = db.MakeNewOutfit();
			b.label = "RL_BG_NewLabel".Translate(work.label).ToString();
			b.filter.CopyAllowancesFrom(work.filter);
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (d == null || !d.IsApparel) continue;
				ThingDef stuff = d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null;
				float armor = 0f;
				try { armor = Mathf.Max(d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff)); } catch { }
				if (armor >= 0.2f) b.filter.SetAllow(d, true);
			}
			SetBattle(work, b);
			return b;
		}

		private static bool InBattle(Pawn p)
		{
			SCGBattleComponent c = SCGBattleComponent.Get();
			return c != null && c.restoreTo.ContainsKey(p.thingIDNumber);
		}

		public static void Enter(Pawn p)
		{
			ResearchedLoadoutSettings st = St;
			SCGBattleComponent c = SCGBattleComponent.Get();
			if (st == null || !st.enabled || !st.battleOutfits || c == null || p?.outfits == null || c.restoreTo.ContainsKey(p.thingIDNumber)) return;
			ApparelPolicy work = p.outfits.CurrentApparelPolicy;
			ApparelPolicy battle = BattleFor(work);
			if (battle == null) return;
			c.restoreTo[p.thingIDNumber] = work.id;
			p.outfits.CurrentApparelPolicy = battle;
		}

		public static void Leave(Pawn p)
		{
			SCGBattleComponent c = SCGBattleComponent.Get();
			int id;
			if (c == null || p == null || !c.restoreTo.TryGetValue(p.thingIDNumber, out id)) return;
			c.restoreTo.Remove(p.thingIDNumber);
			ApparelPolicy work = ById(id);
			// the player changed the outfit by hand meanwhile: keep his choice
			if (work != null && p.outfits != null && p.outfits.CurrentApparelPolicy != BattleFor(work)) return;
			if (work != null && p.outfits != null)
			{
				p.outfits.CurrentApparelPolicy = work;
				// battle pieces the colonist fetched himself are not "forced" – the dresser may take them off
				if (p.mindState != null) p.mindState.nextApparelOptimizeTick = Find.TickManager.TicksGame;
				if (p.Spawned) EquipRush.Start(p, Find.TickManager.TicksGame);
			}
		}

		// ---------------- draft ----------------
		public static void DraftedPostfix(Pawn_DraftController __instance, bool value)
		{
			try
			{
				Pawn p = __instance?.pawn;
				ResearchedLoadoutSettings st = St;
				if (p == null || st == null || !st.enabled || !p.Spawned || !LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) return;
				int now = Find.TickManager.TicksGame;
				if (value)
				{
					Enter(p);
					if (st.armOnDraft)
						arming[p.thingIDNumber] = new Arming { spot = p.Position, until = now + 2500 };
				}
				else
				{
					arming.Remove(p.thingIDNumber);
					bool threat = st.battleOnThreat && p.Map != null && SteadyThreat.Active(p.Map);
					if (!threat) Leave(p);
				}
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] battle outfit: " + e.Message, 7310101);
			}
		}

		public static void Tick(int now)
		{
			ResearchedLoadoutSettings st = St;
			if (st == null || !st.enabled) return;
			try
			{
				ArmingTick(now, st);
				if (now % 600 == 23) ThreatTick(st);
				for (int i = unforce.Count - 1; i >= 0; i--)
				{
					Apparel a = unforce[i];
					Pawn w = a?.Wearer;
					if (w != null && w.outfits?.forcedHandler != null && w.CurJobDef != JobDefOf.Wear)
					{
						w.outfits.forcedHandler.SetForced(a, false);
						unforce.RemoveAt(i);
					}
					else if (a == null || a.Destroyed || (w == null && !a.Spawned)) unforce.RemoveAt(i);
					else if (w == null && now % 2400 == 23) unforce.RemoveAt(i);
				}
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] battle tick: " + e.Message, 7310102);
			}
		}

		private static void ArmingTick(int now, ResearchedLoadoutSettings st)
		{
			if (arming.Count == 0) return;
			foreach (int id in arming.Keys.ToList())
			{
				Arming a = arming[id];
				Pawn p = FindPawn(id);
				if (p == null || !p.Spawned || !p.Drafted || p.Downed || p.Dead || now > a.until || a.count >= 6)
				{
					arming.Remove(id);
					continue;
				}
				Job cur = p.CurJob;
				if (cur != null && ours.Contains(cur)) continue;   // still walking / dressing
				JobDef cd = cur?.def;
				bool idle = cd == null || cd == JobDefOf.Wait_Combat || cd == JobDefOf.Wait || cd == JobDefOf.Wait_MaintainPosture;
				if (!idle || p.jobs.jobQueue.Count > 0)
				{
					arming.Remove(id);   // the player gave another order
					continue;
				}
				if (CombatArmorEnemyNear(p, 22f))
				{
					arming.Remove(id);
					continue;
				}
				Apparel best = CombatArmor.FindArmor(p, st.armRange, true);
				if (best != null)
				{
					Job job = JobMaker.MakeJob(JobDefOf.Wear, best);
					job.playerForced = true;
					GearWhy.TagJob(job, GearWhy.Reason("RL_Why_BattleDraft"));
					ours.Add(job);
					unforce.Add(best);
					p.jobs.TryTakeOrderedJob(job, null, false);
					a.count++;
					a.moved = true;
					continue;
				}
				if (a.moved && st.armReturn && p.Position != a.spot && p.CanReach(a.spot, PathEndMode.OnCell, Danger.Deadly))
				{
					Job go = JobMaker.MakeJob(JobDefOf.Goto, a.spot);
					go.playerForced = true;
					p.jobs.TryTakeOrderedJob(go, null, false);
				}
				arming.Remove(id);
			}
			if (ours.Count > 200) ours.Clear();
		}

		private static void ThreatTick(ResearchedLoadoutSettings st)
		{
			SCGBattleComponent c = SCGBattleComponent.Get();
			if (c == null || Find.Maps == null) return;
			for (int m = 0; m < Find.Maps.Count; m++)
			{
				Map map = Find.Maps[m];
				if (map == null || !map.IsPlayerHome) continue;
				bool threat = st.battleOutfits && st.battleOnThreat && SteadyThreat.Active(map);
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					if (p == null || p.Dead || !LoadoutFilter.IsPlayerColonist(p) || ExemptPawns.IsExempt(p)) continue;
					if (threat) Enter(p);
					else if (!p.Drafted && c.restoreTo.ContainsKey(p.thingIDNumber)) Leave(p);
				}
			}
			// colonists who left the map in battle clothes (caravan, death) go back to work outfits
			foreach (int id in c.restoreTo.Keys.ToList())
			{
				Pawn p = FindPawn(id);
				if (p == null) { c.restoreTo.Remove(id); continue; }
				if (!p.Spawned && !p.Drafted) Leave(p);
			}
		}

		private static bool CombatArmorEnemyNear(Pawn p, float radius)
		{
			IReadOnlyList<Pawn> all = p.Map.mapPawns.AllPawnsSpawned;
			float r2 = radius * radius;
			for (int i = 0; i < all.Count; i++)
			{
				Pawn o = all[i];
				if (o != null && !o.Downed && o.HostileTo(p) && (o.Position - p.Position).LengthHorizontalSquared <= r2) return true;
			}
			return false;
		}

		private static Pawn FindPawn(int id)
		{
			foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists)
				if (p.thingIDNumber == id) return p;
			return null;
		}

		// ---------------- weather and trips ----------------
		private struct TempPlan { public int hour; public float lo, hi; }
		private static readonly Dictionary<int, TempPlan> tempCache = new Dictionary<int, TempPlan>();

		// the coldest and warmest outdoor temperature the colonist should be dressed for:
		// now, the coming hours (setting) and – when he is about to leave with a caravan – the next days
		public static void PlannedTemps(Pawn pawn, Map map, float outdoor, out float lo, out float hi)
		{
			lo = outdoor;
			hi = outdoor;
			ResearchedLoadoutSettings st = St;
			if (st == null || map == null) return;
			int hours = st.seasonClothes ? st.seasonHours : 0;
			bool trip = st.caravanClothes && pawn != null && pawn.GetLord()?.LordJob is LordJob_FormAndSendCaravan;
			if (trip) hours = Mathf.Max(hours, 96);
			if (hours <= 0) return;
			int key = map.uniqueID * 1000 + hours;
			int hourNow = GenTicks.TicksAbs / 2500;
			TempPlan tp;
			if (!tempCache.TryGetValue(key, out tp) || tp.hour != hourNow)
			{
				tp = new TempPlan { hour = hourNow, lo = float.MaxValue, hi = float.MinValue };
				PlanetTile tile = map.Tile;
				int abs = GenTicks.TicksAbs;
				for (int h = 1; h <= hours; h++)
				{
					int t = abs + h * 2500;
					float v = GenTemperature.GetTemperatureFromSeasonAtTile(t, tile) + GenTemperature.OffsetFromSunCycle(t, tile);
					if (v < tp.lo) tp.lo = v;
					if (v > tp.hi) tp.hi = v;
				}
				tempCache[key] = tp;
			}
			if (tp.lo < lo) lo = tp.lo;
			if (tp.hi > hi) hi = tp.hi;
		}

		// ---------------- settings and dialog ----------------
		public static void OutfitRow(Rect r, ApparelPolicy policy)
		{
			ApparelPolicy b = BattleFor(policy);
			string label = b != null ? "RL_BG_BattleIs".Translate(b.label).ToString() : "RL_BG_BattleNone".Translate().ToString();
			if (Widgets.ButtonText(new Rect(r.x, r.y, 320f, r.height), label))
			{
				var opts = new List<FloatMenuOption>
				{
					new FloatMenuOption("RL_BG_None".Translate(), () => SetBattle(policy, null)),
					new FloatMenuOption("RL_BG_Create".Translate(), () => CreateBattle(policy)),
				};
				foreach (ApparelPolicy o in Current.Game.outfitDatabase.AllOutfits)
				{
					if (o == policy) continue;
					ApparelPolicy oo = o;
					opts.Add(new FloatMenuOption(o.label, () => SetBattle(policy, oo)));
				}
				Find.WindowStack.Add(new FloatMenu(opts));
			}
			TooltipHandler.TipRegion(new Rect(r.x, r.y, 320f, r.height), "RL_BG_Battle_Desc".Translate());
			if (b != null && Widgets.ButtonText(new Rect(r.x + 328f, r.y, r.width - 328f, r.height), "RL_BG_Edit".Translate()))
			{
				try { Find.WindowStack.Add(new Dialog_ManageApparelPolicies(b)); } catch { }
			}
		}
	}

	// worn pieces are swapped for a good one from storage before they fall apart
	public static class WornSwap
	{
		public static void Tick(int now)
		{
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (st == null || !st.enabled || !st.wornSwap || Find.Maps == null) return;
			try
			{
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome) continue;
					foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
						TryPawn(p, map, st);
				}
			}
			catch (Exception e)
			{
				MLog.WarnOnce("[Smart Colonist Gear] worn swap: " + e.Message, 7310103);
			}
		}

		private static void TryPawn(Pawn p, Map map, ResearchedLoadoutSettings st)
		{
			if (p == null || p.Drafted || p.Downed || p.InMentalState || p.apparel == null || p.InBed() || ExemptPawns.IsExempt(p) || !LoadoutFilter.IsPlayerColonist(p)) return;
			Job cur = p.CurJob;
			if (cur != null && (cur.playerForced || cur.def == JobDefOf.Wear || cur.def == JobDefOf.LayDown)) return;
			if (SteadyThreat.Active(map)) return;
			float limit = st.wornSwapPercent / 100f;
			foreach (Apparel worn in p.apparel.WornApparel.ToList())
			{
				if (worn == null || !worn.def.useHitPoints || p.apparel.IsLocked(worn)) continue;
				if ((float)worn.HitPoints / worn.MaxHitPoints >= limit) continue;
				Apparel repl = FindReplacement(p, map, worn);
				if (repl == null) continue;
				Job job = JobMaker.MakeJob(JobDefOf.Wear, repl);
				GearWhy.TagJob(job, GearWhy.Reason("RL_Why_Worn"));
				p.jobs.StartJob(job, JobCondition.InterruptForced, null, cur != null, true);
				return;
			}
		}

		private static Apparel FindReplacement(Pawn p, Map map, Apparel worn)
		{
			ApparelPolicy policy = p.outfits?.CurrentApparelPolicy;
			float armor = LoadoutFilter.ArmorValue(worn);
			float cold = worn.GetStatValue(StatDefOf.Insulation_Cold);
			Apparel best = null;
			float bestScore = float.MinValue;
			foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
			{
				Apparel a = t as Apparel;
				if (a == null || !a.Spawned || a.IsForbidden(p) || a.IsBurning() || a.def.apparel == null) continue;
				if (a.def.useHitPoints && (float)a.HitPoints / a.MaxHitPoints < 0.8f) continue;
				if (!SameSlot(a.def, worn.def)) continue;
				if (LoadoutFilter.IsManuallyBanned(a.def) || !LoadoutFilter.IsAllowed(a.def)) continue;
				if (policy?.filter != null && !policy.filter.Allows(a)) continue;
				if (!ApparelUtility.HasPartsToWear(p, a.def) || !a.def.apparel.PawnCanWear(p, false)) continue;
				if (a.WornByCorpse && ThoughtUtility.CanGetThought(p, ThoughtDefOf.DeadMansApparel, true)) continue;
				float ar = LoadoutFilter.ArmorValue(a);
				if (ar < armor * 0.9f) continue;
				if (a.GetStatValue(StatDefOf.Insulation_Cold) < cold - 6f) continue;
				float score = ar * 10f + (a.def == worn.def ? 1f : 0f) - (a.Position - p.Position).LengthHorizontal * 0.002f;
				if (score <= bestScore) continue;
				if (!p.CanReserveAndReach(a, PathEndMode.ClosestTouch, Danger.Some)) continue;
				best = a;
				bestScore = score;
			}
			return best;
		}

		private static bool SameSlot(ThingDef a, ThingDef b)
		{
			if (a == b) return true;
			var la = a.apparel.layers; var lb = b.apparel.layers;
			var ga = a.apparel.bodyPartGroups; var gb = b.apparel.bodyPartGroups;
			if (la == null || lb == null || ga == null || gb == null) return false;
			return la.Count == lb.Count && la.All(lb.Contains) && ga.Count == gb.Count && ga.All(gb.Contains);
		}
	}
}
