using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace ColonyForeman;

// ===========================================================================
// Steward – Crises & Situations (add-on of the Steward core)
// Watches the map for crises (fire, raid, wounded, hunger …) and situations
// (a big harvest, a building backlog, carcasses to butcher …) and raises the
// best-suited colonists for that work while it lasts – inside the core's plan.
// ===========================================================================
public sealed class CrisesSettings : ModSettings
{
	public bool useCrisis = true;
	public long crisisOff;
	public bool useSituations = true;
	public List<string> situationOff = new List<string>();
	public bool crisisMessages = true;
	public bool balancedAutoFocus = true;
	public bool medicalResponse = true;
	public bool fireResponse = true;
	public bool butcherFirst = true;
	public bool raidSafeArea = false;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref useCrisis, "useCrisis", true);
		Scribe_Values.Look(ref crisisOff, "crisisOff", 0L);
		Scribe_Values.Look(ref useSituations, "useSituations", true);
		Scribe_Collections.Look(ref situationOff, "situationOff", LookMode.Value);
		Scribe_Values.Look(ref crisisMessages, "crisisMessages", true);
		Scribe_Values.Look(ref balancedAutoFocus, "balancedAutoFocus", true);
		Scribe_Values.Look(ref medicalResponse, "medicalResponse", true);
		Scribe_Values.Look(ref fireResponse, "fireResponse", true);
		Scribe_Values.Look(ref butcherFirst, "butcherFirst", true);
		Scribe_Values.Look(ref raidSafeArea, "raidSafeArea", false);
		situationOff ??= new List<string>();
	}
}

public sealed class CrisesMod : Mod
{
	public static CrisesSettings S;
	public static CrisesMod Instance;
	private Vector2 scroll;
	private float viewH = 1600f;

