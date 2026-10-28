using System;
using System.Collections.Generic;
using System.IO;
using System.Xml;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace SmoothFPS
{
    // 1.6.0: opt-out rules other mods can ship – things Smooth FPS must never slow down.
    //
    // Way 1 – a def in the mod's Defs folder (Smooth FPS does not need to be loaded; without it
    // the game only warns about an unknown def type, so better put it in a folder that loads only
    // with Smooth FPS, e.g. LoadFolders.xml with IfModActive="misak.graphicstuner"):
    //
    //   <Defs>
    //     <SmoothFPS.ExclusionDef>
    //       <defName>MyMod_SmoothFPSRules</defName>
    //       <pawnKinds><li>MyMod_Guardian</li></pawnKinds>
    //       <hediffs><li>MyMod_TimedCurse</li></hediffs>
    //       <worldObjects><li>MyMod_MovingFortress</li></worldObjects>
    //       <thingDefs><li>MyMod_Reactor</li></thingDefs>
    //     </SmoothFPS.ExclusionDef>
    //   </Defs>
    //
    // Way 2 – a plain file Extras/SmoothFPSRules.xml in the mod's folder (also in a version
    // folder like 1.6/Extras/), read only by Smooth FPS, no error without it:
    //
    //   <SmoothFPSRules>
    //     <pawnKinds><li>MyMod_Guardian</li></pawnKinds>
    //     <hediffs><li>MyMod_TimedCurse</li></hediffs>
    //     <worldObjects><li>MyMod_MovingFortress</li></worldObjects>
    //     <thingDefs><li>MyMod_Reactor</li></thingDefs>
    //   </SmoothFPSRules>
    //
    // All entries are defNames; unknown ones are ignored (so a rule may name defs of other mods).
    // What the rules do:
    //   * pawnKinds / thingDefs – such pawns and things always keep the game's own tick rate
    //     (no slower updates off screen, on other maps or on the world), and such colonists'
    //     job searches are never put off;
    //   * hediffs – a pawn with such a condition keeps the game's own tick rate, and a world pawn
    //     with it is never put to sleep by Smooth FPS (the game's own rules still apply);
    //   * worldObjects – such world objects keep the game's own tick rate.
    public class ExclusionDef : Def
    {
        public List<string> pawnKinds = new List<string>();
        public List<string> hediffs = new List<string>();
        public List<string> worldObjects = new List<string>();
        public List<string> thingDefs = new List<string>();
    }

    public static class ExclusionRules
    {
        public static readonly HashSet<PawnKindDef> PawnKinds = new HashSet<PawnKindDef>();
        public static readonly HashSet<HediffDef> Hediffs = new HashSet<HediffDef>();
        public static readonly HashSet<WorldObjectDef> WorldObjects = new HashSet<WorldObjectDef>();
        public static readonly HashSet<ThingDef> ThingDefs = new HashSet<ThingDef>();
        public static bool Any;
        public static int Entries;
        public static int Mods;

        public static void Load()
        {
            PawnKinds.Clear();
            Hediffs.Clear();
            WorldObjects.Clear();
            ThingDefs.Clear();
            var fromMods = new HashSet<string>();
            int unknown = 0;
            try
            {
                foreach (ExclusionDef d in DefDatabase<ExclusionDef>.AllDefsListForReading)
                {
                    unknown += Add(d.pawnKinds, d.hediffs, d.worldObjects, d.thingDefs);
                    fromMods.Add(d.modContentPack?.PackageId ?? d.defName);
                }
            }
            catch (Exception ex)
            {
                GraphicsTuner.MLog.Warn("[Smooth FPS] reading SmoothFPS.ExclusionDef: " + ex.Message);
            }
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                try
                {
                    var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    files.Add(Path.Combine(mod.RootDir, "Extras", "SmoothFPSRules.xml"));
                    foreach (string folder in mod.foldersToLoadDescendingOrder)
                        files.Add(Path.Combine(folder, "Extras", "SmoothFPSRules.xml"));
                    foreach (string f in files)
                    {
                        if (!File.Exists(f))
                            continue;
                        var doc = new XmlDocument();
                        doc.Load(f);
                        unknown += Add(List(doc, "pawnKinds"), List(doc, "hediffs"), List(doc, "worldObjects"), List(doc, "thingDefs"));
                        fromMods.Add(mod.PackageId);
                    }
                }
                catch (Exception ex)
                {
                    GraphicsTuner.MLog.Warn("[Smooth FPS] rules of " + mod.Name + ": " + ex.Message);
                }
            }
            Entries = PawnKinds.Count + Hediffs.Count + WorldObjects.Count + ThingDefs.Count;
            Mods = fromMods.Count;
            Any = Entries > 0;
            if (fromMods.Count > 0)
                GraphicsTuner.MLog.Info("[Smooth FPS] opt-out rules: " + Entries + " entries from " + Mods + " mod(s)" + (unknown > 0 ? ", " + unknown + " unknown defNames ignored" : "") + ".");
        }

        private static List<string> List(XmlDocument doc, string name)
        {
            var l = new List<string>();
            XmlNode n = doc.DocumentElement?[name];
            if (n == null)
                return l;
            foreach (XmlNode li in n.ChildNodes)
                if (li.NodeType == XmlNodeType.Element && !string.IsNullOrEmpty(li.InnerText))
                    l.Add(li.InnerText.Trim());
            return l;
        }

        private static int Add(List<string> kinds, List<string> hediffs, List<string> objects, List<string> things)
        {
            int unknown = 0;
            unknown += AddAll(kinds, PawnKinds);
            unknown += AddAll(hediffs, Hediffs);
            unknown += AddAll(objects, WorldObjects);
            unknown += AddAll(things, ThingDefs);
            return unknown;
        }

        private static int AddAll<T>(List<string> names, HashSet<T> into) where T : Def
        {
            int unknown = 0;
            if (names == null)
                return 0;
            foreach (string n in names)
            {
                T d = DefDatabase<T>.GetNamedSilentFail(n);
                if (d != null)
                    into.Add(d);
                else
                    unknown++;
            }
            return unknown;
        }

        // a thing or pawn our speed-ups must leave alone
        public static bool Skip(Thing t)
        {
            if (!Any || t == null)
                return false;
            if (ThingDefs.Contains(t.def))
                return true;
            return t is Pawn p && SkipPawn(p);
        }

        public static bool SkipPawn(Pawn p)
        {
            if (!Any || p == null)
                return false;
            if (p.kindDef != null && PawnKinds.Contains(p.kindDef))
                return true;
            if (ThingDefs.Contains(p.def))
                return true;
            if (Hediffs.Count == 0)
                return false;
            List<Hediff> hs = p.health?.hediffSet?.hediffs;
            if (hs == null)
                return false;
            for (int i = 0; i < hs.Count; i++)
                if (hs[i] != null && Hediffs.Contains(hs[i].def))
                    return true;
            return false;
        }

        public static bool Skip(WorldObject o) => Any && o?.def != null && WorldObjects.Contains(o.def);
    }
}
