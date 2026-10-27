using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // ------------------------------------------------------------------
    // 1.5.0 Error doctor: the red errors in the log grouped by message, with the mod that
    // really caused each one (from its own code in the stack – never from the Harmony
    // "PREFIX/POSTFIX" list, which only names mods that touch the same method), a short
    // explanation in plain words, how often and when. Reads the game's own log memory;
    // the work is done only when the window is open or a few new errors arrived.
    // ------------------------------------------------------------------
    public static class ErrorDoctor
    {
        public sealed class Group
        {
            public string key;
            public ModContentPack mod;
            public string kind;            // translation key of the explanation
            public string firstLine;
            public string sample;          // full text + stack of the first one
            public int count;
            public bool warning;
            public DateTime first, last;
        }

        private static readonly Dictionary<string, Group> groups = new Dictionary<string, Group>();
        private static readonly Dictionary<LogMessage, int> seen = new Dictionary<LogMessage, int>(new RefEq());
        private static Dictionary<string, ModContentPack> nsMap;
        private static List<KeyValuePair<string, ModContentPack>> dirMap;
        private static float nextCheck;
        private static float lastAlert = -1000f;
        private static int alerted;

        private sealed class RefEq : IEqualityComparer<LogMessage>
        {
            public bool Equals(LogMessage a, LogMessage b) => ReferenceEquals(a, b);
            public int GetHashCode(LogMessage m) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(m);
        }

        public static IEnumerable<Group> Groups => groups.Values;

        // ---- which mod owns which code namespace (built once, on first use) ----
        private static void BuildMaps()
        {
            if (nsMap != null)
                return;
            nsMap = new Dictionary<string, ModContentPack>();
            dirMap = new List<KeyValuePair<string, ModContentPack>>();
            foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
            {
                try
                {
                    if (!mod.RootDir.NullOrEmpty())
                        dirMap.Add(new KeyValuePair<string, ModContentPack>(mod.RootDir.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant(), mod));
                    if (mod.IsCoreMod || mod.assemblies?.loadedAssemblies == null)
                        continue;
                    foreach (System.Reflection.Assembly a in mod.assemblies.loadedAssemblies)
                    {
                        Type[] types;
                        try { types = a.GetTypes(); }
                        catch (System.Reflection.ReflectionTypeLoadException e) { types = e.Types; }
                        catch { continue; }
                        if (types == null) continue;
                        foreach (Type t in types)
                        {
                            if (t == null) continue;
                            string ns = t.Namespace;
                            string key = ns.NullOrEmpty() ? t.Name : ns;
                            if (!key.NullOrEmpty() && !nsMap.ContainsKey(key) && !Game(key))
                                nsMap[key] = mod;
                        }
                    }
                }
                catch
                {
                }
            }
            // longest folders first so a mod inside another folder is matched before its parent
            dirMap.Sort((x, y) => y.Key.Length.CompareTo(x.Key.Length));
        }

        private static bool Game(string ns) =>
            ns.StartsWith("System") || ns.StartsWith("Verse") || ns.StartsWith("RimWorld") || ns.StartsWith("UnityEngine") || ns.StartsWith("Unity")
            || ns.StartsWith("HarmonyLib") || ns.StartsWith("MonoMod") || ns.StartsWith("Mono.") || ns.StartsWith("Microsoft") || ns.StartsWith("Waylith")
            || ns.StartsWith("Brutal") || ns.StartsWith("Steamworks") || ns.StartsWith("LudeonTK") || ns.StartsWith("<");

        private static readonly Regex frameRx = new Regex(@"^\s*(?:at\s+)?(?:\(wrapper[^)]*\)\s*)?([A-Za-z_][\w`]*(?:[\.:+][\w`<>]+)+)\s*[\(\[]", RegexOptions.Compiled);

        private static ModContentPack ModOfFrame(string line)
        {
            string t = line.TrimStart();
            if (t.StartsWith("- PREFIX") || t.StartsWith("- POSTFIX") || t.StartsWith("- TRANSPILER") || t.StartsWith("- FINALIZER"))
                return null;
            Match m = frameRx.Match(line);
            if (!m.Success)
                return null;
            string[] parts = m.Groups[1].Value.Replace(':', '.').Replace('+', '.').Split('.');
            // longest namespace first: A.B.C.Type.Method → A.B.C, A.B, A; a type without namespace → its name
            for (int k = parts.Length - 2; k >= 1; k--)
            {
                string ns = string.Join(".", parts, 0, k);
                if (Game(ns))
                    return null;
                if (nsMap.TryGetValue(ns, out ModContentPack mod))
                    return mod;
            }
            if (parts.Length >= 2 && nsMap.TryGetValue(parts[0], out ModContentPack m2))
                return m2;
            return null;
        }

        private static ModContentPack Attribute(string text, string stack)
        {
            foreach (string src in new[] { text, stack })
            {
                if (src.NullOrEmpty()) continue;
                foreach (string line in src.Split('\n'))
                {
                    ModContentPack mod = ModOfFrame(line);
                    if (mod != null)
                        return mod;
                }
            }
            // XML and texture errors have no code in the stack – the mod's folder is often in the text
            string low = (text ?? "").Replace('/', '\\').ToLowerInvariant();
            foreach (var kv in dirMap)
                if (low.Contains(kv.Key + "\\"))
                    return kv.Value;
            // "[Some Mod] message" – a mod that writes its own name
            Match tag = Regex.Match(text ?? "", @"^\s*(?:<[^>]+>)*\[([^\]]{2,60})\]");
            if (tag.Success)
            {
                string n = tag.Groups[1].Value.Trim();
                foreach (ModContentPack mod in LoadedModManager.RunningModsListForReading)
                    if (string.Equals(mod.Name, n, StringComparison.OrdinalIgnoreCase) || mod.Name.StartsWith(n + " ", StringComparison.OrdinalIgnoreCase))
                        return mod;
            }
            return null;
        }

        private static string Kind(string text)
        {
            string t = text ?? "";
            if (t.Contains("NullReferenceException")) return "GT_ED_K_Null";
            if (t.Contains("Could not resolve cross-reference") || t.Contains("Could not find type") || t.Contains("Could not find a type") || Regex.IsMatch(t, @"No \S+ named")) return "GT_ED_K_MissingDef";
            if (t.Contains("Patching exception") || t.Contains("HarmonyException") || t.Contains("patch") && t.Contains("ailed")) return "GT_ED_K_Patch";
            if (t.Contains("XML error") || t.Contains("Exception loading def") || t.Contains("doesn't correspond to any field") || t.Contains("XML")) return "GT_ED_K_Xml";
            if (t.Contains("texture") || t.Contains("Texture") || t.Contains("graphic") || t.Contains("Graphic_")) return "GT_ED_K_Texture";
            if (t.IndexOf("duplicate", StringComparison.OrdinalIgnoreCase) >= 0) return "GT_ED_K_Duplicate";
            if (t.Contains("InvalidCastException")) return "GT_ED_K_Cast";
            if (t.Contains("KeyNotFoundException") || t.Contains("IndexOutOfRange") || t.Contains("ArgumentOutOfRange")) return "GT_ED_K_List";
            if (t.Contains("Exception ticking") || t.Contains("Exception in Tick")) return "GT_ED_K_Tick";
            if (t.Contains("generating pawn") || t.Contains("PawnGenerator")) return "GT_ED_K_PawnGen";
            if (t.Contains("JobDriver") || t.Contains("job")) return "GT_ED_K_Job";
            return "GT_ED_K_Other";
        }

        private static string FirstLine(string text)
        {
            string s = Regex.Replace(text ?? "", "<[^>]+>", "");
            int nl = s.IndexOf('\n');
            if (nl > 0) s = s.Substring(0, nl);
            s = s.Trim();
            return s.Length > 300 ? s.Substring(0, 297) + "…" : s;
        }

        private static string KeyOf(string line) => Regex.Replace(line, @"\d+", "#").Substring(0, Math.Min(140, line.Length));

        // reads the new messages of the game's log (cheap when nothing is new)
        public static int Scan(bool warningsToo)
        {
            int added = 0;
            try
            {
                var fresh = new List<LogMessage>();
                using (Log.LockMessages())
                {
                    foreach (LogMessage m in Log.Messages)
                    {
                        if (m == null) continue;
                        bool err = m.type == LogMessageType.Error;
                        if (!err && !(warningsToo && m.type == LogMessageType.Warning)) continue;
                        if (seen.TryGetValue(m, out int r) && r == m.repeats) continue;
                        fresh.Add(m);
                    }
                }
                if (fresh.Count == 0)
                    return 0;
                BuildMaps();
                DateTime now = DateTime.Now;
                foreach (LogMessage m in fresh)
                {
                    seen.TryGetValue(m, out int before);
                    int more = m.repeats - before;
                    seen[m] = m.repeats;
                    if (more <= 0) continue;
                    string first = FirstLine(m.text);
                    string stack = null;
                    try { stack = m.StackTrace; } catch { }
                    ModContentPack mod = Attribute(m.text, stack);
                    string key = (mod?.PackageId ?? "?") + "|" + KeyOf(first);
                    if (!groups.TryGetValue(key, out Group g))
                    {
                        g = new Group { key = key, mod = mod, kind = Kind(m.text), firstLine = first, warning = m.type == LogMessageType.Warning, first = now,
                            sample = (m.text ?? "") + (stack.NullOrEmpty() ? "" : "\n" + stack) };
                        groups[key] = g;
                        added++;
                    }
                    g.count += more;
                    g.last = now;
                }
            }
            catch (Exception e)
            {
                MLog.WarnOnce("[Smooth FPS] error doctor: " + e.Message, 0x5ED0C1);
            }
            return added;
        }

        // every frame: a light look every 20 s; a message when an error from a new place appears
        public static void Update()
        {
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.errorAlert || Current.ProgramState != ProgramState.Playing || Time.realtimeSinceStartup < nextCheck)
                return;
            nextCheck = Time.realtimeSinceStartup + 20f;
            int before = groups.Count;
            int added = Scan(false);
            if (added <= 0 || Time.realtimeSinceStartup - lastAlert < 120f || alerted >= 10)
                return;
            Group g = groups.Values.Where(x => !x.warning).OrderByDescending(x => x.first).FirstOrDefault();
            if (g == null)
                return;
            lastAlert = Time.realtimeSinceStartup;
            alerted++;
            Messages.Message("GT_ED_NewMsg".Translate(g.mod?.Name ?? "GT_ED_Unknown".Translate().ToString()), MessageTypeDefOf.CautionInput, false);
        }

        public static string Explain(Group g) => (g.kind + "").Translate();

        public static void Copy(Group g)
        {
            var sb = new StringBuilder();
            sb.AppendLine("[" + (g.mod?.Name ?? "?") + (g.mod != null ? " – " + g.mod.PackageId : "") + "] ×" + g.count);
            sb.AppendLine(g.sample);
            GUIUtility.systemCopyBuffer = sb.ToString();
            Messages.Message("GT_ED_Copied".Translate(), MessageTypeDefOf.SilentInput, false);
        }

        private static readonly Dictionary<ModContentPack, ulong> ids = new Dictionary<ModContentPack, ulong>();

        // the Workshop id from About/PublishedFileId.txt or the folder name of a Workshop download
        public static ulong WorkshopId(ModContentPack mod)
        {
            if (mod == null) return 0;
            if (ids.TryGetValue(mod, out ulong id)) return id;
            id = 0;
            try
            {
                string f = System.IO.Path.Combine(System.IO.Path.Combine(mod.RootDir, "About"), "PublishedFileId.txt");
                if (System.IO.File.Exists(f)) ulong.TryParse(System.IO.File.ReadAllText(f).Trim(), out id);
                if (id == 0) ulong.TryParse(System.IO.Path.GetFileName(mod.RootDir.TrimEnd('/', '\\')), out id);
            }
            catch
            {
            }
            ids[mod] = id;
            return id;
        }
    }

    public class Dialog_ErrorDoctor : Window
    {
        private Vector2 scroll;
        private bool warnings;
        private List<ErrorDoctor.Group> list = new List<ErrorDoctor.Group>();
        private float nextRefresh;

        public override Vector2 InitialSize => new Vector2(860f, 660f);

        public Dialog_ErrorDoctor()
        {
            doCloseX = true;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = true;
            Refresh();
        }

        private void Refresh()
        {
            ErrorDoctor.Scan(warnings);
            list = ErrorDoctor.Groups.Where(g => warnings || !g.warning).OrderBy(g => g.mod == null ? 1 : 0).ThenByDescending(g => g.count).ToList();
            nextRefresh = Time.realtimeSinceStartup + 3f;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Time.realtimeSinceStartup > nextRefresh)
                Refresh();
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(0f, 0f, inRect.width, 34f), "GT_ED_Title".Translate());
            Text.Font = GameFont.Small;
            int mods = list.Where(g => g.mod != null).Select(g => g.mod).Distinct().Count();
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(0f, 34f, inRect.width - 200f, 24f), "GT_ED_Summary".Translate(list.Sum(g => g.count), list.Count, mods));
            GUI.color = Color.white;
            bool w = warnings;
            Widgets.CheckboxLabeled(new Rect(inRect.width - 190f, 34f, 190f, 24f), "GT_ED_Warnings".Translate(), ref warnings);
            if (w != warnings) Refresh();

            Rect outR = new Rect(0f, 64f, inRect.width, inRect.height - 64f);
            float h = 0f;
            var heights = new List<float>();
            float tw = outR.width - 16f - 24f;
            foreach (var g in list)
            {
                Text.Font = GameFont.Small;
                float hh = 30f + Text.CalcHeight(g.firstLine, tw) + 4f;
                Text.Font = GameFont.Tiny;
                hh += Text.CalcHeight(ErrorDoctor.Explain(g), tw) + 40f;
                Text.Font = GameFont.Small;
                heights.Add(hh);
                h += hh + 8f;
            }
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(h, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < list.Count; i++)
            {
                var g = list[i];
                Rect c = new Rect(0f, y, view.width, heights[i]);
                Widgets.DrawBoxSolid(c, new Color(1f, 1f, 1f, 0.04f));
                Widgets.DrawBoxSolid(new Rect(c.x, c.y, 3f, c.height), g.warning ? MUI.Warn : new Color(0.88f, 0.39f, 0.35f));
                float x = c.x + 12f, cy = c.y + 6f;
                Text.Font = GameFont.Small;
                GUI.color = g.mod == null ? MUI.Muted : Color.white;
                Widgets.Label(new Rect(x, cy, c.width - 260f, 24f), (g.mod?.Name ?? "GT_ED_Unknown".Translate().ToString()).Truncate(c.width - 260f));
                GUI.color = MUI.Muted;
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(c.xMax - 250f, cy, 238f, 24f), "×" + g.count + " · " + g.first.ToString("HH:mm") + (g.last != g.first ? "–" + g.last.ToString("HH:mm") : ""));
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = Color.white;
                cy += 26f;
                float lh = Text.CalcHeight(g.firstLine, tw);
                Widgets.Label(new Rect(x, cy, tw, lh), g.firstLine);
                cy += lh + 4f;
                Text.Font = GameFont.Tiny;
                GUI.color = new Color(0.75f, 0.8f, 0.85f);
                string ex = ErrorDoctor.Explain(g);
                float eh = Text.CalcHeight(ex, tw);
                Widgets.Label(new Rect(x, cy, tw, eh), ex);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                cy += eh + 6f;
                if (Widgets.ButtonText(new Rect(x, cy, 120f, 26f), "GT_ED_Copy".Translate()))
                    ErrorDoctor.Copy(g);
                ulong id = ErrorDoctor.WorkshopId(g.mod);
                if (id != 0 && Widgets.ButtonText(new Rect(x + 128f, cy, 150f, 26f), "GT_ED_Workshop".Translate()))
                    Application.OpenURL("https://steamcommunity.com/sharedfiles/filedetails/?id=" + id);
                TooltipHandler.TipRegion(new Rect(x, c.y, c.width - 20f, cy - c.y), g.sample.Length > 2500 ? g.sample.Substring(0, 2500) + "…" : g.sample);
                y += heights[i] + 8f;
            }
            if (list.Count == 0)
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(0f, 0f, view.width, 80f), "GT_ED_None".Translate());
                Text.Anchor = TextAnchor.UpperLeft;
            }
            Widgets.EndScrollView();
        }
    }
}
