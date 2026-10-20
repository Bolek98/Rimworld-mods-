using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace BodyFit;

// ---------------------------------------------------------------------------
// Knowledge about body types, races and textures. Everything is checked
// against what really exists in the game (any mod), nothing is hard-coded
// to particular mods. Results are cached – rendering asks very often.
// ---------------------------------------------------------------------------
public static class Bodies
{
	public enum Kind { Male, Female, Thin, Fat, Hulk, Child, Baby, Other }

	private static readonly Dictionary<string, bool> texCache = new Dictionary<string, bool>();

	// any of the four directions exists
	public static bool TextureExists(string path)
	{
		if (path.NullOrEmpty())
			return false;
		bool ok;
		lock (BF.Gate)
			if (texCache.TryGetValue(path, out ok))
				return ok;
		// 1.2.3: textures only on the main thread; elsewhere ask for it and say "not ready"
		if (!BF.Main)
		{
			BF.QueueTexture(path);
			return false;
		}
		ok = ContentFinder<Texture2D>.Get(path + "_south", false) != null
		     || ContentFinder<Texture2D>.Get(path + "_north", false) != null
		     || ContentFinder<Texture2D>.Get(path + "_east", false) != null
		     || ContentFinder<Texture2D>.Get(path, false) != null;
		lock (BF.Gate)
			texCache[path] = ok;
		return ok;
	}

	public static Kind KindOf(BodyTypeDef b, Gender g = Gender.None)
	{
		if (b == null)
			return g == Gender.Female ? Kind.Female : Kind.Male;
		if (b == BodyTypeDefOf.Male) return Kind.Male;
		if (b == BodyTypeDefOf.Female) return Kind.Female;
		if (b == BodyTypeDefOf.Thin) return Kind.Thin;
		if (b == BodyTypeDefOf.Fat) return Kind.Fat;
		if (b == BodyTypeDefOf.Hulk) return Kind.Hulk;
		if (b == BodyTypeDefOf.Child) return Kind.Child;
		if (b == BodyTypeDefOf.Baby) return Kind.Baby;
		string n = b.defName.ToLowerInvariant();
		if (n.Contains("baby") || n.Contains("infant") || n.Contains("newborn")) return Kind.Baby;
		if (n.Contains("child") || n.Contains("kid") || n.Contains("teen") || n.Contains("loli") || n.Contains("shota")) return Kind.Child;
		if (n.Contains("hulk") || n.Contains("muscle") || n.Contains("big") || n.Contains("giant") || n.Contains("large")) return Kind.Hulk;
		if (n.Contains("fat") || n.Contains("chub") || n.Contains("heavy")) return Kind.Fat;
		if (n.Contains("thin") || n.Contains("slim") || n.Contains("small") || n.Contains("slender")) return Kind.Thin;
		if (n.Contains("female") || n.Contains("woman") || n.Contains("girl") || n.Contains("lady")) return Kind.Female;
		if (n.Contains("male") || n.Contains("boy")) return Kind.Male;
		return g == Gender.Female ? Kind.Female : Kind.Male;
	}

	public static BodyTypeDef Vanilla(Kind k)
	{
		switch (k)
		{
			case Kind.Female: return BodyTypeDefOf.Female;
			case Kind.Thin: return BodyTypeDefOf.Thin;
			case Kind.Fat: return BodyTypeDefOf.Fat;
			case Kind.Hulk: return BodyTypeDefOf.Hulk;
			case Kind.Child: return BodyTypeDefOf.Child;
			case Kind.Baby: return BodyTypeDefOf.Baby;
			default: return BodyTypeDefOf.Male;
		}
	}

