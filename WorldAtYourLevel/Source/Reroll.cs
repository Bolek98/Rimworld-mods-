using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchLockedRaidGear
{
	// 2.17.0: a button on pawns of other factions – their gear is cut down to the world level right now
	// (for anything that slipped through: ancient sites, other mods' quests, pawns made before the mod was added)
	public static class RerollButton
	{
		private static Texture2D icon;

		public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> values, Pawn __instance)
		{
			foreach (Gizmo g in values) yield return g;
			Pawn p = __instance;
			var s = ResearchLockedRaidGearMod.Settings;
			if (s == null || !s.enabled || !s.rerollButton || p == null || p.Dead || !p.Spawned) yield break;
			if (p.Faction != null && p.Faction.IsPlayer) yield break;
			if (p.RaceProps == null || !p.RaceProps.Humanlike) yield break;
			if (icon == null) icon = ContentFinder<Texture2D>.Get("UI/Commands/LaunchReport", false) ?? BaseContent.BadTex;
			yield return new Command_Action
			{
				defaultLabel = "RLRG_Reroll".Translate(),
				defaultDesc = "RLRG_Reroll_Desc".Translate(),
				icon = icon,
				action = () =>
				{
					ResearchLockedGearFilter.ForceFilter(p);
					Messages.Message("RLRG_RerollDone".Translate(p.LabelShortCap), p, MessageTypeDefOf.NeutralEvent, false);
				}
			};
		}
	}
}
