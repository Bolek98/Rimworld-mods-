using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PowerWaterWorks
{
    // ---------------- hydro plant: weaker when the river freezes ----------------
    public class CompPowerPlantHydro : CompPowerPlant
    {
        public bool Frozen => parent.Spawned && PWWMod.S.hydroWinter && parent.Map.mapTemperature.OutdoorTemp < -2f;

        protected override float DesiredPowerOutput => base.DesiredPowerOutput * (Frozen ? 0.4f : 1f);

        public override string CompInspectStringExtra()
        {
            string s = base.CompInspectStringExtra();
            if (Frozen) s = (s.NullOrEmpty() ? "" : s + "\n") + "PWW_HydroFrozen".Translate();
            return s;
        }
    }

    public class PlaceWorker_Hydro : PlaceWorker
    {
        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            int water = 0;
            foreach (IntVec3 c in GenAdj.OccupiedRect(loc, rot, def.Size))
            {
                if (!c.InBounds(map)) return false;
                List<TerrainAffordanceDef> aff = c.GetAffordances(map);
                if (aff.Contains(TerrainAffordanceDefOf.MovingFluid)) water++;
                else if (!aff.Contains(TerrainAffordanceDefOf.Medium)) return new AcceptanceReport("PWW_HydroGround".Translate());
            }
            return water >= 3 ? AcceptanceReport.WasAccepted : new AcceptanceReport("MustBeOnMovingWater".Translate());
        }
    }

    // ---------------- desalination: near salt water ----------------
    public class PlaceWorker_NearSaltWater : PlaceWorker
    {
        public static bool Salt(TerrainDef t) => t != null && (t.defName.IndexOf("Ocean", StringComparison.OrdinalIgnoreCase) >= 0 || t.defName.IndexOf("Sea", StringComparison.OrdinalIgnoreCase) >= 0 || t.defName.IndexOf("Salt", StringComparison.OrdinalIgnoreCase) >= 0);

        public override AcceptanceReport AllowsPlacing(BuildableDef def, IntVec3 loc, Rot4 rot, Map map, Thing thingToIgnore = null, Thing thing = null)
        {
            CellRect r = GenAdj.OccupiedRect(loc, rot, def.Size).ExpandedBy(3);
            foreach (IntVec3 c in r)
                if (c.InBounds(map) && Salt(c.GetTerrain(map))) return true;
            return new AcceptanceReport("PWW_NeedSea".Translate());
        }
    }

    // ---------------- fission reactor: heat, cooling towers, scram ----------------
    public class CompProperties_Reactor : CompProperties
    {
        public float heatPerHour = 10f;      // without cooling
        public float coolingRadius = 8f;
        public CompProperties_Reactor() { compClass = typeof(CompReactor); }
    }

    public class CompProperties_CoolingTower : CompProperties
    {
        public float cooling = 12f;          // heat units per hour
        public CompProperties_CoolingTower() { compClass = typeof(CompCoolingTower); }
    }

    public class CompCoolingTower : ThingComp
    {
        public CompProperties_CoolingTower Props => (CompProperties_CoolingTower)props;
        public bool Working => parent.Spawned && (parent.TryGetComp<CompPowerTrader>()?.PowerOn ?? true) && !(parent.TryGetComp<CompBreakdownable>()?.BrokenDown ?? false);
    }

    public class CompReactor : ThingComp
    {
        public float heat;
        private static readonly Texture2D Scram = ContentFinder<Texture2D>.Get("UI/Commands/DesirePower", false) ?? BaseContent.BadTex;
        public CompProperties_Reactor Props => (CompProperties_Reactor)props;

        private CompFlickable Flick => parent.TryGetComp<CompFlickable>();
        private CompRefuelable Fuel => parent.TryGetComp<CompRefuelable>();
        public bool Running => (Flick?.SwitchIsOn ?? true) && (Fuel?.HasFuel ?? true);

        public float Cooling()
        {
            float c = 0f;
            if (!parent.Spawned) return 0f;
            foreach (Building b in parent.Map.listerBuildings.allBuildingsColonist)
            {
                CompCoolingTower t = b.TryGetComp<CompCoolingTower>();
                if (t != null && t.Working && b.Position.InHorDistOf(parent.Position, Props.coolingRadius + 2f)) c += t.Props.cooling;
            }
            return c;
        }

        public override void PostExposeData() => Scribe_Values.Look(ref heat, "pwwHeat", 0f);

        public override void CompTick()
        {
            if (!parent.IsHashIntervalTick(250)) return;
            float perInterval = 250f / 2500f;
            float delta = Running ? (Props.heatPerHour - Cooling()) * perInterval : -20f * perInterval;
            heat = Mathf.Clamp(heat + delta, 0f, 100f);
            if (heat >= 100f) Overheat();
        }

        private void Overheat()
        {
            if (PWWMod.S.meltdown)
            {
                Map map = parent.Map;
                IntVec3 pos = parent.Position;
                Find.LetterStack.ReceiveLetter("PWW_MeltdownLabel".Translate(), "PWW_Meltdown".Translate(), LetterDefOf.ThreatBig, new TargetInfo(pos, map));
                GenExplosion.DoExplosion(pos, map, 9.9f, DamageDefOf.Bomb, parent, chanceToStartFire: 0.4f);
                try
                {
                    GameCondition c = GameConditionMaker.MakeCondition(GameConditionDefOf.ToxicFallout, 60000 * 3);
                    map.gameConditionManager.RegisterCondition(c);
                }
                catch { }
                if (!parent.Destroyed) parent.Destroy(DestroyMode.KillFinalize);
                return;
            }
            if (Flick != null) Flick.SwitchIsOn = false;
            heat = 90f;
            Messages.Message("PWW_Scrammed".Translate(), parent, MessageTypeDefOf.NegativeEvent);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            yield return new Gizmo_Heat { reactor = this };
            if (Flick != null && Flick.SwitchIsOn)
                yield return new Command_Action
                {
                    defaultLabel = "PWW_Scram".Translate(),
                    defaultDesc = "PWW_ScramDesc".Translate(),
                    icon = Scram,
                    action = () => { Flick.SwitchIsOn = false; Messages.Message("PWW_Scrammed".Translate(), parent, MessageTypeDefOf.CautionInput, false); }
                };
        }

        public override string CompInspectStringExtra() =>
            "PWW_ReactorHeat".Translate(heat.ToString("0"), Cooling().ToString("0"), Props.heatPerHour.ToString("0"));

        public override void PostDrawExtraSelectionOverlays()
        {
            GenDraw.DrawRadiusRing(parent.Position, Props.coolingRadius + 2f);
        }
    }

    public class Gizmo_Heat : Gizmo
    {
        public CompReactor reactor;
        public Gizmo_Heat() { Order = -100f; }
        public override float GetWidth(float maxWidth) => 140f;

        public override GizmoResult GizmoOnGUI(Vector2 topLeft, float maxWidth, GizmoRenderParms parms)
        {
            Rect r = new Rect(topLeft.x, topLeft.y, GetWidth(maxWidth), 75f);
            Widgets.DrawWindowBackground(r);
            Rect inner = r.ContractedBy(6f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 20f), "PWW_HeatLabel".Translate());
            Rect bar = new Rect(inner.x, inner.y + 24f, inner.width, 22f);
            float f = reactor.heat / 100f;
            Color c = f < 0.6f ? new Color(0.3f, 0.75f, 0.35f) : f < 0.85f ? new Color(0.95f, 0.7f, 0.2f) : new Color(0.95f, 0.3f, 0.25f);
            Widgets.FillableBar(bar, f, SolidColorMaterials.NewSolidColorTexture(c));
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(bar, reactor.heat.ToString("0") + " %");
            Text.Anchor = TextAnchor.UpperLeft;
            Text.Font = GameFont.Small;
            return new GizmoResult(GizmoState.Clear);
        }
    }

    public class PlaceWorker_CoolingRadius : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            GenDraw.DrawRadiusRing(center, 10f);
        }
    }

    // ---------------- diesel: auto-start by stored energy ----------------
    public class CompProperties_AutoStart : CompProperties
    {
        public CompProperties_AutoStart() { compClass = typeof(CompAutoStart); }
    }

    public class CompAutoStart : ThingComp
    {
        public bool auto = true;
        private static Texture2D icon;

        public override void PostExposeData() => Scribe_Values.Look(ref auto, "pwwAuto", true);

        public static float StoredPct(PowerNet net, out bool hasBatteries)
        {
            float cur = 0f, max = 0f;
            foreach (CompPowerBattery b in net.batteryComps)
            {
                cur += b.StoredEnergy;
                max += b.Props.storedEnergyMax;
            }
            hasBatteries = max > 0f;
            return max > 0f ? cur / max : 0f;
        }

        public override void CompTick()
        {
            if (!auto || !parent.IsHashIntervalTick(250)) return;
            CompFlickable f = parent.TryGetComp<CompFlickable>();
            PowerNet net = parent.TryGetComp<CompPowerTrader>()?.PowerNet;
            if (f == null || net == null) return;
            float pct = StoredPct(net, out bool bat);
            bool want;
            if (!bat) want = net.CurrentEnergyGainRate() < 0f || f.SwitchIsOn && net.CurrentEnergyGainRate() < 0.5f;
            else if (pct < PWWMod.S.autoLow) want = true;
            else if (pct > PWWMod.S.autoHigh) want = false;
            else return;
            if (f.SwitchIsOn != want) f.SwitchIsOn = want;
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            if (icon == null) icon = ContentFinder<Texture2D>.Get("UI/Commands/TryReconnect", false) ?? BaseContent.BadTex;
            yield return new Command_Toggle
            {
                defaultLabel = "PWW_AutoStart".Translate(),
                defaultDesc = "PWW_AutoStartDesc".Translate((PWWMod.S.autoLow * 100f).ToString("0"), (PWWMod.S.autoHigh * 100f).ToString("0")),
                icon = icon,
                isActive = () => auto,
                toggleAction = () => auto = !auto
            };
        }
    }

    // ---------------- flywheel: loses 10 % of its energy per day ----------------
    public class CompProperties_SelfDischarge : CompProperties
    {
        public float perDay = 0.1f;
        public CompProperties_SelfDischarge() { compClass = typeof(CompSelfDischarge); }
    }

    public class CompSelfDischarge : ThingComp
    {
        public override void CompTick()
        {
            if (!parent.IsHashIntervalTick(2500)) return;
            CompPowerBattery b = parent.TryGetComp<CompPowerBattery>();
            if (b == null || b.StoredEnergy <= 0f) return;
            float loss = b.StoredEnergy * ((CompProperties_SelfDischarge)props).perDay * 2500f / 60000f;
            b.DrawPower(Mathf.Min(loss, b.StoredEnergy));
        }
    }

    // ---------------- automatic stonecutter: chunks next to it become blocks ----------------
    public class CompProperties_AutoStonecutter : CompProperties
    {
        public int ticksPerChunk = 2500;
        public CompProperties_AutoStonecutter() { compClass = typeof(CompAutoStonecutter); }
    }

    public class CompAutoStonecutter : ThingComp
    {
        private int progress;
        public CompProperties_AutoStonecutter Props => (CompProperties_AutoStonecutter)props;
        private bool Powered => parent.TryGetComp<CompPowerTrader>()?.PowerOn ?? true;

        public override void PostExposeData() => Scribe_Values.Look(ref progress, "pwwProgress", 0);

        private Thing FindChunk()
        {
            foreach (IntVec3 c in GenAdj.CellsAdjacent8Way(parent))
            {
                if (!c.InBounds(parent.Map)) continue;
                foreach (Thing t in c.GetThingList(parent.Map))
                    if (t.def.category == ThingCategory.Item && t.def.butcherProducts != null && t.def.butcherProducts.Count > 0
                        && t.def.thingCategories != null && t.def.thingCategories.Contains(ThingCategoryDefOf.StoneChunks) && !t.IsForbidden(Faction.OfPlayer))
                        return t;
            }
            return null;
        }

        public override void CompTick()
        {
            if (!parent.IsHashIntervalTick(60) || !Powered) return;
            Thing chunk = FindChunk();
            if (chunk == null) { progress = 0; return; }
            progress += 60;
            if (progress < Props.ticksPerChunk) return;
            progress = 0;
            List<ThingDefCountClass> prod = chunk.def.butcherProducts;
            IntVec3 at = chunk.Position;
            chunk.SplitOff(1).Destroy();
            foreach (ThingDefCountClass p in prod)
            {
                Thing block = ThingMaker.MakeThing(p.thingDef);
                block.stackCount = p.count;
                GenPlace.TryPlaceThing(block, at, parent.Map, ThingPlaceMode.Near);
            }
        }

        public override string CompInspectStringExtra() =>
            FindChunk() == null ? "PWW_StonecutterIdle".Translate().ToString() : "PWW_StonecutterWork".Translate(((float)progress / Props.ticksPerChunk).ToStringPercent()).ToString();
    }

    // ---------------- sprinkler: plants around grow faster ----------------
    public class CompProperties_Sprinkler : CompProperties
    {
        public float radius = 5.9f;
        public float bonus = 0.15f;
        public CompProperties_Sprinkler() { compClass = typeof(CompSprinkler); }
    }

    public class CompSprinkler : ThingComp
    {
        public CompProperties_Sprinkler Props => (CompProperties_Sprinkler)props;

        public override void CompTick()
        {
            if (!parent.IsHashIntervalTick(2500)) return;
            if (!(parent.TryGetComp<CompPowerTrader>()?.PowerOn ?? true)) return;
            Map map = parent.Map;
            foreach (IntVec3 c in GenRadial.RadialCellsAround(parent.Position, Props.radius, true))
            {
                if (!c.InBounds(map)) continue;
                Plant p = c.GetPlant(map);
                if (p == null || p.LifeStage != PlantLifeStage.Growing || p.def.plant.growDays <= 0f) continue;
                float perTick = 1f / (60000f * p.def.plant.growDays) * p.GrowthRate;
                if (perTick <= 0f) continue;
                p.Growth += perTick * 2500f * Props.bonus;
            }
        }

        public override void PostDrawExtraSelectionOverlays() => GenDraw.DrawRadiusRing(parent.Position, Props.radius);
    }

    public class PlaceWorker_SprinklerRadius : PlaceWorker
    {
        public override void DrawGhost(ThingDef def, IntVec3 center, Rot4 rot, Color ghostCol, Thing thing = null)
        {
            CompProperties_Sprinkler p = def.GetCompProperties<CompProperties_Sprinkler>();
            if (p != null) GenDraw.DrawRadiusRing(center, p.radius);
        }
    }
}
