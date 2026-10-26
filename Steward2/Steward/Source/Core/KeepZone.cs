using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// 3.3.0: keep zone. A stockpile the player paints like any other (Zones menu).
// The storage tab picks what colonists bring in; while the zone is locked nobody
// takes anything out of it – not for eating, crafting, building, equipping, nor
// to haul it to better storage. Things in it count as forbidden for the colony.
// ---------------------------------------------------------------------------
public class Zone_StewardKeep : Zone_Stockpile
{
	public bool locked = true;
	// 3.4.0: what and how much to bring (0 = everything there is). When the list is
	// not empty it drives the storage filter; the storage tab is the fallback.
	public List<KeepEntry> entries = new List<KeepEntry>();
	[Unsaved] private readonly Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
	[Unsaved] private int countTick = -9999;

	private static readonly Color KeepColor = new Color(0.62f, 0.38f, 0.95f, 0.09f);

	protected override Color NextZoneColor => KeepColor;

	public Zone_StewardKeep() { }

	public Zone_StewardKeep(ZoneManager zm) : base(StorageSettingsPreset.DefaultStockpile, zm)
	{
		label = zm.NewZoneName("CF_Keep_Name".Translate());
		// starts empty: the player picks what belongs here
		settings.filter.SetDisallowAll();
		settings.Priority = StoragePriority.Important;
	}

	public override void ExposeData()
	{
		base.ExposeData();
		Scribe_Values.Look(ref locked, "cfLocked", true);
		Scribe_Collections.Look(ref entries, "cfEntries", LookMode.Deep);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			entries ??= new List<KeepEntry>();
			entries.RemoveAll(e => e == null || e.def == null);
		}
	}

	public bool Managed => entries.Count > 0;

	public KeepEntry EntryFor(ThingDef d)
	{
		for (int i = 0; i < entries.Count; i++)
			if (entries[i].def == d)
				return entries[i];
		return null;
	}

	// how many of a thing lie in the zone (worked out at most every 60 ticks)
	public int CountOf(ThingDef d)
	{
		RecountIfOld();
		return counts.TryGetValue(d, out int n) ? n : 0;
	}

	private void RecountIfOld()
	{
		int now = Find.TickManager.TicksGame;
		if (now - countTick < 60 && now >= countTick)
			return;
		countTick = now;
		counts.Clear();
		Map m = Map;
		if (m == null)
			return;
		for (int i = 0; i < cells.Count; i++)
		{
			List<Thing> list = m.thingGrid.ThingsListAtFast(cells[i]);
			for (int j = 0; j < list.Count; j++)
			{
				Thing t = list[j];
				if (t.def.category != ThingCategory.Item)
					continue;
				counts.TryGetValue(t.def, out int n);
				counts[t.def] = n + t.stackCount;
			}
		}
	}

	public void ForceRecount() => countTick = -9999;

	// The storage filter follows the list: a thing is allowed while the zone holds less than wanted.
	public void SyncFilter(bool full)
	{
		if (!Managed || settings == null)
			return;
		if (full)
		{
			settings.filter.SetDisallowAll();
			ForceRecount();
		}
		for (int i = 0; i < entries.Count; i++)
		{
			KeepEntry e = entries[i];
			bool want = e.count <= 0 || CountOf(e.def) < e.count;
			if (settings.filter.Allows(e.def) != want)
				settings.filter.SetAllow(e.def, want);
		}
	}

	// how many more of this thing the zone still wants (int.MaxValue = no limit, 0 = none)
	public int StillWanted(ThingDef d)
	{
		if (!Managed)
			return int.MaxValue;
		KeepEntry e = EntryFor(d);
		if (e == null)
			return 0;
		if (e.count <= 0)
			return int.MaxValue;
		return Mathf.Max(0, e.count - CountOf(d));
	}

	public override string GetInspectString()
	{
		string s = base.GetInspectString();
		string line = locked ? "CF_Keep_InspectLocked".Translate() : "CF_Keep_InspectOpen".Translate();
		if (Managed)
		{
			var parts = new List<string>();
			foreach (KeepEntry e in entries.Take(6))
				parts.Add(e.def.LabelCap + " " + CountOf(e.def) + (e.count > 0 ? "/" + e.count : ""));
			line += "\n" + string.Join(", ", parts) + (entries.Count > 6 ? " …" : "");
		}
		return s.NullOrEmpty() ? line : s + "\n" + line;
	}

	public override IEnumerable<Gizmo> GetGizmos()
	{
		foreach (Gizmo g in base.GetGizmos())
			yield return g;
		yield return new Command_Action
		{
			defaultLabel = "CF_Keep_What".Translate(),
			defaultDesc = "CF_Keep_WhatDesc".Translate(),
			icon = ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile", reportFailure: false) ?? BaseContent.BadTex,
			defaultIconColor = new Color(0.78f, 0.6f, 1f),
			action = () => Find.WindowStack.Add(new Dialog_KeepZone(this))
		};
		if (Managed)
			yield return new Command_Action
			{
				defaultLabel = "CF_Keep_Now".Translate(),
				defaultDesc = "CF_Keep_NowDesc".Translate(),
				icon = ContentFinder<Texture2D>.Get("UI/Commands/LoadTransporter", reportFailure: false) ?? BaseContent.BadTex,
				action = () => KeepZoneBring.Now(this)
			};
		yield return new Command_Toggle
		{
			defaultLabel = "CF_Keep_Lock".Translate(),
			defaultDesc = "CF_Keep_LockDesc".Translate(),
			icon = ContentFinder<Texture2D>.Get("Things/Special/ForbiddenOverlay", reportFailure: false) ?? BaseContent.BadTex,
			isActive = () => locked,
			toggleAction = () =>
			{
				locked = !locked;
				// things become haulable / usable again (or stop being) right away
				Map?.listerHaulables.Notify_SlotGroupChanged(slotGroup);
			}
		};
	}

	public override IEnumerable<Gizmo> GetZoneAddGizmos()
	{
		Designator_ZoneAddKeep d = DesignatorUtility.FindAllowedDesignator<Designator_ZoneAddKeep>();
		if (d != null)
			yield return d;
	}

	public static bool Holds(Thing t)
	{
		if (!t.Spawned || t.def.category != ThingCategory.Item)
			return false;
		Map m = t.Map;
		// 3.3.1: only what the zone is set to keep – anything else that lands there
		// (goods dropped by a trade ship, a stray item) is hauled away as usual
		if (m == null || !(m.zoneManager.ZoneAt(t.Position) is Zone_StewardKeep k) || !k.locked)
			return false;
		// 3.4.0: with a list, everything on the list stays (also a little over the amount –
		// otherwise the extra would be carried out and back again)
		if (k.Managed)
			return k.EntryFor(t.def) != null;
		return k.settings != null && k.settings.AllowedToAccept(t);
	}
}

