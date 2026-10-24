// ResearchLockedRaidGear – zdrojový kód obnovený dekompilací z ResearchLockedRaidGear.dll (10. 10. 2026), původní zdroják se ztratil.
// Přeložitelné: Source\build.bat  nebo  dotnet build -c Release

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using RimWorld.QuestGen;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

[assembly: AssemblyVersion("0.0.0.0")]
namespace ResearchLockedRaidGear
{
	public sealed class ResearchLockedRaidGearMod : Mod
	{
		private enum SettingsTab
		{
			World,
			Arrivals,
			Rules,
			Filters,
			Factions,
			Raids
		}

		public const string Version = "2.14.1";

		public static ResearchLockedRaidGearSettings Settings;

		private Vector2 factionScroll;

		private SettingsTab tab;

		private static List<FactionDef> factionList;

		private static bool InGame
		{
			get
			{
				return Current.Game != null && Find.ResearchManager != null;
			}
		}

		public ResearchLockedRaidGearMod(ModContentPack content)
			: base(content)
		{
			Settings = GetSettings<ResearchLockedRaidGearSettings>();
			Harmony h = new Harmony("misak.researchlockedraidgear");
			MUI.Init(new ResearchLockedRaidGearSettings(), null, "settingsVersion", "recommended");
			if (Settings.recommended)
			{
				MUI.ApplyAll(Settings);
			}
			MUI.PatchWindow(h, typeof(ResearchLockedRaidGearMod));
			TryPatch(h, AccessTools.Method(typeof(PawnGenerator), "GenerateGearFor"), null, "Postfix", typeof(GenerateGearForPatch));
			TryPatch(h, AccessTools.Method(typeof(PawnGenerator), "GeneratePawn", new Type[1] { typeof(PawnGenerationRequest) }), null, "Postfix", typeof(GeneratePawnPatch));
			TryPatch(h, AccessTools.Method(typeof(Pawn), "SpawnSetup"), null, "Postfix", typeof(PawnSpawnSetupPatch));
			TryPatch(h, AccessTools.Method(typeof(Pawn_EquipmentTracker), "AddEquipment"), "Prefix", null, typeof(AddEquipmentPatch));
			TryPatch(h, AccessTools.Method(typeof(Pawn_ApparelTracker), "Wear"), "Prefix", null, typeof(WearApparelPatch));
			TryPatch(h, AccessTools.Method(typeof(JobDriver_Equip), "TryMakePreToilReservations"), "Prefix", null, typeof(JobDriverEquipPatch));
			TryPatch(h, AccessTools.Method(typeof(JobGiver_PickUpOpportunisticWeapon), "ShouldEquipWeapon"), null, "Postfix", typeof(ShouldEquipWeaponPatch));
			TryPatch(h, AccessTools.Method(typeof(JobGiver_PickUpOpportunisticWeapon), "ShouldEquipUtilityItem"), null, "Postfix", typeof(ShouldEquipUtilityPatch));
			TryPatch(h, AccessTools.Method(typeof(IncidentWorker_RaidEnemy), "FactionCanBeGroupSource"), null, "Postfix", typeof(WorldRaidFactionPatch));
			TryPatch(h, AccessTools.Method(typeof(ThingSetMaker), "Generate", new Type[1] { typeof(ThingSetMakerParams) }), null, "Postfix", typeof(WorldThingSetPatch));
			TryPatch(h, AccessTools.Method(typeof(IncidentWorker), "CanFireNow"), null, "Postfix", typeof(WorldIncidentPatch));
			TryPatch(h, AccessTools.Method(typeof(IncidentWorker), "TryExecute"), "Prefix", "Postfix", typeof(WorldEventReplacePatch));
			TryPatch(h, AccessTools.Method(typeof(IncidentWorker_NeutralGroup), "FactionCanBeGroupSource"), null, "Postfix", typeof(WorldVisitorFactionPatch));
			TryPatch(h, AccessTools.Method(typeof(Storyteller), "TryFire"), null, "Postfix", typeof(StorytellerHistoryPatch));
			TryPatch(h, AccessTools.Method(typeof(Thing), "SpawnSetup", new Type[2]
			{
				typeof(Map),
				typeof(bool)
			}), null, "Postfix", typeof(CauserSpawnPatch));
			TryPatch(h, AccessTools.PropertyGetter(typeof(ResearchProjectDef), "CanStartNow"), null, "Postfix", typeof(WorldResearchLockPatch));
			TryPatch(h, AccessTools.Method(typeof(QuestScriptDef), "CanRun", new Type[2]
			{
				typeof(Slate),
				typeof(IIncidentTarget)
			}), null, "Postfix", typeof(WorldQuestCanRunPatch));
			TryPatch(h, AccessTools.Method(typeof(QuestScriptDef), "CanRun", new Type[2]
			{
				typeof(float),
				typeof(IIncidentTarget)
			}), null, "Postfix", typeof(WorldQuestCanRunPatch));
			// 2.9.5: the counter is lowered in a finalizer, which also runs when GiveQuest throws
			TryPatchFinalizer(h, AccessTools.Method(typeof(IncidentWorker_GiveQuest), "GiveQuest"), "Prefix", "Finalizer", typeof(WorldGiveQuestPatch));
			TryPatch(h, AccessTools.Method(typeof(QuestUtility), "GenerateQuestAndMakeAvailable", new Type[2]
			{
				typeof(QuestScriptDef),
				typeof(Slate)
			}), null, "Postfix", typeof(WorldQuestGeneratedPatch));
			TryPatch(h, AccessTools.Method(typeof(PawnGroupMakerUtility), "GeneratePawns", new Type[2]
			{
				typeof(PawnGroupMakerParms),
				typeof(bool)
			}), null, "Postfix", typeof(RaidCompensationPatch));
			TryPatch(h, AccessTools.Method(typeof(Bill_Production), "Notify_IterationCompleted"), null, "Postfix", typeof(CraftedRecordPatch));
			TryPatch(h, AccessTools.Method(typeof(GenRecipe), "PostProcessProduct"), null, "Postfix", typeof(CraftedProductPatch));
			TryPatch(h, AccessTools.Method(typeof(GlobalControlsUtility), "DoDate"), null, "Postfix", typeof(LevelHud));
			// 2.10.0: raid types the player switched off (incidents, raid strategies, arrival modes)
			TryPatch(h, AccessTools.Method(typeof(IncidentWorker), "CanFireNow"), null, "Postfix", typeof(RaidTypeIncidentPatch));
			RaidTypeFilter.PatchWorkers(h);
			MLog.Game("[World at Your Level] v" + Version + " loaded (messages: Logs\\WorldAtYourLevel.log).");
		}

		private static void TryPatch(Harmony h, MethodInfo method, string prefix = null, string postfix = null, Type owner = null)
		{
			if (method == null || owner == null)
			{
				MLog.Warn("[World at Your Level] patch target not found for " + ((!(owner != null)) ? "?" : owner.Name));
				return;
			}
			try
			{
				HarmonyMethod prefix2 = ((prefix != null) ? new HarmonyMethod(owner, prefix) : null);
				HarmonyMethod postfix2 = ((postfix != null) ? new HarmonyMethod(owner, postfix) : null);
				h.Patch(method, prefix2, postfix2);
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] skip " + method.Name + ": " + ex.Message);
			}
		}

