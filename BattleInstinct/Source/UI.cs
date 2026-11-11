using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace BattleInstinct;

[StaticConstructorOnStartup]
public static class Tex
{
	public static readonly Texture2D Auto = ContentFinder<Texture2D>.Get("BattleInstinct/Auto");
	public static readonly Texture2D[] Stance =
	{
		ContentFinder<Texture2D>.Get("BattleInstinct/Aggressive"),
		ContentFinder<Texture2D>.Get("BattleInstinct/Defensive"),
		ContentFinder<Texture2D>.Get("BattleInstinct/Hold"),
	};
	public static readonly Texture2D Abilities = ContentFinder<Texture2D>.Get("BattleInstinct/Abilities");
}

[HarmonyPatch(typeof(Pawn), nameof(Pawn.GetGizmos))]
public static class Patch_Gizmos
{
	public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> gizmos, Pawn __instance)
	{
		foreach (Gizmo g in gizmos)
			yield return g;
		Pawn p = __instance;
		var s = BattleInstinctMod.Settings;
		if (p == null || !s.enabled || !p.Drafted) yield break;
		if (!p.IsColonistPlayerControlled && !(s.mechs && p.IsColonyMechPlayerControlled)) yield break;
		PawnData d = BIComp.For(p);
		if (d == null) yield break;
		yield return new Command_Toggle
		{
			defaultLabel = "BI_Auto".Translate(),
			defaultDesc = "BI_Auto_Desc".Translate(),
			icon = Tex.Auto,
			isActive = () => d.auto,
			toggleAction = () =>
			{
				d.auto = !d.auto;
				d.ourJob = null;
				d.anchor = p.Position;
			}
		};
		if (!d.auto) yield break;
		int st = (int)d.Stance;
		yield return new Command_Action
		{
			defaultLabel = ("BI_Stance" + st).Translate(),
			defaultDesc = "BI_Stance_Desc".Translate(),
			icon = Tex.Stance[st],
			action = () =>
			{
				d.stance = (st + 1) % 3;
				d.anchor = p.Position;
				d.ourJob = null;
			}
		};
		if (Find.Selector.NumSelected == 1 && p.abilities != null && p.abilities.AllAbilitiesForReading.Any(a => AbilityBrain.KindOf(a.def) != AKind.Ignore))
			yield return new Command_Action
			{
				defaultLabel = "BI_Abilities".Translate(),
				defaultDesc = "BI_Abilities_Desc".Translate(),
				icon = Tex.Abilities,
				action = () => Find.WindowStack.Add(new Dialog_PawnAbilities(p))
			};
	}
}

public class Dialog_PawnAbilities : Window
{
	private readonly Pawn pawn;
	private Vector2 scroll;

	public Dialog_PawnAbilities(Pawn p)
	{
		pawn = p;
		doCloseX = true;
		closeOnClickedOutside = true;
		absorbInputAroundWindow = false;
		draggable = true;
	}

	public override Vector2 InitialSize => new Vector2(560f, 460f);

	public override void DoWindowContents(Rect r)
	{
		PawnData d = BIComp.For(pawn);
		Text.Font = GameFont.Medium;
		Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "BI_AbilitiesTitle".Translate(pawn.LabelShort));
		Text.Font = GameFont.Small;
		GUI.color = MUI.Muted;
		Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 40f), "BI_AbilitiesHelp".Translate());
		GUI.color = Color.white;
		List<Ability> list = pawn.abilities?.AllAbilitiesForReading?.Where(a => a?.def != null).ToList() ?? new List<Ability>();
		Rect outR = new Rect(r.x, r.y + 78f, r.width, r.height - 78f);
		Rect view = new Rect(0f, 0f, outR.width - 18f, list.Count * 34f);
		Widgets.BeginScrollView(outR, ref scroll, view);
		for (int i = 0; i < list.Count; i++)
		{
			Ability a = list[i];
			Rect row = new Rect(0f, i * 34f, view.width, 32f);
			if (i % 2 == 0) Widgets.DrawLightHighlight(row);
			AKind k = AbilityBrain.KindOf(a.def);
			bool blocked = BattleInstinctMod.Settings.blocked.Contains(a.def.defName);
			bool on = AbilityBrain.Enabled(d, a.def);
			bool was = on;
			if (k != AKind.Ignore && !blocked)
				Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 4f), ref on);
			if (on != was)
			{
				string n = a.def.defName;
				if (AbilityBrain.DefaultOn(k)) { if (on) d.abilityOff.Remove(n); else d.abilityOff.Add(n); }
				else { if (on) d.abilityOn.Add(n); else d.abilityOn.Remove(n); }
			}
			GUI.DrawTexture(new Rect(row.x + 34f, row.y + 2f, 28f, 28f), a.def.uiIcon);
			Widgets.Label(new Rect(row.x + 70f, row.y + 6f, 230f, 24f), a.def.LabelCap.ToString().Truncate(230f));
			GUI.color = k == AKind.Ignore || blocked ? MUI.Muted : MUI.Accent;
			Widgets.Label(new Rect(row.x + 306f, row.y + 6f, row.width - 306f, 24f), blocked ? "BI_BlockedAll".Translate().ToString() : ("BI_Kind_" + k).Translate().ToString());
			GUI.color = Color.white;
			TooltipHandler.TipRegion(row, a.def.LabelCap + "\n" + ("BI_Kind_" + k + "_Desc").Translate());
		}
		Widgets.EndScrollView();
	}
}
