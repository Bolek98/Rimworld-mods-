using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace Legacy
{
    // ---------------------------------------------------------------------------
    // Legacy – your old colonies live on. A colony is written down (people, base, treasures,
    // story) when it ends or when you ask; in your next games its ruins lie somewhere in the
    // world: walls of your old base, graves with names, a chronicle to read.
    // ---------------------------------------------------------------------------
    public class LegacySettings : ModSettings
    {
        public bool recommended = true;
        public bool autoOnEnd = true;        // everybody died: the colony is written down
        public bool autoOnAbandon = true;    // the last home was abandoned
        public int ruinsPerWorld = 3;        // old colonies placed in a new world
        public bool loot = true;             // a part of the old treasures lies in the ruins
        public bool graves = true;           // the dead of the old colony lie in graves
        public bool showButton = true;       // "Legacy" button at the bottom

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref autoOnEnd, "autoOnEnd", true);
            Scribe_Values.Look(ref autoOnAbandon, "autoOnAbandon", true);
            Scribe_Values.Look(ref ruinsPerWorld, "ruinsPerWorld", 3);
            Scribe_Values.Look(ref loot, "loot", true);
            Scribe_Values.Look(ref graves, "graves", true);
            Scribe_Values.Look(ref showButton, "showButton", true);
        }
    }

    public class LegacyMod : Mod
    {
        public const string Version = "0.1.1";
        public static LegacySettings S;
        private static List<string> recBullets;

        public LegacyMod(ModContentPack content) : base(content)
        {
            S = GetSettings<LegacySettings>();
            MUI.Init(new LegacySettings(), null, "recommended");
            if (S.recommended) MUI.ApplyAll(S);
            MNews.Register(content, Version);
            var h = new Harmony("bolek98.legacy");
            try { MUI.PatchWindow(h, typeof(LegacyMod)); } catch { }
            Patches.Apply(h);
            Log.Message("[Legacy] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Legacy";

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, S, field);

        public override void DoSettingsWindowContents(Rect r)
        {
            MUI.Version(r, "v" + Version + " · Bolek98");
            if (recBullets == null)
                recBullets = new List<string> { "LG_Rec1".Translate(), "LG_Rec2".Translate(), "LG_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), S, S.recommended, v => { S.recommended = v; if (v) MUI.ApplyAll(S); }, recBullets) + 6f;
            if (S.recommended) y += MUI.LockNote(new Rect(r.x, y, r.width, 0f)) + 4f;
            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            var l = new Listing_Standard { maxOneColumn = true };
            MUI.BeginLock(S.recommended);
            try
            {
                l.Begin(rest);
                MUI.Section(l, "LG_SecWrite".Translate());
                Check(l, "LG_AutoEnd", ref S.autoOnEnd, "autoOnEnd");
                Check(l, "LG_AutoAbandon", ref S.autoOnAbandon, "autoOnAbandon");
                MUI.Section(l, "LG_SecRuins".Translate());
                S.ruinsPerWorld = Mathf.RoundToInt(MUI.Slider(l, "LG_RuinsPerWorld".Translate(S.ruinsPerWorld), S.ruinsPerWorld, 0f, 6f, "LG_RuinsPerWorld_Desc".Translate(), S, "ruinsPerWorld"));
                Check(l, "LG_Loot", ref S.loot, "loot");
                Check(l, "LG_Graves", ref S.graves, "graves");
                Check(l, "LG_ShowButton", ref S.showButton, "showButton");
                l.End();
            }
            finally { MUI.EndLock(rest); }
            LegacyButton.Refresh();
        }
    }

    // ---------------------------------------------------------------------------
    // What is written down (plain data, kept in Config/Legacy as XML – survives every save)
    // ---------------------------------------------------------------------------
    public class LColonist
    {
        public int id;
        public string name = "", nick = "", title = "", childhood = "", deathCause = "";
        public int gender, age, joinedDay = -1, deathDay = -1, kills;
        public bool alive = true;
        public List<string> traits = new List<string>(), skills = new List<string>(), relations = new List<string>();
        public string portrait;     // file name in the portraits folder

        public string Short => nick.NullOrEmpty() ? name : nick;
    }

    public class LBuilding { public string def, stuff; public int x, z, rot; }
    public class LFloor { public string def; public int x, z; }
    public class LItem { public string def, stuff; public int quality = -1, count = 1; public float value; }

    public class LRecord
    {
        public string id = "", colony = "", faction = "", world = "", ending = "going", worldKey = "";
        public int startYear, endYear, days, colonistsEver;
        public float wealth;
        public long savedAt;
        public int width, height;
        public List<LColonist> people = new List<LColonist>();
        public List<LBuilding> buildings = new List<LBuilding>();
        public List<LFloor> floors = new List<LFloor>();
        public List<LItem> items = new List<LItem>();
        public List<string> events = new List<string>();

        public string Years => startYear == endYear ? startYear.ToString() : startYear + "–" + endYear;
        public string EndingLabel => ("LG_End_" + ending).Translate();
    }

    public static class Store
    {
        public static string Folder
        {
            get
            {
                string f = Path.Combine(GenFilePaths.ConfigFolderPath, "Legacy");
                Directory.CreateDirectory(f);
                Directory.CreateDirectory(Path.Combine(f, "portraits"));
                return f;
            }
        }

        public static string PortraitPath(string file) => Path.Combine(Path.Combine(Folder, "portraits"), file);

        private static List<LRecord> cache;
        public static List<LRecord> All
        {
            get
            {
                if (cache != null) return cache;
                cache = new List<LRecord>();
                try
                {
                    foreach (string f in Directory.GetFiles(Folder, "*.xml"))
                    {
                        LRecord r = Load(f);
                        if (r != null) cache.Add(r);
                    }
                }
                catch (Exception e) { Log.Warning("[Legacy] reading the old colonies: " + e.Message); }
                cache = cache.OrderByDescending(r => r.savedAt).ToList();
                return cache;
            }
        }

        public static LRecord Get(string id) => All.FirstOrDefault(r => r.id == id);

        public static void Forget() { cache = null; Pictures.Clear(); }

        public static void Delete(LRecord r)
        {
            try
            {
                File.Delete(Path.Combine(Folder, r.id + ".xml"));
                foreach (LColonist c in r.people)
                    if (!c.portrait.NullOrEmpty() && File.Exists(PortraitPath(c.portrait))) File.Delete(PortraitPath(c.portrait));
            }
            catch (Exception e) { Log.Warning("[Legacy] delete: " + e.Message); }
            Forget();
        }

        // ---------- XML by hand (no Scribe: this runs while a game is loaded) ----------
        private static string F(float v) => v.ToString(CultureInfo.InvariantCulture);

        public static void Save(LRecord r)
        {
            BuildDoc(r).Save(Path.Combine(Folder, r.id + ".xml"));
            Forget();
        }

        // 0.1.1: the record as an XML document (the same for the Legacy folder and an export)
        private static XmlDocument BuildDoc(LRecord r)
        {
            var doc = new XmlDocument();
            XmlElement root = doc.CreateElement("Legacy");
            doc.AppendChild(root);
            void A(XmlElement e, string n, object v) { if (v != null) e.SetAttribute(n, v is float fv ? F(fv) : v.ToString()); }
            A(root, "version", LegacyMod.Version);
            A(root, "id", r.id); A(root, "colony", r.colony); A(root, "faction", r.faction); A(root, "world", r.world);
            A(root, "worldKey", r.worldKey); A(root, "ending", r.ending);
            A(root, "startYear", r.startYear); A(root, "endYear", r.endYear); A(root, "days", r.days);
            A(root, "colonistsEver", r.colonistsEver); A(root, "wealth", r.wealth); A(root, "savedAt", r.savedAt);
            A(root, "width", r.width); A(root, "height", r.height);
            XmlElement ppl = doc.CreateElement("people"); root.AppendChild(ppl);
            foreach (LColonist c in r.people)
            {
                XmlElement e = doc.CreateElement("p"); ppl.AppendChild(e);
                A(e, "id", c.id); A(e, "name", c.name); A(e, "nick", c.nick); A(e, "title", c.title); A(e, "childhood", c.childhood);
                A(e, "gender", c.gender); A(e, "age", c.age); A(e, "joined", c.joinedDay); A(e, "died", c.deathDay);
                A(e, "cause", c.deathCause); A(e, "kills", c.kills); A(e, "alive", c.alive); A(e, "portrait", c.portrait);
                foreach (string t in c.traits) { XmlElement x = doc.CreateElement("trait"); x.InnerText = t; e.AppendChild(x); }
                foreach (string t in c.skills) { XmlElement x = doc.CreateElement("skill"); x.InnerText = t; e.AppendChild(x); }
                foreach (string t in c.relations) { XmlElement x = doc.CreateElement("rel"); x.InnerText = t; e.AppendChild(x); }
            }
            XmlElement bs = doc.CreateElement("buildings"); root.AppendChild(bs);
            var sb = new StringBuilder();
            foreach (LBuilding b in r.buildings) sb.Append(b.def).Append(',').Append(b.stuff ?? "").Append(',').Append(b.x).Append(',').Append(b.z).Append(',').Append(b.rot).Append(';');
            bs.InnerText = sb.ToString();
            XmlElement fl = doc.CreateElement("floors"); root.AppendChild(fl);
            sb.Length = 0;
            foreach (LFloor f in r.floors) sb.Append(f.def).Append(',').Append(f.x).Append(',').Append(f.z).Append(';');
            fl.InnerText = sb.ToString();
            XmlElement its = doc.CreateElement("items"); root.AppendChild(its);
            foreach (LItem i in r.items)
            {
                XmlElement e = doc.CreateElement("i"); its.AppendChild(e);
                A(e, "def", i.def); A(e, "stuff", i.stuff); A(e, "quality", i.quality); A(e, "count", i.count); A(e, "value", i.value);
            }
            XmlElement ev = doc.CreateElement("events"); root.AppendChild(ev);
            foreach (string s in r.events) { XmlElement x = doc.CreateElement("e"); x.InnerText = s; ev.AppendChild(x); }
            return doc;
        }

        // ---------- 0.1.1: export / import (Config/Legacy/exports) ----------
        public static string ExportFolderPath => Path.Combine(Path.Combine(GenFilePaths.ConfigFolderPath, "Legacy"), "exports");

        public static string ExportFolder
        {
            get
            {
                string f = Path.Combine(Folder, "exports");   // Folder creates Config/Legacy first
                Directory.CreateDirectory(f);
                return f;
            }
        }

        public static List<string> ExportFiles()
        {
            try { return Directory.GetFiles(ExportFolder, "*.xml").OrderByDescending(File.GetLastWriteTime).ToList(); }
            catch (Exception e) { Log.Warning("[Legacy] exports: " + e.Message); return new List<string>(); }
        }

        // one self-contained file: the record plus its portraits (PNG as base64)
        public static string Export(LRecord r)
        {
            try
            {
                XmlDocument doc = BuildDoc(r);
                XmlElement pics = doc.CreateElement("portraits");
                doc.DocumentElement.AppendChild(pics);
                foreach (LColonist c in r.people)
                {
                    if (c.portrait.NullOrEmpty() || !File.Exists(PortraitPath(c.portrait))) continue;
                    XmlElement e = doc.CreateElement("img");
                    e.SetAttribute("file", c.portrait);
                    e.InnerText = Convert.ToBase64String(File.ReadAllBytes(PortraitPath(c.portrait)));
                    pics.AppendChild(e);
                }
                string name = GenText.SanitizeFilename((r.colony ?? "colony").Replace(' ', '_')) + "_" + r.id + ".xml";
                string path = Path.Combine(ExportFolder, name);
                doc.Save(path);
                return path;
            }
            catch (Exception e)
            {
                Log.Warning("[Legacy] export: " + e.Message);
                return null;
            }
        }

        // the file becomes one of the old colonies (a copy with a new id if that id is
        // already taken by a different record); portraits are written next to the others
        public static LRecord Import(string path, out bool already)
        {
            already = false;
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                LRecord r = Read(doc, Path.GetFileNameWithoutExtension(path));
                if (r == null) return null;
                LRecord same = Get(r.id);
                if (same != null)
                {
                    if (same.savedAt == r.savedAt && same.colony == r.colony) { already = true; return same; }
                    r.id = r.id + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                }
                foreach (XmlElement e in doc.DocumentElement.SelectNodes("portraits/img"))
                {
                    string file = e.GetAttribute("file");
                    if (file.NullOrEmpty() || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || file.Contains("..")) continue;
                    string to = PortraitPath(file);
                    if (!File.Exists(to)) File.WriteAllBytes(to, Convert.FromBase64String(e.InnerText));
                }
                Save(r);
                return Get(r.id) ?? r;
            }
            catch (Exception e)
            {
                Log.Warning("[Legacy] import " + Path.GetFileName(path) + ": " + e.Message);
                return null;
            }
        }

        private static LRecord Load(string path)
        {
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                return Read(doc, Path.GetFileNameWithoutExtension(path));
            }
            catch (Exception e)
            {
                Log.Warning("[Legacy] could not read " + Path.GetFileName(path) + ": " + e.Message);
                return null;
            }
        }

        private static LRecord Read(XmlDocument doc, string fallbackId)
        {
            try
            {
                XmlElement root = doc.DocumentElement;
                string S(XmlElement e, string n) => e.HasAttribute(n) ? e.GetAttribute(n) : null;
                int I(XmlElement e, string n) => int.TryParse(S(e, n), NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
                var r = new LRecord
                {
                    id = S(root, "id") ?? fallbackId,
                    colony = S(root, "colony") ?? "?", faction = S(root, "faction") ?? "", world = S(root, "world") ?? "",
                    worldKey = S(root, "worldKey") ?? "", ending = S(root, "ending") ?? "going",
                    startYear = I(root, "startYear"), endYear = I(root, "endYear"), days = I(root, "days"),
                    colonistsEver = I(root, "colonistsEver"), width = I(root, "width"), height = I(root, "height")
                };
                float.TryParse(S(root, "wealth"), NumberStyles.Float, CultureInfo.InvariantCulture, out r.wealth);
                long.TryParse(S(root, "savedAt"), out r.savedAt);
                foreach (XmlElement e in root.SelectNodes("people/p"))
                {
                    var c = new LColonist
                    {
                        id = I(e, "id"), name = S(e, "name") ?? "", nick = S(e, "nick") ?? "", title = S(e, "title") ?? "",
                        childhood = S(e, "childhood") ?? "", gender = I(e, "gender"), age = I(e, "age"), joinedDay = I(e, "joined"),
                        deathDay = I(e, "died"), deathCause = S(e, "cause") ?? "", kills = I(e, "kills"),
                        alive = S(e, "alive") != "False", portrait = S(e, "portrait")
                    };
                    foreach (XmlElement x in e.SelectNodes("trait")) c.traits.Add(x.InnerText);
                    foreach (XmlElement x in e.SelectNodes("skill")) c.skills.Add(x.InnerText);
                    foreach (XmlElement x in e.SelectNodes("rel")) c.relations.Add(x.InnerText);
                    r.people.Add(c);
                }
                string bt = root.SelectSingleNode("buildings")?.InnerText ?? "";
                foreach (string part in bt.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = part.Split(',');
                    if (p.Length < 5) continue;
                    r.buildings.Add(new LBuilding { def = p[0], stuff = p[1].Length == 0 ? null : p[1], x = int.Parse(p[2]), z = int.Parse(p[3]), rot = int.Parse(p[4]) });
                }
                string ft = root.SelectSingleNode("floors")?.InnerText ?? "";
                foreach (string part in ft.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] p = part.Split(',');
                    if (p.Length < 3) continue;
                    r.floors.Add(new LFloor { def = p[0], x = int.Parse(p[1]), z = int.Parse(p[2]) });
                }
                foreach (XmlElement e in root.SelectNodes("items/i"))
                {
                    var i = new LItem { def = S(e, "def"), stuff = S(e, "stuff"), quality = I(e, "quality"), count = Math.Max(1, I(e, "count")) };
                    float.TryParse(S(e, "value"), NumberStyles.Float, CultureInfo.InvariantCulture, out i.value);
                    if (!i.def.NullOrEmpty()) r.items.Add(i);
                }
                foreach (XmlElement e in root.SelectNodes("events/e")) r.events.Add(e.InnerText);
                return r;
            }
            catch (Exception e)
            {
                Log.Warning("[Legacy] could not read " + fallbackId + ": " + e.Message);
                return null;
            }
        }
    }

    // portraits as textures for the windows
    public static class Pictures
    {
        private static readonly Dictionary<string, Texture2D> loaded = new Dictionary<string, Texture2D>();

        public static Texture2D Get(string file)
        {
            if (file.NullOrEmpty()) return null;
            if (loaded.TryGetValue(file, out Texture2D t)) return t;
            t = null;
            try
            {
                string p = Store.PortraitPath(file);
                if (File.Exists(p))
                {
                    t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                    t.LoadImage(File.ReadAllBytes(p));
                }
            }
            catch { t = null; }
            loaded[file] = t;
            return t;
        }

        public static void Clear() => loaded.Clear();

        // the portrait is drawn into a texture of the game – read it back and keep it as a picture
        public static bool Shoot(Pawn p, string file)
        {
            try
            {
                RenderTexture rt = PortraitsCache.Get(p, new Vector2(92f, 128f), Rot4.South, default(Vector3), 1.1f, supersample: true, compensateForUIScale: false);
                if (rt == null) return false;
                RenderTexture prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;
                File.WriteAllBytes(Store.PortraitPath(file), tex.EncodeToPNG());
                UnityEngine.Object.Destroy(tex);
                loaded.Remove(file);
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Legacy] portrait of " + p.LabelShort + ": " + e.Message);
                return false;
            }
        }
    }

    // ---------------------------------------------------------------------------
    // The running game: who belonged to the colony, what happened, and the ruins of old colonies
    // ---------------------------------------------------------------------------
    public class LegacyGame : GameComponent
    {
        public string colonyId;
        public int startTick = -1;
        public bool written;            // ended and written down (no second automatic write)
        public bool ruinsPlaced;
        public List<string> placed = new List<string>();
        public List<string> events = new List<string>();
        public Dictionary<int, LColonist> roster = new Dictionary<int, LColonist>();
        private List<int> tmpKeys;
        private List<string> tmpVals;
        private readonly Queue<Pawn> toShoot = new Queue<Pawn>();

        public static LegacyGame Instance => Current.Game?.GetComponent<LegacyGame>();

        public LegacyGame(Game game) { }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref colonyId, "colonyId");
            Scribe_Values.Look(ref startTick, "startTick", -1);
            Scribe_Values.Look(ref written, "written");
            Scribe_Values.Look(ref ruinsPlaced, "ruinsPlaced");
            Scribe_Collections.Look(ref placed, "placed", LookMode.Value);
            Scribe_Collections.Look(ref events, "events", LookMode.Value);
            // the roster as text lines (plain data, no references to pawns that may be gone)
            if (Scribe.mode == LoadSaveMode.Saving)
            {
                tmpKeys = roster.Keys.ToList();
                tmpVals = roster.Values.Select(Pack).ToList();
            }
            Scribe_Collections.Look(ref tmpKeys, "rosterIds", LookMode.Value);
            Scribe_Collections.Look(ref tmpVals, "roster", LookMode.Value);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                roster = new Dictionary<int, LColonist>();
                if (tmpKeys != null && tmpVals != null)
                    for (int i = 0; i < Math.Min(tmpKeys.Count, tmpVals.Count); i++)
                    {
                        LColonist c = Unpack(tmpVals[i]);
                        if (c != null) roster[tmpKeys[i]] = c;
                    }
                placed = placed ?? new List<string>();
                events = events ?? new List<string>();
            }
        }

        private static string Esc(string s) => (s ?? "").Replace("|", "/").Replace("\n", " ").Replace("~", "-");

        private static string Pack(LColonist c) => string.Join("|", new[]
        {
            c.id.ToString(), Esc(c.name), Esc(c.nick), Esc(c.title), Esc(c.childhood), c.gender.ToString(), c.age.ToString(),
            c.joinedDay.ToString(), c.deathDay.ToString(), Esc(c.deathCause), c.kills.ToString(), c.alive ? "1" : "0", Esc(c.portrait),
            string.Join("~", c.traits.Select(Esc).ToArray()), string.Join("~", c.skills.Select(Esc).ToArray()), string.Join("~", c.relations.Select(Esc).ToArray())
        });

        private static LColonist Unpack(string s)
        {
            try
            {
                string[] p = s.Split('|');
                if (p.Length < 16) return null;
                List<string> L(string x) => x.Length == 0 ? new List<string>() : x.Split('~').ToList();
                return new LColonist
                {
                    id = int.Parse(p[0]), name = p[1], nick = p[2], title = p[3], childhood = p[4], gender = int.Parse(p[5]), age = int.Parse(p[6]),
                    joinedDay = int.Parse(p[7]), deathDay = int.Parse(p[8]), deathCause = p[9], kills = int.Parse(p[10]), alive = p[11] == "1",
                    portrait = p[12].Length == 0 ? null : p[12], traits = L(p[13]), skills = L(p[14]), relations = L(p[15])
                };
            }
            catch { return null; }
        }

        public static int Day => GenDate.DaysPassed;

        public void Event(string text)
        {
            events.Add(Find.TickManager != null ? "LG_Day".Translate(Day + 1) + ": " + text : text);
            if (events.Count > 400) events.RemoveRange(0, events.Count - 400);
        }

        public override void FinalizeInit()
        {
            if (colonyId.NullOrEmpty()) colonyId = Guid.NewGuid().ToString("N").Substring(0, 12);
            if (startTick < 0) startTick = Find.TickManager.TicksGame;
            LegacyButton.Refresh();
        }

        public override void GameComponentTick()
        {
            int t = Find.TickManager.TicksGame;
            if (!ruinsPlaced && t % 250 == 17)
            {
                ruinsPlaced = true;
                try { Ruins.PlaceForThisWorld(this); } catch (Exception e) { Log.Warning("[Legacy] placing ruins: " + e); }
            }
            if (t % 2500 == 31)
            {
                try { UpdateRoster(); } catch (Exception e) { Log.Warning("[Legacy] roster: " + e.Message); }
            }
        }

        // portraits are drawn on the GUI pass, one per frame
        public override void GameComponentOnGUI()
        {
            if (toShoot.Count == 0 || UnityEngine.Event.current.type != EventType.Repaint) return;
            Pawn p = toShoot.Dequeue();
            if (p == null || p.Dead || !roster.TryGetValue(p.thingIDNumber, out LColonist c)) return;
            string file = colonyId + "_" + p.thingIDNumber + ".png";
            if (Pictures.Shoot(p, file)) c.portrait = file;
        }

        public void UpdateRoster()
        {
            foreach (Pawn p in PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists)
                Touch(p);
        }

        public LColonist Touch(Pawn p)
        {
            if (p == null || !p.RaceProps.Humanlike) return null;
            bool isNew = !roster.TryGetValue(p.thingIDNumber, out LColonist c);
            if (isNew)
            {
                c = new LColonist { id = p.thingIDNumber, joinedDay = Day };
                roster[p.thingIDNumber] = c;
                if (Find.TickManager.TicksGame - startTick > 2500) Event("LG_Ev_Joined".Translate(p.LabelShort));
            }
            Fill(c, p);
            if (isNew || c.portrait.NullOrEmpty() || (Day % 10 == 0 && Find.TickManager.TicksGame % 60000 < 2500)) toShoot.Enqueue(p);
            return c;
        }

        public static void Fill(LColonist c, Pawn p)
        {
            c.name = p.Name?.ToStringFull ?? p.LabelShort;
            c.nick = (p.Name as NameTriple)?.Nick ?? p.LabelShort;
            c.gender = (int)p.gender;
            c.age = p.ageTracker?.AgeBiologicalYears ?? 0;
            c.title = p.story?.Adulthood?.TitleCapFor(p.gender) ?? p.story?.Childhood?.TitleCapFor(p.gender) ?? "";
            c.childhood = p.story?.Childhood?.TitleCapFor(p.gender) ?? "";
            c.traits = p.story?.traits?.allTraits?.Select(t => (string)t.LabelCap).ToList() ?? new List<string>();
            c.skills = p.skills?.skills?.Where(s => !s.TotallyDisabled).OrderByDescending(s => s.Level).Take(3)
                .Select(s => s.def.LabelCap + " " + s.Level + (s.passion == Passion.Major ? " 🔥🔥" : s.passion == Passion.Minor ? " 🔥" : "")).ToList() ?? new List<string>();
            c.kills = (int)(p.records?.GetValue(RecordDefOf.Kills) ?? 0f);
            var rel = new List<string>();
            if (p.relations != null)
                foreach (DirectPawnRelation d in p.relations.DirectRelations)
                    if (d.otherPawn != null && (d.otherPawn.IsColonist || d.otherPawn.Dead))
                        rel.Add(d.def.GetGenderSpecificLabelCap(d.otherPawn) + ": " + d.otherPawn.LabelShort);
            c.relations = rel.Take(6).ToList();
        }

        public void Died(Pawn p, string cause)
        {
            LColonist c = Touch(p);
            if (c == null || !c.alive) return;
            c.alive = false;
            c.deathDay = Day;
            c.deathCause = cause ?? "";
            Event("LG_Ev_Died".Translate(p.LabelShort, cause.NullOrEmpty() ? "?" : cause));
        }

        // ---------- writing the colony down ----------
        public LRecord Write(string ending, Map onlyMap = null)
        {
            UpdateRoster();
            Map map = onlyMap ?? Find.Maps.Where(m => m.IsPlayerHome).OrderByDescending(m => m.listerBuildings.allBuildingsColonist.Count).FirstOrDefault();
            var r = new LRecord
            {
                id = colonyId,
                colony = map?.Parent?.LabelCap ?? Faction.OfPlayer.Name,
                faction = Faction.OfPlayer.Name,
                world = Find.World?.info?.name ?? "",
                worldKey = WorldKey,
                ending = ending,
                days = Day,
                savedAt = DateTime.Now.Ticks,
                colonistsEver = roster.Count,
                events = events.ToList()
            };
            float lon = map != null ? Find.WorldGrid.LongLatOf(map.Tile).x : 0f;
            r.startYear = GenDate.Year(GenDate.TickGameToAbs(Math.Max(0, startTick)), lon);
            r.endYear = GenDate.Year(Find.TickManager.TicksAbs, lon);
            r.people = roster.Values.OrderBy(c => c.joinedDay).ToList();
            if (map != null)
            {
                r.wealth = map.wealthWatcher.WealthTotal;
                Plan.Capture(map, r);
            }
            Store.Save(r);
            Messages.Message("LG_Written".Translate(r.colony), MessageTypeDefOf.PositiveEvent, false);
            return r;
        }

        public static string WorldKey => Find.World == null ? "" : Find.World.info.seedString + "#" + Find.World.info.persistentRandomValue;
    }

    // ---------------------------------------------------------------------------
    // The base: buildings, floors and the most valuable things of the home map
    // ---------------------------------------------------------------------------
    public static class Plan
    {
        public const int MaxSize = 110, MaxBuildings = 5000;

        public static bool Wanted(ThingDef d)
        {
            if (d?.building == null || d.category != ThingCategory.Building) return false;
            if (d.building.isNaturalRock || d.building.isTrap || !d.destroyable) return false;
            if (typeof(Building_Grave).IsAssignableFrom(d.thingClass)) return false;
            if (d.IsFrame || d.isUnfinishedThing) return false;
            return true;
        }

        public static void Capture(Map map, LRecord r)
        {
            List<Building> bs = map.listerBuildings.allBuildingsColonist.Where(b => b != null && b.Spawned && Wanted(b.def)).ToList();
            if (bs.Count == 0) return;
            // the core of the base: around the middle of all buildings, at most MaxSize wide
            IntVec3 mid = new IntVec3((int)bs.Average(b => b.Position.x), 0, (int)bs.Average(b => b.Position.z));
            CellRect all = CellRect.FromLimits(bs.Min(b => b.OccupiedRect().minX), bs.Min(b => b.OccupiedRect().minZ), bs.Max(b => b.OccupiedRect().maxX), bs.Max(b => b.OccupiedRect().maxZ));
            int w = Math.Min(all.Width, MaxSize), h = Math.Min(all.Height, MaxSize);
            int minX = Mathf.Clamp(mid.x - w / 2, all.minX, all.maxX - w + 1), minZ = Mathf.Clamp(mid.z - h / 2, all.minZ, all.maxZ - h + 1);
            CellRect box = new CellRect(minX, minZ, w, h);
            r.width = w; r.height = h;
            foreach (Building b in bs)
            {
                if (r.buildings.Count >= MaxBuildings) break;
                CellRect o = b.OccupiedRect();
                if (o.minX < box.minX || o.maxX > box.maxX || o.minZ < box.minZ || o.maxZ > box.maxZ) continue;
                r.buildings.Add(new LBuilding { def = b.def.defName, stuff = b.Stuff?.defName, x = b.Position.x - box.minX, z = b.Position.z - box.minZ, rot = b.Rotation.AsInt });
            }
            foreach (IntVec3 c in box)
            {
                TerrainDef t = map.terrainGrid.TerrainAt(c);
                if (t != null && t.BuildableByPlayer && !t.natural) r.floors.Add(new LFloor { def = t.defName, x = c.x - box.minX, z = c.z - box.minZ });
            }
            // treasures: the most valuable things lying in the home area
            var seen = new List<LItem>();
            foreach (Thing t in map.listerThings.ThingsInGroup(ThingRequestGroup.HaulableEver))
            {
                if (t is Corpse || t is MinifiedThing || (t.def.thingCategories?.Contains(ThingCategoryDefOf.Chunks) ?? false) || !map.areaManager.Home[t.Position]) continue;
                float v = t.MarketValue * t.stackCount;
                if (v < 50f) continue;
                QualityCategory q;
                seen.Add(new LItem { def = t.def.defName, stuff = t.Stuff?.defName, quality = t.TryGetQuality(out q) ? (int)q : -1, count = t.stackCount, value = v });
            }
            r.items = seen.OrderByDescending(i => i.value).Take(20).ToList();
        }
    }

    // ---------------------------------------------------------------------------
    // Ruins of old colonies in this world
    // ---------------------------------------------------------------------------
    public static class Ruins
    {
        public static void PlaceForThisWorld(LegacyGame g)
        {
            int want = LegacyMod.S.ruinsPerWorld;
            if (want <= 0) return;
            string here = LegacyGame.WorldKey;
            List<LRecord> old = Store.All.Where(r => r.id != g.colonyId && r.worldKey != here && !g.placed.Contains(r.id) && r.buildings.Count + r.people.Count > 0)
                .OrderByDescending(r => r.savedAt).Take(want).ToList();
            var names = new List<string>();
            foreach (LRecord r in old)
            {
                if (!TileFinder.TryFindNewSiteTile(out PlanetTile tile, 6, 30)) continue;
                var ruins = (LegacyRuins)WorldObjectMaker.MakeWorldObject(LegacyDefOf.Legacy_Ruins);
                ruins.Tile = tile;
                ruins.recordId = r.id;
                ruins.colonyName = r.colony;
                Find.WorldObjects.Add(ruins);
                g.placed.Add(r.id);
                names.Add(r.colony + " (" + r.Years + ")");
            }
            if (names.Count > 0)
                Find.LetterStack.ReceiveLetter("LG_LetterRuins".Translate(), "LG_LetterRuinsText".Translate(string.Join("\n", names.Select(n => "  • " + n).ToArray())), LetterDefOf.PositiveEvent);
        }
    }

    [DefOf]
    public static class LegacyDefOf
    {
        public static WorldObjectDef Legacy_Ruins;
        public static GenStepDef Legacy_RuinsGen;
        static LegacyDefOf() { DefOfHelper.EnsureInitializedInCtor(typeof(LegacyDefOf)); }
    }

    public class LegacyRuins : MapParent
    {
        public string recordId, colonyName;
        public bool looted;

        public LRecord Record => Store.Get(recordId);

        public override string Label => "LG_RuinsOf".Translate(colonyName ?? "?");

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref recordId, "recordId");
            Scribe_Values.Look(ref colonyName, "colonyName");
            Scribe_Values.Look(ref looted, "looted");
        }

        public override IEnumerable<GenStepWithParams> ExtraGenStepDefs
        {
            get { yield return new GenStepWithParams(LegacyDefOf.Legacy_RuinsGen, default(GenStepParams)); }
        }

        public override string GetInspectString()
        {
            LRecord r = Record;
            string s = base.GetInspectString();
            string mine = r == null ? "LG_RecordMissing".Translate().ToString()
                : "LG_RuinsInspect".Translate(r.Years, r.people.Count, r.people.Count(p => !p.alive), r.EndingLabel).ToString();
            return s.NullOrEmpty() ? mine : s + "\n" + mine;
        }

        public override IEnumerable<Gizmo> GetGizmos()
        {
            foreach (Gizmo g in base.GetGizmos()) yield return g;
            LRecord r = Record;
            if (r != null)
                yield return new Command_Action
                {
                    defaultLabel = "LG_Chronicle".Translate(),
                    defaultDesc = "LG_ChronicleDesc".Translate(),
                    icon = TexButton.Info,
                    action = () => Find.WindowStack.Add(new Dialog_Story(r))
                };
        }

        public override IEnumerable<FloatMenuOption> GetFloatMenuOptions(Caravan caravan)
        {
            foreach (FloatMenuOption o in base.GetFloatMenuOptions(caravan)) yield return o;
            if (HasMap) yield break;
            foreach (FloatMenuOption o in CaravanArrivalActionUtility.GetFloatMenuOptions(() => true, () => new CaravanArrivalAction_VisitRuins(this),
                "LG_Visit".Translate(Label), caravan, Tile, this))
                yield return o;
        }

        public override bool ShouldRemoveMapNow(out bool alsoRemoveWorldObject)
        {
            alsoRemoveWorldObject = false;
            return HasMap && !Map.mapPawns.AnyPawnBlockingMapRemoval;
        }

        public override void PostMapGenerate()
        {
            base.PostMapGenerate();
            looted = true;
        }
    }

    public class CaravanArrivalAction_VisitRuins : CaravanArrivalAction
    {
        private LegacyRuins ruins;

        public CaravanArrivalAction_VisitRuins() { }
        public CaravanArrivalAction_VisitRuins(LegacyRuins r) { ruins = r; }

        public override string Label => "LG_Visit".Translate(ruins?.Label ?? "");
        public override string ReportString => "LG_Visiting".Translate(ruins?.Label ?? "");

        public override FloatMenuAcceptanceReport StillValid(Caravan caravan, PlanetTile destinationTile)
        {
            FloatMenuAcceptanceReport rep = base.StillValid(caravan, destinationTile);
            if (!rep) return rep;
            return ruins != null && !ruins.Destroyed && ruins.Tile == destinationTile;
        }

        public override void Arrived(Caravan caravan)
        {
            LongEventHandler.QueueLongEvent(() => Enter(caravan), "GeneratingMapForNewEncounter", false, null);
        }

        private void Enter(Caravan caravan)
        {
            bool first = !ruins.looted;
            Map map = GetOrGenerateMapUtility.GetOrGenerateMap(ruins.Tile, Find.World.info.initialMapSize, null);
            CaravanEnterMapUtility.Enter(caravan, map, CaravanEnterMode.Edge, CaravanDropInventoryMode.DoNotDrop, false);
            LRecord r = ruins.Record;
            Find.LetterStack.ReceiveLetter("LG_LetterArrived".Translate(ruins.colonyName), "LG_LetterArrivedText".Translate(ruins.colonyName, r?.Years ?? "?"), LetterDefOf.NeutralEvent, new LookTargets(caravan.PawnsListForReading));
            if (first && r != null) Find.WindowStack.Add(new Dialog_Story(r));
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_References.Look(ref ruins, "ruins");
        }
    }

    // the old base, worn by time: some walls stand, some lie in rubble; graves with names; a part of the treasures
    public class GenStep_LegacyRuins : GenStep
    {
        public override int SeedPart => 735191408;

        public override void Generate(Map map, GenStepParams parms)
        {
            if (!(map.Parent is LegacyRuins ruins)) return;
            LRecord r = ruins.Record;
            if (r == null) return;
            bool first = !ruins.looted;
            int w = Math.Min(r.width, map.Size.x - 10), h = Math.Min(r.height, map.Size.z - 10);
            IntVec3 o = new IntVec3((map.Size.x - w) / 2, 0, (map.Size.z - h) / 2);
            CellRect box = new CellRect(o.x, o.z, w, h);
            // a clearing for the base
            foreach (IntVec3 c in box.ExpandedBy(1))
            {
                if (!c.InBounds(map)) continue;
                foreach (Thing t in c.GetThingList(map).ToList())
                    if (t.def.category == ThingCategory.Plant || (t.def.building != null && t.def.building.isNaturalRock)) t.Destroy();
                RoofDef roof = map.roofGrid.RoofAt(c);
                if (roof != null && !roof.isThickRoof) map.roofGrid.SetRoof(c, null);
            }
            foreach (LFloor f in r.floors)
            {
                if (!Rand.Chance(0.55f)) continue;
                TerrainDef t = DefDatabase<TerrainDef>.GetNamedSilentFail(f.def);
                IntVec3 c = o + new IntVec3(f.x, 0, f.z);
                if (t == null || !c.InBounds(map) || map.terrainGrid.TerrainAt(c).IsWater) continue;
                try { map.terrainGrid.SetTerrain(c, t); } catch { }
            }
            int made = 0, fell = 0;
            foreach (LBuilding b in r.buildings)
            {
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(b.def);
                if (!Plan.Wanted(d)) continue;
                bool wall = d.passability == Traversability.Impassable && d.size.x == 1 && d.size.z == 1 && !d.IsDoor;
                float keep = wall ? 0.7f : d.IsDoor ? 0.45f : d.Minifiable ? 0.3f : 0.22f;
                IntVec3 c = o + new IntVec3(b.x, 0, b.z);
                Rot4 rot = new Rot4(b.rot);
                if (!Rand.Chance(keep))
                {
                    if (wall && c.InBounds(map)) { FilthMaker.TryMakeFilth(c, map, ThingDefOf.Filth_RubbleBuilding); fell++; }
                    continue;
                }
                CellRect occ = GenAdj.OccupiedRect(c, rot, d.size);
                if (!occ.InBounds(map) || !Inside(occ, box.ExpandedBy(1))) continue;
                ThingDef stuff = d.MadeFromStuff ? (DefDatabase<ThingDef>.GetNamedSilentFail(b.stuff ?? "") ?? GenStuff.DefaultStuffFor(d)) : null;
                if (d.MadeFromStuff && stuff == null) continue;
                try
                {
                    if (!GenConstruct.CanBuildOnTerrain(d, c, map, rot, null, stuff)) continue;
                    Thing t = ThingMaker.MakeThing(d, stuff);
                    t.HitPoints = Mathf.Max(1, Mathf.RoundToInt(t.MaxHitPoints * Rand.Range(0.15f, 0.7f)));
                    GenSpawn.Spawn(t, c, map, rot, WipeMode.Vanish);
                    if (t.def.CanHaveFaction) t.SetFaction(null);
                    made++;
                }
                catch { }
            }
            // rubble and dust over the floor
            foreach (IntVec3 c in box)
                if (Rand.Chance(0.06f) && c.Standable(map)) FilthMaker.TryMakeFilth(c, map, ThingDefOf.Filth_RubbleBuilding);
            if (first && LegacyMod.S.loot) Treasures(map, r, box);
            if (first && LegacyMod.S.graves) Graves(map, r, box);
        }

        private static bool Inside(CellRect a, CellRect b) => a.minX >= b.minX && a.maxX <= b.maxX && a.minZ >= b.minZ && a.maxZ <= b.maxZ;

        private static IntVec3 FreeCell(Map map, CellRect box)
        {
            for (int i = 0; i < 60; i++)
            {
                IntVec3 c = box.RandomCell;
                if (c.InBounds(map) && c.Standable(map) && c.GetFirstItem(map) == null && c.GetEdifice(map) == null) return c;
            }
            return IntVec3.Invalid;
        }

        private static void Treasures(Map map, LRecord r, CellRect box)
        {
            foreach (LItem i in r.items)
            {
                if (!Rand.Chance(0.4f)) continue;
                ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(i.def);
                if (d == null) continue;
                ThingDef stuff = d.MadeFromStuff ? (DefDatabase<ThingDef>.GetNamedSilentFail(i.stuff ?? "") ?? GenStuff.DefaultStuffFor(d)) : null;
                IntVec3 c = FreeCell(map, box);
                if (!c.IsValid) continue;
                try
                {
                    Thing t = ThingMaker.MakeThing(d, stuff);
                    t.stackCount = Mathf.Clamp(Mathf.RoundToInt(i.count * Rand.Range(0.3f, 0.8f)), 1, d.stackLimit);
                    if (i.quality >= 0) t.TryGetComp<CompQuality>()?.SetQuality((QualityCategory)i.quality, ArtGenerationContext.Outsider);
                    if (t.def.useHitPoints) t.HitPoints = Mathf.Max(1, Mathf.RoundToInt(t.MaxHitPoints * Rand.Range(0.4f, 0.9f)));
                    GenSpawn.Spawn(t, c, map);
                    t.SetForbidden(true, false);
                }
                catch { }
            }
        }

        // a small cemetery next to the base
        private static void Graves(Map map, LRecord r, CellRect box)
        {
            List<LColonist> dead = r.people.Where(p => !p.alive).Take(16).ToList();
            if (dead.Count == 0) return;
            IntVec3 start = new IntVec3(box.maxX + 3, 0, box.minZ);
            if (start.x + 2 >= map.Size.x - 3) start = new IntVec3(Math.Max(3, box.minX - 6), 0, box.minZ);
            int n = 0;
            foreach (LColonist p in dead)
            {
                IntVec3 c = start + new IntVec3((n / 6) * 3, 0, (n % 6) * 3);
                n++;
                if (!c.InBounds(map)) continue;
                try
                {
                    CellRect occ = GenAdj.OccupiedRect(c, Rot4.North, ThingDefOf.Grave.size);
                    if (!occ.InBounds(map)) continue;
                    foreach (IntVec3 x in occ)
                        foreach (Thing t in x.GetThingList(map).ToList())
                            if (t.def.category == ThingCategory.Plant || (t.def.building?.isNaturalRock ?? false)) t.Destroy();
                    var grave = (Building_Grave)ThingMaker.MakeThing(ThingDefOf.Grave);
                    GenSpawn.Spawn(grave, c, map, Rot4.North, WipeMode.Vanish);
                    Pawn body = PawnGenerator.GeneratePawn(new PawnGenerationRequest(PawnKindDefOf.Colonist, null, PawnGenerationContext.NonPlayer,
                        forceGenerateNewPawn: true, canGeneratePawnRelations: false, fixedGender: (Gender)p.gender,
                        fixedBiologicalAge: Mathf.Max(18, p.age), fixedChronologicalAge: Mathf.Max(18, p.age), forceNoGear: true));
                    string[] parts = p.name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    body.Name = parts.Length >= 2 ? new NameTriple(parts[0], p.nick.NullOrEmpty() ? parts[0] : p.nick, parts[parts.Length - 1]) : (Name)new NameSingle(p.name);
                    body.Kill(null);
                    Corpse corpse = body.Corpse;
                    if (corpse != null)
                    {
                        corpse.holdingOwner?.Remove(corpse);
                        if (grave.TryAcceptThing(corpse, false))
                        {
                            CompRottable rot = corpse.TryGetComp<CompRottable>();
                            if (rot != null) rot.RotProgress = 1e8f;
                        }
                    }
                }
                catch (Exception e) { Log.Warning("[Legacy] grave of " + p.name + ": " + e.Message); }
            }
        }
    }

    // ---------------------------------------------------------------------------
    // Harmony: deaths, the end of the colony, abandoning the last home
    // ---------------------------------------------------------------------------
    public static class Patches
    {
        public static void Apply(Harmony h)
        {
            void P(Type t, string m, string pre, string post, Type[] args = null)
            {
                try
                {
                    var mi = args == null ? AccessTools.Method(t, m) : AccessTools.Method(t, m, args);
                    if (mi == null) { Log.Warning("[Legacy] not found: " + t.Name + "." + m); return; }
                    h.Patch(mi, pre == null ? null : new HarmonyMethod(typeof(Patches), pre), post == null ? null : new HarmonyMethod(typeof(Patches), post));
                }
                catch (Exception e) { Log.Warning("[Legacy] patch " + t.Name + "." + m + ": " + e.Message); }
            }
            P(typeof(Pawn), nameof(Pawn.Kill), nameof(KillPrefix), null);
            P(typeof(GameEnder), nameof(GameEnder.CheckOrUpdateGameOver), null, nameof(GameOverPostfix));
            P(typeof(MapParent), nameof(MapParent.Abandon), nameof(AbandonPrefix), null);
        }

        public static void KillPrefix(Pawn __instance, DamageInfo? dinfo, Hediff exactCulprit)
        {
            try
            {
                if (__instance.Dead || !__instance.IsFreeColonist || __instance.Faction != Faction.OfPlayer) return;
                LegacyGame g = LegacyGame.Instance;
                if (g == null) return;
                string cause = exactCulprit?.LabelCap ?? (dinfo.HasValue ? dinfo.Value.Def?.LabelCap.ToString() : null);
                if (cause.NullOrEmpty()) cause = __instance.health?.hediffSet?.hediffs?.Where(x => x.Visible).OrderByDescending(x => x.Severity).FirstOrDefault()?.LabelCap;
                g.Died(__instance, cause);
            }
            catch { }
        }

        public static void GameOverPostfix(GameEnder __instance)
        {
            try
            {
                LegacyGame g = LegacyGame.Instance;
                if (g == null || g.written || !__instance.gameEnding || !LegacyMod.S.autoOnEnd) return;
                g.written = true;
                g.Event("LG_Ev_End".Translate());
                g.Write("dead");
            }
            catch (Exception e) { Log.Warning("[Legacy] writing the colony: " + e.Message); }
        }

        public static void AbandonPrefix(MapParent __instance, bool wasGravshipLaunch)
        {
            try
            {
                if (wasGravshipLaunch) return;      // the colony flies on, it does not end
                LegacyGame g = LegacyGame.Instance;
                Map map = __instance.Map;
                if (g == null || g.written || map == null || !map.IsPlayerHome || !LegacyMod.S.autoOnAbandon) return;
                if (Find.Maps.Any(m => m != map && m.IsPlayerHome)) return;      // not the last home
                g.Event("LG_Ev_Abandoned".Translate(__instance.LabelCap));
                g.Write("abandoned", map);
            }
            catch (Exception e) { Log.Warning("[Legacy] writing the colony: " + e.Message); }
        }
    }

    public static class LegacyButton
    {
        public static void Refresh()
        {
            MainButtonDef d = DefDatabase<MainButtonDef>.GetNamedSilentFail("Legacy");
            if (d != null) d.buttonVisible = LegacyMod.S?.showButton ?? true;
        }
    }

    // ---------------------------------------------------------------------------
    // Windows
    // ---------------------------------------------------------------------------
    public static class StoryDrawer
    {
        public static void Draw(Rect r, LRecord rec, ref Vector2 scroll)
        {
            float w = r.width - 16f;
            float h = 110f + rec.people.Count * 118f + 40f + rec.events.Count * 22f + 60f;
            Rect view = new Rect(0f, 0f, w, h);
            Widgets.BeginScrollView(r, ref scroll, view);
            float y = 0f;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, y, w, 34f), rec.colony);
            Text.Font = GameFont.Small;
            y += 36f;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(0f, y, w, 22f), "LG_Summary".Translate(rec.Years, rec.days, rec.people.Count, rec.people.Count(p => !p.alive), rec.EndingLabel));
            y += 22f;
            Widgets.Label(new Rect(0f, y, w, 22f), "LG_Summary2".Translate(rec.faction, rec.world, rec.wealth.ToStringMoney(), rec.buildings.Count));
            GUI.color = Color.white;
            y += 34f;
            foreach (LColonist c in rec.people)
            {
                Rect card = new Rect(0f, y, w, 112f);
                MUI.Card(card);
                Texture2D pic = Pictures.Get(c.portrait);
                Rect pr = new Rect(card.x + 6f, card.y + 4f, 76f, 104f);
                if (pic != null)
                {
                    if (!c.alive) GUI.color = new Color(0.75f, 0.75f, 0.75f, 0.85f);
                    GUI.DrawTexture(pr, pic, ScaleMode.ScaleToFit);
                    GUI.color = Color.white;
                }
                float x = pr.xMax + 10f, cw = card.xMax - x - 8f;
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(x, card.y + 4f, cw, 24f), "<b>" + c.name + "</b>" + (c.alive ? "" : "  †"));
                GUI.color = MUI.Muted;
                Text.Font = GameFont.Tiny;
                string line1 = "LG_Age".Translate(c.age).ToString() + (c.title.NullOrEmpty() ? "" : " · " + c.title) + (c.kills > 0 ? " · " + "LG_Kills".Translate(c.kills) : "");
                Widgets.Label(new Rect(x, card.y + 28f, cw, 18f), line1);
                Widgets.Label(new Rect(x, card.y + 46f, cw, 18f), string.Join(", ", c.traits.ToArray()));
                Widgets.Label(new Rect(x, card.y + 64f, cw, 18f), string.Join(" · ", c.skills.ToArray()));
                GUI.color = c.alive ? MUI.Accent : MUI.Warn;
                string fate = c.alive ? "LG_Alive".Translate().ToString() : "LG_DiedOn".Translate(c.deathDay + 1, c.deathCause.NullOrEmpty() ? "?" : c.deathCause).ToString();
                if (c.relations.Count > 0) fate += "   ·   " + string.Join(", ", c.relations.ToArray());
                Widgets.Label(new Rect(x, card.y + 84f, cw, 22f), fate.Truncate(cw));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                y += 118f;
            }
            y += 8f;
            if (rec.events.Count > 0)
            {
                Widgets.Label(new Rect(0f, y, w, 26f), "<b>" + "LG_Events".Translate() + "</b>");
                y += 28f;
                Text.Font = GameFont.Tiny;
                foreach (string e in rec.events)
                {
                    Widgets.Label(new Rect(8f, y, w - 8f, 22f), e);
                    y += 22f;
                }
                Text.Font = GameFont.Small;
            }
            Widgets.EndScrollView();
        }
    }

    public class Dialog_Story : Window
    {
        private readonly LRecord rec;
        private Vector2 scroll;
        public override Vector2 InitialSize => new Vector2(820f, 760f);

        public Dialog_Story(LRecord r)
        {
            rec = r;
            doCloseX = true;
            doCloseButton = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect r)
        {
            StoryDrawer.Draw(new Rect(r.x, r.y, r.width, r.height - 50f), rec, ref scroll);
        }
    }

    public class MainTabWindow_Legacy : MainTabWindow
    {
        private LRecord sel;
        private bool showCurrent = true;
        private Vector2 listScroll, storyScroll;
        public override Vector2 RequestedTabSize => new Vector2(1060f, 720f);

        // the current colony as a record, without writing it to disk
        private LRecord Current()
        {
            LegacyGame g = LegacyGame.Instance;
            if (g == null) return null;
            g.UpdateRoster();
            return new LRecord
            {
                id = g.colonyId, colony = Find.Maps.FirstOrDefault(m => m.IsPlayerHome)?.Parent?.LabelCap ?? Faction.OfPlayer.Name,
                faction = Faction.OfPlayer.Name, world = Find.World?.info?.name ?? "", days = LegacyGame.Day, ending = "going",
                startYear = GenDate.Year(GenDate.TickGameToAbs(Math.Max(0, g.startTick)), 0f), endYear = GenDate.Year(Find.TickManager.TicksAbs, 0f),
                people = g.roster.Values.OrderBy(c => c.joinedDay).ToList(), events = g.events.ToList(),
                wealth = Find.Maps.Where(m => m.IsPlayerHome).Sum(m => m.wealthWatcher.WealthTotal)
            };
        }

        private LRecord cur;
        private float curAt;

        public override void DoWindowContents(Rect r)
        {
            Rect left = new Rect(r.x, r.y, 280f, r.height);
            Rect right = new Rect(left.xMax + 12f, r.y, r.width - left.width - 12f, r.height);
            MUI.Card(left);
            Rect lc = left.ContractedBy(8f);
            float y = lc.y;
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(lc.x, y, lc.width, 36f), "LG_TabHint".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            y += 38f;
            Rect thisR = new Rect(lc.x, y, lc.width, 30f);
            if (showCurrent) MUI.Fill(thisR, MUI.OnBg);
            if (Widgets.ButtonText(thisR, "LG_ThisColony".Translate(), drawBackground: !showCurrent)) { showCurrent = true; storyScroll = Vector2.zero; }
            y += 34f;
            if (Widgets.ButtonText(new Rect(lc.x, y, lc.width, 30f), "LG_WriteNow".Translate()))
            {
                LegacyGame g = LegacyGame.Instance;
                if (g != null)
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("LG_WriteConfirm".Translate(), () => { g.Write("going"); }));
            }
            TooltipHandler.TipRegion(new Rect(lc.x, y, lc.width, 30f), "LG_WriteNowTip".Translate());
            y += 34f;
            // 0.1.1: export the shown chronicle / import one from Config/Legacy/exports
            float half = (lc.width - 4f) / 2f;
            Rect exR = new Rect(lc.x, y, half, 30f), imR = new Rect(lc.x + half + 4f, y, half, 30f);
            LRecord shown = showCurrent ? cur : sel;
            if (Widgets.ButtonText(exR, "LG_Export".Translate()))
            {
                if (shown == null)
                    Messages.Message("LG_ExportNothing".Translate(), MessageTypeDefOf.RejectInput, false);
                else
                {
                    string path = Store.Export(shown);
                    if (path != null) Messages.Message("LG_Exported".Translate(shown.colony, path), MessageTypeDefOf.PositiveEvent, false);
                    else Messages.Message("LG_ExportFailed".Translate(), MessageTypeDefOf.RejectInput, false);
                }
            }
            TooltipHandler.TipRegion(exR, "LG_ExportTip".Translate(Store.ExportFolderPath));
            if (Widgets.ButtonText(imR, "LG_Import".Translate()))
            {
                var menu = new List<FloatMenuOption>();
                foreach (string f in Store.ExportFiles())
                {
                    string local = f;
                    menu.Add(new FloatMenuOption(Path.GetFileNameWithoutExtension(f), () =>
                    {
                        LRecord got = Store.Import(local, out bool already);
                        if (got == null) Messages.Message("LG_ImportFailed".Translate(Path.GetFileName(local)), MessageTypeDefOf.RejectInput, false);
                        else
                        {
                            Messages.Message((already ? "LG_ImportAlready" : "LG_Imported").Translate(got.colony), MessageTypeDefOf.PositiveEvent, false);
                            sel = got; showCurrent = false; storyScroll = Vector2.zero;
                        }
                    }));
                }
                if (menu.Count == 0) menu.Add(new FloatMenuOption("LG_ImportNone".Translate(), null));
                Find.WindowStack.Add(new FloatMenu(menu));
            }
            TooltipHandler.TipRegion(imR, "LG_ImportTip".Translate(Store.ExportFolderPath));
            y += 40f;
            Widgets.Label(new Rect(lc.x, y, lc.width, 24f), "<b>" + "LG_OldColonies".Translate() + "</b>");
            y += 26f;
            List<LRecord> all = Store.All;
            Rect outR = new Rect(lc.x, y, lc.width, lc.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, all.Count * 56f);
            Widgets.BeginScrollView(outR, ref listScroll, view);
            LRecord del = null;
            for (int i = 0; i < all.Count; i++)
            {
                LRecord rec = all[i];
                Rect row = new Rect(0f, i * 56f, view.width, 52f);
                if (!showCurrent && sel == rec) MUI.Fill(row, MUI.OnBg);
                else if (Mouse.IsOver(row)) Widgets.DrawHighlight(row);
                Widgets.Label(new Rect(row.x + 6f, row.y + 4f, row.width - 40f, 22f), rec.colony.Truncate(row.width - 40f));
                GUI.color = MUI.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 6f, row.y + 26f, row.width - 40f, 20f), rec.Years + " · " + "LG_PeopleN".Translate(rec.people.Count) + " · " + rec.EndingLabel);
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (Widgets.ButtonImage(new Rect(row.xMax - 26f, row.y + 14f, 22f, 22f), TexButton.Delete)) del = rec;
                else if (Widgets.ButtonInvisible(new Rect(row.x, row.y, row.width - 30f, row.height))) { sel = rec; showCurrent = false; storyScroll = Vector2.zero; }
            }
            if (all.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(0f, 0f, view.width, 60f), "LG_NoneYet".Translate());
                GUI.color = Color.white;
            }
            Widgets.EndScrollView();
            if (del != null)
            {
                LRecord d = del;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("LG_DeleteConfirm".Translate(d.colony), () => { Store.Delete(d); if (sel == d) { sel = null; showCurrent = true; } }, true));
            }

            MUI.Card(right);
            Rect rc = right.ContractedBy(10f);
            LRecord show;
            if (showCurrent)
            {
                if (cur == null || Time.realtimeSinceStartup - curAt > 5f) { cur = Current(); curAt = Time.realtimeSinceStartup; }
                show = cur;
            }
            else show = sel;
            if (show != null) StoryDrawer.Draw(rc, show, ref storyScroll);
        }
    }
}