// 3.3.1: orbital trade beacon – one click makes a keep zone over its whole trade area.
// Traders still see and buy everything in it (trade ignores "forbidden"), only colonists
// leave it alone.
[HarmonyPatch(typeof(Building_OrbitalTradeBeacon), nameof(Building_OrbitalTradeBeacon.GetGizmos))]
public static class KeepZoneBeaconPatch
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building_OrbitalTradeBeacon __instance)
	{
		foreach (Gizmo g in __result)
			yield return g;
		if (__instance.Faction != Faction.OfPlayerSilentFail)
			yield break;
		Designator_ZoneAddKeep des = DesignatorUtility.FindAllowedDesignator<Designator_ZoneAddKeep>();
		if (des == null)
			yield break;
		yield return new Command_Action
		{
			defaultLabel = "CF_Keep_Beacon".Translate(),
			defaultDesc = "CF_Keep_BeaconDesc".Translate(),
			icon = des.icon,
			defaultIconColor = des.defaultIconColor,
			action = () =>
			{
				Find.Selector.ClearSelection();
				var cells = __instance.TradeableCells.Where(c => des.CanDesignateCell(c).Accepted).ToList();
				des.DesignateMultiCell(cells);
				Map map = __instance.Map;
				Zone_StewardKeep z = cells.Select(c => map.zoneManager.ZoneAt(c)).OfType<Zone_StewardKeep>().FirstOrDefault()
					?? __instance.TradeableCells.Select(c => map.zoneManager.ZoneAt(c)).OfType<Zone_StewardKeep>().FirstOrDefault();
				if (z != null)
					Find.WindowStack.Add(new Dialog_KeepZone(z));
			}
		};
	}
}

public class Designator_ZoneAddKeep : Designator_ZoneAddStockpile
{
	protected override bool ShowRightClickHideOptions => false;

	public Designator_ZoneAddKeep()
	{
		preset = StorageSettingsPreset.DefaultStockpile;
		zoneTypeToPlace = typeof(Zone_StewardKeep);
		defaultLabel = "CF_Keep_Label".Translate();
		defaultDesc = "CF_Keep_Desc".Translate();
		icon = ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile", reportFailure: false) ?? BaseContent.WhiteTex;
		defaultIconColor = new Color(0.78f, 0.6f, 1f);
	}

