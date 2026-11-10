using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// ===========================================================================
// 1.2.0: crises reworked.
//  - four groups with a clear order: Life > your orders > Shortage > Work > Comfort
//  - each group may take only part of the colony (Life everything, Shortage half,
//    Work a third, Comfort a quarter – a slider for each)
//  - colonists in a priority area are not taken (except when lives are at stake)
//  - a crisis starts gently (one colonist), grows when things do not get better
//  - every crisis is a card: progress, who works on it, Ignore / Snooze a day / Do it now
//  - a journal of what happened
// ===========================================================================
public enum CrisisGroup
{
	Life,
	Shortage,
	Work,
	Comfort
}

public static class CrisisGroups
{
	private const Crisis LifeMask = Crisis.Fire | Crisis.Raid | Crisis.Medical | Crisis.Manhunters | Crisis.PrisonBreak
		| Crisis.DangerousBreak | Crisis.EntityEscape | Crisis.VacuumBreach | Crisis.ToxGas | Crisis.NoFirefighters
		| Crisis.Labor | Crisis.Siege | Crisis.Infestation | Crisis.Epidemic | Crisis.RoofCollapse;

	private const Crisis WorkMask = Crisis.QuestDeadline;

	private const Crisis ComfortMask = Crisis.Mood | Crisis.Hygiene | Crisis.Death;

	public static CrisisGroup Of(Crisis c)
	{
		if ((c & LifeMask) != 0) return CrisisGroup.Life;
		if ((c & WorkMask) != 0) return CrisisGroup.Work;
		if ((c & ComfortMask) != 0) return CrisisGroup.Comfort;
		return CrisisGroup.Shortage;
	}

	private static readonly HashSet<Situation> LifeSit = new HashSet<Situation>
	{
		Situation.MassCasualties, Situation.Infection, Situation.Wounded, Situation.BedHungry, Situation.HungryNoMeals,
		Situation.PrisonersHurt, Situation.Exhausted, Situation.MechThreat, Situation.SlaveRebellionRisk
	};

	private static readonly HashSet<Situation> ShortageSit = new HashSet<Situation>
	{
		Situation.LowWood, Situation.LowSteel, Situation.LowMedicine, Situation.LowFuel, Situation.FoodSpoiling,
		Situation.RottingOutside, Situation.FrostHarvest, Situation.BigHarvest, Situation.HungryPrisoners,
		Situation.AnimalsHungry, Situation.ColdClothing, Situation.NoBedroom, Situation.PrisonBeds,
		Situation.ButcherBacklog, Situation.SowingBacklog, Situation.StorageFull, Situation.Childcare, Situation.Pollution
	};

	private static readonly HashSet<Situation> ComfortSit = new HashSet<Situation>
	{
		Situation.DirtyRooms, Situation.UglyRooms, Situation.LowJoy, Situation.MoodSlipping, Situation.Corpses,
		Situation.Gathering, Situation.Guests, Situation.DirtyPrison, Situation.Snow
	};

	public static CrisisGroup Of(Situation s)
	{
		if (LifeSit.Contains(s)) return CrisisGroup.Life;
		if (ShortageSit.Contains(s)) return CrisisGroup.Shortage;
		if (ComfortSit.Contains(s)) return CrisisGroup.Comfort;
		return CrisisGroup.Work;
	}

	public static string Label(CrisisGroup g) => ("CF3C_Group_" + g).Translate();

	public static Color Color(CrisisGroup g)
	{
		switch (g)
		{
		case CrisisGroup.Life: return new Color(1f, 0.4f, 0.35f);
		case CrisisGroup.Shortage: return new Color(1f, 0.72f, 0.3f);
		case CrisisGroup.Work: return new Color(0.45f, 0.75f, 1f);
		default: return new Color(0.7f, 0.9f, 0.55f);
		}
	}

