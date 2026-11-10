using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace StewardKit;

// 1.1.0: what colonists may or may not carry in their pockets – any item from any mod, for everyone
// or for one role. "Always" = Steward never takes it out of their pockets (Supplies' unloading, the
// game's own dropping, the kit tidy-up); "Never" = it is taken out of the pockets at home.
public sealed class ItemRule : IExposable
{
	public string def;
	public string role = "";   // "" = everyone
	public int mode;           // 1 always, 2 never

	public ItemRule() { }
	public ItemRule(string def, string role, int mode) { this.def = def; this.role = role ?? ""; this.mode = mode; }

	public void ExposeData()
	{
		Scribe_Values.Look(ref def, "def");
		Scribe_Values.Look(ref role, "role", "");
		Scribe_Values.Look(ref mode, "mode", 0);
	}
}

public static class ItemRules
{
	public const int Always = 1, Never = 2;
	private static Dictionary<string, int> lookup;   // "role|def" -> mode
	private static readonly Dictionary<ThingDef, bool> otherMod = new Dictionary<ThingDef, bool>();

	public static void Changed() => lookup = null;

	private static Dictionary<string, int> Lookup()
	{
		if (lookup != null) return lookup;
		lookup = new Dictionary<string, int>();
		foreach (ItemRule r in KitMod.Settings?.itemRules ?? new List<ItemRule>())
			if (r?.def != null && r.mode != 0)
				lookup[(r.role ?? "") + "|" + r.def] = r.mode;
		return lookup;
	}

	public static int ModeFor(string role, ThingDef d)
	{
		if (d == null) return 0;
		var l = Lookup();
		if (l.Count == 0) return 0;
		if (!string.IsNullOrEmpty(role) && l.TryGetValue(role + "|" + d.defName, out int m)) return m;
		return l.TryGetValue("|" + d.defName, out int a) ? a : 0;
	}

	public static int ModeFor(Pawn p, ThingDef d)
	{
		if (p == null || d == null || Lookup().Count == 0) return 0;
		string role = null;
		try { role = Kits.RoleOf(p); } catch { }
		return ModeFor(role, d);
	}

	// things another mod keeps in pockets on purpose (its own carry setting) – never unloaded by us.
	// Dubs Bad Hygiene: water bottles for drinking.
	public static bool OtherModCarries(ThingDef d)
	{
		if (d == null) return false;
		if (otherMod.TryGetValue(d, out bool v)) return v;
		string pid = d.modContentPack?.PackageId ?? "";
		v = d.ingestible != null && pid.IndexOf("dubsbadhygiene", StringComparison.OrdinalIgnoreCase) >= 0
		    && (d.defName.IndexOf("Water", StringComparison.OrdinalIgnoreCase) >= 0 || d.defName.IndexOf("Bottle", StringComparison.OrdinalIgnoreCase) >= 0);
		otherMod[d] = v;
		return v;
	}

	// kit tidy-up: "never" items go out of the pockets
	public static void DropBanned(Pawn p, Map m)
	{
		if (Lookup().Count == 0) return;
		ThingOwner<Thing> inv = p.inventory.innerContainer;
		string role = null;
		try { role = Kits.RoleOf(p); } catch { }
		for (int i = inv.Count - 1; i >= 0; i--)
		{
			Thing t = inv[i];
			if (t?.def == null || ModeFor(role, t.def) != Never) continue;
			inv.TryDrop(t, p.Position, m, ThingPlaceMode.Near, t.stackCount, out Thing _);
		}
	}

	// ---------------- settings tab ----------------
	private static string scope = "";
	private static string search = "";
	private static bool onlyRules;
	private static Vector2 scroll;
	private static List<ThingDef> items;

	private static List<ThingDef> Items()
	{
		if (items != null) return items;
		items = DefDatabase<ThingDef>.AllDefsListForReading
			.Where(d => d.category == ThingCategory.Item && d.EverHaulable && !d.IsCorpse && !d.IsWeapon && !d.IsApparel && !d.destroyOnDrop && d.label != null)
			.OrderBy(d => d.label).ToList();
		return items;
	}

