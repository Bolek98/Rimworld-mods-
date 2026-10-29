using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace RandysTable
{
	// ---------------------------------------------------------------------------
	// 0.2.0: Dice the Gambler's own events – bets (choice letters), challenges with a deadline,
	// situations that last a few days and twisted vanilla events. Every event belongs to one face
	// of the die; after a roll there is a chance that one of that face's events comes that day.
	// Texts: RT_EV_<id> (title), RT_EV_<id>_Text, options RT_EV_<id>_O1 … , results RT_EV_<id>_R…
	// ---------------------------------------------------------------------------
	public abstract class DiceEvent
	{
		public string id;
		public int face;
		public float weight = 1f;
		public bool threat;   // 0.2.1: a threat – follows the difficulty, never on peaceful difficulties

		public string Title => ("RT_EV_" + id).Translate();
		public string Text => ("RT_EV_" + id + "_Text").Translate();
		public string Key(string suffix) => "RT_EV_" + id + "_" + suffix;

		public virtual bool CanRun(Map map) => true;
		public abstract void Run(Map map);

		protected static List<Pawn> Colonists(Map map) =>
			map.mapPawns.FreeColonistsSpawned.Where(p => p != null && !p.Dead).ToList();

		protected void Letter(Map map, LetterDef def, LookTargets look = null, string extra = null)
		{
			string text = Text + (extra.NullOrEmpty() ? "" : "\n\n" + extra);
			Find.LetterStack.ReceiveLetter("RT_EV_Prefix".Translate(Title), text, def, look ?? LookTargets.Invalid);
		}
	}

	// colonists get a hediff (and maybe a thought) for a while
	public sealed class DE_Situation : DiceEvent
	{
		public string hediff, thought;
		public bool bad;

		public override bool CanRun(Map map) => DefDatabase<HediffDef>.GetNamedSilentFail(hediff) != null && Colonists(map).Count > 0;

		public override void Run(Map map)
		{
			HediffDef h = DefDatabase<HediffDef>.GetNamedSilentFail(hediff);
			foreach (Pawn p in Colonists(map))
			{
				Hediff old = p.health.hediffSet.GetFirstHediffOfDef(h);
				if (old != null) p.health.RemoveHediff(old);
				p.health.AddHediff(h);
				DiceReward.Think(p, thought);
			}
			Letter(map, bad ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent);
		}
	}

	// colonists get a thought
	public sealed class DE_Thought : DiceEvent
	{
		public string thought;
		public bool bad;

		public override void Run(Map map)
		{
			foreach (Pawn p in Colonists(map)) DiceReward.Think(p, thought);
			Letter(map, bad ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent);
		}
	}

	// a game condition for a while
	public sealed class DE_Condition : DiceEvent
	{
		public string condition, alt, thought;
		public float days = 1.5f;
		public bool bad = true;

		private GameConditionDef Def(Map map)
		{
			GameConditionDef d = DefDatabase<GameConditionDef>.GetNamedSilentFail(condition);
			if (alt != null && map.mapTemperature.OutdoorTemp > 10f) d = DefDatabase<GameConditionDef>.GetNamedSilentFail(alt) ?? d;
			return d;
		}

		public override bool CanRun(Map map) => Def(map) != null && !map.gameConditionManager.ConditionIsActive(Def(map));

		public override void Run(Map map)
		{
			GameConditionDef d = Def(map);
			GameCondition c = GameConditionMaker.MakeCondition(d, Mathf.RoundToInt(days * 60000f));
			map.gameConditionManager.RegisterCondition(c);
			if (thought != null) foreach (Pawn p in Colonists(map)) DiceReward.Think(p, thought);
			Letter(map, bad ? LetterDefOf.NegativeEvent : LetterDefOf.PositiveEvent, null, d.LabelCap);
		}
	}

	// something falls from the sky
	public sealed class DE_Gift : DiceEvent
	{
		public Func<Map, List<Thing>> make;

		public override void Run(Map map)
		{
			List<Thing> things = make(map);
			if (things == null || things.Count == 0) return;
			IntVec3 spot = DropCellFinder.TradeDropSpot(map);
			DropPodUtility.DropThingsNear(spot, map, things, 110, false, false, true);
			Letter(map, LetterDefOf.PositiveEvent, new TargetInfo(spot, map), DiceReward.Describe(things));
		}
	}

	// just do something
	public sealed class DE_Action : DiceEvent
	{
		public Func<Map, bool> can;
		public Action<Map> act;
		public bool bad;
		public bool letter = true;

		public override bool CanRun(Map map) => can == null || can(map);

		public override void Run(Map map)
		{
			if (letter) Letter(map, bad ? LetterDefOf.ThreatSmall : LetterDefOf.NeutralEvent);
			act(map);
		}
	}

	// a bet: a letter with options
	public sealed class DE_Choice : DiceEvent
	{
		public List<KeyValuePair<string, Action<Map>>> options = new List<KeyValuePair<string, Action<Map>>>();
		public Func<Map, bool> can;

		public override bool CanRun(Map map) => can == null || can(map);

		public DE_Choice Opt(string suffix, Action<Map> act)
		{
			options.Add(new KeyValuePair<string, Action<Map>>(suffix, act));
			return this;
		}

		public override void Run(Map map)
		{
			LetterDef def = DefDatabase<LetterDef>.GetNamedSilentFail("RT_DiceChoice") ?? LetterDefOf.NeutralEvent;
			ChoiceLetter_Dice l = (ChoiceLetter_Dice)LetterMaker.MakeLetter(def);
			l.Label = "RT_EV_Prefix".Translate(Title);
			l.Text = Text;
			l.eventId = id;
			l.map = map;
			Find.LetterStack.ReceiveLetter(l);
		}
	}

	// a challenge: reach a goal before the deadline
	public sealed class DE_Challenge : DiceEvent
	{
		public enum Kind { Record, NoBreaks, NoDeaths }
		public Kind kind = Kind.Record;
		public string record;
		public float perColonist = 1f, minGoal = 1f;
		public float days = 2f;
		public Action<Map> reward, penalty;
		public Action<Map> onStart;

		public override bool CanRun(Map map) => Colonists(map).Count > 0 && (kind != Kind.Record || DefDatabase<RecordDef>.GetNamedSilentFail(record) != null)
			&& !(DiceEvents.Comp?.challenges.Any(c => c.id == id) ?? false);

		public override void Run(Map map)
		{
			int n = Colonists(map).Count;
			var c = new DiceChallenge
			{
				id = id,
				mapId = map.uniqueID,
				start = Find.TickManager.TicksGame,
				end = Find.TickManager.TicksGame + Mathf.RoundToInt(days * 60000f),
				goal = Mathf.Max(minGoal, Mathf.Round(perColonist * n * DiceDifficulty.GoalFactor)),
			};
			c.Init(this, map);
			DiceEvents.Comp?.challenges.Add(c);
			Letter(map, LetterDefOf.NeutralEvent, null, "RT_EV_ChallengeGoal".Translate(c.GoalText(this), days.ToString("0.#")));
			onStart?.Invoke(map);
		}
	}

	public sealed class DiceChallenge : IExposable
	{
		public string id;
		public int mapId, start, end;
		public float goal;
		public Dictionary<int, float> baseline = new Dictionary<int, float>();
		public List<int> who = new List<int>();
		private List<int> tk; private List<float> tv;

		public void ExposeData()
		{
			Scribe_Values.Look(ref id, "id");
			Scribe_Values.Look(ref mapId, "mapId");
			Scribe_Values.Look(ref start, "start");
			Scribe_Values.Look(ref end, "end");
			Scribe_Values.Look(ref goal, "goal");
			Scribe_Collections.Look(ref baseline, "baseline", LookMode.Value, LookMode.Value, ref tk, ref tv);
			Scribe_Collections.Look(ref who, "who", LookMode.Value);
			if (baseline == null) baseline = new Dictionary<int, float>();
			if (who == null) who = new List<int>();
		}

		public Map Map => Find.Maps?.FirstOrDefault(m => m.uniqueID == mapId);

		public void Init(DE_Challenge e, Map map)
		{
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				who.Add(p.thingIDNumber);
				if (e.kind == DE_Challenge.Kind.Record) baseline[p.thingIDNumber] = Value(e, p);
			}
		}

		private static float Value(DE_Challenge e, Pawn p)
		{
			RecordDef r = DefDatabase<RecordDef>.GetNamedSilentFail(e.record);
			return r == null || p.records == null ? 0f : p.records.GetValue(r);
		}

		public float Progress(DE_Challenge e)
		{
			Map map = Map;
			if (map == null || e.kind != DE_Challenge.Kind.Record) return 0f;
			float sum = 0f;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				float b;
				if (!baseline.TryGetValue(p.thingIDNumber, out b)) baseline[p.thingIDNumber] = b = Value(e, p);
				sum += Mathf.Max(0f, Value(e, p) - b);
			}
			return sum;
		}

		public string GoalText(DE_Challenge e)
		{
			if (e.kind == DE_Challenge.Kind.Record) return e.Key("Goal").Translate(Mathf.RoundToInt(goal));
			return e.Key("Goal").Translate();
		}

		public string Status(DE_Challenge e)
		{
			string left = (Mathf.Max(0, end - Find.TickManager.TicksGame) / 2500f).ToString("0") + " h";
			if (e.kind == DE_Challenge.Kind.Record)
				return e.Title + ": " + Mathf.RoundToInt(Progress(e)) + " / " + Mathf.RoundToInt(goal) + " (" + left + ")";
			return e.Title + " (" + left + ")";
		}

		// 1 = won, -1 = lost, 0 = going on
		public int Check(DE_Challenge e)
		{
			Map map = Map;
			if (map == null) return -1;
			int now = Find.TickManager.TicksGame;
			switch (e.kind)
			{
				case DE_Challenge.Kind.Record:
					if (Progress(e) >= goal) return 1;
					return now >= end ? -1 : 0;
				case DE_Challenge.Kind.NoBreaks:
					if (map.mapPawns.FreeColonistsSpawned.Any(p => p.InMentalState)) return -1;
					return now >= end ? 1 : 0;
				default:
					foreach (int id in who)
					{
						Pawn p = Find.WorldPawns.AllPawnsAliveOrDead.Concat(map.mapPawns.AllPawns).FirstOrDefault(x => x.thingIDNumber == id);
						if (p != null && p.Dead) return -1;
					}
					return now >= end ? 1 : 0;
			}
		}
	}

	public class ChoiceLetter_Dice : ChoiceLetter
	{
		public string eventId;
		public Map map;

		public override IEnumerable<DiaOption> Choices
		{
			get
			{
				if (ArchivedOnly)
				{
					yield return Option_Close;
					yield break;
				}
				DE_Choice e = DiceEvents.Get(eventId) as DE_Choice;
				if (e == null || map == null)
				{
					yield return Option_Close;
					yield break;
				}
				foreach (var o in e.options)
				{
					var opt = o;
					yield return new DiaOption(e.Key(opt.Key).Translate())
					{
						action = () =>
						{
							Find.LetterStack.RemoveLetter(this);
							try { opt.Value(map); }
							catch (Exception ex) { Log.Warning("[Randy's Table] bet " + eventId + ": " + ex.Message); }
						},
						resolveTree = true
					};
				}
				yield return Option_Postpone;
			}
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref eventId, "eventId");
			Scribe_References.Look(ref map, "map");
		}
	}

	// 0.2.1: everything the events do follows the storyteller difficulty (threat scale):
	// harder games – stronger raids, bigger goals, smaller rewards; peaceful games – no threats at all
	public static class DiceDifficulty
	{
		public static float D => Mathf.Max(0f, Find.Storyteller?.difficulty?.threatScale ?? 1f);

		public static bool ThreatsAllowed => D >= 0.3f && (Find.Storyteller?.difficulty?.allowBigThreats ?? true);

		// an extra raid factor (1.3 = 30 % stronger) is softened on easy and sharpened on hard difficulties
		public static float Threat(float factor) => Mathf.Max(0.3f, 1f + (factor - 1f) * Mathf.Clamp(D, 0.3f, 2f));

		public static float RewardFactor => Mathf.Clamp(1.35f - 0.35f * D, 0.6f, 1.3f);

		public static float GoalFactor => Mathf.Clamp(0.7f + 0.3f * D, 0.6f, 1.6f);

		public static string Explain() => "RT_EV_Difficulty".Translate(Find.Storyteller?.difficultyDef?.LabelCap ?? "?",
			RewardFactor.ToString("0.0#"), GoalFactor.ToString("0.0#"), (ThreatsAllowed ? "RT_EV_RaidsYes" : "RT_EV_RaidsNo").Translate());
	}

	// ---------------------------------------------------------------------------
	// rewards and penalties
	// ---------------------------------------------------------------------------
	public static class DiceReward
	{
		public static void Think(Pawn p, string thought)
		{
			if (thought == null || p?.needs?.mood == null) return;
			ThoughtDef t = DefDatabase<ThoughtDef>.GetNamedSilentFail(thought);
			if (t != null) p.needs.mood.thoughts.memories.TryGainMemory(t);
		}

		public static void ThinkAll(Map map, string thought)
		{
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned) Think(p, thought);
		}

		public static List<Thing> Make(string defName, int count, ThingDef stuff = null)
		{
			var list = new List<Thing>();
			ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
			if (d == null || count <= 0) return list;
			while (count > 0)
			{
				Thing t = ThingMaker.MakeThing(d, d.MadeFromStuff ? (stuff ?? GenStuff.DefaultStuffFor(d)) : null);
				int n = Mathf.Min(count, d.stackLimit);
				t.stackCount = n;
				count -= n;
				list.Add(t.TryMakeMinified());
			}
			return list;
		}

		public static List<Thing> Goods(float value)
		{
			value *= DiceDifficulty.RewardFactor;
			try
			{
				var parms = new ThingSetMakerParams { totalMarketValueRange = new FloatRange(value * 0.8f, value * 1.2f), countRange = new IntRange(1, 4) };
				return ThingSetMakerDefOf.Reward_ItemsStandard.root.Generate(parms);
			}
			catch
			{
				return Make("Silver", Mathf.RoundToInt(value));
			}
		}

		public static void Drop(Map map, List<Thing> things, string why, bool exact = false)
		{
			if (things == null || things.Count == 0) return;
			// 0.2.1: rewards follow the difficulty (bets pay exactly what was promised)
			if (!exact)
				foreach (Thing t in things)
					if (t.def.stackLimit > 1 && !(t is MinifiedThing)) t.stackCount = Mathf.Max(1, Mathf.RoundToInt(t.stackCount * DiceDifficulty.RewardFactor));
			IntVec3 spot = DropCellFinder.TradeDropSpot(map);
			DropPodUtility.DropThingsNear(spot, map, things, 110, false, false, true);
			Messages.Message("RT_EV_Dropped".Translate(why, Describe(things)), new TargetInfo(spot, map), MessageTypeDefOf.PositiveEvent, false);
		}

		public static string Describe(List<Thing> things) =>
			string.Join(", ", things.Select(t => t.LabelCap.ToString()).ToArray());

		public static void Silver(Map map, int n, string why, bool exact = false) => Drop(map, Make("Silver", n), why, exact);

		public static int SilverOnMap(Map map) =>
			map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => !t.IsForbidden(Faction.OfPlayer)).Sum(t => t.stackCount);

		public static bool TakeSilver(Map map, int n)
		{
			if (SilverOnMap(map) < n) return false;
			foreach (Thing t in map.listerThings.ThingsOfDef(ThingDefOf.Silver).Where(t => !t.IsForbidden(Faction.OfPlayer)).ToList())
			{
				int take = Mathf.Min(n, t.stackCount);
				t.SplitOff(take).Destroy();
				n -= take;
				if (n <= 0) break;
			}
			return true;
		}

		public static void Inspire(Map map, int count)
		{
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.Where(x => x.mindState?.inspirationHandler != null && !x.Inspired).InRandomOrder().Take(count))
			{
				InspirationDef d = p.mindState.inspirationHandler.GetRandomAvailableInspirationDef();
				if (d != null) p.mindState.inspirationHandler.TryStartInspiration(d, "RT_EV_InspiredBy".Translate());
			}
		}

		public static void Raid(Map map, float pointsFactor, string why)
		{
			IncidentDef d = IncidentDefOf.RaidEnemy;
			if (!DiceDifficulty.ThreatsAllowed)
			{
				Messages.Message("RT_EV_NoRaid".Translate(), MessageTypeDefOf.NeutralEvent, false);
				return;
			}
			IncidentParms parms = StorytellerUtility.DefaultParmsNow(d.category, map);
			parms.points *= DiceDifficulty.Threat(pointsFactor);
			parms.forced = true;
			if (!d.Worker.TryExecute(parms))
				Log.Message("[Randy's Table] " + why + ": no raid possible right now.");
		}

		public static bool Fire(Map map, string defName, float pointsFactor = 1f, int delay = 0)
		{
			IncidentDef d = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
			if (d == null) return false;
			if (!DiceDifficulty.ThreatsAllowed && (d.category == IncidentCategoryDefOf.ThreatBig || d.category == IncidentCategoryDefOf.ThreatSmall)) return false;
			IncidentParms parms = StorytellerUtility.DefaultParmsNow(d.category, map);
			if (parms.points > 0f) parms.points *= DiceDifficulty.Threat(pointsFactor);
			parms.forced = true;
			if (delay > 0)
			{
				Find.Storyteller.incidentQueue.Add(d, Find.TickManager.TicksGame + delay, parms, 30000);
				return true;
			}
			try { return d.Worker.CanFireNow(parms) && d.Worker.TryExecute(parms); }
			catch { return false; }
		}

		public static void Hurt(Pawn p)
		{
			try
			{
				BodyPartRecord part = p.health.hediffSet.GetNotMissingParts().Where(x => x.depth == BodyPartDepth.Outside && x.coverageAbs > 0f).RandomElementWithFallback();
				if (part != null) p.health.AddHediff(HediffMaker.MakeHediff(DefDatabase<HediffDef>.GetNamed("Bruise"), p, part).Also(h => h.Severity = 6f), part);
			}
			catch { }
		}

		private static T Also<T>(this T t, Action<T> a) { a(t); return t; }

		public static void Xp(Pawn p, float amount)
		{
			if (p?.skills == null) return;
			SkillRecord best = p.skills.skills.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).FirstOrDefault();
			best?.Learn(amount, true);
		}
	}

	// ---------------------------------------------------------------------------
	// the list of events and the state in the save
	// ---------------------------------------------------------------------------
	public sealed class DiceEventsComp : GameComponent
	{
		public int pendingTick = -1;
		public string pendingId;
		public List<DiceChallenge> challenges = new List<DiceChallenge>();

		public DiceEventsComp(Game g) { }

		public override void ExposeData()
		{
			Scribe_Values.Look(ref pendingTick, "pendingTick", -1);
			Scribe_Values.Look(ref pendingId, "pendingId");
			Scribe_Collections.Look(ref challenges, "challenges", LookMode.Deep);
			if (challenges == null) challenges = new List<DiceChallenge>();
		}

		public override void GameComponentTick()
		{
			int now = Find.TickManager.TicksGame;
			if (pendingTick > 0 && now >= pendingTick)
			{
				string id = pendingId;
				pendingTick = -1;
				pendingId = null;
				DiceEvents.RunNow(id);
			}
			if (now % 1250 == 611 && challenges.Count > 0) DiceEvents.CheckChallenges(this);
		}
	}

	public static class DiceEvents
	{
		public static DiceEventsComp Comp => Current.Game?.GetComponent<DiceEventsComp>();

		private static List<DiceEvent> all;

		public static List<DiceEvent> All
		{
			get
			{
				if (all == null) all = Build();
				return all;
			}
		}

		public static DiceEvent Get(string id) => id == null ? null : All.FirstOrDefault(e => e.id == id);

		public static bool Enabled(DiceEvent e) => RTMod.S != null && !RTMod.S.disabledEvents.Contains(e.id);

		// after a roll: maybe one event of this face later today
		public static void AfterRoll(int face)
		{
			try
			{
				DiceEventsComp c = Comp;
				RTSettings s = RTMod.S;
				if (c == null || s == null || !s.diceEvents || face < 1 || face > 6) return;
				float chance = s.eventChance / 100f;
				if (face >= 5) chance *= Mathf.Min(1f, DiceDifficulty.D);   // easier games: fewer hard events
				if (!Rand.Chance(chance)) return;
				Map map = Find.AnyPlayerHomeMap;
				if (map == null) return;
				DiceEvent e = All.Where(x => x.face == face && Enabled(x) && (!x.threat || DiceDifficulty.ThreatsAllowed) && Safe(x, map)).RandomElementByWeightWithFallback(x => x.weight);
				if (e == null) return;
				c.pendingId = e.id;
				c.pendingTick = Find.TickManager.TicksGame + Rand.Range(3000, 30000);
			}
			catch (Exception ex)
			{
				Log.Warning("[Randy's Table] dice event: " + ex.Message);
			}
		}

		private static bool Safe(DiceEvent e, Map map)
		{
			try { return e.CanRun(map); } catch { return false; }
		}

		public static void RunNow(string id, Map map = null)
		{
			DiceEvent e = Get(id);
			map = map ?? Find.AnyPlayerHomeMap;
			if (e == null || map == null || !Safe(e, map)) return;
			try { e.Run(map); }
			catch (Exception ex) { Log.Warning("[Randy's Table] dice event " + id + ": " + ex); }
		}

		public static void CheckChallenges(DiceEventsComp c)
		{
			for (int i = c.challenges.Count - 1; i >= 0; i--)
			{
				DiceChallenge ch = c.challenges[i];
				DE_Challenge e = Get(ch.id) as DE_Challenge;
				if (e == null) { c.challenges.RemoveAt(i); continue; }
				int r;
				try { r = ch.Check(e); } catch { r = -1; }
				if (r == 0) continue;
				c.challenges.RemoveAt(i);
				Map map = ch.Map ?? Find.AnyPlayerHomeMap;
				if (map == null) continue;
				try
				{
					if (r > 0)
					{
						Find.LetterStack.ReceiveLetter("RT_EV_Won".Translate(e.Title), e.Key("Won").Translate(), LetterDefOf.PositiveEvent);
						e.reward?.Invoke(map);
					}
					else
					{
						Find.LetterStack.ReceiveLetter("RT_EV_Lost".Translate(e.Title), e.Key("Lost").Translate(), LetterDefOf.NegativeEvent);
						e.penalty?.Invoke(map);
					}
				}
				catch (Exception ex) { Log.Warning("[Randy's Table] challenge " + e.id + ": " + ex.Message); }
			}
		}

		private static string Txt(string id, string s) => s.Length == 0 ? ("RT_EV_" + id).Translate().ToString() : ("RT_EV_" + id + "_" + s).Translate().ToString();

		private static List<DiceEvent> Build()
		{
			var L = new List<DiceEvent>();

			// ---------------- 1: calm ----------------
			L.Add(new DE_Thought { id = "Festival", face = 1, thought = "RT_Thought_Festival" });
			L.Add(new DE_Situation { id = "Dreams", face = 1, hediff = "RT_Sit_Rested" });
			L.Add(new DE_Gift { id = "LostDie", face = 1, make = m => DiceReward.Goods(250f + 40f * m.mapPawns.FreeColonistsSpawnedCount) });
			L.Add(new DE_Challenge
			{
				id = "QuietDays", face = 1, kind = DE_Challenge.Kind.NoBreaks, days = 3f,
				reward = m => { DiceReward.ThinkAll(m, "RT_Thought_QuietWeek"); DiceReward.Inspire(m, 2); },
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Action
			{
				id = "Lesson", face = 1,
				act = m =>
				{
					foreach (Pawn p in m.mapPawns.FreeColonistsSpawned.InRandomOrder().Take(3)) DiceReward.Xp(p, 4000f);
				}
			});

			// ---------------- 2: quiet ----------------
			L.Add(new DE_Challenge
			{
				id = "Crafter", face = 2, record = "ThingsCrafted", perColonist = 2f, minGoal = 4f, days = 3f,
				reward = m => DiceReward.Drop(m, DiceReward.Make("ComponentIndustrial", 4).Concat(DiceReward.Make("Plasteel", 30)).ToList(), Txt("Crafter", "")),
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Challenge
			{
				id = "Hunter", face = 2, record = "KillsAnimals", perColonist = 0.5f, minGoal = 3f, days = 2f,
				reward = m => { DiceReward.ThinkAll(m, "RT_Thought_GreatHunt"); DiceReward.Silver(m, 200, Txt("Hunter", "")); },
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Challenge
			{
				id = "Cleaning", face = 2, record = "MessesCleaned", perColonist = 6f, minGoal = 20f, days = 1f,
				reward = m => DiceReward.ThinkAll(m, "RT_Thought_CleanHome"),
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Situation { id = "Focus", face = 2, hediff = "RT_Sit_Focus" });
			L.Add(new DE_Choice { id = "OddEven", face = 2, can = m => DiceReward.SilverOnMap(m) >= 100 }
				.Opt("O1", m => Bet(m, 100, 250, "OddEven"))
				.Opt("O2", m => Bet(m, 300, 750, "OddEven"))
				.Opt("O3", m => { }));

			// ---------------- 3: ordinary ----------------
			L.Add(new DE_Challenge
			{
				id = "Miner", face = 3, record = "CellsMined", perColonist = 6f, minGoal = 20f, days = 2f,
				reward = m => DiceReward.Drop(m, DiceReward.Make("Gold", 40).Concat(DiceReward.Make("Silver", 150)).ToList(), Txt("Miner", "")),
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Challenge
			{
				id = "Builder", face = 3, record = "ThingsConstructed", perColonist = 2f, minGoal = 6f, days = 2f,
				reward = m => DiceReward.Drop(m, DiceReward.Make("Steel", 150).Concat(DiceReward.Make("ComponentIndustrial", 3)).ToList(), Txt("Builder", "")),
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Situation { id = "GoldFever", face = 3, hediff = "RT_Sit_GoldFever" });
			L.Add(new DE_Action
			{
				id = "Reroll", face = 3,
				can = m => DiceState.DiceActive,
				act = m => DiceState.I?.ForceRoll()
			});
			L.Add(new DE_Choice { id = "Roulette", face = 3, can = m => m.mapPawns.FreeColonistsSpawnedCount > 0 }
				.Opt("O1", m =>
				{
					Pawn p = m.mapPawns.FreeColonistsSpawned.RandomElement();
					if (Rand.Chance(0.5f))
					{
						InspirationDef d = p.mindState?.inspirationHandler?.GetRandomAvailableInspirationDef();
						if (d != null) p.mindState.inspirationHandler.TryStartInspiration(d, "RT_EV_InspiredBy".Translate());
						Messages.Message(Txt("Roulette", "Win").Formatted(p.LabelShort), p, MessageTypeDefOf.PositiveEvent, false);
					}
					else
					{
						DiceReward.Hurt(p);
						Messages.Message(Txt("Roulette", "Lose").Formatted(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, false);
					}
				})
				.Opt("O2", m => { }));

			// ---------------- 4: generous ----------------
			L.Add(new DE_Choice { id = "Gift3", face = 4 }
				.Opt("O1", m => DiceReward.Silver(m, 500, Txt("Gift3", "")))
				.Opt("O2", m => DiceReward.Drop(m, DiceReward.Make("MedicineIndustrial", 10), Txt("Gift3", "")))
				.Opt("O3", m => DiceReward.Drop(m, DiceReward.Goods(700f), Txt("Gift3", ""))));
			L.Add(new DE_Situation { id = "LuckyHand", face = 4, hediff = "RT_Sit_LuckyHand" });
			L.Add(new DE_Choice { id = "Pilgrim", face = 4, can = m => DefDatabase<IncidentDef>.GetNamedSilentFail("WandererJoin") != null }
				.Opt("O1", m =>
				{
					DiceReward.Fire(m, "WandererJoin");
					if (Rand.Chance(0.6f)) DiceReward.Fire(m, "RaidEnemy", 0.6f, Rand.Range(60000, 150000));
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Situation { id = "Blessing", face = 4, hediff = "RT_Sit_Blessing" });
			L.Add(new DE_Gift { id = "Supplies", face = 4, make = m => DiceReward.Make("MealSimple", Mathf.Max(6, 3 * m.mapPawns.FreeColonistsSpawnedCount)) });

			// ---------------- 5: hard ----------------
			L.Add(new DE_Action
			{
				id = "TwoSides", threat = true, face = 5, bad = true,
				act = m => { DiceReward.Raid(m, 0.6f, "TwoSides"); DiceReward.Raid(m, 0.6f, "TwoSides"); }
			});
			L.Add(new DE_Situation { id = "BadLuck", threat = true, face = 5, hediff = "RT_Sit_BadLuck", bad = true });
			L.Add(new DE_Challenge
			{
				id = "Fortress", threat = true, face = 5, kind = DE_Challenge.Kind.NoDeaths, days = 2f,
				onStart = m => DiceReward.Raid(m, 0.9f, "Fortress"),
				reward = m =>
				{
					DiceReward.ThinkAll(m, "RT_Thought_Fortress");
					DiceReward.Drop(m, DiceReward.Make("Turret_MiniTurret", 1).Concat(DiceReward.Make("Steel", 100)).ToList(), Txt("Fortress", ""));
				},
				penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
			});
			L.Add(new DE_Choice { id = "Tax", threat = true, face = 5 }
				.Opt("O1", m =>
				{
					int n = Mathf.Max(50, DiceReward.SilverOnMap(m) / 5);
					if (!DiceReward.TakeSilver(m, n)) DiceReward.Raid(m, 0.8f, "Tax");
				})
				.Opt("O2", m => DiceReward.Raid(m, 0.8f, "Tax")));
			L.Add(new DE_Condition { id = "ColdShower", threat = true, face = 5, condition = "ColdSnap", alt = "HeatWave", days = 2f });

			// ---------------- 6: mad ----------------
			L.Add(new DE_Choice { id = "AllIn", threat = true, face = 6 }
				.Opt("O1", m =>
				{
					int r = Rand.RangeInclusive(1, 6);
					if (r >= 4)
					{
						DiceReward.Drop(m, DiceReward.Goods(900f + 120f * m.mapPawns.FreeColonistsSpawnedCount), Txt("AllIn", "") + " (" + r + ")");
						DiceReward.ThinkAll(m, "RT_Thought_WonBet");
					}
					else
					{
						Messages.Message(Txt("AllIn", "Lose").Formatted(r), MessageTypeDefOf.ThreatBig, false);
						DiceReward.Raid(m, 1.3f, "AllIn");
						DiceReward.ThinkAll(m, "RT_Thought_LostBet");
					}
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Action
			{
				id = "MadPack", threat = true, face = 6, bad = true,
				can = m => DefDatabase<IncidentDef>.GetNamedSilentFail("ManhunterPack") != null,
				act = m => DiceReward.Fire(m, "ManhunterPack", 1.5f)
			});
			L.Add(new DE_Condition { id = "Eclipse", threat = true, face = 6, condition = "Eclipse", days = 1f, thought = "RT_Thought_Whisper" });
			L.Add(new DE_Action
			{
				id = "Revenge", threat = true, face = 6, bad = true,
				act = m => DiceReward.Raid(m, 1.3f, "Revenge")
			});
			L.Add(new DE_Action
			{
				id = "Double", threat = true, face = 6, letter = true,
				act = m =>
				{
					DiceEvent good = All.Where(x => x.face <= 4 && x.face >= 1 && !(x is DE_Choice) && Enabled(x) && Safe(x, m) && x.id != "Double" && x.id != "Reroll").RandomElementWithFallback();
					DiceEvent bad = All.Where(x => x.face == 5 && !(x is DE_Choice) && Enabled(x) && Safe(x, m)).RandomElementWithFallback();
					if (good != null) RunNow(good.id, m);
					if (bad != null) RunNow(bad.id, m);
				}
			});
			return L;
		}

		private static void Bet(Map map, int stake, int win, string id)
		{
			if (!DiceReward.TakeSilver(map, stake))
			{
				Messages.Message("RT_EV_NoSilver".Translate(stake), MessageTypeDefOf.RejectInput, false);
				return;
			}
			int r = Rand.RangeInclusive(1, 6);
			if (r % 2 == 0)
			{
				DiceReward.Silver(map, win, Txt(id, "") + " (" + r + ")", true);
				DiceReward.ThinkAll(map, "RT_Thought_WonBet");
			}
			else
			{
				Messages.Message(Txt(id, "Lose").Formatted(r, stake), MessageTypeDefOf.NegativeEvent, false);
				DiceReward.ThinkAll(map, "RT_Thought_LostBet");
			}
		}
	}

	// active challenges, top right
	public class Alert_DiceChallenges : Alert
	{
		public Alert_DiceChallenges()
		{
			defaultLabel = "RT_EV_AlertLabel".Translate();
			defaultPriority = AlertPriority.Medium;
		}

		public override AlertReport GetReport()
		{
			DiceEventsComp c = DiceEvents.Comp;
			return c != null && c.challenges.Count > 0;
		}

		public override string GetLabel()
		{
			DiceEventsComp c = DiceEvents.Comp;
			return "RT_EV_AlertLabel".Translate() + (c != null && c.challenges.Count > 1 ? " ×" + c.challenges.Count : "");
		}

		public override TaggedString GetExplanation()
		{
			DiceEventsComp c = DiceEvents.Comp;
			if (c == null) return "";
			var sb = new System.Text.StringBuilder();
			foreach (DiceChallenge ch in c.challenges)
			{
				DE_Challenge e = DiceEvents.Get(ch.id) as DE_Challenge;
				if (e == null) continue;
				sb.AppendLine(ch.Status(e));
				sb.AppendLine("   " + ch.GoalText(e));
			}
			return sb.ToString().TrimEnd();
		}
	}

	// settings: which events may come (all on by default)
	public class Dialog_DiceEvents : Window
	{
		private Vector2 scroll;
		public override Vector2 InitialSize => new Vector2(640f, 700f);

		public Dialog_DiceEvents()
		{
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = true;
		}

		public override void DoWindowContents(Rect r)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "RT_EventListTitle".Translate());
			Text.Font = GameFont.Small;
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(r.x, r.y + 32f, r.width, 34f), "RT_EventListDesc".Translate() + (Current.Game != null ? "\n" + DiceDifficulty.Explain() : ""));
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			List<DiceEvent> list = DiceEvents.All;
			float rowH = 28f, headH = 30f;
			Rect outR = new Rect(r.x, r.y + 70f, r.width, r.height - 70f - 50f);
			Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * rowH + 6 * headH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			float y = 0f;
			List<string> off = RTMod.S.disabledEvents;
			for (int face = 1; face <= 6; face++)
			{
				Rect h = new Rect(0f, y, view.width, headH);
				GUI.color = MUI.Accent;
				Widgets.Label(new Rect(h.x, h.y + 5f, h.width, 24f), ("RT_Mood" + face).Translate().CapitalizeFirst() + "  (" + face + ")");
				GUI.color = Color.white;
				y += headH;
				foreach (DiceEvent e in list.Where(x => x.face == face))
				{
					Rect row = new Rect(0f, y, view.width, rowH);
					Widgets.DrawHighlightIfMouseover(row);
					bool on = !off.Contains(e.id), was = on;
					Widgets.CheckboxLabeled(new Rect(row.x + 10f, row.y, row.width - (Prefs.DevMode && Current.Game != null ? 90f : 10f), rowH), e.Title, ref on);
					TooltipHandler.TipRegion(row, e.Text);
					if (on != was) { if (on) off.Remove(e.id); else off.Add(e.id); }
					if (Prefs.DevMode && Current.Game != null && Widgets.ButtonText(new Rect(row.xMax - 76f, row.y + 2f, 72f, 24f), "RT_EventTry".Translate()))
						DiceEvents.RunNow(e.id);
					y += rowH;
				}
			}
			Widgets.EndScrollView();
		}
	}
}
