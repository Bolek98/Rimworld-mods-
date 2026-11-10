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
	public bool raidSafeAnimals = true;   // 1.1.0: tame animals go into the safe area too
	// 1.2.0: groups, your orders first, growing step by step
	public float strShortage = 1f;
	public float strWork = 1f;
	public float strComfort = 1f;
	public bool respectOrders = true;
	public bool escalate = true;

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
		Scribe_Values.Look(ref raidSafeAnimals, "raidSafeAnimals", true);
		Scribe_Values.Look(ref strShortage, "strShortage", 1f);
		Scribe_Values.Look(ref strWork, "strWork", 1f);
		Scribe_Values.Look(ref strComfort, "strComfort", 1f);
		Scribe_Values.Look(ref respectOrders, "respectOrders", true);
		Scribe_Values.Look(ref escalate, "escalate", true);
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
		StewardApi.MapExpose += CrisisTracks.Expose;
		StewardApi.MapTick += CrisisTracks.Tick;
		AddButton();
		StewardApi.MapRemoved += CrisisRules.Forget;
		StewardApi.MapRemoved += SafeArea.Forget;
		StewardApi.GameStarted += () =>
		{
			CrisisRules.Reset();
			SafeArea.Reset();
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

	// 1.2.0: a core without the button hook (older Steward) must not stop this mod from loading
	private static void AddButton()
	{
		try { AddButtonNow(); }
		catch (Exception ex) { MLog.Warn("[Steward] crises: no Work tab button (update the Steward core): " + ex.Message); }
	}

	[MethodImpl(MethodImplOptions.NoInlining)]
	private static void AddButtonNow() => Dialog_Crises.Register();

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
		l.GapLine();
		l.Label("CF3C_Title".Translate());
		Dialog_Crises.Slider(l.GetRect(40f), CrisisGroup.Shortage, ref S.strShortage);
		Dialog_Crises.Slider(l.GetRect(40f), CrisisGroup.Work, ref S.strWork);
		Dialog_Crises.Slider(l.GetRect(40f), CrisisGroup.Comfort, ref S.strComfort);
		l.CheckboxLabeled("CF3C_Orders".Translate(), ref S.respectOrders, "CF3C_OrdersTip".Translate());
		l.CheckboxLabeled("CF3C_Escalation".Translate(), ref S.escalate, "CF3C_EscalationTip".Translate());
		l.GapLine();
		l.CheckboxLabeled("CF2C_Medical".Translate(), ref S.medicalResponse, "CF2C_MedicalTip".Translate());
		l.CheckboxLabeled("CF2C_Fire".Translate(), ref S.fireResponse, "CF2C_FireTip".Translate());
		l.CheckboxLabeled("CF2C_Butcher".Translate(), ref S.butcherFirst, "CF2C_ButcherTip".Translate());
		l.CheckboxLabeled("CF2C_SafeArea".Translate(), ref S.raidSafeArea, "CF2C_SafeAreaTip".Translate());
		if (S.raidSafeArea)
			l.CheckboxLabeled("    " + "CF2C_SafeAnimals".Translate(), ref S.raidSafeAnimals, "CF2C_SafeAnimalsTip".Translate());
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
		s.raidSafeAnimals = B("raidSafeAnimals", s.raidSafeAnimals);
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
	public long safeSig;          // 1.1.2: what the safe area looked like when Steward last drew it
	public bool safePlayerOwned;  // 1.1.2: the player changed it – Steward keeps it as it is
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
		Scribe_Values.Look(ref s.safeSig, "cfSafeSig", 0L);
		Scribe_Values.Look(ref s.safePlayerOwned, "cfSafePlayerOwned", false);
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
				// 1.1.2: a crisis starts after an hour, but ends only after it has been gone for 6 hours
				// (food, hygiene, medicine went on and off every few minutes and every time the plan changed)
				bool starts = (found & ~comp.crisis) != 0;
				int wait = starts ? 2500 : ((comp.crisis & ~found & Instant) != 0 ? 2500 : 15000);
				if ((diff & Instant & found) != 0 || now - st.crisisSeenTick >= wait)
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
			if (now - st.autoFocusEval >= AutoFocus.EvalInterval && AutoFocus.Update(map, comp, MapDemand.ForFocus(map), force: false))
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
		CrisisBook book = CrisisTracks.Of(ctx.comp);
		foreach (CrisisTrack t in book.tracks)
			t.who.Clear();
		Crisis cur = ctx.comp.crisis;
		if (cur == Crisis.None)
			return;
		int now = Find.TickManager.TicksGame;
		var raiser = new GroupRaiser(ctx);
		var up = new List<(WorkTypeDef w, CrisisRules.Effect e, Crisis c, CrisisTrack t)>();
		foreach (Crisis c in CrisisRules.All)
		{
			if ((cur & c) == 0 || !CrisisRules.Table.TryGetValue(c, out var effects))
				continue;
			// 1.2.0: the card of this crisis (ignored / snoozed = nothing; a new one starts gently)
			CrisisTrack t = book.Find("c:" + c) ?? new CrisisTrack { key = "c:" + c, since = now };
			if (t.Waiting(now))
				continue;
			t.stage = CrisisTracks.StageOf(t, now);
			CrisisGroup g = t.Group;
			if (CrisisGroups.Strength(g) <= 0.01f)
				continue;
			foreach (var kv in effects)
				foreach (WorkTypeDef w in WorkMatch.Resolve(kv.Key))
				{
					// 1.0.3: urgent work (firefighting, urgent hauling, rescue …) is never lowered or
					// re-planned by a crisis – it stays on 1 for everybody
					WorkKind kind = WorkCatalog.Kind(w);
					if (kind == WorkKind.Emergency || kind == WorkKind.Hidden)
						continue;
					// 1.1.1: rearming turrets and refuelling (Work Split parts of hauling) never wait in a crisis
					if (kv.Value.urgency < 0f && (w.defName == "SW_Rearm" || w.defName == "SW_Refuel"))
						continue;
					if (kv.Value.urgency < 0f)
					{
						// 1.2.0: only lives and real shortages (after the first hour) make other work wait,
						// and never for the people you gave your own orders
						if (g == CrisisGroup.Work || g == CrisisGroup.Comfort || (g == CrisisGroup.Shortage && t.stage < 2))
							continue;
						foreach (Pawn p in ctx.pawns)
						{
							if (g != CrisisGroup.Life && raiser.IsReserved(p))
								continue;
							int v = ctx.Get(p, w);
							if (v > 0 && v <= 2)
								ctx.Set(p, w, ctx.Levels, "CF2C_TipCrisisDown", CrisisRules.Label(c), null, pin: true);
						}
					}
					else if (kv.Value.urgency > 0f)
						up.Add((w, kv.Value, c, t));
				}
		}
		// lives first, then shortages, work, comfort – each group takes only its share of the colony
		foreach (var u in up.OrderBy(x => (int)x.t.Group).ThenByDescending(x => x.e.urgency))
		{
			int baseCount = Math.Max(1, ctx.CountOn(u.w, 2));
			int count = u.e.min >= 99 ? ctx.pawns.Count : Math.Max(u.e.min, Mathf.CeilToInt(baseCount * Math.Max(1f, u.e.workers)));
			if (u.t.Group != CrisisGroup.Life)
				count = GroupRaiser.CountFor(u.t, count, ctx.pawns.Count);
			int level = GroupRaiser.LevelFor(u.t, u.e.urgency >= 0.3f ? 1 : 2);
			raiser.Raise(u.t, u.w, level, count, "CF2C_TipCrisis", CrisisRules.Label(u.c));
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
		// 1.2.0: your priority areas come first – the focus does not take those colonists
		var added = new List<Pawn>();
		foreach (Pawn p in CrisisTracks.Reserved(ctx.map))
			if (ctx.noRaise.Add(p))
				added.Add(p);
		try
		{
			foreach (WorkTypeDef w in ctx.types)
			{
				float d = AutoFocus.Delta(ctx.comp, w);
				if (d <= 0f)
					continue;
				int count = Math.Max(1, Mathf.CeilToInt(Math.Max(1, ctx.CountOn(w, 2)) * (1f + d)));
				ctx.Raise(w, 2, count, "CF2C_TipFocus", Styles.Label(f));
			}
		}
		finally
		{
			foreach (Pawn p in added)
				ctx.noRaise.Remove(p);
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
		CrisisBook book = CrisisTracks.Of(ctx.comp);
		int now = Find.TickManager.TicksGame;
		var raiser = new GroupRaiser(ctx);
		foreach (ActiveSituation s in st.situations.OrderBy(x => (int)CrisisGroups.Of(x.id)))
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
			CrisisTrack t = book.Find("s:" + s.id) ?? new CrisisTrack { key = "s:" + s.id, since = now };
			if (t.Waiting(now))
				continue;
			t.stage = CrisisTracks.StageOf(t, now);
			foreach (SitNeed n in s.needs)
			{
				int count = t.Group == CrisisGroup.Life ? n.workers : GroupRaiser.CountFor(t, n.workers, ctx.pawns.Count);
				raiser.Raise(t, n.work, GroupRaiser.LevelFor(t, n.level), count, "CF2C_TipSituation", s.Label + " (" + s.reason + ")");
			}
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
		// 1.2.2: only enemies that are really close (near a colonist or in the home area) count – enemies
		// wandering at the map edge or camping far away no longer send everybody in and out again
		bool threat = !stewardOff && s != null && s.raidSafeArea && map.IsPlayerHome
			&& ((comp.crisis & ThreatMask) != 0 || AnimalDanger(map)) && EnemyNear(map);
		// 1.1.1 / 1.2.2: a grace of two hours after the danger, and once inside at least an hour,
		// so the area does not flicker on and off (colonists walking in and out)
		int now = Find.TickManager.TicksGame;
		if (threat)
			lastThreat[map] = now;
		else if (!stewardOff && st.safeActive && lastThreat.TryGetValue(map, out int last) && now - last < 5000)
			threat = true;
		if (threat && !st.safeActive)
			safeSince[map] = now;
		else if (!threat && st.safeActive && !stewardOff && s != null && s.raidSafeArea
		         && safeSince.TryGetValue(map, out int since) && now - since < 2500)
			threat = true;
		if (threat && st.safeActive)
		{
			Keep(st, map);
			return;
		}
		if (threat && !st.safeActive)
		{
			Area_Allowed area = Build(map, true, st);
			if (area == null || area.TrueCount < 10)
				return;
			st.safePawns.Clear();
			st.safeOldArea.Clear();
			st.safeActive = true;
			foreach (Pawn p in Sheltered(map))
			{
				if (p.playerSettings == null || p.Drafted || p.GetLord() != null || !p.playerSettings.SupportsAllowedAreas)
					continue;
				st.safePawns.Add(p);
				Area old = p.playerSettings.AreaRestrictionInPawnCurrentMap;
				st.safeOldArea.Add(old == null || old == area ? -1 : old.ID);   // 1.0.8: never the safe area itself
				p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
				// 1.0.8: whoever is outside stops the job and goes in at once
				try
				{
					if (!area[p.Position] && p.jobs?.curJob != null && !p.Downed && !p.InMentalState)
						p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
				}
				catch { }
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
			n += Heal(map, area);
			st.safePawns.Clear();
			st.safeOldArea.Clear();
			st.safeActive = false;
			if (n > 0)
				Messages.Message("CF_SafeOff".Translate(n), MessageTypeDefOf.PositiveEvent, historical: false);
		}
		else if (!threat && !st.safeActive && Find.TickManager.TicksGame % 2500 < 250)
		{
			// 1.0.8: nobody may stay locked in the safe area after the danger (before, colonists could keep it
			// for good – then they did not hunt, fetch food from outside or cook)
			int n = Heal(map, Build(map, false));
			if (n > 0)
			{
				MLog.Info("[Steward] safe area: " + n + " pawns still had it after the danger – freed.");
				Messages.Message("CF_SafeOff".Translate(n), MessageTypeDefOf.PositiveEvent, historical: false);
			}
		}
	}

	private static readonly Dictionary<Map, int> lastThreat = new Dictionary<Map, int>();
	private static readonly Dictionary<Map, int> safeSince = new Dictionary<Map, int>();

	// 1.2.2: a hostile, not fleeing, not downed, visible pawn within 40 cells of a colonist or inside the home area
	public static bool EnemyNear(Map map)
	{
		try
		{
			var colonists = map.mapPawns.FreeColonistsSpawned;
			Area home = map.areaManager.Home;
			foreach (Verse.AI.IAttackTarget at in map.attackTargetsCache.TargetsHostileToColony)
			{
				if (!(at.Thing is Pawn e) || !e.Spawned || e.Dead || e.Downed || e.Fogged())
					continue;
				if (e.InMentalState && e.MentalStateDef == MentalStateDefOf.PanicFlee)
					continue;
				if (!GenHostility.IsActiveThreatToPlayer(e))
					continue;
				if (home != null && home[e.Position])
					return true;
				for (int i = 0; i < colonists.Count; i++)
					if (colonists[i].Position.DistanceToSquared(e.Position) <= 40 * 40)
						return true;
			}
			return false;
		}
		catch
		{
			return true;
		}
	}

	public static void Forget(Map m) { if (m != null) lastThreat.Remove(m); }

	public static void Reset() => lastThreat.Clear();

	// 1.1.1: manhunters and other mad animals count even alone and even while they sleep
	// (vanilla stops seeing a sleeping manhunter as a threat – the raid ended, the area was
	// lifted and the colonists walked out among the resting pack)
	public static bool AnimalDanger(Map map)
	{
		IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			if (p.Dead || p.Downed || p.Faction == Faction.OfPlayer || !p.RaceProps.Animal || !p.InMentalState)
				continue;
			MentalStateDef m = p.MentalStateDef;
			if (m == null || !m.IsAggro || p.Fogged())
				continue;
			if (m == MentalStateDefOf.Manhunter || m == MentalStateDefOf.ManhunterPermanent || p.BodySize >= 0.6f)
				return true;
		}
		return false;
	}

	// 1.1.1: while the danger lasts – whoever becomes free later (undrafted, left a lord, arrived)
	// gets the safe area too, and whoever is outside drops the job and goes in
	private static void Keep(CS st, Map map)
	{
		Area_Allowed area = Build(map, false);
		if (area == null)
			return;
		foreach (Pawn p in Sheltered(map))
		{
			if (p.playerSettings == null || p.Drafted || p.GetLord() != null || !p.playerSettings.SupportsAllowedAreas)
				continue;
			if (!st.safePawns.Contains(p))
			{
				Area old = p.playerSettings.AreaRestrictionInPawnCurrentMap;
				if (old == area)
					continue;   // the player gave it himself
				st.safePawns.Add(p);
				st.safeOldArea.Add(old == null ? -1 : old.ID);
				p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
			}
			else if (p.playerSettings.AreaRestrictionInPawnCurrentMap != area)
				continue;   // the player changed it during the attack – his choice
			try
			{
				if (area[p.Position] || p.Downed || p.InMentalState || p.jobs?.curJob == null)
					continue;
				Verse.AI.Job j = p.jobs.curJob;
				if (j.playerForced)
					continue;
				if (j.def == JobDefOf.Goto && j.targetA.IsValid && area[j.targetA.Cell])
					continue;   // already on the way in
				if (j.def == JobDefOf.Rescue || j.def == JobDefOf.Capture || j.def == JobDefOf.Flee || j.def == JobDefOf.FleeAndCower)
					continue;
				p.jobs.EndCurrentJob(Verse.AI.JobCondition.InterruptForced);
			}
			catch { }
		}
	}

	private static int Heal(Map map, Area_Allowed area)
	{
		if (area == null)
			return 0;
		int n = 0;
		foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).ToList())
		{
			if (p.playerSettings == null || p.playerSettings.AreaRestrictionInPawnCurrentMap != area)
				continue;
			p.playerSettings.AreaRestrictionInPawnCurrentMap = null;
			n++;
		}
		return n;
	}

	private static bool FollowsDraftedMaster(Pawn p)
	{
		Pawn m = p.playerSettings?.Master;
		return m != null && m.Drafted && p.playerSettings.followDrafted;
	}

	// 1.0.8: colonists and the colony's work mechs, robots and droids (not fighting mechs); 1.1.0: tame animals
	private static IEnumerable<Pawn> Sheltered(Map map)
	{
		foreach (Pawn p in map.mapPawns.FreeColonistsSpawned.ToList())
			yield return p;
		bool animals = CrisesMod.S == null || CrisesMod.S.raidSafeAnimals;
		foreach (Pawn p in map.mapPawns.SpawnedPawnsInFaction(Faction.OfPlayer).ToList())
		{
			// 1.1.0: tame animals too (before, only colonists and work mechs went in and the animals
			// wandered outside during an attack) – except those that follow a drafted master into battle
			if (p.RaceProps.Animal && animals && !p.Dead && !p.Downed && !p.Drafted && !FollowsDraftedMaster(p))
			{
				yield return p;
				continue;
			}
			if (p.Dead || p.IsColonist || p.RaceProps.Humanlike || p.RaceProps.Animal)
				continue;
			if (p.RaceProps.IsMechanoid && !p.RaceProps.IsWorkMech)
				continue;   // fighting mechs fight
			yield return p;
		}
	}

	// 1.1.2: a cheap fingerprint of the area's cells – a different one means the player painted it
	private static long Sig(Area a)
	{
		long sum = 17;
		foreach (IntVec3 c in a.ActiveCells)
			sum = sum * 31 + c.x * 1000 + c.z;
		return sum ^ ((long)a.TrueCount << 40);
	}

	private static Area_Allowed Build(Map map, bool rebuild, CS st = null)
	{
		string label = "CF_SafeAreaName".Translate();
		Area_Allowed a = map.areaManager.AllAreas.OfType<Area_Allowed>().FirstOrDefault(x => x.Label == label);
		bool fresh = false;
		if (a == null)
		{
			if (!rebuild || !map.areaManager.TryMakeNewAllowed(out a))
				return null;
			a.RenamableLabel = label;
			fresh = true;
		}
		// 1.1.2: Steward draws the safe area once; when the player edits it, his version is kept and only used
		// (delete the area and Steward draws a new one at the next danger)
		if (rebuild && st != null)
		{
			if (fresh)
				st.safePlayerOwned = false;
			else if (!st.safePlayerOwned && (st.safeSig == 0 || Sig(a) != st.safeSig))   // an area from before 1.1.2 is kept too
			{
				st.safePlayerOwned = true;
				MLog.Info("[Steward] safe area: edited by the player – kept as it is from now on.");
			}
			if (st.safePlayerOwned && a.TrueCount > 0)
				return a;
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
			if (st != null)
				st.safeSig = Sig(a);
		}
		return a;
	}
}
