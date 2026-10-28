using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTurrets
{
	// ---------------------------------------------------------------------------
	// 1.5.0
	// - Turrets page: pick which manned turrets and mortars fire on their own (the rest keep
	//   needing a colonist).
	// - Materials page: every turret that is rearmed (steel, chemfuel, its own ammo from a mod …)
	//   can take extra materials you tick – metals, wood, stone blocks, other resources and ammo.
	// ---------------------------------------------------------------------------
	public static class TurretPage
	{
		private static ATSettings S => ATMod.S;

		private static readonly Color CardOn = new Color(0.20f, 0.32f, 0.22f, 0.55f);
		private static readonly Color CardOff = new Color(0.16f, 0.16f, 0.16f, 0.55f);
		private static readonly Color Edge = new Color(0.35f, 0.35f, 0.35f, 0.8f);
		private static readonly Color EdgeOn = new Color(0.45f, 0.75f, 0.45f, 0.9f);
		private static readonly Color Muted = new Color(0.7f, 0.7f, 0.7f);

		private static string BaseName(ThingDef d) =>
			d.defName.EndsWith(WallTurrets.Suffix) ? d.defName.Substring(0, d.defName.Length - WallTurrets.Suffix.Length) : d.defName;

		// ---------------- which turrets fire alone ----------------
		public static bool AutoOn(ThingDef d)
		{
			if (S?.autoPick == null || d == null) return true;
			return !S.autoPick.TryGetValue(BaseName(d), out bool v) || v;
		}

		private static void SetAuto(ThingDef d, bool on)
		{
			if (on) S.autoPick.Remove(d.defName);
			else S.autoPick[d.defName] = false;
		}

		private static List<ThingDef> manned;

		private static List<ThingDef> Manned
		{
			get
			{
				if (manned == null)
					manned = DefDatabase<ThingDef>.AllDefsListForReading
						.Where(d => d.thingClass != null && typeof(Building_TurretGun).IsAssignableFrom(d.thingClass) && !d.defName.EndsWith(WallTurrets.Suffix)
							&& (d.designationCategory != null || d.minifiedDef != null || (d.comps != null && d.comps.Any(c => c is CompProperties_Mannable))))
						.OrderBy(d => d.label ?? d.defName).ToList();
				return manned;
			}
		}

		private static string filterT = "", filterM = "";
		private static Vector2 scrollT, scrollM;

		public static void DrawTurrets(Rect r)
		{
			float y = r.y;
			Text.Font = GameFont.Tiny;
			GUI.color = Muted;
			string desc = "AT_TurretsDesc".Translate();
			float dh = Text.CalcHeight(desc, r.width);
			Widgets.Label(new Rect(r.x, y, r.width, dh), desc);
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			y += dh + 6f;
			List<ThingDef> all = Manned;
			if (all.Count == 0) { Widgets.Label(new Rect(r.x, y, r.width, 26f), "AT_TurretsNone".Translate()); return; }
			float bw = 170f;
			if (Widgets.ButtonText(new Rect(r.x, y, bw, 28f), "AT_WallAll".Translate())) S.autoPick.Clear();
			if (Widgets.ButtonText(new Rect(r.x + bw + 6f, y, bw, 28f), "AT_WallClear".Translate())) foreach (ThingDef d in all) SetAuto(d, false);
			Text.Anchor = TextAnchor.MiddleRight;
			Widgets.Label(new Rect(r.x + 2 * bw + 12f, y, r.xMax - r.x - 2 * bw - 12f, 28f), "AT_TurretsCount".Translate(all.Count(AutoOn), all.Count));
			Text.Anchor = TextAnchor.UpperLeft;
			y += 34f;
			Widgets.Label(new Rect(r.x, y + 3f, 80f, 24f), "AT_WallSearch".Translate());
			filterT = Widgets.TextField(new Rect(r.x + 80f, y, Mathf.Min(260f, r.width - 80f), 26f), filterT ?? "");
			y += 34f;
			List<ThingDef> shown = Filter(all, filterT);
			Cards(new Rect(r.x, y, r.width, r.yMax - y), shown, ref scrollT, d => AutoOn(d), (d, v) => SetAuto(d, v),
				d => (IsManned(d) ? (IsMortar(d) ? "AT_KindMortar" : "AT_KindManned") : (IsMortar(d) ? "AT_KindMortarAuto" : "AT_KindAuto")).Translate() + "  ·  "
					+ (d.comps != null && d.comps.Any(c => c is CompProperties_Refuelable) ? FuelText(d) + "  ·  " : "") + (d.modContentPack?.Name ?? "?"), null);
		}

		public static bool IsManned(ThingDef d) => d.comps != null && d.comps.Any(c => c is CompProperties_Mannable);

		private static bool IsMortar(ThingDef d)
		{
			ThingDef gun = d.building?.turretGunDef;
			return gun?.comps != null && gun.comps.Any(c => c is CompProperties_ChangeableProjectile);
		}

		// an automatic turret you unticked holds fire until you pick a target
		public static bool HoldsFire(Building_TurretGun t) =>
			t != null && t.Faction == Faction.OfPlayer && !IsManned(t.def) && !AutoOn(t.def);

		private static List<ThingDef> Filter(List<ThingDef> all, string f) =>
			f.NullOrEmpty() ? all : all.Where(d => (d.label ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0
				|| (d.modContentPack?.Name ?? "").IndexOf(f, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

		// cards in two columns, only the visible rows are drawn
		private static void Cards(Rect outR, List<ThingDef> shown, ref Vector2 scroll, Func<ThingDef, bool> get, Action<ThingDef, bool> set, Func<ThingDef, string> sub, Action<ThingDef> button)
		{
			const float h = 44f, gap = 6f;
			int cols = outR.width > 620f ? 2 : 1;
			int rows = (shown.Count + cols - 1) / cols;
			Rect view = new Rect(0f, 0f, outR.width - 16f, rows * (h + gap));
			float w = (view.width - gap * (cols - 1)) / cols;
			Widgets.BeginScrollView(outR, ref scroll, view);
			int firstRow = Mathf.Max(0, (int)(scroll.y / (h + gap)) - 1);
			int lastRow = Mathf.Min(rows - 1, (int)((scroll.y + outR.height) / (h + gap)) + 1);
			for (int i = firstRow * cols; i <= Mathf.Min(shown.Count - 1, lastRow * cols + cols - 1); i++)
			{
				ThingDef d = shown[i];
				bool on = get(d);
				Rect c = new Rect((i % cols) * (w + gap), (i / cols) * (h + gap), w, h);
				Widgets.DrawBoxSolidWithOutline(c, on ? CardOn : CardOff, on ? EdgeOn : Edge);
				Widgets.DrawHighlightIfMouseover(c);
				Rect icon = new Rect(c.x + 4f, c.y + 4f, h - 8f, h - 8f);
				try { Widgets.DefIcon(icon, d); } catch { }
				float right = button != null ? 120f : 34f;
				float tx = icon.xMax + 8f, tw = c.xMax - right - tx;
				Widgets.Label(new Rect(tx, c.y + 3f, tw, 22f), d.LabelCap.Resolve().Truncate(tw));
				Text.Font = GameFont.Tiny;
				GUI.color = Muted;
				string st = sub(d);
				Widgets.Label(new Rect(tx, c.y + 23f, tw, 18f), st.Truncate(tw));
				GUI.color = Color.white;
				Text.Font = GameFont.Small;
				if (button != null)
				{
					if (Widgets.ButtonText(new Rect(c.xMax - 114f, c.y + 8f, 108f, 28f), "AT_FuelEdit".Translate()))
						button(d);
				}
				else
				{
					bool now = on;
					Widgets.Checkbox(c.xMax - 30f, c.y + (h - 24f) / 2f, ref now);
					if (Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 34f, c.height))) now = !on;
					if (now != on) set(d, now);
				}
				if (Mouse.IsOver(c)) TooltipHandler.TipRegion(c, new TipSignal(() => d.LabelCap + "\n" + st, d.shortHash ^ 0x51));
			}
			Widgets.EndScrollView();
		}

		// ---------------- materials ----------------
		private static Dictionary<ThingDef, List<ThingDef>> original;   // turret → what it took before any change
		private static List<ThingDef> refuelable;

		public static List<ThingDef> Refuelable
		{
			get
			{
				if (refuelable == null)
					refuelable = DefDatabase<ThingDef>.AllDefsListForReading
						.Where(d => d.thingClass != null && typeof(Building_Turret).IsAssignableFrom(d.thingClass) && !d.defName.EndsWith(WallTurrets.Suffix)
							&& d.comps != null && d.comps.Any(c => c is CompProperties_Refuelable rp && rp.fuelFilter != null))
						.OrderBy(d => d.label ?? d.defName).ToList();
				return refuelable;
			}
		}

		private static IEnumerable<CompProperties_Refuelable> Props(ThingDef d) =>
			d.comps == null ? Enumerable.Empty<CompProperties_Refuelable>() : d.comps.OfType<CompProperties_Refuelable>().Where(p => p.fuelFilter != null);

		public static List<ThingDef> Original(ThingDef turret)
		{
			Snapshot();
			ThingDef key = DefDatabase<ThingDef>.GetNamedSilentFail(BaseName(turret)) ?? turret;
			return original.TryGetValue(key, out var l) ? l : new List<ThingDef>();
		}

		private static void Snapshot()
		{
			if (original != null) return;
			original = new Dictionary<ThingDef, List<ThingDef>>();
			foreach (ThingDef d in Refuelable)
			{
				var l = new List<ThingDef>();
				foreach (var p in Props(d)) l.AddRange(p.fuelFilter.AllowedThingDefs);
				original[d] = l.Distinct().ToList();
			}
		}

		public static bool Extra(ThingDef turret, ThingDef fuel) => S.fuelExtra.Contains(BaseName(turret) + "|" + fuel.defName);

		public static void SetExtra(ThingDef turret, ThingDef fuel, bool on)
		{
			string k = turret.defName + "|" + fuel.defName;
			if (on) { if (!S.fuelExtra.Contains(k)) S.fuelExtra.Add(k); }
			else S.fuelExtra.Remove(k);
			ApplyFuel(false);
		}

		// what the global switches add (only to turrets that are rearmed with metal, as before)
		private static IEnumerable<ThingDef> GlobalAdds(List<ThingDef> orig)
		{
			if (S == null || orig.Count == 0 || !orig.All(MetalFuel.IsMetal)) yield break;
			foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (d.category != ThingCategory.Item) continue;
				if ((S.anyMetal && MetalFuel.IsMetal(d) && (S.allowSilver || d != ThingDefOf.Silver))
					|| (S.fuelWood && MetalFuel.IsCat(d, StuffCategoryDefOf.Woody))
					|| (S.fuelStone && MetalFuel.IsCat(d, StuffCategoryDefOf.Stony)))
					yield return d;
			}
		}

		// sets every turret's fuel filter to: what it took + the global switches + your extra materials
		public static void ApplyFuel(bool startup)
		{
			if (S == null) return;
			Snapshot();
			int changed = 0;
			foreach (ThingDef t in Refuelable)
			{
				var want = new HashSet<ThingDef>(original[t]);
				foreach (ThingDef d in GlobalAdds(original[t])) want.Add(d);
				foreach (string k in S.fuelExtra)
				{
					int bar = k.IndexOf('|');
					if (bar <= 0 || k.Substring(0, bar) != t.defName) continue;
					ThingDef f = DefDatabase<ThingDef>.GetNamedSilentFail(k.Substring(bar + 1));
					if (f != null) want.Add(f);
				}
				bool more = want.Count > original[t].Count;
				if (more) changed++;
				// the turret and its wall version
				foreach (ThingDef td in new[] { t, DefDatabase<ThingDef>.GetNamedSilentFail(t.defName + WallTurrets.Suffix) })
				{
					if (td == null) continue;
					foreach (var p in Props(td))
					{
						p.fuelFilter.SetDisallowAll();
						foreach (ThingDef f in want) p.fuelFilter.SetAllow(f, true);
					}
				}
			}
			if (startup) Log.Message("[Auto Turrets] " + changed + " turrets take more materials than before (" + S.fuelExtra.Count + " picked by hand).");
		}

		private static List<ThingDef> materials;

		// what a turret could be rearmed with: metals, wood, stone, other raw or made resources, ammo
		public static List<ThingDef> Materials
		{
			get
			{
				if (materials == null)
					materials = DefDatabase<ThingDef>.AllDefsListForReading
						.Where(d => d.category == ThingCategory.Item && d.stackLimit > 1 && d.uiIcon != null && !d.IsCorpse && !d.destroyOnDrop
							&& !d.IsIngestible && !d.IsMedicine && !d.IsDrug && d.BaseMarketValue > 0f
							&& (d.IsStuff || IsAmmo(d) || (d.thingCategories != null && d.thingCategories.Any(c => c.defName.StartsWith("Resources") || c.defName == "Manufactured"))))
						.OrderBy(d => Group(d)).ThenBy(d => d.label).ToList();
				return materials;
			}
		}

		private static bool IsAmmo(ThingDef d) =>
			d.defName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 || (d.thingCategories != null && d.thingCategories.Any(c => c.defName.IndexOf("Ammo", StringComparison.OrdinalIgnoreCase) >= 0 || c.defName.IndexOf("Shell", StringComparison.OrdinalIgnoreCase) >= 0));

		public static int Group(ThingDef d)
		{
			if (MetalFuel.IsMetal(d)) return 0;
			if (MetalFuel.IsCat(d, StuffCategoryDefOf.Woody)) return 1;
			if (MetalFuel.IsCat(d, StuffCategoryDefOf.Stony)) return 2;
			if (IsAmmo(d)) return 3;
			return 4;
		}

		private static string FuelText(ThingDef t)
		{
			var now = new List<ThingDef>();
			foreach (var p in Props(t)) now.AddRange(p.fuelFilter.AllowedThingDefs);
			now = now.Distinct().ToList();
			if (now.Count == 0) return "–";
			string first = string.Join(", ", now.Take(3).Select(x => x.label).ToArray());
			return now.Count > 3 ? first + " +" + (now.Count - 3) : first;
		}

		public static void DrawMaterials(Rect r)
		{
			float y = r.y;
			Widgets.DrawLineHorizontal(r.x, y, r.width);
			y += 6f;
			Text.Font = GameFont.Tiny;
			GUI.color = Muted;
			string desc = "AT_FuelPerTurret".Translate();
			float dh = Text.CalcHeight(desc, r.width);
			Widgets.Label(new Rect(r.x, y, r.width, dh), desc);
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			y += dh + 4f;
			List<ThingDef> all = Refuelable;
			if (all.Count == 0) { Widgets.Label(new Rect(r.x, y, r.width, 26f), "AT_FuelNone".Translate()); return; }
			Widgets.Label(new Rect(r.x, y + 3f, 80f, 24f), "AT_WallSearch".Translate());
			filterM = Widgets.TextField(new Rect(r.x + 80f, y, Mathf.Min(260f, r.width - 80f), 26f), filterM ?? "");
			y += 32f;
			Cards(new Rect(r.x, y, r.width, r.yMax - y), Filter(all, filterM), ref scrollM, d => S.fuelExtra.Any(k => k.StartsWith(d.defName + "|")), null,
				d => "AT_FuelTakes".Translate(FuelText(d)), d => Find.WindowStack.Add(new Dialog_TurretFuel(d)));
		}
	}

	// the materials of one turret
	public class Dialog_TurretFuel : Window
	{
		private readonly ThingDef turret;
		private Vector2 scroll;
		private string filter = "";

		public Dialog_TurretFuel(ThingDef turret)
		{
			this.turret = turret;
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = true;
			closeOnClickedOutside = true;
		}

		public override Vector2 InitialSize => new Vector2(620f, 640f);

		public override void PostClose()
		{
			base.PostClose();
			try { LoadedModManager.GetMod<ATMod>()?.WriteSettings(); } catch { }
		}

		public override void DoWindowContents(Rect r)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(r.x + 40f, r.y, r.width - 40f, 32f), turret.LabelCap);
			try { Widgets.DefIcon(new Rect(r.x, r.y, 32f, 32f), turret); } catch { }
			Text.Font = GameFont.Small;
			float y = r.y + 36f;
			List<ThingDef> orig = TurretPage.Original(turret);
			Text.Font = GameFont.Tiny;
			GUI.color = new Color(0.7f, 0.7f, 0.7f);
			Widgets.Label(new Rect(r.x, y, r.width, 34f), "AT_FuelOriginal".Translate(string.Join(", ", orig.Select(d => d.label).ToArray())));
			GUI.color = Color.white;
			Text.Font = GameFont.Small;
			y += 36f;
			Widgets.Label(new Rect(r.x, y + 3f, 80f, 24f), "AT_WallSearch".Translate());
			filter = Widgets.TextField(new Rect(r.x + 80f, y, 240f, 26f), filter ?? "");
			if (Widgets.ButtonText(new Rect(r.xMax - 150f, y, 150f, 26f), "AT_WallClear".Translate()))
				foreach (ThingDef d in TurretPage.Materials) if (TurretPage.Extra(turret, d)) TurretPage.SetExtra(turret, d, false);
			y += 32f;
			List<ThingDef> list = TurretPage.Materials.Where(d => filter.NullOrEmpty() || d.label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
			Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 50f);
			const float rowH = 28f;
			// group headers
			var rows = new List<object>();
			int g = -1;
			foreach (ThingDef d in list)
			{
				int gg = TurretPage.Group(d);
				if (gg != g) { rows.Add(gg); g = gg; }
				rows.Add(d);
			}
			Rect view = new Rect(0f, 0f, outR.width - 16f, rows.Count * rowH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			for (int i = 0; i < rows.Count; i++)
			{
				Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
				if (rows[i] is int grp)
				{
					GUI.color = new Color(0.55f, 0.85f, 0.55f);
					Text.Font = GameFont.Tiny;
					Widgets.Label(new Rect(row.x + 4f, row.y + 8f, row.width, 20f), ("AT_FuelGroup" + grp).Translate().ToString().ToUpper());
					Text.Font = GameFont.Small;
					GUI.color = Color.white;
					continue;
				}
				ThingDef d = (ThingDef)rows[i];
				if (i % 2 == 0) Widgets.DrawLightHighlight(row);
				Widgets.DefIcon(new Rect(row.x + 4f, row.y + 1f, 24f, 24f), d);
				bool isOrig = orig.Contains(d);
				bool on = isOrig || TurretPage.Extra(turret, d);
				Widgets.Label(new Rect(row.x + 34f, row.y + 2f, row.width - 80f, 24f), d.LabelCap.ToString() + (isOrig ? "  (" + "AT_FuelOrig".Translate().ToString() + ")" : ""));
				bool now = on;
				Widgets.Checkbox(row.xMax - 28f, row.y + 1f, ref now, 24f, isOrig);
				if (!isOrig && Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 30f, row.height))) now = !on;
				if (!isOrig && now != on) TurretPage.SetExtra(turret, d, now);
			}
			Widgets.EndScrollView();
		}
	}
}