	// Most similar first. Children never get adult bodies and the other way round
	// unless nothing else is left.
	public static IEnumerable<BodyTypeDef> Similar(BodyTypeDef b, Gender g)
	{
		// your own choice in the settings comes first
		if (b != null && BodyFitMod.Settings.bodyFallback.TryGetValue(b.defName, out string own))
		{
			BodyTypeDef o = DefDatabase<BodyTypeDef>.GetNamedSilentFail(own);
			if (o != null && o != b)
				yield return o;
		}
		Kind k = KindOf(b, g);
		Kind[] order;
		switch (k)
		{
			case Kind.Female: order = new[] { Kind.Female, Kind.Thin, Kind.Male, Kind.Fat, Kind.Hulk }; break;
			case Kind.Thin: order = g == Gender.Female ? new[] { Kind.Thin, Kind.Female, Kind.Male, Kind.Fat, Kind.Hulk } : new[] { Kind.Thin, Kind.Male, Kind.Female, Kind.Fat, Kind.Hulk }; break;
			case Kind.Fat: order = new[] { Kind.Fat, Kind.Male, Kind.Hulk, Kind.Female, Kind.Thin }; break;
			case Kind.Hulk: order = new[] { Kind.Hulk, Kind.Male, Kind.Fat, Kind.Female, Kind.Thin }; break;
			case Kind.Child: order = new[] { Kind.Child, Kind.Thin, Kind.Female, Kind.Male }; break;
			case Kind.Baby: order = new[] { Kind.Baby, Kind.Child, Kind.Thin, Kind.Female }; break;
			default: order = new[] { Kind.Male, Kind.Hulk, Kind.Thin, Kind.Fat, Kind.Female }; break;
		}
		foreach (Kind o in order)
		{
			BodyTypeDef v = Vanilla(o);
			if (v != null && v != b)
				yield return v;
		}
	}

	// ---- Humanoid Alien Races (optional, by reflection) --------------------
	private static readonly Dictionary<ThingDef, List<BodyTypeDef>> harBodies = new Dictionary<ThingDef, List<BodyTypeDef>>();

	public static bool IsHar(ThingDef race) => race != null && race.GetType().GetField("alienRace") != null;