	public CrisesMod(ModContentPack content) : base(content)
	{
		Instance = this;
		bool fresh = !File.Exists(Migrate.SettingsFile(content.FolderName, GetType().Name));
		S = GetSettings<CrisesSettings>();
		if (fresh)
			Migrate.FromOld(S);
		new Harmony("misak.steward.crises").PatchAll(typeof(CrisesMod).Assembly);
		StewardApi.Register(new CrisisStep());
		StewardApi.Register(new FocusStep());
		StewardApi.Register(new SituationStep());
		StewardApi.MapExpose += CS.Expose;
		StewardApi.MapTick += CS.Tick;
		StewardApi.MapRemoved += CrisisRules.Forget;
		StewardApi.GameStarted += () =>
		{
			CrisisRules.Reset();
			MedicalResponse.ResetGame();
			ButcherFirstPatch.Reset();
			Kitchen.ResetFood();
		};
		StewardApi.WorkTabLine += c =>
		{
			var st = CS.Of(c);
			var parts = new List<string>();
			if (AutoFocus.Current(c) != AutoFocus.None)
				parts.Add(AutoFocus.LongLabel(c));
			if (st.situations.Count > 0)
				parts.Add(SituationRules.Summary(st.situations));
			return parts.Count == 0 ? null : string.Join("\n\n", parts);
		};
		MLog.Info("[Steward] Crises & Situations loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "CF2C_Name".Translate();

	// 1.0.3: one broken text (a modded work type without a label …) must not hide the whole list
	private static string Safe(Func<string> f, string fallback)
	{
		try
		{
			return f() ?? fallback;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] crises settings text failed for " + fallback + ": " + ex, ("cs" + fallback).GetHashCode());
			return fallback;
		}
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		Rect view = new Rect(0f, 0f, inRect.width - 18f, viewH);
		Widgets.BeginScrollView(inRect, ref scroll, view);
		var l = new Listing_Standard();
		l.Begin(view);
		l.maxOneColumn = true;   // no hidden second column when the list is taller than the view
		l.CheckboxLabeled("CF2C_UseCrisis".Translate(), ref S.useCrisis, "CF2C_UseCrisisTip".Translate());
		l.CheckboxLabeled("CF2C_UseSituations".Translate(), ref S.useSituations, "CF2C_UseSituationsTip".Translate());
		l.CheckboxLabeled("CF2C_AutoFocus".Translate(), ref S.balancedAutoFocus, "CF2C_AutoFocusTip".Translate());
		l.CheckboxLabeled("CF2C_Messages".Translate(), ref S.crisisMessages);
		l.CheckboxLabeled("CF2C_Medical".Translate(), ref S.medicalResponse, "CF2C_MedicalTip".Translate());
		l.CheckboxLabeled("CF2C_Fire".Translate(), ref S.fireResponse, "CF2C_FireTip".Translate());
		l.CheckboxLabeled("CF2C_Butcher".Translate(), ref S.butcherFirst, "CF2C_ButcherTip".Translate());
		l.CheckboxLabeled("CF2C_SafeArea".Translate(), ref S.raidSafeArea, "CF2C_SafeAreaTip".Translate());
		l.GapLine();
		if (S.useCrisis)
		{
			l.Label("CF2C_CrisesList".Translate());
			foreach (Crisis c in CrisisRules.All)
			{
				bool on = (S.crisisOff & (long)c) == 0;
				bool was = on;
				l.CheckboxLabeled("   " + Safe(() => CrisisRules.Label(c), c.ToString()), ref on, Safe(() => CrisisRules.Describe(c), ""));
				if (on != was)
					S.crisisOff = on ? S.crisisOff & ~(long)c : S.crisisOff | (long)c;
			}
			l.GapLine();
		}
		if (S.useSituations)
		{
			l.Label("CF2C_SituationsList".Translate());
			foreach (Situation s in SituationRules.All)
			{
				bool on = !S.situationOff.Contains(s.ToString());
				bool was = on;
				l.CheckboxLabeled("   " + Safe(() => SituationRules.Label(s), s.ToString()), ref on, Safe(() => ("CF_SitWhen_" + s).Translate() + "\n\n" + SituationRules.Describe(s), ""));
				if (on != was)
				{
					if (on) S.situationOff.Remove(s.ToString());
					else S.situationOff.Add(s.ToString());
				}
			}
		}
		viewH = l.CurHeight + 30f;
		l.End();
		Widgets.EndScrollView();
	}
}

// settings of Steward 1.x (all in one file) are taken over once
public static class Migrate
{
	public static string SettingsFile(string folder, string handle) => Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename($"Mod_{folder}_{handle}.xml"));

	public static Dictionary<string, string> Old()
	{
		var d = new Dictionary<string, string>();
		try
		{
			string path = SettingsFile("Steward", "ForemanMod");
			if (!File.Exists(path))
				return d;
			var doc = new XmlDocument();
			doc.Load(path);
			XmlNode root = doc.SelectSingleNode("SettingsBlock/ModSettings");
			if (root == null)
				return d;
			foreach (XmlNode n in root.ChildNodes)
				d[n.Name] = n.ChildNodes.Count > 1 || (n.FirstChild != null && n.FirstChild.NodeType == XmlNodeType.Element)
					? string.Join(",", n.ChildNodes.Cast<XmlNode>().Select(x => x.InnerText))
					: n.InnerText;
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] old settings could not be read: " + ex.Message);
		}
		return d;
	}

	public static void FromOld(CrisesSettings s)
	{
		var d = Old();
		if (d.Count == 0)
			return;
		bool B(string k, bool def) => d.TryGetValue(k, out string v) && bool.TryParse(v, out bool b) ? b : def;
		s.useCrisis = B("useCrisis", s.useCrisis);
		s.useSituations = B("useSituations", s.useSituations);
		s.crisisMessages = B("crisisMessages", s.crisisMessages);
		s.balancedAutoFocus = B("balancedAutoFocus", s.balancedAutoFocus);
		s.medicalResponse = B("medicalResponse", s.medicalResponse);
		s.fireResponse = B("fireResponse", s.fireResponse);
		s.butcherFirst = B("butcherFirst", s.butcherFirst);
		s.raidSafeArea = B("raidSafeArea", s.raidSafeArea);
		if (d.TryGetValue("crisisOff", out string co) && long.TryParse(co, out long l))
			s.crisisOff = l;
		if (d.TryGetValue("situationOff", out string so) && !so.NullOrEmpty())
			s.situationOff = so.Split(',').Where(x => !x.NullOrEmpty()).ToList();
		s.Write();
		MLog.Info("[Steward] Crises & Situations: settings taken over from Steward 1.x.");
	}
}

