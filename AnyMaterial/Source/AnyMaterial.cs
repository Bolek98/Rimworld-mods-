using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnyMaterial;

public enum PickRule { MostInStock, Cheapest, Strongest, Prettiest }

public class AMSettings : ModSettings
{
	public bool enabled = true;
	public int rule = 0;   // PickRule.MostInStock
	public bool recommended = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref rule, "rule", 0);
		Scribe_Values.Look(ref recommended, "recommended", true);
	}
}

public class AnyMaterialMod : Mod
{
	public static AMSettings S;
	public const string Version = "0.1.0";
	private static List<string> rec;

	public AnyMaterialMod(ModContentPack content) : base(content)
	{
		S = GetSettings<AMSettings>();
		MUI.Init(new AMSettings(), null, "recommended");
		if (S.recommended) MUI.ApplyAll(S);
		var h = new Harmony("bolek98.anymaterial");
		h.PatchAll();
		MUI.PatchWindow(h, typeof(AnyMaterialMod));
		MNews.Register(content, Version);
		Log.Message("[Any Material] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Any Material";

	public override void DoSettingsWindowContents(Rect r)
	{
		var s = S;
		MUI.Version(r, "v" + Version + " · Bolek98");
		rec ??= new List<string> { "AM_Rec1".Translate(), "AM_Rec2".Translate() };
		float y = r.y;
		y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), s, s.recommended, v => { s.recommended = v; if (v) MUI.ApplyAll(s); }, rec) + 6f;
		if (s.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
		Rect area = new Rect(r.x, y, Mathf.Min(r.width, 640f), r.yMax - y);
		var l = new Listing_Standard();
		MUI.BeginLock(s.recommended);
		try
		{
			l.Begin(area);
			MUI.Check(l, "AM_Enabled".Translate(), "AM_Enabled_Desc".Translate(), ref s.enabled, s, "enabled");
			if (l.ButtonTextLabeled("AM_Rule".Translate(), ("AM_Rule_" + (PickRule)s.rule).Translate()))
			{
				var menu = new List<FloatMenuOption>();
				foreach (PickRule pr in Enum.GetValues(typeof(PickRule)))
				{
					PickRule p = pr;
					menu.Add(new FloatMenuOption(("AM_Rule_" + p).Translate(), () => s.rule = (int)p));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
			GUI.color = MUI.Muted;
			l.Label("AM_Rule_Desc".Translate());
			GUI.color = Color.white;
			l.End();
		}
		finally { MUI.EndLock(area); }
	}
}

// which materials a blueprint may use: null set = any material it can be made of
public class AnyStuff : IExposable
{
	public List<ThingDef> allowed;
	public void ExposeData() => Scribe_Collections.Look(ref allowed, "allowed", LookMode.Def);
}

public class AMMap : MapComponent
{
	public Dictionary<int, AnyStuff> blueprints = new Dictionary<int, AnyStuff>();
	private List<int> k;
	private List<AnyStuff> v;

	public AMMap(Map map) : base(map) { }

	public static AMMap Of(Map m) => m?.GetComponent<AMMap>();

	public AnyStuff Get(Thing bp) => bp != null && blueprints.TryGetValue(bp.thingIDNumber, out AnyStuff a) ? a : null;

	public void Set(Blueprint_Build bp, List<ThingDef> allowed)
	{
		blueprints[bp.thingIDNumber] = new AnyStuff { allowed = allowed?.ToList() };
		Picker.Repick(bp, blueprints[bp.thingIDNumber], force: true);
	}

	public void Clear(Thing bp) => blueprints.Remove(bp.thingIDNumber);

	public override void ExposeData()
	{
		if (Scribe.mode == LoadSaveMode.Saving)
		{
			var alive = new HashSet<int>(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint).Select(t => t.thingIDNumber));
			foreach (int id in blueprints.Keys.ToList())
				if (!alive.Contains(id))
					blueprints.Remove(id);
		}
		Scribe_Collections.Look(ref blueprints, "amBlueprints", LookMode.Value, LookMode.Deep, ref k, ref v);
		blueprints ??= new Dictionary<int, AnyStuff>();
	}
}

public static class Picker
{
	public static IEnumerable<ThingDef> Stuffs(ThingDef building)
		=> DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.IsStuff && d.stuffProps.CanMake(building));

	public static int Need(ThingDef building, ThingDef stuff)
	{
		List<ThingDefCountClass> cost = building.CostListAdjusted(stuff, false);
		return cost?.FirstOrDefault(c => c.thingDef == stuff)?.count ?? building.CostStuffCount;
	}

	private static float Score(ThingDef stuff, ThingDef building, Map map, PickRule rule)
	{
		int have = map.resourceCounter.GetCount(stuff);
		switch (rule)
		{
			case PickRule.Cheapest: return -stuff.BaseMarketValue * Need(building, stuff);
			case PickRule.Strongest: return stuff.GetStatValueAbstract(StatDefOf.StuffPower_Armor_Sharp) + StuffFactor(stuff, StatDefOf.MaxHitPoints);
			case PickRule.Prettiest: return StuffFactor(stuff, StatDefOf.Beauty) + StuffOffset(stuff, StatDefOf.Beauty) * 0.1f;
			default: return have;
		}
	}

	private static float StuffFactor(ThingDef stuff, StatDef stat)
		=> stuff.stuffProps?.statFactors?.FirstOrDefault(m => m.stat == stat)?.value ?? 1f;

	private static float StuffOffset(ThingDef stuff, StatDef stat)
		=> stuff.stuffProps?.statOffsets?.FirstOrDefault(m => m.stat == stat)?.value ?? 0f;

	// keep the current material while there is enough of it; otherwise take the best one there is enough of
	public static void Repick(Blueprint_Build bp, AnyStuff a, bool force = false)
	{
		if (bp?.Map == null || a == null || !(bp.def.entityDefToBuild is ThingDef td) || !td.MadeFromStuff)
			return;
		Map map = bp.Map;
		ThingDef cur = bp.stuffToUse;
		bool curOk = cur != null && (a.allowed == null || a.allowed.Contains(cur)) && map.resourceCounter.GetCount(cur) >= Need(td, cur);
		if (curOk && !force)
			return;
		var rule = (PickRule)AnyMaterialMod.S.rule;
		IEnumerable<ThingDef> cands = (a.allowed ?? Stuffs(td).ToList()).Where(d => d.IsStuff && d.stuffProps.CanMake(td));
		ThingDef best = null;
		float bestScore = float.MinValue;
		foreach (ThingDef d in cands)
		{
			if (map.resourceCounter.GetCount(d) < Need(td, d))
				continue;
			float sc = Score(d, td, map, rule);
			if (sc > bestScore)
			{
				bestScore = sc;
				best = d;
			}
		}
		if (best == null)
		{
			if (curOk) return;
			// nothing is there in full – take what there is most of, so at least something gets delivered
			best = cands.OrderByDescending(d => map.resourceCounter.GetCount(d)).FirstOrDefault();
		}
		if (best != null && best != cur)
		{
			bp.stuffToUse = best;
			bp.DirtyMapMesh(map);
		}
	}
}

// before a builder brings materials, the blueprint picks its material
[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToBlueprints), nameof(WorkGiver_ConstructDeliverResourcesToBlueprints.HasJobOnThing))]
public static class Patch_HasJob
{
	public static void Prefix(Thing t) => Hook.Before(t);
}

