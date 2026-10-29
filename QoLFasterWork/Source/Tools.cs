using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace QoLFW
{
    [StaticConstructorOnStartup]
    public static class Tex
    {
        public static readonly Texture2D Copy = ContentFinder<Texture2D>.Get("UI/Commands/CopySettings", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Paste = ContentFinder<Texture2D>.Get("UI/Commands/PasteSettings", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Haul = ContentFinder<Texture2D>.Get("QoLFW/HaulNow", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Grow = ContentFinder<Texture2D>.Get("QoLFW/ZoneGrow", false) ?? BaseContent.BadTex;
        public static readonly Texture2D Shrink = ContentFinder<Texture2D>.Get("QoLFW/ZoneShrink", false) ?? BaseContent.BadTex;
    }

    // ------------------------------------------------------------------
    // Copy / paste pawn settings
    // ------------------------------------------------------------------
    public class PawnSnapshot
    {
        public string name;
        public Dictionary<WorkTypeDef, int> prio = new Dictionary<WorkTypeDef, int>();
        public List<TimeAssignmentDef> times;
        public ApparelPolicy outfit;
        public FoodPolicy food;
        public DrugPolicy drug;
        public ReadingPolicy reading;
        public MedicalCareCategory med;
        public HostilityResponseMode hostility;
        public Area area;
        public Map areaMap;

        public static PawnSnapshot Of(Pawn p)
        {
            var s = new PawnSnapshot { name = p.LabelShort };
            if (p.workSettings != null && p.workSettings.EverWork)
                foreach (WorkTypeDef w in DefDatabase<WorkTypeDef>.AllDefsListForReading)
                    if (!p.WorkTypeIsDisabled(w)) s.prio[w] = p.workSettings.GetPriority(w);
            if (p.timetable?.times != null) s.times = new List<TimeAssignmentDef>(p.timetable.times);
            try { s.outfit = p.outfits?.CurrentApparelPolicy; } catch { }
            try { s.food = p.foodRestriction?.CurrentFoodPolicy; } catch { }
            try { s.drug = p.drugs?.CurrentPolicy; } catch { }
            try { s.reading = p.reading?.CurrentPolicy; } catch { }
            if (p.playerSettings != null)
            {
                s.med = p.playerSettings.medCare;
                s.hostility = p.playerSettings.hostilityResponse;
                s.area = p.playerSettings.AreaRestrictionInPawnCurrentMap;
                s.areaMap = p.MapHeld;
            }
            return s;
        }

        public void PasteTo(Pawn p, bool work, bool schedule, bool policies, bool withArea)
        {
            if (work && p.workSettings != null && p.workSettings.EverWork)
                foreach (var kv in prio)
                    if (!p.WorkTypeIsDisabled(kv.Key)) p.workSettings.SetPriority(kv.Key, kv.Value);
            if (schedule && times != null && p.timetable?.times != null)
                for (int h = 0; h < 24 && h < times.Count; h++) p.timetable.SetAssignment(h, times[h]);
            if (policies)
            {
                try { if (outfit != null && p.outfits != null) p.outfits.CurrentApparelPolicy = outfit; } catch { }
                try { if (food != null && p.foodRestriction != null) p.foodRestriction.CurrentFoodPolicy = food; } catch { }
                try { if (drug != null && p.drugs != null) p.drugs.CurrentPolicy = drug; } catch { }
                try { if (reading != null && p.reading != null) p.reading.CurrentPolicy = reading; } catch { }
                if (p.playerSettings != null)
                {
                    p.playerSettings.medCare = med;
                    p.playerSettings.hostilityResponse = hostility;
                }
            }
            if (withArea && p.playerSettings != null && p.MapHeld == areaMap)
                p.playerSettings.AreaRestrictionInPawnCurrentMap = this.area;
        }
    }

    [HarmonyPatch(typeof(Pawn), "GetGizmos")]
    public static class Patch_PawnGizmos
    {
        public static PawnSnapshot clip;

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Pawn __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!QoLMod.S.copyPawn || !__instance.IsColonistPlayerControlled) yield break;
            List<Pawn> sel = Selected();
            if (sel.Count == 0 || sel[0] != __instance) yield break;
            if (sel.Count == 1)
            {
                yield return new Command_Action
                {
                    defaultLabel = "QFW_CopyPawn_Btn".Translate(),
                    defaultDesc = "QFW_CopyPawn_BtnDesc".Translate(),
                    icon = Tex.Copy,
                    Order = 2000f,
                    action = () =>
                    {
                        clip = PawnSnapshot.Of(__instance);
                        SoundDefOf.Tick_High.PlayOneShotOnCamera();
                        Q.Msg("QFW_Copied".Translate(__instance.LabelShort));
                    }
                };
            }
            if (clip != null)
            {
                yield return new Command_Action
                {
                    defaultLabel = "QFW_PastePawn_Btn".Translate(),
                    defaultDesc = "QFW_PastePawn_BtnDesc".Translate(clip.name),
                    icon = Tex.Paste,
                    Order = 2001f,
                    action = () => PasteMenu(Selected())
                };
            }
        }

        private static List<Pawn> Selected() =>
            Find.Selector.SelectedObjectsListForReading.OfType<Pawn>().Where(p => p.IsColonistPlayerControlled).ToList();

        private static void PasteMenu(List<Pawn> pawns)
        {
            if (clip == null || pawns.Count == 0) return;
            Action<bool, bool, bool, bool> go = (w, s, po, a) =>
            {
                foreach (Pawn p in pawns)
                {
                    try { clip.PasteTo(p, w, s, po, a); }
                    catch (Exception e) { Log.Warning("[QoL Faster Work] paste to " + p.LabelShort + ": " + e.Message); }
                }
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                Q.Msg("QFW_Pasted".Translate(clip.name, pawns.Count));
            };
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("QFW_PasteAll".Translate(), () => go(true, true, true, true)),
                new FloatMenuOption("QFW_PasteWork".Translate(), () => go(true, false, false, false)),
                new FloatMenuOption("QFW_PasteSchedule".Translate(), () => go(false, true, false, false)),
                new FloatMenuOption("QFW_PastePolicies".Translate(), () => go(false, false, true, false)),
                new FloatMenuOption("QFW_PasteArea".Translate(), () => go(false, false, false, true)),
            };
            Find.WindowStack.Add(new FloatMenu(opts));
        }
    }

    // ------------------------------------------------------------------
    // Haul now: selected items go to storage right away, nearest free colonists carry them.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(ThingWithComps), "GetGizmos")]
    public static class Patch_ItemGizmos
    {
        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, ThingWithComps __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!QoLMod.S.haulNow || __instance.def.category != ThingCategory.Item || !Haulable(__instance)) yield break;
            Thing first = Find.Selector.SelectedObjectsListForReading.OfType<Thing>().FirstOrDefault(Haulable);
            if (first != __instance) yield break;
            yield return new Command_Action
            {
                defaultLabel = "QFW_HaulNow_Btn".Translate(),
                defaultDesc = "QFW_HaulNow_BtnDesc".Translate(),
                icon = Tex.Haul,
                Order = 2100f,
                action = () => HaulNow(Find.Selector.SelectedObjectsListForReading.OfType<Thing>().Where(Haulable).ToList())
            };
        }

        private static bool Haulable(Thing t) =>
            t != null && t.Spawned && t.def.EverHaulable && t.def.category == ThingCategory.Item && t.Map == Find.CurrentMap;

        public static void HaulNow(List<Thing> things)
        {
            if (things.Count == 0) return;
            Map map = things[0].Map;
            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned
                .Where(p => !p.Drafted && !p.Downed && !p.InMentalState && !p.WorkTagIsDisabled(WorkTags.Hauling) && p.jobs != null).ToList();
            if (pawns.Count == 0) { Q.Msg("QFW_HaulNoPawn".Translate(), null, true); return; }
            var load = new Dictionary<Pawn, int>();
            int ok = 0, noStore = 0;
            foreach (Thing t in things)
            {
                if (t.IsForbidden(Faction.OfPlayer)) t.SetForbidden(false, false);
                Pawn best = null; Job bestJob = null; float bestScore = float.MaxValue;
                foreach (Pawn p in pawns)
                {
                    if (!p.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly)) continue;
                    int l; load.TryGetValue(p, out l);
                    float score = l * 10000f + p.Position.DistanceToSquared(t.Position);
                    if (score >= bestScore) continue;
                    Job j = HaulAIUtility.HaulToStorageJob(p, t, true);
                    if (j == null) continue;
                    best = p; bestJob = j; bestScore = score;
                }
                if (best == null) { noStore++; continue; }
                int n; load.TryGetValue(best, out n);
                best.jobs.TryTakeOrderedJob(bestJob, JobTag.Misc, n > 0);
                load[best] = n + 1;
                ok++;
            }
            if (ok > 0) SoundDefOf.Tick_High.PlayOneShotOnCamera();
            if (noStore > 0) Q.Msg("QFW_HaulNoStore".Translate(noStore), null, true);
        }
    }

    // ------------------------------------------------------------------
    // Grow / shrink zone by one cell on every side.
    // ------------------------------------------------------------------
    [HarmonyPatch(typeof(Zone), "GetGizmos")]
    public static class Patch_ZoneGizmos
    {
        private static readonly Dictionary<Type, Designator_ZoneAdd> designators = new Dictionary<Type, Designator_ZoneAdd>();
        private static readonly AccessTools.FieldRef<Designator_ZoneAdd, Type> typeRef =
            AccessTools.FieldRefAccess<Designator_ZoneAdd, Type>("zoneTypeToPlace");

        public static IEnumerable<Gizmo> Postfix(IEnumerable<Gizmo> __result, Zone __instance)
        {
            foreach (Gizmo g in __result) yield return g;
            if (!QoLMod.S.zoneTools || __instance.Map != Find.CurrentMap || DesignatorFor(__instance.GetType()) == null) yield break;
            Zone z = __instance;
            yield return new Command_Action
            {
                defaultLabel = "QFW_ZoneGrow".Translate(),
                defaultDesc = "QFW_ZoneGrow_Desc".Translate(),
                icon = Tex.Grow,
                Order = 2200f,
                action = () => Grow(z)
            };
            yield return new Command_Action
            {
                defaultLabel = "QFW_ZoneShrink".Translate(),
                defaultDesc = "QFW_ZoneShrink_Desc".Translate(),
                icon = Tex.Shrink,
                Order = 2201f,
                action = () => Shrink(z)
            };
        }

        private static Designator_ZoneAdd DesignatorFor(Type zoneType)
        {
            Designator_ZoneAdd d;
            if (designators.TryGetValue(zoneType, out d)) return d;
            d = null;
            foreach (DesignationCategoryDef cat in DefDatabase<DesignationCategoryDef>.AllDefsListForReading)
            {
                foreach (Designator des in All(cat.AllResolvedDesignators))
                {
                    var za = des as Designator_ZoneAdd;
                    if (za == null || za.GetType().Name.Contains("Expand")) continue;
                    Type t;
                    try { t = typeRef(za); } catch { continue; }
                    if (t == zoneType) { d = za; break; }
                }
                if (d != null) break;
            }
            designators[zoneType] = d;
            return d;
        }

        private static IEnumerable<Designator> All(IEnumerable<Designator> list)
        {
            if (list == null) yield break;
            foreach (Designator d in list)
            {
                yield return d;
                if (d is Designator_Dropdown dd)
                    foreach (Designator e in dd.Elements) yield return e;
            }
        }

        private static void Grow(Zone z)
        {
            Map map = z.Map;
            Designator_ZoneAdd des = DesignatorFor(z.GetType());
            if (map == null || des == null) return;
            var add = new HashSet<IntVec3>();
            foreach (IntVec3 c in z.Cells)
                foreach (IntVec3 dir in GenAdj.CardinalDirections)
                {
                    IntVec3 n = c + dir;
                    if (!n.InBounds(map) || add.Contains(n) || map.zoneManager.ZoneAt(n) != null) continue;
                    bool ok;
                    try { ok = des.CanDesignateCell(n).Accepted; } catch { ok = false; }
                    if (ok) add.Add(n);
                }
            if (add.Count == 0) { Q.Msg("QFW_ZoneNoRoom".Translate(), null, true); return; }
            foreach (IntVec3 c in add) z.AddCell(c);
            SoundDefOf.Designate_ZoneAdd.PlayOneShotOnCamera();
        }

        private static void Shrink(Zone z)
        {
            Map map = z.Map;
            if (map == null) return;
            var border = z.Cells.Where(c => GenAdj.CardinalDirections.Any(d =>
            {
                IntVec3 n = c + d;
                return !n.InBounds(map) || map.zoneManager.ZoneAt(n) != z;
            })).ToList();
            if (border.Count >= z.Cells.Count) { Q.Msg("QFW_ZoneTooSmall".Translate(), null, true); return; }
            foreach (IntVec3 c in border) z.RemoveCell(c);
            try { z.CheckContiguous(); } catch { }
            SoundDefOf.Designate_ZoneDelete.PlayOneShotOnCamera();
        }
    }
}