// ---- per-map state (saved inside the core's map record, same names as Steward 1.x) ----
public sealed class CS
{
	private static readonly ConditionalWeakTable<ForemanMapComponent, CS> table = new ConditionalWeakTable<ForemanMapComponent, CS>();

	public ForemanStyle autoFocus = ForemanStyle.Balanced;
	public int autoFocusSince;
	public string autoFocusReason;
	public int autoFocusEval = -99999;
	public int lastRaidTick = -999999;
	public List<ActiveSituation> situations = new List<ActiveSituation>();
	public List<string> situationNames = new List<string>();
	public bool safeActive;
	public List<Pawn> safePawns = new List<Pawn>();
	public List<int> safeOldArea = new List<int>();
	// runtime
	public Crisis? crisisSeen;
	public int crisisSeenTick;
	public int sitEval = -99999;
	public string sitSignature = "";
	public SituationRules.Round round;
	public int slowTold = -999999;

	public static CS Of(ForemanMapComponent c) => table.GetValue(c, _ => new CS());

	public HashSet<Situation> Before()
	{
		var set = new HashSet<Situation>(situations.Select(x => x.id));
		foreach (string n in situationNames ?? new List<string>())
			if (Enum.TryParse(n, out Situation s))
				set.Add(s);
		return set;
	}

	public static void Expose(ForemanMapComponent c)
	{
		CS s = Of(c);
		Scribe_Values.Look(ref s.autoFocus, "autoFocus", ForemanStyle.Balanced);
		Scribe_Values.Look(ref s.autoFocusSince, "autoFocusSince", 0);
		Scribe_Values.Look(ref s.autoFocusReason, "autoFocusReason");
		Scribe_Values.Look(ref s.lastRaidTick, "lastRaidTick", -999999);
		Scribe_Collections.Look(ref s.situationNames, "situations", LookMode.Value);
		Scribe_Values.Look(ref s.safeActive, "cfSafeActive", false);
		Scribe_Collections.Look(ref s.safePawns, "cfSafePawns", LookMode.Reference);
		Scribe_Collections.Look(ref s.safeOldArea, "cfSafeOldArea", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			s.situationNames ??= new List<string>();
			s.safePawns ??= new List<Pawn>();
			s.safeOldArea ??= new List<int>();
			for (int i = s.safePawns.Count - 1; i >= 0; i--)
				if (s.safePawns[i] == null)
				{
					s.safePawns.RemoveAt(i);
					if (i < s.safeOldArea.Count)
						s.safeOldArea.RemoveAt(i);
				}
		}
	}

	// crises that re-plan at once (the others must last a while first)
	private const Crisis Instant = Crisis.Fire | Crisis.Raid | Crisis.Medical | Crisis.Manhunters | Crisis.PrisonBreak | Crisis.DangerousBreak
		| Crisis.EntityEscape | Crisis.VacuumBreach | Crisis.ToxGas | Crisis.NoFirefighters | Crisis.Labor | Crisis.Siege | Crisis.Infestation;

