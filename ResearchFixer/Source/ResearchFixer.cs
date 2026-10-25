using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ResearchFixer
{
    public class RFSettings : ModSettings
    {
        public bool recommended = true;
        // nothing changes until the player confirms the list once
        public bool applied = false;
        public bool fixMisplaced = true;
        public bool researchButton = true;
        // 0.2.0: NEW mark on things unlocked by research (off by default, outside the recommended lock)
        public bool markNew = false;
        public List<string> excluded = new List<string>();
        public int settingsVersion = 1;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref applied, "applied", false);
            Scribe_Values.Look(ref fixMisplaced, "fixMisplaced", true);
            Scribe_Values.Look(ref researchButton, "researchButton", true);
            Scribe_Values.Look(ref markNew, "markNew", false);
            Scribe_Collections.Look(ref excluded, "excluded", LookMode.Value);
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 1);
            if (excluded == null) excluded = new List<string>();
        }
    }

    public class RFMod : Mod
    {
        public const string Version = "0.3.3";
        public static RFSettings Settings;
        public static RFMod Instance;

        public RFMod(ModContentPack content) : base(content)
        {
            Instance = this;
            Settings = GetSettings<RFSettings>();
            MNews.Register(content, RFMod.Version);   // what's new + Bolek98 overview
            MUI.Init(new RFSettings(), new[] { "fixMisplaced", "researchButton" });
            if (Settings.recommended) MUI.ApplyAll(Settings);
            var h = new Harmony("misak.researchfixer");
            // one failing patch must not stop the others
            foreach (Type t in AccessTools.GetTypesFromAssembly(typeof(RFMod).Assembly))
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { h.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { Log.Error("[Research Fixer] patch " + t.Name + " failed: " + e.Message); }
            }
            MUI.PatchWindow(h, typeof(RFMod));
            // runs after every mod's static constructors – and again when the game reloads all defs (language change)
            try
            {
                h.Patch(AccessTools.Method(typeof(StaticConstructorOnStartupUtility), nameof(StaticConstructorOnStartupUtility.CallAll)),
                    postfix: new HarmonyMethod(typeof(Fixer), nameof(Fixer.Run)));
            }
            catch (Exception e) { Log.Error("[Research Fixer] startup hook failed: " + e); }
        }

        public override string SettingsCategory() => "Research Fixer";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            MUI.Version(inRect, "v" + Version + " · Bolek98");
            var bullets = new List<string> { "RF_BulletList".Translate(), "RF_BulletMisplaced".Translate(), "RF_BulletButton".Translate(), "RF_BulletSafe".Translate() };
            float y = inRect.y;
            y += MUI.RecommendedCard(new Rect(inRect.x, y, inRect.width, 0f), Settings, Settings.recommended,
                on => { Settings.recommended = on; if (on) MUI.ApplyAll(Settings); }, bullets) + 10f;
            if (Settings.recommended) y += MUI.LockNote(new Rect(inRect.x, y, inRect.width, 0f)) + 10f;
            Rect body = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 50f - 96f);
            MUI.BeginLock(Settings.recommended);
            var l = new Listing_Standard();
            l.Begin(body);
            MUI.Section(l, "RF_SecWhat".Translate());
            MUI.Check(l, "RF_SetMisplaced".Translate(), "RF_SetMisplacedTip".Translate(), ref Settings.fixMisplaced, Settings, "fixMisplaced");
            MUI.Check(l, "RF_SetButton".Translate(), "RF_SetButtonTip".Translate(), ref Settings.researchButton, Settings, "researchButton");
            l.End();
            MUI.EndLock(body);
            // 0.2.0: a free choice, not part of the recommended settings
            var l2 = new Listing_Standard();
            l2.Begin(new Rect(inRect.x, inRect.yMax - 50f - 92f, inRect.width, 90f));
            MUI.Section(l2, "RF_SecExtra".Translate());
            bool mark = Settings.markNew;
            l2.CheckboxLabeled("RF_SetMarkNew".Translate(), ref mark, "RF_SetMarkNewTip".Translate());
            if (mark != Settings.markNew)
            {
                Settings.markNew = mark;
                if (!mark) RF_NewThings.ClearAll();
            }
            l2.End();
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 40f, 300f, 36f), "RF_Open".Translate()))
                Find.WindowStack.Add(new Dialog_ResearchFixer());
        }
    }

    // ---------------- what was found ----------------
    public enum Cat { Weapon, Apparel, Building }

    public class NewGroup
    {
        public string key, modName, packageId;
        public Cat cat;
        public TechLevel tech;
        public ResearchProjectDef anchor;
        public List<ThingDef> things = new List<ThingDef>();
        public ResearchProjectDef created;
    }

    public class Misplaced
    {
        public string key;
        public ThingDef thing;
        public TechLevel research;     // the highest tech level of its current research
        public string researchLabel;
        public ResearchProjectDef anchor;
        public Cat cat;
    }

    public static class Fixer
    {
        public static readonly List<NewGroup> Groups = new List<NewGroup>();
        public static readonly List<Misplaced> Wrong = new List<Misplaced>();
        public static bool AppliedThisRun;
        public static int AppliedCount;
        public static ResearchTabDef Tab;

        private static readonly AccessTools.FieldRef<ResearchProjectDef, float> fx = AccessTools.FieldRefAccess<ResearchProjectDef, float>("x");
        private static readonly AccessTools.FieldRef<ResearchProjectDef, float> fy = AccessTools.FieldRefAccess<ResearchProjectDef, float>("y");

        public static void Run()
        {
            YartButton.TryPatch();
            try
            {
                Tab = null;
                AppliedCount = 0;
                AppliedThisRun = false;
                Analyze();
                Log.Message("[Research Fixer] v" + RFMod.Version + ": " + Groups.Sum(g => g.things.Count) + " things without research in " + Groups.Count + " groups, " + Wrong.Count + " misplaced.");
                if (RFMod.Settings != null && RFMod.Settings.applied)
                    Apply(RFMod.Settings.excluded);
            }
            catch (Exception e)
            {
                Log.Error("[Research Fixer] " + e);
            }
        }

        // ---------- analysis ----------
        private static Dictionary<ThingDef, List<RecipeDef>> recipesFor;

        private static void Analyze()
        {
            Groups.Clear();
            Wrong.Clear();
            recipesFor = new Dictionary<ThingDef, List<RecipeDef>>();
            foreach (RecipeDef r in DefDatabase<RecipeDef>.AllDefsListForReading)
            {
                if (r.products == null) continue;
                foreach (ThingDefCountClass p in r.products)
                {
                    if (p?.thingDef == null) continue;
                    List<RecipeDef> l;
                    if (!recipesFor.TryGetValue(p.thingDef, out l)) recipesFor[p.thingDef] = l = new List<RecipeDef>();
                    l.Add(r);
                }
            }
            var byKey = new Dictionary<string, NewGroup>();
            foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                try { Check(d, byKey); }
                catch (Exception e) { Log.Warning("[Research Fixer] skipped " + d.defName + ": " + e.Message); }
            }
            Groups.AddRange(byKey.Values.Where(g => g.anchor != null).OrderBy(g => g.tech).ThenBy(g => g.modName).ThenBy(g => g.cat));
            Wrong.Sort((a, b) => string.Compare(a.thing.label, b.thing.label, StringComparison.OrdinalIgnoreCase));
        }

        private static bool CatOf(ThingDef d, out Cat cat)
        {
            cat = Cat.Weapon;
            if (d.IsApparel) { cat = Cat.Apparel; return true; }
            if (d.equipmentType == EquipmentType.Primary && (d.IsRangedWeapon || d.IsMeleeWeapon)) { cat = Cat.Weapon; return true; }
            if (d.category == ThingCategory.Building && d.designationCategory != null && d.BuildableByPlayer)
            {
                cat = Cat.Building;
                return true;
            }
            return false;
        }

        // industrial buildings only for these menus (power, production, security …); spacer and higher everything
        private static readonly HashSet<string> IndustrialBuildMenus = new HashSet<string> { "Power", "Production", "Security", "Temperature", "Biotech", "Ship" };

        private static void Check(ThingDef d, Dictionary<string, NewGroup> byKey)
        {
            if (d.modContentPack == null || d.modContentPack.IsOfficialMod || d.modContentPack.PackageId == "misak.researchfixer") return;
            if (d.techLevel < TechLevel.Industrial || d.techLevel == TechLevel.Undefined) return;
            if (d.IsBlueprint || d.IsFrame || d.isUnfinishedThing || d.IsCorpse) return;
            Cat cat;
            if (!CatOf(d, out cat)) return;
            if (cat == Cat.Building && d.techLevel == TechLevel.Industrial && !IndustrialBuildMenus.Contains(d.designationCategory.defName)) return;
            // clothing (racial outfits, jeans, spacer suits …) stays free – only real armor
            if (cat == Cat.Apparel && Armor(d) < 0.2f) return;

            // the easiest way to get it: highest research tech level on that way (-1 = no research at all)
            int easiest;
            bool craftable;
            Path(d, cat, out easiest, out craftable);
            if (!craftable) return;
            if (easiest < 0)
            {
                string pkg = d.modContentPack.PackageId;
                string key = "new|" + pkg + "|" + cat + "|" + d.techLevel;
                NewGroup g;
                if (!byKey.TryGetValue(key, out g))
                    byKey[key] = g = new NewGroup { key = key, packageId = pkg, modName = d.modContentPack.Name, cat = cat, tech = d.techLevel, anchor = Anchor(cat, d.techLevel, d) };
                g.things.Add(d);
                return;
            }
            // misplaced: the research is two or more eras below the thing
            if ((int)d.techLevel - easiest >= 2)
            {
                ResearchProjectDef a = Anchor(cat, d.techLevel, d);
                if (a == null) return;
                Wrong.Add(new Misplaced { key = "fix|" + d.defName, thing = d, research = (TechLevel)easiest, anchor = a, cat = cat, researchLabel = EasiestLabel(d, cat) });
            }
        }

        private static float Armor(ThingDef d)
        {
            try
            {
                ThingDef stuff = d.MadeFromStuff ? GenStuff.DefaultStuffFor(d) : null;
                return Mathf.Max(d.GetStatValueAbstract(StatDefOf.ArmorRating_Sharp, stuff), d.GetStatValueAbstract(StatDefOf.ArmorRating_Blunt, stuff));
            }
            catch { return 0f; }
        }

        private static IEnumerable<ResearchProjectDef> RecipeResearch(RecipeDef r)
        {
            if (r.researchPrerequisite != null) yield return r.researchPrerequisite;
            if (r.researchPrerequisites != null)
                foreach (ResearchProjectDef p in r.researchPrerequisites) if (p != null) yield return p;
        }

        // for a recipe: research of the recipe plus the easiest bench; -1 = free; int.MaxValue = unusable
        // research without an era (Anomaly knowledge …) counts as a proper lock
        private const int SpecialLock = 99;

        private static int Level(ResearchProjectDef p)
        {
            if (p.techLevel == TechLevel.Undefined || p.knowledgeCategory != null) return SpecialLock;
            return (int)p.techLevel;
        }

        private static int RecipeLevel(RecipeDef r)
        {
            int own = RecipeResearch(r).Select(Level).DefaultIfEmpty(-1).Max();
            int bench = int.MaxValue;
            foreach (ThingDef u in r.AllRecipeUsers)
            {
                if (u == null) continue;
                int b = u.researchPrerequisites == null || u.researchPrerequisites.Count == 0 ? -1 : u.researchPrerequisites.Max(Level);
                if (b < bench) bench = b;
            }
            if (bench == int.MaxValue) return int.MaxValue;
            return Math.Max(own, bench);
        }

        private static void Path(ThingDef d, Cat cat, out int easiest, out bool craftable)
        {
            if (cat == Cat.Building)
            {
                craftable = true;
                easiest = d.researchPrerequisites == null || d.researchPrerequisites.Count == 0 ? -1 : d.researchPrerequisites.Max(Level);
                return;
            }
            easiest = int.MaxValue;
            craftable = false;
            List<RecipeDef> rs;
            if (!recipesFor.TryGetValue(d, out rs)) return;
            foreach (RecipeDef r in rs)
            {
                int lv = RecipeLevel(r);
                if (lv == int.MaxValue) continue;
                craftable = true;
                if (lv < easiest) easiest = lv;
            }
        }

        private static string EasiestLabel(ThingDef d, Cat cat)
        {
            var all = new List<ResearchProjectDef>();
            if (cat == Cat.Building && d.researchPrerequisites != null) all.AddRange(d.researchPrerequisites);
            List<RecipeDef> rs;
            if (cat != Cat.Building && recipesFor.TryGetValue(d, out rs))
                foreach (RecipeDef r in rs)
                {
                    all.AddRange(RecipeResearch(r));
                    foreach (ThingDef u in r.AllRecipeUsers)
                        if (u?.researchPrerequisites != null) all.AddRange(u.researchPrerequisites);
                }
            return string.Join(", ", all.Where(p => p != null).Distinct().Select(p => p.LabelCap.ToString()).ToArray());
        }

        // the vanilla research a fix hangs on
        private static ResearchProjectDef Anchor(Cat cat, TechLevel tech, ThingDef d)
        {
            string[] names;
            bool spacer = tech >= TechLevel.Spacer;
            if (cat == Cat.Weapon) names = spacer ? new[] { "ChargedShot", "Gunsmithing" } : new[] { "Gunsmithing" };
            else if (cat == Cat.Apparel) names = spacer ? new[] { "PoweredArmor", "FlakArmor", "Fabrication" } : new[] { "FlakArmor", "ComplexClothing" };
            else if (spacer) names = new[] { "AdvancedFabrication", "MicroelectronicsBasics", "Fabrication" };
            else if (d.designationCategory != null && d.designationCategory.defName == "Security") names = new[] { "GunTurrets", "Gunsmithing" };
            else names = new[] { "Electricity" };
            foreach (string n in names)
            {
                ResearchProjectDef p = DefDatabase<ResearchProjectDef>.GetNamedSilentFail(n);
                if (p != null) return p;
            }
            return null;
        }

        // ---------- apply ----------
        private static void Apply(List<string> excluded)
        {
            var skip = new HashSet<string>(excluded ?? new List<string>());
            var touched = new HashSet<ResearchProjectDef>();
            var groups = Groups.Where(g => !skip.Contains(g.key)).ToList();
            if (groups.Count > 0)
            {
                Tab = new ResearchTabDef
                {
                    defName = "RF_ModResearch",
                    label = "RF_TabLabel".Translate(),
                    generalTitle = "RF_TabTitle".Translate(),
                    generalDescription = "RF_TabDesc".Translate()
                };
                Tab.modContentPack = RFMod.Instance?.Content;
                Give(Tab);
                DefDatabase<ResearchTabDef>.Add(Tab);
            }
            // grid: one column per 9 projects, eras left to right
            int col = 0, row = 0;
            TechLevel lastTech = TechLevel.Undefined;
            foreach (NewGroup g in groups)
            {
                if (lastTech != TechLevel.Undefined && g.tech != lastTech && row > 0) { col++; row = 0; }
                lastTech = g.tech;
                if (row >= 9) { col++; row = 0; }
                var p = new ResearchProjectDef
                {
                    defName = Safe("RF_" + g.packageId + "_" + g.cat + "_" + g.tech),
                    label = "RF_ProjectLabel".Translate(g.modName, CatLabel(g.cat), g.tech.ToStringHuman()),
                    description = "RF_ProjectDesc".Translate(g.modName, string.Join(", ", g.things.Select(t => t.label).Distinct().ToArray())),
                    baseCost = Cost(g.tech) * (1f + 0.05f * Mathf.Min(g.things.Count, 20)),
                    techLevel = g.tech,
                    prerequisites = new List<ResearchProjectDef> { g.anchor },
                    tab = Tab,
                    researchViewX = col * 1.0f,
                    researchViewY = row * 0.7f
                };
                p.modContentPack = RFMod.Instance?.Content;
                fx(p) = p.researchViewX;
                fy(p) = p.researchViewY;
                if (DefDatabase<ResearchProjectDef>.GetNamedSilentFail(p.defName) != null) continue;
                Give(p);
                DefDatabase<ResearchProjectDef>.Add(p);
                g.created = p;
                touched.Add(p);
                touched.Add(g.anchor);
                var allowed = new HashSet<ThingDef>(g.things);
                foreach (ThingDef t in g.things) Require(t, g.cat, p, allowed);
                row++;
            }
            if (RFMod.Settings.fixMisplaced)
                foreach (Misplaced m in Wrong)
                {
                    if (skip.Contains(m.key)) continue;
                    Require(m.thing, m.cat, m.anchor, new HashSet<ThingDef> { m.thing });
                    touched.Add(m.anchor);
                    AppliedCount++;
                }
            foreach (ResearchProjectDef p in touched) Traverse.Create(p).Field("cachedUnlockedDefs").SetValue(null);
            AppliedCount += groups.Sum(g => g.created == null ? 0 : g.things.Count);
            AppliedThisRun = true;
            Log.Message("[Research Fixer] applied: " + groups.Count(g => g.created != null) + " new research projects, " + AppliedCount + " things now need research.");
        }

        private static void Require(ThingDef t, Cat cat, ResearchProjectDef p, HashSet<ThingDef> allowed)
        {
            if (cat == Cat.Building)
            {
                if (t.researchPrerequisites == null) t.researchPrerequisites = new List<ResearchProjectDef>();
                if (!t.researchPrerequisites.Contains(p)) t.researchPrerequisites.Add(p);
                return;
            }
            List<RecipeDef> rs;
            if (!recipesFor.TryGetValue(t, out rs)) return;
            foreach (RecipeDef r in rs)
            {
                // a recipe that also makes something else is left alone (it would lock unrelated things)
                if (r.products.Any(x => x?.thingDef != null && !allowed.Contains(x.thingDef)))
                {
                    Log.Message("[Research Fixer] recipe " + r.defName + " makes other things too – left unchanged.");
                    continue;
                }
                if (r.researchPrerequisites == null) r.researchPrerequisites = new List<ResearchProjectDef>();
                if (!r.researchPrerequisites.Contains(p)) r.researchPrerequisites.Add(p);
            }
        }

        private static float Cost(TechLevel t)
        {
            if (t <= TechLevel.Industrial) return 1200f;
            if (t == TechLevel.Spacer) return 3000f;
            return 5000f;
        }

        public static string CatLabel(Cat c)
        {
            return c == Cat.Weapon ? "RF_CatWeapon".Translate() : (c == Cat.Apparel ? "RF_CatApparel".Translate() : "RF_CatBuilding".Translate());
        }

        private static string Safe(string s)
        {
            var sb = new System.Text.StringBuilder();
            foreach (char c in s) sb.Append(char.IsLetterOrDigit(c) || c == '_' ? c : '_');
            return sb.ToString();
        }

        // the same as the game does for generated defs
        private static void Give<T>(T d) where T : Def
        {
            var taken = new HashSet<ushort>(DefDatabase<T>.AllDefsListForReading.Select(x => x.shortHash));
            ushort h = (ushort)((uint)GenText.StableStringHash(d.defName) % 65535u);
            while (h == 0 || taken.Contains(h)) h++;
            d.shortHash = h;
            d.generated = true;
            d.ResolveDefNameHash();
            d.PostLoad();
        }
    }

    // a project that no longer exists (fix turned off, mod removed) is not an error in old saves
    [HarmonyPatch(typeof(BackCompatibility), nameof(BackCompatibility.WasDefRemoved))]
    public static class Patch_WasDefRemoved
    {
        public static void Postfix(string defName, Type type, ref bool __result)
        {
            if (!__result && type == typeof(ResearchProjectDef) && defName != null && defName.StartsWith("RF_"))
                __result = true;
        }
    }

    // ---------------- button in the research window ----------------
    [HarmonyPatch(typeof(MainTabWindow_Research), nameof(MainTabWindow_Research.DoWindowContents))]
    public static class Patch_ResearchButton
    {
        public static void Postfix(Rect inRect)
        {
            try
            {
                if (RFMod.Settings == null || !RFMod.Settings.researchButton) return;
                // top right of the left panel – free in vanilla and with research UI mods (tabs, queue …)
                float leftW = Mathf.Max(270f, inRect.width * 0.22f);
                Rect b = new Rect(leftW - 150f, 2f, 146f, 26f);
                if (Widgets.ButtonText(b, "Research Fixer"))
                    Find.WindowStack.Add(new Dialog_ResearchFixer());
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Research Fixer] button: " + e.Message, 0x7f31001);
            }
        }
    }

    // ---------------- window ----------------
    public class Dialog_ResearchFixer : Window
    {
        private static readonly Color Bg = new Color32(24, 27, 31, 255);
        private static readonly Color Line = new Color32(44, 50, 56, 255);
        private static readonly Color Err = new Color32(224, 87, 74, 255);
        private const float Pad = 14f;

        private readonly HashSet<string> skip;
        private Vector2 scroll;
        private readonly bool[] collapsed = new bool[2];
        private readonly Dictionary<object, float> heights = new Dictionary<object, float>();
        private float heightsW;

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1300f, UI.screenWidth - 40f), Mathf.Min(860f, UI.screenHeight - 40f));
        protected override float Margin => 0f;

        public Dialog_ResearchFixer()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            skip = new HashSet<string>(RFMod.Settings.excluded);
        }

        private int Selected => Fixer.Groups.Where(g => !skip.Contains(g.key)).Sum(g => g.things.Count)
            + (RFMod.Settings.fixMisplaced ? Fixer.Wrong.Count(w => !skip.Contains(w.key)) : 0);

        private static void Label(Rect r, string text, Color c, GameFont font = GameFont.Small, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = true)
        {
            GameFont f = Text.Font; TextAnchor a = Text.Anchor; bool w = Text.WordWrap; Color g = GUI.color;
            Text.Font = font; Text.Anchor = anchor; Text.WordWrap = wrap; GUI.color = c;
            Widgets.Label(r, text);
            GUI.color = g; Text.WordWrap = w; Text.Anchor = a; Text.Font = f;
        }

        private static bool Button(Rect r, string label, bool accent = false)
        {
            bool over = Mouse.IsOver(r);
            MUI.Fill(r, accent ? new Color32(43, 74, 42, 255) : (over ? new Color32(36, 42, 48, 255) : new Color32(34, 39, 44, 255)), 4f);
            MUI.Frame(r, accent ? MUI.Accent : (over ? MUI.Muted : MUI.Border), 1f, 4f);
            Label(r, label, accent ? (Color)new Color32(223, 242, 212, 255) : MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter, false);
            if (Widgets.ButtonInvisible(r)) { SoundDefOf.Click.PlayOneShotOnCamera(); return true; }
            return false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            MUI.Fill(inRect, Bg);
            Rect head = new Rect(inRect.x + Pad + 4f, inRect.y, inRect.width - 2 * Pad, 58f);
            Label(new Rect(head.x, head.y + 6f, 220f, 44f), "Research Fixer", MUI.Text1, GameFont.Medium);
            Label(new Rect(head.x + 190f, head.y + 8f, 400f, 40f), "RF_Subtitle".Translate(RFMod.Version), MUI.Muted, GameFont.Tiny);
            string status = RFMod.Settings.applied ? "RF_StatusOn".Translate(Fixer.AppliedThisRun ? Fixer.AppliedCount : 0).ToString() : "RF_StatusOff".Translate().ToString();
            Label(new Rect(head.xMax - 40f - 520f, head.y + 8f, 510f, 40f), status, RFMod.Settings.applied ? MUI.Accent : MUI.Warn, GameFont.Small, TextAnchor.MiddleRight);
            MUI.Fill(new Rect(inRect.x, head.yMax, inRect.width, 1f), Line);

            Rect foot = new Rect(inRect.x, inRect.yMax - 56f, inRect.width, 56f);
            MUI.Fill(new Rect(foot.x, foot.y, foot.width, 1f), Line);
            Rect body = new Rect(inRect.x, head.yMax + 1f, inRect.width, foot.y - head.yMax - 1f);
            float sideW = 360f;
            Rect left = new Rect(body.x, body.y, body.width - sideW, body.height).ContractedBy(Pad, 10f);
            Rect side = new Rect(body.xMax - sideW, body.y, sideW, body.height).ContractedBy(Pad, 10f);
            MUI.Fill(new Rect(body.xMax - sideW, body.y, 1f, body.height), Line);
            DrawList(left);
            DrawSide(side);

            Rect f = foot.ContractedBy(Pad, 10f);
            int nNew = Fixer.Groups.Sum(g => g.things.Count), nWrong = Fixer.Wrong.Count;
            Label(new Rect(f.x, f.y, 600f, f.height), "RF_FootCounts".Translate(nNew, Fixer.Groups.Count, nWrong), MUI.Muted, GameFont.Small, TextAnchor.MiddleLeft, false);
            string apply = "RF_Apply".Translate(Selected);
            float aw = Mathf.Max(220f, Text.CalcSize(apply).x + 40f);
            Rect ar = new Rect(f.xMax - aw, f.y, aw, f.height);
            if (Button(ar, apply, true)) Apply();
            if (RFMod.Settings.applied)
            {
                Rect off = new Rect(ar.x - 210f, f.y, 200f, f.height);
                if (Button(off, "RF_TurnOff".Translate()))
                {
                    RFMod.Settings.applied = false;
                    RFMod.Instance.WriteSettings();
                    AskRestart();
                }
            }
        }

        private void Apply()
        {
            RFMod.Settings.excluded = skip.ToList();
            RFMod.Settings.applied = true;
            RFMod.Instance.WriteSettings();
            AskRestart();
        }

        private void AskRestart()
        {
            // 0.3.0: during a game the game is saved first and loaded again right after the restart
            bool playing = Current.ProgramState == ProgramState.Playing && Current.Game != null;
            Find.WindowStack.Add(new Dialog_MessageBox((playing ? "RF_RestartTextResume" : "RF_RestartText").Translate(), "RF_RestartNow".Translate(), () => RestartResume.Go(), "RF_Later".Translate(), null, null, false));
        }

        private float H(object key, string text, float w)
        {
            if (w != heightsW) { heights.Clear(); heightsW = w; }
            float h;
            if (!heights.TryGetValue(key, out h))
            {
                Text.Font = GameFont.Small;
                heights[key] = h = Mathf.Max(64f, 12f + 24f + Text.CalcHeight(text, w) + 12f);
            }
            return h;
        }

        private string GroupText(NewGroup g)
        {
            string names = string.Join(", ", g.things.Take(12).Select(t => t.LabelCap.ToString()).ToArray()) + (g.things.Count > 12 ? ", …" : "");
            return names + "\n" + "RF_GroupAfter".Translate(g.anchor.LabelCap);
        }

        private string WrongText(Misplaced m)
        {
            return "RF_WrongText".Translate(m.researchLabel.NullOrEmpty() ? "?" : m.researchLabel, m.research.ToStringHuman(), m.anchor.LabelCap);
        }

        private void DrawList(Rect r)
        {
            float cardW = r.width - 16f;
            float textW = cardW - 28f - 150f;
            float total = 0f;
            total += 34f + (Fixer.Groups.Count == 0 ? 30f : (collapsed[0] ? 0f : Fixer.Groups.Sum(g => H(g, GroupText(g), textW) + 8f)));
            total += 34f + (Fixer.Wrong.Count == 0 ? 30f : (collapsed[1] ? 0f : Fixer.Wrong.Sum(m => H(m, WrongText(m), textW) + 8f)));
            Rect view = new Rect(0f, 0f, cardW, Mathf.Max(total + 10f, r.height - 2f));
            Widgets.BeginScrollView(r, ref scroll, view);
            float y = 0f;
            y = Header(y, cardW, 0, "RF_GrpNew".Translate(Fixer.Groups.Sum(g => g.things.Count)), "RF_GrpNewHint".Translate(), Err, Fixer.Groups.Count);
            if (!collapsed[0])
                foreach (NewGroup g in Fixer.Groups)
                {
                    string title = "RF_GroupTitle".Translate(g.modName, g.things.Count, Fixer.CatLabel(g.cat), g.tech.ToStringHuman());
                    string text = GroupText(g);
                    float h = H(g, text, textW);
                    Card(new Rect(0f, y, cardW, h), title, text, g.key, Err, "RF_Include".Translate());
                    y += h + 8f;
                }
            y = Header(y, cardW, 1, "RF_GrpWrong".Translate(Fixer.Wrong.Count), RFMod.Settings.fixMisplaced ? "RF_GrpWrongHint".Translate() : "RF_GrpWrongOff".Translate(), MUI.Warn, Fixer.Wrong.Count);
            if (!collapsed[1])
                foreach (Misplaced m in Fixer.Wrong)
                {
                    string title = "RF_WrongTitle".Translate(m.thing.LabelCap, m.thing.techLevel.ToStringHuman(), m.thing.modContentPack?.Name ?? "?");
                    string text = WrongText(m);
                    float h = H(m, text, textW);
                    Card(new Rect(0f, y, cardW, h), title, text, m.key, MUI.Warn, "RF_Fix".Translate());
                    y += h + 8f;
                }
            Widgets.EndScrollView();
        }

        private float Header(float y, float w, int idx, string title, string hint, Color c, int count)
        {
            Rect hr = new Rect(0f, y + 6f, w, 24f);
            MUI.Fill(new Rect(0f, hr.center.y - 4f, 8f, 8f), c, 4f);
            string t = title.ToUpperInvariant() + (count > 0 ? (collapsed[idx] ? "  ▸" : "  ▾") : "");
            Label(new Rect(16f, hr.y, w, 24f), t, c, GameFont.Small, TextAnchor.MiddleLeft, false);
            Label(new Rect(16f + Text.CalcSize(t).x + 16f, hr.y, w - 200f, 24f), hint, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            if (count > 0 && Widgets.ButtonInvisible(hr)) collapsed[idx] = !collapsed[idx];
            y += 34f;
            if (count == 0)
            {
                Label(new Rect(16f, y, w, 24f), "RF_None".Translate(), MUI.Accent);
                y += 30f;
            }
            return y;
        }

        private void Card(Rect r, string title, string text, string key, Color c, string onLabel)
        {
            bool on = !skip.Contains(key);
            MUI.Fill(r, MUI.CardBg, 4f);
            MUI.Frame(r, MUI.Border, 1f, 4f);
            MUI.Fill(new Rect(r.x, r.y, 3f, r.height), on ? c : MUI.Border);
            float textW = r.width - 28f - 150f;
            Label(new Rect(r.x + 14f, r.y + 10f, textW, 24f), title, on ? MUI.Text1 : MUI.Muted, GameFont.Small, TextAnchor.MiddleLeft, false);
            Label(new Rect(r.x + 14f, r.y + 36f, textW, r.height - 40f), text, new Color32(168, 174, 181, 255), GameFont.Small, TextAnchor.UpperLeft);
            Rect b = new Rect(r.xMax - 12f - 140f, r.y + 12f, 140f, 28f);
            if (Button(b, on ? "✓ " + onLabel : "RF_Skipped".Translate().ToString(), on))
            {
                if (on) skip.Add(key); else skip.Remove(key);
            }
        }

        private float SideCard(Rect r, string title, string text)
        {
            float th = Text.CalcHeight(text, r.width - 24f);
            Rect c = new Rect(r.x, r.y, r.width, 40f + th);
            MUI.Card(c);
            Label(new Rect(c.x + 12f, c.y + 8f, c.width - 24f, 24f), title, MUI.Text1);
            Label(new Rect(c.x + 12f, c.y + 32f, c.width - 24f, th + 2f), text, new Color32(168, 174, 181, 255), GameFont.Small, TextAnchor.UpperLeft);
            return c.height;
        }

        private void DrawSide(Rect r)
        {
            float y = r.y;
            Label(new Rect(r.x, y, r.width, 22f), "RF_How".Translate(), MUI.Muted, GameFont.Tiny);
            y += 26f;
            y += SideCard(new Rect(r.x, y, r.width, 0f), "RF_How1".Translate(), "RF_How1Text".Translate()) + 10f;
            y += SideCard(new Rect(r.x, y, r.width, 0f), "RF_How2".Translate(), "RF_How2Text".Translate()) + 10f;
            y += SideCard(new Rect(r.x, y, r.width, 0f), "RF_How3".Translate(), "RF_How3Text".Translate()) + 10f;
            SideCard(new Rect(r.x, y, r.width, 0f), "RF_How4".Translate(), "RF_How4Text".Translate());
        }
    }
}


