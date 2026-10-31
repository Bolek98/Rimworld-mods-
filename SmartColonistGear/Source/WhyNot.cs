using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchedLoadout
{
	// 2.19.0: right click a weapon or a piece of clothing with a colonist selected – one line says
	// why Smart Colonist Gear does not give it to him (locked, outfit policy, not better, blocked …)
	public class FloatMenuOptionProvider_GearWhyNot : FloatMenuOptionProvider
	{
		protected override bool Drafted => true;
		protected override bool Undrafted => true;
		protected override bool Multiselect => false;

		private static readonly Dictionary<string, string> Keys = new Dictionary<string, string>
		{
			{ "worn", "RL_WN_Worn" }, { "reserved by queue", "RL_WN_Queued" }, { "race/body can't wear", "RL_WN_Body" },
			{ "no body parts", "RL_WN_Body" }, { "CanEquip false", "RL_WN_CantEquip" }, { "gender", "RL_WN_Gender" },
			{ "work clothing for another job", "RL_WN_WorkClothing" }, { "not in stockpile", "RL_WN_Stockpile" },
			{ "forbidden", "RL_WN_Forbidden" }, { "too damaged", "RL_WN_Damaged" }, { "outfit policy", "RL_WN_Policy" },
			{ "would replace protected", "RL_WN_Protected" }, { "same kind not better", "RL_WN_NotBetter" },
			{ "armor downgrade", "RL_WN_ArmorDown" }, { "not worth replacing", "RL_WN_NotBetter" },
			{ "can't reach/reserve", "RL_WN_Reach" }, { "candidate", "RL_WN_Candidate" }, { "not managed", "RL_WN_NotManaged" },
			{ "tool", "RL_WN_Tool" }, { "not allowed by outfit", "RL_WN_Policy" }, { "no CE ammo", "RL_WN_Ammo" },
			{ "flip blocked", "RL_WN_Flip" }, { "recently removed", "RL_WN_Recent" },
		};

		private static readonly Dictionary<string, string> Locks = new Dictionary<string, string>
		{
			{ "rucne zakazano", "RL_WN_LockBanned" }, { "jeste nevyrobeno", "RL_WN_LockNotMade" }, { "chybi vyzkum", "RL_WN_LockResearch" },
		};

		protected override FloatMenuOption GetSingleOptionFor(Thing clickedThing, FloatMenuContext context)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			Pawn pawn = context.FirstSelectedPawn;
			if (settings == null || !settings.enabled || !settings.whyNotMenu || pawn == null || clickedThing == null || !clickedThing.Spawned)
				return null;
			bool apparel = clickedThing is Apparel;
			bool weapon = !apparel && clickedThing.def != null && clickedThing.def.IsWeapon;
			if (!apparel && !weapon)
				return null;
			if (!LoadoutFilter.IsPlayerColonist(pawn))
				return null;
			string text;
			if (ExemptPawns.IsExempt(pawn))
				text = "RL_WN_Exempt".Translate(pawn.LabelShort);
			else
			{
				ResearchedLoadoutMapComponent mc = pawn.Map?.GetComponent<ResearchedLoadoutMapComponent>();
				if (mc == null)
					return null;
				string code;
				try { code = apparel ? mc.ApparelSkip(pawn, (Apparel)clickedThing) : mc.WeaponSkip(pawn, clickedThing); }
				catch { return null; }
				if (code == null || code == "gone")
					return null;
				if (code.StartsWith("locked: "))
				{
					string r = code.Substring(8);
					text = (Locks.TryGetValue(r, out string lk) ? lk : "RL_WN_LockOther").Translate();
				}
				else if (Keys.TryGetValue(code, out string key))
					text = key.Translate(pawn.LabelShort);
				else
					text = code;
				if (weapon && code == "candidate" && !settings.autoEquipPrimary && !settings.autoEquipSidearm)
					text = "RL_WN_WeaponsOff".Translate();
				if (apparel && code == "candidate" && !settings.autoEquipApparel)
					text = "RL_WN_ApparelOff".Translate();
			}
			return new FloatMenuOption("RL_WN_Prefix".Translate() + " " + text, null);
		}
	}
}