	protected override string NewZoneLabel => "CF_Keep_Name".Translate();

	protected override Zone MakeNewZone() => new Zone_StewardKeep(Find.CurrentMap.zoneManager);
}

// Every "may my colony touch this?" question goes through IsForbidden(Thing, Faction)
// (also the pawn version), so one cheap cell lookup covers all work, eating and hauling.
[HarmonyPatch(typeof(ForbidUtility), nameof(ForbidUtility.IsForbidden), new[] { typeof(Thing), typeof(Faction) })]
public static class KeepZoneForbidPatch
{
	public static void Postfix(Thing t, Faction faction, ref bool __result)
	{
		if (__result || t == null || faction == null || faction != Faction.OfPlayerSilentFail)
			return;
		if (Zone_StewardKeep.Holds(t))
			__result = true;
	}
}

public sealed class KeepEntry : IExposable
{
	public ThingDef def;
	public int count;

	public void ExposeData()
	{
		Scribe_Defs.Look(ref def, "def");
		Scribe_Values.Look(ref count, "count");
	}
}

// keeps the filters of all keep zones in step with what lies in them
public sealed class KeepZoneComp : MapComponent
{
	public KeepZoneComp(Map map) : base(map) { }

	public override void MapComponentTick()
	{
		if (Find.TickManager.TicksGame % 120 != 37)
			return;
		List<Zone> zones = map.zoneManager.AllZones;
		for (int i = 0; i < zones.Count; i++)
			if (zones[i] is Zone_StewardKeep k && k.Managed)
				k.SyncFilter(false);
	}
}

// A hauler brings only what is still missing – not a full stack over the amount.
[HarmonyPatch(typeof(Verse.AI.HaulAIUtility), nameof(Verse.AI.HaulAIUtility.HaulToCellStorageJob))]
public static class KeepZoneHaulCountPatch
{
	public static void Postfix(Verse.AI.Job __result, Thing t, IntVec3 storeCell, Pawn p)
	{
		if (__result == null || t == null || p?.Map == null)
			return;
		if (!(p.Map.zoneManager.ZoneAt(storeCell) is Zone_StewardKeep k) || !k.Managed)
			return;
		int left = k.StillWanted(t.def);
		if (left == int.MaxValue)
			return;
		__result.count = Mathf.Max(1, Mathf.Min(__result.count, left));
	}
}

// 3.4.0: "What and how much" – pick things from a list, set an amount for each
public class Dialog_KeepZone : Window
{
	private readonly Zone_StewardKeep zone;
	private string search = "";
	private bool onlyOnMap = true;
	private Vector2 scrollL, scrollR;
	private List<ThingDef> all;
	private HashSet<ThingDef> onMap;
	private readonly Dictionary<KeepEntry, string> buffers = new Dictionary<KeepEntry, string>();

	public override Vector2 InitialSize => new Vector2(820f, 600f);

	public Dialog_KeepZone(Zone_StewardKeep zone)
	{
		this.zone = zone;
		doCloseX = true;
		doCloseButton = false;
		absorbInputAroundWindow = false;
		closeOnClickedOutside = false;
		forcePause = false;
		draggable = true;
	}