[HarmonyPatch(typeof(WorkGiver_ConstructDeliverResourcesToBlueprints), nameof(WorkGiver_ConstructDeliverResourcesToBlueprints.JobOnThing))]
public static class Patch_JobOn
{
	public static void Prefix(Thing t) => Hook.Before(t);
}

public static class Hook
{
	private static int lastTick;
	private static readonly HashSet<int> done = new HashSet<int>();

	public static void Before(Thing t)
	{
		if (!(t is Blueprint_Build bp) || AnyMaterialMod.S == null || !AnyMaterialMod.S.enabled)
			return;
		AnyStuff a = AMMap.Of(bp.Map)?.Get(bp);
		if (a == null)
			return;
		// at most once per blueprint and tick (the work giver asks many times)
		int now = Find.TickManager.TicksGame;
		if (now != lastTick) { lastTick = now; done.Clear(); }
		if (!done.Add(bp.thingIDNumber))
			return;
		try { Picker.Repick(bp, a); }
		catch (Exception e) { Log.WarningOnce("[Any Material] " + e.Message, 0x41af11); }
	}
}

// the material menu of a build button gets "Any material" and "Choose materials…"
[HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.ProcessInput))]
public static class Patch_StuffMenu
{
	public static AnyStuff pending;   // what the next blueprints of the selected build button get
	public static Designator_Build pendingFor;
	private static readonly AccessTools.FieldRef<FloatMenu, List<FloatMenuOption>> Options = AccessTools.FieldRefAccess<FloatMenu, List<FloatMenuOption>>("options");
	private static readonly AccessTools.FieldRef<Designator_Build, ThingDef> StuffRef = AccessTools.FieldRefAccess<Designator_Build, ThingDef>("stuffDef");

