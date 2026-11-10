using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 1.2.0: the Stock page – every key supply at a glance
public sealed partial class MainTabWindow_Supplies
{
	private Vector2 stockScroll;

	private void DrawStock(Rect r, SupplyManager sm)
	{
		float y = r.y;
		Title(ref y, r, "CF_St_Page".Translate(), "CF_St_Desc".Translate());

		// forecast line
		var fc = new List<string>();
		if (sm.WinterComing()) fc.Add("CF_St_Winter".Translate());
		if (sm.DangerNow()) fc.Add("CF_St_Danger".Translate());
		if (fc.Count > 0)
		{
			GUI.color = new Color(0.6f, 0.8f, 1f);
			Widgets.Label(new Rect(r.x, y, r.width, 24f), ("CF_St_Forecast".Translate() + " " + string.Join(" · ", fc)).Truncate(r.width));
			GUI.color = Color.white;
			y += 26f;
		}

		// header
		GUI.color = Dim;
		Text.Font = GameFont.Tiny;
		float cx = r.x + 34f;
		Widgets.Label(new Rect(cx, y, 150f, 18f), "CF_St_ColItem".Translate());
		Widgets.Label(new Rect(cx + 150f, y, 70f, 18f), "CF_St_ColStock".Translate());
		Widgets.Label(new Rect(cx + 220f, y, 80f, 18f), "CF_St_ColDays".Translate());
		Widgets.Label(new Rect(cx + 300f, y, 60f, 18f), "CF_St_ColTrend".Translate());
		Widgets.Label(new Rect(cx + 360f, y, 150f, 18f), "CF_St_ColDays15".Translate());
		Widgets.Label(new Rect(cx + 520f, y, r.xMax - cx - 520f, 18f), "CF_St_ColMaking".Translate());
		Text.Font = GameFont.Small;
		GUI.color = Color.white;
		y += 20f;

		float bottomH = 150f;
		Rect outR = new Rect(r.x, y, r.width, r.yMax - y - bottomH);
		var all = SupplyManager.Lines();
		Rect view = new Rect(0f, 0f, outR.width - 16f, all.Count * 34f);
		Widgets.BeginScrollView(outR, ref stockScroll, view);
		float ry = 0f;
		foreach (StockLine l in all)
		{
			Rect row = new Rect(0f, ry, view.width, 32f);
			Widgets.DrawHighlightIfMouseover(row);
			if (l.icon != null)
				Widgets.DefIcon(new Rect(2f, ry + 3f, 26f, 26f), l.icon);
			float n = l.count(sm);
			float days = sm.DaysLeft(l);
			int trend = sm.Trend(l);
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(34f, ry, 150f, 32f), l.labelKey.Translate().Truncate(146f));
			Widgets.Label(new Rect(184f, ry, 70f, 32f), l.nutrition ? n.ToString("0") : Mathf.RoundToInt(n).ToString());
			GUI.color = SupplyManager.DaysColor(days);
			Widgets.Label(new Rect(254f, ry, 80f, 32f), days >= 999f ? "∞" : days >= 99f ? "99+" : "CF_St_DaysN".Translate(days.ToString(days < 10f ? "0.0" : "0")).ToString());
			GUI.color = trend > 0 ? new Color(0.55f, 0.9f, 0.5f) : trend < 0 ? new Color(1f, 0.6f, 0.5f) : Dim;
			Widgets.Label(new Rect(334f, ry, 60f, 32f), trend > 0 ? "▲ " + trend : trend < 0 ? "▼ " + (-trend) : "–");
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
			if (sm.dayLogs.TryGetValue(l.key, out DayLog d))
				DrawMadeUsed(new Rect(394f, ry + 4f, 150f, 24f), d);
			List<string> making = sm.BeingMade(l);
			Rect mk = new Rect(554f, ry, view.width - 554f, 32f);
			Text.Anchor = TextAnchor.MiddleLeft;
			GUI.color = making.Count == 0 ? Dim : Color.white;
			Text.Font = GameFont.Tiny;
			Widgets.Label(mk, (making.Count == 0 ? (string)"CF_St_NothingMade".Translate() : string.Join(", ", making)).Truncate(mk.width));
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			Text.Anchor = TextAnchor.UpperLeft;
			if (making.Count > 1)
				TooltipHandler.TipRegion(mk, string.Join("\n", making));
			TooltipHandler.TipRegion(new Rect(0f, ry, 554f, 32f), "CF_St_RowTip".Translate(l.labelKey.Translate()));
			ry += 34f;
		}
		Widgets.EndScrollView();

