using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.IO;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // 1.2.0 (idea from a tester): translations from other mods without enabling them.
    // Translation mods you have installed but NOT enabled (Workshop or Mods folder), packs in the URT library
    // (Config/UniversalRimTranslator/Library) and packs downloaded from a URL are read for the active
    // language and added to it like an enabled translation mod – but only where nothing else translated the
    // text: a mod's own translation and enabled translation mods always win, URT's machine translation comes last.
    // Def translations are added only for defs that exist (no "def not found" errors for inactive mods).
    public static class Sources
    {
        public sealed class Source
        {
            public string Id, Name, Kind, Root;
            public readonly List<string> LangDirs = new List<string>();
            public DateTime Newest;
            public int Files, Keyed, Inject, Strings;          // read from the files
            public int UsedKeyed, UsedDefs, UsedStrings;        // really added to the language
            public readonly Dictionary<string, int> Covers = new Dictionary<string, int>();
            public bool On;
        }

        public static List<Source> Found = new List<Source>();
        public static readonly Dictionary<string, int> PerMod = new Dictionary<string, int>(StringComparer.Ordinal);
        public static int TotalUsed;
        public static bool RestartNeeded;
        public static string LastError;
        private static LoadedLanguage loadedFor;
        private static readonly List<KeyValuePair<Source, DefInjectionPackage>> pending = new List<KeyValuePair<Source, DefInjectionPackage>>();

        public static string LibraryRoot => Path.Combine(GenFilePaths.ConfigFolderPath, "UniversalRimTranslator", "Library");

        private static UrtSettings S => UniversalRimTranslatorMod.Settings;

        public static void Patch(Harmony h)
        {
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.LoadData)), postfix: new HarmonyMethod(typeof(Sources), nameof(AfterLoadData)));
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_BeforeImpliedDefs)), prefix: new HarmonyMethod(typeof(Sources), nameof(BeforeInjectEarly)));
            h.Patch(AccessTools.Method(typeof(LoadedLanguage), nameof(LoadedLanguage.InjectIntoData_AfterImpliedDefs)), prefix: new HarmonyMethod(typeof(Sources), nameof(BeforeInjectLate)));
        }

        // ---------------- finding the sources ----------------
        private static string Version => VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor;

        public static List<Source> Find(LoadedLanguage lang)
        {
            var list = new List<Source>();
            if (lang == null) return list;
            string folder = lang.folderName, legacy = lang.LegacyFolderName;
            foreach (ModMetaData m in ModLister.AllInstalledMods.ToList())
            {
                try
                {
                    if (m == null || m.Active || m.Official || m.RootDir == null || !m.RootDir.Exists) continue;
                    var s = new Source
                    {
                        Id = "mod:" + (m.PackageIdNonUnique ?? m.FolderName) + "@" + m.FolderName,
                        Name = m.Name, Kind = m.OnSteamWorkshop ? "Workshop" : "Local", Root = m.RootDir.FullName
                    };
                    if (!ReadRoot(s, m.RootDir.FullName, folder, legacy, true)) continue;
                    list.Add(s);
                }
                catch (Exception e) { MLog.Warn("[URT] sources: " + m?.Name + ": " + e.Message); }
            }
            try
            {
                if (Directory.Exists(LibraryRoot))
                    foreach (string d in Directory.GetDirectories(LibraryRoot))
                    {
                        string n = Path.GetFileName(d);
                        if (n.StartsWith(".")) continue;
                        bool fromUrl = File.Exists(Path.Combine(d, MetaName));
                        var s = new Source { Id = "lib:" + n, Name = PackName(d, n), Kind = fromUrl ? "URL" : "Library", Root = d };
                        // a whole mod, a Languages folder, a language folder or just Keyed/DefInjected
                        if (ReadRoot(s, d, folder, legacy, false)) { list.Add(s); continue; }
                        if (AddLangDirsIn(s, d, folder, legacy)) { list.Add(s); continue; }
                        if (IsLangDir(d)) { s.LangDirs.Add(d); s.Newest = Directory.GetLastWriteTimeUtc(d); list.Add(s); }
                    }
            }
            catch (Exception e) { MLog.Warn("[URT] library: " + e.Message); }

            // order: what the player set, the rest newest first; switched-off ones stay in the list
            var order = Lines(S.importOrder);
            var off = new HashSet<string>(Lines(S.importOff), StringComparer.Ordinal);
            foreach (Source s in list) s.On = !off.Contains(s.Id);
            list = list.OrderBy(s => { int i = order.IndexOf(s.Id); return i < 0 ? int.MaxValue : i; })
                       .ThenByDescending(s => s.Newest).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList();
            return list;
        }

        private static string PackName(string dir, string fallback)
        {
            try
            {
                string about = Path.Combine(dir, "About", "About.xml");
                if (File.Exists(about))
                {
                    var x = System.Xml.Linq.XDocument.Load(about).Root?.Element("name")?.Value;
                    if (!string.IsNullOrWhiteSpace(x)) return x.Trim();
                }
            }
            catch { }
            return fallback;
        }

        // a translation-only mod (no code, no defs) with a folder for the active language
        private static bool ReadRoot(Source s, string root, string folder, string legacy, bool mustBeTranslationOnly)
        {
            string[] tops = Directory.GetDirectories(root).Select(Path.GetFileName).ToArray();
            string lf = Path.Combine(root, "LoadFolders.xml");
            string xml = File.Exists(lf) ? File.ReadAllText(lf) : null;
            List<string> folders = SourcesLogic.LoadFolders(xml, Version, tops, id => ModsConfig.IsActive(id));
            var bases = new List<string>();
            var probe = new Source();
            foreach (string f in folders)
            {
                string baseDir = f.Length == 0 ? root : Path.Combine(root, f.Replace('/', Path.DirectorySeparatorChar));
                if (!Directory.Exists(baseDir)) continue;
                bases.Add(baseDir);
                string langs = Path.Combine(baseDir, "Languages");
                if (Directory.Exists(langs)) AddLangDirsIn(probe, langs, folder, legacy);
            }
            if (probe.LangDirs.Count == 0) return false;     // cheap test first: most mods stop here
            if (mustBeTranslationOnly)
                foreach (string b in bases)
                {
                    string asm = Path.Combine(b, "Assemblies"), defs = Path.Combine(b, "Defs");
                    if (Directory.Exists(asm) && Directory.EnumerateFiles(asm, "*.dll", SearchOption.AllDirectories).Any()) return false;
                    if (Directory.Exists(defs) && Directory.EnumerateFiles(defs, "*.xml", SearchOption.AllDirectories).Any()) return false;
                }
            s.LangDirs.AddRange(probe.LangDirs);
            s.Newest = probe.Newest;
            return true;
        }

        private static bool AddLangDirsIn(Source s, string langs, string folder, string legacy)
        {
            bool any = false;
            foreach (string d in Directory.GetDirectories(langs))
            {
                if (!SourcesLogic.LangDirMatches(Path.GetFileName(d), folder, legacy) || !IsLangDir(d)) continue;
                if (!s.LangDirs.Contains(d)) s.LangDirs.Add(d);
                DateTime t = Directory.GetLastWriteTimeUtc(d);
                if (t > s.Newest) s.Newest = t;
                any = true;
            }
            return any;
        }

        private static bool IsLangDir(string d) =>
            Directory.Exists(Path.Combine(d, "Keyed")) || Directory.Exists(Path.Combine(d, "DefInjected")) || Directory.Exists(Path.Combine(d, "Strings"));

        // ---------------- loading ----------------
        public static void AfterLoadData(LoadedLanguage __instance)
        {
            try
            {
                if (S == null || !S.importOn || __instance == null || __instance == loadedFor) return;
                if (__instance != LanguageDatabase.activeLanguage || __instance == LanguageDatabase.defaultLanguage) return;
                loadedFor = __instance;
                var sw = Stopwatch.StartNew();
                Found = Find(__instance);
                foreach (Source s in Found)
                {
                    if (!s.On) continue;
                    foreach (string d in s.LangDirs) LoadDir(__instance, s, d);
                }
                int on = Found.Count(x => x.On);
                MLog.Info("[URT] translations from other mods: " + Found.Count + " found (" + on + " on), keyed added " + Found.Sum(x => x.UsedKeyed) +
                          ", def texts waiting " + Found.Sum(x => x.Inject) + ", " + sw.ElapsedMilliseconds + " ms");
                AutoUpdate();
            }
            catch (Exception e) { LastError = e.Message; MLog.Error("[URT] translations from other mods: " + e); }
        }

        private static void LoadDir(LoadedLanguage lang, Source s, string dir)
        {
            string keyed = Path.Combine(dir, "Keyed");
            if (Directory.Exists(keyed))
                foreach (string f in Directory.GetFiles(keyed, "*.xml", SearchOption.AllDirectories))
                {
                    s.Files++;
                    string text;
                    try { text = File.ReadAllText(f); } catch { continue; }
                    try
                    {
                        foreach (DirectXmlLoaderSimple.XmlKeyValuePair kv in DirectXmlLoaderSimple.ValuesFromXmlFile(text))
                        {
                            s.Keyed++;
                            if (string.IsNullOrEmpty(kv.key) || kv.value == null || kv.value == "TODO") continue;
                            if (lang.keyedReplacements.ContainsKey(kv.key)) continue;
                            lang.keyedReplacements[kv.key] = new LoadedLanguage.KeyedReplacement
                            {
                                key = kv.key, value = kv.value, fileSource = Path.GetFileName(f), fileSourceLine = kv.lineNumber, fileSourceFullPath = f
                            };
                            s.UsedKeyed++;
                            Count(s, KeyedMod(kv.key));
                        }
                    }
                    catch (Exception e) { MLog.Warn("[URT] " + s.Name + ": " + Path.GetFileName(f) + ": " + e.Message); }
                }
            string inj = Path.Combine(dir, "DefInjected");
            if (Directory.Exists(inj))
                foreach (string td in Directory.GetDirectories(inj))
                {
                    string name = Path.GetFileName(td);
                    Type t = GenTypes.GetTypeInAnyAssembly(name);
                    if (t == null && name.Length > 3) t = GenTypes.GetTypeInAnyAssembly(name.Substring(0, name.Length - 1));
                    if (t == null || !typeof(Def).IsAssignableFrom(t)) continue;
                    var pkg = new DefInjectionPackage(t);
                    VirtualDirectory vd = AbstractFilesystem.GetDirectory(td);
                    foreach (VirtualFile vf in vd.GetFiles("*.xml", SearchOption.AllDirectories))
                    {
                        s.Files++;
                        try { pkg.AddDataFromFile(vf, out bool _, vf.ReadAllText()); }
                        catch (Exception e) { MLog.Warn("[URT] " + s.Name + ": " + vf.Name + ": " + e.Message); }
                    }
                    s.Inject += pkg.injections.Count;
                    if (pkg.injections.Count > 0) pending.Add(new KeyValuePair<Source, DefInjectionPackage>(s, pkg));
                }
            string str = Path.Combine(dir, "Strings");
            if (Directory.Exists(str))
                foreach (string f in Directory.GetFiles(str, "*.txt", SearchOption.AllDirectories))
                {
                    s.Files++;
                    string rel = f.Substring(str.Length + 1);
                    rel = rel.Substring(0, rel.Length - Path.GetExtension(rel).Length).Replace('\\', '/');
                    if (!rel.Contains("/") || lang.stringFiles.ContainsKey(rel)) continue;
                    try
                    {
                        lang.stringFiles[rel] = GenText.LinesFromString(File.ReadAllText(f)).ToList();
                        s.Strings++; s.UsedStrings++;
                    }
                    catch { }
                }
        }

        // the vanilla injection runs right after these prefixes and puts the added texts into the defs
        public static void BeforeInjectEarly(LoadedLanguage __instance) => Merge(__instance);
        public static void BeforeInjectLate(LoadedLanguage __instance)
        {
            Merge(__instance);
            TotalUsed = Found.Sum(x => x.UsedKeyed + x.UsedDefs + x.UsedStrings);
            if (Found.Count > 0)
                MLog.Info("[URT] translations from other mods used: " + TotalUsed + " texts (" + Found.Sum(x => x.UsedDefs) + " def texts) for " + PerMod.Count + " mods.");
        }

        private static void Merge(LoadedLanguage lang)
        {
            try
            {
                if (lang == null || lang != loadedFor || pending.Count == 0) return;
                var parentsOf = new Dictionary<DefInjectionPackage, HashSet<string>>();
                foreach (var p in pending)
                {
                    DefInjectionPackage src = p.Value;
                    DefInjectionPackage dst = lang.defInjections.FirstOrDefault(x => x.defType == src.defType);
                    if (dst == null) { dst = new DefInjectionPackage(src.defType); lang.defInjections.Add(dst); }
                    if (!parentsOf.TryGetValue(dst, out HashSet<string> parents))
                    {
                        parentsOf[dst] = parents = new HashSet<string>(StringComparer.Ordinal);
                        foreach (string k in dst.injections.Keys) AddParents(parents, k);
                    }
                    foreach (var kv in src.injections)
                    {
                        if (kv.Value == null || kv.Value.isPlaceholder || dst.injections.ContainsKey(kv.Key)) continue;
                        int dot = kv.Key.IndexOf('.');
                        if (dot <= 0) continue;
                        Def def = GenDefDatabase.GetDefSilentFail(src.defType, kv.Key.Substring(0, dot), false);
                        if (def == null) continue;      // the mod of this def is not active (or the def is made later)
                        if (Clashes(dst, parents, kv.Key, kv.Value)) continue;
                        dst.injections[kv.Key] = kv.Value;
                        AddParents(parents, kv.Key);
                        p.Key.UsedDefs++;
                        Count(p.Key, def.modContentPack?.Name);
                    }
                }
            }
            catch (Exception e) { LastError = e.Message; MLog.Error("[URT] adding translations from other mods: " + e); }
        }

        // a whole-list translation and single list items must not mix (the game reports an error)
        private static bool Clashes(DefInjectionPackage dst, HashSet<string> parents, string key, DefInjectionPackage.DefInjection inj)
        {
            if (inj.IsFullListInjection) return parents.Contains(key);
            int last = key.LastIndexOf('.');
            if (last > 0 && char.IsDigit(key[key.Length - 1]))
            {
                string parent = key.Substring(0, last);
                if (dst.injections.TryGetValue(parent, out var pi) && pi.IsFullListInjection) return true;
            }
            return false;
        }

        // "A.b.c" → "A", "A.b" (every key that has items under it)
        private static void AddParents(HashSet<string> set, string key)
        {
            for (int i = key.IndexOf('.'); i > 0; i = key.IndexOf('.', i + 1)) set.Add(key.Substring(0, i));
        }

        private static void Count(Source s, string mod)
        {
            if (string.IsNullOrEmpty(mod)) mod = "?";
            s.Covers[mod] = s.Covers.TryGetValue(mod, out int n) ? n + 1 : 1;
            PerMod[mod] = PerMod.TryGetValue(mod, out int m) ? m + 1 : 1;
        }

        private static Dictionary<string, string> modByRoot;
        private static string KeyedMod(string key)
        {
            var en = LanguageDatabase.defaultLanguage;
            if (en == null || !en.keyedReplacements.TryGetValue(key, out var r) || string.IsNullOrEmpty(r.fileSourceFullPath)) return "?";
            if (modByRoot == null)
            {
                modByRoot = new Dictionary<string, string>();
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                    if (!string.IsNullOrEmpty(m.RootDir)) modByRoot[m.RootDir.Replace('\\', '/')] = m.Name;
            }
            string p = r.fileSourceFullPath.Replace('\\', '/');
            foreach (var kv in modByRoot)
                if (p.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
            return "?";
        }

        public static int ImportedFor(string mod) => mod != null && PerMod.TryGetValue(mod, out int n) ? n : 0;

        // ---------------- settings helpers ----------------
        public static List<string> Lines(string v) =>
            (v ?? "").Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        private static string Join(IEnumerable<string> l) => string.Join("\n", l.ToArray());

        public static void SetOn(Source s, bool on)
        {
            s.On = on;
            var off = Lines(S.importOff);
            off.Remove(s.Id);
            if (!on) off.Add(s.Id);
            S.importOff = Join(off);
            RestartNeeded = true;
        }

        public static void Move(Source s, int dir)
        {
            int i = Found.IndexOf(s), j = i + dir;
            if (i < 0 || j < 0 || j >= Found.Count) return;
            Found[i] = Found[j]; Found[j] = s;
            S.importOrder = Join(Found.Select(x => x.Id));
            RestartNeeded = true;
        }

        // ---------------- URL packs ----------------
        public const string MetaName = ".urt_source.txt";
        private const long MaxDownload = 200L * 1024 * 1024, MaxUnpacked = 500L * 1024 * 1024;
        public static readonly Dictionary<string, string> UrlStatus = new Dictionary<string, string>();
        public static volatile bool Downloading;
        private static HttpClient http;

        private static HttpClient Http
        {
            get
            {
                if (http == null)
                {
                    var h = new HttpClientHandler();
                    try { h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
                    http = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(180) };
                    http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "UniversalRimTranslator (RimWorld mod)");
                }
                return http;
            }
        }

        // once a day after the start (setting), or from the button
        public static void AutoUpdate()
        {
            try
            {
                if (S == null || !S.importOn || !S.importUrlAuto || Lines(S.importUrls).Count == 0) return;
                UpdateAll(false);
            }
            catch { }
        }

        public static void UpdateAll(bool force)
        {
            if (Downloading) return;
            List<string> urls = Lines(S.importUrls);
            if (urls.Count == 0) return;
            Downloading = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (string u in urls)
                    {
                        string msg;
                        try { msg = Download(u, force); }
                        catch (Exception e) { msg = "✗ " + e.Message; }
                        lock (UrlStatus) UrlStatus[u] = msg;
                        MLog.Info("[URT] translation pack " + u + ": " + msg);
                    }
                }
                finally { Downloading = false; }
            });
        }

        private static string Download(string url, bool force)
        {
            string dl = SourcesLogic.ToDownloadUrl(url, out string name, out string err);
            if (dl == null) return "✗ " + err;
            string dir = Path.Combine(LibraryRoot, name);
            Dictionary<string, string> meta = ReadMeta(dir);
            string today = DateTime.UtcNow.ToString("yyyy-MM-dd");
            if (!force && meta.TryGetValue("checked", out string c) && c == today) return "URT_Src_UrlChecked".Translate(today);
            var req = new HttpRequestMessage(HttpMethod.Get, dl);
            if (!force && meta.TryGetValue("etag", out string et) && et.Length > 0 && Directory.Exists(dir))
                req.Headers.TryAddWithoutValidation("If-None-Match", et);
            using (HttpResponseMessage resp = Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
            {
                if (resp.StatusCode == HttpStatusCode.NotModified)
                {
                    meta["checked"] = today; WriteMeta(dir, meta);
                    return "URT_Src_UrlSame".Translate();
                }
                if (!resp.IsSuccessStatusCode) return "✗ HTTP " + (int)resp.StatusCode + " " + resp.ReasonPhrase;
                long? len = resp.Content.Headers.ContentLength;
                if (len.HasValue && len.Value > MaxDownload) return "✗ " + "URT_Src_TooBig".Translate(len.Value / (1024 * 1024));
                var ms = new MemoryStream();
                using (Stream st = resp.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                {
                    byte[] buf = new byte[81920];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > MaxDownload) return "✗ " + "URT_Src_TooBig".Translate(ms.Length / (1024 * 1024));
                    }
                }
                ms.Position = 0;
                string tmp = Path.Combine(LibraryRoot, ".tmp_" + name);
                if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
                Directory.CreateDirectory(tmp);
                int files = 0; long total = 0;
                using (var zip = new ZipArchive(ms, ZipArchiveMode.Read))
                {
                    var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
                    string top = SourcesLogic.CommonTop(entries.Select(e => e.FullName));
                    foreach (ZipArchiveEntry e in entries)
                    {
                        string rel = e.FullName.Replace('\\', '/').TrimStart('/');
                        if (top.Length > 0 && rel.StartsWith(top, StringComparison.Ordinal)) rel = rel.Substring(top.Length);
                        if (!SourcesLogic.WantedEntry(rel)) continue;
                        string target = SourcesLogic.SafeTarget(tmp, rel);
                        if (target == null) continue;
                        total += e.Length;
                        if (total > MaxUnpacked) { Directory.Delete(tmp, true); return "✗ " + "URT_Src_TooBig".Translate(total / (1024 * 1024)); }
                        Directory.CreateDirectory(Path.GetDirectoryName(target));
                        using (Stream es = e.Open())
                        using (FileStream fs = File.Create(target)) es.CopyTo(fs);
                        files++;
                    }
                }
                if (files == 0) { Directory.Delete(tmp, true); return "✗ " + "URT_Src_NoLanguages".Translate(); }
                meta = new Dictionary<string, string>
                {
                    ["url"] = url, ["download"] = dl, ["checked"] = today, ["files"] = files.ToString(),
                    ["etag"] = resp.Headers.ETag?.Tag ?? "", ["updated"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm")
                };
                WriteMeta(tmp, meta);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(tmp, dir);
                RestartNeeded = true;
                return "URT_Src_UrlDone".Translate(files);
            }
        }

        private static Dictionary<string, string> ReadMeta(string dir)
        {
            var d = new Dictionary<string, string>();
            try
            {
                string f = Path.Combine(dir, MetaName);
                if (File.Exists(f))
                    foreach (string l in File.ReadAllLines(f))
                    {
                        int i = l.IndexOf('=');
                        if (i > 0) d[l.Substring(0, i)] = l.Substring(i + 1);
                    }
            }
            catch { }
            return d;
        }

        private static void WriteMeta(string dir, Dictionary<string, string> meta)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllLines(Path.Combine(dir, MetaName), meta.Select(kv => kv.Key + "=" + (kv.Value ?? "").Replace("\n", " ")).ToArray());
            }
            catch { }
        }

        public static string MetaInfo(string url)
        {
            string dl = SourcesLogic.ToDownloadUrl(url, out string name, out string err);
            if (dl == null) return "✗ " + err;
            var m = ReadMeta(Path.Combine(LibraryRoot, name));
            return m.TryGetValue("updated", out string u) ? "URT_Src_UrlHave".Translate(u, m.TryGetValue("files", out string f) ? f : "?") : (string)"URT_Src_UrlNotYet".Translate();
        }

        public static void OpenLibrary()
        {
            try
            {
                Directory.CreateDirectory(LibraryRoot);
                Application.OpenURL("file://" + LibraryRoot.Replace('\\', '/'));
            }
            catch (Exception e) { Messages.Message(e.Message, MessageTypeDefOf.RejectInput, false); }
        }

        // the list again (new packs, new subscriptions); what was used stays shown, changes apply after a restart
        public static void Rescan()
        {
            var old = Found.ToDictionary(x => x.Id, x => x);
            List<Source> now = Find(LanguageDatabase.activeLanguage);
            foreach (Source s in now)
                if (old.TryGetValue(s.Id, out Source o))
                {
                    s.UsedKeyed = o.UsedKeyed; s.UsedDefs = o.UsedDefs; s.UsedStrings = o.UsedStrings;
                    s.Files = o.Files; s.Keyed = o.Keyed; s.Inject = o.Inject; s.Strings = o.Strings;
                    foreach (var kv in o.Covers) s.Covers[kv.Key] = kv.Value;
                }
            Found = now;
            RestartNeeded = true;
        }
    }
}