	public static void Postfix(Designator_Build __instance)
	{
		if (AnyMaterialMod.S == null || !AnyMaterialMod.S.enabled)
			return;
		if (!(__instance.PlacingDef is ThingDef td) || !td.MadeFromStuff)
			return;
		FloatMenu menu = Find.WindowStack.WindowOfType<FloatMenu>();
		if (menu == null)
			return;
		List<FloatMenuOption> opts = Options(menu);
		if (opts == null || opts.Count == 0)
			return;
		Designator_Build des = __instance;
		// a normal material: no "any" for the next blueprints
		foreach (FloatMenuOption o in opts)
		{
			Action old = o.action;
			o.action = () => { pending = null; pendingFor = null; old?.Invoke(); };
		}
		Action first = opts[0].action;
		opts.Insert(0, new FloatMenuOption("AM_MenuAny".Translate(), () =>
		{
			first?.Invoke();
			pending = new AnyStuff { allowed = null };
			pendingFor = des;
		}));
		opts.Insert(1, new FloatMenuOption("AM_MenuChoose".Translate(), () =>
		{
			first?.Invoke();
			Find.WindowStack.Add(new Dialog_ChooseStuff(td, null, list =>
			{
				pending = new AnyStuff { allowed = list };
				pendingFor = des;
				if (list.Count > 0) StuffRef(des) = list[0];
			}));
		}));
	}
}

[HarmonyPatch(typeof(Designator_Build), nameof(Designator_Build.DesignateSingleCell))]
public static class Patch_Designate
{
	public static void Postfix(Designator_Build __instance, IntVec3 c)
	{
		AnyStuff p = Patch_StuffMenu.pending;
		if (p == null || Patch_StuffMenu.pendingFor != __instance || __instance.Map == null)
			return;
		AMMap comp = AMMap.Of(__instance.Map);
		foreach (Thing t in c.GetThingList(__instance.Map))
			if (t is Blueprint_Build bp && bp.def.entityDefToBuild == __instance.PlacingDef && comp.Get(bp) == null)
				comp.Set(bp, p.allowed);
	}
}

// a button on blueprints: switch selected blueprints to "any material" (or back to their current one)
[StaticConstructorOnStartup]
public static class Tex
{
	public static readonly Texture2D Any = ContentFinder<Texture2D>.Get("AnyMaterial/Any");
}

