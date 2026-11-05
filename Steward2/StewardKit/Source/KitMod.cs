using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using AutoPharmacy;

namespace StewardKit;

public sealed class KitSettings : ModSettings
{
	public bool recommended = true;
	public bool kitsOn = true;
	public bool refill = true;
	public bool everyonePack = true;     // the "Everyone" lines (emergency pack) for colonists of any role (0.2.0: on by default)
	public bool combatDrugKit = true;    // soldiers carry a combat drug
	public float weightLimit = 0.35f;    // share of carrying capacity the kit may use
	public int maxDistance = 60;         // cells to walk for a refill
	public int checkHours = 2;           // how often a colonist with a full kit is checked again
	public bool dropExtras = true;
	public int spare = 1;                // extras above the kit that may stay
	public bool caravanKits = true;
	public bool slaves;
	public bool customized;              // the player changed the kit table
	public int kitsVersion;              // 0.2.0: version of the automatic table this one came from
	public bool pharmacyMigrated;
	public List<KitLine> lines;
	public AutoPharmacySettings pharmacy = new AutoPharmacySettings();

	public List<KitLine> Lines
	{
		get
		{
			// 0.2.0: an untouched table follows the newer, bigger defaults
			if (lines == null || (lines.Count == 0 && !customized) || (!customized && kitsVersion < Kits.DefaultsVersion))
			{
				lines = Kits.Defaults();
				kitsVersion = Kits.DefaultsVersion;
			}
			return lines;
		}
	}

	public override void ExposeData()
	{
		Scribe_Values.Look(ref recommended, "recommended", true);
		Scribe_Values.Look(ref kitsOn, "kitsOn", true);
		Scribe_Values.Look(ref refill, "refill", true);
		Scribe_Values.Look(ref everyonePack, "everyonePack", true);
		Scribe_Values.Look(ref combatDrugKit, "combatDrugKit", true);
		Scribe_Values.Look(ref weightLimit, "weightLimit", 0.35f);
		Scribe_Values.Look(ref maxDistance, "maxDistance", 60);
		Scribe_Values.Look(ref checkHours, "checkHours", 2);
		Scribe_Values.Look(ref dropExtras, "dropExtras", true);
		Scribe_Values.Look(ref spare, "spare", 1);
		Scribe_Values.Look(ref caravanKits, "caravanKits", true);
		Scribe_Values.Look(ref slaves, "slaves", false);
		Scribe_Values.Look(ref customized, "customized", false);
		Scribe_Values.Look(ref kitsVersion, "kitsVersion", 0);
		Scribe_Values.Look(ref pharmacyMigrated, "pharmacyMigrated", false);
		Scribe_Collections.Look(ref lines, "kitLines", LookMode.Deep);
		Scribe_Deep.Look(ref pharmacy, "pharmacy");
		if (pharmacy == null) pharmacy = new AutoPharmacySettings();
	}
}

public sealed class KitMod : Mod
{
	public static KitSettings Settings;
	public const string Version = "0.2.3";
	private static readonly KitSettings recKit = new KitSettings();
	private static readonly AutoPharmacySettings recAP = new AutoPharmacySettings();

	private enum Tab { Overview, Kits, Pharmacy, Drugs, Combat, Regular, Caravans, Weight, Colonists, Advanced }
	private static Tab tab = Tab.Overview;
	private static Vector2 scroll;
	private static float viewH = 600f;

	public KitMod(ModContentPack content) : base(content)
	{
		Settings = GetSettings<KitSettings>();
		AutoPharmacyMod.Settings = Settings.pharmacy;
		MigrateOldPharmacy();
		UseKitRec();
		if (Settings.recommended)
			ApplyRecommended();
		var h = new Harmony("misak.steward.kit");
		KitPatches.Apply(h);
		AutoPharmacyMod.Patch(h);
		MUI.PatchWindow(h, typeof(KitMod));
		MNews.Register(content, Version);
		Log.Message("[Steward – Kit] v" + Version + " loaded.");
	}

