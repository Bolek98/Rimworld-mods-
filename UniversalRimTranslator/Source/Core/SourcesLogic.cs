using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Urt2
{
    // 1.2.0: the parts of "translations from other mods" that need nothing from the game – kept apart so
    // they are tested outside RimWorld (Common/urt_tests).
    public static class SourcesLogic
    {
        // A language folder of a translation mod: "ChineseSimplified", "ChineseSimplified (简体中文)", the legacy name…
        public static bool LangDirMatches(string dirName, string folderName, string legacyName)
        {
            if (string.IsNullOrEmpty(dirName) || string.IsNullOrEmpty(folderName)) return false;
            string d = Base(dirName), f = Base(folderName);
            if (string.Equals(d, f, StringComparison.OrdinalIgnoreCase)) return true;
            if (!string.IsNullOrEmpty(legacyName) &&
                (string.Equals(dirName.Trim(), legacyName.Trim(), StringComparison.OrdinalIgnoreCase) || string.Equals(d, Base(legacyName), StringComparison.OrdinalIgnoreCase)))
                return true;
            return false;
        }

        // "ChineseSimplified (简体中文)" → "ChineseSimplified"
        private static string Base(string s)
        {
            s = s.Trim();
            int i = s.IndexOf('(');
            if (i > 0) s = s.Substring(0, i).Trim();
            return s;
        }

        // The folders of a mod that the game would load for this version (LoadFolders.xml or the usual
        // root / Common / <version>). Relative paths, "" = the mod root. Missing version folders fall back to
        // the newest older one (a translation for 1.6 is still mostly right in 1.7).
        public static List<string> LoadFolders(string loadFoldersXml, string version, IEnumerable<string> existingTopDirs, Func<string, bool> modActive)
        {
            var dirs = new HashSet<string>(existingTopDirs ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var result = new List<string>();
            if (!string.IsNullOrWhiteSpace(loadFoldersXml))
            {
                try
                {
                    XElement root = XDocument.Parse(loadFoldersXml).Root;
                    if (root != null)
                    {
                        XElement pick = null;
                        Version want = ParseVer(version), best = null;
                        foreach (XElement v in root.Elements())
                        {
                            string n = v.Name.LocalName;
                            if (n.Length < 2 || (n[0] != 'v' && n[0] != 'V')) continue;
                            Version ver = ParseVer(n.Substring(1));
                            if (ver == null || want == null || ver > want) continue;
                            if (best == null || ver > best) { best = ver; pick = v; }
                        }
                        if (pick == null) pick = root.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("default", StringComparison.OrdinalIgnoreCase));
                        if (pick != null)
                        {
                            foreach (XElement li in pick.Elements())
                            {
                                if (!Allowed(li, modActive)) continue;
                                string p = (li.Value ?? "").Trim().Replace('\\', '/').Trim('/');
                                if (p == "/") p = "";
                                if (!result.Contains(p)) result.Add(p);
                            }
                            return result;
                        }
                    }
                }
                catch { }
            }
            result.Add("");
            if (dirs.Contains("Common")) result.Add("Common");
            if (dirs.Contains(version)) result.Add(version);
            else
            {
                Version want = ParseVer(version);
                string older = dirs.Where(d => ParseVer(d) != null && want != null && ParseVer(d) < want).OrderByDescending(d => ParseVer(d)).FirstOrDefault();
                if (older != null) result.Add(older);
            }
            return result;
        }

        private static bool Allowed(XElement li, Func<string, bool> modActive)
        {
            if (modActive == null) return true;
            string ifA = (string)li.Attribute("IfModActive"), ifNot = (string)li.Attribute("IfModNotActive");
            string ifAll = (string)li.Attribute("IfModActiveAll");
            if (!string.IsNullOrEmpty(ifA) && !Split(ifA).Any(modActive)) return false;
            if (!string.IsNullOrEmpty(ifAll) && !Split(ifAll).All(modActive)) return false;
            if (!string.IsNullOrEmpty(ifNot) && Split(ifNot).Any(modActive)) return false;
            return true;
        }

        private static IEnumerable<string> Split(string s) => s.Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0);

        private static Version ParseVer(string s)
        {
            if (string.IsNullOrEmpty(s)) return null;
            s = s.Trim();
            if (s.Count(c => c == '.') == 0) return null;
            return Version.TryParse(s, out Version v) ? v : null;
        }

        // ---------------- URL packs ----------------
        private static readonly Regex GitHub = new Regex(@"^https?://(www\.)?github\.com/(?<o>[A-Za-z0-9_.-]+)/(?<r>[A-Za-z0-9_.-]+?)(\.git)?(/tree/(?<b>[^?#]+))?/?([?#].*)?$", RegexOptions.IgnoreCase);

        // A URL the player gave → the zip to download and a folder name for the library.
        // GitHub repository pages become their zip (default branch, or the branch of a /tree/ link).
        public static string ToDownloadUrl(string url, out string name, out string error)
        {
            name = null; error = null;
            if (string.IsNullOrWhiteSpace(url)) { error = "empty"; return null; }
            url = url.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri u) || (u.Scheme != "http" && u.Scheme != "https")) { error = "not a http(s) address"; return null; }
            if (u.Host.IndexOf("steamcommunity.com", StringComparison.OrdinalIgnoreCase) >= 0) { error = "Steam Workshop pages cannot be downloaded – subscribe to the mod instead"; return null; }
            Match m = GitHub.Match(url);
            if (m.Success && !url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                string o = m.Groups["o"].Value, r = m.Groups["r"].Value, b = m.Groups["b"].Value.Trim('/');
                name = SafeName(o + "_" + r + (b.Length > 0 ? "_" + b : ""));
                return b.Length > 0
                    ? "https://codeload.github.com/" + o + "/" + r + "/zip/refs/heads/" + b
                    : "https://api.github.com/repos/" + o + "/" + r + "/zipball";
            }
            string file = Path.GetFileNameWithoutExtension(u.AbsolutePath);
            name = SafeName(string.IsNullOrEmpty(file) ? u.Host : u.Host + "_" + file);
            return url;
        }

        public static string SafeName(string s)
        {
            var chars = (s ?? "").Select(c => char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' ? c : '_').ToArray();
            string n = new string(chars).Trim('.', '_');
            if (n.Length > 80) n = n.Substring(0, 80);
            return n.Length == 0 ? "pack" : n;
        }

        // Only translation files come out of a zip: Languages/** (xml, txt, png icon) and About/About.xml.
        // relPath is the path inside the zip after the common top folder was cut off.
        public static bool WantedEntry(string relPath)
        {
            if (string.IsNullOrEmpty(relPath)) return false;
            string p = relPath.Replace('\\', '/').TrimStart('/');
            if (p.EndsWith("/")) return false;
            if (string.Equals(p, "About/About.xml", StringComparison.OrdinalIgnoreCase)) return true;
            if (string.Equals(p, "LoadFolders.xml", StringComparison.OrdinalIgnoreCase)) return true;
            string ext = Path.GetExtension(p).ToLowerInvariant();
            if (ext != ".xml" && ext != ".txt" && ext != ".png") return false;
            string[] parts = p.Split('/');
            for (int i = 0; i < parts.Length - 1; i++)
                if (string.Equals(parts[i], "Languages", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // The target file of a zip entry, or null when the entry would land outside the folder (zip slip).
        public static string SafeTarget(string destRoot, string relPath)
        {
            if (string.IsNullOrEmpty(destRoot) || string.IsNullOrEmpty(relPath)) return null;
            string p = relPath.Replace('\\', '/');
            if (p.StartsWith("/") || p.Contains(":") || p.Split('/').Any(x => x == "..")) return null;
            string root = Path.GetFullPath(destRoot);
            if (!root.EndsWith(Path.DirectorySeparatorChar.ToString())) root += Path.DirectorySeparatorChar;
            string full = Path.GetFullPath(Path.Combine(root, p.Replace('/', Path.DirectorySeparatorChar)));
            return full.StartsWith(root, StringComparison.Ordinal) ? full : null;
        }

        // The common top folder of all entries ("repo-main/"), or "" when there is none.
        public static string CommonTop(IEnumerable<string> entries)
        {
            string top = null;
            foreach (string e in entries)
            {
                string p = e.Replace('\\', '/').TrimStart('/');
                int i = p.IndexOf('/');
                if (i <= 0) return "";
                string t = p.Substring(0, i + 1);
                if (top == null) top = t;
                else if (!string.Equals(top, t, StringComparison.Ordinal)) return "";
            }
            return top ?? "";
        }
    }
}