[HarmonyPatch(typeof(Blueprint_Build), nameof(Blueprint_Build.GetGizmos))]
public static class Patch_BlueprintGizmo
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Blueprint_Build __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		if (AnyMaterialMod.S == null || !AnyMaterialMod.S.enabled || __instance.Faction != Faction.OfPlayer)
			yield break;
		if (!(__instance.def.entityDefToBuild is ThingDef td) || !td.MadeFromStuff)
			yield break;
		AMMap comp = AMMap.Of(__instance.Map);
		if (comp == null)
			yield break;
		Blueprint_Build bp = __instance;
		AnyStuff a = comp.Get(bp);
		yield return new Command_Toggle
		{
			defaultLabel = "AM_GizmoAny".Translate(),
			defaultDesc = "AM_GizmoAny_Desc".Translate(),
			icon = Tex.Any,
			isActive = () => comp.Get(bp) != null,
			toggleAction = () =>
			{
				if (comp.Get(bp) != null) comp.Clear(bp);
				else comp.Set(bp, null);
			}
		};
		if (Find.Selector.NumSelected == 1 || a?.allowed != null)
			yield return new Command_Action
			{
				defaultLabel = "AM_GizmoChoose".Translate(),
				defaultDesc = "AM_GizmoChoose_Desc".Translate(),
				icon = Tex.Any,
				action = () =>
				{
					var sel = Find.Selector.SelectedObjects.OfType<Blueprint_Build>().Where(x => x.def == bp.def).ToList();
					Find.WindowStack.Add(new Dialog_ChooseStuff(td, comp.Get(bp)?.allowed, list =>
					{
						foreach (Blueprint_Build b in sel)
							comp.Set(b, list);
					}));
				}
			};
	}
}

public class Dialog_ChooseStuff : Window
{
	private readonly ThingDef building;
	private readonly Action<List<ThingDef>> done;
	private readonly HashSet<ThingDef> on;
	private readonly List<ThingDef> all;
	private Vector2 scroll;

	public Dialog_ChooseStuff(ThingDef building, List<ThingDef> current, Action<List<ThingDef>> done)
	{
		this.building = building;
		this.done = done;
		all = Picker.Stuffs(building).OrderBy(d => d.stuffProps.categories?.FirstOrDefault()?.defName ?? "").ThenBy(d => d.label).ToList();
		on = new HashSet<ThingDef>(current ?? all.Where(d => Find.CurrentMap != null && Find.CurrentMap.resourceCounter.GetCount(d) > 0));
		doCloseX = true;
		forcePause = true;
		absorbInputAroundWindow = true;
	}

	public override Vector2 InitialSize => new Vector2(460f, 560f);

	public override void DoWindowContents(Rect r)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "AM_ChooseTitle".Translate(building.LabelCap));
		Text.Font = GameFont.Small;
		float y = r.y + 36f;
		if (Widgets.ButtonText(new Rect(r.x, y, 140f, 26f), "AM_All".Translate())) { on.Clear(); on.UnionWith(all); }
		if (Widgets.ButtonText(new Rect(r.x + 146f, y, 140f, 26f), "AM_None".Translate())) on.Clear();
		y += 32f;
		Map map = Find.CurrentMap;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 40f);
		Rect view = new Rect(0f, 0f, outR.width - 18f, all.Count * 28f);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < all.Count; i++)
		{
			ThingDef d = all[i];
			Rect row = new Rect(0f, i * 28f, view.width, 26f);
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.DefIcon(new Rect(row.x + 2f, row.y + 1f, 24f, 24f), d);
			bool v = on.Contains(d);
			string have = map != null ? " (" + map.resourceCounter.GetCount(d) + "/" + Picker.Need(building, d) + ")" : "";
			Widgets.CheckboxLabeled(new Rect(row.x + 30f, row.y + 1f, row.width - 34f, 24f), d.LabelCap + have, ref v);
			if (v) on.Add(d); else on.Remove(d);
		}
		Widgets.EndScrollView();
		if (Widgets.ButtonText(new Rect(r.xMax - 160f, r.yMax - 34f, 160f, 32f), "OK".Translate()))
		{
			done?.Invoke(all.Where(on.Contains).ToList());
			Close();
		}
	}
}
