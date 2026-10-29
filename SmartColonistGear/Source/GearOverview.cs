using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchedLoadout
{
	// 2.16.0: who wears what – every colonist's weapon, armor and what is wrong with the gear,
	// plus "Equip everyone now"
	public static class GearOverview
	{
		public static void SetButton()
		{
			MainButtonDef d = DefDatabase<MainButtonDef>.GetNamedSilentFail("RL_GearOverview");
			if (d != null) d.buttonVisible = ResearchedLoadoutMod.Settings?.gearButton ?? true;
		}

		public static float Armor(Pawn p, StatDef stat)
		{
			try
			{
				BodyPartRecord torso = p.RaceProps.body.corePart;
				float sum = 0f;
				if (p.apparel != null)
					foreach (Apparel a in p.apparel.WornApparel)
						if (a.def.apparel.CoversBodyPart(torso)) sum += a.GetStatValue(stat);
				return Mathf.Min(sum, 2f);
			}
			catch { return 0f; }
		}

		// 2.15.1: what each colonist takes next – the real search is not cheap, so it is cached and
		// at most two colonists are refreshed per frame, each again after 3 seconds
		private static readonly Dictionary<int, KeyValuePair<float, string>> nextCache = new Dictionary<int, KeyValuePair<float, string>>();
		private static int refreshedThisFrame, frame = -1;

		public static string Next(Pawn p, Map map)
		{
			float now = Time.realtimeSinceStartup;
			if (nextCache.TryGetValue(p.thingIDNumber, out KeyValuePair<float, string> c) && now - c.Key < 3f)
				return c.Value;
			if (frame != Time.frameCount) { frame = Time.frameCount; refreshedThisFrame = 0; }
			if (refreshedThisFrame >= 2 && nextCache.ContainsKey(p.thingIDNumber))
				return c.Value;
			refreshedThisFrame++;
			string text;
			try
			{
				ResearchedLoadoutMapComponent comp = map.GetComponent<ResearchedLoadoutMapComponent>();
				Thing t = comp?.PeekNext(p, out bool _);
				if (t == null) text = "RL_GO_NextNone".Translate();
				else text = (t.GetSlotGroup() != null ? "RL_GO_NextStorage" : "RL_GO_NextMap").Translate(t.LabelShortCap);
			}
			catch { text = "–"; }
			nextCache[p.thingIDNumber] = new KeyValuePair<float, string>(now, text);
			return text;
		}

		public static List<string> Problems(Pawn p)
		{
			var l = new List<string>();
			try
			{
				if (p.equipment?.Primary == null && !p.WorkTagIsDisabled(WorkTags.Violent)) l.Add("RL_GO_NoWeapon".Translate());
				else if (p.equipment?.Primary != null && !p.equipment.Primary.def.IsRangedWeapon && ResearchedLoadoutMapComponent.RangedForAll(p)) l.Add("RL_GO_OnlyMelee".Translate());
				BodyPartRecord torso = p.RaceProps.body.corePart;
				if (p.apparel != null && !p.apparel.WornApparel.Any(a => a.def.apparel.CoversBodyPart(torso))) l.Add("RL_GO_NoTorso".Translate());
				if (p.apparel != null)
					foreach (Apparel a in p.apparel.WornApparel)
						if (a.def.useHitPoints && a.HitPoints < a.MaxHitPoints * 0.5f) { l.Add("RL_GO_Tattered".Translate(a.LabelShort)); break; }
				if (p.equipment?.Primary != null && p.equipment.Primary.def.useHitPoints && p.equipment.Primary.HitPoints < p.equipment.Primary.MaxHitPoints * 0.4f)
					l.Add("RL_GO_WeaponWorn".Translate());
				if (p.Map != null)
				{
					FloatRange comfy = p.ComfortableTemperatureRange();
					float out_ = p.Map.mapTemperature.OutdoorTemp;
					if (out_ < comfy.min - 3f) l.Add("RL_GO_Cold".Translate());
					else if (out_ > comfy.max + 3f) l.Add("RL_GO_Hot".Translate());
				}
			}
			catch { }
			return l;
		}
	}

	public class MainTabWindow_RLGear : MainTabWindow
	{
		private Vector2 scroll;
		public override Vector2 RequestedTabSize => new Vector2(980f, 560f);

		public override void DoWindowContents(Rect r)
		{
			Map map = Find.CurrentMap;
			if (map == null) return;
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(r.x, r.y, Mathf.Max(160f, r.width - 690f), 32f), "RL_GO_Title".Translate().Truncate(Mathf.Max(160f, r.width - 690f)));
			Text.Font = GameFont.Small;
			Rect all = new Rect(r.xMax - 230f, r.y, 230f, 32f);
			if (Widgets.ButtonText(all, "RL_GO_EquipAll".Translate()))
			{
				int n = map.GetComponent<ResearchedLoadoutMapComponent>()?.EquipAllNow() ?? 0;
				Messages.Message("RL_GO_EquipAllDone".Translate(n), MessageTypeDefOf.TaskCompletion, false);
			}
			TooltipHandler.TipRegion(all, "RL_GO_EquipAll_Desc".Translate());
			// 2.16.0: which weapons each outfit allows
			Rect ow = new Rect(all.x - 238f, r.y, 230f, 32f);
			if (Widgets.ButtonText(ow, "RL_OW_Button".Translate()))
			{
				Find.WindowStack.Add(new Dialog_OutfitWeapons());
			}
			Rect gp = new Rect(ow.x - 208f, r.y, 200f, 32f);
			if (Widgets.ButtonText(gp, "RL_GP_Open".Translate()))
			{
				Find.WindowStack.Add(new Dialog_GearPlanner());
			}
			TooltipHandler.TipRegion(ow, "RL_OW_Desc".Translate());
			List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned.OrderBy(p => p.LabelShort).ToList();
			float y = r.y + 40f;
			float[] cx = { 0f, 0.18f, 0.40f, 0.48f, 0.56f, 0.78f };
			string[] heads = { "RL_GO_Colonist", "RL_GO_Weapon", "RL_GO_ArmorSharp", "RL_GO_ArmorBlunt", "RL_GO_Next", "RL_GO_Problems" };
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			for (int i = 0; i < heads.Length; i++) Widgets.Label(new Rect(r.x + r.width * cx[i], y, 200f, 20f), heads[i].Translate());
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			y += 22f;
			int bad = pawns.Count(p => GearOverview.Problems(p).Count > 0);
			Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 24f);
			const float rowH = 34f;
			Rect view = new Rect(0f, 0f, outR.width - 16f, pawns.Count * rowH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			for (int i = 0; i < pawns.Count; i++)
			{
				Pawn p = pawns[i];
				Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
				if (i % 2 == 0) Widgets.DrawLightHighlight(row);
				Widgets.DrawHighlightIfMouseover(row);
				float w = view.width;
				Widgets.ThingIcon(new Rect(row.x + 2f, row.y + 1f, 30f, 30f), p);
				Text.Anchor = TextAnchor.MiddleLeft;
				Widgets.Label(new Rect(row.x + 36f, row.y, w * cx[1] - 40f, row.height), p.LabelShortCap);
				ThingWithComps wpn = p.equipment?.Primary;
				// 2.17.0: click the weapon to choose another one from storage
				Rect wcell = new Rect(w * cx[1], row.y, w * (cx[2] - cx[1]) - 4f, row.height);
				if (Mouse.IsOver(wcell)) Widgets.DrawHighlight(wcell);
				TooltipHandler.TipRegion(wcell, "RL_GO_PickWeapon".Translate());
				if (Widgets.ButtonInvisible(wcell))
				{
					Find.WindowStack.Add(new FloatMenu(WeaponPlus.PickMenu(p)));
					Event.current.Use();
				}
				if (wpn != null)
				{
					Widgets.ThingIcon(new Rect(w * cx[1], row.y + 3f, 26f, 26f), wpn);
					string hp = wpn.def.useHitPoints ? "  " + ((float)wpn.HitPoints / wpn.MaxHitPoints).ToStringPercent() : "";
					string kind = "  <color=" + (wpn.def.IsRangedWeapon ? "#8fc6e8" : "#e0b070") + ">" + (wpn.def.IsRangedWeapon ? "RL_GO_Ranged" : "RL_GO_Melee").Translate() + "</color>";
					Widgets.Label(new Rect(w * cx[1] + 30f, row.y, w * (cx[2] - cx[1]) - 34f, row.height), (wpn.LabelShortCap + hp).Truncate(w * (cx[2] - cx[1]) - 110f) + kind);
				}
				else
				{
					GUI.color = MUI.Muted;
					Widgets.Label(new Rect(w * cx[1], row.y, 200f, row.height), "–");
					GUI.color = Color.white;
				}
				Widgets.Label(new Rect(w * cx[2], row.y, 100f, row.height), GearOverview.Armor(p, StatDefOf.ArmorRating_Sharp).ToStringPercent());
				Widgets.Label(new Rect(w * cx[3], row.y, 100f, row.height), GearOverview.Armor(p, StatDefOf.ArmorRating_Blunt).ToStringPercent());
				string next = GearOverview.Next(p, map);
				Rect nr = new Rect(w * cx[4], row.y, w * (cx[5] - cx[4]) - 6f, row.height);
				GUI.color = next == (string)"RL_GO_NextNone".Translate() ? MUI.Muted : Color.white;
				Widgets.Label(nr, next.Truncate(nr.width));
				List<string> probs = GearOverview.Problems(p);
				GUI.color = probs.Count > 0 ? MUI.Warn : MUI.Accent;
				string pt = probs.Count > 0 ? string.Join(", ", probs.ToArray()) : (string)"RL_GO_Ok".Translate();
				Widgets.Label(new Rect(w * cx[5], row.y, w * (1f - cx[5]), row.height), pt.Truncate(w * (1f - cx[5])));
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				if (Widgets.ButtonInvisible(row)) CameraJumper.TryJumpAndSelect(p);
				TooltipHandler.TipRegion(row, () => string.Join("\n", (p.apparel?.WornApparel ?? new List<Apparel>()).Select(a => "• " + a.LabelCap).ToArray()), p.thingIDNumber ^ 0x6e7a);
			}
			Widgets.EndScrollView();
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(r.x, r.yMax - 20f, r.width, 20f), "RL_GO_Footer".Translate(pawns.Count, bad));
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
		}
	}
}
