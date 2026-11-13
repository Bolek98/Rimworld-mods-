using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace RandomBills;

public enum BillMode { Weighted, Random, Order }

public class RBSettings : ModSettings
{
	public bool enabled = true;
	public int defaultMode = 0;          // BillMode.Weighted
	public float topWeight = 0.6f;       // weighted: how much the top bills are preferred (0 = like random, 1 = strongly)
	public bool showGizmo = true;
	public bool avoidRepeat = true;      // 0.1.1: the bill a colonist just did gets a smaller chance next time
	public bool recommended = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref defaultMode, "defaultMode", 0);
		Scribe_Values.Look(ref topWeight, "topWeight", 0.6f);
		Scribe_Values.Look(ref showGizmo, "showGizmo", true);
		Scribe_Values.Look(ref avoidRepeat, "avoidRepeat", true);
		Scribe_Values.Look(ref recommended, "recommended", true);
	}
}

public class RandomBillsMod : Mod
{
	public static RBSettings S;
	public const string Version = "0.1.1";
	private static List<string> rec;

	public RandomBillsMod(ModContentPack content) : base(content)
	{
		S = GetSettings<RBSettings>();
		MUI.Init(new RBSettings(), null, "recommended");
		if (S.recommended) MUI.ApplyAll(S);
		var h = new Harmony("bolek98.randombills");
		h.PatchAll();
		MUI.PatchWindow(h, typeof(RandomBillsMod));
		MNews.Register(content, Version);
		Log.Message("[Random Bills] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Random Bills";

	public override void DoSettingsWindowContents(Rect r)
	{
		var s = S;
		MUI.Version(r, "v" + Version + " · Bolek98");
		rec ??= new List<string> { "RB_Rec1".Translate(), "RB_Rec2".Translate() };
		float y = r.y;
		y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), s, s.recommended, v => { s.recommended = v; if (v) MUI.ApplyAll(s); }, rec) + 6f;
		if (s.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
		Rect area = new Rect(r.x, y, Mathf.Min(r.width, 640f), r.yMax - y);
		var l = new Listing_Standard();
		MUI.BeginLock(s.recommended);
		try
		{
			l.Begin(area);
			MUI.Check(l, "RB_Enabled".Translate(), "RB_Enabled_Desc".Translate(), ref s.enabled, s, "enabled");
			if (l.ButtonTextLabeled("RB_DefaultMode".Translate(), ("RB_Mode_" + (BillMode)s.defaultMode).Translate()))
			{
				var menu = new List<FloatMenuOption>();
				foreach (BillMode m in Enum.GetValues(typeof(BillMode)))
				{
					BillMode mm = m;
					menu.Add(new FloatMenuOption(("RB_Mode_" + m).Translate(), () => s.defaultMode = (int)mm));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			s.topWeight = Mathf.Round(MUI.Slider(l, "RB_TopWeight".Translate(s.topWeight.ToStringPercent()), s.topWeight, 0f, 1f, "RB_TopWeight_Desc".Translate(), s, "topWeight") * 20f) / 20f;
			MUI.Check(l, "RB_AvoidRepeat".Translate(), "RB_AvoidRepeat_Desc".Translate(), ref s.avoidRepeat, s, "avoidRepeat");
			MUI.Check(l, "RB_ShowGizmo".Translate(), "RB_ShowGizmo_Desc".Translate(), ref s.showGizmo, s, "showGizmo");
			l.End();
		}
		finally { MUI.EndLock(area); }
	}
}

// the mode of each bench (saved with the map); missing = the default from the settings
public class RBMap : MapComponent
{
	public Dictionary<int, int> modes = new Dictionary<int, int>();
	private List<int> k;
	private List<int> v;

	public RBMap(Map map) : base(map) { }

	public static RBMap Of(Map m) => m?.GetComponent<RBMap>();

	public BillMode ModeOf(Thing bench)
		=> bench != null && modes.TryGetValue(bench.thingIDNumber, out int m) ? (BillMode)m : (BillMode)RandomBillsMod.S.defaultMode;

	public void Set(Thing bench, BillMode m)
	{
		if ((int)m == RandomBillsMod.S.defaultMode) modes.Remove(bench.thingIDNumber);
		else modes[bench.thingIDNumber] = (int)m;
	}

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
			foreach (int id in modes.Keys.ToList())
				if (!map.listerBuildings.allBuildingsColonist.Any(b => b.thingIDNumber == id))
					modes.Remove(id);
		Scribe_Collections.Look(ref modes, "rbModes", LookMode.Value, LookMode.Value, ref k, ref v);
		modes ??= new Dictionary<int, int>();
	}
}

// When a colonist picks a bill, the bench's bills are put in a random (or weighted random) order for that
// one look and put back right after – the game itself still checks everything (ingredients, skill,
// "do X times" / "until you have X", pawn restrictions). A bill whose unfinished item this colonist is
// already working on comes first, so nothing is left half done.
[HarmonyPatch(typeof(WorkGiver_DoBill), "StartOrResumeBillJob")]
public static class Patch_StartOrResume
{
	// 0.1.1: the last bill each colonist did (not saved – only for the next pick)
	private static readonly Dictionary<int, Bill> last = new Dictionary<int, Bill>();

	public static void Prefix(Pawn pawn, IBillGiver giver, out List<Bill> __state)
	{
		__state = null;
		var s = RandomBillsMod.S;
		if (s == null || !s.enabled || pawn == null || !(giver is Building bench) || giver.BillStack == null)
			return;
		if (FloatMenuMakerMap.makingFor == pawn)
			return;   // the right-click menu shows bills in their real order
		List<Bill> bills = giver.BillStack.Bills;
		if (bills == null || bills.Count < 2)
			return;
		BillMode mode = RBMap.Of(bench.Map)?.ModeOf(bench) ?? (BillMode)s.defaultMode;
		if (mode == BillMode.Order)
			return;
		__state = new List<Bill>(bills);
		var keyed = new List<(Bill b, float key)>(bills.Count);
		float strength = Mathf.Clamp01(s.topWeight);
		for (int i = 0; i < bills.Count; i++)
		{
			Bill b = bills[i];
			float key;
			if (b is Bill_ProductionWithUft u && u.BoundWorker == pawn)
				key = -1f;   // finish what he started first
			else
			{
				// weighted random order: key = U^(1/w); higher key first. Top bills get a bigger weight.
				float w = mode == BillMode.Weighted ? Mathf.Pow(1f / (i + 1), strength * 1.5f) : 1f;
				// "do X times" and the rest: the one he just did waits a bit, so the others get their turn
				if (s.avoidRepeat && last.TryGetValue(pawn.thingIDNumber, out Bill lb) && lb == b)
					w *= 0.25f;
				float r = Mathf.Max(Rand.Value, 1e-6f);
				key = -Mathf.Pow(r, 1f / Mathf.Max(w, 1e-4f));
			}
			keyed.Add((b, key));
		}
		keyed.Sort((a, c) => a.key.CompareTo(c.key));
		bills.Clear();
		foreach (var kb in keyed)
			bills.Add(kb.b);
	}

	public static void Postfix(Pawn pawn, Verse.AI.Job __result)
	{
		if (pawn != null && __result?.bill != null)
			last[pawn.thingIDNumber] = __result.bill;
	}

	public static Exception Finalizer(Exception __exception, IBillGiver giver, List<Bill> __state)
	{
		if (__state != null && giver?.BillStack?.Bills != null)
		{
			List<Bill> bills = giver.BillStack.Bills;
			bills.Clear();
			bills.AddRange(__state);
		}
		return __exception;
	}
}

[StaticConstructorOnStartup]
public static class Tex
{
	public static readonly Texture2D[] Mode =
	{
		ContentFinder<Texture2D>.Get("RandomBills/Weighted"),
		ContentFinder<Texture2D>.Get("RandomBills/Random"),
		ContentFinder<Texture2D>.Get("RandomBills/Order"),
	};
}

[HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
public static class Patch_BenchGizmo
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		var s = RandomBillsMod.S;
		if (s == null || !s.enabled || !s.showGizmo || !(__instance is IBillGiver) || __instance.Faction != Faction.OfPlayer)
			yield break;
		if (__instance is Pawn || __instance.def.AllRecipes == null || __instance.def.AllRecipes.Count == 0)
			yield break;
		RBMap comp = RBMap.Of(__instance.Map);
		if (comp == null)
			yield break;
		Building b = __instance;
		BillMode m = comp.ModeOf(b);
		yield return new Command_Action
		{
			defaultLabel = ("RB_Mode_" + m).Translate(),
			defaultDesc = "RB_Gizmo_Desc".Translate(),
			icon = Tex.Mode[(int)m],
			action = () => comp.Set(b, (BillMode)(((int)m + 1) % 3))
		};
	}
}
