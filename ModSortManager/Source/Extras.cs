using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace ModSortManager
{
    // 0.1.2: things checked once when the main menu appears
    public static class Startup
    {
        private static bool done;

        public static void OnMainMenu()
        {
            if (Bisect.Active)
            {
                if (!Find.WindowStack.IsOpen(typeof(Window_Bisect)))
                    Find.WindowStack.Add(new Window_Bisect());
                return;
            }
            if (done) return;
            done = true;
            if (ReturnToSave.Pending) return;
            // 0.2.1: the last session crashed right after a mod list change – offer the list from before
            if (CrashGuard.Check()) return;
            OrderGuard.Check();
        }
    }

    // ---------------- 0.1.2: order guard ----------------
    // remembers the list from every save made inside the game; after a start it compares
    public static class OrderGuard
    {
        private static string FilePath => Path.Combine(MSM.DataDir, "last_saved_order.txt");
        private static bool checkedOnce;
        private static readonly StringComparer Cmp = StringComparer.OrdinalIgnoreCase;
        // the game's own recovery (ModsConfig.Reset after a load error) must not become "your order"
        public static bool suppress;

        public static List<string> Current()
        {
            return ModsConfig.ActiveModsInLoadOrder.Where(m => m != null).Select(m => m.PackageId).ToList();
        }

        // "foo_steam" and "foo" are the same mod for the comparison
        public static string Norm(string id)
        {
            if (id == null) return "";
            string pf = ModMetaData.SteamModPostfix;
            return id.EndsWith(pf, StringComparison.OrdinalIgnoreCase) ? id.Substring(0, id.Length - pf.Length) : id;
        }

        public static void Remember(IEnumerable<string> ids)
        {
            if (suppress) return;
            try { File.WriteAllLines(FilePath, ids.Where(x => !x.NullOrEmpty()).ToArray()); }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] order guard: " + e.Message); }
        }

        public static void RememberAfterSave()
        {
            if (Bisect.Active) Remember(Bisect.original);   // the test lists are never "your order"
            else Remember(Current());
        }

        public static void Check()
        {
            if (checkedOnce) return;
            checkedOnce = true;
            try
            {
                List<string> cur = Current();
                if (!File.Exists(FilePath) || MSMMod.Settings == null || !MSMMod.Settings.orderGuard)
                {
                    Remember(cur);
                    return;
                }
                List<string> saved = File.ReadAllLines(FilePath).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                List<string> curN = cur.Select(Norm).ToList();
                List<string> savedN = saved.Select(Norm).ToList();
                if (savedN.SequenceEqual(curN, Cmp)) return;

                var curSet = new HashSet<string>(curN, Cmp);
                var savedSet = new HashSet<string>(savedN, Cmp);
                // a new DLC is switched on by the game itself – not a change to undo
                List<string> added = curN.Where(x => !savedSet.Contains(x) && !(MSM.FindMod(x)?.Official ?? false)).ToList();
                List<string> removed = savedN.Where(x => !curSet.Contains(x)).ToList();
                List<string> a = savedN.Where(curSet.Contains).ToList();
                List<string> b = curN.Where(savedSet.Contains).ToList();
                List<string> moved = Moved(a, b);
                // only uninstalled / unsubscribed mods are gone – nothing to restore
                bool onlyUninstalled = removed.All(x => MSM.FindMod(x) == null);
                if (added.Count == 0 && moved.Count == 0 && (removed.Count == 0 || onlyUninstalled))
                {
                    if (removed.Count > 0) Log.Message("[Mod Sort Manager] mods no longer installed: " + string.Join(", ", removed.ToArray()));
                    Remember(cur);
                    return;
                }
                // restore list: your order, plus DLCs the game turned on since
                List<string> restore = saved.ToList();
                foreach (string id in cur)
                {
                    ModMetaData m = MSM.FindMod(id);
                    if (m == null || !m.Official || savedSet.Contains(Norm(id))) continue;
                    int last = restore.FindLastIndex(x => MSM.FindMod(x)?.Official ?? false);
                    restore.Insert(last + 1, id);
                }

                var lines = new List<string>();
                if (moved.Count > 0) lines.Add(Part("MSM_GuardMoved", moved));
                if (added.Count > 0) lines.Add(Part("MSM_GuardAdded", added));
                if (removed.Count > 0) lines.Add(Part("MSM_GuardRemoved", removed));
                Log.Message("[Mod Sort Manager] the mod list differs from the last save: moved " + moved.Count + ", added " + added.Count + ", missing " + removed.Count);
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_GuardText".Translate(string.Join("\n\n", lines.ToArray())),
                    "MSM_GuardRestore".Translate(), () => Find.WindowStack.Add(new Dialog_ModSortManager(restore)),
                    "MSM_GuardKeep".Translate(), () => Remember(cur), null, false, null, null));
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] order guard: " + e.Message);
            }
        }

        private static string Part(string key, List<string> ids)
        {
            string names = string.Join(", ", ids.Take(8).Select(Name).ToArray()) + (ids.Count > 8 ? ", …" : "");
            return key.Translate(ids.Count) + "\n" + names;
        }

        private static string Name(string id)
        {
            ModMetaData m = MSM.FindMod(id);
            return m != null ? m.Name : id;
        }

        // mods that left the longest common order (= really moved, not just shifted)
        private static List<string> Moved(List<string> a, List<string> b)
        {
            int n = a.Count, m = b.Count;
            var dp = new int[n + 1, m + 1];
            for (int i = n - 1; i >= 0; i--)
                for (int j = m - 1; j >= 0; j--)
                    dp[i, j] = Cmp.Equals(a[i], b[j]) ? dp[i + 1, j + 1] + 1 : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            var keep = new HashSet<string>(Cmp);
            int x = 0, y = 0;
            while (x < n && y < m)
            {
                if (Cmp.Equals(a[x], b[y])) { keep.Add(a[x]); x++; y++; }
                else if (dp[x + 1, y] >= dp[x, y + 1]) x++;
                else y++;
            }
            return b.Where(id => !keep.Contains(id)).ToList();
        }
    }

    [HarmonyPatch(typeof(ModsConfig), nameof(ModsConfig.Save))]
    public static class Patch_ModsConfigSave
    {
        public static void Postfix()
        {
            try { OrderGuard.RememberAfterSave(); } catch { }
        }
    }

    [HarmonyPatch(typeof(ModsConfig), nameof(ModsConfig.Reset))]
    public static class Patch_ModsConfigReset
    {
        public static void Prefix() { OrderGuard.suppress = true; }
        public static void Finalizer() { OrderGuard.suppress = false; }
    }

    [HarmonyPatch(typeof(ModsConfig), "SaveFromList")]
    public static class Patch_ModsConfigSaveFromList
    {
        public static bool Prepare() => AccessTools.Method(typeof(ModsConfig), "SaveFromList") != null;

        public static void Postfix(List<string> mods)
        {
            try
            {
                // the save's list may name mods that are not installed – keep only what can load
                if (mods != null) OrderGuard.Remember(mods.Where(id => MSM.FindMod(id) != null));
            }
            catch { }
        }
    }

    // ---------------- 0.1.2: newly added mods ----------------
    public static class NewMods
    {
        // a mod counts as new for this many days after it was added
        public const int Days = 7;
        private static readonly Dictionary<string, DateTime> added = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);
        private static string FilePath => Path.Combine(MSM.DataDir, "known_mods.txt");

        private static string Key(ModMetaData m) => m.RootDir.FullName;

        // when the mod appeared: the folder's creation time (Workshop: the subscription), or the first time we saw it
        public static DateTime? AddedAt(ModMetaData m)
        {
            try
            {
                DateTime t;
                return m != null && !m.Official && added.TryGetValue(Key(m), out t) ? t : (DateTime?)null;
            }
            catch { return null; }
        }

        public static bool IsNew(ModMetaData m)
        {
            DateTime? t = AddedAt(m);
            return t.HasValue && (DateTime.Now - t.Value).TotalDays < Days;
        }

        public static void Refresh()
        {
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] p = line.Split('\t');
                        long ticks;
                        if (p[0].Length == 0 || added.ContainsKey(p[0])) continue;
                        if (p.Length > 1 && long.TryParse(p[1], out ticks)) added[p[0]] = new DateTime(ticks);
                        else added[p[0]] = Created(p[0], DateTime.Now);
                    }
                foreach (ModMetaData m in ModLister.AllInstalledMods)
                {
                    if (m.Official) continue;
                    string k = Key(m);
                    if (!added.ContainsKey(k)) added[k] = Created(k, DateTime.Now);
                }
                File.WriteAllLines(FilePath, added.Select(kv => kv.Key + "\t" + kv.Value.Ticks).ToArray());
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] new mods: " + e.Message);
            }
        }

        private static DateTime Created(string dir, DateTime seen)
        {
            try
            {
                DateTime c = Directory.GetCreationTime(dir);
                return c.Year > 2000 && c < seen ? c : seen;
            }
            catch { return seen; }
        }
    }

    // ---------------- 0.1.2: errors since the mods loaded ----------------
    [HarmonyPatch(typeof(Log), nameof(Log.Error), new[] { typeof(string) })]
    public static class Patch_ErrorCount
    {
        public static int Errors;

        public static void Postfix()
        {
            Interlocked.Increment(ref Errors);
        }
    }

    // ---------------- 0.1.2: find the faulty mod by halving ----------------
    public static class Bisect
    {
        private static string FilePath => Path.Combine(MSM.DataDir, "bisect.txt");
        private static bool loaded;
        public static List<string> original, all, suspects, enabled;
        public static int step;
        public static string found;
        public static bool restarting;

        public static bool Active { get { Load(); return original != null; } }

        public static int RestartsLeft => suspects == null || suspects.Count < 2 ? 0 : Mathf.CeilToInt(Mathf.Log(suspects.Count, 2f));

        private static void Load()
        {
            if (loaded) return;
            loaded = true;
            try
            {
                if (!File.Exists(FilePath)) return;
                var d = new Dictionary<string, string>();
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
                }
                Func<string, List<string>> L = k => d.ContainsKey(k) ? d[k].Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList() : new List<string>();
                original = L("original");
                all = L("all");
                suspects = L("suspects");
                enabled = L("enabled");
                int.TryParse(d.ContainsKey("step") ? d["step"] : "1", out step);
                found = d.ContainsKey("found") && d["found"].Length > 0 ? d["found"] : null;
                if (original.Count == 0) Clear();
                else if (suspects.Count == 0 && found == null) found = "";   // broken state: offer restore only
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] bisect state: " + e.Message);
                original = null;
            }
        }

        private static void Write()
        {
            File.WriteAllLines(FilePath, new[]
            {
                "original=" + string.Join(",", original.ToArray()),
                "all=" + string.Join(",", all.ToArray()),
                "suspects=" + string.Join(",", suspects.ToArray()),
                "enabled=" + string.Join(",", enabled.ToArray()),
                "step=" + step,
                "found=" + (found ?? "")
            });
        }

        public static void Clear()
        {
            original = all = suspects = enabled = null;
            found = null;
            loaded = true;
            try { if (File.Exists(FilePath)) File.Delete(FilePath); } catch { }
        }

        // libraries other mods need, the game, DLCs, Harmony and this mod always stay on
        public static List<ModMetaData> Suspects(List<ModMetaData> active)
        {
            var libs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ModMetaData m in active)
                foreach (ModDependency d in m.Dependencies)
                {
                    if (d == null) continue;
                    if (!d.packageId.NullOrEmpty()) libs.Add(d.packageId);
                    if (d.alternativePackageIds != null) libs.AddRange(d.alternativePackageIds);
                }
            return active.Where(m => m != null && !m.Official && !m.IsCoreMod && !libs.Contains(m.PackageIdNonUnique)
                && !m.SamePackageId("brrainz.harmony", true) && !m.SamePackageId("misak.modsortmanager", true)).ToList();
        }

        public static void Start(List<ModMetaData> active)
        {
            List<ModMetaData> sus = Suspects(active);
            original = active.Select(m => m.PackageId).ToList();
            all = sus.Select(m => m.PackageId).ToList();
            suspects = all.ToList();
            step = 1;
            found = null;
            enabled = Half(suspects);
            MSM.BackupModsConfig();
            Write();
            Apply();
        }

        private static List<string> Half(List<string> s) => s.Take((s.Count + 1) / 2).ToList();

        public static void Answer(bool stillThere)
        {
            var en = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            suspects = stillThere ? suspects.Where(en.Contains).ToList() : suspects.Where(x => !en.Contains(x)).ToList();
            step++;
            if (suspects.Count <= 1)
            {
                found = suspects.FirstOrDefault() ?? "";
                Write();
                return;
            }
            enabled = Half(suspects);
            Write();
            Apply();
        }

        // tested mods ruled out earlier stay off – faster starts
        public static List<string> Expected()
        {
            var allSet = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
            var en = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            return original.Where(id => !allSet.Contains(id) || en.Contains(id)).ToList();
        }

        // did the game really start with this step's mods? (a load error resets to Core + DLC)
        public static bool LoadedAsExpected()
        {
            var cur = new HashSet<string>(OrderGuard.Current().Select(OrderGuard.Norm), StringComparer.OrdinalIgnoreCase);
            List<string> exp = Expected().Where(id => MSM.FindMod(id) != null).Select(OrderGuard.Norm).ToList();
            return exp.Count == cur.Count && exp.All(cur.Contains);
        }

        public static void Reapply() { Apply(); }

        private static void Apply()
        {
            var allSet = new HashSet<string>(all, StringComparer.OrdinalIgnoreCase);
            var en = new HashSet<string>(enabled, StringComparer.OrdinalIgnoreCase);
            List<string> ids = original.Where(id => !allSet.Contains(id) || en.Contains(id)).ToList();
            SaveAndRestart(ids);
        }

        public static void Finish(bool disableFound)
        {
            List<string> ids = original.Where(id => !(disableFound && found != null && string.Equals(id, found, StringComparison.OrdinalIgnoreCase))).ToList();
            Clear();
            SaveAndRestart(ids);
        }

        private static void SaveAndRestart(List<string> ids)
        {
            if (restarting) return;
            try
            {
                restarting = true;
                ModsConfig.SetActiveToList(ids);
                ModsConfig.Save();
                Log.Message("[Mod Sort Manager] bisect: saved " + ids.Count + " mods, restarting");
                GenCommandLine.Restart();
            }
            catch (Exception e)
            {
                Log.Error("[Mod Sort Manager] bisect: " + e);
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_SaveFailed".Translate(e.Message)));
            }
        }
    }

    // small panel in the main menu while the search runs – the menu stays usable
    public class Window_Bisect : Window
    {
        public override Vector2 InitialSize => new Vector2(480f, Bisect.found != null ? 300f : 340f);

        protected override float Margin => 0f;

        public Window_Bisect()
        {
            doCloseX = false;
            closeOnCancel = false;
            closeOnAccept = false;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = false;
            preventCameraMotion = false;
            forcePause = false;
            draggable = true;
            // Dialog-layer windows hide the main menu – this one must leave it usable
            layer = WindowLayer.GameUI;
        }

        private bool? loadedOk;

        protected override void SetInitialSizeAndPosition()
        {
            windowRect = new Rect(20f, UI.screenHeight - InitialSize.y - 80f, InitialSize.x, InitialSize.y).Rounded();
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (!Bisect.Active) { Close(); return; }
            MUI.Fill(inRect, new Color32(24, 27, 31, 255));
            MUI.Fill(new Rect(inRect.x, inRect.y, 3f, inRect.height), MUI.Warn);
            Rect r = inRect.ContractedBy(16f);
            float y = r.y;
            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 28f), "MSM_BisectTitle".Translate(), MUI.Text1, GameFont.Medium, TextAnchor.MiddleLeft, false);
            y += 34f;

            if (Bisect.found == "")
            {
                string tx = "MSM_BisectNotFound".Translate();
                Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, Text.CalcHeight(tx, r.width) + 4f), tx, new Color32(200, 204, 208, 255), GameFont.Small, TextAnchor.UpperLeft);
                if (Dialog_ModSortManager.Button(new Rect(r.x, r.yMax - 34f, r.width, 32f), "MSM_BisectRestore".Translate(), true, !Bisect.restarting)) Bisect.Finish(false);
                return;
            }
            if (Bisect.found != null)
            {
                ModMetaData m = MSM.FindMod(Bisect.found);
                string name = m != null ? m.Name : Bisect.found;
                Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 26f), "MSM_BisectFound".Translate(name), MUI.Warn, GameFont.Small, TextAnchor.MiddleLeft, false);
                y += 26f;
                if (m != null && !m.AuthorsString.NullOrEmpty())
                {
                    Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 20f), m.AuthorsString + " · " + m.PackageIdPlayerFacing, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                    y += 22f;
                }
                string t = "MSM_BisectFoundText".Translate();
                float th = Text.CalcHeight(t, r.width);
                Dialog_ModSortManager.Label(new Rect(r.x, y + 4f, r.width, th + 4f), t, new Color32(200, 204, 208, 255), GameFont.Small, TextAnchor.UpperLeft);
                float by = r.yMax - 34f;
                float bw = (r.width - 8f) / 2f;
                if (Dialog_ModSortManager.Button(new Rect(r.x, by, bw, 32f), "MSM_BisectDisable".Translate(), true, !Bisect.restarting)) Bisect.Finish(true);
                if (Dialog_ModSortManager.Button(new Rect(r.x + bw + 8f, by, bw, 32f), "MSM_BisectRestore".Translate(), false, !Bisect.restarting)) Bisect.Finish(false);
                return;
            }

            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, 22f), "MSM_BisectStep".Translate(Bisect.step, Bisect.suspects.Count, Bisect.RestartsLeft), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            y += 24f;
            if (loadedOk == null) loadedOk = Bisect.LoadedAsExpected();
            if (loadedOk == false)
            {
                string mm = "MSM_BisectMismatch".Translate();
                Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, Text.CalcHeight(mm, r.width) + 4f), mm, MUI.Warn, GameFont.Small, TextAnchor.UpperLeft);
                float yb = r.yMax - 72f;
                if (Dialog_ModSortManager.Button(new Rect(r.x, yb, r.width, 32f), "MSM_BisectReapply".Translate(), true, !Bisect.restarting)) Bisect.Reapply();
                if (Dialog_ModSortManager.Button(new Rect(r.x, yb + 40f, r.width, 30f), "MSM_BisectCancel".Translate(), false, !Bisect.restarting)) Bisect.Finish(false);
                return;
            }
            string q = "MSM_BisectEnabled".Translate(Bisect.enabled.Count, Bisect.suspects.Count);
            float qh = Text.CalcHeight(q, r.width);
            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, qh + 2f), q, MUI.Text1, GameFont.Small, TextAnchor.UpperLeft);
            y += qh + 6f;
            string ns = "MSM_BisectNoSave".Translate();
            float nh = Text.CalcHeight(ns, r.width);
            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width, nh + 2f), ns, MUI.Warn, GameFont.Small, TextAnchor.UpperLeft);
            y += nh + 8f;
            Dialog_ModSortManager.Label(new Rect(r.x, y, r.width - 130f, 28f), "MSM_BisectErrors".Translate(Patch_ErrorCount.Errors), Patch_ErrorCount.Errors > 0 ? new Color32(224, 87, 74, 255) : MUI.Accent, GameFont.Small, TextAnchor.MiddleLeft, false);
            if (Dialog_ModSortManager.Button(new Rect(r.xMax - 124f, y, 124f, 28f), "MSM_BisectLog".Translate())) Log.TryOpenLogWindow();

            float y2 = r.yMax - 72f;
            float w2 = (r.width - 8f) / 2f;
            bool can = !Bisect.restarting;
            if (Dialog_ModSortManager.Button(new Rect(r.x, y2, w2, 32f), "MSM_BisectStill".Translate(), false, can)) Bisect.Answer(true);
            if (Dialog_ModSortManager.Button(new Rect(r.x + w2 + 8f, y2, w2, 32f), "MSM_BisectGone".Translate(), true, can)) Bisect.Answer(false);
            if (Dialog_ModSortManager.Button(new Rect(r.x, y2 + 40f, r.width, 30f), "MSM_BisectCancel".Translate(), false, can)) Bisect.Finish(false);
        }
    }
}

namespace ModSortManager
{
    // 0.1.3: mods subscribed / downloaded / unsubscribed while the game runs show up without a restart
    public static class ModWatch
    {
        public static bool Changed;
        public static float LastEvent;

        public static void Mark()
        {
            Changed = true;
            LastEvent = Time.realtimeSinceStartup;
        }
    }

    [HarmonyPatch]
    public static class Patch_WorkshopNotify
    {
        public static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            Type t = AccessTools.TypeByName("Verse.Steam.WorkshopItems");
            if (t == null) yield break;
            foreach (string n in new[] { "Notify_Installed", "Notify_Subscribed", "Notify_Unsubscribed" })
            {
                System.Reflection.MethodInfo m = AccessTools.Method(t, n);
                if (m != null) yield return m;
            }
        }

        public static void Postfix() { ModWatch.Mark(); }
    }
}
