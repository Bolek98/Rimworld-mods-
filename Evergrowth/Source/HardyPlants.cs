using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // Settings
    // ------------------------------------------------------------------
    public class HardyPlantsSettings : ModSettings
    {
        // null until the player changes the list: then the default (wild plants
        // of cold biomes) is used, so new cold-biome plants from other mods are
        // picked up automatically.
        public List<string> chosen;
        // Which group is used while the player has not picked plants by hand.
        public PlantGroup group = PlantGroup.AllWild;

        // 1.1.0: plants also GROW in the cold, not only survive it.
        public const float DefMinGrowth = -10f;
        public const float DefMinOptimal = 0f;
        public const float DefMaxOptimal = 42f;
        public const float DefMaxGrowth = 58f;
        public const float DefFrost = 5f;
        private const int CurrentVersion = 3;

        public float minGrowth = DefMinGrowth;
        public float minOptimal = DefMinOptimal;
        public float maxOptimal = DefMaxOptimal;
        public float maxGrowth = DefMaxGrowth;
        public float frostTolerance = DefFrost;
        private int version = CurrentVersion;

        // 1.3.0: growth speed and growing day and night. Both off by default.
        // The patches below read them on every growth step, so a change works
        // at once for every plant on every map, also for plants that appear
        // later. No restart.
        public bool fasterGrowth = false;
        public float growthSpeed = 2f;
        public bool growAllDay = false;

        // 1.4.0: how many days crops and trees need to grow (at 100 % growth
        // rate). Off by default; applied when the settings window is closed.
        public bool cropDaysOn = false;
        public float cropDays = 5f;
        public bool treeDaysOn = false;
        public float treeDays = 10f;

        // 1.6.0: separate speed for crops, trees and wild plants, harvest
        // yield, wild plant regrowth, soil and blight. All off by default.
        public float cropSpeed = 2f, treeSpeed = 2f, wildSpeed = 2f;
        public bool yieldOn = false;
        public float cropYield = 1.5f, woodYield = 1.5f;
        public bool regrowOn = false;
        public float regrowSpeed = 2f;
        public bool ignoreSoil = false;
        public bool noBlight = false;

        // 1.7.0: chosen profile. 0 Vanilla, 1 Recommended, 2 Lush, 3 Custom.
        // A new install starts on Recommended; settings from an older version
        // stay Custom (missing key). Profiles 0-2 are applied again on every
        // start, so a better recommendation in a new version arrives on its own.
        public int profile = 1;

        public static readonly string[] ProfileFields =
        {
            "fasterGrowth", "growAllDay", "yieldOn", "regrowOn", "ignoreSoil", "noBlight", "cropDaysOn", "treeDaysOn",
            "cropSpeed", "treeSpeed", "wildSpeed", "cropYield", "woodYield", "regrowSpeed"
        };

        private static HardyPlantsSettings[] presetCache;

        public static HardyPlantsSettings PresetValues(int which)
        {
            if (presetCache == null)
            {
                presetCache = new HardyPlantsSettings[3];
                for (int i = 0; i < 3; i++)
                {
                    presetCache[i] = new HardyPlantsSettings();
                    presetCache[i].Preset(i);
                }
            }
            return presetCache[which];
        }

        public bool Matches(int which)
        {
            HardyPlantsSettings p = PresetValues(which);
            foreach (string n in ProfileFields)
            {
                var f = typeof(HardyPlantsSettings).GetField(n);
                object a = f.GetValue(this), b = f.GetValue(p);
                if (a is float ? Mathf.Abs((float)a - (float)b) > 0.0005f : !Equals(a, b))
                    return false;
            }
            return true;
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref version, "version", 1);
            Scribe_Collections.Look(ref chosen, "chosen", LookMode.Value);
            Scribe_Values.Look(ref group, "group", PlantGroup.AllWild);
            Scribe_Values.Look(ref minGrowth, "minGrowth", DefMinGrowth);
            Scribe_Values.Look(ref minOptimal, "minOptimal", DefMinOptimal);
            Scribe_Values.Look(ref maxOptimal, "maxOptimal", DefMaxOptimal);
            Scribe_Values.Look(ref maxGrowth, "maxGrowth", DefMaxGrowth);
            Scribe_Values.Look(ref frostTolerance, "frostTolerance", DefFrost);
            Scribe_Values.Look(ref fasterGrowth, "fasterGrowth", false);
            Scribe_Values.Look(ref growthSpeed, "growthSpeed", 2f);
            Scribe_Values.Look(ref growAllDay, "growAllDay", false);
            Scribe_Values.Look(ref cropDaysOn, "cropDaysOn", false);
            Scribe_Values.Look(ref cropDays, "cropDays", 5f);
            Scribe_Values.Look(ref treeDaysOn, "treeDaysOn", false);
            Scribe_Values.Look(ref treeDays, "treeDays", 10f);
            Scribe_Values.Look(ref cropSpeed, "cropSpeed", -1f);
            Scribe_Values.Look(ref treeSpeed, "treeSpeed", -1f);
            Scribe_Values.Look(ref wildSpeed, "wildSpeed", -1f);
            Scribe_Values.Look(ref yieldOn, "yieldOn", false);
            Scribe_Values.Look(ref cropYield, "cropYield", 1.5f);
            Scribe_Values.Look(ref woodYield, "woodYield", 1.5f);
            Scribe_Values.Look(ref regrowOn, "regrowOn", false);
            Scribe_Values.Look(ref regrowSpeed, "regrowSpeed", 2f);
            Scribe_Values.Look(ref ignoreSoil, "ignoreSoil", false);
            Scribe_Values.Look(ref noBlight, "noBlight", false);
            Scribe_Values.Look(ref profile, "profile", 3);
            if (Scribe.mode == LoadSaveMode.LoadingVars)
            {
                // settings from before 1.6: one speed for everything
                if (cropSpeed <= 0f) cropSpeed = growthSpeed;
                if (treeSpeed <= 0f) treeSpeed = growthSpeed;
                if (wildSpeed <= 0f) wildSpeed = growthSpeed;
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars && version < 2)
            {
                // Settings from 1.0.x only made plants survive; switch to the new
                // defaults so plants grow in the cold.
                ResetValues();
                chosen = null;
                group = PlantGroup.AllWild;
            }
            if (Scribe.mode == LoadSaveMode.LoadingVars)
                version = CurrentVersion;
            if (Scribe.mode == LoadSaveMode.LoadingVars && profile >= 0 && profile <= 2)
                Preset(profile);
        }

        public void Preset(int which)
        {
            fasterGrowth = growAllDay = yieldOn = regrowOn = ignoreSoil = noBlight = cropDaysOn = treeDaysOn = false;
            cropSpeed = treeSpeed = wildSpeed = 1f;
            cropYield = woodYield = 1f;
            regrowSpeed = 1f;
            if (which == 1) // easy farming
            {
                fasterGrowth = true; cropSpeed = 1.5f; treeSpeed = 2f; wildSpeed = 1.5f;
                yieldOn = true; cropYield = 1.25f; woodYield = 1.5f;
                noBlight = true;
            }
            else if (which == 2) // lush world
            {
                fasterGrowth = true; cropSpeed = 2f; treeSpeed = 3f; wildSpeed = 3f;
                growAllDay = true;
                yieldOn = true; cropYield = 1.5f; woodYield = 2f;
                regrowOn = true; regrowSpeed = 3f;
                ignoreSoil = true; noBlight = true;
            }
        }

        public void ResetValues()
        {
            minGrowth = DefMinGrowth;
            minOptimal = DefMinOptimal;
            maxOptimal = DefMaxOptimal;
            maxGrowth = DefMaxGrowth;
            frostTolerance = DefFrost;
        }
    }

    public enum PlantGroup
    {
        ColdBiomes,
        AllWild,
        AllPlants
    }

    // ------------------------------------------------------------------
    // Mod
    // ------------------------------------------------------------------
    public class HardyPlantsMod : Mod
    {
        public const string Version = "1.7.1";
        public static HardyPlantsSettings Settings;
        private Vector2 scroll;
        private Vector2 leftScroll;
        private float leftHeight = 1200f;
        private string search = "";

        public HardyPlantsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<HardyPlantsSettings>();
            if (Settings.profile >= 0 && Settings.profile <= 2)
                Settings.Preset(Settings.profile);
            MUI.Init(HardyPlantsSettings.PresetValues(1), HardyPlantsSettings.ProfileFields);
            var harmony = new Harmony("bolek98.hardyplants");
            harmony.PatchAll();
            MUI.PatchWindow(harmony, typeof(HardyPlantsMod));
            LongEventHandler.ExecuteWhenFinished(HardyPlantsCore.Apply);
        }

        public override string SettingsCategory()
        {
            return "HardyPlants_Title".Translate();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            HardyPlantsCore.Apply();
        }

        private static readonly string[] ProfKeys = { "HardyPlants_PresetVanilla", "HardyPlants_ProfRec", "HardyPlants_PresetLush", "HardyPlants_ProfCustom" };

        public override void DoSettingsWindowContents(Rect inRect)
        {
            HardyPlantsSettings s = Settings;
            // 1.7.0: profile cards on top
            MUI.Version(inRect, "v" + Version + " · Bolek98");
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 20f), "HardyPlants_Profile".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            float cw = (inRect.width - 36f) / 4f;
            for (int i = 0; i < 4; i++)
            {
                Rect c = new Rect(inRect.x + i * (cw + 12f), inRect.y + 22f, cw, 78f);
                if (MUI.ProfileCard(c, ProfKeys[i].Translate(), (ProfKeys[i] + "_Card").Translate(), s.profile == i, i == 1))
                {
                    if (i < 3)
                        s.Preset(i);
                    s.profile = i;
                }
            }
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, inRect.y + 104f, inRect.width, 20f), "HardyPlants_ProfileHint".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            inRect = new Rect(inRect.x, inRect.y + 130f, inRect.width, inRect.height - 130f);
            Rect left = new Rect(inRect.x, inRect.y, inRect.width * 0.55f, inRect.height);
            Rect right = new Rect(left.xMax + 20f, inRect.y, inRect.width - left.width - 20f, inRect.height);

            // ---- temperatures -------------------------------------------------
            // The left column scrolls when the window is small.
            // 1.3.1: one column only. A Listing that runs out of height starts a
            // new column to the right, outside the scroll view: everything after
            // "Full-speed growth from" vanished, the measured height shrank, and
            // the window could not get out of that state.
            Rect leftView = new Rect(0f, 0f, left.width - 16f, Mathf.Max(leftHeight, left.height));
            Widgets.BeginScrollView(left, ref leftScroll, leftView);
            var l = new Listing_Standard { maxOneColumn = true };
            l.Begin(new Rect(0f, 0f, leftView.width, 100000f));
            MUI.Section(l, "HardyPlants_Growth".Translate());
            MUI.Check(l, "HardyPlants_FasterGrowth".Translate(), "HardyPlants_FasterGrowthDesc".Translate(), ref s.fasterGrowth, s, "fasterGrowth");
            if (s.fasterGrowth)
            {
                MUI.RowLabel(l, "HardyPlants_SpeedCrops".Translate(s.cropSpeed.ToString("0.0")), null, s, "cropSpeed");
                s.cropSpeed = SpeedSlider(l, s.cropSpeed, ref bufSpeed);
                MUI.RowLabel(l, "HardyPlants_SpeedTrees".Translate(s.treeSpeed.ToString("0.0")), null, s, "treeSpeed");
                s.treeSpeed = SpeedSlider(l, s.treeSpeed, ref bufSpeedT);
                MUI.RowLabel(l, "HardyPlants_SpeedWild".Translate(s.wildSpeed.ToString("0.0")), null, s, "wildSpeed");
                s.wildSpeed = SpeedSlider(l, s.wildSpeed, ref bufSpeedW);
            }
            MUI.Check(l, "HardyPlants_GrowAllDay".Translate(), "HardyPlants_GrowAllDayDesc".Translate(), ref s.growAllDay, s, "growAllDay");
            MUI.Check(l, "HardyPlants_CropDays".Translate(), "HardyPlants_DaysDesc".Translate(), ref s.cropDaysOn, s, "cropDaysOn");
            if (s.cropDaysOn)
            {
                l.Label("HardyPlants_DaysValue".Translate(s.cropDays.ToString("0.#")));
                s.cropDays = DaysSlider(l, s.cropDays, ref bufCropDays);
            }
            MUI.Check(l, "HardyPlants_TreeDays".Translate(), "HardyPlants_DaysDesc".Translate(), ref s.treeDaysOn, s, "treeDaysOn");
            if (s.treeDaysOn)
            {
                l.Label("HardyPlants_DaysValue".Translate(s.treeDays.ToString("0.#")));
                s.treeDays = DaysSlider(l, s.treeDays, ref bufTreeDays);
            }
            MUI.Section(l, "HardyPlants_SecYield".Translate());
            MUI.Check(l, "HardyPlants_Yield".Translate(), "HardyPlants_YieldDesc".Translate(), ref s.yieldOn, s, "yieldOn");
            if (s.yieldOn)
            {
                MUI.RowLabel(l, "HardyPlants_YieldCrops".Translate(s.cropYield.ToString("0.0#")), null, s, "cropYield");
                s.cropYield = SpeedSlider(l, s.cropYield, ref bufYieldC, 0.1f, 10f);
                MUI.RowLabel(l, "HardyPlants_YieldWood".Translate(s.woodYield.ToString("0.0#")), null, s, "woodYield");
                s.woodYield = SpeedSlider(l, s.woodYield, ref bufYieldW, 0.1f, 10f);
            }
            MUI.Check(l, "HardyPlants_Regrow".Translate(), "HardyPlants_RegrowDesc".Translate(), ref s.regrowOn, s, "regrowOn");
            if (s.regrowOn)
            {
                MUI.RowLabel(l, "HardyPlants_RegrowValue".Translate(s.regrowSpeed.ToString("0.0")), null, s, "regrowSpeed");
                s.regrowSpeed = SpeedSlider(l, s.regrowSpeed, ref bufRegrow, 0.1f, 10f);
            }
            MUI.Check(l, "HardyPlants_IgnoreSoil".Translate(), "HardyPlants_IgnoreSoilDesc".Translate(), ref s.ignoreSoil, s, "ignoreSoil");
            MUI.Check(l, "HardyPlants_NoBlight".Translate(), "HardyPlants_NoBlightDesc".Translate(), ref s.noBlight, s, "noBlight");
            GUI.color = MUI.Muted;
            l.Label("HardyPlants_GrowthNote".Translate());
            GUI.color = Color.white;
            MUI.Section(l, "HardyPlants_Temperatures".Translate());
            GUI.color = MUI.Muted;
            l.Label("HardyPlants_TemperaturesDesc".Translate());
            GUI.color = Color.white;
            l.GapLine();
            // 1.2.0: wider ranges and a number field next to every slider.
            s.minGrowth = TempSlider(l, "HardyPlants_MinGrowth", s.minGrowth, -100f, 30f, 0f, ref bufMinGrowth);
            s.minOptimal = TempSlider(l, "HardyPlants_MinOptimal", s.minOptimal, -90f, 40f, 6f, ref bufMinOptimal);
            s.maxOptimal = TempSlider(l, "HardyPlants_MaxOptimal", s.maxOptimal, 10f, 120f, 42f, ref bufMaxOptimal);
            s.maxGrowth = TempSlider(l, "HardyPlants_MaxGrowth", s.maxGrowth, 20f, 150f, 58f, ref bufMaxGrowth);
            l.Gap();
            l.Label("HardyPlants_Frost".Translate(s.frostTolerance.ToStringTemperatureOffset("F0")));
            s.frostTolerance = SliderWithField(l, s.frostTolerance, 0f, 100f, ref bufFrost);
            l.Label("HardyPlants_FrostDesc".Translate(), -1f);
            HardyPlantsCore.KeepOrder(s);
            l.Gap();
            if (l.ButtonText("HardyPlants_ResetValues".Translate()))
                s.ResetValues();
            MUI.Section(l, "HardyPlants_SecGroup".Translate());
            l.Label("HardyPlants_Group".Translate());
            if (l.RadioButton("HardyPlants_GroupCold".Translate(), s.chosen == null && s.group == PlantGroup.ColdBiomes))
                { s.chosen = null; s.group = PlantGroup.ColdBiomes; }
            if (l.RadioButton("HardyPlants_GroupWild".Translate(), s.chosen == null && s.group == PlantGroup.AllWild))
                { s.chosen = null; s.group = PlantGroup.AllWild; }
            if (l.RadioButton("HardyPlants_GroupAll".Translate(), s.chosen == null && s.group == PlantGroup.AllPlants))
                { s.chosen = null; s.group = PlantGroup.AllPlants; }
            l.RadioButton("HardyPlants_GroupCustom".Translate(), s.chosen != null);
            if (s.profile >= 0 && s.profile <= 2 && !s.Matches(s.profile))
                s.profile = 3;
            leftHeight = l.CurHeight + 20f;
            l.End();
            Widgets.EndScrollView();

            // ---- plant list ---------------------------------------------------
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(right.x, right.y, right.width, 32f), "HardyPlants_Plants".Translate());
            Text.Font = GameFont.Small;
            HashSet<string> chosen = HardyPlantsCore.ChosenSet();
            Widgets.Label(new Rect(right.x, right.y + 34f, right.width, 24f),
                "HardyPlants_Chosen".Translate(chosen.Count));
            search = Widgets.TextField(new Rect(right.x, right.y + 60f, right.width, 28f), search);

            List<ThingDef> plants = HardyPlantsCore.AllPlants()
                .Where(p => search.NullOrEmpty()
                            || p.LabelCap.ToString().IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0
                            || p.defName.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            Rect outRect = new Rect(right.x, right.y + 94f, right.width, right.height - 94f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 16f, plants.Count * 28f);
            Widgets.BeginScrollView(outRect, ref scroll, viewRect);
            float y = 0f;
            foreach (ThingDef p in plants)
            {
                Rect row = new Rect(0f, y, viewRect.width, 26f);
                if (y + 26f >= scroll.y && y <= scroll.y + outRect.height)
                {
                    bool on = chosen.Contains(p.defName);
                    bool was = on;
                    Widgets.DrawHighlightIfMouseover(row);
                    Widgets.DefIcon(new Rect(row.x, row.y + 1f, 24f, 24f), p);
                    Widgets.CheckboxLabeled(new Rect(row.x + 30f, row.y, row.width - 30f, row.height),
                        p.LabelCap + "  (" + HardyPlantsCore.OriginalRange(p) + ")", ref on);
                    if (on != was)
                        HardyPlantsCore.SetChosen(p, on);
                }
                y += 28f;
            }
            Widgets.EndScrollView();
        }

        private string bufMinGrowth, bufMinOptimal, bufMaxOptimal, bufMaxGrowth, bufFrost, bufSpeed, bufSpeedT, bufSpeedW, bufYieldC, bufYieldW, bufRegrow, bufCropDays, bufTreeDays;

        // Days 0.5 – 120 in steps of 0.5, with an exact-number field.
        private static float DaysSlider(Listing_Standard l, float value, ref string buffer)
        {
            Rect row = l.GetRect(28f);
            Rect sliderRect = new Rect(row.x, row.y + 4f, row.width - 90f, 22f);
            Rect fieldRect = new Rect(row.xMax - 80f, row.y, 54f, 26f);
            Rect unitRect = new Rect(row.xMax - 24f, row.y + 2f, 24f, 26f);
            float slid = Mathf.Round(Widgets.HorizontalSlider(sliderRect, value, 0.5f, 120f, true) * 2f) / 2f;
            if (!Mathf.Approximately(slid, value))
            {
                value = slid;
                buffer = null;
            }
            float typed = value;
            Widgets.TextFieldNumeric(fieldRect, ref typed, ref buffer, 0.5f, 120f);
            Widgets.Label(unitRect, "d");
            l.Gap(4f);
            return Mathf.Clamp(typed, 0.5f, 120f);
        }

        // Growth speed ×0.1 – ×20 in steps of 0.1, with an exact-number field.
        private static float SpeedSlider(Listing_Standard l, float value, ref string buffer, float min = 0.1f, float max = 20f)
        {
            Rect row = l.GetRect(28f);
            Rect sliderRect = new Rect(row.x, row.y + 4f, row.width - 90f, 22f);
            Rect fieldRect = new Rect(row.xMax - 80f, row.y, 54f, 26f);
            Rect unitRect = new Rect(row.xMax - 24f, row.y + 2f, 24f, 26f);
            float slid = Mathf.Round(Widgets.HorizontalSlider(sliderRect, value, min, max, true) * 20f) / 20f;
            if (!Mathf.Approximately(slid, value))
            {
                value = slid;
                buffer = null;
            }
            float typed = value;
            Widgets.TextFieldNumeric(fieldRect, ref typed, ref buffer, min, max);
            Widgets.Label(unitRect, "×");
            l.Gap(4f);
            return Mathf.Clamp(typed, min, max);
        }

        private static float TempSlider(Listing_Standard l, string key, float value, float min, float max, float vanilla, ref string buffer)
        {
            l.Label(key.Translate(value.ToStringTemperature("F0"), vanilla.ToStringTemperature("F0")));
            return SliderWithField(l, value, min, max, ref buffer);
        }

        // Slider on the left, exact value in °C on the right.
        private static float SliderWithField(Listing_Standard l, float value, float min, float max, ref string buffer)
        {
            Rect row = l.GetRect(28f);
            Rect sliderRect = new Rect(row.x, row.y + 4f, row.width - 90f, 22f);
            Rect fieldRect = new Rect(row.xMax - 80f, row.y, 54f, 26f);
            Rect unitRect = new Rect(row.xMax - 24f, row.y + 2f, 24f, 26f);
            float slid = Mathf.Round(Widgets.HorizontalSlider(sliderRect, value, min, max, true));
            if (!Mathf.Approximately(slid, value))
            {
                value = slid;
                buffer = null;
            }
            float typed = value;
            Widgets.TextFieldNumeric(fieldRect, ref typed, ref buffer, min, max);
            Widgets.Label(unitRect, "°C");
            l.Gap(4f);
            return Mathf.Clamp(typed, min, max);
        }
    }

    // ------------------------------------------------------------------
    // Core: writes the temperatures into the plant Defs
    // ------------------------------------------------------------------
    public static class HardyPlantsCore
    {
        private sealed class Original
        {
            public float MinGrowth, MinOptimal, MaxOptimal, MaxGrowth;
        }

        private static readonly Dictionary<ThingDef, Original> originals = new Dictionary<ThingDef, Original>();
        private static readonly HashSet<ThingDef> hardy = new HashSet<ThingDef>();
        private static List<ThingDef> allPlants;

        private static readonly string[] ColdBiomeWords =
            { "tundra", "boreal", "taiga", "ice", "glacier", "snow", "arctic", "alpine", "frozen", "cold" };

        public static bool IsHardy(ThingDef def)
        {
            return def != null && hardy.Contains(def);
        }

        // 1.5.1: changing the game language unloads and reloads every Def
        // (PlayDataLoader.ClearAllPlayData + LoadAllPlayData) but does not create
        // the Mod class again. The caches below then pointed at the old Defs:
        // the plant list showed names in the old language, and Apply() changed
        // the old Defs, so the new ones lost every change of this mod.
        // Detect that, drop the caches and apply again to the new Defs.
        private static bool CachesStale()
        {
            if (allPlants == null || allPlants.Count == 0)
                return false;
            ThingDef first = allPlants[0];
            return DefDatabase<ThingDef>.GetNamedSilentFail(first.defName) != first;
        }

        private static void ResetCaches()
        {
            allPlants = null;
            groups.Clear();
            originals.Clear();
            applied.Clear();
            hardy.Clear();
            originalDays.Clear();
            appliedDays.Clear();
        }

        // Called after the game has reloaded all its data (language change).
        public static void Reload()
        {
            if (CachesStale())
                ResetCaches();
            Apply();
        }

        public static List<ThingDef> AllPlants()
        {
            if (CachesStale())
                ResetCaches();
            if (allPlants == null)
            {
                allPlants = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(d => d.plant != null && d.category == ThingCategory.Plant && !d.IsCorpse && d.label != null)
                    .OrderBy(d => d.label)
                    .ToList();
            }
            return allPlants;
        }

        private static readonly Dictionary<PlantGroup, HashSet<string>> groups = new Dictionary<PlantGroup, HashSet<string>>();

        // The plants of a group: wild plants of cold biomes, wild plants of every
        // biome (vanilla and modded), or every plant including crops and trees.
        private static HashSet<string> GroupSet(PlantGroup g)
        {
            HashSet<string> set;
            if (groups.TryGetValue(g, out set))
                return set;
            set = new HashSet<string>();
            if (g == PlantGroup.AllPlants)
            {
                foreach (ThingDef p in AllPlants())
                    set.Add(p.defName);
            }
            else
            {
                foreach (BiomeDef biome in DefDatabase<BiomeDef>.AllDefsListForReading)
                {
                    string name = (biome.defName ?? "").ToLowerInvariant();
                    if (g == PlantGroup.ColdBiomes && !ColdBiomeWords.Any(w => name.Contains(w)))
                        continue;
                    List<ThingDef> wild;
                    try { wild = biome.AllWildPlants; }
                    catch { continue; }
                    foreach (ThingDef p in wild)
                        if (p != null && p.plant != null)
                            set.Add(p.defName);
                }
            }
            groups[g] = set;
            return set;
        }

        private static HashSet<string> DefaultChoice()
        {
            return GroupSet(HardyPlantsMod.Settings.group);
        }

        public static HashSet<string> ChosenSet()
        {
            var s = HardyPlantsMod.Settings;
            return s.chosen == null ? new HashSet<string>(DefaultChoice()) : new HashSet<string>(s.chosen);
        }

        public static void SetChosen(ThingDef plant, bool on)
        {
            var s = HardyPlantsMod.Settings;
            if (s.chosen == null)
                s.chosen = DefaultChoice().ToList();
            s.chosen.Remove(plant.defName);
            if (on)
                s.chosen.Add(plant.defName);
        }

        // min growth < min optimal <= max optimal < max growth
        public static void KeepOrder(HardyPlantsSettings s)
        {
            if (s.minOptimal <= s.minGrowth) s.minOptimal = s.minGrowth + 1f;
            if (s.maxOptimal < s.minOptimal) s.maxOptimal = s.minOptimal;
            if (s.maxGrowth <= s.maxOptimal) s.maxGrowth = s.maxOptimal + 1f;
        }

        public static string OriginalRange(ThingDef p)
        {
            Original o;
            if (!originals.TryGetValue(p, out o))
                o = Read(p);
            return o.MinGrowth.ToStringTemperature("F0") + " – " + o.MaxGrowth.ToStringTemperature("F0");
        }

        private static Original Read(ThingDef p)
        {
            return new Original
            {
                MinGrowth = p.plant.minGrowthTemperature,
                MinOptimal = p.plant.minOptimalGrowthTemperature,
                MaxOptimal = p.plant.maxOptimalGrowthTemperature,
                MaxGrowth = p.plant.maxGrowthTemperature
            };
        }

        // A chosen plant only ever gets more tolerant than it was: colder minimums,
        // warmer maximums. A tropical plant keeps its own heat range, a modded
        // plant that is already tougher keeps its values.
        //
        // 1.0.1: plants that are not chosen are never touched. A plant is put back
        // only if it still has exactly the values this mod gave it, so changes
        // made by other mods (wild plant / growth mods that run later) are kept.
        private static readonly Dictionary<ThingDef, Original> applied = new Dictionary<ThingDef, Original>();

        private static bool Same(Original a, PlantProperties pp)
        {
            return a != null
                && Mathf.Approximately(a.MinGrowth, pp.minGrowthTemperature)
                && Mathf.Approximately(a.MinOptimal, pp.minOptimalGrowthTemperature)
                && Mathf.Approximately(a.MaxOptimal, pp.maxOptimalGrowthTemperature)
                && Mathf.Approximately(a.MaxGrowth, pp.maxGrowthTemperature);
        }

        public static void Apply()
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null)
                return;
            KeepOrder(s);
            HashSet<string> chosen = ChosenSet();
            hardy.Clear();
            int changed = 0;
            foreach (ThingDef p in AllPlants())
            {
                PlantProperties pp = p.plant;
                Original mine;
                applied.TryGetValue(p, out mine);
                bool stillMine = Same(mine, pp);

                if (chosen.Contains(p.defName))
                {
                    // The current values are the base unless they are our own.
                    if (!stillMine)
                        originals[p] = Read(p);
                    Original o = originals[p];
                    pp.minGrowthTemperature = Mathf.Min(o.MinGrowth, s.minGrowth);
                    pp.minOptimalGrowthTemperature = Mathf.Max(pp.minGrowthTemperature + 1f, Mathf.Min(o.MinOptimal, s.minOptimal));
                    pp.maxOptimalGrowthTemperature = Mathf.Max(o.MaxOptimal, s.maxOptimal, pp.minOptimalGrowthTemperature);
                    pp.maxGrowthTemperature = Mathf.Max(o.MaxGrowth, s.maxGrowth, pp.maxOptimalGrowthTemperature + 1f);
                    applied[p] = Read(p);
                    hardy.Add(p);
                    changed++;
                }
                else if (mine != null)
                {
                    Original o;
                    if (stillMine && originals.TryGetValue(p, out o))
                    {
                        pp.minGrowthTemperature = o.MinGrowth;
                        pp.minOptimalGrowthTemperature = o.MinOptimal;
                        pp.maxOptimalGrowthTemperature = o.MaxOptimal;
                        pp.maxGrowthTemperature = o.MaxGrowth;
                    }
                    applied.Remove(p);
                }
            }
            int days = ApplyGrowDays(s);
            Log.Message("[Evergrowth " + HardyPlantsMod.Version + "] " + changed + " plants made hardier" +
                (days > 0 ? ", " + days + " grow times set" : "") + ".");
        }

        // 1.4.0: grow days for crops (sowable, not trees) and trees (no stumps).
        // Only defs that still have the value this mod gave them are put back,
        // so other mods that change grow days keep their changes.
        private static readonly Dictionary<ThingDef, float> originalDays = new Dictionary<ThingDef, float>();
        private static readonly Dictionary<ThingDef, float> appliedDays = new Dictionary<ThingDef, float>();

        private static int ApplyGrowDays(HardyPlantsSettings s)
        {
            int set = 0;
            foreach (ThingDef p in AllPlants())
            {
                PlantProperties pp = p.plant;
                if (pp == null || pp.isStump)
                    continue;
                bool tree = pp.IsTree;
                bool crop = !tree && pp.Sowable;
                float wanted = -1f;
                if (tree && s.treeDaysOn) wanted = s.treeDays;
                else if (crop && s.cropDaysOn) wanted = s.cropDays;

                float mine;
                bool stillMine = appliedDays.TryGetValue(p, out mine) && Mathf.Approximately(mine, pp.growDays);
                if (wanted > 0f)
                {
                    if (!stillMine)
                        originalDays[p] = pp.growDays;
                    pp.growDays = wanted;
                    appliedDays[p] = wanted;
                    set++;
                }
                else if (appliedDays.ContainsKey(p))
                {
                    float o;
                    if (stillMine && originalDays.TryGetValue(p, out o))
                        pp.growDays = o;
                    appliedDays.Remove(p);
                }
            }
            return set;
        }
    }

    // ------------------------------------------------------------------
    // Frost tolerance: a hardy plant goes leafless (and dies, if it dies when
    // leafless) only at a lower temperature.
    // ------------------------------------------------------------------
    // ------------------------------------------------------------------
    // 1.3.0: growth speed – every plant (wild, trees, crops), every map, live.
    // A plant that does not grow right now (0 %) stays at 0 %.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(Plant), "GrowthRate", MethodType.Getter)]
    public static class Patch_GrowthRate
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s != null && s.fasterGrowth && __result > 0f)
            {
                switch (Kind.Of(__instance.def))
                {
                    case 1: __result *= s.treeSpeed; break;
                    case 2: __result *= s.cropSpeed; break;
                    default: __result *= s.wildSpeed; break;
                }
            }
        }
    }

    // Growing day and night: plants do not rest at night ...
    [HarmonyPatch(typeof(Plant), "Resting", MethodType.Getter)]
    public static class Patch_Resting
    {
        public static void Postfix(ref bool __result)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s != null && s.growAllDay)
                __result = false;
        }
    }

    // ... and plants under the open sky are not stopped by darkness. Plants
    // under a roof still need lamps, so indoor farms keep working as before.
    [HarmonyPatch(typeof(Plant), "GrowthRateFactor_Light", MethodType.Getter)]
    public static class Patch_GrowthRateFactor_Light
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.growAllDay || __result >= 1f)
                return;
            Map map = __instance.Map;
            if (map != null && !__instance.Position.Roofed(map))
                __result = 1f;
        }
    }

    // 1.5.1: after a language change the game reloads all Defs; apply the
    // settings again to the new Defs once that is finished.
    [HarmonyPatch(typeof(PlayDataLoader), "LoadAllPlayData")]
    public static class Patch_LoadAllPlayData
    {
        public static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(HardyPlantsCore.Reload);
        }
    }

    [HarmonyPatch(typeof(Plant), "LeaflessTemperatureThresh", MethodType.Getter)]
    public static class Patch_LeaflessTemperatureThresh
    {
        public static void Postfix(Plant __instance, ref float __result)
        {
            if (HardyPlantsCore.IsHardy(__instance.def))
                __result -= HardyPlantsMod.Settings.frostTolerance;
        }
    }

    // 1.6.0 ----------------------------------------------------------------
    // 0 = wild plant, 1 = tree, 2 = crop (can be sown, not a tree)
    public static class Kind
    {
        private static readonly Dictionary<ThingDef, int> cache = new Dictionary<ThingDef, int>();

        public static int Of(ThingDef def)
        {
            int k;
            if (def == null || def.plant == null)
                return 0;
            if (cache.TryGetValue(def, out k))
                return k;
            k = def.plant.IsTree ? 1 : def.plant.Sowable ? 2 : 0;
            cache[def] = k;
            return k;
        }
    }

    // harvest yield: crops and wild plants / wood from trees
    [HarmonyPatch(typeof(Plant), nameof(Plant.YieldNow))]
    public static class Patch_YieldNow
    {
        public static void Postfix(Plant __instance, ref int __result)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.yieldOn || __result <= 0)
                return;
            float f = Kind.Of(__instance.def) == 1 ? s.woodYield : s.cropYield;
            if (!Mathf.Approximately(f, 1f))
                __result = Mathf.Max(1, GenMath.RoundRandom(__result * f));
        }
    }

    // wild plants grow back faster (or slower) after being cut, eaten or burnt
    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.WildPlantSpawnerTick))]
    public static class Patch_WildPlantSpawnerTick
    {
        private static readonly Action<WildPlantSpawner> inner =
            AccessTools.MethodDelegate<Action<WildPlantSpawner>>(AccessTools.Method(typeof(WildPlantSpawner), "WildPlantSpawnerTickInternal"));
        private static readonly Dictionary<WildPlantSpawner, float> acc = new Dictionary<WildPlantSpawner, float>();

        public static bool Prefix(WildPlantSpawner __instance)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.regrowOn || inner == null || Mathf.Approximately(s.regrowSpeed, 1f)
                || DebugSettings.fastEcology || DebugSettings.fastEcologyRegrowRateOnly)
                return true;
            float a;
            acc.TryGetValue(__instance, out a);
            a += s.regrowSpeed;
            int n = 0;
            while (a >= 1f && n < 20)
            {
                a -= 1f;
                n++;
                inner(__instance);
            }
            if (acc.Count > 64)
                acc.Clear();
            acc[__instance] = a;
            return false;
        }
    }

    // 1.7.1: with regrowth on, wild plants also come back under snow wherever it is warm enough for
    // them to grow (the minimum growth temperature of this mod) – before, nothing spawned all winter
    [HarmonyPatch(typeof(WildPlantSpawner), nameof(WildPlantSpawner.CheckSpawnWildPlantAt))]
    public static class Patch_WildSpawnUnderSnow
    {
        [ThreadStatic] public static Map spawning;

        public static void Prefix(WildPlantSpawner __instance, ref Map __state)
        {
            __state = spawning;
            spawning = Traverse.Create(__instance).Field("map").GetValue<Map>();
        }

        public static void Finalizer(Map __state)
        {
            spawning = __state;
        }
    }

    [HarmonyPatch(typeof(PlantUtility), nameof(PlantUtility.SnowAllowsPlanting))]
    public static class Patch_SnowAllowsPlanting
    {
        public static void Postfix(IntVec3 c, Map map, ref bool __result)
        {
            if (__result || Patch_WildSpawnUnderSnow.spawning == null || Patch_WildSpawnUnderSnow.spawning != map)
                return;
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.regrowOn)
                return;
            try
            {
                if (c.GetTemperature(map) >= s.minGrowth)
                    __result = true;
            }
            catch
            {
            }
        }
    }

    // poor soil does not slow plants down (rich soil still helps)
    [HarmonyPatch(typeof(PlantUtility), nameof(PlantUtility.GrowthRateFactorFor_Fertility))]
    public static class Patch_Fertility
    {
        public static void Postfix(float fertilityAtCell, ref float __result)
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s != null && s.ignoreSoil && fertilityAtCell > 0f && __result < 1f)
                __result = 1f;
        }
    }

    // no crop blight
    [HarmonyPatch(typeof(IncidentWorker_CropBlight), "CanFireNowSub")]
    public static class Patch_CropBlightIncident
    {
        public static void Postfix(ref bool __result)
        {
            if (HardyPlantsMod.Settings != null && HardyPlantsMod.Settings.noBlight)
                __result = false;
        }
    }

    [HarmonyPatch(typeof(Plant), nameof(Plant.BlightableNow), MethodType.Getter)]
    public static class Patch_BlightableNow
    {
        public static void Postfix(ref bool __result)
        {
            if (HardyPlantsMod.Settings != null && HardyPlantsMod.Settings.noBlight)
                __result = false;
        }
    }
}
