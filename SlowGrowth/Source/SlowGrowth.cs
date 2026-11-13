using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;

namespace SlowGrowth;

public class SGSettings : ModSettings
{
	public bool enabled = true;
	public int firstDay = 10;          // no newcomer before this day
	public int daysBetween = 8;        // at least this many days between two newcomers
	public int cap = 0;                // no newcomers from events at this many colonists (0 = no limit)
	public float popIntent = 0.5f;     // how eager the storyteller is to send people (1 = normal)
	public bool wanderers = true;      // gate wanderers who join
	public bool questJoiners = true;   // gate quests where someone joins (refugee pods, chased refugees …)
	public bool creepJoiners = true;   // gate Anomaly creepjoiners
	public bool strangerInBlack = false; // the stranger in black comes when things go badly – not gated by default
	public bool recruitsCount = true;  // a recruited prisoner also restarts the waiting time
	public bool recommended = true;
	public bool modJoiners = true;
	// "maximum population" (like Dynamic Population): the colony size the storyteller aims for
	public bool customPop = false;
	public int popTarget = 13;          // soft cap – the storyteller stops wanting more people here
	public int popMax = 18;             // critical – above this it actively works against growth      // events from other mods in which someone joins
	public HashSet<string> learned = new HashSet<string>();   // events seen bringing a new colonist
	public HashSet<string> allowed = new HashSet<string>();   // events the player lets through
	public HashSet<string> learnedQuests = new HashSet<string>();   // 0.1.3: quests seen bringing a new colonist

	public override void ExposeData()
	{
		Scribe_Values.Look(ref enabled, "enabled", true);
		Scribe_Values.Look(ref firstDay, "firstDay", 10);
		Scribe_Values.Look(ref daysBetween, "daysBetween", 8);
		Scribe_Values.Look(ref cap, "cap", 0);
		Scribe_Values.Look(ref popIntent, "popIntent", 0.5f);
		Scribe_Values.Look(ref wanderers, "wanderers", true);
		Scribe_Values.Look(ref questJoiners, "questJoiners", true);
		Scribe_Values.Look(ref creepJoiners, "creepJoiners", true);
		Scribe_Values.Look(ref strangerInBlack, "strangerInBlack", false);
		Scribe_Values.Look(ref recruitsCount, "recruitsCount", true);
		Scribe_Values.Look(ref recommended, "recommended", true);
		Scribe_Values.Look(ref modJoiners, "modJoiners", true);
		Scribe_Values.Look(ref customPop, "customPop", false);
		Scribe_Values.Look(ref popTarget, "popTarget", 13);
		Scribe_Values.Look(ref popMax, "popMax", 18);
		Scribe_Collections.Look(ref learned, "learned", LookMode.Value);
		Scribe_Collections.Look(ref allowed, "allowed", LookMode.Value);
		Scribe_Collections.Look(ref learnedQuests, "learnedQuests", LookMode.Value);
		learnedQuests ??= new HashSet<string>();
		learned ??= new HashSet<string>();
		allowed ??= new HashSet<string>();
	}
}

public class SlowGrowthMod : Mod
{
	public static SGSettings S;
	public const string Version = "0.1.3";
	private static List<string> rec;

