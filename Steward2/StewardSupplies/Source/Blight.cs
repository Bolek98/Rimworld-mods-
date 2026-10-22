using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace ColonyForeman;

// ---------------------------------------------------------------------------
// Steward 2.14.4: blighted plants are marked for cutting by themselves –
// otherwise nobody cuts them and blight spreads through the fields. Only
// plants of the colony: in growing zones, hydroponics / plant pots and the
// home area. Checked every in-game hour; at most 60 marks per check.
// The "blight" crisis then gives plant cutting more colonists.
// ---------------------------------------------------------------------------
public sealed partial class SupplyManager
{
	public bool cutBlight = true;
	public List<string> blightMarks = new List<string>();
	private int nextBlightCheck;

	private void ExposeBlight()
	{
		Scribe_Values.Look(ref cutBlight, "cutBlight", true);
		Scribe_Collections.Look(ref blightMarks, "blightMarks", LookMode.Value);
		if (Scribe.mode == LoadSaveMode.PostLoadInit && blightMarks == null)
			blightMarks = new List<string>();
	}

	private void TickBlight()
	{
		int now = Find.TickManager.TicksGame;
		if (!cutBlight || now < nextBlightCheck)
			return;
		nextBlightCheck = now + 2500;
		try
		{
			MarkBlight();
		}
		catch (Exception ex)
		{
			MLog.Warn("[Steward] blight: " + ex.Message);
		}
	}

	private void MarkBlight()
	{
		DesignationManager dm = map.designationManager;
		Area_Home home = map.areaManager.Home;
		List<Thing> plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant);
		int n = 0;
		for (int i = 0; i < plants.Count && n < 60; i++)
		{
			if (!(plants[i] is Plant p) || !p.Spawned || !p.Blighted)
				continue;
			IntVec3 c = p.Position;
			bool ours = home[c] || map.zoneManager.ZoneAt(c) is Zone_Growing || c.GetEdifice(map) is Building_PlantGrower;
			if (!ours || map.fogGrid.IsFogged(c))
				continue;
			if (dm.DesignationOn(p, DesignationDefOf.CutPlant) != null || dm.DesignationOn(p, DesignationDefOf.HarvestPlant) != null)
				continue;
			dm.AddDesignation(new Designation(p, DesignationDefOf.CutPlant));
			blightMarks.Add(p.ThingID);
			n++;
		}
		if (n > 0 && (SuppliesMod.S == null || SuppliesMod.S.supplyMessages))
			Messages.Message("CF_BlightMarked".Translate(n), MessageTypeDefOf.CautionInput, false);
	}
}