	// 0 = the group is off; 1 = normal
	public static float Strength(CrisisGroup g)
	{
		CrisesSettings s = CrisesMod.S;
		if (s == null) return 1f;
		switch (g)
		{
		case CrisisGroup.Life: return 1f;
		case CrisisGroup.Shortage: return s.strShortage;
		case CrisisGroup.Work: return s.strWork;
		default: return s.strComfort;
		}
	}

	// how many colonists the group may pull over to its work
	public static int Cap(CrisisGroup g, int colonists)
	{
		if (g == CrisisGroup.Life)
			return int.MaxValue;
		float share = g == CrisisGroup.Shortage ? 0.5f : g == CrisisGroup.Work ? 0.34f : 0.25f;
		float str = Strength(g);
		if (str <= 0.01f)
			return 0;
		return Mathf.Clamp(Mathf.CeilToInt(colonists * share * str), 1, Math.Max(1, colonists));
	}
}

// one crisis or situation while it lasts
public sealed class CrisisTrack
{
	public string key;           // "c:Food" or "s:LowWood"
	public int since;
	public int stage = 1;
	public int snoozeUntil = -1;
	public bool ignored;
	public bool pushed;
	public int firstAmount = -1;
	public int amount = -1;
	public string reason;        // situations: "12 wood left" …
	// runtime: who was pulled over in the last plan
	public List<string> who = new List<string>();

	public bool IsCrisis => key.StartsWith("c:", StringComparison.Ordinal);

	public Crisis CrisisFlag => IsCrisis && Enum.TryParse(key.Substring(2), out Crisis c) ? c : Crisis.None;

	public Situation? Sit => !IsCrisis && Enum.TryParse(key.Substring(2), out Situation s) ? s : (Situation?)null;

	public CrisisGroup Group => IsCrisis ? CrisisGroups.Of(CrisisFlag) : Sit.HasValue ? CrisisGroups.Of(Sit.Value) : CrisisGroup.Work;

	public string Label => CrisisTracks.LabelOf(key);

	public bool Waiting(int now) => ignored || snoozeUntil > now;

	public bool Locked => Group == CrisisGroup.Life;
}

public struct JournalLine
{
	public int tick;
	public int kind;   // 0 start, 1 end, 2 ignore, 3 snooze, 4 push, 5 escalate, 6 resume
	public string key;
	public int dur;
}

public sealed class CrisisBook
{
	public List<CrisisTrack> tracks = new List<CrisisTrack>();
	public List<JournalLine> journal = new List<JournalLine>();
	public HashSet<string> raisedThisPlan = new HashSet<string>();

	public CrisisTrack Find(string key) => tracks.FirstOrDefault(t => t.key == key);

	public void Log(int kind, string key, int dur = 0)
	{
		journal.Add(new JournalLine { tick = Verse.Find.TickManager.TicksGame, kind = kind, key = key, dur = dur });
		if (journal.Count > 80)
			journal.RemoveRange(0, journal.Count - 80);
	}
}

public static class CrisisTracks
{
	private static readonly ConditionalWeakTable<ForemanMapComponent, CrisisBook> table = new ConditionalWeakTable<ForemanMapComponent, CrisisBook>();

	public static CrisisBook Of(ForemanMapComponent c) => table.GetValue(c, _ => new CrisisBook());

	public static string LabelOf(string key)
	{
		if (key == null || key.Length < 3)
			return key ?? "";
		string rest = key.Substring(2);
		if (key[0] == 'c' && Enum.TryParse(rest, out Crisis c))
			return CrisisRules.Label(c);
		if (key[0] == 's' && Enum.TryParse(rest, out Situation s))
			return SituationRules.Label(s);
		return rest;
	}

