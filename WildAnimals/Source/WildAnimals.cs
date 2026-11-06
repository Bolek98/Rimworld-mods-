using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WildAnimals
{
    // ======================================================================
    // Wild Animals – Biome Spawn Control (Bolek98)
    // The animal counterpart of Evergrowth – Wild Plants: which wild animals live in every biome,
    // how many of them a map holds and how often new ones arrive. On top of that: a cap per
    // species, seasons per animal, a predator limit and a predator-free belt around the home area,
    // a live view of the current map with a hunting-pressure warning and letters for rare animals.
    // Events (manhunter packs, herd migrations, thrumbo visits …) are not touched.
    // Only mod settings are stored – safe to add to or remove from a save.
    // ======================================================================

    public class WASettings : ModSettings
    {
        public bool recommended = true;
        public bool enabled = true;           // our spawning instead of the game's hidden chance
        public float respawnHours = 8f;       // a new animal or group at most every … in-game hours
        public float groupFactor = 1f;        // group size × this
        public int predatorMaxPct = 30;       // predators at most … % of the wild animals (100 = no limit)
        public int predatorHomeRadius = 30;   // no predator arrives within … cells of the home area (0 = off)
        public bool rareLetters = true;
        public bool pressureAlert = true;
        public bool includePets = false;      // 0.2.0: domestic animals (dogs, cats … – wildness 0) can be put in the biome lists too
        public Dictionary<string, string> lists = new Dictionary<string, string>();   // biome -> "kind:w|kind:w"
        public Dictionary<string, float> target = new Dictionary<string, float>();    // biome -> animals on a 250×250 map
        public Dictionary<string, string> animals = new Dictionary<string, string>(); // kind -> "cap;seasons;rare"

        public static WASettings Recommended() => new WASettings();

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref respawnHours, "respawnHours", 8f);
            Scribe_Values.Look(ref groupFactor, "groupFactor", 1f);
            Scribe_Values.Look(ref predatorMaxPct, "predatorMaxPct", 30);
            Scribe_Values.Look(ref predatorHomeRadius, "predatorHomeRadius", 30);
            Scribe_Values.Look(ref rareLetters, "rareLetters", true);
            Scribe_Values.Look(ref pressureAlert, "pressureAlert", true);
            Scribe_Values.Look(ref includePets, "includePets", false);
            Scribe_Collections.Look(ref lists, "lists", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref target, "target", LookMode.Value, LookMode.Value);
            Scribe_Collections.Look(ref animals, "animals", LookMode.Value, LookMode.Value);
            if (lists == null) lists = new Dictionary<string, string>();
            if (target == null) target = new Dictionary<string, float>();
            if (animals == null) animals = new Dictionary<string, string>();
        }
    }

    public class AnimalCfg
    {
        public int cap;            // 0 = no cap
        public int seasons = 15;   // bits: 1 spring, 2 summer, 4 fall, 8 winter
        public bool rare;
        public int temp = -1;      // only when the outdoor temperature suits it: -1 = default, 0 off, 1 on
    }

    // ------------------------------------------------------------------
    // Core
    // ------------------------------------------------------------------
    public static class WA
    {
        public const string Version = "0.2.2";
        public const float BaseCells = 62500f;   // 250 × 250
        public static WASettings S => WildAnimalsMod.Settings;

        public static string F(float v) => v.ToString("0.####", CultureInfo.InvariantCulture);

        public static string Label(Def d) => d == null ? "?" : (d.label.NullOrEmpty() ? d.defName : d.LabelCap.ToString());

        public static string ModName(Def d)
        {
            if (d?.modContentPack == null) return "?";
            if (d.modContentPack.IsCoreMod) return "RimWorld";
            return d.modContentPack.Name;
        }

        // every wild-capable animal in the game (vanilla, DLCs, mods)
        private static List<PawnKindDef> allAnimals;
        private static bool allPets;
        public static List<PawnKindDef> AllAnimals
        {
            get
            {
                if (allAnimals != null && allPets != PetsOn) allAnimals = null;   // 0.2.0
                allPets = PetsOn;
                if (allAnimals == null)
                    allAnimals = DefDatabase<PawnKindDef>.AllDefsListForReading
                        .Where(k => k.race?.race != null && k.RaceProps.Animal && !k.RaceProps.Humanlike && !k.RaceProps.IsMechanoid
                            && !k.RaceProps.Dryad && (Wildness(k) > 0f || (PetsOn && IsPet(k))) && !Bad.Contains(k))
                        .GroupBy(k => k.race).Select(g => g.OrderBy(k => k.defName == g.Key.defName ? 0 : 1).First())
                        .OrderBy(k => Label(k)).ToList();
                return allAnimals;
            }
        }

        // 0.2.0: domestic animals (dogs, cats and other pets from any mod) – the game gives them wildness 0, so they
        // never live in the wild; with the option on they can be added to a biome like any other animal.
        public static bool PetsOn => WildAnimalsMod.Settings != null && WildAnimalsMod.Settings.includePets;
        public static bool IsPet(PawnKindDef k) => k?.race?.race != null && k.RaceProps.Animal && Wildness(k) <= 0f;
        public static void ResetAnimals() { allAnimals = null; }

        // 0.2.2: why no animal came – one line per map and reason, at most every 6 in-game hours, in Player.log
        private static readonly Dictionary<string, int> whyAt = new Dictionary<string, int>();
        public static void Why(Map map, string text)
        {
            try
            {
                string key = map.uniqueID + "|" + text.Split(':')[0];
                int now = Find.TickManager.TicksGame;
                if (whyAt.TryGetValue(key, out int t) && now - t < 15000) return;
                whyAt[key] = now;
                Verse.Log.Message("[Wild Animals] " + map.Biome.defName + " (map " + map.uniqueID + "): " + text);
            }
            catch { }
        }

        public static float Wildness(PawnKindDef k) { try { return k.race.GetStatValueAbstract(StatDefOf.Wildness); } catch { return 1f; } }

        private static readonly System.Reflection.MethodInfo densityFactor = AccessTools.Method(typeof(GameConditionManager), "AggregateAnimalDensityFactor");

        // animals whose spawning threw once – left out for this session
        public static readonly HashSet<PawnKindDef> Bad = new HashSet<PawnKindDef>();

        public static float ComfyMin(PawnKindDef k) { try { return k.race.GetStatValueAbstract(StatDefOf.ComfyTemperatureMin); } catch { return -999f; } }
        public static float ComfyMax(PawnKindDef k) { try { return k.race.GetStatValueAbstract(StatDefOf.ComfyTemperatureMax); } catch { return 999f; } }

        public static bool Native(BiomeDef b, PawnKindDef k) => b.CommonalityOfAnimal(k) > 0f || b.CommonalityOfCoastalAnimal(k) > 0f || b.CommonalityOfPollutionAnimal(k) > 0f;

        private static readonly Dictionary<BiomeDef, Vector2> tempCache = new Dictionary<BiomeDef, Vector2>();

        // the usual outdoor temperatures of a biome (low, high): from the world tiles of that biome when a
        // game is loaded, otherwise from the comfortable range of the biome's own animals
        public static Vector2 BiomeTemps(BiomeDef b)
        {
            if (tempCache.TryGetValue(b, out Vector2 v)) return v;
            v = new Vector2(-999f, 999f);
            try
            {
                if (Current.ProgramState == ProgramState.Playing && Find.WorldGrid != null)
                {
                    float sum = 0f; int n = 0;
                    var grid = Find.WorldGrid;
                    int count = grid.TilesCount;
                    int step = Mathf.Max(1, count / 4000);
                    for (int i = 0; i < count; i += step)
                    {
                        var t = grid[i];
                        if (t == null || t.PrimaryBiome != b) continue;
                        sum += t.temperature; n++;
                    }
                    if (n > 0) { float avg = sum / n; v = new Vector2(avg - 15f, avg + 15f); }
                }
                if (v.x < -900f)
                {
                    var natives = DefaultList(b).Keys.ToList();
                    if (natives.Count > 0) v = new Vector2(natives.Average(k => ComfyMin(k)) + 10f, natives.Average(k => ComfyMax(k)) - 10f);
                }
            }
            catch { }
            tempCache[b] = v;
            return v;
        }

        // "may freeze" / "may overheat" for an animal in this biome, or null
        public static string TempWarning(BiomeDef b, PawnKindDef k)
        {
            Vector2 t = BiomeTemps(b);
            if (t.x < -900f) return null;
            if (ComfyMin(k) > t.x + 5f) return "WA_MayFreeze".Translate();
            if (ComfyMax(k) < t.y - 5f) return "WA_MayOverheat".Translate();
            return null;
        }

        public static bool TempOnly(BiomeDef b, PawnKindDef k)
        {
            int t = Cfg(k).temp;
            return t < 0 ? !Native(b, k) : t == 1;
        }

        public static bool Coastal(BiomeDef b, PawnKindDef k) => b.CommonalityOfAnimal(k) <= 0f && b.CommonalityOfCoastalAnimal(k) > 0f;

        // the game's own list of a biome
        public static Dictionary<PawnKindDef, float> DefaultList(BiomeDef b)
        {
            var d = new Dictionary<PawnKindDef, float>();
            foreach (PawnKindDef k in b.AllWildAnimals)
            {
                float w = b.CommonalityOfAnimal(k) + b.CommonalityOfCoastalAnimal(k);
                if (w > 0f) d[k] = w;
            }
            return d;
        }

        private static readonly Dictionary<BiomeDef, (string key, Dictionary<PawnKindDef, float> list)> listCache = new Dictionary<BiomeDef, (string, Dictionary<PawnKindDef, float>)>();

        // the list in use: the player's own, or the game's
        public static Dictionary<PawnKindDef, float> List(BiomeDef b)
        {
            string key = !S.recommended && S.lists.TryGetValue(b.defName, out string v) ? v : null;
            if (listCache.TryGetValue(b, out var c) && ReferenceEquals(c.key, key)) return c.list;
            Dictionary<PawnKindDef, float> list;
            if (key == null) list = DefaultList(b);
            else
            {
                list = new Dictionary<PawnKindDef, float>();
                foreach (string part in key.Split('|'))
                {
                    int i = part.LastIndexOf(':');
                    if (i <= 0) continue;
                    PawnKindDef k = DefDatabase<PawnKindDef>.GetNamedSilentFail(part.Substring(0, i));
                    if (k == null) continue;
                    if (float.TryParse(part.Substring(i + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out float w))
                        list[k] = w;
                }
            }
            listCache[b] = (key, list);
            return list;
        }

        public static void Store(BiomeDef b, Dictionary<PawnKindDef, float> list)
        {
            S.lists[b.defName] = string.Join("|", list.Select(kv => kv.Key.defName + ":" + F(kv.Value)).ToArray());
        }

        public static bool Custom(BiomeDef b) => !S.recommended && (S.lists.ContainsKey(b.defName) || S.target.ContainsKey(b.defName));

        // 0.1.3: the animals the game itself puts into this biome
        private static readonly Dictionary<BiomeDef, HashSet<PawnKindDef>> natives = new Dictionary<BiomeDef, HashSet<PawnKindDef>>();
        public static HashSet<PawnKindDef> Natives(BiomeDef b)
        {
            if (!natives.TryGetValue(b, out var h)) natives[b] = h = new HashSet<PawnKindDef>(DefaultList(b).Keys);
            return h;
        }

        // how many of the biome's own animals the player's list has at 0 (or lost)
        public static int NativesOff(BiomeDef b)
        {
            if (S.recommended || !S.lists.ContainsKey(b.defName)) return 0;
            var list = List(b);
            int n = 0;
            foreach (PawnKindDef k in Natives(b))
                if (!list.TryGetValue(k, out float w) || w <= 0f) n++;
            return n;
        }

        // 0.1.3: brings the biome's own animals that are at 0 back to the game's value; everything else stays
        public static int RestoreNatives(BiomeDef b)
        {
            if (S.recommended || !S.lists.ContainsKey(b.defName)) return 0;
            var list = new Dictionary<PawnKindDef, float>(List(b));
            int n = 0;
            foreach (var kv in DefaultList(b))
                if (!list.TryGetValue(kv.Key, out float w) || w <= 0f) { list[kv.Key] = kv.Value; n++; }
            if (n > 0) Store(b, list);
            return n;
        }

        public static string PackageOf(Def d) => d?.modContentPack?.PackageIdPlayerFacing ?? "";

        // how many animals the game would keep on a 250×250 map of this biome
        public static float DefaultTarget(BiomeDef b)
        {
            float weight = BaseCells * b.animalDensity / 10000f;
            float sw = 0f, se = 0f;
            foreach (var kv in DefaultList(b))
            {
                sw += kv.Value;
                se += kv.Value * Mathf.Max(0.05f, kv.Key.ecoSystemWeight);
            }
            if (weight <= 0f || sw <= 0f) return 0f;
            return Mathf.Round(weight / (se / sw));
        }

        public static float TargetBase(BiomeDef b) => !S.recommended && S.target.TryGetValue(b.defName, out float t) ? t : DefaultTarget(b);

        // ---------- per animal ----------
        private static readonly Dictionary<string, AnimalCfg> cfgCache = new Dictionary<string, AnimalCfg>();
        private static readonly Dictionary<PawnKindDef, bool> rareDefault = new Dictionary<PawnKindDef, bool>();

        public static bool DefaultRare(PawnKindDef k)
        {
            if (rareDefault.TryGetValue(k, out bool r)) return r;
            float best = 0f;
            bool any = false;
            foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
            {
                float w = b.CommonalityOfAnimal(k);
                if (w > 0f) { any = true; best = Mathf.Max(best, w); }
            }
            r = any && best < 0.02f;
            rareDefault[k] = r;
            return r;
        }

        public static AnimalCfg Cfg(PawnKindDef k)
        {
            if (S.animals.TryGetValue(k.defName, out string v))
            {
                if (cfgCache.TryGetValue(v, out AnimalCfg c)) return c;
                string[] p = v.Split(';');
                c = new AnimalCfg();
                if (p.Length > 0) int.TryParse(p[0], out c.cap);
                if (p.Length > 1 && !int.TryParse(p[1], out c.seasons)) c.seasons = 15;
                c.rare = p.Length > 2 && p[2] == "1";
                if (p.Length > 3 && !int.TryParse(p[3], out c.temp)) c.temp = -1;
                cfgCache[v] = c;
                return c;
            }
            return new AnimalCfg { rare = DefaultRare(k) };
        }

        public static void SetCfg(PawnKindDef k, AnimalCfg c)
        {
            if (c.cap == 0 && c.seasons == 15 && c.rare == DefaultRare(k) && c.temp < 0) S.animals.Remove(k.defName);
            else S.animals[k.defName] = c.cap + ";" + c.seasons + ";" + (c.rare ? "1" : "0") + ";" + c.temp;
        }

        public static int SeasonBit(Map map)
        {
            switch (GenLocalDate.Season(map))
            {
                case Season.Spring: return 1;
                case Season.Summer: case Season.PermanentSummer: return 2;
                case Season.Fall: return 4;
                case Season.Winter: case Season.PermanentWinter: return 8;
                default: return 15;
            }
        }

        // ---------- the map ----------
        public static bool Wild(Pawn p) => p != null && p.Faction == null && p.RaceProps.Animal && !p.Dead && p.Spawned;

        public static List<Pawn> WildOn(Map map)
        {
            var l = new List<Pawn>();
            IReadOnlyList<Pawn> all = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < all.Count; i++)
                if (Wild(all[i])) l.Add(all[i]);
            return l;
        }

        public static bool InSeason(Map map, PawnKindDef k, int bit)
        {
            if ((Cfg(k).seasons & bit) == 0) return false;
            try { return map.mapTemperature.SeasonAcceptableFor(k.race); } catch { return true; }
        }

        // how many wild animals this map should hold right now
        public static int Target(Map map)
        {
            BiomeDef b = map.Biome;
            float t = TargetBase(b) * map.Area / BaseCells;
            try
            {
                if (b.animalDensity > 0f) t *= map.TileInfo.AnimalDensity / b.animalDensity;
                if (densityFactor != null) t *= (float)densityFactor.Invoke(map.gameConditionManager, new object[] { map });
            }
            catch { }
            // out of season animals leave a gap, as in the game
            var list = List(b);
            float all = 0f, now = 0f;
            int bit = SeasonBit(map);
            foreach (var kv in list)
            {
                if (kv.Value <= 0f) continue;
                all += kv.Value;
                if (InSeason(map, kv.Key, bit)) now += kv.Value;
            }
            if (all <= 0f) return 0;
            t *= now / all;
            return Mathf.Max(0, Mathf.RoundToInt(t));
        }

        // ---------- which animal arrives ----------
        public static PawnKindDef Choose(Map map, IntVec3 loc)
        {
            BiomeDef b = map.BiomeAt(loc) ?? map.Biome;
            var list = List(b);
            if (list.Count == 0) return null;
            List<Pawn> wild = WildOn(map);
            var counts = new Dictionary<PawnKindDef, int>();
            int preds = 0;
            foreach (Pawn p in wild)
            {
                counts.TryGetValue(p.kindDef, out int c);
                counts[p.kindDef] = c + 1;
                if (p.RaceProps.predator) preds++;
            }
            bool predFull = S.predatorMaxPct < 100 && wild.Count > 0 && preds * 100f / wild.Count >= S.predatorMaxPct;
            bool nearHome = S.predatorHomeRadius > 0 && map != MapGenerator.mapBeingGenerated && NearHome(map, loc, S.predatorHomeRadius);
            bool coastal = false;
            try { coastal = map.TileInfo.IsCoastal; } catch { }
            bool water = map.terrainGrid.AnyWaterCells;
            int bit = SeasonBit(map);
            var cand = new List<(PawnKindDef k, float w)>();
            foreach (var kv in list)
            {
                PawnKindDef k = kv.Key;
                float w = kv.Value;
                if (w <= 0f) continue;
                if (!coastal && Coastal(b, k) && !S.lists.ContainsKey(b.defName)) continue;
                if (Bad.Contains(k)) continue;
                if (!PetsOn && IsPet(k)) continue;      // 0.2.0: pets only while the option is on
                if (k.RaceProps.waterSeeker && !water) continue;
                if (TempOnly(b, k))
                {
                    float o = map.mapTemperature.OutdoorTemp;
                    if (o < ComfyMin(k) || o > ComfyMax(k)) continue;
                }
                if (!InSeason(map, k, bit)) continue;
                AnimalCfg cfg = Cfg(k);
                if (cfg.cap > 0 && counts.TryGetValue(k, out int have) && have >= cfg.cap) continue;
                if (k.RaceProps.predator && (predFull || nearHome)) continue;
                try
                {
                    foreach (TileMutatorDef m in map.TileInfo.Mutators)
                        if (m.Worker != null) w *= m.Worker.AnimalCommonalityFactorFor(k, map.Tile);
                }
                catch { }
                if (w <= 0f) continue;
                cand.Add((k, w / Mathf.Max(1f, k.wildGroupSize.Average)));
            }
            if (cand.Count == 0) return null;
            return cand.RandomElementByWeight(c => c.w).k;
        }

        public static bool NearHome(Map map, IntVec3 c, int radius)
        {
            Area home = map.areaManager?.Home;
            if (home == null || home.TrueCount == 0) return false;
            int r2 = radius * radius;
            foreach (IntVec3 h in home.ActiveCells)
                if ((h - c).LengthHorizontalSquared <= r2) return true;
            return false;
        }

        // ---------- per map memory (not saved – only for the live view and the alert) ----------
        public class MapLog
        {
            public int lastArrival = -999999;
            public List<int> kills = new List<int>();
            public List<int> arrived = new List<int>();   // one entry per animal
        }

        private static readonly Dictionary<int, MapLog> logs = new Dictionary<int, MapLog>();

        public static MapLog Log(Map map)
        {
            if (!logs.TryGetValue(map.uniqueID, out MapLog l))
            {
                l = new MapLog();
                logs[map.uniqueID] = l;
            }
            return l;
        }

        public const int PressureWindow = 300000;   // 5 days

        public static void Trim(List<int> l, int now)
        {
            l.RemoveAll(t => now - t > PressureWindow);
        }

        // kills faster than arrivals while the map is below its target
        public static bool Pressure(Map map, out int kills, out int arrivals)
        {
            MapLog l = Log(map);
            int now = Find.TickManager.TicksGame;
            Trim(l.kills, now);
            Trim(l.arrived, now);
            kills = l.kills.Count;
            arrivals = l.arrived.Count;
            return kills >= 3 && kills > arrivals && WildOn(map).Count < Target(map) * 0.8f;
        }

        public static int Ticks => Mathf.Max(250, Mathf.RoundToInt(S.respawnHours * GenDate.TicksPerHour));

        // ---------- presets and share code ----------
        public const string CodeHead = "WA1:";

        public static string Export()
        {
            var sb = new StringBuilder();
            foreach (var kv in S.lists) sb.Append("L\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            foreach (var kv in S.target) sb.Append("T\t").Append(kv.Key).Append('\t').Append(F(kv.Value)).Append('\n');
            foreach (var kv in S.animals) sb.Append("A\t").Append(kv.Key).Append('\t').Append(kv.Value).Append('\n');
            sb.Append("G\trespawn\t").Append(F(S.respawnHours)).Append('\n');
            sb.Append("G\tgroup\t").Append(F(S.groupFactor)).Append('\n');
            sb.Append("G\tpredMax\t").Append(S.predatorMaxPct).Append('\n');
            sb.Append("G\tpredHome\t").Append(S.predatorHomeRadius).Append('\n');
            return CodeHead + Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        public static int Import(string code)
        {
            if (code == null) return -1;
            code = code.Trim();
            if (!code.StartsWith(CodeHead)) return -1;
            string text;
            try { text = Encoding.UTF8.GetString(Convert.FromBase64String(code.Substring(CodeHead.Length))); }
            catch { return -1; }
            var lists = new Dictionary<string, string>();
            var target = new Dictionary<string, float>();
            var animals = new Dictionary<string, string>();
            int n = 0;
            foreach (string line in text.Split('\n'))
            {
                string[] p = line.Split('\t');
                if (p.Length < 3) continue;
                float.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float v);
                switch (p[0])
                {
                    case "L": lists[p[1]] = p[2]; n++; break;
                    case "T": target[p[1]] = v; n++; break;
                    case "A": animals[p[1]] = p[2]; n++; break;
                    case "G":
                        if (p[1] == "respawn") S.respawnHours = Mathf.Clamp(v, 1f, 240f);
                        else if (p[1] == "group") S.groupFactor = Mathf.Clamp(v, 0.25f, 3f);
                        else if (p[1] == "predMax") S.predatorMaxPct = Mathf.Clamp((int)v, 0, 100);
                        else if (p[1] == "predHome") S.predatorHomeRadius = Mathf.Clamp((int)v, 0, 100);
                        n++;
                        break;
                }
            }
            S.recommended = false;
            S.lists = lists;
            S.target = target;
            S.animals = animals;
            return n;
        }

        // 0 = game's lists, 1 = more animals, 2 = fewer animals, 3 = peaceful
        public static void Preset(int which)
        {
            S.recommended = false;
            S.lists.Clear();
            S.target.Clear();
            if (which == 0)
            {
                S.predatorMaxPct = 100; S.predatorHomeRadius = 0; S.respawnHours = 8f; S.groupFactor = 1f;
                return;
            }
            float f = which == 1 ? 2f : which == 2 ? 0.5f : 1f;
            if (which != 3)
                foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
                    if (b.animalDensity > 0f) S.target[b.defName] = Mathf.Round(DefaultTarget(b) * f);
            if (which == 1) S.respawnHours = 4f;
            if (which == 2) S.respawnHours = 16f;
            if (which == 3) { S.predatorMaxPct = 10; S.predatorHomeRadius = 60; }
        }
    }

    // ------------------------------------------------------------------
    // Harmony
    // ------------------------------------------------------------------

    // our arrivals instead of the game's hidden chance
    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.WildAnimalSpawnerTick))]
    public static class Patch_Tick
    {
        public static bool Prefix(WildAnimalSpawner __instance, Map ___map)
        {
            WASettings s = WA.S;
            if (s == null || !s.enabled || ___map == null) return true;
            int now = Find.TickManager.TicksGame;
            if (now % 1213 != 0) return false;
            try
            {
                WA.MapLog log = WA.Log(___map);
                if (log.lastArrival > now) log.lastArrival = 0;      // 0.2.2: a stamp from another save/game must not block for ever
                if (now - log.lastArrival < WA.Ticks) return false;
                int have = WA.WildOn(___map).Count, want = WA.Target(___map);
                if (have >= want) { WA.Why(___map, "full: " + have + " wild animals, target " + want); return false; }
                Map map = ___map;
                if (RCellFinder.TryFindRandomPawnEntryCell(out IntVec3 cell, map, CellFinder.EdgeRoadChance_Animal, true,
                    c => map.reachability.CanReachMapEdge(c, TraverseParms.For(TraverseMode.NoPassClosedDoors).WithFenceblocked(true))))
                {
                    if (__instance.SpawnRandomWildAnimalAt(cell, true))
                    {
                        log.lastArrival = now;
                        WA.Why(___map, "arrived (" + have + " → target " + want + ")");
                    }
                    else WA.Why(___map, "no animal fits now (season, temperature, caps, list) – " + have + " wild, target " + want);
                }
                else WA.Why(___map, "no free map edge to enter from");
            }
            catch (Exception e) { Log.ErrorOnce("[Wild Animals] tick: " + e, 0x3A1D11); }
            return false;
        }
    }

    // map generation fills the map up to our target
    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.AnimalEcosystemFull), MethodType.Getter)]
    public static class Patch_Full
    {
        public static void Postfix(Map ___map, ref bool __result)
        {
            WASettings s = WA.S;
            if (s == null || !s.enabled || ___map == null) return;
            try { __result = WA.WildOn(___map).Count >= WA.Target(___map); } catch { }
        }
    }

    // our choice of animal, group size, rare letters, arrival log
    [HarmonyPatch(typeof(WildAnimalSpawner), nameof(WildAnimalSpawner.SpawnRandomWildAnimalAt))]
    public static class Patch_Spawn
    {
        public class State
        {
            public PawnKindDef kind;
            public IntRange orig;
            public bool scaled, ours;
            public int before;
        }

        public static bool Prefix(Map ___map, IntVec3 loc, ref PawnKindDef animalKind, ref bool __result, out State __state)
        {
            __state = null;
            WASettings s = WA.S;
            if (s == null || !s.enabled || ___map == null || animalKind != null) return true;
            try
            {
                PawnKindDef k = WA.Choose(___map, loc);
                if (k == null) { __result = false; return false; }
                animalKind = k;
                __state = new State { kind = k, orig = k.wildGroupSize, ours = true, before = WA.WildOn(___map).Count };
                if (Mathf.Abs(s.groupFactor - 1f) > 0.01f)
                {
                    IntRange o = k.wildGroupSize;
                    k.wildGroupSize = new IntRange(Mathf.Max(1, Mathf.RoundToInt(o.min * s.groupFactor)), Mathf.Max(1, Mathf.RoundToInt(o.max * s.groupFactor)));
                    __state.scaled = true;
                }
            }
            catch (Exception e) { Log.ErrorOnce("[Wild Animals] choose: " + e, 0x3A1D12); }
            return true;
        }

        public static void Postfix(Map ___map, IntVec3 loc, bool __result, State __state)
        {
            if (__state == null || !__state.ours) return;
            Restore(__state);
            if (!__result || ___map == null || ___map == MapGenerator.mapBeingGenerated) return;
            try
            {
                int now = Find.TickManager.TicksGame;
                int n = Mathf.Max(1, WA.WildOn(___map).Count - __state.before);
                WA.MapLog log = WA.Log(___map);
                for (int i = 0; i < n; i++) log.arrived.Add(now);
                if (WA.S.rareLetters && WA.Cfg(__state.kind).rare)
                {
                    Pawn p = ___map.mapPawns.AllPawnsSpawned.Where(x => WA.Wild(x) && x.kindDef == __state.kind)
                        .OrderBy(x => (x.Position - loc).LengthHorizontalSquared).FirstOrDefault();
                    Find.LetterStack.ReceiveLetter("WA_RareTitle".Translate(WA.Label(__state.kind)),
                        "WA_RareText".Translate(WA.Label(__state.kind)), LetterDefOf.NeutralEvent,
                        p != null ? new LookTargets(p) : new LookTargets(new TargetInfo(loc, ___map)));
                }
            }
            catch (Exception e) { Log.ErrorOnce("[Wild Animals] after spawn: " + e, 0x3A1D13); }
        }

        // an animal whose spawning throws (special requirements of some mod) is left out for this session
        public static Exception Finalizer(Exception __exception, State __state, ref bool __result)
        {
            if (__state != null) Restore(__state);
            if (__exception != null && __state != null && __state.ours)
            {
                WA.Bad.Add(__state.kind);
                Log.Warning("[Wild Animals] " + __state.kind.defName + " could not be spawned and is left out for now: " + __exception.Message);
                __result = false;
                return null;
            }
            return __exception;
        }

        private static void Restore(State st)
        {
            if (st.scaled) { st.kind.wildGroupSize = st.orig; st.scaled = false; }
        }
    }

    // the colony killing wild animals (for the hunting-pressure warning)
    [HarmonyPatch(typeof(Pawn), nameof(Pawn.Kill))]
    public static class Patch_Kill
    {
        public static void Prefix(Pawn __instance, DamageInfo? dinfo)
        {
            try
            {
                if (!WA.Wild(__instance) || !(dinfo?.Instigator is Pawn ip) || ip.Faction != Faction.OfPlayer) return;
                WA.Log(__instance.Map).kills.Add(Find.TickManager.TicksGame);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------
    // Alert: hunting pressure
    // ------------------------------------------------------------------
    public class Alert_WAHuntingPressure : Alert
    {
        private int cacheTick = -99999;
        private bool cached;
        private int k, a;

        public Alert_WAHuntingPressure()
        {
            defaultPriority = AlertPriority.Medium;
        }

        public override string GetLabel() => "WA_AlertLabel".Translate();

        public override TaggedString GetExplanation() => "WA_AlertText".Translate(k, a);

        public override AlertReport GetReport()
        {
            WASettings s = WA.S;
            Map map = Find.CurrentMap;
            if (s == null || !s.enabled || !s.pressureAlert || map == null) return false;
            int now = Find.TickManager.TicksGame;
            if (now - cacheTick > 2000)
            {
                cacheTick = now;
                try { cached = WA.Pressure(map, out k, out a); } catch { cached = false; }
            }
            return cached;
        }
    }

    public static class U
    {
        public static float Slider(Rect r, float value, float min, float max, float step)
        {
            float nv = Widgets.HorizontalSlider(r, value, min, max, true);
            if (nv == value) return value;
            return step > 0f ? Mathf.Round(nv / step) * step : nv;
        }

        public static void Label(Rect r, string text, Color c, GameFont font = GameFont.Small, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            GameFont f = Text.Font;
            TextAnchor a = Text.Anchor;
            Color g = GUI.color;
            Text.Font = font;
            Text.Anchor = anchor;
            GUI.color = c;
            Widgets.Label(r, text);
            GUI.color = g;
            Text.Anchor = a;
            Text.Font = f;
        }
    }
}

namespace WildAnimals
{
    // 0.2.1: a biome that lists an animal twice (its own list + the animal's wildBiomes, or one list twice – e.g. a mod
    // adding RG_Dodo to a biome that already has it) makes the game's commonality cache throw "same key" every time.
    // Then BiomeDef.AllWildAnimals breaks and NO animal of that biome can arrive – also with the game's own spawning.
    // Build the cache ourselves without the crash (first entry wins); the game then just reads it.
    // 0.2.2: also clean the data itself once at startup (like Choose Wild Animal Spawns does): every animal at most
    // once per biome – the game's own lists (AllWildAnimals, coastal, pollution) then never meet a duplicate.
    [StaticConstructorOnStartup]
    public static class BiomeDedupe
    {
        static BiomeDedupe()
        {
            try
            {
                int removed = 0;
                foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
                {
                    var seen = new HashSet<PawnKindDef>();
                    var l = Traverse.Create(b).Field("wildAnimals").GetValue<List<BiomeAnimalRecord>>();
                    if (l != null) removed += l.RemoveAll(r => r == null || r.animal == null || !seen.Add(r.animal));
                }
                foreach (PawnKindDef k in DefDatabase<PawnKindDef>.AllDefsListForReading)
                {
                    List<AnimalBiomeRecord> wb = k?.race?.race?.wildBiomes;
                    if (wb == null) continue;
                    var seenB = new HashSet<BiomeDef>();
                    // the same biome twice in one race's list (an overlap with the biome's own list is handled by the cache patch)
                    removed += wb.RemoveAll(r => r == null || r.biome == null || !seenB.Add(r.biome));
                }
                foreach (BiomeDef b in DefDatabase<BiomeDef>.AllDefsListForReading)
                    Traverse.Create(b).Field("cachedAnimalCommonalities").SetValue(null);
                if (removed > 0) Verse.Log.Message("[Wild Animals] removed " + removed + " duplicate animal entries from biomes (a mod listed them twice).");
            }
            catch (Exception e) { Verse.Log.Warning("[Wild Animals] biome dedupe: " + e.Message); }
        }
    }

    [HarmonyPatch(typeof(BiomeDef), nameof(BiomeDef.CommonalityOfAnimal))]
    public static class Patch_BiomeCache
    {
        static readonly AccessTools.FieldRef<BiomeDef, Dictionary<PawnKindDef, float>> cache =
            AccessTools.FieldRefAccess<BiomeDef, Dictionary<PawnKindDef, float>>("cachedAnimalCommonalities");
        static readonly AccessTools.FieldRef<BiomeDef, List<BiomeAnimalRecord>> wild =
            AccessTools.FieldRefAccess<BiomeDef, List<BiomeAnimalRecord>>("wildAnimals");
        static readonly HashSet<BiomeDef> told = new HashSet<BiomeDef>();

        public static void Prefix(BiomeDef __instance)
        {
            try
            {
                if (cache(__instance) != null) return;
                var d = new Dictionary<PawnKindDef, float>();
                var dupes = new List<string>();
                List<BiomeAnimalRecord> own = wild(__instance);
                if (own != null)
                    foreach (BiomeAnimalRecord r in own)
                    {
                        if (r?.animal == null) continue;
                        if (d.ContainsKey(r.animal)) { dupes.Add(r.animal.defName); continue; }
                        d[r.animal] = r.commonality;
                    }
                foreach (PawnKindDef k in DefDatabase<PawnKindDef>.AllDefsListForReading)
                {
                    List<AnimalBiomeRecord> wb = k?.race?.race?.wildBiomes;
                    if (wb == null) continue;
                    foreach (AnimalBiomeRecord r in wb)
                    {
                        if (r == null || r.biome != __instance) continue;
                        if (d.ContainsKey(k)) { dupes.Add(k.defName); continue; }
                        d[k] = r.commonality;
                    }
                }
                cache(__instance) = d;
                if (dupes.Count > 0 && told.Add(__instance))
                    Log.Message("[Wild Animals] " + __instance.defName + " lists these animals twice (a mod conflict) – kept the first, so the biome's animals can arrive again: " + string.Join(", ", dupes.Distinct().ToArray()));
            }
            catch (Exception e) { Log.WarningOnce("[Wild Animals] biome cache: " + e.Message, 0x3A1D12); }
        }
    }
}