		private static void TryPatchFinalizer(Harmony h, MethodInfo method, string prefix, string finalizer, Type owner)
		{
			if (method == null || owner == null)
			{
				MLog.Warn("[World at Your Level] patch target not found for " + ((owner == null) ? "?" : owner.Name));
				return;
			}
			try
			{
				h.Patch(method, new HarmonyMethod(owner, prefix), null, null, new HarmonyMethod(owner, finalizer));
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] skip " + method.Name + ": " + ex.Message);
			}
		}

		public override void WriteSettings()
		{
			base.WriteSettings();
			ResearchLockedGearFilter.InvalidateAll();
		}

		public override string SettingsCategory()
		{
			return "Tech Progression: World at Your Level";
		}

		private static List<string> recBullets;

		public override void DoSettingsWindowContents(Rect inRect)
		{
			// 2.13.0: recommended settings card on top
			MUI.Version(inRect, "v" + Version + " · Bolek98");
			if (recBullets == null)
			{
				recBullets = new List<string>();
				for (int b = 1; b <= 6; b++)
				{
					recBullets.Add(("RLRG_Rec" + b).Translate());
				}
			}
			float top = inRect.y;
			top += MUI.RecommendedCard(new Rect(inRect.x, top, inRect.width, 0f), Settings, Settings.recommended, delegate(bool v)
			{
				Settings.recommended = v;
				if (v)
				{
					MUI.ApplyAll(Settings);
					ResearchLockedGearFilter.InvalidateAll();
				}
			}, recBullets) + 6f;
			if (Settings.recommended)
			{
				top += MUI.LockNote(new Rect(inRect.x, top, inRect.width, 0f)) + 4f;
			}
			inRect = new Rect(inRect.x, top - 2f, inRect.width, inRect.yMax - (top - 2f));
			List<TabRecord> list = new List<TabRecord>();
			list.Add(new TabRecord("RLRG_TabWorld".Translate(), delegate
			{
				tab = SettingsTab.World;
			}, tab == SettingsTab.World));
			list.Add(new TabRecord("RLRG_TabArrivals".Translate(), delegate
			{
				tab = SettingsTab.Arrivals;
			}, tab == SettingsTab.Arrivals));
			list.Add(new TabRecord("RLRG_TabRules".Translate(), delegate
			{
				tab = SettingsTab.Rules;
			}, tab == SettingsTab.Rules));
			list.Add(new TabRecord("RLRG_TabFilters".Translate(), delegate
			{
				tab = SettingsTab.Filters;
			}, tab == SettingsTab.Filters));
			list.Add(new TabRecord("RLRG_TabFactions".Translate(), delegate
			{
				tab = SettingsTab.Factions;
			}, tab == SettingsTab.Factions));
			list.Add(new TabRecord("RLRG_TabRaids".Translate(), delegate
			{
				tab = SettingsTab.Raids;
			}, tab == SettingsTab.Raids));
			List<TabRecord> tabs = list;
			Rect rect = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
			Widgets.DrawMenuSection(rect);
			TabDrawer.DrawTabs(rect, tabs, 1, 200f);
			Rect r = rect.ContractedBy(12f);
			MUI.BeginLock(Settings.recommended && tab != SettingsTab.Factions && tab != SettingsTab.Raids);
			try
			{
			switch (tab)
			{
			case SettingsTab.World:
				DrawWorldTab(r);
				break;
			case SettingsTab.Arrivals:
				DrawArrivalsTab(r);
				break;
			case SettingsTab.Rules:
				DrawRulesTab(r);
				break;
			case SettingsTab.Filters:
				DrawFiltersTab(r);
				break;
			case SettingsTab.Factions:
				DrawFactionsTab(r);
				break;
			case SettingsTab.Raids:
				DrawRaidsTab(r);
				break;
			}
			}
			finally
			{
				MUI.EndLock(r);
			}
		}

		private void DrawWorldTab(Rect r)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(r);
			listing_Standard.ColumnWidth = (r.width - 34f) / 2f;
			if (InGame)
			{
				ResearchLockedGearFilter.InvalidateAll();
				listing_Standard.Label(ResearchLockedGearFilter.ColonyTechExplain());
				listing_Standard.Label("RLRG_WorldLevelNow".Translate(ResearchLockedGearFilter.TechLimit(Settings).ToStringHuman()));
				listing_Standard.GapLine();
			}
			Check(listing_Standard, "RLRG_ShowHud", ref Settings.showLevelHud, "showLevelHud");
			Check(listing_Standard, "RLRG_ManualLevel", ref Settings.useManualLevel, "useManualLevel");
			if (Settings.useManualLevel)
			{
				if (listing_Standard.ButtonText("RLRG_ManualLevelPick".Translate(Settings.manualLevel.ToStringHuman())))
				{
					List<FloatMenuOption> list = new List<FloatMenuOption>();
					TechLevel[] array = new TechLevel[6]
					{
						TechLevel.Neolithic,
						TechLevel.Medieval,
						TechLevel.Industrial,
						TechLevel.Spacer,
						TechLevel.Ultra,
						TechLevel.Archotech
					};
					foreach (TechLevel techLevel in array)
					{
						TechLevel local = techLevel;
						list.Add(new FloatMenuOption(techLevel.ToStringHuman().CapitalizeFirst(), delegate
						{
							Settings.manualLevel = local;
						}));
					}
					Find.WindowStack.Add(new FloatMenu(list));
				}
			}
			else
			{
				Check(listing_Standard, "RLRG_LevelByProgress", ref Settings.levelByProgress, "levelByProgress");
				if (Settings.levelByProgress)
				{
					Check(listing_Standard, "RLRG_PerTier", ref Settings.perTierThresholds, "perTierThresholds");
					if (Settings.perTierThresholds)
					{
						Settings.pctMedieval = Pct(listing_Standard, TechLevel.Medieval, Settings.pctMedieval);
						Settings.pctIndustrial = Pct(listing_Standard, TechLevel.Industrial, Settings.pctIndustrial);
						Settings.pctSpacer = Pct(listing_Standard, TechLevel.Spacer, Settings.pctSpacer);
						Settings.pctUltra = Pct(listing_Standard, TechLevel.Ultra, Settings.pctUltra);
						Settings.pctArchotech = Pct(listing_Standard, TechLevel.Archotech, Settings.pctArchotech);
					}
					else
					{
						Settings.levelProgressPercent = (int)Sl(listing_Standard, "levelProgressPercent", "RLRG_LevelProgress".Translate(Settings.levelProgressPercent), Settings.levelProgressPercent, 5f, 100f, 0.55f, "RLRG_LevelProgress_Desc".Translate());
					}
				}
			}
			listing_Standard.NewColumn();
			Settings.techLevelsAhead = (int)Sl(listing_Standard, "techLevelsAhead", "RLRG_TechAhead".Translate(Settings.techLevelsAhead), Settings.techLevelsAhead, 0f, 3f, 0.55f, "RLRG_TechAhead_Desc".Translate());
			Settings.yearsPerTechLevel = (int)Sl(listing_Standard, "yearsPerTechLevel", (Settings.yearsPerTechLevel > 0) ? "RLRG_Years".Translate(Settings.yearsPerTechLevel).ToString() : "RLRG_YearsOff".Translate().ToString(), Settings.yearsPerTechLevel, 0f, 5f, 0.55f, "RLRG_Years_Desc".Translate());
			Check(listing_Standard, "RLRG_LockResearch", ref Settings.lockHigherResearch, "lockHigherResearch");
			Check(listing_Standard, "RLRG_WorldMessages", ref Settings.worldMessages, "worldMessages");
			listing_Standard.End();
		}

		private static int Pct(Listing_Standard list, TechLevel lv, int value)
		{
			return (int)list.SliderLabeled("RLRG_PctTier".Translate(lv.ToStringHuman().CapitalizeFirst(), value), value, 5f, 100f, 0.55f);
		}

		private void DrawArrivalsTab(Rect r)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(r);
			listing_Standard.ColumnWidth = (r.width - 34f) / 2f;
			Check(listing_Standard, "RLRG_Enable", ref Settings.enabled, "enabled");
			Check(listing_Standard, "RLRG_FilterAll", ref Settings.filterAllArrivals, "filterAllArrivals");
			Check(listing_Standard, "RLRG_SkipTraders", ref Settings.skipTraders, "skipTraders");
			Check(listing_Standard, "RLRG_ExemptAncients", ref Settings.exemptAncients, "exemptAncients");
			Check(listing_Standard, "RLRG_KeepTitleApparel", ref Settings.keepTitleApparel, "keepTitleApparel");
			Check(listing_Standard, "RLRG_FilterApparel", ref Settings.filterApparel, "filterApparel");
			Check(listing_Standard, "RLRG_FilterInventory", ref Settings.filterInventory, "filterInventory");
			Check(listing_Standard, "RLRG_FilterPossessions", ref Settings.filterPossessions, "filterPossessions");
			Check(listing_Standard, "RLRG_NotifyRaid", ref Settings.notifyRaid, "notifyRaid");
			listing_Standard.NewColumn();
			Check(listing_Standard, "RLRG_Replace", ref Settings.replaceWithClosest, "replaceWithClosest");
			Check(listing_Standard, "RLRG_Drop", ref Settings.dropLockedGear, "dropLockedGear");
			Check(listing_Standard, "RLRG_DamageDropped", ref Settings.damageDroppedGear, "damageDroppedGear");
			Check(listing_Standard, "RLRG_Biocode", ref Settings.biocodeReplacements, "biocodeReplacements");
			Settings.keepChancePercent = (int)Sl(listing_Standard, "keepChancePercent", "RLRG_KeepChance".Translate(Settings.keepChancePercent), Settings.keepChancePercent, 0f, 100f, 0.55f, "RLRG_KeepChance_Desc".Translate());
			Check(listing_Standard, "RLRG_Compensate", ref Settings.compensateRaids, "compensateRaids");
			if (Settings.compensateRaids)
			{
				Settings.compensationPercent = (int)Sl(listing_Standard, "compensationPercent", "RLRG_CompensationPct".Translate(Settings.compensationPercent), Settings.compensationPercent, 0f, 100f, 0.55f, "RLRG_CompensationPct_Desc".Translate());
			}
			// 2.13.0: rare raids
			Settings.rareAdvancedChance = (int)Sl(listing_Standard, "rareAdvancedChance", "RLRG_RareAdvanced".Translate(Settings.rareAdvancedChance), Settings.rareAdvancedChance, 0f, 50f, 0.55f, "RLRG_RareAdvanced_Desc".Translate());
			Settings.rareLowTechChance = (int)Sl(listing_Standard, "rareLowTechChance", "RLRG_RareLowTech".Translate(Settings.rareLowTechChance), Settings.rareLowTechChance, 0f, 50f, 0.55f, "RLRG_RareLowTech_Desc".Translate());
			listing_Standard.End();
		}

		private void DrawRulesTab(Rect r)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(r);
			listing_Standard.ColumnWidth = (r.width - 34f) / 2f;
			Check(listing_Standard, "RLRG_OnlyCrafted", ref Settings.onlyCrafted, "onlyCrafted");
			if (Settings.onlyCrafted && Settings.matchColony)
				Check(listing_Standard, "RLRG_StrictCrafted", ref Settings.strictCrafted, "strictCrafted");
			if (Settings.onlyCrafted && InGame)
			{
				RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
				listing_Standard.Label("RLRG_CraftedCount".Translate((raidGearGameComponent != null) ? SharedCrafted.UnionCount(raidGearGameComponent.crafted) : 0));
				if (raidGearGameComponent != null)
				{
					Rect rect = listing_Standard.GetRect(30f);
					TooltipHandler.TipRegion(rect, "RLRG_CraftedReset_Desc".Translate());
					if (Widgets.ButtonText(rect, "RLRG_CraftedReset".Translate()))
					{
						RaidGearGameComponent gc = raidGearGameComponent;
						Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("RLRG_CraftedResetConfirm".Translate(), delegate
						{
							gc.ResetCrafted();
							SharedCrafted.ResetPartner();
						}, true));
					}
				}
			}
			Check(listing_Standard, "RLRG_MechEscorts", ref Settings.filterMechEscorts, "filterMechEscorts");
			Check(listing_Standard, "RLRG_MatchColony", ref Settings.matchColony, "matchColony");
			if (Settings.matchColony)
			{
				Check(listing_Standard, "RLRG_MatchQuality", ref Settings.matchQuality, "matchQuality");
				Settings.matchMarginPercent = (int)Sl(listing_Standard, "matchMarginPercent", "RLRG_MatchMargin".Translate(Settings.matchMarginPercent), Settings.matchMarginPercent, 0f, 50f, 0.55f, "RLRG_MatchMargin_Desc".Translate());
				if (InGame)
				{
					GUI.color = new Color(0.75f, 0.75f, 0.75f);
					listing_Standard.Label(ResearchLockedGearFilter.MatchExplain());
					GUI.color = Color.white;
				}
			}
			listing_Standard.GapLine();
			Check(listing_Standard, "RLRG_UseRecipes", ref Settings.useRecipes, "useRecipes");
			Check(listing_Standard, "RLRG_BlockHigherTech", ref Settings.blockHigherTech, "blockHigherTech");
			Check(listing_Standard, "RLRG_BlockUntagged", ref Settings.blockUntaggedModItems, "blockUntaggedModItems");
			Check(listing_Standard, "RLRG_TechMismatch", ref Settings.techMismatchCheck, "techMismatchCheck");
			Check(listing_Standard, "RLRG_PowerEstimate", ref Settings.powerEstimate, "powerEstimate");
			listing_Standard.Gap(10f);
			if (listing_Standard.ButtonText("RLRG_OpenList".Translate()))
			{
				Find.WindowStack.Add(new Dialog_RaidGearList());
			}
			listing_Standard.End();
		}

		private void DrawFiltersTab(Rect r)
		{
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(r);
			listing_Standard.ColumnWidth = (r.width - 34f) / 2f;
			Check(listing_Standard, "RLRG_FactionsAtWorld", ref Settings.factionsAtWorldLevel, "factionsAtWorldLevel");
			if (!Settings.factionsAtWorldLevel)
			{
				Check(listing_Standard, "RLRG_WorldRaids", ref Settings.worldRaidFactions, "worldRaidFactions");
				Check(listing_Standard, "RLRG_WorldVisitors", ref Settings.worldVisitors, "worldVisitors");
			}
			Check(listing_Standard, "RLRG_TraderGuarantee", ref Settings.traderGuarantee, "traderGuarantee");
			if (Settings.traderGuarantee)
			{
				Settings.traderGuaranteeDays = (int)Sl(listing_Standard, "traderGuaranteeDays", "RLRG_TraderGuaranteeDays".Translate(Settings.traderGuaranteeDays), Settings.traderGuaranteeDays, 5f, 60f, 0.55f, "RLRG_TraderGuaranteeDays_Desc".Translate());
			}
			if (InGame && listing_Standard.ButtonText("RLRG_Diag_Open".Translate()))
			{
				Find.WindowStack.Add(new Dialog_VisitorDiag());
			}
			Check(listing_Standard, "RLRG_WorldMechs", ref Settings.worldMechanoids, "worldMechanoids");
			Check(listing_Standard, "RLRG_SiteTurrets", ref Settings.siteTurrets, "siteTurrets");
			Check(listing_Standard, "RLRG_WorldItems", ref Settings.worldItems, "worldItems");
			if (Settings.worldItems)
			{
				Settings.traderTechBonus = (int)Sl(listing_Standard, "traderTechBonus", "RLRG_TraderTechBonus".Translate(Settings.traderTechBonus), Settings.traderTechBonus, 0f, 3f, 0.55f, "RLRG_TraderTechBonus_Desc".Translate());
			}
			Check(listing_Standard, "RLRG_WorldIncidents", ref Settings.worldIncidents, "worldIncidents");
			Check(listing_Standard, "RLRG_ReplaceEvents", ref Settings.replaceEvents, "replaceEvents");
			Check(listing_Standard, "RLRG_WorldCausers", ref Settings.worldCausers, "worldCausers");
			Check(listing_Standard, "RLRG_WorldQuests", ref Settings.worldQuests, "worldQuests");
			if (Settings.worldQuests)
			{
				Check(listing_Standard, "RLRG_ReplaceQuests", ref Settings.replaceQuests, "replaceQuests");
			}
			listing_Standard.End();
		}

		private void DrawFactionsTab(Rect r)
		{
			Widgets.Label(new Rect(r.x, r.y, r.width, 26f), "RLRG_ExemptFactions".Translate());
			float num = r.y + 30f;
			List<FactionDef> list = SelectableFactions();
			Rect outRect = new Rect(r.x, num, r.width, r.yMax - num);
			float num2 = 26f;
			Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, (float)list.Count * num2);
			bool inGame = InGame;
			TechLevel techLevel = (inGame ? ResearchLockedGearFilter.TechLimit(Settings) : TechLevel.Undefined);
			Widgets.BeginScrollView(outRect, ref factionScroll, viewRect);
			for (int i = 0; i < list.Count; i++)
			{
				FactionDef factionDef = list[i];
				Rect rect = new Rect(0f, (float)i * num2, viewRect.width, num2);
				if (i % 2 == 1)
				{
					Widgets.DrawLightHighlight(rect);
				}
				bool checkOn = Settings.exemptFactions.Contains(factionDef.defName);
				bool flag = checkOn;
				Widgets.CheckboxLabeled(new Rect(rect.x + 4f, rect.y, rect.width * 0.5f, num2), factionDef.LabelCap, ref checkOn);
				if (checkOn != flag)
				{
					if (checkOn)
					{
						Settings.exemptFactions.Add(factionDef.defName);
					}
					else
					{
						Settings.exemptFactions.Remove(factionDef.defName);
					}
				}
				Widgets.Label(new Rect(rect.x + rect.width * 0.55f, rect.y + 3f, rect.width * 0.2f, num2), factionDef.techLevel.ToStringHuman().CapitalizeFirst());
				if (inGame)
				{
					bool flag2 = checkOn || Settings.factionsAtWorldLevel || factionDef.techLevel == TechLevel.Undefined || factionDef.techLevel == TechLevel.Animal || (!factionDef.humanlikeFaction && !Settings.worldMechanoids) || (int)factionDef.techLevel <= (int)techLevel;
					GUI.color = ((!flag2) ? new Color(1f, 0.6f, 0.4f) : new Color(0.5f, 0.9f, 0.5f));
					Widgets.Label(new Rect(rect.x + rect.width * 0.75f, rect.y + 3f, rect.width * 0.25f, num2), ((!flag2) ? "RLRG_FactionAbove" : "RLRG_FactionOk").Translate());
					GUI.color = Color.white;
				}
			}
			Widgets.EndScrollView();
		}

		private Vector2 raidScroll;

		private string raidSearch = string.Empty;

		// 2.10.0: tab with raid types that will not happen (checked = allowed)
		private void DrawRaidsTab(Rect r)
		{
			ResearchLockedRaidGearSettings s = Settings;
			RaidTypeFilter.EnsureLists();
			int blocked = RaidTypeFilter.BlockedCountAll();
			int total = RaidTypeFilter.TotalCountAll();
			Widgets.Label(new Rect(r.x, r.y, r.width, 26f), "RLRG_RaidsIntro".Translate());
			float y = r.y + 26f;
			GUI.color = (blocked > 0) ? new Color(1f, 0.75f, 0.45f) : new Color(0.7f, 0.9f, 0.7f);
			Widgets.Label(new Rect(r.x, y + 4f, r.width * 0.45f, 26f), "RLRG_RaidsSummary".Translate(blocked, total));
			GUI.color = Color.white;
			Widgets.Label(new Rect(r.x + r.width * 0.45f, y + 4f, 80f, 26f), "RLRG_Search".Translate());
			raidSearch = Widgets.TextField(new Rect(r.x + r.width * 0.45f + 80f, y + 2f, r.width * 0.55f - 80f - 150f, 28f), raidSearch ?? string.Empty);
			Rect resetRect = new Rect(r.xMax - 140f, y, 140f, 30f);
			TooltipHandler.TipRegion(resetRect, "RLRG_RaidsResetAll_Desc".Translate());
			if (Widgets.ButtonText(resetRect, "RLRG_RaidsResetAll".Translate()))
			{
				s.blockedIncidents.Clear();
				s.blockedRaidStrategies.Clear();
				s.blockedArrivalModes.Clear();
				s.blockedQuests.Clear();
				RaidTypeFilter.Invalidate();
			}
			y += 36f;
			List<RaidTypeFilter.Row> rows = RaidTypeFilter.BuildRows(raidSearch);
			float rowH = 26f;
			float headerH = 34f;
			float viewH = 0f;
			for (int i = 0; i < rows.Count; i++)
			{
				viewH += rows[i].header ? headerH : rowH;
			}
			Rect outRect = new Rect(r.x, y, r.width, r.yMax - y);
			Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, viewH);
			Widgets.BeginScrollView(outRect, ref raidScroll, viewRect);
			float cy = 0f;
			int stripe = 0;
			bool changed = false;
			for (int i = 0; i < rows.Count; i++)
			{
				RaidTypeFilter.Row row = rows[i];
				float h = row.header ? headerH : rowH;
				bool visible = cy + h >= raidScroll.y && cy <= raidScroll.y + outRect.height;
				Rect rect = new Rect(0f, cy, viewRect.width, h);
				cy += h;
				if (row.header)
				{
					stripe = 0;
					if (!visible)
					{
						continue;
					}
					List<string> groupList = RaidTypeFilter.ListFor(row.group);
					Text.Font = GameFont.Medium;
					Widgets.Label(new Rect(rect.x, rect.y + 2f, rect.width * 0.6f, 32f), RaidTypeFilter.GroupTitle(row.group));
					Text.Font = GameFont.Small;
					Widgets.Label(new Rect(rect.x + rect.width * 0.6f, rect.y + 8f, rect.width * 0.2f, 26f), "RLRG_RaidsGroupCount".Translate(RaidTypeFilter.BlockedCount(row.group), RaidTypeFilter.TotalCount(row.group)));
					Rect allowRect = new Rect(rect.xMax - 140f, rect.y + 3f, 140f, 28f);
					TooltipHandler.TipRegion(allowRect, "RLRG_RaidsAllowAll_Desc".Translate());
					if (Widgets.ButtonText(allowRect, "RLRG_RaidsAllowAll".Translate()) && groupList != null)
					{
						groupList.Clear();
						changed = true;
					}
					Widgets.DrawLineHorizontal(rect.x, rect.yMax - 1f, rect.width);
					continue;
				}
				stripe++;
				if (!visible)
				{
					continue;
				}
				if (stripe % 2 == 0)
				{
					Widgets.DrawLightHighlight(rect);
				}
				List<string> list = RaidTypeFilter.ListFor(row.group);
				bool allowed = list == null || !list.Contains(row.defName);
				bool before = allowed;
				Widgets.Checkbox(new Vector2(rect.x + 4f, rect.y + 1f), ref allowed);
				if (allowed != before && list != null)
				{
					if (allowed)
					{
						list.Remove(row.defName);
					}
					else if (!list.Contains(row.defName))
					{
						list.Add(row.defName);
					}
					changed = true;
				}
				if (!allowed)
				{
					GUI.color = new Color(1f, 0.55f, 0.5f);
				}
				Widgets.Label(new Rect(rect.x + 36f, rect.y + 3f, rect.width * 0.42f - 36f, rowH), row.label);
				GUI.color = Color.gray;
				Widgets.Label(new Rect(rect.x + rect.width * 0.42f, rect.y + 3f, rect.width * 0.3f, rowH), row.defName);
				Widgets.Label(new Rect(rect.x + rect.width * 0.72f, rect.y + 3f, rect.width * 0.28f, rowH), row.mod);
				GUI.color = Color.white;
				if (Mouse.IsOver(rect))
				{
					string tip = row.label + " (" + row.defName + ")\n" + "RLRG_RaidsSource".Translate(row.mod).ToString();
					if (!string.IsNullOrEmpty(row.desc))
					{
						tip = tip + "\n\n" + row.desc;
					}
					tip = tip + "\n\n" + (allowed ? "RLRG_RaidsAllowedTip" : "RLRG_RaidsBlockedTip").Translate().ToString();
					TooltipHandler.TipRegion(rect, tip);
				}
			}
			Widgets.EndScrollView();
			if (changed)
			{
				RaidTypeFilter.Invalidate();
			}
		}

		private static List<FactionDef> SelectableFactions()
		{
			if (factionList != null)
			{
				return factionList;
			}
			factionList = new List<FactionDef>();
			foreach (FactionDef item in DefDatabase<FactionDef>.AllDefsListForReading)
			{
				if (item != null && !item.isPlayer && item.humanlikeFaction)
				{
					factionList.Add(item);
				}
			}
			factionList.Sort((FactionDef a, FactionDef b) => string.Compare(a.label ?? a.defName, b.label ?? b.defName, StringComparison.OrdinalIgnoreCase));
			return factionList;
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
	}
	public sealed class ResearchLockedRaidGearSettings : ModSettings
	{
		public bool enabled = true;

		public bool useRecipes = true;

		public bool blockHigherTech = true;

		public bool blockUntaggedModItems = true;

		public bool techMismatchCheck = true;

		public bool levelByProgress = true;

		public bool worldRaidFactions = true;

		public bool worldMechanoids;
		// 2.14.0: with "match the colony" enemies still only get what you have crafted
		public bool strictCrafted = true;
		// 2.14.0: turrets on quest sites, bases and sieges only up to the turrets you have built yourself
		public bool siteTurrets = true;

		public bool worldItems = true;

		public bool worldIncidents = true;

		public bool worldMessages = true;

		public bool worldQuests = true;

		public bool useManualLevel;

		public TechLevel manualLevel = TechLevel.Industrial;

		public bool perTierThresholds;

		public int pctMedieval = 25;

		public int pctIndustrial = 25;

		public int pctSpacer = 25;

		public int pctUltra = 25;

		public int pctArchotech = 25;

		public bool lockHigherResearch;

		public bool filterPossessions = true;

		public bool replaceQuests = true;

		public bool worldVisitors;

		public bool traderGuarantee = true;

		public bool factionsAtWorldLevel = true;

		public bool onlyCrafted;

		// 2.11.0: arrivals carry nothing better than the colony has crafted
		public bool matchColony = true;

		public bool matchQuality = true;

		public int matchMarginPercent;

		// 2.11.1: research level readout above the date
		public bool showLevelHud = true;

		// 2.12.0: mechanoids that humanlike raiders bring along must be researched by the colony
		public bool filterMechEscorts = true;

		public int traderGuaranteeDays = 15;

		public bool replaceEvents = true;

		public bool worldCausers = true;

		public int levelProgressPercent = 25;

		public bool powerEstimate = true;

		public bool filterAllArrivals = true;

		public bool skipTraders;

		public bool filterApparel = true;

		public bool filterInventory = true;

		public bool replaceWithClosest = true;

		public bool dropLockedGear;

		public bool damageDroppedGear = true;

		public bool compensateRaids = true;

		public int compensationPercent = 75;

		public int techLevelsAhead;

		// 2.13.0: rare raids – now and then gear one tech level above the colony
		// (smaller raid), or one level below (bigger raid). Chance per raid in %, off (0) by default.
		public int rareAdvancedChance;

		public int rareLowTechChance;

		public int keepChancePercent;

		public bool keepTitleApparel = true;

		public bool exemptAncients = true;

		public List<string> exemptFactions = new List<string>();

		public List<string> bannedDefs = new List<string>();

		public bool biocodeReplacements;

		public int yearsPerTechLevel;

		public bool notifyRaid = true;

		public int traderTechBonus = 1;

		// 2.10.0: raid types switched off by the player (defNames; unknown names are kept but ignored)
		public List<string> blockedIncidents = new List<string>();

		public List<string> blockedRaidStrategies = new List<string>();

		public List<string> blockedArrivalModes = new List<string>();

		// 2.10.2: quests (e.g. "Toxic Gas Blockade") that will not be offered
		public List<string> blockedQuests = new List<string>();

		// 2.9.5: a new install starts at the current version, so only old settings files are migrated
		public int settingsVersion = 5;

		// 2.13.0: "Recommended settings" switch. A new install starts with it on;
		// settings saved by an older version keep their own values (missing key = off).
		public bool recommended = true;

		public float ThresholdFor(TechLevel lv)
		{
			int value = levelProgressPercent;
			if (perTierThresholds)
			{
				switch (lv)
				{
				case TechLevel.Medieval:
					value = pctMedieval;
					break;
				case TechLevel.Industrial:
					value = pctIndustrial;
					break;
				case TechLevel.Spacer:
					value = pctSpacer;
					break;
				case TechLevel.Ultra:
					value = pctUltra;
					break;
				case TechLevel.Archotech:
					value = pctArchotech;
					break;
				}
			}
			return (float)Mathf.Clamp(value, 1, 100) / 100f;
		}

		public override void ExposeData()
		{
			Scribe_Values.Look(ref enabled, "enabled", true);
			Scribe_Values.Look(ref useRecipes, "useRecipes", true);
			Scribe_Values.Look(ref blockHigherTech, "blockHigherTech", true);
			Scribe_Values.Look(ref blockUntaggedModItems, "blockUntaggedModItems", true);
			Scribe_Values.Look(ref techMismatchCheck, "techMismatchCheck", true);
			Scribe_Values.Look(ref levelByProgress, "levelByProgress", true);
			Scribe_Values.Look(ref worldRaidFactions, "worldRaidFactions", true);
			Scribe_Values.Look(ref worldMechanoids, "worldMechanoids", false);
			Scribe_Values.Look(ref strictCrafted, "strictCrafted", true);
			Scribe_Values.Look(ref siteTurrets, "siteTurrets", true);
			Scribe_Values.Look(ref worldItems, "worldItems", true);
			Scribe_Values.Look(ref worldIncidents, "worldIncidents", true);
			Scribe_Values.Look(ref worldMessages, "worldMessages", true);
			Scribe_Values.Look(ref worldQuests, "worldQuests", true);
			Scribe_Values.Look(ref useManualLevel, "useManualLevel", false);
			Scribe_Values.Look(ref manualLevel, "manualLevel", TechLevel.Industrial);
			Scribe_Values.Look(ref perTierThresholds, "perTierThresholds", false);
			Scribe_Values.Look(ref pctMedieval, "pctMedieval", 25);
			Scribe_Values.Look(ref pctIndustrial, "pctIndustrial", 25);
			Scribe_Values.Look(ref pctSpacer, "pctSpacer", 25);
			Scribe_Values.Look(ref pctUltra, "pctUltra", 25);
			Scribe_Values.Look(ref pctArchotech, "pctArchotech", 25);
			Scribe_Values.Look(ref lockHigherResearch, "lockHigherResearch", false);
			Scribe_Values.Look(ref filterPossessions, "filterPossessions", true);
			Scribe_Values.Look(ref replaceQuests, "replaceQuests", true);
			Scribe_Values.Look(ref worldVisitors, "worldVisitors", false);
			Scribe_Values.Look(ref traderGuarantee, "traderGuarantee", true);
			Scribe_Values.Look(ref factionsAtWorldLevel, "factionsAtWorldLevel", true);
			Scribe_Values.Look(ref onlyCrafted, "onlyCrafted", false);
			Scribe_Values.Look(ref matchColony, "matchColony", true);
			Scribe_Values.Look(ref matchQuality, "matchQuality", true);
			Scribe_Values.Look(ref matchMarginPercent, "matchMarginPercent", 0);
			Scribe_Values.Look(ref showLevelHud, "showLevelHud", true);
			Scribe_Values.Look(ref filterMechEscorts, "filterMechEscorts", true);
			Scribe_Values.Look(ref traderGuaranteeDays, "traderGuaranteeDays", 15);
			Scribe_Values.Look(ref replaceEvents, "replaceEvents", true);
			Scribe_Values.Look(ref worldCausers, "worldCausers", true);
			Scribe_Values.Look(ref levelProgressPercent, "levelProgressPercent", 25);
			Scribe_Values.Look(ref powerEstimate, "powerEstimate", true);
			Scribe_Values.Look(ref filterAllArrivals, "filterAllArrivals", true);
			Scribe_Values.Look(ref skipTraders, "skipTraders", false);
			Scribe_Values.Look(ref filterApparel, "filterApparel", true);
			Scribe_Values.Look(ref filterInventory, "filterInventory", true);
			Scribe_Values.Look(ref replaceWithClosest, "replaceWithClosest", true);
			Scribe_Values.Look(ref dropLockedGear, "dropLockedGear", false);
			Scribe_Values.Look(ref damageDroppedGear, "damageDroppedGear", true);
			Scribe_Values.Look(ref compensateRaids, "compensateRaids", true);
			Scribe_Values.Look(ref compensationPercent, "compensationPercent", 75);
			Scribe_Values.Look(ref techLevelsAhead, "techLevelsAhead", 0);
			Scribe_Values.Look(ref rareAdvancedChance, "rareAdvancedChance", 0);
			Scribe_Values.Look(ref rareLowTechChance, "rareLowTechChance", 0);
			Scribe_Values.Look(ref keepChancePercent, "keepChancePercent", 0);
			Scribe_Values.Look(ref keepTitleApparel, "keepTitleApparel", true);
			Scribe_Values.Look(ref exemptAncients, "exemptAncients", true);
			Scribe_Collections.Look(ref exemptFactions, "exemptFactions", LookMode.Value);
			if (exemptFactions == null)
			{
				exemptFactions = new List<string>();
			}
			Scribe_Collections.Look(ref bannedDefs, "bannedDefs", LookMode.Value);
			if (bannedDefs == null)
			{
				bannedDefs = new List<string>();
			}
			Scribe_Values.Look(ref biocodeReplacements, "biocodeReplacements", false);
			Scribe_Values.Look(ref yearsPerTechLevel, "yearsPerTechLevel", 0);
			Scribe_Values.Look(ref notifyRaid, "notifyRaid", true);
			Scribe_Values.Look(ref traderTechBonus, "traderTechBonus", 1);
			Scribe_Collections.Look(ref blockedIncidents, "blockedIncidents", LookMode.Value);
			if (blockedIncidents == null)
			{
				blockedIncidents = new List<string>();
			}
			Scribe_Collections.Look(ref blockedRaidStrategies, "blockedRaidStrategies", LookMode.Value);
			if (blockedRaidStrategies == null)
			{
				blockedRaidStrategies = new List<string>();
			}
			Scribe_Collections.Look(ref blockedArrivalModes, "blockedArrivalModes", LookMode.Value);
			Scribe_Collections.Look(ref blockedQuests, "blockedQuests", LookMode.Value);
			if (blockedQuests == null)
			{
				blockedQuests = new List<string>();
			}
			if (blockedArrivalModes == null)
			{
				blockedArrivalModes = new List<string>();
			}
			if (Scribe.mode == LoadSaveMode.PostLoadInit)
			{
				RaidTypeFilter.Invalidate();
			}
			Scribe_Values.Look(ref settingsVersion, "settingsVersion", 0);
			Scribe_Values.Look(ref recommended, "recommended", false);
			if (Scribe.mode == LoadSaveMode.PostLoadInit && settingsVersion < 3)
			{
				filterAllArrivals = true;
				settingsVersion = 3;
			}
			if (Scribe.mode == LoadSaveMode.PostLoadInit && settingsVersion < 4)
			{
				settingsVersion = 4;
			}
			if (Scribe.mode == LoadSaveMode.PostLoadInit && settingsVersion < 5)
			{
				worldVisitors = false;
				settingsVersion = 5;
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
	public static class ResearchLockedGearFilter
	{
		private sealed class ResearchReq
		{
			public readonly List<ResearchProjectDef> always = new List<ResearchProjectDef>();

			public readonly List<List<ResearchProjectDef>> options = new List<List<ResearchProjectDef>>();

			public bool freeRecipe;

			public bool HasAny
			{
				get
				{
					return always.Count > 0 || (options.Count > 0 && !freeRecipe);
				}
			}
		}

		public static int Applying;

		private static readonly HashSet<int> TempKept = new HashSet<int>();

		private static readonly Dictionary<ThingDef, ResearchReq> researchCache = new Dictionary<ThingDef, ResearchReq>();

		private static readonly Dictionary<ThingDef, bool> gunCache = new Dictionary<ThingDef, bool>();

		private static readonly Dictionary<ThingDef, bool> allowedCache = new Dictionary<ThingDef, bool>();

		private static readonly Dictionary<ThingDef, ThingDef> closestWeaponCache = new Dictionary<ThingDef, ThingDef>();

		private static List<ThingDef> weaponCandidates;

		private static List<ThingDef> apparelCandidates;

		private static int researchFingerprint = -1;

		private static int fingerprintFrame = -1;

		private static TechLevel colonyTech = TechLevel.Neolithic;

		private static bool lastUseRecipes;

		private static readonly Dictionary<int, bool> rangedTierOpen = new Dictionary<int, bool>();

		private static HashSet<int> KeptItems
		{
			get
			{
				RaidGearGameComponent raidGearGameComponent = ((Current.Game == null) ? null : Current.Game.GetComponent<RaidGearGameComponent>());
				return (raidGearGameComponent == null) ? TempKept : raidGearGameComponent.Kept;
			}
		}

		private static bool Contains(this HashSet<int> set, Thing t)
		{
			return set.Contains(t.thingIDNumber);
		}

		private static void Add(this HashSet<int> set, Thing t)
		{
			if (set.Count > 20000)
			{
				set.Clear();
			}
			set.Add(t.thingIDNumber);
		}

		private static void RefreshResearchState()
		{
			int frameCount = Time.frameCount;
			if (frameCount == fingerprintFrame)
			{
				return;
			}
			fingerprintFrame = frameCount;
			TechLevel scenario;
			TechLevel research;
			TechLevel techLevel = ComputeColonyTech(out scenario, out research);
			int num = (int)techLevel;
			List<ResearchProjectDef> allDefsListForReading = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ResearchProjectDef researchProjectDef = allDefsListForReading[i];
				if (researchProjectDef != null && researchProjectDef.IsFinished)
				{
					num = num * 31 + researchProjectDef.shortHash + 1;
				}
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			bool flag = settings != null && settings.useRecipes;
			num = num * 31 + ((settings != null) ? TimeBonus(settings) : 0);
			if (num != researchFingerprint || flag != lastUseRecipes)
			{
				if (flag != lastUseRecipes)
				{
					researchCache.Clear();
				}
				researchFingerprint = num;
				lastUseRecipes = flag;
				colonyTech = techLevel;
				allowedCache.Clear();
				rangedTierOpen.Clear();
				closestWeaponCache.Clear();
				weaponCandidates = null;
				apparelCandidates = null;
			}
		}

		public static void InvalidateAll()
		{
			fingerprintFrame = -1;
			researchFingerprint = int.MinValue;
			researchCache.Clear();
		}

		public static TechLevel ComputeColonyTech(out TechLevel scenario, out TechLevel research)
		{
			scenario = TechLevel.Neolithic;
			Faction ofPlayerSilentFail = Faction.OfPlayerSilentFail;
			if (ofPlayerSilentFail != null && ofPlayerSilentFail.def != null && (int)ofPlayerSilentFail.def.techLevel > (int)scenario)
			{
				scenario = ofPlayerSilentFail.def.techLevel;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings != null && settings.useManualLevel)
			{
				research = settings.manualLevel;
				scenario = settings.manualLevel;
				return settings.manualLevel;
			}
			bool flag = settings == null || settings.levelByProgress;
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
				int num = Mathf.Clamp((int)researchProjectDef.techLevel, 0, 15);
				array[num]++;
				if (researchProjectDef.IsFinished)
				{
					array2[num]++;
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
					float num2 = ((settings == null) ? 0.25f : settings.ThresholdFor((TechLevel)j));
					if (array[j] > 0 && (float)array2[j] >= Mathf.Max(1f, (float)array[j] * num2) && (int)(byte)j > (int)research)
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
			return "RLRG_CurrentLevel".Translate(tl.ToStringHuman(), scenario.ToStringHuman(), research.ToStringHuman());
		}

		private static TechLevel ColonyTech()
		{
			RefreshResearchState();
			return colonyTech;
		}

		public static bool LooksLikeGun(ThingDef def)
		{
			if (def == null || !def.IsRangedWeapon || def.Verbs == null)
			{
				return false;
			}
			bool value;
			if (gunCache.TryGetValue(def, out value))
			{
				return value;
			}
			bool flag = false;
			string text = def.defName ?? string.Empty;
			for (int i = 0; i < def.Verbs.Count; i++)
			{
				VerbProperties verbProperties = def.Verbs[i];
				if (verbProperties != null && verbProperties.defaultProjectile != null)
				{
					ThingDef defaultProjectile = verbProperties.defaultProjectile;
					string text2 = verbProperties.label ?? string.Empty;
					bool flag2 = text.IndexOf("Bow", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("bow", StringComparison.OrdinalIgnoreCase) >= 0 || (defaultProjectile.defName != null && defaultProjectile.defName.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0);
					bool flag3 = text.IndexOf("Throw", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Javelin", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Pilum", StringComparison.OrdinalIgnoreCase) >= 0 || (verbProperties.verbClass != null && verbProperties.verbClass.Name.IndexOf("Throw", StringComparison.OrdinalIgnoreCase) >= 0);
					if (!flag2 && !flag3)
					{
						flag = true;
						break;
					}
				}
			}
			gunCache[def] = flag;
			return flag;
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
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (def == null || settings == null || !settings.enabled || !settings.onlyCrafted)
			{
				return true;
			}
			if (!def.IsWeapon && !def.IsApparel)
			{
				return true;
			}
			RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
			if (raidGearGameComponent == null)
			{
				return true;
			}
			raidGearGameComponent.EnsureCraftedSeeded();
			return raidGearGameComponent.crafted.Contains(def.defName) || SharedCrafted.PartnerHas(def.defName);
		}

		public static bool IsResearchAllowed(ThingDef def)
		{
			if (def == null)
			{
				return true;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || !settings.enabled)
			{
				return true;
			}
			RefreshResearchState();
			bool value;
			if (allowedCache.TryGetValue(def, out value))
			{
				return value;
			}
			bool flag = ComputeAllowed(def, settings);
			allowedCache[def] = flag;
			return flag;
		}

		private static bool ComputeAllowed(ThingDef def, ResearchLockedRaidGearSettings settings)
		{
			if (IsManuallyBanned(def))
			{
				return false;
			}
			if (TotalAhead(settings) > 0 && (int)EffectiveTech(def) <= (int)TechLimit(settings) && (!IsExplosive(def) || ExplosiveTierOpen(def)))
			{
				return true;
			}
			ResearchReq researchRequirements = GetResearchRequirements(def);
			if (researchRequirements.HasAny)
			{
				if (!AllFinished(researchRequirements.always))
				{
					return false;
				}
				bool flag = researchRequirements.options.Count == 0 || researchRequirements.freeRecipe;
				int num = 0;
				while (!flag && num < researchRequirements.options.Count)
				{
					if (AllFinished(researchRequirements.options[num]))
					{
						flag = true;
					}
					num++;
				}
				if (!flag)
				{
					return false;
				}
				if (IsExplosive(def) && !ExplosiveTierOpen(def))
				{
					return false;
				}
				return (int)RequiredTech(def, settings) <= (int)TechLimit(settings);
			}
			if (settings.blockUntaggedModItems && IsNonOfficialModItem(def) && (def.IsWeapon || def.IsApparel) && !researchRequirements.freeRecipe)
			{
				return false;
			}
			TechLevel techLevel = EffectiveTech(def);
			if (def.IsWeapon)
			{
				if (IsExplosive(def))
				{
					return ExplosiveTierOpen(def) && (int)techLevel <= (int)TechLimit(settings);
				}
				if (!def.IsRangedWeapon && (int)techLevel <= 3)
				{
					return true;
				}
				return (int)techLevel <= (int)TechLimit(settings);
			}
			if (settings.blockHigherTech && (int)techLevel > (int)TechLimit(settings))
			{
				return false;
			}
			return true;
		}

		private static TechLevel RequiredTech(ThingDef def, ResearchLockedRaidGearSettings settings)
		{
			TechLevel techLevel = TechLevel.Undefined;
			if (settings.techMismatchCheck && def.techLevel != 0)
			{
				techLevel = def.techLevel;
			}
			if (settings.powerEstimate)
			{
				TechLevel techLevel2 = PowerTech.Estimate(def);
				if ((int)techLevel2 > (int)techLevel)
				{
					techLevel = techLevel2;
				}
			}
			return techLevel;
		}

		private static TechLevel EffectiveTech(ThingDef def)
		{
			TechLevel techLevel = def.techLevel;
			if (techLevel == TechLevel.Undefined)
			{
				techLevel = (LooksLikeGun(def) ? TechLevel.Industrial : ((!IsNonOfficialModItem(def)) ? TechLevel.Neolithic : TechLevel.Medieval));
			}
			else if (LooksLikeGun(def) && (int)techLevel < 4)
			{
				techLevel = TechLevel.Industrial;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings != null && settings.powerEstimate)
			{
				TechLevel techLevel2 = PowerTech.Estimate(def);
				if ((int)techLevel2 > (int)techLevel)
				{
					techLevel = techLevel2;
				}
			}
			return techLevel;
		}

		public static TechLevel TechLimit(ResearchLockedRaidGearSettings settings)
		{
			if (settings != null && settings.useManualLevel)
			{
				return settings.manualLevel;
			}
			int a = (int)ColonyTech() + TotalAhead(settings) + Mathf.Min(0, RareRaid.Shift);
			return (TechLevel)Mathf.Clamp(a, 1, 7);
		}

		public static int TotalAhead(ResearchLockedRaidGearSettings settings)
		{
			return Mathf.Max(0, settings.techLevelsAhead) + TimeBonus(settings) + Mathf.Max(0, RareRaid.Shift);
		}

		public static int TimeBonus(ResearchLockedRaidGearSettings settings)
		{
			if (settings == null || settings.yearsPerTechLevel <= 0 || Find.TickManager == null)
			{
				return 0;
			}
			return Find.TickManager.TicksGame / (settings.yearsPerTechLevel * 3600000);
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

		public static string Explain(ThingDef def)
		{
			if (def == null || Current.Game == null || Find.ResearchManager == null)
			{
				return null;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			StringBuilder stringBuilder = new StringBuilder();
			stringBuilder.AppendLine(((!IsAllowed(def)) ? "RLRG_Ex_Locked" : "RLRG_Ex_Allowed").Translate().ToString());
			if (IsManuallyBanned(def))
			{
				stringBuilder.AppendLine("RLRG_StatusBanned".Translate());
			}
			if (IsResearchAllowed(def) && !CraftedOk(def))
			{
				stringBuilder.AppendLine("RLRG_Ex_NotCrafted".Translate());
			}
			ResearchReq researchRequirements = GetResearchRequirements(def);
			if (researchRequirements.always.Count > 0)
			{
				stringBuilder.AppendLine("RLRG_Ex_Always".Translate(ResearchList(researchRequirements.always)));
			}
			for (int i = 0; i < researchRequirements.options.Count && i < 6; i++)
			{
				stringBuilder.AppendLine("RLRG_Ex_Option".Translate(ResearchList(researchRequirements.options[i])));
			}
			if (researchRequirements.freeRecipe)
			{
				stringBuilder.AppendLine("RLRG_Ex_Free".Translate());
			}
			if (researchRequirements.always.Count == 0 && researchRequirements.options.Count == 0)
			{
				stringBuilder.AppendLine("RLRG_Ex_NoResearch".Translate());
			}
			stringBuilder.AppendLine("RLRG_Ex_Tech".Translate(EffectiveTech(def).ToStringHuman(), TechLimit(settings).ToStringHuman()));
			TechLevel techLevel = PowerTech.Estimate(def);
			if (settings.powerEstimate && techLevel != 0 && (int)techLevel > (int)def.techLevel)
			{
				stringBuilder.AppendLine("RLRG_Ex_Power".Translate(techLevel.ToStringHuman()));
			}
			TechLevel techLevel2 = RequiredTech(def, settings);
			if ((int)techLevel2 > (int)TechLimit(settings))
			{
				stringBuilder.AppendLine("RLRG_Ex_NeedColony".Translate(techLevel2.ToStringHuman()));
			}
			if (def.modContentPack != null)
			{
				stringBuilder.Append("RLRG_Ex_Mod".Translate(def.modContentPack.Name));
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
					list2.Add(list[i].LabelCap + " (" + ((!list[i].IsFinished) ? "RLRG_Ex_Missing" : "RLRG_Ex_Done").Translate() + ")");
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
			if (ResearchLockedRaidGearMod.Settings != null && ResearchLockedRaidGearMod.Settings.useRecipes)
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
				List<ResearchProjectDef> list = new List<ResearchProjectDef>(recipeResearch);
				if (benches[i] != null)
				{
					Add(list, benches[i].researchPrerequisites);
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
			return text.IndexOf("ludeon", StringComparison.OrdinalIgnoreCase) < 0;
		}

		public static bool IsManuallyBanned(ThingDef def)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			return settings != null && settings.bannedDefs != null && def != null && settings.bannedDefs.Contains(def.defName);
		}

		public static bool ShouldFilter(Pawn pawn)
		{
			if (pawn == null || pawn.Dead)
			{
				return false;
			}
			if (pawn.RaceProps == null || (!pawn.RaceProps.Humanlike && !CarriesRealGear(pawn)))
			{
				return false;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || !settings.enabled)
			{
				return false;
			}
			if (pawn.Faction != null && pawn.Faction.IsPlayer)
			{
				return false;
			}
			if (pawn.IsPrisonerOfColony)
			{
				return false;
			}
			if (settings.skipTraders && pawn.trader != null)
			{
				return false;
			}
			if (pawn.Faction != null)
			{
				if (settings.exemptFactions != null && settings.exemptFactions.Contains(pawn.Faction.def.defName))
				{
					return false;
				}
				if (settings.exemptAncients && Find.FactionManager != null && (pawn.Faction == Faction.OfAncients || pawn.Faction == Faction.OfAncientsHostile))
				{
					return false;
				}
			}
			if (settings.filterAllArrivals || settings.factionsAtWorldLevel)
			{
				return true;
			}
			if (pawn.Faction != null && Faction.OfPlayer != null && pawn.Faction.HostileTo(Faction.OfPlayer))
			{
				return true;
			}
			Lord lord = pawn.GetLord();
			if (lord != null && lord.LordJob != null)
			{
				string name = lord.LordJob.GetType().Name;
				if (name.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Raid", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Stage", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Kidnap", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			if (pawn.mindState != null && pawn.mindState.duty != null && pawn.mindState.duty.def != null)
			{
				string text = pawn.mindState.duty.def.defName ?? string.Empty;
				if (text.IndexOf("Assault", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Attack", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Hunt", StringComparison.OrdinalIgnoreCase) >= 0 || text.IndexOf("Kidnap", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		// 2.10.1: mod races that are not "humanlike" (e.g. Wolfein rebel mechanoids)
		// still carry ordinary guns and clothes that can be crafted and dropped.
		// Those items are filtered too; built-in weapons (destroyed on drop) are not.
		public static bool IsRealItem(ThingDef def)
		{
			if (def == null || def.destroyOnDrop)
			{
				return false;
			}
			return def.recipeMaker != null || def.tradeability != Tradeability.None;
		}

		public static bool CarriesRealGear(Pawn pawn)
		{
			if (pawn.equipment != null)
			{
				List<ThingWithComps> eq = pawn.equipment.AllEquipmentListForReading;
				for (int i = 0; i < eq.Count; i++)
				{
					if (eq[i] != null && eq[i].def.IsWeapon && IsRealItem(eq[i].def))
					{
						return true;
					}
				}
			}
			if (pawn.apparel != null)
			{
				List<Apparel> ap = pawn.apparel.WornApparel;
				for (int j = 0; j < ap.Count; j++)
				{
					if (ap[j] != null && IsRealItem(ap[j].def))
					{
						return true;
					}
				}
			}
			return false;
		}

		public static void FilterPawn(Pawn pawn)
		{
			if (Applying > 0 || !ShouldFilter(pawn))
			{
				return;
			}
			Applying++;
			try
			{
				StripDisallowed(pawn, ResearchLockedRaidGearMod.Settings);
			}
			catch (Exception ex)
			{
				MLog.Warn(string.Concat("[World at Your Level] filter failed on ", pawn, ": ", ex));
			}
			finally
			{
				Applying--;
			}
		}

		public static void StripDisallowed(Pawn pawn, ResearchLockedRaidGearSettings settings)
		{
			if (settings == null)
			{
				return;
			}
			bool hadWeapon = pawn.equipment != null && pawn.equipment.Primary != null;
			float valueBefore = ((!RaidCompensation.Collecting) ? 0f : GearValue(pawn));
			try
			{
				StripCore(pawn, settings, hadWeapon);
			}
			finally
			{
				if (RaidCompensation.Collecting)
				{
					RaidCompensation.Record(pawn, valueBefore, GearValue(pawn));
				}
			}
		}

		public static float GearValue(Pawn pawn)
		{
			float num = 0f;
			if (pawn.equipment != null)
			{
				List<ThingWithComps> allEquipmentListForReading = pawn.equipment.AllEquipmentListForReading;
				for (int i = 0; i < allEquipmentListForReading.Count; i++)
				{
					num += allEquipmentListForReading[i].MarketValue;
				}
			}
			if (pawn.apparel != null)
			{
				List<Apparel> wornApparel = pawn.apparel.WornApparel;
				for (int j = 0; j < wornApparel.Count; j++)
				{
					num += wornApparel[j].MarketValue;
				}
			}
			return num;
		}

		private static bool PossessionAboveWorld(Thing thing, ResearchLockedRaidGearSettings settings)
		{
			if (!settings.filterPossessions || thing.def == null)
			{
				return false;
			}
			TechLevel techLevel = thing.def.techLevel;
			if (techLevel == TechLevel.Undefined || techLevel == TechLevel.Animal)
			{
				return false;
			}
			return (int)techLevel > (int)TechLimit(settings);
		}

		// 2.13.0: rare advanced raid – the gear it was generated with is kept later too
		public static void KeepAll(Pawn pawn)
		{
			if (pawn == null)
			{
				return;
			}
			if (pawn.equipment != null)
			{
				foreach (ThingWithComps t in pawn.equipment.AllEquipmentListForReading)
				{
					KeptItems.Add(t);
				}
			}
			if (pawn.apparel != null)
			{
				foreach (Apparel t in pawn.apparel.WornApparel)
				{
					KeptItems.Add(t);
				}
			}
			if (pawn.inventory != null)
			{
				foreach (Thing t in pawn.inventory.innerContainer)
				{
					KeptItems.Add(t);
				}
			}
		}

		private static bool KeepAnyway(Thing thing, ResearchLockedRaidGearSettings settings)
		{
			if (KeptItems.Contains(thing))
			{
				return true;
			}
			if (settings.keepChancePercent > 0 && Rand.Chance((float)settings.keepChancePercent / 100f))
			{
				KeptItems.Add(thing);
				return true;
			}
			return false;
		}

		private static bool RequiredByTitleOrRole(Pawn pawn, ThingDef def)
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
				if (requirement != null && (requirement.RequiredForPawn(pawn, def) || requirement.AllowedForPawn(pawn, def)))
				{
					return true;
				}
			}
			return false;
		}

		private static void StripCore(Pawn pawn, ResearchLockedRaidGearSettings settings, bool hadWeapon)
		{
			// 2.9.5: remember the primary – a real weapon is only handed out when this one was taken away
			ThingWithComps primBefore = (pawn.equipment == null) ? null : pawn.equipment.Primary;
			// 2.10.1: non-humanlike carriers lose only real items and get no replacements
			bool beast = pawn.RaceProps != null && !pawn.RaceProps.Humanlike;
			if (pawn.equipment != null)
			{
				List<ThingWithComps> list = new List<ThingWithComps>(pawn.equipment.AllEquipmentListForReading);
				for (int i = 0; i < list.Count; i++)
				{
					ThingWithComps thingWithComps = list[i];
					if (thingWithComps != null && thingWithComps.def.IsWeapon && (!beast || IsRealItem(thingWithComps.def)) && !PawnThingOk(thingWithComps, beast) && !KeepAnyway(thingWithComps, settings))
					{
						ThingDef thingDef = ((!settings.replaceWithClosest || beast) ? null : (RestuffOk(thingWithComps) ? thingWithComps.def : FindClosestWeapon(thingWithComps.def)));
						pawn.equipment.Remove(thingWithComps);
						RaidCompensation.CountItem(thingDef != null);
						if (thingDef != null)
						{
							GiveWeapon(pawn, thingDef, thingWithComps);
						}
						Discard(pawn, thingWithComps, settings);
					}
				}
			}
			if (settings.filterApparel && pawn.apparel != null)
			{
				List<Apparel> list2 = new List<Apparel>(pawn.apparel.WornApparel);
				for (int j = 0; j < list2.Count; j++)
				{
					Apparel apparel = list2[j];
					if (apparel != null && (!beast || IsRealItem(apparel.def)) && !PawnThingOk(apparel, beast) && (!settings.keepTitleApparel || !RequiredByTitleOrRole(pawn, apparel.def)) && !KeepAnyway(apparel, settings))
					{
						ThingDef thingDef2 = ((!settings.replaceWithClosest || beast) ? null : (RestuffOk(apparel) ? apparel.def : FindClosestApparel(pawn, apparel.def)));
						pawn.apparel.Remove(apparel);
						RaidCompensation.CountItem(thingDef2 != null);
						if (thingDef2 != null)
						{
							GiveApparel(pawn, thingDef2, apparel);
						}
						Discard(pawn, apparel, settings);
					}
				}
			}
			if (settings.filterInventory && pawn.inventory != null)
			{
				List<Thing> list3 = new List<Thing>(pawn.inventory.innerContainer.InnerListForReading);
				for (int k = 0; k < list3.Count; k++)
				{
					Thing thing = list3[k];
					if (thing != null && !((!thing.def.IsWeapon && !thing.def.IsApparel) ? (!PossessionAboveWorld(thing, settings)) : PawnThingOk(thing, beast)) && !KeepAnyway(thing, settings))
					{
						pawn.inventory.innerContainer.Remove(thing);
						RaidCompensation.CountItem(false);
						Discard(pawn, thing, settings);
					}
				}
			}
			// 2.11.0: what stays is capped to the best quality the colony has made
			if (!beast)
			{
				CapPawnQuality(pawn);
			}
			ThingWithComps primAfter = (pawn.equipment == null) ? null : pawn.equipment.Primary;
			if (!beast && hadWeapon && primAfter != primBefore && (primAfter == null || IsExplosive(primAfter.def)))
			{
				EnsureRealWeapon(pawn, settings);
			}
		}

		public static bool IsExplosive(ThingDef def)
		{
			if (def == null || !def.IsWeapon)
			{
				return false;
			}
			if (def.weaponTags != null)
			{
				for (int i = 0; i < def.weaponTags.Count; i++)
				{
					string text = def.weaponTags[i];
					if (text == "GunSingleUse" || (text != null && text.StartsWith("Grenade", StringComparison.OrdinalIgnoreCase)))
					{
						return true;
					}
				}
			}
			string text2 = def.defName ?? string.Empty;
			return text2.IndexOf("Grenade", StringComparison.OrdinalIgnoreCase) >= 0 || text2.IndexOf("Molotov", StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static bool ExplosiveTierOpen(ThingDef def)
		{
			if (def.techLevel == TechLevel.Neolithic || def.techLevel == TechLevel.Medieval)
			{
				return true;
			}
			return HasAllowedRealRanged(EffectiveTech(def));
		}

		private static bool HasAllowedRealRanged(TechLevel tier)
		{
			bool value;
			if (rangedTierOpen.TryGetValue((int)tier, out value))
			{
				return value;
			}
			rangedTierOpen[(int)tier] = false;
			value = false;
			foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (item != null && item.IsRangedWeapon && !IsExplosive(item) && IsPawnWeapon(item) && (int)EffectiveTech(item) >= (int)tier && IsAllowed(item))
				{
					value = true;
					break;
				}
			}
			rangedTierOpen[(int)tier] = value;
			return value;
		}

		private static void EnsureRealWeapon(Pawn pawn, ResearchLockedRaidGearSettings settings)
		{
			if (pawn.equipment == null)
			{
				return;
			}
			ThingWithComps primary = pawn.equipment.Primary;
			if (primary != null && IsExplosive(primary.def))
			{
				pawn.equipment.Remove(primary);
				if (!IsAllowed(primary.def) || pawn.inventory == null || !pawn.inventory.innerContainer.TryAdd(primary))
				{
					Discard(pawn, primary, settings);
				}
			}
			if (pawn.equipment.Primary == null)
			{
				ThingDef thingDef = BestRealWeapon() ?? AbsoluteFallback();
				if (thingDef != null)
				{
					GiveWeapon(pawn, thingDef, null);
				}
			}
		}

		private static ThingDef BestRealWeapon()
		{
			ThingDef result = null;
			float num = float.MinValue;
			List<ThingDef> list = PawnWeaponCandidates();
			for (int i = 0; i < list.Count; i++)
			{
				ThingDef thingDef = list[i];
				if (!IsExplosive(thingDef))
				{
					float num2 = (float)(int)EffectiveTech(thingDef) * 1000f + ((!thingDef.IsRangedWeapon) ? 0f : 500f) + Math.Min(thingDef.BaseMarketValue, 400f) * 0.01f;
					if (num2 > num)
					{
						num = num2;
						result = thingDef;
					}
				}
			}
			return result;
		}

		private static ThingDef AbsoluteFallback()
		{
			string[] array = new string[5] { "Bow_Short", "Bow_Recurve", "MeleeWeapon_Club", "MeleeWeapon_Knife", "Knife" };
			for (int i = 0; i < array.Length; i++)
			{
				ThingDef namedSilentFail = DefDatabase<ThingDef>.GetNamedSilentFail(array[i]);
				if (namedSilentFail != null && !IsExplosive(namedSilentFail))
				{
					return namedSilentFail;
				}
			}
			foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading)
			{
				if (item != null && item.IsWeapon && !item.IsRangedWeapon && !IsExplosive(item) && IsPawnWeapon(item) && (int)item.techLevel <= 3)
				{
					return item;
				}
			}
			return null;
		}

		private static ThingDef FallbackMelee()
		{
			string[] array = new string[3] { "MeleeWeapon_Club", "MeleeWeapon_Knife", "Knife" };
			for (int i = 0; i < array.Length; i++)
			{
				ThingDef namedSilentFail = DefDatabase<ThingDef>.GetNamedSilentFail(array[i]);
				if (namedSilentFail != null && IsAllowed(namedSilentFail))
				{
					return namedSilentFail;
				}
			}
			List<ThingDef> list = PawnWeaponCandidates();
			for (int j = 0; j < list.Count; j++)
			{
				if (!list[j].IsRangedWeapon)
				{
					return list[j];
				}
			}
			return null;
		}

		private static void Discard(Pawn pawn, Thing thing, ResearchLockedRaidGearSettings settings)
		{
			if (thing == null || thing.Destroyed)
			{
				return;
			}
			// 2.12.3: never on the ground at the map edge. Every visitor, trader and raider
			// is filtered when he arrives – dropping there piled up clothes at the edge of
			// the map with every group. Raiders now carry the locked item in their
			// inventory (loot when they are beaten); visitors and traders keep nothing.
			if (settings.dropLockedGear && pawn != null && pawn.Faction != Faction.OfPlayer)
			{
				if (pawn.HostileTo(Faction.OfPlayer) && pawn.inventory != null && !thing.Spawned && thing.holdingOwner == null)
				{
					if (settings.damageDroppedGear)
					{
						if (thing.def.useHitPoints && thing.MaxHitPoints > 0)
						{
							thing.HitPoints = Mathf.Max(1, Mathf.RoundToInt((float)thing.MaxHitPoints * Rand.Range(0.2f, 0.5f)));
						}
						Apparel ap = thing as Apparel;
						if (ap != null)
						{
							ap.Notify_PawnKilled();
						}
					}
					if (pawn.inventory.innerContainer.TryAdd(thing, true))
					{
						return;
					}
				}
				if (!thing.Destroyed && !thing.Spawned)
				{
					thing.Destroy();
				}
				return;
			}
			if (settings.dropLockedGear && pawn != null && pawn.Spawned && pawn.Map != null)
			{
				if (settings.damageDroppedGear)
				{
					if (thing.def.useHitPoints && thing.MaxHitPoints > 0)
					{
						thing.HitPoints = Mathf.Max(1, Mathf.RoundToInt((float)thing.MaxHitPoints * Rand.Range(0.2f, 0.5f)));
					}
					Apparel apparel = thing as Apparel;
					if (apparel != null)
					{
						apparel.Notify_PawnKilled();
					}
				}
				GenPlace.TryPlaceThing(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near);
				if (thing.Spawned)
				{
					return;
				}
			}
			if (!thing.Destroyed)
			{
				thing.Destroy();
			}
		}

		// ------------------------------------------------------------------
		// 2.11.0: "same or worse than the colony". Everyone who arrives may carry
		// gear the colony has crafted, or anything weaker of the same kind:
		// guns up to the strongest gun crafted, melee up to the strongest melee
		// weapon (material counts), armor up to the best armor crafted (ordinary
		// clothes always), and quality up to the best quality crafted.
		// ------------------------------------------------------------------
		private static int matchSig = int.MinValue;

		private static bool lastMatchOn;

		private static float capRanged;

		private static float capMelee;

		private static float capArmor;

		private static QualityCategory capQualW = QualityCategory.Normal;

		private static QualityCategory capQualA = QualityCategory.Normal;

		private static List<ThingDef> matchWeapons;

		private static List<ThingDef> matchApparel;

		private static HashSet<string> matchCrafted = new HashSet<string>();

		private static float armorFloor = -1f;

		public static bool MatchOn
		{
			get
			{
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				bool on = settings != null && settings.enabled && settings.matchColony && Current.Game != null && RaidGearGameComponent.Get() != null;
				if (on != lastMatchOn)
				{
					lastMatchOn = on;
					matchSig = int.MinValue;
					closestWeaponCache.Clear();
				}
				return on;
			}
		}

		public static float MeleeValue(ThingDef def, ThingDef stuff)
		{
			try
			{
				return def.GetStatValueAbstract(StatDefOf.MeleeWeapon_AverageDPS, stuff ?? GenStuff.DefaultStuffFor(def));
			}
			catch
			{
				return 0f;
			}
		}

		public static float ArmorValue(ThingDef def, ThingDef stuff)
		{
			try
			{
				ThingDef st = stuff ?? GenStuff.DefaultStuffFor(def);
				return def.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, st) + 0.5f * def.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, st);
			}
			catch
			{
				return 0f;
			}
		}

		private static float ArmorFloor()
		{
			if (armorFloor >= 0f)
			{
				return armorFloor;
			}
			armorFloor = 0.2f;
			string[] plain = new string[8] { "Apparel_BasicShirt", "Apparel_Pants", "Apparel_CollarShirt", "Apparel_Duster", "Apparel_Parka", "Apparel_TribalA", "Apparel_CowboyHat", "Apparel_Tuque" };
			for (int i = 0; i < plain.Length; i++)
			{
				ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(plain[i]);
				if (d != null)
				{
					armorFloor = Mathf.Max(armorFloor, ArmorValue(d, null));
				}
			}
			return armorFloor;
		}

		private static int matchFrame = -1;

		private static void RefreshMatch()
		{
			// once per frame is enough (raid generation runs inside one frame)
			if (Time.frameCount == matchFrame && matchSig != int.MinValue)
			{
				return;
			}
			matchFrame = Time.frameCount;
			RefreshResearchState();
			RaidGearGameComponent gc = RaidGearGameComponent.Get();
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (gc == null || settings == null)
			{
				return;
			}
			gc.EnsureCraftedSeeded();
			gc.EnsureProductSeeded();
			HashSet<string> partner = SharedCrafted.PartnerList();
			int count = gc.crafted.Count + ((partner == null) ? 0 : partner.Count * 7);
			int sig = ((count * 31 + researchFingerprint) * 31 + gc.recVersion) * 31 + settings.matchMarginPercent;
			if (sig == matchSig)
			{
				return;
			}
			matchSig = sig;
			matchCrafted = new HashSet<string>(gc.crafted);
			if (partner != null)
			{
				matchCrafted.UnionWith(partner);
			}
			float r = 0f;
			float m = 0f;
			float a = 0f;
			foreach (string name in matchCrafted)
			{
				ThingDef def = DefDatabase<ThingDef>.GetNamedSilentFail(name);
				if (def == null)
				{
					continue;
				}
				try
				{
					if (def.IsWeapon && !def.IsApparel)
					{
						if (def.IsRangedWeapon)
						{
							if (!IsExplosive(def))
							{
								r = Mathf.Max(r, PowerTech.RangedPower(def));
							}
						}
						else
						{
							m = Mathf.Max(m, MeleeValue(def, null));
						}
					}
					else if (def.IsApparel)
					{
						a = Mathf.Max(a, ArmorValue(def, null));
					}
				}
				catch
				{
				}
			}
			r = Mathf.Max(r, gc.recRanged);
			m = Mathf.Max(m, gc.recMelee);
			a = Mathf.Max(Mathf.Max(a, gc.recArmor), ArmorFloor());
			float k = 1f + (float)settings.matchMarginPercent / 100f;
			capRanged = r * k;
			capMelee = m * k;
			capArmor = a * k;
			capQualW = (gc.recQualW > QualityCategory.Normal) ? gc.recQualW : QualityCategory.Normal;
			capQualA = (gc.recQualA > QualityCategory.Normal) ? gc.recQualA : QualityCategory.Normal;
			matchWeapons = null;
			matchApparel = null;
			closestWeaponCache.Clear();
		}

		private static readonly Dictionary<ThingDef, bool> specialCache = new Dictionary<ThingDef, bool>();

		private static readonly Dictionary<ThingDef, List<RecipeDef>> recipeCache = new Dictionary<ThingDef, List<RecipeDef>>();

		// fire, explosions, EMP, toxins … – more dangerous than the plain damage number says
		public static bool IsSpecialRanged(ThingDef def)
		{
			bool v;
			if (specialCache.TryGetValue(def, out v))
			{
				return v;
			}
			v = false;
			try
			{
				if (IsExplosive(def))
				{
					v = true;
				}
				else if (def.Verbs != null)
				{
					for (int i = 0; i < def.Verbs.Count; i++)
					{
						VerbProperties vp = def.Verbs[i];
						ThingDef proj = (vp == null) ? null : vp.defaultProjectile;
						if (proj == null || proj.projectile == null)
						{
							continue;
						}
						ProjectileProperties pp = proj.projectile;
						if (pp.explosionRadius > 0.5f)
						{
							v = true;
						}
						DamageDef dd = pp.damageDef;
						if (dd != null && dd != DamageDefOf.Bullet && dd.defName != "Arrow" && dd.defName != "ArrowHighVelocity" && dd != DamageDefOf.Cut && dd != DamageDefOf.Blunt && dd != DamageDefOf.Stab && dd != DamageDefOf.Crush && dd != DamageDefOf.Scratch && dd != DamageDefOf.Bite)
						{
							v = true;
						}
					}
				}
			}
			catch
			{
			}
			specialCache[def] = v;
			return v;
		}

		// really researched: a recipe for it exists and the colony could use it right now
		public static bool StrictResearched(ThingDef def)
		{
			List<RecipeDef> list;
			if (!recipeCache.TryGetValue(def, out list))
			{
				list = new List<RecipeDef>();
				foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
				{
					if (r != null && r.products != null && r.products.Count > 0 && r.ProducedThingDef == def)
					{
						list.Add(r);
					}
				}
				recipeCache[def] = list;
			}
			for (int i = 0; i < list.Count; i++)
			{
				RecipeDef r = list[i];
				try
				{
					if (!r.AvailableNow)
					{
						continue;
					}
					foreach (ThingDef user in r.AllRecipeUsers)
					{
						if (user != null && AllFinishedOrNull(user.researchPrerequisites))
						{
							return true;
						}
					}
				}
				catch
				{
				}
			}
			return false;
		}

		private static bool AllFinishedOrNull(List<ResearchProjectDef> l)
		{
			if (l == null)
			{
				return true;
			}
			for (int i = 0; i < l.Count; i++)
			{
				if (l[i] != null && !l[i].IsFinished)
				{
					return false;
				}
			}
			return true;
		}

		public static bool MatchDefOk(ThingDef def, ThingDef stuff)
		{
			if (def == null || (!def.IsWeapon && !def.IsApparel))
			{
				return true;
			}
			RefreshMatch();
			// 2.12.0: something the colony never made has to be really researched (a usable recipe),
			// and fire / explosive / EMP weapons only once the colony has made that kind itself
			if (!matchCrafted.Contains(def.defName))
			{
				if (def.IsRangedWeapon && IsSpecialRanged(def))
				{
					return false;
				}
				if (!StrictResearched(def) && !(def.IsApparel && ArmorValue(def, stuff) <= ArmorFloor() + 0.001f && (int)def.techLevel <= 3))
				{
					return false;
				}
			}
			if (def.IsWeapon && !def.IsApparel)
			{
				if (def.IsRangedWeapon)
				{
					if (IsExplosive(def))
					{
						return matchCrafted.Contains(def.defName);
					}
					return PowerTech.RangedPower(def) <= capRanged + 0.001f;
				}
				return MeleeValue(def, stuff) <= capMelee + 0.001f;
			}
			return ArmorValue(def, stuff) <= capArmor + 0.001f;
		}

		// gear of an arriving pawn: researched and not better than the colony's
		// 2.14.0: "match the colony" + "only crafted" + strict: both must hold
		private static bool Strict
		{
			get
			{
				ResearchLockedRaidGearSettings st = ResearchLockedRaidGearMod.Settings;
				return st != null && st.onlyCrafted && st.strictCrafted && MatchOn;
			}
		}

		private static bool PawnThingOk(Thing t, bool beast)
		{
			if (t == null)
			{
				return true;
			}
			if (beast || !MatchOn)
			{
				return IsAllowed(t.def);
			}
			if (!IsResearchAllowed(t.def))
			{
				return false;
			}
			return MatchDefOk(t.def, t.Stuff) && (!Strict || CraftedOk(t.def));
		}

		// the same kind is fine, only its material is too good: same kind from ordinary material
		private static bool RestuffOk(Thing t)
		{
			if (t == null || t.Stuff == null || !MatchOn || !t.def.MadeFromStuff)
			{
				return false;
			}
			return IsResearchAllowed(t.def) && MatchDefOk(t.def, null) && (!Strict || CraftedOk(t.def));
		}

		private static void CapQuality(Thing t)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (t == null || settings == null || !settings.matchQuality || !MatchOn)
			{
				return;
			}
			CompQuality cq = t.TryGetComp<CompQuality>();
			if (cq == null)
			{
				return;
			}
			RefreshMatch();
			QualityCategory cap = t.def.IsApparel ? capQualA : capQualW;
			if (cq.Quality > cap)
			{
				cq.SetQuality(cap, ArtGenerationContext.Outsider);
			}
		}

		private static void CapPawnQuality(Pawn pawn)
		{
			if (!MatchOn)
			{
				return;
			}
			if (pawn.equipment != null)
			{
				List<ThingWithComps> eq = pawn.equipment.AllEquipmentListForReading;
				for (int i = 0; i < eq.Count; i++)
				{
					CapQuality(eq[i]);
				}
			}
			if (pawn.apparel != null)
			{
				List<Apparel> worn = pawn.apparel.WornApparel;
				for (int j = 0; j < worn.Count; j++)
				{
					CapQuality(worn[j]);
				}
			}
			if (pawn.inventory != null)
			{
				List<Thing> inv = pawn.inventory.innerContainer.InnerListForReading;
				for (int k = 0; k < inv.Count; k++)
				{
					if (inv[k] != null && (inv[k].def.IsWeapon || inv[k].def.IsApparel))
					{
						CapQuality(inv[k]);
					}
				}
			}
		}

		private static List<ThingDef> PawnWeaponCandidates()
		{
			if (!MatchOn)
			{
				return WeaponCandidates();
			}
			RefreshMatch();
			if (matchWeapons != null)
			{
				return matchWeapons;
			}
			List<ThingDef> list = new List<ThingDef>();
			List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < all.Count; i++)
			{
				ThingDef d = all[i];
				if (IsPawnWeapon(d) && IsResearchAllowed(d) && MatchDefOk(d, null) && (!Strict || CraftedOk(d)))
				{
					list.Add(d);
				}
			}
			matchWeapons = list;
			return list;
		}

		private static List<ThingDef> PawnApparelCandidates()
		{
			if (!MatchOn)
			{
				return ApparelCandidates();
			}
			RefreshMatch();
			if (matchApparel != null)
			{
				return matchApparel;
			}
			List<ThingDef> list = new List<ThingDef>();
			List<ThingDef> all = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < all.Count; i++)
			{
				ThingDef d = all[i];
				if (IsPawnApparel(d) && IsResearchAllowed(d) && MatchDefOk(d, null) && (!Strict || CraftedOk(d)))
				{
					list.Add(d);
				}
			}
			matchApparel = list;
			return list;
		}

		public static string MatchExplain()
		{
			if (!MatchOn)
			{
				return string.Empty;
			}
			RefreshMatch();
			return "RLRG_MatchNow".Translate(capRanged.ToString("0.0"), capMelee.ToString("0.0"), capArmor.ToString("0.00"), capQualW.GetLabel(), capQualA.GetLabel());
		}

		private static List<ThingDef> WeaponCandidates()
		{
			RefreshResearchState();
			if (weaponCandidates != null)
			{
				return weaponCandidates;
			}
			List<ThingDef> list = new List<ThingDef>();
			List<ThingDef> allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ThingDef thingDef = allDefsListForReading[i];
				if (IsPawnWeapon(thingDef) && IsAllowed(thingDef))
				{
					list.Add(thingDef);
				}
			}
			weaponCandidates = list;
			return list;
		}

		private static List<ThingDef> ApparelCandidates()
		{
			RefreshResearchState();
			if (apparelCandidates != null)
			{
				return apparelCandidates;
			}
			List<ThingDef> list = new List<ThingDef>();
			List<ThingDef> allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ThingDef thingDef = allDefsListForReading[i];
				if (IsPawnApparel(thingDef) && IsAllowed(thingDef))
				{
					list.Add(thingDef);
				}
			}
			apparelCandidates = list;
			return list;
		}

		public static bool IsPawnWeapon(ThingDef def)
		{
			if (def == null || !def.IsWeapon || def.IsApparel || def.destroyOnDrop)
			{
				return false;
			}
			if (def.weaponTags == null || def.weaponTags.Count == 0)
			{
				return false;
			}
			if (def.weaponTags.Contains("TurretGun") || def.weaponTags.Contains("GunSingleUse"))
			{
				return false;
			}
			return typeof(ThingWithComps).IsAssignableFrom(def.thingClass);
		}

		public static bool IsPawnApparel(ThingDef def)
		{
			if (def == null || !def.IsApparel || def.apparel == null || def.destroyOnDrop)
			{
				return false;
			}
			return (def.apparel.tags != null && def.apparel.tags.Count > 0) || (def.tradeTags != null && def.tradeTags.Count > 0);
		}

		private static ThingDef FindClosestWeapon(ThingDef original)
		{
			ThingDef value;
			if (closestWeaponCache.TryGetValue(original, out value))
			{
				return value;
			}
			ThingDef thingDef = null;
			float num = float.MaxValue;
			List<ThingDef> list = PawnWeaponCandidates();
			bool flag = LooksLikeGun(original);
			bool isRangedWeapon = original.IsRangedWeapon;
			for (int i = 0; i < list.Count; i++)
			{
				ThingDef thingDef2 = list[i];
				if (thingDef2 != original)
				{
					float num2 = 0f;
					if (thingDef2.IsRangedWeapon != isRangedWeapon)
					{
						num2 += 500f;
					}
					if (LooksLikeGun(thingDef2) != flag)
					{
						num2 += 200f;
					}
					num2 += (float)Math.Abs((int)(thingDef2.techLevel - original.techLevel)) * 80f;
					num2 += Math.Abs(thingDef2.BaseMarketValue - original.BaseMarketValue) * 0.08f;
					if (num2 < num)
					{
						num = num2;
						thingDef = thingDef2;
					}
				}
			}
			closestWeaponCache[original] = thingDef;
			return thingDef;
		}

		private static ThingDef FindClosestApparel(Pawn pawn, ThingDef original)
		{
			ThingDef result = null;
			float num = float.MaxValue;
			List<ThingDef> list = PawnApparelCandidates();
			for (int i = 0; i < list.Count; i++)
			{
				ThingDef thingDef = list[i];
				if (thingDef != original && (original.apparel == null || original.apparel.LastLayer == thingDef.apparel.LastLayer) && thingDef.apparel.PawnCanWear(pawn) && ApparelUtility.HasPartsToWear(pawn, thingDef))
				{
					float num2 = (float)Math.Abs((int)(thingDef.techLevel - original.techLevel)) * 80f;
					num2 += Math.Abs(thingDef.BaseMarketValue - original.BaseMarketValue) * 0.08f;
					if (!CoversSameParts(thingDef, original))
					{
						num2 += 300f;
					}
					if (num2 < num)
					{
						num = num2;
						result = thingDef;
					}
				}
			}
			return result;
		}

		private static bool CoversSameParts(ThingDef a, ThingDef b)
		{
			if (a.apparel == null || b.apparel == null || a.apparel.bodyPartGroups == null || b.apparel.bodyPartGroups == null)
			{
				return false;
			}
			for (int i = 0; i < a.apparel.bodyPartGroups.Count; i++)
			{
				if (b.apparel.bodyPartGroups.Contains(a.apparel.bodyPartGroups[i]))
				{
					return true;
				}
			}
			return false;
		}

		private static ThingDef PickStuff(ThingDef def, Thing original)
		{
			if (!def.MadeFromStuff)
			{
				return null;
			}
			if (original != null && original.Stuff != null && original.Stuff.stuffProps != null && original.Stuff.stuffProps.CanMake(def) && IsAllowed(original.Stuff) && (!MatchOn || MatchDefOk(def, original.Stuff)))
			{
				return original.Stuff;
			}
			return GenStuff.DefaultStuffFor(def);
		}

		private static void CopyCondition(Thing made, Thing original)
		{
			if (made != null && original != null)
			{
				CompQuality compQuality = original.TryGetComp<CompQuality>();
				CompQuality compQuality2 = made.TryGetComp<CompQuality>();
				if (compQuality != null && compQuality2 != null)
				{
					compQuality2.SetQuality(compQuality.Quality, ArtGenerationContext.Outsider);
				}
				if (original.def.useHitPoints && made.def.useHitPoints && original.MaxHitPoints > 0)
				{
					float num = (float)original.HitPoints / (float)original.MaxHitPoints;
					made.HitPoints = Mathf.Clamp(Mathf.RoundToInt((float)made.MaxHitPoints * num), 1, made.MaxHitPoints);
				}
			}
		}

		private static void Biocode(Thing thing, Pawn pawn)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings != null && settings.biocodeReplacements)
			{
				CompBiocodable compBiocodable = thing.TryGetComp<CompBiocodable>();
				if (compBiocodable != null && !compBiocodable.Biocoded)
				{
					compBiocodable.CodeFor(pawn);
				}
			}
		}

		private static void GiveWeapon(Pawn pawn, ThingDef def, Thing original)
		{
			if (pawn.equipment == null || def == null)
			{
				return;
			}
			Thing thing = ThingMaker.MakeThing(def, PickStuff(def, original));
			ThingWithComps thingWithComps = thing as ThingWithComps;
			if (thingWithComps == null)
			{
				thing.Destroy();
				return;
			}
			CopyCondition(thingWithComps, original);
			CapQuality(thingWithComps);
			Biocode(thingWithComps, pawn);
			Applying++;
			try
			{
				pawn.equipment.AddEquipment(thingWithComps);
			}
			finally
			{
				Applying--;
			}
		}

		private static void GiveApparel(Pawn pawn, ThingDef def, Thing original)
		{
			if (pawn.apparel == null || def == null || !ApparelUtility.HasPartsToWear(pawn, def))
			{
				return;
			}
			List<Apparel> wornApparel = pawn.apparel.WornApparel;
			for (int i = 0; i < wornApparel.Count; i++)
			{
				if (!ApparelUtility.CanWearTogether(def, wornApparel[i].def, pawn.RaceProps.body))
				{
					return;
				}
			}
			Apparel apparel = ThingMaker.MakeThing(def, PickStuff(def, original)) as Apparel;
			if (apparel == null)
			{
				return;
			}
			CopyCondition(apparel, original);
			CapQuality(apparel);
			Biocode(apparel, pawn);
			Applying++;
			try
			{
				pawn.apparel.Wear(apparel, false);
			}
			finally
			{
				Applying--;
			}
		}

		// 2.12.3: a blocked item of a player pawn goes on the ground; raiders keep it in the
		// inventory (loot), visitors and traders lose it – no piles at the map edge.
		public static void PutAside(Pawn pawn, Thing thing)
		{
			if (pawn.Faction == Faction.OfPlayer)
			{
				GenPlace.TryPlaceThing(thing, pawn.Position, pawn.Map, ThingPlaceMode.Near);
				return;
			}
			if (pawn.HostileTo(Faction.OfPlayer) && pawn.inventory != null && pawn.inventory.innerContainer.TryAdd(thing, true))
			{
				return;
			}
			if (!thing.Destroyed && !thing.Spawned && thing.holdingOwner == null)
			{
				thing.Destroy();
			}
		}

		public static bool BlocksPickup(Pawn pawn, Thing thing)
		{
			if (Applying > 0 || pawn == null || thing == null || !pawn.Spawned)
			{
				return false;
			}
			if (!ShouldFilter(pawn))
			{
				return false;
			}
			return !IsAllowed(thing.def);
		}
	}
	public sealed class Dialog_RaidGearList : Window
	{
		// 2.9.7: third page – what traders may not sell right now because of the world level
		private enum ListMode
		{
			Weapons,
			Apparel,
			Traders
		}

		private string search = string.Empty;

		private ListMode mode = ListMode.Weapons;

		private Vector2 scroll;

		private List<ThingDef> weaponDefs;

		private List<ThingDef> apparelDefs;

		private List<ThingDef> traderBlocked;

		public override Vector2 InitialSize
		{
			get
			{
				return new Vector2(820f, 680f);
			}
		}

		public Dialog_RaidGearList()
		{
			doCloseX = true;
			doCloseButton = true;
			absorbInputAroundWindow = true;
			forcePause = true;
		}

		private static int CompareDefs(ThingDef a, ThingDef b)
		{
			int num = a.techLevel.CompareTo(b.techLevel);
			return (num == 0) ? string.Compare(a.label ?? a.defName, b.label ?? b.defName, StringComparison.OrdinalIgnoreCase) : num;
		}

		private List<ThingDef> Defs(bool inGame)
		{
			if (mode == ListMode.Traders)
			{
				if (traderBlocked == null)
				{
					traderBlocked = BuildTraderBlocked(inGame);
				}
				return traderBlocked;
			}
			if (weaponDefs == null)
			{
				weaponDefs = new List<ThingDef>();
				apparelDefs = new List<ThingDef>();
				foreach (ThingDef item in DefDatabase<ThingDef>.AllDefsListForReading)
				{
					if (ResearchLockedGearFilter.IsPawnWeapon(item))
					{
						weaponDefs.Add(item);
					}
					else if (ResearchLockedGearFilter.IsPawnApparel(item))
					{
						apparelDefs.Add(item);
					}
				}
				weaponDefs.Sort(CompareDefs);
				apparelDefs.Sort(CompareDefs);
			}
			return (mode == ListMode.Apparel) ? apparelDefs : weaponDefs;
		}

		// goods a trader could carry (items and minifiable buildings), filtered with the same rule as trader stock
		private static List<ThingDef> BuildTraderBlocked(bool inGame)
		{
			List<ThingDef> list = new List<ThingDef>();
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!inGame || settings == null || !settings.enabled || !settings.worldItems)
			{
				return list;
			}
			List<ThingDef> allDefsListForReading = DefDatabase<ThingDef>.AllDefsListForReading;
			for (int i = 0; i < allDefsListForReading.Count; i++)
			{
				ThingDef thingDef = allDefsListForReading[i];
				if (thingDef == null || !thingDef.tradeability.TraderCanSell() || thingDef.IsCorpse || (thingDef.category != ThingCategory.Item && !thingDef.Minifiable))
				{
					continue;
				}
				try
				{
					if (!WorldTech.TraderDefAllowed(thingDef))
					{
						list.Add(thingDef);
					}
				}
				catch
				{
				}
			}
			list.Sort(CompareDefs);
			return list;
		}

		private bool ModeButton(Rect rect, ListMode m, string key)
		{
			if (mode == m)
			{
				Widgets.DrawHighlightSelected(rect);
			}
			string desc = key + "_Desc";
			if (desc.CanTranslate())
			{
				TooltipHandler.TipRegion(rect, desc.Translate());
			}
			return Widgets.ButtonText(rect, key.Translate());
		}

		public override void DoWindowContents(Rect inRect)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			bool flag = Current.Game != null && Find.ResearchManager != null;
			float y = inRect.y;
			if (ModeButton(new Rect(inRect.x, y, 140f, 30f), ListMode.Weapons, "RLRG_TabWeapons"))
			{
				mode = ListMode.Weapons;
			}
			if (ModeButton(new Rect(inRect.x + 150f, y, 140f, 30f), ListMode.Apparel, "RLRG_TabApparel"))
			{
				mode = ListMode.Apparel;
			}
			if (ModeButton(new Rect(inRect.x + 300f, y, 140f, 30f), ListMode.Traders, "RLRG_TabTraders"))
			{
				mode = ListMode.Traders;
				traderBlocked = null;   // bans or research may have changed – rebuild on every switch
			}
			Widgets.Label(new Rect(inRect.x + 455f, y + 5f, 70f, 26f), "RLRG_Search".Translate());
			search = Widgets.TextField(new Rect(inRect.x + 525f, y + 2f, inRect.width - 525f, 28f), search ?? string.Empty);
			y += 38f;
			bool traders = mode == ListMode.Traders;
			Rect hintRect = new Rect(inRect.x, y, inRect.width, 44f);
			if (!traders)
			{
				Widgets.Label(hintRect, (!flag) ? "RLRG_ListNoGame".Translate() : "RLRG_ListHint".Translate());
			}
			else if (!flag)
			{
				Widgets.Label(hintRect, "RLRG_TraderNoGame".Translate());
			}
			else if (!settings.enabled || !settings.worldItems)
			{
				Widgets.Label(hintRect, "RLRG_TraderOff".Translate());
			}
			else
			{
				TechLevel techLevel = WorldTech.Level();
				TechLevel techLevel2 = WorldTech.TraderMaxTech();
				Widgets.Label(new Rect(hintRect.x, hintRect.y, hintRect.width, 22f), "RLRG_TraderMax".Translate(techLevel2.ToStringHuman().CapitalizeFirst(), techLevel.ToStringHuman(), (int)techLevel2 - (int)techLevel));
				Widgets.Label(new Rect(hintRect.x, hintRect.y + 22f, hintRect.width, 22f), "RLRG_TraderCount".Translate(Defs(flag).Count));
				TooltipHandler.TipRegion(hintRect, "RLRG_TraderMax_Desc".Translate(techLevel2.ToStringHuman()));
			}
			y += 46f;
			List<ThingDef> list = Defs(flag);
			List<ThingDef> list2 = new List<ThingDef>();
			for (int i = 0; i < list.Count; i++)
			{
				ThingDef thingDef = list[i];
				if (string.IsNullOrEmpty(search) || (thingDef.label ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || thingDef.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
				{
					list2.Add(thingDef);
				}
			}
			float num = 26f;
			Rect outRect = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 50f);
			Rect viewRect = new Rect(0f, 0f, outRect.width - 20f, (float)list2.Count * num);
			Widgets.BeginScrollView(outRect, ref scroll, viewRect);
			int num2 = Mathf.Max(0, (int)(scroll.y / num));
			int num3 = Mathf.Min(list2.Count, num2 + (int)(outRect.height / num) + 2);
			bool flag2 = false;
			for (int j = num2; j < num3; j++)
			{
				ThingDef thingDef2 = list2[j];
				Rect rect = new Rect(0f, (float)j * num, viewRect.width, num);
				if (j % 2 == 1)
				{
					Widgets.DrawLightHighlight(rect);
				}
				if (traders)
				{
					DrawTraderRow(rect, thingDef2, num);
					continue;
				}
				bool checkOn = settings.bannedDefs.Contains(thingDef2.defName);
				bool flag3 = checkOn;
				Widgets.Checkbox(new Vector2(rect.x + 4f, rect.y + 1f), ref checkOn);
				if (checkOn != flag3)
				{
					if (checkOn)
					{
						settings.bannedDefs.Add(thingDef2.defName);
					}
					else
					{
						settings.bannedDefs.Remove(thingDef2.defName);
					}
					flag2 = true;
				}
				Widgets.Label(new Rect(rect.x + 36f, rect.y + 3f, rect.width * 0.5f, num), thingDef2.LabelCap);
				Widgets.Label(new Rect(rect.x + rect.width * 0.55f, rect.y + 3f, 120f, num), thingDef2.techLevel.ToStringHuman().CapitalizeFirst());
				string label;
				Color color;
				if (checkOn)
				{
					label = "RLRG_StatusBanned".Translate();
					color = new Color(1f, 0.45f, 0.45f);
				}
				else if (!flag)
				{
					label = "-";
					color = Color.gray;
				}
				else if (ResearchLockedGearFilter.IsAllowed(thingDef2))
				{
					label = "RLRG_StatusAllowed".Translate();
					color = new Color(0.5f, 0.9f, 0.5f);
				}
				else
				{
					label = "RLRG_StatusLocked".Translate();
					color = new Color(0.95f, 0.75f, 0.35f);
				}
				GUI.color = color;
				Widgets.Label(new Rect(rect.x + rect.width * 0.75f, rect.y + 3f, rect.width * 0.25f, num), label);
				GUI.color = Color.white;
				if (flag && Mouse.IsOver(rect))
				{
					TooltipHandler.TipRegion(rect, ResearchLockedGearFilter.Explain(thingDef2));
				}
			}
			Widgets.EndScrollView();
			if (flag2)
			{
				ResearchLockedGearFilter.InvalidateAll();
			}
		}

		private static void DrawTraderRow(Rect rect, ThingDef def, float height)
		{
			Widgets.Label(new Rect(rect.x + 36f, rect.y + 3f, rect.width * 0.5f, height), def.LabelCap);
			Widgets.Label(new Rect(rect.x + rect.width * 0.55f, rect.y + 3f, 120f, height), def.techLevel.ToStringHuman().CapitalizeFirst());
			bool gear = def.IsWeapon || def.IsApparel;
			GUI.color = new Color(0.95f, 0.75f, 0.35f);
			Widgets.Label(new Rect(rect.x + rect.width * 0.75f, rect.y + 3f, rect.width * 0.25f, height), (gear ? "RLRG_TraderBlockedGear" : "RLRG_TraderBlocked").Translate());
			GUI.color = Color.white;
			if (Mouse.IsOver(rect))
			{
				string tip = (gear ? "RLRG_TraderBlockedGear_Desc" : "RLRG_TraderBlocked_Desc").Translate(def.techLevel.ToStringHuman(), WorldTech.TraderMaxTech().ToStringHuman()).ToString();
				if (gear)
				{
					string text = ResearchLockedGearFilter.Explain(def);
					if (!string.IsNullOrEmpty(text))
					{
						tip = tip + "\n\n" + text;
					}
				}
				TooltipHandler.TipRegion(rect, tip);
			}
		}

		public override void PostClose()
		{
			base.PostClose();
			ResearchLockedRaidGearMod.Settings.Write();
		}
	}
	public sealed class RaidGearGameComponent : GameComponent
	{
		public HashSet<int> Kept = new HashSet<int>();

		private TechLevel lastWorldLevel;

		public List<int> evTicks = new List<int>();

		public List<string> evCats = new List<string>();

		public List<string> evDefs = new List<string>();

		public int lastTraderTick = -1;

		public int lastVisitorTick = -1;

		public int lastGuaranteeTry = -1;

		public int lastDiagLog = -1;

		public HashSet<string> crafted = new HashSet<string>();

		public bool craftedSeeded;

		// 2.11.0: the best things the colony really made (material and quality count)
		public float recRanged;

		public float recMelee;

		public float recArmor;

		public QualityCategory recQualW = QualityCategory.Normal;

		public QualityCategory recQualA = QualityCategory.Normal;

		public bool productSeeded;

		public int recVersion;

		public void NoteProduct(Thing t, bool fromColony)
		{
			if (t == null || t.def == null || (!t.def.IsWeapon && !t.def.IsApparel))
			{
				return;
			}
			bool changed = false;
			try
			{
				if (t.def.IsWeapon && !t.def.IsApparel)
				{
					if (t.def.IsRangedWeapon)
					{
						if (!ResearchLockedGearFilter.IsExplosive(t.def))
						{
							float r = PowerTech.RangedPower(t.def);
							if (r > recRanged)
							{
								recRanged = r;
								changed = true;
							}
						}
					}
					else
					{
						float m = ResearchLockedGearFilter.MeleeValue(t.def, t.Stuff);
						if (m > recMelee)
						{
							recMelee = m;
							changed = true;
						}
					}
				}
				else
				{
					float a = ResearchLockedGearFilter.ArmorValue(t.def, t.Stuff);
					if (a > recArmor)
					{
						recArmor = a;
						changed = true;
					}
				}
				QualityCategory q;
				if (t.TryGetQuality(out q))
				{
					if (t.def.IsApparel)
					{
						if (q > recQualA)
						{
							recQualA = q;
							changed = true;
						}
					}
					else if (q > recQualW)
					{
						recQualW = q;
						changed = true;
					}
				}
			}
			catch
			{
			}
			if (changed)
			{
				recVersion++;
			}
		}

		// an ongoing game: the gear of crafted kinds the colony already has counts as made
		public void EnsureProductSeeded()
		{
			if (productSeeded || Current.Game == null || Current.ProgramState != ProgramState.Playing || Find.Maps == null)
			{
				return;
			}
			productSeeded = true;
			try
			{
				HashSet<string> kinds = new HashSet<string>(crafted);
				HashSet<string> partner = SharedCrafted.PartnerList();
				if (partner != null)
				{
					kinds.UnionWith(partner);
				}
				foreach (Map map in Find.Maps)
				{
					if (map == null || !map.IsPlayerHome)
					{
						continue;
					}
					foreach (Pawn p in map.mapPawns.FreeColonistsSpawned)
					{
						if (p.equipment != null)
						{
							foreach (ThingWithComps eq in p.equipment.AllEquipmentListForReading)
							{
								if (eq != null && kinds.Contains(eq.def.defName))
								{
									NoteProduct(eq, false);
								}
							}
						}
						if (p.apparel != null)
						{
							foreach (Apparel ap in p.apparel.WornApparel)
							{
								if (ap != null && kinds.Contains(ap.def.defName))
								{
									NoteProduct(ap, false);
								}
							}
						}
					}
					foreach (Thing w in map.listerThings.ThingsInGroup(ThingRequestGroup.Weapon))
					{
						if (w != null && w.IsInAnyStorage() && kinds.Contains(w.def.defName))
						{
							NoteProduct(w, false);
						}
					}
					foreach (Thing a in map.listerThings.ThingsInGroup(ThingRequestGroup.Apparel))
					{
						if (a != null && a.IsInAnyStorage() && kinds.Contains(a.def.defName))
						{
							NoteProduct(a, false);
						}
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] colony gear check failed: " + ex.Message);
			}
		}

		public RaidGearGameComponent(Game game)
		{
		}

		public override void FinalizeInit()
		{
			base.FinalizeInit();
			// 2.9.4: per-game static state must not leak from a previously loaded save
			ThreatCausers.Reset();
			WorldGiveQuestPatch.FromStoryteller = 0;
			WorldGiveQuestPatch.CurrentParms = null;
			WorldEvents.LastReplacement = null;
			WorldQuestGeneratedPatch.Reset();
			ResearchLockedGearFilter.InvalidateAll();
		}

		public void ResetCrafted()
		{
			crafted.Clear();
			craftedSeeded = true;
			recRanged = 0f;
			recMelee = 0f;
			recArmor = 0f;
			recQualW = QualityCategory.Normal;
			recQualA = QualityCategory.Normal;
			productSeeded = true;
			recVersion++;
			ResearchLockedGearFilter.InvalidateAll();
			MLog.Info("[World at Your Level] Only crafted: list cleared, only gear crafted from now on counts.");
		}

		public void NoteCrafted(ThingDef def)
		{
			if (def != null && (def.IsWeapon || def.IsApparel) && crafted.Add(def.defName))
			{
				ResearchLockedGearFilter.InvalidateAll();
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				if (settings != null && settings.enabled && settings.onlyCrafted && settings.worldMessages)
				{
					Messages.Message("RLRG_MsgCraftedNew".Translate(def.LabelCap), MessageTypeDefOf.NeutralEvent, false);
				}
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
				MLog.Info("[World at Your Level] Only crafted: took over " + crafted.Count + " gear kinds from the existing game.");
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] crafted seed failed: " + ex.Message);
			}
		}

		public static RaidGearGameComponent Get()
		{
			return (Current.Game == null) ? null : Current.Game.GetComponent<RaidGearGameComponent>();
		}

		public void Record(IncidentDef def)
		{
			if (def != null && Find.TickManager != null)
			{
				evTicks.Add(Find.TickManager.TicksGame);
				evCats.Add((def.category == null) ? "Misc" : def.category.defName);
				evDefs.Add(def.defName);
				while (evTicks.Count > 300)
				{
					evTicks.RemoveAt(0);
					evCats.RemoveAt(0);
					evDefs.RemoveAt(0);
				}
			}
		}

		public override void ExposeData()
		{
			Scribe_Collections.Look(ref Kept, "rlrgKept", LookMode.Value);
			if (Kept == null)
			{
				Kept = new HashSet<int>();
			}
			Scribe_Values.Look(ref lastWorldLevel, "rlrgWorldLevel", TechLevel.Undefined);
			Scribe_Collections.Look(ref evTicks, "rlrgEvTicks", LookMode.Value);
			Scribe_Collections.Look(ref evCats, "rlrgEvCats", LookMode.Value);
			Scribe_Collections.Look(ref evDefs, "rlrgEvDefs", LookMode.Value);
			Scribe_Values.Look(ref lastTraderTick, "rlrgLastTrader", -1);
			Scribe_Values.Look(ref lastVisitorTick, "rlrgLastVisitor", -1);
			Scribe_Values.Look(ref lastGuaranteeTry, "rlrgLastGuarantee", -1);
			Scribe_Values.Look(ref lastDiagLog, "rlrgLastDiagLog", -1);
			Scribe_Collections.Look(ref crafted, "rlrgCrafted", LookMode.Value);
			if (crafted == null)
			{
				crafted = new HashSet<string>();
			}
			Scribe_Values.Look(ref craftedSeeded, "rlrgCraftedSeeded", false);
			Scribe_Values.Look(ref recRanged, "rlrgRecRanged", 0f);
			Scribe_Values.Look(ref recMelee, "rlrgRecMelee", 0f);
			Scribe_Values.Look(ref recArmor, "rlrgRecArmor", 0f);
			Scribe_Values.Look(ref recQualW, "rlrgRecQualW", QualityCategory.Normal);
			Scribe_Values.Look(ref recQualA, "rlrgRecQualA", QualityCategory.Normal);
			Scribe_Values.Look(ref productSeeded, "rlrgProductSeeded", false);
			if (evTicks == null || evCats == null || evDefs == null || evTicks.Count != evCats.Count || evTicks.Count != evDefs.Count)
			{
				evTicks = new List<int>();
				evCats = new List<string>();
				evDefs = new List<string>();
			}
		}

		public override void GameComponentTick()
		{
			if (Find.TickManager == null)
			{
				return;
			}
			if (Find.TickManager.TicksGame % 60 == 0)
			{
				try
				{
					ThreatCausers.Tick();
				}
				catch (Exception ex)
				{
					MLog.Warn("[World at Your Level] causer check failed: " + ex.Message);
				}
			}
			if (Find.TickManager.TicksGame % 2500 != 0)
			{
				return;
			}
			try
			{
				VisitorDiag.HourlyCheck(this);
			}
			catch (Exception ex2)
			{
				MLog.Warn("[World at Your Level] trader guarantee failed: " + ex2.Message);
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || !settings.enabled)
			{
				return;
			}
			TechLevel techLevel = WorldTech.Level();
			if (lastWorldLevel == TechLevel.Undefined)
			{
				lastWorldLevel = techLevel;
			}
			else if (techLevel != lastWorldLevel)
			{
				bool flag = (int)techLevel > (int)lastWorldLevel;
				lastWorldLevel = techLevel;
				if (settings.worldMessages)
				{
					Messages.Message(((!flag) ? "RLRG_WorldDown" : "RLRG_WorldUp").Translate(techLevel.ToStringHuman()), (!flag) ? MessageTypeDefOf.NeutralEvent : MessageTypeDefOf.PositiveEvent, false);
				}
			}
		}
	}
	public static class ThreatCausers
	{
		private static bool knownReady;

		private static readonly HashSet<int> knownCausers = new HashSet<int>();

		private static readonly List<Thing> pendingKill = new List<Thing>();

		private static readonly Dictionary<ThingDef, int> defTech = new Dictionary<ThingDef, int>();

		public static void Reset()
		{
			knownReady = false;
			knownCausers.Clear();
			pendingKill.Clear();
			scannedAfterLoad = false;
		}

		private static bool Enabled
		{
			get
			{
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				return settings != null && settings.enabled && settings.worldCausers && Current.Game != null && Find.ResearchManager != null;
			}
		}

		private static bool Has(string s, string part)
		{
			return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		private static int ThreatTech(string name, ThingDef def)
		{
			if (string.IsNullOrEmpty(name))
			{
				return 0;
			}
			if (!Has(name, "CauseGameCondition") && !Has(name, "ConditionCauser") && !Has(name, "Spewer"))
			{
				return 0;
			}
			int num = (int)((def != null) ? def.techLevel : TechLevel.Undefined);
			return (num > 1) ? num : 6;
		}

		private static int TechOf(ThingDef def)
		{
			int value;
			if (defTech.TryGetValue(def, out value))
			{
				return value;
			}
			string text = def.defName ?? string.Empty;
			if (def.comps != null)
			{
				for (int i = 0; i < def.comps.Count; i++)
				{
					if (def.comps[i] != null && def.comps[i].compClass != null)
					{
						text += def.comps[i].compClass.Name;
					}
				}
			}
			value = ThreatTech(text, def);
			defTech[def] = value;
			return value;
		}

		public static bool UnwantedMech(Pawn pawn)
		{
			ResearchLockedRaidGearSettings st = ResearchLockedRaidGearMod.Settings;
			return st != null && st.worldMechanoids && pawn != null && !pawn.Dead && pawn.RaceProps != null && pawn.RaceProps.IsMechanoid
			       && pawn.Faction != null && !pawn.Faction.IsPlayer && pawn.Map != null && !pawn.IsColonyMech
			       && pawn.HostileTo(Faction.OfPlayer)
			       && (!WorldTech.FactionWithinWorld(pawn.Faction) || !MechEscorts.Researched(pawn.def));
		}

		// 2.13.2: once after loading a save – mechanoids and drones that were already on the maps
		private static bool scannedAfterLoad;

		public static void ResetScan() => scannedAfterLoad = false;

		private static void ScanOnce()
		{
			if (scannedAfterLoad || Find.Maps == null)
				return;
			scannedAfterLoad = true;
			try
			{
				foreach (Map m in Find.Maps)
					foreach (Pawn p in new List<Pawn>(m.mapPawns.AllPawnsSpawned))
						if (UnwantedMech(p))
							pendingKill.Add(p);
			}
			catch
			{
			}
		}

		public static void OnSpawn(Thing thing, bool respawningAfterLoad)
		{
			// 2.13.2: every hostile mechanoid or drone – of any faction (mechanoids, human factions like the Wolfein
			// MegaCorp, mod factions), from raids, quests, quest sites, events or map generation – that the colony
			// could not build yet (no researched recipe for it), or whose faction is above the world level, is removed
			if (!respawningAfterLoad && thing is Pawn pawn && Enabled)
			{
				try
				{
					if (UnwantedMech(pawn))
						pendingKill.Add(pawn);
				}
				catch
				{
				}
				return;
			}
			if (!respawningAfterLoad && thing != null && !(thing is Pawn) && thing.def != null && Enabled)
			{
				int num = TechOf(thing.def);
				if (num != 0 && num > (int)WorldTech.Level() && (thing.Faction == null || !thing.Faction.IsPlayer))
				{
					pendingKill.Add(thing);
				}
				else if (EnemyTurretTooGood(thing))
				{
					pendingKill.Add(thing);
				}
			}
		}

		// 2.14.0: a turret of another faction (quest site, base, siege …) stays only when you have
		// built a turret of the same tech level or higher yourself
		private static int ownTurretTech = -1;
		private static int ownTurretTick = -99999;

		private static bool IsTurretDef(ThingDef d)
		{
			return d != null && d.category == ThingCategory.Building && d.thingClass != null && typeof(Building_Turret).IsAssignableFrom(d.thingClass);
		}

		private static int TurretTech(ThingDef d)
		{
			int t = (int)d.techLevel;
			if (t <= (int)TechLevel.Animal)
				t = (int)TechLevel.Industrial;
			return t;
		}

		private static int OwnTurretTech()
		{
			int now = Find.TickManager?.TicksGame ?? 0;
			if (ownTurretTech >= 0 && now - ownTurretTick < 2500)
				return ownTurretTech;
			int best = 0;
			try
			{
				foreach (Map m in Find.Maps)
					foreach (Building b in m.listerBuildings.allBuildingsColonist)
						if (IsTurretDef(b.def))
							best = Math.Max(best, TurretTech(b.def));
			}
			catch
			{
			}
			ownTurretTech = best;
			ownTurretTick = now;
			return best;
		}

		private static bool EnemyTurretTooGood(Thing thing)
		{
			ResearchLockedRaidGearSettings st = ResearchLockedRaidGearMod.Settings;
			if (st == null || !st.siteTurrets || !IsTurretDef(thing.def))
				return false;
			if (thing.Faction != null && thing.Faction.IsPlayer)
				return false;
			if (st.exemptAncients && (thing.Faction == Faction.OfAncients || thing.Faction == Faction.OfAncientsHostile))
				return false;
			return TurretTech(thing.def) > OwnTurretTech();
		}

		public static void Tick()
		{
			if (!Enabled)
			{
				pendingKill.Clear();
				return;
			}
			ScanOnce();
			// 2.13.2: one message per kind, not one per pawn
			var told = new Dictionary<string, int>();
			for (int i = 0; i < pendingKill.Count; i++)
			{
				Thing thing = pendingKill[i];
				if (thing != null && !thing.Destroyed)
				{
					string text = thing.def.LabelCap;
					try
					{
						thing.Destroy();
					}
					catch
					{
						continue;
					}
					told.TryGetValue(text, out int n);
					told[text] = n + 1;
				}
			}
			foreach (var kv in told)
				Messages.Message("RLRG_CauserBlocked".Translate(kv.Value > 1 ? kv.Key + " ×" + kv.Value : kv.Key), MessageTypeDefOf.NeutralEvent, false);
			pendingKill.Clear();
			if (Find.TickManager != null && Find.TickManager.TicksGame % 600 == 0)
			{
				HoldBackWorldCausers();
			}
		}

		private static void HoldBackWorldCausers()
		{
			if (Find.WorldObjects == null)
			{
				return;
			}
			List<WorldObject> allWorldObjects = Find.WorldObjects.AllWorldObjects;
			List<WorldObject> list = new List<WorldObject>();
			for (int i = 0; i < allWorldObjects.Count; i++)
			{
				WorldObject worldObject = allWorldObjects[i];
				if (worldObject == null || (worldObject.Faction != null && worldObject.Faction.IsPlayer))
				{
					continue;
				}
				int num = CauserTech(worldObject);
				if (num == 0)
				{
					continue;
				}
				bool flag = knownReady && !knownCausers.Contains(worldObject.ID);
				knownCausers.Add(worldObject.ID);
				if (flag && num > (int)WorldTech.Level())
				{
					if (worldObject.questTags != null && worldObject.questTags.Count > 0)
					{
						EndQuestsWith(worldObject);
					}
					list.Add(worldObject);
				}
			}
			knownReady = true;
			for (int j = 0; j < list.Count; j++)
			{
				WorldObject worldObject2 = list[j];
				string text = ((worldObject2.def == null) ? "?" : (worldObject2.def.label ?? worldObject2.def.defName));
				try
				{
					worldObject2.Destroy();
				}
				catch
				{
					continue;
				}
				Messages.Message("RLRG_CauserBlocked".Translate(text), MessageTypeDefOf.NeutralEvent, false);
			}
		}

		private static void EndQuestsWith(WorldObject wo)
		{
			if (Find.QuestManager == null)
			{
				return;
			}
			foreach (Quest item in Find.QuestManager.QuestsListForReading)
			{
				if (item == null || (item.State != 0 && item.State != QuestState.Ongoing))
				{
					continue;
				}
				bool flag = false;
				foreach (QuestPart item2 in item.PartsListForReading)
				{
					QuestPart_SpawnWorldObject questPart_SpawnWorldObject = item2 as QuestPart_SpawnWorldObject;
					if (questPart_SpawnWorldObject != null && questPart_SpawnWorldObject.worldObject == wo)
					{
						flag = true;
						break;
					}
				}
				if (flag)
				{
					try
					{
						item.hidden = true;
						item.End(QuestEndOutcome.Unknown, false, false);
					}
					catch (Exception ex)
					{
						MLog.Warn("[World at Your Level] quest end failed: " + ex.Message);
					}
				}
			}
		}

		public static bool QuestHasCauserAboveWorld(Quest quest)
		{
			if (quest == null || !Enabled)
			{
				return false;
			}
			int num = (int)WorldTech.Level();
			foreach (QuestPart item in quest.PartsListForReading)
			{
				QuestPart_SpawnWorldObject questPart_SpawnWorldObject = item as QuestPart_SpawnWorldObject;
				if (questPart_SpawnWorldObject != null && questPart_SpawnWorldObject.worldObject != null)
				{
					int num2 = CauserTech(questPart_SpawnWorldObject.worldObject);
					if (num2 > num)
					{
						return true;
					}
				}
			}
			return false;
		}

		public static void RemoveQuestCausers(Quest quest)
		{
			if (quest == null)
			{
				return;
			}
			foreach (QuestPart item in quest.PartsListForReading)
			{
				QuestPart_SpawnWorldObject questPart_SpawnWorldObject = item as QuestPart_SpawnWorldObject;
				if (questPart_SpawnWorldObject != null && questPart_SpawnWorldObject.worldObject != null && questPart_SpawnWorldObject.worldObject.Spawned && CauserTech(questPart_SpawnWorldObject.worldObject) > (int)WorldTech.Level())
				{
					try
					{
						questPart_SpawnWorldObject.worldObject.Destroy();
					}
					catch
					{
					}
				}
			}
		}

		private static int CauserTech(WorldObject wo)
		{
			if (wo == null || wo.def == null)
			{
				return 0;
			}
			if (ThreatTech(wo.def.defName, null) > 0)
			{
				return 6;
			}
			Site site = wo as Site;
			if (site == null || site.parts == null)
			{
				return 0;
			}
			int num = 0;
			for (int i = 0; i < site.parts.Count; i++)
			{
				SitePart sitePart = site.parts[i];
				if (sitePart != null && sitePart.def != null)
				{
					string text = sitePart.def.defName ?? string.Empty;
					if (sitePart.def.Worker != null)
					{
						text += sitePart.def.Worker.GetType().Name;
					}
					num = Math.Max(num, ThreatTech(text, null));
				}
			}
			return num;
		}
	}
	public static class CauserSpawnPatch
	{
		public static void Postfix(Thing __instance, bool respawningAfterLoad)
		{
			ThreatCausers.OnSpawn(__instance, respawningAfterLoad);
		}
	}
	public static class VisitorDiag
	{
		public static readonly string[] Defs = new string[3] { "TraderCaravanArrival", "VisitorGroup", "TravelerGroup" };

		public static bool Running;

		private static readonly MethodInfo firedTooRecently = AccessTools.Method(typeof(IncidentWorker), "FiredTooRecently");

		private static readonly string[] ThreatDefs = new string[4] { "RaidEnemy", "ManhunterPack", "Infestation", "MechCluster" };

		private static string Days(int ticks)
		{
			return ((float)ticks / 60000f).ToString("0.#");
		}

		public static void NoteArrival(IncidentWorker w)
		{
			if (w == null)
			{
				return;
			}
			RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
			if (raidGearGameComponent != null && Find.TickManager != null)
			{
				if (w is IncidentWorker_TraderCaravanArrival)
				{
					raidGearGameComponent.lastTraderTick = Find.TickManager.TicksGame;
				}
				else if (w is IncidentWorker_NeutralGroup)
				{
					raidGearGameComponent.lastVisitorTick = Find.TickManager.TicksGame;
				}
			}
		}

		public static Map HomeMap()
		{
			Map result = null;
			int num = -1;
			List<Map> maps = Find.Maps;
			for (int i = 0; i < maps.Count; i++)
			{
				Map map = maps[i];
				if (map != null && map.IsPlayerHome)
				{
					int freeColonistsSpawnedCount = map.mapPawns.FreeColonistsSpawnedCount;
					if (freeColonistsSpawnedCount > num)
					{
						result = map;
						num = freeColonistsSpawnedCount;
					}
				}
			}
			return result;
		}

		public static string BlockingLord(Map map, Faction f)
		{
			Faction faction = map.ParentFaction ?? Faction.OfPlayer;
			List<Lord> lords = map.lordManager.lords;
			for (int i = 0; i < lords.Count; i++)
			{
				Lord lord = lords[i];
				if (lord == null || lord.faction == null || lord.faction == f || lord.faction == faction || !lord.AnyActivePawn)
				{
					continue;
				}
				LordJob lordJob = lord.LordJob;
				if ((lordJob == null || lordJob.CanBlockHostileVisitors) && !(lordJob is LordJob_VoluntarilyJoinable) && lord.faction.HostileTo(f) && !lord.faction.HostileTo(faction))
				{
					string text = lord.faction.Name;
					if (lord.ownedPawns != null && lord.ownedPawns.Count > 0 && lord.ownedPawns[0] != null)
					{
						string text2 = text;
						text = text2 + ": " + lord.ownedPawns[0].LabelShortCap + ((lord.ownedPawns.Count <= 1) ? string.Empty : (" +" + (lord.ownedPawns.Count - 1))) + " @ " + lord.ownedPawns[0].Position;
					}
					return text + " [" + ((lordJob == null) ? "-" : lordJob.GetType().Name) + "]";
				}
			}
			return null;
		}

		private static string Why(IncidentDef def, IncidentParms p, Map map)
		{
			List<string> list = new List<string>();
			try
			{
				if (Find.Scenario != null)
				{
					foreach (ScenPart allPart in Find.Scenario.AllParts)
					{
						ScenPart_DisableIncident scenPart_DisableIncident = allPart as ScenPart_DisableIncident;
						if (scenPart_DisableIncident != null && scenPart_DisableIncident.Incident == def)
						{
							list.Add("RLRG_Diag_R_Scenario".Translate().ToString());
						}
					}
				}
				if (Find.Storyteller != null && Find.Storyteller.difficulty != null && !Find.Storyteller.difficulty.AllowedBy(def.disabledWhen))
				{
					list.Add("RLRG_Diag_R_Difficulty".Translate().ToString());
				}
				if (GenDate.DaysPassedSinceSettle < def.earliestDay)
				{
					list.Add("RLRG_Diag_R_Early".Translate(def.earliestDay).ToString());
				}
				if (!def.TargetAllowed(map))
				{
					list.Add("RLRG_Diag_R_Target".Translate().ToString());
				}
				BiomeDef biome = map.Biome;
				if ((!def.allowedBiomes.NullOrEmpty() && !def.allowedBiomes.Contains(biome)) || (!def.disallowedBiomes.NullOrEmpty() && def.disallowedBiomes.Contains(biome)))
				{
					list.Add("RLRG_Diag_R_Biome".Translate().ToString());
				}
				if (firedTooRecently != null && (bool)firedTooRecently.Invoke(def.Worker, new object[1] { map }))
				{
					list.Add("RLRG_Diag_R_Recent".Translate().ToString());
				}
				foreach (GameCondition activeCondition in map.GameConditionManager.ActiveConditions)
				{
					if (activeCondition != null && activeCondition.def != null && activeCondition.def.preventNeutralVisitors)
					{
						list.Add("RLRG_Diag_R_Condition".Translate(activeCondition.LabelCap).ToString());
					}
				}
				IncidentWorker_NeutralGroup incidentWorker_NeutralGroup = def.Worker as IncidentWorker_NeutralGroup;
				if (incidentWorker_NeutralGroup != null && list.Count == 0)
				{
					bool flag = false;
					bool flag2 = false;
					foreach (Faction item in Find.FactionManager.AllFactionsListForReading)
					{
						if (incidentWorker_NeutralGroup.FactionCanBeGroupSource(item, p))
						{
							flag = true;
						}
						else if (incidentWorker_NeutralGroup.FactionCanBeGroupSource(item, p, true))
						{
							flag2 = true;
						}
					}
					if (!flag)
					{
						list.Add(((!flag2) ? "RLRG_Diag_R_NoFaction" : "RLRG_Diag_R_OnlyDesperate").Translate().ToString());
					}
				}
			}
			catch (Exception ex)
			{
				list.Add("error: " + ex.Message);
			}
			if (list.Count == 0)
			{
				list.Add("RLRG_Diag_R_Other".Translate().ToString());
			}
			return string.Join("; ", list.ToArray());
		}

		private static string FactionLine(Faction f, IncidentWorker_NeutralGroup trader, IncidentParms tp, Map map)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			List<string> list = new List<string>();
			if (f.defeated)
			{
				list.Add("RLRG_Diag_F_Defeated".Translate().ToString());
			}
			if (f.HostileTo(Faction.OfPlayer))
			{
				list.Add("RLRG_Diag_F_Hostile".Translate(f.PlayerGoodwill).ToString());
			}
			if (f.def.caravanTraderKinds == null || f.def.caravanTraderKinds.Count <= 0 || f.def.pawnGroupMakers == null || !f.def.pawnGroupMakers.Any((PawnGroupMaker g) => g.kindDef == PawnGroupKindDefOf.Trader))
			{
				list.Add("RLRG_Diag_F_NoTraders".Translate().ToString());
			}
			string text = BlockingLord(map, f);
			if (text != null)
			{
				list.Add("RLRG_Diag_F_Lord".Translate(text).ToString());
			}
			if (settings != null && settings.enabled && settings.worldVisitors && !WorldTech.FactionWithinWorld(f))
			{
				list.Add("RLRG_Diag_F_World".Translate().ToString());
			}
			bool flag = false;
			bool flag2 = false;
			if (trader != null && tp != null)
			{
				try
				{
					flag = trader.FactionCanBeGroupSource(f, tp);
					flag2 = !flag && trader.FactionCanBeGroupSource(f, tp, true);
				}
				catch
				{
				}
			}
			if (flag)
			{
				return "RLRG_Diag_F_Ok".Translate().ToString();
			}
			FloatRange allowedArrivalTemperatureRange = f.def.allowedArrivalTemperatureRange;
			if (flag2 && list.Count == 0)
			{
				return "RLRG_Diag_F_Desperate".Translate(allowedArrivalTemperatureRange.min.ToStringTemperature("F0") + " .. " + allowedArrivalTemperatureRange.max.ToStringTemperature("F0")).ToString();
			}
			if (list.Count == 0)
			{
				if (!allowedArrivalTemperatureRange.Includes(map.mapTemperature.OutdoorTemp) || !allowedArrivalTemperatureRange.Includes(map.mapTemperature.SeasonalTemp))
				{
					list.Add("RLRG_Diag_F_Temp".Translate(allowedArrivalTemperatureRange.min.ToStringTemperature("F0") + " .. " + allowedArrivalTemperatureRange.max.ToStringTemperature("F0")).ToString());
				}
				else
				{
					list.Add("RLRG_Diag_F_Other".Translate().ToString());
				}
			}
			return "RLRG_Diag_F_No".Translate(string.Join("; ", list.ToArray())).ToString();
		}

		public static string Report(Map map)
		{
			StringBuilder stringBuilder = new StringBuilder();
			if (map == null || Current.Game == null)
			{
				return "RLRG_Diag_NoMap".Translate().ToString();
			}
			int ticksGame = Find.TickManager.TicksGame;
			RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			stringBuilder.AppendLine("RLRG_Diag_Map".Translate((map.Parent == null) ? "?" : map.Parent.LabelCap.ToString(), map.mapTemperature.OutdoorTemp.ToStringTemperature("F0"), map.mapTemperature.SeasonalTemp.ToStringTemperature("F0")).ToString());
			if (raidGearGameComponent != null)
			{
				stringBuilder.AppendLine((raidGearGameComponent.lastTraderTick < 0) ? "RLRG_Diag_LastTraderNone".Translate().ToString() : "RLRG_Diag_LastTrader".Translate(Days(ticksGame - raidGearGameComponent.lastTraderTick)).ToString());
				if (raidGearGameComponent.lastVisitorTick >= 0)
				{
					stringBuilder.AppendLine("RLRG_Diag_LastVisitor".Translate(Days(ticksGame - raidGearGameComponent.lastVisitorTick)).ToString());
				}
			}
			if (settings != null)
			{
				stringBuilder.AppendLine((!settings.traderGuarantee) ? "RLRG_Diag_GuaranteeOff".Translate().ToString() : "RLRG_Diag_GuaranteeOn".Translate(settings.traderGuaranteeDays).ToString());
			}
			int num = 0;
			int num2 = 0;
			foreach (Faction item in Find.FactionManager.AllFactionsListForReading)
			{
				if (!item.Hidden && !item.IsPlayer && !item.temporary)
				{
					if (item.def.CanEverBeNonHostile)
					{
						num2++;
					}
					if (!item.HostileTo(Faction.OfPlayer))
					{
						num++;
					}
				}
			}
			stringBuilder.AppendLine("RLRG_Diag_Allies".Translate(num, num2).ToString());
			if (num == 0)
			{
				stringBuilder.AppendLine("RLRG_Diag_NoAllies".Translate().ToString());
			}
			stringBuilder.AppendLine();
			IncidentWorker_NeutralGroup trader = null;
			IncidentParms tp = null;
			stringBuilder.AppendLine("RLRG_Diag_Events".Translate().ToString());
			string[] defs = Defs;
			foreach (string text in defs)
			{
				IncidentDef namedSilentFail = DefDatabase<IncidentDef>.GetNamedSilentFail(text);
				if (namedSilentFail == null)
				{
					stringBuilder.AppendLine("   - " + text + ": " + "RLRG_Diag_R_Missing".Translate());
					continue;
				}
				IncidentParms incidentParms = StorytellerUtility.DefaultParmsNow(namedSilentFail.category, map);
				if (text == "TraderCaravanArrival")
				{
					trader = namedSilentFail.Worker as IncidentWorker_NeutralGroup;
					tp = incidentParms;
				}
				bool flag;
				try
				{
					flag = namedSilentFail.Worker.CanFireNow(incidentParms);
				}
				catch (Exception ex)
				{
					stringBuilder.AppendLine("   - " + namedSilentFail.LabelCap + ": error " + ex.Message);
					continue;
				}
				stringBuilder.AppendLine("   - " + namedSilentFail.LabelCap + ": " + ((!flag) ? "RLRG_Diag_Cannot".Translate(Why(namedSilentFail, incidentParms, map)).ToString() : "RLRG_Diag_Can".Translate().ToString()));
			}
			stringBuilder.AppendLine();
			try
			{
				ThreatReport(stringBuilder, map, raidGearGameComponent, ticksGame);
			}
			catch (Exception ex2)
			{
				stringBuilder.AppendLine("threats error: " + ex2.Message);
			}
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("RLRG_Diag_Factions".Translate().ToString());
			foreach (Faction item2 in Find.FactionManager.AllFactionsListForReading)
			{
				if (item2 != null && !item2.IsPlayer && !item2.Hidden && item2.def != null && item2.def.humanlikeFaction)
				{
					stringBuilder.AppendLine("   - " + item2.Name + " (" + item2.def.LabelCap + ", " + item2.def.techLevel.ToStringHuman() + "): " + FactionLine(item2, trader, tp, map));
				}
			}
			return stringBuilder.ToString().TrimEnd(Array.Empty<char>());
		}

		private static void ThreatReport(StringBuilder sb, Map map, RaidGearGameComponent comp, int now)
		{
			sb.AppendLine("RLRG_Diag_Threats".Translate().ToString());
			Difficulty difficulty = ((Find.Storyteller == null) ? null : Find.Storyteller.difficulty);
			if (difficulty != null)
			{
				sb.AppendLine("RLRG_Diag_ThreatScale".Translate(difficulty.threatScale.ToStringPercent(), ((!difficulty.allowBigThreats) ? "RLRG_Diag_No" : "RLRG_Diag_Yes").Translate()).ToString());
				if (difficulty.threatScale <= 0.01f || !difficulty.allowBigThreats)
				{
					sb.AppendLine("RLRG_Diag_ThreatsOff".Translate().ToString());
				}
			}
			sb.AppendLine("RLRG_Diag_ThreatPoints".Translate(Mathf.RoundToInt(StorytellerUtility.DefaultThreatPointsNow(map))).ToString());
			if (comp != null)
			{
				int num = -1;
				for (int num2 = comp.evTicks.Count - 1; num2 >= 0; num2--)
				{
					if (comp.evCats[num2] == "ThreatBig" || comp.evCats[num2] == "ThreatSmall")
					{
						num = comp.evTicks[num2];
						break;
					}
				}
				sb.AppendLine((num < 0) ? "RLRG_Diag_LastThreatNone".Translate().ToString() : "RLRG_Diag_LastThreat".Translate(Days(now - num)).ToString());
			}
			IncidentWorker_RaidEnemy incidentWorker_RaidEnemy = null;
			IncidentParms incidentParms = null;
			string[] threatDefs = ThreatDefs;
			foreach (string defName in threatDefs)
			{
				IncidentDef namedSilentFail = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
				if (namedSilentFail != null)
				{
					IncidentParms incidentParms2 = StorytellerUtility.DefaultParmsNow(namedSilentFail.category, map);
					if (namedSilentFail.Worker is IncidentWorker_RaidEnemy)
					{
						incidentWorker_RaidEnemy = (IncidentWorker_RaidEnemy)namedSilentFail.Worker;
						incidentParms = incidentParms2;
					}
					bool flag;
					try
					{
						flag = namedSilentFail.Worker.CanFireNow(incidentParms2);
					}
					catch (Exception ex)
					{
						sb.AppendLine("   - " + namedSilentFail.LabelCap + ": error " + ex.Message);
						continue;
					}
					string text = string.Empty;
					if (!WorldTech.IncidentAllowed(namedSilentFail))
					{
						text = " (" + "RLRG_Diag_AboveWorld".Translate() + ")";
					}
					sb.AppendLine("   - " + namedSilentFail.LabelCap + ": " + ((!flag) ? "RLRG_Diag_Cannot".Translate(Why(namedSilentFail, incidentParms2, map)).ToString() : "RLRG_Diag_Can".Translate().ToString()) + text);
				}
			}
			if (incidentWorker_RaidEnemy == null || incidentParms == null)
			{
				return;
			}
			sb.AppendLine("RLRG_Diag_Raiders".Translate().ToString());
			int num3 = 0;
			int num4 = 0;
			foreach (Faction item in Find.FactionManager.AllFactionsListForReading)
			{
				if (item != null && !item.IsPlayer && item.def != null && !item.defeated && item.HostileTo(Faction.OfPlayer) && item.def.pawnGroupMakers != null && item.def.pawnGroupMakers.Any((PawnGroupMaker g) => g.kindDef == PawnGroupKindDefOf.Combat))
				{
					bool flag2 = false;
					bool flag3 = false;
					try
					{
						flag2 = incidentWorker_RaidEnemy.FactionCanBeGroupSource(item, incidentParms);
						flag3 = !flag2 && incidentWorker_RaidEnemy.FactionCanBeGroupSource(item, incidentParms, true);
					}
					catch
					{
					}
					if (flag2)
					{
						num3++;
					}
					if (flag3)
					{
						num4++;
					}
					string text2 = (flag2 ? "RLRG_Diag_RaidOk".Translate().ToString() : ((flag3 && !WorldTech.FactionAllowed(item)) ? "RLRG_Diag_RaidWorld".Translate().ToString() : (flag3 ? "RLRG_Diag_RaidDesperate".Translate().ToString() : ((!((float)GenDate.DaysPassedSinceSettle < item.def.earliestRaidDays)) ? "RLRG_Diag_RaidNo".Translate().ToString() : "RLRG_Diag_RaidEarly".Translate(item.def.earliestRaidDays).ToString()))));
					sb.AppendLine("   - " + item.Name + " (" + item.def.LabelCap + ", " + item.def.techLevel.ToStringHuman() + "): " + text2);
				}
			}
			if (num3 == 0 && num4 == 0)
			{
				sb.AppendLine("RLRG_Diag_NoRaiders".Translate().ToString());
			}
		}

		public static string Summon(Map map)
		{
			if (map == null)
			{
				return "RLRG_Diag_NoMap".Translate().ToString();
			}
			IncidentDef namedSilentFail = DefDatabase<IncidentDef>.GetNamedSilentFail("TraderCaravanArrival");
			if (namedSilentFail == null)
			{
				return "RLRG_Diag_R_Missing".Translate().ToString();
			}
			IncidentParms parms = StorytellerUtility.DefaultParmsNow(namedSilentFail.category, map);
			bool flag = false;
			Running = true;
			try
			{
				flag = namedSilentFail.Worker.TryExecute(parms);
			}
			catch (Exception ex)
			{
				return "error: " + ex.Message;
			}
			finally
			{
				Running = false;
			}
			if (!flag)
			{
				return Why(namedSilentFail, StorytellerUtility.DefaultParmsNow(namedSilentFail.category, map), map);
			}
			RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
			if (raidGearGameComponent != null)
			{
				raidGearGameComponent.lastTraderTick = Find.TickManager.TicksGame;
				raidGearGameComponent.Record(namedSilentFail);
			}
			return string.Empty;
		}

		public static void HourlyCheck(RaidGearGameComponent comp)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (comp == null || settings == null || !settings.traderGuarantee || settings.traderGuaranteeDays <= 0)
			{
				return;
			}
			int ticksGame = Find.TickManager.TicksGame;
			if (comp.lastTraderTick < 0)
			{
				comp.lastTraderTick = Math.Max(0, ticksGame - (settings.traderGuaranteeDays - 1) * 60000);
				for (int num = comp.evDefs.Count - 1; num >= 0; num--)
				{
					if (comp.evDefs[num] == "TraderCaravanArrival")
					{
						comp.lastTraderTick = comp.evTicks[num];
						break;
					}
				}
			}
			else
			{
				if (ticksGame - comp.lastTraderTick < settings.traderGuaranteeDays * 60000 || (comp.lastGuaranteeTry >= 0 && ticksGame - comp.lastGuaranteeTry < 30000))
				{
					return;
				}
				comp.lastGuaranteeTry = ticksGame;
				Map map = HomeMap();
				if (map == null)
				{
					return;
				}
				int ticks = ticksGame - comp.lastTraderTick;
				string text = Summon(map);
				if (text.Length == 0)
				{
					MLog.Info("[World at Your Level] Trader guarantee: called a caravan after " + Days(ticks) + " days without one.");
				}
				else if (comp.lastDiagLog < 0 || ticksGame - comp.lastDiagLog >= 300000)
				{
					comp.lastDiagLog = ticksGame;
					MLog.Info("[World at Your Level] Trader guarantee: a caravan could not come (" + text + ").\n" + Report(map));
					if (settings.worldMessages)
					{
						Messages.Message("RLRG_Diag_GuaranteeFailed".Translate(text), MessageTypeDefOf.CautionInput, false);
					}
				}
			}
		}
	}
	public sealed class Dialog_VisitorDiag : Window
	{
		private string text;

		private Vector2 scroll;

		public override Vector2 InitialSize
		{
			get
			{
				return new Vector2(900f, 680f);
			}
		}

		public Dialog_VisitorDiag()
		{
			doCloseX = true;
			doCloseButton = false;
			absorbInputAroundWindow = true;
			closeOnClickedOutside = true;
			Refresh();
		}

		private void Refresh()
		{
			try
			{
				text = VisitorDiag.Report(VisitorDiag.HomeMap());
			}
			catch (Exception ex)
			{
				text = "error: " + ex;
			}
		}

		public override void DoWindowContents(Rect inRect)
		{
			Text.Font = GameFont.Medium;
			Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "RLRG_Diag_Title2".Translate());
			Text.Font = GameFont.Small;
			Rect outRect = new Rect(inRect.x, inRect.y + 40f, inRect.width, inRect.height - 90f);
			float height = Text.CalcHeight(this.text, outRect.width - 20f) + 10f;
			Rect rect = new Rect(0f, 0f, outRect.width - 20f, height);
			Widgets.BeginScrollView(outRect, ref scroll, rect);
			Widgets.Label(rect, this.text);
			Widgets.EndScrollView();
			float num = (inRect.width - 20f) / 3f;
			float y = inRect.yMax - 40f;
			if (Widgets.ButtonText(new Rect(inRect.x, y, num, 36f), "RLRG_Diag_Summon".Translate()))
			{
				string text = VisitorDiag.Summon(VisitorDiag.HomeMap());
				if (text.Length == 0)
				{
					Messages.Message("RLRG_Diag_SummonOk".Translate(), MessageTypeDefOf.PositiveEvent, false);
				}
				else
				{
					Messages.Message("RLRG_Diag_SummonFail".Translate(text), MessageTypeDefOf.RejectInput, false);
				}
				Refresh();
			}
			if (Widgets.ButtonText(new Rect(inRect.x + num + 10f, y, num, 36f), "RLRG_Diag_ToLog".Translate()))
			{
				MLog.Info("[World at Your Level] Visitor diagnostics:\n" + this.text);
				Messages.Message("RLRG_Diag_Logged".Translate(), MessageTypeDefOf.NeutralEvent, false);
			}
			if (Widgets.ButtonText(new Rect(inRect.x + 2f * (num + 10f), y, num, 36f), "RLRG_Diag_Refresh".Translate()))
			{
				Refresh();
			}
		}
	}
	public static class WorldTech
	{
		private static readonly Dictionary<string, TechLevel> incidentTech = new Dictionary<string, TechLevel>
		{
			{
				"MechCluster",
				TechLevel.Ultra
			},
			{
				"PsychicEmanatorShipPartCrash",
				TechLevel.Ultra
			},
			{
				"DefoliatorShipPartCrash",
				TechLevel.Ultra
			},
			{
				"ShipChunkDrop",
				TechLevel.Spacer
			},
			{
				"OrbitalTraderArrival",
				TechLevel.Industrial
			}
		};

		private static bool Active
		{
			get
			{
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				return settings != null && settings.enabled && Current.Game != null && Find.ResearchManager != null;
			}
		}

		public static TechLevel Level()
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			return (settings == null) ? TechLevel.Archotech : ResearchLockedGearFilter.TechLimit(settings);
		}

		public static bool FactionAllowed(Faction f)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!Active || settings == null || !settings.worldRaidFactions || settings.factionsAtWorldLevel || f == null || f.def == null || f.IsPlayer)
			{
				return true;
			}
			if (settings.exemptFactions != null && settings.exemptFactions.Contains(f.def.defName))
			{
				return true;
			}
			if (!f.def.humanlikeFaction && !settings.worldMechanoids)
			{
				return true;
			}
			if (settings.exemptAncients && (f == Faction.OfAncients || f == Faction.OfAncientsHostile))
			{
				return true;
			}
			TechLevel techLevel = f.def.techLevel;
			if (techLevel == TechLevel.Undefined || techLevel == TechLevel.Animal)
			{
				return true;
			}
			return (int)techLevel <= (int)Level();
		}

		public static bool FactionWithinWorld(Faction f)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || settings.factionsAtWorldLevel || f == null || f.def == null || f.IsPlayer)
			{
				return true;
			}
			if (settings.exemptFactions != null && settings.exemptFactions.Contains(f.def.defName))
			{
				return true;
			}
			if (!f.def.humanlikeFaction && !settings.worldMechanoids)
			{
				return true;
			}
			if (settings.exemptAncients && (f == Faction.OfAncients || f == Faction.OfAncientsHostile))
			{
				return true;
			}
			TechLevel techLevel = f.def.techLevel;
			if (techLevel == TechLevel.Undefined || techLevel == TechLevel.Animal)
			{
				return true;
			}
			return (int)techLevel <= (int)Level();
		}

		public static bool QuestDefAllowed(QuestScriptDef def)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!Active || settings == null || !settings.worldQuests || def == null || def.defName == null)
			{
				return true;
			}
			string defName = def.defName;
			if (Has(defName, "Bestowing") || Has(defName, "EndGame") || Has(defName, "Monolith") || Has(defName, "Tutorial"))
			{
				return true;
			}
			TechLevel techLevel = TechLevel.Undefined;
			if (Has(defName, "Archonexus") || Has(defName, "Archotech") || Has(defName, "Archite"))
			{
				techLevel = TechLevel.Archotech;
			}
			else if (settings.worldMechanoids && Has(defName, "Mech") && !Has(defName, "Mechanic"))
			{
				techLevel = TechLevel.Ultra;
			}
			else if (Has(defName, "Gravship") || Has(defName, "Orbital"))
			{
				techLevel = TechLevel.Spacer;
			}
			return techLevel == TechLevel.Undefined || (int)techLevel <= (int)Level();
		}

		private static bool Has(string s, string part)
		{
			return s.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
		}

		public static bool QuestAllowed(Quest quest)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!Active || settings == null || !settings.worldQuests || quest == null)
			{
				return true;
			}
			try
			{
				foreach (Faction involvedFaction in quest.InvolvedFactions)
				{
					if (!FactionWithinWorld(involvedFaction))
					{
						return false;
					}
				}
			}
			catch
			{
				return true;
			}
			return true;
		}

		// 2.9.7: the item rules work on a def, so the list dialog can show them without a Thing
		private static ThingDef StockDef(Thing thing)
		{
			MinifiedThing minifiedThing = thing as MinifiedThing;
			if (minifiedThing != null && minifiedThing.InnerThing != null)
			{
				return minifiedThing.InnerThing.def;
			}
			return thing.def;
		}

		public static bool ItemAllowed(Thing thing)
		{
			if (thing == null || thing is Pawn)
			{
				return true;
			}
			return ItemDefAllowed(StockDef(thing));
		}

		public static bool ItemDefAllowed(ThingDef def)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!Active || settings == null || !settings.worldItems || def == null)
			{
				return true;
			}
			if (def.IsWeapon || def.IsApparel)
			{
				return ResearchLockedGearFilter.IsResearchAllowed(def);
			}
			TechLevel techLevel = def.techLevel;
			if (techLevel == TechLevel.Undefined || techLevel == TechLevel.Animal)
			{
				return true;
			}
			return (int)techLevel <= (int)Level();
		}

		// 2.9.6: traders carry goods up to (world level + traderTechBonus)
		public static bool TraderItemAllowed(Thing thing)
		{
			if (thing == null || thing is Pawn)
			{
				return true;
			}
			return TraderDefAllowed(StockDef(thing));
		}

		public static TechLevel TraderMaxTech()
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			int bonus = (settings == null) ? 0 : Math.Max(0, settings.traderTechBonus);
			return (TechLevel)Math.Min((int)Level() + bonus, (int)TechLevel.Archotech);
		}

		public static bool TraderDefAllowed(ThingDef def)
		{
			if (ItemDefAllowed(def))
			{
				return true;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || settings.traderTechBonus <= 0)
			{
				return false;
			}
			TechLevel techLevel = def.techLevel;
			if (techLevel == TechLevel.Undefined || techLevel == TechLevel.Animal)
			{
				return true;
			}
			return (int)techLevel <= (int)TraderMaxTech();
		}

		public static bool IncidentAllowed(IncidentDef def)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (!Active || settings == null || !settings.worldIncidents || def == null)
			{
				return true;
			}
			TechLevel value;
			if (!incidentTech.TryGetValue(def.defName, out value))
			{
				string defName = def.defName;
				if (defName.IndexOf("Spewer", StringComparison.OrdinalIgnoreCase) >= 0 || defName.IndexOf("ConditionCauser", StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return 6 <= (int)Level();
				}
				return true;
			}
			if (def.defName == "MechCluster" && !settings.worldMechanoids)
			{
				return true;
			}
			return (int)value <= (int)Level();
		}
	}
	public static class WorldVisitorFactionPatch
	{
		public static void Postfix(Faction f, ref bool __result)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (__result && settings != null && settings.enabled && settings.worldVisitors && Current.Game != null && !WorldTech.FactionWithinWorld(f))
			{
				__result = false;
			}
		}
	}
	public static class WorldEvents
	{
		public static bool Replacing;

		public static IncidentDef LastReplacement;

		private static bool Active(IncidentParms parms)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || !settings.enabled || !settings.replaceEvents || Replacing || VisitorDiag.Running || Current.Game == null)
			{
				return false;
			}
			return parms != null && !parms.forced && parms.quest == null && parms.target != null;
		}

		public static bool ShouldReplaceBefore(IncidentWorker w, IncidentParms parms)
		{
			if (w == null || w.def == null || !Active(parms))
			{
				return false;
			}
			return !WorldTech.IncidentAllowed(w.def);
		}

		public static bool ShouldReplaceAfterFail(IncidentWorker w, IncidentParms parms)
		{
			if (w == null || w.def == null || !Active(parms))
			{
				return false;
			}
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (w is IncidentWorker_RaidEnemy)
			{
				return settings.worldRaidFactions;
			}
			if (w is IncidentWorker_NeutralGroup)
			{
				return settings.worldVisitors;
			}
			return false;
		}

		public static bool Replace(IncidentDef blocked, IncidentParms parms)
		{
			Replacing = true;
			try
			{
				IIncidentTarget target = parms.target;
				List<IncidentCategoryDef> list = new List<IncidentCategoryDef>();
				if (blocked.category != null)
				{
					list.Add(blocked.category);
				}
				if (IncidentCategoryDefOf.Misc != null && !list.Contains(IncidentCategoryDefOf.Misc))
				{
					list.Add(IncidentCategoryDefOf.Misc);
				}
				foreach (IncidentCategoryDef item in list)
				{
					List<IncidentDef> list2 = new List<IncidentDef>();
					foreach (IncidentDef item2 in DefDatabase<IncidentDef>.AllDefsListForReading)
					{
						if (item2 != null && item2 != blocked && item2.category == item && !(item2.baseChance <= 0f) && item2.Worker != null && item2.TargetAllowed(target) && WorldTech.IncidentAllowed(item2))
						{
							list2.Add(item2);
						}
					}
					for (int i = 0; i < 10; i++)
					{
						if (list2.Count <= 0)
						{
							break;
						}
						IncidentDef result;
						if (!list2.TryRandomElementByWeight((IncidentDef d) => d.baseChance, out result))
						{
							break;
						}
						list2.Remove(result);
						IncidentParms parms2 = StorytellerUtility.DefaultParmsNow(item, target);
						if (result.Worker.CanFireNow(parms2) && result.Worker.TryExecute(parms2))
						{
							LastReplacement = result;
							if (Prefs.DevMode)
							{
								MLog.Info("[World at Your Level] event " + blocked.defName + " replaced with " + result.defName + ".");
							}
							return true;
						}
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] event replacement failed: " + ex.Message);
			}
			finally
			{
				Replacing = false;
			}
			return false;
		}
	}
	public static class StorytellerHistoryPatch
	{
		public static void Postfix(FiringIncident fi, bool __result)
		{
			IncidentDef incidentDef = WorldEvents.LastReplacement ?? ((fi == null) ? null : fi.def);
			WorldEvents.LastReplacement = null;
			if (__result && incidentDef != null)
			{
				RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
				if (raidGearGameComponent != null)
				{
					raidGearGameComponent.Record(incidentDef);
				}
			}
		}
	}
	public sealed class MainTabWindow_WorldOverview : MainTabWindow
	{
		private Vector2 scroll;

		public override Vector2 RequestedTabSize
		{
			get
			{
				return new Vector2(860f, 640f);
			}
		}

		private static string Days(float ticks)
		{
			return (ticks / 60000f).ToString("0.#");
		}

		public override void DoWindowContents(Rect inRect)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			RaidGearGameComponent comp = RaidGearGameComponent.Get();
			int ticksGame = Find.TickManager.TicksGame;
			Rect rect = new Rect(0f, 0f, inRect.width - 20f, 1400f);
			Widgets.BeginScrollView(inRect, ref scroll, rect);
			Listing_Standard listing_Standard = new Listing_Standard();
			listing_Standard.Begin(rect);
			try
			{
				DrawContents(listing_Standard, settings, comp, ticksGame);
			}
			catch (Exception ex)
			{
				GUI.color = Color.white;
				listing_Standard.Label("error: " + ex.Message);
			}
			listing_Standard.End();
			Widgets.EndScrollView();
		}

		private void DrawContents(Listing_Standard list, ResearchLockedRaidGearSettings settings, RaidGearGameComponent comp, int now)
		{
			Text.Font = GameFont.Medium;
			list.Label("RLRG_OvTitle".Translate());
			Text.Font = GameFont.Small;
			if (Find.Storyteller != null && Find.Storyteller.def != null)
			{
				list.Label("RLRG_OvStoryteller".Translate(Find.Storyteller.def.LabelCap, (Find.Storyteller.difficultyDef == null) ? "-" : Find.Storyteller.difficultyDef.LabelCap.ToString()));
			}
			if (settings != null)
			{
				list.Label("RLRG_WorldLevelNow".Translate(ResearchLockedGearFilter.TechLimit(settings).ToStringHuman()));
				list.Label(ResearchLockedGearFilter.ColonyTechExplain());
			}
			if (comp != null)
			{
				list.Label((comp.lastTraderTick < 0) ? "RLRG_Diag_LastTraderNone".Translate().ToString() : "RLRG_Diag_LastTrader".Translate(Days(now - comp.lastTraderTick)).ToString());
			}
			if (comp != null && settings != null && settings.onlyCrafted)
			{
				list.Label("RLRG_CraftedCount".Translate(SharedCrafted.UnionCount(comp.crafted)));
			}
			if (list.ButtonText("RLRG_Diag_Open".Translate()))
			{
				Find.WindowStack.Add(new Dialog_VisitorDiag());
			}
			list.GapLine();
			Text.Font = GameFont.Medium;
			list.Label("RLRG_OvQuests".Translate());
			Text.Font = GameFont.Small;
			int num = 0;
			List<Quest> list2 = new List<Quest>();
			foreach (Quest item in Find.QuestManager.QuestsListForReading)
			{
				if (!item.hidden)
				{
					if (item.State == QuestState.Ongoing)
					{
						num++;
					}
					else if (item.State == QuestState.NotYetAccepted)
					{
						list2.Add(item);
					}
				}
			}
			list.Label("RLRG_OvQuestCounts".Translate(num, list2.Count));
			list2.Sort((Quest a, Quest b) => a.TicksUntilExpiry.CompareTo(b.TicksUntilExpiry));
			for (int i = 0; i < list2.Count && i < 8; i++)
			{
				Quest quest = list2[i];
				string text = ((quest.TicksUntilExpiry <= 0) ? string.Empty : "RLRG_OvExpires".Translate(quest.TicksUntilExpiry.ToStringTicksToPeriod()).ToString());
				list.Label("   - " + quest.name + ((text.Length <= 0) ? string.Empty : ("  (" + text + ")")));
			}
			list.GapLine();
			Text.Font = GameFont.Medium;
			list.Label("RLRG_OvEvents".Translate());
			Text.Font = GameFont.Small;
			list.Label("RLRG_OvEventsHint".Translate());
			if (comp == null || comp.evTicks.Count == 0)
			{
				list.Label("RLRG_OvNoEvents".Translate());
				return;
			}
			Rect rect = list.GetRect(24f);
			DrawRow(rect, "RLRG_OvColKind".Translate(), "RLRG_OvColLast".Translate(), "RLRG_OvCol30".Translate(), "RLRG_OvColAvg".Translate(), "RLRG_OvColNext".Translate(), true);
			List<string> list3 = new List<string>();
			for (int j = 0; j < comp.evCats.Count; j++)
			{
				if (!list3.Contains(comp.evCats[j]))
				{
					list3.Add(comp.evCats[j]);
				}
			}
			list3.Sort();
			foreach (string item2 in list3)
			{
				int num2 = -1;
				int num3 = 0;
				int num4 = 0;
				int num5 = -1;
				long num6 = 0L;
				string defName = string.Empty;
				for (int k = 0; k < comp.evTicks.Count; k++)
				{
					if (!(comp.evCats[k] != item2))
					{
						int num7 = comp.evTicks[k];
						if (num5 >= 0)
						{
							num6 += num7 - num5;
							num4++;
						}
						num5 = num7;
						num2 = num7;
						defName = comp.evDefs[k];
						if (now - num7 <= 1800000)
						{
							num3++;
						}
					}
				}
				string d = ((num4 <= 0) ? "-" : "RLRG_OvDays".Translate(Days((float)num6 / (float)num4)).ToString());
				string e = "-";
				if (num4 > 0)
				{
					float num8 = (float)num6 / (float)num4 - (float)(now - num2);
					e = ((!(num8 <= 0f)) ? "RLRG_OvInDays".Translate(Days(num8)).ToString() : "RLRG_OvAnyTime".Translate().ToString());
				}
				IncidentDef namedSilentFail = DefDatabase<IncidentDef>.GetNamedSilentFail(defName);
				IncidentCategoryDef namedSilentFail2 = DefDatabase<IncidentCategoryDef>.GetNamedSilentFail(item2);
				string a2 = ((namedSilentFail2 == null || namedSilentFail2.label.NullOrEmpty()) ? item2 : namedSilentFail2.LabelCap.ToString());
				string b2 = "RLRG_OvAgo".Translate(Days(now - num2)).ToString() + ((namedSilentFail == null) ? string.Empty : (" (" + namedSilentFail.LabelCap.ToString() + ")"));
				DrawRow(list.GetRect(24f), a2, b2, num3.ToString(), d, e, false);
			}
			list.Gap(10f);
			list.Label("RLRG_OvRecent".Translate());
			int num9 = comp.evTicks.Count - 1;
			int num10 = 0;
			while (num9 >= 0 && num10 < 10)
			{
				IncidentDef namedSilentFail3 = DefDatabase<IncidentDef>.GetNamedSilentFail(comp.evDefs[num9]);
				list.Label("   - " + "RLRG_OvAgo".Translate(Days(now - comp.evTicks[num9])) + ": " + ((namedSilentFail3 == null) ? comp.evDefs[num9] : namedSilentFail3.LabelCap.ToString()));
				num9--;
				num10++;
			}
		}

		private static void DrawRow(Rect r, string a, string b, string c, string d, string e, bool header)
		{
			if (header)
			{
				GUI.color = new Color(0.8f, 0.8f, 0.8f);
			}
			float num = r.x;
			float[] array = new float[5] { 0.2f, 0.36f, 0.1f, 0.16f, 0.18f };
			string[] array2 = new string[5] { a, b, c, d, e };
			for (int i = 0; i < array2.Length; i++)
			{
				Widgets.Label(new Rect(num, r.y, r.width * array[i] - 6f, r.height), array2[i]);
				num += r.width * array[i];
			}
			GUI.color = Color.white;
		}
	}
	public static class WorldEventReplacePatch
	{
		public static bool Prefix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
		{
			WorldEvents.LastReplacement = null;   // 2.9.5: no stale record from an earlier replacement
			if (!WorldEvents.ShouldReplaceBefore(__instance, parms))
			{
				return true;
			}
			__result = WorldEvents.Replace(__instance.def, parms);
			return false;
		}

		public static void Postfix(IncidentWorker __instance, IncidentParms parms, ref bool __result)
		{
			if (__result)
			{
				VisitorDiag.NoteArrival(__instance);
			}
			else if (WorldEvents.ShouldReplaceAfterFail(__instance, parms))
			{
				__result = WorldEvents.Replace(__instance.def, parms);
			}
		}
	}
	// 2.11.1: small readout above the date: research level, progress to the next level,
	// details per tech tier in the tooltip, click opens the World tab
	public static class LevelHud
	{
		private static int lastFrame = -1000;

		private static string line = string.Empty;

		private static string tip = string.Empty;

		private static float progress;

		private static bool failed;

		private static readonly TechLevel[] Tiers = new TechLevel[6]
		{
			TechLevel.Neolithic,
			TechLevel.Medieval,
			TechLevel.Industrial,
			TechLevel.Spacer,
			TechLevel.Ultra,
			TechLevel.Archotech
		};

		private static void Refresh()
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			int[] total = new int[16];
			int[] done = new int[16];
			List<ResearchProjectDef> all = DefDatabase<ResearchProjectDef>.AllDefsListForReading;
			for (int i = 0; i < all.Count; i++)
			{
				ResearchProjectDef r = all[i];
				if (r == null)
				{
					continue;
				}
				int t = Mathf.Clamp((int)r.techLevel, 0, 15);
				total[t]++;
				if (r.IsFinished)
				{
					done[t]++;
				}
			}
			TechLevel scenario;
			TechLevel research;
			TechLevel colony = ResearchLockedGearFilter.ComputeColonyTech(out scenario, out research);
			TechLevel world = ResearchLockedGearFilter.TechLimit(settings);
			// next tier above the colony level that has any research
			int next = -1;
			for (int j = (int)colony + 1; j < 16; j++)
			{
				if (total[j] > 0)
				{
					next = j;
					break;
				}
			}
			if (next < 0 || (settings != null && settings.useManualLevel))
			{
				progress = 1f;
				line = "RLRG_HudLine".Translate(colony.ToStringHuman().CapitalizeFirst());
			}
			else
			{
				float th = (settings == null) ? 0.25f : settings.ThresholdFor((TechLevel)next);
				float need = Mathf.Max(1f, Mathf.Ceil(total[next] * th));
				progress = Mathf.Clamp01(done[next] / need);
				line = "RLRG_HudLineNext".Translate(colony.ToStringHuman().CapitalizeFirst(), ((TechLevel)next).ToStringHuman(), Mathf.RoundToInt(progress * 100f));
			}
			StringBuilder sb = new StringBuilder();
			sb.AppendLine("RLRG_HudTipTitle".Translate().ToString());
			sb.AppendLine("RLRG_HudTipColony".Translate(colony.ToStringHuman(), scenario.ToStringHuman(), research.ToStringHuman()).ToString());
			if (world != colony)
			{
				sb.AppendLine("RLRG_HudTipWorld".Translate(world.ToStringHuman()).ToString());
			}
			sb.AppendLine();
			for (int k = 0; k < Tiers.Length; k++)
			{
				int t = (int)Tiers[k];
				if (total[t] == 0)
				{
					continue;
				}
				float th2 = (settings == null) ? 0.25f : settings.ThresholdFor(Tiers[k]);
				int need2 = Mathf.Max(1, Mathf.CeilToInt(total[t] * th2));
				string mark = (done[t] >= need2 && (settings == null || settings.levelByProgress)) ? "✔" : "·";
				sb.AppendLine("RLRG_HudTipTier".Translate(mark, Tiers[k].ToStringHuman().CapitalizeFirst(), done[t], total[t], Mathf.RoundToInt(100f * done[t] / total[t]), need2).ToString());
			}
			sb.AppendLine();
			sb.Append("RLRG_HudTipClick".Translate().ToString());
			tip = sb.ToString();
		}

		public static void Postfix(float leftX, float width, ref float curBaseY)
		{
			try
			{
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				if (failed || settings == null || !settings.enabled || !settings.showLevelHud || Current.ProgramState != ProgramState.Playing)
				{
					return;
				}
				if (Time.frameCount - lastFrame > 120 || line.Length == 0)
				{
					lastFrame = Time.frameCount;
					Refresh();
				}
				Text.Font = GameFont.Small;
				float h = 26f;
				Rect rect = new Rect(leftX, curBaseY - h, width, h);
				Text.Anchor = TextAnchor.MiddleRight;
				Rect textRect = new Rect(rect.x, rect.y, rect.width - 10f, rect.height - 4f);
				Widgets.Label(textRect, line);
				Text.Anchor = TextAnchor.UpperLeft;
				float tw = Mathf.Min(Text.CalcSize(line).x, rect.width - 10f);
				Rect bar = new Rect(rect.xMax - 10f - tw, rect.yMax - 5f, tw, 3f);
				Widgets.DrawBoxSolid(bar, new Color(0.15f, 0.15f, 0.15f, 0.8f));
				Widgets.DrawBoxSolid(new Rect(bar.x, bar.y, bar.width * progress, bar.height), new Color(0.4f, 0.8f, 1f, 0.9f));
				if (Mouse.IsOver(rect))
				{
					Widgets.DrawHighlight(rect);
				}
				TooltipHandler.TipRegion(rect, tip);
				if (Widgets.ButtonInvisible(rect))
				{
					MainButtonDef tab = DefDatabase<MainButtonDef>.GetNamedSilentFail("RLRG_WorldOverview");
					if (tab != null && Find.MainTabsRoot != null)
					{
						Find.MainTabsRoot.ToggleTab(tab);
					}
				}
				curBaseY -= h;
			}
			catch (Exception ex)
			{
				Text.Anchor = TextAnchor.UpperLeft;
				failed = true;
				MLog.Warn("[World at Your Level] level readout failed, switched off until restart: " + ex.Message);
			}
		}
	}
	// 2.11.0: what the colony really made (material and quality) raises the cap for arrivals
	public static class CraftedProductPatch
	{
		public static void Postfix(Thing __result, Pawn worker)
		{
			try
			{
				if (__result == null || worker == null || worker.Faction == null || !worker.Faction.IsPlayer)
				{
					return;
				}
				RaidGearGameComponent gc = RaidGearGameComponent.Get();
				if (gc != null)
				{
					gc.NoteProduct(__result, true);
				}
			}
			catch
			{
			}
		}
	}
	public static class CraftedRecordPatch
	{
		public static void Postfix(Bill __instance, Pawn billDoer)
		{
			try
			{
				if (__instance == null || __instance.recipe == null || __instance.recipe.products == null || (billDoer != null && billDoer.Faction != null && !billDoer.Faction.IsPlayer))
				{
					return;
				}
				RaidGearGameComponent raidGearGameComponent = RaidGearGameComponent.Get();
				if (raidGearGameComponent == null)
				{
					return;
				}
				raidGearGameComponent.EnsureCraftedSeeded();
				for (int i = 0; i < __instance.recipe.products.Count; i++)
				{
					ThingDefCountClass thingDefCountClass = __instance.recipe.products[i];
					if (thingDefCountClass != null)
					{
						raidGearGameComponent.NoteCrafted(thingDefCountClass.thingDef);
					}
				}
			}
			catch
			{
			}
		}
	}
	public static class WorldRaidFactionPatch
	{
		public static void Postfix(Faction f, bool desperate, ref bool __result)
		{
			if (!desperate && __result && !WorldTech.FactionAllowed(f))
			{
				__result = false;
			}
		}
	}

	// 2.9.6 / 2.6.3: one shared "only crafted" list with the partner mod
	// (Smart Colonist Gear <-> World at Your Level), read by reflection.
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
					partnerType = AccessTools.TypeByName("ResearchedLoadout.ResearchedLoadoutGameComponent");
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
	public static class WorldThingSetPatch
	{
		public static void Postfix(ThingSetMakerParams parms, ref List<Thing> __result)
		{
			if (__result == null || __result.Count == 0)
			{
				return;
			}
			// 2.9.6: trader stock may be a few levels above the world (setting)
			bool trader = parms.traderDef != null;
			for (int num = __result.Count - 1; num >= 0; num--)
			{
				Thing thing = __result[num];
				if (thing != null && !(trader ? WorldTech.TraderItemAllowed(thing) : WorldTech.ItemAllowed(thing)))
				{
					__result.RemoveAt(num);
					if (!thing.Destroyed && !thing.Spawned)
					{
						thing.Destroy();
					}
				}
			}
		}
	}
	public static class WorldResearchLockPatch
	{
		public static void Postfix(ResearchProjectDef __instance, ref bool __result)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (__result && settings != null && settings.enabled && settings.lockHigherResearch && __instance != null && Current.Game != null && Find.ResearchManager != null)
			{
				TechLevel techLevel = __instance.techLevel;
				if (techLevel != 0 && (int)techLevel > (int)(WorldTech.Level() + 1))
				{
					__result = false;
				}
			}
		}
	}
	public static class WorldQuestCanRunPatch
	{
		public static void Postfix(QuestScriptDef __instance, ref bool __result)
		{
			if (__result && (!WorldTech.QuestDefAllowed(__instance) || RaidTypeFilter.QuestBlocked(__instance)))
			{
				__result = false;
			}
		}
	}
	public static class WorldGiveQuestPatch
	{
		public static int FromStoryteller;

		public static IncidentParms CurrentParms;

		public static void Prefix(IncidentParms parms)
		{
			FromStoryteller++;
			CurrentParms = parms;
		}

		public static void Finalizer()
		{
			if (FromStoryteller > 0)
			{
				FromStoryteller--;
			}
			if (FromStoryteller == 0)
			{
				CurrentParms = null;
			}
		}
	}
	public static class WorldQuestGeneratedPatch
	{
		private static int replacing;

		private static Quest LastDropped;

		public static void Reset()
		{
			replacing = 0;
			LastDropped = null;
		}

		public static void Postfix(Quest __result)
		{
			if (__result == null)
			{
				return;
			}
			// 2.13.1: a quest you blocked, or one above the world level (mech quests …), is dropped
			// whoever made it – other mods (quest generators, race mods) bypass the storyteller
			if (WorldGiveQuestPatch.FromStoryteller <= 0)
			{
				QuestScriptDef root = __result.root;
				if (root != null && (RaidTypeFilter.QuestBlocked(root) || !WorldTech.QuestDefAllowed(root)))
				{
					__result.hidden = true;
					try
					{
						__result.End(QuestEndOutcome.Unknown, false, false);
					}
					catch
					{
					}
					MLog.Info("[World at Your Level] quest " + root.defName + " dropped (blocked or above the world level).");
				}
				return;
			}
			bool flag = false;
			try
			{
				flag = ThreatCausers.QuestHasCauserAboveWorld(__result);
			}
			catch
			{
			}
			if (!flag && WorldTech.QuestAllowed(__result))
			{
				return;
			}
			LastDropped = __result;
			__result.hidden = true;
			try
			{
				__result.End(QuestEndOutcome.Unknown, false, false);
				if (flag)
				{
					ThreatCausers.RemoveQuestCausers(__result);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] quest cleanup failed: " + ex.Message);
			}
			if (Prefs.DevMode)
			{
				MLog.Info("[World at Your Level] quest " + __result.root.defName + " skipped (faction above world level).");
			}
			TryReplace(__result);
		}

		private static void TryReplace(Quest dropped)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			IncidentParms currentParms = WorldGiveQuestPatch.CurrentParms;
			if (settings == null || !settings.replaceQuests || replacing > 0 || currentParms == null || currentParms.target == null)
			{
				return;
			}
			replacing++;
			try
			{
				for (int i = 0; i < 5; i++)
				{
					QuestScriptDef questScriptDef = NaturalRandomQuestChooser.ChooseNaturalRandomQuest(currentParms.points, currentParms.target);
					if (questScriptDef == null)
					{
						break;
					}
					Quest quest = QuestUtility.GenerateQuestAndMakeAvailable(questScriptDef, currentParms.points);
					if (quest != null && quest != LastDropped)
					{
						if (questScriptDef.sendAvailableLetter)
						{
							QuestUtility.SendLetterQuestAvailable(quest);
						}
						if (Prefs.DevMode)
						{
							MLog.Info("[World at Your Level] quest " + dropped.root.defName + " replaced with " + questScriptDef.defName + ".");
						}
						break;
					}
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] quest replacement failed: " + ex.Message);
			}
			finally
			{
				replacing--;
			}
		}
	}
	public static class WorldIncidentPatch
	{
		public static void Postfix(IncidentWorker __instance, ref bool __result)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if ((settings == null || !settings.replaceEvents || WorldEvents.Replacing) && __result && __instance != null && !WorldTech.IncidentAllowed(__instance.def))
			{
				__result = false;
			}
		}
	}
	public static class RaidCompensation
	{
		public static bool Collecting;

		private static bool generatingExtra;

		public static bool GeneratingExtra => generatingExtra;

		private static readonly Dictionary<int, Vector2> values = new Dictionary<int, Vector2>();

		private static readonly MethodInfo MemberwiseCloneMethod = AccessTools.Method(typeof(object), "MemberwiseClone");

		// 2.9.6: what was taken from the raiders (for the raid report)
		private static int itemsTaken;

		private static int itemsReplaced;

		public static void CountItem(bool replaced)
		{
			if (!Collecting)
			{
				return;
			}
			itemsTaken++;
			if (replaced)
			{
				itemsReplaced++;
			}
		}

		public static void Record(Pawn pawn, float valueBefore, float valueAfter)
		{
			Vector2 value;
			if (values.TryGetValue(pawn.thingIDNumber, out value))
			{
				values[pawn.thingIDNumber] = new Vector2(value.x, valueAfter);
			}
			else
			{
				values[pawn.thingIDNumber] = new Vector2(valueBefore, valueAfter);
			}
		}

		private static bool Applies(PawnGroupMakerParms parms)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (settings == null || !settings.enabled)
			{
				return false;
			}
			if ((!settings.compensateRaids || settings.compensationPercent <= 0) && !settings.notifyRaid)
			{
				return false;
			}
			if (generatingExtra || Collecting || parms == null || parms.faction == null)
			{
				return false;
			}
			if (parms.groupKind != PawnGroupKindDefOf.Combat || parms.points <= 0f)
			{
				return false;
			}
			if (parms.faction.IsPlayer || Faction.OfPlayer == null || !parms.faction.HostileTo(Faction.OfPlayer))
			{
				return false;
			}
			if (settings.exemptFactions != null && settings.exemptFactions.Contains(parms.faction.def.defName))
			{
				return false;
			}
			return true;
		}

		public static IEnumerable<Pawn> Wrap(IEnumerable<Pawn> original, PawnGroupMakerParms parms)
		{
			if (original == null || !Applies(parms))
			{
				return original;
			}
			values.Clear();
			itemsTaken = 0;
			itemsReplaced = 0;
			Collecting = true;
			List<Pawn> list;
			try
			{
				list = new List<Pawn>(original);
			}
			finally
			{
				Collecting = false;
			}
			float num = 0f;
			float num2 = 0f;
			foreach (Vector2 value in values.Values)
			{
				num += value.x;
				num2 += value.y;
			}
			values.Clear();
			try
			{
				if (list.Count == 0 || num <= 1f || num2 >= num)
				{
					return list;
				}
				ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
				float num3 = Mathf.Clamp01((num - num2) / num);
				int num4 = 0;
				if (settings.compensateRaids && settings.compensationPercent > 0)
				{
					num4 = AddExtra(list, parms, num3, settings);
				}
				if (settings.notifyRaid && TargetsPlayerHome(parms))
				{
					string text = ((num4 <= 0) ? "RLRG_MsgRaid".Translate(Mathf.RoundToInt(num3 * 100f)).ToString() : "RLRG_MsgRaidCompensated".Translate(Mathf.RoundToInt(num3 * 100f), num4).ToString());
					if (itemsTaken > 0)
					{
						text = text + " " + "RLRG_MsgRaidItems".Translate(itemsTaken, itemsReplaced, list.Count).ToString();
					}
					Messages.Message(text, MessageTypeDefOf.NeutralEvent, false);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] raid compensation failed: " + ex.Message);
			}
			return list;
		}

		private static bool TargetsPlayerHome(PawnGroupMakerParms parms)
		{
			List<Map> maps = Find.Maps;
			for (int i = 0; i < maps.Count; i++)
			{
				if (maps[i].IsPlayerHome && maps[i].Tile == parms.tile)
				{
					return true;
				}
			}
			return false;
		}

		private static int AddExtra(List<Pawn> pawns, PawnGroupMakerParms parms, float lostFrac, ResearchLockedRaidGearSettings settings)
		{
			int num = 0;
			float a = parms.points * lostFrac * (float)settings.compensationPercent / 100f;
			a = Mathf.Min(a, parms.points * 0.5f);
			float num2 = parms.faction.def.MinPointsToGeneratePawnGroup(parms.groupKind, parms);
			if (a < num2)
			{
				return 0;
			}
			PawnGroupMakerParms pawnGroupMakerParms = (PawnGroupMakerParms)MemberwiseCloneMethod.Invoke(parms, null);
			pawnGroupMakerParms.points = a;
			pawnGroupMakerParms.seed = null;
			pawnGroupMakerParms.forceOneDowned = false;
			generatingExtra = true;
			try
			{
				foreach (Pawn item in PawnGroupMakerUtility.GeneratePawns(pawnGroupMakerParms, false))
				{
					pawns.Add(item);
					num++;
				}
				if (Prefs.DevMode)
				{
					MLog.Info("[World at Your Level] raid lost " + Mathf.RoundToInt(lostFrac * 100f) + "% gear value, +" + Mathf.RoundToInt(a) + " points, +" + num + " pawns.");
				}
			}
			finally
			{
				generatingExtra = false;
			}
			return num;
		}
	}
	// 2.12.0: humanlike raiders (mechanitors) bring only mechanoids the colony has researched
	public static class MechEscorts
	{
		private static readonly Dictionary<ThingDef, List<RecipeDef>> recipes = new Dictionary<ThingDef, List<RecipeDef>>();

		public static bool Researched(ThingDef race)
		{
			List<RecipeDef> list;
			if (!recipes.TryGetValue(race, out list))
			{
				list = new List<RecipeDef>();
				foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
				{
					if (r != null && r.ProducedThingDef == race)
					{
						list.Add(r);
					}
				}
				recipes[race] = list;
			}
			if (list.Count == 0)
			{
				// no gestation recipe (special mechs): only with the top mech research
				ResearchProjectDef top = DefDatabase<ResearchProjectDef>.GetNamedSilentFail("UltraMechtech");
				return top != null && top.IsFinished;
			}
			for (int i = 0; i < list.Count; i++)
			{
				if (list[i].AvailableNow)
				{
					return true;
				}
			}
			return false;
		}

		public static IEnumerable<Pawn> Filter(IEnumerable<Pawn> original, PawnGroupMakerParms parms)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (original == null || settings == null || !settings.enabled || !settings.filterMechEscorts || parms == null || parms.faction == null || parms.faction.IsPlayer || !parms.faction.def.humanlikeFaction || !ModsConfig.BiotechActive)
			{
				return original;
			}
			List<Pawn> list = new List<Pawn>(original);
			int removed = 0;
			for (int i = list.Count - 1; i >= 0; i--)
			{
				Pawn p = list[i];
				if (p == null || p.RaceProps == null || !p.RaceProps.IsMechanoid || Researched(p.def))
				{
					continue;
				}
				list.RemoveAt(i);
				removed++;
				try
				{
					if (!p.Spawned && !p.Destroyed)
					{
						Find.WorldPawns.PassToWorld(p, PawnDiscardDecideMode.Discard);
					}
				}
				catch
				{
				}
			}
			if (removed > 0 && list.Count == 0 && original != null)
			{
				// never leave an empty raid behind
				return original;
			}
			if (removed > 0 && settings.notifyRaid && Prefs.DevMode)
			{
				MLog.Info("[World at Your Level] removed " + removed + " unresearched mechanoids from a " + parms.faction.Name + " group.");
			}
			return list;
		}
	}
	// 2.13.0: rare raids (idea from a Workshop comment). Now and then a raid
	// brings gear one tech level above the colony – fewer raiders, and the gear
	// stays as loot – or gear one level below with more raiders.
	public static class RareRaid
	{
		public static int Shift;

		private static bool busy;

		private const float AdvancedPoints = 0.75f;

		private const float LowTechPoints = 1.35f;

		private static bool TargetsPlayerHome(PawnGroupMakerParms parms)
		{
			List<Map> maps = Find.Maps;
			for (int i = 0; i < maps.Count; i++)
			{
				if (maps[i].IsPlayerHome && maps[i].Tile == parms.tile)
				{
					return true;
				}
			}
			return false;
		}

		public static IEnumerable<Pawn> Wrap(IEnumerable<Pawn> original, PawnGroupMakerParms parms)
		{
			ResearchLockedRaidGearSettings settings = ResearchLockedRaidGearMod.Settings;
			if (original == null || busy || settings == null || !settings.enabled || settings.useManualLevel)
			{
				return original;
			}
			if (RaidCompensation.Collecting || RaidCompensation.GeneratingExtra || parms == null || parms.faction == null || parms.points <= 0f)
			{
				return original;
			}
			if (parms.groupKind != PawnGroupKindDefOf.Combat || parms.faction.IsPlayer || Faction.OfPlayer == null || !parms.faction.HostileTo(Faction.OfPlayer))
			{
				return original;
			}
			if (settings.exemptFactions != null && settings.exemptFactions.Contains(parms.faction.def.defName))
			{
				return original;
			}
			if (Find.Maps == null || !TargetsPlayerHome(parms))
			{
				return original;
			}
			int limit = (int)ResearchLockedGearFilter.TechLimit(settings);
			int shift = 0;
			// advanced only when the raiders' faction really has something above the colony
			if (settings.rareAdvancedChance > 0 && limit < 7 && (int)parms.faction.def.techLevel > limit && Rand.Chance(settings.rareAdvancedChance / 100f))
			{
				shift = 1;
			}
			else if (settings.rareLowTechChance > 0 && limit > 2 && Rand.Chance(settings.rareLowTechChance / 100f))
			{
				shift = -1;
			}
			if (shift == 0)
			{
				return original;
			}
			parms.points *= shift > 0 ? AdvancedPoints : LowTechPoints;
			List<Pawn> list;
			busy = true;
			Shift = shift;
			ResearchLockedGearFilter.InvalidateAll();
			try
			{
				list = new List<Pawn>(original);
				if (shift > 0)
				{
					// what they carry now stays theirs when they arrive on the map
					foreach (Pawn p in list)
					{
						ResearchLockedGearFilter.KeepAll(p);
					}
				}
			}
			finally
			{
				Shift = 0;
				busy = false;
				ResearchLockedGearFilter.InvalidateAll();
			}
			try
			{
				TechLevel shown = (TechLevel)Mathf.Clamp(limit + shift, 1, 7);
				Messages.Message((shift > 0 ? "RLRG_MsgRareAdvanced" : "RLRG_MsgRareLowTech").Translate(shown.ToStringHuman()), shift > 0 ? MessageTypeDefOf.ThreatBig : MessageTypeDefOf.NeutralEvent, false);
				if (Prefs.DevMode)
				{
					MLog.Info("[World at Your Level] rare raid " + (shift > 0 ? "+1" : "-1") + " tech level, " + Mathf.RoundToInt(parms.points) + " points, " + list.Count + " pawns.");
				}
			}
			catch
			{
			}
			return list;
		}
	}

	public static class RaidCompensationPatch
	{
		public static void Postfix(PawnGroupMakerParms parms, ref IEnumerable<Pawn> __result)
		{
			__result = MechEscorts.Filter(RaidCompensation.Wrap(RareRaid.Wrap(__result, parms), parms), parms);
		}
	}
	public static class GenerateGearForPatch
	{
		public static void Postfix(Pawn pawn)
		{
			ResearchLockedGearFilter.FilterPawn(pawn);
		}
	}
	public static class GeneratePawnPatch
	{
		public static void Postfix(Pawn __result)
		{
			ResearchLockedGearFilter.FilterPawn(__result);
		}
	}
	public static class PawnSpawnSetupPatch
	{
		public static void Postfix(Pawn __instance, bool respawningAfterLoad)
		{
			if (!respawningAfterLoad && __instance != null && __instance.Map != null)
			{
				ResearchLockedGearFilter.FilterPawn(__instance);
			}
		}
	}
	public static class AddEquipmentPatch
	{
		public static bool Prefix(Pawn_EquipmentTracker __instance, ThingWithComps newEq)
		{
			if (__instance == null || newEq == null)
			{
				return true;
			}
			Pawn pawn = __instance.pawn;
			if (!ResearchLockedGearFilter.BlocksPickup(pawn, newEq))
			{
				return true;
			}
			// 2.9.7: a thing still held by a container must not be placed (GenSpawn error)
			if (!newEq.Destroyed && !newEq.Spawned && newEq.holdingOwner == null && pawn.Map != null)
			{
				ResearchLockedGearFilter.PutAside(pawn, newEq);
			}
			return false;
		}
	}
	public static class WearApparelPatch
	{
		public static bool Prefix(Pawn_ApparelTracker __instance, Apparel newApparel)
		{
			if (__instance == null || newApparel == null)
			{
				return true;
			}
			if (!ResearchLockedGearFilter.BlocksPickup(__instance.pawn, newApparel))
			{
				return true;
			}
			// 2.9.5: put a blocked piece down instead of leaving it nowhere
			Pawn pawn = __instance.pawn;
			if (pawn != null && !newApparel.Destroyed && !newApparel.Spawned && newApparel.ParentHolder == null && pawn.Map != null)
			{
				ResearchLockedGearFilter.PutAside(pawn, newApparel);
			}
			return false;
		}
	}
	public static class JobDriverEquipPatch
	{
		public static bool Prefix(JobDriver_Equip __instance, ref bool __result)
		{
			Job job = __instance.job;
			if (job == null || job.targetA.Thing == null)
			{
				return true;
			}
			if (!ResearchLockedGearFilter.BlocksPickup(__instance.pawn, job.targetA.Thing))
			{
				return true;
			}
			__result = false;
			return false;
		}
	}
	public static class ShouldEquipWeaponPatch
	{
		public static void Postfix(Thing newWep, Pawn pawn, ref bool __result)
		{
			if (__result && ResearchLockedGearFilter.BlocksPickup(pawn, newWep))
			{
				__result = false;
			}
		}
	}
	public static class ShouldEquipUtilityPatch
	{
		public static void Postfix(Thing thing, Pawn pawn, ref bool __result)
		{
			if (__result && ResearchLockedGearFilter.BlocksPickup(pawn, thing))
			{
				__result = false;
			}
		}
	}

	// 2.10.0: raid types the player switched off in the "Raids" tab
	public static class RaidTypeFilter
	{
		public sealed class Row
		{
			public bool header;

			public int group;

			public string defName;

			public string label;

			public string mod;

			public string desc;
		}

		public const int GroupIncidents = 0;

		public const int GroupStrategies = 1;

		public const int GroupArrivals = 2;

		public const int GroupQuests = 3;

		private static HashSet<string> questSet;

		private static List<Row>[] all;

		private static HashSet<string> incSet;

		private static HashSet<string> stratSet;

		private static HashSet<string> modeSet;

		private static int bypass;

		private static bool loggedStrategy;

		private static bool loggedMode;

		public static void Invalidate()
		{
			incSet = null;
			stratSet = null;
			modeSet = null;
			questSet = null;
		}

		private static HashSet<string> MakeSet(List<string> list)
		{
			HashSet<string> set = new HashSet<string>();
			if (list != null)
			{
				for (int i = 0; i < list.Count; i++)
				{
					if (!string.IsNullOrEmpty(list[i]))
					{
						set.Add(list[i]);
					}
				}
			}
			return set;
		}

		private static void EnsureSets()
		{
			if (incSet != null && stratSet != null && modeSet != null && questSet != null)
			{
				return;
			}
			ResearchLockedRaidGearSettings s = ResearchLockedRaidGearMod.Settings;
			incSet = MakeSet((s == null) ? null : s.blockedIncidents);
			stratSet = MakeSet((s == null) ? null : s.blockedRaidStrategies);
			modeSet = MakeSet((s == null) ? null : s.blockedArrivalModes);
			questSet = MakeSet((s == null) ? null : s.blockedQuests);
		}

		public static bool QuestBlocked(QuestScriptDef def)
		{
			if (def == null)
			{
				return false;
			}
			EnsureSets();
			return questSet.Count > 0 && questSet.Contains(def.defName);
		}

		public static bool IncidentBlocked(IncidentDef def)
		{
			if (def == null)
			{
				return false;
			}
			EnsureSets();
			return incSet.Count > 0 && incSet.Contains(def.defName);
		}

		public static bool StrategyBlocked(RaidStrategyDef def)
		{
			if (def == null)
			{
				return false;
			}
			EnsureSets();
			return stratSet.Count > 0 && stratSet.Contains(def.defName);
		}

		public static bool ModeBlocked(PawnsArrivalModeDef def)
		{
			if (def == null)
			{
				return false;
			}
			EnsureSets();
			return modeSet.Count > 0 && modeSet.Contains(def.defName);
		}

		public static List<string> ListFor(int group)
		{
			ResearchLockedRaidGearSettings s = ResearchLockedRaidGearMod.Settings;
			if (s == null)
			{
				return null;
			}
			switch (group)
			{
			case GroupIncidents:
				return s.blockedIncidents;
			case GroupStrategies:
				return s.blockedRaidStrategies;
			case GroupQuests:
				return s.blockedQuests;
			default:
				return s.blockedArrivalModes;
			}
		}

		public static string GroupTitle(int group)
		{
			switch (group)
			{
			case GroupIncidents:
				return "RLRG_RaidsGroupIncidents".Translate();
			case GroupStrategies:
				return "RLRG_RaidsGroupStrategies".Translate();
			case GroupQuests:
				return "RLRG_RaidsGroupQuests".Translate();
			default:
				return "RLRG_RaidsGroupArrivals".Translate();
			}
		}

		// ---------- harmony patching of every CanUseWith override ----------

		public static void PatchWorkers(Harmony h)
		{
			PatchOverrides(h, typeof(RaidStrategyWorker), new Type[2]
			{
				typeof(IncidentParms),
				typeof(PawnGroupKindDef)
			}, "StrategyPostfix");
			PatchOverrides(h, typeof(PawnsArrivalModeWorker), new Type[1] { typeof(IncidentParms) }, "ModePostfix");
		}

		private static void PatchOverrides(Harmony h, Type baseType, Type[] args, string postfix)
		{
			List<Type> types = new List<Type>();
			types.Add(baseType);
			try
			{
				foreach (Type t in GenTypes.AllSubclasses(baseType))
				{
					types.Add(t);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] could not list " + baseType.Name + " subclasses: " + ex.Message);
			}
			HashSet<MethodInfo> done = new HashSet<MethodInfo>();
			HarmonyMethod hm = new HarmonyMethod(typeof(RaidTypeFilter), postfix);
			for (int i = 0; i < types.Count; i++)
			{
				MethodInfo m = null;
				try
				{
					m = types[i].GetMethod("CanUseWith", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly, null, args, null);
				}
				catch
				{
				}
				if (m == null || m.IsAbstract || m.GetMethodBody() == null || !done.Add(m))
				{
					continue;
				}
				try
				{
					h.Patch(m, null, hm);
				}
				catch (Exception ex2)
				{
					MLog.Warn("[World at Your Level] skip " + types[i].Name + ".CanUseWith: " + ex2.Message);
				}
			}
		}

		public static void StrategyPostfix(RaidStrategyWorker __instance, IncidentParms parms, PawnGroupKindDef groupKind, ref bool __result)
		{
			if (!__result || bypass > 0 || __instance == null || __instance.def == null)
			{
				return;
			}
			try
			{
				if (!StrategyBlocked(__instance.def))
				{
					return;
				}
				if (parms != null && !AnyUnblockedStrategy(parms, groupKind))
				{
					// nothing else is left for this raid: fall back to ImmediateAttack instead of breaking it
					RaidStrategyDef fallback = RaidStrategyDefOf.ImmediateAttack;
					if (__instance.def == fallback || !RawStrategy(fallback, parms, groupKind))
					{
						if (!loggedStrategy)
						{
							loggedStrategy = true;
							MLog.Info("[World at Your Level] all raid strategies usable for a raid are blocked in settings; using " + __instance.def.defName + " as fallback.");
						}
						return;
					}
				}
				__result = false;
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[World at Your Level] raid strategy filter: " + ex, 0x52464C31);
			}
		}

		public static void ModePostfix(PawnsArrivalModeWorker __instance, IncidentParms parms, ref bool __result)
		{
			if (!__result || bypass > 0 || __instance == null || __instance.def == null)
			{
				return;
			}
			try
			{
				if (!ModeBlocked(__instance.def))
				{
					return;
				}
				if (parms != null)
				{
					List<PawnsArrivalModeDef> candidates = (parms.raidStrategy != null && parms.raidStrategy.arriveModes != null) ? parms.raidStrategy.arriveModes : DefDatabase<PawnsArrivalModeDef>.AllDefsListForReading;
					if (!AnyUnblockedMode(candidates, parms))
					{
						PawnsArrivalModeDef fallback = PawnsArrivalModeDefOf.EdgeWalkIn;
						if (__instance.def == fallback || !candidates.Contains(fallback) || !RawMode(fallback, parms))
						{
							if (!loggedMode)
							{
								loggedMode = true;
								MLog.Info("[World at Your Level] all arrival modes usable for a raid are blocked in settings; using " + __instance.def.defName + " as fallback.");
							}
							return;
						}
					}
				}
				__result = false;
			}
			catch (Exception ex)
			{
				Log.ErrorOnce("[World at Your Level] arrival mode filter: " + ex, 0x52464C32);
			}
		}

		private static bool RawStrategy(RaidStrategyDef def, IncidentParms parms, PawnGroupKindDef groupKind)
		{
			if (def == null || def.workerClass == null)
			{
				return false;
			}
			bypass++;
			try
			{
				return def.Worker.CanUseWith(parms, groupKind);
			}
			catch
			{
				return false;
			}
			finally
			{
				bypass--;
			}
		}

		private static bool RawMode(PawnsArrivalModeDef def, IncidentParms parms)
		{
			if (def == null || def.workerClass == null)
			{
				return false;
			}
			bypass++;
			try
			{
				return def.Worker.CanUseWith(parms);
			}
			catch
			{
				return false;
			}
			finally
			{
				bypass--;
			}
		}

		private static bool AnyUnblockedStrategy(IncidentParms parms, PawnGroupKindDef groupKind)
		{
			List<RaidStrategyDef> defs = DefDatabase<RaidStrategyDef>.AllDefsListForReading;
			for (int i = 0; i < defs.Count; i++)
			{
				RaidStrategyDef d = defs[i];
				if (d != null && !StrategyBlocked(d) && RawStrategy(d, parms, groupKind))
				{
					return true;
				}
			}
			return false;
		}

		private static bool AnyUnblockedMode(List<PawnsArrivalModeDef> defs, IncidentParms parms)
		{
			for (int i = 0; i < defs.Count; i++)
			{
				PawnsArrivalModeDef d = defs[i];
				if (d != null && !ModeBlocked(d) && RawMode(d, parms))
				{
					return true;
				}
			}
			return false;
		}

		// ---------- lists for the settings tab ----------

		public static bool IsThreatIncident(IncidentDef def)
		{
			if (def == null)
			{
				return false;
			}
			if (def.category != null && (def.category == IncidentCategoryDefOf.ThreatBig || def.category == IncidentCategoryDefOf.ThreatSmall))
			{
				return true;
			}
			if (def.workerClass != null && (typeof(IncidentWorker_Raid).IsAssignableFrom(def.workerClass)))
			{
				return true;
			}
			// 2.12.1: mods with their own threat categories (Vanilla Genetics Expanded "roaming
			// monstrosities" = GR_HybridRaid) – a mod category that scales with threat points, or whose
			// name or worker says raid / threat / manhunter / monster
			if (def.category != null && def.category.modContentPack != null && !def.category.modContentPack.IsOfficialMod && def.category.needsParmsPoints)
			{
				return true;
			}
			// 2.12.2: every threat – anything the game announces with a threat letter (red/yellow)
			// or a bad-event letter (toxic fallout, solar flare, psychic drone, blight …)
			if (def.letterDef != null && (def.letterDef == LetterDefOf.ThreatBig || def.letterDef == LetterDefOf.ThreatSmall || def.letterDef == LetterDefOf.NegativeEvent))
			{
				return true;
			}
			if (def.letterDef != null && def.letterDef.defName.IndexOf("Threat", StringComparison.OrdinalIgnoreCase) >= 0)
			{
				return true;
			}
			string n = (def.category?.defName ?? "") + "|" + (def.workerClass?.Name ?? "") + "|" + def.defName;
			string[] words = { "Raid", "Threat", "Manhunter", "Monst", "Infestation", "Siege", "Ambush", "Assault", "Attack", "Hostile", "Predator", "Insanity", "Scaria", "Horde", "Swarm", "Shambler", "Mech", "Drone", "Bombard", "Disease", "Plague", "Blight", "Fallout", "Flare", "Eclipse", "Volcanic" };
			for (int i = 0; i < words.Length; i++)
			{
				if (n.IndexOf(words[i], StringComparison.OrdinalIgnoreCase) >= 0)
				{
					return true;
				}
			}
			return false;
		}

		private static string ModName(Def def)
		{
			if (def == null || def.modContentPack == null)
			{
				return "?";
			}
			return def.modContentPack.Name ?? def.modContentPack.PackageId ?? "?";
		}

		private static string Nice(Def def)
		{
			if (!string.IsNullOrEmpty(def.label))
			{
				return def.LabelCap.ToString();
			}
			return GenText.SplitCamelCase(def.defName);
		}

		private static string CleanText(string text)
		{
			if (string.IsNullOrEmpty(text))
			{
				return null;
			}
			return text.Replace("{0}", "…").Replace("{1}", "…");
		}

		private static int CompareRows(Row a, Row b)
		{
			return string.Compare(a.label, b.label, StringComparison.OrdinalIgnoreCase);
		}

		public static void EnsureLists()
		{
			if (all != null)
			{
				return;
			}
			List<Row>[] lists = new List<Row>[4];
			lists[0] = new List<Row>();
			lists[1] = new List<Row>();
			lists[2] = new List<Row>();
			lists[3] = new List<Row>();
			try
			{
				foreach (IncidentDef d in DefDatabase<IncidentDef>.AllDefsListForReading)
				{
					if (IsThreatIncident(d))
					{
						Row row = new Row();
						row.group = GroupIncidents;
						row.defName = d.defName;
						row.label = Nice(d);
						row.mod = ModName(d);
						row.desc = d.description;
						lists[0].Add(row);
					}
				}
				foreach (RaidStrategyDef d2 in DefDatabase<RaidStrategyDef>.AllDefsListForReading)
				{
					if (d2 == null)
					{
						continue;
					}
					Row row2 = new Row();
					row2.group = GroupStrategies;
					row2.defName = d2.defName;
					row2.label = !string.IsNullOrEmpty(d2.label) ? Nice(d2) : (!string.IsNullOrEmpty(d2.letterLabelEnemy) ? d2.letterLabelEnemy.CapitalizeFirst() : Nice(d2));
					row2.mod = ModName(d2);
					row2.desc = !string.IsNullOrEmpty(d2.description) ? d2.description : CleanText(d2.arrivalTextEnemy);
					lists[1].Add(row2);
				}
				foreach (PawnsArrivalModeDef d3 in DefDatabase<PawnsArrivalModeDef>.AllDefsListForReading)
				{
					if (d3 == null)
					{
						continue;
					}
					Row row3 = new Row();
					row3.group = GroupArrivals;
					row3.defName = d3.defName;
					row3.label = Nice(d3);
					row3.mod = ModName(d3);
					row3.desc = !string.IsNullOrEmpty(d3.description) ? d3.description : CleanText(d3.textEnemy);
					lists[2].Add(row3);
				}
				// 2.10.2: quests the storyteller picks at random (threat quests first in your mind,
				// but every random quest can be switched off here)
				foreach (QuestScriptDef d4 in DefDatabase<QuestScriptDef>.AllDefsListForReading)
				{
					if (d4 == null || !d4.IsRootRandomSelected)
					{
						continue;
					}
					Row row4 = new Row();
					row4.group = GroupQuests;
					row4.defName = d4.defName;
					bool threat = d4.questAvailableLetterDef != null && (d4.questAvailableLetterDef == LetterDefOf.ThreatBig || d4.questAvailableLetterDef == LetterDefOf.ThreatSmall);
					row4.label = (threat ? "[!] " : string.Empty) + Nice(d4);
					row4.mod = ModName(d4);
					row4.desc = d4.description;
					lists[3].Add(row4);
				}
			}
			catch (Exception ex)
			{
				MLog.Warn("[World at Your Level] could not list raid types: " + ex.Message);
			}
			for (int i = 0; i < 4; i++)
			{
				lists[i].Sort(CompareRows);
			}
			all = lists;
		}

		public static List<Row> BuildRows(string search)
		{
			EnsureLists();
			List<Row> rows = new List<Row>();
			for (int g = 0; g < 4; g++)
			{
				Row header = new Row();
				header.header = true;
				header.group = g;
				rows.Add(header);
				List<Row> list = all[g];
				for (int i = 0; i < list.Count; i++)
				{
					Row r = list[i];
					if (string.IsNullOrEmpty(search) || (r.label ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (r.defName ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0 || (r.mod ?? string.Empty).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
					{
						rows.Add(r);
					}
				}
			}
			return rows;
		}

		public static int TotalCount(int group)
		{
			EnsureLists();
			return all[group].Count;
		}

		// only defs that exist in this game are counted
		public static int BlockedCount(int group)
		{
			EnsureLists();
			List<string> blocked = ListFor(group);
			if (blocked == null || blocked.Count == 0)
			{
				return 0;
			}
			int n = 0;
			List<Row> list = all[group];
			for (int i = 0; i < list.Count; i++)
			{
				if (blocked.Contains(list[i].defName))
				{
					n++;
				}
			}
			return n;
		}

		public static int TotalCountAll()
		{
			return TotalCount(0) + TotalCount(1) + TotalCount(2) + TotalCount(3);
		}

		public static int BlockedCountAll()
		{
			return BlockedCount(0) + BlockedCount(1) + BlockedCount(2) + BlockedCount(3);
		}
	}

	public static class RaidTypeIncidentPatch
	{
		public static void Postfix(IncidentWorker __instance, ref bool __result)
		{
			try
			{
				if (__result && __instance != null && RaidTypeFilter.IncidentBlocked(__instance.def))
				{
					__result = false;
				}
			}
			catch
			{
			}
		}
	}
}
