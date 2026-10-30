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

                // 1.3.0: the wall's own picture – a 1×1 wall turret looks like a piece of the wall with the gun on top
                XmlElement wallGfx = null;
                foreach (XmlElement e in all)
                    if (e["defName"]?.InnerText?.Trim() == "Wall")
                    {
                        for (XmlElement x = e; x != null && wallGfx == null;)
                        {
                            wallGfx = x["graphicData"];
                            string p = x.GetAttribute("ParentName");
                            x = !p.NullOrEmpty() && byName.TryGetValue(p, out XmlElement px) ? px : null;
                        }
                        break;
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
                    if (size.x == 1 && size.z == 1 && wallGfx != null)
                    {
                        // the base is drawn as the wall (joined with the walls next to it, in the wall's material)
                        foreach (XmlNode old in c.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "graphicData").ToList())
                            c.RemoveChild(old);
                        var g = (XmlElement)xml.ImportNode(wallGfx, true);
                        g.SetAttribute("Inherit", "False");
                        c.AppendChild(g);
                        // built from a material like a wall (stone blocks, metal, wood) – 5 of it, as one wall piece
                        if (Find(e, "stuffCategories").NullOrEmpty())
                        {
                            XmlElement sc = xml.CreateElement("stuffCategories");
                            foreach (string k in new[] { "Stony", "Metallic", "Woody" })
                            {
                                XmlElement li = xml.CreateElement("li");
                                li.InnerText = k;
                                sc.AppendChild(li);
                            }
                            foreach (XmlNode old in c.ChildNodes.OfType<XmlElement>().Where(x => x.Name == "stuffCategories").ToList())
                                c.RemoveChild(old);
                            c.AppendChild(sc);
                            Set(c, "costStuffCount", "5");
                        }
                        wallLook.Add(defName + Suffix);
                    }
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
                    // the menu shows the turret, not the wall picture
                    if (orig.uiIcon != null && wallLook.Contains(w.defName))
                    {
                        w.uiIcon = orig.uiIcon;
                        w.uiIconPath = orig.uiIconPath;
                        w.uiIconColor = orig.uiIconColor;
                        w.uiIconScale = orig.uiIconScale;
                    }
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
        public static readonly HashSet<string> wallLook = new HashSet<string>();
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
            restartAt = -10f;
        }

        private static void Flash(string text)
        {
            flash = text;
            flashUntil = Time.realtimeSinceStartup + 3f;
        }

        // compared with the start of the game once a second, not every frame
        private static bool restartCache;
        private static float restartAt = -10f;
        public static bool RestartNeeded()
        {
            if (Time.realtimeSinceStartup - restartAt < 1f) return restartCache;
            restartAt = Time.realtimeSinceStartup;
            ATSettings s = ATMod.S;
            restartCache = s.wallTurrets != startEnabled
                || (s.wallTurrets && Candidates.Any(d => Picked(d.defName, d.size.x * d.size.z) != startMade.Contains(d.defName)));
            return restartCache;
        }

        private static Vector2 listScroll;
        private static readonly Dictionary<ThingDef, string> subText = new Dictionary<ThingDef, string>();

        public static void DrawPage(Rect r)
        {
            ATSettings s = ATMod.S;
            if (s.wallPick == null) s.wallPick = new Dictionary<string, bool>();
            float y = r.y;
            // what it is + the switch
            Text.Font = GameFont.Tiny;
            GUI.color = new Color(0.75f, 0.75f, 0.75f);
            float dh = Text.CalcHeight("AT_WallTurrets_Desc".Translate(), r.width);
            Widgets.Label(new Rect(r.x, y, r.width, dh), "AT_WallTurrets_Desc".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += dh + 6f;
            Widgets.CheckboxLabeled(new Rect(r.x, y, Mathf.Min(420f, r.width), 26f), "AT_WallEnable".Translate(), ref s.wallTurrets);
            y += 32f;
            if (!s.wallTurrets) return;
            // 1.4.0: firing arc
            Rect ow = new Rect(r.x, y, Mathf.Min(420f, r.width), 26f);
            Widgets.CheckboxLabeled(ow, "AT_WallOutward".Translate(), ref s.wallTurretsOutward);
            TooltipHandler.TipRegion(ow, "AT_WallOutward_Desc".Translate());
            y += 30f;

            List<ThingDef> all = Candidates;
            if (all.Count == 0)
            {
                Widgets.Label(new Rect(r.x, y, r.width, 26f), "AT_WallNone".Translate());
                return;
            }
            int picked = 0;
            foreach (ThingDef d in all) if (Picked(d.defName, d.size.x * d.size.z)) picked++;

            // buttons + counter
            float bw = Mathf.Min(170f, (r.width - 200f) / 3f);
            Rect b1 = new Rect(r.x, y, bw, 28f), b2 = new Rect(b1.xMax + 6f, y, bw, 28f), b3 = new Rect(b2.xMax + 6f, y, bw, 28f);
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
            Widgets.Label(new Rect(b3.xMax + 6f, y, r.xMax - b3.xMax - 6f, 28f), "AT_WallCount".Translate(picked, all.Count));
            Text.Anchor = TextAnchor.UpperLeft;
            y += 34f;

            // search + feedback on one line
            Widgets.Label(new Rect(r.x, y + 3f, 80f, 24f), "AT_WallSearch".Translate());
            filter = Widgets.TextField(new Rect(r.x + 80f, y, Mathf.Min(260f, r.width - 80f), 26f), filter ?? "");
            if (flash != null && Time.realtimeSinceStartup < flashUntil)
            {
                GUI.color = new Color(0.55f, 0.9f, 0.55f);
                Widgets.Label(new Rect(r.x + 350f, y + 3f, r.width - 350f, 24f), "✓ " + flash);
                GUI.color = Color.white;
            }
            y += 34f;

            List<ThingDef> shown = filter.NullOrEmpty() ? all
                : all.Where(d => (d.label ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0
                              || (d.modContentPack?.Name ?? "").IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

            // cards in two columns, in their own scroll area; only the visible rows are drawn
            const float h = 44f, gap = 6f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            int cols = outR.width > 620f ? 2 : 1;
            int rows = (shown.Count + cols - 1) / cols;
            Rect view = new Rect(0f, 0f, outR.width - 16f, rows * (h + gap));
            float w = (view.width - gap * (cols - 1)) / cols;
            Widgets.BeginScrollView(outR, ref listScroll, view);
            int firstRow = Mathf.Max(0, (int)(listScroll.y / (h + gap)) - 1);
            int lastRow = Mathf.Min(rows - 1, (int)((listScroll.y + outR.height) / (h + gap)) + 1);
            for (int i = firstRow * cols; i <= Mathf.Min(shown.Count - 1, lastRow * cols + cols - 1); i++)
            {
                ThingDef d = shown[i];
                int a = d.size.x * d.size.z;
                bool on = Picked(d.defName, a);
                Rect c = new Rect((i % cols) * (w + gap), (i / cols) * (h + gap), w, h);
                Widgets.DrawBoxSolidWithOutline(c, on ? CardOn : CardOff, on ? EdgeOn : Edge);
                Widgets.DrawHighlightIfMouseover(c);
                Rect icon = new Rect(c.x + 4f, c.y + 4f, h - 8f, h - 8f);
                try { Widgets.DefIcon(icon, d); } catch { }
                float tx = icon.xMax + 8f, tw = c.xMax - 34f - tx - 20f;
                Widgets.Label(new Rect(tx, c.y + 3f, tw, 22f), d.LabelCap);
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.7f, 0.7f, 0.7f);
                if (!subText.TryGetValue(d, out string sub))
                    subText[d] = sub = d.size.x + "×" + d.size.z + "  ·  " + (d.modContentPack?.Name ?? "?");
                Widgets.Label(new Rect(tx, c.y + 23f, tw, 18f), sub);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                if (Recommended(a))
                {
                    GUI.color = Gold;
                    Widgets.Label(new Rect(c.xMax - 52f, c.y + 2f, 20f, 20f), "★");
                    GUI.color = Color.white;
                }
                bool now = on;
                Widgets.Checkbox(c.xMax - 30f, c.y + (h - 24f) / 2f, ref now);
                if (Widgets.ButtonInvisible(new Rect(c.x, c.y, c.width - 34f, c.height)))
                    now = !on;
                if (now != on)
                {
                    SetPick(d, now);
                    restartAt = -10f;
                }
                if (Mouse.IsOver(c))
                    TooltipHandler.TipRegion(c, new TipSignal(() => d.LabelCap.ToString() + (Recommended(a) ? "\n★ " + "AT_WallRecTip".Translate().ToString() : "") + (d.description.NullOrEmpty() ? "" : "\n\n" + d.description), d.shortHash));
            }
            Widgets.EndScrollView();
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
