using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GenderBalance;

// ---------------------------------------------------------------------------
// Gender Balance: chance that a newly generated person is a man.
//   * people who join your colony (wanderers, starting colonists, anyone
//     generated for your faction)
//   * everyone else (visitors, traders, raiders, refugees, slaves for sale …)
//     – optional, off by default = the game's own 50 %.
// Never changes: a gender that a quest, a relative, a pawn kind or a race
// asks for, races with one gender only (also Humanoid Alien Races with
// maleGenderProbability 0 or 1), animals and mechanoids.
// The game picks the gender right before it rolls the age; the patch sits
// exactly there, so everything after it (body, backstory, name, traits,
// relations) already fits the new gender.
// ---------------------------------------------------------------------------
public class GenderSettings : ModSettings
{
	public float maleJoiners = 0.5f;
	public bool othersOn;
	public float maleOthers = 0.5f;
	// 1.3.0: chosen profile. 0 Game (50/50), 1 More women, 2 More men, 3 Custom.
	// Missing key = Custom, so settings from older versions stay as they were.
	public int profile = 0;
	// 1.1.0: Humanoid Alien Races – per-race chance of a man, by race defName.
	// A race that is not in here keeps its own maleGenderProbability.
	public Dictionary<string, float> raceMale = new Dictionary<string, float>();

	public static readonly string[] ProfileFields = { "maleJoiners", "othersOn", "maleOthers" };

	public static GenderSettings PresetValues(int which)
	{
		var p = new GenderSettings();
		if (which == 1)
		{
			p.maleJoiners = 0.25f;
			p.othersOn = true;
			p.maleOthers = 0.25f;
		}
		else if (which == 2)
		{
			p.maleJoiners = 0.75f;
			p.othersOn = true;
			p.maleOthers = 0.75f;
		}
		return p;
	}

	public void Preset(int which)
	{
		GenderSettings p = PresetValues(which);
		maleJoiners = p.maleJoiners;
		othersOn = p.othersOn;
		maleOthers = p.maleOthers;
	}

	public bool Matches(int which)
	{
		GenderSettings p = PresetValues(which);
		return Mathf.Approximately(maleJoiners, p.maleJoiners) && othersOn == p.othersOn
			&& (!othersOn || Mathf.Approximately(maleOthers, p.maleOthers));
	}

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref maleJoiners, "maleJoiners", 0.5f);
		Scribe_Values.Look(ref othersOn, "othersOn", false);
		Scribe_Values.Look(ref maleOthers, "maleOthers", 0.5f);
		Scribe_Values.Look(ref profile, "profile", 3);
		Scribe_Collections.Look(ref raceMale, "raceMale", LookMode.Value, LookMode.Value);
		if (raceMale == null)
			raceMale = new Dictionary<string, float>();
		if (Scribe.mode == LoadSaveMode.LoadingVars && profile >= 0 && profile <= 2)
			Preset(profile);
	}

	public bool TryGetRaceOverride(ThingDef race, out float male)
	{
		male = 0.5f;
		return race != null && raceMale != null && raceMale.TryGetValue(race.defName, out male);
	}
}

public class GenderBalanceMod : Mod
{
	public const string Version = "1.3.1";
	public static GenderSettings Settings;
	private Vector2 scroll;
	private float viewHeight = 600f;
	private static readonly string[] ProfKeys = { "GB_ProfGame", "GB_ProfWomen", "GB_ProfMen", "GB_ProfCustom" };

