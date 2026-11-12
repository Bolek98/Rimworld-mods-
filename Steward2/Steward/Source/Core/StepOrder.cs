using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.3.0: the order of who has priority when two parts of Steward want different things for one cell
public class Dialog_StepOrder : Window
{
	private readonly ForemanSettings s;

	public Dialog_StepOrder(ForemanSettings s)
	{
		this.s = s;
		doCloseX = true;
		doCloseButton = true;
		closeOnClickedOutside = true;
		draggable = true;
	}

	public override Vector2 InitialSize => new Vector2(560f, 640f);

	public override void DoWindowContents(Rect r)
	{
		List<string> order = s.stepOrder;
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "CF2_OrderTitle".Translate());
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 54f), "CF2_OrderHelp".Translate());
		GUI.color = Color.white;
		float y = r.y + 92f;
		for (int i = 0; i < order.Count; i++)
		{
			Rect row = new Rect(r.x, y, r.width, 34f);
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			Widgets.Label(new Rect(row.x + 8f, row.y + 7f, 30f, 24f), (i + 1) + ".");
			Widgets.Label(new Rect(row.x + 40f, row.y + 7f, 200f, 24f), ("CF2_Cat_" + order[i]).Translate());
			GUI.color = MUI.Muted;
			Widgets.Label(new Rect(row.x + 240f, row.y + 7f, row.width - 320f, 24f), ((string)("CF2_CatDesc_" + order[i]).Translate()).Truncate(row.width - 320f));
			GUI.color = Color.white;
			TooltipHandler.TipRegion(row, ("CF2_CatDesc_" + order[i]).Translate());
			if (i > 0 && Widgets.ButtonText(new Rect(row.xMax - 72f, row.y + 3f, 32f, 28f), "▲"))
			{
				(order[i - 1], order[i]) = (order[i], order[i - 1]);
				s.Write();
			}
			if (i < order.Count - 1 && Widgets.ButtonText(new Rect(row.xMax - 36f, row.y + 3f, 32f, 28f), "▼"))
			{
				(order[i + 1], order[i]) = (order[i], order[i + 1]);
				s.Write();
			}
			y += 36f;
		}
		if (Widgets.ButtonText(new Rect(r.x, y + 8f, 200f, 32f), "CF2_OrderReset".Translate()))
		{
			s.stepOrder = new List<string>(ForemanSettings.DefaultStepOrder);
			s.Write();
		}
	}
}
