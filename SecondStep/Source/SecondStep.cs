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

	public class SecondStepSettings : ModSettings
	{
		// work types a colonist can't do with the aid (defNames)
		public List<string> crutchBlocked = new List<string> { "Mining", "Construction", "Hunting", "Firefighter" };
		public List<string> chairBlocked = new List<string> { "Mining", "Construction", "Hunting", "Firefighter", "Growing", "PlantCutting" };

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref crutchBlocked, "crutchBlocked", LookMode.Value);
			Scribe_Collections.Look(ref chairBlocked, "chairBlocked", LookMode.Value);
			if (crutchBlocked == null) crutchBlocked = new SecondStepSettings().crutchBlocked;
			if (chairBlocked == null) chairBlocked = new SecondStepSettings().chairBlocked;
		}
	}

	public class SecondStepMod : Mod
	{
		public const string Version = "0.2.0";
		public static SecondStepSettings S;
		private Vector2 scroll;

		public SecondStepMod(ModContentPack content) : base(content)
		{
			S = GetSettings<SecondStepSettings>();
		}

		public override string SettingsCategory() => "Second Step";

		public override void DoSettingsWindowContents(Rect rect)
		{
			Text.Font = GameFont.Small;
			Widgets.Label(new Rect(rect.x, rect.y, rect.width, 48f), "SS_SetBlockedHelp".Translate());
			var works = DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w => w.visible).OrderBy(w => w.naturalPriority * -1).ToList();
			Rect head = new Rect(rect.x, rect.y + 52f, rect.width, 26f);
			Widgets.Label(new Rect(head.x, head.y, 260f, 26f), "SS_SetWork".Translate());
			Widgets.Label(new Rect(head.x + 270f, head.y, 120f, 26f), "SS_OptCrutches".Translate());
			Widgets.Label(new Rect(head.x + 400f, head.y, 160f, 26f), "SS_OptWheelchair".Translate());
			Rect outR = new Rect(rect.x, head.yMax + 4f, rect.width, rect.height - 90f);
			Rect view = new Rect(0f, 0f, outR.width - 20f, works.Count * 28f);
			bool changed = false;
			Widgets.BeginScrollView(outR, ref scroll, view);
			for (int i = 0; i < works.Count; i++)
			{
				WorkTypeDef w = works[i];
				float y = i * 28f;
				if (i % 2 == 0) Widgets.DrawLightHighlight(new Rect(0f, y, view.width, 28f));
				Widgets.Label(new Rect(4f, y + 3f, 260f, 24f), w.labelShort.CapitalizeFirst());
				changed |= Box(new Vector2(300f, y + 2f), S.crutchBlocked, w.defName);
				changed |= Box(new Vector2(440f, y + 2f), S.chairBlocked, w.defName);
			}
			Widgets.EndScrollView();
			if (changed && Current.Game != null)
				foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists)
					p.Notify_DisabledWorkTypesChanged();
		}

		private static bool Box(Vector2 at, List<string> list, string name)
		{
			bool on = list.Contains(name), was = on;
			Widgets.Checkbox(at, ref on, 24f, false, true);
			if (on == was) return false;
			if (on) list.Add(name); else list.Remove(name);
			return true;
		}
	}

	[StaticConstructorOnStartup]
	public static class SecondStepInit
	{
		static SecondStepInit()
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
			Log.Message("[Second Step] v" + SecondStepMod.Version + " loaded.");
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

		public static AidKind KindOf(Apparel a) => a?.def.GetModExtension<AidExtension>()?.kind ?? AidKind.None;

		public static bool InChair(Pawn p) => KindOf(WornAid(p)) == AidKind.Wheelchair;

		private static HediffDef limp;
		public static HediffDef Limp => limp ?? (limp = DefDatabase<HediffDef>.GetNamedSilentFail("SS_Limping"));

		// limping: a leg is missing, at least one is left and no aid helps
		public static void RefreshLimp(Pawn p)
		{
			if (p?.health?.hediffSet == null || Limp == null || p.Dead) return;
			bool want = MissingLeg(p) && HasAnyLeg(p) && WornAid(p) == null;
			Hediff h = p.health.hediffSet.GetFirstHediffOfDef(Limp);
			if (want && h == null) p.health.AddHediff(Limp);
			else if (!want && h != null) p.health.RemoveHediff(h);
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
				Legs.RefreshLimp(pawn);
				pawn.Notify_DisabledWorkTypesChanged();
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
				Legs.RefreshLimp(pawn);
				pawn.Notify_DisabledWorkTypesChanged();
				AidGame.Get()?.StopPush(pawn);
			}
			catch (Exception e) { Log.Warning("[Second Step] unequip: " + e.Message); }
		}
	}

	public class Apparel_Wheelchair : Apparel_MobilityAid
	{
		public override IEnumerable<Gizmo> GetWornGizmos()
		{
			foreach (Gizmo g in base.GetWornGizmos()) yield return g;
			Pawn p = Wearer;
			if (p == null || !p.IsColonistPlayerControlled) yield break;
			AidGame game = AidGame.Get();
			Pawn pusher = game?.PusherOf(p);
			if (pusher == null)
			{
				yield return new Command_Action
				{
					defaultLabel = "SS_Push".Translate(),
					defaultDesc = "SS_PushDesc".Translate(),
					icon = def.uiIcon,
					action = () => Find.Targeter.BeginTargeting(new TargetingParameters
					{
						canTargetPawns = true,
						canTargetBuildings = false,
						validator = t => t.Thing is Pawn o && o != p && o.IsColonistPlayerControlled && !o.Downed && !Legs.InChair(o) && o.Map == p.Map
					}, t => game?.StartPush(p, (Pawn)t.Thing))
				};
			}
			else
			{
				yield return new Command_Action
				{
					defaultLabel = "SS_PushStop".Translate(pusher.LabelShort),
					defaultDesc = "SS_PushStopDesc".Translate(),
					icon = def.uiIcon,
					action = () => game?.StopPush(p)
				};
			}
		}
	}

	// the front of the wheelchair (armrests, foot plates, the near wheel) drawn over the sitting colonist, in the chair's colour
	public class PawnRenderNode_ChairFront : PawnRenderNode
	{
		public PawnRenderNode_ChairFront(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree) : base(pawn, props, tree) { }

		public override Graphic GraphicFor(Pawn pawn)
		{
			if (props.texPath.NullOrEmpty()) return null;
			return GraphicDatabase.Get<Graphic_Multi>(props.texPath, ShaderDatabase.Cutout, Vector2.one, apparel?.DrawColor ?? Color.white);
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
		private Dictionary<int, int> pushers = new Dictionary<int, int>();   // chair user -> pusher

		private static HediffDef pushedDef, crutchDef;
		private static readonly HashSet<string> BenchJobs = new HashSet<string>
		{
			"DoBill", "Research", "TendPatient", "OperateScanner", "OperateDeepDrill", "Repair", "UnloadYourInventory", "PlayStatic", "Meditate", "Reign", "Pray"
		};

		private static Pawn Find_(int id) => PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_Colonists.FirstOrDefault(x => x.thingIDNumber == id);

		public Pawn PusherOf(Pawn user) => user != null && pushers.TryGetValue(user.thingIDNumber, out int id) ? Find_(id) : null;

		public void StartPush(Pawn user, Pawn pusher)
		{
			if (user == null || pusher == null) return;
			foreach (var kv in pushers.Where(kv => kv.Value == pusher.thingIDNumber).ToList()) pushers.Remove(kv.Key);
			pushers[user.thingIDNumber] = pusher.thingIDNumber;
			GiveFollow(user, pusher);
			Messages.Message("SS_PushStarted".Translate(pusher.LabelShort, user.LabelShort), user, MessageTypeDefOf.SilentInput, false);
		}

		public void StopPush(Pawn user)
		{
			if (user == null || !pushers.TryGetValue(user.thingIDNumber, out int id)) return;
			pushers.Remove(user.thingIDNumber);
			Pawn pusher = Find_(id);
			if (pusher?.CurJob != null && pusher.CurJob.def == JobDefOf.FollowClose && pusher.CurJob.targetA.Thing == user)
				pusher.jobs.EndCurrentJob(JobCondition.InterruptForced);
			SetPushed(user, false);
		}

		private static void GiveFollow(Pawn user, Pawn pusher)
		{
			Job j = JobMaker.MakeJob(JobDefOf.FollowClose, user);
			j.followRadius = 1.5f;
			j.expiryInterval = 2500;
			j.checkOverrideOnExpire = true;
			pusher.jobs.StartJob(j, JobCondition.InterruptForced, null, false, true);
		}

		private static void SetPushed(Pawn user, bool on)
		{
			if (pushedDef == null) pushedDef = DefDatabase<HediffDef>.GetNamedSilentFail("SS_Pushed");
			if (pushedDef == null || user?.health == null) return;
			Hediff h = user.health.hediffSet.GetFirstHediffOfDef(pushedDef);
			if (on && h == null) user.health.AddHediff(pushedDef);
			else if (!on && h != null) user.health.RemoveHediff(h);
		}

		private void PushTick()
		{
			foreach (var kv in pushers.ToList())
			{
				Pawn user = Find_(kv.Key), pusher = Find_(kv.Value);
				bool ok = user != null && pusher != null && Legs.InChair(user) && user.Spawned && pusher.Spawned && user.Map == pusher.Map
					&& !pusher.Downed && !pusher.Drafted && !pusher.InMentalState && !Legs.InChair(pusher);
				if (!ok)
				{
					pushers.Remove(kv.Key);
					if (user != null) SetPushed(user, false);
					continue;
				}
				Job cur = pusher.CurJob;
				bool following = cur != null && cur.def == JobDefOf.FollowClose && cur.targetA.Thing == user;
				// needs (food, sleep, joy …) come first; idle or ordinary work goes back to pushing
				if (!following && (cur == null || cur.workGiverDef != null || cur.def == JobDefOf.Wait_Wander || cur.def == JobDefOf.GotoWander || cur.def == JobDefOf.Wait))
					GiveFollow(user, pusher);
				SetPushed(user, following && pusher.Position.InHorDistOf(user.Position, 2.5f));
			}
		}

		// crutch wearers working at a bench lean one crutch aside: both hands free, one crutch drawn
		private void LeanTick()
		{
			if (crutchDef == null) crutchDef = DefDatabase<HediffDef>.GetNamedSilentFail("SS_OnCrutches");
			if (crutchDef == null) return;
			foreach (Map map in Find.Maps)
				foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
				{
					Hediff h = p.health.hediffSet.GetFirstHediffOfDef(crutchDef);
					if (h == null) continue;
					Job j = p.CurJob;
					bool lean = j != null && !p.pather.Moving && BenchJobs.Contains(j.def.defName);
					float want = lean ? 1f : 0.1f;
					if (Math.Abs(h.Severity - want) < 0.01f) continue;
					h.Severity = want;
					p.Drawer?.renderer?.SetAllGraphicsDirty();
				}
		}

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
			if (now % 30 == 0)
			{
				LeanTick();
				if (pushers.Count > 0) PushTick();
			}
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
			Legs.RefreshLimp(p);
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
			Scribe_Collections.Look(ref pushers, "ssPushers", LookMode.Value, LookMode.Value);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (choice == null) choice = new Dictionary<int, int>();
				if (lastLetter == null) lastLetter = new Dictionary<int, int>();
				if (pushers == null) pushers = new Dictionary<int, int>();
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
				Legs.RefreshLimp(p);
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
			if (chair != null && __result < 0.45f && diffSet?.HasHediff(chair) == true)
				__result = 0.45f;
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

	[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetDisabledWorkTypes))]
	public static class Patch_DisabledWork
	{
		public static void Postfix(Pawn __instance, bool permanentOnly, List<WorkTypeDef> __result)
		{
			if (permanentOnly || __result == null || SecondStepMod.S == null || __instance?.apparel == null) return;
			AidKind k = Legs.KindOf(Legs.WornAid(__instance));
			List<string> names = k == AidKind.Crutches ? SecondStepMod.S.crutchBlocked : k == AidKind.Wheelchair ? SecondStepMod.S.chairBlocked : null;
			if (names == null) return;
			foreach (string n in names)
			{
				WorkTypeDef w = DefDatabase<WorkTypeDef>.GetNamedSilentFail(n);
				if (w != null && !__result.Contains(w)) __result.Add(w);
			}
		}
	}

	// one worn texture for every body type
	[HarmonyPatch(typeof(ApparelGraphicRecordGetter), nameof(ApparelGraphicRecordGetter.TryGetGraphicApparel))]
	public static class Patch_Graphic
	{
		public static bool Prefix(Apparel apparel, ref ApparelGraphicRecord rec, ref bool __result)
		{
			if (apparel?.def.GetModExtension<AidExtension>() == null || apparel.WornGraphicPath.NullOrEmpty()) return true;
			string path = apparel.WornGraphicPath;
			Pawn w = apparel.Wearer;
			if (Legs.KindOf(apparel) == AidKind.Crutches && w != null)
			{
				Hediff h = w.health?.hediffSet?.GetFirstHediffOfDef(DefDatabase<HediffDef>.GetNamedSilentFail("SS_OnCrutches"));
				if (h != null && h.Severity >= 0.5f) path += "Lean";
			}
			Graphic g = GraphicDatabase.Get<Graphic_Multi>(path, ShaderDatabase.Cutout, apparel.def.graphicData.drawSize, apparel.DrawColor);
			rec = new ApparelGraphicRecord(g, apparel);
			__result = true;
			return false;
		}
	}
}