	public static void Draw(Rect r)
	{
		KitSettings s = KitMod.Settings;
		float y = r.y;
		string intro = "SK_Items_Intro".Translate();
		float ih = Text.CalcHeight(intro, r.width);
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, y, r.width, ih), intro);
		GUI.color = Color.white;
		y += ih + 6f;

		Widgets.Label(new Rect(r.x, y + 4f, 80f, 26f), "SK_Items_For".Translate());
		if (Widgets.ButtonText(new Rect(r.x + 80f, y, 200f, 28f), scope == "" ? (string)"SK_Items_All".Translate() : Kits.RoleLabel(scope)))
		{
			var opts = new List<FloatMenuOption> { new FloatMenuOption("SK_Items_All".Translate(), () => { scope = ""; scroll = Vector2.zero; }) };
			foreach (string role in Kits.Roles.Where(x => x != "Everyone"))
			{
				string rr = role;
				opts.Add(new FloatMenuOption(Kits.RoleLabel(rr), () => { scope = rr; scroll = Vector2.zero; }));
			}
			Find.WindowStack.Add(new FloatMenu(opts));
		}
		Widgets.Label(new Rect(r.x + 300f, y + 4f, 70f, 26f), "SK_Items_Search".Translate());
		search = Widgets.TextField(new Rect(r.x + 370f, y, 220f, 28f), search ?? "");
		Widgets.CheckboxLabeled(new Rect(r.x + 610f, y + 2f, 220f, 26f), "SK_Items_OnlyRules".Translate(), ref onlyRules);
		y += 36f;

		string q = (search ?? "").Trim().ToLowerInvariant();
		var list = Items().Where(d =>
		{
			if (onlyRules && !s.itemRules.Any(x => x.def == d.defName && (x.role ?? "") == scope)) return false;
			if (q.Length == 0) return true;
			return d.label.ToLowerInvariant().Contains(q) || d.defName.ToLowerInvariant().Contains(q) || (d.modContentPack?.Name ?? "").ToLowerInvariant().Contains(q);
		}).ToList();

		float rowH = 30f;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		Rect view = new Rect(0f, 0f, outR.width - 18f, Mathf.Max(outR.height, list.Count * rowH));
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
			ThingDef d = list[i];
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), d);
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(row.x + 32f, row.y, row.width * 0.42f - 32f, rowH), d.LabelCap.ToString().Truncate(row.width * 0.42f - 34f));
			GUI.color = MUI.Muted;
			string mod = d.modContentPack == null ? "" : d.modContentPack.Name;
			if (OtherModCarries(d)) mod += " · " + "SK_Items_OtherMod".Translate();
			Widgets.Label(new Rect(row.x + row.width * 0.42f, row.y, row.width * 0.33f, rowH), mod.Truncate(row.width * 0.33f - 4f));
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
			ItemRule rule = s.itemRules.FirstOrDefault(x => x.def == d.defName && (x.role ?? "") == scope);
			int mode = rule?.mode ?? 0;
			string label = mode == Always ? "SK_Items_Always".Translate() : mode == Never ? "SK_Items_Never".Translate() : (string)"SK_Items_Default".Translate();
			if (scope != "" && mode == 0)
			{
				int all = ModeFor("", d);
				if (all != 0) label += " (" + (all == Always ? "SK_Items_Always".Translate() : "SK_Items_Never".Translate()) + ")";
			}
			Rect b = new Rect(row.x + row.width * 0.75f, row.y + 1f, row.width * 0.25f - 4f, rowH - 4f);
			if (mode == Always) GUI.color = new Color(0.6f, 1f, 0.6f);
			else if (mode == Never) GUI.color = new Color(1f, 0.6f, 0.6f);
			if (Widgets.ButtonText(b, label))
			{
				ThingDef dd = d;
				Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
				{
					new FloatMenuOption("SK_Items_Default".Translate(), () => Set(dd, 0)),
					new FloatMenuOption("SK_Items_Always".Translate(), () => Set(dd, Always)),
					new FloatMenuOption("SK_Items_Never".Translate(), () => Set(dd, Never)),
				}));
			}
			GUI.color = Color.white;
		}
		Widgets.EndScrollView();
	}

	private static void Set(ThingDef d, int mode)
	{
		KitSettings s = KitMod.Settings;
		s.itemRules.RemoveAll(x => x.def == d.defName && (x.role ?? "") == scope);
		if (mode != 0) s.itemRules.Add(new ItemRule(d.defName, scope, mode));
		Changed();
		Kits.ClearCache();
		s.Write();
	}
}