	public static void Expose(ForemanMapComponent comp)
	{
		CrisisBook b = Of(comp);
		var keys = b.tracks.Select(t => t.key).ToList();
		var data = b.tracks.Select(t => string.Join("|", t.since, t.stage, t.snoozeUntil, t.ignored ? 1 : 0, t.pushed ? 1 : 0, t.firstAmount)).ToList();
		var jr = b.journal.Select(j => string.Join("|", j.tick, j.kind, j.key, j.dur)).ToList();
		Scribe_Collections.Look(ref keys, "cf3Keys", LookMode.Value);
		Scribe_Collections.Look(ref data, "cf3Data", LookMode.Value);
		Scribe_Collections.Look(ref jr, "cf3Journal", LookMode.Value);
		if (Scribe.mode != LoadSaveMode.LoadingVars)
			return;
		b.tracks.Clear();
		b.journal.Clear();
		if (keys != null && data != null)
			for (int i = 0; i < keys.Count && i < data.Count; i++)
			{
				string[] d = data[i].Split('|');
				if (d.Length < 6)
					continue;
				b.tracks.Add(new CrisisTrack
				{
					key = keys[i],
					since = Int(d[0]),
					stage = Mathf.Clamp(Int(d[1]), 1, 3),
					snoozeUntil = Int(d[2]),
					ignored = d[3] == "1",
					pushed = d[4] == "1",
					firstAmount = Int(d[5])
				});
			}
		if (jr != null)
			foreach (string line in jr)
			{
				string[] d = line.Split('|');
				if (d.Length >= 4)
					b.journal.Add(new JournalLine { tick = Int(d[0]), kind = Int(d[1]), key = d[2], dur = Int(d[3]) });
			}
	}

	private static int Int(string s) => int.TryParse(s, out int v) ? v : 0;

	// every 250 ticks after the crisis check: start, end and grow the tracks
	public static void Tick(ForemanMapComponent comp)
	{
		if (!comp.auto)
			return;
		CrisisBook b = Of(comp);
		CS st = CS.Of(comp);
		int now = Find.TickManager.TicksGame;
		var live = new Dictionary<string, ActiveSituation>();
		foreach (Crisis c in CrisisRules.All)
			if ((comp.crisis & c) != 0)
				live["c:" + c] = null;
		foreach (ActiveSituation s in st.situations)
			live["s:" + s.id] = s;
		bool replan = false;
		// ended
		for (int i = b.tracks.Count - 1; i >= 0; i--)
		{
			CrisisTrack t = b.tracks[i];
			if (live.ContainsKey(t.key))
				continue;
			b.tracks.RemoveAt(i);
			b.Log(1, t.key, now - t.since);
		}
		// started / still going
		foreach (var kv in live)
		{
			CrisisTrack t = b.Find(kv.Key);
			if (t == null)
			{
				t = new CrisisTrack { key = kv.Key, since = now };
				b.tracks.Add(t);
				b.Log(0, kv.Key);
			}
			if (kv.Value != null)
			{
				t.amount = kv.Value.amount;
				t.reason = kv.Value.reason;
				if (t.firstAmount < 0)
					t.firstAmount = t.amount;
			}
			if (t.snoozeUntil > 0 && t.snoozeUntil <= now)
			{
				t.snoozeUntil = -1;
				t.since = now;   // starts again gently
				t.stage = 1;
				b.Log(6, t.key);
				replan = true;
			}
			int stage = StageOf(t, now);
			if (stage > t.stage)
			{
				if (stage == 3 && !t.pushed && t.Group != CrisisGroup.Life && t.stage > 0)
				{
					b.Log(5, t.key);
					if (CrisesMod.S.crisisMessages && comp.map.IsPlayerHome && !t.Waiting(now))
						Messages.Message("CF3C_Escalate".Translate(t.Label), MessageTypeDefOf.CautionInput, historical: false);
				}
				replan |= !t.Waiting(now);
			}
			t.stage = stage;
		}
		if (replan)
			comp.RequestPlan(urgent: false);
	}

	// 1 = gently (one colonist), 2 = normal, 3 = full
	public static int StageOf(CrisisTrack t, int now)
	{
		CrisisGroup g = t.Group;
		if (g == CrisisGroup.Life || t.pushed)
			return 3;
		if (CrisesMod.S != null && !CrisesMod.S.escalate)
			return 2;
		int age = now - t.since;
		int toTwo = g == CrisisGroup.Shortage ? 2500 : 7500;
		int toThree = g == CrisisGroup.Shortage ? 20000 : 30000;
		if (age < toTwo)
			return 1;
		if (age < toThree)
			return 2;
		// it gets better on its own – no need to push harder
		if (t.firstAmount > 0 && t.amount >= 0 && t.amount <= t.firstAmount * 0.8f)
			return 2;
		return 3;
	}

