using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // ---------------------------------------------------------------------------
    // 0.3.1: replacements. Many old mods have a continued, updated or better version by another
    // modder. The list comes from "Use This Instead" by Mlie (MIT, updated regularly) when it is
    // installed, plus our own Data/replacements.xml. A mod with a replacement is shown with the
    // reason (not made for this game version, errors in the log this session) and one click
    // subscribes to the replacement and later swaps it in at the same place of the load order.
    // ---------------------------------------------------------------------------
    public class ReplaceRule
    {
        public string oldId = "", oldName = "", oldPackage = "", newId = "", newName = "", newAuthor = "", newPackage = "", why = "";
        public List<string> oldVersions = new List<string>(), newVersions = new List<string>();
        public string source = "";
    }

    public class ReplaceRow
    {
        public ModMetaData oldMod;
        public ReplaceRule rule;
        public ModMetaData newMod;           // installed already
        public bool oldActive, newForThisVersion, oldForThisVersion;
        public int errors;
        public int Score => (oldActive ? 100 : 0) + (newForThisVersion && !oldForThisVersion ? 50 : 0) + Mathf.Min(40, errors) + (newMod != null ? 5 : 0);
    }

    public static class Replacements
    {
        private static List<ReplaceRule> rules;
        public static string SourceInfo = "";
        public static bool HasUti;

        public static string Cur => VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor;

        public static void Reload() { rules = null; }

        public static List<ReplaceRule> Rules
        {
            get
            {
                if (rules == null) Load();
                return rules;
            }
        }

        private static void Load()
        {
            rules = new List<ReplaceRule>();
            int uti = 0, own = 0;
            try
            {
                ModMetaData m = ModLister.AllInstalledMods.FirstOrDefault(x => string.Equals(x.PackageIdNonUnique, "mlie.usethisinstead", StringComparison.OrdinalIgnoreCase));
                HasUti = m != null;
                string f = m == null ? null : Path.Combine(m.RootDir.FullName, "replacements.json");
                if (f != null && File.Exists(f))
                {
                    object root = MiniJson.Parse(File.ReadAllText(f));
                    if (root is Dictionary<string, object> d && d.TryGetValue("rules", out object rl) && rl is List<object> list)
                        foreach (object o in list)
                        {
                            if (!(o is Dictionary<string, object> r)) continue;
                            var rule = new ReplaceRule
                            {
                                oldId = S(r, "oldWorkshopId"), oldName = S(r, "oldName"), oldPackage = S(r, "oldPackageId"),
                                newId = S(r, "newWorkshopId"), newName = S(r, "newName"), newAuthor = S(r, "newAuthor"), newPackage = S(r, "newPackageId"),
                                oldVersions = L(r, "oldVersions"), newVersions = L(r, "newVersions"), source = "Use This Instead"
                            };
                            if (rule.newId.Length > 0 && rule.newId != rule.oldId) { rules.Add(rule); uti++; }
                        }
                }
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] Use This Instead list: " + e.Message); }
            try
            {
                string own0 = Path.Combine(ModSortManager.Rules.Root ?? "", "Data", "replacements.xml");
                if (File.Exists(own0))
                {
                    var doc = new System.Xml.XmlDocument();
                    doc.Load(own0);
                    foreach (System.Xml.XmlNode n in doc.SelectNodes("/Replacements/li"))
                    {
                        string X(string k) => n.SelectSingleNode(k)?.InnerText?.Trim() ?? "";
                        var rule = new ReplaceRule
                        {
                            oldId = X("oldWorkshopId"), oldName = X("oldName"), oldPackage = X("oldPackageId"),
                            newId = X("newWorkshopId"), newName = X("newName"), newAuthor = X("newAuthor"), newPackage = X("newPackageId"),
                            why = X("why"), source = "Mod Sort Manager"
                        };
                        foreach (System.Xml.XmlNode v in n.SelectNodes("newVersions/li")) rule.newVersions.Add(v.InnerText.Trim());
                        if (rule.newId.Length > 0) { rules.Add(rule); own++; }
                    }
                }
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] replacements.xml: " + e.Message); }
            SourceInfo = (HasUti ? "Use This Instead: " + uti : "") + (own > 0 ? (HasUti ? " · " : "") + "Mod Sort Manager: " + own : "");
        }

        private static string S(Dictionary<string, object> d, string k) => d.TryGetValue(k, out object v) && v != null ? v.ToString().Trim() : "";

        private static List<string> L(Dictionary<string, object> d, string k)
        {
            var l = new List<string>();
            if (d.TryGetValue(k, out object v) && v is List<object> a)
                foreach (object o in a) if (o != null) l.Add(o.ToString());
            return l;
        }

        public static string IdOf(ModMetaData m)
        {
            ulong id = ShareCode.WorkshopIdOf(m);
            if (id != 0) return id.ToString();
            try
            {
                string f = Path.Combine(m.RootDir.FullName, "About", "PublishedFileId.txt");
                if (File.Exists(f)) return File.ReadAllText(f).Trim();
            }
            catch { }
            return "";
        }

        // every installed mod that has a replacement, best reasons first
        public static List<ReplaceRow> Find(List<ModMetaData> active, List<LogBlame> blames, ICollection<string> hidden)
        {
            var rows = new List<ReplaceRow>();
            List<ReplaceRule> all = Rules;
            if (all.Count == 0) return rows;
            var byId = new Dictionary<string, List<ReplaceRule>>();
            var byPkg = new Dictionary<string, List<ReplaceRule>>(StringComparer.OrdinalIgnoreCase);
            foreach (ReplaceRule r in all)
            {
                if (r.oldId.Length > 0) { if (!byId.TryGetValue(r.oldId, out var l)) byId[r.oldId] = l = new List<ReplaceRule>(); l.Add(r); }
                else if (r.oldPackage.Length > 0) { if (!byPkg.TryGetValue(r.oldPackage, out var l)) byPkg[r.oldPackage] = l = new List<ReplaceRule>(); l.Add(r); }
            }
            var installedById = new Dictionary<string, ModMetaData>();
            foreach (ModMetaData m in ModLister.AllInstalledMods)
            {
                string id = IdOf(m);
                if (id.Length > 0) installedById[id] = m;
            }
            string cur = Cur;
            foreach (ModMetaData m in ModLister.AllInstalledMods)
            {
                if (m.Official) continue;
                string id = IdOf(m);
                List<ReplaceRule> cand = null;
                if (id.Length > 0) byId.TryGetValue(id, out cand);
                if (cand == null) byPkg.TryGetValue(m.PackageIdNonUnique ?? "", out cand);
                if (cand == null) continue;
                // the replacement for this game version first, then the newest one
                ReplaceRule best = cand.Where(r => r.newId != id)
                    .OrderByDescending(r => r.newVersions.Contains(cur))
                    .ThenByDescending(r => r.newVersions.Count == 0 ? "" : r.newVersions.Max())
                    .FirstOrDefault();
                if (best == null) continue;
                if (hidden.Contains(id + ">" + best.newId)) continue;
                bool oldOk = Rules_VersionOk(m, cur);
                bool newOk = best.newVersions.Count == 0 || best.newVersions.Contains(cur);
                if (!newOk && oldOk) continue;      // the old one works here and the replacement does not
                installedById.TryGetValue(best.newId, out ModMetaData nm);
                if (nm == m) continue;
                var row = new ReplaceRow
                {
                    oldMod = m, rule = best, newMod = nm,
                    oldActive = active.Contains(m), oldForThisVersion = oldOk, newForThisVersion = newOk,
                    errors = blames?.FirstOrDefault(b => b.mod == m)?.count ?? 0
                };
                rows.Add(row);
            }
            return rows.OrderByDescending(r => r.Score).ThenBy(r => r.oldMod.Name).ToList();
        }

        private static bool Rules_VersionOk(ModMetaData m, string cur)
        {
            try { return m.SupportedVersionsReadOnly.Any(v => v.Major + "." + v.Minor == cur); }
            catch { return true; }
        }
    }

    // a small JSON reader (objects, arrays, strings, numbers, true/false/null)
    public static class MiniJson
    {
        public static object Parse(string s)
        {
            int i = 0;
            return Value(s, ref i);
        }

        private static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        private static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) return null;
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>();
                i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    i++;   // :
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    break;
                }
                return d;
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                while (true)
                {
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    break;
                }
                return l;
            }
            if (c == '"') return Str(s, ref i);
            int st = i;
            while (i < s.Length && ",}] \t\r\n".IndexOf(s[i]) < 0) i++;
            string w = s.Substring(st, i - st);
            if (w == "true") return true;
            if (w == "false") return false;
            if (w == "null") return null;
            return double.TryParse(w, NumberStyles.Float, CultureInfo.InvariantCulture, out double dv) ? (object)dv : w;
        }

        private static string Str(string s, ref int i)
        {
            var sb = new StringBuilder();
            if (i < s.Length && s[i] == '"') i++;
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') break;
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': break;
                        case 'u':
                            if (i + 4 <= s.Length && int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int cp)) sb.Append((char)cp);
                            i += 4;
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            return sb.ToString();
        }
    }
}