	public SlowGrowthMod(ModContentPack content) : base(content)
	{
		S = GetSettings<SGSettings>();
		MUI.Init(new SGSettings(), null, "recommended", "learned", "allowed", "learnedQuests");
		if (S.recommended) MUI.ApplyAll(S);
		var h = new Harmony("bolek98.slowgrowth");
		h.PatchAll();
		MethodInfo pop = AccessTools.Method(typeof(StorytellerUtilityPopulation), "CalculatePopulationIntent");
		if (pop != null) h.Patch(pop, postfix: new HarmonyMethod(typeof(Patches), nameof(Patches.PopIntent)));
		MUI.PatchWindow(h, typeof(SlowGrowthMod));
		MNews.Register(content, Version);
		Log.Message("[Slow Growth] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "Slow Growth";

	private static readonly (string key, int t, int m)[] Presets =
	{
		("Hermit", 2, 4), ("Small", 6, 10), ("Normal", 13, 18), ("Large", 22, 32), ("Huge", 40, 60)
	};

	private static void Check(Listing_Standard l, string key, ref bool v, string field)
		=> MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

	public override void DoSettingsWindowContents(Rect r)
	{
		var s = S;
		MUI.Version(r, "v" + Version + " · Bolek98");
		rec ??= new List<string> { "SG_Rec1".Translate(), "SG_Rec2".Translate(), "SG_Rec3".Translate() };
		float y = r.y;
		y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), s, s.recommended, v => { s.recommended = v; if (v) MUI.ApplyAll(s); }, rec) + 6f;
		if (s.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
		// where the current game stands
		if (Current.ProgramState == ProgramState.Playing)
		{
			GUI.color = MUI.Accent;
			Widgets.Label(new Rect(r.x, y, r.width, 26f), Gate.StatusLine());
			GUI.color = Color.white;
			y += 28f;
		}
		Rect area = new Rect(r.x, y, r.width, r.yMax - y);
		var l = new Listing_Standard();
		MUI.BeginLock(s.recommended);
		try
		{
			l.Begin(area);
			l.ColumnWidth = (area.width - 24f) / 2f;
			Check(l, "SG_Enabled", ref s.enabled, "enabled");
			s.firstDay = Mathf.RoundToInt(MUI.Slider(l, "SG_FirstDay".Translate(s.firstDay), s.firstDay, 0f, 60f, "SG_FirstDay_Desc".Translate(), s, "firstDay"));
			s.daysBetween = Mathf.RoundToInt(MUI.Slider(l, "SG_Between".Translate(s.daysBetween), s.daysBetween, 0f, 30f, "SG_Between_Desc".Translate(), s, "daysBetween"));
			string capTxt = s.cap <= 0 ? (string)"SG_NoCap".Translate() : s.cap.ToString();
			s.cap = Mathf.RoundToInt(MUI.Slider(l, "SG_Cap".Translate(capTxt), s.cap, 0f, 30f, "SG_Cap_Desc".Translate(), s, "cap"));
			s.popIntent = Mathf.Round(MUI.Slider(l, "SG_Pop".Translate(s.popIntent.ToStringPercent()), s.popIntent, 0f, 1f, "SG_Pop_Desc".Translate(), s, "popIntent") * 20f) / 20f;
			MUI.Section(l, "SG_SecPop".Translate());
			Check(l, "SG_CustomPop", ref s.customPop, "customPop");
			if (s.customPop)
			{
				Rect pr = l.GetRect(28f);
				float bw = pr.width / Presets.Length;
				for (int i = 0; i < Presets.Length; i++)
				{
					var pz = Presets[i];
					bool cur = s.popTarget == pz.t && s.popMax == pz.m;
					if (cur) Widgets.DrawHighlightSelected(new Rect(pr.x + i * bw, pr.y, bw - 4f, pr.height));
					if (Widgets.ButtonText(new Rect(pr.x + i * bw, pr.y, bw - 4f, pr.height), ("SG_Preset_" + pz.key).Translate()))
					{ s.popTarget = pz.t; s.popMax = pz.m; }
				}
				l.Gap(4f);
				s.popTarget = Mathf.RoundToInt(MUI.Slider(l, "SG_PopTarget".Translate(s.popTarget), s.popTarget, 1f, 60f, "SG_PopTarget_Desc".Translate(), s, "popTarget"));
				s.popMax = Mathf.Max(s.popTarget + 1, Mathf.RoundToInt(MUI.Slider(l, "SG_PopMax".Translate(s.popMax), s.popMax, 2f, 80f, "SG_PopMax_Desc".Translate(), s, "popMax")));
			}
			l.NewColumn();
			MUI.Section(l, "SG_SecWho".Translate());
			Check(l, "SG_Wanderers", ref s.wanderers, "wanderers");
			Check(l, "SG_Quests", ref s.questJoiners, "questJoiners");
			if (ModsConfig.AnomalyActive) Check(l, "SG_Creep", ref s.creepJoiners, "creepJoiners");
			Check(l, "SG_Stranger", ref s.strangerInBlack, "strangerInBlack");
			Check(l, "SG_ModJoiners", ref s.modJoiners, "modJoiners");
			Check(l, "SG_Recruits", ref s.recruitsCount, "recruitsCount");
			l.End();
		}
		finally { MUI.EndLock(area); }
		float top = s.customPop ? 470f : 360f;
		DrawList(new Rect(r.x, area.y + top, r.width, r.yMax - area.y - top));
	}

	private Vector2 scroll;
	private void DrawList(Rect r)
	{
		if (r.height < 60f) return;
		List<IncidentDef> list = DefDatabase<IncidentDef>.AllDefsListForReading.Where(d => { var k = Gate.KindOf(d); return k != JoinKind.None && k != JoinKind.Always; })
			.OrderBy(d => d.modContentPack?.IsOfficialMod == true ? 0 : 1).ThenBy(d => d.label ?? d.defName).ToList();
		List<QuestScriptDef> qs = DefDatabase<QuestScriptDef>.AllDefsListForReading.Where(q => S.learnedQuests.Contains(q.defName)).ToList();
		Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "SG_ListHead".Translate(list.Count + qs.Count));
		Rect outR = new Rect(r.x, r.y + 26f, r.width, r.height - 26f);
		Rect view = new Rect(0f, 0f, outR.width - 18f, (list.Count + qs.Count) * 28f);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			IncidentDef d = list[i];
			Rect row = new Rect(0f, i * 28f, view.width, 26f);
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			bool wait = !S.allowed.Contains(d.defName);
			bool was = wait;
			Widgets.Checkbox(new Vector2(row.x + 2f, row.y + 1f), ref wait);
			if (wait != was) { if (wait) S.allowed.Remove(d.defName); else S.allowed.Add(d.defName); }
			Widgets.Label(new Rect(row.x + 32f, row.y + 2f, 300f, 24f), ((string)(d.LabelCap.NullOrEmpty() ? d.defName : d.LabelCap.ToString())).Truncate(300f));
			GUI.color = MUI.Accent;
			Widgets.Label(new Rect(row.x + 340f, row.y + 2f, 190f, 24f), ("SG_Kind_" + Gate.KindOf(d)).Translate());
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(row.x + 536f, row.y + 2f, row.width - 536f, 24f), (d.modContentPack?.Name ?? "").Truncate(row.width - 536f));
			GUI.color = Color.white;
		}
		for (int j = 0; j < qs.Count; j++)
		{
			QuestScriptDef q = qs[j];
			int i = list.Count + j;
			Rect row = new Rect(0f, i * 28f, view.width, 26f);
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			bool wait = !S.allowed.Contains(q.defName);
			bool was = wait;
			Widgets.Checkbox(new Vector2(row.x + 2f, row.y + 1f), ref wait);
			if (wait != was) { if (wait) S.allowed.Remove(q.defName); else S.allowed.Add(q.defName); }
			Widgets.Label(new Rect(row.x + 32f, row.y + 2f, 300f, 24f), q.defName.Truncate(300f));
			GUI.color = MUI.Accent;
			Widgets.Label(new Rect(row.x + 340f, row.y + 2f, 190f, 24f), "SG_Kind_Quest".Translate());
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(row.x + 536f, row.y + 2f, row.width - 536f, 24f), (q.modContentPack?.Name ?? "").Truncate(row.width - 536f));
			GUI.color = Color.white;
		}
		Widgets.EndScrollView();
	}
}