	// the colonists the player gave other orders (priority areas) – not taken except for lives
	public static HashSet<Pawn> Reserved(Map map)
	{
		var set = new HashSet<Pawn>();
		if (CrisesMod.S != null && !CrisesMod.S.respectOrders)
			return set;
		try
		{
			if (!PriorityAreaComp.Enabled)
				return set;
			PriorityAreaComp comp = PriorityAreaComp.For(map);
			if (comp == null)
				return set;
			foreach (PriorityArea a in comp.areas)
				if (a.lastCount != 0 && !comp.PausedFor(a))
					foreach (Pawn p in a.assigned)
						if (p != null)
							set.Add(p);
		}
		catch
		{
		}
		return set;
	}
}

// the raising itself, shared by the crisis and situation steps
public sealed class GroupRaiser
{
	private readonly PlanContext ctx;
	private readonly HashSet<Pawn> reserved;
	private readonly Dictionary<CrisisGroup, HashSet<Pawn>> used = new Dictionary<CrisisGroup, HashSet<Pawn>>();

	public GroupRaiser(PlanContext ctx)
	{
		this.ctx = ctx;
		reserved = CrisisTracks.Reserved(ctx.map);
	}

	public bool IsReserved(Pawn p) => reserved.Contains(p);

	public static int CountFor(int stage, int count)
	{
		if (stage <= 1)
			return Math.Min(1, count);
		if (stage >= 3)
			return Mathf.CeilToInt(count * 1.5f);
		return count;
	}

	public static int LevelFor(CrisisTrack t, int level)
	{
		CrisisGroup g = t.Group;
		if (g == CrisisGroup.Life || t.pushed)
			return level;
		if (g == CrisisGroup.Shortage && t.stage >= 3)
			return level;
		return Math.Max(level, 2);
	}

	public int Raise(CrisisTrack t, WorkTypeDef w, int level, int count, string reasonKey, object arg)
	{
		CrisisGroup g = t.Group;
		int cap = CrisisGroups.Cap(g, ctx.pawns.Count);
		if (cap <= 0 || count <= 0)
			return 0;
		if (!used.TryGetValue(g, out var mine))
			used[g] = mine = new HashSet<Pawn>();
		int done = 0;
		foreach (Pawn p in ctx.Best(w))
		{
			if (done >= count)
				break;
			if (g != CrisisGroup.Life && reserved.Contains(p))
				continue;
			int cur = ctx.Get(p, w);
			if (cur > 0 && cur <= level)
			{
				// already on it – he is not pulled away from anything
				done++;
				ctx.pinned.Add(PlanContext.Key(p, w));
				Note(t, p);
				continue;
			}
			if (!mine.Contains(p) && mine.Count >= cap)
				continue;
			if (ctx.Set(p, w, level, reasonKey, arg, null, pin: true))
			{
				done++;
				mine.Add(p);
				Note(t, p);
			}
		}
		return done;
	}

	private static void Note(CrisisTrack t, Pawn p)
	{
		string n = p.LabelShortCap;
		if (!t.who.Contains(n))
			t.who.Add(n);
	}
}

public static partial class SituationRules
{
	public static int OffOf(Situation s) => defs.FirstOrDefault(d => d.id == s)?.off ?? 1;
}

// ---- the window ----------------------------------------------------------------
public sealed class Dialog_Crises : Window
{
	private readonly ForemanMapComponent comp;
	private int tab;
	private Vector2 scroll;
	private float viewH;
	private bool changed;

	public override Vector2 InitialSize => new Vector2(780f, 680f);

