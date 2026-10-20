using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace StorytellerTuner;

public class TunerSettings : ModSettings
{
	// "D|Hard|threatScale" -> "1.5", "S|Cassandra|0|onDays" -> "4", "S|Randy|pop" -> "0.5"
	public Dictionary<string, string> values = new Dictionary<string, string>();

	public override void ExposeData()
	{
		Scribe_Collections.Look(ref values, "values", LookMode.Value, LookMode.Value);
		values ??= new Dictionary<string, string>();
	}
}

// one editable value
public sealed class Entry
{
	public string key, label, tip;
	public object target;
	public FieldInfo field;          // null for a synthetic value (population scale)
	public string original;          // as text
	public Action<string> setSynthetic;
	public Func<string> getSynthetic;

	public string Get()
	{
		if (field == null)
			return getSynthetic();
		return Tuner.ToText(field.GetValue(target));
	}

	public void Set(string text)
	{
		if (field == null)
		{
			setSynthetic(text);
			return;
		}
		object v = Tuner.FromText(field.FieldType, text);
		if (v != null)
			field.SetValue(target, v);
	}
}

public class TunerMod : Mod
{
	public static TunerSettings Settings;
	private int tab;
	private Def selected;
	private bool currentGame;
	private Vector2 scroll;
	private readonly Dictionary<string, string> buffers = new Dictionary<string, string>();

	public TunerMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<TunerSettings>();
	}

	public override string SettingsCategory() => "Storyteller Tuner";

	public override void WriteSettings()
	{
		base.WriteSettings();
		Tuner.ApplyToCurrentGame();
	}

	public override void DoSettingsWindowContents(Rect inRect)
	{
		Tuner.EnsureInit();
		var tabs = new List<TabRecord>
		{
			new TabRecord("ST_TabDifficulty".Translate(), () => { tab = 0; selected = null; currentGame = false; buffers.Clear(); }, tab == 0),
			new TabRecord("ST_TabStoryteller".Translate(), () => { tab = 1; selected = null; currentGame = false; buffers.Clear(); }, tab == 1)
		};
		Rect body = inRect;
		body.yMin += 32f;
		TabDrawer.DrawTabs(body, tabs);
		Rect top = new Rect(body.x, body.y + 4f, body.width, 30f);

		// chooser
		string name = currentGame ? "ST_CurrentGame".Translate().ToString() : selected?.LabelCap.ToString() ?? "ST_Choose".Translate().ToString();
		if (Widgets.ButtonText(new Rect(top.x, top.y, 320f, 30f), name))
		{
			var opts = new List<FloatMenuOption>();
			if (tab == 0)
			{
				if (Current.Game != null && Find.Storyteller?.difficulty != null)
					opts.Add(new FloatMenuOption("ST_CurrentGame".Translate(), () => { currentGame = true; selected = null; buffers.Clear(); }));
				foreach (DifficultyDef d in DefDatabase<DifficultyDef>.AllDefsListForReading)
					opts.Add(new FloatMenuOption(d.LabelCap.ToString() + (d.isCustom ? " (" + "ST_CustomNote".Translate().ToString() + ")" : ""), () => { selected = d; currentGame = false; buffers.Clear(); }));
			}
			else
			{
				foreach (StorytellerDef s in DefDatabase<StorytellerDef>.AllDefsListForReading.Where(s => s.listVisible))
					opts.Add(new FloatMenuOption(s.LabelCap, () => { selected = s; currentGame = false; buffers.Clear(); }));
			}
			Find.WindowStack.Add(new FloatMenu(opts));
		}
		List<Entry> entries = currentGame ? Tuner.CurrentGameEntries() : selected == null ? null : Tuner.EntriesFor(selected);
		if (entries == null)
		{
			Widgets.Label(new Rect(top.x, top.yMax + 10f, body.width, 60f), (tab == 0 ? "ST_HelpDifficulty" : "ST_HelpStoryteller").Translate());
			return;
		}
		if (!currentGame && Widgets.ButtonText(new Rect(top.x + 330f, top.y, 200f, 30f), "ST_Reset".Translate()))
		{
			Tuner.Reset(selected);
			buffers.Clear();
		}
		if (tab == 0 && !currentGame && Current.Game != null && Find.Storyteller?.difficulty != null
		    && Widgets.ButtonText(new Rect(top.x + 540f, top.y, 260f, 30f), "ST_FromGame".Translate()))
		{
			Tuner.CopyFromGame((DifficultyDef)selected);
			buffers.Clear();
		}
		string note = currentGame ? "ST_NoteCurrent".Translate() : (tab == 0 ? "ST_NoteDifficulty" : "ST_NoteStoryteller").Translate();
		Text.Font = GameFont.Tiny;
		Widgets.Label(new Rect(top.x, top.yMax + 2f, body.width, 34f), note);
		Text.Font = GameFont.Small;

		Rect outer = new Rect(body.x, top.yMax + 38f, body.width, body.yMax - top.yMax - 38f);
		float rowH = 26f;
		Rect view = new Rect(0f, 0f, outer.width - 20f, entries.Count * rowH + 10f);
		Widgets.BeginScrollView(outer, ref scroll, view);
		float y = 0f;
		foreach (Entry e in entries)
		{
			Rect row = new Rect(0f, y, view.width, rowH - 2f);
			if (Mouse.IsOver(row))
				Widgets.DrawHighlight(row);
			Rect labelR = new Rect(row.x + 4f, row.y, row.width * 0.6f, row.height);
			Widgets.Label(labelR, e.label);
			string tip = e.tip ?? "";
			if (!currentGame)
				tip += (tip.Length > 0 ? "\n\n" : "") + "ST_Original".Translate(e.original);
			TooltipHandler.TipRegion(row, tip);
			Rect valR = new Rect(row.x + row.width * 0.62f, row.y, row.width * 0.36f, row.height);
			Type t = e.field?.FieldType ?? typeof(float);
			string cur = e.Get();
			if (t == typeof(bool))
			{
				bool b = cur == "True";
				bool nb = b;
				Widgets.Checkbox(new Vector2(valR.x, valR.y), ref nb, 22f);
				if (nb != b)
					Tuner.Change(e, nb ? "True" : "False", currentGame);
			}
			else
			{
				if (!buffers.TryGetValue(e.key, out string buf))
					buf = cur;
				string nbuf = Widgets.TextField(valR, buf);
				buffers[e.key] = nbuf;
				if (nbuf != cur && Tuner.Valid(t, nbuf))
					Tuner.Change(e, nbuf, currentGame);
				bool changed = !currentGame && e.original != cur;
				if (changed)
					Widgets.DrawBoxSolid(new Rect(valR.x - 6f, valR.y + 4f, 3f, valR.height - 8f), new Color(1f, 0.8f, 0.3f));
			}
			y += rowH;
		}
		Widgets.EndScrollView();
	}
}

