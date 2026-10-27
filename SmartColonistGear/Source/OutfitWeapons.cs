using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using RimWorld;
using UnityEngine;
using Verse;

namespace ResearchedLoadout
{
	// 2.16.0: Combat Extended – a ranged weapon is only taken when ammo for it exists (on the map, in the
	// colonist's pockets or loaded in the weapon). Everything goes through reflection; without CE this is a no-op.
	public static class CEAmmo
	{
		private static bool inited;
		private static bool active;
		private static Type compType;
		private static PropertyInfo useAmmoProp;
		private static PropertyInfo curMagProp;
		private static readonly Dictionary<ThingDef, List<ThingDef>> ammoByWeapon = new Dictionary<ThingDef, List<ThingDef>>();

		public static bool Active
		{
			get
			{
				if (!inited) Init();
				ResearchedLoadoutSettings s = ResearchedLoadoutMod.Settings;
				return active && (s == null || s.ceAmmoCheck);
			}
		}

		public static bool Loaded
		{
			get
			{
				if (!inited) Init();
				return active;
			}
		}

		private static void Init()
		{
			inited = true;
			try
			{
				compType = GenTypes.GetTypeInAnyAssembly("CombatExtended.CompAmmoUser");
				active = compType != null || ModsConfig.IsActive("CETeam.CombatExtended");
				if (compType == null)
				{
					active = false;
					return;
				}
				useAmmoProp = compType.GetProperty("UseAmmo", BindingFlags.Public | BindingFlags.Instance);
				curMagProp = compType.GetProperty("CurMagCount", BindingFlags.Public | BindingFlags.Instance);
			}
			catch (Exception ex)
			{
				active = false;
				MLog.WarnOnce("[Smart Colonist Gear] Combat Extended ammo check off: " + ex.Message, 0x5ce0a1);
			}
		}

		private static ThingComp AmmoComp(Thing thing)
		{
			ThingWithComps twc = thing as ThingWithComps;
			if (twc == null || compType == null) return null;
			List<ThingComp> comps = twc.AllComps;
			for (int i = 0; i < comps.Count; i++)
			{
				if (comps[i] != null && compType.IsInstanceOfType(comps[i])) return comps[i];
			}
			return null;
		}

		// ammo kinds of the weapon's ammo set (empty = does not use ammo)
		private static List<ThingDef> AmmoDefs(ThingDef def, ThingComp comp)
		{
			if (ammoByWeapon.TryGetValue(def, out List<ThingDef> list)) return list;
			list = new List<ThingDef>();
			try
			{
				object props = comp.props;
				FieldInfo setField = props == null ? null : props.GetType().GetField("ammoSet", BindingFlags.Public | BindingFlags.Instance);
				object set = setField?.GetValue(props);
				FieldInfo typesField = set == null ? null : set.GetType().GetField("ammoTypes", BindingFlags.Public | BindingFlags.Instance);
				IEnumerable types = typesField?.GetValue(set) as IEnumerable;
				if (types != null)
				{
					foreach (object link in types)
					{
						if (link == null) continue;
						FieldInfo ammoField = link.GetType().GetField("ammo", BindingFlags.Public | BindingFlags.Instance);
						ThingDef ammo = ammoField?.GetValue(link) as ThingDef;
						if (ammo != null && !list.Contains(ammo)) list.Add(ammo);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Smart Colonist Gear] Combat Extended ammo set of " + def.defName + " unreadable: " + ex.Message, 0x5ce0a2);
			}
			ammoByWeapon[def] = list;
			return list;
		}

		// true = no reason to skip the weapon (no CE, melee, no ammo needed, or ammo exists)
		public static bool HasAmmo(Thing weapon, Pawn pawn)
		{
			if (weapon == null || weapon.def == null || !weapon.def.IsRangedWeapon || !Active) return true;
			try
			{
				ThingComp comp = AmmoComp(weapon);
				if (comp == null) return true;
				if (useAmmoProp != null && !(bool)useAmmoProp.GetValue(comp, null)) return true;
				if (curMagProp != null && (int)curMagProp.GetValue(comp, null) > 0) return true;
				List<ThingDef> ammo = AmmoDefs(weapon.def, comp);
				if (ammo.Count == 0) return true;
				Map map = weapon.MapHeld ?? pawn?.MapHeld;
				for (int i = 0; i < ammo.Count; i++)
				{
					if (pawn?.inventory != null && pawn.inventory.innerContainer.Contains(ammo[i])) return true;
					if (map != null && map.listerThings.ThingsOfDef(ammo[i]).Count > 0) return true;
				}
				return false;
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Smart Colonist Gear] Combat Extended ammo check failed: " + ex.Message, 0x5ce0a3);
				return true;
			}
		}
	}

	// 2.16.0: which weapon kinds colonists with a given outfit (apparel policy) may take by themselves.
	// Stored in the game as "policyId|weaponDefName" for every kind that is NOT allowed; nothing stored = all allowed.
	public static class OutfitWeapons
	{
		private static HashSet<string> Set => ResearchedLoadoutGameComponent.Get()?.outfitWeaponBans;

		private static string Key(ApparelPolicy policy, ThingDef def) => policy.id + "|" + def.defName;

		public static bool Allows(Pawn pawn, ThingDef def)
		{
			HashSet<string> set = Set;
			if (set == null || set.Count == 0 || def == null) return true;
			ApparelPolicy policy = pawn?.outfits?.CurrentApparelPolicy;
			return policy == null || !set.Contains(Key(policy, def));
		}

		public static bool Allows(ApparelPolicy policy, ThingDef def)
		{
			HashSet<string> set = Set;
			return set == null || policy == null || !set.Contains(Key(policy, def));
		}