	// the old stand-alone Auto Pharmacy settings are taken over once
	private static void MigrateOldPharmacy()
	{
		if (Settings.pharmacyMigrated || AutoPharmacyMod.OldModActive) return;
		Settings.pharmacyMigrated = true;
		try
		{
			string dir = GenFilePaths.ConfigFolderPath;
			string path = Directory.Exists(dir) ? Directory.GetFiles(dir, "Mod_*_AutoPharmacyMod.xml").FirstOrDefault() : null;
			if (path == null) return;
			AutoPharmacySettings old = null;
			Scribe.loader.InitLoading(path);
			try
			{
				Scribe_Deep.Look(ref old, "ModSettings");
			}
			finally
			{
				Scribe.loader.FinalizeLoading();
			}
			if (old != null)
			{
				Settings.pharmacy = old;
				AutoPharmacyMod.Settings = old;
				Log.Message("[Steward – Kit] settings of Auto Pharmacy taken over.");
			}
		}
		catch (Exception e)
		{
			try { Scribe.ForceStop(); } catch { }
			Log.Warning("[Steward – Kit] could not take over the Auto Pharmacy settings: " + e.Message);
		}
	}

	private static void UseKitRec() => MUI.Init(recKit, null, "recommended", "customized", "pharmacyMigrated");
	private static void UseApRec() => MUI.Init(recAP, null, "recommended");

	private static void ApplyRecommended()
	{
		UseKitRec();
		MUI.ApplyAll(Settings);
		UseApRec();
		MUI.ApplyAll(Settings.pharmacy);
		Settings.pharmacy.recommended = true;
		UseKitRec();
	}

	public override string SettingsCategory() => "SK_Name".CanTranslate() ? (string)"SK_Name".Translate() : "Steward – Kit";

	private static void Check(Listing_Standard l, string key, ref bool v, string field)
	{
		string tip = key + "_Desc";
		MUI.Check(l, key.Translate(), tip.CanTranslate() ? tip.Translate().ToString() : null, ref v, Settings, field);
	}

	private static void Muted(Listing_Standard l, string text)
	{
		GUI.color = MUI.Muted;
		l.Label(text);
		GUI.color = Color.white;
	}

	public override void DoSettingsWindowContents(Rect r)
	{
		Kits.ClearCache();   // 0.2.2: changes in this window count at once
		MUI.Version(r, "v" + Version + " · Bolek98");
		// two rows of tabs (ten tabs do not fit in one)
		var all = (Tab[])Enum.GetValues(typeof(Tab));
		int half = (all.Length + 1) / 2;
		float top = r.y + 64f;
		for (int row = 0; row < 2; row++)
		{
			var recs = new List<TabRecord>();
			for (int i = row * half; i < Math.Min(all.Length, (row + 1) * half); i++)
			{
				Tab t = all[i];
				recs.Add(new TabRecord(("SK_Tab_" + t).Translate(), () => { tab = t; scroll = Vector2.zero; }, tab == t));
			}
			TabDrawer.DrawTabs(new Rect(r.x, r.y + 32f + row * 32f, r.width, 1f), recs, Mathf.Max(120f, r.width / half));
		}
		Rect body = new Rect(r.x, top + 6f, r.width, r.yMax - top - 6f);
		Widgets.DrawMenuSection(body);
		body = body.ContractedBy(10f);
		try
		{
			switch (tab)
			{
				case Tab.Overview: Overview(body); break;
				case Tab.Kits: KitTable(body); break;
				case Tab.Pharmacy: UseApRec(); AutoPharmacyMod.DrawGeneral(body, Settings.recommended); break;
				case Tab.Drugs: UseApRec(); AutoPharmacyMod.DrawDrugs(body); break;
				case Tab.Combat: UseApRec(); AutoPharmacyMod.DrawCombat(body, Settings.recommended); break;
				case Tab.Regular: UseApRec(); AutoPharmacyMod.DrawRegular(body, Settings.recommended); break;
				case Tab.Caravans: Caravans(body); break;
				case Tab.Weight: Weight(body); break;
				case Tab.Colonists: Colonists(body); break;
				case Tab.Advanced: Advanced(body); break;
			}
		}
		finally
		{
			UseKitRec();
		}
	}

