using System;
using System.Collections.Generic;
using System.Linq;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AutoTurrets
{
    // ------------------------------------------------------------------
    // 1.2.0 Turrets in walls: for every turret picked in the settings the
    // game gets a second version "… (in wall)". It is built straight over a
    // wall (or a smoothed rock wall), replaces that wall piece and works as
    // a wall itself (blocks movement, holds the roof, keeps the room sealed)
    // while it shoots out. The original turret is not changed at all.
    // The copies are made in the XML before the game reads the defs, so a
    // change in the settings applies after a restart.
    // ------------------------------------------------------------------
    public static class WallTurrets
    {
        public const string Suffix = "_ATWall";

        // small turrets (up to 2×2) are recommended, big ones (3×3 mortars …) not
        public static bool Recommended(int area) => area <= 4;

        public static bool Picked(string defName, int area)
        {
            ATSettings s = ATMod.S;
            if (s?.wallPick != null && s.wallPick.TryGetValue(defName, out bool v))
                return v;
            return Recommended(area);
        }

        private static List<ThingDef> candidates;
        public static List<ThingDef> Candidates
        {
            get
            {
                if (candidates == null)
                    candidates = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.thingClass != null && typeof(Building_Turret).IsAssignableFrom(d.thingClass)
                                    && d.designationCategory != null && !d.defName.EndsWith(Suffix))
                        .OrderBy(d => d.label ?? d.defName).ToList();
                return candidates;
            }
        }

        // ---------------- XML stage ----------------
        public static void MakeCopies(XmlDocument xml, Dictionary<XmlNode, LoadableXmlAsset> assets)
        {
            ATSettings s = ATMod.S;
            startEnabled = s != null && s.wallTurrets;
            if (s == null || !s.wallTurrets || xml?.DocumentElement == null)
                return;
            try
            {
                var all = xml.DocumentElement.ChildNodes.OfType<XmlElement>().Where(e => e.Name == "ThingDef").ToList();
                var byName = new Dictionary<string, XmlElement>();
                foreach (XmlElement e in all)
                {
                    string n = e.GetAttribute("Name");
                    if (!n.NullOrEmpty()) byName[n] = e;
                }
                var defNames = new HashSet<string>(all.Select(e => e["defName"]?.InnerText).Where(x => x != null));

                string Find(XmlElement e, string field)
                {
                    for (int guard = 0; e != null && guard < 30; guard++)
                    {
                        XmlElement c = e[field];
                        if (c != null) return c.InnerText?.Trim();
                        string p = e.GetAttribute("ParentName");
                        if (p.NullOrEmpty() || !byName.TryGetValue(p, out e)) return null;
                    }
                    return null;
                }

                int made = 0;
                foreach (XmlElement e in all)
                {
                    if (e.GetAttribute("Abstract").ToLowerInvariant() == "true") continue;
                    string defName = e["defName"]?.InnerText?.Trim();
                    if (defName.NullOrEmpty() || defName.EndsWith(Suffix) || defNames.Contains(defName + Suffix)) continue;
                    if (Find(e, "designationCategory").NullOrEmpty()) continue;
                    string cls = Find(e, "thingClass");
                    if (cls.NullOrEmpty()) continue;
                    Type t = GenTypes.GetTypeInAnyAssembly(cls);
                    if (t == null || !typeof(Building_Turret).IsAssignableFrom(t)) continue;
                    IntVec2 size = new IntVec2(1, 1);
                    string sz = Find(e, "size");
                    if (!sz.NullOrEmpty()) { try { size = IntVec2.FromString(sz); } catch { } }
                    if (!Picked(defName, size.x * size.z)) continue;

                    var c = (XmlElement)e.CloneNode(true);
                    c.RemoveAttribute("Name");
                    c.RemoveAttribute("Abstract");
                    Set(c, "defName", defName + Suffix);
                    Set(c, "passability", "Impassable");
                    Set(c, "fillPercent", "1");
                    Set(c, "holdsRoof", "true");
                    Set(c, "tradeability", "None");
                    Empty(c, "thingSetMakerTags");
                    XmlElement b = c["building"];
                    if (b == null) { b = xml.CreateElement("building"); c.AppendChild(b); }
                    Set(b, "canPlaceOverWall", "true");
                    Empty(b, "buildingTags");     // raider and quest bases keep using the normal turrets
                    e.ParentNode.InsertAfter(c, e);
                    if (assets != null && assets.TryGetValue(e, out LoadableXmlAsset a))
                        assets[c] = a;
                    made++;
                    startMade.Add(defName);
                }
                if (made > 0)
                    Log.Message("[Auto Turrets] wall versions of " + made + " turrets added.");
            }
            catch (Exception ex)
            {
                Log.Error("[Auto Turrets] could not make wall turrets: " + ex);
            }
        }

        private static void Set(XmlElement parent, string name, string value)
        {
            foreach (XmlNode old in parent.ChildNodes.OfType<XmlElement>().Where(x => x.Name == name).ToList())
                parent.RemoveChild(old);
            XmlElement n = parent.OwnerDocument.CreateElement(name);
            n.InnerText = value;
            parent.AppendChild(n);
        }

        private static void Empty(XmlElement parent, string name)
        {
            foreach (XmlNode old in parent.ChildNodes.OfType<XmlElement>().Where(x => x.Name == name).ToList())
                parent.RemoveChild(old);
            XmlElement n = parent.OwnerDocument.CreateElement(name);
            n.SetAttribute("Inherit", "False");
            parent.AppendChild(n);
        }

        // ---------------- after load: names and walls ----------------
        public static void Finish()
        {
            ATSettings s = ATMod.S;
            if (s == null || !s.wallTurrets)
                return;
            try
            {
                // our walls (and walls from mods) may be built over
                foreach (ThingDef d in DefDatabase<ThingDef>.AllDefsListForReading)
                    if (d.building != null && (d == ThingDefOf.Wall || (d.IsWall && d.BuildableByPlayer)))
                        d.building.isPlaceOverableWall = true;

                foreach (ThingDef w in DefDatabase<ThingDef>.AllDefsListForReading.Where(d => d.defName.EndsWith(Suffix)).ToList())
                {
                    ThingDef orig = DefDatabase<ThingDef>.GetNamedSilentFail(w.defName.Substring(0, w.defName.Length - Suffix.Length));
                    if (orig == null) continue;
                    w.label = "AT_WallLabel".Translate(orig.label);
                    w.description = (orig.description ?? "") + "\n\n" + "AT_WallDescExtra".Translate();
                    if (w.blueprintDef != null) w.blueprintDef.label = w.label + "BlueprintLabelExtra".Translate();
                    if (w.installBlueprintDef != null) w.installBlueprintDef.label = w.label + "BlueprintLabelExtra".Translate();
                    if (w.frameDef != null) w.frameDef.label = w.label + "FrameLabelExtra".Translate();
                    w.ClearCachedData();
                    w.blueprintDef?.ClearCachedData();
                    w.installBlueprintDef?.ClearCachedData();
                    w.frameDef?.ClearCachedData();
                }
            }
            catch (Exception ex)
            {
                Log.Error("[Auto Turrets] wall turrets: " + ex);
            }
        }

        // ---------------- settings list ----------------
        // what this game session was started with (wall versions exist only for these)
        public static bool startEnabled;
        public static readonly HashSet<string> startMade = new HashSet<string>();

        private static string filter = "";
        private static string flash;
        private static float flashUntil;

        private static readonly Color CardOn = new Color(0.20f, 0.32f, 0.22f, 0.55f);
        private static readonly Color CardOff = new Color(0.16f, 0.16f, 0.16f, 0.55f);
        private static readonly Color Edge = new Color(0.35f, 0.35f, 0.35f, 0.8f);
        private static readonly Color EdgeOn = new Color(0.45f, 0.75f, 0.45f, 0.9f);
        private static readonly Color Gold = new Color(1f, 0.82f, 0.3f);

        private static void SetPick(ThingDef d, bool on)
        {
            int area = d.size.x * d.size.z;
            if (on == Recommended(area)) ATMod.S.wallPick.Remove(d.defName);
            else ATMod.S.wallPick[d.defName] = on;
        }

        private static void Flash(string text)
        {
            flash = text;
            flashUntil = Time.realtimeSinceStartup + 3f;
        }

        public static bool RestartNeeded()
        {
            ATSettings s = ATMod.S;
            if (s.wallTurrets != startEnabled) return true;
            if (!s.wallTurrets) return false;
            foreach (ThingDef d in Candidates)
                if (Picked(d.defName, d.size.x * d.size.z) != startMade.Contains(d.defName))
                    return true;
            return false;
        }

        public static void DrawList(Listing_Standard l)
        {
            ATSettings s = ATMod.S;
            if (s.wallPick == null) s.wallPick = new Dictionary<string, bool>();
            List<ThingDef> all = Candidates;
            if (all.Count == 0)
            {
                l.Label("AT_WallNone".Translate());
                return;
            }
            int picked = all.Count(d => Picked(d.defName, d.size.x * d.size.z));

            // buttons + counter
            Rect row = l.GetRect(30f);
            float bw = Mathf.Min(170f, (row.width - 200f) / 3f);
            Rect b1 = new Rect(row.x, row.y, bw, 28f);
            Rect b2 = new Rect(b1.xMax + 6f, row.y, bw, 28f);
            Rect b3 = new Rect(b2.xMax + 6f, row.y, bw, 28f);
            if (Widgets.ButtonText(b1, "AT_WallRecommended".Translate()))
            {
                s.wallPick.Clear();
                Flash("AT_WallRecDone".Translate(all.Count(d => Recommended(d.size.x * d.size.z))));
            }
            if (Widgets.ButtonText(b2, "AT_WallAll".Translate()))
            {
                foreach (ThingDef d in all) SetPick(d, true);
                Flash("AT_WallAllDone".Translate(all.Count));
            }
            if (Widgets.ButtonText(b3, "AT_WallClear".Translate()))
            {
                foreach (ThingDef d in all) SetPick(d, false);
                Flash("AT_WallClearDone".Translate());
            }
            Text.Anchor = TextAnchor.MiddleRight;
            Widgets.Label(new Rect(b3.xMax + 6f, row.y, row.xMax - b3.xMax - 6f, 28f), "AT_WallCount".Translate(picked, all.Count));
            Text.Anchor = TextAnchor.UpperLeft;

            // feedback after a button
            if (flash != null && Time.realtimeSinceStartup < flashUntil)
            {
                GUI.color = new Color(0.55f, 0.9f, 0.55f);
                l.Label("✓ " + flash);
                GUI.color = Color.white;
            }

            // search
            Rect sr = l.GetRect(28f);
            Widgets.Label(new Rect(sr.x, sr.y + 3f, 80f, 24f), "AT_WallSearch".Translate());
            filter = Widgets.TextField(new Rect(sr.x + 80f, sr.y, Mathf.Min(300f, sr.width - 80f), 26f), filter ?? "");
            l.Gap(4f);

            List<ThingDef> shown = filter.NullOrEmpty() ? all
                : all.Where(d => (d.label ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                              || (d.modContentPack?.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // cards in two columns
            const float h = 44f, gap = 6f;
            int cols = l.ColumnWidth > 620f ? 2 : 1;
            int rows = (shown.Count + cols - 1) / cols;
            Rect area = l.GetRect(rows * (h + gap));
            float w = (area.width - gap * (cols - 1)) / cols;
            for (int i = 0; i < shown.Count; i++)
            {
                ThingDef d = shown[i];
                int a = d.size.x * d.size.z;
                bool on = Picked(d.defName, a);
                Rect c = new Rect(area.x + (i % cols) * (w + gap), area.y + (i / cols) * (h + gap), w, h);
                Widgets.DrawBoxSolidWithOutline(c, on ? CardOn : CardOff, on ? EdgeOn : Edge);
                Widgets.DrawHighlightIfMouseover(c);

                Rect icon = new Rect(c.x + 4f, c.y + 4f, h - 8f, h - 8f);
                try { Widgets.DefIcon(icon, d); } catch { }

                float tx = icon.xMax + 8f, tw = c.xMax - 34f - tx;
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.Label(new Rect(tx, c.y + 3f, tw, 22f), d.LabelCap.Truncate(tw));
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                string sub = d.size.x + "×" + d.size.z + "  ·  " + (d.modContentPack?.Name ?? "?");
                Widgets.Label(new Rect(tx, c.y + 23f, tw, 18f), sub.Truncate(tw));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;

                if (Recommended(a))
                {
                    GUI.color = Gold;
                    Text.Anchor = TextAnchor.UpperRight;
                    Widgets.Label(new Rect(c.xMax - 52f, c.y + 2f, 20f, 20f), "★");
                    Text.Anchor = TextAnchor.UpperLeft;
                    GUI.color = Color.white;
                    TooltipHandler.TipRegion(new Rect(c.xMax - 52f, c.y + 2f, 20f, 20f), "AT_WallRecTip".Translate());
                }
                bool now = on;
                Widgets.Checkbox(c.xMax - 30f, c.y + (h - 24f) / 2f, ref now);
                if (Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 34f, c.height)))
                    now = !on;
                if (now != on)
                    SetPick(d, now);
                TooltipHandler.TipRegion(c, d.description.NullOrEmpty() ? d.LabelCap.ToString() : d.LabelCap + "\n\n" + d.description.Truncate(400f));
            }
            Text.Anchor = TextAnchor.UpperLeft;
        }
    }

    // copies are made after all mods' patches, so they also carry other mods' changes
    [HarmonyPatch(typeof(LoadedModManager), nameof(LoadedModManager.ApplyPatches))]
    public static class Patch_ApplyPatches
    {
        public static void Postfix(XmlDocument xmlDoc, Dictionary<XmlNode, LoadableXmlAsset> assetlookup) =>
            WallTurrets.MakeCopies(xmlDoc, assetlookup);
    }

    [StaticConstructorOnStartup]
    public static class WallTurretsStartup
    {
        static WallTurretsStartup() => WallTurrets.Finish();
    }
}