// when did the last newcomer join (saved with the game)
public class SGComp : GameComponent
{
	public int lastJoinTick = -1;
	public HashSet<int> known;   // 0.1.3: colonists already seen – a new one is a newcomer, however he got here
	public SGComp(Game g) { }
	public static SGComp Get() => Current.Game?.GetComponent<SGComp>();

	public override void ExposeData()
	{
		Scribe_Values.Look(ref lastJoinTick, "lastJoinTick", -1);
		Scribe_Collections.Look(ref known, "known", LookMode.Value);
	}

	private static IEnumerable<Pawn> Colonists()
		=> PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep.Where(p => p.RaceProps.Humanlike);

	// some mods make a new colonist already in the player's faction (no faction change) – look every few seconds
	public override void GameComponentTick()
	{
		if (Find.TickManager.TicksGame % 250 != 17) return;
		var s = SlowGrowthMod.S;
		if (s == null || !s.enabled) return;
		try
		{
			if (known == null)
			{
				known = new HashSet<int>(Colonists().Select(p => p.thingIDNumber));
				return;
			}
			foreach (Pawn p in Colonists())
				if (!known.Contains(p.thingIDNumber))
					Notice(p, null);
		}
		catch (Exception e) { Log.WarningOnce("[Slow Growth] " + e.Message, 0x5160a1); }
	}

