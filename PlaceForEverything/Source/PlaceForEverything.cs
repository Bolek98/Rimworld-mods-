using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PlaceForEverything
{
    public class PFESettings : ModSettings
    {
        public bool recommended = true;
        public bool alert = true;            // "N kinds of items have nowhere to go"
        public bool wholeCategory = true;    // allow the item's whole category (future items of that kind too)
        public bool rename = true;           // default storage names get a label of what is inside
        public bool includeForbidden = false;
        public bool onlyHome = true;         // only items inside the home area
        public bool includeChunks = false;   // rock and slag chunks usually stay where they are
        public bool stackLimits = true;      // 0.1.2: stack limit / refill per storage (gizmo)

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref alert, "alert", true);
            Scribe_Values.Look(ref wholeCategory, "wholeCategory", true);
            Scribe_Values.Look(ref rename, "rename", true);
            Scribe_Values.Look(ref includeForbidden, "includeForbidden", false);
            Scribe_Values.Look(ref onlyHome, "onlyHome", true);
            Scribe_Values.Look(ref includeChunks, "includeChunks", false);
            Scribe_Values.Look(ref stackLimits, "stackLimits", true);
        }
    }

    public class PFEMod : Mod
    {
        public const string Version = "0.1.2";
        public static PFESettings S;
        private static List<string> recBullets;

        public PFEMod(ModContentPack content) : base(content)
        {
            S = GetSettings<PFESettings>();
            MUI.Init(new PFESettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("bolek98.placeforeverything");
            try { MUI.PatchWindow(h, typeof(PFEMod)); } catch { }
            h.PatchAll();
        }

        public override string SettingsCategory() => "Place for Everything";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "PFE_Rec1".Translate(), "PFE_Rec2".Translate(), "PFE_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                Check(l, "PFE_Alert", ref S.alert, "alert");
                Check(l, "PFE_WholeCategory", ref S.wholeCategory, "wholeCategory");
                Check(l, "PFE_Rename", ref S.rename, "rename");
                Check(l, "PFE_Forbidden", ref S.includeForbidden, "includeForbidden");
                Check(l, "PFE_OnlyHome", ref S.onlyHome, "onlyHome");
                Check(l, "PFE_Chunks", ref S.includeChunks, "includeChunks");
                Check(l, "PFE_StackLimits", ref S.stackLimits, "stackLimits");
                l.End();
            }
            finally { MUI.EndLock(rest); }
        }
    }

    // ---------------------------------------------------------------------------
    // What a storage is: where it lies and what is around it.
    // ---------------------------------------------------------------------------
    public sealed class Store
    {
        public ISlotGroupParent parent;
        public StorageSettings settings;
        public StorageSettings parentSettings;
        public IRenameable named;        // a zone or a storage group (single shelves have no name)
        public string label;
        public Room room;
        public bool outdoors, freezer, hospital, kitchen;
        public HashSet<ThingDef> benchUses = new HashSet<ThingDef>();   // ingredients of workbenches nearby
        public int cells;

        public bool CanHold(ThingDef d)
        {
            if (parentSettings != null && !parentSettings.AllowedToAccept(d)) return false;
            return true;
        }

        public static Store Of(SlotGroup g) => Of(g, true);

        // deep = also look at the room and the workbenches (only for the preview; the alert needs less)
        public static Store Of(SlotGroup g, bool deep)
        {
            ISlotGroupParent p = g.parent;
            if (p == null) return null;
            StorageSettings st = p.GetStoreSettings();
            if (st == null) return null;
            var s = new Store { parent = p, settings = st, parentSettings = p.GetParentStoreSettings(), label = p.SlotYielderLabel() };
            if (p is Zone z) s.named = z;
            else if (g.StorageGroup != null) s.named = g.StorageGroup;
            List<IntVec3> cells = p.AllSlotCellsList();
            s.cells = cells.Count;
            Map map = p.Map;
            if (!deep || map == null || cells.Count == 0) return s;
            s.room = cells[cells.Count / 2].GetRoom(map);
            s.outdoors = s.room == null || s.room.PsychologicallyOutdoors || s.room.UsesOutdoorTemperature;
            if (!s.outdoors)
            {
                s.freezer = s.room.Temperature < 0f;
                RoomRoleDef role = s.room.Role;
                s.hospital = role == RoomRoleDefOf.Hospital;
                s.kitchen = role != null && role.defName == "Kitchen";
            }
            // workbenches in the same room (or within 8 cells outdoors) and what they use
            IEnumerable<Thing> near = s.outdoors
                ? GenRadial.RadialDistinctThingsAround(cells[cells.Count / 2], map, 8f, true)
                : s.room.ContainedAndAdjacentThings;
            foreach (Thing t in near)
            {
                if (!(t is Building_WorkTable) || t.def.AllRecipes == null) continue;
                if (t.def.building != null && t.def.building.isMealSource) s.kitchen = true;
                foreach (RecipeDef rec in t.def.AllRecipes)
                {
                    if (rec.ingredients == null) continue;
                    foreach (IngredientCount ing in rec.ingredients)
                        if (ing.filter != null)
                            foreach (ThingDef d in ing.filter.AllowedThingDefs)
                                s.benchUses.Add(d);
                }
            }
            return s;
        }
    }

    // One kind of item without a place, and where it should go.
    public sealed class Proposal
    {
        public ThingDef def;
        public int count;
        public Thing example;
        public ThingCategoryDef category;
        public List<Store> options = new List<Store>();
        public Store target;
        public string why;
        public bool on = true;
    }

    public static class Finder
    {
        // every kind of item lying on the map that no storage accepts
        public static List<Proposal> Find(Map map, bool deep = true)
        {
            var res = new List<Proposal>();
            if (map == null) return res;
            List<Store> stores = map.haulDestinationManager.AllGroupsListForReading.Select(g => Store.Of(g, deep)).Where(s => s != null).ToList();
            var byDef = new Dictionary<ThingDef, Proposal>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (!t.Spawned || t.Fogged() || t.IsInAnyStorage() || t is Pawn) continue;
                if (!PFEMod.S.includeForbidden && t.IsForbidden(Faction.OfPlayer)) continue;
                if (PFEMod.S.onlyHome && !map.areaManager.Home[t.Position]) continue;
                if (!PFEMod.S.includeChunks && Under(t.def, ThingCategoryDefOf.Chunks)) continue;
                if (t.def.category != ThingCategory.Item && !(t is Corpse)) continue;
                if (t is Corpse c && c.InnerPawn != null && c.InnerPawn.RaceProps.Humanlike) continue;   // human bodies are the player's call
                if (byDef.TryGetValue(t.def, out Proposal known)) { known.count += t.stackCount; continue; }
                if (stores.Any(s => s.settings.AllowedToAccept(t))) continue;
                byDef[t.def] = new Proposal { def = t.def, count = t.stackCount, example = t };
            }
            foreach (Proposal p in byDef.Values)
            {
                p.category = p.def.thingCategories?.FirstOrDefault();
                foreach (Store s in stores)
                    if (s.CanHold(p.def)) p.options.Add(s);
                if (p.options.Count == 0) continue;     // nothing could ever take it (e.g. no shelf for it, no zone)
                if (deep) p.target = Best(p, out p.why);
                res.Add(p);
            }
            return res.OrderBy(p => p.category?.label ?? "").ThenBy(p => p.def.label).ToList();
        }

        private static bool Under(ThingDef d, ThingCategoryDef cat) =>
            cat != null && d.thingCategories != null && d.thingCategories.Any(c => c == cat || c.Parents.Contains(cat));

        private static Store Best(Proposal p, out string why)
        {
            ThingDef d = p.def;
            bool rots = d.GetCompProperties<CompProperties_Rottable>() != null;
            bool food = Under(d, ThingCategoryDefOf.Foods) || d.IsIngestible && !d.IsDrug && !d.IsMedicine;
            bool med = d.IsMedicine || Under(d, ThingCategoryDefOf.Medicine) || Under(d, ThingCategoryDefOf.BodyParts);
            bool drug = d.IsDrug || Under(d, ThingCategoryDefOf.Drugs);
            bool chunk = Under(d, ThingCategoryDefOf.Chunks);
            bool corpse = typeof(Corpse).IsAssignableFrom(d.thingClass);
            ThingCategoryDef top = TopCategory(d);

            Store best = null;
            float bestScore = float.MinValue;
            string bestWhy = "";
            foreach (Store s in p.options)
            {
                float score = 0f;
                string w = "PFE_Why_Space";
                // siblings: the storage already takes items of the same kind
                int sib = p.category != null ? p.category.childThingDefs.Count(x => x != d && s.settings.filter.Allows(x)) : 0;
                int sibTop = top != null ? top.DescendantThingDefs.Take(400).Count(x => s.settings.filter.Allows(x)) : 0;
                if (sib > 0) { score += 30f + Mathf.Min(sib, 10); w = "PFE_Why_Similar"; }
                else if (sibTop > 0) { score += 15f + Mathf.Min(sibTop, 10) * 0.5f; w = "PFE_Why_Similar"; }
                if (s.benchUses.Contains(d)) { score += 40f; w = "PFE_Why_Bench"; }
                if ((rots || corpse) && s.freezer) { score += 60f; w = "PFE_Why_Freezer"; }
                else if (rots && !s.freezer) score -= 20f;
                if (food && !rots && s.kitchen) { score += 25f; w = "PFE_Why_Kitchen"; }
                if ((med || drug) && s.hospital) { score += 50f; w = "PFE_Why_Hospital"; }
                if (!med && !drug && s.hospital) score -= 25f;
                if ((chunk || corpse) && s.outdoors) { score += 35f; w = "PFE_Why_Outside"; }
                if ((chunk || corpse) && !s.outdoors) score -= 30f;
                if (!chunk && !corpse && s.outdoors && d.GetStatValueAbstract(StatDefOf.DeteriorationRate) > 0f) { score -= 30f; }
                if (!food && !corpse && s.freezer) score -= 15f;
                if (!food && s.kitchen) score -= 10f;
                score += Mathf.Min(s.cells, 40) * 0.1f;          // a roomier place a little first
                if (score > bestScore) { bestScore = score; best = s; bestWhy = w; }
            }
            why = bestWhy;
            return best;
        }

        public static ThingCategoryDef TopCategory(ThingDef d)
        {
            ThingCategoryDef c = d.thingCategories?.FirstOrDefault();
            if (c == null) return null;
            while (c.parent != null && c.parent != ThingCategoryDefOf.Root) c = c.parent;
            return c;
        }
    }

    // ---------------------------------------------------------------------------
    // Applying, naming, undo.
    // ---------------------------------------------------------------------------
    public class PFEMap : MapComponent
    {
        private sealed class Saved
        {
            public StorageSettings settings;
            public StorageSettings copy;
            public IRenameable named;
            public string oldLabel;
        }

        private static List<Saved> undo;
        private static Map undoMap;
        private HashSet<string> ours = new HashSet<string>();    // storages this mod named (renamed again on the next run)
        public int lastCount;
        private int countedAt = -99999;

        public PFEMap(Map map) : base(map) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref ours, "ours", LookMode.Value);
            if (ours == null) ours = new HashSet<string>();
        }

        public int Homeless()
        {
            int now = Find.TickManager.TicksGame;
            if (now - countedAt > 600)
            {
                countedAt = now;
                try { lastCount = Finder.Find(map, false).Count; } catch { lastCount = 0; }
            }
            return lastCount;
        }

        public void Recount() => countedAt = -99999;

        private static string Key(IRenameable n) => n is Zone z ? "Z" + z.ID : n is StorageGroup g ? "G" + g.loadID : null;

        private bool DefaultName(IRenameable n)
        {
            string k = Key(n);
            if (k != null && ours.Contains(k)) return true;
            if (n is Zone z) return z.label == null || z.label.StartsWith(z.BaseLabel ?? "\u0001", StringComparison.Ordinal);
            if (n is StorageGroup g) return string.IsNullOrEmpty(g.RenamableLabel) || g.RenamableLabel == g.BaseLabel || g.RenamableLabel.StartsWith(g.BaseLabel ?? "\u0001", StringComparison.Ordinal);
            return false;
        }

        public void Apply(List<Proposal> list)
        {
            var saved = new List<Saved>();
            var touched = new List<Store>();
            foreach (Proposal p in list.Where(x => x.on && x.target != null))
            {
                Store s = p.target;
                if (!saved.Any(x => x.settings == s.settings))
                {
                    var copy = new StorageSettings();
                    copy.CopyFrom(s.settings);
                    saved.Add(new Saved { settings = s.settings, copy = copy, named = s.named, oldLabel = s.named?.RenamableLabel });
                    touched.Add(s);
                }
                if (PFEMod.S.wholeCategory && p.category != null && p.category != ThingCategoryDefOf.Root)
                    s.settings.filter.SetAllow(p.category, true);
                else
                    s.settings.filter.SetAllow(p.def, true);
                s.settings.filter.SetAllow(p.def, true);     // the item itself always
            }
            if (PFEMod.S.rename)
                foreach (Store s in touched)
                    if (s.named != null && DefaultName(s.named))
                    {
                        s.named.RenamableLabel = NameFor(s);
                        string k = Key(s.named);
                        if (k != null) ours.Add(k);
                    }
            undo = saved;
            undoMap = map;
            Recount();
            Messages.Message("PFE_Applied".Translate(list.Count(x => x.on && x.target != null), touched.Count), MessageTypeDefOf.TaskCompletion, false);
        }

        public static bool CanUndo(Map m) => undo != null && undo.Count > 0 && undoMap == m;

        public static void Undo()
        {
            if (undo == null) return;
            foreach (Saved s in undo)
            {
                s.settings.CopyFrom(s.copy);
                if (s.named != null && s.oldLabel != null) s.named.RenamableLabel = s.oldLabel;
            }
            undo = null;
            undoMap?.GetComponent<PFEMap>()?.Recount();
            Messages.Message("PFE_Undone".Translate(), MessageTypeDefOf.NeutralEvent, false);
        }

        // "Storage – Food, Medicine": the biggest kinds the storage takes now
        private static string NameFor(Store s)
        {
            var counts = new Dictionary<ThingCategoryDef, int>();
            foreach (ThingDef d in s.settings.filter.AllowedThingDefs)
            {
                ThingCategoryDef top = Finder.TopCategory(d);
                if (top == null) continue;
                // one level deeper for the big ones, so "Food" says raw food or meals
                ThingCategoryDef c = d.thingCategories.FirstOrDefault();
                while (c != null && c.parent != null && c.parent != top && c.parent != ThingCategoryDefOf.Root) c = c.parent;
                ThingCategoryDef key = c ?? top;
                counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
            }
            var names = counts.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key.LabelCap.ToString()).ToList();
            string place = s.freezer ? (string)"PFE_Place_Freezer".Translate() : s.hospital ? "PFE_Place_Hospital".Translate() : s.kitchen ? "PFE_Place_Kitchen".Translate()
                : s.outdoors ? "PFE_Place_Outside".Translate() : "PFE_Place_Storage".Translate();
            if (counts.Count > 6) return place + " – " + "PFE_Everything".Translate();
            return names.Count == 0 ? place : place + " – " + string.Join(", ", names.ToArray());
        }
    }

    // ---------------------------------------------------------------------------
    // Preview.
    // ---------------------------------------------------------------------------
    public class Dialog_PFE : Window
    {
        private readonly Map map;
        private List<Proposal> list;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(980f, 680f);

        public Dialog_PFE(Map map)
        {
            this.map = map;
            list = Finder.Find(map);
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
        }

        private static string StoreName(Store s)
        {
            string n = s.named?.RenamableLabel ?? s.label ?? "?";
            string where = s.freezer ? (string)"PFE_Place_Freezer".Translate() : s.hospital ? "PFE_Place_Hospital".Translate() : s.kitchen ? "PFE_Place_Kitchen".Translate() : s.outdoors ? "PFE_Place_Outside".Translate() : null;
            return where == null ? n : n + " (" + where + ")";
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 34f), "PFE_Title".Translate());
            Text.Font = GameFont.Small;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y + 36f, r.width, 44f), "PFE_Intro".Translate());
            GUI.color = Color.white;
            float y = r.y + 82f;

            if (list.Count == 0)
            {
                Widgets.Label(new Rect(r.x, y, r.width, 30f), "PFE_Nothing".Translate());
            }
            else
            {
                Rect head = new Rect(r.x, y, r.width - 16f, 24f);
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(head.x + 32f, head.y, 280f, 24f), "PFE_ColItem".Translate());
                Widgets.Label(new Rect(head.x + 320f, head.y, 330f, 24f), "PFE_ColWhere".Translate());
                Widgets.Label(new Rect(head.x + 660f, head.y, 260f, 24f), "PFE_ColWhy".Translate());
                GUI.color = Color.white;
                y += 26f;
                Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 44f);
                const float rowH = 32f;
                Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * rowH);
                Widgets.BeginScrollView(outR, ref scroll, view);
                for (int i = 0; i < list.Count; i++)
                {
                    Proposal p = list[i];
                    Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                    if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                    Widgets.Checkbox(new Vector2(row.x + 4f, row.y + 3f), ref p.on);
                    Widgets.ThingIcon(new Rect(row.x + 32f, row.y + 2f, 26f, 26f), p.example);
                    Text.Anchor = TextAnchor.MiddleLeft;
                    Rect nameR = new Rect(row.x + 62f, row.y, 250f, row.height);
                    Widgets.Label(nameR, (p.def.LabelCap + " ×" + p.count).Truncate(nameR.width));
                    TooltipHandler.TipRegion(nameR, (string)p.def.LabelCap + (p.category != null ? "\n" + (string)"PFE_Category".Translate(p.category.LabelCap) : ""));
                    Rect pick = new Rect(row.x + 320f, row.y + 2f, 330f, row.height - 4f);
                    if (Widgets.ButtonText(pick, p.target != null ? StoreName(p.target).Truncate(pick.width - 10f) : "–"))
                    {
                        var opts = p.options.Select(s => new FloatMenuOption(StoreName(s), () => { p.target = s; p.why = "PFE_Why_Yours"; p.on = true; })).ToList();
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    GUI.color = MUI.Muted;
                    Widgets.Label(new Rect(row.x + 660f, row.y, row.width - 660f, row.height), ((string)p.why.Translate()).Truncate(row.width - 664f));
                    GUI.color = Color.white;
                    Text.Anchor = TextAnchor.UpperLeft;
                }
                Widgets.EndScrollView();
            }

            float by = r.yMax - 36f;
            if (list.Count > 0 && Widgets.ButtonText(new Rect(r.xMax - 220f, by, 220f, 34f), "PFE_Apply".Translate(list.Count(x => x.on))))
            {
                map.GetComponent<PFEMap>()?.Apply(list);
                Close();
            }
            if (PFEMap.CanUndo(map) && Widgets.ButtonText(new Rect(r.x, by, 220f, 34f), "PFE_Undo".Translate()))
            {
                PFEMap.Undo();
                list = Finder.Find(map);
            }
            if (list.Count > 0)
            {
                if (Widgets.ButtonText(new Rect(r.x + 230f, by, 130f, 34f), "PFE_All".Translate())) foreach (Proposal p in list) p.on = true;
                if (Widgets.ButtonText(new Rect(r.x + 366f, by, 130f, 34f), "PFE_None".Translate())) foreach (Proposal p in list) p.on = false;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Ways in: an alert, and a button on every stockpile and shelf.
    // ---------------------------------------------------------------------------
    public class Alert_PFEHomeless : Alert
    {
        public Alert_PFEHomeless()
        {
            defaultLabel = "PFE_AlertLabel".Translate();
            defaultPriority = AlertPriority.Medium;
        }

        public override AlertReport GetReport()
        {
            if (!PFEMod.S.alert || Find.CurrentMap == null || !Find.CurrentMap.IsPlayerHome) return false;
            PFEMap c = Find.CurrentMap.GetComponent<PFEMap>();
            return c != null && c.Homeless() > 0;
        }

        public override string GetLabel() => "PFE_AlertLabelN".Translate(Find.CurrentMap?.GetComponent<PFEMap>()?.lastCount ?? 0);

        public override TaggedString GetExplanation() => "PFE_AlertDesc".Translate();

        protected override void OnClick()
        {
            if (Find.CurrentMap != null) Find.WindowStack.Add(new Dialog_PFE(Find.CurrentMap));
        }
    }

    public static class Gizmos
    {
        public static Command_Action Button(Map map) => new Command_Action
        {
            defaultLabel = "PFE_Button".Translate(),
            defaultDesc = "PFE_ButtonDesc".Translate(),
            icon = ContentFinder<Texture2D>.Get("UI/Designators/ZoneCreate_Stockpile", false) ?? BaseContent.BadTex,
            action = () => Find.WindowStack.Add(new Dialog_PFE(map))
        };
    }

    [HarmonyPatch(typeof(Zone_Stockpile), nameof(Zone_Stockpile.GetGizmos))]
    public static class Patch_ZoneGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Zone_Stockpile __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (__instance.Map != null) yield return Gizmos.Button(__instance.Map);
            if (__instance.Map != null && PFEMod.S.stackLimits) yield return LimitGizmo.Button(__instance.Map, __instance);   // 0.1.2
        }
    }

    [HarmonyPatch(typeof(Building_Storage), nameof(Building_Storage.GetGizmos))]
    public static class Patch_StorageGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Building_Storage __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (__instance.Map != null && __instance.Faction == Faction.OfPlayer) yield return Gizmos.Button(__instance.Map);
            if (__instance.Map != null && __instance.Faction == Faction.OfPlayer && PFEMod.S.stackLimits) yield return LimitGizmo.Button(__instance.Map, __instance);   // 0.1.2
        }
    }
}
