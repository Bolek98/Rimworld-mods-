using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.4.0: "what is expensive on the map" – counts what the game has to update every tick
    // on the current map, with simple tips. Read-only, nothing is changed.
    public class Dialog_MapCost : Window
    {
        private class Row { public string label; public int count; public string tip; public bool warn; }

        private readonly List<Row> rows = new List<Row>();
        private readonly List<KeyValuePair<string, int>> topTicking = new List<KeyValuePair<string, int>>();
        private readonly List<string> tips = new List<string>();
        private Vector2 scroll;
        private string mapName;

        public override Vector2 InitialSize => new Vector2(760f, 620f);

        public Dialog_MapCost()
        {
            doCloseX = true;
            doCloseButton = false;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = true;
            Build();
        }

        private void Add(string key, int count, int warnAt, string tipKey = null)
        {
            bool warn = warnAt > 0 && count >= warnAt;
            rows.Add(new Row { label = key.Translate(), count = count, warn = warn, tip = tipKey });
            if (warn && tipKey != null)
                tips.Add(tipKey.Translate(count));
        }

        private void Build()
        {
            Map map = Find.CurrentMap;
            if (map == null)
                return;
            mapName = map.Parent?.LabelCap ?? map.ToString();
            try
            {
                var pawns = map.mapPawns.AllPawnsSpawned;
                int colonists = pawns.Count(p => p.IsColonist);
                int animalsOwn = pawns.Count(p => p.RaceProps.Animal && p.Faction == Faction.OfPlayer);
                int animalsWild = pawns.Count(p => p.RaceProps.Animal && p.Faction == null);
                int mechs = pawns.Count(p => p.RaceProps.IsMechanoid);
                int hostile = pawns.Count(p => p.HostileTo(Faction.OfPlayer));
                int other = pawns.Count - colonists - animalsOwn - animalsWild;

                List<Thing> all = map.listerThings.AllThings;
                int ticking = 0, rare = 0, longT = 0;
                var byDef = new Dictionary<ThingDef, int>();
                foreach (Thing t in all)
                {
                    switch (t.def.tickerType)
                    {
                        case TickerType.Normal:
                            ticking++;
                            if (!(t is Pawn))
                            {
                                byDef.TryGetValue(t.def, out int n);
                                byDef[t.def] = n + 1;
                            }
                            break;
                        case TickerType.Rare: rare++; break;
                        case TickerType.Long: longT++; break;
                    }
                }
                foreach (var kv in byDef.OrderByDescending(k => k.Value).Take(12))
                    topTicking.Add(new KeyValuePair<string, int>(kv.Key.LabelCap.ToString() + " (" + (kv.Key.modContentPack?.Name ?? "?") + ")", kv.Value));

                int filth = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count;
                int plants = map.listerThings.ThingsInGroup(ThingRequestGroup.Plant).Count;
                int corpses = map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).Count;
                int loose = 0;
                foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
                    if (t.Spawned && !t.IsInValidStorage() && !(t is Corpse))
                        loose++;
                int fires = map.listerThings.ThingsOfDef(ThingDefOf.Fire).Count;
                int buildings = map.listerBuildings.allBuildingsColonist.Count;
                int worldPawns = Find.WorldPawns?.AllPawnsAlive?.Count ?? 0;

                Add("GT_MC_Colonists", colonists, 0);
                Add("GT_MC_AnimalsOwn", animalsOwn, 60, "GT_MC_TipAnimals");
                Add("GT_MC_AnimalsWild", animalsWild, 80, "GT_MC_TipWild");
                Add("GT_MC_Mechs", mechs, 0);
                Add("GT_MC_Hostile", hostile, 0);
                Add("GT_MC_OtherPawns", Math.Max(0, other - mechs), 0);
                Add("GT_MC_Ticking", ticking, 4000, "GT_MC_TipTicking");
                Add("GT_MC_Rare", rare, 0);
                Add("GT_MC_Long", longT, 0);
                Add("GT_MC_Filth", filth, 600, "GT_MC_TipFilth");
                Add("GT_MC_Loose", loose, 1500, "GT_MC_TipLoose");
                Add("GT_MC_Corpses", corpses, 40, "GT_MC_TipCorpses");
                Add("GT_MC_Plants", plants, 0);
                Add("GT_MC_Fires", fires, 30, "GT_MC_TipFires");
                Add("GT_MC_Buildings", buildings, 0);
                Add("GT_MC_WorldPawns", worldPawns, 3000, "GT_MC_TipWorldPawns");
                Add("GT_MC_Things", all.Count, 0);
            }
            catch (Exception ex)
            {
                MLog.Warn("[Smooth FPS] map cost: " + ex.Message);
            }
            if (tips.Count == 0)
                tips.Add("GT_MC_AllGood".Translate());
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, inRect.y, inRect.width, 34f), "GT_MC_Title".Translate());
            Text.Font = GameFont.Small;
            if (mapName == null)
            {
                Widgets.Label(new Rect(inRect.x, inRect.y + 40f, inRect.width, 30f), "GT_BenchNoGame".Translate());
                return;
            }
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(inRect.x, inRect.y + 34f, inRect.width, 24f), "GT_MC_Sub".Translate(mapName));
            GUI.color = Color.white;

            float colW = (inRect.width - 20f) / 2f;
            Rect outR = new Rect(inRect.x, inRect.y + 62f, inRect.width, inRect.height - 62f - 40f);
            float leftH = rows.Count * 26f + 10f;
            float rightH = 30f + topTicking.Count * 26f + 20f + tips.Count * 64f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(leftH, rightH));
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0f;
            foreach (Row row in rows)
            {
                Rect r = new Rect(0f, y, colW, 24f);
                Widgets.DrawHighlightIfMouseover(r);
                if (row.warn) GUI.color = MUI.Warn;
                Widgets.Label(new Rect(r.x + 4f, r.y, r.width - 80f, 24f), row.label);
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(r.xMax - 80f, r.y, 76f, 24f), row.count.ToString("N0"));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                y += 26f;
            }
            float x2 = colW + 20f;
            float y2 = 0f;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(x2, y2, colW, 26f), "GT_MC_Top".Translate());
            GUI.color = Color.white;
            y2 += 28f;
            Text.Font = GameFont.Tiny;
            foreach (var kv in topTicking)
            {
                Widgets.Label(new Rect(x2, y2, colW - 60f, 24f), kv.Key);
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(x2 + colW - 60f, y2, 56f, 24f), kv.Value.ToString("N0"));
                Text.Anchor = TextAnchor.UpperLeft;
                y2 += 24f;
            }
            Text.Font = GameFont.Small;
            y2 += 12f;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(x2, y2, colW, 26f), "GT_MC_Tips".Translate());
            GUI.color = Color.white;
            y2 += 28f;
            foreach (string t in tips)
            {
                float h = Text.CalcHeight("• " + t, colW);
                Widgets.Label(new Rect(x2, y2, colW, h), "• " + t);
                y2 += h + 6f;
            }
            Widgets.EndScrollView();
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 34f, 200f, 32f), "GT_MC_Refresh".Translate()))
            {
                rows.Clear(); topTicking.Clear(); tips.Clear();
                Build();
            }
            // 1.4.0: filth outside the home area – nobody cleans it, the game still keeps it
            Map map = Find.CurrentMap;
            if (map != null)
            {
                int outside = map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Count(f => !map.areaManager.Home[f.Position]);
                if (outside > 0 && Widgets.ButtonText(new Rect(inRect.x + 210f, inRect.yMax - 34f, 300f, 32f), "GT_MC_CleanFilth".Translate(outside)))
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("GT_MC_CleanFilthConfirm".Translate(outside), () =>
                    {
                        foreach (Thing f in map.listerThings.ThingsInGroup(ThingRequestGroup.Filth).Where(f => !map.areaManager.Home[f.Position]).ToList())
                            if (!f.Destroyed) f.Destroy();
                        rows.Clear(); topTicking.Clear(); tips.Clear();
                        Build();
                    }, destructive: true));
            }
        }
    }

    // 1.4.0: laptop mode – lower FPS, fewer effects; always or only on battery
    public static class Laptop
    {
        private static float nextCheck;
        private static bool battery;

        public static bool OnBattery()
        {
            if (Time.realtimeSinceStartup < nextCheck)
                return battery;
            nextCheck = Time.realtimeSinceStartup + 10f;
            try { battery = SystemInfo.batteryStatus == BatteryStatus.Discharging; }
            catch { battery = false; }
            return battery;
        }

        public static bool Active(GfxSettings s)
        {
            if (s == null) return false;
            return s.laptop == 1 || (s.laptop == 2 && OnBattery());
        }

        public static float EffectFactor(GfxSettings s) => Active(s) ? 0.6f : 1f;
    }
}
