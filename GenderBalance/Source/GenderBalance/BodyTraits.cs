using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace GenderBalance;

// ---------------------------------------------------------------------------
// 1.3.4: optional body-type filter per race and gender, and forced / blocked
// traits per race, for every newly generated person of that race (colonists,
// visitors, raiders …). Nothing is blocked or forced by default.
//   * body type: a blocked adult body type is replaced by a random allowed one
//     (children and babies, a body type the request forces, and gene body types
//     – applied later by the genes themselves – are left alone)
//   * blocked traits are removed (unless forced by the scenario, the pawn kind
//     or a gene) and replaced by another random trait when one can be found
//   * forced traits are added (an unforced conflicting trait is removed first)
// Settings are stored as plain strings so they survive missing mods:
//   bodyBlocked["Human|Female"] = "Fat,Hulk"
//   traitBlocked["Human"] = "Pyromaniac:0,Beauty:-2"  (degree * = any degree)
//   traitForced["Human"]  = "Tough:0"
// ---------------------------------------------------------------------------
public static class BodyTraits
{
	public const string AnyDegree = "*";

	public static void Apply(Harmony harmony)
	{
		try
		{
			MethodInfo body = AccessTools.Method(typeof(PawnGenerator), "GenerateBodyType");
			if (body != null)
				harmony.Patch(body, postfix: new HarmonyMethod(typeof(BodyTraits), nameof(BodyPostfix)) { priority = Priority.Last });
			else
				Log.Warning("[Gender Balance] PawnGenerator.GenerateBodyType not found – body type filter inactive.");
			MethodInfo traits = AccessTools.Method(typeof(PawnGenerator), "GenerateTraits");
			if (traits != null)
				harmony.Patch(traits, postfix: new HarmonyMethod(typeof(BodyTraits), nameof(TraitsPostfix)) { priority = Priority.Last });
			else
				Log.Warning("[Gender Balance] PawnGenerator.GenerateTraits not found – trait rules inactive.");
		}
		catch (Exception ex)
		{
			Log.Warning("[Gender Balance] body type / trait rules inactive: " + ex.Message);
		}
	}

	// ---------- settings helpers ----------
	public static string BodyKey(ThingDef race, Gender g) => race.defName + "|" + g;

	public static List<string> Split(string s)
	{
		var l = new List<string>();
		if (string.IsNullOrEmpty(s))
			return l;
		foreach (string p in s.Split(','))
			if (p.Trim().Length > 0)
				l.Add(p.Trim());
		return l;
	}

	public static void Toggle(Dictionary<string, string> d, string key, string item)
	{
		List<string> l = d.TryGetValue(key, out string v) ? Split(v) : new List<string>();
		if (!l.Remove(item))
			l.Add(item);
		if (l.Count == 0)
			d.Remove(key);
		else
			d[key] = string.Join(",", l.ToArray());
	}

	public static bool Contains(Dictionary<string, string> d, string key, string item)
	{
		return d != null && d.TryGetValue(key, out string v) && Split(v).Contains(item);
	}

	public static string TraitItem(TraitDef def, int degree) => def.defName + ":" + degree;

	// "defName:degree" -> def, degree (null degree = any)
	public static bool ParseTrait(string item, out TraitDef def, out int? degree)
	{
		def = null;
		degree = null;
		int i = item.LastIndexOf(':');
		string name = i < 0 ? item : item.Substring(0, i);
		def = DefDatabase<TraitDef>.GetNamedSilentFail(name);
		if (def == null)
			return false;
		if (i >= 0 && item.Substring(i + 1) != AnyDegree && int.TryParse(item.Substring(i + 1), out int d))
			degree = d;
		return true;
	}

	public static string TraitLabel(string item)
	{
		if (!ParseTrait(item, out TraitDef def, out int? degree))
			return item;
		if (degree.HasValue && def.degreeDatas != null && def.degreeDatas.Count > 0)
		{
			TraitDegreeData data = def.degreeDatas.FirstOrDefault(x => x.degree == degree.Value);
			if (data != null)
				return data.LabelCap;
		}
		return def.LabelCap;
	}

