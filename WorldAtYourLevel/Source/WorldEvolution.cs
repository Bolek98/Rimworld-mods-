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
		// 2.16.0: world news, research data sold to factions
		public List<string> news = new List<string>();
		public Dictionary<int, string> sold = new Dictionary<int, string>();
		public Dictionary<int, int> lastSale = new Dictionary<int, int>();
		private List<int> k1, k2, k3, k4, k5;
		private List<float> v1, v2;
		private List<string> v3, v4;
		private List<int> v5;

		public WorldEvolutionComponent(Game game) { }

		public static WorldEvolutionComponent Get() => Current.Game?.GetComponent<WorldEvolutionComponent>();

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref offset, "offset", LookMode.Value, LookMode.Value, ref k1, ref v1);
			Scribe_Collections.Look(ref pace, "pace", LookMode.Value, LookMode.Value, ref k2, ref v2);
			Scribe_Collections.Look(ref note, "note", LookMode.Value, LookMode.Value, ref k3, ref v3);
			if (offset == null) offset = new Dictionary<int, float>();
			if (pace == null) pace = new Dictionary<int, float>();
			Scribe_Collections.Look(ref news, "news", LookMode.Value);
			Scribe_Collections.Look(ref sold, "soldData", LookMode.Value, LookMode.Value, ref k4, ref v4);
			Scribe_Collections.Look(ref lastSale, "lastSale", LookMode.Value, LookMode.Value, ref k5, ref v5);
			if (note == null) note = new Dictionary<int, string>();
			if (news == null) news = new List<string>();
			if (sold == null) sold = new Dictionary<int, string>();
			if (lastSale == null) lastSale = new Dictionary<int, int>();
		}

		public override void GameComponentTick()
		{
			int t = Find.TickManager.TicksGame;
			if (t % 60000 == 17321) WorldEvolution.Daily(this);
			if (t % 180000 == 91234) WorldEvolution.Relations();   // 2.16.0
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
			Try(h, AccessTools.Method(typeof(FactionDialogMaker), "FactionDialogFor"), null, "DialogPostfix");   // 2.16.0
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
			if (!s.autoExempt && s.exemptFactions != null && s.exemptFactions.Contains(f.def.defName)) return false;   // 2.18.0: automatic mode – every faction grows with the world
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

		private static void Nudge(Faction f, float by, string why, string cause = null)
		{
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			if (c == null || !Tracked(f)) return;
			int before = Step(f);
			float v;
			c.offset.TryGetValue(f.loadID, out v);
			c.offset[f.loadID] = Mathf.Clamp(v + by, -2.4f, 1.4f);
			if (why != null) c.note[f.loadID] = why;
			Announce(f, before, cause);
		}

		// 2.16.0: world news – a short flavored line when a faction rises or falls a level,
		// as a message or a letter (setting), always kept in the World tab history
		private static void Announce(Faction f, int before, string cause = null)
		{
			int after = Step(f);
			if (after == before || St == null) return;
			try
			{
				int lv = (int)LevelOf(f);
				string text;
				if (after > before)
				{
					string key = "RLRG_NW_Up_" + lv;
					text = (key.CanTranslate() ? key.Translate(f.Name) : "RLRG_EV_FactionUp".Translate(f.Name, LevelOf(f).ToStringHuman())).ToString();
					if (cause == "trade") text += "RLRG_NW_CauseTrade".Translate().ToString();
					else if (cause == "data") text += "RLRG_NW_CauseData".Translate().ToString();
				}
				else
				{
					string why = cause == "raid" ? "RLRG_NW_CauseRaid".Translate().ToString() : "";
					text = "RLRG_NW_Down".Translate(f.Name, why, LevelOf(f).ToStringHuman()).ToString();
				}
				WorldEvolutionComponent c = WorldEvolutionComponent.Get();
				if (c != null)
				{
					c.news.Add(Find.TickManager.TicksAbs + "|" + text);
					while (c.news.Count > 40) c.news.RemoveAt(0);
				}
				int mode = St.newsMode;
				if (mode == 2)
				{
					Settlement home = Find.WorldObjects?.Settlements?.FirstOrDefault(x => x.Faction == f);
					Find.LetterStack.ReceiveLetter("RLRG_NW_Title".Translate(), text, LetterDefOf.NeutralEvent, home != null ? new LookTargets(home) : null, f);
				}
				else if (mode == 1)
					Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] world news: " + e.Message);
			}
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
				Nudge(__state, -0.8f, "RLRG_EV_NoteRaided".Translate().ToString(), "raid");
			}
			catch { }
		}

		public static void TradePostfix(bool __result, bool actuallyTraded)
		{
			try
			{
				if (!__result || !actuallyTraded) return;
				Faction f = TradeSession.trader?.Faction;
				if (f != null) Nudge(f, 0.06f, "RLRG_EV_NoteTrade".Translate().ToString(), "trade");
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
					.Where(x => x != null && !x.IsPlayer && !x.HostileTo(Faction.OfPlayerSilentFail) && !x.Hidden && !x.temporary && x.def.caravanTraderKinds != null && x.def.caravanTraderKinds.Count > 0 && (int)LevelOf(x) >= (int)level)
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

		// ---------------- 2.16.0: relations by tech level ----------------
		// Factions far apart in tech envy each other, factions at the same level trade and get along.
		// A small step every 3 days towards a target goodwill; player relations are never touched.
		private static bool RelationsOn
		{
			get { ResearchLockedRaidGearSettings s = St; return s != null && s.enabled && s.factionTech && s.factionRelations; }
		}

		private static int TargetGoodwill(int gap) => gap == 0 ? 40 : gap >= 2 ? -40 : int.MinValue;

		public static void Relations()
		{
			try
			{
				if (!RelationsOn || Find.FactionManager == null) return;
				int strength = Mathf.Clamp(St.relationStrength, 1, 10);
				List<Faction> all = Find.FactionManager.AllFactionsListForReading.Where(x => Tracked(x) && !x.defeated).ToList();
				for (int i = 0; i < all.Count; i++)
					for (int j = i + 1; j < all.Count; j++)
					{
						Faction a = all[i], b = all[j];
						int target = TargetGoodwill(Mathf.Abs((int)LevelOf(a) - (int)LevelOf(b)));
						if (target == int.MinValue) continue;
						int now = a.GoodwillWith(b);
						if (now == target) continue;
						int change = Mathf.Clamp(target - now, -strength, strength);
						if (!a.CanChangeGoodwillFor(b, change)) continue;
						a.TryAffectGoodwillWith(b, change, false, false, null, null);
					}
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] faction relations: " + e.Message);
			}
		}

		private static void DrawRelations(Listing_Standard list)
		{
			if (!RelationsOn) return;
			list.Gap(8f);
			Text.Font = GameFont.Medium;
			list.Label("RLRG_EV_RelTitle".Translate());
			Text.Font = GameFont.Small;
			List<Faction> all = Find.FactionManager.AllFactionsListForReading.Where(x => Tracked(x) && !x.defeated).ToList();
			int shown = 0;
			for (int i = 0; i < all.Count && shown < 14; i++)
				for (int j = i + 1; j < all.Count && shown < 14; j++)
				{
					Faction a = all[i], b = all[j];
					int la = (int)LevelOf(a), lb = (int)LevelOf(b);
					int gap = Mathf.Abs(la - lb);
					int gw = a.GoodwillWith(b);
					string line = null;
					if (gap == 0 && gw >= 20) line = "RLRG_EV_RelFriends".Translate(a.Name, b.Name, gw).ToString();
					else if (gap >= 2 && gw <= -10)
					{
						Faction low = la < lb ? a : b, high = la < lb ? b : a;
						line = "RLRG_EV_RelEnvy".Translate(low.Name, high.Name, gap, gw).ToString();
					}
					if (line == null) continue;
					Rect r = list.GetRect(Text.CalcHeight(line, list.ColumnWidth));
					GUI.color = Color.gray;
					Widgets.Label(r, line);
					GUI.color = Color.white;
					shown++;
				}
			if (shown == 0)
			{
				GUI.color = Color.gray;
				list.Label("RLRG_EV_RelNone".Translate());
				GUI.color = Color.white;
			}
		}

		private static void DrawNews(Listing_Standard list)
		{
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			list.Gap(8f);
			Text.Font = GameFont.Medium;
			list.Label("RLRG_NW_History".Translate());
			Text.Font = GameFont.Small;
			if (c == null || c.news.Count == 0)
			{
				GUI.color = Color.gray;
				list.Label("RLRG_NW_HistoryEmpty".Translate());
				GUI.color = Color.white;
				return;
			}
			Vector2 longLat = Find.WorldGrid != null && Find.AnyPlayerHomeMap != null ? Find.WorldGrid.LongLatOf(Find.AnyPlayerHomeMap.Tile) : Vector2.zero;
			for (int i = c.news.Count - 1, n = 0; i >= 0 && n < 12; i--, n++)
			{
				string e = c.news[i];
				int bar = e.IndexOf('|');
				long ticks;
				string when = bar > 0 && long.TryParse(e.Substring(0, bar), out ticks) ? GenDate.DateFullStringAt(ticks, longLat) : "";
				string line = (when.NullOrEmpty() ? "" : when + " – ") + (bar >= 0 ? e.Substring(bar + 1) : e);
				Rect r = list.GetRect(Text.CalcHeight(line, list.ColumnWidth));
				GUI.color = n == 0 ? Color.white : Color.gray;
				Widgets.Label(r, line);
				GUI.color = Color.white;
			}
		}

		// ---------------- 2.16.0: selling research data ----------------
		private const int SaleCooldown = 7 * 60000;

		private static TechLevel PlayerLevel()
		{
			int best = (int)TechLevel.Neolithic;
			foreach (ResearchProjectDef r in DefDatabase<ResearchProjectDef>.AllDefsListForReading)
				if (r != null && r.IsFinished && (int)r.techLevel > best) best = (int)r.techLevel;
			return (TechLevel)best;
		}

		private static bool AlreadySold(WorldEvolutionComponent c, Faction f, ResearchProjectDef r)
		{
			string have;
			return c != null && c.sold.TryGetValue(f.loadID, out have) && have != null && ("," + have + ",").Contains("," + r.defName + ",");
		}

		private static List<ResearchProjectDef> Sellable(Faction f)
		{
			WorldEvolutionComponent c = WorldEvolutionComponent.Get();
			int lv = (int)LevelOf(f);
			return DefDatabase<ResearchProjectDef>.AllDefsListForReading
				.Where(r => r != null && r.IsFinished && (int)r.techLevel > lv && r.baseCost > 0f && !AlreadySold(c, f, r))
				.OrderByDescending(r => (int)r.techLevel).ThenByDescending(r => r.baseCost).Take(8).ToList();
		}

		private static int Price(Faction f, ResearchProjectDef r)
		{
			int gap = Mathf.Max(1, (int)r.techLevel - (int)LevelOf(f));
			return Mathf.Clamp(Mathf.RoundToInt(r.baseCost * 0.12f * (1f + 0.5f * (gap - 1)) / 10f) * 10, 50, 2500);
		}

		private static int Goodwill(Faction f, ResearchProjectDef r)
		{
			int gap = Mathf.Max(1, (int)r.techLevel - (int)LevelOf(f));
			return Mathf.Clamp(3 + gap * 2, 3, 12);
		}

		public static void DialogPostfix(Pawn negotiator, Faction faction, DiaNode __result)
		{
			try
			{
				ResearchLockedRaidGearSettings s = St;
				if (s == null || !s.enabled || !s.factionTech || !s.sellData || __result == null || negotiator == null || faction == null) return;
				Map map = negotiator.Map;
				if (map == null || !map.IsPlayerHome || !Tracked(faction) || faction.HostileTo(Faction.OfPlayerSilentFail)) return;
				if ((int)LevelOf(faction) >= (int)PlayerLevel()) return;
				DiaOption opt = new DiaOption("RLRG_SD_Option".Translate());
				WorldEvolutionComponent c = WorldEvolutionComponent.Get();
				int last;
				List<ResearchProjectDef> list = Sellable(faction);
				if (negotiator.skills != null && negotiator.skills.GetSkill(SkillDefOf.Social).TotallyDisabled)
					opt.Disable("WorkTypeDisablesOption".Translate(SkillDefOf.Social.label));
				else if (c != null && c.lastSale.TryGetValue(faction.loadID, out last) && Find.TickManager.TicksGame - last < SaleCooldown)
					opt.Disable("RLRG_SD_Cooldown".Translate(((SaleCooldown - (Find.TickManager.TicksGame - last)) / 60000f).ToString("0.#")));
				else if (list.Count == 0)
					opt.Disable("RLRG_SD_Nothing".Translate());
				else
					opt.link = SaleNode(negotiator, faction, list);
				int at = Mathf.Max(0, __result.options.Count - 1);
				__result.options.Insert(at, opt);
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] research data dialog: " + e.Message);
			}
		}

		private static DiaNode SaleNode(Pawn negotiator, Faction faction, List<ResearchProjectDef> list)
		{
			DiaNode node = new DiaNode("RLRG_SD_Intro".Translate(faction.Name, LevelOf(faction).ToStringHuman()));
			foreach (ResearchProjectDef r in list)
			{
				ResearchProjectDef proj = r;
				int silver = Price(faction, proj), gw = Goodwill(faction, proj);
				DiaOption o = new DiaOption("RLRG_SD_Item".Translate(proj.LabelCap, silver, gw));
				o.action = () => Sell(negotiator, faction, proj, silver, gw);
				o.link = new DiaNode("RLRG_SD_Done".Translate(faction.Name, silver, proj.LabelCap))
				{
					options = { new DiaOption("OK".Translate()) { linkLateBind = FactionDialogMaker.ResetToRoot(faction, negotiator) } }
				};
				node.options.Add(o);
			}
			node.options.Add(new DiaOption("RLRG_SD_Back".Translate()) { linkLateBind = FactionDialogMaker.ResetToRoot(faction, negotiator) });
			return node;
		}

		private static void Sell(Pawn negotiator, Faction faction, ResearchProjectDef proj, int silver, int gw)
		{
			try
			{
				Map map = negotiator.Map;
				WorldEvolutionComponent c = WorldEvolutionComponent.Get();
				if (map == null || c == null) return;
				List<Thing> pay = new List<Thing>();
				int left = silver;
				while (left > 0)
				{
					Thing t = ThingMaker.MakeThing(ThingDefOf.Silver);
					t.stackCount = Mathf.Min(left, ThingDefOf.Silver.stackLimit);
					left -= t.stackCount;
					pay.Add(t);
				}
				DropPodUtility.DropThingsNear(DropCellFinder.TradeDropSpot(map), map, pay, 110, false, false, true, false);
				faction.TryAffectGoodwillWith(Faction.OfPlayerSilentFail, gw, true, false, null, null);
				string have;
				c.sold.TryGetValue(faction.loadID, out have);
				c.sold[faction.loadID] = have.NullOrEmpty() ? proj.defName : have + "," + proj.defName;
				c.lastSale[faction.loadID] = Find.TickManager.TicksGame;
				Nudge(faction, Mathf.Clamp(proj.baseCost / 8000f, 0.08f, 0.35f), "RLRG_EV_NoteData".Translate().ToString(), "data");
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] research data sale: " + e.Message);
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
			DrawRelations(list);
			DrawNews(list);
			if (s.raidByDefense && Find.CurrentMap != null && Find.CurrentMap.IsPlayerHome)
			{
				list.Gap(6f);
				list.Label("RLRG_EV_DefenseNow".Translate(Mathf.RoundToInt(DefensePoints(Find.CurrentMap)), Mathf.RoundToInt(StorytellerUtility.DefaultThreatPointsNow(Find.CurrentMap))));
			}
		}
	}
}