	public Dialog_Crises(ForemanMapComponent comp)
	{
		this.comp = comp;
		doCloseX = true;
		closeOnClickedOutside = false;
		absorbInputAroundWindow = false;
		draggable = true;
		resizeable = true;
		forcePause = false;
	}

	public static void Register()
	{
		StewardApi.AddButton(new WorkTabButton
		{
			id = "crises",
			width = 100f,
			// 1.2.0: the button counts real crises; situations only when there is no crisis
			label = c =>
			{
				var list = Active(c);
				int crises = list.Count(t => t.IsCrisis);
				if (crises > 0)
					return "CF3C_Button".Translate(crises);
				int sits = list.Count;
				return sits > 0 ? (string)"CF3C_ButtonSit".Translate(sits) : (string)"CF3C_ButtonNone".Translate();
			},
			color = c =>
			{
				var list = Active(c);
				if (list.Any(t => t.Group == CrisisGroup.Life))
					return CrisisGroups.Color(CrisisGroup.Life);
				if (list.Any(t => t.IsCrisis))
					return CrisisGroups.Color(CrisisGroup.Shortage);
				return Color.white;
			},
			tip = c => "CF3C_ButtonTip".Translate(),
			click = c => Find.WindowStack.Add(new Dialog_Crises(c))
		});
	}

	public static List<CrisisTrack> Active(ForemanMapComponent c)
	{
		int now = Find.TickManager.TicksGame;
		return CrisisTracks.Of(c).tracks.Where(t => !t.Waiting(now)).ToList();
	}

