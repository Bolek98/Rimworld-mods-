using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandysTable
{
	// 0.3.0: the second batch – 30 more events, five for every face of the die
	public static partial class DiceEvents
	{
		private static bool Inc(string defName) => DefDatabase<IncidentDef>.GetNamedSilentFail(defName) != null;

		private static DE_Challenge Work(string id, int face, string record, float per, float min, float days, Action<Map> reward) => new DE_Challenge
		{
			id = id, face = face, record = record, perColonist = per, minGoal = min, days = days,
			reward = reward, penalty = m => DiceReward.ThinkAll(m, "RT_Thought_FailedChallenge")
		};

		private static void AddBatch2(List<DiceEvent> L)
		{
			// ---------------- 1: calm ----------------
			L.Add(new DE_Thought { id = "Stargazing", face = 1, thought = "RT_Thought_Stargazing" });
			L.Add(new DE_Situation { id = "GreenThumb", face = 1, hediff = "RT_Sit_GreenThumb" });
			L.Add(new DE_Action
			{
				id = "Guests", face = 1, can = m => Inc("VisitorGroup"),
				act = m => DiceReward.Fire(m, "VisitorGroup")
			});
			L.Add(new DE_Action
			{
				id = "Balm", face = 1,
				can = m => m.mapPawns.FreeColonistsSpawned.Any(p => p.health.hediffSet.hediffs.Any(h => h is Hediff_Injury)),
				act = m =>
				{
					foreach (Pawn p in m.mapPawns.FreeColonistsSpawned)
						foreach (Hediff_Injury h in p.health.hediffSet.hediffs.OfType<Hediff_Injury>().ToList())
							h.Heal(10f * DiceDifficulty.RewardFactor);
				}
			});
			L.Add(Work("Cook", 1, "MealsCooked", 3f, 8f, 2f, m =>
			{
				DiceReward.ThinkAll(m, "RT_Thought_Feast");
				DiceReward.Silver(m, 150, Txt("Cook", ""));
			}));

			// ---------------- 2: quiet ----------------
			L.Add(Work("Scholar", 2, "ResearchPointsResearched", 250f, 700f, 2f, m =>
			{
				DiceReward.Inspire(m, 2);
				DiceReward.Drop(m, DiceReward.Goods(600f), Txt("Scholar", ""));
			}));
			L.Add(Work("Gardener", 2, "PlantsHarvested", 8f, 20f, 2f, m =>
			{
				DiceReward.ThinkAll(m, "RT_Thought_GoodWork");
				DiceReward.Silver(m, 200, Txt("Gardener", ""));
			}));
			L.Add(new DE_Choice { id = "HighLow", face = 2, can = m => DiceReward.SilverOnMap(m) >= 150 }
				.Opt("O1", m => Guess(m, 150, 300, true, "HighLow"))
				.Opt("O2", m => Guess(m, 150, 300, false, "HighLow"))
				.Opt("O3", m => { }));
			L.Add(new DE_Situation { id = "SteadyHands", face = 2, hediff = "RT_Sit_SteadyHands" });
			L.Add(new DE_Action
			{
				id = "Market", face = 2, can = m => Inc("TraderCaravanArrival"),
				act = m => DiceReward.Fire(m, "TraderCaravanArrival")
			});

			// ---------------- 3: ordinary ----------------
			L.Add(Work("Sower", 3, "PlantsSown", 10f, 30f, 2f, m =>
			{
				DiceReward.ThinkAll(m, "RT_Thought_GoodWork");
				DiceReward.Silver(m, 180, Txt("Sower", ""));
			}));
			L.Add(new DE_Choice { id = "Swap", face = 3, can = m => DiceReward.SilverOnMap(m) >= 100 }
				.Opt("O1", m =>
				{
					if (!DiceReward.TakeSilver(m, 100)) { Messages.Message("RT_EV_NoSilver".Translate(100), MessageTypeDefOf.RejectInput, false); return; }
					DiceReward.Drop(m, DiceReward.Goods(Rand.Range(40f, 420f)), Txt("Swap", ""), true);
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Thought { id = "Tales", face = 3, thought = "RT_Thought_Tales" });
			L.Add(new DE_Action
			{
				id = "RainDance", face = 3,
				can = m => DefDatabase<WeatherDef>.GetNamedSilentFail("Rain") != null && m.weatherManager.curWeather?.defName != "Rain" && m.mapTemperature.OutdoorTemp > 0f,
				act = m => m.weatherManager.TransitionTo(DefDatabase<WeatherDef>.GetNamed("Rain"))
			});
			L.Add(Work("Tamer", 3, "AnimalsTamed", 0.25f, 1f, 3f, m =>
			{
				DiceReward.ThinkAll(m, "RT_Thought_GoodWork");
				DiceReward.Silver(m, 250, Txt("Tamer", ""));
			}));

			// ---------------- 4: generous ----------------
			L.Add(new DE_Gift { id = "Windfall", face = 4, make = m => DiceReward.Make("Silver", Mathf.RoundToInt((150f + 30f * m.mapPawns.FreeColonistsSpawnedCount) * DiceDifficulty.RewardFactor)) });
			L.Add(new DE_Gift { id = "Medkit", face = 4, make = m => DiceReward.Make("MedicineIndustrial", 6).Concat(DiceReward.Make("MedicineHerbal", 10)).ToList() });
			L.Add(new DE_Choice { id = "MysteryBox", face = 4 }
				.Opt("O1", m =>
				{
					if (Rand.Chance(0.7f) || !DiceDifficulty.ThreatsAllowed || !Inc("ManhunterPack"))
						DiceReward.Drop(m, DiceReward.Goods(900f), Txt("MysteryBox", ""));
					else
					{
						Messages.Message(Txt("MysteryBox", "Lose"), MessageTypeDefOf.ThreatSmall, false);
						DiceReward.Fire(m, "ManhunterPack", 0.5f);
					}
				})
				.Opt("O2", m => DiceReward.Silver(m, 300, Txt("MysteryBox", ""), true)));
			L.Add(new DE_Action { id = "Muse", face = 4, act = m => DiceReward.Inspire(m, 3) });
			L.Add(new DE_Action
			{
				id = "StrayHerd", face = 4, can = m => Inc("FarmAnimalsWanderIn"),
				act = m => DiceReward.Fire(m, "FarmAnimalsWanderIn")
			});

			// ---------------- 5: hard ----------------
			L.Add(new DE_Action
			{
				id = "Blight", face = 5, bad = true, can = m => Inc("CropBlight"),
				act = m => DiceReward.Fire(m, "CropBlight")
			});
			L.Add(new DE_Action
			{
				id = "Sickness", threat = true, face = 5, bad = true,
				can = m => Diseases().Any(),
				act = m => DiceReward.Fire(m, Diseases().RandomElement())
			});
			L.Add(new DE_Condition { id = "Fallout", threat = true, face = 5, condition = "ToxicFallout", days = 1f });
			L.Add(new DE_Choice { id = "Toll", threat = true, face = 5 }
				.Opt("O1", m =>
				{
					int n = TollSilver(m);
					if (!DiceReward.TakeSilver(m, n))
					{
						Messages.Message("RT_EV_NoSilver".Translate(n), MessageTypeDefOf.RejectInput, false);
						DiceReward.Raid(m, 1f, "Toll");
					}
				})
				.Opt("O2", m =>
				{
					DiceReward.Raid(m, 1f, "Toll");
					DiceReward.Drop(m, DiceReward.Goods(450f), Txt("Toll", ""));
				}));
			L.Add(new DE_Condition { id = "Flare", threat = true, face = 5, condition = "SolarFlare", days = 0.5f });

			// ---------------- 6: mad ----------------
			L.Add(new DE_Action
			{
				id = "Swarm", threat = true, face = 6, bad = true, can = m => Inc("Infestation"),
				act = m => { if (!DiceReward.Fire(m, "Infestation")) DiceReward.Raid(m, 1.2f, "Swarm"); }
			});
			L.Add(new DE_Action
			{
				id = "Drone", threat = true, face = 6, bad = true, can = m => Inc("PsychicDrone"),
				act = m => DiceReward.Fire(m, "PsychicDrone")
			});
			L.Add(new DE_Choice { id = "Jackpot", threat = true, face = 6 }
				.Opt("O1", m =>
				{
					int a = Rand.RangeInclusive(1, 6), b = Rand.RangeInclusive(1, 6);
					if (a == b)
					{
						DiceReward.Drop(m, DiceReward.Goods(2500f), Txt("Jackpot", "") + " (" + a + "+" + b + ")");
						DiceReward.ThinkAll(m, "RT_Thought_WonBet");
					}
					else
					{
						Messages.Message(Txt("Jackpot", "Lose").Formatted(a, b), MessageTypeDefOf.ThreatBig, false);
						DiceReward.Raid(m, 1.5f, "Jackpot");
						DiceReward.ThinkAll(m, "RT_Thought_LostBet");
					}
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Condition { id = "LongNight", threat = true, face = 6, condition = "VolcanicWinter", days = 3f, thought = "RT_Thought_Whisper" });
			L.Add(new DE_Action
			{
				id = "IronDice", threat = true, face = 6, bad = true,
				act = m => { if (!Inc("MechCluster") || !DiceReward.Fire(m, "MechCluster")) DiceReward.Raid(m, 1.4f, "IronDice"); }
			});
		}

		public static int TollSilver(Map m) =>
			Mathf.RoundToInt(80f * Mathf.Max(1, m.mapPawns.FreeColonistsSpawnedCount) * Mathf.Max(0.5f, DiceDifficulty.D));

		private static IEnumerable<string> Diseases() =>
			new[] { "Disease_Flu", "Disease_Plague", "Disease_GutWorms", "Disease_MuscleParasites" }.Where(Inc);

		// high (4–6) or low (1–3)
		private static void Guess(Map map, int stake, int win, bool high, string id)
		{
			if (!DiceReward.TakeSilver(map, stake))
			{
				Messages.Message("RT_EV_NoSilver".Translate(stake), MessageTypeDefOf.RejectInput, false);
				return;
			}
			int r = Rand.RangeInclusive(1, 6);
			if ((r >= 4) == high)
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
}
