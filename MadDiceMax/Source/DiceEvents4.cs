using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandysTable
{
	// 0.5.0: the fourth batch – Max's card table: lotteries, card nights, debts, cursed and loaded dice,
	// a wheel of fortune. 20 more events (100 in all). Nothing here needs a DLC or another mod.
	public static partial class DiceEvents
	{
		private static void AddBatch4(List<DiceEvent> L)
		{
			// ---------------- 1: calm ----------------
			L.Add(new DE_Action
			{
				id = "CardNight", face = 1,
				can = m => Colonists2(m).Count >= 2,
				act = m =>
				{
					foreach (Pawn p in Colonists2(m))
						if (p.needs?.joy != null) p.needs.joy.CurLevel = Mathf.Min(1f, p.needs.joy.CurLevel + 0.3f * DiceDifficulty.RewardFactor);
					DiceReward.ThinkAll(m, "RT_Thought_CardNight");
				}
			});
			L.Add(new DE_Action
			{
				id = "LuckyCharm", face = 1,
				can = m => Colonists2(m).Count > 0 && DefDatabase<HediffDef>.GetNamedSilentFail("RT_Sit_Charm") != null,
				act = m =>
				{
					HediffDef h = DefDatabase<HediffDef>.GetNamed("RT_Sit_Charm");
					Pawn p = Colonists2(m).Where(x => !x.health.hediffSet.HasHediff(h)).RandomElementWithFallback();
					if (p == null) return;
					p.health.AddHediff(h);
					Messages.Message(Txt("LuckyCharm", "Who").Formatted(p.LabelShort), p, MessageTypeDefOf.PositiveEvent, false);
				}
			});
			L.Add(new DE_Action
			{
				id = "Siesta", face = 1,
				can = m => Colonists2(m).Any(p => p.needs?.rest != null && p.needs.rest.CurLevel < 0.8f),
				act = m =>
				{
					foreach (Pawn p in Colonists2(m))
						if (p.needs?.rest != null) p.needs.rest.CurLevel = Mathf.Min(1f, p.needs.rest.CurLevel + 0.35f);
				}
			});

			// ---------------- 2: quiet ----------------
			L.Add(new DE_Choice { id = "Lottery", face = 2, can = m => DiceReward.SilverOnMap(m) >= 50 }
				.Opt("O1", m => Lottery(m, 1))
				.Opt("O2", m => Lottery(m, 3))
				.Opt("O3", m => { }));
			L.Add(new DE_Action
			{
				id = "CardDuel", face = 2,
				can = m => Colonists2(m).Count(p => p.skills != null) >= 2,
				act = m =>
				{
					List<Pawn> two = Colonists2(m).Where(p => p.skills != null).InRandomOrder().Take(2).ToList();
					if (two.Count < 2) return;
					// the better social skill wins a bit more often
					float a = 1f + two[0].skills.GetSkill(SkillDefOf.Social).Level, b = 1f + two[1].skills.GetSkill(SkillDefOf.Social).Level;
					Pawn win = Rand.Chance(a / (a + b)) ? two[0] : two[1];
					Pawn lose = win == two[0] ? two[1] : two[0];
					foreach (Pawn p in two)
						if (!p.skills.GetSkill(SkillDefOf.Social).TotallyDisabled) p.skills.Learn(SkillDefOf.Social, 1500f, true);
					DiceReward.Think(win, "RT_Thought_WonCards");
					DiceReward.Think(lose, "RT_Thought_LostCards");
					Messages.Message(Txt("CardDuel", "Who").Formatted(win.LabelShort, lose.LabelShort), win, MessageTypeDefOf.NeutralEvent, false);
				}
			});
			L.Add(Work("Hauler", 2, "ThingsHauled", 15f, 40f, 1.5f, m =>
			{
				DiceReward.ThinkAll(m, "RT_Thought_GoodWork");
				DiceReward.Silver(m, 160, Txt("Hauler", ""));
			}));

			// ---------------- 3: ordinary ----------------
			L.Add(new DE_Choice { id = "Debt", threat = true, face = 3 }
				.Opt("O1", m =>
				{
					int n = Mathf.Max(40, TollSilver(m) / 2);
					if (!DiceReward.TakeSilver(m, n))
					{
						Messages.Message("RT_EV_NoSilver".Translate(n), MessageTypeDefOf.RejectInput, false);
						DiceReward.Raid(m, 0.6f, "Debt");
					}
				})
				.Opt("O2", m =>
				{
					if (Rand.Chance(0.5f)) Messages.Message(Txt("Debt", "Lucky"), MessageTypeDefOf.PositiveEvent, false);
					else DiceReward.Raid(m, 0.6f, "Debt");
				}));
			L.Add(new DE_Choice { id = "CursedDie", face = 3, can = m => Colonists2(m).Count > 0 }
				.Opt("O1", m =>
				{
					DiceReward.Drop(m, DiceReward.Goods(600f), Txt("CursedDie", ""));
					HediffDef h = DefDatabase<HediffDef>.GetNamedSilentFail("RT_Sit_BadLuck");
					Pawn p = Colonists2(m).RandomElementWithFallback();
					if (h != null && p != null && !p.health.hediffSet.HasHediff(h))
					{
						p.health.AddHediff(h);
						Messages.Message(Txt("CursedDie", "Who").Formatted(p.LabelShort), p, MessageTypeDefOf.NegativeEvent, false);
					}
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Choice { id = "TableRound", face = 3, can = m => Colonists2(m).Count > 0 }
				.Opt("O1", m =>
				{
					int good = 0, bad = 0;
					foreach (Pawn p in Colonists2(m))
					{
						int r = Rand.RangeInclusive(1, 6);
						if (r == 6)
						{
							InspirationDef d = p.mindState?.inspirationHandler?.GetRandomAvailableInspirationDef();
							if (d != null && !p.Inspired && p.mindState.inspirationHandler.TryStartInspiration(d, "RT_EV_InspiredBy".Translate())) good++;
						}
						else if (r == 1) { DiceReward.Hurt(p); bad++; }
					}
					Messages.Message(Txt("TableRound", "Result").Formatted(good, bad), MessageTypeDefOf.NeutralEvent, false);
				})
				.Opt("O2", m => { }));
			L.Add(new DE_Action
			{
				id = "Pickpocket", face = 3, bad = true,
				can = m => DiceReward.SilverOnMap(m) >= 60,
				act = m =>
				{
					int n = Mathf.Clamp(Mathf.RoundToInt(DiceReward.SilverOnMap(m) * 0.12f * Mathf.Max(0.5f, DiceDifficulty.D)), 10, 400);
					if (DiceReward.TakeSilver(m, n))
					{
						Messages.Message(Txt("Pickpocket", "Lose").Formatted(n), MessageTypeDefOf.NegativeEvent, false);
						DiceReward.ThinkAll(m, "RT_Thought_Robbed");
					}
				}
			});

			// ---------------- 4: generous ----------------
			L.Add(new DE_Action
			{
				id = "DoubleCaravan", face = 4, can = m => Inc("TraderCaravanArrival"),
				act = m =>
				{
					DiceReward.Fire(m, "TraderCaravanArrival");
					DiceReward.Fire(m, "TraderCaravanArrival", 1f, Rand.Range(15000, 40000));
				}
			});
			L.Add(new DE_Action
			{
				id = "PrizeAnimal", face = 4, can = m => PrizeKinds().Any(),
				act = m =>
				{
					PawnKindDef k = PrizeKinds().RandomElement();
					Pawn a = PawnGenerator.GeneratePawn(k, Faction.OfPlayer);
					IntVec3 spot = DropCellFinder.TradeDropSpot(m);
					DropPodUtility.DropThingsNear(spot, m, new List<Thing> { a }, 110, false, false, true);
					Messages.Message(Txt("PrizeAnimal", "Who").Formatted(a.LabelCap), new TargetInfo(spot, m), MessageTypeDefOf.PositiveEvent, false);
				}
			});
			L.Add(new DE_Gift
			{
				id = "OnTheHouse", face = 4,
				make = m =>
				{
					int n = Mathf.Max(4, m.mapPawns.FreeColonistsSpawnedCount);
					List<Thing> l = DiceReward.Make("MealFine", Mathf.RoundToInt(n * 1.5f * DiceDifficulty.RewardFactor));
					l.AddRange(DiceReward.Make("Beer", n));
					DiceReward.ThinkAll(m, "RT_Thought_OnTheHouse");
					return l;
				}
			});
			L.Add(new DE_Action
			{
				id = "GoodWord", face = 4, can = m => FriendlyFactions().Any(),
				act = m =>
				{
					Faction f = FriendlyFactions().RandomElement();
					f.TryAffectGoodwillWith(Faction.OfPlayer, Mathf.RoundToInt(12f * DiceDifficulty.RewardFactor), true, true);
				}
			});

			// ---------------- 5: hard ----------------
			L.Add(new DE_Action
			{
				id = "SoreLoser", face = 5, bad = true,
				can = m => Colonists2(m).Any(p => !p.InMentalState && !p.Downed),
				act = m =>
				{
					Pawn p = Colonists2(m).Where(x => !x.InMentalState && !x.Downed).RandomElementWithFallback();
					p?.mindState?.mentalStateHandler?.TryStartMentalState(MentalStateDefOf.Wander_Sad, Txt("SoreLoser", ""), true);
					DiceReward.Think(p, "RT_Thought_LostCards");
				}
			});
			L.Add(new DE_Situation { id = "LoadedDice", face = 5, hediff = "RT_Sit_Loaded", bad = true });
			L.Add(new DE_Action
			{
				id = "Zzzt", face = 5, bad = true, can = m => Inc("ShortCircuit"),
				act = m => DiceReward.Fire(m, "ShortCircuit")
			});

			// ---------------- 6: mad ----------------
			L.Add(new DE_Choice { id = "Wheel", threat = true, face = 6 }
				.Opt("O1", m => SpinWheel(m))
				.Opt("O2", m => { }));
			L.Add(new DE_Action
			{
				id = "ChaosRound", threat = true, face = 6,
				act = m =>
				{
					var used = new HashSet<string> { "ChaosRound", "Double", "Reroll" };
					for (int i = 0; i < 3; i++)
					{
						DiceEvent e = All.Where(x => !(x is DE_Choice) && !(x is DE_Challenge) && !used.Contains(x.id) && Enabled(x)
							&& (!x.threat || DiceDifficulty.ThreatsAllowed) && Safe(x, m)).RandomElementWithFallback();
						if (e == null) break;
						used.Add(e.id);
						RunNow(e.id, m);
					}
				}
			});
			L.Add(new DE_Action
			{
				id = "StoneDice", threat = true, face = 6, bad = true, can = m => Inc("MeteoriteImpact"),
				act = m =>
				{
					DiceReward.Fire(m, "MeteoriteImpact");
					DiceReward.Fire(m, "MeteoriteImpact", 1f, Rand.Range(2500, 7500));
				}
			});
		}

		private static List<Pawn> Colonists2(Map m) =>
			m.mapPawns.FreeColonistsSpawned.Where(p => p != null && !p.Dead).ToList();

		private static IEnumerable<PawnKindDef> PrizeKinds() =>
			new[] { "Horse", "Alpaca", "Muffalo", "Dromedary", "Cow", "Goat", "Husky", "LabradorRetriever" }
				.Select(n => DefDatabase<PawnKindDef>.GetNamedSilentFail(n)).Where(k => k != null && k.RaceProps != null && k.RaceProps.Animal);

		private static IEnumerable<Faction> FriendlyFactions() =>
			Find.FactionManager.AllFactionsVisible.Where(f => f != null && !f.IsPlayer && !f.defeated && !f.temporary
				&& !f.HostileTo(Faction.OfPlayer) && f.CanChangeGoodwillFor(Faction.OfPlayer, 1));

		// a ticket costs 50 silver; every ticket wins 400 with a 1 in 6 chance
		private static void Lottery(Map map, int tickets)
		{
			int stake = 50 * tickets;
			if (!DiceReward.TakeSilver(map, stake))
			{
				Messages.Message("RT_EV_NoSilver".Translate(stake), MessageTypeDefOf.RejectInput, false);
				return;
			}
			int wins = 0;
			for (int i = 0; i < tickets; i++) if (Rand.RangeInclusive(1, 6) == 6) wins++;
			if (wins > 0)
			{
				DiceReward.Silver(map, 400 * wins, Txt("Lottery", "") + " ×" + wins, true);
				DiceReward.ThinkAll(map, "RT_Thought_WonBet");
			}
			else Messages.Message(Txt("Lottery", "Lose").Formatted(stake), MessageTypeDefOf.NegativeEvent, false);
		}

		// six fields: a big gift, everybody inspired, everybody healed, nothing, wild animals, a raid
		private static void SpinWheel(Map m)
		{
			int r = Rand.RangeInclusive(1, 6);
			Messages.Message(Txt("Wheel", "F" + r), MessageTypeDefOf.NeutralEvent, false);
			switch (r)
			{
				case 1: DiceReward.Drop(m, DiceReward.Goods(1500f), Txt("Wheel", "")); break;
				case 2: DiceReward.Inspire(m, 99); break;
				case 3:
					foreach (Pawn p in Colonists2(m))
						foreach (Hediff_Injury h in p.health.hediffSet.hediffs.OfType<Hediff_Injury>().ToList())
							h.Heal(30f);
					break;
				case 4: break;
				case 5: if (!DiceReward.Fire(m, "ManhunterPack", 0.8f)) DiceReward.Raid(m, 0.8f, "Wheel"); break;
				default: DiceReward.Raid(m, 1.2f, "Wheel"); break;
			}
			DiceReward.ThinkAll(m, r <= 3 ? "RT_Thought_WonBet" : r >= 5 ? "RT_Thought_LostBet" : null);
		}
	}
}
