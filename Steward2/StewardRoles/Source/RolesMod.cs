using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ===========================================================================
// Steward – Roles (add-on of the Steward core)
// Give a colonist a role (doctor, cook, builder, soldier …). The role's work
// stays on priority 1 in every plan of the Steward; the rest of his work the
// Steward plans as before. Soldiers can draft themselves when enemies attack
// and go back to work afterwards. Roles can be suggested from skills and
// passions for the whole colony in one click.
// ===========================================================================
public enum ColonyRole
{
	None,
	Doctor,
	Warden,
	Cook,
	Hunter,
	Farmer,
	Builder,
	Miner,
	Crafter,
	Researcher,
	Handler,
	Hauler,
	Soldier
}

public sealed class RolesSettings : ModSettings
{
	public bool enabled = true;
	public bool soldierDraft = true;
	public bool soldierLightWork = true;
	public bool messages = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref soldierDraft, "soldierDraft", true);
		Scribe_Values.Look(ref soldierLightWork, "soldierLightWork", true);
		Scribe_Values.Look(ref messages, "messages", true);
	}
}

public sealed class RolesMod : Mod
{
	public const string Version = "0.1.0";
	public static RolesSettings S;

	public RolesMod(ModContentPack content) : base(content)
	{
		S = GetSettings<RolesSettings>();
		new Harmony("misak.steward.roles").PatchAll(typeof(RolesMod).Assembly);
		StewardApi.Register(new RolesStep());
		StewardApi.MapTick += SoldierDuty.Tick;
		StewardApi.GameStarted += SoldierDuty.Reset;
		StewardApi.WorkTabLine += RolesUI.WorkTabLine;
		StewardApi.AddSettingsPage(new SettingsPage { label = "SR_Name".Translate(), draw = r => RolesUI.DrawPage(r, false), write = () => S.Write() });
		MLog.Info("[Steward] Roles v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "SR_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect) => RolesUI.DrawPage(inRect, true);
}

// which work belongs to which role – by skill where possible, so modded work types of the same skill count too
public static class RoleWork
{
	private static readonly Dictionary<ColonyRole, List<WorkTypeDef>> cache = new Dictionary<ColonyRole, List<WorkTypeDef>>();

	public static readonly ColonyRole[] All = (ColonyRole[])Enum.GetValues(typeof(ColonyRole));

	public static string Label(ColonyRole r) => ("SR_Role_" + r).Translate();

	public static string Desc(ColonyRole r) => ("SR_RoleDesc_" + r).Translate();

	private static SkillDef Skill(string n) => DefDatabase<SkillDef>.GetNamedSilentFail(n);

	private static IEnumerable<SkillDef> Skills(ColonyRole r)
	{
		switch (r)
		{
			case ColonyRole.Doctor: yield return Skill("Medicine"); break;
			case ColonyRole.Cook: yield return Skill("Cooking"); break;
			case ColonyRole.Farmer: yield return Skill("Plants"); break;
			case ColonyRole.Builder: yield return Skill("Construction"); break;
			case ColonyRole.Miner: yield return Skill("Mining"); break;
			case ColonyRole.Crafter: yield return Skill("Crafting"); yield return Skill("Artistic"); break;
			case ColonyRole.Researcher: yield return Skill("Intellectual"); break;
			case ColonyRole.Handler: yield return Skill("Animals"); break;
			case ColonyRole.Warden: yield return Skill("Social"); break;
			case ColonyRole.Hunter: yield return Skill("Shooting"); break;
			case ColonyRole.Soldier: yield return Skill("Shooting"); yield return Skill("Melee"); break;
		}
	}

	private static IEnumerable<string> Named(ColonyRole r)
	{
		switch (r)
		{
			case ColonyRole.Warden: return new[] { "Warden" };
			case ColonyRole.Hunter: return new[] { "Hunting" };
			case ColonyRole.Hauler: return new[] { "Hauling", "Cleaning" };
			case ColonyRole.Researcher: return new[] { "Research" };
			case ColonyRole.Doctor: return new[] { "Doctor" };
			case ColonyRole.Handler: return new[] { "Handling" };
			default: return Array.Empty<string>();
		}
	}

