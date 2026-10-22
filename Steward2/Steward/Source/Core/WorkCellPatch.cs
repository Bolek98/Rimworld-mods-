using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyForeman;

[HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "DoCell")]
public static class WorkCellPatch
{
	// 2.52.3: the middle click is taken BEFORE the game – vanilla uses up every mouse button on the work box
	// (in checkbox mode a middle click even switched the work on/off), so the lock only worked on the edge
	public static void Prefix(PawnColumnWorker_WorkPriority __instance, Rect rect, Pawn pawn)
	{
		if (Event.current.type != EventType.MouseDown || Event.current.button != 2 || !Mouse.IsOver(rect))
			return;
		WorkTypeDef w = __instance.def?.workType;
		ForemanGameComponent game = ForemanGameComponent.Get();
		if (w == null || game == null || pawn == null)
			return;
		game.ToggleCell(pawn, w);
		SoundDefOf.Click.PlayOneShotOnCamera();
		Event.current.Use();
	}

	public static void Postfix(PawnColumnWorker_WorkPriority __instance, Rect rect, Pawn pawn)
	{
		WorkTypeDef workTypeDef = ((__instance.def == null) ? null : __instance.def.workType);
		ForemanGameComponent foremanGameComponent = ForemanGameComponent.Get();
		if (workTypeDef != null && foremanGameComponent != null && pawn != null)
		{
			if (foremanGameComponent.AnyLock(pawn, workTypeDef))
			{
				GUI.DrawTexture(new Rect(rect.xMax - 12f, rect.y + 1f, 11f, 11f), ForemanTex.Lock);
			}
			string text = ForemanMapComponent.For(pawn.MapHeld)?.TipFor(pawn, workTypeDef);
			if (text != null)
			{
				TooltipHandler.TipRegion(rect, new TipSignal(text, (pawn.thingIDNumber * 397) ^ workTypeDef.index));
			}
		}
	}
}
