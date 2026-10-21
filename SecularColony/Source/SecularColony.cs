// Secular Colony – your colony ignores ideoligion (RimWorld 1.6, Ideology DLC)
// Author: Bolek98. C# 5 syntax only (fallback csc).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace SecularColony
{
	public class SecularSettings : ModSettings
	{
		public bool enabled = true;
		public bool moods = true;
		public bool certainty = true;
		public bool willing = true;
		public bool mentalBreaks = true;
		public bool affectSlaves = true;
		public bool hideAlerts = true;
		// 1.1.0: colonists want a new look by themselves
		public bool lookChange = true;
		public float lookChangeDays = 15f;
		public bool lookChangeMessage = false;
		public bool styleFree = true;
		// 1.4.0: barber's table, styles by gender, hair colours
		public bool barberTable = true;
		public bool genderStyles = true;
		public bool hairColours = true;
		public float hairColourChance = 0.3f;
		// 1.2.0: relationships without beliefs – 0 = by traits, 1 = one partner for everyone, 2 = free love for everyone
		public int relationRule = 0;
		public int maxPartners = 4;
		public float polyChance = 1f;
		public bool polyNoJealousy = true;
		public bool polygamyMood = true;
		// 1.5.0: partial mode – keep some belief moods
		public bool keepRituals;
		public bool keepRoles;
		public bool keepBuildings;
		public bool keepApparel;
		public bool recommended = true;

		public override void ExposeData()
		{
			base.ExposeData();
			Scribe_Values.Look(ref enabled, "enabled", true);
			Scribe_Values.Look(ref moods, "moods", true);
			Scribe_Values.Look(ref certainty, "certainty", true);
			Scribe_Values.Look(ref willing, "willing", true);
			Scribe_Values.Look(ref mentalBreaks, "mentalBreaks", true);
			Scribe_Values.Look(ref affectSlaves, "affectSlaves", true);
			Scribe_Values.Look(ref hideAlerts, "hideAlerts", true);
			Scribe_Values.Look(ref lookChange, "lookChange", true);
			Scribe_Values.Look(ref lookChangeDays, "lookChangeDays", 15f);
			Scribe_Values.Look(ref lookChangeMessage, "lookChangeMessage", false);
			Scribe_Values.Look(ref styleFree, "styleFree", true);
			Scribe_Values.Look(ref barberTable, "barberTable", true);
			Scribe_Values.Look(ref genderStyles, "genderStyles", true);
			Scribe_Values.Look(ref hairColours, "hairColours", true);
			Scribe_Values.Look(ref hairColourChance, "hairColourChance", 0.3f);
			Scribe_Values.Look(ref relationRule, "relationRule", 0);
			Scribe_Values.Look(ref maxPartners, "maxPartners", 4);
			Scribe_Values.Look(ref polyChance, "polyChance", 1f);
			Scribe_Values.Look(ref polyNoJealousy, "polyNoJealousy", true);
			Scribe_Values.Look(ref polygamyMood, "polygamyMood", true);
			Scribe_Values.Look(ref keepRituals, "keepRituals", false);
			Scribe_Values.Look(ref keepRoles, "keepRoles", false);
			Scribe_Values.Look(ref keepBuildings, "keepBuildings", false);
			Scribe_Values.Look(ref keepApparel, "keepApparel", false);
			Scribe_Values.Look(ref recommended, "recommended", false);
			relationRule = Mathf.Clamp(relationRule, 0, 2);
			maxPartners = Mathf.Clamp(maxPartners, 2, 8);
			polyChance = Mathf.Clamp(polyChance, 0f, 3f);
			lookChangeDays = Mathf.Clamp(lookChangeDays, 3f, 60f);
		}
	}

	public class SecularMod : Mod
	{
		public const string Version = "1.5.0";
		public static SecularSettings Settings;
		private Vector2 scroll;
		private float viewH = 1200f;
		private static List<string> recBullets;

		public SecularMod(ModContentPack content) : base(content)
		{
			Settings = GetSettings<SecularSettings>();
			MUI.Init(new SecularSettings(), null, "recommended");
			if (Settings.recommended)
				MUI.ApplyAll(Settings);
			try
			{
				Harmony h = new Harmony("misak.secularcolony");
				MUI.PatchWindow(h, typeof(SecularMod));
				Patches.Apply(h);
				SalonPatches.Apply(h);
			}
			catch (Exception ex)
			{
				Log.Error("[Secular Colony] Harmony failed: " + ex);
			}
			Log.Message("[Secular Colony] v" + Version + " loaded.");
		}

		public override string SettingsCategory()
		{
			return "SC_ModName".Translate();
		}

		public override void DoSettingsWindowContents(Rect inRect)
		{
			SecularSettings S = Settings;
			MUI.Version(inRect, "v" + Version + " · Bolek98");
			if (recBullets == null)
			{
				recBullets = new List<string>();
				for (int i = 1; i <= 4; i++)
					recBullets.Add(("SC_Rec" + i).Translate());
			}
			float y0 = inRect.y;
			if (!ModsConfig.IdeologyActive)
			{
				GUI.color = MUI.Warn;
				Widgets.Label(new Rect(inRect.x, y0, inRect.width, 24f), "SC_NoIdeology".Translate());
				GUI.color = Color.white;
				y0 += 26f;
			}
			y0 += MUI.RecommendedCard(new Rect(inRect.x, y0, inRect.width, 0f), S, S.recommended, v =>
			{
				S.recommended = v;
				if (v) MUI.ApplyAll(S);
			}, recBullets) + 6f;
			if (S.recommended)
				y0 += MUI.LockNote(new Rect(inRect.x, y0, inRect.width, 0f)) + 4f;
			Rect outR = new Rect(inRect.x, y0, inRect.width, inRect.yMax - y0);
			Rect view = new Rect(0f, 0f, outR.width - 16f, viewH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			MUI.BeginLock(S.recommended);
			float colW = (view.width - 24f) / 2f;
			float hL, hR;
			try
			{
				var list = new Listing_Standard();
				list.Begin(new Rect(0f, 0f, colW, 100000f));
				Check(list, "SC_Enable", ref S.enabled, "enabled");
				MUI.Section(list, "SC_H_Belief".Translate());
				Check(list, "SC_Moods", ref S.moods, "moods");
				if (S.moods)
				{
					Note(list, "SC_KeepHint".Translate());
					Check(list, "SC_KeepRituals", ref S.keepRituals, "keepRituals");
					Check(list, "SC_KeepRoles", ref S.keepRoles, "keepRoles");
					Check(list, "SC_KeepBuildings", ref S.keepBuildings, "keepBuildings");
					Check(list, "SC_KeepApparel", ref S.keepApparel, "keepApparel");
				}
				Check(list, "SC_Certainty", ref S.certainty, "certainty");
				Check(list, "SC_Willing", ref S.willing, "willing");
				Check(list, "SC_MentalBreaks", ref S.mentalBreaks, "mentalBreaks");
				Check(list, "SC_Alerts", ref S.hideAlerts, "hideAlerts");
				Check(list, "SC_Slaves", ref S.affectSlaves, "affectSlaves");
				MUI.Section(list, "SC_H_Look".Translate());
				Check(list, "SC_LookChange", ref S.lookChange, "lookChange");
				if (S.lookChange)
				{
					S.lookChangeDays = Mathf.Round(MUI.Slider(list, "SC_LookChangeDays".Translate(Mathf.RoundToInt(S.lookChangeDays)), S.lookChangeDays, 3f, 60f, "SC_LookChangeDays_Desc".Translate(), S, "lookChangeDays"));
					Check(list, "SC_LookChangeMessage", ref S.lookChangeMessage, "lookChangeMessage");
				}
				Check(list, "SC_StyleFree", ref S.styleFree, "styleFree");
				Check(list, "SC_BarberTable", ref S.barberTable, "barberTable");
				Check(list, "SC_GenderStyles", ref S.genderStyles, "genderStyles");
				Check(list, "SC_HairColours", ref S.hairColours, "hairColours");
				if (S.hairColours)
				{
					int hc = Mathf.RoundToInt(S.hairColourChance * 100f);
					S.hairColourChance = Mathf.Round(MUI.Slider(list, "SC_HairColourChance".Translate(hc), S.hairColourChance, 0f, 1f, "SC_HairColourChance_Desc".Translate(), S, "hairColourChance") * 20f) / 20f;
				}
				hL = list.CurHeight;
				list.End();

				var r = new Listing_Standard();
				r.Begin(new Rect(colW + 24f, 0f, colW, 100000f));
				MUI.Section(r, "SC_H_Love".Translate());
				Note(r, "SC_LoveHint".Translate());
				for (int i = 0; i < 3; i++)
				{
					if (r.RadioButton(("SC_Rule" + i).Translate(), S.relationRule == i, 0f, ("SC_Rule" + i + "_Desc").Translate()))
						S.relationRule = i;
				}
				r.Gap(4f);
				if (S.relationRule != 1)
					S.maxPartners = Mathf.RoundToInt(MUI.Slider(r, "SC_MaxPartners".Translate(S.maxPartners), S.maxPartners, 2f, 8f, "SC_MaxPartners_Desc".Translate(), S, "maxPartners"));
				int pct = Mathf.RoundToInt(S.polyChance * 100f);
				S.polyChance = Mathf.Round(MUI.Slider(r, "SC_PolyChance".Translate(pct), S.polyChance, 0f, 3f, "SC_PolyChance_Desc".Translate(), S, "polyChance") * 10f) / 10f;
				Check(r, "SC_PolyNoJealousy", ref S.polyNoJealousy, "polyNoJealousy");
				Check(r, "SC_PolygamyMood", ref S.polygamyMood, "polygamyMood");
				hR = r.CurHeight;
				r.End();
			}
			finally
			{
				MUI.EndLock(new Rect(0f, 0f, view.width, viewH));
			}
			// 1.3.0: remove the ideoligion completely (classic mode) – an action, never locked
			var n = new Listing_Standard();
			float yN = Mathf.Max(hL, hR) + 10f;
			n.Begin(new Rect(0f, yN, view.width, 400f));
			MUI.Section(n, "SC_H_NoBelief".Translate());
			bool inGame = Current.ProgramState == ProgramState.Playing && Find.IdeoManager != null && ModsConfig.IdeologyActive;
			if (!inGame)
				Note(n, "SC_NoBelief_NoGame".Translate());
			else if (Find.IdeoManager.classicMode)
			{
				GUI.color = MUI.Accent;
				n.Label("SC_NoBelief_Done".Translate());
				GUI.color = Color.white;
				if (n.ButtonText("SC_NoBelief_Undo".Translate()))
					NoBelief.Undo();
			}
			else
			{
				n.Label("SC_NoBelief_Desc".Translate());
				if (n.ButtonText("SC_NoBelief_Button".Translate()))
					Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("SC_NoBelief_Confirm".Translate(), delegate { NoBelief.Convert(); }, true));
			}
			n.Gap(6f);
			Note(n, "SC_Note".Translate());
			viewH = yN + n.CurHeight + 20f;
			n.End();
			Widgets.EndScrollView();
		}

		private static void Note(Listing_Standard l, string text)
		{
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			l.Label(text);
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			l.Gap(2f);
		}

		private static void Check(Listing_Standard l, string key, ref bool value, string field)
		{
			MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref value, Settings, field);
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			PolyTraits.ApplyChance();
			SecularGameComponent.RequestCleanup();
		}
	}

	public static class Secular
	{
		private static HashSet<ThoughtDef> ideoThoughts;

		// 1.5.0: 0 belief/precept, 1 ritual, 2 role, 3 building, 4 apparel and colours
		private static Dictionary<ThoughtDef, int> category;

		private static int CategoryOf(ThoughtDef def)
		{
			string n = (def.thoughtClass?.Name ?? "") + "|" + (def.workerClass?.Name ?? "") + "|" + def.defName;
			if (n.IndexOf("Ritual", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Relic", StringComparison.OrdinalIgnoreCase) >= 0)
				return 1;
			if (n.IndexOf("IdeoRole", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Leader", StringComparison.OrdinalIgnoreCase) >= 0)
				return 2;
			if (n.IndexOf("Building", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Altar", StringComparison.OrdinalIgnoreCase) >= 0)
				return 3;
			if (n.IndexOf("Apparel", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("Color", StringComparison.OrdinalIgnoreCase) >= 0)
				return 4;
			return 0;
		}

		private static bool Kept(int cat)
		{
			SecularSettings s = SecularMod.Settings;
			switch (cat)
			{
				case 1: return s.keepRituals;
				case 2: return s.keepRoles;
				case 3: return s.keepBuildings;
				case 4: return s.keepApparel;
				default: return false;
			}
		}

		private static HashSet<MentalBreakDef> ideoBreaks;

		private static readonly string[] IdeoThoughtClasses = new string[]
		{
			"Thought_Situational_Precept_SlavesInColony", "Thought_IdeoRoleApparelRequirementNotMet", "Thought_IdeoRoleLost", "Thought_RelicAtRitual", "Thought_AttendedRitual", "Thought_IdeoRoleEmpty", "Thought_IdeoMissingBuilding", "Thought_IdeoDisrespectedBuilding", "Thought_Situational_Precept_HighLife", "Thought_IdeoLeaderResentment"
		};

		public static bool Active
		{
			get
			{
				SecularSettings s = SecularMod.Settings;
				return s != null && s.enabled && ModsConfig.IdeologyActive && Current.Game != null;
			}
		}

		public static bool IsOurs(Pawn pawn)
		{
			if (pawn == null || pawn.Faction == null || !pawn.Faction.IsPlayer || pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
			{
				return false;
			}
			if (pawn.IsSlave && !SecularMod.Settings.affectSlaves)
			{
				return false;
			}
			return true;
		}

		public static bool On(Pawn pawn, bool feature)
		{
			return feature && Active && IsOurs(pawn);
		}

		private static bool IsIdeoClass(Type t)
		{
			for (Type type = t; type != null && type != typeof(object); type = type.BaseType)
			{
				for (int i = 0; i < IdeoThoughtClasses.Length; i++)
				{
					if (type.Name == IdeoThoughtClasses[i])
					{
						return true;
					}
				}
			}
			return false;
		}

		private static void Build()
		{
			ideoThoughts = new HashSet<ThoughtDef>();
			ideoBreaks = new HashSet<MentalBreakDef>();
			foreach (ThoughtDef def in DefDatabase<ThoughtDef>.AllDefsListForReading)
			{
				if (def == null)
				{
					continue;
				}
				Type w = def.workerClass;
				if (w != null && (typeof(ThoughtWorker_Precept).IsAssignableFrom(w) || typeof(ThoughtWorker_Precept_Social).IsAssignableFrom(w) || typeof(ThoughtWorker_WearingColor_Ideo).IsAssignableFrom(w)))
				{
					ideoThoughts.Add(def);
				}
				else if (def.thoughtClass != null && IsIdeoClass(def.thoughtClass))
				{
					ideoThoughts.Add(def);
				}
			}
			foreach (PreceptDef p in DefDatabase<PreceptDef>.AllDefsListForReading)
			{
				if (p == null || p.comps == null)
				{
					continue;
				}
				for (int i = 0; i < p.comps.Count; i++)
				{
					PreceptComp_MentalBreak mb = p.comps[i] as PreceptComp_MentalBreak;
					if (mb != null && mb.mentalBreakDef != null)
					{
						ideoBreaks.Add(mb.mentalBreakDef);
					}
					PreceptComp_SituationalThought st = p.comps[i] as PreceptComp_SituationalThought;
					if (st != null && st.thought != null)
					{
						ideoThoughts.Add(st.thought);
					}
				}
			}
			foreach (MentalBreakDef b in DefDatabase<MentalBreakDef>.AllDefsListForReading)
			{
				if (b != null && (b.requiredPrecept != null || (b.workerClass != null && typeof(MentalBreakWorker_IdeoChange).IsAssignableFrom(b.workerClass))))
				{
					ideoBreaks.Add(b);
				}
			}
			Log.Message("[Secular Colony] ideoligion thoughts: " + ideoThoughts.Count + ", ideoligion mental breaks: " + ideoBreaks.Count + ".");
		}

		// 1.0.2: changing the game language reloads every Def (new objects), but
		// these sets kept the old ones, so after a language change no belief
		// thought or mental break was recognised any more. A new or loaded game
		// always comes after such a reload, so the sets are rebuilt per game.
		public static void ResetCaches()
		{
			ideoThoughts = null;
			ideoBreaks = null;
			category = null;
		}

		public static bool IsIdeoThought(ThoughtDef def)
		{
			if (def == null)
			{
				return false;
			}
			if (ideoThoughts == null)
			{
				Build();
			}
			if (!ideoThoughts.Contains(def))
				return false;
			if (category == null)
				category = new Dictionary<ThoughtDef, int>();
			if (!category.TryGetValue(def, out int cat))
				category[def] = cat = CategoryOf(def);
			return !Kept(cat);
		}

		public static bool IsIdeoMemory(Thought_Memory t)
		{
			if (t == null) return false;
			if (IsIdeoThought(t.def)) return true;
			// a memory from a precept counts unless its kind is kept (1.5.0)
			if (t.sourcePrecept == null) return false;
			if (category == null) category = new Dictionary<ThoughtDef, int>();
			if (!category.TryGetValue(t.def, out int cat)) category[t.def] = cat = CategoryOf(t.def);
			return !Kept(cat);
		}

		public static bool IsIdeoBreak(MentalBreakDef def)
		{
			if (def == null)
			{
				return false;
			}
			if (ideoBreaks == null)
			{
				Build();
			}
			return ideoBreaks.Contains(def);
		}

		public static int CleanPawn(Pawn pawn)
		{
			if (pawn == null || pawn.needs == null || pawn.needs.mood == null || pawn.needs.mood.thoughts == null)
			{
				return 0;
			}
			MemoryThoughtHandler memories = pawn.needs.mood.thoughts.memories;
			if (memories == null)
			{
				return 0;
			}
			List<Thought_Memory> list = memories.Memories;
			int removed = 0;
			for (int i = list.Count - 1; i >= 0; i--)
			{
				if (IsIdeoMemory(list[i]))
				{
					memories.RemoveMemory(list[i]);
					removed++;
				}
			}
			return removed;
		}
	}

	// 1.1.0: colonists sometimes want a new look by themselves. Only raises the
	// vanilla wish (Pawn_StyleTracker.lookChangeDesired); vanilla's think tree
	// (ThinkNode_ConditionalWantsLookChange -> JobGiver_UseStylingStationAutomatic)
	// then sends the pawn to a styling station and Toils_StyleChange picks the style.
	public static class LookWishes
	{
		public const int CheckInterval = 2500;

		// a wish we raised that is not fulfilled in this time is dropped again,
		// so nobody keeps the "wants a look change" thought forever
		private const int ExpireTicks = 60000 * 4;

		private static readonly AccessTools.FieldRef<Pawn_StyleTracker, bool> DesiredField = MakeField();

		private static TraitDef beauty;

		private static bool beautyLooked;

		private static AccessTools.FieldRef<Pawn_StyleTracker, bool> MakeField()
		{
			try
			{
				return AccessTools.FieldRefAccess<Pawn_StyleTracker, bool>("lookChangeDesired");
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] Pawn_StyleTracker.lookChangeDesired not found: " + ex.Message);
				return null;
			}
		}

		public static void ResetCaches()
		{
			beauty = null;
			beautyLooked = false;
		}

		public static bool HasStation(Map map)
		{
			// 1.4.0: a barber's table (Vanilla Hair Expanded …) counts too
			return map != null && map.listerBuildings != null && Salon.AnyTable(map);
		}

		public static bool CanWish(Pawn p)
		{
			if (p == null || p.Dead || !p.Spawned || p.Map == null || p.RaceProps == null || !p.RaceProps.Humanlike)
			{
				return false;
			}
			if (!p.IsFreeNonSlaveColonist || p.IsPrisoner || p.IsQuestLodger() || p.Downed || p.InMentalState)
			{
				return false;
			}
			if (p.story == null || p.style == null || p.ageTracker == null || !p.DevelopmentalStage.Adult())
			{
				return false;
			}
			return p.style.CanDesireLookChange && !p.style.LookChangeDesired;
		}

		// average days between wishes for this pawn: setting, nudged by mood and beauty
		public static float MtbDays(Pawn p)
		{
			float days = SecularMod.Settings.lookChangeDays;
			if (p.needs != null && p.needs.mood != null)
			{
				float mood = p.needs.mood.CurLevelPercentage;
				if (mood >= 0.7f)
				{
					days *= 0.75f;
				}
				else if (mood < 0.3f)
				{
					days *= 1.5f;
				}
			}
			if (!beautyLooked)
			{
				beauty = DefDatabase<TraitDef>.GetNamedSilentFail("Beauty");
				beautyLooked = true;
			}
			if (beauty != null && p.story.traits != null && p.story.traits.HasTrait(beauty) && p.story.traits.DegreeOfTrait(beauty) > 0)
			{
				days *= 0.7f;
			}
			return Mathf.Max(1f, days);
		}

		// at least one hair, beard or tattoo the pawn could switch to
		public static bool HasAlternative(Pawn p)
		{
			if (Alt<HairDef>(p, p.story.hairDef, null) || Alt<TattooDef>(p, p.style.FaceTattoo, TattooType.Face) || Alt<TattooDef>(p, p.style.BodyTattoo, TattooType.Body))
			{
				return true;
			}
			return p.style.CanWantBeard && Alt<BeardDef>(p, p.style.beardDef, null);
		}

		private static bool Alt<T>(Pawn p, T current, TattooType? type) where T : StyleItemDef
		{
			List<T> all = DefDatabase<T>.AllDefsListForReading;
			for (int i = 0; i < all.Count; i++)
			{
				if (all[i] != current && PawnStyleItemChooser.WantsToUseStyle(p, all[i], type) && PawnStyleItemChooser.TotalStyleItemLikelihood(all[i], p) > 0f)
				{
					return true;
				}
			}
			return false;
		}

		public static void Tick(Dictionary<Pawn, int> wishes)
		{
			SecularSettings s = SecularMod.Settings;
			int now = Find.TickManager.TicksGame;
			ExpireWishes(wishes, now);
			if (!s.lookChange || DesiredField == null || !Secular.Active)
			{
				return;
			}
			List<Map> maps = Find.Maps;
			for (int m = 0; m < maps.Count; m++)
			{
				if (!HasStation(maps[m]))
				{
					continue;
				}
				List<Pawn> pawns = maps[m].mapPawns.FreeColonistsSpawned;
				for (int i = 0; i < pawns.Count; i++)
				{
					Pawn p = pawns[i];
					if (!CanWish(p) || !Rand.MTBEventOccurs(MtbDays(p), 60000f, CheckInterval) || !HasAlternative(p))
					{
						continue;
					}
					DesiredField(p.style) = true;
					wishes[p] = now;
					if (s.lookChangeMessage)
					{
						Messages.Message("SC_LookWish".Translate(p.Named("PAWN")), p, MessageTypeDefOf.NeutralEvent, false);
					}
				}
			}
		}

		private static readonly List<Pawn> tmpRemove = new List<Pawn>();

		private static void ExpireWishes(Dictionary<Pawn, int> wishes, int now)
		{
			if (wishes.Count == 0)
			{
				return;
			}
			tmpRemove.Clear();
			foreach (KeyValuePair<Pawn, int> kv in wishes)
			{
				Pawn p = kv.Key;
				if (p == null || p.Destroyed || p.style == null || DesiredField == null || !DesiredField(p.style))
				{
					// fulfilled (or gone)
					tmpRemove.Add(p);
				}
				else if (now - kv.Value > ExpireTicks || !SecularMod.Settings.lookChange || (p.Spawned && !HasStation(p.Map)))
				{
					// unfulfilled for too long, no station any more, or feature switched off
					DesiredField(p.style) = false;
					tmpRemove.Add(p);
				}
			}
			for (int i = 0; i < tmpRemove.Count; i++)
			{
				wishes.Remove(tmpRemove[i]);
			}
			tmpRemove.Clear();
		}
	}

	public class SecularGameComponent : GameComponent
	{
		private static bool cleanupRequested = true;

		private Dictionary<Pawn, int> lookWishes = new Dictionary<Pawn, int>();

		private List<Pawn> tmpWishPawns;

		private List<int> tmpWishTicks;

		public SecularGameComponent(Game game)
		{
			Secular.ResetCaches();
			LookWishes.ResetCaches();
			PolyTraits.ResetCaches();
			cleanupRequested = true;
		}

		public override void ExposeData()
		{
			base.ExposeData();
			if (Scribe.mode == LoadSaveMode.Saving)
			{
				lookWishes.RemoveAll(kv => kv.Key == null || kv.Key.Destroyed);
			}
			Scribe_Collections.Look(ref lookWishes, "lookWishes", LookMode.Reference, LookMode.Value, ref tmpWishPawns, ref tmpWishTicks);
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				if (lookWishes == null)
				{
					lookWishes = new Dictionary<Pawn, int>();
				}
				lookWishes.RemoveAll(kv => kv.Key == null);
			}
		}

		public static void RequestCleanup()
		{
			cleanupRequested = true;
		}

		public override void FinalizeInit()
		{
			base.FinalizeInit();
			cleanupRequested = true;
			NoBelief.ApplyUi();
		}

		public override void GameComponentTick()
		{
			if (Find.TickManager == null)
			{
				return;
			}
			if (Find.TickManager.TicksGame % LookWishes.CheckInterval == 1250)
			{
				try
				{
					LookWishes.Tick(lookWishes);
				}
				catch (Exception ex)
				{
					Log.Warning("[Secular Colony] look change check failed: " + ex.Message);
				}
			}
			if (!cleanupRequested && Find.TickManager.TicksGame % 2500 != 0)
			{
				return;
			}
			cleanupRequested = false;
			SecularSettings s = SecularMod.Settings;
			if (!Secular.Active || !s.moods)
			{
				return;
			}
			try
			{
				int total = 0;
				foreach (Pawn pawn in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
				{
					if (Secular.IsOurs(pawn))
					{
						total += Secular.CleanPawn(pawn);
					}
				}
				if (total > 0 && Prefs.DevMode)
				{
					Log.Message("[Secular Colony] removed " + total + " ideoligion memories.");
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] cleanup failed: " + ex.Message);
			}
		}
	}

	public static class Patches
	{
		private static Harmony harmony;

		private static int ok;

		private static int failed;

		public static void Apply(Harmony h)
		{
			harmony = h;
			// moods
			TryPatch(AccessTools.Method(typeof(MemoryThoughtHandler), "TryGainMemory", new Type[] { typeof(Thought_Memory), typeof(Pawn) }), "TryGainMemory_Prefix", null);
			TryPatch(AccessTools.Method(typeof(ThoughtUtility), "CanGetThought"), null, "CanGetThought_Postfix");
			// certainty and conversion
			TryPatch(AccessTools.PropertyGetter(typeof(Pawn_IdeoTracker), "CertaintyChangePerDay"), null, "CertaintyChange_Postfix");
			TryPatch(AccessTools.Method(typeof(Pawn_IdeoTracker), "IdeoConversionAttempt"), "Conversion_Prefix", null);
			TryPatch(AccessTools.Method(typeof(Pawn_IdeoTracker), "OffsetCertainty"), "OffsetCertainty_Prefix", null);
			TryPatch(AccessTools.Method(typeof(InteractionWorker_ConvertIdeoAttempt), "RandomSelectionWeight"), null, "ConvertWeight_Postfix");
			// willingness (no "ideoligion forbids")
			TryPatch(AccessTools.Method(typeof(Ideo), "MemberWillingToDo"), "Willing_Prefix", null);
			// mental breaks from precepts
			foreach (Type t in typeof(MentalBreakWorker).AllSubclassesNonAbstract())
			{
				MethodInfo m = t.GetMethod("BreakCanOccur", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
				if (m != null)
				{
					TryPatch(m, null, "BreakCanOccur_Postfix");
				}
			}
			TryPatch(AccessTools.Method(typeof(MentalBreakWorker), "BreakCanOccur"), null, "BreakCanOccur_Postfix");
			// 1.0.1: belief alerts (empty roles, ideoligion buildings, date rituals, age reversal demand)
			string[] alerts = new string[] { "Alert_RolesEmpty", "Alert_IdeoBuildingMissing", "Alert_IdeoBuildingDisrespected", "Alert_DateRitualComing", "Alert_AgeReversalDemandNear" };
			for (int i = 0; i < alerts.Length; i++)
			{
				Type t = AccessTools.TypeByName("RimWorld." + alerts[i]);
				TryPatch((t == null) ? null : AccessTools.Method(t, "GetReport"), null, "Alert_Postfix");
			}
			// 1.3.0: newcomers take the colony's belief-free ideoligion
			TryPatch(AccessTools.Method(typeof(Pawn), "SetFaction"), null, "SetFaction_Postfix");
			// 1.1.0: style choice without ideoligion
			TryPatch(AccessTools.Method(typeof(PawnStyleItemChooser), "WantsToUseStyle"), null, "WantsToUseStyle_Postfix");
			TryPatch(AccessTools.Method(typeof(PawnStyleItemChooser), "TotalStyleItemLikelihood"), null, "StyleLikelihood_Postfix");
			if (failed > 0)
			{
				Log.Warning("[Secular Colony] " + failed + " patch(es) failed, " + ok + " applied.");
			}
		}

		private static void TryPatch(MethodBase target, string prefix, string postfix)
		{
			if (target == null)
			{
				failed++;
				Log.Warning("[Secular Colony] patch target not found for " + (prefix ?? postfix));
				return;
			}
			try
			{
				harmony.Patch(target, (prefix == null) ? null : new HarmonyMethod(typeof(Patches), prefix), (postfix == null) ? null : new HarmonyMethod(typeof(Patches), postfix));
				ok++;
			}
			catch (Exception ex)
			{
				failed++;
				Log.Warning("[Secular Colony] patch " + target.DeclaringType.Name + "." + target.Name + " failed: " + ex.Message);
			}
		}

		public static bool TryGainMemory_Prefix(MemoryThoughtHandler __instance, Thought_Memory newThought)
		{
			try
			{
				if (newThought != null && __instance != null && Secular.On(__instance.pawn, SecularMod.Settings.moods) && Secular.IsIdeoMemory(newThought))
				{
					return false;
				}
				// 1.2.0: polyamorous colonists are not jealous
				if (newThought != null && __instance != null && newThought.def == ThoughtDefOf.CheatedOnMe && SecularMod.Settings.polyNoJealousy && PolyTraits.IsPolyamorous(__instance.pawn))
				{
					return false;
				}
			}
			catch
			{
			}
			return true;
		}

		public static void CanGetThought_Postfix(Pawn pawn, ThoughtDef def, ref bool __result)
		{
			if (__result && def != null && Secular.On(pawn, SecularMod.Settings.moods) && Secular.IsIdeoThought(def))
			{
				__result = false;
			}
		}

		public static void CertaintyChange_Postfix(Pawn_IdeoTracker __instance, ref float __result)
		{
			if (__result < 0f && __instance != null && Secular.On(PawnOf(__instance), SecularMod.Settings.certainty))
			{
				__result = 0f;
			}
		}

		public static bool Conversion_Prefix(Pawn_IdeoTracker __instance, ref bool __result)
		{
			if (__instance != null && Secular.On(PawnOf(__instance), SecularMod.Settings.certainty))
			{
				__result = false;
				return false;
			}
			return true;
		}

		public static bool OffsetCertainty_Prefix(Pawn_IdeoTracker __instance, float offset)
		{
			if (offset < 0f && __instance != null && Secular.On(PawnOf(__instance), SecularMod.Settings.certainty))
			{
				return false;
			}
			return true;
		}

		public static void ConvertWeight_Postfix(Pawn initiator, Pawn recipient, ref float __result)
		{
			if (__result > 0f && SecularMod.Settings.certainty && Secular.Active && (Secular.IsOurs(initiator) || Secular.IsOurs(recipient)))
			{
				__result = 0f;
			}
		}

		public static bool Willing_Prefix(HistoryEvent ev, ref bool __result)
		{
			try
			{
				if (NoBelief.Converting)
				{
					__result = true;
					return false;
				}
				Pawn pawn;
				if (ev.args.TryGetArg<Pawn>(HistoryEventArgsNames.Doer, out pawn) && Secular.On(pawn, SecularMod.Settings.willing))
				{
					// 1.2.0: how many partners – by the rule chosen in the settings, not "anything goes"
					int n = PolyTraits.PartnerCount(ev.def);
					__result = n <= 1 || PolyTraits.AllowsPartners(pawn, n);
					return false;
				}
			}
			catch
			{
			}
			return true;
		}

		public static void SetFaction_Postfix(Pawn __instance, Faction newFaction)
		{
			try
			{
				if (newFaction != null && newFaction.IsPlayer)
				{
					NoBelief.Adopt(__instance);
				}
			}
			catch
			{
			}
		}

		public static void BreakCanOccur_Postfix(MentalBreakWorker __instance, Pawn pawn, ref bool __result)
		{
			if (__result && __instance != null && Secular.On(pawn, SecularMod.Settings.mentalBreaks) && Secular.IsIdeoBreak(__instance.def))
			{
				__result = false;
			}
		}

		public static void Alert_Postfix(ref AlertReport __result)
		{
			if (__result.active && SecularMod.Settings.hideAlerts && Secular.Active)
			{
				__result = AlertReport.Inactive;
			}
		}

		private static bool StyleFree(Pawn pawn)
		{
			return pawn != null && pawn.Ideo != null && Find.IdeoManager != null && !Find.IdeoManager.classicMode && Secular.On(pawn, SecularMod.Settings.styleFree);
		}

		// Vanilla refuses styles the pawn's ideoligion rates "never". For our pawns
		// only the non-ideoligion checks of the original method count.
		public static void WantsToUseStyle_Postfix(Pawn pawn, StyleItemDef styleItemDef, TattooType? tattooType, ref bool __result)
		{
			if (__result || styleItemDef == null || !StyleFree(pawn))
			{
				return;
			}
			try
			{
				if (pawn.genes != null && ModsConfig.BiotechActive && !pawn.genes.StyleItemAllowed(styleItemDef))
				{
					return;
				}
				if (ModsConfig.AnomalyActive && pawn.IsMutant && !pawn.mutant.Def.StyleItemAllowed(styleItemDef))
				{
					return;
				}
				if (ModsConfig.AnomalyActive && pawn.IsCreepJoiner && !pawn.creepjoiner.form.StyleItemAllowed(styleItemDef))
				{
					return;
				}
				TattooDef tattoo = styleItemDef as TattooDef;
				if (tattoo != null)
				{
					if (tattooType.HasValue && tattoo.tattooType != tattooType.Value)
					{
						return;
					}
				}
				else if (styleItemDef is BeardDef)
				{
					if (pawn.style == null || !pawn.style.CanWantBeard)
					{
						return;
					}
				}
				else
				{
					HairDef hair = styleItemDef as HairDef;
					if (hair != null && !PawnStyleItemChooser.AgeAppropriateHairStyle(pawn, hair))
					{
						return;
					}
				}
				__result = true;
			}
			catch
			{
			}
		}

		// Weight without the ideoligion: the style's own gender (styleGender) and
		// the pawn kind's style tags, as if the colony had no culture forced on it.
		public static void StyleLikelihood_Postfix(StyleItemDef styleItem, Pawn pawn, ref float __result)
		{
			if (styleItem == null || !StyleFree(pawn))
			{
				return;
			}
			try
			{
				if (ModsConfig.AnomalyActive && styleItem.requiredMutant != null && (pawn.mutant == null || pawn.mutant.Def != styleItem.requiredMutant))
				{
					__result = 0f;
					return;
				}
				if (ModsConfig.BiotechActive && pawn.genes != null && styleItem.requiredGene != null && !pawn.genes.HasActiveGene(styleItem.requiredGene))
				{
					__result = 0f;
					return;
				}
				float identity = 1f;
				if (pawn.kindDef != null && !pawn.kindDef.styleItemTags.NullOrEmpty())
				{
					identity = PawnStyleItemChooser.StyleItemChoiceLikelihoodFromTags(styleItem, pawn.kindDef.styleItemTags);
				}
				__result = GenderWeight(styleItem.styleGender, pawn.gender) * identity;
			}
			catch
			{
			}
		}

		private static float GenderWeight(StyleGender g, Gender gender)
		{
			if (gender == Gender.Male)
			{
				switch (g)
				{
					case StyleGender.Female: return 1f;
					case StyleGender.FemaleUsually: return 5f;
					case StyleGender.MaleUsually: return 30f;
					case StyleGender.Male: return 70f;
					default: return 60f;
				}
			}
			if (gender == Gender.Female)
			{
				switch (g)
				{
					case StyleGender.Male: return 1f;
					case StyleGender.MaleUsually: return 5f;
					case StyleGender.FemaleUsually: return 30f;
					case StyleGender.Female: return 70f;
					default: return 60f;
				}
			}
			return 100f;
		}

		// 1.0.2: a compiled field accessor instead of FieldInfo.GetValue – this is
		// read from CertaintyChangePerDay, i.e. for every pawn on every ideo tick.
		private static readonly AccessTools.FieldRef<Pawn_IdeoTracker, Pawn> PawnField = MakePawnField();

		private static AccessTools.FieldRef<Pawn_IdeoTracker, Pawn> MakePawnField()
		{
			try
			{
				return AccessTools.FieldRefAccess<Pawn_IdeoTracker, Pawn>("pawn");
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] Pawn_IdeoTracker.pawn not found: " + ex.Message);
				return null;
			}
		}

		private static Pawn PawnOf(Pawn_IdeoTracker tracker)
		{
			return (PawnField == null || tracker == null) ? null : PawnField(tracker);
		}
	}

	// 1.2.0: polyamorous / polygamous traits. Their defNames are the ones RimJobWorld looks
	// for ("Some Polyamory Mod" / "Some Polygamy Mod"), so RJW treats these colonists as poly too.
	[StaticConstructorOnStartup]
	public static class PolyTraits
	{
		// looked up per game, not once: a language change reloads every Def as a new object
		// and old references would never match a pawn's trait again (same as 1.0.2)
		private static TraitDef amorous;
		private static TraitDef gamous;
		private static bool looked;

		private static float baseAmorous = -1f;
		private static float baseGamous = -1f;

		public static TraitDef Polyamorous
		{
			get
			{
				Look();
				return amorous;
			}
		}

		public static TraitDef Polygamous
		{
			get
			{
				Look();
				return gamous;
			}
		}

		static PolyTraits()
		{
			ApplyChance();
		}

		private static void Look()
		{
			if (looked)
			{
				return;
			}
			looked = true;
			amorous = DefDatabase<TraitDef>.GetNamedSilentFail("Polyamorous");
			gamous = DefDatabase<TraitDef>.GetNamedSilentFail("Polygamous");
		}

		public static void ResetCaches()
		{
			looked = false;
			baseAmorous = -1f;
			baseGamous = -1f;
			ApplyChance();
		}

		public static void ApplyChance()
		{
			try
			{
				FieldInfo f = AccessTools.Field(typeof(TraitDef), "commonality");
				if (f == null)
				{
					return;
				}
				float mult = (SecularMod.Settings == null) ? 1f : SecularMod.Settings.polyChance;
				if (Polyamorous != null)
				{
					if (baseAmorous < 0f)
					{
						baseAmorous = (float)f.GetValue(Polyamorous);
					}
					f.SetValue(Polyamorous, baseAmorous * mult);
				}
				if (Polygamous != null)
				{
					if (baseGamous < 0f)
					{
						baseGamous = (float)f.GetValue(Polygamous);
					}
					f.SetValue(Polygamous, baseGamous * mult);
				}
			}
			catch (Exception ex)
			{
				Log.Warning("[Secular Colony] poly trait chance: " + ex.Message);
			}
		}

		public static bool IsPolyamorous(Pawn p)
		{
			return p != null && p.story != null && p.story.traits != null && Polyamorous != null && p.story.traits.HasTrait(Polyamorous);
		}

		public static bool IsPolygamous(Pawn p)
		{
			return p != null && p.story != null && p.story.traits != null && Polygamous != null && p.story.traits.HasTrait(Polygamous);
		}

		// the vanilla "how many partners / spouses" events
		public static int PartnerCount(HistoryEventDef def)
		{
			if (def == null)
			{
				return 0;
			}
			if (def == HistoryEventDefOf.GotMarried_SpouseCount_Two)
			{
				return 2;
			}
			if (def == HistoryEventDefOf.GotMarried_SpouseCount_Three)
			{
				return 3;
			}
			if (def == HistoryEventDefOf.GotMarried_SpouseCount_Four)
			{
				return 4;
			}
			if (def == HistoryEventDefOf.GotMarried_SpouseCount_FiveOrMore)
			{
				return 5;
			}
			return 0;
		}

		public static bool AllowsPartners(Pawn p, int n)
		{
			SecularSettings s = SecularMod.Settings;
			switch (s.relationRule)
			{
			case 1:
				return false;
			case 2:
				return n <= s.maxPartners || n >= 5 && s.maxPartners >= 5;
			default:
				if (!IsPolyamorous(p) && !IsPolygamous(p))
				{
					return false;
				}
				return n <= s.maxPartners || n >= 5 && s.maxPartners >= 5;
			}
		}
	}

	// "a big family" – a polygamous colonist is happy with more spouses
	public class ThoughtWorker_BigFamily : ThoughtWorker
	{
		protected override ThoughtState CurrentStateInternal(Pawn p)
		{
			SecularSettings s = SecularMod.Settings;
			if (s == null || !s.polygamyMood || !PolyTraits.IsPolygamous(p))
			{
				return ThoughtState.Inactive;
			}
			int spouses = p.GetSpouseCount(false);
			if (spouses < 2)
			{
				return ThoughtState.Inactive;
			}
			return ThoughtState.ActiveAtStage(Mathf.Min(spouses - 2, 2));
		}
	}

	// 1.3.0: the ideoligion disappears from the colony. The save is switched to the game's
	// own classic mode (no certainty, no conversions, no ideoligion tab), every colonist gets
	// one plain classic ideoligion without roles, relics or ideoligion buildings, and anyone
	// who joins later takes it too. The rest of this mod keeps its moods and rules away.
	public static class NoBelief
	{
		public static bool Converting;

		public static bool On
		{
			get
			{
				return Secular.Active && Find.IdeoManager != null && Find.IdeoManager.classicMode;
			}
		}

		public static void ApplyUi()
		{
			try
			{
				if (MainButtonDefOf.Ideos != null)
				{
					MainButtonDefOf.Ideos.buttonVisible = !On;
				}
			}
			catch
			{
			}
		}

		public static void Convert()
		{
			Faction pf = Faction.OfPlayer;
			if (pf == null || pf.ideos == null || Find.IdeoManager == null)
			{
				return;
			}
			Converting = true;
			int n = 0;
			try
			{
				Ideo old = pf.ideos.PrimaryIdeo;
				CultureDef culture = (old != null && old.culture != null) ? old.culture : DefDatabase<CultureDef>.AllDefs.RandomElement();
				Ideo ideo = IdeoGenerator.GenerateClassicIdeo(culture, new IdeoGenerationParms(pf.def), false);
				List<Precept> precepts = ideo.PreceptsListForReading.ToList();
				for (int i = 0; i < precepts.Count; i++)
				{
					if (precepts[i] is Precept_Role || precepts[i] is Precept_Relic || precepts[i] is Precept_Building)
					{
						ideo.RemovePrecept(precepts[i]);
					}
				}
				if (old != null)
				{
					ideo.primaryFactionColor = old.primaryFactionColor;
				}
				Find.IdeoManager.Add(ideo);
				Find.IdeoManager.classicMode = true;
				pf.ideos.SetPrimary(ideo);
				foreach (Pawn p in AllOurs().ToList())
				{
					if (p.ideo != null && p.Ideo != ideo)
					{
						p.ideo.SetIdeo(ideo);
						p.ideo.OffsetCertainty(1f);
						n++;
					}
				}
				pf.ideos.RecalculateIdeosBasedOnPlayerPawns();
				pf.ideos.SetPrimary(ideo);
			}
			catch (Exception ex)
			{
				Log.Error("[Secular Colony] removing the ideoligion failed: " + ex);
			}
			finally
			{
				Converting = false;
			}
			ApplyUi();
			SecularGameComponent.RequestCleanup();
			Messages.Message("SC_NoBelief_Converted".Translate(n), MessageTypeDefOf.PositiveEvent, false);
		}

		public static void Undo()
		{
			if (Find.IdeoManager == null)
			{
				return;
			}
			Find.IdeoManager.classicMode = false;
			ApplyUi();
			Messages.Message("SC_NoBelief_Undone".Translate(), MessageTypeDefOf.NeutralEvent, false);
		}

		// someone joins the colony: same belief-free ideoligion as everyone else
		public static void Adopt(Pawn p)
		{
			if (!On || p == null || p.ideo == null || p.RaceProps == null || !p.RaceProps.Humanlike)
			{
				return;
			}
			Ideo primary = Faction.OfPlayer.ideos.PrimaryIdeo;
			if (primary == null || p.Ideo == primary)
			{
				return;
			}
			Converting = true;
			try
			{
				p.ideo.SetIdeo(primary);
				p.ideo.OffsetCertainty(1f);
			}
			finally
			{
				Converting = false;
			}
		}

		private static IEnumerable<Pawn> AllOurs()
		{
			HashSet<Pawn> seen = new HashSet<Pawn>();
			foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_OfPlayerFaction)
			{
				if (p != null && p.RaceProps != null && p.RaceProps.Humanlike && seen.Add(p))
				{
					yield return p;
				}
			}
			if (Find.WorldPawns != null)
			{
				foreach (Pawn p in Find.WorldPawns.AllPawnsAlive)
				{
					if (p != null && p.Faction == Faction.OfPlayer && p.RaceProps != null && p.RaceProps.Humanlike && seen.Add(p))
					{
						yield return p;
					}
				}
			}
		}
	}
}