	public GenderBalanceMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<GenderSettings>();
		try
		{
			var harmony = new Harmony("bolek98.genderbalance");
			MUI.Init(GenderSettings.PresetValues(0), GenderSettings.ProfileFields);
			MUI.PatchWindow(harmony, typeof(GenderBalanceMod));
			MethodInfo target = AccessTools.Method(typeof(PawnGenerator), "GenerateRandomAge");
			if (target == null)
			{
				Log.Error("[Gender Balance] PawnGenerator.GenerateRandomAge not found – mod inactive.");
				return;
			}
			// 1.1.0: run after other prefixes. Humanoid Alien Races patches the same
			// method from a static constructor (after this Mod class is created) and
			// rolls the gender again from maleGenderProbability; with equal priority
			// its prefix ran after ours and undid our choice for alien races.
			var prefix = new HarmonyMethod(typeof(GenderPatch), nameof(GenderPatch.Prefix)) { priority = Priority.Last };
			harmony.Patch(target, prefix: prefix);
			// 1.2.0: the game often re-uses an old world pawn instead of making a new
			// person (up to 80 % of the time in a long game) – those keep the gender
			// they were made with. The choice among them now follows the sliders too.
			MethodInfo redress = AccessTools.Method(typeof(PawnGenerator), "IsValidCandidateToRedress");
			if (redress != null)
				harmony.Patch(redress, postfix: new HarmonyMethod(typeof(GenderPatch), nameof(GenderPatch.RedressPostfix)));
			Log.Message("[Gender Balance] v" + Version + " loaded.");
		}
		catch (Exception ex)
		{
			Log.Error("[Gender Balance] patch failed: " + ex);
		}
	}

	public override string SettingsCategory() => "Gender Balance";

	private static void Muted(Rect r, string text, GameFont font = GameFont.Tiny)
	{
		GUI.color = MUI.Muted;
		Text.Font = font;
		Widgets.Label(r, text);
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		GenderSettings s = Settings;
		MUI.Version(inRect, "v" + Version + " · Bolek98");
		// 1.3.0: profile cards on top
		Muted(new Rect(inRect.x, inRect.y, inRect.width - 160f, 20f), "GB_Profile".Translate());
		float cw = (inRect.width - 36f) / 4f;
		for (int i = 0; i < 4; i++)
		{
			Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 78f);
			if (MUI.ProfileCard(c, ProfKeys[i].Translate(), (ProfKeys[i] + "_Card").Translate(), s.profile == i, false))
			{
				if (i < 3)
					s.Preset(i);
				s.profile = i;
			}
		}
		float y = inRect.y + 106f;
		Muted(new Rect(inRect.x, y, inRect.width, 20f), (s.profile == 3 ? "GB_ProfileHintCustom" : "GB_ProfileHint").Translate());
		y += 22f;
		string colony = ColonyLine();
		if (colony != null)
		{
			Muted(new Rect(inRect.x, y, inRect.width, 22f), colony, GameFont.Small);
			y += 24f;
		}
		Rect outRect = new Rect(inRect.x, y + 4f, inRect.width, inRect.yMax - y - 4f);
		Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(viewHeight, outRect.height));
		Widgets.BeginScrollView(outRect, ref scroll, viewRect);
		var l = new Listing_Standard { maxOneColumn = true };
		l.Begin(new Rect(0f, 0f, viewRect.width, 100000f));
		l.ColumnWidth = Mathf.Min(viewRect.width, 640f);

		bool locked = s.profile != 3;
		float lockTop = l.CurHeight;
		MUI.BeginLock(locked);
		try
		{
			MUI.Section(l, "GB_SecColony".Translate());
			s.maleJoiners = Round(MUI.Slider(l, "GB_Joiners".Translate(Pct(s.maleJoiners), Pct(1f - s.maleJoiners)), s.maleJoiners, 0f, 1f, "GB_JoinersTip".Translate(), s, "maleJoiners"));
			MUI.Section(l, "GB_SecOthers".Translate());
			MUI.Check(l, "GB_OthersOn".Translate(), "GB_OthersTip".Translate(), ref s.othersOn, s, "othersOn");
			if (s.othersOn)
			{
				s.maleOthers = Round(MUI.Slider(l, "GB_Others".Translate(Pct(s.maleOthers), Pct(1f - s.maleOthers)), s.maleOthers, 0f, 1f, "GB_OthersTip".Translate(), s, "maleOthers"));
				if (Mathf.Approximately(s.maleOthers, 0.5f))
				{
					GUI.color = MUI.Warn;
					l.Label("GB_OthersHalf".Translate());
					GUI.color = Color.white;
				}
			}
		}
		finally
		{
			MUI.EndLock(new Rect(0f, lockTop, l.ColumnWidth, l.CurHeight - lockTop));
		}
		if (s.profile >= 0 && s.profile <= 2 && !s.Matches(s.profile))
			s.profile = 3;
		l.Gap(8f);
		GUI.color = MUI.Muted;
		l.Label("GB_Note".Translate());
		GUI.color = Color.white;
		DoRaceList(l);
		viewHeight = l.CurHeight + 20f;
		l.End();
		Widgets.EndScrollView();
	}

	// 1.3.0: how the colony looks right now (only in a running game)
	private static string ColonyLine()
	{
		if (Current.ProgramState != ProgramState.Playing || Find.Maps == null)
			return null;
		int men = 0, women = 0;
		foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
		{
			if (p.gender == Gender.Male) men++;
			else if (p.gender == Gender.Female) women++;
		}
		if (men + women == 0)
			return null;
		return "GB_ColonyNow".Translate(men, women);
	}

	// 1.1.0: one slider per Humanoid Alien Races race. Default = the race's own
	// maleGenderProbability (no override); "Reset" removes the override.
	private static void DoRaceList(Listing_Standard l)
	{
		List<ThingDef> races = AlienRaces.All();
		if (races.Count == 0)
			return;
		MUI.Section(l, "GB_Races".Translate());
		GUI.color = MUI.Muted;
		l.Label("GB_Races_Desc".Translate());
		GUI.color = Color.white;
		l.Gap(6f);
		foreach (ThingDef race in races)
		{
			float own = AlienRaces.OwnMaleProbability(race);
			bool set = Settings.TryGetRaceOverride(race, out float male);
			if (!set)
				male = own;
			string label = "GB_Race".Translate(race.LabelCap, Pct(male), Pct(1f - male));
			string ownText = "GB_RaceOwn".Translate(Pct(own));
			Rect row = l.GetRect(Text.LineHeight);
			Rect resetRect = new Rect(row.xMax - 110f, row.y, 110f, row.height);
			Rect labelRect = new Rect(row.x, row.y, row.width - 118f, row.height);
			GUI.color = set ? MUI.Text1 : MUI.Muted;
			Widgets.Label(labelRect, label + "  " + ownText);
			GUI.color = Color.white;
			TooltipHandler.TipRegion(labelRect, (TipSignal)"GB_Race_Desc".Translate(race.defName, Pct(own)).ToString());
			if (set && Widgets.ButtonText(resetRect, "GB_RaceReset".Translate()))
			{
				Settings.raceMale.Remove(race.defName);
				male = own;
				set = false;
			}
			if (set)
				TooltipHandler.TipRegion(resetRect, (TipSignal)"GB_RaceReset_Desc".Translate(Pct(own)).ToString());
			float slid = Round(l.Slider(male, 0f, 1f));
			if (!Mathf.Approximately(slid, male))
				Settings.raceMale[race.defName] = slid;
			l.Gap(4f);
		}
	}

	private static string Pct(float v) => Mathf.RoundToInt(v * 100f) + " %";

	private static float Round(float v) => Mathf.Round(v * 20f) / 20f;   // steps of 5 %
}

