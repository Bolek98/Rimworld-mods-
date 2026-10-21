using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ColonyForeman;

[HarmonyPatch(typeof(PawnColumnWorker_WorkPriority), "DoCell")]
public static class WorkCellPatch
{
	public static void Prefix(PawnColumnWorker_WorkPriority __instance, Pawn pawn, out int __state)
	{
		__state = -1;
		WorkTypeDef w = __instance.def?.workType;
		if (w != null && pawn?.workSettings != null && pawn.workSettings.EverWork)
			__state = pawn.workSettings.GetPriority(w);
	}

	public static void Postfix(PawnColumnWorker_WorkPriority __instance, Rect rect, Pawn pawn, int __state)
	{
		WorkTypeDef workTypeDef = ((__instance.def == null) ? null : __instance.def.workType);
		ForemanGameComponent foremanGameComponent = ForemanGameComponent.Get();
		if (workTypeDef != null && foremanGameComponent != null && pawn != null)
		{
			// 2.49.6: changed by hand -> locked, the steward keeps your value
			if (__state >= 0 && pawn.workSettings != null && pawn.workSettings.GetPriority(workTypeDef) != __state
				&& foremanGameComponent.LockCell(pawn, workTypeDef))
				Messages.Message("CF_AutoLocked".Translate(pawn.LabelShort, workTypeDef.labelShort), MessageTypeDefOf.SilentInput, false);
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
