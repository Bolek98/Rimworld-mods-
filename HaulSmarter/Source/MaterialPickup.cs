using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace HaulSmarter
{
    // ------------------------------------------------------------------
    // 3.4 Smart material pick-up: a builder who goes to bring material to a
    // construction site first takes EVERYTHING the site (and the sites right
    // next to it) still needs – several materials and more than one armful –
    // into his pockets in one round, then builds from the pockets
    // ("Building from the pockets") instead of running back for each part.
    // ------------------------------------------------------------------
    public static class MaterialPickup
    {
        public const float SiteRadius = 10f;
        public const int MaxSites = 8;
        private static readonly Dictionary<int, int> cooldown = new Dictionary<int, int>();
        // which materials a fetch job was made for (the job's own list of defs)
        private static readonly Dictionary<int, HashSet<ThingDef>> wanted = new Dictionary<int, HashSet<ThingDef>>();

        public static bool Wanted(Job job, ThingDef def) =>
            job != null && (!wanted.TryGetValue(job.loadID, out HashSet<ThingDef> set) || set.Contains(def));

        private static int Need(Pawn pawn, IConstructible c, ThingDef def, bool forced) =>
            forced || !(c is IHaulEnroute en) ? c.ThingCountNeeded(def) : en.GetSpaceRemainingWithEnroute(def, pawn);

        public static Job Make(Pawn pawn, IConstructible c, bool forced)
        {
            HSSettings s = HSMod.S;
            if (s == null || !s.smartPickup || !s.buildFromPockets || !Compat.Hauling || !Safe.On("pickup") || !HaulUtil.CanUsePockets(pawn))
                return null;
            if (!(c is Thing site) || !site.Spawned || c is Blueprint_Install || pawn.Map == null)
                return null;
            int now = Find.TickManager.TicksGame;
            if (cooldown.TryGetValue(pawn.thingIDNumber, out int until) && now < until)
                return null;
            try
            {
                Map map = pawn.Map;
                // the site and other sites of ours right next to it
                var sites = new List<IConstructible> { c };
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingFrame).Concat(map.listerThings.ThingsInGroup(ThingRequestGroup.Blueprint)))
                {
                    if (sites.Count >= MaxSites) break;
                    if (t == site || !(t is IConstructible ic) || t is Blueprint_Install || t.Faction != pawn.Faction || t.IsForbidden(pawn))
                        continue;
                    if (t.Position.DistanceTo(site.Position) > SiteRadius || !pawn.CanReserve(t))
                        continue;
                    sites.Add(ic);
                }
                // what they still need
                var need = new Dictionary<ThingDef, int>();
                foreach (IConstructible ic in sites)
                {
                    List<ThingDefCountClass> cost;
                    try { cost = ic.TotalMaterialCost(); } catch { continue; }
                    if (cost == null) continue;
                    foreach (ThingDefCountClass tc in cost)
                    {
                        int n = Need(pawn, ic, tc.thingDef, forced);
                        if (n <= 0) continue;
                        need.TryGetValue(tc.thingDef, out int had);
                        need[tc.thingDef] = had + n;
                    }
                }
                if (need.Count == 0)
                    return null;
                // only worth it when one armful is not enough: two materials, or more than he can carry in his hands
                bool worth = need.Count >= 2 || sites.Count >= 2 || need.Any(kv => kv.Value > pawn.carryTracker.MaxStackSpaceEver(kv.Key));
                if (!worth)
                    return null;

                // collect the stacks, nearest first, as much as fits
                float free = HaulUtil.FreeKg(pawn);
                var things = new List<LocalTargetInfo>();
                var counts = new List<int>();
                foreach (var kv in need.OrderByDescending(x => x.Value * x.Key.BaseMass))
                {
                    int left = kv.Value;
                    var stacks = map.listerThings.ThingsOfDef(kv.Key)
                        .Where(t => t.Spawned && !t.IsForbidden(pawn) && !t.IsBurning() && pawn.CanReserve(t))
                        .OrderBy(t => t.Position.DistanceToSquared(pawn.Position)).Take(12).ToList();
                    foreach (Thing t in stacks)
                    {
                        if (left <= 0 || free <= 0.2f) break;
                        if (!pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Some))
                            continue;
                        float m = HaulUtil.UnitMass(t);
                        int n = Mathf.Min(left, t.stackCount);
                        if (m > 0.0001f) n = Mathf.Min(n, Mathf.FloorToInt(free / m));
                        if (n <= 0) break;
                        things.Add(t);
                        counts.Add(n);
                        left -= n;
                        free -= n * m;
                    }
                }
                if (things.Count == 0)
                    return null;
                // a single stack he can carry in his hands anyway: the normal way is just as good
                if (things.Count == 1 && sites.Count == 1)
                    return null;
                if (s.smartRoute && things.Count > 2)
                    Route.Order(pawn.Position, site.Position, things, counts);

                Job job = JobMaker.MakeJob(HSDefOf.HaulSmarterFetch);
                job.targetQueueA = things;
                job.countQueue = counts;
                job.targetC = things[0];
                job.playerForced = forced;
                wanted[job.loadID] = new HashSet<ThingDef>(need.Keys);
                if (wanted.Count > 200)
                    foreach (int k in wanted.Keys.Take(100).ToList()) wanted.Remove(k);
                cooldown[pawn.thingIDNumber] = now + 600;   // not again right away if the round finds nothing
                return job;
            }
            catch (Exception e)
            {
                Safe.Fail("pickup", e);
                return null;
            }
        }
    }
}
