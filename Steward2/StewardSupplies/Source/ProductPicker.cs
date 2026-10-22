using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ColonyForeman;

// 2.4: a searchable product list (in the spirit of Dubs Mint Menus): every
// product your workbenches can make, grouped by bench, with icons, the info
// card and a search box. Click to add or remove a production target.
public sealed class Dialog_StewardProductPicker : Window
{
	private readonly SupplyManager sm;
	private string search = "";
	private Vector2 scroll;
	private List<(ThingDef product, RecipeDef recipe, ThingDef bench)> all;

	public override Vector2 InitialSize => new Vector2(620f, 720f);

	public Dialog_StewardProductPicker(SupplyManager sm)
	{
		this.sm = sm;
		doCloseX = true;
		doCloseButton = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = true;
		all = sm.AllProducibleProducts();
	}

	public override void DoWindowContents(Rect inRect)
	{
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "CF_X_PickTitle".Translate());
		Text.Font = GameFont.Small;
		GUI.SetNextControlName("CF_PickSearch");
		search = Widgets.TextField(new Rect(inRect.x, inRect.y + 38f, inRect.width - 40f, 30f), search);
		if (Event.current.type == EventType.Layout && string.IsNullOrEmpty(GUI.GetNameOfFocusedControl()))
			GUI.FocusControl("CF_PickSearch");
		if (Widgets.ButtonImage(new Rect(inRect.xMax - 32f, inRect.y + 41f, 24f, 24f), TexButton.CloseXSmall))
			search = "";

		var rows = all.Where(x => search.NullOrEmpty()
		                          || x.product.label.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0
		                          || x.bench.label.IndexOf(search, System.StringComparison.OrdinalIgnoreCase) >= 0)
			.GroupBy(x => x.bench)
			.OrderBy(g => g.Key.label)
			.ToList();

		Rect outRect = new Rect(inRect.x, inRect.y + 76f, inRect.width, inRect.height - 76f - 50f);
		float rowH = 34f, headH = 30f;
		float height = rows.Sum(g => headH + g.Count() * rowH);
		Rect view = new Rect(0f, 0f, outRect.width - 16f, height);
		Widgets.BeginScrollView(outRect, ref scroll, view);
		float y = 0f;
		foreach (var g in rows)
		{
			Rect head = new Rect(0f, y, view.width, headH);
			Widgets.DrawBoxSolid(head, new Color(0.2f, 0.2f, 0.2f));
			Widgets.DefIcon(new Rect(head.x + 4f, head.y + 3f, 24f, 24f), g.Key);
			Widgets.Label(new Rect(head.x + 34f, head.y + 4f, head.width - 40f, 24f), g.Key.LabelCap);
			y += headH;
			foreach (var x in g.OrderBy(p => p.product.label))
			{
				Rect row = new Rect(0f, y, view.width, rowH - 2f);
				bool added = sm.production.Any(p => p.product == x.product);
				if (added)
					Widgets.DrawHighlightSelected(row);
				else
					Widgets.DrawHighlightIfMouseover(row);
				Widgets.InfoCardButton(row.x + 6f, row.y + 5f, x.product);
				Widgets.DefIcon(new Rect(row.x + 36f, row.y + 2f, 28f, 28f), x.product);
				Widgets.Label(new Rect(row.x + 72f, row.y + 6f, row.width - 200f, 24f), x.product.LabelCap);
				GUI.color = new Color(0.75f, 0.75f, 0.75f);
				Widgets.Label(new Rect(row.xMax - 120f, row.y + 6f, 116f, 24f), "CF_X_InStock".Translate(sm.ProductCount(x.product)));
				GUI.color = Color.white;
				if (Widgets.ButtonInvisible(new Rect(row.x + 34f, row.y, row.width - 34f, row.height)))
				{
					if (added)
					{
						ProductionTarget t = sm.production.First(p => p.product == x.product);
						sm.RemoveProduction(t);
					}
					else
						sm.production.Add(new ProductionTarget { product = x.product, recipe = x.recipe, target = 20 });
				}
				y += rowH;
			}
		}
		Widgets.EndScrollView();
	}
}
