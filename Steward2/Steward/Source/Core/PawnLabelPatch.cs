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
			foreach (WorkTypeDef item in WorkCatalog.Visible)
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
			StewardTools.CopyMenu(pawn, list);
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
			// 3.1.0: a locked colonist stands out (orange row, bigger lock)
			Widgets.DrawBoxSolid(rect, new Color(1f, 0.6f, 0.2f, 0.12f));
			GUI.color = new Color(1f, 0.75f, 0.35f);
			GUI.DrawTexture(new Rect(rect.xMax - 17f, rect.y + (rect.height - 15f) / 2f, 15f, 15f), ForemanTex.Lock);
			GUI.color = Color.white;
		}
		// 3.1.0: why the steward plans him or not
		if (Mouse.IsOver(rect))
		{
			ForemanMapComponent comp = ForemanMapComponent.For(pawn.MapHeld ?? Find.CurrentMap);
			string st = StewardStatus.For(pawn, comp);
			if (!st.NullOrEmpty())
				TooltipHandler.TipRegion(rect, new TipSignal(st, pawn.thingIDNumber ^ 0x5717));
		}
	}
}