	public override void PostClose()
	{
		base.PostClose();
		if (changed)
		{
			CrisesMod.Instance?.WriteSettings();
			comp.RequestPlan(urgent: false);
		}
	}

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width - 40f, 34f), "CF3C_Title".Translate());
		Text.Font = GameFont.Small;
		float y = inRect.y + 40f;
		var tabs = new List<TabRecord>
		{
			new TabRecord("CF3C_TabActive".Translate(), () => tab = 0, tab == 0),
			new TabRecord("CF3C_TabJournal".Translate(), () => tab = 1, tab == 1)
		};
		Rect body = new Rect(inRect.x, y + 32f, inRect.width, inRect.height - y - 32f - 150f);
		TabDrawer.DrawTabs(new Rect(body.x, body.y, body.width, 0f), tabs);
		Widgets.DrawMenuSection(body);
		Rect inner = body.ContractedBy(8f);
		if (tab == 0)
			DrawActive(inner);
		else
			DrawJournal(inner);
		DrawSettings(new Rect(inRect.x, body.yMax + 8f, inRect.width, 140f));
	}

	private void DrawActive(Rect r)
	{
		CrisisBook b = CrisisTracks.Of(comp);
		int now = Find.TickManager.TicksGame;
		var list = b.tracks.OrderBy(t => t.IsCrisis ? 0 : 1).ThenBy(t => t.Waiting(now) ? 1 : 0).ThenBy(t => (int)t.Group).ThenBy(t => t.since).ToList();
		if (list.Count == 0)
		{
			GUI.color = new Color(0.7f, 0.9f, 0.7f);
			Text.Anchor = TextAnchor.MiddleCenter;
			Widgets.Label(r, "CF3C_Nothing".Translate());
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			return;
		}
		Rect view = new Rect(0f, 0f, r.width - 16f, Math.Max(viewH, r.height));
		Widgets.BeginScrollView(r, ref scroll, view);
		float cy = 0f;
		int head = -1;
		foreach (CrisisTrack t in list)
		{
			// 1.2.0: crises first, then situations under their own heading
			int kind = t.IsCrisis ? 0 : 1;
			if (kind != head)
			{
				head = kind;
				Text.Font = GameFont.Small;
				GUI.color = new Color(0.75f, 0.75f, 0.75f);
				int count = list.Count(x => (x.IsCrisis ? 0 : 1) == kind);
				Widgets.Label(new Rect(4f, cy + 2f, view.width, 24f), (kind == 0 ? "CF3C_HeadCrises" : "CF3C_HeadSits").Translate(count));
				GUI.color = Color.white;
				Widgets.DrawLineHorizontal(0f, cy + 26f, view.width);
				cy += 32f;
			}
			cy += Card(new Rect(0f, cy, view.width, 0f), t, now) + 6f;
		}
		viewH = cy;
		Widgets.EndScrollView();
	}

	private float Card(Rect r, CrisisTrack t, int now)
	{
		const float h = 92f;
		r.height = h;
		bool waiting = t.Waiting(now);
		CrisisGroup g = t.Group;
		Color gc = CrisisGroups.Color(g);
		Widgets.DrawBoxSolid(r, new Color(0.13f, 0.13f, 0.13f, waiting ? 0.5f : 0.9f));
		Widgets.DrawBoxSolid(new Rect(r.x, r.y, 5f, r.height), waiting ? gc * 0.5f : gc);
		Widgets.DrawHighlightIfMouseover(r);
		if (waiting)
			GUI.color = new Color(1f, 1f, 1f, 0.55f);
		float x = r.x + 14f;
		// title + group
		Text.Font = GameFont.Small;
		string title = t.Label;
		float tw = Math.Min(Text.CalcSize(title).x + 6f, r.width * 0.5f);
		Widgets.Label(new Rect(x, r.y + 6f, tw, 24f), title.Truncate(tw));
		Text.Font = GameFont.Tiny;
		Color keep = GUI.color;
		GUI.color = new Color(gc.r, gc.g, gc.b, keep.a);
		Widgets.Label(new Rect(x + tw + 6f, r.y + 9f, 140f, 20f), CrisisGroups.Label(g));
		GUI.color = keep;
		// stage dots + age, right
		string age = "CF3C_For".Translate((now - t.since).ToStringTicksToPeriod(allowSeconds: false, shortForm: true, canUseDecimals: false));
		Text.Anchor = TextAnchor.UpperRight;
		Widgets.Label(new Rect(r.xMax - 260f, r.y + 9f, 180f, 20f), age);
		Text.Anchor = TextAnchor.UpperLeft;
		Rect dots = new Rect(r.xMax - 72f, r.y + 10f, 60f, 14f);
		for (int i = 0; i < 3; i++)
		{
			Rect d = new Rect(dots.x + i * 18f, dots.y, 12f, 12f);
			Widgets.DrawBoxSolid(d, i < t.stage ? gc : new Color(0.3f, 0.3f, 0.3f));
		}
		TooltipHandler.TipRegion(dots, "CF3C_Stage".Translate(t.stage) + "\n\n" + "CF3C_StageTip".Translate());
		// line 2: reason / progress / status
		float y2 = r.y + 32f;
		if (waiting)
		{
			string w = t.ignored ? (string)"CF3C_Ignored".Translate() : (string)"CF3C_Snoozed".Translate((t.snoozeUntil - now).ToStringTicksToPeriod(allowSeconds: false, shortForm: true, canUseDecimals: false));
			Widgets.Label(new Rect(x, y2, r.width - 30f, 20f), w);
		}
		else if (!t.IsCrisis && t.amount >= 0 && t.Sit.HasValue)
		{
			int off = SituationRules.OffOf(t.Sit.Value);
			int first = Math.Max(t.firstAmount, t.amount);
			float done = first <= off ? 0f : Mathf.Clamp01((first - t.amount) / (float)(first - off));
			Rect bar = new Rect(x, y2 + 3f, 180f, 12f);
			Widgets.DrawBoxSolid(bar, new Color(0.22f, 0.22f, 0.22f));
			Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * done, bar.height), gc);
			string txt = "CF3C_Left".Translate(t.amount, off);
			int age2 = now - t.since;
			if (t.firstAmount > t.amount && age2 > 1250)
			{
				float rate = (t.firstAmount - t.amount) / (float)age2;
				int eta = Mathf.CeilToInt((t.amount - off) / Math.Max(rate, 0.000001f));
				if (eta > 0 && eta < 60000 * 15)
					txt += " · " + "CF3C_Eta".Translate(eta.ToStringTicksToPeriod(allowSeconds: false, shortForm: true, canUseDecimals: false));
			}
			Widgets.Label(new Rect(bar.xMax + 8f, y2, r.width - 220f, 20f), txt.Truncate(r.width - 230f));
			if (!t.reason.NullOrEmpty())
				TooltipHandler.TipRegion(new Rect(x, y2, r.width - 30f, 20f), t.reason);
		}
		else
		{
			string d = t.IsCrisis ? CrisisRules.Describe(t.CrisisFlag) : (t.reason ?? "");
			Widgets.Label(new Rect(x, y2, r.width - 30f, 20f), d.Truncate(r.width - 30f));
		}
		// line 3: who + buttons
		float y3 = r.y + 58f;
		string who = waiting ? "" : t.who.Count > 0 ? (string)"CF3C_Who".Translate(string.Join(", ", t.who)) : (string)"CF3C_WhoNone".Translate();
		float bw = 112f;
		float bx = r.xMax - 3 * (bw + 6f) - 6f;
		Widgets.Label(new Rect(x, y3 + 4f, bx - x - 6f, 20f), who.Truncate(bx - x - 6f));
		Text.Font = GameFont.Tiny;
		GUI.color = Color.white;
		if (t.Locked)
		{
			GUI.color = new Color(1f, 0.6f, 0.55f);
			Text.Anchor = TextAnchor.MiddleRight;
			Widgets.Label(new Rect(bx, y3, r.xMax - bx - 8f, 26f), "CF3C_LifeLocked".Translate());
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
		}
		else if (waiting)
		{
			if (Widgets.ButtonText(new Rect(r.xMax - bw - 8f, y3, bw, 26f), "CF3C_Resume".Translate()))
			{
				t.ignored = false;
				t.snoozeUntil = -1;
				t.since = now;
				t.stage = 1;
				CrisisTracks.Of(comp).Log(6, t.key);
				changed = true;
			}
		}
		else
		{
			Rect b1 = new Rect(bx, y3, bw, 26f), b2 = new Rect(bx + bw + 6f, y3, bw, 26f), b3 = new Rect(bx + 2 * (bw + 6f), y3, bw, 26f);
			if (Widgets.ButtonText(b1, "CF3C_Ignore".Translate()))
			{
				t.ignored = true;
				t.pushed = false;
				CrisisTracks.Of(comp).Log(2, t.key);
				changed = true;
			}
			TooltipHandler.TipRegion(b1, "CF3C_IgnoreTip".Translate());
			if (Widgets.ButtonText(b2, "CF3C_Snooze".Translate()))
			{
				t.snoozeUntil = now + 60000;
				t.pushed = false;
				CrisisTracks.Of(comp).Log(3, t.key);
				changed = true;
			}
			TooltipHandler.TipRegion(b2, "CF3C_SnoozeTip".Translate());
			if (t.pushed)
				GUI.color = new Color(1f, 0.8f, 0.4f);
			if (Widgets.ButtonText(b3, t.pushed ? "CF3C_Unpush".Translate() : "CF3C_Push".Translate()))
			{
				t.pushed = !t.pushed;
				t.stage = CrisisTracks.StageOf(t, now);
				if (t.pushed)
					CrisisTracks.Of(comp).Log(4, t.key);
				changed = true;
			}
			GUI.color = Color.white;
			TooltipHandler.TipRegion(b3, "CF3C_PushTip".Translate());
		}
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
		return h;
	}

	private void DrawJournal(Rect r)
	{
		CrisisBook b = CrisisTracks.Of(comp);
		if (b.journal.Count == 0)
		{
			Widgets.Label(r, "CF3C_JournalEmpty".Translate());
			return;
		}
		float lh = 22f;
		Rect view = new Rect(0f, 0f, r.width - 16f, b.journal.Count * lh);
		Widgets.BeginScrollView(r, ref scroll, view);
		float y = 0f;
		float lon = 0f;
		try { lon = Find.WorldGrid.LongLatOf(comp.map.Tile).x; } catch { }
		for (int i = b.journal.Count - 1; i >= 0; i--)
		{
			JournalLine j = b.journal[i];
			string when = GenDate.DateFullStringWithHourAt(GenDate.TickGameToAbs(j.tick), new Vector2(lon, 0f));
			string label = CrisisTracks.LabelOf(j.key);
			string what;
			switch (j.kind)
			{
			case 0: what = "CF3C_J_Start".Translate(label); break;
			case 1: what = "CF3C_J_End".Translate(label, j.dur.ToStringTicksToPeriod(allowSeconds: false, shortForm: true, canUseDecimals: false)); break;
			case 2: what = "CF3C_J_Ignore".Translate(label); break;
			case 3: what = "CF3C_J_Snooze".Translate(label); break;
			case 4: what = "CF3C_J_Push".Translate(label); break;
			case 5: what = "CF3C_J_Escalate".Translate(label); break;
			default: what = "CF3C_J_Resume".Translate(label); break;
			}
			Rect row = new Rect(0f, y, view.width, lh);
			if (i % 2 == 0)
				Widgets.DrawLightHighlight(row);
			GUI.color = j.kind == 1 ? new Color(0.65f, 0.9f, 0.6f) : j.kind == 5 ? new Color(1f, 0.7f, 0.4f) : Color.white;
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(4f, y + 3f, 190f, lh), when);
			Text.Font = GameFont.Small;
			Widgets.Label(new Rect(200f, y + 1f, view.width - 204f, lh), what.Truncate(view.width - 204f));
			GUI.color = Color.white;
			y += lh;
		}
		Widgets.EndScrollView();
	}

	private void DrawSettings(Rect r)
	{
		CrisesSettings s = CrisesMod.S;
		float w = (r.width - 20f) / 2f;
		float y = r.y;
		changed |= Slider(new Rect(r.x, y, w, 40f), CrisisGroup.Shortage, ref s.strShortage);
		changed |= Slider(new Rect(r.x + w + 20f, y, w, 40f), CrisisGroup.Work, ref s.strWork);
		y += 44f;
		changed |= Slider(new Rect(r.x, y, w, 40f), CrisisGroup.Comfort, ref s.strComfort);
		GUI.color = CrisisGroups.Color(CrisisGroup.Life);
		Widgets.Label(new Rect(r.x + w + 20f, y + 10f, w, 24f), "CF3C_StrLife".Translate());
		GUI.color = Color.white;
		y += 46f;
		bool a = s.respectOrders, e = s.escalate;
		Widgets.CheckboxLabeled(new Rect(r.x, y, w, 26f), "CF3C_Orders".Translate(), ref s.respectOrders);
		TooltipHandler.TipRegion(new Rect(r.x, y, w, 26f), "CF3C_OrdersTip".Translate());
		Widgets.CheckboxLabeled(new Rect(r.x + w + 20f, y, w, 26f), "CF3C_Escalation".Translate(), ref s.escalate);
		TooltipHandler.TipRegion(new Rect(r.x + w + 20f, y, w, 26f), "CF3C_EscalationTip".Translate());
		changed |= a != s.respectOrders || e != s.escalate;
	}

	public static bool Slider(Rect r, CrisisGroup g, ref float v)
	{
		float old = v;
		GUI.color = CrisisGroups.Color(g);
		Widgets.Label(new Rect(r.x, r.y, r.width, 20f), "CF3C_Str".Translate(CrisisGroups.Label(g), v <= 0.01f ? (string)"CF3C_Off".Translate() : v.ToStringPercent()));
		GUI.color = Color.white;
		v = Widgets.HorizontalSlider(new Rect(r.x, r.y + 22f, r.width, 16f), v, 0f, 2f, roundTo: 0.25f);
		TooltipHandler.TipRegion(r, "CF3C_StrTip".Translate());
		return Math.Abs(old - v) > 0.001f;
	}
}
