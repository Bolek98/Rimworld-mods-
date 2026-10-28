using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using RimWorld;
using Verse;

namespace ModSortManager
{
    // ---------------------------------------------------------------------------
    // 0.3.0: load order rules for the smart sort
    //  - the player's own rules (mod settings): always at the top / bottom, after / before a mod
    //  - rules shipped with Mod Sort Manager (Data/rules.xml)
    //  - RimSort's community rules (communityRules.json) when RimSort is installed
    // A rule that would make a loop with the mods' own rules is skipped.
    // ---------------------------------------------------------------------------
    public class OrderRule
    {
        public string mod;      // package id, lower case
        public string kind;     // top, bottom, after, before
        public string other;    // for after / before
        public int source;      // 0 user, 1 MSM file, 2 RimSort

        public string Describe()
        {
            string what;
            if (kind == "top") what = "MSM_RuleTop".Translate();
            else if (kind == "bottom") what = "MSM_RuleBottom".Translate();
            else
            {
                string name = MSM.FindMod(other)?.Name ?? other;
                what = (kind == "after" ? "MSM_RuleAfter" : "MSM_RuleBefore").Translate(name);
            }
            string src = source == 0 ? "MSM_RuleSrcUser".Translate() : source == 1 ? "MSM_RuleSrcMsm".Translate() : "MSM_RuleSrcRimSort".Translate();
            return what + " (" + src + ")";
        }

        public string Encode() => mod + "|" + kind + "|" + (other ?? "");

        public static OrderRule Decode(string s)
        {
            if (s.NullOrEmpty()) return null;
            string[] p = s.Split('|');
            if (p.Length < 2) return null;
            var r = new OrderRule { mod = p[0].Trim().ToLowerInvariant(), kind = p[1].Trim().ToLowerInvariant(), other = p.Length > 2 ? p[2].Trim().ToLowerInvariant() : null, source = 0 };
            return r.Valid ? r : null;
        }

        public bool Valid => !mod.NullOrEmpty() && (kind == "top" || kind == "bottom" || ((kind == "after" || kind == "before") && !other.NullOrEmpty()));
    }

    public static class Rules
    {
        private static List<OrderRule> fileRules;
        private static HashSet<string> fileNoWarn;
        private static bool warned;

        public static string Root;   // Mod Sort Manager's own folder (set at start)

        private static void Warn(string what, Exception e)
        {
            if (warned) return;
            warned = true;
            Log.Warning("[Mod Sort Manager] " + what + ": " + e.Message);
        }

        // ---------- user rules ----------
        public static List<OrderRule> User()
        {
            var res = new List<OrderRule>();
            List<string> l = MSMMod.Settings?.userRules;
            if (l == null) return res;
            foreach (string s in l) { OrderRule r = OrderRule.Decode(s); if (r != null) res.Add(r); }
            return res;
        }

        public static List<OrderRule> UserFor(ModMetaData m) => User().Where(r => r.mod == Id(m)).ToList();

        public static void AddUser(OrderRule r)
        {
            if (r == null || !r.Valid) return;
            string e = r.Encode();
            if (!MSMMod.Settings.userRules.Contains(e)) MSMMod.Settings.userRules.Add(e);
            // top and bottom exclude each other
            if (r.kind == "top" || r.kind == "bottom")
            {
                string opp = r.mod + "|" + (r.kind == "top" ? "bottom" : "top") + "|";
                MSMMod.Settings.userRules.Remove(opp);
            }
            MSMMod.Settings.Write();
        }

        public static void RemoveUser(OrderRule r)
        {
            MSMMod.Settings.userRules.Remove(r.Encode());
            MSMMod.Settings.Write();
        }

        public static string Id(ModMetaData m) => m?.PackageIdNonUnique ?? "";

        // all rules the smart sort follows, in this order: user, shipped file, RimSort
        public static List<OrderRule> ForSort()
        {
            var res = User();
            if (MSMMod.Settings == null || MSMMod.Settings.sharedRules)
            {
                LoadFiles();
                res.AddRange(fileRules);
            }
            return res;
        }