	// the role's main work: priority 1
	public static List<WorkTypeDef> Main(ColonyRole r)
	{
		if (cache.TryGetValue(r, out var list))
			return list;
		list = new List<WorkTypeDef>();
		if (r != ColonyRole.None && r != ColonyRole.Soldier)
		{
			var named = Named(r).ToList();
			if (named.Count > 0)
				list.AddRange(named.Select(n => DefDatabase<WorkTypeDef>.GetNamedSilentFail(n)).Where(w => w != null));
			else
			{
				var skills = Skills(r).Where(s => s != null).ToList();
				list.AddRange(DefDatabase<WorkTypeDef>.AllDefsListForReading.Where(w =>
					w.relevantSkills != null && w.relevantSkills.Count > 0 && w.relevantSkills.All(s => skills.Contains(s))
					&& w.defName != "Hunting" && w.defName != "Warden" && w.defName != "Doctor"));
			}
		}
		cache[r] = list;
		return list;
	}

	// soldiers keep free for fights: only light work that can be dropped at once
	public static List<WorkTypeDef> Light()
	{
		return new[] { "Firefighter", "Hauling", "Cleaning" }.Select(n => DefDatabase<WorkTypeDef>.GetNamedSilentFail(n)).Where(w => w != null).ToList();
	}

	// how well a pawn fits a role: best skill level + passion
	public static float Score(Pawn p, ColonyRole r)
	{
		if (p?.skills == null || r == ColonyRole.None)
			return -1f;
		if (r == ColonyRole.Hauler)
			return 4f;
		var main = Main(r);
		if (r != ColonyRole.Soldier && main.Count > 0 && main.All(w => p.WorkTypeIsDisabled(w)))
			return -1f;
		float best = -1f;
		foreach (SkillDef s in Skills(r))
		{
			if (s == null)
				continue;
			SkillRecord rec = p.skills.GetSkill(s);
			if (rec == null || rec.TotallyDisabled)
				continue;
			float v = rec.Level + (rec.passion == Passion.Major ? 6f : rec.passion == Passion.Minor ? 3f : 0f);
			best = Mathf.Max(best, v);
		}
		if (r == ColonyRole.Soldier && p.WorkTagIsDisabled(WorkTags.Violent))
			return -1f;
		return best;
	}
}

// the roles of all colonists, saved with the game
public sealed class RolesGame : GameComponent
{
	public static RolesGame Instance;
	public Dictionary<int, ColonyRole> roles = new Dictionary<int, ColonyRole>();

	public RolesGame(Game game)
	{
		Instance = this;
	}

	public static ColonyRole Of(Pawn p)
	{
		if (p == null || Instance == null)
			return ColonyRole.None;
		return Instance.roles.TryGetValue(p.thingIDNumber, out ColonyRole r) ? r : ColonyRole.None;
	}

	public static void Set(Pawn p, ColonyRole r)
	{
		if (p == null || Instance == null)
			return;
		if (r == ColonyRole.None)
			Instance.roles.Remove(p.thingIDNumber);
		else
			Instance.roles[p.thingIDNumber] = r;
		if (p.MapHeld != null)
			ForemanMapComponent.For(p.MapHeld)?.RequestPlan(true);
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref roles, "roles", LookMode.Value, LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
			roles ??= new Dictionary<int, ColonyRole>();
	}

