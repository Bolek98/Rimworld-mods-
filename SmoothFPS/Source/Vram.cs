using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;

namespace GraphicsTuner
{
    // 1.3.0: video memory (1.7 / Waylith only, read by reflection – nothing happens on 1.6).
    //  * how much of the graphics card's memory the game uses and how much the driver allows it
    //    (other programs – browser, Discord, OBS, Medal – take from the same card),
    //  * a warning when it is almost full: textures then move out of the card and may show up
    //    late, blank or as red crosses,
    //  * which mods take the most of it (textures counted per mod).
    public static class Vram
    {
        private static bool tried, ok;
        private static MethodInfo trySummary;
        private static PropertyInfo pUsed, pBudget, pTotal;
        private static Type allocType;
        private static MethodInfo collectAll;
        private static PropertyInfo aCat, aBytes, aName;

        public static long UsedBytes, BudgetBytes, TotalBytes;
        private static float nextRead;
        private static float lastWarn = -9999f;

        public static bool Available
        {
            get
            {
                Init();
                return ok;
            }
        }

        // 1.6.1: GenTypes only searches the game's and mods' assemblies – engine types are asked for by name
        private static Type W(string name) => Type.GetType(name + ", Waylith") ?? GenTypes.GetTypeInAnyAssembly(name);

        private static void Init()
        {
            if (tried)
                return;
            tried = true;
            try
            {
                Type rep = W("Waylith.Graphics.GpuMemoryReport");
                Type sum = W("Waylith.Graphics.GpuMemorySummary");
                if (rep == null || sum == null)
                    return;
                trySummary = rep.GetMethod("TryGetSummary", BindingFlags.Public | BindingFlags.Static);
                pUsed = sum.GetProperty("DeviceLocalDriverUsageBytes");
                pBudget = sum.GetProperty("DeviceLocalBudgetBytes");
                pTotal = sum.GetProperty("DeviceLocalTotalBytes");
                Type tracker = W("Waylith.Graphics.GpuMemoryTracker");
                allocType = tracker?.GetNestedType("TrackedAllocation", BindingFlags.Public);
                if (allocType != null)
                {
                    collectAll = tracker.GetMethod("CollectAll", BindingFlags.Public | BindingFlags.Static);
                    aCat = allocType.GetProperty("Category");
                    aBytes = allocType.GetProperty("Bytes");
                    aName = allocType.GetProperty("Name");
                }
                ok = trySummary != null && pUsed != null && pBudget != null;
            }
            catch
            {
                ok = false;
            }
        }

        public static bool Read()
        {
            if (!Available)
                return false;
            try
            {
                object[] args = { null };
                if (!(bool)trySummary.Invoke(null, args) || args[0] == null)
                    return false;
                UsedBytes = (long)pUsed.GetValue(args[0]);
                BudgetBytes = (long)pBudget.GetValue(args[0]);
                TotalBytes = pTotal != null ? (long)pTotal.GetValue(args[0]) : 0L;
                return BudgetBytes > 0;
            }
            catch
            {
                ok = false;
                return false;
            }
        }

        public static float Fill => BudgetBytes > 0 ? (float)UsedBytes / BudgetBytes : 0f;

        public static string Gb(long b) => (b / 1073741824.0).ToString("0.00");