		// bottom: what stops production, trader advice, switches
		float by = r.yMax - bottomH + 6f;
		Text.Font = GameFont.Tiny;
		if (sm.bottlenecks.Count > 0)
		{
			GUI.color = new Color(1f, 0.75f, 0.45f);
			string b = "CF_St_Stuck".Translate() + " " + string.Join(" · ", sm.bottlenecks);
			Rect br = new Rect(r.x, by, r.width, 32f);
			Widgets.Label(br, b.Truncate(r.width * 2f));
			TooltipHandler.TipRegion(br, string.Join("\n", sm.bottlenecks));
			by += 34f;
		}
		sm.TradeAdvice(out List<string> sell, out List<string> buy);
		GUI.color = Dim;
		string trade = "CF_St_TradeNow".Translate() + " "
			+ (buy.Count > 0 ? (string)"CF_St_Buy".Translate(string.Join(", ", buy)) + "  " : "")
			+ (sell.Count > 0 ? (string)"CF_St_Sell".Translate(string.Join(", ", sell)) : "")
			+ (buy.Count == 0 && sell.Count == 0 ? (string)"CF_St_TradeNothing".Translate() : "");
		Widgets.Label(new Rect(r.x, by, r.width, 20f), trade.Truncate(r.width));
		GUI.color = Color.white;
		Text.Font = GameFont.Small;
		by += 24f;
		float w = (r.width - 30f) / 2f;
		Check(new Rect(r.x, by, w, 26f), "CF_St_OptRooms".Translate(), ref sm.roomBills, "CF_St_OptRooms_Desc".Translate());
		Check(new Rect(r.x + w + 30f, by, w, 26f), "CF_St_OptForecast".Translate(), ref sm.forecastOn, "CF_St_OptForecast_Desc".Translate());
		by += 28f;
		Check(new Rect(r.x, by, w, 26f), "CF_St_OptRecipes".Translate(), ref sm.recipeByStock, "CF_St_OptRecipes_Desc".Translate());
		Check(new Rect(r.x + w + 30f, by, w, 26f), "CF_St_OptTrader".Translate(), ref sm.traderTips, "CF_St_OptTrader_Desc".Translate());
	}

	// made (green) and used (red) per day, last 15 days
	private static void DrawMadeUsed(Rect r, DayLog d)
	{
		Widgets.DrawBoxSolid(r, new Color(0.1f, 0.1f, 0.1f));
		var made = d.made.Concat(new[] { d.madeToday }).ToList();
		var used = d.used.Concat(new[] { d.usedToday }).ToList();
		int count = Mathf.Max(made.Count, used.Count);
		if (count < 1)
			return;
		float max = Mathf.Max(1, Mathf.Max(made.DefaultIfEmpty(0).Max(), used.DefaultIfEmpty(0).Max()));
		float slot = r.width / Mathf.Max(DayLog.Days + 1, count);
		float mid = r.y + r.height / 2f;
		for (int i = 0; i < count; i++)
		{
			float x = r.x + i * slot + 1f;
			float bw = Mathf.Max(1f, slot - 2f);
			if (i < made.Count && made[i] > 0)
			{
				float h = (r.height / 2f - 1f) * made[i] / max;
				Widgets.DrawBoxSolid(new Rect(x, mid - h, bw, h), new Color(0.45f, 0.8f, 0.45f));
			}
			if (i < used.Count && used[i] > 0)
			{
				float h = (r.height / 2f - 1f) * used[i] / max;
				Widgets.DrawBoxSolid(new Rect(x, mid, bw, h), new Color(0.9f, 0.45f, 0.4f));
			}
		}
		TooltipHandler.TipRegion(r, "CF_St_GraphTip".Translate(d.madeToday, d.usedToday, d.AvgUsed().ToString("0")));
	}
}

public sealed partial class SupplyManager
{
	// anything red (under 3 days) – the nav button lights up
	public bool StockAlarm()
	{
		try
		{
			foreach (StockLine l in Lines())
			{
				if (l.key != "food" && l.key != "meals" && l.key != "medicine")
					continue;
				if (DaysLeft(l) < 3f)
					return true;
			}
		}
		catch { }
		return false;
	}

	public string StockSummary()
	{
		try
		{
			StockLine food = Lines().First(l => l.key == "food");
			float d = DaysLeft(food);
			return "CF_St_Short".Translate(d >= 99f ? "99+" : d.ToString("0.0"));
		}
		catch { return ""; }
	}
}