	// adult body types a race can have (HAR: its own list), for one gender
	public static List<BodyTypeDef> BodyTypesFor(ThingDef race, Gender g)
	{
		var list = new List<BodyTypeDef>();
		List<BodyTypeDef> own = HarBodyTypes(race);
		IEnumerable<BodyTypeDef> src = own != null && own.Count > 0 ? own
			: new[] { BodyTypeDefOf.Male, BodyTypeDefOf.Female, BodyTypeDefOf.Thin, BodyTypeDefOf.Fat, BodyTypeDefOf.Hulk };
		foreach (BodyTypeDef b in src)
		{
			if (b == null || b == BodyTypeDefOf.Child || b == BodyTypeDefOf.Baby || list.Contains(b))
				continue;
			if (g == Gender.Male && b == BodyTypeDefOf.Female)
				continue;
			if (g == Gender.Female && b == BodyTypeDefOf.Male)
				continue;
			list.Add(b);
		}
		return list;
	}

	private static readonly Dictionary<ThingDef, List<BodyTypeDef>> harBodies = new Dictionary<ThingDef, List<BodyTypeDef>>();

	// Humanoid Alien Races: alienRace.generalSettings.alienPartGenerator.bodyTypes (by reflection)
	private static List<BodyTypeDef> HarBodyTypes(ThingDef race)
	{
		if (race == null)
			return null;
		if (harBodies.TryGetValue(race, out List<BodyTypeDef> l))
			return l;
		l = null;
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object general = alien?.GetType().GetField("generalSettings")?.GetValue(alien);
			object parts = general?.GetType().GetField("alienPartGenerator")?.GetValue(general);
			object bodies = parts?.GetType().GetField("bodyTypes")?.GetValue(parts);
			if (bodies is IEnumerable<BodyTypeDef> e)
				l = e.Where(b => b != null).ToList();
		}
		catch
		{
		}
		harBodies[race] = l;
		return l;
	}

	// ---------- patches ----------
	public static void BodyPostfix(Pawn pawn, PawnGenerationRequest request)
	{
		try
		{
			GenderSettings s = GenderBalanceMod.Settings;
			if (s == null || s.bodyBlocked.Count == 0 || pawn?.story?.bodyType == null || !pawn.RaceProps.Humanlike)
				return;
			if (request.ForceBodyType != null)
				return;
			BodyTypeDef cur = pawn.story.bodyType;
			if (cur == BodyTypeDefOf.Child || cur == BodyTypeDefOf.Baby)
				return;
			string key = BodyKey(pawn.def, pawn.gender);
			if (!Contains(s.bodyBlocked, key, cur.defName))
				return;
			List<BodyTypeDef> ok = BodyTypesFor(pawn.def, pawn.gender).Where(b => !Contains(s.bodyBlocked, key, b.defName)).ToList();
			if (ok.TryRandomElement(out BodyTypeDef pick))
				pawn.story.bodyType = pick;
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Gender Balance] body type filter: " + ex.Message, 0x6b0b99);
		}
	}

	public static void TraitsPostfix(Pawn pawn, PawnGenerationRequest request)
	{
		try
		{
			GenderSettings s = GenderBalanceMod.Settings;
			if (s == null || pawn?.story?.traits == null || !pawn.RaceProps.Humanlike)
				return;
			if (request.AllowedDevelopmentalStages.Newborn())
				return;
			string race = pawn.def.defName;
			bool hasBlocked = s.traitBlocked.TryGetValue(race, out string blockedStr) && !string.IsNullOrEmpty(blockedStr);
			bool hasForced = s.traitForced.TryGetValue(race, out string forcedStr) && !string.IsNullOrEmpty(forcedStr);
			if (!hasBlocked && !hasForced)
				return;
			List<string> blocked = Split(blockedStr);
			TraitSet set = pawn.story.traits;
			if (hasBlocked)
			{
				int removed = 0;
				foreach (Trait t in set.allTraits.ToList())
					if (IsBlocked(blocked, t.def, t.Degree) && !t.ScenForced && t.sourceGene == null)
					{
						set.RemoveTrait(t);
						removed++;
					}
				// keep the number of traits: another random trait that is not blocked
				for (int i = 0; i < removed; i++)
					for (int tries = 0; tries < 6; tries++)
					{
						Trait n = PawnGenerator.GenerateTraitsFor(pawn, 1, request).FirstOrFallback();
						if (n == null)
							break;
						if (IsBlocked(blocked, n.def, n.Degree) || set.HasTrait(n.def))
							continue;
						set.GainTrait(n);
						break;
					}
			}
			if (hasForced)
				foreach (string item in Split(forcedStr))
				{
					if (!ParseTrait(item, out TraitDef def, out int? degree) || set.HasTrait(def))
						continue;
					int deg = degree ?? (def.degreeDatas != null && def.degreeDatas.Count > 0 ? def.degreeDatas[0].degree : 0);
					if (set.allTraits.Any(t => t.ScenForced && (t.def == def || def.ConflictsWith(t))))
						continue;
					foreach (Trait t in set.allTraits.Where(t => def.ConflictsWith(t) && t.sourceGene == null).ToList())
						set.RemoveTrait(t);
					set.GainTrait(new Trait(def, deg));
				}
		}
		catch (Exception ex)
		{
			Log.WarningOnce("[Gender Balance] trait rules: " + ex.Message, 0x6b0b9a);
		}
	}

	private static bool IsBlocked(List<string> blocked, TraitDef def, int degree)
	{
		return blocked.Contains(def.defName + ":" + degree) || blocked.Contains(def.defName + ":" + AnyDegree) || blocked.Contains(def.defName);
	}

	// ---------- settings UI ----------
	private static readonly HashSet<string> open = new HashSet<string>();
	private static List<ThingDef> races;

	public static List<ThingDef> HumanlikeRaces()
	{
		if (races == null)
			races = DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d.race != null && d.race.Humanlike && d.race.intelligence == Intelligence.Humanlike)
				.OrderBy(d => d == ThingDefOf.Human ? 0 : 1).ThenBy(d => d.label ?? d.defName)
				.ToList();
		return races;
	}

	public static void DoSection(Listing_Standard l, GenderSettings s)
	{
		MUI.Section(l, "GB_BodyTraits".Translate());
		GUI.color = MUI.Muted;
		l.Label("GB_BodyTraits_Desc".Translate());
		GUI.color = Color.white;
		l.Gap(4f);
		foreach (ThingDef race in HumanlikeRaces())
		{
			bool isOpen = open.Contains(race.defName);
			int rules = Count(s, race);
			Rect head = l.GetRect(26f);
			Widgets.DrawLightHighlight(head);
			string text = (isOpen ? "− " : "+ ") + race.LabelCap + (rules > 0 ? "  –  " + "GB_RulesCount".Translate(rules).ToString() : "");
			GUI.color = rules > 0 ? MUI.Text1 : Color.white;
			Widgets.Label(new Rect(head.x + 6f, head.y + 3f, head.width - 6f, 24f), text);
			GUI.color = Color.white;
			if (Widgets.ButtonInvisible(head))
			{
				if (isOpen)
					open.Remove(race.defName);
				else
					open.Add(race.defName);
			}
			if (open.Contains(race.defName))
				DoRace(l, s, race);
			l.Gap(2f);
		}
	}

	private static int Count(GenderSettings s, ThingDef race)
	{
		int n = 0;
		foreach (Gender g in new[] { Gender.Male, Gender.Female, Gender.None })
			if (s.bodyBlocked.TryGetValue(BodyKey(race, g), out string v))
				n += Split(v).Count;
		if (s.traitBlocked.TryGetValue(race.defName, out string b))
			n += Split(b).Count;
		if (s.traitForced.TryGetValue(race.defName, out string f))
			n += Split(f).Count;
		return n;
	}

	private static void DoRace(Listing_Standard l, GenderSettings s, ThingDef race)
	{
		Gender[] genders = race.race.hasGenders ? new[] { Gender.Male, Gender.Female } : new[] { Gender.None };
		foreach (Gender g in genders)
		{
			string key = BodyKey(race, g);
			string who = (g == Gender.Male ? "GB_BodyMen" : g == Gender.Female ? "GB_BodyWomen" : "GB_BodyAll").Translate();
			Rect row = l.GetRect(28f);
			Widgets.Label(new Rect(row.x + 16f, row.y + 4f, 170f, 24f), who);
			float x = row.x + 190f;
			foreach (BodyTypeDef b in BodyTypesFor(race, g))
			{
				bool blocked = Contains(s.bodyBlocked, key, b.defName);
				string lbl = b.defName;
				float w = Mathf.Max(70f, Text.CalcSize(lbl).x + 30f);
				if (x + w > row.xMax)
					break;
				GUI.color = blocked ? new Color(1f, 0.45f, 0.4f) : Color.white;
				if (Widgets.ButtonText(new Rect(x, row.y, w, 26f), (blocked ? "× " : "") + lbl))
					Toggle(s.bodyBlocked, key, b.defName);
				GUI.color = Color.white;
				TooltipHandler.TipRegion(new Rect(x, row.y, w, 26f), (TipSignal)(blocked ? "GB_BodyBlockedTip" : "GB_BodyAllowedTip").Translate().ToString());
				x += w + 4f;
			}
		}
		TraitRow(l, s.traitForced, race, "GB_TraitsForced".Translate(), "GB_TraitsForcedTip".Translate(), false);
		TraitRow(l, s.traitBlocked, race, "GB_TraitsBlocked".Translate(), "GB_TraitsBlockedTip".Translate(), true);
		l.Gap(6f);
	}

	private static void TraitRow(Listing_Standard l, Dictionary<string, string> d, ThingDef race, string title, string tip, bool allowAny)
	{
		List<string> items = d.TryGetValue(race.defName, out string v) ? Split(v) : new List<string>();
		Rect row = l.GetRect(28f);
		Rect t = new Rect(row.x + 16f, row.y + 4f, 170f, 24f);
		Widgets.Label(t, title);
		TooltipHandler.TipRegion(t, (TipSignal)tip);
		float x = row.x + 190f;
		if (Widgets.ButtonText(new Rect(x, row.y, 80f, 26f), "GB_TraitAdd".Translate()))
		{
			var opts = new List<FloatMenuOption>();
			foreach (TraitDef def in DefDatabase<TraitDef>.AllDefsListForReading.OrderBy(z => z.label ?? z.defName))
			{
				if (def.degreeDatas == null || def.degreeDatas.Count == 0)
					continue;
				if (allowAny && def.degreeDatas.Count > 1)
				{
					string anyItem = def.defName + ":" + AnyDegree;
					if (!items.Contains(anyItem))
						opts.Add(new FloatMenuOption(def.LabelCap + " (" + "GB_TraitAnyDegree".Translate() + ")", () => Toggle(d, race.defName, anyItem)));
				}
				foreach (TraitDegreeData data in def.degreeDatas)
				{
					string item = TraitItem(def, data.degree);
					if (items.Contains(item))
						continue;
					opts.Add(new FloatMenuOption(data.LabelCap, () => Toggle(d, race.defName, item)));
				}
			}
			if (opts.Count > 0)
				Find.WindowStack.Add(new FloatMenu(opts));
		}
		x += 86f;
		// chips; click to remove
		foreach (string item in items)
		{
			string lbl = TraitLabel(item) + " ×";
			float w = Text.CalcSize(lbl).x + 16f;
			if (x + w > row.xMax)
			{
				row = l.GetRect(28f);
				x = row.x + 190f + 86f;
			}
			if (Widgets.ButtonText(new Rect(x, row.y, w, 26f), lbl))
			{
				Toggle(d, race.defName, item);
				break;
			}
			x += w + 4f;
		}
	}
}