	// every 250 ticks per map (from the core)
	public static void Tick(ForemanMapComponent comp)
	{
		Map map = comp.map;
		CS st = Of(comp);
		if (!comp.auto)
		{
			if (st.safeActive)
				SafeArea.Tick(comp, map, stewardOff: true);
			if (comp.crisis != Crisis.None)
				comp.crisis = Crisis.None;
			return;
		}
		int now = Find.TickManager.TicksGame;
		// crises
		try
		{
			long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
			Crisis found = CrisesMod.S.useCrisis ? MapDemand.Quick(map, 0).crisis : Crisis.None;
			double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
			if (ms > 40.0 && now - st.slowTold > 60000)
			{
				st.slowTold = now;
				MLog.Info("[Steward] crisis check took " + ms.ToString("0") + " ms");
			}
			Crisis diff = found ^ comp.crisis;
			if (diff == Crisis.None)
				st.crisisSeen = null;
			else
			{
				if (!st.crisisSeen.HasValue || st.crisisSeen.Value != found)
				{
					st.crisisSeen = found;
					st.crisisSeenTick = now;
				}
				if ((diff & Instant & found) != 0 || now - st.crisisSeenTick >= 2500)
				{
					Crisis before = comp.crisis;
					comp.crisis = found;
					st.crisisSeen = null;
					Tell(map, found & ~before, before & ~found);
					comp.AddHistory("CF2C_HistCrisis".Translate(found == Crisis.None ? (string)"CF2C_NoCrisis".Translate() : CrisisText.Label(found)));
					// only real danger re-plans at once; winter, low medicine, breakdowns … after the calm gap
					comp.RequestPlan(urgent: (diff & Instant) != 0);
				}
			}
			if ((comp.crisis & Crisis.Raid) != 0)
				st.lastRaidTick = now;
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] crisis check failed: " + ex, 0x5c0001);
		}
		// Balanced picks a focus by itself
		try
		{
			if (now - st.autoFocusEval >= AutoFocus.EvalInterval && AutoFocus.Update(map, comp, null, force: false))
				comp.RequestPlan(urgent: false);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] auto focus failed: " + ex.Message, 0x5c0002);
		}
		// situations
		if (st.round == null && now - st.sitEval >= SituationRules.EvalInterval)
		{
			st.sitEval = now;
			st.round = SituationRules.Active(comp) ? SituationRules.Start(map, comp.crisis, st.Before()) : null;
			if (st.round == null)
				st.situations = new List<ActiveSituation>();
		}
		bool roundDone = false;
		if (st.round != null)
		{
			try { roundDone = SituationRules.Step(st.round, 8); }
			catch (Exception ex) { MLog.WarnOnce("[Steward] situations failed: " + ex, 0x5c0007); st.round = null; }
		}
		if (roundDone)
		{
			try
			{
				List<ActiveSituation> found = st.round.Result;
				st.round = null;
				// 3.0.2: a new plan only when situations start or end (not when their numbers change)
				string sig = string.Join(",", found.Select(x => x.id.ToString()).OrderBy(x => x));
				bool changed = sig != st.sitSignature;
				if (changed)
					TellSituations(map, st, found);
				// the numbers are kept fresh for the next plan either way
				st.situations = found;
				st.situationNames = found.Select(x => x.id.ToString()).ToList();
				st.sitSignature = sig;
				if (changed)
					comp.RequestPlan(urgent: false);
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Steward] situations failed: " + ex, 0x5c0003);
			}
		}
		try { MedicalResponse.Tick(map); }
		catch (Exception ex) { MLog.WarnOnce("[Steward] medical response failed: " + ex.Message, 0x5c0004); }
		try { SafeArea.Tick(comp, map, false); }
		catch (Exception ex) { MLog.WarnOnce("[Steward] safe area failed: " + ex.Message, 0x5c0005); }
	}

	private static void Tell(Map map, Crisis started, Crisis ended)
	{
		if (!CrisesMod.S.crisisMessages || !map.IsPlayerHome)
			return;
		if (started != Crisis.None)
			Messages.Message("CF_CrisisStart".Translate(CrisisText.Label(started)), MessageTypeDefOf.CautionInput, historical: false);
		if (ended != Crisis.None)
			Messages.Message("CF_CrisisEnd".Translate(CrisisText.Label(ended)), MessageTypeDefOf.PositiveEvent, historical: false);
	}

	private static void TellSituations(Map map, CS st, List<ActiveSituation> found)
	{
		if (!CrisesMod.S.crisisMessages || !map.IsPlayerHome)
			return;
		HashSet<Situation> before = new HashSet<Situation>(st.situations.Select(x => x.id));
		var after = new HashSet<Situation>(found.Select(x => x.id));
		var started = found.Where(x => !before.Contains(x.id)).ToList();
		var ended = before.Where(x => !after.Contains(x)).ToList();
		if (started.Count > 0)
		{
			string text = string.Join("; ", started.Select(x =>
			{
				string t = x.Label + " (" + x.reason + ")";
				string staff = x.Staffing();
				return staff.NullOrEmpty() ? t : t + " → " + staff;
			}));
			Messages.Message("CF_SitStart".Translate(text), MessageTypeDefOf.NeutralEvent, historical: false);
		}
		if (ended.Count > 0)
			Messages.Message("CF_SitEnd".Translate(string.Join(", ", ended.Select(x => SituationRules.Label(x)))), MessageTypeDefOf.PositiveEvent, historical: false);
	}
}