namespace ResearchFixer
{
    // 0.3.0: "Restart now" during a game: the game is saved at once (own save file, your saves are not
    // touched), the game restarts, and the save is loaded again automatically – back where you were,
    // with the research fixes active.
    [StaticConstructorOnStartup]
    public static class RestartResume
    {
        private static string Marker => System.IO.Path.Combine(GenFilePaths.SaveDataFolderPath, "ResearchFixer_resume.txt");

        static RestartResume()
        {
            try
            {
                new Harmony("misak.researchfixer.resume").Patch(AccessTools.Method(typeof(UIRoot_Entry), nameof(UIRoot_Entry.Init)),
                    postfix: new HarmonyMethod(typeof(RestartResume), nameof(AfterMenu)));
            }
            catch (Exception e)
            {
                Log.Warning("[Research Fixer] resume after restart not available: " + e.Message);
            }
        }

        public static void Go()
        {
            if (Current.ProgramState != ProgramState.Playing || Current.Game == null)
            {
                GenCommandLine.Restart();
                return;
            }
            LongEventHandler.QueueLongEvent(() =>
            {
                try
                {
                    string name = GenText.SanitizeFilename((Faction.OfPlayer?.Name ?? "Colony") + " - Research Fixer");
                    GameDataSaveLoader.SaveGame(name);
                    System.IO.File.WriteAllText(Marker, name + "\n" + DateTime.Now.ToString("o"));
                    Log.Message("[Research Fixer] game saved as \"" + name + "\" – it loads again after the restart.");
                }
                catch (Exception e)
                {
                    Log.Error("[Research Fixer] save before restart failed: " + e);
                }
            }, "SavingLongEvent", false, null);
            LongEventHandler.ExecuteWhenFinished(() => GenCommandLine.Restart());
        }

        public static void AfterMenu()
        {
            try
            {
                if (!System.IO.File.Exists(Marker))
                    return;
                string[] lines = System.IO.File.ReadAllLines(Marker);
                System.IO.File.Delete(Marker);   // once only – a crash while loading does not loop
                if (lines.Length < 2 || !DateTime.TryParse(lines[1], null, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime when) || (DateTime.Now - when).TotalMinutes > 20)
                    return;
                string name = lines[0].Trim();
                if (name.Length == 0 || !System.IO.File.Exists(GenFilePaths.FilePathForSavedGame(name)))
                    return;
                Log.Message("[Research Fixer] loading \"" + name + "\" again after the restart.");
                GameDataSaveLoader.LoadGame(name);
            }
            catch (Exception e)
            {
                Log.Warning("[Research Fixer] resume after restart: " + e.Message);
            }
        }
    }
}