public static class GenderPatch
{
	private static readonly Dictionary<ThingDef, bool> twoGenders = new Dictionary<ThingDef, bool>();

	public static void Prefix(Pawn pawn, PawnGenerationRequest request)
	{
		try
		{
			GenderSettings s = GenderBalanceMod.Settings;
			if (s == null || pawn == null || !pawn.RaceProps.Humanlike || !pawn.RaceProps.hasGenders)
				return;
			if (request.FixedGender.HasValue || (request.KindDef != null && request.KindDef.fixedGender.HasValue))
				return;
			if (pawn.RaceProps.forceGender != Gender.None)
				return;
			// 1.1.0: a per-race value set in the settings replaces the race's own
			// maleGenderProbability and wins over the two general sliders.
			if (!ChanceFor(pawn.def, request, out float male))
				return;
			pawn.gender = Rand.Chance(male) ? Gender.Male : Gender.Female;
			// 1.2.0: Humanoid Alien Races rolls the gender again after the pawn
			// appears for races with only one gender – our choice must stay
			HarFix.Keep(pawn);
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Gender Balance] " + ex.Message, 0x6b0b98);
		}
	}

	// the chance of a man this mod wants for a new person, false = leave it to the game
	public static bool ChanceFor(ThingDef race, PawnGenerationRequest request, out float male)
	{
		male = 0.5f;
		GenderSettings s = GenderBalanceMod.Settings;
		if (s == null || race == null)
			return false;
		if (s.TryGetRaceOverride(race, out male))
			return true;
		if (!TwoGenders(race))
			return false;
		bool joiner = request.Faction != null && request.Faction.IsPlayer;
		if (joiner)
			male = s.maleJoiners;
		else if (s.othersOn)
			male = s.maleOthers;
		else
			return false;
		return !Mathf.Approximately(male, 0.5f);
	}

	// 1.2.0: an old world pawn may be re-used only with the chance its gender has:
	// with 80 % men, a woman is accepted only 1 in 4 times, a man always.
	public static void RedressPostfix(Pawn pawn, PawnGenerationRequest request, ref bool __result)
	{
		try
		{
			if (!__result || pawn == null || !pawn.RaceProps.Humanlike || !pawn.RaceProps.hasGenders || pawn.gender == Gender.None)
				return;
			if (request.FixedGender.HasValue || (request.KindDef != null && request.KindDef.fixedGender.HasValue))
				return;
			if (!ChanceFor(pawn.def, request, out float male))
				return;
			float top = Mathf.Max(male, 1f - male);
			if (top <= 0f)
				return;
			float keep = (pawn.gender == Gender.Male ? male : 1f - male) / top;
			if (keep < 1f && !Rand.Chance(keep))
				__result = false;
		}
		catch
		{
		}
	}

	// Humanoid Alien Races: alienRace.generalSettings.maleGenderProbability.
	// 0 or 1 means a one-gender race – never touched.
	private static bool TwoGenders(ThingDef race)
	{
		if (race == null)
			return false;
		if (twoGenders.TryGetValue(race, out bool v))
			return v;
		float? p = AlienRaces.ReadMaleProbability(race);
		v = !p.HasValue || (p.Value > 0.01f && p.Value < 0.99f);
		twoGenders[race] = v;
		return v;
	}
}