	// a humanlike just became ours
	public void Notice(Pawn p, IncidentDef ev)
	{
		known ??= new HashSet<int>(Colonists().Where(x => x != p).Select(x => x.thingIDNumber));
		if (!known.Add(p.thingIDNumber)) return;
		if (Current.ProgramState != ProgramState.Playing || Find.TickManager.TicksGame < 600) return;
		if (!Joins.Counts(p)) return;
		lastJoinTick = Find.TickManager.TicksGame;
		Joins.Learn(p, ev);
	}
}

public static class Joins
{
	// pawn -> the event that spawned it (only this session; enough for learning)
	public static readonly Dictionary<int, IncidentDef> spawnedBy = new Dictionary<int, IncidentDef>();

	public static bool Counts(Pawn p)
	{
		if (p.RaceProps == null || !p.RaceProps.Humanlike) return false;
		if (p.IsSlave || p.IsQuestLodger()) return false;
		if (p.guest != null && p.guest.IsPrisoner) return false;
		if (p.DevelopmentalStage.Baby() || p.ageTracker.AgeBiologicalYears < 1) return false;   // born here
		if (!SlowGrowthMod.S.recruitsCount && p.records != null && p.records.GetValue(RecordDefOf.TimeAsPrisoner) > 0f) return false;
		return true;
	}

	public static void Learn(Pawn p, IncidentDef ev)
	{
		var s = SlowGrowthMod.S;
		if (p.records != null && p.records.GetValue(RecordDefOf.TimeAsPrisoner) > 0f) return;   // recruited, not brought by an event
		ev ??= Patch_TryExecute.running;
		if (ev == null) spawnedBy.TryGetValue(p.thingIDNumber, out ev);
		if (ev != null)
		{
			if (Gate.KindOf(ev) == JoinKind.None && !(typeof(IncidentWorker_GameEndedWanderersJoin).IsAssignableFrom(ev.workerClass)) && s.learned.Add(ev.defName))
			{
				Gate.Forget(ev);
				Save();
				Log.Message("[Slow Growth] learned: event '" + ev.defName + "' brings new colonists – it waits from now on.");
			}
			return;
		}
		// a quest that brought him (its own way, not the vanilla "join" step)
		Quest q = Find.QuestManager?.QuestsListForReading?.LastOrDefault(x => x.root != null && x.QuestLookTargets.Any(t => t.Thing == p));
		if (q != null && !Gate.QuestJoins(q.root) && s.learnedQuests.Add(q.root.defName))
		{
			Save();
			Log.Message("[Slow Growth] learned: quest '" + q.root.defName + "' brings new colonists – it waits from now on.");
		}
	}

	private static void Save() => LoadedModManager.GetMod<SlowGrowthMod>()?.WriteSettings();
}

