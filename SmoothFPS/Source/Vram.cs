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
    }
}
