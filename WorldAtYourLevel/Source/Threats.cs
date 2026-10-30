using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ResearchLockedRaidGear
{
	// ---------------------------------------------------------------------------
	// 2.18.0: the "Threats" tab. Every event the game knows, sorted into big threats, small threats,
	// bad events (diseases, fallout, flares, mod monsters …) and – on request – everything else.
	// Unchecked = it never starts: not from the storyteller, not from quests or mods (the block sits on
	// IncidentWorker.CanFireNow, which every way of starting an event asks first). Uses the same list
	// as the old "threat events" group of the raids tab, so earlier choices stay.
	// ---------------------------------------------------------------------------
	public static class ThreatsTab
	{
		private sealed class Row
		{
			public bool header;
			public int group;
			public string defName, label, mod, desc;
			public ThingDef thing;   // group 4: a mechanoid, drone or condition causer this mod removes
		}

		// 0 big threats, 1 small threats, 2 bad events, 3 other events, 4 removed by this mod
		private const int Groups = 5;
		private const int Removed = 4;
		private static readonly int[] order = { Removed, 0, 1, 2, 3 };

		public static void Invalidate() => all = null;
		private static List<Row>[] all;
		private static Vector2 scroll;
		private static string search = string.Empty;
		private static bool showAll;

		private static int GroupOf(IncidentDef d)
		{
			if (d.category == IncidentCategoryDefOf.ThreatBig)
				return 0;
			if (d.category == IncidentCategoryDefOf.ThreatSmall)
				return 1;
			return RaidTypeFilter.IsThreatIncident(d) ? 2 : 3;
		}

		private static void Ensure()
		{
			if (all != null)
				return;
			var lists = new List<Row>[Groups];
			for (int i = 0; i < Groups; i++)
				lists[i] = new List<Row>();
			try
			{
				foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefsListForReading)
				{
					if (d == null)
						continue;
					int g = GroupOf(d);
					lists[g].Add(new Row
					{
						group = g,
						defName = d.defName,
						label = !string.IsNullOrEmpty(d.label) ? d.LabelCap.ToString() : GenText.SplitCamelCase(d.defName),
						mod = d.modContentPack?.Name ?? d.modContentPack?.PackageId ?? "?",
						desc = d.description,
					});
				}
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] could not list events: " + e.Message);
			}
			try
			{
				var things = new HashSet<ThingDef>();
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
					if (d != null && ((d.race != null && d.race.IsMechanoid) || ThreatCausers.IsCauser(d)))
						things.Add(d);
				List<string> seen = ResearchLockedRaidGearMod.Settings?.removedSeen;
				if (seen != null)
					foreach (string n in seen)
					{
						ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(n);
						if (d != null)
							things.Add(d);
					}
				foreach (ThingDef d in things)
					lists[Removed].Add(new Row
					{
						group = Removed,
						thing = d,
						defName = d.defName,
						label = !string.IsNullOrEmpty(d.label) ? d.LabelCap.ToString() : GenText.SplitCamelCase(d.defName),
						mod = d.modContentPack?.Name ?? d.modContentPack?.PackageId ?? "?",
						desc = d.description,
					});
			}
			catch (Exception e)
			{
				MLog.Warn("[World at Your Level] could not list removed things: " + e.Message);
			}
			for (int i = 0; i < Groups; i++)
				lists[i].Sort((a, b) => string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase));
			all = lists;
		}

		private static bool Match(Row r)
		{
			return string.IsNullOrEmpty(search)
			       || (r.label ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
			       || (r.defName ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
			       || (r.mod ?? "").IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool Off(List<string> list, Row r)
		{
			return r.thing != null ? ThreatCausers.WouldRemove(r.thing) : list.Contains(r.defName);
		}

		private static int Blocked(List<string> list, int g)
		{
			int n = 0;
			foreach (Row r in all[g])
				if (Off(list, r))
					n++;
			return n;
		}

		private static string Status(Row r, out Color c)
		{
			ResearchLockedRaidGearSettings st = ResearchLockedRaidGearMod.Settings;
			if (ThreatCausers.Kept(r.thing))
			{
				c = new Color(0.7f, 0.9f, 0.7f);
				return "RLRG_RemStatusKept".Translate();
			}
			if (ThreatCausers.WouldRemove(r.thing))
			{
				c = new Color(1f, 0.55f, 0.5f);
				return (r.thing.race != null ? "RLRG_RemStatusMech" : "RLRG_RemStatusCauser").Translate();
			}
			if (st?.removedSeen != null && st.removedSeen.Contains(r.defName))
			{
				c = new Color(1f, 0.75f, 0.45f);
				return "RLRG_RemStatusSeen".Translate();
			}
			c = Color.gray;
			return "RLRG_RemStatusStays".Translate();
		}

		public static void Draw(Rect r, ResearchLockedRaidGearSettings s)
		{
			Ensure();
			List<string> list = s.blockedIncidents;
			bool changed = false;

			// options above the list
			var l = new Listing_Standard();
			l.Begin(new Rect(r.x, r.y, r.width, 120f));
			l.ColumnWidth = (r.width - 34f) / 2f;
			MUI.Check(l, "RLRG_KeepCalledBosses".Translate(), "RLRG_KeepCalledBosses_Desc".Translate(), ref s.keepCalledBosses, s, "keepCalledBosses");
			l.NewColumn();
			bool before = showAll;
			l.CheckboxLabeled("RLRG_ThreatsShowAll".Translate(), ref showAll, "RLRG_ThreatsShowAll_Desc".Translate());
			l.End();
			float y = r.y + 40f;
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(r.x, y, r.width, 34f), "RLRG_ThreatsIntro".Translate());
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			y += 36f;

			int blockedAll = 0, totalAll = 0;
			foreach (int g in order)
			{
				if (g == 3 && !showAll)
					continue;
				blockedAll += Blocked(list, g);
				totalAll += all[g].Count;
			}
			GUI.color = blockedAll > 0 ? new Color(1f, 0.75f, 0.45f) : new Color(0.7f, 0.9f, 0.7f);
			Widgets.Label(new Rect(r.x, y + 4f, r.width * 0.4f, 26f), "RLRG_ThreatsSummary".Translate(blockedAll, totalAll));
			GUI.color = Color.white;
			Widgets.Label(new Rect(r.x + r.width * 0.4f, y + 4f, 80f, 26f), "RLRG_Search".Translate());
			search = Widgets.TextField(new Rect(r.x + r.width * 0.4f + 80f, y + 2f, r.width * 0.6f - 80f - 150f, 28f), search ?? string.Empty);
			Rect resetRect = new Rect(r.xMax - 140f, y, 140f, 30f);
			TooltipHandler.TipRegion(resetRect, "RLRG_ThreatsResetAll_Desc".Translate());
			if (Widgets.ButtonText(resetRect, "RLRG_RaidsResetAll".Translate()))
			{
				list.Clear();
				changed = true;
			}
			y += 36f;

			// rows
			var rows = new List<Row>();
			foreach (int g in order)
			{
				if (g == 3 && !showAll)
					continue;
				rows.Add(new Row { header = true, group = g });
				foreach (Row row in all[g])
					if (Match(row))
						rows.Add(row);
			}
			const float rowH = 26f, headerH = 34f;
			float viewH = 0f;
			foreach (Row row in rows)
				viewH += row.header ? headerH : rowH;
			Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
			Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, viewH);
			Widgets.BeginScrollView(outRect, ref scroll, viewRect);
			float cy = 0f;
			int stripe = 0;
			foreach (Row row in rows)
			{
				float h = row.header ? headerH : rowH;
				bool visible = cy + h >= scroll.y && cy <= scroll.y + outRect.height;
				Rect rect = new Rect(0f, cy, viewRect.width, h);
				cy += h;
				if (row.header)
				{
					stripe = 0;
					if (!visible)
						continue;
					Text.Font = GameFont.Medium;
					Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width * 0.5f, 32f), ("RLRG_ThreatsGroup" + row.group).Translate());
					Text.Font = GameFont.Small;
					Widgets.Label(new Rect(rect.x + rect.width * 0.5f, rect.y + 8f, rect.width * 0.18f, 26f), "RLRG_RaidsGroupCount".Translate(Blocked(list, row.group), all[row.group].Count));
					Rect offRect = new Rect(rect.xMax - 290f, rect.y + 3f, 140f, 28f);
					Rect onRect = new Rect(rect.xMax - 140f, rect.y + 3f, 140f, 28f);
					TooltipHandler.TipRegion(offRect, "RLRG_ThreatsBlockAll_Desc".Translate());
					TooltipHandler.TipRegion(onRect, "RLRG_RaidsAllowAll_Desc".Translate());
					List<string> target = row.group == Removed ? s.keepDefs : list;
					bool rem = row.group == Removed;
					if (rem)
					{
						TooltipHandler.TipRegion(offRect, "RLRG_RemRules_Desc".Translate());
						TooltipHandler.TipRegion(onRect, "RLRG_RemKeepAll_Desc".Translate());
					}
					if (Widgets.ButtonText(offRect, (rem ? "RLRG_RemRules" : "RLRG_ThreatsBlockAll").Translate()))
					{
						foreach (Row x in all[row.group])
							if (Match(x))
							{
								if (rem)
									target.Remove(x.defName);
								else if (!target.Contains(x.defName))
									target.Add(x.defName);
							}
						changed = true;
					}
					if (Widgets.ButtonText(onRect, (rem ? "RLRG_RemKeepAll" : "RLRG_RaidsAllowAll").Translate()))
					{
						foreach (Row x in all[row.group])
							if (Match(x))
							{
								if (rem)
								{
									if (!target.Contains(x.defName))
										target.Add(x.defName);
								}
								else
									target.Remove(x.defName);
							}
						changed = true;
					}
					Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
					continue;
				}
				stripe++;
				if (!visible)
					continue;
				if (stripe % 2 == 0)
					Widgets.DrawLightHighlight(rect);
				bool allowed = !Off(list, row);
				bool was = allowed;
				Widgets.Checkbox(new Vector2(rect.x + 4f, rect.y + 1f), ref allowed);
				if (allowed != was)
				{
					List<string> target = row.thing != null ? s.keepDefs : list;
					bool add = row.thing != null ? allowed : !allowed;
					if (add)
					{
						if (!target.Contains(row.defName))
							target.Add(row.defName);
					}
					else
						target.Remove(row.defName);
					changed = true;
				}
				if (!allowed)
					GUI.color = new Color(1f, 0.55f, 0.5f);
				Widgets.Label(new Rect(rect.x + 36f, rect.y + 3f, rect.width * 0.42f - 36f, rowH), row.label);
				GUI.color = Color.gray;
				if (row.thing != null)
				{
					string st = Status(row, out Color sc);
					GUI.color = sc;
					Widgets.Label(new Rect(rect.x + rect.width * 0.42f, rect.y + 3f, rect.width * 0.3f, rowH), st);
					GUI.color = Color.gray;
				}
				else
					Widgets.Label(new Rect(rect.x + rect.width * 0.42f, rect.y + 3f, rect.width * 0.3f, rowH), row.defName);
				Widgets.Label(new Rect(rect.x + rect.width * 0.72f, rect.y + 3f, rect.width * 0.28f, rowH), row.mod);
				GUI.color = Color.white;
				if (Mouse.IsOver(rect))
				{
					string tip = row.label + " (" + row.defName + ")\n" + "RLRG_RaidsSource".Translate(row.mod).ToString();
					if (!string.IsNullOrEmpty(row.desc))
						tip += "\n\n" + row.desc;
					if (row.thing != null)
						tip += "\n\n" + "RLRG_RemTip".Translate().ToString();
					else
						tip += "\n\n" + (allowed ? "RLRG_RaidsAllowedTip" : "RLRG_ThreatsBlockedTip").Translate().ToString();
					TooltipHandler.TipRegion(rect, tip);
				}
			}
			Widgets.EndScrollView();
			if (changed)
				RaidTypeFilter.Invalidate();
		}
	}
}
