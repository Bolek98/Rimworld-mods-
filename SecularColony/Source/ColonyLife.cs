// Secular Colony 1.7.0 – colony life without belief: ceremony helper, secular moods,
// slavery acceptance and a colony speaker.
using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace SecularColony
{
	public static class ColonyLife
	{
		public static void Apply(Harmony h)
		{
			try
			{
				// the vanilla "bestower waiting" alert is replaced by ours (clickable) while the helper is on
				Type t = AccessTools.TypeByName("RimWorld.Alert_BestowerWaiting");
				if (t != null)
					h.Patch(AccessTools.Method(t, "GetReport"), postfix: new HarmonyMethod(typeof(ColonyLife), nameof(VanillaAlert_Postfix)));
				h.Patch(AccessTools.Method(typeof(Thing), nameof(Thing.Ingested)), postfix: new HarmonyMethod(typeof(ColonyLife), nameof(Ingested_Postfix)));
				h.Patch(AccessTools.Method(typeof(Pawn), nameof(Pawn.GetGizmos)), postfix: new HarmonyMethod(typeof(ColonyLife), nameof(Gizmos_Postfix)));
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] colony life patches failed: " + ex.Message);
			}
		}

		public static void VanillaAlert_Postfix(ref AlertReport __result)
		{
			if (__result.active && SecularMod.Settings != null && SecularMod.Settings.ceremonyHelper)
				__result = AlertReport.Inactive;
		}

		// ---------------------------------------------------------------- ceremony helper

		public static IEnumerable<Lord> WaitingCeremonies()
		{
			if (!ModsConfig.RoyaltyActive || Find.Maps == null)
				yield break;
			foreach (Map map in Find.Maps)
				foreach (Lord lord in map.lordManager.lords)
					if (lord.CurLordToil is LordToil_BestowingCeremony_Wait && lord.LordJob is LordJob_BestowingCeremony)
						yield return lord;
		}

		// ticks until the ceremony quest ends (its shown expiry), -1 when unknown
		public static int TicksLeft(Lord lord)
		{
			try
			{
				if (lord?.questTags == null || Find.QuestManager == null)
					return -1;
				int best = -1;
				foreach (Quest q in Find.QuestManager.QuestsListForReading)
				{
					if (q.State != QuestState.Ongoing || !q.PartsListForReading.Any(x => x is QuestPart_BestowingCeremony b && b.questTag != null && lord.questTags.Contains(b.questTag)))
						continue;
					foreach (QuestPart part in q.PartsListForReading)
					{
						QuestPart_Delay d = part as QuestPart_Delay;
						if (d == null || d.State != QuestPartState.Enabled || d.expiryInfoPart.NullOrEmpty())
							continue;
						int left = d.TicksLeft;
						if (left > 0 && (best < 0 || left < best))
							best = left;
					}
				}
				return best;
			}
			catch
			{
				return -1;
			}
		}

		// what the vanilla "Begin ceremony" button would say when it can't start now (null = can start)
		public static string CannotStart(Lord lord)
		{
			LordJob_BestowingCeremony job = (LordJob_BestowingCeremony)lord.LordJob;
			Pawn bestower = job.bestower, target = job.target;
			if (bestower == null || target == null)
				return "?";
			if (!JobDriver_BestowingCeremony.AnalyzeThroneRoom(bestower, target))
				return "BestowingCeremonyThroneroomRequirementsNotSatisfiedShort".Translate(target.Named("PAWN"), target.royalty.GetTitleAwardedWhenUpdating(bestower.Faction, target.royalty.GetFavor(bestower.Faction)).label.Named("TITLE"));
			if (!job.GetSpot().IsValid)
				return "MessageBestowerUnreachable".Translate();
			Lord other = target.GetLord();
			if (other != null)
				return (other.LordJob is LordJob_Ritual) ? "CantStartRitualTargetIsAlreadyInRitual".Translate(target.LabelShort).ToString() : "MessageBestowingTargetIsBusy".Translate(target.LabelShort).ToString();
			return null;
		}

		// opens the same "begin ritual" window as the vanilla button on the bestower
		public static void Begin(Lord lord)
		{
			LordJob_BestowingCeremony job = (LordJob_BestowingCeremony)lord.LordJob;
			CameraJumper.TryJumpAndSelect(job.bestower);
			string why = CannotStart(lord);
			if (why != null)
			{
				Messages.Message("SC_CeremonyCant".Translate(why), job.target, MessageTypeDefOf.RejectInput, false);
				return;
			}
			Command cmd = lord.CurLordToil.GetPawnGizmos(job.bestower).OfType<Command>().FirstOrDefault();
			if (cmd != null)
				cmd.ProcessInput(Event.current);
		}

		private static readonly HashSet<int> warned = new HashSet<int>();

		// once a day: a message when a waiting ceremony ends in less than a day
		public static void CheckExpiry()
		{
			if (SecularMod.Settings == null || !SecularMod.Settings.ceremonyHelper)
				return;
			foreach (Lord lord in WaitingCeremonies())
			{
				int left = TicksLeft(lord);
				if (left < 0 || left > GenDate.TicksPerDay || !warned.Add(lord.loadID))
					continue;
				LordJob_BestowingCeremony job = (LordJob_BestowingCeremony)lord.LordJob;
				Messages.Message("SC_CeremonyExpiring".Translate(job.target.LabelShort, left.ToStringTicksToPeriod()), job.bestower, MessageTypeDefOf.CautionInput, false);
			}
		}

		// ---------------------------------------------------------------- secular moods

		public static bool MoodsOn => SecularMod.Settings != null && SecularMod.Settings.moods && Secular.Active;

		// "shared a meal": a meal eaten while at least two other colonists eat in the same room
		public static void Ingested_Postfix(Thing __instance, Pawn ingester)
		{
			try
			{
				if (!MoodsOn || !SecularMod.Settings.secularMoods || ingester == null || !ingester.Spawned || !Secular.IsOurs(ingester))
					return;
				if (__instance?.def?.ingestible == null || !__instance.def.IsNutritionGivingIngestible || __instance.def.ingestible.preferability < FoodPreferability.MealAwful)
					return;
				Room room = ingester.GetRoom();
				if (room == null || room.PsychologicallyOutdoors)
					return;
				int others = 0;
				foreach (Pawn p in ingester.Map.mapPawns.FreeColonistsSpawned)
					if (p != ingester && p.GetRoom() == room && p.CurJobDef == JobDefOf.Ingest)
						others++;
				if (others < 2)
					return;
				ThoughtDef def = DefDatabase<ThoughtDef>.GetNamedSilentFail("SC_SharedMeal");
				if (def != null)
					ingester.needs?.mood?.thoughts?.memories?.TryGainMemory(def);
			}
			catch { }
		}

		// ---------------------------------------------------------------- colony speaker

		public static HediffDef SpeakerDef => DefDatabase<HediffDef>.GetNamedSilentFail("SC_ColonySpeaker");

		public static IEnumerable<Gizmo> Gizmos_Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
		{
			foreach (Gizmo g in __result)
				yield return g;
			SecularSettings s = SecularMod.Settings;
			Pawn p = __instance;
			if (s == null || !s.colonySpeaker || Current.Game == null || p == null || !p.IsColonistPlayerControlled || p.IsSlave || !p.RaceProps.Humanlike)
				yield break;
			SecularGameComponent gc = Current.Game.GetComponent<SecularGameComponent>();
			if (gc == null)
				yield break;
			bool isSpeaker = gc.speakerId == p.thingIDNumber;
			Command_Action c = new Command_Action();
			c.defaultLabel = (isSpeaker ? "SC_SpeakerUnset" : "SC_SpeakerSet").Translate();
			c.defaultDesc = "SC_SpeakerSet_Desc".Translate();
			c.icon = ContentFinder<Texture2D>.Get("UI/Icons/Rituals/BestowCeremony", false) ?? BaseContent.BadTex;
			if (isSpeaker)
				c.defaultIconColor = new Color(1f, 0.85f, 0.4f);
			c.action = delegate
			{
				gc.SetSpeaker(isSpeaker ? null : p);
				if (!isSpeaker)
					Messages.Message("SC_SpeakerNow".Translate(p.LabelShort), p, MessageTypeDefOf.PositiveEvent, false);
			};
			yield return c;
		}

		// only the chosen speaker carries the hediff; dead or gone speakers are cleared
		public static void SyncSpeaker(SecularGameComponent gc)
		{
			HediffDef def = SpeakerDef;
			if (def == null)
				return;
			bool on = SecularMod.Settings != null && SecularMod.Settings.colonySpeaker;
			foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
			{
				if (p?.health?.hediffSet == null)
					continue;
				Hediff h = p.health.hediffSet.GetFirstHediffOfDef(def);
				bool want = on && gc.speakerId == p.thingIDNumber && !p.IsSlave && !p.IsPrisoner;
				if (want && h == null)
					p.health.AddHediff(def);
				else if (!want && h != null)
					p.health.RemoveHediff(h);
			}
			if (on && gc.speakerId != 0 && !PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction.Any(x => x.thingIDNumber == gc.speakerId))
				gc.speakerId = 0;
		}
	}

	// clickable alert: "ceremony ready" – click opens the begin window directly
	public class Alert_SecularCeremony : Alert
	{
		private readonly List<Pawn> culprits = new List<Pawn>();
		private Lord first;

		public Alert_SecularCeremony()
		{
			defaultPriority = AlertPriority.High;
			requireRoyalty = true;
		}

		public override AlertReport GetReport()
		{
			culprits.Clear();
			first = null;
			if (SecularMod.Settings == null || !SecularMod.Settings.ceremonyHelper)
				return AlertReport.Inactive;
			foreach (Lord lord in ColonyLife.WaitingCeremonies())
			{
				if (first == null)
					first = lord;
				culprits.Add(((LordJob_BestowingCeremony)lord.LordJob).bestower);
			}
			return culprits.Count == 0 ? AlertReport.Inactive : AlertReport.CulpritsAre(culprits);
		}

		public override string GetLabel()
		{
			if (first == null)
				return "SC_CeremonyAlert".Translate("");
			return "SC_CeremonyAlert".Translate(((LordJob_BestowingCeremony)first.LordJob).target.LabelShort);
		}

		public override TaggedString GetExplanation()
		{
			if (first == null)
				return "";
			LordJob_BestowingCeremony job = (LordJob_BestowingCeremony)first.LordJob;
			string text = "SC_CeremonyAlertDesc".Translate(job.bestower.LabelShort, job.target.LabelShort);
			int left = ColonyLife.TicksLeft(first);
			if (left > 0)
				text += "\n\n" + "SC_CeremonyExpires".Translate(left.ToStringTicksToPeriod());
			string why = ColonyLife.CannotStart(first);
			if (why != null)
				text += "\n\n" + "SC_CeremonyCant".Translate(why);
			return text;
		}

		public override string GetJumpToTargetsText => "SC_CeremonyClick".Translate();

		protected override void OnClick()
		{
			if (first != null)
				ColonyLife.Begin(first);
		}
	}

	// "proud of our colony": colony wealth, only without belief moods
	public class ThoughtWorker_SecularPride : ThoughtWorker
	{
		protected override ThoughtState CurrentStateInternal(Pawn p)
		{
			if (!ColonyLife.MoodsOn || !SecularMod.Settings.secularMoods || !Secular.IsOurs(p) || p.IsSlave || p.MapHeld == null || !p.MapHeld.IsPlayerHome)
				return ThoughtState.Inactive;
			float wealth = p.MapHeld.wealthWatcher.WealthTotal;
			if (wealth >= 600000f) return ThoughtState.ActiveAtStage(2);
			if (wealth >= 250000f) return ThoughtState.ActiveAtStage(1);
			if (wealth >= 80000f) return ThoughtState.ActiveAtStage(0);
			return ThoughtState.Inactive;
		}
	}

	// slavery accepted / rejected by the colony (without precepts)
	public class ThoughtWorker_SecularSlavery : ThoughtWorker
	{
		protected override ThoughtState CurrentStateInternal(Pawn p)
		{
			SecularSettings s = SecularMod.Settings;
			if (s == null || s.slaveryView == 1 || !ColonyLife.MoodsOn || !Secular.IsOurs(p) || p.IsSlave || p.MapHeld == null)
				return ThoughtState.Inactive;
			if (p.MapHeld.mapPawns.SlavesOfColonySpawned.Count == 0)
				return ThoughtState.Inactive;
			return ThoughtState.ActiveAtStage(s.slaveryView == 0 ? 0 : 1);
		}
	}
}
