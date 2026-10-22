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

namespace ColonyForeman;

// ===========================================================================
// Steward – Supplies (add-on of the Steward core)
// Stock targets (hunting, wood, foraging, mining), production bills,
// livestock, meals, power, household (chunks, loose items, pockets), master bills.
// Works on its own – the core only has to be there.
// ===========================================================================
public sealed class SuppliesSettings : ModSettings
{
	public bool masterBills = false;
	public bool supplyMessages = true;
	public bool usePickUpAndHaul = true;
	public bool noStorageAlert = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref masterBills, "masterBills", false);
		Scribe_Values.Look(ref supplyMessages, "supplyMessages", true);
		Scribe_Values.Look(ref usePickUpAndHaul, "usePickUpAndHaul", true);
		Scribe_Values.Look(ref noStorageAlert, "noStorageAlert", true);
	}
}

public sealed class SuppliesMod : Mod
{
	public static SuppliesSettings S;
	public static SuppliesMod Instance;

	public SuppliesMod(ModContentPack content) : base(content)
	{
		Instance = this;
		bool fresh = !File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename($"Mod_{content.FolderName}_{GetType().Name}.xml")));
		S = GetSettings<SuppliesSettings>();
		if (fresh)
			FromOld(S);
		new Harmony("misak.steward.supplies").PatchAll(typeof(SuppliesMod).Assembly);
		StewardApi.MapExpose += SupState.Expose;
		StewardApi.MapTick += c =>
		{
			if (Find.TickManager.TicksGame % 2500 >= 250)
				return;
			try { MasterBillsRule.MasterBills(c, c.map); }
			catch (Exception ex) { MLog.WarnOnce("[Steward] master bills failed: " + ex.Message, 0x5e0001); }
		};
		StewardApi.GameStarted += () =>
		{
			WorkGiver_StewardHaulProducts.Reset();
			WorkGiver_StewardTidyHome.Reset();
			PickUpAndHaulCompat.Reset();
			MainTabWindow_Supplies.ResetOpenWindow();
		};
		MLog.Info("[Steward] Supplies loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "CF2U_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect)
	{
		var l = new Listing_Standard();
		l.Begin(inRect);
		l.Label("CF2U_Hint".Translate());
		l.GapLine();
		l.CheckboxLabeled("CF2U_Messages".Translate(), ref S.supplyMessages);
		l.CheckboxLabeled("CF2U_MasterBills".Translate(), ref S.masterBills, "CF2U_MasterBillsTip".Translate());
		l.CheckboxLabeled("CF2U_NoStorage".Translate(), ref S.noStorageAlert, "CF2U_NoStorageTip".Translate());
		if (PickUpAndHaulCompat.Present)
			l.CheckboxLabeled("CF2U_PUAH".Translate(), ref S.usePickUpAndHaul, "CF2U_PUAHTip".Translate());
		l.End();
	}

	private static void FromOld(SuppliesSettings s)
	{
		try
		{
			string path = Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename("Mod_Steward_ForemanMod.xml"));
			if (!File.Exists(path))
				return;
			var doc = new XmlDocument();
			doc.Load(path);
			bool B(string k, bool def)
			{
				XmlNode n = doc.SelectSingleNode("SettingsBlock/ModSettings/" + k);
				return n != null && bool.TryParse(n.InnerText, out bool b) ? b : def;
			}
			s.masterBills = B("masterBills", s.masterBills);
			s.supplyMessages = B("supplyMessages", s.supplyMessages);
			s.usePickUpAndHaul = B("usePickUpAndHaul", s.usePickUpAndHaul);
			s.noStorageAlert = B("noStorageAlert", s.noStorageAlert);
			s.Write();
			MLog.Info("[Steward] Supplies: settings taken over from Steward 1.x.");
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] Supplies: old settings could not be read: " + ex.Message);
		}
	}
}

// master bills adopted by the steward (saved in the core's map record, Steward 1.x name)
public sealed class SupState
{
	private static readonly ConditionalWeakTable<ForemanMapComponent, SupState> table = new ConditionalWeakTable<ForemanMapComponent, SupState>();

	public HashSet<string> masterBills = new HashSet<string>();
	private List<string> list = new List<string>();

	public static SupState Of(ForemanMapComponent c) => table.GetValue(c, _ => new SupState());

	public static void Expose(ForemanMapComponent c)
	{
		SupState s = Of(c);
		if (Scribe.mode == LoadSaveMode.Saving)
			s.list = s.masterBills.ToList();
		Scribe_Collections.Look(ref s.list, "cfMasterBills", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
			s.masterBills = new HashSet<string>(s.list ?? new List<string>());
	}
}
