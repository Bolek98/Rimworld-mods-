using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace PlaceForEverything
{
    // ---------------------------------------------------------------------------
    // 0.1.2: stack limit and refill threshold per stockpile / shelf.
    //   Limit  – a cell of this storage holds at most N % of a full stack of a
    //            kind of item (several stacks of one kind on a shelf count together).
    //   Refill – a cell that already holds that kind gets more only once it has
    //            dropped below N % of the limit (100 % = top up at once).
    // Kept per stockpile (zone ID) and storage building (thing ID) in a map
    // component. Hauling respects it through StoreUtility.IsGoodStoreCell and
    // the cell's free stack space (GridsUtility.GetItemStackSpaceLeftFor).
    // ---------------------------------------------------------------------------
    public sealed class StackLimit : IExposable
    {
        public int limitPct = 100;
        public int refillPct = 100;

        public bool Active => limitPct < 100 || refillPct < 100;

        public int Cap(ThingDef d) => Mathf.Max(1, Mathf.RoundToInt(d.stackLimit * Mathf.Clamp(limitPct, 1, 100) / 100f));

        public int RefillAt(int cap) => Mathf.Max(1, Mathf.CeilToInt(cap * Mathf.Clamp(refillPct, 1, 100) / 100f));

        public void ExposeData()
        {
            Scribe_Values.Look(ref limitPct, "limitPct", 100);
            Scribe_Values.Look(ref refillPct, "refillPct", 100);
        }
    }

    public class PFELimits : MapComponent
    {
        public Dictionary<int, StackLimit> zones = new Dictionary<int, StackLimit>();
        public Dictionary<int, StackLimit> buildings = new Dictionary<int, StackLimit>();
        private List<int> k1, k2;
        private List<StackLimit> v1, v2;

        public PFELimits(Map map) : base(map) { }

        private static Map lastMap;
        private static PFELimits lastComp;

        public static PFELimits For(Map map)
        {
            if (map == null)
                return null;
            if (lastMap != map || lastComp == null)
            {
                lastMap = map;
                lastComp = map.GetComponent<PFELimits>();
            }
            return lastComp;
        }

        public bool Any => zones.Count > 0 || buildings.Count > 0;

        public static StackLimit At(Map map, IntVec3 c)
        {
            PFELimits comp = For(map);
            if (comp == null || !comp.Any)
                return null;
            SlotGroup g = c.GetSlotGroup(map);
            if (g == null)
                return null;
            StackLimit l;
            if (g.parent is Zone_Stockpile z)
                return comp.zones.TryGetValue(z.ID, out l) ? l : null;
            if (g.parent is Building_Storage b)
                return comp.buildings.TryGetValue(b.thingIDNumber, out l) ? l : null;
            return null;
        }

        public StackLimit Get(object storage, bool create)
        {
            Dictionary<int, StackLimit> dict = storage is Zone_Stockpile ? zones : storage is Building_Storage ? buildings : null;
            int id = storage is Zone_Stockpile z ? z.ID : storage is Building_Storage b ? b.thingIDNumber : -1;
            if (dict == null)
                return null;
            if (!dict.TryGetValue(id, out StackLimit l) && create)
                dict[id] = l = new StackLimit();
            return l;
        }

        public void Set(object storage, int limitPct, int refillPct)
        {
            StackLimit l = Get(storage, true);
            if (l == null)
                return;
            l.limitPct = Mathf.Clamp(limitPct, 1, 100);
            l.refillPct = Mathf.Clamp(refillPct, 1, 100);
            if (!l.Active)
            {
                if (storage is Zone_Stockpile z) zones.Remove(z.ID);
                else if (storage is Building_Storage b) buildings.Remove(b.thingIDNumber);
            }
        }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                // storages that are gone are forgotten
                var zoneIds = new HashSet<int>(map.zoneManager.AllZones.Select(z => z.ID));
                foreach (int id in zones.Keys.ToList())
                    if (!zoneIds.Contains(id) || !zones[id].Active) zones.Remove(id);
                var bIds = new HashSet<int>(map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial).Select(t => t.thingIDNumber));
                foreach (int id in buildings.Keys.ToList())
                    if (!bIds.Contains(id) || !buildings[id].Active) buildings.Remove(id);
            }
            Scribe_Collections.Look(ref zones, "pfeZoneLimits", LookMode.Value, LookMode.Deep, ref k1, ref v1);
            Scribe_Collections.Look(ref buildings, "pfeBuildingLimits", LookMode.Value, LookMode.Deep, ref k2, ref v2);
            if (zones == null) zones = new Dictionary<int, StackLimit>();
            if (buildings == null) buildings = new Dictionary<int, StackLimit>();
        }

        // how much of this kind lies in the cell (the hauled thing itself not counted)
        public static int CountIn(IntVec3 c, Map map, ThingDef d, Thing except)
        {
            int n = 0;
            List<Thing> list = map.thingGrid.ThingsListAt(c);
            for (int i = 0; i < list.Count; i++)
                if (list[i].def == d && list[i] != except)
                    n += list[i].stackCount;
            return n;
        }
    }

    internal static class LimitGuard
    {
        public static bool failed;

        public static void Fail(Exception ex)
        {
            if (failed) return;
            failed = true;
            Log.Warning("[Place for Everything] stack limits turned off for this session: " + ex.Message);
        }
    }

    [HarmonyPatch(typeof(StoreUtility), nameof(StoreUtility.IsGoodStoreCell))]
    public static class Patch_LimitGoodCell
    {
        public static void Postfix(IntVec3 c, Map map, Thing t, ref bool __result)
        {
            if (!__result || t == null || map == null || LimitGuard.failed || PFEMod.S == null || !PFEMod.S.stackLimits)
                return;
            try
            {
                StackLimit l = PFELimits.At(map, c);
                if (l == null || !l.Active)
                    return;
                int cap = l.Cap(t.def);
                int have = PFELimits.CountIn(c, map, t.def, t);
                if (have >= cap || (have > 0 && l.refillPct < 100 && have >= l.RefillAt(cap)))
                    __result = false;
            }
            catch (Exception ex) { LimitGuard.Fail(ex); }
        }
    }

    [HarmonyPatch(typeof(GridsUtility), nameof(GridsUtility.GetItemStackSpaceLeftFor))]
    public static class Patch_LimitSpaceLeft
    {
        public static void Postfix(IntVec3 c, Map map, ThingDef itemDef, ref int __result)
        {
            if (__result <= 0 || itemDef == null || map == null || LimitGuard.failed || PFEMod.S == null || !PFEMod.S.stackLimits)
                return;
            try
            {
                StackLimit l = PFELimits.At(map, c);
                if (l == null || l.limitPct >= 100)
                    return;
                int left = l.Cap(itemDef) - PFELimits.CountIn(c, map, itemDef, null);
                if (left < __result)
                    __result = Mathf.Max(0, left);
            }
            catch (Exception ex) { LimitGuard.Fail(ex); }
        }
    }

    public static class LimitGizmo
    {
        public static Command_Action Button(Map map, object storage)
        {
            StackLimit l = PFELimits.For(map)?.Get(storage, false);
            int lim = l?.limitPct ?? 100, ref1 = l?.refillPct ?? 100;
            return new Command_Action
            {
                defaultLabel = lim >= 100 && ref1 >= 100 ? (string)"PFE_LimitOff".Translate() : "PFE_LimitOn".Translate(lim, ref1).ToString(),
                defaultDesc = "PFE_LimitDesc".Translate(),
                icon = ContentFinder<Texture2D>.Get("UI/Commands/SetTargetFuelLevel", false) ?? ContentFinder<Texture2D>.Get("UI/Buttons/Copy", false) ?? BaseContent.BadTex,
                action = () => Find.WindowStack.Add(new Dialog_PFELimit(map, storage))
            };
        }
    }

    public class Dialog_PFELimit : Window
    {
        private readonly Map map;
        private readonly object storage;
        private float limit, refill;

        public override Vector2 InitialSize => new Vector2(460f, 250f);

        public Dialog_PFELimit(Map map, object storage)
        {
            this.map = map;
            this.storage = storage;
            StackLimit l = PFELimits.For(map)?.Get(storage, false);
            limit = l?.limitPct ?? 100;
            refill = l?.refillPct ?? 100;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect r)
        {
            string name = storage is Zone z ? z.label : storage is Thing t ? t.LabelCap.ToString() : "";
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "PFE_LimitTitle".Translate(name).Truncate(r.width));
            Text.Font = GameFont.Small;
            float y = r.y + 40f;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), "PFE_LimitStack".Translate(Mathf.RoundToInt(limit)));
            y += 24f;
            limit = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x, y, r.width, 20f), limit, 5f, 100f) / 5f) * 5f;
            y += 30f;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), refill >= 100f ? (string)"PFE_LimitRefillOff".Translate() : "PFE_LimitRefill".Translate(Mathf.RoundToInt(refill)).ToString());
            y += 24f;
            refill = Mathf.Round(Widgets.HorizontalSlider(new Rect(r.x, y, r.width, 20f), refill, 10f, 100f) / 5f) * 5f;
            y += 30f;
            TooltipHandler.TipRegion(new Rect(r.x, r.y + 40f, r.width, y - r.y - 40f), "PFE_LimitDesc".Translate());
            if (Widgets.ButtonText(new Rect(r.x, r.yMax - 34f, 140f, 34f), "PFE_LimitReset".Translate()))
            {
                limit = 100f;
                refill = 100f;
            }
            if (Widgets.ButtonText(new Rect(r.xMax - 140f, r.yMax - 34f, 140f, 34f), "OK".Translate()))
            {
                PFELimits.For(map)?.Set(storage, Mathf.RoundToInt(limit), Mathf.RoundToInt(refill));
                Close();
            }
        }
    }
}
