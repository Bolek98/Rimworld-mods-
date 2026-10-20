using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyForeman;

[HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "DoCell")]
public static class WorkCellPatch
{
	public static void Postfix(PawnColumnWorker_WorkPriority __instance, Rect rect, Pawn pawn)
	{
		WorkTypeDef workTypeDef = ((__instance.def == null) ? null : __instance.def.workType);
		ForemanGameComponent foremanGameComponent = ForemanGameComponent.Get();
		if (workTypeDef != null && foremanGameComponent != null && pawn != null)
		{
			if (Event.current.type == EventType.MouseDown && Event.current.button == 2 && Mouse.IsOver(rect))
			{
				foremanGameComponent.ToggleCell(pawn, workTypeDef);
				SoundDefOf.Click.PlayOneShotOnCamera();
				Event.current.Use();
			}
			if (foremanGameComponent.AnyLock(pawn, workTypeDef))
			{
				GUI.DrawTexture(new Rect(rect.xMax - 12f, rect.y + 1f, 11f, 11f), ForemanTex.Lock);
			}
			string text = ForemanMapComponent.For(pawn.MapHeld)?.Reason(pawn, workTypeDef);
			if (text != null)
			{
				TooltipHandler.TipRegion(rect, new TipSignal(text, (pawn.thingIDNumber * 397) ^ workTypeDef.index));
			}
		}
	}
}
