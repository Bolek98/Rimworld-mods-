using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandysTable
{
	// 0.4.0: the third batch – the sky and the wild: weathers, conditions and a few threats.
	// Weathers of Seasons & Holidays (SH_…) are used when that mod is there, otherwise the game's.
	public static partial class DiceEvents
	{
		private static WeatherDef Weather(string defName) => DefDatabase<WeatherDef>.GetNamedSilentFail(defName);

		private static bool CanSky(Map m, string defName) =>
			Weather(defName) != null && m.weatherManager.curWeather?.defName != defName;

		private static void Sky(Map m, string defName)
		{
			WeatherDef w = Weather(defName);
			if (w != null) m.weatherManager.TransitionTo(w);
		}

		// the first weather of the list that exists
		private static string FirstWeather(params string[] names) => names.FirstOrDefault(n => Weather(n) != null);

		private static bool Cold(Map m) => m.mapTemperature.OutdoorTemp < 2f;

		private static void AddBatch3(List<DiceEvent> L)
		{
			// ---------------- 1: calm ----------------
			L.Add(new DE_Action
			{
				id = "ClearSky", face = 1,
				can = m => Weather("Clear") != null && m.weatherManager.curWeather != null && m.weatherManager.curWeather.defName != "Clear",
				act = m => Sky(m, "Clear")
			});
			L.Add(new DE_Condition { id = "Aurora", face = 1, condition = "Aurora", days = 0.6f, bad = false, thought = "RT_Thought_Aurora" });
			L.Add(new DE_Action
			{
				id = "MorningFog", face = 1,
				can = m => CanSky(m, "Fog"),
				act = m => { Sky(m, "Fog"); DiceReward.ThinkAll(m, "RT_Thought_Fog"); }
			});

			// ---------------- 2: quiet ----------------
			L.Add(new DE_Action
			{
				id = "Snowfall", face = 2,
				can = m => Cold(m) && CanSky(m, "SnowGentle"),
				act = m => { Sky(m, "SnowGentle"); DiceReward.ThinkAll(m, "RT_Thought_Snowfall"); }
			});
			L.Add(new DE_Choice { id = "SkyBet", face = 2, can = m => DiceReward.SilverOnMap(m) >= 120 && Weather("Clear") != null && Weather("Rain") != null }
				.Opt("O1", m => SkyBet(m, true))
				.Opt("O2", m => SkyBet(m, false))
				.Opt("O3", m => { }));
			L.Add(new DE_Thought { id = "Rainbow", face = 2, thought = "RT_Thought_Rainbow" });

			// ---------------- 3: ordinary ----------------
			L.Add(new DE_Action
			{
				id = "Thunder", face = 3,
				can = m => CanSky(m, "RainyThunderstorm"),
				act = m => Sky(m, "RainyThunderstorm")
			});
			L.Add(new DE_Condition { id = "OddWeather", face = 3, condition = "ColdSnap", alt = "HeatWave", days = 1f });
			L.Add(new DE_Action
			{
				id = "StrangeSky", face = 3,
				can = m => SHWeathers().Any(n => CanSky(m, n)),
				act = m => Sky(m, SHWeathers().Where(n => CanSky(m, n)).RandomElement())
			});

			// ---------------- 4: generous ----------------
			L.Add(new DE_Action
			{
				id = "Lights", face = 4,
				can = m => Weather("SH_Aurora") != null || DefDatabase<GameConditionDef>.GetNamedSilentFail("Aurora") != null,
				act = m =>
				{
					if (Weather("SH_Aurora") != null) Sky(m, "SH_Aurora");
					else
					{
						GameConditionDef d = DefDatabase<GameConditionDef>.GetNamed("Aurora");
						if (!m.gameConditionManager.ConditionIsActive(d))
							m.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(d, 30000));
					}
					DiceReward.Inspire(m, 2);
					DiceReward.ThinkAll(m, "RT_Thought_Aurora");
				}
			});
			L.Add(new DE_Gift { id = "SkyScrap", face = 4, make = m => DiceReward.Make("Steel", Mathf.RoundToInt(90 * DiceDifficulty.RewardFactor)).Concat(DiceReward.Make("ComponentIndustrial", Mathf.Max(2, Mathf.RoundToInt(4 * DiceDifficulty.RewardFactor)))).ToList() });
			L.Add(new DE_Action
			{
				id = "GoodHarvestRain", face = 4,
				can = m => !Cold(m) && Weather("Rain") != null,
				act = m =>
				{
					Sky(m, "Rain");
					// growing plants get a little ahead
					foreach (Plant p in m.listerThings.ThingsInGroup(ThingRequestGroup.Plant).OfType<Plant>().Where(p => p.Spawned && p.IsCrop && p.LifeStage == PlantLifeStage.Growing).ToList())
						p.Growth = Mathf.Min(1f, p.Growth + 0.08f * DiceDifficulty.RewardFactor);
				}
			});

			// ---------------- 5: hard ----------------
			L.Add(new DE_Condition { id = "Flashstorm", threat = true, face = 5, condition = "Flashstorm", days = 0.5f });
			L.Add(new DE_Action
			{
				id = "MadAnimal", threat = true, face = 5, bad = true, can = m => Inc("AnimalInsanitySingle"),
				act = m => DiceReward.Fire(m, "AnimalInsanitySingle")
			});
			L.Add(new DE_Action
			{
				id = "Blizzard", face = 5, bad = true,
				can = m => Cold(m) && FirstWeather("SH_Blizzard", "SnowHard") != null && DefDatabase<GameConditionDef>.GetNamedSilentFail("ColdSnap") != null,
				act = m =>
				{
					Sky(m, FirstWeather("SH_Blizzard", "SnowHard"));
					GameConditionDef d = DefDatabase<GameConditionDef>.GetNamed("ColdSnap");
					if (!m.gameConditionManager.ConditionIsActive(d))
						m.gameConditionManager.RegisterCondition(GameConditionMaker.MakeCondition(d, 45000));
				}
			});
			L.Add(new DE_Condition { id = "Eclipse", face = 5, condition = "Eclipse", days = 1f });
			L.Add(new DE_Action
			{
				id = "Hail", face = 5, bad = true,
				can = m => !Cold(m) && CanSky(m, "SH_Hail"),
				act = m => Sky(m, "SH_Hail")
			});

			// ---------------- 6: mad ----------------
			L.Add(new DE_Action
			{
				id = "Stampede", threat = true, face = 6, bad = true, can = m => Inc("ManhunterPack"),
				act = m => DiceReward.Fire(m, "ManhunterPack", 1.2f)
			});
			L.Add(new DE_Action
			{
				id = "StormRaid", threat = true, face = 6, bad = true,
				act = m =>
				{
					if (Weather("RainyThunderstorm") != null) Sky(m, "RainyThunderstorm");
					DiceReward.Raid(m, 1.2f, "StormRaid");
				}
			});
			L.Add(new DE_Choice { id = "DoubleOrNothing", face = 6, can = m => DiceReward.SilverOnMap(m) >= 200 }
				.Opt("O1", m =>
				{
					int stake = Mathf.Min(1000, DiceReward.SilverOnMap(m) / 2);
					if (!DiceReward.TakeSilver(m, stake)) return;
					if (Rand.Chance(0.5f))
					{
						DiceReward.Silver(m, stake * 2, Txt("DoubleOrNothing", ""), true);
						DiceReward.ThinkAll(m, "RT_Thought_WonBet");
					}
					else
					{
						Messages.Message(Txt("DoubleOrNothing", "Lose").Formatted(stake), MessageTypeDefOf.NegativeEvent, false);
						DiceReward.ThinkAll(m, "RT_Thought_LostBet");
					}
				})
				.Opt("O2", m => { }));
		}

		private static IEnumerable<string> SHWeathers() =>
			new[] { "SH_Aurora", "SH_Monsoon", "SH_Sandstorm", "SH_Heatwave", "SH_FreezingRain", "SH_Hoarfrost" }.Where(n => Weather(n) != null);

		// guess the weather: sun or rain – the right guess pays double, and the weather comes either way
		private static void SkyBet(Map map, bool sun)
		{
			const int stake = 120;
			if (!DiceReward.TakeSilver(map, stake))
			{
				Messages.Message("RT_EV_NoSilver".Translate(stake), MessageTypeDefOf.RejectInput, false);
				return;
			}
			bool isSun = Rand.Chance(0.5f);
			Sky(map, isSun ? "Clear" : "Rain");
			if (isSun == sun)
			{
				DiceReward.Silver(map, stake * 2, Txt("SkyBet", ""), true);
				DiceReward.ThinkAll(map, "RT_Thought_WonBet");
			}
			else
			{
				Messages.Message(Txt("SkyBet", "Lose").Formatted(stake), MessageTypeDefOf.NegativeEvent, false);
				DiceReward.ThinkAll(map, "RT_Thought_LostBet");
			}
		}
	}
}