public enum JoinKind { None, Wanderer, Quest, Creep, Stranger, Always, Mod }

public static class Gate
{
	private static readonly Dictionary<IncidentDef, JoinKind> incidents = new Dictionary<IncidentDef, JoinKind>();
	private static readonly Dictionary<QuestScriptDef, bool> quests = new Dictionary<QuestScriptDef, bool>();

	public static int Colonists => PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists_NoCryptosleep.Count(p => p.RaceProps.Humanlike);

	public static float Today => Find.TickManager.TicksGame / (float)GenDate.TicksPerDay;

	// is a newcomer allowed right now? (why not → reason)
	public static bool Open(out string why)
	{
		why = null;
		var s = SlowGrowthMod.S;
		if (s == null || !s.enabled || Current.Game == null) return true;
		float day = Today;
		if (day < s.firstDay) { why = "SG_WhyFirst".Translate((s.firstDay - day).ToString("0.0")); return false; }
		int last = SGComp.Get()?.lastJoinTick ?? -1;
		if (last >= 0 && s.daysBetween > 0)
		{
			float since = (Find.TickManager.TicksGame - last) / (float)GenDate.TicksPerDay;
			if (since < s.daysBetween) { why = "SG_WhyBetween".Translate((s.daysBetween - since).ToString("0.0")); return false; }
		}
		if (s.cap > 0 && Colonists >= s.cap) { why = "SG_WhyCap".Translate(s.cap); return false; }
		return true;
	}

	public static string StatusLine()
	{
		int n = Colonists;
		return Open(out string why) ? "SG_StatusOpen".Translate(n) : "SG_StatusClosed".Translate(n, why);
	}

	public static bool Gated(IncidentDef def)
	{
		if (def != null && SlowGrowthMod.S.allowed.Contains(def.defName)) return false;
		return Gated(KindOf(def));
	}

	public static bool Gated(JoinKind k)
	{
		var s = SlowGrowthMod.S;
		switch (k)
		{
			case JoinKind.Wanderer: return s.wanderers;
			case JoinKind.Quest: return s.questJoiners;
			case JoinKind.Creep: return s.creepJoiners;
			case JoinKind.Stranger: return s.strangerInBlack;
			case JoinKind.Mod: return s.modJoiners;
			default: return false;
		}
	}

	private static readonly string[] JoinWords = { "Join", "Wanderer", "Refugee", "Recruit", "Survivor", "Castaway", "Newcomer", "Volunteer", "Defector", "Deserter", "Orphan", "Foundling", "Stray", "Lost" };

	public static void Forget(IncidentDef def) => incidents.Remove(def);

	public static JoinKind KindOf(IncidentDef def)
	{
		if (def == null) return JoinKind.None;
		if (incidents.TryGetValue(def, out JoinKind k)) return k;
		k = Classify(def);
		incidents[def] = k;
		return k;
	}

	private static JoinKind Classify(IncidentDef def)
	{
		JoinKind k = ClassifyVanilla(def);
		if (k != JoinKind.None) return k;
		if (SlowGrowthMod.S.learned.Contains(def.defName)) return JoinKind.Mod;
		// events from other mods: the name says somebody joins, and it is no raid, visit or trader
		Type w = def.workerClass;
		if (w == null || typeof(IncidentWorker_RaidEnemy).IsAssignableFrom(w) || typeof(IncidentWorker_NeutralGroup).IsAssignableFrom(w)
		    || w.Name.IndexOf("Raid", StringComparison.OrdinalIgnoreCase) >= 0 || w.Name.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0
		    || w.Name.IndexOf("Visitor", StringComparison.OrdinalIgnoreCase) >= 0 || w.Name.IndexOf("Caravan", StringComparison.OrdinalIgnoreCase) >= 0)
			return JoinKind.None;
		if (def.modContentPack != null && def.modContentPack.IsOfficialMod) return JoinKind.None;
		// the event says it adds people to the colony (the storyteller uses this)
		if (def.populationEffect != IncidentPopulationEffect.None) return JoinKind.Mod;
		if (def.pawnKind != null && def.pawnKind.RaceProps?.Humanlike == true && def.pawnKind.defaultFactionDef?.isPlayer == true) return JoinKind.Mod;
		string text = def.defName + " " + w.Name;
		if (JoinWords.Any(x => text.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0)) return JoinKind.Mod;
		return JoinKind.None;
	}