	// suggest roles for the colonists of a map: the important roles first to the best fitting free colonist,
	// everyone left gets his own best role. Soldiers and roles you set yourself stay.
	public static int Suggest(Map map, bool keepSet)
	{
		var pawns = Colonists(map).ToList();
		var free = pawns.Where(p => !keepSet || Of(p) == ColonyRole.None).ToList();
		var taken = new HashSet<ColonyRole>(pawns.Where(p => !free.Contains(p)).Select(Of));
		int n = 0;
		ColonyRole[] order = { ColonyRole.Doctor, ColonyRole.Cook, ColonyRole.Builder, ColonyRole.Farmer, ColonyRole.Researcher,
			ColonyRole.Crafter, ColonyRole.Miner, ColonyRole.Hunter, ColonyRole.Handler, ColonyRole.Warden };
		foreach (ColonyRole r in order)
		{
			if (taken.Contains(r) || free.Count == 0)
				continue;
			if (r == ColonyRole.Warden && !map.mapPawns.PrisonersOfColony.Any())
				continue;
			Pawn best = free.OrderByDescending(p => RoleWork.Score(p, r)).ThenBy(p => p.thingIDNumber).FirstOrDefault();
			if (best == null || RoleWork.Score(best, r) < 4f)
				continue;
			Set(best, r);
			free.Remove(best);
			taken.Add(r);
			n++;
		}
		foreach (Pawn p in free)
		{
			ColonyRole r = order.OrderByDescending(x => RoleWork.Score(p, x)).First();
			Set(p, RoleWork.Score(p, r) >= 6f ? r : ColonyRole.Hauler);
			n++;
		}
		return n;
	}

	public static IEnumerable<Pawn> Colonists(Map map)
	{
		if (map == null)
			return Enumerable.Empty<Pawn>();
		return map.mapPawns.FreeColonistsSpawned.Where(p => p.RaceProps.Humanlike && (p.DevelopmentalStage.Adult()))
			.OrderBy(p => p.LabelShortCap);
	}
}

// inside every Steward plan: the role's main work goes to priority 1 (your locked cells stay as you set them)
public sealed class RolesStep : IPlanStep
{
	public int Order => 850;
	public string Id => "roles";

	public void Apply(PlanContext ctx)
	{
		if (RolesMod.S == null || !RolesMod.S.enabled || RolesGame.Instance == null)
			return;
		foreach (Pawn p in ctx.pawns)
		{
			ColonyRole r = RolesGame.Of(p);
			if (r == ColonyRole.None)
				continue;
			string label = RoleWork.Label(r);
			if (r == ColonyRole.Soldier)
			{
				if (!RolesMod.S.soldierLightWork)
					continue;
				var light = RoleWork.Light();
				foreach (WorkTypeDef w in ctx.types)
				{
					if (ctx.Locked(p, w) || ctx.Pinned(p, w))
						continue;
					if (light.Contains(w))
						ctx.Set(p, w, Math.Min(2, ctx.Levels), "SR_Reason", label);
					else if (ctx.Get(p, w) > 0 && ctx.Get(p, w) < ctx.Levels)
						ctx.Set(p, w, ctx.Levels, "SR_ReasonSoldier", label);
				}
				continue;
			}
			foreach (WorkTypeDef w in RoleWork.Main(r))
				if (ctx.types.Contains(w) && ctx.CanDo(p, w))
					ctx.Set(p, w, 1, "SR_Reason", label, null, pin: true);
		}
	}
}

// soldiers draft themselves when enemies attack the home map and go back to work afterwards
public static class SoldierDuty
{
	private static readonly Dictionary<Map, HashSet<Pawn>> drafted = new Dictionary<Map, HashSet<Pawn>>();
	private static readonly Dictionary<Map, int> lastThreat = new Dictionary<Map, int>();
	// soldiers you drafted or undrafted yourself during this attack – left alone until it is over
	private static readonly HashSet<Pawn> released = new HashSet<Pawn>();
	internal static bool busy;
	private const int Grace = 1250;

	public static void Reset()
	{
		drafted.Clear();
		lastThreat.Clear();
		released.Clear();
	}

	private static void Draft(Pawn p, bool on)
	{
		busy = true;
		try { p.drafter.Drafted = on; }
		finally { busy = false; }
	}

