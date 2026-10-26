using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

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
	}

	public override string GetInspectString()
	{
		string s = base.GetInspectString();
		string line = locked ? "CF_Keep_InspectLocked".Translate() : "CF_Keep_InspectOpen".Translate();
		return s.NullOrEmpty() ? line : s + "\n" + line;
	}

	public override IEnumerable<Gizmo> GetGizmos()
	{
		foreach (Gizmo g in base.GetGizmos())
			yield return g;
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
		return m != null && m.zoneManager.ZoneAt(t.Position) is Zone_StewardKeep k && k.locked
			&& k.settings != null && k.settings.AllowedToAccept(t);
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
				des.DesignateMultiCell(__instance.TradeableCells.Where(c => des.CanDesignateCell(c).Accepted).ToList());
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
