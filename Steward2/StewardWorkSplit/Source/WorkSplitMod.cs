using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace StewardWorkSplit;

public sealed class WorkSplitSettings : ModSettings
{
	public Dictionary<string, bool> on = new Dictionary<string, bool>();        // only parts the player switched
	public Dictionary<string, string> moves = new Dictionary<string, string>(); // advanced: work giver -> work type

	public bool IsOn(Part p) => on.TryGetValue(p.def, out bool v) ? v : p.onByDefault;

	public void Set(Part p, bool v)
	{
		if (v == p.onByDefault)
			on.Remove(p.def);
		else
			on[p.def] = v;
	}

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref on, "on", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref moves, "moves", LookMode.Value, LookMode.Value);
		on ??= new Dictionary<string, bool>();
		moves ??= new Dictionary<string, string>();
	}
}

public sealed class WorkSplitMod : Mod
{
	public static WorkSplitSettings Settings;
	public const string Version = "0.1.0";

	private enum Tab { Parts, Advanced }
	private static Tab tab = Tab.Parts;
	private static Vector2 scroll;
	private static float viewH = 600f;
	private static string search = "";

	public WorkSplitMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<WorkSplitSettings>();
		var h = new Harmony("misak.steward.worksplit");
		try
		{
			h.Patch(AccessTools.Method(typeof(DefGenerator), nameof(DefGenerator.GenerateImpliedDefs_PreResolve)),
				prefix: new HarmonyMethod(typeof(Split), nameof(Split.Prefix)),
				postfix: new HarmonyMethod(typeof(Split), nameof(Split.Postfix)));
			h.Patch(AccessTools.Method(typeof(Pawn_WorkSettings), nameof(Pawn_WorkSettings.EnableAndInitialize)),
				postfix: new HarmonyMethod(typeof(Seed), nameof(Seed.InitPostfix)));
		}
		catch (Exception e)
		{
			Log.Error("[Steward – Work Split] could not hook into the game: " + e);
		}
		MUI.PatchWindow(h, typeof(WorkSplitMod));
		MNews.Register(content, Version);
		Log.Message("[Steward – Work Split] v" + Version + " loaded.");
	}

	public override string SettingsCategory() => "SWS_Name".CanTranslate() ? (string)"SWS_Name".Translate() : "Steward – Work Split";

	// ---------------- helpers ----------------
	private static string WorkLabel(WorkTypeDef w) => w == null ? "?" : (w.labelShort.NullOrEmpty() ? w.LabelCap.ToString() : w.labelShort.CapitalizeFirst());

	private static string WorkLabel(string defName) => WorkLabel(DefDatabase<WorkTypeDef>.GetNamedSilentFail(defName)) is string s && s != "?" ? s : defName;

	private static string GiverLabel(WorkGiverDef g)
	{
		if (g == null)
			return "?";
		if (!g.label.NullOrEmpty())
			return g.label.CapitalizeFirst();
		if (!g.gerund.NullOrEmpty())
			return g.gerund.CapitalizeFirst();
		return g.defName;
	}

	private static bool Pending()
	{
		foreach (Part p in Split.Parts)
			if (Settings.IsOn(p) != Split.AppliedOn.Contains(p.def))
				return true;
		if (Settings.moves.Count != Split.AppliedMoves.Count)
			return true;
		foreach (var kv in Settings.moves)
			if (!Split.AppliedMoves.TryGetValue(kv.Key, out string v) || v != kv.Value)
				return true;
		return false;
	}

	// where a job goes with the parts as they are set now (without the advanced moves)
	private static string DefaultTarget(WorkGiverDef g)
	{
		if (!Split.OriginalType.TryGetValue(g.defName, out string orig))
			return g.workType?.defName;
		foreach (Part p in Split.Parts)
			if (p.parent == orig && Settings.IsOn(p) && p.givers.Contains(g.defName) && DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def) != null)
				return p.def;
		return orig;
	}

	private static void Muted(Rect r, string text)
	{
		GUI.color = MUI.Muted;
		Widgets.Label(r, text);
		GUI.color = Color.white;
	}

	// ---------------- window ----------------
	public override void DoSettingsWindowContents(Rect r)
	{
		MUI.Version(r, "v" + Version + " · Bolek98");
		var recs = new List<TabRecord>
		{
			new TabRecord("SWS_Tab_Parts".Translate(), () => { tab = Tab.Parts; scroll = Vector2.zero; }, tab == Tab.Parts),
			new TabRecord("SWS_Tab_Advanced".Translate(), () => { tab = Tab.Advanced; scroll = Vector2.zero; }, tab == Tab.Advanced),
		};
		TabDrawer.DrawTabs(new Rect(r.x, r.y + 32f, r.width, 1f), recs, 200f);
		Rect body = new Rect(r.x, r.y + 38f, r.width, r.height - 38f);
		Widgets.DrawMenuSection(body);
		body = body.ContractedBy(10f);
		if (Pending())
		{
			Rect bar = new Rect(body.x, body.y, body.width, 30f);
			MUI.Fill(bar, MUI.CardBg2, 4f);
			GUI.color = MUI.Warn;
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(bar, "SWS_Restart".Translate());
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			body.yMin += 36f;
		}
		if (tab == Tab.Parts)
			Parts(body);
		else
			Advanced(body);
	}

	private static void Parts(Rect r)
	{
		float y = r.y;
		float introH = Text.CalcHeight("SWS_Intro".Translate(), r.width);
		Muted(new Rect(r.x, y, r.width, introH), "SWS_Intro".Translate());
		y += introH + 4f;
		bool steward = ModsConfig.IsActive("misak.colonyforeman");
		string st = (steward ? "SWS_Steward" : "SWS_NoSteward").Translate();
		float sh = Text.CalcHeight(st, r.width);
		Muted(new Rect(r.x, y, r.width, sh), st);
		y += sh + 6f;

		float bw = 150f;
		if (Widgets.ButtonText(new Rect(r.x, y, bw, 28f), "SWS_Default".Translate()))
			Settings.on.Clear();
		if (Widgets.ButtonText(new Rect(r.x + bw + 8f, y, bw, 28f), "SWS_AllOn".Translate()))
			foreach (Part p in Split.Parts) Settings.Set(p, true);
		if (Widgets.ButtonText(new Rect(r.x + 2 * (bw + 8f), y, bw, 28f), "SWS_AllOff".Translate()))
			foreach (Part p in Split.Parts) Settings.Set(p, false);
		y += 36f;

		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		Rect view = new Rect(0f, 0f, outR.width - 18f, viewH);
		Widgets.BeginScrollView(outR, ref scroll, view);
		float cy = 0f;
		foreach (var group in Split.Parts.GroupBy(p => p.parent))
		{
			WorkTypeDef parent = DefDatabase<WorkTypeDef>.GetNamedSilentFail(group.Key);
			if (parent == null)
				continue;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(0f, cy, view.width, 32f), WorkLabel(parent));
			Text.Font = GameFont.Small;
			cy += 32f;
			MUI.Fill(new Rect(0f, cy, view.width, 1f), MUI.Border);
			cy += 4f;
			foreach (Part p in group)
			{
				WorkTypeDef c = DefDatabase<WorkTypeDef>.GetNamedSilentFail(p.def);
				if (c == null)
					continue;
				var jobs = p.givers.Select(n => DefDatabase<WorkGiverDef>.GetNamedSilentFail(n)).Where(g => g != null).Select(GiverLabel).ToList();
				string jobsText = jobs.Count > 0 ? (string)"SWS_Jobs".Translate(string.Join(", ", jobs)) : (string)"SWS_NoJobs".Translate();
				float jh = Text.CalcHeight(jobsText, view.width - 90f);
				Rect row = new Rect(0f, cy, view.width, 26f + jh + 6f);
				Widgets.DrawHighlightIfMouseover(row);
				if (!c.description.NullOrEmpty())
					TooltipHandler.TipRegion(row, c.description);
				bool on = Settings.IsOn(p);
				if (MUI.Switch(new Rect(row.x + 4f, row.y + 4f, 48f, 24f), on))
					Settings.Set(p, !on);
				Widgets.Label(new Rect(row.x + 64f, row.y + 3f, row.width - 70f, 26f), WorkLabel(c));
				Muted(new Rect(row.x + 64f, row.y + 26f, row.width - 70f, jh), jobsText);
				cy += row.height + 2f;
			}
			cy += 8f;
		}
		viewH = Mathf.Max(cy, outR.height);
		Widgets.EndScrollView();
	}

	private static List<WorkGiverDef> giverCache;

	private static void Advanced(Rect r)
	{
		float y = r.y;
		float introH = Text.CalcHeight("SWS_AdvIntro".Translate(), r.width);
		Muted(new Rect(r.x, y, r.width, introH), "SWS_AdvIntro".Translate());
		y += introH + 6f;
		Widgets.Label(new Rect(r.x, y + 3f, 90f, 26f), "SWS_Search".Translate());
		search = Widgets.TextField(new Rect(r.x + 90f, y, 260f, 28f), search ?? "");
		if (Settings.moves.Count > 0 && Widgets.ButtonText(new Rect(r.xMax - 200f, y, 200f, 28f), "SWS_ResetMoves".Translate()))
			Settings.moves.Clear();
		y += 36f;

		if (giverCache == null)
			giverCache = DefDatabase<WorkGiverDef>.AllDefsListForReading
				.Where(g => Split.OriginalType.ContainsKey(g.defName))
				.OrderByDescending(g => DefDatabase<WorkTypeDef>.GetNamedSilentFail(Split.OriginalType[g.defName])?.naturalPriority ?? 0)
				.ThenBy(g => Split.OriginalType[g.defName])
				.ThenBy(GiverLabel)
				.ToList();
		string q = (search ?? "").Trim().ToLowerInvariant();
		var list = giverCache.Where(g => q.Length == 0
			|| GiverLabel(g).ToLowerInvariant().Contains(q)
			|| g.defName.ToLowerInvariant().Contains(q)
			|| WorkLabel(Split.OriginalType[g.defName]).ToLowerInvariant().Contains(q)
			|| (g.modContentPack?.Name ?? "").ToLowerInvariant().Contains(q)).ToList();

		float rowH = 30f;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		Rect view = new Rect(0f, 0f, outR.width - 18f, Mathf.Max(outR.height, list.Count * rowH));
		Widgets.BeginScrollView(outR, ref scroll, view);
		float nameW = view.width * 0.36f, fromW = view.width * 0.24f;
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height)
				continue;
			WorkGiverDef g = list[i];
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			string mod = g.modContentPack == null || g.modContentPack.IsCoreMod ? "" : g.modContentPack.Name;
			Widgets.Label(new Rect(row.x + 4f, row.y + 3f, nameW - 8f, 26f), GiverLabel(g).Truncate(nameW - 8f));
			TooltipHandler.TipRegion(new Rect(row.x, row.y, nameW, rowH), g.defName + (mod.Length > 0 ? "\n" + mod : ""));
			Muted(new Rect(row.x + nameW, row.y + 3f, fromW - 8f, 26f), WorkLabel(Split.OriginalType[g.defName]).Truncate(fromW - 8f));
			bool moved = Settings.moves.TryGetValue(g.defName, out string to);
			string def = DefaultTarget(g);
			string btn = moved ? WorkLabel(to) : (string)"SWS_DefaultTo".Translate(WorkLabel(def));
			Rect b = new Rect(row.x + nameW + fromW, row.y + 1f, view.width - nameW - fromW - 4f, rowH - 4f);
			if (moved)
				GUI.color = MUI.Accent;
			if (Widgets.ButtonText(b, btn.Truncate(b.width - 12f)))
				Find.WindowStack.Add(new FloatMenu(Targets(g)));
			GUI.color = Color.white;
		}
		Widgets.EndScrollView();
	}

	private static List<FloatMenuOption> Targets(WorkGiverDef g)
	{
		var opts = new List<FloatMenuOption>
		{
			new FloatMenuOption("SWS_DefaultTo".Translate(WorkLabel(DefaultTarget(g))), () => Settings.moves.Remove(g.defName))
		};
		var ours = new HashSet<string>(Split.Parts.Select(p => p.def));
		foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading
			.Where(w => w.visible || (ours.Contains(w.defName) && DefDatabase<WorkTypeDef>.GetNamedSilentFail(Split.PartOf(w.defName).parent) != null))
			.OrderBy(WorkLabel))
		{
			WorkTypeDef ww = w;
			opts.Add(new FloatMenuOption(WorkLabel(w), () => Settings.moves[g.defName] = ww.defName));
		}
		return opts;
	}
}