        public static void LoadFiles()
        {
            if (fileRules != null) return;
            fileRules = new List<OrderRule>();
            try { ReadXmlRules(Path.Combine(Root ?? "", "Data", "rules.xml"), 1); }
            catch (Exception e) { Warn("rules.xml", e); }
            try { ReadRimSort(); }
            catch (Exception e) { Warn("RimSort rules", e); }
        }

        // <MsmRules><rule mod="a" position="top|bottom" after="b" before="c"/></MsmRules>
        private static void ReadXmlRules(string path, int source)
        {
            if (!File.Exists(path)) return;
            var doc = new XmlDocument();
            doc.Load(path);
            XmlNodeList nodes = doc.SelectNodes("//rule");
            if (nodes == null) return;
            foreach (XmlNode n in nodes)
            {
                string mod = n.Attributes?["mod"]?.Value?.Trim().ToLowerInvariant();
                if (mod.NullOrEmpty()) continue;
                string pos = n.Attributes["position"]?.Value?.Trim().ToLowerInvariant();
                if (pos == "top" || pos == "bottom") fileRules.Add(new OrderRule { mod = mod, kind = pos, source = source });
                foreach (string k in new[] { "after", "before" })
                {
                    string o = n.Attributes[k]?.Value;
                    if (o.NullOrEmpty()) continue;
                    foreach (string one in o.Split(',', ';'))
                        if (!one.Trim().NullOrEmpty()) fileRules.Add(new OrderRule { mod = mod, kind = k, other = one.Trim().ToLowerInvariant(), source = source });
                }
            }
        }

