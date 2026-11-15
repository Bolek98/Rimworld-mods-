using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SecondStep
{
	public enum AidKind { None, Prosthetic, Crutches, Wheelchair }

	public class AidExtension : DefModExtension
	{
		public AidKind kind;
		public HediffDef hediff;
	}

	[StaticConstructorOnStartup]
	public static class SecondStepMod
	{
		public const string Version = "0.1.0";

		static SecondStepMod()
		{
			new Harmony("misak.secondstep").PatchAll();
			// the "seat in a wheelchair" surgery for every humanlike race, not only Human
			RecipeDef seat = DefDatabase<RecipeDef>.GetNamedSilentFail("SS_SeatInWheelchair");
			if (seat != null)
			{
				if (seat.recipeUsers == null) seat.recipeUsers = new List<ThingDef>();
				var cache = AccessTools.Field(typeof(ThingDef), "allRecipesCached");
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d.race != null && d.race.Humanlike && !seat.recipeUsers.Contains(d))
					{
						seat.recipeUsers.Add(d);
						cache?.SetValue(d, null);
					}
			}
			Log.Message("[Second Step] v" + Version + " loaded.");
		}
	}

	public static class Legs
	{
		public static bool IsLegPart(BodyPartRecord part)
		{
			if (part?.def?.tags == null) return false;
			return part.def.tags.Contains(BodyPartTagDefOf.MovingLimbCore) || part.def.tags.Contains(BodyPartTagDefOf.MovingLimbSegment);
		}

		// a missing leg or foot that no prosthetic replaced
		public static bool MissingLeg(Pawn p)
		{
			if (p?.health?.hediffSet == null) return false;
			foreach (Hediff h in p.health.hediffSet.hediffs)
				if (h is Hediff_MissingPart && IsLegPart(h.Part))
					return true;
			return false;
		}

		// at least one leg to stand on (crutches need it)
		public static bool HasAnyLeg(Pawn p)
		{
			if (p?.health?.hediffSet == null) return false;
			foreach (BodyPartRecord r in p.health.hediffSet.GetNotMissingParts())
				if (r.def.tags != null && r.def.tags.Contains(BodyPartTagDefOf.MovingLimbCore))
					return true;
			return false;
		}

		public static Apparel WornAid(Pawn p)
		{
			if (p?.apparel == null) return null;
			foreach (Apparel a in p.apparel.WornApparel)
				if (a.def.GetModExtension<AidExtension>() != null)
					return a;
			return null;
		}

		public static ThingDef AidDef(AidKind k) =>
			k == AidKind.Crutches ? DefDatabase<ThingDef>.GetNamedSilentFail("SS_Crutches")
			: k == AidKind.Wheelchair ? DefDatabase<ThingDef>.GetNamedSilentFail("SS_Wheelchair") : null;

		// the best leg prosthetic: one in stock first, otherwise one the colony can make
		public static RecipeDef BestProsthetic(Pawn p, out BodyPartRecord part, out bool inStock)
		{
			part = null;
			inStock = false;
			RecipeDef best = null;
			float bestScore = float.MinValue;
			Map map = p.MapHeld;
			foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
			{
				if (r.addsHediff?.addedPartProps == null || r.appliedOnFixedBodyParts == null || !r.appliedOnFixedBodyParts.Contains(BodyPartDefOf.Leg))
					continue;
				if (!r.AvailableNow || r.ingredients == null || r.ingredients.Count == 0)
					continue;
				ThingDef item = r.ingredients.SelectMany(i => i.filter.AllowedThingDefs).FirstOrDefault(d => d.isTechHediff);
				if (item == null) continue;
				bool have = map != null && map.listerThings.ThingsOfDef(item).Any(t => !t.IsForbidden(Faction.OfPlayer));
				bool canMake = item.recipeMaker != null && (item.recipeMaker.researchPrerequisite == null || item.recipeMaker.researchPrerequisite.IsFinished)
					&& (item.recipeMaker.researchPrerequisites == null || item.recipeMaker.researchPrerequisites.All(x => x.IsFinished));
				if (!have && !canMake) continue;
				BodyPartRecord target = null;
				try { target = r.Worker.GetPartsToApplyOn(p, r).FirstOrDefault(x => x.def == BodyPartDefOf.Leg); }
				catch { }
				if (target == null) continue;
				float eff = r.addsHediff.addedPartProps.partEfficiency;
				// in stock: the best one; to make: the simplest one (it is ready soonest)
				float score = have ? 10f + eff : 5f - eff;
				if (score <= bestScore) continue;
				bestScore = score;
				best = r;
				part = target;
				inStock = have;
			}
			return best;
		}
	}

	public class Apparel_MobilityAid : Apparel
	{
		public override void Notify_Equipped(Pawn pawn)
		{
			base.Notify_Equipped(pawn);
			try
			{
				AidExtension ext = def.GetModExtension<AidExtension>();
				if (ext?.hediff != null && !pawn.health.hediffSet.HasHediff(ext.hediff))
					pawn.health.AddHediff(ext.hediff);
				// the outfit policy must not take it away
				pawn.outfits?.forcedHandler?.SetForced(this, true);
			}
			catch (Exception e) { Log.Warning("[Second Step] equip: " + e.Message); }
		}

		public override void Notify_Unequipped(Pawn pawn)
		{
			base.Notify_Unequipped(pawn);
			try
			{
				AidExtension ext = def.GetModExtension<AidExtension>();
				Hediff h = ext?.hediff == null ? null : pawn.health.hediffSet.GetFirstHediffOfDef(ext.hediff);
				if (h != null) pawn.health.RemoveHediff(h);
			}
			catch (Exception e) { Log.Warning("[Second Step] unequip: " + e.Message); }
		}
	}

	// a doctor seats a colonist who can't walk; the wheelchair brought as ingredient is worn (a copy keeps stuff, quality and hit points)
	public class Recipe_SeatInWheelchair : Recipe_Surgery
	{
		public override bool AvailableOnNow(Thing thing, BodyPartRecord part = null)
		{
			return thing is Pawn p && Legs.MissingLeg(p) && Legs.WornAid(p) == null;
		}

		public override void ApplyOnPawn(Pawn pawn, BodyPartRecord part, Pawn billDoer, List<Thing> ingredients, Bill bill)
		{
			Thing src = ingredients?.FirstOrDefault(t => t.def.defName == "SS_Wheelchair");
			ThingDef def = DefDatabase<ThingDef>.GetNamed("SS_Wheelchair");
			Apparel chair = (Apparel)ThingMaker.MakeThing(def, src?.Stuff ?? GenStuff.DefaultStuffFor(def));
			if (src != null)
			{
				chair.HitPoints = Math.Min(src.HitPoints, chair.MaxHitPoints);
				if (src.TryGetQuality(out QualityCategory q)) chair.TryGetComp<CompQuality>()?.SetQuality(q, null);
			}
			Apparel old = Legs.WornAid(pawn);
			if (old != null) pawn.apparel.TryDrop(old, out _, pawn.PositionHeld, false);
			pawn.apparel.Wear(chair, false, false);
			AidGame.Get()?.SetChoice(pawn, AidKind.Wheelchair);
		}
	}

	public class AidGame : GameComponent
	{
		private Dictionary<int, int> choice = new Dictionary<int, int>();
		private Dictionary<int, int> pendingSince = new Dictionary<int, int>();
		private Dictionary<int, int> lastLetter = new Dictionary<int, int>();

		public AidGame(Game game) { }

		public static AidGame Get() => Current.Game?.GetComponent<AidGame>();

		public AidKind ChoiceOf(Pawn p) => p != null && choice.TryGetValue(p.thingIDNumber, out int c) ? (AidKind)c : AidKind.None;

		public void SetChoice(Pawn p, AidKind k)
		{
			if (p == null) return;
			choice[p.thingIDNumber] = (int)k;
		}

		public void Queue(Pawn p)
		{
			if (p == null) return;
			if (!pendingSince.ContainsKey(p.thingIDNumber))
				pendingSince[p.thingIDNumber] = Find.TickManager.TicksGame;
		}

		public override void GameComponentTick()
		{
			int now = Find.TickManager.TicksGame;
			if (now % 120 == 0 && pendingSince.Count > 0)
				SendLetters(now);
			if (now % 600 == 0)
				Upkeep();
		}

		private void SendLetters(int now)
		{
			foreach (var kv in pendingSince.ToList())
			{
				if (now - kv.Value < 120) continue;
				pendingSince.Remove(kv.Key);
				Pawn p = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.FirstOrDefault(x => x.thingIDNumber == kv.Key);
				if (p == null || p.Dead || !Legs.MissingLeg(p)) continue;
				if (lastLetter.TryGetValue(kv.Key, out int last) && now - last < GenDate.TicksPerDay) continue;
				// a pawn in a wheelchair who loses the other leg does not need to be asked again
				if (Legs.WornAid(p)?.def.defName == "SS_Wheelchair") continue;
				lastLetter[kv.Key] = now;
				var letter = (Letter_LegChoice)LetterMaker.MakeLetter(
					"SS_LetterLabel".Translate(p.LabelShort, p.Named("PAWN")),
					"SS_LetterText".Translate(p.LabelShort, p.Named("PAWN")).AdjustedFor(p),
					DefDatabase<LetterDef>.GetNamed("SS_LegChoice"), new LookTargets(p));
				letter.pawn = p;
				Find.LetterStack.ReceiveLetter(letter);
			}
		}

		// put on the chosen aid when one is free; leave it when a prosthetic made it useless
		private void Upkeep()
		{
			foreach (Map map in Find.Maps)
			{
				if (!map.IsPlayerHome) continue;
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
				{
					try { UpkeepPawn(p, map); }
					catch (Exception e) { Log.Warning("[Second Step] " + p.LabelShort + ": " + e.Message); }
				}
			}
		}

		private void UpkeepPawn(Pawn p, Map map)
		{
			Apparel worn = Legs.WornAid(p);
			bool needs = Legs.MissingLeg(p);
			if (worn != null)
			{
				AidExtension ext = worn.def.GetModExtension<AidExtension>();
				bool crutchesNoLeg = ext.kind == AidKind.Crutches && !Legs.HasAnyLeg(p);
				if ((!needs || crutchesNoLeg) && !p.Downed && !p.Drafted)
				{
					p.apparel.TryDrop(worn, out _, p.PositionHeld, false);
					if (!needs) Messages.Message("SS_AidLeft".Translate(p.LabelShort, worn.LabelShort, p.Named("PAWN")), p, MessageTypeDefOf.NeutralEvent, false);
				}
				return;
			}
			if (!needs || p.Downed || p.Drafted || p.InMentalState) return;
			AidKind k = ChoiceOf(p);
			if (k != AidKind.Crutches && k != AidKind.Wheelchair) return;
			if (k == AidKind.Crutches && !Legs.HasAnyLeg(p)) return;
			Job cur = p.CurJob;
			if (cur != null && (cur.playerForced || cur.def == JobDefOf.Wear || p.jobs.curDriver?.asleep == true)) return;
			ThingDef def = Legs.AidDef(k);
			Thing aid = GenClosest.ClosestThingReachable(p.Position, map, ThingRequest.ForDef(def), PathEndMode.ClosestTouch,
				TraverseParms.For(p), 9999f, t => !t.IsForbidden(p) && p.CanReserve(t) && !t.IsBurning());
			if (aid == null) return;
			Job job = JobMaker.MakeJob(JobDefOf.Wear, aid);
			p.jobs.StartJob(job, JobCondition.InterruptOptional, null, true);
		}

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref choice, "ssChoice", LookMode.Value, LookMode.Value);
			Scribe_Collections.Look(ref lastLetter, "ssLastLetter", LookMode.Value, LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (choice == null) choice = new Dictionary<int, int>();
				if (lastLetter == null) lastLetter = new Dictionary<int, int>();
			}
		}
	}

	public class Letter_LegChoice : ChoiceLetter
	{
		public Pawn pawn;

		public override bool CanShowInLetterStack => base.CanShowInLetterStack && pawn != null && !pawn.Dead;

		public override IEnumerable<DiaOption> Choices
		{
			get
			{
				if (ArchivedOnly || pawn == null || pawn.Dead)
				{
					yield return Option_Close;
					yield break;
				}
				// prosthetic
				RecipeDef r = Legs.BestProsthetic(pawn, out BodyPartRecord part, out bool inStock);
				string item = r?.ingredients?.SelectMany(i => i.filter.AllowedThingDefs).FirstOrDefault(d => d.isTechHediff)?.label ?? "";
				var pro = new DiaOption(r == null ? "SS_OptProstheticNone".Translate().ToString()
					: (inStock ? "SS_OptProsthetic" : "SS_OptProstheticMake").Translate(item).ToString());
				pro.action = () =>
				{
					AidGame.Get()?.SetChoice(pawn, AidKind.Prosthetic);
					if (r != null && part != null && !pawn.health.surgeryBills.Bills.Any(b => b.recipe == r))
						HealthCardUtility.CreateSurgeryBill(pawn, r, part, null, true);
					Find.LetterStack.RemoveLetter(this);
				};
				pro.resolveTree = true;
				if (r == null) pro.Disable("SS_NoProsthetic".Translate());
				yield return pro;
				// crutches
				var cr = new DiaOption("SS_OptCrutches".Translate());
				cr.action = () => { Pick(AidKind.Crutches); };
				cr.resolveTree = true;
				if (!Legs.HasAnyLeg(pawn)) cr.Disable("SS_NoLegForCrutches".Translate());
				yield return cr;
				// wheelchair
				var wc = new DiaOption("SS_OptWheelchair".Translate());
				wc.action = () => { Pick(AidKind.Wheelchair); };
				wc.resolveTree = true;
				yield return wc;
				yield return Option_Postpone;
			}
		}

		private void Pick(AidKind k)
		{
			AidGame.Get()?.SetChoice(pawn, k);
			Find.LetterStack.RemoveLetter(this);
			ThingDef def = Legs.AidDef(k);
			Map map = pawn.MapHeld;
			if (def == null || map == null) return;
			if (map.listerThings.ThingsOfDef(def).Any(t => !t.IsForbidden(Faction.OfPlayer)))
			{
				if (k == AidKind.Wheelchair && pawn.Downed) AddSeatBill();
				return;
			}
			// nothing on the map: one bill on a bench that can make it
			RecipeDef make = DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(x => x.ProducedThingDef == def);
			if (make == null) return;
			if (!make.AvailableNow)
			{
				Messages.Message("SS_NeedResearch".Translate(def.label, make.researchPrerequisite?.LabelCap ?? ""), pawn, MessageTypeDefOf.CautionInput, false);
				return;
			}
			Building_WorkTable bench = map.listerBuildings.allBuildingsColonist.OfType<Building_WorkTable>()
				.FirstOrDefault(b => b.def.AllRecipes != null && b.def.AllRecipes.Contains(make) && b.CurrentlyUsableForBills());
			if (bench == null)
			{
				Messages.Message("SS_NoBench".Translate(def.label), pawn, MessageTypeDefOf.CautionInput, false);
				return;
			}
			if (bench.BillStack.Bills.Any(b => b.recipe == make)) return;
			Bill_Production bill = (Bill_Production)make.MakeNewBill();
			bill.repeatMode = BillRepeatModeDefOf.RepeatCount;
			bill.repeatCount = 1;
			bill.RenamableLabel = "SS_BillName".Translate(def.LabelCap, pawn.LabelShort);
			bench.BillStack.AddBill(bill);
			Messages.Message("SS_BillAdded".Translate(def.label, bench.LabelShort), bench, MessageTypeDefOf.PositiveEvent, false);
			if (k == AidKind.Wheelchair && pawn.Downed) AddSeatBill();
		}

		private void AddSeatBill()
		{
			RecipeDef seat = DefDatabase<RecipeDef>.GetNamedSilentFail("SS_SeatInWheelchair");
			if (seat == null || pawn.health.surgeryBills.Bills.Any(b => b.recipe == seat)) return;
			HealthCardUtility.CreateSurgeryBill(pawn, seat, null, null, false);
		}

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_References.Look(ref pawn, "pawn");
		}
	}

	// a lost leg or foot of a colonist: ask what comes next
	[HarmonyPatch(typeof(Pawn_HealthTracker), nameof(Pawn_HealthTracker.AddHediff), new[] { typeof(Hediff), typeof(BodyPartRecord), typeof(DamageInfo?), typeof(DamageWorker.DamageResult) })]
	public static class Patch_AddHediff
	{
		public static void Postfix(Hediff hediff, Pawn ___pawn)
		{
			try
			{
				if (!(hediff is Hediff_MissingPart) || !Legs.IsLegPart(hediff.Part)) return;
				Pawn p = ___pawn;
				if (p == null || p.Dead || !p.IsColonist || Current.ProgramState != ProgramState.Playing) return;
				AidGame.Get()?.Queue(p);
			}
			catch { }
		}
	}

	// in a wheelchair a colonist moves even without legs
	[HarmonyPatch(typeof(PawnCapacityWorker_Moving), nameof(PawnCapacityWorker_Moving.CalculateCapacityLevel))]
	public static class Patch_Moving
	{
		private static HediffDef chair;

		public static void Postfix(HediffSet diffSet, ref float __result)
		{
			if (chair == null) chair = DefDatabase<HediffDef>.GetNamedSilentFail("SS_InWheelchair");
			if (chair != null && __result < 0.6f && diffSet?.HasHediff(chair) == true)
				__result = 0.6f;
		}
	}

	// healthy colonists never pick crutches or a wheelchair from the outfit
	[HarmonyPatch(typeof(JobGiver_OptimizeApparel), nameof(JobGiver_OptimizeApparel.ApparelScoreRaw))]
	public static class Patch_ApparelScore
	{
		public static void Postfix(Pawn pawn, Apparel ap, ref float __result)
		{
			if (ap?.def.GetModExtension<AidExtension>() != null)
				__result = -1000f;
		}
	}

	// one worn texture for every body type
	[HarmonyPatch(typeof(ApparelGraphicRecordGetter), nameof(ApparelGraphicRecordGetter.TryGetGraphicApparel))]
	public static class Patch_Graphic
	{
		public static bool Prefix(Apparel apparel, ref ApparelGraphicRecord rec, ref bool __result)
		{
			if (apparel?.def.GetModExtension<AidExtension>() == null || apparel.WornGraphicPath.NullOrEmpty()) return true;
			Graphic g = GraphicDatabase.Get<Graphic_Multi>(apparel.WornGraphicPath, ShaderDatabase.Cutout, apparel.def.graphicData.drawSize, apparel.DrawColor);
			rec = new ApparelGraphicRecord(g, apparel);
			__result = true;
			return false;
		}
	}
}
