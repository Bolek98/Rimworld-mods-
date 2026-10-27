using System;
using System.Collections.Generic;
using RimWorld;
using Verse;

namespace HardyPlants
{
    // ------------------------------------------------------------------
    // 1.9.1 Crop growth sync (setting, off by default): crops in one grow
    // zone that were sown within about a day of each other ripen together.
    // Once an in-game hour the plants of each grow zone are sorted by growth;
    // a plant that is at most one day of growth behind the leader of its
    // group is moved half of the gap closer. Plants sown much later (a new
    // sowing after a harvest) form their own group and are left alone.
    // Only reads zone cells, so the cost is tiny.
    // ------------------------------------------------------------------
    public class CropSyncComp : MapComponent
    {
        private const int Interval = 2500;
        private static bool warned;
        private readonly List<Plant> buf = new List<Plant>();

        public CropSyncComp(Map map) : base(map) { }

        public override void MapComponentTick()
        {
            HardyPlantsSettings s = HardyPlantsMod.Settings;
            if (s == null || !s.cropSync)
                return;
            if ((Find.TickManager.TicksGame + map.uniqueID * 97) % Interval != 0)
                return;
            try
            {
                List<Zone> zones = map.zoneManager.AllZones;
                for (int i = 0; i < zones.Count; i++)
                    if (zones[i] is Zone_Growing z)
                        Sync(z);
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[Evergrowth] crop growth sync turned itself off for this session: " + ex);
                }
                s.cropSync = false;
            }
        }

        private void Sync(Zone_Growing z)
        {
            ThingDef def = z.PlantDefToGrow;
            if (def == null || def.plant == null || def.plant.growDays <= 0f || def.plant.IsTree)
                return;
            buf.Clear();
            List<IntVec3> cells = z.Cells;
            for (int i = 0; i < cells.Count; i++)
            {
                Plant p = cells[i].GetPlant(map);
                if (p == null || p.def != def || !p.sown || p.Blighted || p.LifeStage == PlantLifeStage.Sowing)
                    continue;
                buf.Add(p);
            }
            if (buf.Count < 2)
                return;
            buf.Sort((a, b) => b.Growth.CompareTo(a.Growth));
            float day = 1f / def.plant.growDays;
            float head = buf[0].Growth;
            for (int i = 1; i < buf.Count; i++)
            {
                Plant p = buf[i];
                float g = p.Growth;
                float gap = head - g;
                if (gap > day)
                {
                    head = g;   // a later sowing: its own group
                    continue;
                }
                if (gap < 0.002f)
                    continue;
                float ng = gap < 0.01f ? head : g + gap * 0.5f;
                p.Growth = ng;
                if ((int)(g * 10f) != (int)(ng * 10f) || ng >= 1f)
                    map.mapDrawer.MapMeshDirty(p.Position, MapMeshFlagDefOf.Things);
            }
            buf.Clear();
        }
    }
}