	// ---------------- Overview ----------------
	private static List<string> recBullets;

	private static void Overview(Rect r)
	{
		if (recBullets == null)
			recBullets = new List<string> { "SK_Rec1".Translate(), "SK_Rec2".Translate(), "SK_Rec3".Translate(), "SK_Rec4".Translate() };
		float y = r.y;
		y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), Settings, Settings.recommended, v =>
		{
			Settings.recommended = v;
			Settings.pharmacy.recommended = v;
			if (v) ApplyRecommended();
		}, recBullets) + 6f;
		var l = new Listing_Standard();
		Rect sw = new Rect(r.x, y, r.width, 64f);
		l.Begin(sw);
		l.ColumnWidth = (sw.width - 24f) / 2f;
		Check(l, "SK_KitsOn", ref Settings.kitsOn, "kitsOn");
		l.NewColumn();
		Check(l, "SK_Refill", ref Settings.refill, "refill");
		l.End();
		y += 64f;
		Widgets.Label(new Rect(r.x, y, r.width, 26f), "SK_WhoCarries".Translate());
		y += 28f;
		Map map = Find.CurrentMap;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		if (map == null)
		{
			GUI.color = MUI.Muted;
			Widgets.Label(outR, "SK_NoGame".Translate());
			GUI.color = Color.white;
			return;
		}
		List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.LabelShort).ToList();
		// 0.2.0: what the whole colony carries
		var tot = new Dictionary<string, int[]>();
		foreach (Pawn pp in pawns)
			foreach (KitLine w in Kits.Wanted(pp))
			{
				if (!tot.TryGetValue(w.item, out int[] v)) tot[w.item] = v = new int[2];
				v[0] += Math.Min(Kits.Have(pp, w.item), w.count);
				v[1] += w.count;
			}
		if (tot.Count > 0)
		{
			float tx = outR.x;
			Widgets.Label(new Rect(tx, outR.y, 150f, 26f), "SK_Totals".Translate());
			tx += 150f;
			foreach (var kv in tot)
			{
				string ttxt = kv.Value[0] + "/" + kv.Value[1];
				float tw = Text.CalcSize(ttxt).x + 30f;
				if (tx + tw > outR.xMax) break;
				Texture2D tic = Kits.ItemIcon(kv.Key);
				if (tic != null) GUI.DrawTexture(new Rect(tx, outR.y + 1f, 24f, 24f), tic);
				GUI.color = kv.Value[0] >= kv.Value[1] ? MUI.Accent : MUI.Warn;
				Widgets.Label(new Rect(tx + 26f, outR.y + 2f, tw - 26f, 24f), ttxt);
				GUI.color = Color.white;
				TooltipHandler.TipRegion(new Rect(tx, outR.y, tw, 26f), Kits.ItemLabel(kv.Key) + ": " + ttxt);
				tx += tw + 8f;
			}
			outR.yMin += 30f;
		}
		float rowH = 32f;
		Rect view = new Rect(0f, 0f, outR.width - 18f, Mathf.Max(outR.height, pawns.Count * rowH));
		Widgets.BeginScrollView(outR, ref scroll, view);
		float nameW = Mathf.Min(170f, view.width * 0.22f), roleW = Mathf.Min(150f, view.width * 0.18f);
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.Label(new Rect(row.x + 4f, row.y + 4f, nameW, 24f), p.LabelShort.Truncate(nameW));
			string role = Kits.RoleOf(p);
			Widgets.Label(new Rect(row.x + 8f + nameW, row.y + 4f, roleW, 24f), (role == null ? (string)"SK_RoleOff".Translate() : Kits.RoleLabel(role)).Truncate(roleW));
			float x = row.x + 14f + nameW + roleW;
			foreach (KitLine w in Kits.Wanted(p))
			{
				int have = Kits.Have(p, w.item);
				string txt = have + "/" + w.count;
				float tw = Text.CalcSize(txt).x + 30f;
				if (x + tw > row.xMax) break;
				Texture2D ic = Kits.ItemIcon(w.item);
				if (ic != null) GUI.DrawTexture(new Rect(x, row.y + 3f, 24f, 24f), ic);
				GUI.color = have >= w.count ? MUI.Accent : MUI.Warn;
				Widgets.Label(new Rect(x + 26f, row.y + 4f, tw - 26f, 24f), txt);
				GUI.color = Color.white;
				TooltipHandler.TipRegion(new Rect(x, row.y, tw, rowH), Kits.ItemLabel(w.item) + ": " + txt);
				x += tw + 6f;
			}
		}
		Widgets.EndScrollView();
	}

	// ---------------- Kit table ----------------
	private static List<string> addable;

	private static List<string> Addable()
	{
		if (addable != null) return addable;
		addable = new List<string>(Kits.Categories);
		foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
		{
			if (d.category != ThingCategory.Item || d.IsCorpse || d.IsWeapon || d.IsApparel || d.MadeFromStuff || d.destroyOnDrop) continue;
			bool ok = d.IsMedicine || d.IsDrug || (d.ingestible != null && (d.ingestible.IsMeal || d.ingestible.preferability >= FoodPreferability.RawTasty))
				|| d == ThingDefOf.ComponentIndustrial || d.defName == "ComponentSpacer";
			if (!ok) continue;
			if (d.GetStatValueAbstract(StatDefOf.Mass) > 1.5f) continue;
			addable.Add(d.defName);
		}
		return addable;
	}

	private static void KitTable(Rect r)
	{
		float y = r.y;
		GUI.color = MUI.Muted;
		float hh = Text.CalcHeight("SK_KitsHelp".Translate(), r.width - 230f);
		Widgets.Label(new Rect(r.x, y, r.width - 230f, hh), "SK_KitsHelp".Translate());
		GUI.color = Color.white;
		if (Widgets.ButtonText(new Rect(r.xMax - 220f, y, 220f, 30f), "SK_ResetKits".Translate()))
		{
			Settings.lines = Kits.Defaults();
			Settings.customized = false;
			Settings.kitsVersion = Kits.DefaultsVersion;
		}
		// 0.2.0: an edited table keeps the player's numbers – a note says newer defaults exist
		if (Settings.customized && Settings.kitsVersion < Kits.DefaultsVersion)
		{
			GUI.color = MUI.Warn;
			Widgets.Label(new Rect(r.xMax - 220f, y + 32f, 220f, 40f), "SK_NewDefaults".Translate());
			GUI.color = Color.white;
		}
		y += Mathf.Max(hh, 34f) + 6f;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
		Rect view = new Rect(0f, 0f, outR.width - 18f, viewH);
		Widgets.BeginScrollView(outR, ref scroll, view);
		float cy = 0f;
		List<KitLine> lines = Settings.Lines;
		foreach (string role in Kits.Roles)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(0f, cy, view.width, 32f), Kits.RoleLabel(role));
			Text.Font = GameFont.Small;
			cy += 32f;
			foreach (KitLine line in lines.Where(x => x.role == role).ToList())
			{
				Rect row = new Rect(12f, cy, view.width - 12f, 30f);
				Widgets.DrawLightHighlight(row);
				Texture2D ic = Kits.ItemIcon(line.item);
				if (ic != null) GUI.DrawTexture(new Rect(row.x + 4f, row.y + 3f, 24f, 24f), ic);
				float labW = Mathf.Max(80f, row.width - 34f - 190f);
				Widgets.Label(new Rect(row.x + 34f, row.y + 4f, labW, 24f), Kits.ItemLabel(line.item).Truncate(labW));
				float bx = row.xMax - 186f;
				if (Widgets.ButtonText(new Rect(bx, row.y + 2f, 30f, 26f), "−")) { line.count = Math.Max(0, line.count - 1); Settings.customized = true; }
				Text.Anchor = TextAnchor.MiddleCenter;
				Widgets.Label(new Rect(bx + 32f, row.y + 2f, 40f, 26f), line.count.ToString());
				Text.Anchor = TextAnchor.UpperLeft;
				if (Widgets.ButtonText(new Rect(bx + 74f, row.y + 2f, 30f, 26f), "+")) { line.count = Math.Min(50, line.count + 1); Settings.customized = true; }
				if (Widgets.ButtonText(new Rect(row.xMax - 76f, row.y + 2f, 72f, 26f), "SK_Remove".Translate()))
				{
					lines.Remove(line);
					Settings.customized = true;
				}
				cy += 32f;
			}
			if (Widgets.ButtonText(new Rect(12f, cy, 200f, 28f), "SK_AddItem".Translate()))
			{
				string rr = role;
				var menu = new List<FloatMenuOption>();
				foreach (string it in Addable())
				{
					string li = it;
					if (lines.Any(x => x.role == rr && x.item == li)) continue;
					menu.Add(new FloatMenuOption(Kits.ItemLabel(li), () => { lines.Add(new KitLine(rr, li, 1)); Settings.customized = true; }, Kits.ItemIcon(li), Color.white));
				}
				if (menu.Count > 0) Find.WindowStack.Add(new FloatMenu(menu));
			}
			cy += 40f;
		}
		viewH = cy + 10f;
		Widgets.EndScrollView();
	}

	// ---------------- Caravans ----------------
	private static void Caravans(Rect r)
	{
		var l = new Listing_Standard();
		MUI.BeginLock(Settings.recommended);
		try
		{
			l.Begin(new Rect(r.x, r.y, Mathf.Min(r.width, 640f), r.height));
			Check(l, "SK_CaravanKits", ref Settings.caravanKits, "caravanKits");
			Muted(l, "SK_CaravanHelp".Translate());
			l.End();
		}
		finally { MUI.EndLock(r); }
	}

	// ---------------- Weight & limits ----------------
	private static void Weight(Rect r)
	{
		var l = new Listing_Standard();
		MUI.BeginLock(Settings.recommended);
		try
		{
			l.Begin(new Rect(r.x, r.y, Mathf.Min(r.width, 640f), r.height));
			Settings.weightLimit = MUI.Slider(l, "SK_Weight".Translate(Mathf.RoundToInt(Settings.weightLimit * 100f)), Settings.weightLimit, 0.05f, 0.8f, "SK_Weight_Desc".Translate(), Settings, "weightLimit");
			Settings.maxDistance = Mathf.RoundToInt(MUI.Slider(l, "SK_Distance".Translate(Settings.maxDistance), Settings.maxDistance, 15f, 150f, "SK_Distance_Desc".Translate(), Settings, "maxDistance"));
			Settings.checkHours = Mathf.RoundToInt(MUI.Slider(l, "SK_CheckHours".Translate(Settings.checkHours), Settings.checkHours, 1f, 24f, "SK_CheckHours_Desc".Translate(), Settings, "checkHours"));
			Check(l, "SK_DropExtras", ref Settings.dropExtras, "dropExtras");
			if (Settings.dropExtras)
				Settings.spare = Mathf.RoundToInt(MUI.Slider(l, "SK_Spare".Translate(Settings.spare), Settings.spare, 0f, 5f, "SK_Spare_Desc".Translate(), Settings, "spare"));
			l.End();
		}
		finally { MUI.EndLock(r); }
	}

	// ---------------- Colonists ----------------
	private static void Colonists(Rect r)
	{
		Map map = Find.CurrentMap;
		GUI.color = MUI.Muted;
		float hh = Text.CalcHeight("SK_ColonistsHelp".Translate(), r.width);
		Widgets.Label(new Rect(r.x, r.y, r.width, hh), "SK_ColonistsHelp".Translate());
		GUI.color = Color.white;
		Rect outR = new Rect(r.x, r.y + hh + 6f, r.width, r.height - hh - 6f);
		if (map == null || KitGame.Get() == null)
		{
			GUI.color = MUI.Muted;
			Widgets.Label(outR, "SK_NoGame".Translate());
			GUI.color = Color.white;
			return;
		}
		KitGame g = KitGame.Get();
		List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.LabelShort).ToList();
		float rowH = 32f;
		Rect view = new Rect(0f, 0f, outR.width - 18f, Mathf.Max(outR.height, pawns.Count * rowH));
		Widgets.BeginScrollView(outR, ref scroll, view);
		float nameW = Mathf.Min(220f, view.width * 0.35f);
		for (int i = 0; i < pawns.Count; i++)
		{
			Pawn p = pawns[i];
			Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
			if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.Label(new Rect(row.x + 4f, row.y + 4f, nameW, 24f), p.LabelShort.Truncate(nameW));
			g.overrides.TryGetValue(p.thingIDNumber, out string o);
			string cur = o == null ? "SK_Mode_Auto".Translate(Kits.RoleOf(p) is string rr ? Kits.RoleLabel(rr) : (string)"SK_RoleOff".Translate()) : o == "Off" ? (string)"SK_RoleOff".Translate() : Kits.RoleLabel(o);
			if (Widgets.ButtonText(new Rect(row.x + nameW + 12f, row.y + 2f, Mathf.Min(260f, row.width - nameW - 16f), 26f), cur))
			{
				Pawn pp = p;
				var menu = new List<FloatMenuOption>
				{
					new FloatMenuOption("SK_Mode_AutoPlain".Translate(), () => { g.overrides.Remove(pp.thingIDNumber); Kits.ClearCache(); }),
					new FloatMenuOption("SK_RoleOff".Translate(), () => { g.overrides[pp.thingIDNumber] = "Off"; Kits.ClearCache(); })
				};
				foreach (string role in Kits.Roles)
				{
					string rl = role;
					menu.Add(new FloatMenuOption(Kits.RoleLabel(rl), () => { g.overrides[pp.thingIDNumber] = rl; Kits.ClearCache(); }));
				}
				Find.WindowStack.Add(new FloatMenu(menu));
			}
		}
		Widgets.EndScrollView();
	}

	// ---------------- Advanced ----------------
	private static void Advanced(Rect r)
	{
		var l = new Listing_Standard();
		MUI.BeginLock(Settings.recommended);
		try
		{
			l.Begin(new Rect(r.x, r.y, Mathf.Min(r.width, 640f), r.height));
			Check(l, "SK_EveryonePack", ref Settings.everyonePack, "everyonePack");
			Check(l, "SK_CombatDrugKit", ref Settings.combatDrugKit, "combatDrugKit");
			Check(l, "SK_Slaves", ref Settings.slaves, "slaves");
			l.End();
		}
		finally { MUI.EndLock(r); }
		var l2 = new Listing_Standard();
		l2.Begin(new Rect(r.x, r.y + 120f, Mathf.Min(r.width, 640f), r.height - 120f));
		Muted(l2, (Kits.RolesModOn ? "SK_RolesOn" : "SK_RolesOff").Translate());
		if (AutoPharmacyMod.OldModActive)
		{
			GUI.color = MUI.Warn;
			l2.Label("SK_OldPharmacy".Translate());
			GUI.color = Color.white;
		}
		l2.End();
	}
}
