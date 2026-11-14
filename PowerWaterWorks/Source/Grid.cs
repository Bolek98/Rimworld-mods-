using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace PowerWaterWorks
{
    // ---------------- grid overview: production, use and storage of a power net ----------------
    public struct GridSample
    {
        public float gen, use, stored, cap;
    }

    public class MapComponent_GridStats : MapComponent
    {
        private readonly Dictionary<PowerNet, List<GridSample>> samples = new Dictionary<PowerNet, List<GridSample>>();

        public MapComponent_GridStats(Map map) : base(map) { }

        public static GridSample Now(PowerNet net)
        {
            var s = new GridSample();
            foreach (CompPowerTrader c in net.powerComps)
            {
                if (!c.PowerOn && !(c is CompPowerPlant)) continue;
                if (c.PowerOutput > 0f) s.gen += c.PowerOutput;
                else s.use -= c.PowerOutput;
            }
            foreach (CompPowerBattery b in net.batteryComps)
            {
                s.stored += b.StoredEnergy;
                s.cap += b.Props.storedEnergyMax;
            }
            return s;
        }

        public List<GridSample> History(PowerNet net) => net != null && samples.TryGetValue(net, out var l) ? l : new List<GridSample>();

        public override void MapComponentTick()
        {
            if (Find.TickManager.TicksGame % 2500 != 7) return;
            List<PowerNet> nets = map.powerNetManager.AllNetsListForReading;
            foreach (PowerNet gone in samples.Keys.Where(n => !nets.Contains(n)).ToList()) samples.Remove(gone);
            foreach (PowerNet net in nets)
            {
                if (!samples.TryGetValue(net, out var l)) samples[net] = l = new List<GridSample>();
                l.Add(Now(net));
                if (l.Count > 24) l.RemoveAt(0);
            }
        }
    }

    public class CompProperties_GridOverview : CompProperties
    {
        public CompProperties_GridOverview() { compClass = typeof(CompGridOverview); }
    }

    public class CompGridOverview : ThingComp
    {
        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            PowerNet net = parent.TryGetComp<CompPower>()?.PowerNet;
            if (net == null) yield break;
            yield return new Command_Action
            {
                defaultLabel = "PWW_GridOverview".Translate(),
                defaultDesc = "PWW_GridOverviewDesc".Translate(),
                icon = ThingDefOf.PowerConduit.uiIcon,
                action = () => Find.WindowStack.Add(new Window_Grid(parent))
            };
        }
    }

    public class Window_Grid : Window
    {
        private readonly Thing source;
        public override Vector2 InitialSize => new Vector2(560f, 420f);

        public Window_Grid(Thing t)
        {
            source = t;
            doCloseX = true;
            draggable = true;
            preventCameraMotion = false;
            closeOnClickedOutside = true;
        }

        public override void DoWindowContents(Rect r)
        {
            PowerNet net = source.TryGetComp<CompPower>()?.PowerNet;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "PWW_GridOverview".Translate());
            Text.Font = GameFont.Small;
            if (net == null || !source.Spawned) { Widgets.Label(new Rect(r.x, r.y + 40f, r.width, 30f), "PWW_GridNone".Translate()); return; }
            GridSample now = MapComponent_GridStats.Now(net);
            float y = r.y + 40f;
            Line(ref y, r, "PWW_GridGen".Translate(now.gen.ToString("0")), new Color(0.45f, 0.85f, 0.45f));
            Line(ref y, r, "PWW_GridUse".Translate(now.use.ToString("0")), new Color(0.95f, 0.6f, 0.35f));
            float bal = now.gen - now.use;
            Line(ref y, r, "PWW_GridBalance".Translate((bal >= 0f ? "+" : "") + bal.ToString("0")), bal >= 0f ? Color.white : new Color(1f, 0.4f, 0.35f));
            Line(ref y, r, "PWW_GridStored".Translate(now.stored.ToString("0"), now.cap.ToString("0")), new Color(0.5f, 0.75f, 1f));
            y += 8f;
            List<GridSample> h = source.Map.GetComponent<MapComponent_GridStats>()?.History(net) ?? new List<GridSample>();
            Rect g = new Rect(r.x, y, r.width, r.yMax - y - 4f);
            Widgets.DrawBoxSolid(g, new Color(0.08f, 0.08f, 0.09f));
            if (h.Count < 2)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(g, "PWW_GridWait".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
                return;
            }
            float max = Mathf.Max(1f, h.Max(s => Mathf.Max(s.gen, s.use)));
            Plot(g, h.Select(s => s.gen / max).ToList(), new Color(0.45f, 0.85f, 0.45f));
            Plot(g, h.Select(s => s.use / max).ToList(), new Color(0.95f, 0.6f, 0.35f));
            float capMax = Mathf.Max(1f, h.Max(s => s.cap));
            Plot(g, h.Select(s => s.stored / capMax).ToList(), new Color(0.5f, 0.75f, 1f));
            Text.Font = GameFont.Tiny;
            GUI.color = Color.gray;
            Widgets.Label(new Rect(g.x + 4f, g.y + 2f, 200f, 18f), max.ToString("0") + " W");
            Widgets.Label(new Rect(g.x + 4f, g.yMax - 18f, 300f, 18f), "PWW_Grid24h".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void Line(ref float y, Rect r, string text, Color c)
        {
            GUI.color = c;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), text);
            GUI.color = Color.white;
            y += 24f;
        }

        private static void Plot(Rect g, List<float> v, Color c)
        {
            float step = g.width / (v.Count - 1);
            for (int i = 1; i < v.Count; i++)
            {
                Vector2 a = new Vector2(g.x + (i - 1) * step, g.yMax - 6f - Mathf.Clamp01(v[i - 1]) * (g.height - 26f));
                Vector2 b = new Vector2(g.x + i * step, g.yMax - 6f - Mathf.Clamp01(v[i]) * (g.height - 26f));
                Widgets.DrawLine(a, b, c, 2f);
            }
        }
    }
}
