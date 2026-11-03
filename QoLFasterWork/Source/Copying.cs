using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace QoLFW
{
    // ==================================================================
    // 1.1.0: one bill as plain data (defNames and values) – used for saved bill templates and
    // for settings that wait for a building to be built (pipette, auto rebuild).
    // ==================================================================
    public class BillData : IExposable
    {
        public string recipe;
        public bool suspended, paused, pauseWhenSatisfied, includeEquipped, includeTainted, limitToAllowedStuff;
        public float radius = 999f;
        public IntRange skill = new IntRange(0, 20);
        public string repeatMode, storeMode;
        public int repeatCount = 1, targetCount = 10, unpauseAt = 5;
        public FloatRange hp = FloatRange.ZeroToOne, filterHp = FloatRange.ZeroToOne;
        public QualityRange quality = QualityRange.All, filterQuality = QualityRange.All;
        public List<string> allowed = new List<string>();
        public List<string> specialOff = new List<string>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref recipe, "recipe");
            Scribe_Values.Look(ref suspended, "suspended");
            Scribe_Values.Look(ref paused, "paused");
            Scribe_Values.Look(ref pauseWhenSatisfied, "pauseWhenSatisfied");
            Scribe_Values.Look(ref includeEquipped, "includeEquipped");
            Scribe_Values.Look(ref includeTainted, "includeTainted");
            Scribe_Values.Look(ref limitToAllowedStuff, "limitToAllowedStuff");
            Scribe_Values.Look(ref radius, "radius", 999f);
            Scribe_Values.Look(ref skill, "skill", new IntRange(0, 20));
            Scribe_Values.Look(ref repeatMode, "repeatMode");
            Scribe_Values.Look(ref storeMode, "storeMode");
            Scribe_Values.Look(ref repeatCount, "repeatCount", 1);
            Scribe_Values.Look(ref targetCount, "targetCount", 10);
            Scribe_Values.Look(ref unpauseAt, "unpauseAt", 5);
            Scribe_Values.Look(ref hp, "hp", FloatRange.ZeroToOne);
            Scribe_Values.Look(ref filterHp, "filterHp", FloatRange.ZeroToOne);
            Scribe_Values.Look(ref quality, "quality", QualityRange.All);
            Scribe_Values.Look(ref filterQuality, "filterQuality", QualityRange.All);
            Scribe_Collections.Look(ref allowed, "allowed", LookMode.Value);
            Scribe_Collections.Look(ref specialOff, "specialOff", LookMode.Value);
            if (allowed == null) allowed = new List<string>();
            if (specialOff == null) specialOff = new List<string>();
        }

        public static BillData Of(Bill b)
        {
            if (b?.recipe == null || b is Bill_Medical) return null;
            var d = new BillData
            {
                recipe = b.recipe.defName,
                suspended = b.suspended,
                radius = b.ingredientSearchRadius,
                skill = b.allowedSkillRange
            };
            if (b.ingredientFilter != null)
            {
                d.allowed = b.ingredientFilter.AllowedThingDefs.Select(t => t.defName).ToList();
                foreach (SpecialThingFilterDef sf in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
                    if (!b.ingredientFilter.Allows(sf)) d.specialOff.Add(sf.defName);
                d.filterHp = b.ingredientFilter.AllowedHitPointsPercents;
                d.filterQuality = b.ingredientFilter.AllowedQualityLevels;
            }
            if (b is Bill_Production p)
            {
                d.repeatMode = p.repeatMode?.defName;
                d.repeatCount = p.repeatCount;
                d.targetCount = p.targetCount;
                d.pauseWhenSatisfied = p.pauseWhenSatisfied;
                d.unpauseAt = p.unpauseWhenYouHave;
                d.includeEquipped = p.includeEquipped;
                d.includeTainted = p.includeTainted;
                d.hp = p.hpRange;
                d.quality = p.qualityRange;
                d.limitToAllowedStuff = p.limitToAllowedStuff;
                d.paused = p.paused;
                try { d.storeMode = p.GetStoreMode()?.defName; } catch { }
            }
            return d;
        }

        // A new bill on the giver; null when that bench cannot do the recipe (or it is not known).
        public Bill MakeFor(IBillGiver giver)
        {
            RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(recipe ?? "");
            if (r == null || !Bills.CanDo(giver, r)) return null;
            Bill b = r.MakeNewBill();
            b.suspended = suspended;
            b.ingredientSearchRadius = radius;
            b.allowedSkillRange = skill;
            if (b.ingredientFilter != null)
            {
                b.ingredientFilter.SetDisallowAll();
                foreach (string n in allowed)
                {
                    ThingDef t = DefDatabase<ThingDef>.GetNamedSilentFail(n);
                    if (t != null && (r.fixedIngredientFilter == null || r.fixedIngredientFilter.Allows(t))) b.ingredientFilter.SetAllow(t, true);
                }
                foreach (SpecialThingFilterDef sf in DefDatabase<SpecialThingFilterDef>.AllDefsListForReading)
                    b.ingredientFilter.SetAllow(sf, !specialOff.Contains(sf.defName));
                b.ingredientFilter.AllowedHitPointsPercents = filterHp;
                b.ingredientFilter.AllowedQualityLevels = filterQuality;
            }
            if (b is Bill_Production p)
            {
                BillRepeatModeDef rm = DefDatabase<BillRepeatModeDef>.GetNamedSilentFail(repeatMode ?? "");
                if (rm != null) p.repeatMode = rm;
                p.repeatCount = repeatCount;
                p.targetCount = targetCount;
                p.pauseWhenSatisfied = pauseWhenSatisfied;
                p.unpauseWhenYouHave = unpauseAt;
                p.includeEquipped = includeEquipped;
                p.includeTainted = includeTainted;
                p.hpRange = hp;
                p.qualityRange = quality;
                p.limitToAllowedStuff = limitToAllowedStuff;
                p.paused = paused;
                BillStoreModeDef sm = DefDatabase<BillStoreModeDef>.GetNamedSilentFail(storeMode ?? "");
                // a chosen stockpile cannot travel between buildings or games: best stockpile instead
                if (sm != null && sm != BillStoreModeDefOf.SpecificStockpile) { try { p.SetStoreMode(sm); } catch { } }
            }
            return b;
        }
    }

    public class BillTemplate : IExposable
    {
        public string name;
        public List<BillData> bills = new List<BillData>();

        public void ExposeData()
        {
            Scribe_Values.Look(ref name, "name");
            Scribe_Collections.Look(ref bills, "bills", LookMode.Deep);
            if (bills == null) bills = new List<BillData>();
            bills.RemoveAll(b => b == null);
        }
    }

    // ==================================================================
    // 1.1.0: copy / paste all bills of a bench, and named templates usable in every game.
    // ==================================================================
    public static class Bills
    {
        private static List<Bill> clip;
        private static string clipFrom;
        private static List<BillTemplate> templates;

        private static string FilePath => Path.Combine(GenFilePaths.ConfigFolderPath, "QoLFasterWork_BillTemplates.xml");

        public static bool CanDo(IBillGiver giver, RecipeDef r)
        {
            Thing t = giver as Thing;
            if (t == null || r == null) return false;
            List<RecipeDef> all = t.def.AllRecipes;
            return all != null && all.Contains(r) && r.AvailableNow;
        }

        public static List<BillTemplate> Templates
        {
            get
            {
                if (templates == null) Load();
                return templates;
            }
        }

        private static void Load()
        {
            templates = new List<BillTemplate>();
            if (!File.Exists(FilePath)) return;
            try
            {
                Scribe.loader.InitLoading(FilePath);
                try { Scribe_Collections.Look(ref templates, "templates", LookMode.Deep); }
                finally { Scribe.loader.FinalizeLoading(); }
            }
            catch (Exception e)
            {
                Scribe.ForceStop();
                Log.Warning("[QoL Faster Work] bill templates could not be read: " + e.Message);
            }
            if (templates == null) templates = new List<BillTemplate>();
            templates.RemoveAll(t => t == null || t.name.NullOrEmpty());
        }

        private static void Save()
        {
            try
            {
                Scribe.saver.InitSaving(FilePath, "QoLBillTemplates");
                try { Scribe_Collections.Look(ref templates, "templates", LookMode.Deep); }
                finally { Scribe.saver.FinalizeSaving(); }
            }
            catch (Exception e)
            {
                Scribe.ForceStop();
                Log.Warning("[QoL Faster Work] bill templates could not be saved: " + e.Message);
            }
        }

        public static IEnumerable<Gizmo> GizmosFor(Building b)
        {
            if (!(b is IBillGiver giver) || giver.BillStack == null || b.Faction != Faction.OfPlayer) yield break;
            var sel = Find.Selector.SelectedObjectsListForReading;
            if (sel.Count > 0 && sel[0] != b) yield break;    // one set of buttons for the whole selection
            if (sel.Count <= 1)
            {
                yield return new Command_Action
                {
                    defaultLabel = "QFW_CopyBills".Translate(),
                    defaultDesc = "QFW_CopyBills_Desc".Translate(),
                    icon = Tex.Copy,
                    Order = 2300f,
                    action = () =>
                    {
                        clip = giver.BillStack.Bills.Where(x => !(x is Bill_Medical)).Select(x => x.Clone()).ToList();
                        clipFrom = b.LabelCap;
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                        Q.Msg("QFW_BillsCopied".Translate(clip.Count, clipFrom));
                    }
                };
            }
            var c = new Command_Action
            {
                defaultLabel = "QFW_PasteBills".Translate(),
                defaultDesc = "QFW_PasteBills_Desc".Translate(clipFrom ?? "-"),
                icon = Tex.Paste,
                Order = 2301f,
                action = () => PasteMenu()
            };
            if (clip == null || clip.Count == 0) c.Disable("QFW_BillsNothing".Translate());
            yield return c;
            yield return new Command_Action
            {
                defaultLabel = "QFW_BillTemplates".Translate(),
                defaultDesc = "QFW_BillTemplates_Desc".Translate(),
                icon = Tex.Copy,
                Order = 2302f,
                action = () => TemplateMenu(b, giver)
            };
        }

        private static List<IBillGiver> SelectedGivers() =>
            Find.Selector.SelectedObjectsListForReading.OfType<Building>().Where(x => x is IBillGiver && x.Faction == Faction.OfPlayer).Cast<IBillGiver>().ToList();

        private static void PasteMenu()
        {
            Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
            {
                new FloatMenuOption("QFW_PasteAppend".Translate(), () => Paste(false)),
                new FloatMenuOption("QFW_PasteReplace".Translate(), () => Paste(true)),
            }));
        }

        private static void Paste(bool replace)
        {
            int added = 0, skipped = 0;
            foreach (IBillGiver g in SelectedGivers())
            {
                Thing t = g as Thing;
                if (replace)
                    foreach (Bill old in g.BillStack.Bills.Where(x => !(x is Bill_Medical)).ToList()) g.BillStack.Delete(old);
                foreach (Bill src in clip)
                {
                    if (!CanDo(g, src.recipe)) { skipped++; continue; }
                    Bill n = src.Clone();
                    // a pawn or a storage group of another map would be invalid there
                    if (n.PawnRestriction != null && (n.PawnRestriction.Dead || n.PawnRestriction.MapHeld != t?.MapHeld)) n.SetAnyPawnRestriction();
                    if (n is Bill_Production p && p.GetStoreMode() == BillStoreModeDefOf.SpecificStockpile && GroupMap(p.GetSlotGroup()) != t?.MapHeld)
                        p.SetStoreMode(BillStoreModeDefOf.BestStockpile);
                    g.BillStack.AddBill(n);
                    added++;
                }
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Q.Msg("QFW_BillsPasted".Translate(added, skipped), null, added == 0);
        }

        private static Map GroupMap(ISlotGroup g)
        {
            if (g is SlotGroup sg) return sg.parent?.Map;
            if (g is StorageGroup st) return st.Map;
            return null;
        }

        private static void TemplateMenu(Building b, IBillGiver giver)
        {
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("QFW_TplSave".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_Name("QFW_TplName".Translate(), b.def.LabelCap, name =>
                    {
                        var t = new BillTemplate { name = name };
                        t.bills = giver.BillStack.Bills.Select(BillData.Of).Where(x => x != null).ToList();
                        Templates.RemoveAll(x => x.name == name);
                        Templates.Add(t);
                        Save();
                        Q.Msg("QFW_TplSaved".Translate(name, t.bills.Count));
                    })))
            };
            foreach (BillTemplate t in Templates.OrderBy(x => x.name))
            {
                BillTemplate tt = t;
                int can = t.bills.Count(x => { RecipeDef r = DefDatabase<RecipeDef>.GetNamedSilentFail(x.recipe ?? ""); return CanDo(giver, r); });
                opts.Add(new FloatMenuOption("QFW_TplApply".Translate(t.name, can, t.bills.Count), () => Apply(tt)));
            }
            if (Templates.Count > 0)
                opts.Add(new FloatMenuOption("QFW_TplDelete".Translate(), () =>
                    Find.WindowStack.Add(new FloatMenu(Templates.Select(t => new FloatMenuOption(t.name, () => { Templates.Remove(t); Save(); })).ToList()))));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private static void Apply(BillTemplate t)
        {
            int added = 0, skipped = 0;
            foreach (IBillGiver g in SelectedGivers())
                foreach (BillData d in t.bills)
                {
                    Bill n = d.MakeFor(g);
                    if (n == null) { skipped++; continue; }
                    g.BillStack.AddBill(n);
                    added++;
                }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Q.Msg("QFW_BillsPasted".Translate(added, skipped), null, added == 0);
        }
    }

    // A small "type a name" window.
    public class Dialog_Name : Window
    {
        private readonly string title;
        private string text;
        private readonly Action<string> done;
        private bool focused;

        public Dialog_Name(string title, string start, Action<string> done)
        {
            this.title = title; text = start; this.done = done;
            doCloseX = true; closeOnClickedOutside = true; absorbInputAroundWindow = true; forcePause = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 170f);

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 34f), title);
            Text.Font = GameFont.Small;
            GUI.SetNextControlName("QFWName");
            text = Widgets.TextField(new Rect(r.x, r.y + 44f, r.width, 30f), text ?? "");
            if (!focused) { GUI.FocusControl("QFWName"); focused = true; }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if ((Widgets.ButtonText(new Rect(r.xMax - 140f, r.yMax - 34f, 140f, 32f), "OK".Translate()) || enter) && !text.NullOrEmpty())
            {
                if (enter) Event.current.Use();
                done(text.Trim());
                Close();
            }
        }
    }

    [HarmonyPatch(typeof(Building), "GetGizmos")]
    public static class Patch_BuildingGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (QoLMod.S.billCopy && __instance is IBillGiver)
                foreach (Gizmo g in Bills.GizmosFor(__instance)) yield return g;
            if (QoLMod.S.storagePasteAll && __instance is IStoreSettingsParent sp && __instance.Faction == Faction.OfPlayer)
            {
                Gizmo g = StorageAll.Gizmo(__instance, sp);
                if (g != null) yield return g;
            }
        }
    }

    [HarmonyPatch(typeof(Zone_Stockpile), "GetGizmos")]
    public static class Patch_StockpileGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Zone_Stockpile __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!QoLMod.S.storagePasteAll) yield break;
            Gizmo x = StorageAll.Gizmo(__instance, __instance);
            if (x != null) yield return x;
        }
    }

    // ==================================================================
    // 1.1.0: storage settings of one storage → every storage of the same kind on the map.
    // (Copy / paste to the selected ones is in the game already.)
    // ==================================================================
    public static class StorageAll
    {
        public static Gizmo Gizmo(object owner, IStoreSettingsParent sp)
        {
            if (sp.GetStoreSettings() == null || Find.Selector.NumSelected > 1) return null;
            return new Command_Action
            {
                defaultLabel = "QFW_StorageAll".Translate(),
                defaultDesc = "QFW_StorageAll_Desc".Translate(),
                icon = Tex.Paste,
                Order = 2310f,
                action = () => Apply(owner, sp)
            };
        }

        private static void Apply(object owner, IStoreSettingsParent sp)
        {
            StorageSettings src = sp.GetStoreSettings();
            int n = 0;
            Map map = Find.CurrentMap;
            if (map == null) return;
            if (owner is Zone_Stockpile)
            {
                foreach (Zone z in map.zoneManager.AllZones)
                    if (z is Zone_Stockpile s && s != owner) { s.GetStoreSettings().CopyFrom(src); n++; }
            }
            else if (owner is Building b)
            {
                foreach (Building o in map.listerBuildings.allBuildingsColonist)
                    if (o != b && o.def == b.def && o is IStoreSettingsParent osp && osp.GetStoreSettings() != null) { osp.GetStoreSettings().CopyFrom(src); n++; }
            }
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Q.Msg("QFW_StorageAllDone".Translate(n));
        }
    }

    // ==================================================================
    // 1.1.0: copy / paste animal settings (master, follow, area, training, food, medical care).
    // ==================================================================
    public class AnimalSnapshot
    {
        public string name;
        public Pawn master;
        public bool followDrafted, followFieldwork;
        public Area area;
        public Map areaMap;
        public FoodPolicy food;
        public MedicalCareCategory med;
        public Dictionary<TrainableDef, bool> wanted = new Dictionary<TrainableDef, bool>();

        public static AnimalSnapshot Of(Pawn p)
        {
            var s = new AnimalSnapshot { name = p.LabelShort };
            if (p.playerSettings != null)
            {
                s.master = p.playerSettings.Master;
                s.followDrafted = p.playerSettings.followDrafted;
                s.followFieldwork = p.playerSettings.followFieldwork;
                s.area = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                s.areaMap = p.MapHeld;
                s.med = p.playerSettings.medCare;
            }
            try { s.food = p.foodRestriction?.CurrentFoodPolicy; } catch { }
            if (p.training != null)
                foreach (TrainableDef td in DefDatabase<TrainableDef>.AllDefsListForReading)
                    if (p.training.CanAssignToTrain(td).Accepted) s.wanted[td] = p.training.GetWanted(td);
            return s;
        }

        public void PasteTo(Pawn p)
        {
            if (p.playerSettings != null)
            {
                if (master == null || (!master.Dead && p.training != null && p.training.HasLearned(TrainableDefOf.Obedience)))
                    try { p.playerSettings.Master = master; } catch { }
                p.playerSettings.followDrafted = followDrafted;
                p.playerSettings.followFieldwork = followFieldwork;
                p.playerSettings.medCare = med;
                if (p.MapHeld == areaMap) p.playerSettings.AreaRestrictionInPawnCurrentMap = area;
            }
            try { if (food != null && p.foodRestriction != null && p.foodRestriction.Configurable) p.foodRestriction.CurrentFoodPolicy = food; } catch { }
            if (p.training != null)
                foreach (var kv in wanted)
                    if (p.training.CanAssignToTrain(kv.Key).Accepted && p.training.GetWanted(kv.Key) != kv.Value)
                        try { p.training.SetWantedRecursive(kv.Key, kv.Value); } catch { }
        }
    }

    [HarmonyPatch(typeof(Pawn), "GetGizmos")]
    public static class Patch_AnimalGizmos
    {
        public static AnimalSnapshot clip;

        private static bool IsMyAnimal(Pawn p) => p != null && p.RaceProps != null && p.RaceProps.Animal && p.Faction == Faction.OfPlayer;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!QoLMod.S.animalCopy || !IsMyAnimal(__instance)) yield break;
            List<Pawn> sel = Find.Selector.SelectedObjectsListForReading.OfType<Pawn>().Where(IsMyAnimal).ToList();
            if (sel.Count == 0 || sel[0] != __instance) yield break;
            if (sel.Count == 1)
                yield return new Command_Action
                {
                    defaultLabel = "QFW_CopyAnimal".Translate(),
                    defaultDesc = "QFW_CopyAnimal_Desc".Translate(),
                    icon = Tex.Copy,
                    Order = 2000f,
                    action = () =>
                    {
                        clip = AnimalSnapshot.Of(__instance);
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                        Q.Msg("QFW_Copied".Translate(__instance.LabelShort));
                    }
                };
            if (clip != null)
                yield return new Command_Action
                {
                    defaultLabel = "QFW_PasteAnimal".Translate(sel.Count),
                    defaultDesc = "QFW_PasteAnimal_Desc".Translate(clip.name),
                    icon = Tex.Paste,
                    Order = 2001f,
                    action = () =>
                    {
                        foreach (Pawn p in sel)
                            try { clip.PasteTo(p); } catch (Exception e) { Log.Warning("[QoL Faster Work] animal paste: " + e.Message); }
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                        Q.Msg("QFW_Pasted".Translate(clip.name, sel.Count));
                    }
                };
        }
    }

    // ==================================================================
    // 1.1.0: settings that wait for a building (pipette copy, auto rebuild) and the rebuild queue.
    // ==================================================================
    public class PendingSettings : IExposable
    {
        public IntVec3 cell;
        public ThingDef def;
        public int tick;
        public List<BillData> bills = new List<BillData>();
        public StorageSettings storage;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Values.Look(ref tick, "tick");
            Scribe_Collections.Look(ref bills, "bills", LookMode.Deep);
            Scribe_Deep.Look(ref storage, "storage");
            if (bills == null) bills = new List<BillData>();
        }

        public static PendingSettings From(Thing src, IntVec3 cell, ThingDef def)
        {
            var p = new PendingSettings { cell = cell, def = def, tick = Find.TickManager.TicksGame };
            if (src is IBillGiver g && g.BillStack != null)
                p.bills = g.BillStack.Bills.Select(BillData.Of).Where(x => x != null).ToList();
            if (src is IStoreSettingsParent sp && sp.GetStoreSettings() != null)
            {
                p.storage = new StorageSettings();
                p.storage.CopyFrom(sp.GetStoreSettings());
            }
            return p.bills.Count > 0 || p.storage != null ? p : null;
        }
    }

    public class RebuildEntry : IExposable
    {
        public IntVec3 cell;
        public Rot4 rot;
        public ThingDef def, stuff;
        public ThingStyleDef style;

        public void ExposeData()
        {
            Scribe_Values.Look(ref cell, "cell");
            Scribe_Values.Look(ref rot, "rot");
            Scribe_Defs.Look(ref def, "def");
            Scribe_Defs.Look(ref stuff, "stuff");
            Scribe_Defs.Look(ref style, "style");
        }
    }

    public class QoLMapComp : MapComponent
    {
        public List<PendingSettings> pending = new List<PendingSettings>();
        public List<RebuildEntry> rebuild = new List<RebuildEntry>();

        public QoLMapComp(Map map) : base(map) { }

        public static QoLMapComp Of(Map m) => m?.GetComponent<QoLMapComp>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref pending, "qfwPending", LookMode.Deep);
            Scribe_Collections.Look(ref rebuild, "qfwRebuild", LookMode.Deep);
            if (pending == null) pending = new List<PendingSettings>();
            if (rebuild == null) rebuild = new List<RebuildEntry>();
            pending.RemoveAll(p => p == null || p.def == null);
            rebuild.RemoveAll(r => r == null || r.def == null);
        }

        public void Add(PendingSettings p)
        {
            if (p == null) return;
            pending.RemoveAll(x => x.cell == p.cell && x.def == p.def);
            pending.Add(p);
        }

        public override void MapComponentTick()
        {
            int now = Find.TickManager.TicksGame;
            if (now % 250 != 0) return;
            try
            {
                if (rebuild.Count > 0 && !GenHostility.AnyHostileActiveThreatToPlayer(map))
                {
                    foreach (RebuildEntry e in rebuild.ToList())
                        AutoRebuild.Place(map, e);
                    rebuild.Clear();
                }
                if (now % 2500 == 0 && pending.Count > 0)
                    pending.RemoveAll(p => now - p.tick > GenDate.TicksPerDay * 5 || !Waiting(p));
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] map tick: " + e.Message, 81723410); }
        }

        // a blueprint or frame of that building is still on the cell
        private bool Waiting(PendingSettings p)
        {
            if (!p.cell.InBounds(map)) return false;
            foreach (Thing t in p.cell.GetThingList(map))
                if ((t is Blueprint_Build || t is Frame) && t.def.entityDefToBuild == p.def) return true;
            return rebuild.Any(r => r.cell == p.cell && r.def == p.def);
        }

        public void Built(Building b)
        {
            if (pending.Count == 0) return;
            PendingSettings p = pending.FirstOrDefault(x => x.def == b.def && b.OccupiedRect().Contains(x.cell));
            if (p == null) return;
            pending.Remove(p);
            int n = 0;
            if (b is IBillGiver g && g.BillStack != null)
                foreach (BillData d in p.bills)
                {
                    Bill bill = d.MakeFor(g);
                    if (bill != null) { g.BillStack.AddBill(bill); n++; }
                }
            if (p.storage != null && b is IStoreSettingsParent sp && sp.GetStoreSettings() != null)
                sp.GetStoreSettings().CopyFrom(p.storage);
            if (n > 0 || p.storage != null) Q.Msg("QFW_SettingsTaken".Translate(b.LabelCap), b);
        }
    }

    [HarmonyPatch(typeof(Building), "SpawnSetup")]
    public static class Patch_BuildingSpawn
    {
        public static void Postfix(Building __instance, Map map, bool respawningAfterLoad)
        {
            if (respawningAfterLoad || __instance.Faction != Faction.OfPlayer) return;
            try { QoLMapComp.Of(map)?.Built(__instance); }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] settings for new building: " + e.Message, 81723411); }
        }
    }

    // ==================================================================
    // 1.1.0: auto rebuild – a destroyed building of yours gets a blueprint on its spot.
    // The game's own auto rebuild (home area only, when it is switched on) goes first.
    // ==================================================================
    public static class AutoRebuild
    {
        public enum Kind { Walls, Furniture, Power, Production, Security, Other }

        public static Kind KindOf(ThingDef d)
        {
            if (d.thingClass != null && typeof(Building_Door).IsAssignableFrom(d.thingClass)) return Kind.Walls;
            if (d.passability == Traversability.Impassable && d.fillPercent >= 0.99f && d.size.x == 1 && d.size.z == 1 && !d.hasInteractionCell) return Kind.Walls;
            string cat = d.designationCategory?.defName ?? "";
            if (cat == "Security" || d.building?.IsTurret == true || (d.thingClass != null && typeof(Building_Trap).IsAssignableFrom(d.thingClass))) return Kind.Security;
            if (d.thingClass != null && typeof(IBillGiver).IsAssignableFrom(d.thingClass)) return Kind.Production;
            if (d.EverTransmitsPower || cat == "Power" || (d.comps != null && d.comps.Any(c => c?.compClass != null && (typeof(CompPower).IsAssignableFrom(c.compClass) || (c.compClass.Namespace ?? "").Contains("PipeSystem") || c.compClass.Name.Contains("Pipe")))))
                return Kind.Power;
            if (cat == "Furniture" || cat == "Joy") return Kind.Furniture;
            return Kind.Other;
        }

        public static bool KindOn(Kind k)
        {
            QSettings s = QoLMod.S;
            switch (k)
            {
                case Kind.Walls: return s.rbWalls;
                case Kind.Furniture: return s.rbFurniture;
                case Kind.Power: return s.rbPower;
                case Kind.Production: return s.rbProduction;
                case Kind.Security: return s.rbSecurity;
                default: return s.rbOther;
            }
        }

        public static void Place(Map map, RebuildEntry e)
        {
            if (map == null || e.def == null || !e.cell.InBounds(map)) return;
            if (HasBlueprint(map, e.cell, e.def)) return;
            if (!GenConstruct.CanPlaceBlueprintAt(e.def, e.cell, e.rot, map, false, null, null, e.stuff).Accepted) return;
            try { GenConstruct.PlaceBlueprintForBuild(e.def, e.cell, map, e.rot, Faction.OfPlayer, e.stuff, null, e.style); }
            catch (Exception ex) { Log.WarningOnce("[QoL Faster Work] rebuild " + e.def.defName + ": " + ex.Message, 81723412); }
        }

        public static bool HasBlueprint(Map map, IntVec3 c, ThingDef def)
        {
            foreach (Thing t in c.GetThingList(map))
                if ((t is Blueprint_Build || t is Frame) && t.def.entityDefToBuild == def) return true;
            return false;
        }
    }

    [HarmonyPatch(typeof(Building), "Destroy")]
    public static class Patch_BuildingDestroy
    {
        public class State
        {
            public Map map;
            public IntVec3 cell;
            public Rot4 rot;
            public ThingDef def, stuff;
            public ThingStyleDef style;
            public PendingSettings settings;
        }

        public static void Prefix(Building __instance, DestroyMode mode, out State __state)
        {
            __state = null;
            try
            {
                QSettings s = QoLMod.S;
                if (!s.autoRebuild || mode != DestroyMode.KillFinalize || !__instance.Spawned || __instance.Faction != Faction.OfPlayer) return;
                ThingDef d = __instance.def;
                if (d.blueprintDef == null || !d.BuildableByPlayer || !d.IsResearchFinished || !AutoRebuild.KindOn(AutoRebuild.KindOf(d))) return;
                Map map = __instance.Map;
                if (!s.rbOutsideHome && !map.areaManager.Home[__instance.Position]) return;
                __state = new State
                {
                    map = map, cell = __instance.Position, rot = __instance.Rotation, def = d,
                    stuff = __instance.Stuff, style = __instance.StyleDef,
                    settings = s.rbSettings ? PendingSettings.From(__instance, __instance.Position, d) : null
                };
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] rebuild check: " + e.Message, 81723413); }
        }

        public static void Postfix(State __state)
        {
            if (__state == null) return;
            try
            {
                QoLMapComp comp = QoLMapComp.Of(__state.map);
                if (comp == null) return;
                if (__state.settings != null) comp.Add(__state.settings);
                // the game's own auto rebuild may have placed it already
                if (AutoRebuild.HasBlueprint(__state.map, __state.cell, __state.def)) return;
                var e = new RebuildEntry { cell = __state.cell, rot = __state.rot, def = __state.def, stuff = __state.stuff, style = __state.style };
                if (QoLMod.S.rbWaitPeace && GenHostility.AnyHostileActiveThreatToPlayer(__state.map))
                {
                    comp.rebuild.RemoveAll(x => x.cell == e.cell && x.def == e.def);
                    comp.rebuild.Add(e);
                }
                else AutoRebuild.Place(__state.map, e);
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] rebuild: " + e.Message, 81723414); }
        }
    }

    // ==================================================================
    // 1.1.0: the pipette remembers the source building; blueprints placed with that designator
    // get its bills and storage settings when they are built.
    // ==================================================================
    public static class PipetteSource
    {
        public static Designator designator;
        public static Thing source;

        public static void Set(Thing src)
        {
            designator = null; source = null;
            if (!QoLMod.S.pipetteSettings || src == null) return;
            if (!(src is IBillGiver) && !(src is IStoreSettingsParent)) return;
            source = src;
            designator = Find.DesignatorManager.SelectedDesignator;
        }
    }

    [HarmonyPatch(typeof(Designator_Build), "DesignateSingleCell")]
    public static class Patch_PipettePlace
    {
        public static void Postfix(Designator_Build __instance, IntVec3 c)
        {
            try
            {
                if (PipetteSource.source == null || PipetteSource.designator != __instance) return;
                if (PipetteSource.source.Destroyed && !(PipetteSource.source is IBillGiver)) { PipetteSource.source = null; return; }
                ThingDef def = __instance.PlacingDef as ThingDef;
                if (def == null || def != PipetteSource.source.def) return;
                Map map = Find.CurrentMap;
                if (!AutoRebuild.HasBlueprint(map, c, def)) return;
                QoLMapComp.Of(map)?.Add(PendingSettings.From(PipetteSource.source, c, def));
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] pipette settings: " + e.Message, 81723415); }
        }
    }
}