	private static JoinKind ClassifyVanilla(IncidentDef def)
	{
		JoinKind k;
		k = JoinKind.None;
		Type w = def.workerClass;
		string dn = def.defName ?? "";
		if (w != null)
		{
			if (typeof(IncidentWorker_GameEndedWanderersJoin).IsAssignableFrom(w)) k = JoinKind.Always;   // everybody is gone – always let them come
			else if (dn.IndexOf("StrangerInBlack", StringComparison.OrdinalIgnoreCase) >= 0) k = JoinKind.Stranger;
			else if (typeof(IncidentWorker_WandererJoin).IsAssignableFrom(w)) k = JoinKind.Wanderer;
			else if (w.Name.IndexOf("CreepJoiner", StringComparison.OrdinalIgnoreCase) >= 0) k = JoinKind.Creep;
			else if (w.Name.IndexOf("WandererJoin", StringComparison.OrdinalIgnoreCase) >= 0 || w.Name.IndexOf("Joiner", StringComparison.OrdinalIgnoreCase) >= 0) k = JoinKind.Wanderer;
			else if (typeof(IncidentWorker_GiveQuest).IsAssignableFrom(w) && def.questScriptDef != null && QuestJoins(def.questScriptDef)) k = JoinKind.Quest;
		}
		return k;
	}

	// does somebody join the colony in this quest? (any QuestNode_JoinPlayer in its tree)
	public static bool QuestJoins(QuestScriptDef q)
	{
		if (q == null) return false;
		if (q.rootIncreasesPopulation) return true;   // the game itself marks quests that add people
		if (quests.TryGetValue(q, out bool b)) return b;
		quests[q] = false;   // guards against loops
		try { b = Walk(q.root, new HashSet<object>(), 0); }
		catch { b = false; }
		quests[q] = b;
		return b;
	}

	private static bool Walk(object node, HashSet<object> seen, int depth)
	{
		if (node == null || depth > 40 || !seen.Add(node)) return false;
		if (node is QuestNode_JoinPlayer) return true;
		if (node is QuestScriptDef sub) return QuestJoins(sub);
		if (!(node is QuestNode)) return false;
		foreach (FieldInfo f in AccessTools.GetDeclaredFields(node.GetType()).Concat(BaseFields(node.GetType())))
		{
			if (f.IsStatic) continue;
			object v = f.GetValue(node);
			if (v is QuestNode || v is QuestScriptDef)
			{
				if (Walk(v, seen, depth + 1)) return true;
			}
			else if (v is IEnumerable list && !(v is string))
			{
				foreach (object o in list)
					if ((o is QuestNode || o is QuestScriptDef) && Walk(o, seen, depth + 1)) return true;
			}
		}
		return false;
	}

	private static IEnumerable<FieldInfo> BaseFields(Type t)
	{
		for (Type b = t.BaseType; b != null && b != typeof(object) && b != typeof(QuestNode); b = b.BaseType)
			foreach (FieldInfo f in AccessTools.GetDeclaredFields(b))
				yield return f;
	}
}

