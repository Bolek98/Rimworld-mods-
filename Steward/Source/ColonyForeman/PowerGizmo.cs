using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace ColonyForeman;

// 2.15: on every device of a type the steward may switch off, a toggle
// "Steward may switch off" – turn it off to keep this one device always on.
[HarmonyPatch(typeof(Building), nameof(Building.GetGizmos))]
public static class PowerGizmoPatch
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Building __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		if (__instance?.Map == null || __instance.Faction != Faction.OfPlayer)
			yield break;
		SupplyManager sm = SupplyManager.For(__instance.Map);
		if (sm == null || !sm.powerOn || !sm.powerDevices.Contains(__instance.def.defName))
			yield break;
		if (__instance.TryGetComp<CompFlickable>() == null || __instance.TryGetComp<CompPowerTrader>() == null)
			yield break;
		string id = __instance.ThingID;
		yield return new Command_Toggle
		{
			defaultLabel = "CF_P_MaySwitch".Translate(),
			defaultDesc = "CF_P_MaySwitchDesc".Translate(),
			icon = TexCommand.DesirePower,
			isActive = () => !sm.powerExempt.Contains(id),
			toggleAction = () =>
			{
				if (!sm.powerExempt.Remove(id))
				{
					sm.powerExempt.Add(id);
					// switched off by the steward right now? back on
					if (sm.poweredOffByUs.Remove(id))
						SupplyManager.SetSwitch(__instance, true);
				}
			}
		};
	}
}