		public static void Set_(ApparelPolicy policy, ThingDef def, bool allow)
		{
			HashSet<string> set = Set;
			if (set == null || policy == null || def == null) return;
			if (allow) set.Remove(Key(policy, def));
			else set.Add(Key(policy, def));
		}

		public static int BannedCount(ApparelPolicy policy)
		{
			HashSet<string> set = Set;
			if (set == null || policy == null) return 0;
			string p = policy.id + "|";
			return set.Count(s => s.StartsWith(p));
		}

		// entries of deleted outfits are dropped when saving
		public static void Prune(HashSet<string> set)
		{
			try
			{
				List<ApparelPolicy> all = Current.Game?.outfitDatabase?.AllOutfits;
				if (set == null || all == null) return;
				HashSet<string> ids = new HashSet<string>(all.Select(o => o.id + "|"));
				set.RemoveWhere(s => { int i = s.IndexOf('|'); return i < 0 || !ids.Contains(s.Substring(0, i + 1)); });
			}
			catch
			{
			}
		}

		private static List<ThingDef> weapons;

		public static List<ThingDef> Weapons()
		{
			if (weapons != null) return weapons;
			weapons = DefDatabase<ThingDef>.AllDefsListForReading
				.Where(d => d != null && d.IsWeapon && !d.IsApparel && d.equipmentType == EquipmentType.Primary && !d.destroyOnDrop && LoadoutFilter.IsManagedWeapon(d))
				.OrderBy(d => d.IsRangedWeapon ? 0 : 1).ThenBy(d => (int)d.techLevel).ThenBy(d => d.label)
				.ToList();
			return weapons;
		}
	}

	public sealed class Dialog_OutfitWeapons : Window
	{
		private ApparelPolicy policy;
		private Vector2 scroll;
		private string search = "";

		public override Vector2 InitialSize => new Vector2(520f, 640f);

		public Dialog_OutfitWeapons()
		{
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = true;
			policy = Current.Game?.outfitDatabase?.AllOutfits?.FirstOrDefault();
		}

		public override void DoWindowContents(Rect r)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "RL_OW_Title".Translate());
			Text.Font = GameFont.Small;
			float y = r.y + 36f;
			GUI.color = MUI.Muted;
			Text.Font = GameFont.Tiny;
			Widgets.Label(new Rect(r.x, y, r.width, 34f), "RL_OW_Desc".Translate());
			Text.Font = GameFont.Small;
			GUI.color = Color.white;
			y += 36f;
			List<ApparelPolicy> all = Current.Game?.outfitDatabase?.AllOutfits;
			if (all == null || all.Count == 0) return;
			if (policy == null || !all.Contains(policy)) policy = all[0];
			if (Widgets.ButtonText(new Rect(r.x, y, 240f, 30f), "RL_OW_Outfit".Translate(policy.label)))
			{
				Find.WindowStack.Add(new FloatMenu(all.Select(o => new FloatMenuOption(o.label, () => policy = o)).ToList()));
			}
			int banned = OutfitWeapons.BannedCount(policy);
			GUI.color = banned > 0 ? MUI.Warn : MUI.Accent;
			Text.Anchor = TextAnchor.MiddleLeft;
			Widgets.Label(new Rect(r.x + 250f, y, r.width - 250f, 30f), banned > 0 ? "RL_OW_Banned".Translate(banned).ToString() : "RL_OW_AllAllowed".Translate().ToString());
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			y += 36f;
			List<ThingDef> list = OutfitWeapons.Weapons();
			float bw = (r.width - 16f) / 3f;
			if (Widgets.ButtonText(new Rect(r.x, y, bw, 28f), "RL_OW_AllowAll".Translate()))
				foreach (ThingDef d in list) OutfitWeapons.Set_(policy, d, true);
			if (Widgets.ButtonText(new Rect(r.x + bw + 8f, y, bw, 28f), "RL_OW_OnlyRanged".Translate()))
				foreach (ThingDef d in list) OutfitWeapons.Set_(policy, d, d.IsRangedWeapon);
			if (Widgets.ButtonText(new Rect(r.x + 2f * (bw + 8f), y, bw, 28f), "RL_OW_OnlyMelee".Translate()))
				foreach (ThingDef d in list) OutfitWeapons.Set_(policy, d, !d.IsRangedWeapon);
			y += 34f;
			search = Widgets.TextField(new Rect(r.x, y, r.width, 26f), search ?? "");
			y += 30f;
			string q = (search ?? "").Trim().ToLowerInvariant();
			List<ThingDef> shown = q.Length == 0 ? list : list.Where(d => d.label != null && d.label.ToLowerInvariant().Contains(q)).ToList();
			const float rowH = 28f;
			Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 50f);
			Rect view = new Rect(0f, 0f, outR.width - 16f, shown.Count * rowH);
			Widgets.BeginScrollView(outR, ref scroll, view);
			int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1);
			int last = Mathf.Min(shown.Count - 1, first + (int)(outR.height / rowH) + 2);
			for (int i = first; i <= last; i++)
			{
				ThingDef d = shown[i];
				Rect row = new Rect(0f, i * rowH, view.width, rowH);
				if (i % 2 == 0) Widgets.DrawLightHighlight(row);
				Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), d);
				bool on = OutfitWeapons.Allows(policy, d);
				bool was = on;
				Widgets.CheckboxLabeled(new Rect(row.x + 30f, row.y, row.width - 30f, rowH), d.LabelCap.ToString() + (d.IsRangedWeapon ? "" : "  (" + "RL_OW_Melee".Translate().ToString() + ")"), ref on);
				if (on != was) OutfitWeapons.Set_(policy, d, on);
				TooltipHandler.TipRegion(row, d.description ?? "");
			}
			Widgets.EndScrollView();
		}
	}
}
