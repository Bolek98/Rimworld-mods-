using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // Stage 2: the scan of all defs takes 20+ seconds with a big mod list. Its result only changes
    // when mods, their files, the language or URT change – so it is kept on disk with a fingerprint
    // of exactly those, and the next start with the same fingerprint reads it in a moment.
    public static class ScanCache
    {
        private const int FormatVersion = 1;

        public static string FilePath(string folder, string target) => Path.Combine(folder, "scan_" + target + ".bin");

        // mods (id, order, files: size + time), the active language and its injections, the number of defs, URT
        public static string Fingerprint()
        {
            ulong h = 14695981039346656037UL;
            void Add(string s)
            {
                if (s == null) s = "\0";
                for (int i = 0; i < s.Length; i++) { h ^= s[i]; h *= 1099511628211UL; }
                h ^= 0xFF; h *= 1099511628211UL;
            }
            Add(FormatVersion + "|" + UrtVersion.Number);
            LoadedLanguage active = LanguageDatabase.activeLanguage;
            Add(active?.folderName);
            int inj = 0;
            if (active != null)
                foreach (DefInjectionPackage p in active.defInjections)
                    inj += p.injections.Count;
            Add(inj + "|" + (active?.keyedReplacements.Count ?? 0) + "|" + (LanguageDatabase.defaultLanguage?.keyedReplacements.Count ?? 0));
            int defs = 0;
            foreach (Type t in GenDefDatabase.AllDefTypesWithDatabases())
            {
                try { defs += (int)GenGeneric.GetStaticPropertyOnGenericType(typeof(DefDatabase<>), t, "DefCount"); }
                catch { }
            }
            Add("defs " + defs);
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
            {
                Add(m.PackageId);
                List<string> folders = m.foldersToLoadDescendingOrder ?? new List<string> { m.RootDir };
                foreach (string f in folders)
                    foreach (string sub in new[] { "Defs", "Patches", "Languages", "Assemblies" })
                    {
                        string dir = Path.Combine(f, sub);
                        if (!Directory.Exists(dir)) continue;
                        try
                        {
                            foreach (string file in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            {
                                string ext = Path.GetExtension(file).ToLowerInvariant();
                                if (ext != ".xml" && ext != ".tar" && ext != ".dll" && ext != ".txt") continue;
                                var fi = new FileInfo(file);
                                Add(file.Substring(f.Length) + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks);
                            }
                        }
                        catch { Add("?" + dir); }
                    }
            }
            return h.ToString("X16");
        }

        public static void Save(string path, string fingerprint, List<Item> items, string report)
        {
            try
            {
                string tmp = path + ".tmp";
                using (var w = new BinaryWriter(File.Create(tmp), Encoding.UTF8))
                {
                    w.Write(FormatVersion);
                    w.Write(fingerprint);
                    w.Write(report ?? "");
                    w.Write(items.Count);
                    foreach (Item it in items)
                    {
                        w.Write((byte)it.Kind);
                        w.Write(it.DefType?.FullName ?? "");
                        S(w, it.Path);
                        w.Write(it.ListIndex);
                        S(w, it.Source);
                        S(w, it.SourceLang);
                        S(w, it.Mod);
                        w.Write(it.IsRule);
                        w.Write(it.Priority);
                    }
                    w.Write(Scan.OriginalLists.Count);
                    foreach (var kv in Scan.OriginalLists)
                    {
                        w.Write(kv.Key);
                        w.Write(kv.Value.Count);
                        foreach (string s in kv.Value) S(w, s);
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception e)
            {
                MLog.Warn("[URT] scan cache not saved: " + e.Message);
            }
        }

        public static bool TryLoad(string path, string fingerprint, out List<Item> items, out string report)
        {
            items = null;
            report = null;
            try
            {
                if (!File.Exists(path)) return false;
                using (var r = new BinaryReader(File.OpenRead(path), Encoding.UTF8))
                {
                    if (r.ReadInt32() != FormatVersion || r.ReadString() != fingerprint) return false;
                    report = r.ReadString();
                    int n = r.ReadInt32();
                    var list = new List<Item>(n);
                    var types = new Dictionary<string, Type>();
                    for (int i = 0; i < n; i++)
                    {
                        var it = new Item { Kind = (ItemKind)r.ReadByte() };
                        string tn = r.ReadString();
                        if (tn.Length > 0)
                        {
                            if (!types.TryGetValue(tn, out Type t)) types[tn] = t = GenTypes.GetTypeInAnyAssembly(tn);
                            if (t == null) return false;
                            it.DefType = t;
                        }
                        it.Path = G(r);
                        it.ListIndex = r.ReadInt32();
                        it.Source = G(r);
                        it.SourceLang = G(r);
                        it.Mod = G(r);
                        it.IsRule = r.ReadBoolean();
                        it.Priority = r.ReadInt32();
                        list.Add(it);
                    }
                    Scan.OriginalLists.Clear();
                    int ln = r.ReadInt32();
                    for (int i = 0; i < ln; i++)
                    {
                        string k = r.ReadString();
                        int c = r.ReadInt32();
                        var l = new List<string>(c);
                        for (int j = 0; j < c; j++) l.Add(G(r));
                        Scan.OriginalLists[k] = l;
                    }
                    items = list;
                    return true;
                }
            }
            catch (Exception e)
            {
                MLog.Warn("[URT] scan cache not usable: " + e.Message);
                Scan.OriginalLists.Clear();
                return false;
            }
        }

        private static void S(BinaryWriter w, string s)
        {
            w.Write(s != null);
            if (s != null) w.Write(s);
        }

        private static string G(BinaryReader r) => r.ReadBoolean() ? r.ReadString() : null;
    }
}