	private void BuildLists()
	{
		all = DefDatabase<ThingDef>.AllDefsListForReading
			.Where(d => d.category == ThingCategory.Item && d.EverStorable(false) && !d.IsCorpse && d != ThingDefOf.MinifiedThing && !d.label.NullOrEmpty())
			.OrderBy(d => d.label).ToList();
		onMap = new HashSet<ThingDef>();
		Map m = zone.Map;
		if (m != null)
			foreach (Thing t in m.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
				if (t.def.category == ThingCategory.Item)
					onMap.Add(t.def);
	}

	private void Changed()
	{
		zone.SyncFilter(true);
		zone.Map?.listerHaulables.Notify_SlotGroupChanged(zone.slotGroup);
	}

	public override void DoWindowContents(Rect inRect)
	{
		if (all == null)
			BuildLists();
		MUI.Fill(inRect, MUI.CardBg);
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x + 4f, inRect.y, inRect.width - 230f, 34f), "CF_Keep_WinTitle".Translate(zone.label));
		Text.Font = GameFont.Small;
		Rect now = new Rect(inRect.xMax - 220f, inRect.y + 2f, 180f, 30f);
		if (zone.Managed)
		{
			if (Widgets.ButtonText(now, "CF_Keep_Now".Translate()))
				KeepZoneBring.Now(zone);
			TooltipHandler.TipRegion(now, "CF_Keep_NowDesc".Translate());
		}
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(inRect.x + 4f, inRect.y + 34f, inRect.width - 8f, 24f), "CF_Keep_WinHint".Translate());
		GUI.color = Color.white;

		float top = inRect.y + 64f;
		float h = inRect.height - (top - inRect.y);
		Rect left = new Rect(inRect.x, top, inRect.width * 0.42f, h);
		Rect right = new Rect(left.xMax + 10f, top, inRect.width - left.width - 10f, h);
		DrawPicker(left);
		DrawChosen(right);
	}

	private void DrawPicker(Rect r)
	{
		MUI.Card(r);
		r = r.ContractedBy(8f);
		search = Widgets.TextField(new Rect(r.x, r.y, r.width, 28f), search);
		if (search.NullOrEmpty())
		{
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(r.x + 6f, r.y + 4f, r.width, 24f), "CF_Keep_Search".Translate());
			GUI.color = Color.white;
		}
		Widgets.CheckboxLabeled(new Rect(r.x, r.y + 32f, r.width, 26f), "CF_Keep_OnlyOnMap".Translate(), ref onlyOnMap);

		string q = search.Trim().ToLowerInvariant();
		var list = all.Where(d => (!onlyOnMap || onMap.Contains(d)) && zone.EntryFor(d) == null
			&& (q.Length == 0 || d.label.ToLowerInvariant().Contains(q))).ToList();
		Rect outR = new Rect(r.x, r.y + 64f, r.width, r.height - 64f);
		Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * 30f);
		Widgets.BeginScrollView(outR, ref scrollL, view);
		for (int i = 0; i < list.Count; i++)
		{
			float y = i * 30f;
			if (y + 30f < scrollL.y || y > scrollL.y + outR.height)
				continue;
			ThingDef d = list[i];
			Rect row = new Rect(0f, y, view.width, 28f);
			if (Mouse.IsOver(row))
				MUI.Fill(row, MUI.CardBg2);
			Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), d);
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(row.x + 32f, row.y, row.width - 60f, row.height), d.LabelCap);
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = MUI.Accent;
			Widgets.Label(new Rect(row.xMax - 22f, row.y + 3f, 20f, 24f), "+");
			GUI.color = Color.white;
			TooltipHandler.TipRegion(row, d.description ?? "");
			if (Widgets.ButtonInvisible(row))
			{
				zone.entries.Add(new KeepEntry { def = d, count = 0 });
				Changed();
			}
		}
		Widgets.EndScrollView();
	}

	private void DrawChosen(Rect r)
	{
		MUI.Card(r, true);
		r = r.ContractedBy(8f);
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, r.y, r.width, 24f), "CF_Keep_Chosen".Translate());
		GUI.color = Color.white;
		if (zone.entries.Count == 0)
		{
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(r.x, r.y + 30f, r.width, 60f), "CF_Keep_NoneYet".Translate());
			GUI.color = Color.white;
			return;
		}
		Rect outR = new Rect(r.x, r.y + 28f, r.width, r.height - 28f);
		Rect view = new Rect(0f, 0f, outR.width - 16f, zone.entries.Count * 40f);
		KeepEntry remove = null;
		Widgets.BeginScrollView(outR, ref scrollR, view);
		for (int i = 0; i < zone.entries.Count; i++)
		{
			KeepEntry e = zone.entries[i];
			Rect row = new Rect(0f, i * 40f, view.width, 36f);
			MUI.Fill(row, MUI.CardBg2);
			Widgets.DefIcon(new Rect(row.x + 4f, row.y + 4f, 28f, 28f), e.def);
			Text.Anchor = TextAnchor.MiddleLeft;
			float x = row.x + 38f;
			Widgets.Label(new Rect(x, row.y, 150f, row.height), e.def.LabelCap.Truncate(150f));
			x += 154f;
			int have = zone.CountOf(e.def);
			bool done = e.count > 0 && have >= e.count;
			GUI.color = done ? MUI.Accent : MUI.Muted;
			Widgets.Label(new Rect(x, row.y, 90f, row.height), "CF_Keep_Have".Translate(have));
			GUI.color = Color.white;
			x += 94f;
			// amount: empty / 0 = everything there is
			if (!buffers.TryGetValue(e, out string buf))
				buf = e.count > 0 ? e.count.ToString() : "";
			string nb = Widgets.TextField(new Rect(x, row.y + 5f, 70f, 26f), buf);
			if (nb != buf)
			{
				buffers[e] = nb;
				int v;
				int.TryParse(new string(nb.Where(char.IsDigit).ToArray()), out v);
				if (v != e.count)
				{
					e.count = v;
					Changed();
				}
			}
			if (buf.NullOrEmpty() && nb.NullOrEmpty())
			{
				GUI.color = MUI.Muted;
				Widgets.Label(new Rect(x + 6f, row.y, 64f, row.height), "CF_Keep_All".Translate());
				GUI.color = Color.white;
			}
			x += 76f;
			// quick amounts
			foreach (int step in new[] { 10, 100 })
			{
				if (Widgets.ButtonText(new Rect(x, row.y + 6f, 40f, 24f), "+" + step))
				{
					e.count = Mathf.Max(0, e.count) + step;
					buffers[e] = e.count.ToString();
					Changed();
				}
				x += 44f;
			}
			Text.Anchor = TextAnchor.UpperLeft;
			if (Widgets.ButtonText(new Rect(row.xMax - 30f, row.y + 6f, 26f, 24f), "×"))
				remove = e;
			TooltipHandler.TipRegion(new Rect(row.x, row.y, 300f, row.height), "CF_Keep_RowTip".Translate());
		}
		Widgets.EndScrollView();
		if (remove != null)
		{
			zone.entries.Remove(remove);
			buffers.Remove(remove);
			if (zone.entries.Count == 0)
				zone.settings.filter.SetDisallowAll();
			Changed();
		}
	}
}

