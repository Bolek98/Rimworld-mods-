using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace SCGWardrobe;

// Tailor planner: what the colony should make. Nothing is ordered until the player presses "Add bill".
public sealed class TailorSuggestion
{
	public RecipeDef recipe;
	public int count;
	public string reason;
	public List<string> who = new List<string>();
}

public static class Tailor
{
	private static RecipeDef RecipeFor(ThingDef d)
	{
		if (d == null) return null;
		RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail("Make_" + d.defName);
		if (r != null) return r;
		return DefDatabase<RecipeDef>.AllDefsListForReading.FirstOrDefault(x => x.ProducedThingDef == d);
	}

	public static Building BenchFor(Map map, RecipeDef r)
	{
		if (map == null || r == null) return null;
		return map.listerBuildings.allBuildingsColonist.FirstOrDefault(b => b is IBillGiver && b.def.AllRecipes != null && b.def.AllRecipes.Contains(r));
	}

	private static bool Covers(Pawn p, BodyPartGroupDef g, ApparelLayerDef layer)
	{
		return p.apparel.WornApparel.Any(a => a.def.apparel.bodyPartGroups.Contains(g) && (layer == null || a.def.apparel.layers.Contains(layer)));
	}

	public static List<TailorSuggestion> Suggest(Map map)
	{
		var res = new Dictionary<RecipeDef, TailorSuggestion>();
		if (map == null) return new List<TailorSuggestion>();
		WardrobeSettings S = WardrobeMod.Settings;
		void Add(ThingDef d, string reason, Pawn p)
		{
			RecipeDef r = RecipeFor(d);
			if (r == null || !r.AvailableNow || BenchFor(map, r) == null) return;
			if (!res.TryGetValue(r, out TailorSuggestion s)) res[r] = s = new TailorSuggestion { recipe = r, reason = reason };
			s.count++;
			if (p != null && !s.who.Contains(p.LabelShort)) s.who.Add(p.LabelShort);
		}
		ThingDef shirt = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_BasicShirt");
		ThingDef pants = DefDatabase<ThingDef>.GetNamedSilentFail("Apparel_Pants");
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
		{
			if (p.apparel == null) continue;
			if (shirt != null && !Covers(p, BodyPartGroupDefOf.Torso, ApparelLayerDefOf.OnSkin) && shirt.apparel.PawnCanWear(p)) Add(shirt, "WR_SugBareTorso", p);
			if (pants != null && !Covers(p, BodyPartGroupDefOf.Legs, null) && pants.apparel.PawnCanWear(p)) Add(pants, "WR_SugBareLegs", p);
			foreach (Apparel a in p.apparel.WornApparel)
			{
				if (p.apparel.IsLocked(a) || a.def.useHitPoints == false) continue;
				float hp = (float)a.HitPoints / a.MaxHitPoints;
				if (hp < S.replaceBelow && !Spare(map, a.def)) Add(a.def, "WR_SugTattered", p);
				else if (a.TryGetQuality(out QualityCategory q) && q <= QualityCategory.Poor) Add(a.def, "WR_SugPoor", p);
			}
		}
		// sleepwear / festive clothes if the colony has none at all
		if (S.sleepwear) MissingSet(map, S.SleepDefs, "WR_SugSleep", Add);
		if (S.festive) MissingSet(map, S.FestiveDefs, "WR_SugFestive", Add);
		return res.Values.OrderByDescending(s => s.count).ToList();
	}

	private static void MissingSet(Map map, List<string> defs, string reason, Action<ThingDef, string, Pawn> add)
	{
		if (defs.Count == 0) return;
		var set = new HashSet<string>(defs);
		int have = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel).Count(t => set.Contains(t.def.defName))
			+ map.mapPawns.FreeColonistsSpawned.Sum(p => p.apparel?.WornApparel.Count(a => set.Contains(a.def.defName)) ?? 0);
		if (have > 0) return;
		foreach (string d in defs)
		{
			ThingDef td = DefDatabase<ThingDef>.GetNamedSilentFail(d);
			RecipeDef r = td == null ? null : RecipeFor(td);
			if (r == null || !r.AvailableNow || BenchFor(map, r) == null) continue;
			for (int i = 0; i < Math.Min(map.mapPawns.FreeColonistsSpawnedCount, 6); i++) add(td, reason, null);
			return;
		}
	}

	private static bool Spare(Map map, ThingDef d)
	{
		foreach (Thing t in map.listerThings.ThingsOfDef(d))
			if (t.Spawned && !t.IsForbidden(Faction.OfPlayer) && (float)t.HitPoints / t.MaxHitPoints >= WardrobeMod.Settings.replaceBelow && !(t is Apparel a && a.WornByCorpse))
				return true;
		return false;
	}

	public static void AddBill(Map map, TailorSuggestion s)
	{
		Building b = BenchFor(map, s.recipe);
		if (!(b is IBillGiver g)) return;
		var bill = new Bill_Production(s.recipe) { repeatMode = BillRepeatModeDefOf.RepeatCount, repeatCount = Math.Max(1, s.count) };
		g.BillStack.AddBill(bill);
		Messages.Message("WR_BillAdded".Translate(s.recipe.LabelCap, b.LabelCap, bill.repeatCount), b, MessageTypeDefOf.TaskCompletion, false);
	}
}

// ---------------- colonist buttons: favourites ----------------
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class GizmoPatch
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
	{
		foreach (Gizmo g in __result) yield return g;
		WardrobeSettings S = WardrobeMod.Settings;
		if (S == null || !S.enabled || !S.favorites) yield break;
		Pawn p = __instance;
		if (p == null || !p.IsColonistPlayerControlled || p.apparel == null || Find.Selector.NumSelected != 1) yield break;
		WardrobeGame game = WardrobeGame.Get();
		if (game == null) yield break;
		int n = game.FavoriteCount(p);
		yield return new Command_Action
		{
			defaultLabel = "WR_SetFavorites".Translate(),
			defaultDesc = "WR_SetFavorites_Desc".Translate().ToString() + (n > 0 ? "\n\n" + "WR_FavoritesNow".Translate(n).ToString() : ""),
			icon = ContentFinder<Texture2D>.Get("UI/Icons/Options/OutfitsIcon", false) ?? BaseContent.BadTex,
			action = () =>
			{
				var things = new List<Thing>(p.apparel.WornApparel.Cast<Thing>());
				if (p.equipment?.Primary != null) things.Add(p.equipment.Primary);
				game.SetFavorites(p, things);
				Messages.Message("WR_FavoritesSaved".Translate(p.LabelShort, things.Count), p, MessageTypeDefOf.SilentInput, false);
			}
		};
		if (n > 0)
			yield return new Command_Action
			{
				defaultLabel = "WR_ClearFavorites".Translate(),
				defaultDesc = "WR_ClearFavorites_Desc".Translate(),
				icon = ContentFinder<Texture2D>.Get("UI/Designators/Cancel", false) ?? BaseContent.BadTex,
				action = () => game.ClearFavorites(p)
			};
	}
}
