using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

[HarmonyPatch(typeof(PawnColumnWorker_Label), "DoCell")]
public static class PawnLabelPatch
{
	private static readonly FieldInfo TableDefField = AccessTools.Field(typeof(PawnTable), "def");

	// 2.52.3: before the game – vanilla's name button reacts to every mouse button (jumped to the colonist)
	public static void Prefix(Rect rect, Pawn pawn, PawnTable table)
	{
		if (Event.current.type != EventType.MouseDown || Event.current.button != 2 || !Mouse.IsOver(rect))
			return;
		if (table == null || pawn == null || TableDefField == null || TableDefField.GetValue(table) != PawnTableDefOf.Work)
			return;
		ForemanGameComponent game = ForemanGameComponent.Get();
		if (game == null)
			return;
		{
			Event.current.Use();
			List<FloatMenuOption> list = new List<FloatMenuOption>();
			bool flag = game.PawnLocked(pawn);
			list.Add(new FloatMenuOption(((!flag) ? "CF_LockPawn" : "CF_UnlockPawn").Translate(), delegate
			{
				game.TogglePawn(pawn);
			}));
			foreach (WorkTypeDef item in WorkPolicies.VisibleWorkTypes())
			{
				if (!pawn.WorkTypeIsDisabled(item))
				{
					WorkTypeDef local = item;
					string label = ((!game.Prefers(pawn, item)) ? "CF_PrefersOff" : "CF_PrefersOn").Translate(item.labelShort);
					list.Add(new FloatMenuOption(label, delegate
					{
						game.TogglePreference(pawn, local);
					}));
				}
			}
			Find.WindowStack.Add(new FloatMenu(list));
		}
	}

	public static void Postfix(Rect rect, Pawn pawn, PawnTable table)
	{
		if (table == null || pawn == null || TableDefField == null || TableDefField.GetValue(table) != PawnTableDefOf.Work)
			return;
		ForemanGameComponent game = ForemanGameComponent.Get();
		if (game == null)
			return;
		if (game.PawnLocked(pawn))
		{
			GUI.DrawTexture(new Rect(rect.xMax - 14f, rect.y + (rect.height - 12f) / 2f, 12f, 12f), ForemanTex.Lock);
		}
	}
}
