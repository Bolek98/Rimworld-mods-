using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyForeman;

[HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "DoHeader")]
public static class WorkHeaderPatch
{
	// 2.52.3: before the game, which uses up the click on the lower part of the header
	public static void Prefix(PawnColumnWorker_WorkPriority __instance, Rect rect)
	{
		if (Event.current.type != EventType.MouseDown || Event.current.button != 2 || !Mouse.IsOver(rect))
			return;
		WorkTypeDef w = __instance.def?.workType;
		ForemanGameComponent game = ForemanGameComponent.Get();
		if (w == null || game == null)
			return;
		game.ToggleWork(w);
		SoundDefOf.Click.PlayOneShotOnCamera();
		Event.current.Use();
	}

	public static void Postfix(PawnColumnWorker_WorkPriority __instance, Rect rect)
	{
		WorkTypeDef workTypeDef = ((__instance.def == null) ? null : __instance.def.workType);
		ForemanGameComponent foremanGameComponent = ForemanGameComponent.Get();
		if (workTypeDef != null && foremanGameComponent != null)
		{
			if (foremanGameComponent.WorkLocked(workTypeDef))
			{
				GUI.DrawTexture(new Rect(rect.center.x - 6f, rect.yMax - 13f, 12f, 12f), ForemanTex.Lock);
			}
			TooltipHandler.TipRegion(rect, "CF_HeaderHint".Translate());
		}
	}
}