// the fire response every second (its own little map component, nothing saved)
public sealed class CrisesMapTicker : MapComponent
{
	public CrisesMapTicker(Map map) : base(map)
	{
	}

	public override void MapComponentTick()
	{
		if (Find.TickManager.TicksGame % 60 != 0)
			return;
		ForemanMapComponent comp = ForemanMapComponent.For(map);
		if (comp == null || !comp.auto)
			return;
		try { FireResponse.Tick(map); }
		catch (Exception ex) { MLog.WarnOnce("[Steward] fire response failed: " + ex.Message, 0x5c0006); }
	}
}

// ---- plan steps -------------------------------------------------------------
// 300: every active crisis raises its work (and lowers what can wait)
public sealed class CrisisStep : IPlanStep
{
	public int Order => 300;
	public string Id => "crises.crisis";

	public void Apply(PlanContext ctx)
	{
		Crisis cur = ctx.comp.crisis;
		if (cur == Crisis.None)
			return;
		var up = new List<(WorkTypeDef w, CrisisRules.Effect e, Crisis c)>();
		foreach (Crisis c in CrisisRules.All)
		{
			if ((cur & c) == 0 || !CrisisRules.Table.TryGetValue(c, out var effects))
				continue;
			foreach (var kv in effects)
				foreach (WorkTypeDef w in WorkMatch.Resolve(kv.Key))
				{
					// 1.0.3: urgent work (firefighting, urgent hauling, rescue …) is never lowered or
					// re-planned by a crisis – it stays on 1 for everybody
					WorkKind kind = WorkCatalog.Kind(w);
					if (kind == WorkKind.Emergency || kind == WorkKind.Hidden)
						continue;
					if (kv.Value.urgency < 0f)
					{
						// what can wait: main people go to the lowest level for now
						foreach (Pawn p in ctx.pawns)
						{
							int v = ctx.Get(p, w);
							if (v > 0 && v <= 2)
								ctx.Set(p, w, ctx.Levels, "CF2C_TipCrisisDown", CrisisRules.Label(c), null, pin: true);
						}
					}
					else if (kv.Value.urgency > 0f)
						up.Add((w, kv.Value, c));
				}
		}
		foreach (var u in up)
		{
			int baseCount = Math.Max(1, ctx.CountOn(u.w, 2));
			int count = u.e.min >= 99 ? ctx.pawns.Count : Math.Max(u.e.min, Mathf.CeilToInt(baseCount * Math.Max(1f, u.e.workers)));
			int level = u.e.urgency >= 0.3f ? 1 : 2;
			ctx.Raise(u.w, level, count, "CF2C_TipCrisis", CrisisRules.Label(u.c));
		}
	}
}

// 320: Balanced picked a focus (Building, Survival …): its work gets more people
public sealed class FocusStep : IPlanStep
{
	public int Order => 320;
	public string Id => "crises.focus";

	public void Apply(PlanContext ctx)
	{
		ForemanStyle f = AutoFocus.Current(ctx.comp);
		if (f == AutoFocus.None)
			return;
		foreach (WorkTypeDef w in ctx.types)
		{
			float d = AutoFocus.Delta(ctx.comp, w);
			if (d <= 0f)
				continue;
			int count = Math.Max(1, Mathf.CeilToInt(Math.Max(1, ctx.CountOn(w, 2)) * (1f + d)));
			ctx.Raise(w, 2, count, "CF2C_TipFocus", Styles.Label(f));
		}
	}
}