        // every 5 seconds; a warning (at most every 10 minutes) when the card is almost full
        public static void Tick()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextRead)
                return;
            nextRead = now + 5f;
            GfxSettings s = GraphicsTunerMod.Settings;
            if (s == null || !s.vramWarn || !Read())
                return;
            if (Fill >= 0.95f && now - lastWarn > 600f && Current.ProgramState == ProgramState.Playing)
            {
                lastWarn = now;
                string top = string.Join(", ", TopMods(3).Select(kv => kv.Key + " " + Gb(kv.Value) + " GB").ToArray());
                Messages.Message("GT_VramWarn".Translate(Gb(UsedBytes), Gb(BudgetBytes), top), MessageTypeDefOf.CautionInput, false);
                MLog.Warn("[Smooth FPS] video memory almost full: " + Gb(UsedBytes) + " / " + Gb(BudgetBytes) + " GB. Most: " + top);
            }
        }

        public static string Line()
        {
            if (!Read())
                return null;
            return "GT_VramNow".Translate(Gb(UsedBytes), Gb(BudgetBytes), Gb(TotalBytes), Mathf.RoundToInt(Fill * 100f));
        }

        // ---- who uses it -------------------------------------------------------
        private static Dictionary<string, string> texToMod;

        private static string ModOf(string name)
        {
            if (name.NullOrEmpty())
                return null;
            if (texToMod == null)
            {
                texToMod = new Dictionary<string, string>();
                foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
                {
                    try
                    {
                        var holder = m.GetContentHolder<Texture2D>();
                        if (holder?.contentList == null)
                            continue;
                        foreach (var kv in holder.contentList)
                        {
                            if (kv.Value == null)
                                continue;
                            string n = kv.Value.name;
                            if (!n.NullOrEmpty())
                                texToMod[n] = m.Name;    // later mods win, as in the game
                            string leaf = kv.Key;
                            int slash = leaf.LastIndexOf('/');
                            if (slash >= 0) leaf = leaf.Substring(slash + 1);
                            if (!texToMod.ContainsKey(leaf))
                                texToMod[leaf] = m.Name;
                        }
                    }
                    catch { }
                }
            }
            if (texToMod.TryGetValue(name, out string mod))
                return mod;
            if (name.StartsWith("BodyFitRefit_") || name.StartsWith("BodyFitClip_"))
                return "Body Fit";
            return null;
        }

        public static List<KeyValuePair<string, long>> TopMods(int count)
        {
            var bytes = new Dictionary<string, long>();
            if (!Available || collectAll == null)
                return new List<KeyValuePair<string, long>>();
            try
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(allocType));
                collectAll.Invoke(null, new object[] { list });
                foreach (object a in list)
                {
                    string cat = aCat.GetValue(a)?.ToString();
                    long b = (long)aBytes.GetValue(a);
                    string key;
                    if (cat == "GameTexture" || cat == "UITexture")
                        key = ModOf(aName.GetValue(a) as string) ?? "GT_VramGame".Translate().ToString();
                    else if (cat == "RenderTarget")
                        key = "GT_VramTargets".Translate().ToString();
                    else if (cat == "Atlas")
                        key = "GT_VramAtlas".Translate().ToString();
                    else
                        key = "GT_VramOther".Translate().ToString();
                    bytes.TryGetValue(key, out long o);
                    bytes[key] = o + b;
                }
            }
            catch (Exception ex)
            {
                MLog.WarnOnce("[Smooth FPS] video memory list: " + ex.Message, 0x5f0a11);
            }
            return bytes.OrderByDescending(kv => kv.Value).Take(count).ToList();
        }

        public static string Report()
        {
            var sb = new StringBuilder();
            string line = Line();
            sb.AppendLine(line ?? (string)"GT_VramNone".Translate());
            sb.AppendLine();
            sb.AppendLine("GT_VramTop".Translate());
            foreach (var kv in TopMods(15))
                sb.AppendLine("  " + Gb(kv.Value) + " GB – " + kv.Key);
            sb.AppendLine();
            sb.AppendLine("GT_VramAdvice".Translate());
            return sb.ToString().TrimEnd();
        }
            // ---- 1.6.3: every mod's textures, whether they sit in the card now or not ----
        // The tracker above only sees what is in the card right now (and puts atlases and screen
        // surfaces in their own bins). This counts each mod's own pictures by their size, so the
        // list shows which mods would free the most when switched off.
        public sealed class ModTex
        {
            public string mod;
            public int count, big;
            public long bytes;
            public string largest;
            public long largestBytes;
        }

        private static List<ModTex> modTex;

        public static long TexBytes(Texture2D t)
        {
            if (t == null)
                return 0;
            int w = t.width, h = t.height;
            float bpp;
            string f = t.format.ToString();
            if (f.StartsWith("DXT1") || f.StartsWith("BC4") || f.Contains("ETC_") || f.StartsWith("ETC2_RGB") && !f.Contains("A8"))
                bpp = 0.5f;
            else if (f.StartsWith("DXT5") || f.StartsWith("BC5") || f.StartsWith("BC7") || f.StartsWith("BC6") || f.Contains("ASTC") || f.Contains("ETC2_RGBA8"))
                bpp = 1f;
            else if (f == "Alpha8" || f == "R8")
                bpp = 1f;
            else if (f.Contains("RGBAHalf") || f.Contains("RGBA64"))
                bpp = 8f;
            else if (f.Contains("RGBAFloat"))
                bpp = 16f;
            else
                bpp = 4f;
            float mips = t.mipmapCount > 1 ? 1.333f : 1f;
            return (long)(w * (long)h * bpp * mips);
        }

        public static List<ModTex> ModTextures(bool refresh = false)
        {
            if (modTex != null && !refresh)
                return modTex;
            var list = new List<ModTex>();
            var seen = new HashSet<Texture2D>();
            foreach (ModContentPack m in LoadedModManager.RunningModsListForReading)
            {
                var e = new ModTex { mod = m.Name };
                try
                {
                    var holder = m.GetContentHolder<Texture2D>();
                    if (holder?.contentList != null)
                        foreach (var kv in holder.contentList)
                        {
                            Texture2D t = kv.Value;
                            if (t == null || !seen.Add(t))
                                continue;
                            long b = 0;
                            try { b = TexBytes(t); } catch { }
                            e.count++;
                            e.bytes += b;
                            if (b >= 4L * 1024 * 1024) e.big++;
                            if (b > e.largestBytes) { e.largestBytes = b; e.largest = kv.Key; }
                        }
                }
                catch { }
                if (e.count > 0)
                    list.Add(e);
            }
            modTex = list.OrderByDescending(x => x.bytes).ToList();
            return modTex;
        }

        // the biggest single surfaces the game draws into (screen, portraits, effects)
        public static List<KeyValuePair<string, long>> TopTargets(int count)
        {
            var bytes = new Dictionary<string, long>();
            if (!Available || collectAll == null)
                return new List<KeyValuePair<string, long>>();
            try
            {
                var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(allocType));
                collectAll.Invoke(null, new object[] { list });
                foreach (object a in list)
                {
                    if (aCat.GetValue(a)?.ToString() != "RenderTarget")
                        continue;
                    string n = aName.GetValue(a) as string;
                    if (n.NullOrEmpty()) n = "?";
                    // portraits and the like share a name prefix – group them
                    int cut = n.IndexOfAny(new[] { '#', '(', '_' });
                    string key = cut > 3 ? n.Substring(0, cut) : n;
                    bytes.TryGetValue(key, out long o);
                    bytes[key] = o + (long)aBytes.GetValue(a);
                }
            }
            catch { }
            return bytes.OrderByDescending(kv => kv.Value).Take(count).ToList();
        }

        public static string Mb(long b) => (b / 1048576.0).ToString(b < 10L * 1048576 ? "0.0" : "0");
    }

    // 1.6.3: the window "which mods take the video memory"
    public class Dialog_VramMods : Window
    {
        private Vector2 scroll;
        private List<Vram.ModTex> rows;
        private List<KeyValuePair<string, long>> bins, targets;
        private long total;

        public override Vector2 InitialSize => new Vector2(760f, 640f);

        public Dialog_VramMods()
        {
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
            Refresh();
        }

        private void Refresh()
        {
            rows = Vram.ModTextures(true);
            total = rows.Sum(r => r.bytes);
            bins = Vram.TopMods(6);
            targets = Vram.TopTargets(5);
        }

        public override void DoWindowContents(Rect inRect)
        {
            float y = inRect.y;
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(inRect.x, y, inRect.width - 120f, 32f), "GT_VramTitle".Translate());
            Text.Font = GameFont.Small;
            if (Widgets.ButtonText(new Rect(inRect.xMax - 110f, y + 2f, 100f, 28f), "GT_VramRefresh".Translate()))
                Refresh();
            y += 36f;
            string line = Vram.Line();
            if (line != null)
            {
                Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), line);
                y += 24f;
                if (bins.Count > 0)
                {
                    GUI.color = new Color(0.75f, 0.78f, 0.82f);
                    string b = string.Join("  ·  ", bins.Select(kv => kv.Key + " " + Vram.Gb(kv.Value) + " GB").ToArray());
                    float h = Text.CalcHeight(b, inRect.width);
                    Widgets.Label(new Rect(inRect.x, y, inRect.width, h), b);
                    y += h;
                    if (targets.Count > 0)
                    {
                        string t = "GT_VramTargetsTop".Translate(string.Join(", ", targets.Select(kv => kv.Key + " " + Vram.Mb(kv.Value) + " MB").ToArray()));
                        h = Text.CalcHeight(t, inRect.width);
                        Widgets.Label(new Rect(inRect.x, y, inRect.width, h), t);
                        y += h;
                    }
                    GUI.color = Color.white;
                }
                y += 6f;
            }
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 24f), "GT_VramModsHead".Translate(rows.Count, Vram.Gb(total)));
            y += 26f;
            // header
            GUI.color = new Color(0.75f, 0.78f, 0.82f);
            Widgets.Label(new Rect(inRect.x + 4f, y, 300f, 22f), "GT_VramColMod".Translate());
            Widgets.Label(new Rect(inRect.xMax - 330f, y, 90f, 22f), "GT_VramColSize".Translate());
            Widgets.Label(new Rect(inRect.xMax - 236f, y, 220f, 22f), "GT_VramColCount".Translate());
            GUI.color = Color.white;
            y += 24f;
            Rect outR = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 52f);
            float rowH = 30f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, rows.Count * rowH);
            long max = rows.Count > 0 ? Math.Max(1L, rows[0].bytes) : 1L;
            Widgets.BeginScrollView(outR, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1), last = Mathf.Min(rows.Count - 1, (int)((scroll.y + outR.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                Vram.ModTex r = rows[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                // bar behind the size
                float frac = (float)r.bytes / max;
                Color bar = frac > 0.5f ? new Color(0.85f, 0.35f, 0.3f, 0.35f) : frac > 0.15f ? new Color(0.9f, 0.7f, 0.3f, 0.3f) : new Color(0.4f, 0.7f, 0.45f, 0.25f);
                Widgets.DrawBoxSolid(new Rect(row.x, row.y, row.width * frac, row.height), bar);
                string name = ((i + 1) + ". " + r.mod).Truncate(row.width - 340f);
                Widgets.Label(new Rect(row.x + 4f, row.y + 4f, row.width - 340f, 24f), name);
                Text.Anchor = TextAnchor.UpperRight;
                Widgets.Label(new Rect(row.xMax - 330f, row.y + 4f, 80f, 24f), Vram.Mb(r.bytes) + " MB");
                Text.Anchor = TextAnchor.UpperLeft;
                GUI.color = new Color(0.75f, 0.78f, 0.82f);
                Widgets.Label(new Rect(row.xMax - 236f, row.y + 4f, 232f, 24f), ("GT_VramCount".Translate(r.count, r.big)).ToString().Truncate(232f));
                GUI.color = Color.white;
                if (Mouse.IsOver(row))
                    TooltipHandler.TipRegion(row, "GT_VramRowTip".Translate(r.mod, Vram.Mb(r.bytes), r.count, r.big, r.largest ?? "-", Vram.Mb(r.largestBytes)));
            }
            Widgets.EndScrollView();
            GUI.color = new Color(0.75f, 0.78f, 0.82f);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(inRect.x, inRect.yMax - 48f, inRect.width, 48f), "GT_VramModsNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
        }
    }
}