// 1.1.0: Humanoid Alien Races, read by reflection (no hard dependency).
public static class AlienRaces
{
	private static List<ThingDef> all;
	private static readonly Dictionary<ThingDef, float> own = new Dictionary<ThingDef, float>();

	// alienRace.generalSettings.maleGenderProbability, or null for a non-HAR race.
	public static float? ReadMaleProbability(ThingDef race)
	{
		if (race == null)
			return null;
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object general = alien?.GetType().GetField("generalSettings")?.GetValue(alien);
			FieldInfo f = general?.GetType().GetField("maleGenderProbability");
			if (f != null)
				return Convert.ToSingle(f.GetValue(general));
		}
		catch
		{
		}
		return null;
	}

	public static float OwnMaleProbability(ThingDef race)
	{
		if (!own.TryGetValue(race, out float p))
		{
			p = Mathf.Clamp01(ReadMaleProbability(race) ?? 0.5f);
			own[race] = p;
		}
		return p;
	}

	// Humanlike HAR races that have genders, sorted by name.
	public static List<ThingDef> All()
	{
		// Defs are recreated when the game language changes: rebuild then.
		if (all != null && all.Count > 0 && DefDatabase<ThingDef>.GetNamedSilentFail(all[0].defName) != all[0])
		{
			all = null;
			own.Clear();
		}
		if (all == null)
		{
			all = new List<ThingDef>();
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (d.race == null || !d.race.Humanlike || !d.race.hasGenders || d.race.forceGender != Gender.None)
					continue;
				if (d.GetType().GetField("alienRace") == null || !ReadMaleProbability(d).HasValue)
					continue;
				all.Add(d);
			}
			all.SortBy(d => d.label ?? d.defName);
		}
		return all;
	}
}

// 1.2.0: Humanoid Alien Races sets AlienComp.fixGenderPostSpawn for races whose
// own chance is 0 % or 100 % and later rolls the gender again from that value.
public static class HarFix
{
	private static bool init;
	private static Type compType;
	private static FieldInfo flag;

	public static void Keep(Pawn pawn)
	{
		try
		{
			if (!init)
			{
				init = true;
				compType = AccessTools.TypeByName("AlienRace.AlienPartGenerator+AlienComp");
				if (compType != null)
					flag = AccessTools.Field(compType, "fixGenderPostSpawn");
			}
			if (flag == null || pawn.AllComps == null)
				return;
			foreach (ThingComp c in pawn.AllComps)
			{
				if (c != null && compType.IsInstanceOfType(c))
				{
					flag.SetValue(c, false);
					return;
				}
			}
		}
		catch
		{
		}
	}
}
