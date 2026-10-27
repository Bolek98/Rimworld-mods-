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
        public static float DrawList(Listing_Standard l)
        {
            float start = l.CurHeight;
            ATSettings s = ATMod.S;
            if (s.wallPick == null) s.wallPick = new Dictionary<string, bool>();
            l.Label("AT_WallPick".Translate());
            Rect btn = l.GetRect(28f);
            if (Widgets.ButtonText(new Rect(btn.x, btn.y, Mathf.Min(260f, btn.width), btn.height), "AT_WallRecommended".Translate()))
                s.wallPick.Clear();
            if (Candidates.Count == 0)
                l.Label("AT_WallNone".Translate());
            foreach (ThingDef d in Candidates)
            {
                int area = d.size.x * d.size.z;
                bool on = Picked(d.defName, area);
                bool was = on;
                string text = "   " + d.LabelCap + " (" + d.size.x + "×" + d.size.z + ")" + (Recommended(area) ? " ★" : "");
                l.CheckboxLabeled(text, ref on, d.modContentPack?.Name);
                if (on != was)
                {
                    if (on == Recommended(area)) s.wallPick.Remove(d.defName);
                    else s.wallPick[d.defName] = on;
                }
            }
            return l.CurHeight - start;
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