// 3.4.1: "Bring now" – free colonists drop what they do and carry the missing things
// to the zone right away (one load each; the rest follows with normal hauling).
public static class KeepZoneBring
{
	private static bool Busy(Pawn p)
	{
		Verse.AI.Job j = p.CurJob;
		if (j == null)
			return false;
		if (j.playerForced)
			return true;
		JobDef d = j.def;
		return d == JobDefOf.BeatFire || d == JobDefOf.ExtinguishSelf || d == JobDefOf.TendPatient || d == JobDefOf.Rescue
			|| d == JobDefOf.Capture || d == JobDefOf.FleeAndCower || d == JobDefOf.Flee || d == JobDefOf.LayDown;
	}

	public static void Now(Zone_StewardKeep zone)
	{
		Map map = zone?.Map;
		if (map == null || !zone.Managed)
			return;
		zone.SyncFilter(true);
		var left = new Dictionary<ThingDef, int>();
		foreach (KeepEntry e in zone.entries)
		{
			int w = zone.StillWanted(e.def);
			if (w > 0)
				left[e.def] = w;
		}
		if (left.Count == 0)
		{
			Messages.Message("CF_Keep_NowFull".Translate(), MessageTypeDefOf.RejectInput, false);
			return;
		}
		var things = new List<Thing>();
		foreach (ThingDef d in left.Keys)
			foreach (Thing t in map.listerThings.ThingsOfDef(d))
				if (t.Spawned && !(map.zoneManager.ZoneAt(t.Position) is Zone_StewardKeep))
					things.Add(t);
		var pawns = map.mapPawns.FreeColonistsSpawned
			.Where(p => !p.Downed && !p.Drafted && !p.InMentalState && p.Awake() && !Busy(p)
				&& !p.WorkTypeIsDisabled(WorkTypeDefOf.Hauling))
			.ToList();
		int jobs = 0;
		var taken = new HashSet<Thing>();
		foreach (Pawn p in pawns)
		{
			Thing best = null;
			float bestD = float.MaxValue;
			foreach (Thing t in things)
			{
				if (taken.Contains(t) || !left.TryGetValue(t.def, out int w) || w <= 0)
					continue;
				float dist = (t.Position - p.Position).LengthHorizontalSquared;
				if (dist >= bestD || t.IsForbidden(p) || !p.CanReserveAndReach(t, Verse.AI.PathEndMode.ClosestTouch, Danger.Some))
					continue;
				best = t;
				bestD = dist;
			}
			if (best == null)
				continue;
			if (!StoreUtility.TryFindBestBetterStoreCellForIn(best, p, map, StoragePriority.Unstored, p.Faction, zone.slotGroup, out IntVec3 cell))
				continue;
			Verse.AI.Job job = Verse.AI.HaulAIUtility.HaulToCellStorageJob(p, best, cell, false);
			if (job == null)
				continue;
			job.playerForced = true;
			if (!p.jobs.TryTakeOrderedJob(job, Verse.AI.JobTag.Misc))
				continue;
			taken.Add(best);
			left[best.def] -= Mathf.Max(1, job.count);
			jobs++;
		}
		if (jobs > 0)
			Messages.Message("CF_Keep_NowStarted".Translate(jobs), MessageTypeDefOf.TaskCompletion, false);
		else
			Messages.Message("CF_Keep_NowNobody".Translate(), MessageTypeDefOf.RejectInput, false);
	}
}