// 350: every active situation gets the colonists it asks for
public sealed class SituationStep : IPlanStep
{
	public int Order => 350;
	public string Id => "crises.situations";

	public void Apply(PlanContext ctx)
	{
		CS st = CS.Of(ctx.comp);
		foreach (ActiveSituation s in st.situations)
		{
			if (s.id == Situation.Exhausted && s.pawns != null)
			{
				// tired colonists: nothing on priority 1 for now
				foreach (Pawn p in s.pawns.Where(ctx.plan.ContainsKey))
					foreach (WorkTypeDef w in ctx.plan[p].Keys.ToList())
					{
						int v = ctx.Get(p, w);
						if (v == 1 && WorkCatalog.Kind(w) != WorkKind.Emergency || v == 2)
							ctx.Set(p, w, 2, "CF2C_TipExhausted", null, null, pin: true);
					}
				continue;
			}
			foreach (SitNeed n in s.needs)
				ctx.Raise(n.work, n.level, n.workers, "CF2C_TipSituation", s.Label + " (" + s.reason + ")");
		}
	}
}

// ---- safe area during attacks (off by default) ---------------------------------------
public static class SafeArea
{
	public const Crisis ThreatMask = Crisis.Raid | Crisis.Manhunters | Crisis.Siege;

	public static void Tick(ForemanMapComponent comp, Map map, bool stewardOff)
	{
		CS st = CS.Of(comp);
		CrisesSettings s = CrisesMod.S;
		bool threat = !stewardOff && s != null && s.raidSafeArea && (comp.crisis & ThreatMask) != 0 && map.IsPlayerHome;
		if (threat && !st.safeActive)
		{
			Area_Allowed area = Build(map, true);
			if (area == null || area.TrueCount < 10)
				return;
			st.safePawns.Clear();
			st.safeOldArea.Clear();
			st.safeActive = true;
			foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
			{
				if (p.playerSettings == null || p.Drafted || p.GetLord() != null)
					continue;
				st.safePawns.Add(p);
				st.safeOldArea.Add(p.playerSettings.AreaRestrictionInPawnCurrentMap?.ID ?? -1);
				p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
			}
			Messages.Message("CF_SafeOn".Translate(st.safePawns.Count), MessageTypeDefOf.CautionInput, historical: false);
		}
		else if (!threat && st.safeActive)
		{
			Area_Allowed area = Build(map, false);
			int n = 0;
			for (int i = 0; i < st.safePawns.Count; i++)
			{
				Pawn p = st.safePawns[i];
				if (p?.playerSettings == null || p.Dead || p.MapHeld != map)
					continue;
				if (area != null && p.playerSettings.AreaRestrictionInPawnCurrentMap != area)
					continue;
				int id = i < st.safeOldArea.Count ? st.safeOldArea[i] : -1;
				p.playerSettings.AreaRestrictionInPawnCurrentMap = id < 0 ? null : map.areaManager.AllAreas.FirstOrDefault(a => a.ID == id);
				n++;
			}
			st.safePawns.Clear();
			st.safeOldArea.Clear();
			st.safeActive = false;
			if (n > 0)
				Messages.Message("CF_SafeOff".Translate(n), MessageTypeDefOf.PositiveEvent, historical: false);
		}
	}

	private static Area_Allowed Build(Map map, bool rebuild)
	{
		string label = "CF_SafeAreaName".Translate();
		Area_Allowed a = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => x.Label == label);
		if (a == null)
		{
			if (!rebuild || !map.areaManager.TryMakeNewAllowed(out a))
				return null;
			a.RenamableLabel = label;
		}
		if (rebuild)
		{
			foreach (IntVec3 c in a.ActiveCells.ToList())
				a[c] = false;
			foreach (IntVec3 c in map.areaManager.Home.ActiveCells)
			{
				if (!c.Roofed(map))
					continue;
				Room r = c.GetRoom(map);
				if (r != null && !r.PsychologicallyOutdoors && !r.TouchesMapEdge)
					a[c] = true;
			}
		}
		return a;
	}
}