public static class Patches
{
	// the storyteller wants fewer people – it then fires fewer "someone joins" events
	public static void PopIntent(StorytellerDef def, float curPop, float popAdaptation, ref float __result)
	{
		var s = SlowGrowthMod.S;
		if (s == null || !s.enabled) return;
		if (s.customPop)
		{
			// own curve: wants people up to the target, nobody at the target, pushes back up to the maximum
			float t = Mathf.Max(1, s.popTarget), m = Mathf.Max(t + 1f, s.popMax);
			float v;
			if (curPop <= t) v = Mathf.Lerp(1.3f, 0f, curPop / t);
			else if (curPop <= m) v = Mathf.Lerp(0f, -1f, (curPop - t) / (m - t));
			else v = -1f - Mathf.Min(1f, (curPop - m) / Mathf.Max(2f, m * 0.25f));
			if (v > 0f && def?.populationIntentFactorFromPopAdaptDaysCurve != null)
				v *= def.populationIntentFactorFromPopAdaptDaysCurve.Evaluate(popAdaptation);
			__result = v;
		}
		if (__result > 0f) __result *= Mathf.Clamp01(s.popIntent);
	}
}

[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.CanFireNow))]
public static class Patch_CanFire
{
	public static void Postfix(IncidentWorker __instance, ref bool __result)
	{
		if (!__result) return;
		if (Gate.Gated(__instance.def) && !Gate.Open(out _)) __result = false;
	}
}

// remembers which event is running – if somebody joins during it, it is a "joiner" event from now on
[HarmonyPatch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute))]
public static class Patch_TryExecute
{
	[ThreadStatic] public static IncidentDef running;

	public static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result, out IncidentDef __state)
	{
		__state = running;
		running = __instance.def;
		// forced through without CanFireNow (another mod, a quest) – still wait
		if (parms != null && !parms.forced && Gate.Gated(__instance.def) && !Gate.Open(out _))
		{
			running = __state;
			__result = false;
			return false;
		}
		return true;
	}

	public static void Postfix(IncidentDef __state) => running = __state;
}

// random quests: a quest where somebody joins cannot be picked while the gate is closed
[HarmonyPatch(typeof(QuestScriptDef), nameof(QuestScriptDef.CanRun), new[] { typeof(Slate), typeof(IIncidentTarget) })]
public static class Patch_QuestCanRun
{
	public static void Postfix(QuestScriptDef __instance, ref bool __result)
	{
		var s = SlowGrowthMod.S;
		if (!__result || s == null || !s.enabled || !s.questJoiners) return;
		if (Current.ProgramState != ProgramState.Playing) return;
		if (s.allowed.Contains(__instance.defName)) return;
		if ((Gate.QuestJoins(__instance) || s.learnedQuests.Contains(__instance.defName)) && !Gate.Open(out _)) __result = false;
	}
}

// somebody joined – the waiting starts again
[HarmonyPatch(typeof(Pawn), nameof(Pawn.SetFaction))]
public static class Patch_SetFaction
{
	public static void Prefix(Pawn __instance, Faction newFaction, out bool __state)
	{
		__state = newFaction != null && newFaction.IsPlayer && __instance.Faction != newFaction;
	}

	public static void Postfix(Pawn __instance, bool __state)
	{
		if (!__state || Current.ProgramState != ProgramState.Playing) return;
		if (__instance.RaceProps == null || !__instance.RaceProps.Humanlike) return;
		if (__instance.IsSlave || (__instance.guest != null && __instance.guest.IsPrisoner)) return;
		SGComp.Get()?.Notice(__instance, Patch_TryExecute.running);
	}
}

// a pawn spawned during an event – remember which one (for pawns made straight into the player's faction)
[HarmonyPatch(typeof(Pawn), nameof(Pawn.SpawnSetup))]
public static class Patch_PawnSpawn
{
	public static void Postfix(Pawn __instance, bool respawningAfterLoad)
	{
		if (respawningAfterLoad || Patch_TryExecute.running == null || __instance.RaceProps?.Humanlike != true) return;
		Joins.spawnedBy[__instance.thingIDNumber] = Patch_TryExecute.running;
		if (__instance.Faction != null && __instance.Faction.IsPlayer && !__instance.IsSlave && !(__instance.guest?.IsPrisoner ?? false))
			SGComp.Get()?.Notice(__instance, Patch_TryExecute.running);
	}
}