	public static void Tick(ForemanMapComponent comp)
	{
		Map map = comp.map;
		if (RolesMod.S == null || !RolesMod.S.enabled || !RolesMod.S.soldierDraft || !map.IsPlayerHome || RolesGame.Instance == null)
			return;
		int now = Find.TickManager.TicksGame;
		bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
		if (!drafted.TryGetValue(map, out var mine))
			drafted[map] = mine = new HashSet<Pawn>();
		if (threat)
		{
			lastThreat[map] = now;
			int n = 0;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
			{
				if (RolesGame.Of(p) != ColonyRole.Soldier || p.drafter == null || p.Drafted || mine.Contains(p) || released.Contains(p))
					continue;
				if (p.Downed || p.InMentalState || p.WorkTagIsDisabled(WorkTags.Violent) || p.health.HasHediffsNeedingTend() && p.health.summaryHealth.SummaryHealthPercent < 0.5f)
					continue;
				Draft(p, true);
				mine.Add(p);
				n++;
			}
			if (n > 0 && RolesMod.S.messages)
				Messages.Message("SR_SoldiersDrafted".Translate(n), MessageTypeDefOf.NeutralEvent, false);
			return;
		}
		if (lastThreat.TryGetValue(map, out int last) && now - last < Grace)
			return;
		released.RemoveWhere(p => p == null || p.MapHeld == null || p.MapHeld == map);
		if (mine.Count == 0)
			return;
		int back = 0;
		foreach (Pawn p in mine)
		{
			if (p != null && p.Spawned && p.MapHeld == map && p.Drafted && p.drafter != null)
			{
				Draft(p, false);
				back++;
			}
		}
		mine.Clear();
		if (back > 0 && RolesMod.S.messages)
			Messages.Message("SR_SoldiersBack".Translate(back), MessageTypeDefOf.NeutralEvent, false);
	}

	// you undraft or draft a soldier yourself: he is yours again until the next attack
	public static void Forget(Pawn p)
	{
		bool was = false;
		foreach (var kv in drafted)
			was |= kv.Value.Remove(p);
		if (was)
			released.Add(p);
	}
}

[HarmonyPatch(typeof(Pawn_DraftController), nameof(Pawn_DraftController.Drafted), MethodType.Setter)]
public static class DraftWatch
{
	public static void Postfix(Pawn_DraftController __instance, bool value)
	{
		// any change that did not come from us (your click, the game) – that soldier is yours until the attack ends
		if (!SoldierDuty.busy && __instance?.pawn != null)
			SoldierDuty.Forget(__instance.pawn);
	}
}

// the role button on a selected colonist
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class RoleGizmo
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
	{
		foreach (Gizmo g in __result)
			yield return g;
		if (RolesMod.S == null || !RolesMod.S.enabled || RolesGame.Instance == null)
			yield break;
		Pawn p = __instance;
		if (p == null || !p.IsColonistPlayerControlled || !p.RaceProps.Humanlike || !p.DevelopmentalStage.Adult())
			yield break;
		ColonyRole r = RolesGame.Of(p);
		yield return new Command_Action
		{
			defaultLabel = r == ColonyRole.None ? "SR_GizmoNone".Translate().ToString() : RoleWork.Label(r),
			defaultDesc = "SR_GizmoDesc".Translate() + "\n\n" + (r == ColonyRole.None ? "SR_GizmoNoneDesc".Translate().ToString() : RoleWork.Label(r) + ": " + RoleWork.Desc(r)),
			icon = RolesUI.Icon(r),
			defaultIconColor = r == ColonyRole.None ? new Color(1f, 1f, 1f, 0.45f) : Color.white,
			action = () => Find.WindowStack.Add(new FloatMenu(RolesUI.Options(new List<Pawn> { p })))
		};
	}
}