[StaticConstructorOnStartup]
public static class Tuner
{
	private static bool init;
	private static readonly Dictionary<Def, List<Entry>> entries = new Dictionary<Def, List<Entry>>();
	private static readonly Dictionary<StorytellerDef, List<CurvePoint>> popOriginal = new Dictionary<StorytellerDef, List<CurvePoint>>();
	private static readonly HashSet<string> skip = new HashSet<string> { "difficulty", "isCustom", "isExtreme", "listOrder", "index", "shortHash", "debugRandomId", "randSeed", "generated", "ignoreConfigErrors", "ignoreIllegalLabelCharacterConfigError" };

	static Tuner()
	{
		try
		{
			EnsureInit();
			int n = 0;
			foreach (var kv in TunerMod.Settings.values.ToList())
				if (Apply(kv.Key, kv.Value))
					n++;
			Log.Message("[Storyteller Tuner] v1.0.0 loaded – " + n + " changed values applied.");
		}
		catch (Exception e)
		{
			Log.Error("[Storyteller Tuner] setup failed: " + e);
		}
	}

	public static void EnsureInit()
	{
		if (init)
			return;
		init = true;
		foreach (DifficultyDef d in DefDatabase<DifficultyDef>.AllDefsListForReading)
			entries[d] = Build(d, "D|" + d.defName + "|", d);
		foreach (StorytellerDef s in DefDatabase<StorytellerDef>.AllDefsListForReading)
		{
			var list = new List<Entry>();
			// population intent
			if (s.populationIntentFactorFromPopCurve != null)
			{
				popOriginal[s] = s.populationIntentFactorFromPopCurve.Points.ToList();
				StorytellerDef st = s;
				float scale = 1f;
				list.Add(new Entry
				{
					key = "S|" + s.defName + "|pop", label = "ST_F_pop".Translate(), tip = "ST_T_pop".Translate(), original = "1",
					getSynthetic = () => ToText(scale),
					setSynthetic = txt =>
					{
						if (!float.TryParse(txt, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
							return;
						scale = f;
						st.populationIntentFactorFromPopCurve.SetPoints(popOriginal[st].Select(p => new CurvePoint(p.x, p.y > 0f ? p.y * f : p.y)));
					}
				});
			}
			list.AddRange(Build(s, "S|" + s.defName + "|x|", s));
			for (int i = 0; i < s.comps.Count; i++)
			{
				StorytellerCompProperties c = s.comps[i];
				string head = CompName(c, i);
				string prefix = "S|" + s.defName + "|" + i + "|";
				foreach (Entry e in Build(c, prefix, c))
				{
					e.label = head + ": " + e.label;
					list.Add(e);
				}
				if (c is StorytellerCompProperties_ThreatsGenerator tg && tg.parms != null)
					foreach (Entry e in Build(tg.parms, prefix + "parms.", tg.parms))
					{
						e.label = head + ": " + e.label;
						list.Add(e);
					}
				if (c is StorytellerCompProperties_RandomMain rm && rm.categoryWeights != null)
					foreach (IncidentCategoryEntry w in rm.categoryWeights)
					{
						if (w?.category == null)
							continue;
						FieldInfo f = typeof(IncidentCategoryEntry).GetField("weight");
						list.Add(new Entry
						{
							key = prefix + "w." + w.category.defName, target = w, field = f,
							label = head + ": " + "ST_F_weight".Translate(w.category.LabelCap), tip = "ST_T_weight".Translate(), original = ToText(w.weight)
						});
					}
			}
			entries[s] = list;
		}
	}

	private static string CompName(StorytellerCompProperties c, int i)
	{
		string n = c.GetType().Name.Replace("StorytellerCompProperties_", "");
		string what = null;
		FieldInfo cat = c.GetType().GetField("category");
		if (cat?.GetValue(c) is Def cd)
			what = cd.LabelCap;
		FieldInfo inc = c.GetType().GetField("incident");
		if (what == null && inc?.GetValue(c) is Def id)
			what = id.LabelCap;
		string key = "ST_C_" + n;
		string name = key.CanTranslate() ? key.Translate().ToString() : n;
		return "#" + (i + 1) + " " + name + (what != null ? " (" + what + ")" : "");
	}

	private static List<Entry> Build(object obj, string prefix, object target)
	{
		var list = new List<Entry>();
		foreach (FieldInfo f in obj.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
		{
			if (skip.Contains(f.Name) || f.IsInitOnly)
				continue;
			Type t = f.FieldType;
			if (t != typeof(float) && t != typeof(int) && t != typeof(bool) && t != typeof(FloatRange) && t != typeof(IntRange))
				continue;
			// Def's own bools (e.g. listVisible, tutorialMode) are not tuning values
			if (obj is StorytellerDef && t == typeof(bool))
				continue;
			string lk = "ST_F_" + f.Name, tk = "ST_T_" + f.Name;
			list.Add(new Entry
			{
				key = prefix + f.Name, target = target, field = f,
				label = lk.CanTranslate() ? lk.Translate().ToString() : f.Name,
				tip = tk.CanTranslate() ? tk.Translate().ToString() : null,
				original = ToText(f.GetValue(target))
			});
		}
		return list;
	}

	public static List<Entry> EntriesFor(Def d)
	{
		EnsureInit();
		return entries.TryGetValue(d, out var l) ? l : null;
	}

	// the running game's own difficulty values (saved with the game, not in the mod settings)
	private static Difficulty lastDiff;
	private static List<Entry> currentEntries;

	public static List<Entry> CurrentGameEntries()
	{
		Difficulty diff = Find.Storyteller?.difficulty;
		if (diff == null)
			return null;
		if (diff != lastDiff)
		{
			lastDiff = diff;
			currentEntries = Build(diff, "G|", diff);
		}
		return currentEntries;
	}

	private static Entry Find_(string key)
	{
		foreach (var l in entries.Values)
			foreach (Entry e in l)
				if (e.key == key)
					return e;
		return null;
	}

	public static bool Apply(string key, string value)
	{
		Entry e = Find_(key);
		if (e == null)
			return false;
		e.Set(value);
		return true;
	}

	private static readonly HashSet<Def> dirty = new HashSet<Def>();

	public static void Change(Entry e, string value, bool currentGame)
	{
		if (e.field != null)
		{
			object v = FromText(e.field.FieldType, value);
			if (v == null)
				return;
			value = ToText(v);
		}
		e.Set(value);
		if (currentGame)
			return;
		foreach (var kv in entries)
			if (kv.Value.Contains(e))
				dirty.Add(kv.Key);
		if (value == e.original)
			TunerMod.Settings.values.Remove(e.key);
		else
			TunerMod.Settings.values[e.key] = value;
	}

	public static void Reset(Def d)
	{
		foreach (Entry e in EntriesFor(d) ?? new List<Entry>())
		{
			e.Set(e.original);
			TunerMod.Settings.values.Remove(e.key);
		}
		dirty.Add(d);
	}

	public static void CopyFromGame(DifficultyDef d)
	{
		Difficulty diff = Find.Storyteller?.difficulty;
		if (diff == null)
			return;
		foreach (Entry e in EntriesFor(d))
		{
			FieldInfo g = typeof(Difficulty).GetField(e.field?.Name ?? "", BindingFlags.Public | BindingFlags.Instance);
			if (g == null || g.FieldType != e.field.FieldType)
				continue;
			Change(e, ToText(g.GetValue(diff)), false);
		}
	}

	// a running game that uses an edited (non-custom) difficulty gets the new values
	public static void ApplyToCurrentGame()
	{
		try
		{
			Storyteller st = Current.Game != null ? Find.Storyteller : null;
			DifficultyDef d = st?.difficultyDef;
			// only when you changed this difficulty now – your edits of the game's own values stay
			if (st?.difficulty == null || d == null || d.isCustom || !dirty.Contains(d))
				return;
			st.difficulty.CopyFrom(d);
			dirty.Remove(d);
		}
		catch
		{
		}
	}

	public static string ToText(object v)
	{
		switch (v)
		{
			case float f: return f.ToString("0.###", CultureInfo.InvariantCulture);
			case int i: return i.ToString(CultureInfo.InvariantCulture);
			case bool b: return b ? "True" : "False";
			case FloatRange fr: return fr.min.ToString("0.###", CultureInfo.InvariantCulture) + "~" + fr.max.ToString("0.###", CultureInfo.InvariantCulture);
			case IntRange ir: return ir.min + "~" + ir.max;
		}
		return v?.ToString() ?? "";
	}

	public static bool Valid(Type t, string s) => FromText(t, s) != null;

	public static object FromText(Type t, string s)
	{
		s = (s ?? "").Trim().Replace(',', '.');
		var inv = CultureInfo.InvariantCulture;
		if (t == typeof(float))
			return float.TryParse(s, NumberStyles.Float, inv, out float f) ? f : (object)null;
		if (t == typeof(int))
			return int.TryParse(s, NumberStyles.Integer, inv, out int i) ? i : (object)null;
		if (t == typeof(bool))
			return s == "True" ? true : s == "False" ? false : (object)null;
		string[] p = s.Split('~');
		if (p.Length != 2)
			return null;
		if (t == typeof(FloatRange) && float.TryParse(p[0], NumberStyles.Float, inv, out float a) && float.TryParse(p[1], NumberStyles.Float, inv, out float b))
			return new FloatRange(a, b);
		if (t == typeof(IntRange) && int.TryParse(p[0], out int c) && int.TryParse(p[1], out int d))
			return new IntRange(c, d);
		return null;
	}
}