        // RimSort keeps its databases in its own data folder; the community rules file is
        // { "rules": { "<packageId>": { "loadAfter": { "<id>": {...} }, "loadBefore": {...}, "loadBottom": { "value": true } } } }
        private static IEnumerable<string> RimSortCandidates()
        {
            var bases = new List<string>();
            try { bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)); } catch { }
            try { bases.Add(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)); } catch { }
            try
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                bases.Add(Path.Combine(home, ".local", "share"));
                bases.Add(Path.Combine(home, "Library", "Application Support"));
            }
            catch { }
            foreach (string b in bases.Where(x => !x.NullOrEmpty()).Distinct())
                foreach (string app in new[] { "RimSort", "rimsort" })
                {
                    yield return Path.Combine(b, app, "dbs", "communityRules.json");
                    yield return Path.Combine(b, app, "dbs", "Community-Rules-Database", "communityRules.json");
                    yield return Path.Combine(b, app, "communityRules.json");
                }
        }

        public static string RimSortFile;

        private static void ReadRimSort()
        {
            string path = RimSortCandidates().FirstOrDefault(p => { try { return File.Exists(p); } catch { return false; } });
            if (path == null) return;
            RimSortFile = path;
            object root = Json.Parse(File.ReadAllText(path));
            if (!(root is Dictionary<string, object> top)) return;
            if (!(top.TryGetValue("rules", out object rulesObj) && rulesObj is Dictionary<string, object> rules)) return;
            foreach (var kv in rules)
            {
                string mod = kv.Key.Trim().ToLowerInvariant();
                if (!(kv.Value is Dictionary<string, object> r)) continue;
                foreach (string k in new[] { "loadAfter", "loadBefore" })
                    if (r.TryGetValue(k, out object o) && o is Dictionary<string, object> ids)
                        foreach (string other in ids.Keys)
                            fileRules.Add(new OrderRule { mod = mod, kind = k == "loadAfter" ? "after" : "before", other = other.Trim().ToLowerInvariant(), source = 2 });
                foreach (string k in new[] { "loadBottom", "loadTop", "loadFirst" })
                    if (r.TryGetValue(k, out object o) && o is Dictionary<string, object> v && v.TryGetValue("value", out object val) && val is bool b && b)
                        fileRules.Add(new OrderRule { mod = mod, kind = k == "loadBottom" ? "bottom" : "top", source = 2 });
            }
        }

        // ---------- no version warning ----------
        public static bool VersionOk(ModMetaData m)
        {
            if (m == null || m.Official || m.VersionCompatible) return true;
            try
            {
                string id = Id(m);
                if (MSMMod.Settings != null && MSMMod.Settings.noWarn.Contains(id)) return true;
                LoadNoWarn();
                return fileNoWarn.Contains(id);
            }
            catch { return false; }
        }

        public static bool UserNoWarn(ModMetaData m) => MSMMod.Settings != null && MSMMod.Settings.noWarn.Contains(Id(m));

        public static void SetUserNoWarn(ModMetaData m, bool on)
        {
            string id = Id(m);
            if (on) { if (!MSMMod.Settings.noWarn.Contains(id)) MSMMod.Settings.noWarn.Add(id); }
            else MSMMod.Settings.noWarn.Remove(id);
            MSMMod.Settings.Write();
        }

        private static void LoadNoWarn()
        {
            if (fileNoWarn != null) return;
            fileNoWarn = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try { ReadIdList(Path.Combine(Root ?? "", "Data", "noVersionWarning.xml")); }
            catch (Exception e) { Warn("noVersionWarning.xml", e); }
            // the "No Version Warning" mod ships lists of package ids that work in newer versions
            try
            {
                string cur = VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor;
                foreach (ModMetaData nvw in ModLister.AllInstalledMods.Where(x => x.PackageIdNonUnique.Contains("noversionwarning") || x.Name == "No Version Warning"))
                {
                    string dir = nvw.RootDir.FullName;
                    string verDir = Path.Combine(dir, cur);
                    string scan = Directory.Exists(verDir) ? verDir : dir;
                    foreach (string f in Directory.GetFiles(scan, "*.xml", SearchOption.AllDirectories))
                    {
                        if (f.Replace('\\', '/').Contains("/About/")) continue;
                        ReadIdList(f);
                    }
                }
            }
            catch (Exception e) { Warn("No Version Warning", e); }
        }

        // every <li> that looks like a package id
        private static void ReadIdList(string path)
        {
            if (!File.Exists(path)) return;
            var doc = new XmlDocument();
            doc.Load(path);
            XmlNodeList lis = doc.SelectNodes("//li");
            if (lis == null) return;
            foreach (XmlNode li in lis)
            {
                string t = li.InnerText?.Trim().ToLowerInvariant();
                if (!t.NullOrEmpty() && t.Contains('.') && !t.Contains(' ') && t.Length < 120) fileNoWarn.Add(t.Replace("_steam", ""));
            }
        }
    }

    // a tiny JSON reader (objects, arrays, strings, numbers, true/false/null) for the RimSort rules
    public static class Json
    {
        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i);
            return v;
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
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (i < s.Length)
                {
                    Ws(s, ref i);
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ':') i++;
                    d[k] = Value(s, ref i);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; break; }
                    throw new FormatException("bad object at " + i);
                }
                return d;
            }
            if (c == '[')
            {
                var l = new List<object>();
                i++;
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (i < s.Length)
                {
                    l.Add(Value(s, ref i));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; break; }
                    throw new FormatException("bad array at " + i);
                }
                return l;
            }
            if (c == '"') return Str(s, ref i);
            if (string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (i == st) throw new FormatException("bad value at " + i);
            double.TryParse(s.Substring(st, i - st), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double num);
            return num;
        }

        private static string Str(string s, ref int i)
        {
            if (i >= s.Length || s[i] != '"') throw new FormatException("string expected at " + i);
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '"')
            {
                char c = s[i++];
                if (c == '\\' && i < s.Length)
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u':
                            if (i + 4 <= s.Length) { sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; }
                            break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            i++;
            return sb.ToString();
        }
    }
}