public static class RolesUI
{
	private static readonly Dictionary<ColonyRole, Texture2D> icons = new Dictionary<ColonyRole, Texture2D>();
	private static Vector2 scroll;

	public static Texture2D Icon(ColonyRole r)
	{
		if (icons.TryGetValue(r, out Texture2D t) && t != null)
			return t;
		t = null;
		Texture2D Path(string s) => ContentFinder<Texture2D>.Get(s, false);
		Texture2D Thing(string d) => DefDatabase<ThingDef>.GetNamedSilentFail(d)?.uiIcon;
		switch (r)
		{
			case ColonyRole.Doctor: t = Path("UI/Commands/AsMedical"); break;
			case ColonyRole.Warden: t = Path("UI/Commands/ForPrisoners"); break;
			case ColonyRole.Cook: t = Thing("MealSimple"); break;
			case ColonyRole.Hunter: t = Path("UI/Designators/Hunt"); break;
			case ColonyRole.Farmer: t = Path("UI/Commands/SetPlantToGrow"); break;
			case ColonyRole.Builder: t = Path("UI/Designators/BuildRoofArea"); break;
			case ColonyRole.Miner: t = Path("UI/Designators/Mine"); break;
			case ColonyRole.Crafter: t = Thing("ComponentIndustrial"); break;
			case ColonyRole.Researcher: t = Thing("SimpleResearchBench"); break;
			case ColonyRole.Handler: t = Path("UI/Designators/Tame"); break;
			case ColonyRole.Hauler: t = Path("UI/Designators/Haul"); break;
			case ColonyRole.Soldier: t = Path("UI/Commands/Draft"); break;
		}
		if (t == null)
			t = Path("UI/Designators/Haul") ?? BaseContent.BadTex;
		icons[r] = t;
		return t;
	}

	public static List<FloatMenuOption> Options(List<Pawn> pawns)
	{
		var list = new List<FloatMenuOption>();
		Pawn one = pawns.Count == 1 ? pawns[0] : null;
		foreach (ColonyRole r in RoleWork.All)
		{
			ColonyRole role = r;
			string label = role == ColonyRole.None ? "SR_RoleNone".Translate().ToString() : RoleWork.Label(role);
			if (one != null && role != ColonyRole.None)
			{
				float sc = RoleWork.Score(one, role);
				if (sc < 0f)
				{
					list.Add(new FloatMenuOption(label + " (" + "SR_CannotDo".Translate() + ")", null, Icon(role), Color.white));
					continue;
				}
				if (role != ColonyRole.Hauler)
					label += "  (" + Mathf.RoundToInt(sc) + ")";
			}
			if (one != null && RolesGame.Of(one) == role)
				label = "✓ " + label;
			list.Add(new FloatMenuOption(label, () =>
			{
				foreach (Pawn p in pawns)
					RolesGame.Set(p, role);
			}, Icon(role), Color.white));
		}
		return list;
	}

	public static string WorkTabLine(ForemanMapComponent comp)
	{
		if (RolesMod.S == null || !RolesMod.S.enabled || RolesGame.Instance == null)
			return null;
		int n = RolesGame.Colonists(comp.map).Count(p => RolesGame.Of(p) != ColonyRole.None);
		return n > 0 ? "SR_TabLine".Translate(n).ToString() : null;
	}

