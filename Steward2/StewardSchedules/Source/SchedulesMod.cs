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
// Steward – Schedules (add-on of the Steward core)
// Automatic schedules (recreation hours that adapt, optional night shift),
// rest for the exhausted, care for colonists close to a mental break, and
// small comforts (sitting down to eat, why there is no toilet …).
// ===========================================================================
public sealed class SchedulesSettings : ModSettings
{
	public bool autoSchedule = true;
	public bool nightShift = false;
	public bool scheduleCare = true;
	public bool sitToEat = true;
	public bool quietMessages = false;
	public bool crisisMessages = true;

	public override void ExposeData()
	{
		Scribe_Values.Look(ref autoSchedule, "autoSchedule", true);
		Scribe_Values.Look(ref nightShift, "nightShift", false);
		Scribe_Values.Look(ref scheduleCare, "scheduleCare", true);
		Scribe_Values.Look(ref sitToEat, "sitToEat", true);
		Scribe_Values.Look(ref quietMessages, "quietMessages2", false);
		Scribe_Values.Look(ref crisisMessages, "crisisMessages", true);
	}
}

public sealed class SchedulesMod : Mod
{
	public static SchedulesSettings S;

	public SchedulesMod(ModContentPack content) : base(content)
	{
		bool fresh = !File.Exists(Path.Combine(GenFilePaths.ConfigFolderPath, GenText.SanitizeFilename($"Mod_{content.FolderName}_{GetType().Name}.xml")));
		S = GetSettings<SchedulesSettings>();
		if (fresh)
			FromOld(S);
		new Harmony("misak.steward.schedules").PatchAll(typeof(SchedulesMod).Assembly);
		StewardApi.MapExpose += SchedState.Expose;
		StewardApi.MapTick += SchedState.Tick;
		StewardApi.GameStarted += () =>
		{
			MoodCare.ResetGame();
			Comfort.ResetGame();
			ScheduleTabWatch.EditedByYou.Clear();
		};
		MLog.Info("[Steward] Schedules loaded.");
	}

	public override string SettingsCategory() => "Steward – " + "CF2S_Name".Translate();

	public override void DoSettingsWindowContents(Rect inRect)
	{
		var l = new Listing_Standard();
		l.Begin(inRect);
		l.CheckboxLabeled("CF2S_Auto".Translate(), ref S.autoSchedule, "CF2S_AutoTip".Translate());
		if (S.autoSchedule)
			l.CheckboxLabeled("   " + "CF2S_Night".Translate(), ref S.nightShift, "CF2S_NightTip".Translate());
		l.CheckboxLabeled("CF2S_Care".Translate(), ref S.scheduleCare, "CF2S_CareTip".Translate());
		l.CheckboxLabeled("CF2S_SitToEat".Translate(), ref S.sitToEat, "CF2S_SitToEatTip".Translate());
		l.CheckboxLabeled("CF2S_Messages".Translate(), ref S.crisisMessages);
		l.CheckboxLabeled("CF2S_Quiet".Translate(), ref S.quietMessages, "CF2S_QuietTip".Translate());
		l.End();
	}

	private static void FromOld(SchedulesSettings s)
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
			s.autoSchedule = B("autoSchedule", s.autoSchedule);
			s.nightShift = B("nightShift", s.nightShift);
			s.scheduleCare = B("scheduleCare", s.scheduleCare);
			s.sitToEat = B("sitToEat", s.sitToEat);
			s.quietMessages = B("quietMessages2", s.quietMessages);
			s.crisisMessages = B("crisisMessages", s.crisisMessages);
			s.Write();
			MLog.Info("[Steward] Schedules: settings taken over from Steward 1.x.");
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] Schedules: old settings could not be read: " + ex.Message);
		}
	}
}

// per-map state, saved in the core's map record with the Steward 1.x names
public sealed class SchedState
{
	private static readonly ConditionalWeakTable<ForemanMapComponent, SchedState> table = new ConditionalWeakTable<ForemanMapComponent, SchedState>();

	public Dictionary<int, string> scheduled = new Dictionary<int, string>();
	public Dictionary<int, string> schedHold = new Dictionary<int, string>();
	public bool manualReset2492;

	public static SchedState Of(ForemanMapComponent c) => table.GetValue(c, _ => new SchedState());

	public static void Expose(ForemanMapComponent c)
	{
		SchedState s = Of(c);
		Scribe_Collections.Look(ref s.scheduled, "cfScheduled", LookMode.Value, LookMode.Value);
		Scribe_Collections.Look(ref s.schedHold, "cfSchedHold", LookMode.Value, LookMode.Value);
		Scribe_Values.Look(ref s.manualReset2492, "cfManualReset2492", false);
		if (Scribe.mode == LoadSaveMode.PostLoadInit)
		{
			s.scheduled ??= new Dictionary<int, string>();
			s.schedHold ??= new Dictionary<int, string>();
		}
	}

	// every 250 ticks per map; works whether the priority planning is on or not
	public static void Tick(ForemanMapComponent comp)
	{
		Map map = comp.map;
		if (!map.IsPlayerHome)
			return;
		SchedState st = Of(comp);
		// the steward is off on this map: nothing changes, held hours go back
		if (!comp.auto)
		{
			if (st.schedHold.Count > 0)
			{
				try { ScheduleCare.ReleaseAll(comp, map); }
				catch (Exception ex) { MLog.WarnOnce("[Steward] give back hours failed: " + ex.Message, 0x5d0002); }
			}
			return;
		}
		try
		{
			AutoSchedules.Schedules(comp, map);
			ScheduleCare.Tick(comp, map);
			MoodCare.Tick(comp, map);
		}
		catch (Exception ex)
		{
			MLog.WarnOnce("[Steward] schedules failed: " + ex, 0x5d0001);
		}
	}
}