	// The body types a HAR race allows (empty = no limit / not HAR).
	public static List<BodyTypeDef> HarBodyTypes(ThingDef race)
	{
		if (race == null)
			return new List<BodyTypeDef>();
		List<BodyTypeDef> list;
		lock (BF.Gate)
			if (harBodies.TryGetValue(race, out list))
				return list;
		list = new List<BodyTypeDef>();
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object general = alien?.GetType().GetField("generalSettings")?.GetValue(alien);
			object gen = general?.GetType().GetField("alienPartGenerator")?.GetValue(general);
			if (gen?.GetType().GetField("bodyTypes")?.GetValue(gen) is System.Collections.IEnumerable e)
				foreach (object o in e)
					if (o is BodyTypeDef b)
						list.Add(b);
		}
		catch
		{
		}
		lock (BF.Gate)
			harBodies[race] = list;
		return list;
	}

	// ---- is a pawn's body type usable at all? ------------------------------
	public static bool BodyUsable(Pawn p, BodyTypeDef b)
	{
		if (b == null)
			return false;
		if (IsHar(p.def))
		{
			// HAR draws its own bodies: the body type must be allowed AND (1.2.5) the race
			// must really have a picture for it – other mods (BB bodies …) add body types
			// to races that have no picture for them, and the pawn becomes invisible
			List<BodyTypeDef> allowed = HarBodyTypes(p.def);
			if (allowed.Count != 0 && !allowed.Contains(b))
				return false;
			return RaceHasBody(p.def, b);
		}
		return TextureExists(b.bodyNakedGraphicPath);
	}

	// 1.2.5: does a HAR race have its own body picture for this body type?
	// (true when the race has no own body path – then HAR uses the vanilla bodies)
	public static bool RaceHasBody(ThingDef race, BodyTypeDef b)
	{
		if (race == null || b == null)
			return false;
		if (!RaceHasOwnBodyPath(race))
			return TextureExists(b.bodyNakedGraphicPath);
		return RaceBodyPath(race, b) != null;
	}

	private static bool RaceHasOwnBodyPath(ThingDef race)
	{
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object gp = alien?.GetType().GetField("graphicPaths")?.GetValue(alien);
			object body = gp?.GetType().GetField("body")?.GetValue(gp);
			string basePath = body as string ?? body?.GetType().GetField("path")?.GetValue(body) as string;
			return !basePath.NullOrEmpty();
		}
		catch
		{
			return false;
		}
	}

	public static BodyTypeDef BestUsable(Pawn p, BodyTypeDef b)
	{
		if (IsHar(p.def))
		{
			List<BodyTypeDef> allowed = HarBodyTypes(p.def);
			// 1.2.5: only body types the race really has a picture for
			allowed = allowed.Where(x => RaceHasBody(p.def, x)).ToList();
			if (allowed.Count == 0)
				return b;
			Kind want = KindOf(b, p.gender);
			// same kind first, then the similarity order, then anything allowed
			BodyTypeDef same = allowed.FirstOrDefault(x => KindOf(x, p.gender) == want);
			if (same != null)
				return same;
			foreach (BodyTypeDef v in Similar(b, p.gender))
			{
				Kind vk = KindOf(v, p.gender);
				BodyTypeDef m = allowed.FirstOrDefault(x => KindOf(x, p.gender) == vk);
				if (m != null)
					return m;
			}
			return allowed[0];
		}
		foreach (BodyTypeDef v in Similar(b, p.gender))
			if (TextureExists(v.bodyNakedGraphicPath))
				return v;
		return b;
	}

	// ---- 1.2.2: the race a piece of clothing was drawn for -----------------
	// Clothing from a race mod (Kiiro coat, Maru outfit …) is drawn on that
	// race's bodies. Its home race is the alien race defined in the same mod.
	private static readonly Dictionary<ThingDef, ThingDef> homeRace = new Dictionary<ThingDef, ThingDef>();

	// 2.3.6: a head picture of a race (HAR head types) – for hats and masks made for that race
	private static readonly Dictionary<(ThingDef, Gender), string> raceHead = new Dictionary<(ThingDef, Gender), string>();

	public static string RaceHead(ThingDef race, Gender g)
	{
		if (race == null)
			return null;
		string path;
		lock (BF.Gate)
			if (raceHead.TryGetValue((race, g), out path))
				return path;
		path = null;
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object general = alien?.GetType().GetField("generalSettings")?.GetValue(alien);
			object gen = general?.GetType().GetField("alienPartGenerator")?.GetValue(general);
			if (gen?.GetType().GetField("headTypes")?.GetValue(gen) is System.Collections.IEnumerable e)
			{
				string any = null;
				foreach (object o in e)
					if (o is HeadTypeDef h && !h.graphicPath.NullOrEmpty())
					{
						any ??= h.graphicPath;
						if (h.gender == g) { path = h.graphicPath; break; }
					}
				path ??= any;
			}
		}
		catch
		{
		}
		lock (BF.Gate)
			raceHead[(race, g)] = path;
		return path;
	}

	public static ThingDef HomeRace(ThingDef apparel)
	{
		if (apparel == null)
			return null;
		ThingDef r;
		lock (BF.Gate)
			if (homeRace.TryGetValue(apparel, out r))
				return r;
		r = null;
		ModContentPack mod = apparel.modContentPack;
		if (mod != null && !mod.IsOfficialMod)
		{
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (d.modContentPack == mod && d.race != null && d.race.Humanlike && IsHar(d))
				{
					r = d;
					break;
				}
			}
		}
		lock (BF.Gate)
			homeRace[apparel] = r;
		return r;
	}

	// The body picture of a race for a body type (HAR: graphicPaths.body.path +
	// "Naked_" + body, also with Female_/Male_ in front; Baby uses Child). null = not found.
	private static readonly Dictionary<(ThingDef, BodyTypeDef), string> raceBody = new Dictionary<(ThingDef, BodyTypeDef), string>();

	public static string RaceBodyPath(ThingDef race, BodyTypeDef b)
	{
		if (race == null || b == null)
			return null;
		string path;
		lock (BF.Gate)
			if (raceBody.TryGetValue((race, b), out path))
				return path;
		path = null;
		bool wasNotReady = BF.NotReady;
		BF.NotReady = false;
		try
		{
			object alien = race.GetType().GetField("alienRace")?.GetValue(race);
			object gp = alien?.GetType().GetField("graphicPaths")?.GetValue(alien);
			object body = gp?.GetType().GetField("body")?.GetValue(gp);
			string basePath = body as string ?? body?.GetType().GetField("path")?.GetValue(body) as string;
			if (!basePath.NullOrEmpty())
			{
				// 2.2.1: HAR draws babies with the race's Child picture (Naked_Child) –
				// a race without Naked_Baby is fine, its babies must not be "repaired"
				string n = b == BodyTypeDefOf.Baby ? BodyTypeDefOf.Child.defName : b.defName;
				foreach (string c in new[] { basePath + "Naked_" + n, basePath + "Female_Naked_" + n, basePath + "Male_Naked_" + n })
				{
					if (TextureExists(c))
					{
						path = c;
						break;
					}
				}
			}
		}
		catch
		{
		}
		if (path == null && !IsHar(race))
			path = b.bodyNakedGraphicPath;
		bool guessed = BF.NotReady;
		BF.NotReady = wasNotReady || guessed;
		if (!guessed)
			lock (BF.Gate)
				raceBody[(race, b)] = path;
		return path;
	}

	public static void ClearCaches()
	{
		lock (BF.Gate)
		{
			texCache.Clear();
			harBodies.Clear();
			homeRace.Clear();
			raceBody.Clear();
		}
	}
}
