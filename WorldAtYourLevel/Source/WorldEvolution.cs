using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ResearchLockedRaidGear
{
	// 2.17.0 "A world that grows":
	// - every faction has its own tech level around the world level: some lag behind, some lead.
	//   It drifts slowly towards the faction's own pace; destroying one of their settlements sets them
	//   back, trading with them helps them. Their raiders and visitors carry gear of their level.
	// - raids by real defence (optional): threat points follow your fighters, their gear and turrets
	// - studying foreign tech: a weapon or apparel above your research can be taken apart for research
	// - a new era brings a trader with the new goods a few days later
	public sealed class WorldEvolutionComponent : GameComponent
	{
		public Dictionary<int, float> offset = new Dictionary<int, float>();
		public Dictionary<int, float> pace = new Dictionary<int, float>();
		public Dictionary<int, string> note = new Dictionary<int, string>();
		private List<int> k1, k2, k3;
		private List<float> v1, v2;
		private List<string> v3;

		public WorldEvolutionComponent(Game game) { }

		public static WorldEvolutionComponent Get() => Current.Game?.GetComponent<WorldEvolutionComponent>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref offset, "offset", LookMode.Value, LookMode.Value, ref k1, ref v1);
			Scribe_Collections.Look(ref pace, "pace", LookMode.Value, LookMode.Value, ref k2, ref v2);
			Scribe_Collections.Look(ref note, "note", LookMode.Value, LookMode.Value, ref k3, ref v3);
			if (offset == null) offset = new Dictionary<int, float>();
			if (pace == null) pace = new Dictionary<int, float>();
			if (note == null) note = new Dictionary<int, string>();
		}

		public override void GameComponentTick()
		{
			if (Find.TickManager.TicksGame % 60000 == 17321) WorldEvolution.Daily(this);
		}
	}

	public static class WorldEvolution
	{
		private static ResearchLockedRaidGearSettings St => ResearchLockedRaidGearMod.Settings;

		public static void Patch(Harmony h)
		{
			Try(h, AccessTools.Method(typeof(SettlementDefeatUtility), "CheckDefeated"), "DefeatPrefix", "DefeatPostfix");
			Try(h, AccessTools.Method(typeof(TradeDeal), "TryExecute"), null, "TradePostfix");
			Try(h, AccessTools.Method(typeof(StorytellerUtility), "DefaultThreatPointsNow"), null, "PointsPostfix");
			Try(h, AccessTools.Method(typeof(ThingWithComps), "GetGizmos"), null, "GizmoPostfix");
		}

		private static void Try(Harmony h, System.Reflection.MethodInfo m, string pre, string post)
		{
			try
			{
				if (m == null) { MLog.Warn("[World at Your Level] evolution: method not found for " + (pre ?? post)); return; }
				h.Patch(m, pre == null ? null : new HarmonyMethod(typeof(WorldEvolution), pre), post == null ? null : new HarmonyMethod(typeof(WorldEvolution), post));
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] evolution patch " + m + ": " + e.Message);
			}
		}

		// ---------------- faction levels ----------------
		public static bool Tracked(Faction f)
		{
			ResearchLockedRaidGearSettings s = St;
			if (s == null || !s.enabled || !s.factionTech || f == null || f.def == null || f.IsPlayer || f.Hidden || f.temporary || !f.def.humanlikeFaction) return false;
			if (s.useManualLevel) return false;
			if (s.exemptFactions != null && s.exemptFactions.Contains(f.def.defName)) return false;
			if (f == Faction.OfAncients || f == Faction.OfAncientsHostile) return false;
			return true;
		}

		private static float PaceFor(Faction f)
		{
			// stable per faction and world: about a third lag behind, a few lead
			uint h = (uint)Gen.HashCombineInt(f.loadID, (Find.World?.info?.seedString ?? "").GetHashCode());
			float r = (h % 1000u) / 1000f;
			if (r < 0.35f) return -1.2f;
			if (r < 0.85f) return Mathf.Lerp(-0.3f, 0.3f, (r - 0.35f) / 0.5f);
			return 0.9f;
		}

		public static float Offset(Faction f)
		{
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			float v;
			return c != null && c.offset.TryGetValue(f.loadID, out v) ? v : 0f;
		}

		public static int Step(Faction f)
		{
			if (!Tracked(f)) return 0;
			int st = Mathf.RoundToInt(Offset(f));
			if (!St.factionLead && st > 0) st = 0;
			return Mathf.Clamp(st, -2, 1);
		}

		public static TechLevel WorldLevel() => ResearchLockedGearFilter.TechLimit(St);

		public static TechLevel LevelOf(Faction f)
		{
			TechLevel w = WorldLevel();
			return (TechLevel)Mathf.Clamp((int)w + Step(f), 1, 7);
		}

		// tech cap for a pawn of a lagging faction (-1 = none)
		public static int CapFor(Pawn p)
		{
			try
			{
				Faction f = p?.Faction;
				if (f == null || Step(f) >= 0) return -1;
				return (int)LevelOf(f);
			}
			catch { return -1; }
		}

		// tech level a pawn of a leading faction may carry above the world (-1 = none)
		public static int LeadFor(Pawn p)
		{
			try
			{
				ResearchLockedRaidGearSettings s = St;
				Faction f = p?.Faction;
				if (f == null || s == null || s.onlyCrafted || Step(f) <= 0) return -1;
				return (int)LevelOf(f);
			}
			catch { return -1; }
		}

		private static void Nudge(Faction f, float by, string why)
		{
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			if (c == null || !Tracked(f)) return;
			int before = Step(f);
			float v;
			c.offset.TryGetValue(f.loadID, out v);
			c.offset[f.loadID] = Mathf.Clamp(v + by, -2.4f, 1.4f);
			if (why != null) c.note[f.loadID] = why;
			Announce(f, before);
		}

		private static void Announce(Faction f, int before)
		{
			int after = Step(f);
			if (after == before || St == null || !St.worldMessages) return;
			Messages.Message((after > before ? "RLRG_EV_FactionUp" : "RLRG_EV_FactionDown").Translate(f.Name, LevelOf(f).ToStringHuman()),
				after > before ? MessageTypeDefOf.NegativeEvent : MessageTypeDefOf.PositiveEvent, false);
		}

		public static void Daily(WorldEvolutionComponent c)
		{
			try
			{
				if (St == null || !St.enabled || !St.factionTech || Find.FactionManager == null) return;
				foreach (Faction f in Find.FactionManager.AllFactionsListForReading)
				{
					if (!Tracked(f) || f.defeated) continue;
					float p;
					if (!c.pace.TryGetValue(f.loadID, out p)) c.pace[f.loadID] = p = PaceFor(f);
					float v;
					c.offset.TryGetValue(f.loadID, out v);
					int before = Step(f);
					float step = 0.05f * Mathf.Sign(p - v);
					if (Mathf.Abs(p - v) < 0.05f) step = p - v;
					v += step + Rand.Range(-0.015f, 0.015f);
					c.offset[f.loadID] = Mathf.Clamp(v, -2.4f, 1.4f);
					Announce(f, before);
				}
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] faction levels: " + e.Message);
			}
		}

		public static void DefeatPrefix(Settlement factionBase, out Faction __state)
		{
			__state = factionBase?.Faction;
		}

		public static void DefeatPostfix(Settlement factionBase, Faction __state)
		{
			try
			{
				if (__state == null || factionBase == null || Find.WorldObjects.Contains(factionBase)) return;
				Nudge(__state, -0.8f, "RLRG_EV_NoteRaided".Translate().ToString());
			}
			catch { }
		}

		public static void TradePostfix(bool __result, bool actuallyTraded)
		{
			try
			{
				if (!__result || !actuallyTraded) return;
				Faction f = TradeSession.trader?.Faction;
				if (f != null) Nudge(f, 0.06f, "RLRG_EV_NoteTrade".Translate().ToString());
			}
			catch { }
		}

		// ---------------- raids by real defence ----------------
		public static void PointsPostfix(IIncidentTarget target, ref float __result)
		{
			try
			{
				ResearchLockedRaidGearSettings s = St;
				Map map = target as Map;
				if (s == null || !s.enabled || !s.raidByDefense || map == null || !map.IsPlayerHome) return;
				float def = DefensePoints(map);
				if (def <= 0f) return;
				float w = Mathf.Clamp01(s.defenseWeight / 100f);
				float scale = Find.Storyteller?.difficulty?.threatScale ?? 1f;
				__result = Mathf.Max(35f, Mathf.Lerp(__result, def * scale, w));
			}
			catch { }
		}

		public static float DefensePoints(Map map)
		{
			float pts = 0f;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (p == null || p.Downed || p.WorkTagIsDisabled(WorkTags.Violent)) continue;
				float v = 45f;
				ThingWithComps w = p.equipment?.Primary;
				if (w != null) v += Mathf.Min(140f, w.MarketValue * 0.12f);
				v += 70f * Mathf.Max(p.GetStatValue(StatDefOf.ArmorRating_Sharp), 0f);
				int sk = Mathf.Max(p.skills?.GetSkill(SkillDefOf.Shooting)?.Level ?? 0, p.skills?.GetSkill(SkillDefOf.Melee)?.Level ?? 0);
				v += sk * 4f;
				pts += v;
			}
			foreach (Building b in map.listerBuildings.allBuildingsColonist)
			{
				if (b is Building_Turret) pts += 60f + Mathf.Min(200f, b.MarketValue * 0.05f);
			}
			foreach (Pawn m in map.mapPawns.SpawnedColonyMechs)
				if (m != null && !m.Downed) pts += m.kindDef?.combatPower ?? 100f;
			return pts;
		}

		// ---------------- study foreign tech ----------------
		public static IEnumerable<Gizmo> GizmoPostfix(IEnumerable<Gizmo> __result, ThingWithComps __instance)
		{
			foreach (Gizmo g in __result) yield return g;
			ResearchLockedRaidGearSettings s = St;
			if (s == null || !s.enabled || !s.studyTech || __instance == null || !__instance.Spawned || __instance.Map == null || !__instance.Map.IsPlayerHome) yield break;
			ThingDef d = __instance.def;
			if (d == null || d.category != ThingCategory.Item || (!d.IsWeapon && !d.IsApparel)) yield break;
			if (Find.Selector.SingleSelectedThing != __instance) yield break;
			bool locked;
			try { locked = !ResearchLockedGearFilter.IsResearchAllowed(d); } catch { yield break; }
			if (!locked) yield break;
			ThingWithComps t = __instance;
			ResearchProjectDef proj = ProjectFor(d);
			var cmd = new Command_Action
			{
				defaultLabel = "RLRG_EV_StudyBtn".Translate(),
				defaultDesc = proj != null ? "RLRG_EV_StudyBtn_Desc".Translate(proj.LabelCap, Mathf.RoundToInt(Amount(t, proj))).ToString() : "RLRG_EV_StudyNone".Translate().ToString(),
				icon = d.uiIcon ?? BaseContent.BadTex,
				iconDrawScale = 0.85f,
				action = () => Study(t)
			};
			if (proj == null) cmd.Disable("RLRG_EV_StudyNone".Translate());
			else if (!t.Map.listerBuildings.allBuildingsColonist.Any(b => b is Building_ResearchBench)) cmd.Disable("RLRG_EV_StudyNoBench".Translate());
			yield return cmd;
		}

		private static ResearchProjectDef ProjectFor(ThingDef d)
		{
			var open = DefDatabase<ResearchProjectDef>.AllDefsListForReading.Where(r => r != null && !r.IsFinished).ToList();
			ResearchProjectDef best = open.Where(r => r.UnlockedDefs != null && r.UnlockedDefs.Contains(d)).OrderBy(r => r.CostApparent).FirstOrDefault();
			if (best != null) return best;
			int lv = (int)(d.techLevel == TechLevel.Undefined ? TechLevel.Industrial : d.techLevel);
			return open.Where(r => r.CanStartNow && (int)r.techLevel <= lv).OrderByDescending(r => (int)r.techLevel).ThenBy(r => r.CostApparent).FirstOrDefault();
		}

		private static float Amount(Thing t, ResearchProjectDef proj)
		{
			float v = t.MarketValue * 2.5f;
			return Mathf.Min(v, proj.CostApparent * 0.35f, Mathf.Max(0f, proj.CostApparent - Find.ResearchManager.GetProgress(proj)));
		}

		private static void Study(Thing t)
		{
			try
			{
				ResearchProjectDef proj = ProjectFor(t.def);
				if (proj == null || t.Destroyed) return;
				float amount = Amount(t, proj);
				string label = t.LabelCap;
				Find.ResearchManager.AddProgress(proj, amount);
				t.Destroy(DestroyMode.Vanish);
				Messages.Message("RLRG_EV_Studied".Translate(label, proj.LabelCap, Mathf.RoundToInt(amount)), MessageTypeDefOf.PositiveEvent, false);
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] study: " + e.Message);
			}
		}

		// ---------------- a new era ----------------
		public static void OnLevelRise(TechLevel level)
		{
			try
			{
				ResearchLockedRaidGearSettings s = St;
				if (s == null || !s.enabled || !s.eraEvents) return;
				Map map = Find.AnyPlayerHomeMap;
				IncidentDef inc = IncidentDefOf.TraderCaravanArrival;
				if (map == null || inc == null) return;
				Faction f = Find.FactionManager.AllFactionsListForReading
					.Where(x => x != null && !x.IsPlayer && !x.HostileTo(Faction.OfPlayer) && !x.Hidden && !x.temporary && x.def.caravanTraderKinds != null && x.def.caravanTraderKinds.Count > 0 && (int)LevelOf(x) >= (int)level)
					.RandomElementWithFallback(null);
				if (f == null) return;
				IncidentParms parms = StorytellerUtility.DefaultParmsNow(inc.category, map);
				parms.faction = f;
				parms.forced = true;
				int at = Find.TickManager.TicksGame + Rand.RangeInclusive(60000, 120000);
				Find.Storyteller.incidentQueue.Add(new QueuedIncident(new FiringIncident(inc, null, parms), at, 30000));
				Find.LetterStack.ReceiveLetter("RLRG_EV_EraTraderTitle".Translate(), "RLRG_EV_EraTraderText".Translate(f.Name, level.ToStringHuman()), LetterDefOf.PositiveEvent);
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] era event: " + e.Message);
			}
		}

		// ---------------- World window ----------------
		public static void DrawFactions(Listing_Standard list)
		{
			ResearchLockedRaidGearSettings s = St;
			if (s == null || Find.FactionManager == null) return;
			list.GapLine();
			Text.Font = GameFont.Medium;
			list.Label("RLRG_EV_FactionsTitle".Translate());
			Text.Font = GameFont.Small;
			if (!s.factionTech)
			{
				GUI.color = Color.gray;
				list.Label("RLRG_EV_FactionsOff".Translate());
				GUI.color = Color.white;
				return;
			}
			TechLevel world = WorldLevel();
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			foreach (Faction f in Find.FactionManager.AllFactionsListForReading.Where(Tracked).OrderByDescending(x => Offset(x)))
			{
				Rect r = list.GetRect(24f);
				Widgets.DrawHighlightIfMouseover(r);
				GUI.color = f.Color;
				Widgets.Label(new Rect(r.x, r.y, r.width * 0.42f, 24f), f.Name.Truncate(r.width * 0.42f));
				int st = Step(f);
				GUI.color = st > 0 ? new Color(1f, 0.55f, 0.45f) : st < 0 ? new Color(0.55f, 0.85f, 0.55f) : Color.white;
				Widgets.Label(new Rect(r.x + r.width * 0.43f, r.y, r.width * 0.2f, 24f), LevelOf(f).ToStringHuman().CapitalizeFirst());
				GUI.color = Color.gray;
				float p;
				string trend = "";
				if (c != null && c.pace.TryGetValue(f.loadID, out p))
				{
					float o = Offset(f);
					trend = p > o + 0.1f ? "RLRG_EV_Rising".Translate().ToString() : p < o - 0.1f ? "RLRG_EV_Falling".Translate().ToString() : "RLRG_EV_Steady".Translate().ToString();
				}
				string n;
				if (c != null && c.note.TryGetValue(f.loadID, out n) && !n.NullOrEmpty()) trend += " · " + n;
				Widgets.Label(new Rect(r.x + r.width * 0.64f, r.y, r.width * 0.36f, 24f), trend);
				GUI.color = Color.white;
				TooltipHandler.TipRegion(r, "RLRG_EV_FactionTip".Translate(f.Name, LevelOf(f).ToStringHuman(), world.ToStringHuman()));
			}
			if (s.raidByDefense && Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome)
			{
				list.Gap(6f);
				list.Label("RLRG_EV_DefenseNow".Translate(Mathf.RoundToInt(DefensePoints(Find.CurrentMap)), Mathf.RoundToInt(StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap))));
			}
		}
	}
}