	public static void DrawPage(Rect rect, bool own)
	{
		RolesSettings s = RolesMod.S;
		var l = new Listing_Standard();
		Rect top = new Rect(rect.x, rect.y, rect.width, 150f);
		l.Begin(top);
		bool was = s.enabled, wasLight = s.soldierLightWork;
		l.CheckboxLabeled("SR_Enabled".Translate(), ref s.enabled, "SR_EnabledTip".Translate());
		l.CheckboxLabeled("SR_SoldierDraft".Translate(), ref s.soldierDraft, "SR_SoldierDraftTip".Translate());
		l.CheckboxLabeled("SR_SoldierLight".Translate(), ref s.soldierLightWork, "SR_SoldierLightTip".Translate());
		l.CheckboxLabeled("SR_Messages".Translate(), ref s.messages);
		l.End();
		if ((was != s.enabled || wasLight != s.soldierLightWork) && Find.CurrentMap != null)
			ForemanMapComponent.For(Find.CurrentMap)?.RequestPlan(true);

		Rect rest = new Rect(rect.x, top.yMax + 6f, rect.width, rect.height - top.height - 6f);
		Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
		if (map == null || RolesGame.Instance == null)
		{
			Widgets.Label(rest, "SR_NoGame".Translate());
			return;
		}
		var pawns = RolesGame.Colonists(map).ToList();
		Rect bar = new Rect(rest.x, rest.y, rest.width, 30f);
		if (Widgets.ButtonText(new Rect(bar.x, bar.y, 230f, 28f), "SR_SuggestFree".Translate()))
			Messages.Message("SR_Suggested".Translate(RolesGame.Suggest(map, true)), MessageTypeDefOf.NeutralEvent, false);
		TooltipHandler.TipRegion(new Rect(bar.x, bar.y, 230f, 28f), "SR_SuggestFreeTip".Translate());
		if (Widgets.ButtonText(new Rect(bar.x + 238f, bar.y, 230f, 28f), "SR_SuggestAll".Translate()))
			Messages.Message("SR_Suggested".Translate(RolesGame.Suggest(map, false)), MessageTypeDefOf.NeutralEvent, false);
		TooltipHandler.TipRegion(new Rect(bar.x + 238f, bar.y, 230f, 28f), "SR_SuggestAllTip".Translate());
		if (Widgets.ButtonText(new Rect(bar.x + 476f, bar.y, 150f, 28f), "SR_ClearAll".Translate()))
			foreach (Pawn p in pawns)
				RolesGame.Set(p, ColonyRole.None);

		Rect list = new Rect(rest.x, bar.yMax + 6f, rest.width, rest.height - bar.height - 6f);
		const float rowH = 30f;
		Rect view = new Rect(0f, 0f, list.width - 16f, pawns.Count * rowH);
		Widgets.BeginScrollView(list, ref scroll, view);
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (i % 2 == 1)
				Widgets.DrawLightHighlight(row);
			Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 1f, 26f, 26f), p);
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(row.x + 34f, row.y, 200f, row.height), p.LabelShortCap);
			ColonyRole r = RolesGame.Of(p);
			Rect btn = new Rect(row.x + 240f, row.y + 1f, 200f, 26f);
			GUI.DrawTexture(new Rect(btn.x - 28f, btn.y + 1f, 24f, 24f), Icon(r));
			if (Widgets.ButtonText(btn, r == ColonyRole.None ? "SR_RoleNone".Translate().ToString() : RoleWork.Label(r)))
				Find.WindowStack.Add(new FloatMenu(Options(new List<Pawn> { p })));
			string main = r == ColonyRole.Soldier ? "SR_SoldierShort".Translate().ToString()
				: string.Join(", ", RoleWork.Main(r).Select(w => w.labelShort.CapitalizeFirst()));
			GUI.color = Color.gray;
			Widgets.Label(new Rect(btn.xMax + 10f, row.y, row.width - btn.xMax - 10f, row.height), main);
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
		}
		Widgets.EndScrollView();
	}
}

// a short line in the colonist's inspect pane
[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetInspectString))]
public static class RoleInspect
{
	public static void Postfix(Pawn __instance, ref string __result)
	{
		if (RolesMod.S == null || !RolesMod.S.enabled || RolesGame.Instance == null || __instance == null || !__instance.IsColonist)
			return;
		ColonyRole r = RolesGame.Of(__instance);
		if (r == ColonyRole.None)
			return;
		string line = "SR_Inspect".Translate(RoleWork.Label(r));
		__result = string.IsNullOrEmpty(__result) ? line : __result + "\n" + line;
	}
}
