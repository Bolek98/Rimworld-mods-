// ResearchedLoadout – zdrojový kód obnovený dekompilací z ResearchedLoadout.dll (10. 10. 2026), původní zdroják se ztratil.
// Přeložitelné: Source\build.bat  nebo  dotnet build -c Release

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

[assembly: AssemblyVersion("0.0.0.0")]
namespace ResearchedLoadout
{
	public sealed class ResearchedLoadoutMod : Mod
	{
		private enum SettingsTab
		{
			General,
			Weapons,
			Sidearms,
			Apparel,
			Temperature,
			Bans,
			Other
		}

		private sealed class BanRow
		{
			public ThingDef def;

			public string label;

			public string pack;
		}

		public static ResearchedLoadoutSettings Settings;

		private SettingsTab tab;

		private string banSearch = string.Empty;

		private Vector2 weaponScroll = Vector2.zero;

		private Vector2 apparelScroll = Vector2.zero;

		private string shownSearch = "\0";

		private string weaponBanBlob;

		private string apparelBanBlob;

		private HashSet<string> weaponBans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private HashSet<string> apparelBans = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private List<BanRow> shownWeapons;

		private List<BanRow> shownApparel;

		private static List<BanRow> allWeaponRows;

		private static List<BanRow> allApparelRows;

		public ResearchedLoadoutMod(ModContentPack content)
			: base(content)
		{
			Settings = GetSettings<ResearchedLoadoutSettings>();
			MNews.Register(content, MUIVersion.Version);
			Harmony harmony = new Harmony("misak.researchedloadout");
			MUI.Init(new ResearchedLoadoutSettings(), null, "settingsVersion", "recommended", "debugLog");
			if (Settings.recommended)
			{
				MUI.ApplyAll(Settings);
			}
			MUI.PatchWindow(harmony, typeof(ResearchedLoadoutMod));
			Type[] array = new Type[12]
			{
				typeof(JobDriverTakeInventoryPatch),
				typeof(JobDriverEquipPatch),
				typeof(OptimizeApparelPatch),
				typeof(ApparelScoreRawPatch),
				typeof(ApparelDropStorePatch),
				typeof(EquipmentDropStorePatch),
				typeof(PawnGizmoPatch),
				typeof(FinishProjectPatch),
				typeof(ApparelScoreGainArmorPatch),
				typeof(PlayerChosenWeaponPatch),
				typeof(CraftedRecordPatch),
				typeof(ApparelFlipPatch)
			};
			for (int i = 0; i < array.Length; i++)
			{
				try
				{
					harmony.CreateClassProcessor(array[i]).Patch();
				}
				catch (Exception ex)
				{
					Log.Error("[Smart Colonist Gear] Patch " + array[i].Name + " failed: " + ex);
				}
			}
			SidearmsCompat.PatchFilter(harmony);
			SidearmsSettings.PatchHidePage(harmony);
			MLog.Game("[Smart Colonist Gear] v" + MUIVersion.Version + " loaded (messages: Logs\\SmartColonistGear.log).");
		}

		public override string SettingsCategory()
		{
			return "Tech Progression: Smart Colonist Gear";
		}

		private static List<string> recBullets;

		public override void DoSettingsWindowContents(Rect inRect)
		{
			bool useRecipes = Settings.useRecipes;
			// 2.13.0: recommended settings card on top
			MUI.Version(inRect, "v" + MUIVersion.Version + " · Bolek98");
			if (recBullets == null)
			{
				recBullets = new List<string>();
				for (int b = 1; b <= 6; b++)
				{
					recBullets.Add(("RL_Rec" + b).Translate());
				}
			}
			float top = inRect.y;
			top += MUI.RecommendedCard(new Rect(inRect.x, top, inRect.width, 0f), Settings, Settings.recommended, delegate(bool v)
			{
				Settings.recommended = v;
				if (v)
				{
					MUI.ApplyAll(Settings);
				}
			}, recBullets) + 6f;
			if (Settings.recommended)
			{
				top += MUI.LockNote(new Rect(inRect.x, top, inRect.width, 0f)) + 4f;
			}
			inRect = new Rect(inRect.x, top - 2f, inRect.width, inRect.yMax - (top - 2f));
			List<TabRecord> list = new List<TabRecord>();
			list.Add(new TabRecord("RL_TabGeneral".Translate(), delegate
			{
				tab = SettingsTab.General;
			}, tab == SettingsTab.General));
			list.Add(new TabRecord("RL_TabWeapons".Translate(), delegate
			{
				tab = SettingsTab.Weapons;
			}, tab == SettingsTab.Weapons));
			List<TabRecord> list2 = list;
			if (true)
			{
				list2.Add(new TabRecord("RL_TabSidearms".Translate(), delegate
				{
					tab = SettingsTab.Sidearms;
				}, tab == SettingsTab.Sidearms));
			}
			list2.AddRange(new TabRecord[4]
			{
				new TabRecord("RL_TabApparel".Translate(), delegate
				{
					tab = SettingsTab.Apparel;
				}, tab == SettingsTab.Apparel),
				new TabRecord("RL_TabTemperature".Translate(), delegate
				{
					tab = SettingsTab.Temperature;
				}, tab == SettingsTab.Temperature),
				new TabRecord("RL_TabBans".Translate(), delegate
				{
					tab = SettingsTab.Bans;
				}, tab == SettingsTab.Bans),
				new TabRecord("RL_TabOther".Translate(), delegate
				{
					tab = SettingsTab.Other;
				}, tab == SettingsTab.Other)
			});
			Rect rect = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
			Widgets.DrawMenuSection(rect);
			TabDrawer.DrawTabs(rect, list2, 1, 200f);
			Rect rect2 = rect.ContractedBy(12f);
			MUI.BeginLock(Settings.recommended && tab != SettingsTab.Bans);
			try
			{
			switch (tab)
			{
			case SettingsTab.General:
				DrawGeneralTab(rect2);
				break;
			case SettingsTab.Weapons:
				DrawWeaponsTab(rect2);
				break;
			case SettingsTab.Sidearms:
				SidearmsSettings.DrawTab(rect2);
				break;
			case SettingsTab.Apparel:
				DrawApparelTab(rect2);
				break;
			case SettingsTab.Temperature:
				DrawTemperatureTab(rect2);
				break;
			case SettingsTab.Bans:
				DrawBansTab(rect2);
				break;
			case SettingsTab.Other:
				DrawOtherTab(rect2);
				break;
			}
			}
			finally
			{
				MUI.EndLock(rect2);
			}
			if (useRecipes != Settings.useRecipes)
			{
				LoadoutFilter.InvalidateResearch();
			}
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			SidearmsSettings.Save();
		}

		private static Listing_Standard Begin(Rect r)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(r);
			listing_Standard.ColumnWidth = (r.width - 34f) / 2f;
			return listing_Standard;
		}

		private void DrawGeneralTab(Rect r)
		{
			Listing_Standard listing_Standard = Begin(r);
			Check(listing_Standard, "RL_Enable", ref Settings.enabled, "enabled");
			Check(listing_Standard, "RL_UseRecipes", ref Settings.useRecipes, "useRecipes");
			Check(listing_Standard, "RL_BlockHigherTech", ref Settings.blockHigherTech, "blockHigherTech");
			Check(listing_Standard, "RL_BlockUntagged", ref Settings.blockUntaggedModItems, "blockUntaggedModItems");
			Check(listing_Standard, "RL_TechMismatch", ref Settings.techMismatchCheck, "techMismatchCheck");
			Check(listing_Standard, "RL_PowerEstimate", ref Settings.powerEstimate, "powerEstimate");
			Check(listing_Standard, "RL_BlockLockedEquip", ref Settings.blockLockedEquip, "blockLockedEquip");
			Check(listing_Standard, "RL_BlockLockedApparel", ref Settings.blockLockedApparel, "blockLockedApparel");
			Check(listing_Standard, "RL_StripLocked", ref Settings.stripLockedGear, "stripLockedGear");
			Check(listing_Standard, "RL_OnlyCrafted", ref Settings.onlyCrafted, "onlyCrafted");
			if (Settings.onlyCrafted && Current.Game != null)
			{
				ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
				listing_Standard.Label("RL_CraftedCount".Translate((researchedLoadoutGameComponent != null) ? SharedCrafted.UnionCount(researchedLoadoutGameComponent.crafted) : 0));
				if (researchedLoadoutGameComponent != null)
				{
					Rect rect = listing_Standard.GetRect(30f);
					TooltipHandler.TipRegion(rect, "RL_CraftedReset_Desc".Translate());
					if (Widgets.ButtonText(rect, "RL_CraftedReset".Translate()))
					{
						ResearchedLoadoutGameComponent gc = researchedLoadoutGameComponent;
						Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RL_CraftedResetConfirm".Translate(), delegate
						{
							gc.ResetCrafted();
							SharedCrafted.ResetPartner();
						}, true));
					}
					// 2.8.3: pick by hand from the gear the colony has
					Rect rect8 = listing_Standard.GetRect(30f);
					TooltipHandler.TipRegion(rect8, "RL_CraftedPick_Desc".Translate());
					if (Widgets.ButtonText(rect8, "RL_CraftedPick".Translate()))
					{
						Find.WindowStack.Add(new Dialog_CraftedPicker(researchedLoadoutGameComponent));
					}
					// 2.8.2: take back everything the colony already has (worn, carried, stored)
					Rect rect9 = listing_Standard.GetRect(30f);
					TooltipHandler.TipRegion(rect9, "RL_CraftedRestore_Desc".Translate());
					if (Widgets.ButtonText(rect9, "RL_CraftedRestore".Translate()))
					{
						int added = researchedLoadoutGameComponent.RestoreCraftedFromColony();
						Messages.Message("RL_CraftedRestored".Translate(added), MessageTypeDefOf.NeutralEvent, false);
					}
				}
			}
			listing_Standard.NewColumn();
			if (Current.Game != null && Find.ResearchManager != null)
			{
				LoadoutFilter.InvalidateColonyTech();
				listing_Standard.Label(LoadoutFilter.ColonyTechExplain());
				listing_Standard.GapLine();
			}
			if (LoadoutFilter.WorldModPresent)
			{
				Check(listing_Standard, "RL_SyncWorld", ref Settings.syncWithWorld, "syncWithWorld");
			}
			Check(listing_Standard, "RL_LevelByProgress", ref Settings.levelByProgress, "levelByProgress");
			if (Settings.levelByProgress)
			{
				Settings.levelProgressPercent = (int)Sl(listing_Standard, "levelProgressPercent", "RL_LevelProgress".Translate(Settings.levelProgressPercent), Settings.levelProgressPercent, 5f, 100f, 0.5f, "RL_LevelProgress_Desc".Translate());
			}
			listing_Standard.End();
		}

		private void DrawWeaponsTab(Rect r)
		{
			Listing_Standard listing_Standard = Begin(r);
			Check(listing_Standard, "RL_AutoPrimary", ref Settings.autoEquipPrimary, "autoEquipPrimary");
			Check(listing_Standard, "RL_UseSkills", ref Settings.useSkills, "useSkills");
			Check(listing_Standard, "RL_PreferNewer", ref Settings.preferNewerTech, "preferNewerTech");
			Check(listing_Standard, "RL_PreferNewEra", ref Settings.preferNewEra, "preferNewEra");
			Check(listing_Standard, "RL_ToolsNotWeapons", ref Settings.toolsNotWeapons, "toolsNotWeapons");
			Check(listing_Standard, "RL_AutoSidearm", ref Settings.autoEquipSidearm, "autoEquipSidearm");
			Check(listing_Standard, "RL_PrioritizeFighters", ref Settings.prioritizeFighters, "prioritizeFighters");
			Check(listing_Standard, "RL_ArmSlaves", ref Settings.armSlaves, "armSlaves");
			Check(listing_Standard, "RL_RoleGear", ref Settings.roleGear, "roleGear");
			Check(listing_Standard, "RL_SpecialEffects", ref Settings.specialEffects, "specialEffects");
			listing_Standard.NewColumn();
			Check(listing_Standard, "RL_ConsiderDistance", ref Settings.considerDistance, "considerDistance");
			Check(listing_Standard, "RL_PauseThreat", ref Settings.pauseDuringThreat, "pauseDuringThreat");
			Settings.weaponCalmHours = (int)Sl(listing_Standard, "weaponCalmHours", "RL_WeaponCalm".Translate(Settings.weaponCalmHours), Settings.weaponCalmHours, 0f, 72f, 0.5f, "RL_WeaponCalm_Desc".Translate());
			listing_Standard.End();
		}

		private void DrawApparelTab(Rect r)
		{
			Listing_Standard listing_Standard = Begin(r);
			Check(listing_Standard, "RL_OwnDresser", ref Settings.useOwnDresser, "useOwnDresser");
			Check(listing_Standard, "RL_AutoApparel", ref Settings.autoEquipApparel, "autoEquipApparel");
			Check(listing_Standard, "RL_OnlyStockpile", ref Settings.onlyFromStockpile, "onlyFromStockpile");
			Check(listing_Standard, "RL_ReplaceBroken", ref Settings.replaceBrokenGear, "replaceBrokenGear");
			Check(listing_Standard, "RL_StoreDropped", ref Settings.storeDroppedGear, "storeDroppedGear");
			Check(listing_Standard, "RL_KeepArmor", ref Settings.keepArmor, "keepArmor");
			Check(listing_Standard, "RL_StatOffsets", ref Settings.statOffsets, "statOffsets");
			Check(listing_Standard, "RL_WorkClothing", ref Settings.workClothing, "workClothing");
			if (Settings.workClothing)
			{
				Settings.workClothingMinSkill = (int)Sl(listing_Standard, "workClothingMinSkill", "RL_WorkClothingSkill".Translate(Settings.workClothingMinSkill), Settings.workClothingMinSkill, 0f, 15f, 0.5f, "RL_WorkClothingSkill_Desc".Translate());
			}
			Check(listing_Standard, "RL_CoverageFirst", ref Settings.coverageFirst, "coverageFirst");
			Check(listing_Standard, "RL_FullBody", ref Settings.dressFullBody, "dressFullBody");
			Check(listing_Standard, "RL_DressByTraits", ref Settings.dressByTraits, "dressByTraits");
			listing_Standard.NewColumn();
			Check(listing_Standard, "RL_GenderClothing", ref Settings.genderClothing, "genderClothing");
			if (Settings.genderClothing)
			{
				Check(listing_Standard, "RL_GenderGuess", ref Settings.genderGuess, "genderGuess");
			}
			Settings.gearStability = (int)Sl(listing_Standard, "gearStability", "RL_GearStability".Translate(Settings.gearStability), Settings.gearStability, 0f, 100f, 0.5f, "RL_GearStability_Desc".Translate());
			Settings.apparelCalmHours = (int)Sl(listing_Standard, "apparelCalmHours", "RL_ApparelCalm".Translate(Settings.apparelCalmHours), Settings.apparelCalmHours, 0f, 48f, 0.5f, "RL_ApparelCalm_Desc".Translate());
			Settings.minApparelHpPercent = (int)Sl(listing_Standard, "minApparelHpPercent", "RL_MinHp".Translate(Settings.minApparelHpPercent), Settings.minApparelHpPercent, 5f, 90f, 0.5f, "RL_MinHp_Desc".Translate());
			listing_Standard.End();
		}

		private void DrawTemperatureTab(Rect r)
		{
			Listing_Standard listing_Standard = Begin(r);
			Check(listing_Standard, "RL_SurvivalApparel", ref Settings.survivalApparel, "survivalApparel");
			if (Settings.survivalApparel)
			{
				Check(listing_Standard, "RL_TempByThresholds", ref Settings.tempByThresholds, "tempByThresholds");
				if (Settings.tempByThresholds)
				{
					Settings.coldThreshold = Mathf.Round(Sl(listing_Standard, "coldThreshold", "RL_ColdThreshold".Translate(Settings.coldThreshold.ToStringTemperature("F0")), Settings.coldThreshold, -40f, 20f, 0.5f, "RL_ColdThreshold_Desc".Translate()));
					Settings.heatThreshold = Mathf.Round(Sl(listing_Standard, "heatThreshold", "RL_HeatThreshold".Translate(Settings.heatThreshold.ToStringTemperature("F0")), Settings.heatThreshold, 20f, 60f, 0.5f, "RL_HeatThreshold_Desc".Translate()));
				}
			}
			listing_Standard.GapLine();
			Check(listing_Standard, "RL_GuestLodgers", ref Settings.guestLodgers, "guestLodgers");
			Check(listing_Standard, "RL_GuestVisitors", ref Settings.guestVisitors, "guestVisitors");
			listing_Standard.GapLine();
			Check(listing_Standard, "RL_ThreatApparel", ref Settings.threatApparel, "threatApparel");
			if (Settings.threatApparel)
			{
				Check(listing_Standard, "RL_ThreatBills", ref Settings.threatBills, "threatBills");
			}
			listing_Standard.GapLine();
			Check(listing_Standard, "RL_EmergencyGear", ref Settings.emergencyGear, "emergencyGear");
			if (Settings.emergencyGear)
			{
				Check(listing_Standard, "RL_EmergencyToxic", ref Settings.emergencyToxic, "emergencyToxic");
				Check(listing_Standard, "RL_EmergencyTemperature", ref Settings.emergencyTemperature, "emergencyTemperature");
				Check(listing_Standard, "RL_EmergencyRaid", ref Settings.emergencyRaid, "emergencyRaid");
				Check(listing_Standard, "RL_EmergencyProvide", ref Settings.emergencyProvide, "emergencyProvide");
			}
			listing_Standard.NewColumn();
			listing_Standard.Label("RL_TempInfo".Translate());
			listing_Standard.End();
		}

		private void DrawOtherTab(Rect r)
		{
			Listing_Standard listing_Standard = Begin(r);
			Check(listing_Standard, "RL_Notify", ref Settings.notifyPlayer, "notifyPlayer");
			Check(listing_Standard, "RL_DebugLog", ref Settings.debugLog, "debugLog");
			// 2.15.0: who else changes colonist gear
			listing_Standard.Gap(12f);
			listing_Standard.Label("RL_OthersTitle".Translate());
			List<string> census = GearWhy.Census();
			listing_Standard.Label("  " + (census.Count == 0 ? "RL_OthersNone".Translate().ToString() : "RL_OthersPatch".Translate(string.Join(", ", census.ToArray())).ToString()));
			string seen = GearWhy.SeenText();
			listing_Standard.Label("  " + seen);
			Text.Font = GameFont.Tiny;
			listing_Standard.Label("RL_OthersHint".Translate());
			Text.Font = GameFont.Small;
			listing_Standard.End();
		}

		private void DrawBansTab(Rect inRect)
		{
			float y = inRect.y;
			float height = 24f;
			Widgets.Label(new Rect(inRect.x, y, 70f, height), "RL_Search".Translate());
			banSearch = Widgets.TextField(new Rect(inRect.x + 70f, y, Mathf.Min(320f, inRect.width - 70f), height), banSearch ?? string.Empty);
			y += 30f;
			float num = 6f;
			float num2 = (inRect.width - num * 3f) / 4f;
			if (Widgets.ButtonText(new Rect(inRect.x, y, num2, height), "RL_BanModWeapons".Translate()))
			{
				Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RL_ConfirmBanWeapons".Translate(), delegate
				{
					Settings.bannedWeapons = CollectUnofficialNames(true);
					LoadoutFilter.InvalidateBans();
				}, true));
			}
			if (Widgets.ButtonText(new Rect(inRect.x + (num2 + num), y, num2, height), "RL_AllowWeapons".Translate()))
			{
				Settings.bannedWeapons = string.Empty;
				LoadoutFilter.InvalidateBans();
			}
			if (Widgets.ButtonText(new Rect(inRect.x + (num2 + num) * 2f, y, num2, height), "RL_BanModApparel".Translate()))
			{
				Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RL_ConfirmBanApparel".Translate(), delegate
				{
					Settings.bannedApparel = CollectUnofficialNames(false);
					LoadoutFilter.InvalidateBans();
				}, true));
			}
			if (Widgets.ButtonText(new Rect(inRect.x + (num2 + num) * 3f, y, num2, height), "RL_AllowApparel".Translate()))
			{
				Settings.bannedApparel = string.Empty;
				LoadoutFilter.InvalidateBans();
			}
			y += 30f;
			float num3 = (inRect.width - 8f) * 0.5f;
			float num4 = inRect.yMax - y;
			if (num4 < 120f)
			{
				num4 = 120f;
			}
			DrawBanColumn(new Rect(inRect.x, y, num3, num4), true, ref weaponScroll);
			DrawBanColumn(new Rect(inRect.x + num3 + 8f, y, num3, num4), false, ref apparelScroll);
		}

		private static void Check(Listing_Standard list, string key, ref bool value, string field)
		{
			string key2 = key + "_Desc";
			MUI.Check(list, key.Translate(), (!key2.CanTranslate()) ? null : key2.Translate().ToString(), ref value, Settings, field);
		}

		private static float Sl(Listing_Standard list, string field, string label, float value, float min, float max, float labelPct, string tip = null)
		{
			return MUI.Slider(list, label, value, min, max, tip, Settings, field);
		}

		private static void Check(Listing_Standard list, string key, ref bool value)
		{
			string key2 = key + "_Desc";
			list.CheckboxLabeled(key.Translate(), ref value, (!key2.CanTranslate()) ? null : key2.Translate().ToString());
		}

		private static void EnsureCatalog()
		{
			if (allWeaponRows != null)
			{
				return;
			}
			allWeaponRows = new List<BanRow>();
			allApparelRows = new List<BanRow>();
			List<ThingDef> allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ThingDef thingDef = allDefsListForReading[i];
				if (thingDef != null && (thingDef.IsWeapon || thingDef.IsApparel))
				{
					BanRow banRow = new BanRow();
					banRow.def = thingDef;
					banRow.label = thingDef.LabelCap.ToString();
					banRow.pack = ((thingDef.modContentPack == null) ? "Vanilla" : thingDef.modContentPack.Name);
					if (thingDef.IsWeapon)
					{
						allWeaponRows.Add(banRow);
					}
					if (thingDef.IsApparel)
					{
						allApparelRows.Add(banRow);
					}
				}
			}
			allWeaponRows.Sort(CompareBan);
			allApparelRows.Sort(CompareBan);
		}

		private static int CompareBan(BanRow a, BanRow b)
		{
			return string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase);
		}

		private static List<BanRow> FilterRows(List<BanRow> source, string filter)
		{
			List<BanRow> list = new List<BanRow>();
			for (int i = 0; i < source.Count; i++)
			{
				BanRow banRow = source[i];
				if (filter.Length == 0 || banRow.label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0 || banRow.def.defName.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					list.Add(banRow);
				}
			}
			return list;
		}

		private List<BanRow> VisibleRows(bool weapons)
		{
			EnsureCatalog();
			string text = (banSearch ?? string.Empty).Trim();
			if (shownWeapons == null || text != shownSearch)
			{
				shownSearch = text;
				shownWeapons = FilterRows(allWeaponRows, text);
				shownApparel = FilterRows(allApparelRows, text);
			}
			return (!weapons) ? shownApparel : shownWeapons;
		}

		private HashSet<string> BanSet(bool weapons)
		{
			string text = ((!weapons) ? Settings.bannedApparel : Settings.bannedWeapons);
			if (weapons)
			{
				if (weaponBanBlob != text)
				{
					weaponBanBlob = text;
					ParseBanBlob(text, weaponBans);
				}
				return weaponBans;
			}
			if (apparelBanBlob != text)
			{
				apparelBanBlob = text;
				ParseBanBlob(text, apparelBans);
			}
			return apparelBans;
		}

		private static string CollectUnofficialNames(bool weapons)
		{
			List<string> list = new List<string>();
			List<ThingDef> allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ThingDef thingDef = allDefsListForReading[i];
				if (thingDef != null && (!weapons || LoadoutFilter.IsRealWeapon(thingDef)) && (weapons || thingDef.IsApparel) && thingDef.modContentPack != null && !thingDef.modContentPack.IsOfficialMod)
				{
					list.Add(thingDef.defName);
				}
			}
			list.Sort();
			return string.Join('\n'.ToString(), list.ToArray());
		}

		private void DrawBanColumn(Rect rect, bool weapons, ref Vector2 scroll)
		{
			Widgets.DrawBoxSolid(rect, new Color(0.07f, 0.08f, 0.09f, 1f));
			Widgets.DrawBox(rect);
			string label = ((!weapons) ? "RL_BanApparelTitle".Translate().ToString() : "RL_BanWeaponsTitle".Translate().ToString());
			Widgets.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 22f), label);
			HashSet<string> hashSet = BanSet(weapons);
			List<BanRow> list = VisibleRows(weapons);
			Rect outRect = new Rect(rect.x + 4f, rect.y + 26f, rect.width - 8f, rect.height - 30f);
			Rect viewRect = new Rect(0f, 0f, rect.width - 20f, Mathf.Max((float)list.Count * 24f + 8f, outRect.height));
			Widgets.BeginScrollView(outRect, ref scroll, viewRect);
			int num = Mathf.FloorToInt(scroll.y / 24f) - 1;
			if (num < 0)
			{
				num = 0;
			}
			int num2 = num + Mathf.CeilToInt(outRect.height / 24f) + 3;
			if (num2 > list.Count)
			{
				num2 = list.Count;
			}
			bool flag = false;
			for (int i = num; i < num2; i++)
			{
				BanRow banRow = list[i];
				Rect rect2 = new Rect(0f, (float)i * 24f, viewRect.width, 24f);
				bool flag2 = hashSet.Contains(banRow.def.defName);
				bool checkOn = flag2;
				bool flag3 = Current.Game != null && Find.ResearchManager != null;
				if (flag3 && !flag2)
				{
					GUI.color = ((!LoadoutFilter.IsAllowed(banRow.def)) ? new Color(1f, 0.75f, 0.4f) : new Color(0.6f, 0.95f, 0.6f));
				}
				Widgets.CheckboxLabeled(rect2, banRow.label + "  (" + banRow.pack + ")", ref checkOn);
				GUI.color = Color.white;
				if (flag3 && Mouse.IsOver(rect2))
				{
					TooltipHandler.TipRegion(rect2, LoadoutFilter.Explain(banRow.def));
				}
				if (checkOn != flag2)
				{
					flag = true;
					if (checkOn)
					{
						hashSet.Add(banRow.def.defName);
					}
					else
					{
						hashSet.Remove(banRow.def.defName);
					}
				}
			}
			Widgets.EndScrollView();
			if (flag)
			{
				List<string> list2 = new List<string>(hashSet);
				list2.Sort();
				string text = string.Join('\n'.ToString(), list2.ToArray());
				if (weapons)
				{
					Settings.bannedWeapons = text;
				}
				else
				{
					Settings.bannedApparel = text;
				}
				LoadoutFilter.InvalidateBans();
			}
		}

		private static void ParseBanBlob(string blob, HashSet<string> dest)
		{
			dest.Clear();
			if (string.IsNullOrEmpty(blob))
			{
				return;
			}
			string[] array = blob.Split('\n', '\r', ',', ';');
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i].Trim();
				if (text.Length > 0)
				{
					dest.Add(text);
				}
			}
		}
	}
	public static class PowerTech
	{
		private static bool built;

		private static float maxRangedIndustrial;

		private static float maxRangedSpacer;

		private static float maxMeleeIndustrial;

		private static float maxArmorIndustrial;

		private static float maxArmorSpacer;

		private static readonly Dictionary<ThingDef, TechLevel> cache = new Dictionary<ThingDef, TechLevel>();

		private const float Margin = 1.15f;

		public static TechLevel Estimate(ThingDef def)
		{
			if (def == null || def.modContentPack == null || def.modContentPack.IsOfficialMod)
			{
				return TechLevel.Undefined;
			}
			TechLevel value;
			if (cache.TryGetValue(def, out value))
			{
				return value;
			}
			Build();
			TechLevel techLevel = TechLevel.Undefined;
			try
			{
				if (def.IsRangedWeapon)
				{
					float num = RangedPower(def);
					if (maxRangedSpacer > 0f && num > maxRangedSpacer * 1.15f)
					{
						techLevel = TechLevel.Ultra;
					}
					else if (maxRangedIndustrial > 0f && num > maxRangedIndustrial * 1.15f)
					{
						techLevel = TechLevel.Spacer;
					}
				}
				else if (def.IsMeleeWeapon)
				{
					float statValueAbstract = def.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, GenStuff.DefaultStuffFor(def));
					if (maxMeleeIndustrial > 0f && statValueAbstract > maxMeleeIndustrial * 1.15f)
					{
						techLevel = TechLevel.Spacer;
					}
				}
				else if (def.IsApparel)
				{
					float statValueAbstract2 = def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, GenStuff.DefaultStuffFor(def));
					if (maxArmorSpacer > 0f && statValueAbstract2 > maxArmorSpacer * 1.15f)
					{
						techLevel = TechLevel.Ultra;
					}
					else if (maxArmorIndustrial > 0f && statValueAbstract2 > maxArmorIndustrial * 1.15f)
					{
						techLevel = TechLevel.Spacer;
					}
				}
			}
			catch
			{
				techLevel = TechLevel.Undefined;
			}
			cache[def] = techLevel;
			return techLevel;
		}

		private static void Build()
		{
			if (built)
			{
				return;
			}
			built = true;
			foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (item == null || item.modContentPack == null || !item.modContentPack.IsCoreMod)
				{
					continue;
				}
				try
				{
					if (item.IsWeapon && item.equipmentType == EquipmentType.Primary && !item.IsStuff)
					{
						if (item.IsRangedWeapon)
						{
							if (item.weaponTags == null || (!item.weaponTags.Contains("TurretGun") && !item.weaponTags.Contains("GunSingleUse")))
							{
								float b = RangedPower(item);
								if ((int)item.techLevel <= 4)
								{
									maxRangedIndustrial = Mathf.Max(maxRangedIndustrial, b);
								}
								else if (item.techLevel == TechLevel.Spacer)
								{
									maxRangedSpacer = Mathf.Max(maxRangedSpacer, b);
								}
							}
						}
						else if ((int)item.techLevel <= 4)
						{
							maxMeleeIndustrial = Mathf.Max(maxMeleeIndustrial, item.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, GenStuff.DefaultStuffFor(item)));
						}
					}
					else if (item.IsApparel)
					{
						float statValueAbstract = item.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, GenStuff.DefaultStuffFor(item));
						if ((int)item.techLevel <= 4)
						{
							maxArmorIndustrial = Mathf.Max(maxArmorIndustrial, statValueAbstract);
						}
						else if (item.techLevel == TechLevel.Spacer)
						{
							maxArmorSpacer = Mathf.Max(maxArmorSpacer, statValueAbstract);
						}
					}
				}
				catch
				{
				}
			}
		}

		public static float RangedPower(ThingDef def)
		{
			if (def.Verbs == null || def.Verbs.Count == 0)
			{
				return 0f;
			}
			VerbProperties verbProperties = def.Verbs[0];
			if (verbProperties == null || verbProperties.defaultProjectile == null || verbProperties.defaultProjectile.projectile == null)
			{
				return 0f;
			}
			ProjectileProperties projectile = verbProperties.defaultProjectile.projectile;
			ThingDef stuff = GenStuff.DefaultStuffFor(def);
			float num = projectile.GetDamageAmount(1f, null);
			if (num <= 0f)
			{
				return 0f;
			}
			float value = 0.15f;
			try
			{
				value = projectile.GetArmorPenetration();
			}
			catch
			{
			}
			int num2 = Mathf.Max(1, verbProperties.burstShotCount);
			float statValueAbstract = def.GetStatValueAbstract(StatDefOf.RangedWeapon_Cooldown, stuff);
			float num3 = Mathf.Max(0.1f, verbProperties.warmupTime + statValueAbstract + (float)((num2 - 1) * verbProperties.ticksBetweenBurstShots) / 60f);
			float range = verbProperties.range;
			float num4 = 0.35f * def.GetStatValueAbstract(StatDefOf.AccuracyShort, stuff) + 0.45f * ((!(range >= 25f)) ? 0f : def.GetStatValueAbstract(StatDefOf.AccuracyMedium, stuff)) + 0.2f * ((!(range >= 40f)) ? 0f : def.GetStatValueAbstract(StatDefOf.AccuracyLong, stuff));
			return num * (float)num2 / num3 * num4 * (0.75f + Mathf.Clamp(value, 0f, 1f)) * (1f + Mathf.Min(range, 60f) / 150f);
		}
	}
	[HarmonyPatch(typeof(Pawn_EquipmentTracker), "AddEquipment")]
	public static class PlayerChosenWeaponPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Pawn_EquipmentTracker __instance, ThingWithComps newEq)
		{
			if (__instance == null || newEq == null)
			{
				return;
			}
			Pawn pawn = __instance.pawn;
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			if (researchedLoadoutGameComponent != null && pawn != null && LoadoutFilter.IsPlayerColonist(pawn))
			{
				Job curJob = pawn.CurJob;
				if (curJob != null && curJob.playerForced && curJob.def == JobDefOf.Equip)
				{
					researchedLoadoutGameComponent.playerChosenWeapons.Add(newEq.thingIDNumber);
				}
				else if ((curJob == null || curJob.def != JobDefOf.Equip || !curJob.playerForced) && curJob != null && curJob.def == JobDefOf.Equip)
				{
					researchedLoadoutGameComponent.playerChosenWeapons.Remove(newEq.thingIDNumber);
				}
				if (researchedLoadoutGameComponent.playerChosenWeapons.Count > 5000)
				{
					researchedLoadoutGameComponent.playerChosenWeapons.Clear();
				}
			}
		}
	}
	[HarmonyPatch(typeof(JobGiver_OptimizeApparel), "ApparelScoreGain")]
	public static class ApparelScoreGainArmorPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Pawn pawn, Apparel ap, ref float __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.enabled && LoadoutFilter.IsPlayerColonist(pawn))
			{
				ThreatResponse.Boost(pawn, ap, ref __result);
				EmergencyGear.Boost(pawn, ap, ref __result);
				ArmorSet.Adjust(pawn, ap, ref __result);
				GearSense.Adjust(pawn, ap, ref __result);
				if (__result > 0f && settings.keepArmor && LoadoutFilter.WouldDowngradeArmor(pawn, ap))
				{
					__result = -1f;
				}
			}
		}
	}
	public static class Coverage
	{
		public static bool Enabled
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return settings != null && settings.coverageFirst;
			}
		}

		public static float GapBonus(Pawn pawn, ThingDef def)
		{
			if (!Enabled || pawn == null || pawn.apparel == null || def == null)
			{
				return 0f;
			}
			return GroupValue(Missing(CoveredGroups(pawn, null), GroupsOf(def)));
		}

		public static float LossPenalty(Pawn pawn, ThingDef def)
		{
			if (!Enabled || pawn == null || pawn.apparel == null || def == null)
			{
				return 0f;
			}
			return GroupValue(Missing(CoveredGroups(pawn, def), CoveredGroups(pawn, null)));
		}

		public static bool FillsBasicGap(Pawn pawn, ThingDef def)
		{
			if (pawn == null || pawn.apparel == null || def == null || !def.IsApparel)
			{
				return false;
			}
			List<BodyPartGroupDef> list = CoveredGroups(pawn, null);
			List<BodyPartGroupDef> list2 = GroupsOf(def);
			bool flag = false;
			bool flag2 = false;
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i] != null)
				{
					if (list[i].defName == "Torso")
					{
						flag = true;
					}
					else if (list[i].defName == "Legs")
					{
						flag2 = true;
					}
				}
			}
			for (int j = 0; j < list2.Count; j++)
			{
				if (list2[j] != null)
				{
					if (list2[j].defName == "Torso" && !flag)
					{
						return true;
					}
					if (list2[j].defName == "Legs" && !flag2)
					{
						return true;
					}
				}
			}
			return false;
		}

		public static bool NeedsBasicsAny(Pawn pawn)
		{
			return pawn != null && pawn.apparel != null && MissingBasics(CoveredGroups(pawn, null));
		}

		public static bool NeedsBasics(Pawn pawn)
		{
			return Enabled && pawn != null && pawn.apparel != null && MissingBasics(CoveredGroups(pawn, null));
		}

		public static bool StillNaked(Pawn pawn, ThingDef adding)
		{
			return pawn != null && pawn.apparel != null && MissingBasics(CoveredGroups(pawn, adding));
		}

		private static bool MissingBasics(List<BodyPartGroupDef> covered)
		{
			bool flag = false;
			bool flag2 = false;
			for (int i = 0; i < covered.Count; i++)
			{
				if (covered[i] != null)
				{
					if (covered[i].defName == "Torso")
					{
						flag = true;
					}
					else if (covered[i].defName == "Legs")
					{
						flag2 = true;
					}
				}
			}
			return !flag || !flag2;
		}

		private static float GroupValue(List<BodyPartGroupDef> groups)
		{
			float num = 0f;
			for (int i = 0; i < groups.Count; i++)
			{
				if (groups[i] != null)
				{
					switch (groups[i].defName)
					{
					case "Torso":
						num += 500f;
						break;
					case "Legs":
						num += 350f;
						break;
					case "FullHead":
					case "UpperHead":
						num += 140f;
						break;
					default:
						num += 70f;
						break;
					}
				}
			}
			return num;
		}

		private static List<BodyPartGroupDef> GroupsOf(ThingDef def)
		{
			List<BodyPartGroupDef> list = new List<BodyPartGroupDef>();
			AddGroups(list, def);
			return list;
		}

		private static List<BodyPartGroupDef> Missing(List<BodyPartGroupDef> covered, List<BodyPartGroupDef> wanted)
		{
			List<BodyPartGroupDef> list = new List<BodyPartGroupDef>();
			for (int i = 0; i < wanted.Count; i++)
			{
				if (wanted[i] != null && !covered.Contains(wanted[i]))
				{
					list.Add(wanted[i]);
				}
			}
			return list;
		}

		private static List<BodyPartGroupDef> CoveredGroups(Pawn pawn, ThingDef adding)
		{
			List<BodyPartGroupDef> list = new List<BodyPartGroupDef>();
			if (pawn != null && pawn.apparel != null)
			{
				List<Apparel> wornApparel = pawn.apparel.WornApparel;
				for (int i = 0; i < wornApparel.Count; i++)
				{
					if (adding == null || ApparelUtility.CanWearTogether(adding, wornApparel[i].def, pawn.RaceProps.body))
					{
						AddGroups(list, wornApparel[i].def);
					}
				}
			}
			AddGroups(list, adding);
			return list;
		}

		private static void AddGroups(List<BodyPartGroupDef> covered, ThingDef def)
		{
			if (def == null || def.apparel == null || def.apparel.bodyPartGroups == null)
			{
				return;
			}
			for (int i = 0; i < def.apparel.bodyPartGroups.Count; i++)
			{
				BodyPartGroupDef bodyPartGroupDef = def.apparel.bodyPartGroups[i];
				if (bodyPartGroupDef != null && !covered.Contains(bodyPartGroupDef))
				{
					covered.Add(bodyPartGroupDef);
				}
			}
		}
	}
	// 2.12.0: a threat counts only after it has lasted a little, and it ends only after a
	// while without any enemy. One enemy wandering in and out of the map, or the danger
	// level jumping up and down, no longer makes colonists put armor on and off.
	public static class SteadyThreat
	{
		private const int StartAfter = 300;
		private const int EndAfter = 3000;
		private const int CheckEvery = 60;

		private sealed class State
		{
			public int firstSeen = -1;
			public int lastSeen = -999999;
			public int checkedAt = -999999;
			public bool active;
		}

		private static readonly Dictionary<int, State> states = new Dictionary<int, State>();

		public static void ResetGameState()
		{
			states.Clear();
		}

		public static bool Active(Map map)
		{
			if (map == null || Find.TickManager == null)
			{
				return false;
			}
			int now = Find.TickManager.TicksGame;
			State st;
			if (!states.TryGetValue(map.uniqueID, out st))
			{
				st = new State();
				states[map.uniqueID] = st;
			}
			if (now - st.checkedAt < CheckEvery && now >= st.checkedAt)
			{
				return st.active;
			}
			st.checkedAt = now;
			bool raw = false;
			try
			{
				raw = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false)
				      || (map.dangerWatcher != null && map.dangerWatcher.DangerRating == StoryDanger.High);
			}
			catch
			{
			}
			if (raw)
			{
				if (st.firstSeen < 0 || now - st.lastSeen > EndAfter)
				{
					st.firstSeen = now;
				}
				st.lastSeen = now;
			}
			else if (now - st.lastSeen > EndAfter)
			{
				st.firstSeen = -1;
			}
			st.active = st.firstSeen >= 0 && now - st.firstSeen >= StartAfter && now - st.lastSeen <= EndAfter;
			return st.active;
		}
	}

	// 2.12.0: a colonist in body armor completes the set – helmet, gloves, boots – and does
	// not swap a piece of the set for a weaker one. Extra warm pieces only when it is not hot.
	public static class ArmorSet
	{
		public static bool WearsBodyArmor(Pawn pawn)
		{
			if (pawn == null || pawn.apparel == null)
			{
				return false;
			}
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel a = worn[i];
				if (a.def.apparel != null && a.def.apparel.bodyPartGroups != null && LoadoutFilter.IsArmor(a))
				{
					for (int j = 0; j < a.def.apparel.bodyPartGroups.Count; j++)
					{
						if (a.def.apparel.bodyPartGroups[j] == BodyPartGroupDefOf.Torso)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		public static bool Hot(Pawn pawn)
		{
			if (pawn == null || pawn.MapHeld == null)
			{
				return false;
			}
			int cold;
			int heat;
			LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
			if (heat > 0)
			{
				return true;
			}
			try
			{
				float max = pawn.GetStatValue(StatDefOf.ComfyTemperatureMax);
				float t = Mathf.Max(pawn.MapHeld.mapTemperature.OutdoorTemp, pawn.AmbientTemperature);
				return t >= max - 3f;
			}
			catch
			{
				return false;
			}
		}

		// body part groups already protected by a worn armor piece
		private static bool ArmorCovers(Pawn pawn, BodyPartGroupDef g, Apparel except)
		{
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel a = worn[i];
				if (a == except || a.def.apparel == null || a.def.apparel.bodyPartGroups == null || !LoadoutFilter.IsArmor(a))
				{
					continue;
				}
				if (a.def.apparel.bodyPartGroups.Contains(g))
				{
					return true;
				}
			}
			return false;
		}

		public static void Adjust(Pawn pawn, Apparel ap, ref float score)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.dressFullBody || pawn == null || pawn.apparel == null || ap == null || ap.def.apparel == null || score < -0.5f)
			{
				return;
			}
			if (!WearsBodyArmor(pawn))
			{
				return;
			}
			float armor = LoadoutFilter.ArmorValue(ap);
			List<Apparel> worn = pawn.apparel.WornApparel;
			// never swap a piece of the set for a weaker one
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel w = worn[i];
				if (w != ap && LoadoutFilter.IsArmor(w) && !LoadoutFilter.IsBelowMinHp(w)
				    && !ApparelUtility.CanWearTogether(ap.def, w.def, pawn.RaceProps.body) && armor < LoadoutFilter.ArmorValue(w) * 0.9f
				    && !((EmergencyGear.CounterMask(ap.def) & EmergencyGear.Toxic) != 0 && EmergencyGear.ToxicFor(pawn) && !EmergencyGear.Active(pawn.MapHeld, EmergencyGear.Raid)))
				{
					score = -1f;
					return;
				}
			}
			if (armor < 0.15f || ap.def.apparel.bodyPartGroups == null)
			{
				return;
			}
			bool fills = false;
			for (int j = 0; j < ap.def.apparel.bodyPartGroups.Count; j++)
			{
				if (!ArmorCovers(pawn, ap.def.apparel.bodyPartGroups[j], null))
				{
					fills = true;
					break;
				}
			}
			if (!fills)
			{
				return;
			}
			// a very warm piece (fur helmet...) only when it is not hot
			if (Hot(pawn) && ap.GetStatValue(StatDefOf.Insulation_Cold) >= 10f && ap.GetStatValue(StatDefOf.Insulation_Heat) <= 0f)
			{
				return;
			}
			score += 0.6f + armor;
		}
	}

	// 2.14.0: more sensible choices – a gas mask only for who is in toxic air, never two masks,
	// an armored helmet before a plain hat, and the helmet comes back after the danger
	public static class GearSense
	{
		private static readonly Dictionary<ThingDef, bool> maskCache = new Dictionary<ThingDef, bool>();

		// protective gear against toxic air with (almost) no armor: gas masks, respirators …
		public static bool IsMask(ThingDef def)
		{
			if (def == null || !def.IsApparel)
			{
				return false;
			}
			bool v;
			if (maskCache.TryGetValue(def, out v))
			{
				return v;
			}
			v = false;
			try
			{
				if ((EmergencyGear.CounterMask(def) & EmergencyGear.Toxic) != 0)
				{
					ThingDef stuff = def.MadeFromStuff ? GenStuff.DefaultStuffFor(def) : null;
					float armor = Mathf.Max(def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), def.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff));
					// 2.16.3: face masks of mods often have a little armour (Tailin gas mask) – up to 0.3 still a mask
					v = armor < 0.3f && def.GetStatValueAbstract(StatDefOf.Insulation_Cold, stuff) < 6f;
				}
			}
			catch
			{
			}
			maskCache[def] = v;
			return v;
		}

		public static bool NeedsMask(Pawn pawn)
		{
			return EmergencyGear.ToxicFor(pawn, true) || ThreatResponse.ToxicCondition(pawn == null ? null : pawn.MapHeld);
		}

		// only judged when toxic detection is on – without it we can not know who needs one
		public static bool UnneededMask(Pawn pawn, Apparel ap)
		{
			return ap != null && IsMask(ap.def) && EmergencyGear.ToxicDetectionOn && !NeedsMask(pawn);
		}

		public static bool Required(Pawn pawn, ThingDef def)
		{
			try
			{
				List<ApparelRequirementWithSource> req = pawn.apparel == null ? null : pawn.apparel.AllRequirements;
				if (req == null)
				{
					return false;
				}
				for (int i = 0; i < req.Count; i++)
				{
					if (req[i].requirement != null && req[i].requirement.RequiredForPawn(pawn, def))
					{
						return true;
					}
				}
			}
			catch
			{
			}
			return false;
		}

		private static float ToxicRes(Apparel a)
		{
			return Mathf.Max(a.GetStatValue(StatDefOf.ToxicEnvironmentResistance), 0f);
		}

		// the candidate would be worn next to toxic gear the colonist already has
		public static bool AddsSecondMask(Pawn pawn, Apparel ap)
		{
			if (pawn.apparel == null || !IsMask(ap.def))
			{
				return false;
			}
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel w = worn[i];
				if (w != ap && (EmergencyGear.CounterMask(w.def) & EmergencyGear.Toxic) != 0 && ApparelUtility.CanWearTogether(ap.def, w.def, pawn.RaceProps.body))
				{
					return true;
				}
			}
			return false;
		}

		// a worn mask next to other, at least as good toxic gear
		public static bool WeakerDuplicate(Pawn pawn, Apparel mask)
		{
			List<Apparel> worn = pawn.apparel.WornApparel;
			float mine = ToxicRes(mask);
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel w = worn[i];
				if (w == mask || (EmergencyGear.CounterMask(w.def) & EmergencyGear.Toxic) == 0)
				{
					continue;
				}
				float other = ToxicRes(w);
				// equal ones: keep the one worn first (lower index)
				if (other > mine + 0.001f || (Mathf.Abs(other - mine) <= 0.001f && (!IsMask(w.def) || i < worn.IndexOf(mask))))
				{
					return true;
				}
			}
			return false;
		}

		public static bool IsHelmet(Apparel ap)
		{
			ApparelProperties p = ap.def.apparel;
			return p != null && p.layers != null && p.layers.Contains(ApparelLayerDefOf.Overhead) && LoadoutFilter.ArmorValue(ap) >= 0.15f;
		}

		// replaces only head things without armor (hat, cap, an unneeded mask)
		private static bool ReplacesOnlySoftHeadgear(Pawn pawn, Apparel ap, out bool any)
		{
			any = false;
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = 0; i < worn.Count; i++)
			{
				Apparel w = worn[i];
				if (ApparelUtility.CanWearTogether(ap.def, w.def, pawn.RaceProps.body))
				{
					continue;
				}
				any = true;
				if (LoadoutFilter.ArmorValue(w) >= 0.1f || LoadoutFilter.IsProtectedApparel(pawn, w) || Required(pawn, w.def) || (IsMask(w.def) && NeedsMask(pawn)))
				{
					return false;
				}
			}
			return true;
		}

		// a helmet taken off for a mask (or a hat) may be put on again right away
		public static bool MayWearAgain(Pawn pawn, Apparel ap)
		{
			try
			{
				if (IsMask(ap.def))
				{
					return NeedsMask(pawn);
				}
				bool any;
				return IsHelmet(ap) && ReplacesOnlySoftHeadgear(pawn, ap, out any);
			}
			catch
			{
				return false;
			}
		}

		public static void Adjust(Pawn pawn, Apparel ap, ref float score)
		{
			if (pawn == null || ap == null || pawn.apparel == null || ap.def.apparel == null)
			{
				return;
			}
			try
			{
				if (IsMask(ap.def) && !pawn.apparel.WornApparel.Contains(ap))
				{
					if (AddsSecondMask(pawn, ap) || (EmergencyGear.ToxicDetectionOn && !NeedsMask(pawn)))
					{
						score = -1f;
						return;
					}
				}
				if (score > -0.5f && IsHelmet(ap) && !pawn.apparel.WornApparel.Contains(ap))
				{
					int cold;
					int heat;
					LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
					bool any;
					if (cold == 0 && heat == 0 && ReplacesOnlySoftHeadgear(pawn, ap, out any))
					{
						score += (any ? 0.6f : 0.2f) + 2f * LoadoutFilter.ArmorValue(ap);
					}
				}
			}
			catch
			{
			}
		}
	}

	public static class ThreatResponse
	{
		private struct Need
		{
			public StatDef stat;

			public float min;

			public float floor;

			public float boost;

			public float scale;
		}

		private static readonly HashSet<long> announced = new HashSet<long>();

		public static void ResetGameState()
		{
			announced.Clear();
		}

		public static bool Enabled
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return settings != null && settings.enabled && settings.threatApparel;
			}
		}

		public static void Boost(Pawn pawn, Apparel apparel, ref float score)
		{
			if (!Enabled || pawn == null || apparel == null || score < 0f || !pawn.IsColonist || pawn.Map == null)
			{
				return;
			}
			List<Need> list = new List<Need>();
			CollectNeeds(pawn, pawn.Map, list);
			for (int i = 0; i < list.Count; i++)
			{
				float statValue = apparel.GetStatValue(list[i].stat);
				if (statValue > list[i].floor)
				{
					score += list[i].boost + statValue * list[i].scale;
				}
			}
		}

		public static void Tick()
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (!Enabled || settings == null || !settings.threatBills || Find.Maps == null)
			{
				return;
			}
			List<Need> list = new List<Need>();
			for (int i = 0; i < Find.Maps.Count; i++)
			{
				Map map = Find.Maps[i];
				if (map == null || !map.IsPlayerHome)
				{
					continue;
				}
				list.Clear();
				foreach (Pawn freeColonist in map.mapPawns.FreeColonists)
				{
					CollectNeeds(freeColonist, map, list);
				}
				for (int j = 0; j < list.Count; j++)
				{
					EnsureApparel(map, list[j].stat, list[j].min, list[j].stat.shortHash);
				}
			}
		}

		private static void CollectNeeds(Pawn pawn, Map map, List<Need> needs)
		{
			if (pawn != null && map != null && !pawn.Dead)
			{
				int cold;
				int heat;
				LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
				if (cold > 0)
				{
					AddNeed(needs, StatDefOf.Insulation_Cold, 12f, 8f, 220f, 1f);
				}
				if (heat > 0)
				{
					AddNeed(needs, StatDefOf.Insulation_Heat, 12f, 8f, 220f, 1f);
				}
				if (HazardActive(map, pawn))
				{
					AddNeed(needs, StatDefOf.ToxicEnvironmentResistance, 0.35f, 0.05f, 500f, 300f);
				}
				StatDef namedSilentFail = DefDatabase<StatDef>.GetNamedSilentFail("VacuumResistance");
				if (namedSilentFail != null && VacuumActive(map))
				{
					AddNeed(needs, namedSilentFail, 0.3f, 0.05f, 500f, 200f);
				}
				// 2.12.0: only a threat that lasts (no on/off flapping while one enemy wanders around)
				if (SteadyThreat.Active(map))
				{
					AddNeed(needs, StatDefOf.ArmorRating_Sharp, 0.15f, 0.05f, 120f, 80f);
				}
			}
		}

		private static void AddNeed(List<Need> needs, StatDef stat, float min, float floor, float boost, float scale)
		{
			if (stat == null)
			{
				return;
			}
			for (int i = 0; i < needs.Count; i++)
			{
				if (needs[i].stat == stat)
				{
					return;
				}
			}
			needs.Add(new Need
			{
				stat = stat,
				min = min,
				floor = floor,
				boost = boost,
				scale = scale
			});
		}

		private static bool Has(string s, string part)
		{
			return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool HazardActive(Map map, Pawn pawn)
		{
			// 2.8.0: toxic gas on the map detected by EmergencyGear – 2.14.0: only for colonists who are in it
			if (EmergencyGear.ToxicFor(pawn))
			{
				return true;
			}
			return ToxicCondition(map);
		}

		public static bool ToxicCondition(Map map)
		{
			if (map != null && map.gameConditionManager != null)
			{
				foreach (GameCondition activeCondition in map.gameConditionManager.ActiveConditions)
				{
					if (activeCondition != null && activeCondition.def != null)
					{
						string s = activeCondition.def.defName + activeCondition.GetType().Name;
						if (Has(s, "Toxic") || Has(s, "Poison") || Has(s, "Fallout") || Has(s, "Pollution") || Has(s, "Radiation") || Has(s, "Acid"))
						{
							return true;
						}
					}
				}
			}
			// 2.12.0: no longer "is the colonist standing on polluted ground" – that switched
			// on and off with every step and colonists kept changing clothes
			return false;
		}

		private static bool VacuumActive(Map map)
		{
			if (map.gameConditionManager == null)
			{
				return false;
			}
			foreach (GameCondition activeCondition in map.gameConditionManager.ActiveConditions)
			{
				if (activeCondition != null && activeCondition.def != null)
				{
					string s = activeCondition.def.defName + activeCondition.GetType().Name;
					if (Has(s, "Vacuum") || Has(s, "Decompression"))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static void EnsureApparel(Map map, StatDef stat, float min, int kind)
		{
			int num = 0;
			foreach (Pawn freeColonist in map.mapPawns.FreeColonists)
			{
				if (freeColonist != null && !freeColonist.Dead && freeColonist.GetStatValue(stat) < min)
				{
					num++;
				}
			}
			if (num <= 0 || CountUseful(map, stat, min) >= num)
			{
				return;
			}
			ThingDef thingDef = BestCraftable(stat, min);
			if (thingDef == null)
			{
				// 2.6.4: map id * 10 + stat hash collided between maps/stats
				if (announced.Add(((long)map.uniqueID << 32) | (uint)kind))
				{
					Messages.Message("RL_ThreatNoRecipe".Translate(), MessageTypeDefOf.NeutralEvent, false);
				}
			}
			else
			{
				if (HasBill(map, thingDef))
				{
					return;
				}
				RecipeDef recipeDef = RecipeFor(thingDef);
				Building_WorkTable building_WorkTable = ((recipeDef == null) ? null : FindBench(map, recipeDef));
				if (building_WorkTable != null)
				{
					Bill_Production bill_Production = recipeDef.MakeNewBill() as Bill_Production;
					if (bill_Production != null)
					{
						bill_Production.repeatMode = BillRepeatModeDefOf.TargetCount;
						bill_Production.targetCount = num;
						bill_Production.SetStoreMode(BillStoreModeDefOf.BestStockpile);
						building_WorkTable.billStack.AddBill(bill_Production);
						Messages.Message("RL_ThreatBill".Translate(thingDef.LabelCap), MessageTypeDefOf.PositiveEvent, false);
					}
				}
			}
		}

		private static int CountUseful(Map map, StatDef stat, float min)
		{
			int num = 0;
			foreach (Thing item in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
			{
				if (item != null && !item.IsForbidden(Faction.OfPlayer) && item.GetStatValue(stat) >= min && (LoadoutFilter.IsAllowed(item.def) || EmergencyGear.ExemptKinds(map, item.def) != 0))
				{
					num += item.stackCount;
				}
			}
			foreach (Pawn freeColonist in map.mapPawns.FreeColonists)
			{
				if (freeColonist == null || freeColonist.apparel == null)
				{
					continue;
				}
				foreach (Apparel item2 in freeColonist.apparel.WornApparel)
				{
					if (item2.GetStatValue(stat) >= min)
					{
						num++;
						break;
					}
				}
			}
			return num;
		}

		private static ThingDef BestCraftable(StatDef stat, float min)
		{
			ThingDef result = null;
			float num = min;
			foreach (RecipeDef item in DefDatabase<RecipeDef>.AllDefsListForReading)
			{
				if (item == null || item.products == null || !item.AvailableNow)
				{
					continue;
				}
				foreach (ThingDefCountClass product in item.products)
				{
					ThingDef thingDef = ((product == null) ? null : product.thingDef);
					if (thingDef != null && thingDef.IsApparel && LoadoutFilter.IsAllowed(thingDef))
					{
						float statValueAbstract = thingDef.GetStatValueAbstract(stat, (!thingDef.MadeFromStuff) ? null : GenStuff.DefaultStuffFor(thingDef));
						if (statValueAbstract >= num)
						{
							num = statValueAbstract;
							result = thingDef;
						}
					}
				}
			}
			return result;
		}

		private static RecipeDef RecipeFor(ThingDef product)
		{
			foreach (RecipeDef item in DefDatabase<RecipeDef>.AllDefsListForReading)
			{
				if (item == null || item.products == null || !item.AvailableNow)
				{
					continue;
				}
				foreach (ThingDefCountClass product2 in item.products)
				{
					if (product2 != null && product2.thingDef == product)
					{
						return item;
					}
				}
			}
			return null;
		}

		public static bool HasBill(Map map, ThingDef product)
		{
			foreach (Building item in map.listerBuildings.allBuildingsColonist)
			{
				Building_WorkTable building_WorkTable = item as Building_WorkTable;
				if (building_WorkTable == null)
				{
					continue;
				}
				foreach (Bill item2 in building_WorkTable.billStack)
				{
					if (item2 == null || item2.recipe == null || item2.recipe.products == null)
					{
						continue;
					}
					foreach (ThingDefCountClass product2 in item2.recipe.products)
					{
						if (product2 != null && product2.thingDef == product)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		public static Building_WorkTable FindBench(Map map, RecipeDef recipe)
		{
			foreach (Building item in map.listerBuildings.allBuildingsColonist)
			{
				Building_WorkTable building_WorkTable = item as Building_WorkTable;
				if (building_WorkTable != null && recipe.AvailableOnNow(building_WorkTable))
				{
					return building_WorkTable;
				}
			}
			return null;
		}
	}
	// 2.8.0: "Emergency gear" – while a hazard is active on a map (toxic air, extreme cold/heat, an attack),
	// gear that counters it may be used even when it is not researched / not crafted yet.
	// Hazards are evaluated per map every CheckInterval ticks (cheap samples only), counter kinds are cached per def.
	public static class EmergencyGear
	{
		public const int Toxic = 1;

		public const int Cold = 2;

		public const int Heat = 4;

		public const int Raid = 8;

		private const int CheckInterval = 250;

		// a hazard must be missing in this many checks in a row before it ends (no flapping)
		private const int EndConfirmChecks = 6;

		// worn emergency gear is kept this long after the hazard ends; afterwards the normal filter applies again
		private const int LingerTicks = 5000;

		private sealed class MapState
		{
			public int active;

			public int announced;

			public int provisioned;

			public int checks;

			public readonly int[] misses = new int[4];

			public readonly int[] endedAt = new int[4] { -999999, -999999, -999999, -999999 };
		}

		private static readonly Dictionary<int, MapState> states = new Dictionary<int, MapState>();

		private static readonly Dictionary<ThingDef, int> counterCache = new Dictionary<ThingDef, int>();

		private static readonly Dictionary<int, float> lastToxic = new Dictionary<int, float>();

		private static readonly List<int> scratchIds = new List<int>();

		// 2.14.0: toxic air is per colonist – who was actually in it and when; map-wide only for
		// weather-like conditions (fallout, haze) and gas spread over the home area
		private static readonly Dictionary<int, int> exposedAt = new Dictionary<int, int>();

		private static readonly Dictionary<int, int> wideAt = new Dictionary<int, int>();

		private const int ExposureTicks = CheckInterval * (EndConfirmChecks + 1);

		public static bool ToxicDetectionOn => (EnabledMask() & Toxic) != 0;

		// this colonist needs toxic protection now (linger: or did so recently)
		public static bool ToxicFor(Pawn pawn, bool linger = false)
		{
			if (pawn == null || !ToxicDetectionOn)
			{
				return false;
			}
			Map map = pawn.MapHeld;
			if (map == null || Find.TickManager == null)
			{
				return false;
			}
			int now = Find.TickManager.TicksGame;
			int window = linger ? LingerTicks : ExposureTicks;
			int t;
			if (wideAt.TryGetValue(map.uniqueID, out t) && now - t < window)
			{
				return true;
			}
			return exposedAt.TryGetValue(pawn.thingIDNumber, out t) && now - t < window;
		}

		private static readonly string[] ConditionKeywords = new string[9] { "Toxic", "Poison", "Fallout", "Pollut", "Gas", "Smog", "Smoke", "Miasma", "Acid" };

		private static readonly string[] GasThingKeywords = new string[6] { "Tox", "Poison", "Gas", "Stink", "Chem", "Acid" };

		public static void ResetGameState()
		{
			states.Clear();
			counterCache.Clear();
			lastToxic.Clear();
			exposedAt.Clear();
			wideAt.Clear();
			substituteCache.Clear();
			globalChecks = 0;
		}

		private static int Bit(int kind)
		{
			switch (kind)
			{
			case Toxic:
				return 0;
			case Cold:
				return 1;
			case Heat:
				return 2;
			default:
				return 3;
			}
		}

		private static int KindOfBit(int bit)
		{
			return 1 << bit;
		}

		// the kinds currently switched on in the settings
		public static int EnabledMask()
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.emergencyGear)
			{
				return 0;
			}
			int num = 0;
			if (settings.emergencyToxic)
			{
				num |= Toxic;
			}
			if (settings.emergencyTemperature)
			{
				num |= Cold | Heat;
			}
			if (settings.emergencyRaid)
			{
				num |= Raid;
			}
			return num;
		}

		public static int ActiveMask(Map map)
		{
			if (map == null)
			{
				return 0;
			}
			MapState value;
			if (!states.TryGetValue(map.uniqueID, out value))
			{
				return 0;
			}
			return value.active & EnabledMask();
		}

		public static bool Active(Map map, int kind)
		{
			return (ActiveMask(map) & kind) != 0;
		}

		public static bool AnyActive(Map map)
		{
			return ActiveMask(map) != 0;
		}

		// active now, or ended less than LingerTicks ago
		private static int LingerMask(Map map)
		{
			if (map == null)
			{
				return 0;
			}
			MapState value;
			if (!states.TryGetValue(map.uniqueID, out value))
			{
				return 0;
			}
			int num = value.active;
			int num2 = ((Find.TickManager == null) ? 0 : Find.TickManager.TicksGame);
			for (int i = 0; i < 4; i++)
			{
				if (num2 - value.endedAt[i] < LingerTicks)
				{
					num |= KindOfBit(i);
				}
			}
			return num & EnabledMask();
		}

		// ---------- which gear counters which hazard (def level, cached) ----------

		public static int CounterMask(ThingDef def)
		{
			if (def == null)
			{
				return 0;
			}
			int value;
			if (counterCache.TryGetValue(def, out value))
			{
				return value;
			}
			value = 0;
			try
			{
				if (def.IsApparel)
				{
					if (Positive(def, StatDefOf.ToxicEnvironmentResistance) || Positive(def, StatDefOf.ToxicResistance))
					{
						value |= Toxic;
					}
					ThingDef stuff = ((!def.MadeFromStuff) ? null : GenStuff.DefaultStuffFor(def));
					float statValueAbstract = def.GetStatValueAbstract(StatDefOf.Insulation_Cold, stuff);
					float statValueAbstract2 = def.GetStatValueAbstract(StatDefOf.Insulation_Heat, stuff);
					float num = Offset(def, StatDefOf.ComfyTemperatureMin);
					float num2 = Offset(def, StatDefOf.ComfyTemperatureMax);
					if (statValueAbstract >= 6f || Offset(def, StatDefOf.Insulation_Cold) >= 6f || num <= -5f)
					{
						value |= Cold;
					}
					if (statValueAbstract2 >= 6f || Offset(def, StatDefOf.Insulation_Heat) >= 6f || num2 >= 5f)
					{
						value |= Heat;
					}
					float num3 = Mathf.Max(def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), def.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff));
					if (num3 >= 0.15f)
					{
						value |= Raid;
					}
				}
				else if (LoadoutFilter.IsManagedWeapon(def))
				{
					value |= Raid;
				}
			}
			catch
			{
			}
			counterCache[def] = value;
			return value;
		}

		private static float Offset(ThingDef def, StatDef stat)
		{
			if (stat == null || def.equippedStatOffsets == null)
			{
				return 0f;
			}
			return def.equippedStatOffsets.GetStatOffsetFromList(stat);
		}

		private static bool Positive(ThingDef def, StatDef stat)
		{
			if (stat == null)
			{
				return false;
			}
			if (def.equippedStatOffsets != null && def.equippedStatOffsets.GetStatOffsetFromList(stat) > 0.001f)
			{
				return true;
			}
			return def.statBases != null && def.statBases.GetStatValueFromList(stat, 0f) > 0.001f;
		}

		// ---------- filter exemption ----------

		// the hazard kinds (active on the map) this def counters; 0 = no exemption
		public static int ExemptKinds(Map map, ThingDef def)
		{
			if (map == null || def == null || (!def.IsApparel && !def.IsWeapon))
			{
				return 0;
			}
			int num = ActiveMask(map);
			if (num == 0)
			{
				return 0;
			}
			int num2 = CounterMask(def) & num;
			if (num2 == 0 || LoadoutFilter.IsManuallyBanned(def))
			{
				return 0;
			}
			return num2;
		}

		// apparel: allowed by the emergency (def level)
		public static bool AllowsDef(Pawn pawn, ThingDef def)
		{
			if (pawn == null || def == null || !def.IsApparel)
			{
				return false;
			}
			int kinds = ExemptKinds(pawn.MapHeld, def);
			if ((kinds & Toxic) != 0 && !ToxicFor(pawn, true))
			{
				kinds &= ~Toxic;
			}
			return kinds != 0;
		}

		// apparel or weapon (weapons only during an attack and only the colony's own: stored, carried, or inside the home area)
		public static bool AllowsThing(Pawn pawn, Thing thing)
		{
			if (pawn == null || thing == null || thing.def == null)
			{
				return false;
			}
			if (thing.def.IsApparel)
			{
				return AllowsDef(pawn, thing.def);
			}
			if (!LoadoutFilter.IsManagedWeapon(thing.def))
			{
				return false;
			}
			Map mapHeld = pawn.MapHeld;
			if ((ExemptKinds(mapHeld, thing.def) & Raid) == 0)
			{
				return false;
			}
			// 2.16.5: a not-yet-made weapon only for someone with no weapon at all – an armed colonist
			// no longer swaps a good sword for a locked stun baton (or takes it as a sidearm) in a raid
			Thing primary = pawn.equipment?.Primary;
			if (primary != null && primary != thing)
			{
				return false;
			}
			return IsColonyOwned(thing, mapHeld);
		}

		private static bool IsColonyOwned(Thing thing, Map map)
		{
			Pawn_InventoryTracker pawn_InventoryTracker = thing.ParentHolder as Pawn_InventoryTracker;
			if (pawn_InventoryTracker != null)
			{
				return pawn_InventoryTracker.pawn != null && pawn_InventoryTracker.pawn.Faction != null && pawn_InventoryTracker.pawn.Faction.IsPlayer;
			}
			Pawn_EquipmentTracker pawn_EquipmentTracker = thing.ParentHolder as Pawn_EquipmentTracker;
			if (pawn_EquipmentTracker != null)
			{
				return pawn_EquipmentTracker.pawn != null && pawn_EquipmentTracker.pawn.Faction != null && pawn_EquipmentTracker.pawn.Faction.IsPlayer;
			}
			if (!thing.Spawned || map == null)
			{
				return false;
			}
			if (thing.IsInAnyStorage())
			{
				return true;
			}
			Area home = map.areaManager.Home;
			return home != null && home[thing.Position] && !thing.IsForbidden(Faction.OfPlayer);
		}

		// worn/held emergency gear is not stripped while the hazard lasts and for a while after it ends
		public static bool KeepWorn(Pawn pawn, ThingDef def)
		{
			if (pawn == null || def == null)
			{
				return false;
			}
			Map mapHeld = pawn.MapHeld;
			if (mapHeld == null)
			{
				return false;
			}
			int num = LingerMask(mapHeld);
			if ((num & Toxic) != 0 && !ToxicFor(pawn, true))
			{
				num &= ~Toxic;
			}
			if (num == 0)
			{
				return false;
			}
			return (CounterMask(def) & num) != 0 && !LoadoutFilter.IsManuallyBanned(def);
		}

		// score bonus while the hazard is active, so colonists actually put the best counter on.
		// With "Protection against threats" on, ThreatResponse already gives (a bigger) bonus – no double counting.
		public static void Boost(Pawn pawn, Apparel apparel, ref float score)
		{
			if (pawn == null || apparel == null || score < 0f)
			{
				return;
			}
			Map map = pawn.Map;
			// 2.8.1: weak toxic substitute (face covering without toxic resistance)
			bool toxicHere = Active(map, Toxic) && ToxicFor(pawn);
			if (toxicHere && (CounterMask(apparel.def) & Toxic) == 0 && SubstituteScore(apparel.def, Toxic) > 0f)
			{
				score += 5f;
			}
			if (ThreatResponse.Enabled)
			{
				return;
			}
			int num = ActiveMask(map) & CounterMask(apparel.def);
			if (!toxicHere)
			{
				num &= ~Toxic;
			}
			if (num == 0)
			{
				return;
			}
			if ((num & Toxic) != 0)
			{
				score += 40f + 100f * Mathf.Max(apparel.GetStatValue(StatDefOf.ToxicEnvironmentResistance), 0f);
			}
			if ((num & Cold) != 0)
			{
				score += 8f + 0.5f * Mathf.Max(apparel.GetStatValue(StatDefOf.Insulation_Cold), 0f);
			}
			if ((num & Heat) != 0)
			{
				score += 8f + 0.5f * Mathf.Max(apparel.GetStatValue(StatDefOf.Insulation_Heat), 0f);
			}
			if ((num & Raid) != 0)
			{
				score += 10f + 40f * LoadoutFilter.ArmorValue(apparel);
			}
		}

		// ---------- labels for messages and the tooltip ----------

		public static string KindsLabel(int mask)
		{
			List<string> list = new List<string>();
			if ((mask & Toxic) != 0)
			{
				list.Add("RL_EmKind_Toxic".Translate().ToString());
			}
			if ((mask & Cold) != 0)
			{
				list.Add("RL_EmKind_Cold".Translate().ToString());
			}
			if ((mask & Heat) != 0)
			{
				list.Add("RL_EmKind_Heat".Translate().ToString());
			}
			if ((mask & Raid) != 0)
			{
				list.Add("RL_EmKind_Raid".Translate().ToString());
			}
			return string.Join(", ", list.ToArray());
		}

		private static string GearLabel(int kind)
		{
			switch (kind)
			{
			case Toxic:
				return "RL_EmGear_Toxic".Translate().ToString();
			case Cold:
				return "RL_EmGear_Cold".Translate().ToString();
			case Heat:
				return "RL_EmGear_Heat".Translate().ToString();
			default:
				return "RL_EmGear_Raid".Translate().ToString();
			}
		}

		// one line for LoadoutFilter.Explain, or null
		public static string ExplainLine(ThingDef def)
		{
			if (def == null || (!def.IsApparel && !LoadoutFilter.IsManagedWeapon(def)))
			{
				return null;
			}
			int num = CounterMask(def);
			if (num == 0)
			{
				return null;
			}
			Map map = Find.CurrentMap;
			int num2 = ExemptKinds(map, def);
			if (num2 != 0 && !LoadoutFilter.IsAllowed(def))
			{
				return "RL_Ex_Emergency".Translate(KindsLabel(num2)).ToString();
			}
			if ((num & EnabledMask()) != 0)
			{
				return "RL_Ex_EmergencyFor".Translate(KindsLabel(num & EnabledMask())).ToString();
			}
			return null;
		}

		// ---------- 2.8.1: find, craft, substitute ----------

		// provisioning runs when a hazard starts and then every ProvideEveryChecks hazard checks (~2500 ticks)
		private const int ProvideEveryChecks = 10;

		private static int globalChecks;

		private static readonly Dictionary<ThingDef, float[]> substituteCache = new Dictionary<ThingDef, float[]>();

		private static BodyPartGroupDef mouthGroup;

		private static bool mouthLooked;

		private static bool ProvideEnabled
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return settings != null && settings.emergencyProvide;
			}
		}

		// strength of a proper counter (CounterMask) for the kind; 0 = not a proper counter
		private static float ProperScore(ThingDef def, int kind)
		{
			if (def == null || (CounterMask(def) & kind) == 0)
			{
				return 0f;
			}
			return Mathf.Max(SubstituteScore(def, kind), 0.01f);
		}

		// strength as any (also weak) substitute; proper counters are included; 0 = useless
		public static float SubstituteScore(ThingDef def, int kind)
		{
			if (def == null || !def.IsApparel)
			{
				return 0f;
			}
			float[] value;
			if (!substituteCache.TryGetValue(def, out value))
			{
				value = new float[4];
				try
				{
					ThingDef stuff = ((!def.MadeFromStuff) ? null : GenStuff.DefaultStuffFor(def));
					float num = Mathf.Max(StatAny(def, StatDefOf.ToxicEnvironmentResistance), StatAny(def, StatDefOf.ToxicResistance));
					if (num > 0.001f)
					{
						value[0] = 1f + num * 100f;
					}
					else if (CoversFace(def))
					{
						value[0] = 0.5f;
					}
					float num2 = def.GetStatValueAbstract(StatDefOf.Insulation_Cold, stuff) - Offset(def, StatDefOf.ComfyTemperatureMin);
					if (num2 >= 2f)
					{
						value[1] = num2;
					}
					float num3 = def.GetStatValueAbstract(StatDefOf.Insulation_Heat, stuff) + Offset(def, StatDefOf.ComfyTemperatureMax);
					if (num3 >= 2f)
					{
						value[2] = num3;
					}
					float num4 = Mathf.Max(def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), def.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff));
					if (num4 >= 0.05f)
					{
						value[3] = num4 * 100f;
					}
				}
				catch
				{
				}
				substituteCache[def] = value;
			}
			return value[Bit(kind)];
		}

		private static float StatAny(ThingDef def, StatDef stat)
		{
			if (stat == null)
			{
				return 0f;
			}
			float num = ((def.equippedStatOffsets == null) ? 0f : def.equippedStatOffsets.GetStatOffsetFromList(stat));
			float num2 = ((def.statBases == null) ? 0f : def.statBases.GetStatValueFromList(stat, 0f));
			return Mathf.Max(num, num2);
		}

		// weak toxic fallback: face/head coverings over the mouth, nose or eyes
		private static bool CoversFace(ThingDef def)
		{
			if (def.apparel == null || def.apparel.bodyPartGroups == null)
			{
				return false;
			}
			if (!mouthLooked)
			{
				mouthLooked = true;
				mouthGroup = DefDatabase<BodyPartGroupDef>.GetNamedSilentFail("Mouth");
			}
			List<BodyPartGroupDef> bodyPartGroups = def.apparel.bodyPartGroups;
			for (int i = 0; i < bodyPartGroups.Count; i++)
			{
				BodyPartGroupDef bodyPartGroupDef = bodyPartGroups[i];
				if (bodyPartGroupDef != null && (bodyPartGroupDef == BodyPartGroupDefOf.FullHead || bodyPartGroupDef == BodyPartGroupDefOf.Eyes || (mouthGroup != null && bodyPartGroupDef == mouthGroup)))
				{
					return true;
				}
			}
			return false;
		}

		private static float ItemScore(ThingDef def, int kind, bool proper)
		{
			return (!proper) ? SubstituteScore(def, kind) : ProperScore(def, kind);
		}

		private static bool Wears(Pawn pawn, int kind, bool proper)
		{
			if (pawn.apparel == null)
			{
				return false;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (ItemScore(wornApparel[i].def, kind, proper) > 0f)
				{
					return true;
				}
			}
			return false;
		}

		// an item colonists could take right now: allowed (proper counters are exempt during the hazard), not banned, not burning
		private static bool Usable(Thing thing, int kind, bool proper, Map map)
		{
			if (thing == null || thing.def == null || !thing.Spawned || thing.Destroyed || LoadoutFilter.IsManuallyBanned(thing.def) || thing.IsBurning() || LoadoutFilter.IsBelowMinHp(thing))
			{
				return false;
			}
			if (ItemScore(thing.def, kind, proper) <= 0f)
			{
				return false;
			}
			return LoadoutFilter.IsAllowed(thing.def) || (ExemptKinds(map, thing.def) & kind) != 0;
		}

		private static void CollectHostiles(Map map, List<IntVec3> hostiles)
		{
			hostiles.Clear();
			if (map.attackTargetsCache == null)
			{
				return;
			}
			foreach (IAttackTarget item in map.attackTargetsCache.TargetsHostileToColony)
			{
				Thing thing = ((item == null) ? null : item.Thing);
				if (thing == null || !thing.Spawned)
				{
					continue;
				}
				Pawn pawn = thing as Pawn;
				if (pawn != null && (pawn.Downed || pawn.Dead))
				{
					continue;
				}
				hostiles.Add(thing.Position);
				if (hostiles.Count >= 200)
				{
					break;
				}
			}
		}

		private static bool NearHostile(IntVec3 c, List<IntVec3> hostiles)
		{
			for (int i = 0; i < hostiles.Count; i++)
			{
				if ((hostiles[i] - c).LengthHorizontalSquared <= 400)
				{
					return true;
				}
			}
			return false;
		}

		private static int CountUsable(List<Thing> things, int kind, bool proper, Map map)
		{
			int num = 0;
			for (int i = 0; i < things.Count; i++)
			{
				Thing thing = things[i];
				if (Usable(thing, kind, proper, map) && !thing.IsForbidden(Faction.OfPlayer))
				{
					num += thing.stackCount;
				}
			}
			return num;
		}

		// unforbids up to `count` usable forbidden items anywhere on the map (best first); returns how many
		private static int Unforbid(Map map, List<Thing> things, int kind, bool proper, bool weapons, int count, List<IntVec3> hostiles, List<string> labels)
		{
			if (count <= 0)
			{
				return 0;
			}
			List<Thing> list = new List<Thing>();
			for (int i = 0; i < things.Count; i++)
			{
				Thing thing = things[i];
				if (thing == null || !thing.IsForbidden(Faction.OfPlayer))
				{
					continue;
				}
				if (weapons)
				{
					if (thing.def == null || !thing.Spawned || !LoadoutFilter.IsManagedWeapon(thing.def) || LoadoutFilter.IsManuallyBanned(thing.def) || thing.IsBurning() || (!LoadoutFilter.IsAllowed(thing.def) && (ExemptKinds(map, thing.def) & Raid) == 0))
					{
						continue;
					}
				}
				else if (!Usable(thing, kind, proper, map))
				{
					continue;
				}
				IntVec3 position = thing.Position;
				if (!position.Fogged(map) && !position.ContainsStaticFire(map) && !NearHostile(position, hostiles) && map.reachability.CanReachColony(position))
				{
					list.Add(thing);
				}
			}
			if (list.Count == 0)
			{
				return 0;
			}
			if (weapons)
			{
				list.Sort(CompareMarketValue);
			}
			else
			{
				sortKind = kind;
				sortProper = proper;
				list.Sort(CompareScore);
			}
			int num = 0;
			for (int j = 0; j < list.Count && num < count; j++)
			{
				list[j].SetForbidden(false, false);
				num++;
				if (labels.Count < 4)
				{
					labels.Add(list[j].LabelShort);
				}
			}
			return num;
		}

		private static int sortKind;

		private static bool sortProper;

		private static int CompareScore(Thing a, Thing b)
		{
			return ItemScore(b.def, sortKind, sortProper).CompareTo(ItemScore(a.def, sortKind, sortProper));
		}

		private static int CompareMarketValue(Thing a, Thing b)
		{
			return b.MarketValue.CompareTo(a.MarketValue);
		}

		// best apparel with a researched (AvailableNow) recipe and a built bench that can make it
		private static bool FindCraftable(Map map, int kind, bool proper, out ThingDef best, out RecipeDef bestRecipe, out Building_WorkTable bestBench)
		{
			best = null;
			bestRecipe = null;
			bestBench = null;
			float num = 0f;
			List<RecipeDef> allDefsListForReading = DefDatabase<RecipeDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				RecipeDef recipeDef = allDefsListForReading[i];
				if (recipeDef == null || recipeDef.products == null || recipeDef.products.Count == 0 || !recipeDef.AvailableNow)
				{
					continue;
				}
				for (int j = 0; j < recipeDef.products.Count; j++)
				{
					ThingDefCountClass thingDefCountClass = recipeDef.products[j];
					ThingDef thingDef = ((thingDefCountClass == null) ? null : thingDefCountClass.thingDef);
					if (thingDef == null || !thingDef.IsApparel || LoadoutFilter.IsManuallyBanned(thingDef))
					{
						continue;
					}
					float num2 = ItemScore(thingDef, kind, proper);
					if (num2 > num)
					{
						Building_WorkTable building_WorkTable = ThreatResponse.FindBench(map, recipeDef);
						if (building_WorkTable != null)
						{
							num = num2;
							best = thingDef;
							bestRecipe = recipeDef;
							bestBench = building_WorkTable;
						}
					}
				}
			}
			return best != null;
		}

		// adds a "do X" bill marked as the mod's; returns a message part
		private static string AddBill(Map map, int kind, ThingDef def, RecipeDef recipe, Building_WorkTable bench, int count, bool substitute)
		{
			if (ThreatResponse.HasBill(map, def))
			{
				return "RL_EmProv_BillExists".Translate(def.label).ToString();
			}
			Bill_Production bill_Production = recipe.MakeNewBill() as Bill_Production;
			if (bill_Production == null)
			{
				return null;
			}
			bill_Production.repeatMode = BillRepeatModeDefOf.RepeatCount;
			bill_Production.repeatCount = count;
			bill_Production.SetStoreMode(BillStoreModeDefOf.BestStockpile);
			bench.billStack.AddBill(bill_Production);
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			if (researchedLoadoutGameComponent != null)
			{
				researchedLoadoutGameComponent.emergencyBills.Add(bill_Production.GetUniqueLoadID() + "|" + kind + "|" + map.uniqueID);
			}
			RL.Debug("emergency bill: " + count + "x " + def.defName + " at " + bench.def.defName + (substitute ? " (substitute)" : string.Empty));
			return (substitute ? "RL_EmProv_Substitute" : "RL_EmProv_Bill").Translate(count, def.label, bench.LabelShort).ToString();
		}

		private static void Provide(Map map, MapState state, int kind, List<Pawn> colonists)
		{
			List<Pawn> list = new List<Pawn>();
			for (int i = 0; i < colonists.Count; i++)
			{
				Pawn pawn = colonists[i];
				if (pawn != null && !pawn.Dead && pawn.apparel != null && LoadoutFilter.IsPlayerColonist(pawn) && !ExemptPawns.IsExempt(pawn) && !pawn.IsQuestLodger())
				{
					list.Add(pawn);
				}
			}
			if (list.Count == 0)
			{
				return;
			}
			List<Thing> list2 = map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
			List<IntVec3> list3 = new List<IntVec3>();
			if (Active(map, Raid))
			{
				CollectHostiles(map, list3);
			}
			List<string> list4 = new List<string>();
			List<string> list5 = new List<string>();
			int num = 0;
			// proper counters: find, then craft
			int num2 = 0;
			for (int j = 0; j < list.Count; j++)
			{
				if (!Wears(list[j], kind, true))
				{
					num2++;
				}
			}
			int num3 = num2 - CountUsable(list2, kind, true, map);
			if (num3 > 0)
			{
				int num4 = Unforbid(map, list2, kind, true, false, num3, list3, list4);
				num += num4;
				num3 -= num4;
			}
			if (num3 > 0)
			{
				ThingDef def;
				RecipeDef recipe;
				Building_WorkTable bench;
				if (FindCraftable(map, kind, true, out def, out recipe, out bench))
				{
					string text = AddBill(map, kind, def, recipe, bench, Mathf.Min(num3, list.Count), false);
					if (text != null)
					{
						list5.Add(text);
					}
				}
				else
				{
					// no recipe for proper protection: the closest substitute, found or crafted
					int num5 = 0;
					for (int k = 0; k < list.Count; k++)
					{
						if (!Wears(list[k], kind, false))
						{
							num5++;
						}
					}
					int num6 = Mathf.Min(num3, num5 - CountUsable(list2, kind, false, map));
					if (num6 > 0)
					{
						int num7 = Unforbid(map, list2, kind, false, false, num6, list3, list4);
						num += num7;
						num6 -= num7;
					}
					ThingDef def2;
					RecipeDef recipe2;
					Building_WorkTable bench2;
					if (num6 > 0 && FindCraftable(map, kind, false, out def2, out recipe2, out bench2))
					{
						string text2 = AddBill(map, kind, def2, recipe2, bench2, Mathf.Min(num6, list.Count), true);
						if (text2 != null)
						{
							list5.Add(text2);
						}
					}
					else if (num6 > 0 && num == 0)
					{
						list5.Add("RL_EmProv_Nothing".Translate().ToString());
					}
				}
			}
			// attack: unarmed colonists get the colony's forbidden weapons unforbidden
			if (kind == Raid)
			{
				int num8 = 0;
				for (int l = 0; l < list.Count; l++)
				{
					Pawn pawn2 = list[l];
					if (pawn2.equipment != null && pawn2.equipment.Primary == null && !pawn2.WorkTagIsDisabled(WorkTags.Violent) && (!pawn2.IsSlave || ResearchedLoadoutMod.Settings.armSlaves))
					{
						num8++;
					}
				}
				if (num8 > 0)
				{
					List<Thing> list6 = map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon);
					int num9 = 0;
					for (int m = 0; m < list6.Count; m++)
					{
						Thing thing = list6[m];
						if (thing != null && thing.Spawned && LoadoutFilter.IsManagedWeapon(thing.def) && !thing.IsForbidden(Faction.OfPlayer) && (LoadoutFilter.IsAllowed(thing.def) || (ExemptKinds(map, thing.def) & Raid) != 0))
						{
							num9++;
						}
					}
					num += Unforbid(map, list6, kind, true, true, num8 - num9, list3, list4);
				}
			}
			if (num > 0)
			{
				list5.Insert(0, "RL_EmProv_Unforbade".Translate(num, string.Join(", ", list4.ToArray())).ToString());
				RL.Debug("emergency: unforbade " + num + " (" + string.Join(", ", list4.ToArray()) + ")");
			}
			if (list5.Count > 0 && (state.provisioned & kind) == 0)
			{
				state.provisioned |= kind;
				Messages.Message("RL_EmProv_Msg".Translate(KindsLabel(kind), GearLabel(kind), string.Join("; ", list5.ToArray())).ToString(), MessageTypeDefOf.NeutralEvent, false);
			}
		}

		// removes the mod's emergency bills whose hazard is over; a bill already being worked on is reduced to 0 and removed later
		private static void CleanupBills(Map map)
		{
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			if (researchedLoadoutGameComponent == null || researchedLoadoutGameComponent.emergencyBills.Count == 0 || map.listerBuildings == null)
			{
				return;
			}
			string text = "|" + map.uniqueID;
			int num = ActiveMask(map);
			HashSet<string> hashSet = new HashSet<string>();
			List<Building> allBuildingsColonist = map.listerBuildings.allBuildingsColonist;
			for (int i = 0; i < allBuildingsColonist.Count; i++)
			{
				Building_WorkTable building_WorkTable = allBuildingsColonist[i] as Building_WorkTable;
				if (building_WorkTable == null || building_WorkTable.billStack == null)
				{
					continue;
				}
				List<Bill> bills = building_WorkTable.billStack.Bills;
				for (int num2 = bills.Count - 1; num2 >= 0; num2--)
				{
					Bill bill = bills[num2];
					if (bill == null)
					{
						continue;
					}
					string uniqueLoadID = bill.GetUniqueLoadID();
					string text2 = null;
					int num3 = 0;
					foreach (string emergencyBill in researchedLoadoutGameComponent.emergencyBills)
					{
						if (emergencyBill.StartsWith(uniqueLoadID + "|") && emergencyBill.EndsWith(text))
						{
							text2 = emergencyBill;
							string[] array = emergencyBill.Split('|');
							if (array.Length >= 3)
							{
								int.TryParse(array[1], out num3);
							}
							break;
						}
					}
					if (text2 == null)
					{
						continue;
					}
					hashSet.Add(text2);
					if ((num & num3) != 0)
					{
						continue;
					}
					if (BillStarted(bill, map))
					{
						Bill_Production bill_Production = bill as Bill_Production;
						if (bill_Production != null && bill_Production.repeatMode == BillRepeatModeDefOf.RepeatCount)
						{
							bill_Production.repeatCount = 0;
						}
					}
					else
					{
						building_WorkTable.billStack.Delete(bill);
						researchedLoadoutGameComponent.emergencyBills.Remove(text2);
						RL.Debug("emergency bill removed: " + uniqueLoadID);
					}
				}
			}
			// forget entries of this map whose bill no longer exists (done or deleted by the player)
			List<string> list = new List<string>();
			foreach (string emergencyBill2 in researchedLoadoutGameComponent.emergencyBills)
			{
				if (emergencyBill2.EndsWith(text) && !hashSet.Contains(emergencyBill2))
				{
					list.Add(emergencyBill2);
				}
			}
			for (int j = 0; j < list.Count; j++)
			{
				researchedLoadoutGameComponent.emergencyBills.Remove(list[j]);
			}
		}

		private static bool BillStarted(Bill bill, Map map)
		{
			Bill_ProductionWithUft bill_ProductionWithUft = bill as Bill_ProductionWithUft;
			if (bill_ProductionWithUft != null && bill_ProductionWithUft.BoundUft != null)
			{
				return true;
			}
			List<Pawn> freeColonistsSpawned = map.mapPawns.FreeColonistsSpawned;
			for (int i = 0; i < freeColonistsSpawned.Count; i++)
			{
				Job curJob = freeColonistsSpawned[i].CurJob;
				if (curJob != null && curJob.bill == bill)
				{
					return true;
				}
			}
			return false;
		}

		private static void ProvideAll(Map map, MapState state, List<Pawn> colonists, int kinds)
		{
			if (!ProvideEnabled)
			{
				return;
			}
			for (int i = 0; i < 4; i++)
			{
				int num = KindOfBit(i);
				if ((kinds & num) == 0)
				{
					continue;
				}
				try
				{
					Provide(map, state, num, colonists);
				}
				catch (Exception ex)
				{
					RL.Debug("emergency provide failed: " + ex.Message);
				}
			}
		}

		// ---------- hazard detection ----------

		public static void Tick()
		{
			if (Find.Maps == null)
			{
				return;
			}
			if (EnabledMask() == 0)
			{
				return;
			}
			if (states.Count > Find.Maps.Count)
			{
				scratchIds.Clear();
				foreach (int key in states.Keys)
				{
					bool flag = false;
					for (int i = 0; i < Find.Maps.Count; i++)
					{
						if (Find.Maps[i] != null && Find.Maps[i].uniqueID == key)
						{
							flag = true;
							break;
						}
					}
					if (!flag)
					{
						scratchIds.Add(key);
					}
				}
				for (int j = 0; j < scratchIds.Count; j++)
				{
					states.Remove(scratchIds[j]);
				}
			}
			if (lastToxic.Count > 2000)
			{
				lastToxic.Clear();
			}
			if (exposedAt.Count > 2000)
			{
				exposedAt.Clear();
			}
			globalChecks++;
			bool flag2 = globalChecks % ProvideEveryChecks == 0;
			for (int k = 0; k < Find.Maps.Count; k++)
			{
				Map map = Find.Maps[k];
				if (map == null)
				{
					continue;
				}
				try
				{
					Refresh(map);
					if (flag2)
					{
						CleanupBills(map);
					}
				}
				catch (Exception ex)
				{
					RL.Debug("emergency check failed: " + ex.Message);
				}
			}
		}

		public static int CheckIntervalTicks
		{
			get
			{
				return CheckInterval;
			}
		}

		private static void Refresh(Map map)
		{
			List<Pawn> freeColonistsSpawned = map.mapPawns.FreeColonistsSpawned;
			MapState value;
			states.TryGetValue(map.uniqueID, out value);
			if (freeColonistsSpawned == null || freeColonistsSpawned.Count == 0)
			{
				if (value != null && value.active == 0)
				{
					states.Remove(map.uniqueID);
				}
				return;
			}
			int num = Detect(map, freeColonistsSpawned) & EnabledMask();
			if (value == null)
			{
				if (num == 0)
				{
					return;
				}
				value = new MapState();
				states[map.uniqueID] = value;
			}
			int ticksGame = Find.TickManager.TicksGame;
			int num2 = 0;
			int num3 = 0;
			for (int i = 0; i < 4; i++)
			{
				int num4 = KindOfBit(i);
				bool flag = (num & num4) != 0;
				bool flag2 = (value.active & num4) != 0;
				if (flag)
				{
					value.misses[i] = 0;
					if (!flag2)
					{
						value.active |= num4;
						num2 |= num4;
					}
				}
				else if (flag2)
				{
					value.misses[i]++;
					if (value.misses[i] >= EndConfirmChecks)
					{
						value.active &= ~num4;
						value.misses[i] = 0;
						value.endedAt[i] = ticksGame;
						num3 |= num4;
					}
				}
			}
			if (num2 != 0)
			{
				OnStarted(map, value, num2, freeColonistsSpawned);
			}
			if (num3 != 0)
			{
				OnEnded(map, value, num3);
			}
			// 2.8.1: find / craft / substitute when colonists lack protection
			value.checks++;
			int num5 = value.active & EnabledMask();
			if (num2 != 0)
			{
				ProvideAll(map, value, freeColonistsSpawned, num2 & EnabledMask());
			}
			else if (num5 != 0 && value.checks % ProvideEveryChecks == 0)
			{
				ProvideAll(map, value, freeColonistsSpawned, num5);
			}
		}

		private static void OnStarted(Map map, MapState state, int kinds, List<Pawn> colonists)
		{
			RL.Debug("emergency started on map " + map.uniqueID + ": " + KindsLabel(kinds));
			// let vanilla dressing (and our own pass) re-evaluate right away
			int ticksGame = Find.TickManager.TicksGame;
			for (int i = 0; i < colonists.Count; i++)
			{
				Pawn pawn = colonists[i];
				if (pawn != null && pawn.mindState != null && pawn.mindState.nextApparelOptimizeTick > ticksGame)
				{
					pawn.mindState.nextApparelOptimizeTick = ticksGame;
				}
			}
			ResearchedLoadoutMapComponent.ResearchVersion++;
			// announce only when there really is locked gear on the map that is now allowed
			for (int j = 0; j < 4; j++)
			{
				int num = KindOfBit(j);
				if ((kinds & num) != 0 && (state.announced & num) == 0 && LockedCounterExists(map, num))
				{
					state.announced |= num;
					Messages.Message("RL_EmergencyOn".Translate(KindsLabel(num), GearLabel(num)).ToString(), MessageTypeDefOf.NeutralEvent, false);
				}
			}
		}

		private static void OnEnded(Map map, MapState state, int kinds)
		{
			RL.Debug("emergency ended on map " + map.uniqueID + ": " + KindsLabel(kinds));
			for (int i = 0; i < 4; i++)
			{
				int num = KindOfBit(i);
				if ((kinds & num) != 0 && (state.announced & num) != 0)
				{
					state.announced &= ~num;
					Messages.Message("RL_EmergencyOff".Translate(KindsLabel(num)).ToString(), MessageTypeDefOf.SituationResolved, false);
				}
			}
			state.provisioned &= ~kinds;
			CleanupBills(map);
		}

		private static bool LockedCounterExists(Map map, int kind)
		{
			if (map.listerThings == null)
			{
				return false;
			}
			if (LockedCounterIn(map, map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel), kind))
			{
				return true;
			}
			return kind == Raid && LockedCounterIn(map, map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon), kind);
		}

		private static bool LockedCounterIn(Map map, List<Thing> things, int kind)
		{
			if (things == null)
			{
				return false;
			}
			for (int i = 0; i < things.Count; i++)
			{
				Thing thing = things[i];
				if (thing != null && thing.def != null && !thing.IsForbidden(Faction.OfPlayer) && (CounterMask(thing.def) & kind) != 0 && !LoadoutFilter.IsAllowed(thing.def) && !LoadoutFilter.IsManuallyBanned(thing.def) && (thing.def.IsApparel || IsColonyOwned(thing, map)))
				{
					return true;
				}
			}
			return false;
		}

		private static int Detect(Map map, List<Pawn> colonists)
		{
			int num = 0;
			int num2 = EnabledMask();
			if ((num2 & Toxic) != 0 && ToxicNow(map, colonists))
			{
				num |= Toxic;
			}
			if ((num2 & (Cold | Heat)) != 0)
			{
				num |= TemperatureNow(map, colonists);
			}
			if ((num2 & Raid) != 0 && RaidNow(map))
			{
				num |= Raid;
			}
			return num;
		}

		private static bool Has(string s, string[] parts)
		{
			for (int i = 0; i < parts.Length; i++)
			{
				if (s.IndexOf(parts[i], StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		private static bool ToxicNow(Map map, List<Pawn> colonists)
		{
			int now = Find.TickManager.TicksGame;
			bool wide = false;
			// 1) game conditions: toxic fallout, noxious haze, or anything toxic/gas/smoke-like from mods (whole map)
			if (map.gameConditionManager != null)
			{
				List<GameCondition> activeConditions = map.gameConditionManager.ActiveConditions;
				for (int i = 0; i < activeConditions.Count && !wide; i++)
				{
					GameCondition gameCondition = activeConditions[i];
					if (gameCondition != null && gameCondition.def != null)
					{
						if (gameCondition.def == GameConditionDefOf.ToxicFallout || (GameConditionDefOf.NoxiousHaze != null && gameCondition.def == GameConditionDefOf.NoxiousHaze))
						{
							wide = true;
						}
						else if (Has(gameCondition.def.defName + gameCondition.GetType().Name, ConditionKeywords))
						{
							wide = true;
						}
					}
				}
			}
			bool flag = map.gasGrid != null && map.gasGrid.CalculateGasEffects;
			bool flag2 = ModsConfig.BiotechActive && map.pollutionGrid != null;
			bool any = false;
			for (int j = 0; j < colonists.Count; j++)
			{
				Pawn pawn = colonists[j];
				if (pawn == null || !pawn.Spawned)
				{
					continue;
				}
				IntVec3 position = pawn.Position;
				bool exposed = false;
				// 2) toxic buildup rising on this colonist = some toxic source, whatever mod it comes from
				if (pawn.health != null && HediffDefOf.ToxicBuildup != null)
				{
					Hediff firstHediffOfDef = pawn.health.hediffSet.GetFirstHediffOfDef(HediffDefOf.ToxicBuildup);
					float num2 = ((firstHediffOfDef == null) ? 0f : firstHediffOfDef.Severity);
					float value;
					bool flag3 = lastToxic.TryGetValue(pawn.thingIDNumber, out value);
					lastToxic[pawn.thingIDNumber] = num2;
					if (flag3 && num2 > value + 0.003f)
					{
						exposed = true;
					}
				}
				// 3) toxic gas at or near the colonist (2.14.0: rot stink no longer counts – it smells, it does not poison)
				if (!exposed && flag && GasNear(map, position))
				{
					exposed = true;
				}
				// 4) mod gases spawned as things
				if (!exposed && GasThingAt(map, position))
				{
					exposed = true;
				}
				// 5) out in a polluted area (Biotech) – 2.14.0: an area, not one polluted cell
				if (!exposed && flag2 && PollutedAround(map, position))
				{
					exposed = true;
				}
				if (exposed)
				{
					exposedAt[pawn.thingIDNumber] = now;
					any = true;
				}
			}
			// 6) toxic gas spread over the home area (coarse sample, e.g. a gas generator next to the base)
			if (!wide && flag && GasInHome(map))
			{
				wide = true;
			}
			if (wide)
			{
				wideAt[map.uniqueID] = now;
			}
			return wide || any;
		}

		private static bool PollutedAround(Map map, IntVec3 c)
		{
			try
			{
				if (!c.InBounds(map) || c.Roofed(map) || !map.pollutionGrid.IsPolluted(c))
				{
					return false;
				}
				int all = 0;
				int bad = 0;
				for (int i = -4; i <= 4; i += 2)
				{
					for (int j = -4; j <= 4; j += 2)
					{
						IntVec3 v = new IntVec3(c.x + i, 0, c.z + j);
						if (!v.InBounds(map))
						{
							continue;
						}
						all++;
						if (map.pollutionGrid.IsPolluted(v) && !v.Roofed(map))
						{
							bad++;
						}
					}
				}
				return all > 0 && bad * 10 >= all * 4;
			}
			catch
			{
				return false;
			}
		}

		private static bool GasNear(Map map, IntVec3 c)
		{
			for (int i = -1; i <= 1; i++)
			{
				for (int j = -1; j <= 1; j++)
				{
					IntVec3 intVec = new IntVec3(c.x + i * 3, 0, c.z + j * 3);
					if (intVec.InBounds(map) && map.gasGrid.DensityAt(intVec, GasType.ToxGas) > 0)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool GasThingAt(Map map, IntVec3 c)
		{
			List<Thing> list = map.thingGrid.ThingsListAtFast(c);
			for (int i = 0; i < list.Count; i++)
			{
				Thing thing = list[i];
				if (thing != null && thing.def != null && thing.def.category == ThingCategory.Gas && Has(thing.def.defName, GasThingKeywords))
				{
					return true;
				}
			}
			return false;
		}

		private static bool GasInHome(Map map)
		{
			Area home = map.areaManager.Home;
			if (home == null || home.TrueCount == 0)
			{
				return false;
			}
			IntVec3 size = map.Size;
			int num = 0;
			for (int i = 1; i < size.x; i += 3)
			{
				for (int j = 1; j < size.z; j += 3)
				{
					IntVec3 intVec = new IntVec3(i, 0, j);
					if (home[intVec] && map.gasGrid.DensityAt(intVec, GasType.ToxGas) > 0)
					{
						num++;
						if (num >= 3)
						{
							return true;
						}
					}
				}
			}
			return false;
		}

		private static int TemperatureNow(Map map, List<Pawn> colonists)
		{
			int num = 0;
			float num2 = 999f;
			float num3 = -999f;
			for (int i = 0; i < colonists.Count; i++)
			{
				Pawn pawn = colonists[i];
				if (pawn == null || pawn.def == null)
				{
					continue;
				}
				if (pawn.health != null)
				{
					if (pawn.health.hediffSet.HasHediff(HediffDefOf.Hypothermia))
					{
						num |= Cold;
					}
					if (pawn.health.hediffSet.HasHediff(HediffDefOf.Heatstroke))
					{
						num |= Heat;
					}
				}
				// the race's own comfort range, without apparel (otherwise the warm clothes would end the emergency)
				num2 = Mathf.Min(num2, pawn.def.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin));
				num3 = Mathf.Max(num3, pawn.def.GetStatValueAbstract(StatDefOf.ComfyTemperatureMax));
			}
			if (num2 > 900f)
			{
				return num;
			}
			bool flag = false;
			bool flag2 = false;
			if (map.gameConditionManager != null)
			{
				flag = map.gameConditionManager.ConditionIsActive(GameConditionDefOf.ColdSnap) || map.gameConditionManager.ConditionIsActive(GameConditionDefOf.VolcanicWinter);
				flag2 = map.gameConditionManager.ConditionIsActive(GameConditionDefOf.HeatWave);
			}
			float outdoorTemp = map.mapTemperature.OutdoorTemp;
			if (outdoorTemp < num2 - ((!flag) ? 16f : 6f))
			{
				num |= Cold;
			}
			if (outdoorTemp > num3 + ((!flag2) ? 14f : 6f))
			{
				num |= Heat;
			}
			return num;
		}

		private static bool RaidNow(Map map)
		{
			try
			{
				return GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
			}
			catch
			{
				return false;
			}
		}
	}
	public static class Specialization
	{
		public enum CombatRole
		{
			Shooter,
			Melee,
			Balanced,
			Worker
		}

		private sealed class Info
		{
			public int tick;

			public CombatRole role;

			public SkillDef work;
		}

		private static readonly Dictionary<int, Info> cache = new Dictionary<int, Info>();

		public static void ResetGameState()
		{
			cache.Clear();
		}

		private static Dictionary<StatDef, float> general;

		private static Dictionary<StatDef, float> shooter;

		private static Dictionary<StatDef, float> melee;

		private static Dictionary<StatDef, float> worker;

		private static Dictionary<string, Dictionary<StatDef, float>> bySkill;

		private static readonly Dictionary<ThingDef, SkillDef> workSkillCache = new Dictionary<ThingDef, SkillDef>();

		private static int Level(Pawn p, SkillDef sk)
		{
			SkillRecord skillRecord = ((p.skills == null || sk == null) ? null : p.skills.GetSkill(sk));
			if (skillRecord == null || skillRecord.TotallyDisabled)
			{
				return 0;
			}
			return skillRecord.Level + ((skillRecord.passion == Passion.Major) ? 3 : ((skillRecord.passion == Passion.Minor) ? 1 : 0));
		}

		private static Info Get(Pawn p)
		{
			int num = ((Find.TickManager != null) ? Find.TickManager.TicksGame : 0);
			Info value;
			if (cache.TryGetValue(p.thingIDNumber, out value) && num - value.tick < 2500 && num >= value.tick)
			{
				return value;
			}
			Info info = new Info();
			info.tick = num;
			value = info;
			int num2 = Level(p, SkillDefOf.Shooting);
			int num3 = Level(p, SkillDefOf.Melee);
			bool flag = p.story != null && p.story.traits != null && p.story.traits.HasTrait(TraitDefOf.Brawler);
			if (p.WorkTagIsDisabled(WorkTags.Violent))
			{
				value.role = CombatRole.Worker;
			}
			else if (flag)
			{
				value.role = CombatRole.Melee;
			}
			else if (Mathf.Max(num2, num3) < 5)
			{
				value.role = CombatRole.Worker;
			}
			else if (num2 >= num3 + 3)
			{
				value.role = CombatRole.Shooter;
			}
			else if (num3 >= num2 + 3)
			{
				value.role = CombatRole.Melee;
			}
			else
			{
				value.role = CombatRole.Balanced;
			}
			int num4 = 5;
			if (p.skills != null)
			{
				foreach (SkillRecord skill in p.skills.skills)
				{
					if (skill.def != SkillDefOf.Shooting && skill.def != SkillDefOf.Melee)
					{
						int num5 = Level(p, skill.def);
						if (num5 > num4)
						{
							num4 = num5;
							value.work = skill.def;
						}
					}
				}
			}
			cache[p.thingIDNumber] = value;
			return value;
		}

		public static CombatRole RoleOf(Pawn p)
		{
			return Get(p).role;
		}

		public static string Describe(Pawn p)
		{
			Info info = Get(p);
			string text = ("RL_Role_" + info.role).Translate();
			string text2 = ((info.work == null) ? "-" : info.work.LabelCap.ToString());
			return "RL_GizmoRole".Translate(text, text2);
		}

		private static Dictionary<StatDef, float> Map(params object[] pairs)
		{
			Dictionary<StatDef, float> dictionary = new Dictionary<StatDef, float>();
			for (int i = 0; i + 1 < pairs.Length; i += 2)
			{
				StatDef namedSilentFail = DefDatabase<StatDef>.GetNamedSilentFail((string)pairs[i]);
				if (namedSilentFail != null)
				{
					dictionary[namedSilentFail] = Convert.ToSingle(pairs[i + 1]);
				}
			}
			return dictionary;
		}

		private static void Build()
		{
			if (general == null)
			{
				general = Map("MoveSpeed", 2f, "WorkSpeedGlobal", 2f, "MentalBreakThreshold", -3f, "GlobalLearningFactor", 0.5f, "ImmunityGainSpeed", 1f, "CarryingCapacity", 0.004f);
				shooter = Map("ShootingAccuracyPawn", 4f, "AimingDelayFactor", -2f, "MoveSpeed", 1f);
				melee = Map("MeleeHitChance", 4f, "MeleeDodgeChance", 4f, "MeleeDamageFactor", 2f, "MeleeCooldownFactor", -2f);
				worker = Map("MoveSpeed", 1.5f, "WorkSpeedGlobal", 1f);
				Dictionary<string, Dictionary<StatDef, float>> dictionary = new Dictionary<string, Dictionary<StatDef, float>>();
				dictionary.Add("Medicine", Map("MedicalTendSpeed", 2f, "MedicalTendQuality", 3f, "MedicalSurgerySuccessChance", 3f));
				dictionary.Add("Construction", Map("ConstructionSpeed", 2f, "ConstructSuccessChance", 2f));
				dictionary.Add("Plants", Map("PlantWorkSpeed", 2f, "PlantHarvestYield", 2f));
				dictionary.Add("Mining", Map("MiningSpeed", 2f, "MiningYield", 2f));
				dictionary.Add("Cooking", Map("CookSpeed", 2f, "FoodPoisonChance", -5f, "ButcheryFleshSpeed", 1f));
				dictionary.Add("Crafting", Map("GeneralLaborSpeed", 2f));
				dictionary.Add("Artistic", Map("GeneralLaborSpeed", 1f));
				dictionary.Add("Intellectual", Map("ResearchSpeed", 2f));
				dictionary.Add("Social", Map("SocialImpact", 2f, "NegotiationAbility", 2f, "TradePriceImprovement", 3f));
				dictionary.Add("Animals", Map("TameAnimalChance", 2f, "TrainAnimalChance", 2f, "AnimalGatherYield", 1f));
				bySkill = dictionary;
			}
		}

		private static float Sum(List<StatModifier> offsets, Dictionary<StatDef, float> weights)
		{
			float num = 0f;
			if (offsets == null || weights == null)
			{
				return 0f;
			}
			for (int i = 0; i < offsets.Count; i++)
			{
				float value;
				if (offsets[i] != null && offsets[i].stat != null && weights.TryGetValue(offsets[i].stat, out value))
				{
					num += offsets[i].value * value;
				}
			}
			return num;
		}

		public static float OffsetBonus(Pawn p, ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.statOffsets || p == null || def == null || def.equippedStatOffsets == null)
			{
				return 0f;
			}
			Build();
			List<StatModifier> equippedStatOffsets = def.equippedStatOffsets;
			float num = Sum(equippedStatOffsets, general);
			if (settings.roleGear)
			{
				Info info = Get(p);
				switch (info.role)
				{
				case CombatRole.Shooter:
					num += Sum(equippedStatOffsets, shooter);
					break;
				case CombatRole.Melee:
					num += Sum(equippedStatOffsets, melee);
					break;
				case CombatRole.Balanced:
					num += 0.5f * (Sum(equippedStatOffsets, shooter) + Sum(equippedStatOffsets, melee));
					break;
				case CombatRole.Worker:
					num += Sum(equippedStatOffsets, worker);
					break;
				}
				Dictionary<StatDef, float> value;
				if (info.work != null && bySkill.TryGetValue(info.work.defName, out value))
				{
					num += Sum(equippedStatOffsets, value);
				}
			}
			return num;
		}

		public static SkillDef WorkSkillOf(ThingDef def)
		{
			if (def == null || !def.IsApparel || def.equippedStatOffsets == null)
			{
				return null;
			}
			SkillDef value;
			if (workSkillCache.TryGetValue(def, out value))
			{
				return value;
			}
			Build();
			SkillDef skillDef = null;
			float num = 0.02f;
			foreach (KeyValuePair<string, Dictionary<StatDef, float>> item in bySkill)
			{
				float num2 = Sum(def.equippedStatOffsets, item.Value);
				if (num2 > num)
				{
					SkillDef namedSilentFail = DefDatabase<SkillDef>.GetNamedSilentFail(item.Key);
					if (namedSilentFail != null)
					{
						num = num2;
						skillDef = namedSilentFail;
					}
				}
			}
			workSkillCache[def] = skillDef;
			return skillDef;
		}

		public static bool IsSpecialist(Pawn p, SkillDef sk)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (p == null || sk == null || p.skills == null || settings == null)
			{
				return false;
			}
			SkillRecord skill = p.skills.GetSkill(sk);
			if (skill == null || skill.TotallyDisabled || skill.Level < settings.workClothingMinSkill)
			{
				return false;
			}
			if (Get(p).work == sk)
			{
				return true;
			}
			if (p.workSettings == null || !p.workSettings.EverWork || Find.PlaySettings == null || !Find.PlaySettings.useWorkPriorities)
			{
				return false;
			}
			List<WorkTypeDef> allDefsListForReading = DefDatabase<WorkTypeDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				WorkTypeDef workTypeDef = allDefsListForReading[i];
				if (workTypeDef.relevantSkills != null && workTypeDef.relevantSkills.Contains(sk) && !p.WorkTypeIsDisabled(workTypeDef) && p.workSettings.GetPriority(workTypeDef) == 1)
				{
					return true;
				}
			}
			return false;
		}

		public static bool WorkClothingBlocked(Pawn p, ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.workClothing || p == null)
			{
				return false;
			}
			SkillDef skillDef = WorkSkillOf(def);
			return skillDef != null && !IsSpecialist(p, skillDef);
		}

		public static float WorkClothingBonus(Pawn p, ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.workClothing || p == null)
			{
				return 0f;
			}
			SkillDef skillDef = WorkSkillOf(def);
			if (skillDef == null || !IsSpecialist(p, skillDef))
			{
				return 0f;
			}
			Build();
			Dictionary<StatDef, float> value;
			float num = ((!bySkill.TryGetValue(skillDef.defName, out value)) ? 0f : Sum(def.equippedStatOffsets, value));
			return 0.8f + Mathf.Clamp(num * 3f, 0f, 2f);
		}

		public static float ApparelBonus(Pawn p, Apparel ap)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || p == null || ap == null)
			{
				return 0f;
			}
			float num = OffsetBonus(p, ap.def) + WorkClothingBonus(p, ap.def);
			if (settings.roleGear)
			{
				float num2 = ap.GetStatValue(StatDefOf.ArmorRating_Sharp) + 0.5f * ap.GetStatValue(StatDefOf.ArmorRating_Blunt);
				switch (RoleOf(p))
				{
				case CombatRole.Melee:
					num += 0.4f * num2;
					break;
				case CombatRole.Balanced:
					num += 0.1f * num2;
					break;
				case CombatRole.Worker:
					num -= 0.3f * num2;
					break;
				}
			}
			return num;
		}

		public static float WeaponFactor(Pawn p, Thing weapon)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.roleGear || p == null || weapon == null)
			{
				return 1f;
			}
			float num = 1f + Mathf.Clamp(OffsetBonus(p, weapon.def), -0.8f, 0.8f);
			if (weapon.def.IsRangedWeapon && weapon.def.Verbs != null && weapon.def.Verbs.Count > 0)
			{
				VerbProperties verbProperties = weapon.def.Verbs[0];
				int num2 = Level(p, SkillDefOf.Shooting);
				num *= Mathf.Clamp(1f + (float)(num2 - 8) / 40f * (verbProperties.range - 25f) / 25f, 0.85f, 1.2f);
				if (RoleOf(p) == CombatRole.Worker && verbProperties.warmupTime > 2.5f)
				{
					num *= 0.8f;
				}
			}
			return Mathf.Max(0.1f, num);
		}

		public static float SpecialEffectFactor(ProjectileProperties proj)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.specialEffects || proj == null)
			{
				return 1f;
			}
			float num = 1f;
			if (proj.explosionRadius > 0.5f)
			{
				num *= 0.75f;
			}
			string text = ((proj.damageDef == null) ? string.Empty : (proj.damageDef.defName ?? string.Empty));
			StringComparison comparisonType = StringComparison.OrdinalIgnoreCase;
			if (text.IndexOf("Flame", comparisonType) >= 0 || text.IndexOf("Burn", comparisonType) >= 0)
			{
				num *= 0.6f;
			}
			else if (text.IndexOf("EMP", comparisonType) >= 0)
			{
				num *= 0.5f;
			}
			else if (text.IndexOf("Stun", comparisonType) >= 0)
			{
				num *= 0.7f;
			}
			else if (text.IndexOf("Smoke", comparisonType) >= 0 || text.IndexOf("Extinguish", comparisonType) >= 0)
			{
				num *= 0.15f;
			}
			else if (text.IndexOf("Tox", comparisonType) >= 0)
			{
				num *= 0.7f;
			}
			return num;
		}
	}
	// 2.7.0: "Dress by traits and skills" – a small, bounded multiplier on the apparel score.
	// Only reorders apparel that already passed every filter (research, bans, gender, HP…); never unlocks anything.
	public static class WearerFit
	{
		public const float MinFactor = 0.75f;

		public const float MaxFactor = 1.35f;

		private const int ProfileTicks = 2500;

		private const int FitTicks = 2500;

		private const byte KindNone = 0;

		private const byte KindSkill = 1;

		private const byte KindPsychic = 2;

		private const byte KindMove = 3;

		private const byte KindWorkGlobal = 4;

		private sealed class StatInfo
		{
			public byte kind;

			public float sign = 1f;

			public float scale = 1f;

			public SkillDef[] skills;

			public bool shooting;

			public bool melee;

			public bool social;
		}

		private sealed class Profile
		{
			public int tick;

			public float[] skillWeight;

			public float moveWeight;

			public float psyWeight;

			public float armorPref;

			public int qualityPref;

			public bool brawler;

			public float socialBonus;

			public bool anyLiveFactor;
		}

		private struct FitEntry
		{
			public int tick;

			public float value;
		}

		private static readonly Dictionary<int, Profile> profiles = new Dictionary<int, Profile>();

		private static readonly Dictionary<long, FitEntry> fits = new Dictionary<long, FitEntry>();

		private static readonly Dictionary<StatDef, StatInfo> statInfos = new Dictionary<StatDef, StatInfo>();

		private static bool traitsResolved;

		private static TraitDef tBrawler;

		private static TraitDef tTough;

		private static TraitDef tWimp;

		private static TraitDef tNimble;

		private static TraitDef tSpeed;

		private static TraitDef tGreedy;

		private static TraitDef tJealous;

		private static TraitDef tAscetic;

		private static TraitDef tPsySens;

		private static TraitDef tBeauty;

		private static TraitDef tKind;

		private static Dictionary<string, string[]> extraSkills;

		private static Dictionary<string, byte> fixedKinds;

		private static HashSet<string> lowerIsBetter;

		public static void ResetGameState()
		{
			profiles.Clear();
			fits.Clear();
		}

		private static int Now()
		{
			return (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
		}

		private static void ResolveTraits()
		{
			if (traitsResolved)
			{
				return;
			}
			traitsResolved = true;
			tBrawler = DefDatabase<TraitDef>.GetNamedSilentFail("Brawler");
			tTough = DefDatabase<TraitDef>.GetNamedSilentFail("Tough");
			tWimp = DefDatabase<TraitDef>.GetNamedSilentFail("Wimp");
			tNimble = DefDatabase<TraitDef>.GetNamedSilentFail("Nimble");
			tSpeed = DefDatabase<TraitDef>.GetNamedSilentFail("SpeedOffset");
			tGreedy = DefDatabase<TraitDef>.GetNamedSilentFail("Greedy");
			tJealous = DefDatabase<TraitDef>.GetNamedSilentFail("Jealous");
			tAscetic = DefDatabase<TraitDef>.GetNamedSilentFail("Ascetic");
			tPsySens = DefDatabase<TraitDef>.GetNamedSilentFail("PsychicSensitivity");
			tBeauty = DefDatabase<TraitDef>.GetNamedSilentFail("Beauty");
			tKind = DefDatabase<TraitDef>.GetNamedSilentFail("Kind");
		}

		private static void BuildMaps()
		{
			if (extraSkills != null)
			{
				return;
			}
			Dictionary<string, string[]> d = new Dictionary<string, string[]>();
			d["GeneralLaborSpeed"] = new string[2] { "Crafting", "Artistic" };
			d["SmeltingSpeed"] = new string[1] { "Crafting" };
			d["SmithingSpeed"] = new string[1] { "Crafting" };
			d["TailoringSpeed"] = new string[1] { "Crafting" };
			d["MedicalTendQuality"] = new string[1] { "Medicine" };
			d["MedicalTendSpeed"] = new string[1] { "Medicine" };
			d["MedicalSurgerySuccessChance"] = new string[1] { "Medicine" };
			d["MedicalOperationSpeed"] = new string[1] { "Medicine" };
			d["ResearchSpeed"] = new string[1] { "Intellectual" };
			d["PlantWorkSpeed"] = new string[1] { "Plants" };
			d["PlantHarvestYield"] = new string[1] { "Plants" };
			d["MiningSpeed"] = new string[1] { "Mining" };
			d["MiningYield"] = new string[1] { "Mining" };
			d["DeepDrillingSpeed"] = new string[1] { "Mining" };
			d["ConstructionSpeed"] = new string[1] { "Construction" };
			d["ConstructSuccessChance"] = new string[1] { "Construction" };
			d["FixBrokenDownBuildingSuccessChance"] = new string[1] { "Construction" };
			d["SmoothingSpeed"] = new string[1] { "Construction" };
			d["CookSpeed"] = new string[1] { "Cooking" };
			d["FoodPoisonChance"] = new string[1] { "Cooking" };
			d["ButcheryFleshSpeed"] = new string[1] { "Cooking" };
			d["ButcheryFleshEfficiency"] = new string[1] { "Cooking" };
			d["ShootingAccuracyPawn"] = new string[1] { "Shooting" };
			d["AimingDelayFactor"] = new string[1] { "Shooting" };
			d["HuntingStealth"] = new string[1] { "Shooting" };
			d["MeleeHitChance"] = new string[1] { "Melee" };
			d["MeleeDodgeChance"] = new string[1] { "Melee" };
			d["MeleeDamageFactor"] = new string[1] { "Melee" };
			d["MeleeCooldownFactor"] = new string[1] { "Melee" };
			d["SocialImpact"] = new string[1] { "Social" };
			d["NegotiationAbility"] = new string[1] { "Social" };
			d["TradePriceImprovement"] = new string[1] { "Social" };
			d["ArrestSuccessChance"] = new string[1] { "Social" };
			d["AnimalGatherYield"] = new string[1] { "Animals" };
			d["AnimalGatherSpeed"] = new string[1] { "Animals" };
			d["TameAnimalChance"] = new string[1] { "Animals" };
			d["TrainAnimalChance"] = new string[1] { "Animals" };
			extraSkills = d;
			Dictionary<string, byte> k = new Dictionary<string, byte>();
			k["PsychicSensitivity"] = KindPsychic;
			k["PsychicEntropyMax"] = KindPsychic;
			k["PsychicEntropyRecoveryRate"] = KindPsychic;
			k["PsychicEntropyGain"] = KindPsychic;
			k["MeditationFocusGain"] = KindPsychic;
			k["MeditationFocusStrength"] = KindPsychic;
			k["PsyfocusCostFactor"] = KindPsychic;
			k["MoveSpeed"] = KindMove;
			k["WorkSpeedGlobal"] = KindWorkGlobal;
			fixedKinds = k;
			HashSet<string> h = new HashSet<string>();
			h.Add("AimingDelayFactor");
			h.Add("MeleeCooldownFactor");
			h.Add("FoodPoisonChance");
			h.Add("PsychicEntropyGain");
			h.Add("PsyfocusCostFactor");
			h.Add("MentalBreakThreshold");
			lowerIsBetter = h;
		}

		private static void AddSkill(List<SkillDef> list, SkillDef sk)
		{
			if (sk != null && !list.Contains(sk))
			{
				list.Add(sk);
			}
		}

		private static StatInfo InfoOf(StatDef stat)
		{
			StatInfo info;
			if (statInfos.TryGetValue(stat, out info))
			{
				return info;
			}
			BuildMaps();
			info = new StatInfo();
			string name = stat.defName ?? string.Empty;
			if (lowerIsBetter.Contains(name))
			{
				info.sign = -1f;
			}
			info.scale = 1f / Mathf.Max(Mathf.Abs(stat.defaultBaseValue), 0.25f);
			byte kind;
			if (fixedKinds.TryGetValue(name, out kind))
			{
				info.kind = kind;
			}
			else
			{
				// generic: any stat (vanilla or modded) whose value depends on a skill counts for that skill
				List<SkillDef> list = new List<SkillDef>();
				if (stat.skillNeedOffsets != null)
				{
					for (int i = 0; i < stat.skillNeedOffsets.Count; i++)
					{
						if (stat.skillNeedOffsets[i] != null)
						{
							AddSkill(list, stat.skillNeedOffsets[i].skill);
						}
					}
				}
				if (stat.skillNeedFactors != null)
				{
					for (int j = 0; j < stat.skillNeedFactors.Count; j++)
					{
						if (stat.skillNeedFactors[j] != null)
						{
							AddSkill(list, stat.skillNeedFactors[j].skill);
						}
					}
				}
				string[] extra;
				if (extraSkills.TryGetValue(name, out extra))
				{
					for (int k = 0; k < extra.Length; k++)
					{
						AddSkill(list, DefDatabase<SkillDef>.GetNamedSilentFail(extra[k]));
					}
				}
				if (list.Count > 0)
				{
					info.kind = KindSkill;
					info.skills = list.ToArray();
					for (int m = 0; m < info.skills.Length; m++)
					{
						if (info.skills[m] == SkillDefOf.Shooting)
						{
							info.shooting = true;
						}
						else if (info.skills[m] == SkillDefOf.Melee)
						{
							info.melee = true;
						}
						else if (info.skills[m] == SkillDefOf.Social)
						{
							info.social = true;
						}
					}
				}
			}
			statInfos[stat] = info;
			return info;
		}

		private static bool SkillIsActive(Pawn p, SkillDef sk, bool violent)
		{
			if (sk == SkillDefOf.Shooting || sk == SkillDefOf.Melee)
			{
				return violent;
			}
			if (p.workSettings == null || !p.workSettings.EverWork)
			{
				return false;
			}
			List<WorkTypeDef> all = DefDatabase<WorkTypeDef>.AllDefsListForReading;
			for (int i = 0; i < all.Count; i++)
			{
				WorkTypeDef w = all[i];
				if (w.relevantSkills != null && w.relevantSkills.Contains(sk) && p.workSettings.WorkIsActive(w))
				{
					return true;
				}
			}
			return false;
		}

		private static Profile GetProfile(Pawn p)
		{
			int now = Now();
			Profile pr;
			if (profiles.TryGetValue(p.thingIDNumber, out pr) && now - pr.tick < ProfileTicks && now >= pr.tick)
			{
				return pr;
			}
			if (pr == null)
			{
				pr = new Profile();
				profiles[p.thingIDNumber] = pr;
			}
			ResolveTraits();
			pr.tick = now;
			List<SkillDef> skillDefs = DefDatabase<SkillDef>.AllDefsListForReading;
			if (pr.skillWeight == null || pr.skillWeight.Length != skillDefs.Count)
			{
				pr.skillWeight = new float[skillDefs.Count];
			}
			for (int i = 0; i < pr.skillWeight.Length; i++)
			{
				pr.skillWeight[i] = 0f;
			}
			bool violent = !p.WorkTagIsDisabled(WorkTags.Violent);
			if (p.skills != null)
			{
				List<SkillRecord> skills = p.skills.skills;
				for (int j = 0; j < skills.Count; j++)
				{
					SkillRecord r = skills[j];
					if (r == null || r.def == null || r.TotallyDisabled || r.def.index >= pr.skillWeight.Length)
					{
						continue;
					}
					// level 3 -> 0, level 15+ -> 1; passion +0.15 / +0.3; work the pawn doesn't do counts 30 %
					float w = Mathf.Clamp01((r.Level - 3f) / 12f);
					if (r.passion == Passion.Major)
					{
						w += 0.3f;
					}
					else if (r.passion == Passion.Minor)
					{
						w += 0.15f;
					}
					if (!SkillIsActive(p, r.def, violent))
					{
						w *= 0.3f;
					}
					pr.skillWeight[r.def.index] = w;
				}
			}
			TraitSet traits = (p.story == null) ? null : p.story.traits;
			pr.brawler = traits != null && tBrawler != null && traits.HasTrait(tBrawler);
			pr.moveWeight = 0.4f;
			pr.armorPref = 0f;
			pr.qualityPref = 0;
			pr.psyWeight = 0f;
			pr.socialBonus = 0f;
			if (traits != null)
			{
				if (tSpeed != null)
				{
					int sd = traits.DegreeOfTrait(tSpeed);
					if (sd >= 2)
					{
						pr.moveWeight = 1f;
					}
					else if (sd != 0)
					{
						pr.moveWeight = 0.8f;
					}
				}
				if (tNimble != null && traits.HasTrait(tNimble))
				{
					pr.moveWeight = Mathf.Max(pr.moveWeight, 0.8f);
				}
				if (tWimp != null && traits.HasTrait(tWimp))
				{
					pr.armorPref += 0.6f;
				}
				if (tTough != null && traits.HasTrait(tTough))
				{
					pr.armorPref -= 0.2f;
				}
				if (pr.brawler)
				{
					pr.armorPref += 0.3f;
				}
				if ((tGreedy != null && traits.HasTrait(tGreedy)) || (tJealous != null && traits.HasTrait(tJealous)))
				{
					pr.qualityPref = 1;
				}
				else if (tAscetic != null && traits.HasTrait(tAscetic))
				{
					pr.qualityPref = -1;
				}
				if (tPsySens != null)
				{
					int ps = traits.DegreeOfTrait(tPsySens);
					if (ps >= 2)
					{
						pr.psyWeight = 0.6f;
					}
					else if (ps == 1)
					{
						pr.psyWeight = 0.4f;
					}
					else if (ps == -1)
					{
						pr.psyWeight = -0.15f;
					}
				}
				if ((tBeauty != null && traits.DegreeOfTrait(tBeauty) > 0) || (tKind != null && traits.HasTrait(tKind)))
				{
					pr.socialBonus = 0.2f;
				}
			}
			if (ModsConfig.RoyaltyActive && p.HasPsylink)
			{
				pr.psyWeight = Mathf.Max(pr.psyWeight, Mathf.Min(1f, 0.5f + 0.1f * p.GetPsylinkLevel()));
			}
			pr.anyLiveFactor = pr.armorPref != 0f || pr.qualityPref != 0;
			return pr;
		}

		private static float StatWeight(Profile pr, StatInfo info)
		{
			switch (info.kind)
			{
			case KindMove:
				return pr.moveWeight;
			case KindWorkGlobal:
				return 0.4f;
			case KindPsychic:
				return pr.psyWeight;
			case KindSkill:
			{
				if (info.shooting && pr.brawler)
				{
					return -0.3f;
				}
				float w = 0f;
				for (int i = 0; i < info.skills.Length; i++)
				{
					int idx = info.skills[i].index;
					if (idx < pr.skillWeight.Length && pr.skillWeight[idx] > w)
					{
						w = pr.skillWeight[idx];
					}
				}
				if (info.melee && pr.brawler)
				{
					w += 0.3f;
				}
				if (info.social)
				{
					w += pr.socialBonus;
				}
				return w;
			}
			default:
				return 0f;
			}
		}

		// per pawn+def part: stat offsets of the apparel weighted by what the wearer does and is good at
		private static float DefFit(Pawn p, Profile pr, ThingDef def)
		{
			List<StatModifier> offs = def.equippedStatOffsets;
			if (offs == null || offs.Count == 0)
			{
				return 1f;
			}
			float s = 0f;
			for (int i = 0; i < offs.Count; i++)
			{
				StatModifier m = offs[i];
				if (m == null || m.stat == null || m.value == 0f)
				{
					continue;
				}
				StatInfo info = InfoOf(m.stat);
				if (info.kind == KindNone)
				{
					continue;
				}
				float w = StatWeight(pr, info);
				if (w == 0f)
				{
					continue;
				}
				s += Mathf.Clamp(m.value * info.sign * info.scale, -0.5f, 0.5f) * w;
			}
			return 1f + Mathf.Clamp(s * 2f, -0.2f, 0.25f);
		}

		public static float Factor(Pawn p, Apparel ap)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.dressByTraits || p == null || ap == null || ap.def == null)
			{
				return 1f;
			}
			Profile pr = GetProfile(p);
			int now = Now();
			long key = ((long)p.thingIDNumber << 16) | (long)ap.def.index;
			FitEntry e;
			float f;
			if (fits.TryGetValue(key, out e) && now - e.tick < FitTicks && now >= e.tick)
			{
				f = e.value;
			}
			else
			{
				f = DefFit(p, pr, ap.def);
				if (fits.Count > 20000)
				{
					fits.Clear();
				}
				e.tick = now;
				e.value = f;
				fits[key] = e;
			}
			if (pr.anyLiveFactor)
			{
				if (pr.armorPref != 0f)
				{
					float armor = ap.GetStatValue(StatDefOf.ArmorRating_Sharp) + 0.5f * ap.GetStatValue(StatDefOf.ArmorRating_Blunt);
					f *= 1f + pr.armorPref * Mathf.Clamp(armor, 0f, 1.5f) * 0.15f;
				}
				QualityCategory qc;
				if (pr.qualityPref != 0 && ap.TryGetQuality(out qc))
				{
					int dq = (int)qc - (int)QualityCategory.Normal;
					if (pr.qualityPref > 0)
					{
						f *= 1f + 0.03f * dq;
					}
					else if (dq > 0)
					{
						f *= 1f - 0.025f * dq;
					}
				}
			}
			return Mathf.Clamp(f, MinFactor, MaxFactor);
		}

		public static void Apply(Pawn p, Apparel ap, ref float score)
		{
			if (score > 0f)
			{
				score *= Factor(p, ap);
			}
		}
	}
	public static class ApparelGender
	{
		private static readonly Dictionary<ThingDef, Gender> cache = new Dictionary<ThingDef, Gender>();

		private static readonly Dictionary<ThingDef, string> why = new Dictionary<ThingDef, string>();

		private static readonly string[] femaleParts = new string[29]
		{
			"dress", "skirt", "bikini", "lingerie", "gown", "girl", "female", "woman", "women", "lady",
			"ladies", "cheongsam", "qipao", "stocking", "pantyhose", "leotard", "corset", "blouse", "heels", "brassiere",
			"princess", "queen", "maiden", "nightie", "camisole", "babydoll", "negligee", "sundress", "bunnysuit"
		};

		private static readonly string[] femaleWords = new string[6] { "bra", "maid", "nun", "panties", "miko", "her" };

		private static readonly string[] maleParts = new string[4] { "gentleman", "gentlemen", "tuxedo", "masculine" };

		private static readonly string[] maleWords = new string[8] { "male", "man", "men", "mens", "boy", "boys", "king", "his" };

		private static readonly string[] falseFemale = new string[6] { "headdress", "head dress", "address", "dresser", "redress", "skirmish" };

		private static bool cachedGuess = true;

		public static Gender Of(ThingDef def)
		{
			if (def == null || def.apparel == null)
			{
				return Gender.None;
			}
			// 2.6.4: the cache depends on the "guess gender" setting – toggling it had no effect until restart
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			bool guess = settings == null || settings.genderGuess;
			if (guess != cachedGuess)
			{
				cachedGuess = guess;
				cache.Clear();
				why.Clear();
			}
			Gender value;
			if (cache.TryGetValue(def, out value))
			{
				return value;
			}
			string reason;
			value = Compute(def, out reason);
			cache[def] = value;
			why[def] = reason;
			return value;
		}

		public static string Reason(ThingDef def)
		{
			Of(def);
			string value;
			return (!why.TryGetValue(def, out value)) ? null : value;
		}

		private static Gender Compute(ThingDef def, out string reason)
		{
			reason = null;
			if (def.apparel.gender != 0)
			{
				reason = "definice";
				return def.apparel.gender;
			}
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && !settings.genderGuess)
			{
				return Gender.None;
			}
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.Append(def.defName).Append(' ').Append(def.label)
				.Append(' ');
			if (def.apparel.wornGraphicPath != null)
			{
				stringBuilder.Append(def.apparel.wornGraphicPath).Append(' ');
			}
			if (def.apparel.tags != null)
			{
				stringBuilder.Append(string.Join(" ", def.apparel.tags.ToArray())).Append(' ');
			}
			string t = Normalize(stringBuilder.ToString());
			string text = Normalize(def.description ?? string.Empty);
			string text2 = FindFemale(t);
			string text3 = FindMale(t);
			if (text2 == null && text3 == null)
			{
				text2 = FindPhrase(text, new string[6] { "for women", "women s", "womens", "for girls", "feminine", "female" });
				text3 = FindPhrase(text, new string[5] { "for men", "men s", "mens", "for boys", "masculine" });
				if (text2 != null && text.Contains("female") && text3 == null && text.Contains(" male"))
				{
					text3 = "male";
				}
			}
			if (text2 != null && text3 == null)
			{
				reason = text2;
				return Gender.Female;
			}
			if (text3 != null && text2 == null)
			{
				reason = text3;
				return Gender.Male;
			}
			return Gender.None;
		}

		private static string Normalize(string s)
		{
			StringBuilder stringBuilder = new StringBuilder(" ");
			for (int i = 0; i < s.Length; i++)
			{
				char c = s[i];
				if (char.IsUpper(c) && i > 0 && char.IsLower(s[i - 1]))
				{
					stringBuilder.Append(' ');
				}
				stringBuilder.Append((!char.IsLetterOrDigit(c)) ? ' ' : char.ToLowerInvariant(c));
			}
			stringBuilder.Append(' ');
			return stringBuilder.ToString();
		}

		private static string FindFemale(string t)
		{
			string text = t;
			for (int i = 0; i < falseFemale.Length; i++)
			{
				text = text.Replace(falseFemale[i], " ");
			}
			for (int j = 0; j < femaleParts.Length; j++)
			{
				if (text.Contains(femaleParts[j]))
				{
					return femaleParts[j];
				}
			}
			for (int k = 0; k < femaleWords.Length; k++)
			{
				if (text.Contains(" " + femaleWords[k] + " "))
				{
					return femaleWords[k];
				}
			}
			return null;
		}

		private static string FindMale(string t)
		{
			for (int i = 0; i < maleParts.Length; i++)
			{
				if (t.Contains(maleParts[i]))
				{
					return maleParts[i];
				}
			}
			for (int j = 0; j < maleWords.Length; j++)
			{
				if (t.Contains(" " + maleWords[j] + " "))
				{
					return maleWords[j];
				}
			}
			return null;
		}

		private static string FindPhrase(string t, string[] phrases)
		{
			for (int i = 0; i < phrases.Length; i++)
			{
				if (t.Contains(" " + phrases[i] + " "))
				{
					return phrases[i];
				}
			}
			return null;
		}

		public static bool Mismatch(Pawn pawn, ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.genderClothing || pawn == null || pawn.gender == Gender.None || def == null)
			{
				return false;
			}
			Gender gender = Of(def);
			return gender != 0 && gender != pawn.gender;
		}
	}
	public static class ApparelMemory
	{
		private static readonly Dictionary<int, int> lastWear = new Dictionary<int, int>();

		private static readonly Dictionary<string, int> putOn = new Dictionary<string, int>();

		private static readonly Dictionary<string, int> removed = new Dictionary<string, int>();

		private const int RemovedMemoryTicks = 60000;

		// 2.6.4: tick values from another save made "calm"/"just put on" wrong (e.g. negative age after loading an older save)
		public static void ResetGameState()
		{
			lastWear.Clear();
			putOn.Clear();
			removed.Clear();
		}

		private static int Now
		{
			get
			{
				return (Find.TickManager != null) ? Find.TickManager.TicksGame : 0;
			}
		}

		private static int CalmTicks
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return (settings != null) ? (settings.apparelCalmHours * 2500) : 0;
			}
		}

		private static string Key(Pawn p, ThingDef d)
		{
			return p.thingIDNumber + "|" + d.defName;
		}

		public static bool Calm(Pawn pawn, int now)
		{
			int value;
			return lastWear.TryGetValue(pawn.thingIDNumber, out value) && now - value < CalmTicks;
		}

		public static void NoteWear(Pawn pawn, Apparel apparel, int now)
		{
			lastWear[pawn.thingIDNumber] = now;
			putOn[Key(pawn, apparel.def)] = now;
			if (pawn.apparel == null)
			{
				return;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (!ApparelUtility.CanWearTogether(apparel.def, wornApparel[i].def, pawn.RaceProps.body))
				{
					removed[Key(pawn, wornApparel[i].def)] = now;
				}
			}
		}

		public static void NoteRemoved(Pawn pawn, ThingDef def)
		{
			if (pawn != null && def != null)
			{
				removed[Key(pawn, def)] = Now;
			}
		}

		public static bool RecentlyRemoved(Pawn pawn, ThingDef def)
		{
			int value;
			return removed.TryGetValue(Key(pawn, def), out value) && Now - value < 60000;
		}

		public static bool JustPutOn(Pawn pawn, ThingDef def)
		{
			int value;
			return putOn.TryGetValue(Key(pawn, def), out value) && Now - value < Mathf.Max(CalmTicks, 15000);
		}
	}
	public static class RL
	{
		public static void Debug(string msg)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.debugLog)
			{
				MLog.Info("[Smart Colonist Gear] " + msg);
			}
		}

		private static bool CanNotify(Pawn pawn)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			return settings != null && settings.notifyPlayer && pawn != null && pawn.Spawned;
		}

		public static void NotifyWeapon(Pawn pawn, Thing weapon, Thing old)
		{
			WeaponSwapMemory.Note(pawn, old);
			if (CanNotify(pawn) && weapon != null)
			{
				string text = ((old != null) ? "RL_MsgWeapon".Translate(pawn.LabelShort, weapon.LabelNoCount, old.LabelNoCount).ToString() : "RL_MsgWeaponNew".Translate(pawn.LabelShort, weapon.LabelNoCount).ToString());
				Messages.Message(text, new LookTargets(pawn), MessageTypeDefOf.SilentInput, false);
			}
		}

		public static void NotifySidearm(Pawn pawn, Thing weapon)
		{
			if (CanNotify(pawn) && weapon != null && ResearchedLoadoutMod.Settings.sidearmNotify)
			{
				Messages.Message("RL_MsgSidearm".Translate(pawn.LabelShort, weapon.LabelNoCount).ToString(), new LookTargets(pawn), MessageTypeDefOf.SilentInput, false);
			}
		}

		public static void NotifyApparel(Pawn pawn, Thing apparel)
		{
			if (CanNotify(pawn) && apparel != null)
			{
				Messages.Message("RL_MsgApparel".Translate(pawn.LabelShort, apparel.LabelNoCount).ToString(), new LookTargets(pawn), MessageTypeDefOf.SilentInput, false);
			}
		}
	}
	public sealed class ResearchedLoadoutSettings : ModSettings
	{
		public bool enabled = true;

		public bool useRecipes = true;

		public bool blockHigherTech = true;

		public bool blockUntaggedModItems = true;

		public bool techMismatchCheck = true;

		public bool levelByProgress = true;

		public bool syncWithWorld = true;

		public bool survivalApparel = true;

		public int apparelCalmHours = 12;

		public bool keepArmor = true;

		public bool tempByThresholds = true;

		public bool genderClothing = true;

		public bool genderGuess = true;

		public bool roleGear = true;

		public bool statOffsets = true;

		public bool workClothing = true;

		// 2.7.0: apparel choice by the wearer's traits and skills
		public bool dressByTraits = true;

		public bool hideSidearmsPage = true;

		// 2.8.0: emergency gear in dangerous situations
		public bool emergencyGear = true;

		public bool emergencyToxic = true;

		public bool emergencyTemperature = true;

		// 2.15.0: guests use colony gear and give it back when they leave
		public bool guestLodgers = true;

		public bool guestVisitors = true;

		public bool emergencyRaid = true;

		// 2.8.1: find (unforbid) and craft emergency gear or a substitute
		public bool emergencyProvide = true;

		public int gearStability = 30;

		public int workClothingMinSkill = 6;

		public bool specialEffects = true;

		public bool coverageFirst = true;

		// 2.8.5
		public bool dressFullBody = true;

		public bool threatApparel = true;

		public bool threatBills = true;

		public float coldThreshold;

		public float heatThreshold = 35f;

		public int levelProgressPercent = 25;

		public bool powerEstimate = true;

		public bool autoEquipPrimary = true;

		public bool useSkills = true;

		// 2.15.0: newer tech preferred (guns over bows, newer armor over plate); work tools are never weapons
		public bool preferNewerTech = true;

		// 2.15.0
		public bool preferNewEra = true;

		public bool toolsNotWeapons = true;

		public bool autoEquipSidearm = true;

		// 2.8.2: extra sidearm settings
		public bool sidearmFilter = true;

		public bool sidearmUpgrade = true;

		public bool sidearmNotify = true;

		public bool sidearmOriginalUi = true;

		// 2.9.0
		public bool sidearmBrawlerNoRanged = true;

		public int sidearmMinShooting;

		public int sidearmMinMelee;

		// 2.9.1: how many weapons a colonist may carry in total (weapon in hand included), 0 = no own limit
		public int sidearmMaxTotal = 3;

		// 2.15.1: how many weapons of each kind a colonist carries (the one in hand included)
		public int sidearmMaxMelee = 1;

		public int sidearmMaxRanged = 1;

		// 2.9.1: weapons the colony has researched but never made are put away too (not only unresearched ones)
		public bool stripUncraftedWeapons = true;

		// 2.10.0: put on armor from storage before hunting and when the map is attacked
		public bool armorOnHunt = true;

		public bool armorOnThreat = true;

		public bool armorDrafted = true;

		// 2.11.0: own sidearm system when Simple Sidearms is not installed
		public bool ownSwitchMelee = true;

		public float ownMeleeRange = 2.9f;

		public bool ownSwitchBack = true;

		public bool ownSwitchGizmo = true;

		public float ownMaxMass = 0f;

		public bool autoEquipApparel = true;

		public bool useOwnDresser;

		public bool onlyFromStockpile;

		public bool blockLockedEquip = true;

		public bool blockLockedApparel = true;

		public bool stripLockedGear = true;

		public bool onlyCrafted;

		public bool replaceBrokenGear = true;

		public bool stripWornLowHp;

		public bool storeDroppedGear = true;

		public bool pauseDuringThreat = true;

		public bool armSlaves;

		public bool prioritizeFighters = true;

		public bool considerDistance = true;

		public bool notifyPlayer;

		public bool debugLog;

		public int minApparelHpPercent = 50;

		public int weaponCalmHours = 24;

		public string bannedWeapons = string.Empty;

		public string bannedApparel = string.Empty;

		// 2.6.4: a new install starts at the current version; with 0 the value was never written
		// (it equals the Scribe default) and the migrations below reset useOwnDresser/onlyFromStockpile on every start
		public int settingsVersion = 11;

		// 2.13.0: "Recommended settings" switch. A new install starts with it on;
		// settings saved by an older version keep their own values (missing key = off).
		public bool recommended = true;

		public override void ExposeData()
		{
			Scribe_Values.Look(ref enabled, "enabled", true);
			Scribe_Values.Look(ref useRecipes, "useRecipes", true);
			Scribe_Values.Look(ref blockHigherTech, "blockHigherTech", true);
			Scribe_Values.Look(ref blockUntaggedModItems, "blockUntaggedModItems", true);
			Scribe_Values.Look(ref techMismatchCheck, "techMismatchCheck", true);
			Scribe_Values.Look(ref levelByProgress, "levelByProgress", true);
			Scribe_Values.Look(ref syncWithWorld, "syncWithWorld", true);
			Scribe_Values.Look(ref survivalApparel, "survivalApparel", true);
			Scribe_Values.Look(ref apparelCalmHours, "apparelCalmHours", 12);
			Scribe_Values.Look(ref keepArmor, "keepArmor", true);
			Scribe_Values.Look(ref tempByThresholds, "tempByThresholds", true);
			Scribe_Values.Look(ref genderClothing, "genderClothing", true);
			Scribe_Values.Look(ref genderGuess, "genderGuess", true);
			Scribe_Values.Look(ref roleGear, "roleGear", true);
			Scribe_Values.Look(ref statOffsets, "statOffsets", true);
			Scribe_Values.Look(ref workClothing, "workClothing", true);
			Scribe_Values.Look(ref dressByTraits, "dressByTraits", true);
			Scribe_Values.Look(ref hideSidearmsPage, "hideSidearmsPage", true);
			Scribe_Values.Look(ref emergencyGear, "emergencyGear", true);
			Scribe_Values.Look(ref emergencyToxic, "emergencyToxic", true);
			Scribe_Values.Look(ref emergencyTemperature, "emergencyTemperature", true);
			Scribe_Values.Look(ref guestLodgers, "guestLodgers", true);
			Scribe_Values.Look(ref guestVisitors, "guestVisitors", true);
			Scribe_Values.Look(ref emergencyRaid, "emergencyRaid", true);
			Scribe_Values.Look(ref emergencyProvide, "emergencyProvide", true);
			Scribe_Values.Look(ref gearStability, "gearStability", 30);
			Scribe_Values.Look(ref workClothingMinSkill, "workClothingMinSkill", 6);
			Scribe_Values.Look(ref specialEffects, "specialEffects", true);
			Scribe_Values.Look(ref coverageFirst, "coverageFirst", true);
			Scribe_Values.Look(ref dressFullBody, "dressFullBody", true);
			Scribe_Values.Look(ref threatApparel, "threatApparel", true);
			Scribe_Values.Look(ref threatBills, "threatBills", true);
			Scribe_Values.Look(ref coldThreshold, "coldThreshold", 0f);
			Scribe_Values.Look(ref heatThreshold, "heatThreshold", 35f);
			Scribe_Values.Look(ref levelProgressPercent, "levelProgressPercent", 25);
			Scribe_Values.Look(ref powerEstimate, "powerEstimate", true);
			Scribe_Values.Look(ref autoEquipPrimary, "autoEquipPrimary", true);
			Scribe_Values.Look(ref useSkills, "useSkills", true);
			Scribe_Values.Look(ref preferNewerTech, "preferNewerTech", true);
			Scribe_Values.Look(ref preferNewEra, "preferNewEra", true);
			Scribe_Values.Look(ref toolsNotWeapons, "toolsNotWeapons", true);
			Scribe_Values.Look(ref autoEquipSidearm, "autoEquipSidearm", true);
			Scribe_Values.Look(ref sidearmFilter, "sidearmFilter", true);
			Scribe_Values.Look(ref sidearmUpgrade, "sidearmUpgrade", true);
			Scribe_Values.Look(ref sidearmNotify, "sidearmNotify", true);
			Scribe_Values.Look(ref sidearmOriginalUi, "sidearmOriginalUi", true);
			Scribe_Values.Look(ref sidearmBrawlerNoRanged, "sidearmBrawlerNoRanged", true);
			Scribe_Values.Look(ref sidearmMinShooting, "sidearmMinShooting", 0);
			Scribe_Values.Look(ref sidearmMinMelee, "sidearmMinMelee", 0);
			Scribe_Values.Look(ref sidearmMaxTotal, "sidearmMaxTotal", 3);
			Scribe_Values.Look(ref sidearmMaxMelee, "sidearmMaxMelee", 1);
			Scribe_Values.Look(ref sidearmMaxRanged, "sidearmMaxRanged", 1);
			Scribe_Values.Look(ref stripUncraftedWeapons, "stripUncraftedWeapons", true);
			Scribe_Values.Look(ref armorOnHunt, "armorOnHunt", true);
			Scribe_Values.Look(ref armorOnThreat, "armorOnThreat", true);
			Scribe_Values.Look(ref armorDrafted, "armorDrafted", true);
			Scribe_Values.Look(ref ownSwitchMelee, "ownSwitchMelee", true);
			Scribe_Values.Look(ref ownMeleeRange, "ownMeleeRange", 2.9f);
			Scribe_Values.Look(ref ownSwitchBack, "ownSwitchBack", true);
			Scribe_Values.Look(ref ownSwitchGizmo, "ownSwitchGizmo", true);
			Scribe_Values.Look(ref ownMaxMass, "ownMaxMass", 0f);
			Scribe_Values.Look(ref autoEquipApparel, "autoEquipApparel", true);
			Scribe_Values.Look(ref useOwnDresser, "useOwnDresser", false);
			Scribe_Values.Look(ref onlyFromStockpile, "onlyFromStockpile", false);
			Scribe_Values.Look(ref blockLockedEquip, "blockLockedEquip", true);
			Scribe_Values.Look(ref blockLockedApparel, "blockLockedApparel", true);
			Scribe_Values.Look(ref stripLockedGear, "stripLockedGear", true);
			Scribe_Values.Look(ref onlyCrafted, "onlyCrafted", false);
			Scribe_Values.Look(ref replaceBrokenGear, "replaceBrokenGear", true);
			Scribe_Values.Look(ref stripWornLowHp, "stripWornLowHp", false);
			Scribe_Values.Look(ref storeDroppedGear, "storeDroppedGear", true);
			Scribe_Values.Look(ref pauseDuringThreat, "pauseDuringThreat", true);
			Scribe_Values.Look(ref armSlaves, "armSlaves", false);
			Scribe_Values.Look(ref prioritizeFighters, "prioritizeFighters", true);
			Scribe_Values.Look(ref considerDistance, "considerDistance", true);
			Scribe_Values.Look(ref notifyPlayer, "notifyPlayer", false);
			Scribe_Values.Look(ref debugLog, "debugLog", false);
			Scribe_Values.Look(ref minApparelHpPercent, "minApparelHpPercent", 50);
			Scribe_Values.Look(ref weaponCalmHours, "weaponCalmHours", 24);
			Scribe_Values.Look(ref bannedWeapons, "bannedWeapons", string.Empty);
			Scribe_Values.Look(ref bannedApparel, "bannedApparel", string.Empty);
			Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0);
			Scribe_Values.Look(ref recommended, "recommended", false);
			if (Scribe.mode != LoadSaveMode.PostLoadInit)
			{
				return;
			}
			if (settingsVersion < 3)
			{
				useRecipes = true;
				stripWornLowHp = false;
				settingsVersion = 3;
			}
			if (settingsVersion < 6)
			{
				useOwnDresser = false;
				onlyFromStockpile = false;
				settingsVersion = 6;
			}
			// 2.6.4: version 7 runs before 8 – it was skipped before
			if (settingsVersion < 7)
			{
				if (minApparelHpPercent == 40)
				{
					minApparelHpPercent = 50;
				}
				settingsVersion = 7;
			}
			if (settingsVersion < 8)
			{
				blockUntaggedModItems = true;
				settingsVersion = 8;
			}
			// 2.7.0: new option, on for existing users too
			if (settingsVersion < 9)
			{
				dressByTraits = true;
				settingsVersion = 9;
			}
			// 2.8.0: emergency gear, on for existing users too
			if (settingsVersion < 10)
			{
				emergencyGear = true;
				emergencyToxic = true;
				emergencyTemperature = true;
				emergencyRaid = true;
				settingsVersion = 10;
			}
			// 2.8.1: find and craft emergency gear, on for existing users too
			if (settingsVersion < 11)
			{
				emergencyProvide = true;
				settingsVersion = 11;
			}
			LoadoutFilter.InvalidateResearch();
			LoadoutFilter.InvalidateBans();
		}
	}
	public static class LoadoutFilter
	{
		private sealed class ResearchReq
		{
			public readonly List<ResearchProjectDef> always = new List<ResearchProjectDef>();

			public readonly List<List<ResearchProjectDef>> options = new List<List<ResearchProjectDef>>();

			public bool freeRecipe;
		}

		private static readonly HashSet<string> bannedWeapons = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static readonly HashSet<string> bannedApparel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		private static string bannedWeaponsBlob;

		private static string bannedApparelBlob;

		private static bool bansDirty = true;

		private static readonly Dictionary<ThingDef, ResearchReq> researchCache = new Dictionary<ThingDef, ResearchReq>();

		private static int techTick = int.MinValue;

		private static TechLevel cachedTech = TechLevel.Neolithic;

		private static bool worldLooked;

		private static MethodInfo worldLevelMethod;

		private static int needTick = -1;

		private static int needPawn = -1;

		private static int needCold;

		private static int needHeat;

		public static bool WorldModPresent
		{
			get
			{
				if (!worldLooked)
				{
					worldLooked = true;
					Type type = AccessTools.TypeByName("ResearchLockedRaidGear.ResearchLockedGearFilter");
					if (type != null)
					{
						worldLevelMethod = AccessTools.Method(type, "ComputeColonyTech", new Type[2]
						{
							typeof(TechLevel).MakeByRefType(),
							typeof(TechLevel).MakeByRefType()
						});
					}
				}
				return worldLevelMethod != null;
			}
		}

		public static void InvalidateBans()
		{
			bansDirty = true;
		}

		public static void InvalidateResearch()
		{
			researchCache.Clear();
			techTick = int.MinValue;
		}

		private static void EnsureBans()
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null)
			{
				if (bansDirty || bannedWeaponsBlob != settings.bannedWeapons)
				{
					ParseBanBlob(settings.bannedWeapons, bannedWeapons);
					bannedWeaponsBlob = settings.bannedWeapons;
				}
				if (bansDirty || bannedApparelBlob != settings.bannedApparel)
				{
					ParseBanBlob(settings.bannedApparel, bannedApparel);
					bannedApparelBlob = settings.bannedApparel;
				}
				bansDirty = false;
			}
		}

		private static void ParseBanBlob(string blob, HashSet<string> dest)
		{
			dest.Clear();
			if (string.IsNullOrEmpty(blob))
			{
				return;
			}
			string[] array = blob.Split('\n', '\r', ',', ';');
			for (int i = 0; i < array.Length; i++)
			{
				string text = array[i].Trim();
				if (text.Length > 0)
				{
					dest.Add(text);
				}
			}
		}

		public static bool IsAllowed(ThingDef def)
		{
			if (!IsResearchAllowed(def))
			{
				return false;
			}
			return CraftedOk(def);
		}

		public static bool CraftedOk(ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (def == null || settings == null || !settings.enabled || !settings.onlyCrafted)
			{
				return true;
			}
			if (!def.IsWeapon && !def.IsApparel)
			{
				return true;
			}
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			if (researchedLoadoutGameComponent == null)
			{
				return true;
			}
			researchedLoadoutGameComponent.EnsureCraftedSeeded();
			return researchedLoadoutGameComponent.crafted.Contains(def.defName) || SharedCrafted.PartnerHas(def.defName);
		}

		public static bool IsResearchAllowed(ThingDef def)
		{
			if (def == null)
			{
				return true;
			}
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled)
			{
				return true;
			}
			if (!def.IsWeapon && !def.IsApparel)
			{
				return true;
			}
			if (IsManuallyBanned(def))
			{
				return false;
			}
			TechLevel techLevel = RequiredTech(def, settings);
			ResearchReq researchRequirements = GetResearchRequirements(def);
			if (!AllFinished(researchRequirements.always))
			{
				return false;
			}
			bool flag = false;
			if (researchRequirements.options.Count > 0 && !researchRequirements.freeRecipe)
			{
				bool flag2 = false;
				for (int i = 0; i < researchRequirements.options.Count; i++)
				{
					if (AllFinished(researchRequirements.options[i]))
					{
						flag2 = true;
						break;
					}
				}
				if (!flag2)
				{
					return false;
				}
				flag = true;
			}
			else if (researchRequirements.always.Count > 0)
			{
				flag = true;
			}
			if (flag)
			{
				return (int)techLevel <= (int)ColonyTech();
			}
			if (researchRequirements.freeRecipe)
			{
				return (int)Max(def.techLevel, techLevel) <= (int)ColonyTech();
			}
			if ((def.techLevel == TechLevel.Neolithic || (def.techLevel == TechLevel.Undefined && !IsNonOfficialModItem(def))) && (int)techLevel <= 2)
			{
				return true;
			}
			if (settings.blockHigherTech && (int)Max(def.techLevel, techLevel) > (int)ColonyTech())
			{
				return false;
			}
			if (settings.blockUntaggedModItems && IsNonOfficialModItem(def))
			{
				return false;
			}
			return true;
		}

		private static TechLevel Max(TechLevel a, TechLevel b)
		{
			return ((int)a <= (int)b) ? b : a;
		}

		public static TechLevel RequiredTech(ThingDef def, ResearchedLoadoutSettings settings)
		{
			TechLevel techLevel = TechLevel.Undefined;
			if (settings.techMismatchCheck && def.techLevel != 0)
			{
				techLevel = def.techLevel;
			}
			if (settings.powerEstimate)
			{
				techLevel = Max(techLevel, PowerTech.Estimate(def));
			}
			return techLevel;
		}

		public static bool IsManuallyBanned(ThingDef def)
		{
			if (def == null)
			{
				return false;
			}
			EnsureBans();
			if (def.IsWeapon)
			{
				return bannedWeapons.Contains(def.defName);
			}
			return bannedApparel.Contains(def.defName);
		}

		public static bool IsRealWeapon(ThingDef def)
		{
			return def != null && def.IsWeapon && def.equipmentType == EquipmentType.Primary && !def.IsStuff;
		}

		// 2.6.1: grenades and other thrown weapons are never auto-picked, compared or dropped
		public static bool IsThrownWeapon(ThingDef def)
		{
			if (def == null || !def.IsRangedWeapon)
			{
				return false;
			}
			if (def.weaponTags != null)
			{
				for (int i = 0; i < def.weaponTags.Count; i++)
				{
					string text = def.weaponTags[i];
					if (text != null && text.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0)
					{
						return true;
					}
				}
			}
			VerbProperties verbProperties = (def.Verbs != null && def.Verbs.Count > 0) ? def.Verbs[0] : null;
			if (verbProperties == null)
			{
				return false;
			}
			if (verbProperties.rangedFireRulepack != null && verbProperties.rangedFireRulepack.defName == "Combat_RangedFire_Thrown")
			{
				return true;
			}
			return verbProperties.verbClass != null && verbProperties.verbClass.Name.IndexOf("Throw", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		// 2.15.0: newer tech gets a bonus (guns over bows, flak or powered armor over plate)
		public static float TechMul(ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (def == null || settings == null || !settings.preferNewerTech || def.techLevel == TechLevel.Undefined || def.techLevel == TechLevel.Animal)
			{
				return 1f;
			}
			return Mathf.Clamp(1f + 0.12f * ((int)def.techLevel - (int)TechLevel.Medieval), 0.85f, 1.4f) * EraMul(def);
		}

		// 2.15.0: gear of the colony's newest era (the tech level it has reached) is clearly preferred
		// when it is there; gear two or more eras behind loses. Weapons and apparel alike.
		// 2.15.1: stronger (current era ×1.6, two eras behind ×0.7) and by the item's real era – its own
		// tech level, the research it needs and its power (modded items often have no tech level)
		public static float EraMul(ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (def == null || settings == null || !settings.preferNewEra)
			{
				return 1f;
			}
			TechLevel era = EffectiveTech(def);
			if (era == TechLevel.Undefined || era == TechLevel.Animal)
			{
				return 1f;
			}
			int behind = (int)ColonyTech() - (int)era;
			if (behind <= 0)
			{
				return 1.6f;
			}
			return behind == 1 ? 1f : 0.7f;
		}

		// 2.15.1: the era decides first between weapons of the same kind (a worse rifle beats a better
		// bow); items without any era count as medieval
		public static bool NewEraFirst => ResearchedLoadoutMod.Settings != null && ResearchedLoadoutMod.Settings.preferNewEra;

		public static int EraRank(ThingDef def)
		{
			TechLevel t = EffectiveTech(def);
			if (t == TechLevel.Undefined)
			{
				t = TechLevel.Medieval;
			}
			return (int)Max(Min(t, ColonyTech()), TechLevel.Neolithic);
		}

		private static TechLevel Min(TechLevel a, TechLevel b)
		{
			return ((int)a <= (int)b) ? a : b;
		}

		private static readonly Dictionary<ThingDef, TechLevel> eraCache = new Dictionary<ThingDef, TechLevel>();

		public static TechLevel EffectiveTech(ThingDef def)
		{
			if (def == null)
			{
				return TechLevel.Undefined;
			}
			if (eraCache.TryGetValue(def, out TechLevel known))
			{
				return known;
			}
			TechLevel t = def.techLevel == TechLevel.Animal ? TechLevel.Undefined : def.techLevel;
			try
			{
				ResearchReq req = GetResearchRequirements(def);
				foreach (ResearchProjectDef r in req.always)
				{
					t = Max(t, r.techLevel);
				}
				if (req.options.Count > 0)
				{
					// the easiest way to make it decides
					TechLevel easiest = TechLevel.Archotech;
					foreach (List<ResearchProjectDef> opt in req.options)
					{
						TechLevel o = TechLevel.Undefined;
						foreach (ResearchProjectDef r in opt)
						{
							o = Max(o, r.techLevel);
						}
						if ((int)o < (int)easiest)
						{
							easiest = o;
						}
					}
					if (easiest != TechLevel.Archotech)
					{
						t = Max(t, easiest);
					}
				}
				if (ResearchedLoadoutMod.Settings != null && ResearchedLoadoutMod.Settings.powerEstimate)
				{
					t = Max(t, PowerTech.Estimate(def));
				}
			}
			catch
			{
			}
			eraCache[def] = t;
			return t;
		}

		private static readonly Dictionary<ThingDef, bool> toolCache = new Dictionary<ThingDef, bool>();

		private static readonly string[] toolStats = { "MiningSpeed", "MiningYield", "PlantWorkSpeed", "PlantHarvestYield", "ConstructionSpeed", "SmoothingSpeed", "DeepDrillingSpeed", "ButcheryFleshSpeed", "ButcheryFleshEfficiency", "WorkSpeedGlobal", "TreeFelling", "Harvest", "Sowing" };

		private static readonly string[] toolWords = { "Pickaxe", "Shovel", "Sickle", "Hoe", "Wrench", "Spade", "Scythe" };

		// 2.15.0: pickaxes and other work tools: used for work, never picked as a weapon
		public static bool ToolNotWeapon(ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (def == null || settings == null || !settings.toolsNotWeapons || !def.IsWeapon)
			{
				return false;
			}
			bool value;
			if (toolCache.TryGetValue(def, out value))
			{
				return value;
			}
			value = false;
			if (def.equippedStatOffsets != null)
			{
				for (int i = 0; i < def.equippedStatOffsets.Count && !value; i++)
				{
					StatModifier m = def.equippedStatOffsets[i];
					if (m == null || m.stat == null || m.value <= 0f)
					{
						continue;
					}
					for (int j = 0; j < toolStats.Length; j++)
					{
						if (m.stat.defName.IndexOf(toolStats[j], StringComparison.OrdinalIgnoreCase) >= 0)
						{
							value = true;
							break;
						}
					}
				}
			}
			if (!value && def.weaponTags != null)
			{
				for (int i = 0; i < def.weaponTags.Count; i++)
				{
					string t = def.weaponTags[i];
					if (t != null && (t.Equals("Tool", StringComparison.OrdinalIgnoreCase) || t.IndexOf("WorkTool", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("SurvivalTool", StringComparison.OrdinalIgnoreCase) >= 0))
					{
						value = true;
					}
				}
			}
			if (!value)
			{
				for (int i = 0; i < toolWords.Length; i++)
				{
					if (def.defName.IndexOf(toolWords[i], StringComparison.OrdinalIgnoreCase) >= 0)
					{
						value = true;
						break;
					}
				}
			}
			toolCache[def] = value;
			return value;
		}

		public static bool IsManagedWeapon(ThingDef def)
		{
			return IsRealWeapon(def) && !IsThrownWeapon(def);
		}

		public static bool IsPlayerColonist(Pawn pawn)
		{
			return pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.RaceProps != null && pawn.RaceProps.ToolUser && !pawn.Dead && (pawn.apparel != null || pawn.equipment != null);
		}

		public static TechLevel ColonyTechPublic()
		{
			return ColonyTech();
		}

		public static void InvalidateColonyTech()
		{
			techTick = -2147483647;
		}

		public static void ResetGameState()
		{
			InvalidateResearch();
			needTick = -1;
			needPawn = -1;
		}

		private static TechLevel ColonyTech()
		{
			int num = ((Find.TickManager == null) ? int.MinValue : Find.TickManager.TicksGame);
			if (num != int.MinValue && num == techTick)
			{
				return cachedTech;
			}
			TechLevel scenario;
			TechLevel research;
			cachedTech = ComputeColonyTech(out scenario, out research);
			techTick = num;
			return cachedTech;
		}

		private static TechLevel ComputeColonyTech(out TechLevel scenario, out TechLevel research)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.syncWithWorld && WorldModPresent)
			{
				try
				{
					object[] array = new object[2]
					{
						TechLevel.Undefined,
						TechLevel.Undefined
					};
					TechLevel result = (TechLevel)worldLevelMethod.Invoke(null, array);
					scenario = (TechLevel)array[0];
					research = (TechLevel)array[1];
					return result;
				}
				catch
				{
				}
			}
			return ComputeOwnColonyTech(out scenario, out research);
		}

		private static TechLevel ComputeOwnColonyTech(out TechLevel scenario, out TechLevel research)
		{
			scenario = TechLevel.Neolithic;
			Faction ofPlayerSilentFail = Faction.OfPlayerSilentFail;
			if (ofPlayerSilentFail != null && ofPlayerSilentFail.def != null && (int)ofPlayerSilentFail.def.techLevel > (int)scenario)
			{
				scenario = ofPlayerSilentFail.def.techLevel;
			}
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			bool flag = settings == null || settings.levelByProgress;
			float num = ((settings == null) ? 0.25f : ((float)settings.levelProgressPercent / 100f));
			int[] array = new int[16];
			int[] array2 = new int[16];
			research = TechLevel.Neolithic;
			List<ResearchProjectDef> allDefsListForReading = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ResearchProjectDef researchProjectDef = allDefsListForReading[i];
				if (researchProjectDef == null)
				{
					continue;
				}
				int num2 = Mathf.Clamp((int)researchProjectDef.techLevel, 0, 15);
				array[num2]++;
				if (researchProjectDef.IsFinished)
				{
					array2[num2]++;
					if (!flag && (int)researchProjectDef.techLevel > (int)research)
					{
						research = researchProjectDef.techLevel;
					}
				}
			}
			if (flag)
			{
				for (int j = 1; j < 16; j++)
				{
					if (array[j] > 0 && (float)array2[j] >= Mathf.Max(1f, (float)array[j] * num) && (int)(byte)j > (int)research)
					{
						research = (TechLevel)j;
					}
				}
			}
			return ((int)scenario <= (int)research) ? research : scenario;
		}

		public static string ColonyTechExplain()
		{
			TechLevel scenario;
			TechLevel research;
			TechLevel tl = ComputeColonyTech(out scenario, out research);
			return "RL_CurrentLevel".Translate(tl.ToStringHuman(), scenario.ToStringHuman(), research.ToStringHuman());
		}

		private static bool AllFinished(List<ResearchProjectDef> list)
		{
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i] != null && !list[i].IsFinished)
				{
					return false;
				}
			}
			return true;
		}

		public static bool SurvivalAllowed(Pawn pawn, ThingDef def)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.survivalApparel || pawn == null || def == null || !def.IsApparel)
			{
				return false;
			}
			if (IsManuallyBanned(def))
			{
				return false;
			}
			int cold;
			int heat;
			TemperatureNeed(pawn, out cold, out heat);
			if (cold == 0 && heat == 0)
			{
				return false;
			}
			ThingDef stuff = ((!def.MadeFromStuff) ? null : GenStuff.DefaultStuffFor(def));
			float statValueAbstract = def.GetStatValueAbstract(StatDefOf.Insulation_Cold, stuff);
			float statValueAbstract2 = def.GetStatValueAbstract(StatDefOf.Insulation_Heat, stuff);
			return (cold > 0 && statValueAbstract >= 5f) || (heat > 0 && statValueAbstract2 >= 5f);
		}

		public static bool SurvivalAllowed(Pawn pawn, Thing apparel)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.survivalApparel || pawn == null || apparel == null || !apparel.def.IsApparel)
			{
				return false;
			}
			if (IsManuallyBanned(apparel.def))
			{
				return false;
			}
			int cold;
			int heat;
			TemperatureNeed(pawn, out cold, out heat);
			if (cold == 0 && heat == 0)
			{
				return false;
			}
			float statValue = apparel.GetStatValue(StatDefOf.Insulation_Cold);
			float statValue2 = apparel.GetStatValue(StatDefOf.Insulation_Heat);
			return (cold > 0 && statValue >= 5f) || (heat > 0 && statValue2 >= 5f);
		}

		public static bool AllowedFor(Pawn pawn, Thing thing)
		{
			return IsAllowed(thing.def) || SurvivalAllowed(pawn, thing) || NakedFallback(pawn, thing.def) || EmergencyGear.AllowsThing(pawn, thing);
		}

		public static bool NakedFallback(Pawn pawn, ThingDef def)
		{
			if (pawn == null || def == null || !def.IsApparel)
			{
				return false;
			}
			if (!IsResearchAllowed(def))
			{
				return false;
			}
			return Coverage.FillsBasicGap(pawn, def);
		}

		public static void TemperatureNeed(Pawn pawn, out int cold, out int heat)
		{
			int num = ((Find.TickManager != null) ? Find.TickManager.TicksGame : 0);
			if (num == needTick && pawn.thingIDNumber == needPawn)
			{
				cold = needCold;
				heat = needHeat;
				return;
			}
			cold = 0;
			heat = 0;
			Map mapHeld = pawn.MapHeld;
			if (mapHeld != null && pawn.health != null)
			{
				if (pawn.health.hediffSet.HasHediff(HediffDefOf.Hypothermia))
				{
					cold = 2;
				}
				if (pawn.health.hediffSet.HasHediff(HediffDefOf.Heatstroke))
				{
					heat = 2;
				}
				float outdoorTemp = mapHeld.mapTemperature.OutdoorTemp;
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				if (settings != null && settings.tempByThresholds)
				{
					if (cold == 0 && outdoorTemp < settings.coldThreshold)
					{
						cold = 1;
					}
					if (heat == 0 && outdoorTemp > settings.heatThreshold)
					{
						heat = 1;
					}
				}
				else
				{
					FloatRange floatRange = pawn.SafeTemperatureRange();
					if (cold == 0 && outdoorTemp < floatRange.min)
					{
						cold = 1;
					}
					if (heat == 0 && outdoorTemp > floatRange.max)
					{
						heat = 1;
					}
				}
			}
			needTick = num;
			needPawn = pawn.thingIDNumber;
			needCold = cold;
			needHeat = heat;
		}

		public static bool SurvivalKeepWorn(Pawn pawn, Apparel apparel)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.survivalApparel || pawn == null || apparel == null)
			{
				return false;
			}
			if (IsManuallyBanned(apparel.def))
			{
				return false;
			}
			Map mapHeld = pawn.MapHeld;
			if (mapHeld == null)
			{
				return false;
			}
			float statValue = apparel.GetStatValue(StatDefOf.Insulation_Cold);
			float statValue2 = apparel.GetStatValue(StatDefOf.Insulation_Heat);
			if (statValue < 5f && statValue2 < 5f)
			{
				return false;
			}
			float outdoorTemp = mapHeld.mapTemperature.OutdoorTemp;
			bool flag;
			bool flag2;
			if (settings.tempByThresholds)
			{
				flag = statValue >= 5f && outdoorTemp < settings.coldThreshold + 3f;
				flag2 = statValue2 >= 5f && outdoorTemp > settings.heatThreshold - 3f;
			}
			else
			{
				FloatRange floatRange = pawn.SafeTemperatureRange();
				flag = statValue >= 5f && outdoorTemp < floatRange.min + statValue;
				flag2 = statValue2 >= 5f && outdoorTemp > floatRange.max - statValue2;
			}
			return flag || flag2 || FreezingOrOverheating(pawn);
		}

		public static float ArmorValue(Thing apparel)
		{
			if (apparel == null)
			{
				return 0f;
			}
			return Mathf.Max(apparel.GetStatValue(StatDefOf.ArmorRating_Sharp), apparel.GetStatValue(StatDefOf.ArmorRating_Blunt));
		}

		public static bool IsArmor(Thing apparel)
		{
			return ArmorValue(apparel) >= 0.15f;
		}

		public static bool WouldDowngradeArmor(Pawn pawn, Apparel candidate)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.keepArmor || pawn == null || pawn.apparel == null || candidate == null)
			{
				return false;
			}
			// 2.8.0: in toxic air a gas mask may replace an armored helmet, unless there is also an attack
			Map mapHeld = pawn.MapHeld;
			if ((EmergencyGear.CounterMask(candidate.def) & EmergencyGear.Toxic) != 0 && (EmergencyGear.ToxicFor(pawn) || ThreatResponse.ToxicCondition(mapHeld)) && !EmergencyGear.Active(mapHeld, EmergencyGear.Raid))
			{
				return false;
			}
			float num = ArmorValue(candidate);
			TechLevel candEra = EffectiveTech(candidate.def);
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				Apparel apparel = wornApparel[i];
				if (apparel != candidate && !ApparelUtility.CanWearTogether(candidate.def, apparel.def, pawn.RaceProps.body) && IsArmor(apparel) && !IsBelowMinHp(apparel) && (IsResearchAllowed(apparel.def) || SurvivalKeepWorn(pawn, apparel) || EmergencyGear.KeepWorn(pawn, apparel.def)) && num < ArmorValue(apparel) * (NewerEra(candEra, apparel.def, settings) ? 0.7f : 0.9f))
				{
					// 2.15.1: armor of the colony's newer era may be a little weaker (flak vest for plate armor)
					return true;
				}
			}
			return false;
		}

		private static bool NewerEra(TechLevel cand, ThingDef worn, ResearchedLoadoutSettings settings)
		{
			if (!settings.preferNewEra || cand == TechLevel.Undefined)
			{
				return false;
			}
			TechLevel w = EffectiveTech(worn);
			return w != TechLevel.Undefined && (int)cand > (int)w && (int)cand >= (int)ColonyTech() - 1;
		}

		public static bool FreezingOrOverheating(Pawn pawn)
		{
			int cold;
			int heat;
			TemperatureNeed(pawn, out cold, out heat);
			return cold >= 2 || heat >= 2;
		}

		public static bool AllowedFor(Pawn pawn, ThingDef def)
		{
			return IsAllowed(def) || SurvivalAllowed(pawn, def) || NakedFallback(pawn, def) || EmergencyGear.AllowsDef(pawn, def);
		}

		public static string LockReason(ThingDef def)
		{
			if (def == null || IsAllowed(def))
			{
				return null;
			}
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (IsManuallyBanned(def))
			{
				return "rucne zakazano";
			}
			if (IsResearchAllowed(def))
			{
				return "jeste nevyrobeno";
			}
			ResearchReq researchRequirements = GetResearchRequirements(def);
			if (!AllFinished(researchRequirements.always))
			{
				return "chybi vyzkum";
			}
			if (researchRequirements.options.Count > 0 && !researchRequirements.freeRecipe)
			{
				bool flag = false;
				for (int i = 0; i < researchRequirements.options.Count; i++)
				{
					if (AllFinished(researchRequirements.options[i]))
					{
						flag = true;
						break;
					}
				}
				if (!flag)
				{
					return "chybi vyzkum";
				}
				return "vyspelejsi nez kolonie (" + RequiredTech(def, settings).ToStringHuman() + ")";
			}
			if (researchRequirements.always.Count > 0 || researchRequirements.freeRecipe)
			{
				return "vyspelejsi nez kolonie (" + Max(def.techLevel, RequiredTech(def, settings)).ToStringHuman() + ")";
			}
			if (settings.blockHigherTech && (int)Max(def.techLevel, RequiredTech(def, settings)) > (int)ColonyTech())
			{
				return "bez vyzkumu, vyssi tech level";
			}
			return "modova vec bez vyzkumu";
		}

		public static string Explain(ThingDef def)
		{
			if (def == null || Current.Game == null || Find.ResearchManager == null)
			{
				return null;
			}
			StringBuilder stringBuilder = new StringBuilder();
			bool flag = IsAllowed(def);
			stringBuilder.AppendLine(((!flag) ? "RL_Ex_Locked" : "RL_Ex_Allowed").Translate().ToString());
			if (IsManuallyBanned(def))
			{
				stringBuilder.AppendLine("RL_Ex_Banned".Translate());
			}
			if (IsResearchAllowed(def) && !CraftedOk(def))
			{
				stringBuilder.AppendLine("RL_Ex_NotCrafted".Translate());
			}
			string text0 = EmergencyGear.ExplainLine(def);
			if (text0 != null)
			{
				stringBuilder.AppendLine(text0);
			}
			ResearchReq researchRequirements = GetResearchRequirements(def);
			if (researchRequirements.always.Count > 0)
			{
				stringBuilder.AppendLine("RL_Ex_Always".Translate(ResearchList(researchRequirements.always)));
			}
			for (int i = 0; i < researchRequirements.options.Count && i < 6; i++)
			{
				stringBuilder.AppendLine("RL_Ex_Option".Translate(ResearchList(researchRequirements.options[i])));
			}
			if (researchRequirements.freeRecipe)
			{
				stringBuilder.AppendLine("RL_Ex_Free".Translate());
			}
			if (researchRequirements.always.Count == 0 && researchRequirements.options.Count == 0)
			{
				stringBuilder.AppendLine("RL_Ex_NoResearch".Translate());
			}
			stringBuilder.AppendLine("RL_Ex_Tech".Translate(def.techLevel.ToStringHuman(), ColonyTech().ToStringHuman()));
			TechLevel techLevel = PowerTech.Estimate(def);
			if (ResearchedLoadoutMod.Settings.powerEstimate && techLevel != 0 && (int)techLevel > (int)def.techLevel)
			{
				stringBuilder.AppendLine("RL_Ex_Power".Translate(techLevel.ToStringHuman()));
			}
			TechLevel techLevel2 = RequiredTech(def, ResearchedLoadoutMod.Settings);
			if ((int)techLevel2 > (int)ColonyTech())
			{
				stringBuilder.AppendLine("RL_Ex_NeedColony".Translate(techLevel2.ToStringHuman()));
			}
			if (def.IsApparel)
			{
				string text;
				switch (ApparelGender.Of(def))
				{
				case Gender.Female:
					text = "RL_GenderFemale".Translate().ToString();
					break;
				case Gender.Male:
					text = "RL_GenderMale".Translate().ToString();
					break;
				default:
					text = "RL_GenderUnisex".Translate().ToString();
					break;
				}
				string text2 = text;
				string text3 = ApparelGender.Reason(def);
				stringBuilder.AppendLine("RL_Ex_Gender".Translate(text2 + ((text3 == null) ? string.Empty : (" (" + text3 + ")"))));
			}
			if (def.modContentPack != null)
			{
				stringBuilder.Append("RL_Ex_Mod".Translate(def.modContentPack.Name));
			}
			return stringBuilder.ToString().TrimEnd(Array.Empty<char>());
		}

		private static string ResearchList(List<ResearchProjectDef> list)
		{
			List<string> list2 = new List<string>();
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i] != null)
				{
					list2.Add(list[i].LabelCap + " (" + ((!list[i].IsFinished) ? "RL_Ex_Missing" : "RL_Ex_Done").Translate() + ")");
				}
			}
			return string.Join(", ", list2.ToArray());
		}

		private static ResearchReq GetResearchRequirements(ThingDef def)
		{
			ResearchReq value;
			if (researchCache.TryGetValue(def, out value))
			{
				return value;
			}
			ResearchReq researchReq = new ResearchReq();
			Add(researchReq.always, def.researchPrerequisites);
			if (def.recipeMaker != null)
			{
				List<ResearchProjectDef> list = new List<ResearchProjectDef>();
				Add(list, def.recipeMaker.researchPrerequisite);
				Add(list, def.recipeMaker.researchPrerequisites);
				AddBenchOptions(researchReq, list, def.recipeMaker.recipeUsers);
			}
			if (ResearchedLoadoutMod.Settings != null && ResearchedLoadoutMod.Settings.useRecipes)
			{
				List<RecipeDef> allDefsListForReading = DefDatabase<RecipeDef>.AllDefsListForReading;
				for (int i = 0; i < allDefsListForReading.Count; i++)
				{
					RecipeDef recipeDef = allDefsListForReading[i];
					if (recipeDef == null || recipeDef.products == null)
					{
						continue;
					}
					bool flag = false;
					for (int j = 0; j < recipeDef.products.Count; j++)
					{
						if (recipeDef.products[j] != null && recipeDef.products[j].thingDef == def)
						{
							flag = true;
							break;
						}
					}
					if (!flag)
					{
						continue;
					}
					List<ResearchProjectDef> list2 = new List<ResearchProjectDef>();
					Add(list2, recipeDef.researchPrerequisite);
					Add(list2, recipeDef.researchPrerequisites);
					List<ThingDef> list3 = new List<ThingDef>();
					foreach (ThingDef allRecipeUser in recipeDef.AllRecipeUsers)
					{
						list3.Add(allRecipeUser);
					}
					AddBenchOptions(researchReq, list2, list3);
				}
			}
			researchCache[def] = researchReq;
			return researchReq;
		}

		private static void AddBenchOptions(ResearchReq req, List<ResearchProjectDef> recipeResearch, List<ThingDef> benches)
		{
			if (benches == null || benches.Count == 0)
			{
				if (recipeResearch.Count > 0)
				{
					req.options.Add(recipeResearch);
				}
				return;
			}
			for (int i = 0; i < benches.Count; i++)
			{
				ThingDef thingDef = benches[i];
				List<ResearchProjectDef> list = new List<ResearchProjectDef>(recipeResearch);
				if (thingDef != null)
				{
					Add(list, thingDef.researchPrerequisites);
				}
				if (list.Count > 0)
				{
					req.options.Add(list);
				}
				else
				{
					req.freeRecipe = true;
				}
			}
		}

		private static void Add(List<ResearchProjectDef> list, ResearchProjectDef project)
		{
			if (project != null && !list.Contains(project))
			{
				list.Add(project);
			}
		}

		private static void Add(List<ResearchProjectDef> list, List<ResearchProjectDef> projects)
		{
			if (projects != null)
			{
				for (int i = 0; i < projects.Count; i++)
				{
					Add(list, projects[i]);
				}
			}
		}

		private static bool IsNonOfficialModItem(ThingDef def)
		{
			if (def.modContentPack == null || def.modContentPack.IsOfficialMod)
			{
				return false;
			}
			string text = def.modContentPack.PackageId ?? string.Empty;
			if (text.IndexOf("ludeon", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return false;
			}
			return true;
		}

		public static bool IsBelowMinHp(Thing thing)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.replaceBrokenGear)
			{
				return false;
			}
			if (thing == null || thing.MaxHitPoints <= 0)
			{
				return false;
			}
			return 100f * (float)thing.HitPoints / (float)thing.MaxHitPoints < (float)settings.minApparelHpPercent;
		}

		public static bool IsProtectedApparel(Pawn pawn, Apparel apparel)
		{
			if (pawn == null || apparel == null)
			{
				return false;
			}
			if (pawn.apparel != null && pawn.apparel.IsLocked(apparel))
			{
				return true;
			}
			if (pawn.outfits != null && pawn.outfits.forcedHandler != null && pawn.outfits.forcedHandler.IsForced(apparel))
			{
				return true;
			}
			return false;
		}

		public static bool IsPersonaWeapon(Thing thing)
		{
			ThingWithComps thingWithComps = thing as ThingWithComps;
			return thingWithComps != null && thingWithComps.GetComp<CompBladelinkWeapon>() != null;
		}
	}
	public class ResearchedLoadoutMapComponent : MapComponent
	{
		private struct CarryRule
		{
			public int maxWeapons;

			public float maxSidearmMass;

			public bool fromSidearmsMod;
		}

		private const int PassInterval = 1250;

		private const int CacheRefreshPasses = 2;

		private const int CooldownAfterAction = 2500;

		private const int CooldownAfterStrip = 600;

		private const int CooldownNothingToDo = 2500;

		private int _ticker;

		private int _cacheAge;

		private List<Thing> _weaponCache;

		private List<Thing> _apparelCache;

		private readonly Dictionary<int, int> _pawnCooldown = new Dictionary<int, int>();

		private readonly HashSet<int> _reservedThings = new HashSet<int>();

		public static int ResearchVersion;

		private int _seenResearchVersion = -1;

		private readonly List<Pawn> _order = new List<Pawn>();

		private readonly Dictionary<int, int> _weaponCalmUntil = new Dictionary<int, int>();

		private int _lastDiag = -999999;

		private int _lastPauseDiag = -999999;

		private string _lastDiagText;

		private static readonly FieldInfo NeededWarmthField = AccessTools.Field(typeof(JobGiver_OptimizeApparel), "neededWarmth");

		public static float Stability
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return (settings == null) ? 0.3f : ((float)Mathf.Clamp(settings.gearStability, 0, 100) / 100f);
			}
		}

		public ResearchedLoadoutMapComponent(Map map)
			: base(map)
		{
		}

		private readonly List<Pawn> _queue = new List<Pawn>();

		public override void MapComponentTick()
		{
			CombatArmor.Tick(map);
			OwnSidearms.Tick(map);
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || (!settings.autoEquipPrimary && !settings.autoEquipSidearm && !settings.autoEquipApparel && !settings.stripLockedGear && !settings.replaceBrokenGear))
			{
				return;
			}
			if (_seenResearchVersion != ResearchVersion)
			{
				_seenResearchVersion = ResearchVersion;
				_pawnCooldown.Clear();
				_weaponCache = null;
				_apparelCache = null;
				_ticker = 1250;
			}
			// 2.15.0: temperature crossed the set range – dress or undress right away, not at the next round
			if (settings.autoEquipApparel && Find.TickManager.TicksGame % 250 == 0)
			{
				TempFastLane(settings);
			}
			MaybeReport(Find.TickManager.TicksGame);
			_ticker++;
			// 2.16.7: the colonists of a round are checked one by one (one every 5 ticks), not all in one tick –
			// with many colonists and much gear on a big map that was a 200+ ms stop every 20 seconds
			if (_queue.Count > 0)
			{
				if (_ticker % 5 == 0)
				{
					Pawn next = _queue[0];
					_queue.RemoveAt(0);
					if (next != null && next.Spawned && next.Map == map && !next.Dead)
						ProcessPawn(next, Find.TickManager.TicksGame, settings);
				}
				return;
			}
			if (_ticker < 1250)
			{
				return;
			}
			_ticker = 0;
			if (settings.pauseDuringThreat && RealThreat() != null)
			{
				DiagPause();
				return;
			}
			_cacheAge++;
			if (_weaponCache == null || _apparelCache == null || _cacheAge >= 2)
			{
				_weaponCache = CollectCandidates(ThingRequestGroup.Weapon);
				_apparelCache = CollectCandidates(ThingRequestGroup.Apparel);
				_cacheAge = 0;
				DiagSummary();
			}
			_reservedThings.Clear();
			int now = ((Find.TickManager != null) ? Find.TickManager.TicksGame : 0);
			List<Pawn> freeColonistsSpawned = map.mapPawns.FreeColonistsSpawned;
			if (freeColonistsSpawned == null || freeColonistsSpawned.Count == 0)
			{
				return;
			}
			for (int i = 0; i < freeColonistsSpawned.Count; i++)
			{
				Pawn pawn = freeColonistsSpawned[i];
				if (pawn == null || pawn.jobs == null || pawn.jobs.jobQueue == null)
				{
					continue;
				}
				for (int j = 0; j < pawn.jobs.jobQueue.Count; j++)
				{
					QueuedJob queuedJob = pawn.jobs.jobQueue[j];
					if (queuedJob != null && queuedJob.job != null && IsGearJob(queuedJob.job.def) && queuedJob.job.targetA.Thing != null)
					{
						_reservedThings.Add(queuedJob.job.targetA.Thing.thingIDNumber);
					}
				}
			}
			_order.Clear();
			_order.AddRange(freeColonistsSpawned);
			if (settings.prioritizeFighters)
			{
				_order.Sort(CompareFighters);
			}
			_queue.Clear();
			_queue.AddRange(_order);
			_order.Clear();
		}

		// 2.15.1 (debug log on): once per game start, a report for every colonist – what the mod would take
		// now and why the rest of the gear on the map is skipped. Zalohy_modu\SCG_report.txt
		private static bool _reported;
		private int _reportAt = -1;

		private void MaybeReport(int now)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (_reported || settings == null || !settings.debugLog)
				return;
			if (_reportAt < 0) { _reportAt = now + 2500; return; }
			if (now < _reportAt)
				return;
			_reported = true;
			try
			{
				var sb = new StringBuilder();
				sb.AppendLine("# SCG report " + DateTime.Now.ToString("s") + " tick " + now + " map " + map.uniqueID);
				sb.AppendLine("weapons on map " + (_weaponCache?.Count ?? -1) + ", apparel on map " + (_apparelCache?.Count ?? -1) + ", onlyFromStockpile=" + settings.onlyFromStockpile + " onlyCrafted=" + settings.onlyCrafted + " ownDresser=" + settings.useOwnDresser + " pauseThreat=" + settings.pauseDuringThreat + " threat=" + (RealThreat() != null));
				var apCache = _apparelCache == null ? null : new List<Thing>(_apparelCache);
				var wpCache = _weaponCache == null ? null : new List<Thing>(_weaponCache);
				foreach (Pawn p in new List<Pawn>(map.mapPawns.FreeColonistsSpawned))
				{
					try {
					sb.AppendLine("== " + p.LabelShort + " (" + p.def.defName + ")");
					_pawnCooldown.TryGetValue(p.thingIDNumber, out int cd);
					sb.AppendLine("  colonist=" + LoadoutFilter.IsPlayerColonist(p) + " exempt=" + ExemptPawns.IsExempt(p) + " drafted=" + p.Drafted + " downed=" + p.Downed + " mental=" + p.InMentalState + " cooldownLeft=" + Math.Max(0, cd - now) + " job=" + (p.CurJob?.def?.defName ?? "-") + (p.CurJob != null && p.CurJob.playerForced ? "(forced)" : "") + " queue=" + (p.jobs?.jobQueue?.Count ?? 0) + " calmApparel=" + ApparelMemory.Calm(p, now) + " violent=" + !p.WorkTagIsDisabled(WorkTags.Violent));
					Thing prim = p.equipment?.Primary;
					sb.AppendLine("  weapon: " + (prim?.def?.defName ?? "none") + " | weaponCalm=" + WeaponCalm(p, prim, now));
					try
					{
						Thing bw = FindBestWeapon(p, null, true);
						sb.AppendLine("  best weapon: " + (bw?.def?.defName ?? "none") + (bw != null ? " upgrade=" + IsUpgrade(p, prim, bw, true) : ""));
					}
					catch (Exception ex) { sb.AppendLine("  best weapon: error " + ex.Message); }
					sb.AppendLine("  wearing: " + string.Join(", ", p.apparel?.WornApparel?.ConvertAll(a => a.def.defName).ToArray() ?? new string[0]));
					try
					{
						Thing ba = FindBestApparel(p, false);
						Thing bb = FindBestApparel(p, true);
						sb.AppendLine("  best apparel: " + (ba?.def?.defName ?? "none") + " | only additions: " + (bb?.def?.defName ?? "none"));
					}
					catch (Exception ex) { sb.AppendLine("  best apparel: error " + ex.Message); }
					var why = new Dictionary<string, int>();
					var ex1 = new Dictionary<string, string>();
					if (apCache != null)
						foreach (Thing t in apCache)
						{
							Apparel a = t as Apparel;
							if (a == null || p.apparel == null) continue;
							string r = ApparelSkip(p, a);
							why.TryGetValue(r, out int n);
							why[r] = n + 1;
							if (!ex1.ContainsKey(r)) ex1[r] = a.def.defName;
						}
					foreach (var kv in why)
						sb.AppendLine("    apparel " + kv.Key + ": " + kv.Value + " (e.g. " + ex1[kv.Key] + ")");
					var wwhy = new Dictionary<string, int>();
					var wex = new Dictionary<string, string>();
					if (wpCache != null)
						foreach (Thing t in wpCache)
						{
							string r = WeaponSkip(p, t);
							wwhy.TryGetValue(r, out int n);
							wwhy[r] = n + 1;
							if (!wex.ContainsKey(r)) wex[r] = t.def.defName;
						}
					foreach (var kv in wwhy)
						sb.AppendLine("    weapon " + kv.Key + ": " + kv.Value + " (e.g. " + wex[kv.Key] + ")");
					sb.AppendLine("  recent: " + GearWhy.Describe(p).Replace("\n", " / "));
					} catch (Exception pex) { sb.AppendLine("  ERROR " + pex); }
				}
				string dir = System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "Zalohy_modu");
				System.IO.Directory.CreateDirectory(dir);
				System.IO.File.WriteAllText(System.IO.Path.Combine(dir, "SCG_report.txt"), sb.ToString());
				MLog.Info("[Smart Colonist Gear] report written: Zalohy_modu\\SCG_report.txt");
			}
			catch (Exception ex) { MLog.Warn("[Smart Colonist Gear] report: " + ex); }
		}

		private string ApparelSkip(Pawn pawn, Apparel apparel)
		{
			if (apparel.Destroyed || !apparel.Spawned) return "gone";
			if (pawn.apparel.WornApparel.Contains(apparel)) return "worn";
			if (_reservedThings.Contains(apparel.thingIDNumber)) return "reserved by queue";
			if (!LoadoutFilter.AllowedFor(pawn, apparel)) return "locked: " + (LoadoutFilter.LockReason(apparel.def) ?? "filter");
			if (apparel.def.apparel == null || !apparel.def.apparel.PawnCanWear(pawn)) return "race/body can't wear";
			if (!ApparelUtility.HasPartsToWear(pawn, apparel.def)) return "no body parts";
			if (!EquipmentUtility.CanEquip(apparel, pawn)) return "CanEquip false";
			bool gap = Coverage.FillsBasicGap(pawn, apparel.def);
			if (!gap && ApparelGender.Mismatch(pawn, apparel.def)) return "gender";
			if (!gap && Specialization.WorkClothingBlocked(pawn, apparel.def)) return "work clothing for another job";
			if (!InAllowedStorage(apparel)) return "not in stockpile";
			if (apparel.IsForbidden(pawn)) return "forbidden";
			if (IsBroken(apparel)) return "too damaged";
			if (PolicyForbidsThing(pawn, apparel)) return "outfit policy";
			if (WouldReplaceProtected(pawn, apparel)) return "would replace protected";
			if (SameKindNotBetter(pawn, apparel)) return "same kind not better";
			if (LoadoutFilter.WouldDowngradeArmor(pawn, apparel)) return "armor downgrade";
			if (!CanWearWithCurrent(pawn, apparel) && !WorthReplacing(pawn, apparel)) return "not worth replacing";
			if (!pawn.CanReserveAndReach(apparel, PathEndMode.ClosestTouch, Danger.Some)) return "can't reach/reserve";
			return "candidate";
		}

		private string WeaponSkip(Pawn pawn, Thing thing)
		{
			if (thing == null || thing.Destroyed || !thing.Spawned || thing.def == null) return "gone";
			if (!LoadoutFilter.IsManagedWeapon(thing.def)) return "not managed";
			if (LoadoutFilter.ToolNotWeapon(thing.def)) return "tool";
			if (_reservedThings.Contains(thing.thingIDNumber)) return "reserved by queue";
			if (!LoadoutFilter.IsAllowed(thing.def) && !EmergencyGear.AllowsThing(pawn, thing)) return "locked: " + (LoadoutFilter.LockReason(thing.def) ?? "filter");
			if (!InAllowedStorage(thing)) return "not in stockpile";
			if (IsBroken(thing)) return "too damaged";
			if (thing.IsForbidden(pawn)) return "forbidden";
			if (!EquipmentUtility.CanEquip(thing, pawn)) return "CanEquip false";
			if (!pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some)) return "can't reach/reserve";
			return "candidate";
		}

		private static int FighterSkill(Pawn pawn)
		{
			return Mathf.Max(SkillLevel(pawn, SkillDefOf.Shooting), SkillLevel(pawn, SkillDefOf.Melee));
		}

		private static int CompareFighters(Pawn a, Pawn b)
		{
			int num = FighterSkill(b).CompareTo(FighterSkill(a));
			return (num == 0) ? a.thingIDNumber.CompareTo(b.thingIDNumber) : num;
		}

		private void TempFastLane(ResearchedLoadoutSettings settings)
		{
			int now = Find.TickManager.TicksGame;
			GuestWardrobe.VisitorTick(map, now);
			List<Pawn> list = map.mapPawns.FreeColonistsSpawned;
			for (int i = 0; i < list.Count; i++)
			{
				Pawn pawn = list[i];
				if (!LoadoutFilter.IsPlayerColonist(pawn) || pawn.Drafted || pawn.Downed)
				{
					continue;
				}
				if (TempRush.Check(pawn, now))
				{
					_pawnCooldown.Remove(pawn.thingIDNumber);
					ProcessPawn(pawn, now, settings);
				}
			}
		}

		private void ProcessPawn(Pawn pawn, int now, ResearchedLoadoutSettings settings)
		{
			int value;
			if (LoadoutFilter.IsPlayerColonist(pawn) && !pawn.Downed && !pawn.InMentalState && pawn.jobs != null && (!pawn.IsQuestLodger() || GuestWardrobe.LodgersOn) && !ExemptPawns.IsExempt(pawn) && !pawn.Drafted && !IsBusyWithWeaponJob(pawn) && (!_pawnCooldown.TryGetValue(pawn.thingIDNumber, out value) || now >= value) && (pawn.CurJob == null || !pawn.CurJob.playerForced) && !TryAct(pawn, now, settings))
			{
				_pawnCooldown[pawn.thingIDNumber] = now + 2500;
			}
		}

		private bool TryAct(Pawn pawn, int now, ResearchedLoadoutSettings settings)
		{
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Tattered"));
			if (settings.replaceBrokenGear && StripOneTattered(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now, 600);
				return true;
			}
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Heat"));
			if (StripForHeat(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now, TempRush.Active(pawn) ? 60 : 600);
				return true;
			}
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Policy"));
			if (settings.replaceBrokenGear && StripOnePolicy(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now, 600);
				return true;
			}
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_TooMany"));
			if (!GuestWardrobe.IsGuest(pawn) && TrimExtraWeapons(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now);
				return true;
			}
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Locked"));
			if (settings.stripLockedGear && !GuestWardrobe.IsGuest(pawn) && StripLockedGear(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now, 600);
				return true;
			}
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Unneeded"));
			if (settings.autoEquipApparel && TakeOffUnneeded(pawn))
			{
				GearWhy.Done(pawn);
				RememberOrder(pawn, null, now, 600);
				return true;
			}
			bool flag = !pawn.WorkTagIsDisabled(WorkTags.Violent) && (!pawn.IsSlave || settings.armSlaves);
			Thing thing = ((pawn.equipment == null) ? null : pawn.equipment.Primary);
			if (WeaponCalm(pawn, thing, now))
			{
				flag = false;
			}
			if (SidearmsCompat.Active)
			{
				GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Weapon"));
				if (flag && TrySidearmsWeapons(pawn, thing, now, settings))
				{
					GearWhy.Done(pawn);
					return true;
				}
			}
			else if (settings.autoEquipPrimary && flag && pawn.equipment != null && !LoadoutFilter.IsPersonaWeapon(thing))
			{
				Thing thing2 = FindBestWeapon(pawn, null, true);
				if (thing2 != null && IsUpgrade(pawn, thing, thing2, true))
				{
					Job job = JobMaker.MakeJob(JobDefOf.Equip, thing2);
					GearWhy.Pending(pawn, (thing != null && LoadoutFilter.ToolNotWeapon(thing.def)) ? GearWhy.Reason("RL_Why_ToolNotWeapon", thing.LabelShort) : GearWhy.Reason("RL_Why_Weapon", thing != null ? "RL_Why_Instead".Translate(thing.LabelShort).ToString() : null));
					if (GiveSoftJob(pawn, job))
					{
						RL.Debug(pawn.LabelShort + " bere " + thing2.def.defName + " misto " + ((thing != null) ? thing.def.defName : "nic"));
						RL.NotifyWeapon(pawn, thing2, thing);
						RememberOrder(pawn, thing2, now);
						return true;
					}
				}
			}
			CarryRule rule = CarryRuleFor(pawn);
			if (!SidearmsCompat.Present && settings.autoEquipSidearm && flag && thing != null && pawn.inventory != null && CountWeapons(pawn) < rule.maxWeapons)
			{
				bool? wantRanged = OppositeIsRanged(thing.def);
				if ((wantRanged.HasValue ? CountKind(pawn, wantRanged.Value) < MaxKind(wantRanged.Value) : !HasInventoryWeapon(pawn, wantRanged)) && (!wantRanged.HasValue || SidearmSkillOk(pawn, wantRanged.Value)) && (wantRanged != true || !IsBrawler(pawn) || !settings.sidearmBrawlerNoRanged))
				{
					Thing thing3 = FindBestWeapon(pawn, wantRanged, false);
					if (thing3 != null && FitsCarryMass(pawn, thing3, rule))
					{
						Job job2 = JobMaker.MakeJob(JobDefOf.TakeInventory, thing3);
						job2.count = 1;
						GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Sidearm"));
						if (GiveSoftJob(pawn, job2))
						{
							RL.NotifySidearm(pawn, thing3);
							RememberOrder(pawn, thing3, now);
							return true;
						}
					}
				}
				// 2.15.1: more of the main kind when the setting allows it
				if (thing.def.IsWeapon && TryExtraOfKind(pawn, thing.def.IsRangedWeapon, now))
				{
					return true;
				}
			}
			if (settings.useOwnDresser && settings.autoEquipApparel)
			{
				bool onlyAdditions = ApparelMemory.Calm(pawn, now) && !LoadoutFilter.FreezingOrOverheating(pawn) && !TempRush.Active(pawn) && !Coverage.NeedsBasicsAny(pawn) && !EmergencyGear.AnyActive(map);
				Thing thing4 = FindBestApparel(pawn, onlyAdditions);
				if (thing4 != null)
				{
					Job job3 = JobMaker.MakeJob(JobDefOf.Wear, thing4);
					string whyKey = LoadoutFilter.FreezingOrOverheating(pawn) ? "RL_Why_Temperature" : Coverage.NeedsBasicsAny(pawn) ? "RL_Why_Basics" : EmergencyGear.AnyActive(map) ? "RL_Why_Emergency" : "RL_Why_Apparel";
					GearWhy.Pending(pawn, GearWhy.Reason(whyKey));
					if (GiveSoftJob(pawn, job3))
					{
						ApparelMemory.NoteWear(pawn, (Apparel)thing4, now);
						RL.Debug(pawn.LabelShort + " oblika " + thing4.def.defName);
						RL.NotifyApparel(pawn, thing4);
						RememberOrder(pawn, thing4, now, TempRush.Active(pawn) ? 60 : ((!Coverage.StillNaked(pawn, thing4.def)) ? 2500 : 180));
						return true;
					}
				}
			}
			GearWhy.Clear();
			return false;
		}

		private static CarryRule CarryRuleFor(Pawn pawn)
		{
			CarryRule result = default(CarryRule);
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			result.maxWeapons = ((settings == null || !settings.autoEquipSidearm) ? 1 : 2);
			result.maxSidearmMass = 0f;
			if (SidearmsCompat.Present)
			{
				result.fromSidearmsMod = true;
				result.maxWeapons = 99;
			}
			else if (settings != null && settings.ownMaxMass > 0f)
			{
				result.maxSidearmMass = settings.ownMaxMass;
			}
			if (settings != null && settings.sidearmMaxTotal > 0)
			{
				result.maxWeapons = Math.Min(result.maxWeapons, settings.sidearmMaxTotal);
			}
			return result;
		}

		public static Thing BestCarried(Pawn pawn, bool ranged)
		{
			Thing result = null;
			float num = float.MinValue;
			Thing thing = ((pawn.equipment == null) ? null : pawn.equipment.Primary);
			if (thing != null && thing.def.IsRangedWeapon == ranged && !LoadoutFilter.IsThrownWeapon(thing.def))
			{
				result = thing;
				num = ScoreWeaponPower(thing) + (LoadoutFilter.NewEraFirst ? LoadoutFilter.EraRank(thing.def) * 1000000f : 0f);
			}
			if (pawn.inventory != null)
			{
				List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
				for (int i = 0; i < innerListForReading.Count; i++)
				{
					Thing thing2 = innerListForReading[i];
					if (thing2 != null && thing2.def != null && LoadoutFilter.IsManagedWeapon(thing2.def) && thing2.def.IsRangedWeapon == ranged)
					{
						float num2 = ScoreWeaponPower(thing2) + (LoadoutFilter.NewEraFirst ? LoadoutFilter.EraRank(thing2.def) * 1000000f : 0f);
						if (num2 > num)
						{
							num = num2;
							result = thing2;
						}
					}
				}
			}
			return result;
		}

		public static bool PrefersRanged(Pawn pawn, Thing primary)
		{
			if (primary != null)
			{
				return primary.def.IsRangedWeapon;
			}
			if (IsBrawler(pawn))
			{
				return false;
			}
			if (pawn.skills == null)
			{
				return true;
			}
			SkillRecord skill = pawn.skills.GetSkill(SkillDefOf.Shooting);
			SkillRecord skill2 = pawn.skills.GetSkill(SkillDefOf.Melee);
			int num = ((skill != null && !skill.TotallyDisabled) ? skill.Level : 0);
			int num2 = ((skill2 != null && !skill2.TotallyDisabled) ? skill2.Level : 0);
			return num >= num2;
		}

		private bool TrySidearmsWeapons(Pawn pawn, Thing primary, int now, ResearchedLoadoutSettings settings)
		{
			if (SidearmsCompat.IsUsingAutotool(pawn))
			{
				return false;
			}
			if (LoadoutFilter.IsPersonaWeapon(primary))
			{
				return false;
			}
			bool flag = PrefersRanged(pawn, primary);
			if (settings.autoEquipPrimary && !SidearmsCompat.IsCurrentWeaponForced(pawn) && TrySidearmsUpgradeType(pawn, flag, true, now))
			{
				return true;
			}
			// 2.16.0: an unarmed colonist whose preferred kind is not available takes the other kind
			// (a melee fighter with no melee weapon around picks up a bow instead of staying unarmed)
			if (settings.autoEquipPrimary && primary == null && !SidearmsCompat.IsCurrentWeaponForced(pawn) && TrySidearmsUpgradeType(pawn, !flag, true, now))
			{
				return true;
			}
			if (settings.autoEquipSidearm && pawn.equipment != null && pawn.equipment.Primary != null && TrySidearmsUpgradeType(pawn, !flag, false, now))
			{
				return true;
			}
			// 2.15.1: a second (third) weapon of the main kind when the setting allows more than one
			if (settings.autoEquipSidearm && pawn.equipment != null && pawn.equipment.Primary != null && TryExtraOfKind(pawn, flag, now))
			{
				return true;
			}
			return false;
		}

		private bool TryExtraOfKind(Pawn pawn, bool ranged, int now)
		{
			if (CountKind(pawn, ranged) >= MaxKind(ranged) || (ranged && IsBrawler(pawn)) || !SidearmSkillOk(pawn, ranged))
			{
				return false;
			}
			Thing thing = FindBestWeapon(pawn, ranged, false);
			if (thing == null || CarriesDef(pawn, thing.def))
			{
				return false;
			}
			Job job;
			if (SidearmsCompat.Present)
			{
				JobDef jd = SidearmsCompat.EquipSecondaryJob;
				if (jd == null || !SidearmsCompat.CanPickup(pawn, thing))
				{
					return false;
				}
				job = JobMaker.MakeJob(jd, thing);
			}
			else
			{
				if (CountWeapons(pawn) >= CarryRuleFor(pawn).maxWeapons || !FitsCarryMass(pawn, thing, CarryRuleFor(pawn)))
				{
					return false;
				}
				job = JobMaker.MakeJob(JobDefOf.TakeInventory, thing);
			}
			job.count = 1;
			GearWhy.Pending(pawn, GearWhy.Reason("RL_Why_Sidearm"));
			if (!GiveSoftJob(pawn, job))
			{
				return false;
			}
			RL.Debug(pawn.LabelShort + " bere dalsi zbran stejneho druhu " + thing.def.defName);
			RL.NotifySidearm(pawn, thing);
			RememberOrder(pawn, thing, now);
			return true;
		}

		// weapons of one kind the colonist has: the one in hand and the carried ones
		public static int CountKind(Pawn pawn, bool ranged)
		{
			int n = 0;
			Thing p = pawn.equipment?.Primary;
			if (p != null && p.def.IsRangedWeapon == ranged && LoadoutFilter.IsRealWeapon(p.def))
			{
				n++;
			}
			if (pawn.inventory != null)
			{
				foreach (Thing t in pawn.inventory.innerContainer.InnerListForReading)
				{
					if (t != null && t.def != null && LoadoutFilter.IsRealWeapon(t.def) && t.def.IsRangedWeapon == ranged)
					{
						n += t.stackCount;
					}
				}
			}
			return n;
		}

		private static bool CarriesDef(Pawn pawn, ThingDef def)
		{
			if (pawn.equipment?.Primary?.def == def)
			{
				return true;
			}
			return pawn.inventory != null && pawn.inventory.innerContainer.InnerListForReading.Any(t => t != null && t.def == def);
		}

		public static int MaxKind(bool ranged)
		{
			ResearchedLoadoutSettings s = ResearchedLoadoutMod.Settings;
			return s == null ? 1 : (ranged ? s.sidearmMaxRanged : s.sidearmMaxMelee);
		}

		private bool TrySidearmsUpgradeType(Pawn pawn, bool ranged, bool mainType, int now)
		{
			ResearchedLoadoutSettings ss = ResearchedLoadoutMod.Settings;
			if (ranged && IsBrawler(pawn) && (mainType || ss.sidearmBrawlerNoRanged))
			{
				return false;
			}
			// 2.9.0: a sidearm of a type the colonist cannot really use is not taken
			if (!mainType && !SidearmSkillOk(pawn, ranged))
			{
				return false;
			}
			Thing thing = FindBestWeapon(pawn, ranged, mainType);
			if (thing == null)
			{
				return false;
			}
			Thing thing3 = ((pawn.equipment == null) ? null : pawn.equipment.Primary);
			bool flag3 = thing3 != null && LoadoutFilter.IsThrownWeapon(thing3.def);
			// 2.6.1: the main-type pass compares against the weapon in hand, not a better copy in the backpack
			Thing thing2 = (mainType && thing3 != null && !flag3 && thing3.def.IsRangedWeapon == ranged) ? thing3 : BestCarried(pawn, ranged);
			if (thing2 != null && thing2 != thing3 && IsPlayerChosen(thing2))
			{
				return false;
			}
			if (thing2 != null && !IsUpgrade(pawn, thing2, thing, true))
			{
				return false;
			}
			if (thing2 != null && LoadoutFilter.IsPersonaWeapon(thing2))
			{
				return false;
			}
			if (thing2 != null && thing2 == thing3)
			{
				Job job = JobMaker.MakeJob(JobDefOf.Equip, thing);
				if (!GiveSoftJob(pawn, job))
				{
					return false;
				}
				RL.Debug(pawn.LabelShort + " (SS) bere " + thing.def.defName + " misto " + thing2.def.defName);
				RL.NotifyWeapon(pawn, thing, thing2);
				RememberOrder(pawn, thing, now);
				return true;
			}
			if (thing3 == null || (flag3 && IsPlayerChosen(thing3)))
			{
				if (thing3 != null)
				{
					return false;
				}
				Job job2 = JobMaker.MakeJob(JobDefOf.Equip, thing);
				if (!GiveSoftJob(pawn, job2))
				{
					return false;
				}
				RL.NotifyWeapon(pawn, thing, null);
				RememberOrder(pawn, thing, now);
				return true;
			}
			JobDef equipSecondaryJob = SidearmsCompat.EquipSecondaryJob;
			if (equipSecondaryJob == null)
			{
				return false;
			}
			if (thing2 != null)
			{
				ThingWithComps thingWithComps = thing2 as ThingWithComps;
				if (thingWithComps == null)
				{
					return false;
				}
				// 2.8.2: swapping a carried sidearm for a better one can be switched off
				if (!mainType && !ResearchedLoadoutMod.Settings.sidearmUpgrade)
				{
					return false;
				}
				bool flag = SidearmsCompat.CanPickup(pawn, thing);
				bool flag2 = thing.GetStatValue(StatDefOf.Mass) <= thing2.GetStatValue(StatDefOf.Mass) + 0.01f;
				if (!flag && !flag2)
				{
					return false;
				}
				Job job3 = JobMaker.MakeJob(equipSecondaryJob, thing);
				job3.count = 1;
				if (flag)
				{
					// 2.6.2: Simple Sidearms accepts the new one – give the job first, drop only when it is given
					if (!GiveSoftJob(pawn, job3))
					{
						return false;
					}
					if (!SidearmsCompat.DropSidearm(pawn, thingWithComps))
					{
						return false;
					}
				}
				else
				{
					// no free slot: the old one has to go first; if the new job is refused, take the old one back
					if (!SidearmsCompat.DropSidearm(pawn, thingWithComps))
					{
						return false;
					}
					if (!GiveSoftJob(pawn, job3))
					{
						Job job5 = JobMaker.MakeJob(equipSecondaryJob, thing2);
						job5.count = 1;
						GiveSoftJob(pawn, job5);
						return false;
					}
				}
				GearStorage.TryQueueStore(pawn, thing2);
				RL.Debug(pawn.LabelShort + " (SS) meni sidearm " + thing2.def.defName + " za " + thing.def.defName);
				RL.NotifyWeapon(pawn, thing, thing2);
				RememberOrder(pawn, thing, now);
				return true;
			}
			if (!SidearmsCompat.CanPickup(pawn, thing))
			{
				return false;
			}
			// 2.15.1: no more of this kind than the setting allows (the one in hand included)
			if (!mainType && CountKind(pawn, ranged) >= MaxKind(ranged))
			{
				return false;
			}
			Job job4 = JobMaker.MakeJob(equipSecondaryJob, thing);
			job4.count = 1;
			if (!GiveSoftJob(pawn, job4))
			{
				return false;
			}
			RL.Debug(pawn.LabelShort + " (SS) bere sidearm " + thing.def.defName);
			RL.NotifySidearm(pawn, thing);
			RememberOrder(pawn, thing, now);
			return true;
		}

		private static bool SidearmSkillOk(Pawn pawn, bool ranged)
		{
			ResearchedLoadoutSettings s = ResearchedLoadoutMod.Settings;
			int min = (s == null) ? 0 : (ranged ? s.sidearmMinShooting : s.sidearmMinMelee);
			if (min <= 0 || pawn.skills == null)
			{
				return true;
			}
			SkillRecord sk = pawn.skills.GetSkill(ranged ? SkillDefOf.Shooting : SkillDefOf.Melee);
			return sk == null || (!sk.TotallyDisabled && sk.Level >= min);
		}

		public static int CountWeapons(Pawn pawn)
		{
			int num = ((pawn.equipment != null && pawn.equipment.Primary != null) ? 1 : 0);
			if (pawn.inventory == null)
			{
				return num;
			}
			List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < innerListForReading.Count; i++)
			{
				Thing thing = innerListForReading[i];
				if (thing != null && thing.def != null && LoadoutFilter.IsManagedWeapon(thing.def))
				{
					num += thing.stackCount;
				}
			}
			return num;
		}

		private static float InventoryWeaponMass(Pawn pawn)
		{
			if (pawn.inventory == null)
			{
				return 0f;
			}
			float num = 0f;
			List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < innerListForReading.Count; i++)
			{
				Thing thing = innerListForReading[i];
				if (thing != null && thing.def != null && LoadoutFilter.IsManagedWeapon(thing.def))
				{
					num += thing.GetStatValue(StatDefOf.Mass) * (float)thing.stackCount;
				}
			}
			return num;
		}

		private static bool FitsCarryMass(Pawn pawn, Thing weapon, CarryRule rule)
		{
			if (rule.maxSidearmMass <= 0f || weapon == null)
			{
				return true;
			}
			return InventoryWeaponMass(pawn) + weapon.GetStatValue(StatDefOf.Mass) <= rule.maxSidearmMass + 0.05f;
		}

		public static bool IsPlayerChosen(Thing thing)
		{
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			return thing != null && researchedLoadoutGameComponent != null && researchedLoadoutGameComponent.playerChosenWeapons.Contains(thing.thingIDNumber);
		}

		private static bool TrimExtraWeapons(Pawn pawn)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.autoEquipSidearm || pawn.inventory == null)
			{
				return false;
			}
			CarryRule carryRule = CarryRuleFor(pawn);
			if (carryRule.fromSidearmsMod && carryRule.maxWeapons >= 99)
			{
				return false;
			}
			bool result = false;
			for (int i = 0; i < 8; i++)
			{
				bool flag = CountWeapons(pawn) > carryRule.maxWeapons;
				bool flag2 = carryRule.maxSidearmMass > 0f && InventoryWeaponMass(pawn) > carryRule.maxSidearmMass + 0.05f;
				if (!flag && !flag2)
				{
					break;
				}
				Thing thing = WorstInventoryWeapon(pawn);
				Thing lastResultingThing;
				if (thing != null)
				{
					SidearmsCompat.Forget(pawn, thing);
				}
				if (thing == null || !pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out lastResultingThing))
				{
					break;
				}
				result = true;
				GearStorage.TryQueueStore(pawn, lastResultingThing);
			}
			return result;
		}

		private static Thing WorstInventoryWeapon(Pawn pawn)
		{
			if (pawn.inventory == null)
			{
				return null;
			}
			Thing result = null;
			float num = float.MaxValue;
			List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < innerListForReading.Count; i++)
			{
				Thing thing = innerListForReading[i];
				if (thing != null && thing.def != null && LoadoutFilter.IsManagedWeapon(thing.def) && !IsPlayerChosen(thing))
				{
					float num2 = ScoreWeapon(pawn, thing);
					if (num2 < num)
					{
						num = num2;
						result = thing;
					}
				}
			}
			return result;
		}

		private static bool StripOneTattered(Pawn pawn)
		{
			if (pawn.apparel == null)
			{
				return false;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			Apparel apparel = null;
			float num = float.MaxValue;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				Apparel apparel2 = wornApparel[i];
				if (apparel2 != null && apparel2.def != null && apparel2.def.apparel != null && apparel2.def.apparel.careIfDamaged && LoadoutFilter.IsBelowMinHp(apparel2) && !LoadoutFilter.IsProtectedApparel(pawn, apparel2))
				{
					float num2 = (float)apparel2.HitPoints / (float)apparel2.MaxHitPoints;
					if (num2 < num)
					{
						apparel = apparel2;
						num = num2;
					}
				}
			}
			if (apparel == null)
			{
				return false;
			}
			return RemoveApparelAndStore(pawn, apparel);
		}

		private static bool StripForHeat(Pawn pawn)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.survivalApparel || pawn.apparel == null)
			{
				return false;
			}
			int cold;
			int heat;
			LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
			if (heat == 0 || cold > 0)
			{
				return false;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			Apparel apparel = null;
			float num = -1f;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				Apparel apparel2 = wornApparel[i];
				if (!LoadoutFilter.IsArmor(apparel2) && !LoadoutFilter.IsProtectedApparel(pawn, apparel2) && !pawn.apparel.IsLocked(apparel2) && (EmergencyGear.CounterMask(apparel2.def) & EmergencyGear.Toxic & EmergencyGear.ActiveMask(pawn.MapHeld)) == 0)
				{
					float statValue = apparel2.GetStatValue(StatDefOf.Insulation_Heat);
					if (statValue < num)
					{
						num = statValue;
						apparel = apparel2;
					}
				}
			}
			return apparel != null && RemoveApparelAndStore(pawn, apparel);
		}

		private static bool RemoveApparelAndStore(Pawn pawn, Apparel apparel)
		{
			// 2.15.0: a guest's own clothes go into the backpack, not into the colony's storage
			if (GuestWardrobe.IsGuest(pawn) && !GuestWardrobe.IsBorrowed(apparel))
			{
				return GuestWardrobe.Stash(pawn, apparel);
			}
			ApparelMemory.NoteRemoved(pawn, apparel.def);
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.storeDroppedGear && GearStorage.CanHaulFor(pawn))
			{
				Job job = JobMaker.MakeJob(JobDefOf.RemoveApparel, apparel);
				job.haulDroppedApparel = true;
				return GiveSoftJob(pawn, job);
			}
			Apparel resultingAp;
			return pawn.apparel.TryDrop(apparel, out resultingAp, pawn.Position, false);
		}

		// 2.14.0: a second gas mask, or a gas mask nobody needs any more, goes back to storage
		private static bool TakeOffUnneeded(Pawn pawn)
		{
			if (pawn.apparel == null || !pawn.Spawned || pawn.Drafted)
			{
				return false;
			}
			List<Apparel> worn = pawn.apparel.WornApparel;
			for (int i = worn.Count - 1; i >= 0; i--)
			{
				Apparel a = worn[i];
				if (a == null || !GearSense.IsMask(a.def) || LoadoutFilter.IsProtectedApparel(pawn, a) || GearSense.Required(pawn, a.def))
				{
					continue;
				}
				string why = GearSense.WeakerDuplicate(pawn, a) ? "druha maska" : null;
				if (why == null && GearSense.UnneededMask(pawn, a) && !EmergencyGear.KeepWorn(pawn, a.def) && !ApparelMemory.JustPutOn(pawn, a.def))
				{
					why = "maska uz neni potreba";
				}
				// 2.16.3: another mod puts it straight back on – then leave it, no tug of war
				if (why != null && MaskTugOfWar.GaveUp(pawn, a.def))
					continue;
				if (why != null && RemoveApparelAndStore(pawn, a))
				{
					MaskTugOfWar.Removed(pawn, a.def);
					RL.Debug(pawn.LabelShort + " sundava " + a.def.defName + " (" + why + ")");
					return true;
				}
			}
			return false;
		}

		private static bool StripOnePolicy(Pawn pawn)
		{
			if (pawn.apparel == null)
			{
				return false;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int num = wornApparel.Count - 1; num >= 0; num--)
			{
				Apparel apparel = wornApparel[num];
				if (apparel != null && PolicyForbidsDef(pawn, apparel.def) && !LoadoutFilter.IsProtectedApparel(pawn, apparel))
				{
					return RemoveApparelAndStore(pawn, apparel);
				}
			}
			return false;
		}

		private static ApparelPolicy PolicyOf(Pawn pawn)
		{
			if (pawn == null || pawn.outfits == null)
			{
				return null;
			}
			return pawn.outfits.CurrentApparelPolicy;
		}

		private static bool PolicyForbidsDef(Pawn pawn, ThingDef def)
		{
			ApparelPolicy apparelPolicy = PolicyOf(pawn);
			if (def == null || apparelPolicy == null || apparelPolicy.filter == null)
			{
				return false;
			}
			return !apparelPolicy.filter.Allows(def);
		}

		private static bool PolicyForbidsThing(Pawn pawn, Thing thing)
		{
			ApparelPolicy apparelPolicy = PolicyOf(pawn);
			if (thing == null || apparelPolicy == null || apparelPolicy.filter == null)
			{
				return false;
			}
			return !apparelPolicy.filter.Allows(thing);
		}

		// 2.9.1: not researched – or researched but never made by the colony (with "only crafted");
		// a weapon you gave the colonist yourself stays
		private static bool WeaponLocked(Thing weapon)
		{
			// 2.15.3: a weapon you equipped yourself is never taken away – not even for missing research
			if (IsPlayerChosen(weapon))
			{
				return false;
			}
			if (!LoadoutFilter.IsResearchAllowed(weapon.def))
			{
				return true;
			}
			ResearchedLoadoutSettings s = ResearchedLoadoutMod.Settings;
			return s != null && s.stripUncraftedWeapons && !IsPlayerChosen(weapon) && !LoadoutFilter.CraftedOk(weapon.def);
		}

		private static bool StripLockedGear(Pawn pawn)
		{
			bool result = false;
			if (pawn.equipment != null)
			{
				List<ThingWithComps> allEquipmentListForReading = pawn.equipment.AllEquipmentListForReading;
				for (int num = allEquipmentListForReading.Count - 1; num >= 0; num--)
				{
					ThingWithComps thingWithComps = allEquipmentListForReading[num];
					if (thingWithComps != null && thingWithComps.def != null && LoadoutFilter.IsRealWeapon(thingWithComps.def) && WeaponLocked(thingWithComps) && !LoadoutFilter.IsPersonaWeapon(thingWithComps) && !EmergencyGear.KeepWorn(pawn, thingWithComps.def))
					{
						SidearmsCompat.Forget(pawn, thingWithComps);
						ThingWithComps resultingEq;
						if (pawn.equipment.TryDropEquipment(thingWithComps, out resultingEq, pawn.Position, false))
						{
							result = true;
							GearStorage.TryQueueStore(pawn, resultingEq);
						}
					}
				}
			}
			if (pawn.apparel != null)
			{
				List<Apparel> wornApparel = pawn.apparel.WornApparel;
				for (int num2 = wornApparel.Count - 1; num2 >= 0; num2--)
				{
					Apparel apparel = wornApparel[num2];
					if (apparel != null && apparel.def != null && !LoadoutFilter.IsResearchAllowed(apparel.def) && !LoadoutFilter.SurvivalKeepWorn(pawn, apparel) && !EmergencyGear.KeepWorn(pawn, apparel.def) && !ApparelMemory.JustPutOn(pawn, apparel.def) && !LoadoutFilter.IsProtectedApparel(pawn, apparel) && RemoveApparelAndStore(pawn, apparel))
					{
						return true;
					}
				}
			}
			if (pawn.inventory != null)
			{
				List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
				for (int num3 = innerListForReading.Count - 1; num3 >= 0; num3--)
				{
					Thing thing = innerListForReading[num3];
					if (thing != null && thing.def != null && LoadoutFilter.IsRealWeapon(thing.def) && WeaponLocked(thing) && !LoadoutFilter.IsPersonaWeapon(thing) && !EmergencyGear.KeepWorn(pawn, thing.def))
					{
						SidearmsCompat.Forget(pawn, thing);
						Thing lastResultingThing;
						if (pawn.inventory.innerContainer.TryDrop(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near, out lastResultingThing))
						{
							result = true;
							GearStorage.TryQueueStore(pawn, lastResultingThing);
						}
					}
				}
			}
			return result;
		}

		private void RememberOrder(Pawn pawn, Thing thing, int now)
		{
			RememberOrder(pawn, thing, now, 2500);
		}

		private bool WeaponCalm(Pawn pawn, Thing primary, int now)
		{
			ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
			if (primary != null && researchedLoadoutGameComponent != null && researchedLoadoutGameComponent.playerChosenWeapons.Contains(primary.thingIDNumber) && !IsBroken(primary))
			{
				return true;
			}
			int value;
			if (!_weaponCalmUntil.TryGetValue(pawn.thingIDNumber, out value))
			{
				// 2.6.2: the calm is not saved – after loading a game give everyone an hour of calm
				// instead of letting all colonists swap weapons at once
				_weaponCalmUntil[pawn.thingIDNumber] = now + 2500;
				return primary != null && !IsBroken(primary);
			}
			if (now >= value)
			{
				return false;
			}
			return primary != null && !IsBroken(primary);
		}

		private void RememberOrder(Pawn pawn, Thing thing, int now, int cooldown)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (thing != null && LoadoutFilter.IsRealWeapon(thing.def) && settings != null && settings.weaponCalmHours > 0)
			{
				_weaponCalmUntil[pawn.thingIDNumber] = now + settings.weaponCalmHours * 2500;
			}
			_pawnCooldown[pawn.thingIDNumber] = now + cooldown;
			if (thing != null)
			{
				_reservedThings.Add(thing.thingIDNumber);
			}
		}

		private static bool IsGearJob(JobDef def)
		{
			return def == JobDefOf.Equip || def == JobDefOf.TakeInventory || def == JobDefOf.Wear || def == JobDefOf.RemoveApparel || (def != null && def == SidearmsCompat.EquipSecondaryJob);
		}

		private static bool IsBusyWithWeaponJob(Pawn pawn)
		{
			if (pawn.CurJob != null && IsGearJob(pawn.CurJob.def))
			{
				return true;
			}
			if (pawn.jobs != null && pawn.jobs.jobQueue != null)
			{
				for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
				{
					QueuedJob queuedJob = pawn.jobs.jobQueue[i];
					if (queuedJob != null && queuedJob.job != null && IsGearJob(queuedJob.job.def))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool GiveSoftJob(Pawn pawn, Job job)
		{
			if (pawn == null || pawn.jobs == null || job == null)
			{
				return false;
			}
			if (pawn.CurJob != null && pawn.CurJob.playerForced)
			{
				return false;
			}
			GearWhy.TagJob(pawn, job);
			if (IsIdleEnough(pawn) || pawn.jobs.jobQueue == null)
			{
				pawn.jobs.StartJob(job, JobCondition.InterruptForced, null, false, true, null, JobTag.Misc);
				return true;
			}
			pawn.jobs.jobQueue.EnqueueLast(job, JobTag.Misc);
			return true;
		}

		internal static bool IsIdleEnough(Pawn pawn)
		{
			if (pawn.CurJob == null)
			{
				return true;
			}
			JobDef def = pawn.CurJob.def;
			return def == JobDefOf.Wait || def == JobDefOf.Wait_Wander || def == JobDefOf.GotoWander;
		}

		private static bool? OppositeIsRanged(ThingDef primary)
		{
			if (primary == null)
			{
				return null;
			}
			return !primary.IsRangedWeapon;
		}

		private static bool HasInventoryWeapon(Pawn pawn, bool? wantRanged)
		{
			if (pawn.inventory == null)
			{
				return false;
			}
			List<Thing> innerListForReading = pawn.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < innerListForReading.Count; i++)
			{
				Thing thing = innerListForReading[i];
				if (thing != null && thing.def != null && LoadoutFilter.IsRealWeapon(thing.def))
				{
					if (!wantRanged.HasValue)
					{
						return true;
					}
					if (thing.def.IsRangedWeapon == wantRanged.Value)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool IsUpgrade(Pawn pawn, Thing current, Thing candidate, bool weapon)
		{
			if (candidate == null || candidate == current)
			{
				return false;
			}
			if (current == null)
			{
				return true;
			}
			if (weapon)
			{
				if (current.def != null && candidate.def != null && current.def.IsRangedWeapon != candidate.def.IsRangedWeapon)
				{
					float num = ScoreWeapon(pawn, candidate);
					float num2 = ScoreWeapon(pawn, current);
					return num > num2 * (1.25f + Stability * 0.5f + DistanceExtra(pawn, candidate));
				}
				if (LoadoutFilter.ToolNotWeapon(current.def) && !LoadoutFilter.ToolNotWeapon(candidate.def))
				{
					return true;
				}
				// 2.15.1: the newer era wins even when the weapon is weaker; an older one never replaces a newer
				// one (unless the one in hand is falling apart)
				if (LoadoutFilter.NewEraFirst && current.def != null && candidate.def != null)
				{
					int ec = LoadoutFilter.EraRank(candidate.def), eh = LoadoutFilter.EraRank(current.def);
					if (ec > eh)
					{
						return !IsBroken(candidate);
					}
					if (ec < eh)
					{
						return IsBroken(current) && !IsBroken(candidate);
					}
				}
				float num3 = ScoreWeaponPower(candidate) * LoadoutFilter.TechMul(candidate.def);
				float num4 = ScoreWeaponPower(current) * LoadoutFilter.TechMul(current.def);
				if (IsBroken(current) && !IsBroken(candidate))
				{
					return num3 >= num4 * 0.9f;
				}
				// 2.16.1: no going back and forth – the kind just put away is not taken again for two days
				// unless the one in hand is falling apart
				if (WeaponSwapMemory.JustDropped(pawn, candidate.def) && !IsBroken(current))
				{
					return false;
				}
				if (current.def == candidate.def && current.Stuff == candidate.Stuff)
				{
					QualityCategory qc;
					QualityCategory qc2;
					if (current.TryGetQuality(out qc) && candidate.TryGetQuality(out qc2))
					{
						// 2.15.1: quality matters less – the same weapon only for a clearly better one (2 levels)
						return (int)qc2 >= (int)qc + 2 && !IsBroken(candidate);
					}
					return false;
				}
				return num3 > num4 * (1f + Mathf.Max(0.15f, Stability) + DistanceExtra(pawn, candidate));
			}
			float num5 = ScoreApparel(pawn, candidate);
			float num6 = ScoreApparel(pawn, current);
			if (IsBroken(current) && !IsBroken(candidate))
			{
				return num5 >= num6 * 0.9f;
			}
			return num5 > num6 * (1.04f + Stability);
		}

		private static float DistanceExtra(Pawn pawn, Thing thing)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.considerDistance || pawn == null || thing == null || !thing.Spawned || !pawn.Spawned || thing.Map != pawn.Map)
			{
				return 0f;
			}
			float num = pawn.Position.DistanceTo(thing.Position);
			return Mathf.Min(0.25f, num / 400f);
		}

		private static float QualityMul(Thing thing)
		{
			if (thing == null)
			{
				return 1f;
			}
			CompQuality compQuality = thing.TryGetComp<CompQuality>();
			// 2.15.1: quality matters less (awful ×0.9 … legendary ×1.2; was ×0.65 … ×2.33)
			float num = ((compQuality != null) ? (0.9f + (float)(int)compQuality.Quality * 0.05f) : 1f);
			return num * HpMul(thing);
		}

		private static float HpMul(Thing thing)
		{
			if (thing == null || thing.MaxHitPoints <= 0)
			{
				return 1f;
			}
			return 0.55f + 0.45f * (float)thing.HitPoints / (float)thing.MaxHitPoints;
		}

		private static bool InAllowedStorage(Thing thing)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.onlyFromStockpile)
			{
				return true;
			}
			if (thing == null || !thing.Spawned || thing.Map == null)
			{
				return false;
			}
			SlotGroup slotGroup = thing.GetSlotGroup();
			return slotGroup != null && slotGroup.parent != null;
		}

		private Thing RealThreat()
		{
			if (!GenHostility.AnyHostileActiveThreatToPlayer(map))
			{
				return null;
			}
			List<Pawn> freeColonistsSpawned = map.mapPawns.FreeColonistsSpawned;
			Area area = ((map.areaManager == null) ? null : map.areaManager.Home);
			foreach (IAttackTarget item in map.attackTargetsCache.TargetsHostileToColony)
			{
				Thing thing = item.Thing;
				if (thing == null || !thing.Spawned || !GenHostility.IsActiveThreatToPlayer(item))
				{
					continue;
				}
				Pawn pawn = thing as Pawn;
				if (pawn != null)
				{
					if (pawn.Downed || pawn.Dead)
					{
						continue;
					}
					DutyDef dutyDef = ((pawn.mindState == null || pawn.mindState.duty == null) ? null : pawn.mindState.duty.def);
					if (dutyDef == DutyDefOf.ExitMapRandom || dutyDef == DutyDefOf.ExitMapBest || dutyDef == DutyDefOf.ExitMapBestAndDefendSelf || dutyDef == DutyDefOf.ExitMapNearDutyTarget)
					{
						continue;
					}
				}
				if (area != null && area[thing.Position])
				{
					return thing;
				}
				for (int i = 0; i < freeColonistsSpawned.Count; i++)
				{
					if (freeColonistsSpawned[i].Position.InHorDistOf(thing.Position, 40f))
					{
						return thing;
					}
				}
			}
			return null;
		}

		private void DiagPause()
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			int ticksGame = Find.TickManager.TicksGame;
			if (settings != null && settings.debugLog && ticksGame - _lastPauseDiag >= 2500)
			{
				_lastPauseDiag = ticksGame;
				Thing thing = RealThreat();
				string text = ((thing == null) ? string.Empty : (thing.LabelShort + " @ " + thing.Position));
				RL.Debug("PAUZA: na mape je aktivni nepritel (" + text + "). Vypni 'Pauza behem utoku', pokud tam zustava trvale.");
			}
		}

		private void DiagSummary()
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			int ticksGame = Find.TickManager.TicksGame;
			if (settings == null || !settings.debugLog || ticksGame - _lastDiag < 2500)
			{
				return;
			}
			_lastDiag = ticksGame;
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			int num5 = 0;
			int num6 = 0;
			foreach (Thing item in _weaponCache)
			{
				if (LoadoutFilter.IsRealWeapon(item.def))
				{
					num++;
					if (LoadoutFilter.IsAllowed(item.def))
					{
						num2++;
					}
					if (InAllowedStorage(item))
					{
						num3++;
					}
				}
			}
			foreach (Thing item2 in _apparelCache)
			{
				num4++;
				if (LoadoutFilter.IsAllowed(item2.def))
				{
					num5++;
				}
				if (InAllowedStorage(item2))
				{
					num6++;
				}
			}
			List<string> list = new List<string>();
			Dictionary<string, int> dictionary = new Dictionary<string, int>();
			foreach (Thing item3 in _weaponCache)
			{
				CountReason(item3, dictionary, list);
			}
			foreach (Thing item4 in _apparelCache)
			{
				CountReason(item4, dictionary, list);
			}
			List<string> list2 = new List<string>();
			foreach (KeyValuePair<string, int> item5 in dictionary)
			{
				list2.Add(item5.Key + " " + item5.Value + "x");
			}
			string text = ((list2.Count <= 0) ? null : ("Duvody zamceni: " + string.Join(", ", list2.ToArray()) + " | priklady: " + string.Join("; ", list.ToArray())));
			string text2 = "Souhrn: uroven kolonie " + LoadoutFilter.ColonyTechPublic().ToStringHuman() + " | zbrane na mape " + num + " (povoleno " + num2 + ", dostupne " + num3 + ") | obleceni " + num4 + " (povoleno " + num5 + ", dostupne " + num6 + ")" + ((!settings.onlyFromStockpile) ? string.Empty : " | jen ze skladu");
			// 2.6.2: compare without the "available" counts, which change with every hauled item
			string text3 = LoadoutFilter.ColonyTechPublic().ToStringHuman() + num + "/" + num2 + "|" + num4 + "/" + num5 + text;
			if (!(text3 == _lastDiagText))
			{
				_lastDiagText = text3;
				if (text != null)
				{
					RL.Debug(text);
				}
				RL.Debug(text2);
			}
		}

		private static void CountReason(Thing t, Dictionary<string, int> reasons, List<string> examples)
		{
			if (t == null || t.def == null || (t.def.IsWeapon && !LoadoutFilter.IsRealWeapon(t.def)))
			{
				return;
			}
			string text = LoadoutFilter.LockReason(t.def);
			if (text != null)
			{
				string key = ((!text.StartsWith("vyspelejsi")) ? text : "vyspelejsi nez kolonie");
				int value;
				reasons.TryGetValue(key, out value);
				reasons[key] = value + 1;
				string example = t.def.defName + " (" + text + ")";
				if (examples.Count < 8 && !examples.Contains(example))
				{
					examples.Add(example);
				}
			}
		}

		private List<Thing> CollectCandidates(ThingRequestGroup group)
		{
			List<Thing> list = new List<Thing>();
			if (map.listerThings == null)
			{
				return list;
			}
			List<Thing> list2 = map.listerThings.ThingsInGroup(group);
			if (list2 == null)
			{
				return list;
			}
			for (int i = 0; i < list2.Count; i++)
			{
				Thing thing = list2[i];
				if (thing != null && thing.Spawned && !thing.Destroyed)
				{
					list.Add(thing);
				}
			}
			return list;
		}

		private Thing FindBestWeapon(Pawn pawn, bool? wantRanged, bool forPrimary)
		{
			Thing best = null;
			float bestScore = float.MinValue;
			List<Thing> list = _weaponCache ?? CollectCandidates(ThingRequestGroup.Weapon);
			for (int i = 0; i < list.Count; i++)
			{
				ConsiderWeapon(pawn, list[i], wantRanged, forPrimary, ref best, ref bestScore);
			}
			return best;
		}

		private void ConsiderWeapon(Pawn pawn, Thing thing, bool? wantRanged, bool forPrimary, ref Thing best, ref float bestScore)
		{
			if (thing != null && !thing.Destroyed && thing.Spawned && thing.def != null && LoadoutFilter.IsManagedWeapon(thing.def) && !LoadoutFilter.ToolNotWeapon(thing.def) && !_reservedThings.Contains(thing.thingIDNumber) && (LoadoutFilter.IsAllowed(thing.def) || EmergencyGear.AllowsThing(pawn, thing)) && InAllowedStorage(thing) && (!wantRanged.HasValue || thing.def.IsRangedWeapon == wantRanged.Value) && (!forPrimary || !thing.def.IsRangedWeapon || !IsBrawler(pawn)) && !IsBroken(thing) && !thing.IsForbidden(pawn) && EquipmentUtility.CanEquip(thing, pawn) && (!thing.def.IsRangedWeapon || !WearsRangedBlockingShield(pawn)))
			{
				float num = ScoreWeapon(pawn, thing);
				// 2.15.1: among weapons of one kind the newest era wins first, then the score
				if (wantRanged.HasValue && LoadoutFilter.NewEraFirst && num > 0f)
				{
					num += LoadoutFilter.EraRank(thing.def) * 1000000f;
				}
				if (num > bestScore && (!forPrimary || !BetterFighterWants(pawn, thing)) && pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some))
				{
					bestScore = num;
					best = thing;
				}
			}
		}

		public static bool WearsRangedBlockingShield(Pawn pawn)
		{
			if (pawn == null || pawn.apparel == null)
			{
				return false;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (IsRangedBlockingShield(wornApparel[i]))
				{
					return true;
				}
			}
			return false;
		}

		public static bool IsRangedBlockingShield(Thing apparel)
		{
			CompShield compShield = ((apparel == null) ? null : apparel.TryGetComp<CompShield>());
			return compShield != null && compShield.Props != null && compShield.Props.blocksRangedWeapons;
		}

		private static int SkillLevel(Pawn pawn, SkillDef skill)
		{
			if (pawn == null || pawn.skills == null || skill == null)
			{
				return 0;
			}
			SkillRecord skill2 = pawn.skills.GetSkill(skill);
			return (skill2 != null && !skill2.TotallyDisabled) ? skill2.Level : 0;
		}

		private static bool MayGetWeapons(Pawn pawn)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (pawn == null || pawn.Dead || pawn.Downed || !pawn.Spawned || pawn.equipment == null)
			{
				return false;
			}
			if (pawn.WorkTagIsDisabled(WorkTags.Violent))
			{
				return false;
			}
			if (pawn.IsSlave && (settings == null || !settings.armSlaves))
			{
				return false;
			}
			if (ExemptPawns.IsExempt(pawn))
			{
				return false;
			}
			return true;
		}

		private bool IsBestOfType(Thing thing)
		{
			if (thing == null || thing.def == null || _weaponCache == null)
			{
				return true;
			}
			float num = ScoreWeaponPower(thing);
			bool isRangedWeapon = thing.def.IsRangedWeapon;
			for (int i = 0; i < _weaponCache.Count; i++)
			{
				Thing thing2 = _weaponCache[i];
				if (thing2 != null && thing2 != thing && thing2.def != null && !thing2.Destroyed && thing2.Spawned && LoadoutFilter.IsRealWeapon(thing2.def) && thing2.def.IsRangedWeapon == isRangedWeapon && LoadoutFilter.IsAllowed(thing2.def) && !IsBroken(thing2) && ScoreWeaponPower(thing2) > num * 1.02f)
				{
					return false;
				}
			}
			return true;
		}

		private bool BetterFighterWants(Pawn pawn, Thing thing)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.prioritizeFighters || !settings.autoEquipPrimary)
			{
				return false;
			}
			bool isRangedWeapon = thing.def.IsRangedWeapon;
			SkillDef skill = ((!isRangedWeapon) ? SkillDefOf.Melee : SkillDefOf.Shooting);
			int num = SkillLevel(pawn, skill);
			List<Pawn> freeColonistsSpawned = map.mapPawns.FreeColonistsSpawned;
			for (int i = 0; i < freeColonistsSpawned.Count; i++)
			{
				Pawn pawn2 = freeColonistsSpawned[i];
				if (pawn2 != null && pawn2 != pawn && MayGetWeapons(pawn2) && SkillLevel(pawn2, skill) >= num + 3 && (!isRangedWeapon || (!IsBrawler(pawn2) && !WearsRangedBlockingShield(pawn2))) && EquipmentUtility.CanEquip(thing, pawn2))
				{
					Thing primary = pawn2.equipment.Primary;
					if (!LoadoutFilter.IsPersonaWeapon(primary) && (!SidearmsCompat.Active || !SidearmsCompat.IsCurrentWeaponForced(pawn2)) && IsUpgrade(pawn2, primary, thing, true) && IsBestOfType(thing))
					{
						return true;
					}
				}
			}
			return false;
		}

		public static bool IsBrawler(Pawn pawn)
		{
			return pawn.story != null && pawn.story.traits != null && pawn.story.traits.HasTrait(TraitDefOf.Brawler);
		}

		private static float ProjectileDamage(ProjectileProperties projectile)
		{
			if (projectile == null)
			{
				return 0f;
			}
			float num = -1f;
			FieldInfo field = typeof(ProjectileProperties).GetField("damageAmountBase", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			if (field != null)
			{
				object value = field.GetValue(projectile);
				if (value != null)
				{
					num = Convert.ToSingle(value);
				}
			}
			if (num < 0f && projectile.damageDef != null)
			{
				num = projectile.damageDef.defaultDamage;
			}
			return (!(num < 0f)) ? num : 0f;
		}

		private static float RangedEffectiveDps(Thing thing, VerbProperties verb)
		{
			if (verb == null || verb.defaultProjectile == null || verb.defaultProjectile.projectile == null)
			{
				return 0f;
			}
			ProjectileProperties projectile = verb.defaultProjectile.projectile;
			float num = ProjectileDamage(projectile);
			if (num <= 0f)
			{
				return 0f;
			}
			int num2 = Mathf.Max(1, verb.burstShotCount);
			float num3 = 1f;
			float num4 = 0.5f;
			float num5 = 0.5f;
			float num6 = 0.5f;
			float value = 0.15f;
			try
			{
				num3 = thing.GetStatValue(StatDefOf.RangedWeapon_Cooldown);
				num4 = thing.GetStatValue(StatDefOf.AccuracyShort);
				num5 = thing.GetStatValue(StatDefOf.AccuracyMedium);
				num6 = thing.GetStatValue(StatDefOf.AccuracyLong);
				value = projectile.GetArmorPenetration(thing);
			}
			catch
			{
			}
			float num7 = Mathf.Max(0.1f, verb.warmupTime + num3 + (float)((num2 - 1) * verb.ticksBetweenBurstShots) / 60f);
			float num8 = num * (float)num2 / num7;
			float range = verb.range;
			float num9 = 0.35f * ((!(range >= 12f)) ? (num4 * range / 12f) : num4) + 0.45f * ((!(range >= 25f)) ? 0f : num5) + 0.2f * ((!(range >= 40f)) ? 0f : num6);
			float num10 = 0.75f + Mathf.Clamp(value, 0f, 1f);
			float num11 = 1f + Mathf.Min(range, 60f) / 150f;
			return num8 * num9 * num10 * num11 * Specialization.SpecialEffectFactor(projectile);
		}

		// 2.6.1: melee DPS without the holder's own melee factors, so the weapon in hand
		// and weapons on the floor are compared on equal terms (no more axe/mace flip-flop)
		private static float MeleeDpsNoWielder(Thing thing)
		{
			float num = 0f;
			float num2 = 0f;
			float num3 = 0f;
			foreach (VerbUtility.VerbPropertiesWithSource item in VerbUtility.GetAllVerbProperties(thing.def.Verbs, thing.def.tools))
			{
				if (item.verbProps == null || !item.verbProps.IsMeleeAttack)
				{
					continue;
				}
				float num4 = item.verbProps.AdjustedMeleeSelectionWeight(item.tool, null, thing, null, false);
				num += num4;
				num2 += num4 * item.verbProps.AdjustedMeleeDamageAmount(item.tool, null, thing, null);
				num3 += num4 * item.verbProps.AdjustedCooldown(item.tool, null, thing);
			}
			if (num <= 0f || num3 <= 0f)
			{
				return 0f;
			}
			return num2 / num3;
		}

		private static float ScoreWeaponPower(Thing thing)
		{
			if (thing == null || thing.def == null)
			{
				return 0f;
			}
			float num = 8f;
			if (thing.def.IsRangedWeapon && thing.def.Verbs != null && thing.def.Verbs.Count > 0)
			{
				float num2 = RangedEffectiveDps(thing, thing.def.Verbs[0]);
				if (num2 > 0f)
				{
					num += num2 * 20f;
					return num * HpMul(thing);
				}
				VerbProperties verbProperties = thing.def.Verbs[0];
				num += verbProperties.range * 2f;
			}
			else
			{
				float num3 = 0f;
				try
				{
					num3 = MeleeDpsNoWielder(thing);
				}
				catch
				{
					num3 = 0f;
				}
				if (num3 > 0f)
				{
					num += num3 * 12f;
					return num * HpMul(thing);
				}
				if (thing.def.tools != null)
				{
					for (int i = 0; i < thing.def.tools.Count; i++)
					{
						Tool tool = thing.def.tools[i];
						if (tool != null)
						{
							num += tool.power * 2f;
						}
					}
				}
			}
			return num * QualityMul(thing);
		}

		private static float ScoreWeapon(Pawn pawn, Thing thing)
		{
			float num = 10f + ScoreWeaponPower(thing) + thing.MarketValue * 0.005f;
			bool isRangedWeapon = thing.def.IsRangedWeapon;
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.useSkills && pawn.skills != null)
			{
				SkillRecord skill = pawn.skills.GetSkill(SkillDefOf.Shooting);
				SkillRecord skill2 = pawn.skills.GetSkill(SkillDefOf.Melee);
				int num2 = ((skill != null && !skill.TotallyDisabled) ? skill.Level : 0);
				int num3 = ((skill2 != null && !skill2.TotallyDisabled) ? skill2.Level : 0);
				num = ((!isRangedWeapon) ? (num + 18f * (float)num3) : (num + 18f * (float)num2));
				if (isRangedWeapon && num2 + 2 < num3)
				{
					num *= 0.45f;
				}
				if (!isRangedWeapon && num3 + 2 < num2)
				{
					num *= 0.45f;
				}
			}
			else
			{
				num += ((!isRangedWeapon) ? 25f : 50f);
			}
			if (isRangedWeapon && IsBrawler(pawn))
			{
				num *= 0.1f;
			}
			// 2.15.0: a work tool in hand is replaced by any real weapon; newer tech is preferred
			if (LoadoutFilter.ToolNotWeapon(thing.def))
			{
				num *= 0.05f;
			}
			num *= LoadoutFilter.TechMul(thing.def);
			return num * Specialization.WeaponFactor(pawn, thing);
		}

		private static bool IsBroken(Thing thing)
		{
			return LoadoutFilter.IsBelowMinHp(thing);
		}

		private Thing FindBestApparel(Pawn pawn, bool onlyAdditions = false)
		{
			if (pawn.apparel == null)
			{
				return null;
			}
			Thing result = null;
			float num = float.MinValue;
			List<Thing> apparelCache = _apparelCache;
			if (apparelCache == null)
			{
				return null;
			}
			List<float> list = VanillaWornScores(pawn);
			for (int i = 0; i < apparelCache.Count; i++)
			{
				Apparel apparel = apparelCache[i] as Apparel;
				if (apparel == null || apparel.Destroyed || !apparel.Spawned || _reservedThings.Contains(apparel.thingIDNumber) || !LoadoutFilter.AllowedFor(pawn, apparel) || (onlyAdditions && !CanWearWithCurrent(pawn, apparel)))
				{
					continue;
				}
				bool flag = Coverage.FillsBasicGap(pawn, apparel.def);
				if ((!flag && ApparelGender.Mismatch(pawn, apparel.def)) || (!flag && Specialization.WorkClothingBlocked(pawn, apparel.def)) || (!flag && ApparelMemory.RecentlyRemoved(pawn, apparel.def) && !LoadoutFilter.FreezingOrOverheating(pawn) && !TempRush.Active(pawn) && !GearSense.MayWearAgain(pawn, apparel)) || !InAllowedStorage(apparel) || apparel.IsForbidden(pawn) || IsBroken(apparel) || PolicyForbidsThing(pawn, apparel) || apparel.def.apparel == null || !apparel.def.apparel.PawnCanWear(pawn) || (IsRangedBlockingShield(apparel) && PrefersRanged(pawn, (pawn.equipment == null) ? null : pawn.equipment.Primary)) || !ApparelUtility.HasPartsToWear(pawn, apparel.def) || !EquipmentUtility.CanEquip(apparel, pawn) || WouldReplaceProtected(pawn, apparel) || SameKindNotBetter(pawn, apparel) || LoadoutFilter.WouldDowngradeArmor(pawn, apparel))
				{
					continue;
				}
				float num2;
				if (list != null)
				{
					num2 = JobGiver_OptimizeApparel.ApparelScoreGain(pawn, apparel, list);
					num2 -= Mathf.Abs(num2) * DistanceExtra(pawn, apparel);
					if (num2 < 0.05f)
					{
						// 2.8.5: a free slot is filled even by a low-scoring piece, if it is fine to wear
						if (!FullBodyAddition(pawn, apparel))
						{
							continue;
						}
						num2 = 0.05f;
					}
				}
				else
				{
					bool flag2 = CanWearWithCurrent(pawn, apparel);
					if (!flag2 && !WorthReplacing(pawn, apparel))
					{
						continue;
					}
					num2 = ScoreApparel(pawn, apparel) + Specialization.WorkClothingBonus(pawn, apparel.def) * 15f;
					num2 += Coverage.GapBonus(pawn, apparel.def) - Coverage.LossPenalty(pawn, apparel.def);
					if (flag2 && num2 <= 0f)
					{
						continue;
					}
				}
				if (num2 > num && pawn.CanReserveAndReach(apparel, PathEndMode.ClosestTouch, Danger.Some))
				{
					num = num2;
					result = apparel;
				}
			}
			return result;
		}

		private static List<float> VanillaWornScores(Pawn pawn)
		{
			if (NeededWarmthField == null || pawn.MapHeld == null)
			{
				return null;
			}
			try
			{
				int cold;
				int heat;
				LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
				NeededWarmth neededWarmth = ((cold > 0) ? NeededWarmth.Warm : ((heat > 0) ? NeededWarmth.Cool : NeededWarmth.Any));
				NeededWarmthField.SetValue(null, neededWarmth);
				List<float> list = new List<float>();
				List<Apparel> wornApparel = pawn.apparel.WornApparel;
				for (int i = 0; i < wornApparel.Count; i++)
				{
					list.Add(JobGiver_OptimizeApparel.ApparelScoreRaw(pawn, wornApparel[i]));
				}
				return list;
			}
			catch
			{
				return null;
			}
		}

		private static bool WouldReplaceProtected(Pawn pawn, Apparel item)
		{
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (LayersConflict(item.def, wornApparel[i].def))
				{
					if (LoadoutFilter.IsProtectedApparel(pawn, wornApparel[i]))
					{
						return true;
					}
					if (BreaksRequirement(pawn, wornApparel[i].def, item.def))
					{
						return true;
					}
				}
			}
			return false;
		}

		private static float ScoreApparel(Pawn pawn, Thing apparel)
		{
			float num = (apparel.GetStatValue(StatDefOf.ArmorRating_Sharp) * 120f + apparel.GetStatValue(StatDefOf.ArmorRating_Blunt) * 70f + apparel.GetStatValue(StatDefOf.ArmorRating_Heat) * 25f) * HpMul(apparel) * LoadoutFilter.TechMul(apparel.def);
			float num2 = 2f + num + TemperatureScore(pawn, apparel) + apparel.MarketValue * 0.003f;
			List<StatModifier> equippedStatOffsets = apparel.def.equippedStatOffsets;
			if (equippedStatOffsets != null)
			{
				num2 += equippedStatOffsets.GetStatOffsetFromList(StatDefOf.MoveSpeed) * 25f;
				num2 += equippedStatOffsets.GetStatOffsetFromList(StatDefOf.WorkSpeedGlobal) * 60f;
			}
			if (num2 < 0.5f)
			{
				num2 = 0.5f;
			}
			Apparel apparel2 = apparel as Apparel;
			if (pawn != null && apparel2 != null)
			{
				if (apparel2.WornByCorpse && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.DeadMansApparel, true))
				{
					num2 = num2 * 0.1f - 5f;
				}
				if (apparel2.Stuff != null && ThingDefOf.Human.race != null && apparel2.Stuff == ThingDefOf.Human.race.leatherDef && (pawn.Ideo == null || !pawn.Ideo.LikesHumanLeatherApparel) && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.HumanLeatherApparelSad, true))
				{
					num2 = num2 * 0.1f - 5f;
				}
				if (num2 > 0f)
				{
					num2 *= RequirementFactor(pawn, apparel.def);
				}
			}
			return num2;
		}

		private static float TemperatureScore(Pawn pawn, Thing apparel)
		{
			float statValue = apparel.GetStatValue(StatDefOf.Insulation_Cold);
			float statValue2 = apparel.GetStatValue(StatDefOf.Insulation_Heat);
			Map map = ((pawn == null) ? null : pawn.MapHeld);
			if (map == null)
			{
				return (statValue + statValue2) * 0.1f;
			}
			float num2;
			try
			{
				float num = GenTemperature.AverageTemperatureAtTileForTwelfth(map.Tile, GenLocalDate.Twelfth(map));
				num2 = (num * 2f + map.mapTemperature.OutdoorTemp) / 3f;
			}
			catch
			{
				num2 = map.mapTemperature.OutdoorTemp;
			}
			float statValueAbstract = pawn.def.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin);
			float statValueAbstract2 = pawn.def.GetStatValueAbstract(StatDefOf.ComfyTemperatureMax);
			float num3 = Mathf.Clamp01((statValueAbstract - num2) / 25f);
			float num4 = Mathf.Clamp01((num2 - statValueAbstract2) / 15f);
			return statValue * (0.1f + 1.2f * num3) + statValue2 * (0.1f + 1.2f * num4);
		}

		private static float RequirementFactor(Pawn pawn, ThingDef def)
		{
			if (pawn.apparel == null || def.apparel == null || def.apparel.bodyPartGroups == null)
			{
				return 1f;
			}
			List<ApparelRequirementWithSource> allRequirements = pawn.apparel.AllRequirements;
			if (allRequirements == null || allRequirements.Count == 0)
			{
				return 1f;
			}
			bool flag = false;
			bool flag2 = false;
			for (int i = 0; i < allRequirements.Count; i++)
			{
				ApparelRequirement requirement = allRequirements[i].requirement;
				if (requirement != null)
				{
					if (requirement.RequiredForPawn(pawn, def))
					{
						flag = true;
					}
					else if (requirement.AllowedForPawn(pawn, def))
					{
						flag2 = true;
					}
				}
			}
			return flag ? 25f : ((!flag2) ? 1f : 10f);
		}

		private static bool BreaksRequirement(Pawn pawn, ThingDef worn, ThingDef incoming)
		{
			if (pawn.apparel == null)
			{
				return false;
			}
			List<ApparelRequirementWithSource> allRequirements = pawn.apparel.AllRequirements;
			if (allRequirements == null)
			{
				return false;
			}
			for (int i = 0; i < allRequirements.Count; i++)
			{
				ApparelRequirement requirement = allRequirements[i].requirement;
				if (requirement != null)
				{
					bool flag = requirement.RequiredForPawn(pawn, worn) || requirement.AllowedForPawn(pawn, worn);
					bool flag2 = requirement.RequiredForPawn(pawn, incoming) || requirement.AllowedForPawn(pawn, incoming);
					if (flag && !flag2)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool LayersConflict(ThingDef aDef, ThingDef bDef)
		{
			if (aDef == null || bDef == null || aDef.apparel == null || bDef.apparel == null)
			{
				return false;
			}
			if (aDef == bDef)
			{
				return true;
			}
			ApparelProperties apparel = aDef.apparel;
			ApparelProperties apparel2 = bDef.apparel;
			if (apparel.layers == null || apparel2.layers == null)
			{
				return false;
			}
			bool flag = false;
			for (int i = 0; i < apparel.layers.Count; i++)
			{
				if (apparel2.layers.Contains(apparel.layers[i]))
				{
					flag = true;
					break;
				}
			}
			if (!flag)
			{
				return false;
			}
			if (apparel.bodyPartGroups == null || apparel2.bodyPartGroups == null)
			{
				return true;
			}
			for (int j = 0; j < apparel.bodyPartGroups.Count; j++)
			{
				if (apparel2.bodyPartGroups.Contains(apparel.bodyPartGroups[j]))
				{
					return true;
				}
			}
			return false;
		}

		private static bool SameKindNotBetter(Pawn pawn, Apparel item)
		{
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				Apparel apparel = wornApparel[i];
				if (apparel.def == item.def && apparel.Stuff == item.Stuff && !IsBroken(apparel))
				{
					QualityCategory qc;
					QualityCategory qc2;
					if (!apparel.TryGetQuality(out qc) || !item.TryGetQuality(out qc2))
					{
						return true;
					}
					if ((int)qc2 < (int)qc + 2)
					{
						return true;
					}
				}
			}
			return false;
		}

		private static bool WorthReplacing(Pawn pawn, Apparel item)
		{
			float num = ScoreApparel(pawn, item);
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			float num2 = float.MaxValue;
			bool flag = false;
			bool flag2 = false;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (LayersConflict(item.def, wornApparel[i].def))
				{
					flag = true;
					if (IsBroken(wornApparel[i]))
					{
						flag2 = true;
					}
					float num3 = ScoreApparel(pawn, wornApparel[i]);
					if (num3 < num2)
					{
						num2 = num3;
					}
				}
			}
			if (flag && flag2 && !IsBroken(item) && num >= num2 * 0.9f)
			{
				return true;
			}
			return flag && num > num2 + Mathf.Abs(num2) * (0.08f + Stability + DistanceExtra(pawn, item)) + 0.01f;
		}

		// 2.8.5: "dress the whole body": the piece only adds (replaces nothing), covers body parts,
		// is not a utility belt item, scores above zero and does not go against the temperature
		private static bool FullBodyAddition(Pawn pawn, Apparel item)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.dressFullBody || pawn == null || pawn.apparel == null || item == null || item.def.apparel == null)
			{
				return false;
			}
			ApparelProperties ap = item.def.apparel;
			if (ap.bodyPartGroups == null || ap.bodyPartGroups.Count == 0 || ap.layers == null || ap.layers.Count == 0)
			{
				return false;
			}
			bool onlyBelt = true;
			for (int i = 0; i < ap.layers.Count; i++)
			{
				if (ap.layers[i] != ApparelLayerDefOf.Belt)
				{
					onlyBelt = false;
				}
			}
			if (onlyBelt || !CanWearWithCurrent(pawn, item))
			{
				return false;
			}
			if (item.WornByCorpse && ThoughtUtility.CanGetThought(pawn, ThoughtDefOf.DeadMansApparel, true))
			{
				return false;
			}
			int cold;
			int heat;
			LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
			float insCold = item.GetStatValue(StatDefOf.Insulation_Cold);
			float insHeat = item.GetStatValue(StatDefOf.Insulation_Heat);
			if (heat > 0 && (insHeat < 0f || (insCold >= 10f && insHeat <= 0f)))
			{
				return false;
			}
			// 2.12.0: extra clothing only when it is not hot; armor pieces of a set are fine
			if (ArmorSet.Hot(pawn) && insCold > 2f && !(ArmorSet.WearsBodyArmor(pawn) && LoadoutFilter.IsArmor(item)))
			{
				return false;
			}
			if (cold > 0 && insCold < 0f)
			{
				return false;
			}
			try
			{
				if (JobGiver_OptimizeApparel.ApparelScoreRaw(pawn, item) <= 0f)
				{
					return false;
				}
			}
			catch
			{
			}
			return true;
		}

		private static bool CanWearWithCurrent(Pawn pawn, Apparel item)
		{
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (LayersConflict(item.def, wornApparel[i].def))
				{
					return false;
				}
			}
			return true;
		}
	}

	// 2.9.6 / 2.6.3: one shared "only crafted" list with the partner mod
	// (Smart Colonist Gear <-> World at Your Level), read by reflection.
	// 2.8.3: list of all gear kinds the colony has; ticked = allowed now, the rest is allowed once crafted
	public sealed class Dialog_CraftedPicker : Window
	{
		private sealed class Row
		{
			public ThingDef def;

			public int count;

			public string label;

			public bool weapon;

			public bool researched;
		}

		private readonly ResearchedLoadoutGameComponent gc;

		private readonly List<Row> rows = new List<Row>();

		private Vector2 scroll;

		private string search = string.Empty;

		private int show;

		public override Vector2 InitialSize
		{
			get
			{
				return new Vector2(680f, 720f);
			}
		}

		public Dialog_CraftedPicker(ResearchedLoadoutGameComponent gc)
		{
			this.gc = gc;
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = true;
			closeOnClickedOutside = false;
			forcePause = true;
			Build();
		}

		private void Build()
		{
			Dictionary<ThingDef, int> counts = new Dictionary<ThingDef, int>();
			try
			{
				if (Find.Maps != null)
				{
					foreach (Map map in Find.Maps)
					{
						if (map == null || !map.IsPlayerHome)
						{
							continue;
						}
						foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
						{
							if (pawn.equipment != null)
							{
								foreach (ThingWithComps eq in pawn.equipment.AllEquipmentListForReading)
								{
									Add(counts, eq);
								}
							}
							if (pawn.apparel != null)
							{
								foreach (Apparel ap in pawn.apparel.WornApparel)
								{
									Add(counts, ap);
								}
							}
							if (pawn.inventory != null && pawn.inventory.innerContainer != null)
							{
								foreach (Thing inv in pawn.inventory.innerContainer)
								{
									Add(counts, inv);
								}
							}
						}
						Area home = map.areaManager != null ? map.areaManager.Home : null;
						AddGroup(counts, map, ThingRequestGroup.Weapon, home);
						AddGroup(counts, map, ThingRequestGroup.Apparel, home);
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] gear list failed: " + ex.Message);
			}
			// kinds already on the list stay visible so they can be unticked
			foreach (string name in gc.crafted)
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
				if (def != null && !counts.ContainsKey(def))
				{
					counts[def] = 0;
				}
			}
			foreach (KeyValuePair<ThingDef, int> kv in counts)
			{
				Row row = new Row();
				row.def = kv.Key;
				row.count = kv.Value;
				row.label = kv.Key.LabelCap.ToString();
				row.weapon = kv.Key.IsWeapon;
				row.researched = LoadoutFilter.IsResearchAllowed(kv.Key);
				rows.Add(row);
			}
			rows.Sort(delegate(Row a, Row b)
			{
				if (a.weapon != b.weapon)
				{
					return a.weapon ? -1 : 1;
				}
				return string.Compare(a.label, b.label, StringComparison.CurrentCultureIgnoreCase);
			});
		}

		private static void AddGroup(Dictionary<ThingDef, int> counts, Map map, ThingRequestGroup group, Area home)
		{
			foreach (Thing t in map.listerThings.ThingsInGroup(group))
			{
				if (t == null || !t.Spawned || t.IsForbidden(Faction.OfPlayer))
				{
					continue;
				}
				if (t.IsInAnyStorage() || (home != null && home[t.Position]))
				{
					Add(counts, t);
				}
			}
		}

		private static void Add(Dictionary<ThingDef, int> counts, Thing t)
		{
			if (t == null || t.def == null || (!t.def.IsWeapon && !t.def.IsApparel))
			{
				return;
			}
			if (t.def.IsCorpse || t is Pawn)
			{
				return;
			}
			int n;
			counts.TryGetValue(t.def, out n);
			counts[t.def] = n + Math.Max(1, t.stackCount);
		}

		private bool IsOn(ThingDef def)
		{
			return gc.crafted.Contains(def.defName) || SharedCrafted.PartnerHas(def.defName);
		}

		private void SetOn(ThingDef def, bool on)
		{
			if (on)
			{
				gc.crafted.Add(def.defName);
			}
			else
			{
				gc.crafted.Remove(def.defName);
				HashSet<string> partner = SharedCrafted.PartnerList();
				if (partner != null)
				{
					partner.Remove(def.defName);
				}
			}
			gc.craftedSeeded = true;
		}

		private bool Visible(Row r)
		{
			if (show == 1 && !r.weapon)
			{
				return false;
			}
			if (show == 2 && r.weapon)
			{
				return false;
			}
			if (!string.IsNullOrEmpty(search) && r.label.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) < 0 && r.def.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) < 0)
			{
				return false;
			}
			return true;
		}

		public override void DoWindowContents(Rect inRect)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "RL_PickTitle".Translate());
			Text.Font = GameFont.Small;
			float y = inRect.y + 36f;
			GUI.color = new Color(0.8f, 0.8f, 0.8f);
			Widgets.Label(new Rect(inRect.x, y, inRect.width, 44f), "RL_PickHelp".Translate());
			GUI.color = Color.white;
			y += 46f;
			search = Widgets.TextField(new Rect(inRect.x, y, 220f, 28f), search ?? string.Empty);
			string[] tabs = new string[3] { "RL_PickAll", "RL_PickWeapons", "RL_PickApparel" };
			float x = inRect.x + 230f;
			for (int i = 0; i < 3; i++)
			{
				Rect b = new Rect(x, y, 100f, 28f);
				if (show == i)
				{
					Widgets.DrawHighlightSelected(b);
				}
				if (Widgets.ButtonText(b, tabs[i].Translate()))
				{
					show = i;
				}
				x += 104f;
			}
			y += 32f;
			if (Widgets.ButtonText(new Rect(inRect.x, y, 160f, 28f), "RL_PickOnAll".Translate()))
			{
				foreach (Row r in rows)
				{
					if (Visible(r))
					{
						SetOn(r.def, true);
					}
				}
				LoadoutFilter.InvalidateResearch();
			}
			if (Widgets.ButtonText(new Rect(inRect.x + 166f, y, 160f, 28f), "RL_PickOffAll".Translate()))
			{
				foreach (Row r in rows)
				{
					if (Visible(r))
					{
						SetOn(r.def, false);
					}
				}
				LoadoutFilter.InvalidateResearch();
			}
			int on = 0;
			foreach (Row r in rows)
			{
				if (IsOn(r.def))
				{
					on++;
				}
			}
			Text.Anchor = TextAnchor.MiddleRight;
			Widgets.Label(new Rect(inRect.x + 330f, y, inRect.width - 330f, 28f), "RL_PickCount".Translate(on, rows.Count));
			Text.Anchor = TextAnchor.UpperLeft;
			y += 34f;
			Rect outer = new Rect(inRect.x, y, inRect.width, inRect.height - y + inRect.y - 50f);
			List<Row> vis = new List<Row>();
			foreach (Row r in rows)
			{
				if (Visible(r))
				{
					vis.Add(r);
				}
			}
			const float rowH = 30f;
			Rect view = new Rect(0f, 0f, outer.width - 16f, Math.Max(vis.Count * rowH, outer.height - 4f));
			Widgets.BeginScrollView(outer, ref scroll, view);
			bool changed = false;
			for (int i = 0; i < vis.Count; i++)
			{
				Row r = vis[i];
				Rect rr = new Rect(0f, i * rowH, view.width, rowH);
				if (rr.yMax < scroll.y || rr.y > scroll.y + outer.height)
				{
					continue;
				}
				if (i % 2 == 1)
				{
					Widgets.DrawLightHighlight(rr);
				}
				Widgets.DrawHighlightIfMouseover(rr);
				Widgets.ThingIcon(new Rect(rr.x + 2f, rr.y + 3f, 24f, 24f), r.def);
				Text.Anchor = TextAnchor.MiddleLeft;
				string label = r.label;
				if (r.count > 0)
				{
					label = label + "  ×" + r.count;
				}
				Widgets.Label(new Rect(rr.x + 32f, rr.y, rr.width - 260f, rowH), label);
				GUI.color = new Color(0.7f, 0.7f, 0.7f);
				string state = !r.researched ? "RL_PickNoResearch".Translate().ToString() : (r.weapon ? "RL_PickWeapon".Translate().ToString() : "RL_PickApparel1".Translate().ToString());
				Widgets.Label(new Rect(rr.xMax - 226f, rr.y, 190f, rowH), state);
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				bool cur = IsOn(r.def);
				bool val = cur;
				Widgets.Checkbox(new Vector2(rr.xMax - 28f, rr.y + 3f), ref val);
				if (val != cur)
				{
					SetOn(r.def, val);
					changed = true;
				}
				TooltipHandler.TipRegion(rr, r.def.defName + (r.def.modContentPack != null ? "  (" + r.def.modContentPack.Name + ")" : string.Empty));
			}
			Widgets.EndScrollView();
			if (changed)
			{
				LoadoutFilter.InvalidateResearch();
			}
		}
	}
	public static class SharedCrafted
	{
		private static bool searched;

		private static Type partnerType;

		private static FieldInfo craftedField;

		private static MethodInfo resetMethod;

		private static object Partner()
		{
			if (!searched)
			{
				searched = true;
				try
				{
					partnerType = AccessTools.TypeByName("ResearchLockedRaidGear.RaidGearGameComponent");
					if (partnerType != null)
					{
						craftedField = AccessTools.Field(partnerType, "crafted");
						resetMethod = AccessTools.Method(partnerType, "ResetCrafted");
					}
				}
				catch
				{
					partnerType = null;
				}
			}
			if (partnerType == null || Current.Game == null || Current.Game.components == null)
			{
				return null;
			}
			List<GameComponent> components = Current.Game.components;
			for (int i = 0; i < components.Count; i++)
			{
				if (components[i] != null && components[i].GetType() == partnerType)
				{
					return components[i];
				}
			}
			return null;
		}

		public static HashSet<string> PartnerList()
		{
			try
			{
				object obj = Partner();
				if (obj == null || craftedField == null)
				{
					return null;
				}
				return craftedField.GetValue(obj) as HashSet<string>;
			}
			catch
			{
				return null;
			}
		}

		public static bool PartnerHas(string defName)
		{
			HashSet<string> hashSet = PartnerList();
			return hashSet != null && hashSet.Contains(defName);
		}

		public static int UnionCount(HashSet<string> own)
		{
			HashSet<string> hashSet = PartnerList();
			if (hashSet == null || hashSet.Count == 0)
			{
				return (own != null) ? own.Count : 0;
			}
			HashSet<string> hashSet2 = new HashSet<string>(hashSet);
			if (own != null)
			{
				hashSet2.UnionWith(own);
			}
			return hashSet2.Count;
		}

		public static void ResetPartner()
		{
			try
			{
				object obj = Partner();
				if (obj != null && resetMethod != null)
				{
					resetMethod.Invoke(obj, new object[0]);
				}
			}
			catch
			{
			}
		}
	}
	public sealed class ResearchedLoadoutGameComponent : GameComponent
	{
		public HashSet<string> exempt = new HashSet<string>();

		public HashSet<int> playerChosenWeapons = new HashSet<int>();

		public HashSet<string> crafted = new HashSet<string>();

		public bool craftedSeeded;

		private List<string> guestData;

		// 2.8.1: bills added by emergency gear: "<bill load id>|<hazard kind>|<map id>"
		public HashSet<string> emergencyBills = new HashSet<string>();

		public ResearchedLoadoutGameComponent(Game game)
		{
			GuestWardrobe.ResetGameState();
		}

		// 2.6.4: per-game static state must not leak from a previously loaded save
		public override void FinalizeInit()
		{
			base.FinalizeInit();
			ApparelMemory.ResetGameState();
			Specialization.ResetGameState();
			WearerFit.ResetGameState();
			ThreatResponse.ResetGameState();
			SteadyThreat.ResetGameState();
			CombatArmor.ResetGameState();
			OwnSidearms.ResetGameState();
			EmergencyGear.ResetGameState();
			LoadoutFilter.ResetGameState();
			ResearchedLoadoutMapComponent.ResearchVersion++;
		}

		public void NoteCrafted(ThingDef def)
		{
			if (def != null && (def.IsWeapon || def.IsApparel) && crafted.Add(def.defName))
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				if (settings != null && settings.enabled && settings.onlyCrafted && settings.debugLog)
				{
					MLog.Info("[Smart Colonist Gear] crafted for the first time: " + def.defName);
				}
			}
		}

		// 2.6.1: forget everything taken over or crafted so far; from now on only what is really crafted counts
		public void ResetCrafted()
		{
			crafted.Clear();
			craftedSeeded = true;
			LoadoutFilter.InvalidateResearch();
			MLog.Info("[Smart Colonist Gear] Only crafted: list cleared, only gear crafted from now on counts.");
		}

		// 2.8.2: re-take all gear kinds the colony has now: worn, equipped, carried and stored on home maps
		public int RestoreCraftedFromColony()
		{
			int before = crafted.Count;
			craftedSeeded = true;
			if (Current.Game == null || Find.Maps == null)
			{
				return 0;
			}
			try
			{
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome)
					{
						continue;
					}
					foreach (Pawn pawn in map.mapPawns.FreeColonistsSpawned)
					{
						if (pawn.equipment != null)
						{
							foreach (ThingWithComps eq in pawn.equipment.AllEquipmentListForReading)
							{
								NoteRestore(eq);
							}
						}
						if (pawn.apparel != null)
						{
							foreach (Apparel ap in pawn.apparel.WornApparel)
							{
								NoteRestore(ap);
							}
						}
						if (pawn.inventory != null && pawn.inventory.innerContainer != null)
						{
							foreach (Thing inv in pawn.inventory.innerContainer)
							{
								NoteRestore(inv);
							}
						}
					}
					foreach (Thing w in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
					{
						if (w != null && w.IsInAnyStorage())
						{
							NoteRestore(w);
						}
					}
					foreach (Thing a in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
					{
						if (a != null && a.IsInAnyStorage())
						{
							NoteRestore(a);
						}
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] crafted restore failed: " + ex.Message);
			}
			LoadoutFilter.InvalidateResearch();
			int added = crafted.Count - before;
			MLog.Info("[Smart Colonist Gear] Only crafted: restored " + added + " gear kinds from the colony.");
			return added;
		}

		private void NoteRestore(Thing t)
		{
			if (t != null && t.def != null && (t.def.IsWeapon || t.def.IsApparel))
			{
				crafted.Add(t.def.defName);
			}
		}

		public void EnsureCraftedSeeded()
		{
			if (craftedSeeded || Current.Game == null || Current.ProgramState != ProgramState.Playing || Find.Maps == null)
			{
				return;
			}
			craftedSeeded = true;
			try
			{
				HashSet<ThingDef> hashSet = new HashSet<ThingDef>();
				foreach (RecipeDef item in DefDatabase<RecipeDef>.AllDefsListForReading)
				{
					if (item == null || item.products == null || !item.AvailableNow)
					{
						continue;
					}
					for (int i = 0; i < item.products.Count; i++)
					{
						if (item.products[i] != null && item.products[i].thingDef != null)
						{
							hashSet.Add(item.products[i].thingDef);
						}
					}
				}
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome)
					{
						continue;
					}
					foreach (Pawn item2 in map.mapPawns.FreeColonistsSpawned)
					{
						if (item2.equipment != null)
						{
							foreach (ThingWithComps item3 in item2.equipment.AllEquipmentListForReading)
							{
								if (item3 != null && hashSet.Contains(item3.def))
								{
									crafted.Add(item3.def.defName);
								}
							}
						}
						if (item2.apparel == null)
						{
							continue;
						}
						foreach (Apparel item4 in item2.apparel.WornApparel)
						{
							if (item4 != null && hashSet.Contains(item4.def))
							{
								crafted.Add(item4.def.defName);
							}
						}
					}
					foreach (Thing item5 in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
					{
						if (item5 != null && item5.IsInAnyStorage() && hashSet.Contains(item5.def))
						{
							crafted.Add(item5.def.defName);
						}
					}
					foreach (Thing item6 in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
					{
						if (item6 != null && item6.IsInAnyStorage() && hashSet.Contains(item6.def))
						{
							crafted.Add(item6.def.defName);
						}
					}
				}
				MLog.Info("[Smart Colonist Gear] Only crafted: took over " + crafted.Count + " gear kinds from the existing game.");
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] crafted seed failed: " + ex.Message);
			}
		}

		public static ResearchedLoadoutGameComponent Get()
		{
			return (Current.Game == null) ? null : Current.Game.GetComponent<ResearchedLoadoutGameComponent>();
		}

		public override void GameComponentTick()
		{
			// 2.8.0: hazard check for emergency gear (cheap, cached per map)
			if (Find.TickManager != null && Find.TickManager.TicksGame % EmergencyGear.CheckIntervalTicks == 113)
			{
				try
				{
					EmergencyGear.Tick();
				}
				catch (Exception ex0)
				{
					MLog.Warn("[Smart Colonist Gear] emergency gear check failed: " + ex0.Message);
				}
			}
			if (Find.TickManager != null && Find.TickManager.TicksGame % 2500 == 1234)
			{
				try
				{
					ThreatResponse.Tick();
				}
				catch (Exception ex)
				{
					MLog.Warn("[Smart Colonist Gear] threat response failed: " + ex.Message);
				}
			}
		}

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref exempt, "researchedLoadoutExempt", LookMode.Value);
			GuestWardrobe.Expose(ref guestData);
			if (exempt == null)
			{
				exempt = new HashSet<string>();
			}
			Scribe_Collections.Look(ref playerChosenWeapons, "researchedLoadoutChosenWeapons", LookMode.Value);
			if (playerChosenWeapons == null)
			{
				playerChosenWeapons = new HashSet<int>();
			}
			Scribe_Collections.Look(ref crafted, "researchedLoadoutCrafted", LookMode.Value);
			if (crafted == null)
			{
				crafted = new HashSet<string>();
			}
			Scribe_Values.Look(ref craftedSeeded, "researchedLoadoutCraftedSeeded", false);
			Scribe_Collections.Look(ref emergencyBills, "researchedLoadoutEmergencyBills", LookMode.Value);
			if (emergencyBills == null)
			{
				emergencyBills = new HashSet<string>();
			}
		}
	}
	public static class ExemptPawns
	{
		private static ResearchedLoadoutGameComponent Comp
		{
			get
			{
				return (Current.Game == null) ? null : Current.Game.GetComponent<ResearchedLoadoutGameComponent>();
			}
		}

		public static bool IsExempt(Pawn pawn)
		{
			ResearchedLoadoutGameComponent comp = Comp;
			return comp != null && pawn != null && comp.exempt.Contains(pawn.GetUniqueLoadID());
		}

		public static void Set(Pawn pawn, bool exempt)
		{
			ResearchedLoadoutGameComponent comp = Comp;
			if (comp != null && pawn != null)
			{
				if (exempt)
				{
					comp.exempt.Add(pawn.GetUniqueLoadID());
				}
				else
				{
					comp.exempt.Remove(pawn.GetUniqueLoadID());
				}
			}
		}
	}
	[HarmonyPatch(typeof(Pawn), "GetGizmos")]
	public static class PawnGizmoPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.enabled && __instance != null && __instance.IsColonistPlayerControlled)
			{
				__result = Append(__result, __instance);
			}
		}

		private static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> gizmos, Pawn pawn)
		{
			foreach (Gizmo gizmo in gizmos)
			{
				yield return gizmo;
			}
			foreach (Gizmo g in OwnSidearms.Gizmos(pawn))
			{
				yield return g;
			}
			yield return new Command_Toggle
			{
				defaultLabel = "RL_GizmoLabel".Translate(),
				defaultDesc = "RL_GizmoDesc".Translate() + "\n\n" + Specialization.Describe(pawn) + "\n\n" + GearWhy.Describe(pawn),
				icon = TexCommand.Attack,
				isActive = () => !ExemptPawns.IsExempt(pawn),
				toggleAction = delegate
				{
					ExemptPawns.Set(pawn, !ExemptPawns.IsExempt(pawn));
				}
			};
		}
	}
	[HarmonyPatch(typeof(ResearchManager), "FinishProject")]
	public static class FinishProjectPatch
	{
		[HarmonyPostfix]
		public static void Postfix()
		{
			LoadoutFilter.InvalidateResearch();
			ResearchedLoadoutMapComponent.ResearchVersion++;
		}
	}
	public static class SidearmsSettings
	{
		private static Type modType;

		private static Type settingsType;

		private static bool typesInit;

		private static bool dirty;

		private static readonly string[] Presets = new string[7] { "Disabled", "LoadoutOnly", "Lite", "Basic", "Advanced", "Excessive", "Brawler" };

		public static bool Available
		{
			get
			{
				return Settings != null;
			}
		}

		private static object Settings
		{
			get
			{
				InitTypes();
				if (modType == null)
				{
					return null;
				}
				try
				{
					PropertyInfo propertyInfo = AccessTools.Property(modType, "Settings");
					return (!(propertyInfo != null)) ? null : propertyInfo.GetValue(null, null);
				}
				catch
				{
					return null;
				}
			}
		}

		private static void InitTypes()
		{
			if (!typesInit)
			{
				typesInit = true;
				modType = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.SimpleSidearms");
				settingsType = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.SimpleSidearms_Settings");
			}
		}

		private static FieldInfo F(string name)
		{
			InitTypes();
			return (!(settingsType != null)) ? null : AccessTools.Field(settingsType, name);
		}

		public static bool GetBool(string name)
		{
			object settings = Settings;
			FieldInfo fieldInfo = F(name);
			return settings != null && fieldInfo != null && (bool)fieldInfo.GetValue(settings);
		}

		public static float GetFloat(string name)
		{
			object settings = Settings;
			FieldInfo fieldInfo = F(name);
			return (settings == null || !(fieldInfo != null)) ? 0f : Convert.ToSingle(fieldInfo.GetValue(settings));
		}

		public static string GetEnum(string name)
		{
			object settings = Settings;
			FieldInfo fieldInfo = F(name);
			return (settings == null || !(fieldInfo != null)) ? string.Empty : fieldInfo.GetValue(settings).ToString();
		}

		private static void Set(string name, object value)
		{
			object settings = Settings;
			FieldInfo fieldInfo = F(name);
			if (settings != null && !(fieldInfo == null))
			{
				if (fieldInfo.FieldType.IsEnum && value is string)
				{
					value = Enum.Parse(fieldInfo.FieldType, (string)value);
				}
				else if (fieldInfo.FieldType == typeof(int))
				{
					value = Convert.ToInt32(value);
				}
				else if (fieldInfo.FieldType == typeof(float))
				{
					value = Convert.ToSingle(value);
				}
				fieldInfo.SetValue(settings, value);
			}
		}

		public static void SetField(string name, object value)
		{
			Set(name, value);
		}

		public static void MarkChanged()
		{
			Changed();
		}

		public static HashSet<ThingDef> GetSet(string name)
		{
			object settings = Settings;
			FieldInfo fieldInfo = F(name);
			if (settings == null || fieldInfo == null)
			{
				return null;
			}
			HashSet<ThingDef> set = fieldInfo.GetValue(settings) as HashSet<ThingDef>;
			if (set == null)
			{
				set = new HashSet<ThingDef>();
				fieldInfo.SetValue(settings, set);
			}
			return set;
		}

		public static void OpenOriginal()
		{
			try
			{
				Mod mod = LoadedModManager.GetMod(modType);
				if (mod != null)
				{
					Find.WindowStack.Add(new Dialog_ModSettings(mod));
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] could not open Simple Sidearms settings: " + ex.Message);
			}
		}

		private static void Changed()
		{
			Set("ActivePreset", "Custom");
			object settings = Settings;
			string[] array = new string[3] { "LimitModeSingle_Match_Cache", "LimitModeSingleMelee_Match_Cache", "LimitModeSingleRanged_Match_Cache" };
			foreach (string name in array)
			{
				FieldInfo fieldInfo = F(name);
				if (settings != null && fieldInfo != null)
				{
					fieldInfo.SetValue(settings, null);
				}
			}
			dirty = true;
		}

		public static void ApplyPreset(string preset)
		{
			object settings = Settings;
			if (settings == null || settingsType == null)
			{
				return;
			}
			try
			{
				MethodInfo methodInfo = AccessTools.Method(settingsType, "ApplyPreset");
				FieldInfo fieldInfo = F("ActivePreset");
				if (!(methodInfo == null) && !(fieldInfo == null))
				{
					methodInfo.Invoke(settings, new object[1] { Enum.Parse(fieldInfo.FieldType, preset) });
					dirty = true;
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms preset failed: " + ex.Message);
			}
		}

		public static void Save()
		{
			if (!dirty || modType == null)
			{
				return;
			}
			dirty = false;
			try
			{
				Mod mod = LoadedModManager.GetMod(modType);
				if (mod != null)
				{
					mod.WriteSettings();
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms settings save failed: " + ex.Message);
			}
		}

		private static void Toggle(Listing_Standard list, string key, string field)
		{
			bool checkOn = GetBool(field);
			bool flag = checkOn;
			string key2 = key + "_Desc";
			list.CheckboxLabeled(key.Translate(), ref checkOn, (!key2.CanTranslate()) ? null : key2.Translate().ToString());
			if (checkOn != flag)
			{
				Set(field, checkOn);
				Changed();
			}
		}

		public static void Draw(Listing_Standard list)
		{
			if (!Available)
			{
				list.Label("RL_SS_Missing".Translate());
				return;
			}
			string @enum = GetEnum("ActivePreset");
			string text = ((!("RL_SS_P_" + @enum).CanTranslate()) ? @enum : ("RL_SS_P_" + @enum).Translate().ToString());
			if (list.ButtonText("RL_SS_Preset".Translate(text)))
			{
				List<FloatMenuOption> list2 = new List<FloatMenuOption>();
				string[] presets = Presets;
				foreach (string text2 in presets)
				{
					string local = text2;
					list2.Add(new FloatMenuOption(("RL_SS_P_" + text2).Translate(), delegate
					{
						ApplyPreset(local);
					}));
				}
				Find.WindowStack.Add(new FloatMenu(list2));
			}
			list.Gap(6f);
			bool @bool = GetBool("SeparateModes");
			int num = ((!@bool) ? ((!(GetEnum("LimitModeAmount") == "Slots")) ? 6 : ((int)GetFloat("LimitModeAmount_Slots"))) : ((!(GetEnum("LimitModeAmountTotal") == "Slots")) ? 3 : ((int)GetFloat("LimitModeAmountTotal_Slots"))));
			int num2 = (int)list.SliderLabeled("RL_SS_Slots".Translate(num), num, 0f, 6f, 0.6f, "RL_SS_Slots_Desc".Translate());
			float num3 = ((!@bool) ? ((GetEnum("LimitModeSingle") == "AbsoluteWeight") ? GetFloat("LimitModeSingle_AbsoluteMass") : ((!(GetEnum("LimitModeSingle") == "RelativeWeight")) ? 0f : (GetFloat("LimitModeSingle_RelativeMass") * 35f))) : ((!(GetEnum("LimitModeSingleRanged") == "None")) ? Mathf.Max(GetFloat("LimitModeSingleRanged_RelativeMass") * 35f, GetFloat("LimitModeSingleRanged_AbsoluteMass")) : 0f));
			num3 = Mathf.Round(num3 * 2f) / 2f;
			float num4 = Mathf.Round(list.SliderLabeled((!(num3 <= 0f)) ? "RL_SS_Mass".Translate(num3.ToString("0.#")).ToString() : "RL_SS_MassNone".Translate().ToString(), num3, 0f, 10f, 0.6f, "RL_SS_Mass_Desc".Translate()) * 2f) / 2f;
			if (num2 != num || Mathf.Abs(num4 - num3) > 0.01f)
			{
				Set("SeparateModes", false);
				Set("LimitModeAmount", "Slots");
				Set("LimitModeAmount_Slots", num2);
				if (num4 <= 0f)
				{
					Set("LimitModeSingle", "None");
				}
				else
				{
					Set("LimitModeSingle", "AbsoluteWeight");
					Set("LimitModeSingle_AbsoluteMass", num4);
				}
				Changed();
			}
			list.Gap(6f);
			Toggle(list, "RL_SS_ToolSwitch", "ToolAutoSwitch");
			Toggle(list, "RL_SS_OptimalMelee", "OptimalMelee");
			Toggle(list, "RL_SS_CQC", "CQCAutoSwitch");
			Toggle(list, "RL_SS_RangedSwitch", "RangedCombatAutoSwitch");
			list.NewColumn();
			Toggle(list, "RL_SS_ReEquip", "ReEquipOutOfCombat");
			Toggle(list, "RL_SS_ReEquipBest", "ReEquipBest");
			Toggle(list, "RL_SS_ReEquipCombat", "ReEquipInCombat");
			Toggle(list, "RL_SS_SkipDangerous", "SkipDangerousWeapons");
			Toggle(list, "RL_SS_SkipEMP", "SkipEMPWeapons");
			Toggle(list, "RL_SS_Caravans", "PreserveInventoryInCaravans");
			Toggle(list, "RL_SS_Alerts", "ShowAlertsMissingSidearm");
			Toggle(list, "RL_SS_Sounds", "PlaySounds");
			float @float = GetFloat("SidearmSpawnChance");
			int num5 = Mathf.RoundToInt(@float * 100f);
			int num6 = (int)list.SliderLabeled("RL_SS_Spawn".Translate(num5), num5, 0f, 100f, 0.6f, "RL_SS_Spawn_Desc".Translate());
			if (num6 != num5)
			{
				Set("SidearmSpawnChance", (float)num6 / 100f);
				Changed();
			}
		}

		// 2.8.2: the Sidearms tab = this mod's own sidearm options on top, the original Simple Sidearms window below
		private static MethodInfo origDraw;

		private static bool origTried;

		public static void DrawTab(Rect r)
		{
			SidearmsPanel.Draw(r);
		}

		private static void CheckS(Listing_Standard l, string key, ref bool value)
		{
			string desc = key + "_Desc";
			l.CheckboxLabeled(key.Translate(), ref value, desc.CanTranslate() ? desc.Translate().ToString() : null);
		}

		private static bool DrawOriginal(Rect r)
		{
			object settings = Settings;
			if (settings == null || settingsType == null)
			{
				return false;
			}
			if (!origTried)
			{
				origTried = true;
				origDraw = AccessTools.Method(settingsType, "DoSettingsWindowContents", new Type[1] { typeof(Rect) });
				if (origDraw == null)
				{
					MLog.Warn("[Smart Colonist Gear] Simple Sidearms settings window not found, using the short version.");
				}
			}
			if (origDraw == null)
			{
				return false;
			}
			// 2.8.4: the original tab switcher does not show inside this window – draw our own tab bar,
			// then draw the original window with its own header clipped away
			FieldInfo tabField = F("ActiveTab");
			if (tabField == null)
			{
				return false;
			}
			string[] tabNames = new string[5] { "Presets", "Automation", "Allowances", "Spawning", "Misc" };
			string cur = tabField.GetValue(settings).ToString();
			float bw = (r.width - 4f * 6f) / 5f;
			for (int i = 0; i < tabNames.Length; i++)
			{
				Rect b = new Rect(r.x + i * (bw + 6f), r.y, bw, 30f);
				bool active = cur == tabNames[i];
				GUI.color = active ? new Color(1f, 0.85f, 0.5f) : Color.white;
				if (Widgets.ButtonText(b, ("RL_SS_Tab_" + tabNames[i]).Translate()) && !active)
				{
					tabField.SetValue(settings, Enum.Parse(tabField.FieldType, tabNames[i]));
				}
				GUI.color = Color.white;
				if (active)
				{
					Widgets.DrawBox(b, 2);
				}
			}
			Rect area = new Rect(r.x, r.y + 38f, r.width, r.height - 38f);
			Widgets.BeginGroup(area);
			bool ok = false;
			try
			{
				origDraw.Invoke(settings, new object[1] { new Rect(0f, -50f, area.width, area.height + 50f) });
				dirty = true;
				ok = true;
			}
			catch (Exception ex0)
			{
				origDraw = null;
				Exception inner0 = ex0.InnerException ?? ex0;
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms settings window failed, using the short version: " + inner0.Message);
			}
			Widgets.EndGroup();
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
			return ok;
			try
			{
				origDraw.Invoke(settings, new object[1] { r });
				dirty = true;
				return true;
			}
			catch (Exception ex)
			{
				origDraw = null;
				Exception inner = ex.InnerException ?? ex;
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms settings window failed, using the short version: " + inner.Message);
				return false;
			}
		}

		public static void PatchHidePage(Harmony harmony)
		{
			InitTypes();
			if (modType == null || harmony == null)
			{
				return;
			}
			try
			{
				MethodInfo methodInfo = AccessTools.Method(modType, "SettingsCategory");
				if (methodInfo != null)
				{
					MethodInfo original = methodInfo;
					HarmonyMethod postfix = new HarmonyMethod(typeof(SidearmsSettings), "CategoryPostfix");
					harmony.Patch(original, null, postfix);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] could not hide Simple Sidearms settings page: " + ex.Message);
			}
		}

		public static void CategoryPostfix(ref string __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.hideSidearmsPage)
			{
				__result = string.Empty;
			}
		}
	}
	// 2.9.0: own sidearm panel. Same settings as Simple Sidearms (it still does the work),
	// but in tabs of this mod: overview with colonists, preset cards, clearer limits with a
	// weapon picker, plus this mod's own sidearm rules.
	public static class SidearmsPanel
	{
		private static int tab;

		private static Vector2 scroll;

		private static float viewHeight = 900f;

		private static string weaponSearch = string.Empty;

		private static List<ThingDef> validWeapons;

		private static readonly string[] TabKeys = new string[7] { "Overview", "Presets", "Automation", "Limits", "Spawning", "Misc", "Own" };

		private static readonly string[] PresetNames = new string[7] { "Basic", "Lite", "Advanced", "Excessive", "Brawler", "LoadoutOnly", "Disabled" };

		private static readonly string[] SingleModes = new string[4] { "None", "RelativeWeight", "AbsoluteWeight", "Selection" };

		private static readonly string[] AmountModes = new string[4] { "None", "RelativeWeight", "AbsoluteWeight", "Slots" };

		private static readonly string[] WeaponModes = new string[4] { "BySkill", "Ranged", "Melee", "ByGenerated" };

		private static readonly string[] FumbleModes = new string[4] { "Never", "InDistress", "InCombat", "Always" };

		private const float Capacity = 35f;

		private static string T(string key)
		{
			return key.Translate().ToString();
		}

		private static string Tip(string key)
		{
			string k = key + "_Desc";
			return k.CanTranslate() ? k.Translate().ToString() : null;
		}

		public static void Draw(Rect r)
		{
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (!SidearmsSettings.Available)
			{
				// without Simple Sidearms only this mod's own rules exist
				Listing_Standard l0 = new Listing_Standard();
				l0.Begin(r);
				l0.End();
				Rect outer0 = new Rect(r.x, r.y, r.width, r.height);
				Rect view0 = new Rect(0f, 0f, outer0.width - 18f, Mathf.Max(viewHeight, outer0.height - 4f));
				Widgets.BeginScrollView(outer0, ref scroll, view0);
				Listing_Standard l1 = new Listing_Standard();
				l1.Begin(new Rect(0f, 0f, view0.width, 100000f));
				l1.Label("RL_SS_Missing".Translate());
				l1.GapLine();
				DrawBuiltIn(l1, st);
				l1.GapLine();
				DrawOwn(l1, st);
				viewHeight = l1.CurHeight + 20f;
				l1.End();
				Widgets.EndScrollView();
				return;
			}
			float bw = (r.width - 6f * 6f) / 7f;
			for (int i = 0; i < TabKeys.Length; i++)
			{
				Rect b = new Rect(r.x + i * (bw + 6f), r.y, bw, 30f);
				bool active = tab == i;
				GUI.color = active ? new Color(1f, 0.85f, 0.5f) : Color.white;
				if (Widgets.ButtonText(b, T("RL_SP_Tab_" + TabKeys[i])))
				{
					tab = i;
					scroll = Vector2.zero;
				}
				GUI.color = Color.white;
				if (active)
				{
					Widgets.DrawBox(b, 2);
				}
			}
			Rect outer = new Rect(r.x, r.y + 38f, r.width, r.height - 38f);
			Rect view = new Rect(0f, 0f, outer.width - 18f, Mathf.Max(viewHeight, outer.height - 4f));
			Widgets.BeginScrollView(outer, ref scroll, view);
			Listing_Standard l = new Listing_Standard();
			l.Begin(new Rect(0f, 0f, view.width, 100000f));
			try
			{
				switch (tab)
				{
				case 0:
					DrawOverview(l, st, view.width);
					break;
				case 1:
					DrawPresets(l);
					break;
				case 2:
					DrawAutomation(l);
					break;
				case 3:
					DrawLimits(l, view.width);
					break;
				case 4:
					DrawSpawning(l);
					break;
				case 5:
					DrawMisc(l);
					break;
				default:
					DrawOwn(l, st);
					break;
				}
			}
			catch (Exception ex)
			{
				l.Label("Error: " + ex.Message);
			}
			viewHeight = l.CurHeight + 20f;
			l.End();
			Widgets.EndScrollView();
			Text.Anchor = TextAnchor.UpperLeft;
			GUI.color = Color.white;
		}

		// ---------------- helpers ----------------
		private static void Header(Listing_Standard l, string key)
		{
			Text.Font = GameFont.Medium;
			l.Label(T(key));
			Text.Font = GameFont.Small;
			l.GapLine(6f);
		}

		private static void Hint(Listing_Standard l, string text)
		{
			GUI.color = new Color(0.72f, 0.72f, 0.72f);
			l.Label(text);
			GUI.color = Color.white;
		}

		private static void Toggle(Listing_Standard l, string field)
		{
			string key = "RL_SP_" + field;
			bool v = SidearmsSettings.GetBool(field);
			bool old = v;
			l.CheckboxLabeled(T(key), ref v, Tip(key));
			if (v != old)
			{
				SidearmsSettings.SetField(field, v);
				SidearmsSettings.MarkChanged();
			}
		}

		private static void EnumRow(Listing_Standard l, string labelKey, string field, string[] names, string prefix)
		{
			string cur = SidearmsSettings.GetEnum(field);
			string tipL = Tip(labelKey);
			if (tipL != null)
			{
				l.Label(T(labelKey), -1f, new TipSignal?(new TipSignal(tipL)));
			}
			else
			{
				l.Label(T(labelKey));
			}
			Rect row = l.GetRect(30f);
			float w = (row.width - (names.Length - 1) * 4f) / names.Length;
			for (int i = 0; i < names.Length; i++)
			{
				Rect b = new Rect(row.x + i * (w + 4f), row.y, w, 28f);
				bool on = cur == names[i];
				GUI.color = on ? new Color(0.55f, 1f, 0.55f) : Color.white;
				string k = prefix + names[i];
				if (Widgets.ButtonText(b, T(k)) && !on)
				{
					SidearmsSettings.SetField(field, names[i]);
					SidearmsSettings.MarkChanged();
				}
				GUI.color = Color.white;
				string tip = Tip(k);
				if (tip != null)
				{
					TooltipHandler.TipRegion(b, tip);
				}
			}
			l.Gap(4f);
		}

		private static void Percent(Listing_Standard l, string key, string field, float min, float max)
		{
			float v = SidearmsSettings.GetFloat(field);
			int pct = Mathf.RoundToInt(v * 100f);
			int n = Mathf.RoundToInt(l.SliderLabeled(key.Translate(pct), pct, min * 100f, max * 100f, 0.6f, Tip(key)));
			if (n != pct)
			{
				SidearmsSettings.SetField(field, n / 100f);
				SidearmsSettings.MarkChanged();
			}
		}

		private static void Kg(Listing_Standard l, string key, string field, float max, bool relative)
		{
			float v = SidearmsSettings.GetFloat(field);
			float kg = relative ? v * Capacity : v;
			kg = Mathf.Round(kg * 10f) / 10f;
			float n = Mathf.Round(l.SliderLabeled(key.Translate(kg.ToString("0.0")), kg, 0f, max, 0.6f, Tip(key)) * 10f) / 10f;
			if (Mathf.Abs(n - kg) > 0.04f)
			{
				SidearmsSettings.SetField(field, relative ? n / Capacity : n);
				SidearmsSettings.MarkChanged();
			}
		}

		private static void Slots(Listing_Standard l, string field)
		{
			int v = Mathf.RoundToInt(SidearmsSettings.GetFloat(field));
			int n = Mathf.RoundToInt(l.SliderLabeled("RL_SP_SlotsN".Translate(v), v, 0f, 10f, 0.6f, Tip("RL_SP_SlotsN")));
			if (n != v)
			{
				SidearmsSettings.SetField(field, n);
				SidearmsSettings.MarkChanged();
			}
		}

		// ---------------- overview ----------------
		private static void DrawOverview(Listing_Standard l, ResearchedLoadoutSettings st, float width)
		{
			Header(l, "RL_SP_Tab_Overview");
			string preset = SidearmsSettings.GetEnum("ActivePreset");
			string pk = "RL_SS_P_" + preset;
			l.Label("RL_SP_NowPreset".Translate(pk.CanTranslate() ? T(pk) : preset));
			l.Label(LimitsSummary());
			int auto = 0;
			string[] autos = new string[5] { "ToolAutoSwitch", "OptimalMelee", "CQCAutoSwitch", "RangedCombatAutoSwitch", "ReEquipOutOfCombat" };
			for (int i = 0; i < autos.Length; i++)
			{
				if (SidearmsSettings.GetBool(autos[i]))
				{
					auto++;
				}
			}
			l.Label("RL_SP_NowAuto".Translate(auto, autos.Length));
			l.Label("RL_SP_NowOwn".Translate(st.autoEquipSidearm ? T("RL_SP_On") : T("RL_SP_Off"), st.sidearmFilter ? T("RL_SP_On") : T("RL_SP_Off"), st.sidearmUpgrade ? T("RL_SP_On") : T("RL_SP_Off")));
			l.Gap(6f);
			Rect qb = l.GetRect(28f);
			float w = (qb.width - 3f * 6f) / 4f;
			string[] quick = new string[4] { "Basic", "Lite", "Advanced", "Brawler" };
			for (int i = 0; i < quick.Length; i++)
			{
				if (Widgets.ButtonText(new Rect(qb.x + i * (w + 6f), qb.y, w, 28f), "RL_SP_Use".Translate(T("RL_SS_P_" + quick[i]))))
				{
					SidearmsSettings.ApplyPreset(quick[i]);
				}
			}
			l.GapLine();
			if (Current.Game == null || Find.CurrentMap == null)
			{
				Hint(l, T("RL_SP_NoGame"));
				return;
			}
			Text.Font = GameFont.Medium;
			l.Label(T("RL_SP_Colonists"));
			Text.Font = GameFont.Small;
			Rect head = l.GetRect(24f);
			GUI.color = new Color(1f, 0.85f, 0.5f);
			Widgets.Label(new Rect(head.x, head.y, width * 0.2f, 24f), T("RL_SP_ColName"));
			Widgets.Label(new Rect(head.x + width * 0.2f, head.y, width * 0.25f, 24f), T("RL_SP_ColPrimary"));
			Widgets.Label(new Rect(head.x + width * 0.45f, head.y, width * 0.42f, 24f), T("RL_SP_ColSide"));
			Widgets.Label(new Rect(head.x + width * 0.87f, head.y, width * 0.13f, 24f), T("RL_SP_ColMass"));
			GUI.color = Color.white;
			int row = 0;
			foreach (Pawn p in Find.CurrentMap.mapPawns.FreeColonistsSpawned)
			{
				Rect rr = l.GetRect(24f);
				if (row % 2 == 1)
				{
					Widgets.DrawLightHighlight(rr);
				}
				row++;
				string prim = (p.equipment != null && p.equipment.Primary != null) ? p.equipment.Primary.LabelCapNoCount : "–";
				string side = string.Empty;
				float mass = 0f;
				int count = 0;
				if (p.inventory != null)
				{
					foreach (Thing t in p.inventory.innerContainer)
					{
						if (t != null && t.def.IsWeapon && !t.def.IsApparel && t.def.equipmentType == EquipmentType.Primary)
						{
							side = (side.Length == 0) ? t.LabelCapNoCount : (side + ", " + t.LabelCapNoCount);
							mass += t.GetStatValue(StatDefOf.Mass) * t.stackCount;
							count++;
						}
					}
				}
				if (p.WorkTagIsDisabled(WorkTags.Violent))
				{
					side = T("RL_SP_NonViolent");
				}
				Widgets.Label(new Rect(rr.x, rr.y, width * 0.2f, 24f), p.LabelShortCap);
				Widgets.Label(new Rect(rr.x + width * 0.2f, rr.y, width * 0.25f, 24f), prim.Truncate(width * 0.24f));
				Widgets.Label(new Rect(rr.x + width * 0.45f, rr.y, width * 0.42f, 24f), (count == 0 && side.Length == 0) ? "–" : side.Truncate(width * 0.41f));
				Widgets.Label(new Rect(rr.x + width * 0.87f, rr.y, width * 0.13f, 24f), mass.ToString("0.0") + " kg");
				TooltipHandler.TipRegion(rr, side);
			}
		}

		private static string ModeText(string single, string singleField, string amount, string amountField, bool relSingleField)
		{
			string a;
			switch (single)
			{
			case "RelativeWeight":
				a = "RL_SP_SumSingleKg".Translate((SidearmsSettings.GetFloat(singleField + "_RelativeMass") * Capacity).ToString("0.0"));
				break;
			case "AbsoluteWeight":
				a = "RL_SP_SumSingleKg".Translate(SidearmsSettings.GetFloat(singleField + "_AbsoluteMass").ToString("0.0"));
				break;
			case "Selection":
				a = T("RL_SP_SumSingleSel");
				break;
			default:
				a = T("RL_SP_SumSingleAny");
				break;
			}
			string b;
			switch (amount)
			{
			case "RelativeWeight":
				b = "RL_SP_SumAmountKg".Translate((SidearmsSettings.GetFloat(amountField + "_RelativeMass") * Capacity).ToString("0.0"));
				break;
			case "AbsoluteWeight":
				b = "RL_SP_SumAmountKg".Translate(SidearmsSettings.GetFloat(amountField + "_AbsoluteMass").ToString("0.0"));
				break;
			case "Slots":
				b = "RL_SP_SumAmountN".Translate(Mathf.RoundToInt(SidearmsSettings.GetFloat(amountField + "_Slots")));
				break;
			default:
				b = T("RL_SP_SumAmountAny");
				break;
			}
			return a + ", " + b;
		}

		private static string LimitsSummary()
		{
			if (!SidearmsSettings.GetBool("SeparateModes"))
			{
				return "RL_SP_SumCommon".Translate(ModeText(SidearmsSettings.GetEnum("LimitModeSingle"), "LimitModeSingle", SidearmsSettings.GetEnum("LimitModeAmount"), "LimitModeAmount", true));
			}
			string melee = ModeText(SidearmsSettings.GetEnum("LimitModeSingleMelee"), "LimitModeSingleMelee", SidearmsSettings.GetEnum("LimitModeAmountMelee"), "LimitModeAmountMelee", true);
			string ranged = ModeText(SidearmsSettings.GetEnum("LimitModeSingleRanged"), "LimitModeSingleRanged", SidearmsSettings.GetEnum("LimitModeAmountRanged"), "LimitModeAmountRanged", true);
			return "RL_SP_SumSeparate".Translate(melee, ranged);
		}

		// ---------------- presets ----------------
		private static void DrawPresets(Listing_Standard l)
		{
			Header(l, "RL_SP_Tab_Presets");
			Hint(l, T("RL_SP_PresetsHint"));
			string cur = SidearmsSettings.GetEnum("ActivePreset");
			for (int i = 0; i < PresetNames.Length; i++)
			{
				string p = PresetNames[i];
				string desc = ("RL_SS_PD_" + p).Translate().ToString();
				float h = Mathf.Max(56f, Text.CalcHeight(desc, l.ColumnWidth - 60f) + 32f);
				Rect card = l.GetRect(h);
				bool on = cur == p;
				Widgets.DrawMenuSection(card);
				if (on)
				{
					Widgets.DrawBoxSolid(card.ContractedBy(1f), new Color(0.3f, 0.45f, 0.25f, 0.35f));
				}
				Widgets.DrawHighlightIfMouseover(card);
				Rect inner = card.ContractedBy(8f);
				Text.Font = GameFont.Small;
				GUI.color = on ? new Color(0.6f, 1f, 0.6f) : new Color(1f, 0.85f, 0.5f);
				Widgets.Label(new Rect(inner.x, inner.y, inner.width - 40f, 24f), T("RL_SS_P_" + p) + (p == "Basic" ? "  ★" : string.Empty));
				GUI.color = Color.white;
				Widgets.Label(new Rect(inner.x, inner.y + 22f, inner.width - 40f, inner.height - 22f), desc);
				Widgets.RadioButton(new Vector2(inner.xMax - 26f, inner.y + 2f), on);
				if (Widgets.ButtonInvisible(card) && !on)
				{
					SidearmsSettings.ApplyPreset(p);
				}
				l.Gap(4f);
			}
			if (cur == "Custom")
			{
				Hint(l, T("RL_SP_CustomNow"));
			}
		}

		// ---------------- automation ----------------
		private static void DrawAutomation(Listing_Standard l)
		{
			Header(l, "RL_SP_Tab_Automation");
			Toggle(l, "ToolAutoSwitch");
			Toggle(l, "OptimalMelee");
			Toggle(l, "CQCAutoSwitch");
			if (SidearmsSettings.GetBool("CQCAutoSwitch"))
			{
				Toggle(l, "CQCTargetOnly");
			}
			Toggle(l, "RangedCombatAutoSwitch");
			if (SidearmsSettings.GetBool("RangedCombatAutoSwitch"))
			{
				Percent(l, "RL_SP_MaxWarmup", "RangedCombatAutoSwitchMaxWarmup", 0f, 1f);
			}
			l.GapLine();
			Hint(l, T("RL_SP_BiasHint"));
			Percent(l, "RL_SP_BiasMelee", "SpeedSelectionBiasMelee", 0.5f, 2f);
			Percent(l, "RL_SP_BiasRanged", "SpeedSelectionBiasRanged", 0.5f, 2f);
			l.GapLine();
			Toggle(l, "ReEquipOutOfCombat");
			if (SidearmsSettings.GetBool("ReEquipOutOfCombat"))
			{
				Toggle(l, "ReEquipBest");
				Toggle(l, "ReEquipInCombat");
			}
			Toggle(l, "SkipDangerousWeapons");
			Toggle(l, "SkipEMPWeapons");
		}

		// ---------------- limits ----------------
		private static void DrawLimits(Listing_Standard l, float width)
		{
			Header(l, "RL_SP_Tab_Limits");
			Hint(l, LimitsSummary());
			Toggle(l, "AllowBlockedWeaponUse");
			Toggle(l, "SeparateModes");
			l.GapLine();
			if (!SidearmsSettings.GetBool("SeparateModes"))
			{
				LimitBlock(l, "", "Both", width);
			}
			else
			{
				Text.Font = GameFont.Medium;
				l.Label(T("RL_SP_Melee"));
				Text.Font = GameFont.Small;
				LimitBlock(l, "Melee", "Melee", width);
				l.GapLine();
				Text.Font = GameFont.Medium;
				l.Label(T("RL_SP_Ranged"));
				Text.Font = GameFont.Small;
				LimitBlock(l, "Ranged", "Ranged", width);
				l.GapLine();
				EnumRow(l, "RL_SP_TotalLimit", "LimitModeAmountTotal", AmountModes, "RL_SP_EA_");
				AmountSlider(l, "LimitModeAmountTotal");
			}
			l.GapLine();
			Hint(l, T("RL_SP_LimitsHint"));
		}

		private static void LimitBlock(Listing_Standard l, string suffix, string kind, float width)
		{
			string single = "LimitModeSingle" + suffix;
			string amount = "LimitModeAmount" + suffix;
			EnumRow(l, "RL_SP_SingleLimit", single, SingleModes, "RL_SP_ES_");
			string sm = SidearmsSettings.GetEnum(single);
			if (sm == "RelativeWeight")
			{
				Kg(l, "RL_SP_SingleKgRel", single + "_RelativeMass", 15f, true);
			}
			else if (sm == "AbsoluteWeight")
			{
				Kg(l, "RL_SP_SingleKg", single + "_AbsoluteMass", 15f, false);
			}
			else if (sm == "Selection")
			{
				WeaponPicker(l, single + "_Selection", kind, width);
			}
			EnumRow(l, "RL_SP_AmountLimit", amount, AmountModes, "RL_SP_EA_");
			AmountSlider(l, amount);
		}

		private static void AmountSlider(Listing_Standard l, string amount)
		{
			string am = SidearmsSettings.GetEnum(amount);
			if (am == "RelativeWeight")
			{
				Kg(l, "RL_SP_AmountKgRel", amount + "_RelativeMass", 35f, true);
			}
			else if (am == "AbsoluteWeight")
			{
				Kg(l, "RL_SP_AmountKg", amount + "_AbsoluteMass", 35f, false);
			}
			else if (am == "Slots")
			{
				Slots(l, amount + "_Slots");
			}
		}

		private static List<ThingDef> ValidWeapons()
		{
			if (validWeapons != null)
			{
				return validWeapons;
			}
			List<ThingDef> list = new List<ThingDef>();
			try
			{
				Type gf = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.Utilities.GettersFilters");
				MethodInfo m = (gf == null) ? null : AccessTools.Method(gf, "getValidWeaponsThingDefsOnly");
				IEnumerable<ThingDef> e = (m == null) ? null : m.Invoke(null, new object[0]) as IEnumerable<ThingDef>;
				if (e != null)
				{
					list.AddRange(e);
				}
			}
			catch
			{
			}
			if (list.Count == 0)
			{
				foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
				{
					if (d != null && d.IsWeapon && !d.IsApparel && d.equipmentType == EquipmentType.Primary && !d.destroyOnDrop && d.weaponTags != null && d.weaponTags.Count > 0)
					{
						list.Add(d);
					}
				}
			}
			list.Sort(delegate(ThingDef a, ThingDef b)
			{
				float ma = a.GetStatValueAbstract(StatDefOf.Mass);
				float mb = b.GetStatValueAbstract(StatDefOf.Mass);
				return (ma != mb) ? ma.CompareTo(mb) : string.Compare(a.label, b.label, StringComparison.CurrentCultureIgnoreCase);
			});
			validWeapons = list;
			return list;
		}

		private static void WeaponPicker(Listing_Standard l, string field, string kind, float width)
		{
			HashSet<ThingDef> sel = SidearmsSettings.GetSet(field);
			if (sel == null)
			{
				Hint(l, "–");
				return;
			}
			Rect top = l.GetRect(28f);
			weaponSearch = Widgets.TextField(new Rect(top.x, top.y, 200f, 28f), weaponSearch ?? string.Empty);
			float[] kgs = new float[3] { 1f, 2f, 3f };
			for (int i = 0; i < kgs.Length; i++)
			{
				if (Widgets.ButtonText(new Rect(top.x + 210f + i * 130f, top.y, 124f, 28f), "RL_SP_PickLight".Translate(kgs[i].ToString("0"))))
				{
					sel.Clear();
					foreach (ThingDef d in ValidWeapons())
					{
						if (KindOk(d, kind) && d.GetStatValueAbstract(StatDefOf.Mass) <= kgs[i] + 0.001f)
						{
							sel.Add(d);
						}
					}
					SidearmsSettings.MarkChanged();
				}
			}
			if (Widgets.ButtonText(new Rect(top.x + 210f + 3 * 130f, top.y, 100f, 28f), T("RL_SP_PickNone")))
			{
				sel.Clear();
				SidearmsSettings.MarkChanged();
			}
			int shown = 0;
			foreach (ThingDef d in ValidWeapons())
			{
				if (!KindOk(d, kind))
				{
					continue;
				}
				string label = d.LabelCap.ToString();
				if (!string.IsNullOrEmpty(weaponSearch) && label.IndexOf(weaponSearch, StringComparison.CurrentCultureIgnoreCase) < 0)
				{
					continue;
				}
				if (shown >= 60)
				{
					Hint(l, T("RL_SP_PickMore"));
					break;
				}
				shown++;
				Rect rr = l.GetRect(26f);
				if (shown % 2 == 0)
				{
					Widgets.DrawLightHighlight(rr);
				}
				Widgets.ThingIcon(new Rect(rr.x, rr.y + 1f, 24f, 24f), d);
				Text.Anchor = TextAnchor.MiddleLeft;
				Widgets.Label(new Rect(rr.x + 30f, rr.y, rr.width * 0.55f, 26f), label);
				GUI.color = new Color(0.72f, 0.72f, 0.72f);
				Widgets.Label(new Rect(rr.x + rr.width * 0.6f, rr.y, 140f, 26f), d.GetStatValueAbstract(StatDefOf.Mass).ToString("0.0") + " kg · " + (d.IsRangedWeapon ? T("RL_SP_RangedShort") : T("RL_SP_MeleeShort")));
				GUI.color = Color.white;
				Text.Anchor = TextAnchor.UpperLeft;
				bool on = sel.Contains(d);
				bool v = on;
				Widgets.Checkbox(new Vector2(rr.xMax - 28f, rr.y + 1f), ref v);
				if (v != on)
				{
					if (v)
					{
						sel.Add(d);
					}
					else
					{
						sel.Remove(d);
					}
					SidearmsSettings.MarkChanged();
				}
			}
			Hint(l, "RL_SP_PickCount".Translate(sel.Count));
		}

		private static bool KindOk(ThingDef d, string kind)
		{
			if (kind == "Melee")
			{
				return !d.IsRangedWeapon;
			}
			if (kind == "Ranged")
			{
				return d.IsRangedWeapon;
			}
			return true;
		}

		// ---------------- spawning ----------------
		private static void DrawSpawning(Listing_Standard l)
		{
			Header(l, "RL_SP_Tab_Spawning");
			Hint(l, T("RL_SP_SpawnHint"));
			Percent(l, "RL_SP_SpawnChance", "SidearmSpawnChance", 0f, 1f);
			Percent(l, "RL_SP_SpawnDropoff", "SidearmSpawnChanceDropoff", 0f, 1f);
			Percent(l, "RL_SP_Budget", "SidearmBudgetMultiplier", 0f, 1f);
			Percent(l, "RL_SP_BudgetDropoff", "SidearmBudgetDropoff", 0f, 1f);
			float c = SidearmsSettings.GetFloat("SidearmSpawnChance");
			float d = SidearmsSettings.GetFloat("SidearmSpawnChanceDropoff");
			l.Gap(6f);
			l.Label("RL_SP_SpawnExample".Translate(Mathf.RoundToInt(c * 100f), Mathf.RoundToInt(c * c * d * 100f), Mathf.RoundToInt(c * c * d * c * d * 100f)));
			if (ModsConfig.IsActive("misak.researchlockedraidgear"))
			{
				Hint(l, T("RL_SP_SpawnWorld"));
			}
		}

		// ---------------- misc ----------------
		private static void DrawMisc(Listing_Standard l)
		{
			Header(l, "RL_SP_Tab_Misc");
			EnumRow(l, "RL_SP_ColonistMode", "ColonistDefaultWeaponMode", WeaponModes, "RL_SP_EW_");
			EnumRow(l, "RL_SP_NpcMode", "NPCDefaultWeaponMode", WeaponModes, "RL_SP_EW_");
			EnumRow(l, "RL_SP_Fumble", "FumbleMode", FumbleModes, "RL_SP_EF_");
			l.GapLine();
			Toggle(l, "PreserveInventoryInCaravans");
			Toggle(l, "HideSidearmsInCaravanDialogs");
			Toggle(l, "ShowAlertsMissingSidearm");
			Toggle(l, "PlaySounds");
			Toggle(l, "ShowBrainscope");
		}

		// ---------------- this mod ----------------
		// 2.11.0: settings of the built-in sidearm system (only without Simple Sidearms)
		private static void DrawBuiltIn(Listing_Standard l, ResearchedLoadoutSettings st)
		{
			Header(l, "RL_OS_Header");
			Own(l, "RL_OS_SwitchMelee", ref st.ownSwitchMelee);
			if (st.ownSwitchMelee)
			{
				st.ownMeleeRange = Mathf.Round(l.SliderLabeled("RL_OS_MeleeRange".Translate(st.ownMeleeRange.ToString("0.0")), st.ownMeleeRange, 1.5f, 8f, 0.6f, Tip("RL_OS_MeleeRange")) * 10f) / 10f;
			}
			Own(l, "RL_OS_SwitchBack", ref st.ownSwitchBack);
			Own(l, "RL_OS_Gizmo", ref st.ownSwitchGizmo);
			st.ownMaxMass = Mathf.Round(l.SliderLabeled(st.ownMaxMass > 0f ? "RL_OS_MaxMass".Translate(st.ownMaxMass.ToString("0.#")).ToString() : T("RL_OS_MaxMassOff"), st.ownMaxMass, 0f, 30f, 0.6f, Tip("RL_OS_MaxMass")) * 2f) / 2f;
		}

		private static void DrawOwn(Listing_Standard l, ResearchedLoadoutSettings st)
		{
			Header(l, "RL_SP_Tab_Own");
			Own(l, "RL_AutoSidearm", ref st.autoEquipSidearm);
			Own(l, "RL_SS_Filter", ref st.sidearmFilter);
			Own(l, "RL_SS_Upgrade", ref st.sidearmUpgrade);
			Own(l, "RL_SS_Notify", ref st.sidearmNotify);
			Own(l, "RL_SP_BrawlerNoRanged", ref st.sidearmBrawlerNoRanged);
			l.Gap(4f);
			st.sidearmMinShooting = Mathf.RoundToInt(l.SliderLabeled("RL_SP_MinShoot".Translate(st.sidearmMinShooting), st.sidearmMinShooting, 0f, 20f, 0.6f, Tip("RL_SP_MinShoot")));
			st.sidearmMinMelee = Mathf.RoundToInt(l.SliderLabeled("RL_SP_MinMelee".Translate(st.sidearmMinMelee), st.sidearmMinMelee, 0f, 20f, 0.6f, Tip("RL_SP_MinMelee")));
			l.Gap(4f);
			st.sidearmMaxRanged = Mathf.RoundToInt(l.SliderLabeled("RL_SP_MaxRanged".Translate(st.sidearmMaxRanged), st.sidearmMaxRanged, 0f, 3f, 0.6f, Tip("RL_SP_MaxRanged")));
			st.sidearmMaxMelee = Mathf.RoundToInt(l.SliderLabeled("RL_SP_MaxMelee".Translate(st.sidearmMaxMelee), st.sidearmMaxMelee, 0f, 3f, 0.6f, Tip("RL_SP_MaxMelee")));
			st.sidearmMaxTotal = Mathf.RoundToInt(l.SliderLabeled(st.sidearmMaxTotal > 0 ? "RL_SP_MaxTotal".Translate(st.sidearmMaxTotal).ToString() : T("RL_SP_MaxTotalOff"), st.sidearmMaxTotal, 0f, 8f, 0.6f, Tip("RL_SP_MaxTotal")));
			Own(l, "RL_SP_StripUncrafted", ref st.stripUncraftedWeapons);
			l.Gap(6f);
			Own(l, "RL_SP_ArmorHunt", ref st.armorOnHunt);
			Own(l, "RL_SP_ArmorThreat", ref st.armorOnThreat);
			Own(l, "RL_SP_ArmorDrafted", ref st.armorDrafted);
			if (SidearmsSettings.Available)
			{
				l.GapLine();
				Own(l, "RL_SS_HidePage", ref st.hideSidearmsPage);
				if (l.ButtonText(T("RL_SP_OpenOriginal")))
				{
					SidearmsSettings.OpenOriginal();
				}
			}
		}

		private static void Own(Listing_Standard l, string key, ref bool v)
		{
			l.CheckboxLabeled(T(key), ref v, Tip(key));
		}
	}
	// 2.11.0: built-in sidearms when Simple Sidearms is not installed – colonists carry
	// a weapon of the other type (see the carry rules) and switch to it in a fight:
	// melee when an enemy comes close, back to the main weapon when it is gone.
	// Drafted colonists get a button for each carried weapon.
	public static class OwnSidearms
	{
		private const int Interval = 20;

		// pawn id -> weapon id that was in hand before an automatic switch
		private static readonly Dictionary<int, int> switchedFrom = new Dictionary<int, int>();

		// pawn id -> tick until no automatic switch (after a switch / a manual choice)
		private static readonly Dictionary<int, int> hold = new Dictionary<int, int>();

		// pawns whose weapon the player chose by the button while drafted
		private static readonly HashSet<int> manual = new HashSet<int>();

		public static void ResetGameState()
		{
			switchedFrom.Clear();
			hold.Clear();
			manual.Clear();
		}

		public static bool Enabled
		{
			get
			{
				ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
				return st != null && st.enabled && st.autoEquipSidearm && !SidearmsCompat.Present;
			}
		}

		private static bool Usable(Pawn p)
		{
			return p != null && p.Spawned && !p.Dead && !p.Downed && p.equipment != null && p.inventory != null && !p.WorkTagIsDisabled(WorkTags.Violent) && LoadoutFilter.IsPlayerColonist(p) && !ExemptPawns.IsExempt(p);
		}

		private static bool CarriesWeapon(Pawn p)
		{
			List<Thing> inv = p.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < inv.Count; i++)
			{
				if (inv[i] != null && inv[i].def != null && LoadoutFilter.IsManagedWeapon(inv[i].def))
				{
					return true;
				}
			}
			return false;
		}

		public static void Tick(Map map)
		{
			if (map == null || !Enabled)
			{
				return;
			}
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			int now = Find.TickManager.TicksGame;
			if ((now + map.uniqueID * 3) % Interval != 0)
			{
				return;
			}
			try
			{
				bool threat = GenHostility.AnyHostileActiveThreatToPlayer(map, false, false);
				List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
				for (int i = 0; i < pawns.Count; i++)
				{
					Pawn p = pawns[i];
					if (!p.Drafted)
					{
						manual.Remove(p.thingIDNumber);
					}
					if (!Usable(p) || p.InMentalState || manual.Contains(p.thingIDNumber) || !CarriesWeapon(p))
					{
						continue;
					}
					int h;
					if (hold.TryGetValue(p.thingIDNumber, out h) && now < h)
					{
						continue;
					}
					ThingWithComps prim = p.equipment.Primary;
					if (prim != null && LoadoutFilter.IsPersonaWeapon(prim))
					{
						continue;
					}
					if (p.stances != null && p.stances.curStance is Stance_Warmup)
					{
						continue;
					}
					float near = threat ? NearestEnemy(p, 12f) : 999f;
					bool inHandRanged = prim != null && prim.def.IsRangedWeapon;
					// enemy right next to us and a gun in hand -> melee
					if (st.ownSwitchMelee && near <= st.ownMeleeRange && (prim == null || inHandRanged))
					{
						ThingWithComps melee = ResearchedLoadoutMapComponent.BestCarried(p, false) as ThingWithComps;
						if (melee != null && melee != prim && Swap(p, melee))
						{
							if (prim != null && !switchedFrom.ContainsKey(p.thingIDNumber))
							{
								switchedFrom[p.thingIDNumber] = prim.thingIDNumber;
							}
							hold[p.thingIDNumber] = now + 120;
							continue;
						}
					}
					// enemy gone (or far) -> back to the weapon the colonist had before
					int fromId;
					if (st.ownSwitchBack && switchedFrom.TryGetValue(p.thingIDNumber, out fromId) && near > st.ownMeleeRange + 3f)
					{
						ThingWithComps back = FindCarried(p, fromId);
						if (back == null)
						{
							switchedFrom.Remove(p.thingIDNumber);
						}
						else if (Swap(p, back))
						{
							switchedFrom.Remove(p.thingIDNumber);
							hold[p.thingIDNumber] = now + 120;
							continue;
						}
					}
					// empty hands (weapon dropped or broken) -> take the carried one that suits the colonist
					if (prim == null)
					{
						bool ranged = ResearchedLoadoutMapComponent.PrefersRanged(p, null) && !(near <= st.ownMeleeRange);
						ThingWithComps w = (ResearchedLoadoutMapComponent.BestCarried(p, ranged) ?? ResearchedLoadoutMapComponent.BestCarried(p, !ranged)) as ThingWithComps;
						if (w != null && Swap(p, w))
						{
							hold[p.thingIDNumber] = now + 120;
						}
					}
				}
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Smart Colonist Gear] built-in sidearms: " + ex.Message, 7310033);
			}
		}

		private static ThingWithComps FindCarried(Pawn p, int id)
		{
			List<Thing> inv = p.inventory.innerContainer.InnerListForReading;
			for (int i = 0; i < inv.Count; i++)
			{
				if (inv[i] != null && inv[i].thingIDNumber == id)
				{
					return inv[i] as ThingWithComps;
				}
			}
			return null;
		}

		private static float NearestEnemy(Pawn p, float max)
		{
			IReadOnlyList<Pawn> all = p.Map.mapPawns.AllPawnsSpawned;
			float best = max * max;
			bool found = false;
			for (int i = 0; i < all.Count; i++)
			{
				Pawn o = all[i];
				if (o == null || o == p || o.Downed || o.Dead || !o.HostileTo(p))
				{
					continue;
				}
				float d = (o.Position - p.Position).LengthHorizontalSquared;
				if (d <= best)
				{
					best = d;
					found = true;
				}
			}
			return found ? Mathf.Sqrt(best) : 999f;
		}

		// puts the weapon in hand into the backpack and takes the carried one
		public static bool Swap(Pawn p, ThingWithComps target)
		{
			if (target == null || p.equipment == null || p.inventory == null || !p.inventory.innerContainer.Contains(target))
			{
				return false;
			}
			ThingWithComps prim = p.equipment.Primary;
			if (prim != null && !p.equipment.TryTransferEquipmentToContainer(prim, p.inventory.innerContainer))
			{
				return false;
			}
			if (!p.inventory.innerContainer.Remove(target))
			{
				return false;
			}
			p.equipment.AddEquipment(target);
			if (p.equipment.Primary != target)
			{
				// could not take it – put it back
				if (!target.Destroyed && target.ParentHolder == null)
				{
					p.inventory.innerContainer.TryAdd(target, true);
				}
				return false;
			}
			if (p.stances != null)
			{
				p.stances.CancelBusyStanceSoft();
			}
			return true;
		}

		public static IEnumerable<Gizmo> Gizmos(Pawn p)
		{
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (!Enabled || st == null || !st.ownSwitchGizmo || p == null || !p.Drafted || !Usable(p))
			{
				yield break;
			}
			List<Thing> inv = new List<Thing>(p.inventory.innerContainer.InnerListForReading);
			for (int i = 0; i < inv.Count; i++)
			{
				ThingWithComps w = inv[i] as ThingWithComps;
				if (w == null || w.def == null || !LoadoutFilter.IsManagedWeapon(w.def))
				{
					continue;
				}
				Pawn pawn = p;
				ThingWithComps weapon = w;
				yield return new Command_Action
				{
					defaultLabel = "RL_OS_Take".Translate(weapon.LabelShortCap),
					defaultDesc = "RL_OS_Take_Desc".Translate(weapon.LabelCap),
					icon = weapon.def.uiIcon,
					iconAngle = weapon.def.uiIconAngle,
					defaultIconColor = weapon.DrawColor,
					action = delegate
					{
						if (Swap(pawn, weapon))
						{
							manual.Add(pawn.thingIDNumber);
							switchedFrom.Remove(pawn.thingIDNumber);
						}
					}
				};
			}
		}
	}
	// 2.10.0: before a hunt and when the base is attacked, colonists quickly put on
	// the best armor lying in storage (allowed by this mod and their apparel policy)
	public static class CombatArmor
	{
		private const int Interval = 120;

		private static readonly Dictionary<int, int> nextTry = new Dictionary<int, int>();

		private static readonly HashSet<JobDef> busy = new HashSet<JobDef>();

		public static void ResetGameState()
		{
			nextTry.Clear();
		}

		private static void InitBusy()
		{
			if (busy.Count > 0)
			{
				return;
			}
			string[] names = new string[] { "Wear", "Flee", "FleeAndCower", "BeatFire", "ExtinguishSelf", "TendPatient", "Rescue", "Capture", "Arrest", "AttackMelee", "AttackStatic", "Equip", "LayDown", "Vomit", "Ingest", "DeliverToBed", "CarryToCryptosleepCasket", "Lovin", "ManTurret", "Strip", "Reload" };
			for (int i = 0; i < names.Length; i++)
			{
				JobDef d = DefDatabase<JobDef>.GetNamedSilentFail(names[i]);
				if (d != null)
				{
					busy.Add(d);
				}
			}
		}

		public static void Tick(Map map)
		{
			ResearchedLoadoutSettings st = ResearchedLoadoutMod.Settings;
			if (map == null || st == null || !st.enabled || (!st.armorOnHunt && !st.armorOnThreat))
			{
				return;
			}
			int now = Find.TickManager.TicksGame;
			if ((now + map.uniqueID * 7) % Interval != 0 || !map.IsPlayerHome)
			{
				return;
			}
			try
			{
				InitBusy();
				bool threat = st.armorOnThreat && Threat(map);
				List<Pawn> pawns = new List<Pawn>(map.mapPawns.FreeColonistsSpawned);
				for (int i = 0; i < pawns.Count; i++)
				{
					Pawn p = pawns[i];
					if (p == null || p.Dead || p.Downed || p.InMentalState || p.apparel == null || p.jobs == null || p.InBed() || ExemptPawns.IsExempt(p) || !LoadoutFilter.IsPlayerColonist(p))
					{
						continue;
					}
					int t;
					if (nextTry.TryGetValue(p.thingIDNumber, out t) && now < t)
					{
						continue;
					}
					Job cur = p.CurJob;
					JobDef cd = (cur == null) ? null : cur.def;
					bool hunting = st.armorOnHunt && cd == JobDefOf.Hunt && !p.Drafted;
					bool drafted = p.Drafted;
					if (!hunting && !threat)
					{
						continue;
					}
					if (drafted)
					{
						if (!st.armorDrafted || !threat || (cd != null && cd != JobDefOf.Wait_Combat && cd != JobDefOf.Wait && cd != JobDefOf.Wait_MaintainPosture) || EnemyNear(p, 22f))
						{
							continue;
						}
					}
					else if (!hunting)
					{
						if (cd != null && (busy.Contains(cd) || (cur != null && cur.playerForced)))
						{
							continue;
						}
						if (p.needs != null && p.needs.food != null && p.needs.food.Starving)
						{
							continue;
						}
					}
					Apparel best = FindArmor(p, drafted ? 28f : 70f, threat);
					if (best == null)
					{
						nextTry[p.thingIDNumber] = now + 2500;
						continue;
					}
					Job job = JobMaker.MakeJob(JobDefOf.Wear, best);
					GearWhy.TagJob(job, GearWhy.Reason(hunting ? "RL_Why_ArmorHunt" : "RL_Why_Armor"));
					if (drafted)
					{
						job.playerForced = true;
						p.jobs.TryTakeOrderedJob(job, null, false);
					}
					else
					{
						p.jobs.StartJob(job, JobCondition.InterruptForced, null, cd != null, true);
					}
					nextTry[p.thingIDNumber] = now + 400;
				}
			}
			catch (Exception ex)
			{
				MLog.WarnOnce("[Smart Colonist Gear] combat armor: " + ex.Message, 7310021);
			}
		}

		private static bool Threat(Map map)
		{
			return SteadyThreat.Active(map);
		}

		private static bool EnemyNear(Pawn p, float radius)
		{
			IReadOnlyList<Pawn> all = p.Map.mapPawns.AllPawnsSpawned;
			float r2 = radius * radius;
			for (int i = 0; i < all.Count; i++)
			{
				Pawn o = all[i];
				if (o != null && !o.Downed && o.HostileTo(p) && (o.Position - p.Position).LengthHorizontalSquared <= r2)
				{
					return true;
				}
			}
			return false;
		}

		public static Apparel FindArmor(Pawn p, float maxDist, bool threat)
		{
			List<Thing> list = p.Map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel);
			float d2 = maxDist * maxDist;
			ApparelPolicy policy = (p.outfits == null) ? null : p.outfits.CurrentApparelPolicy;
			int cold;
			int heat;
			LoadoutFilter.TemperatureNeed(p, out cold, out heat);
			Apparel best = null;
			float bestGain = 0.08f;
			for (int i = 0; i < list.Count; i++)
			{
				Apparel a = list[i] as Apparel;
				if (a == null || !a.Spawned || a.def.apparel == null || (a.Position - p.Position).LengthHorizontalSquared > d2)
				{
					continue;
				}
				float armor = LoadoutFilter.ArmorValue(a);
				if (armor < 0.15f || a.IsForbidden(p) || a.IsBurning())
				{
					continue;
				}
				if (LoadoutFilter.IsManuallyBanned(a.def) || (!LoadoutFilter.IsAllowed(a.def) && !EmergencyGear.AllowsThing(p, a)))
				{
					continue;
				}
				if (policy != null && policy.filter != null && !policy.filter.Allows(a))
				{
					continue;
				}
				if (!ApparelUtility.HasPartsToWear(p, a.def) || !a.def.apparel.PawnCanWear(p, false))
				{
					continue;
				}
				if (a.WornByCorpse && ThoughtUtility.CanGetThought(p, ThoughtDefOf.DeadMansApparel, true))
				{
					continue;
				}
				float lost = 0f;
				float lostCold = 0f;
				bool locked = false;
				List<Apparel> worn = p.apparel.WornApparel;
				for (int j = 0; j < worn.Count; j++)
				{
					if (!ApparelUtility.CanWearTogether(a.def, worn[j].def, p.RaceProps.body))
					{
						if (p.apparel.IsLocked(worn[j]))
						{
							locked = true;
							break;
						}
						lost += LoadoutFilter.ArmorValue(worn[j]);
						lostCold += worn[j].GetStatValue(StatDefOf.Insulation_Cold);
					}
				}
				if (locked)
				{
					continue;
				}
				if (cold > 0 && lostCold - a.GetStatValue(StatDefOf.Insulation_Cold) > 8f)
				{
					continue;
				}
				float gain = armor - lost;
				if (gain <= bestGain)
				{
					continue;
				}
				if (!p.CanReserveAndReach(a, PathEndMode.ClosestTouch, threat ? Danger.Some : Danger.Deadly, 1, -1, null, false))
				{
					continue;
				}
				best = a;
				bestGain = gain;
			}
			return best;
		}
	}

	public static class SidearmsCompat
	{
		private static bool _init;

		private static bool _active;

		private static bool _present;

		private static MethodInfo _canPickup;

		private static MethodInfo _dropSidearm;

		private static MethodInfo _getMemory;

		private static MethodInfo _isForced;

		private static MethodInfo _isAutotool;

		private static MethodInfo _informDropped;

		private static JobDef _equipSecondary;

		public static bool Active
		{
			get
			{
				Init();
				return _active;
			}
		}

		public static bool Present
		{
			get
			{
				Init();
				return _present;
			}
		}

		public static JobDef EquipSecondaryJob
		{
			get
			{
				Init();
				return _equipSecondary;
			}
		}

		private static void Init()
		{
			if (_init)
			{
				return;
			}
			_init = true;
			try
			{
				Type type = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.SimpleSidearms");
				if (!(type == null))
				{
					_present = true;
					Type type2 = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.Utilities.StatCalculator");
					Type type3 = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.Utilities.WeaponAssingment");
					Type type4 = AccessTools.TypeByName("SimpleSidearms.rimworld.CompSidearmMemory");
					if (type2 != null)
					{
						_canPickup = AccessTools.Method(type2, "CanPickupSidearmInstance", new Type[3]
						{
							typeof(ThingWithComps),
							typeof(Pawn),
							typeof(string).MakeByRefType()
						});
					}
					if (type3 != null)
					{
						_dropSidearm = AccessTools.Method(type3, "DropSidearm", new Type[4]
						{
							typeof(Pawn),
							typeof(ThingWithComps),
							typeof(bool),
							typeof(bool)
						});
					}
					if (type4 != null)
					{
						_getMemory = AccessTools.Method(type4, "GetMemoryCompForPawn", new Type[2]
						{
							typeof(Pawn),
							typeof(bool)
						});
						_isForced = AccessTools.Method(type4, "IsCurrentWeaponForced", new Type[1] { typeof(bool) });
						_isAutotool = AccessTools.Method(type4, "IsUsingAutotool", new Type[2]
						{
							typeof(bool),
							typeof(bool)
						});
						_informDropped = AccessTools.Method(type4, "InformOfDroppedSidearm", new Type[2]
						{
							typeof(Thing),
							typeof(bool)
						});
					}
					_equipSecondary = DefDatabase<JobDef>.GetNamedSilentFail("EquipSecondary");
					_active = _canPickup != null && _equipSecondary != null;
					if (_active)
					{
						MLog.Info("[Smart Colonist Gear] Simple Sidearms found - weapons follow its limits and memory.");
					}
					else
					{
						MLog.Warn("[Smart Colonist Gear] Simple Sidearms found, but hooking failed (different version?). Sidearms are left to SS.");
					}
				}
			}
			catch (Exception ex)
			{
				_active = false;
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms hook failed: " + ex.Message);
			}
		}

		public static void PatchFilter(Harmony harmony)
		{
			if (harmony == null)
			{
				return;
			}
			try
			{
				Type type = AccessTools.TypeByName("PeteTimesSix.SimpleSidearms.Utilities.StatCalculator");
				if (!(type == null))
				{
					MethodInfo methodInfo = AccessTools.Method(type, "CanPickupSidearmInstance", new Type[3]
					{
						typeof(ThingWithComps),
						typeof(Pawn),
						typeof(string).MakeByRefType()
					});
					if (methodInfo == null)
					{
						MLog.Warn("[Smart Colonist Gear] Simple Sidearms: CanPickupSidearmInstance not found, sidearm filter not hooked.");
						return;
					}
					MethodInfo original = methodInfo;
					HarmonyMethod postfix = new HarmonyMethod(typeof(SidearmsCompat), "CanPickupPostfix");
					harmony.Patch(original, null, postfix);
					MLog.Info("[Smart Colonist Gear] Simple Sidearms: sidearms also follow this mod's filter.");
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] Simple Sidearms filter hook failed: " + ex.Message);
			}
		}

		public static void CanPickupPostfix(object[] __args, ref bool __result)
		{
			if (!__result || __args == null)
			{
				return;
			}
			try
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				if (settings == null || !settings.enabled || !settings.blockLockedEquip || !settings.sidearmFilter)
				{
					return;
				}
				ThingWithComps thingWithComps = null;
				Pawn pawn = null;
				for (int i = 0; i < __args.Length; i++)
				{
					if (thingWithComps == null)
					{
						thingWithComps = __args[i] as ThingWithComps;
					}
					if (pawn == null)
					{
						pawn = __args[i] as Pawn;
					}
				}
				// 2.9.1: own limit of weapons per colonist
				if (thingWithComps != null && pawn != null && settings.sidearmMaxTotal > 0 && LoadoutFilter.IsPlayerColonist(pawn) && !ExemptPawns.IsExempt(pawn) && !HeldBy(thingWithComps, pawn) && ResearchedLoadoutMapComponent.CountWeapons(pawn) >= settings.sidearmMaxTotal)
				{
					__result = false;
					if (__args.Length > 2 && __args[2] == null)
					{
						__args[2] = "RL_SidearmTooMany".Translate(settings.sidearmMaxTotal).ToString();
					}
					return;
				}
				if (thingWithComps != null && pawn != null && thingWithComps != pawn && thingWithComps.def != null && LoadoutFilter.IsPlayerColonist(pawn) && !ExemptPawns.IsExempt(pawn) && !LoadoutFilter.IsPersonaWeapon(thingWithComps) && !LoadoutFilter.IsAllowed(thingWithComps.def) && !EmergencyGear.AllowsThing(pawn, thingWithComps))
				{
					__result = false;
					if (__args.Length > 2 && __args[2] == null)
					{
						__args[2] = "RL_SidearmLocked".Translate().ToString();
					}
				}
			}
			catch
			{
			}
		}

		private static bool HeldBy(Thing t, Pawn pawn)
		{
			Pawn_InventoryTracker inv = t.ParentHolder as Pawn_InventoryTracker;
			if (inv != null)
			{
				return inv.pawn == pawn;
			}
			Pawn_EquipmentTracker eq = t.ParentHolder as Pawn_EquipmentTracker;
			return eq != null && eq.pawn == pawn;
		}

		private static object Memory(Pawn pawn)
		{
			if (_getMemory == null || pawn == null)
			{
				return null;
			}
			try
			{
				return _getMemory.Invoke(null, new object[2] { pawn, true });
			}
			catch
			{
				return null;
			}
		}

		public static bool CanPickup(Pawn pawn, Thing weapon)
		{
			Init();
			ThingWithComps thingWithComps = weapon as ThingWithComps;
			if (_canPickup == null || thingWithComps == null || pawn == null)
			{
				return false;
			}
			try
			{
				object[] parameters = new object[3] { thingWithComps, pawn, null };
				return (bool)_canPickup.Invoke(null, parameters);
			}
			catch
			{
				return false;
			}
		}

		public static bool IsCurrentWeaponForced(Pawn pawn)
		{
			Init();
			object obj = Memory(pawn);
			if (obj == null || _isForced == null)
			{
				return false;
			}
			try
			{
				return (bool)_isForced.Invoke(obj, new object[1] { false });
			}
			catch
			{
				return false;
			}
		}

		public static bool IsUsingAutotool(Pawn pawn)
		{
			Init();
			object obj = Memory(pawn);
			if (obj == null || _isAutotool == null)
			{
				return false;
			}
			try
			{
				return (bool)_isAutotool.Invoke(obj, new object[2] { true, true });
			}
			catch
			{
				return false;
			}
		}

		public static void Forget(Pawn pawn, Thing weapon)
		{
			Init();
			if (!_active || weapon == null)
			{
				return;
			}
			object obj = Memory(pawn);
			if (obj == null || _informDropped == null)
			{
				return;
			}
			try
			{
				_informDropped.Invoke(obj, new object[2] { weapon, true });
			}
			catch
			{
			}
		}

		public static bool DropSidearm(Pawn pawn, ThingWithComps weapon)
		{
			Init();
			if (_dropSidearm == null || weapon == null)
			{
				return false;
			}
			try
			{
				_dropSidearm.Invoke(null, new object[4] { pawn, weapon, true, true });
				return weapon.Spawned;
			}
			catch
			{
				return false;
			}
		}
	}
	public static class GearStorage
	{
		public static bool Enabled
		{
			get
			{
				ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
				return settings != null && settings.enabled && settings.storeDroppedGear;
			}
		}

		public static bool CanHaulFor(Pawn pawn)
		{
			if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Dead || pawn.Downed)
			{
				return false;
			}
			if (pawn.Drafted || pawn.InMentalState)
			{
				return false;
			}
			if (!LoadoutFilter.IsPlayerColonist(pawn) || pawn.jobs == null || pawn.jobs.jobQueue == null)
			{
				return false;
			}
			if (pawn.GetLord() != null)
			{
				return false;
			}
			if (pawn.health == null || pawn.health.capacities == null || !pawn.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
			{
				return false;
			}
			return true;
		}

		private static bool AlreadyQueued(Pawn pawn, Thing thing)
		{
			if (pawn.CurJob != null && pawn.CurJob.targetA.Thing == thing && pawn.CurJob.def != JobDefOf.Equip && pawn.CurJob.def != JobDefOf.Wear)
			{
				return true;
			}
			for (int i = 0; i < pawn.jobs.jobQueue.Count; i++)
			{
				QueuedJob queuedJob = pawn.jobs.jobQueue[i];
				if (queuedJob != null && queuedJob.job != null && queuedJob.job.targetA.Thing == thing)
				{
					return true;
				}
			}
			return false;
		}

		public static bool TryQueueStore(Pawn pawn, Thing thing)
		{
			if (thing == null || thing.Destroyed || !thing.Spawned)
			{
				return false;
			}
			thing.SetForbidden(false, false);
			if (!Enabled || !CanHaulFor(pawn) || thing.Map != pawn.Map)
			{
				return false;
			}
			if (AlreadyQueued(pawn, thing))
			{
				return false;
			}
			if (!pawn.CanReserveAndReach(thing, PathEndMode.ClosestTouch, Danger.Some))
			{
				return false;
			}
			Job job = HaulAIUtility.HaulToStorageJob(pawn, thing, false);
			if (job == null)
			{
				return false;
			}
			if (ResearchedLoadoutMapComponent.IsIdleEnough(pawn) && (pawn.CurJob == null || !pawn.CurJob.playerForced) && !IsGearJobDef(pawn.CurJob))
			{
				pawn.jobs.StartJob(job, JobCondition.InterruptForced, null, false, true, null, JobTag.Misc);
			}
			else
			{
				pawn.jobs.jobQueue.EnqueueFirst(job, JobTag.Misc);
			}
			return true;
		}

		private static bool IsGearJobDef(Job job)
		{
			if (job == null)
			{
				return false;
			}
			JobDef def = job.def;
			return def == JobDefOf.Equip || def == JobDefOf.Wear || def == JobDefOf.RemoveApparel;
		}

		public static void OnSwapDrop(Pawn pawn, Thing dropped)
		{
			if (!Enabled || pawn == null || dropped == null)
			{
				return;
			}
			Job curJob = pawn.CurJob;
			if (curJob == null || (curJob.def != JobDefOf.Wear && curJob.def != JobDefOf.Equip && (curJob.def != JobDefOf.RemoveApparel || curJob.haulDroppedApparel)) || !LoadoutFilter.IsPlayerColonist(pawn) || ExemptPawns.IsExempt(pawn))
			{
				return;
			}
			try
			{
				TryQueueStore(pawn, dropped);
			}
			catch (Exception ex)
			{
				MLog.Warn("[Smart Colonist Gear] Hauling " + dropped.ToStringSafe() + " failed: " + ex.Message);
			}
		}
	}
	[HarmonyPatch(typeof(Pawn_ApparelTracker), "TryDrop", new Type[]
	{
		typeof(Apparel),
		typeof(Apparel),
		typeof(IntVec3),
		typeof(bool)
	}, new ArgumentType[]
	{
		ArgumentType.Normal,
		ArgumentType.Out,
		ArgumentType.Normal,
		ArgumentType.Normal
	})]
	public static class ApparelDropStorePatch
	{
		[HarmonyPostfix]
		public static void Postfix(Pawn_ApparelTracker __instance, bool __result, ref Apparel resultingAp)
		{
			if (__result && __instance != null)
			{
				GearStorage.OnSwapDrop(__instance.pawn, resultingAp);
			}
		}
	}
	[HarmonyPatch(typeof(Pawn_EquipmentTracker), "TryDropEquipment", new Type[]
	{
		typeof(ThingWithComps),
		typeof(ThingWithComps),
		typeof(IntVec3),
		typeof(bool)
	}, new ArgumentType[]
	{
		ArgumentType.Normal,
		ArgumentType.Out,
		ArgumentType.Normal,
		ArgumentType.Normal
	})]
	public static class EquipmentDropStorePatch
	{
		[HarmonyPostfix]
		public static void Postfix(Pawn_EquipmentTracker __instance, bool __result, ref ThingWithComps resultingEq)
		{
			if (__result && __instance != null)
			{
				GearStorage.OnSwapDrop(__instance.pawn, resultingEq);
			}
		}
	}
	[HarmonyPatch(typeof(JobDriver_TakeInventory), "TryMakePreToilReservations")]
	public static class JobDriverTakeInventoryPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(JobDriver_TakeInventory __instance, ref bool __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.blockLockedEquip)
			{
				return true;
			}
			Job job = __instance.job;
			if (job == null || job.targetA.Thing == null || job.targetA.Thing.def == null)
			{
				return true;
			}
			if (!job.targetA.Thing.def.IsWeapon)
			{
				return true;
			}
			Pawn pawn = __instance.pawn;
			if (!LoadoutFilter.IsPlayerColonist(pawn))
			{
				return true;
			}
			if (LoadoutFilter.IsAllowed(job.targetA.Thing.def) || EmergencyGear.AllowsThing(pawn, job.targetA.Thing))
			{
				return true;
			}
			__result = false;
			return false;
		}
	}
	[HarmonyPatch(typeof(Bill_Production), "Notify_IterationCompleted")]
	public static class CraftedRecordPatch
	{
		[HarmonyPostfix]
		public static void Postfix(Bill_Production __instance, Pawn billDoer)
		{
			try
			{
				if (__instance == null || __instance.recipe == null || __instance.recipe.products == null || (billDoer != null && billDoer.Faction != null && !billDoer.Faction.IsPlayer))
				{
					return;
				}
				ResearchedLoadoutGameComponent researchedLoadoutGameComponent = ResearchedLoadoutGameComponent.Get();
				if (researchedLoadoutGameComponent == null)
				{
					return;
				}
				researchedLoadoutGameComponent.EnsureCraftedSeeded();
				for (int i = 0; i < __instance.recipe.products.Count; i++)
				{
					ThingDefCountClass thingDefCountClass = __instance.recipe.products[i];
					if (thingDefCountClass != null)
					{
						researchedLoadoutGameComponent.NoteCrafted(thingDefCountClass.thingDef);
					}
				}
			}
			catch
			{
			}
		}
	}
	[HarmonyPatch(typeof(JobDriver_Equip), "TryMakePreToilReservations")]
	public static class JobDriverEquipPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(JobDriver_Equip __instance, ref bool __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.blockLockedEquip)
			{
				return true;
			}
			Job job = __instance.job;
			if (job == null || job.targetA.Thing == null)
			{
				return true;
			}
			Pawn pawn = __instance.pawn;
			if (!LoadoutFilter.IsPlayerColonist(pawn))
			{
				return true;
			}
			if (LoadoutFilter.IsAllowed(job.targetA.Thing.def) || EmergencyGear.AllowsThing(pawn, job.targetA.Thing))
			{
				return true;
			}
			__result = false;
			return false;
		}
	}
	[HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
	public static class OptimizeApparelPatch
	{
		[HarmonyPrefix]
		public static bool Prefix(Pawn pawn, ref Job __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || !settings.useOwnDresser || !settings.autoEquipApparel)
			{
				return true;
			}
			if (!LoadoutFilter.IsPlayerColonist(pawn))
			{
				return true;
			}
			if (ExemptPawns.IsExempt(pawn))
			{
				return true;
			}
			__result = null;
			return false;
		}

		[HarmonyPostfix]
		public static void Postfix(Pawn pawn, ref Job __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings != null && settings.enabled && settings.blockLockedApparel && __result != null && __result.def == JobDefOf.Wear && __result.targetA.Thing != null && LoadoutFilter.IsPlayerColonist(pawn) && !LoadoutFilter.AllowedFor(pawn, __result.targetA.Thing))
			{
				__result = null;
			}
		}
	}
	[HarmonyPatch]
	public static class ApparelScoreRawPatch
	{
		public static IEnumerable<MethodBase> TargetMethods()
		{
			List<MethodInfo> methods = AccessTools.GetDeclaredMethods(typeof(JobGiver_OptimizeApparel));
			for (int i = 0; i < methods.Count; i++)
			{
				MethodInfo j = methods[i];
				if (j.Name == "ApparelScoreRaw" && j.ReturnType == typeof(float))
				{
					yield return j;
				}
			}
		}

		public static bool Prepare()
		{
			using (IEnumerator<MethodBase> enumerator = TargetMethods().GetEnumerator())
			{
				if (enumerator.MoveNext())
				{
					return true;
				}
			}
			MLog.Warn("[Smart Colonist Gear] JobGiver_OptimizeApparel.ApparelScoreRaw not found, vanilla apparel filter disabled.");
			return false;
		}

		[HarmonyPostfix]
		public static void Postfix(object[] __args, ref float __result)
		{
			ResearchedLoadoutSettings settings = ResearchedLoadoutMod.Settings;
			if (settings == null || !settings.enabled || __args == null)
			{
				return;
			}
			Pawn pawn = null;
			Apparel apparel = null;
			for (int i = 0; i < __args.Length; i++)
			{
				if (pawn == null)
				{
					pawn = __args[i] as Pawn;
				}
				if (apparel == null)
				{
					apparel = __args[i] as Apparel;
				}
			}
			if (apparel == null || pawn == null || !LoadoutFilter.IsPlayerColonist(pawn))
			{
				return;
			}
			if (pawn.apparel != null && pawn.apparel.WornApparel.Contains(apparel))
			{
				__result += Specialization.ApparelBonus(pawn, apparel);
				WearerFit.Apply(pawn, apparel, ref __result);
				// 2.16.4: a mask the colonist must wear (role / ideology) is not pushed off – put on
				// and taken off again in a loop before
				if (__result > 0f && GearSense.UnneededMask(pawn, apparel) && !GearSense.Required(pawn, apparel.def))
				{
					__result *= 0.05f;
				}
				if (__result > 0f && !LoadoutFilter.IsBelowMinHp(apparel))
				{
					__result *= 1f + ResearchedLoadoutMapComponent.Stability;
				}
				if (ApparelGender.Mismatch(pawn, apparel.def) && __result > 0f)
				{
					__result *= 0.1f;
				}
				if (Specialization.WorkClothingBlocked(pawn, apparel.def) && __result > 0f)
				{
					__result *= 0.3f;
				}
			}
			else if (GearSense.UnneededMask(pawn, apparel) && !GearSense.Required(pawn, apparel.def))
			{
				// 2.16.3: no gas mask put on when there is no gas
				__result = -1000f;
			}
			else if (ApparelFlip.Blocked(pawn, apparel.def) && !LoadoutFilter.FreezingOrOverheating(pawn) && !TempRush.Active(pawn) && !GearSense.Required(pawn, apparel.def))
			{
				// 2.16.4: taken off again and again (helmet <-> gas mask …) – stays off for a while
				__result = -1000f;
			}
			else if ((ApparelGender.Mismatch(pawn, apparel.def) || Specialization.WorkClothingBlocked(pawn, apparel.def)) && !Coverage.FillsBasicGap(pawn, apparel.def))
			{
				__result = -1000f;
			}
			else if (settings.blockLockedApparel && !LoadoutFilter.AllowedFor(pawn, apparel))
			{
				__result = -1000f;
			}
			else if (LoadoutFilter.IsBelowMinHp(apparel))
			{
				__result = -1000f;
			}
			else
			{
				__result += Specialization.ApparelBonus(pawn, apparel);
				__result += Coverage.GapBonus(pawn, apparel.def);
				__result -= Coverage.LossPenalty(pawn, apparel.def);
				WearerFit.Apply(pawn, apparel, ref __result);
			}
			// 2.15.0: the newest era first (worn and candidate apparel alike, so it is a fair comparison)
			if (__result > 0f)
			{
				__result *= LoadoutFilter.EraMul(apparel.def);
			}
		}
	}
}


namespace ResearchedLoadout
{
	// 2.15.0: when the temperature crosses the set range, colonists change clothes right away
	// (no waiting for the next half-hour round, the calm time or the "recently taken off" memory)
	public static class TempRush
	{
		private static readonly Dictionary<int, int> lastState = new Dictionary<int, int>();
		private static readonly Dictionary<int, int> until = new Dictionary<int, int>();

		public static bool Active(Pawn pawn)
		{
			int u;
			return pawn != null && until.TryGetValue(pawn.thingIDNumber, out u) && Find.TickManager.TicksGame < u;
		}

		// true while the colonist should be handled now
		public static bool Check(Pawn pawn, int now)
		{
			int cold, heat;
			LoadoutFilter.TemperatureNeed(pawn, out cold, out heat);
			int state = cold > 0 ? 1 : heat > 0 ? 2 : 0;
			int last;
			bool known = lastState.TryGetValue(pawn.thingIDNumber, out last);
			lastState[pawn.thingIDNumber] = state;
			if (known && last != state)
			{
				// one game hour to finish changing
				until[pawn.thingIDNumber] = now + 2500;
				return true;
			}
			return Active(pawn);
		}
	}
}


namespace ResearchedLoadout
{
	// 2.16.1: remembers the weapon kind a colonist just put away
	public static class WeaponSwapMemory
	{
		private static readonly System.Collections.Generic.Dictionary<int, (Verse.ThingDef def, int tick)> dropped = new System.Collections.Generic.Dictionary<int, (Verse.ThingDef, int)>();

		public static void Note(Verse.Pawn pawn, Verse.Thing old)
		{
			if (pawn == null || old?.def == null || Verse.Find.TickManager == null)
				return;
			dropped[pawn.thingIDNumber] = (old.def, Verse.Find.TickManager.TicksGame);
		}

		public static bool JustDropped(Verse.Pawn pawn, Verse.ThingDef def)
		{
			if (pawn == null || def == null || Verse.Find.TickManager == null || !dropped.TryGetValue(pawn.thingIDNumber, out var e))
				return false;
			int age = Verse.Find.TickManager.TicksGame - e.tick;
			return e.def == def && age >= 0 && age < 120000;
		}
	}
}


namespace ResearchedLoadout
{
	// 2.16.3: a mask taken off three times within a day is back on again each time – someone else wants
	// it on; Smart Colonist Gear stops taking it off for two days (and says so once in its log)
	public static class MaskTugOfWar
	{
		private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> removed = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>();
		private static readonly System.Collections.Generic.Dictionary<string, int> stopUntil = new System.Collections.Generic.Dictionary<string, int>();

		private static string Key(Verse.Pawn p, Verse.ThingDef d) => p.thingIDNumber + "|" + d.defName;
		private static int Now => Verse.Find.TickManager?.TicksGame ?? 0;

		public static void Removed(Verse.Pawn p, Verse.ThingDef d)
		{
			string k = Key(p, d);
			if (!removed.TryGetValue(k, out var l))
				removed[k] = l = new System.Collections.Generic.List<int>();
			l.Add(Now);
			l.RemoveAll(t => Now - t > 60000 || t > Now);
			if (l.Count >= 3)
			{
				stopUntil[k] = Now + 120000;
				l.Clear();
				MLog.Info("[Smart Colonist Gear] " + p.LabelShort + " keeps getting " + d.defName + " put back on by another mod – left on for two days.");
			}
		}

		public static bool GaveUp(Verse.Pawn p, Verse.ThingDef d) => stopUntil.TryGetValue(Key(p, d), out int t) && Now < t && Now >= t - 120000;
	}
}


namespace ResearchedLoadout
{
	// 2.16.4: two pieces that can't be worn together (a helmet and a gas mask) could each look better
	// while the other one is worn – the colonist swapped them every few seconds. A piece taken off three
	// times within a day is not put on again for two days.
	public static class ApparelFlip
	{
		private static readonly System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>> removed = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<int>>();
		private static readonly System.Collections.Generic.Dictionary<string, int> blockUntil = new System.Collections.Generic.Dictionary<string, int>();
		private static int Now => Verse.Find.TickManager?.TicksGame ?? 0;
		private static string Key(Verse.Pawn p, Verse.ThingDef d) => p.thingIDNumber + "|" + d.defName;

		public static void Removed(Verse.Pawn p, Verse.ThingDef d)
		{
			if (p == null || d == null || !p.Spawned || !LoadoutFilter.IsPlayerColonist(p) || p.Dead)
				return;
			string k = Key(p, d);
			if (!removed.TryGetValue(k, out var l))
				removed[k] = l = new System.Collections.Generic.List<int>();
			int now = Now;
			l.Add(now);
			l.RemoveAll(t => now - t > 60000 || t > now);
			if (l.Count >= 3)
			{
				l.Clear();
				blockUntil[k] = now + 120000;
				MLog.Info("[Smart Colonist Gear] " + p.LabelShort + " kept putting on and taking off " + d.defName + " – left off for two days.");
			}
		}

		public static bool Blocked(Verse.Pawn p, Verse.ThingDef d)
		{
			if (p == null || d == null || blockUntil.Count == 0)
				return false;
			int now = Now;
			return blockUntil.TryGetValue(Key(p, d), out int t) && now < t && now >= t - 120000;
		}
	}

	[HarmonyLib.HarmonyPatch(typeof(RimWorld.Pawn_ApparelTracker), "Notify_ApparelRemoved")]
	public static class ApparelFlipPatch
	{
		private static readonly System.Reflection.FieldInfo pawnField = HarmonyLib.AccessTools.Field(typeof(RimWorld.Pawn_ApparelTracker), "pawn");

		[HarmonyLib.HarmonyPostfix]
		public static void Postfix(RimWorld.Pawn_ApparelTracker __instance, RimWorld.Apparel apparel)
		{
			try
			{
				if (Verse.Current.ProgramState != Verse.ProgramState.Playing)
					return;
				ApparelFlip.Removed(pawnField?.GetValue(__instance) as Verse.Pawn, apparel?.def);
			}
			catch
			{
			}
		}
	}
}
