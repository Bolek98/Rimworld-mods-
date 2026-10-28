using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Xml;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Steam;

namespace ModSortManager
{
    public class MSMSettings : ModSettings
    {
        public bool recommended = true;
        // the game's "Mods" button opens Mod Sort Manager
        public bool replaceModsWindow = true;
        // entry in the Esc menu during a game (off by default)
        public bool escMenuEntry = false;
        // main menu: after the restart load the newest save
        public bool loadLastSave = false;
        // in a game: after the restart return to the game
        public bool returnToGame = true;
        // 0.1.2: after a start, compare the mod list with the last save
        public bool orderGuard = true;
        public bool preSaveCheck = true;
        public int settingsVersion = 1;
        // 0.2.1: "works for me" – pairs in Possible conflicts the player hid ("idA|idB", lower case, sorted)
        public List<string> hiddenSame = new List<string>();
        // 0.3.0: the player's load order rules ("mod|top|", "mod|after|other" …, lower case)
        public List<string> userRules = new List<string>();
        // 0.3.0: mods the player marked as working in this game version (no old-version warning)
        public List<string> noWarn = new List<string>();
        // 0.3.0: smart sort also follows Data/rules.xml and RimSort's community rules
        public bool sharedRules = true;
        // 0.3.1: replacements the player hid ("oldWorkshopId>newWorkshopId")
        public List<string> replaceHidden = new List<string>();

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "recommended", true);
            Scribe_Values.Look(ref replaceModsWindow, "replaceModsWindow", true);
            Scribe_Values.Look(ref escMenuEntry, "escMenuEntry", false);
            Scribe_Values.Look(ref loadLastSave, "loadLastSave", false);
            Scribe_Values.Look(ref returnToGame, "returnToGame", true);
            Scribe_Values.Look(ref orderGuard, "orderGuard", true);
            Scribe_Values.Look(ref preSaveCheck, "preSaveCheck", true);
            Scribe_Values.Look(ref settingsVersion, "settingsVersion", 1);
            Scribe_Collections.Look(ref hiddenSame, "hiddenSame", LookMode.Value);
            if (hiddenSame == null) hiddenSame = new List<string>();
            Scribe_Collections.Look(ref userRules, "userRules", LookMode.Value);
            if (userRules == null) userRules = new List<string>();
            Scribe_Collections.Look(ref noWarn, "noWarn", LookMode.Value);
            if (noWarn == null) noWarn = new List<string>();
            Scribe_Values.Look(ref sharedRules, "sharedRules", true);
            Scribe_Collections.Look(ref replaceHidden, "replaceHidden", LookMode.Value);
            if (replaceHidden == null) replaceHidden = new List<string>();
        }
    }

    public class MSMMod : Mod
    {
        public const string Version = "0.3.1";
        public static MSMSettings Settings;

        public MSMMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<MSMSettings>();
            Rules.Root = content.RootDir;   // 0.3.0
            CrashGuard.SessionStart();      // 0.2.1: notice when the last session did not end properly
            Removal.ProcessPending();       // 0.2.1: mods that could not be removed while they were running
            MNews.Register(content, Version);
            MUI.Init(new MSMSettings(), null, "recommended", "settingsVersion", "loadLastSave", "returnToGame");
            if (Settings.recommended)
                MUI.ApplyAll(Settings);
            var h = new Harmony("misak.modsortmanager");
            // 0.1.2: one failing patch must not stop the others
            foreach (Type t in AccessTools.GetTypesFromAssembly(Assembly.GetExecutingAssembly()))
            {
                if (t.GetCustomAttributes(typeof(HarmonyPatch), false).Length == 0) continue;
                try { h.CreateClassProcessor(t).Patch(); }
                catch (Exception e) { Log.Error("[Mod Sort Manager] patch " + t.Name + " failed: " + e.Message); }
            }
            MUI.PatchWindow(h, typeof(MSMMod));
            Log.Message("[Mod Sort Manager] v" + Version + " loaded.");
        }

        public override string SettingsCategory() => "Mod Sort Manager";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            MUI.Version(inRect, "v" + Version + " · Bolek98");
            var bullets = new List<string> { "MSM_BulletButton".Translate(), "MSM_BulletBackup".Translate(), "MSM_BulletAside".Translate(), "MSM_BulletEscOff".Translate() };
            float y = inRect.y;
            y += MUI.RecommendedCard(new Rect(inRect.x, y, inRect.width, 0f), Settings, Settings.recommended,
                on => { Settings.recommended = on; if (on) MUI.ApplyAll(Settings); }, bullets) + 10f;
            if (Settings.recommended)
                y += MUI.LockNote(new Rect(inRect.x, y, inRect.width, 0f)) + 10f;
            Rect body = new Rect(inRect.x, y, inRect.width, inRect.yMax - y - 50f);
            MUI.BeginLock(Settings.recommended);
            var l = new Listing_Standard();
            l.Begin(body);
            MUI.Section(l, "MSM_SecWhere".Translate());
            MUI.Check(l, "MSM_SetReplace".Translate(), "MSM_SetReplaceTip".Translate(), ref Settings.replaceModsWindow, Settings, "replaceModsWindow");
            MUI.Check(l, "MSM_SetEsc".Translate(), "MSM_SetEscTip".Translate(), ref Settings.escMenuEntry, Settings, "escMenuEntry");
            MUI.Check(l, "MSM_SetGuard".Translate(), "MSM_SetGuardTip".Translate(), ref Settings.orderGuard, Settings, "orderGuard");
            MUI.Check(l, "MSM_SetPreSave".Translate(), "MSM_SetPreSaveTip".Translate(), ref Settings.preSaveCheck, Settings, "preSaveCheck");
            MUI.Check(l, "MSM_SetRules".Translate(), "MSM_SetRulesTip".Translate(), ref Settings.sharedRules, Settings, "sharedRules");
            l.End();
            MUI.EndLock(body);
            if (Widgets.ButtonText(new Rect(inRect.x, inRect.yMax - 40f, 280f, 36f), "MSM_OpenNow".Translate()))
                Find.WindowStack.Add(new Dialog_ModSortManager());
        }
    }

    public static class MSM
    {
        public static bool openingVanilla;

        // another manager replaces the game's mod window – we do not fight it
        public static string OtherManager()
        {
            string[] ids = { "fluffy.modmanager", "fluffy.modmanager.continued" };
            foreach (string id in ids)
            {
                ModMetaData m = ModLister.GetActiveModWithIdentifier(id, true);
                if (m != null)
                    return m.Name;
            }
            return null;
        }

        public static bool ShouldReplace()
        {
            return MSMMod.Settings != null && MSMMod.Settings.replaceModsWindow && !openingVanilla && OtherManager() == null;
        }

        // the original window was opened from Mod Sort Manager – closing it goes back
        public static bool vanillaFromUs;

        public static void OpenVanilla()
        {
            vanillaFromUs = true;
            openingVanilla = true;
            try { Find.WindowStack.Add(new Page_ModsConfig()); }
            finally { openingVanilla = false; }
        }

        public static string DataDir
        {
            get
            {
                string d = Path.Combine(GenFilePaths.SaveDataFolderPath, "ModSortManager");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static string BackupDir
        {
            get { string d = Path.Combine(DataDir, "Backups"); Directory.CreateDirectory(d); return d; }
        }

        public static string ParkDir
        {
            // next to the Mods folder (same drive, so a folder move works), not inside it
            get { string d = Path.Combine(Directory.GetParent(Path.GetFullPath(GenFilePaths.ModsFolderPath).TrimEnd('\\', '/')).FullName, "Mods_MSM_odlozene"); Directory.CreateDirectory(d); return d; }
        }

        // copy of the current ModsConfig.xml, the last 20 are kept
        public static void BackupModsConfig()
        {
            try
            {
                string src = GenFilePaths.ModsConfigFilePath;
                if (!File.Exists(src))
                    return;
                FileInfo newest = new DirectoryInfo(BackupDir).GetFiles("ModsConfig_*.xml").OrderByDescending(f => f.Name).FirstOrDefault();
                if (newest != null && File.ReadAllText(newest.FullName) == File.ReadAllText(src))
                    return;   // same as the last backup
                string dst = Path.Combine(BackupDir, "ModsConfig_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss") + ".xml");
                File.Copy(src, dst, true);
                var old = new DirectoryInfo(BackupDir).GetFiles("ModsConfig_*.xml").OrderByDescending(f => f.Name).Skip(20).ToList();
                foreach (FileInfo f in old)
                    f.Delete();
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] backup failed: " + e.Message);
            }
        }

        public static List<string> ReadModsConfigIds(string path)
        {
            var list = new List<string>();
            try
            {
                var doc = new XmlDocument();
                doc.Load(path);
                XmlNode n = doc.SelectSingleNode("//activeMods");
                if (n != null)
                    foreach (XmlNode li in n.ChildNodes)
                        if (li.Name == "li" && !li.InnerText.NullOrEmpty())
                            list.Add(li.InnerText.Trim());
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] could not read " + path + ": " + e.Message);
            }
            return list;
        }

        public static ModMetaData FindMod(string id)
        {
            if (id.NullOrEmpty()) return null;
            ModMetaData m = ModLister.GetModWithIdentifier(id) ?? ModLister.GetModWithIdentifier(id, true);
            if (m == null && id.EndsWith(ModMetaData.SteamModPostfix, StringComparison.OrdinalIgnoreCase))
            {
                string bare = id.Substring(0, id.Length - ModMetaData.SteamModPostfix.Length);
                m = ModLister.GetModWithIdentifier(bare) ?? ModLister.GetModWithIdentifier(bare, true);
            }
            return m;
        }

        public static string SourceLabel(ModMetaData m)
        {
            if (m.Official) return "MSM_SrcOfficial".Translate();
            if (m.OnSteamWorkshop) return "MSM_SrcSteam".Translate();
            return "MSM_SrcLocal".Translate();
        }

        public static string Versions(ModMetaData m)
        {
            try
            {
                var v = m.SupportedVersionsReadOnly;
                if (v == null || v.Count == 0) return "?";
                return string.Join(", ", v.Select(x => x.Major + "." + x.Minor).ToArray());
            }
            catch { return "?"; }
        }

        public static void OpenFolder(string path)
        {
            try { Application.OpenURL("file:///" + path.Replace('\\', '/')); }
            catch (Exception e) { Messages.Message(e.Message, MessageTypeDefOf.RejectInput, false); }
        }

        public static void OpenPage(ModMetaData m)
        {
            if (m == null) return;
            if (m.OnSteamWorkshop)
                SteamUtility.OpenUrl("https://steamcommunity.com/sharedfiles/filedetails/?id=" + m.RootDir.Name);   // 0.1.9: in the Steam overlay
            else if (!m.Url.NullOrEmpty())
                SteamUtility.OpenUrl(m.Url);
        }

        public static bool CanOpenPage(ModMetaData m)
        {
            return m != null && (m.OnSteamWorkshop || !m.Url.NullOrEmpty());
        }

        public static Mod SettingsHandle(ModMetaData m)
        {
            try
            {
                foreach (Mod h in LoadedModManager.ModHandles)
                    if (h.Content != null && m.SamePackageId(h.Content.PackageId, true) && !h.SettingsCategory().NullOrEmpty())
                        return h;
            }
            catch { }
            return null;
        }

        public static bool CanUpload(ModMetaData m)
        {
            try { return m != null && !m.Official && !m.OnSteamWorkshop && SteamManager.Initialized && m.CanToUploadToWorkshop(); }
            catch { return false; }
        }

        // the same steps as the game's own "Upload to Steam Workshop"
        public static void Upload(ModMetaData mod)
        {
            try
            {
                List<string> issues = mod.loadFolders?.GetIssueList(mod);
                if (mod.HadIncorrectlyFormattedVersionInMetadata)
                {
                    Messages.Message("MessageModNeedsWellFormattedTargetVersion".Translate(VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor), MessageTypeDefOf.RejectInput, false);
                    return;
                }
                if (mod.HadIncorrectlyFormattedPackageId)
                {
                    Verse.Find.WindowStack.Add(new Dialog_MessageBox("MessageModNeedsWellFormattedPackageId".Translate()));
                    return;
                }
                if (issues != null && issues.Count > 0)
                {
                    Verse.Find.WindowStack.Add(new Dialog_MessageBox("ModHadLoadFolderIssues".Translate() + "\n" + issues.ToLineList("  - ")));
                    return;
                }
                Action author = delegate
                {
                    Dialog_MessageBox d = Dialog_MessageBox.CreateConfirmation("ConfirmContentAuthor".Translate(), () => DoUpload(mod), true);
                    d.buttonAText = "Yes".Translate();
                    d.buttonBText = "No".Translate();
                    d.interactionDelay = 6f;
                    Verse.Find.WindowStack.Add(d);
                };
                Type dt = AccessTools.TypeByName("RimWorld.Dialog_ConfirmModUpload");
                Window w = null;
                try { w = dt == null ? null : Activator.CreateInstance(dt, mod, author) as Window; } catch { }
                if (w != null) Verse.Find.WindowStack.Add(w); else author();
            }
            catch (Exception e)
            {
                Log.Error("[Mod Sort Manager] upload: " + e);
            }
        }

        // 0.1.1: the game sends the description only when the item is created – an update through
        // Mod Sort Manager also sends the current description from About.xml
        public static float uploadWithDescriptionUntil = -1f;

        private static void DoUpload(ModMetaData mod)
        {
            uploadWithDescriptionUntil = Time.realtimeSinceStartup + 120f;
            Type w = AccessTools.TypeByName("Verse.Steam.Workshop");
            MethodInfo m = w == null ? null : AccessTools.Method(w, "BakeAndUpload", new[] { typeof(ModMetaData) });   // 1.7
            if (m != null) { m.Invoke(null, new object[] { mod }); return; }
            m = w == null ? null : w.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                .FirstOrDefault(x => x.Name == "Upload" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(ModMetaData)));   // 1.6
            if (m != null) m.Invoke(null, new object[] { mod });
            else Messages.Message("Workshop upload not available.", MessageTypeDefOf.RejectInput, false);
        }

        private static MethodInfo unsubscribe;

        public static bool Unsubscribe(ModMetaData m)
        {
            try
            {
                if (unsubscribe == null)
                {
                    Type t = AccessTools.TypeByName("Verse.Steam.Workshop");
                    unsubscribe = t?.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
                        .FirstOrDefault(x => x.Name == "Unsubscribe" && x.GetParameters().Length == 1 && x.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(ModMetaData)));
                }
                if (unsubscribe == null) return false;
                unsubscribe.Invoke(null, new object[] { m });
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] unsubscribe failed: " + e.Message);
                return false;
            }
        }
    }

    // ---------------- 0.2.1: removing a mod for good ----------------
    public static class Removal
    {
        public static readonly HashSet<string> Gone = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static string PendingPath => Path.Combine(MSM.DataDir, "remove_pending.txt");

        public static bool IsGone(ModMetaData m) => m != null && Gone.Contains(m.RootDir.FullName);

        // true = done now; false = done at the next start (the mod's files are in use)
        public static bool RemoveFolder(ModMetaData m, bool permanent)
        {
            string dir = m.RootDir.FullName;
            if (TryRemove(dir, permanent)) { Gone.Add(dir); return true; }
            try { File.AppendAllText(PendingPath, (permanent ? "delete|" : "park|") + dir + "\n"); } catch { }
            Gone.Add(dir);
            return false;
        }

        private static bool TryRemove(string dir, bool permanent)
        {
            try
            {
                if (!Directory.Exists(dir)) return true;
                if (permanent) Directory.Delete(dir, true);
                else Directory.Move(dir, Path.Combine(MSM.ParkDir, Path.GetFileName(dir) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss")));
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] could not remove " + dir + " now (" + e.Message + ") – it is removed at the next start.");
                return false;
            }
        }

        public static void ProcessPending()
        {
            try
            {
                if (!File.Exists(PendingPath)) return;
                var left = new List<string>();
                foreach (string line in File.ReadAllLines(PendingPath))
                {
                    string[] p = line.Split(new[] { '|' }, 2);
                    if (p.Length < 2 || p[1].Length == 0) continue;
                    if (TryRemove(p[1], p[0] == "delete")) Log.Message("[Mod Sort Manager] removed " + p[1]);
                    else left.Add(line);
                }
                if (left.Count == 0) File.Delete(PendingPath);
                else File.WriteAllLines(PendingPath, left.ToArray());
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] pending removals: " + e.Message); }
        }
    }

    // ---------------- return to the game after a restart ----------------
    public static class ReturnToSave
    {
        private static string FlagPath => Path.Combine(MSM.DataDir, "return.txt");
        private static bool checkedOnce;

        public static bool Pending { get { try { return File.Exists(FlagPath); } catch { return false; } } }

        // check = show the game's own version / mod mismatch warning before loading
        public static void Write(string saveName, bool check = false)
        {
            try { File.WriteAllText(FlagPath, (check ? "check:" : "") + saveName); }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] " + e.Message); }
        }

        public static string NewestSave()
        {
            try
            {
                FileInfo f = GenFilePaths.AllSavedGameFiles.OrderByDescending(x => x.LastWriteTime).FirstOrDefault();
                return f == null ? null : Path.GetFileNameWithoutExtension(f.Name);
            }
            catch { return null; }
        }

        // called from the main menu: once per start
        public static void Check()
        {
            if (checkedOnce) return;
            checkedOnce = true;
            try
            {
                if (!File.Exists(FlagPath)) return;
                string name = File.ReadAllText(FlagPath).Trim();
                File.Delete(FlagPath);   // never loop
                bool check = name.StartsWith("check:");
                if (check) name = name.Substring(6);
                if (name.NullOrEmpty() || !File.Exists(GenFilePaths.FilePathForSavedGame(name))) return;
                Log.Message("[Mod Sort Manager] returning to save " + name);
                if (check) GameDataSaveLoader.CheckVersionAndLoadGame(name);
                else GameDataSaveLoader.LoadGame(name);
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] return to save failed: " + e.Message);
            }
        }
    }

    // ---------------- patches ----------------
    [HarmonyPatch(typeof(WindowStack), nameof(WindowStack.Add))]
    public static class Patch_WindowStackAdd
    {
        public static bool Prefix(Window window)
        {
            try
            {
                if (window is Page_ModsConfig && MSM.ShouldReplace())
                {
                    Find.WindowStack.Add(new Dialog_ModSortManager());
                    return false;
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Mod Sort Manager] " + e.Message, 0x35a1001);
            }
            return true;
        }
    }

    // a small button in the game's own mod window (shown when it opens: setting off or "original window")
    [HarmonyPatch(typeof(Page_ModsConfig), nameof(Page_ModsConfig.DoWindowContents))]
    public static class Patch_VanillaButton
    {
        // top right corner – free in the game's window
        private static Rect Btn(Rect rect) => new Rect(rect.xMax - 200f, rect.y, 200f, 30f);

        public static void Prefix(Page_ModsConfig __instance, Rect rect)
        {
            try
            {
                Event ev = Event.current;
                if (ev.type == EventType.MouseDown && ev.button == 0 && Mouse.IsOver(Btn(rect)))
                {
                    ev.Use();
                    MSM.vanillaFromUs = false;
                    Traverse.Create(__instance).Field("discardChanges").SetValue(true);
                    __instance.Close();
                    Find.WindowStack.Add(new Dialog_ModSortManager());
                }
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Mod Sort Manager] vanilla button: " + e.Message, 0x35a1003);
            }
        }

        public static void Postfix(Rect rect)
        {
            try
            {
                Widgets.ButtonText(Btn(rect), "Mod Sort Manager");
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Mod Sort Manager] vanilla button: " + e.Message, 0x35a1002);
            }
        }
    }

    [HarmonyPatch(typeof(Page_ModsConfig), nameof(Page_ModsConfig.PostClose))]
    public static class Patch_VanillaClosed
    {
        public static void Postfix(Page_ModsConfig __instance)
        {
            try
            {
                if (!MSM.vanillaFromUs) return;
                MSM.vanillaFromUs = false;
                // saved with changed mods → the game asks to restart; otherwise back to Mod Sort Manager
                var t = Traverse.Create(__instance);
                bool saved = t.Field("saveChanges").GetValue<bool>();
                int hash = t.Field("activeModsWhenOpenedHash").GetValue<int>();
                if (saved && hash != ModLister.InstalledModsListHash(true)) return;
                Find.WindowStack.Add(new Dialog_ModSortManager());
            }
            catch (Exception e)
            {
                Log.WarningOnce("[Mod Sort Manager] back from the original window: " + e.Message, 0x35a1004);
            }
        }
    }

    [HarmonyPatch(typeof(OptionListingUtility), nameof(OptionListingUtility.DrawOptionListing))]
    public static class Patch_EscMenu
    {
        private static string ourLabel;

        public static void Prefix(List<ListableOption> optList)
        {
            try
            {
                if (MSMMod.Settings == null || !MSMMod.Settings.escMenuEntry || Current.ProgramState != ProgramState.Playing || optList == null)
                    return;
                string options = "Options".Translate();
                int idx = optList.FindIndex(o => o != null && o.label == options);
                if (idx < 0) return;
                if (ourLabel == null) ourLabel = "Mod Sort Manager";
                if (optList.Any(o => o != null && o.label == ourLabel)) return;
                optList.Insert(idx + 1, new ListableOption(ourLabel, () =>
                {
                    try { Find.MainTabsRoot.EscapeCurrentTab(false); } catch { }
                    Find.WindowStack.Add(new Dialog_ModSortManager());
                }) { minHeight = 34f });
            }
            catch
            {
            }
        }
    }

    [HarmonyPatch(typeof(MainMenuDrawer), nameof(MainMenuDrawer.MainMenuOnGUI))]
    public static class Patch_MainMenu
    {
        public static void Postfix()
        {
            if (Current.ProgramState != ProgramState.Entry) return;
            try { Startup.OnMainMenu(); } catch (Exception e) { Log.WarningOnce("[Mod Sort Manager] " + e.Message, 0x35a1005); }
            if (!Bisect.Active) ReturnToSave.Check();
        }
    }

    // ---------------- duplicates on disk ----------------
    public class DupCopy
    {
        public string id, name, version, path;
        public bool steam;
        public ModMetaData meta;   // as the game knows it (null = ignored by the game)
        public DateTime time;
    }

    public static class Duplicates
    {
        // copies moved away / unsubscribed this session (Steam unsubscribing finishes later)
        public static readonly HashSet<string> Handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static List<List<DupCopy>> Scan()
        {
            var all = new List<DupCopy>();
            try
            {
                foreach (DirectoryInfo d in new DirectoryInfo(GenFilePaths.ModsFolderPath).GetDirectories())
                    Read(d, false, all);
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] mods folder scan: " + e.Message); }
            var steamDirs = new HashSet<string>();
            try
            {
                Type t = AccessTools.TypeByName("Verse.Steam.WorkshopItems");
                var items = t?.GetProperty("AllSubscribedItems", BindingFlags.Static | BindingFlags.Public)?.GetValue(null, null) as System.Collections.IEnumerable;
                if (items != null)
                    foreach (object it in items)
                    {
                        var dir = it.GetType().GetProperty("Directory")?.GetValue(it, null) as DirectoryInfo;
                        if (dir != null) steamDirs.Add(dir.FullName);
                    }
            }
            catch { }
            foreach (ModMetaData m in ModLister.AllInstalledMods)
                if (m.OnSteamWorkshop) steamDirs.Add(m.RootDir.FullName);
            foreach (string s in steamDirs)
                try { Read(new DirectoryInfo(s), true, all); } catch { }

            var byPath = new Dictionary<string, ModMetaData>(StringComparer.OrdinalIgnoreCase);
            foreach (ModMetaData m in ModLister.AllInstalledMods)
                byPath[m.RootDir.FullName] = m;
            foreach (DupCopy c in all)
            {
                ModMetaData m;
                if (byPath.TryGetValue(c.path, out m)) c.meta = m;
            }
            return all.Where(c => !c.id.StartsWith("ludeon.") && !Handled.Contains(c.path)).GroupBy(c => c.id).Where(g => g.Count() > 1).Select(g => g.ToList()).ToList();
        }

        private static void Read(DirectoryInfo d, bool steam, List<DupCopy> all)
        {
            if (!d.Exists) return;
            string about = GenFile.ResolveCaseInsensitiveFilePath(Path.Combine(d.FullName, "About"), "About.xml");
            if (about == null || !File.Exists(about)) return;
            try
            {
                var doc = new XmlDocument();
                doc.Load(about);
                string id = doc.SelectSingleNode("/ModMetaData/packageId")?.InnerText?.Trim();
                if (id.NullOrEmpty()) return;
                all.Add(new DupCopy
                {
                    id = id.ToLowerInvariant(),
                    name = doc.SelectSingleNode("/ModMetaData/name")?.InnerText?.Trim() ?? d.Name,
                    version = doc.SelectSingleNode("/ModMetaData/modVersion")?.InnerText?.Trim() ?? "",
                    path = d.FullName,
                    steam = steam,
                    time = File.GetLastWriteTime(about)
                });
            }
            catch { }
        }

        // the newer copy: by modVersion, otherwise by date
        public static DupCopy Newest(List<DupCopy> g)
        {
            DupCopy best = null;
            foreach (DupCopy c in g)
            {
                if (best == null) { best = c; continue; }
                Version a, b;
                bool pa = Version.TryParse(c.version, out a), pb = Version.TryParse(best.version, out b);
                if (pa && pb && a != b) { if (a > b) best = c; continue; }
                if (c.time > best.time) best = c;
            }
            return best;
        }
    }

    // ---------------- "do the same thing": Harmony patches that can replace the same code ----------------
    public class SamePair
    {
        public ModMetaData a, b;
        public List<string> methods = new List<string>();
        public bool hot;
        // 0.1.7: how each of the two changes every shared method ("prefix (can skip)", "transpiler" …)
        public List<SameMethod> detail = new List<SameMethod>();
        public bool blocking;       // at least one of them can skip the original (and the other's prefix)
    }

    public class SameMethod
    {
        public string method;
        public string kindsA, kindsB;
        public string runsFirst;    // the mod whose prefix runs first (Harmony priority)
    }

    public static class SameThing
    {
        private static readonly HashSet<string> Hot = new HashSet<string>
        {
            "TickManager.TickManagerUpdate", "TickManager.DoSingleTick", "JobGiver_OptimizeApparel.TryGiveJob",
            "JobGiver_OptimizeApparel.ApparelScoreGain", "JobGiver_OptimizeApparel.ApparelScoreRaw", "HaulAIUtility.HaulToStorageJob",
            "StoreUtility.TryFindBestBetterStoreCellFor", "MapDrawer.MapMeshDrawerUpdate_First", "Pawn_ApparelTracker.Wear",
            "ResearchManager.ResearchPerformed", "MainTabWindow_Research.DoWindowContents"
        };

        public static List<SamePair> Analyze()
        {
            var result = new List<SamePair>();
            try
            {
                var asm = new Dictionary<Assembly, ModContentPack>();
                foreach (ModContentPack p in LoadedModManager.RunningModsListForReading)
                {
                    if (p == null || p.IsOfficialMod || p.assemblies == null) continue;
                    foreach (Assembly a in p.assemblies.loadedAssemblies)
                        if (a != null) asm[a] = p;
                }
                var pairs = new Dictionary<string, SamePair>();
                foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
                {
                    Patches info = Harmony.GetPatchInfo(m);
                    if (info == null) continue;
                    // every kind of patch each mod has on this method
                    var kinds = new Dictionary<ModContentPack, List<string>>();
                    ModContentPack firstPrefix = null;
                    int bestPri = int.MinValue, bestIdx = int.MaxValue;
                    Action<IEnumerable<Patch>, string> collect = (ps, kind) =>
                    {
                        foreach (Patch p in ps)
                        {
                            ModContentPack o;
                            if (p.PatchMethod?.DeclaringType == null || !asm.TryGetValue(p.PatchMethod.DeclaringType.Assembly, out o)) continue;
                            string k = kind;
                            if (kind == "prefix" && (p.PatchMethod as MethodInfo)?.ReturnType == typeof(bool))
                            {
                                k = "prefix!";
                                if (p.priority > bestPri || (p.priority == bestPri && p.index < bestIdx)) { bestPri = p.priority; bestIdx = p.index; firstPrefix = o; }
                            }
                            List<string> l;
                            if (!kinds.TryGetValue(o, out l)) kinds[o] = l = new List<string>();
                            if (!l.Contains(k)) l.Add(k);
                        }
                    };
                    collect(info.Prefixes, "prefix");
                    collect(info.Postfixes, "postfix");
                    collect(info.Transpilers, "transpiler");
                    collect(info.Finalizers, "finalizer");
                    var mods = new HashSet<ModContentPack>();
                    foreach (Patch p in info.Prefixes)
                    {
                        var mi = p.PatchMethod as MethodInfo;
                        ModContentPack o;
                        if (mi != null && mi.ReturnType == typeof(bool) && mi.DeclaringType != null && asm.TryGetValue(mi.DeclaringType.Assembly, out o)) mods.Add(o);
                    }
                    foreach (Patch p in info.Transpilers)
                    {
                        ModContentPack o;
                        if (p.PatchMethod?.DeclaringType != null && asm.TryGetValue(p.PatchMethod.DeclaringType.Assembly, out o)) mods.Add(o);
                    }
                    if (mods.Count < 2) continue;
                    string label = (m.DeclaringType != null ? m.DeclaringType.Name + "." : "") + m.Name;
                    var list = mods.OrderBy(x => x.PackageId).ToList();
                    for (int i = 0; i < list.Count; i++)
                        for (int j = i + 1; j < list.Count; j++)
                        {
                            string key = list[i].PackageId + "|" + list[j].PackageId;
                            SamePair sp;
                            if (!pairs.TryGetValue(key, out sp))
                            {
                                ModMetaData ma = MSM.FindMod(list[i].PackageId), mb = MSM.FindMod(list[j].PackageId);
                                if (ma == null || mb == null || Related(ma, mb)) { pairs[key] = null; continue; }
                                sp = pairs[key] = new SamePair { a = ma, b = mb };
                            }
                            if (sp == null) continue;
                            if (!sp.methods.Contains(label))
                            {
                                sp.methods.Add(label);
                                List<string> ka, kb;
                                kinds.TryGetValue(list[i], out ka);
                                kinds.TryGetValue(list[j], out kb);
                                sp.detail.Add(new SameMethod
                                {
                                    method = label,
                                    kindsA = Kinds(ka), kindsB = Kinds(kb),
                                    runsFirst = firstPrefix == list[i] ? sp.a.Name : firstPrefix == list[j] ? sp.b.Name : null
                                });
                                if ((ka != null && ka.Contains("prefix!")) || (kb != null && kb.Contains("prefix!"))) sp.blocking = true;
                            }
                            if (Hot.Contains(label)) sp.hot = true;
                        }
                }
                result = pairs.Values.Where(p => p != null && (p.hot || p.methods.Count >= 3))
                    .OrderByDescending(p => p.hot).ThenByDescending(p => p.methods.Count).ToList();   // 0.2.2: no cap (it showed only the first 15)
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] patch analysis failed: " + e.Message);
            }
            return result;
        }

        // "prefix (can skip), postfix" in the player's language
        private static string Kinds(List<string> l)
        {
            if (l == null || l.Count == 0) return "–";
            return string.Join(", ", l.Select(k => ("MSM_Kind_" + (k == "prefix!" ? "prefixSkip" : k)).Translate().ToString()).ToArray());
        }

        // made to work together: one needs the other, or the same author
        private static bool Related(ModMetaData a, ModMetaData b)
        {
            if (a.Dependencies.Any(d => b.SamePackageId(d.packageId, true)) || b.Dependencies.Any(d => a.SamePackageId(d.packageId, true)))
                return true;
            if (a.LoadAfter.Any(x => b.SamePackageId(x, true)) && b.LoadBefore.Any(x => a.SamePackageId(x, true)))
                return true;
            return !a.AuthorsString.NullOrEmpty() && a.AuthorsString == b.AuthorsString;
        }
    }

    // ---------------- 0.1.8: errors in the log, by the mod that caused them ----------------
    public class LogBlame
    {
        public ModMetaData mod;
        public int count;
        public string sample;
    }

    public static class LogErrors
    {
        private static readonly HashSet<string> NotMods = new HashSet<string>
        { "System", "Verse", "RimWorld", "UnityEngine", "HarmonyLib", "MonoMod", "Mono", "Microsoft", "Unity", "Waylith", "LudeonTK", "ModSortManager", "<>c", "Steamworks", "Ionic", "NAudio" };

        public static List<LogBlame> Analyze()
        {
            var res = new Dictionary<ModContentPack, LogBlame>();
            try
            {
                // the first part of the namespaces of each mod's code, and the mods' folders
                var byNs = new Dictionary<string, ModContentPack>();
                var byName = new Dictionary<string, ModContentPack>(StringComparer.OrdinalIgnoreCase);
                var byDir = new List<KeyValuePair<string, ModContentPack>>();
                foreach (ModContentPack p in LoadedModManager.RunningModsListForReading)
                {
                    if (p == null || p.IsOfficialMod) continue;
                    if (!p.Name.NullOrEmpty()) byName[p.Name] = p;
                    if (!p.RootDir.NullOrEmpty()) byDir.Add(new KeyValuePair<string, ModContentPack>(p.RootDir.Replace('\\', '/'), p));
                    if (p.assemblies == null) continue;
                    foreach (Assembly a in p.assemblies.loadedAssemblies)
                    {
                        Type[] types;
                        try { types = a.GetTypes(); } catch { continue; }
                        foreach (Type ty in types)
                        {
                            string ns = ty.Namespace ?? ty.Name;
                            int dot = ns.IndexOf('.');
                            string first = dot > 0 ? ns.Substring(0, dot) : ns;
                            if (!NotMods.Contains(first) && !byNs.ContainsKey(first)) byNs[first] = p;
                        }
                    }
                }
                foreach (LogMessage m in Log.Messages.ToList())
                {
                    if (m == null || (m.type != LogMessageType.Error && m.type != LogMessageType.Warning)) continue;
                    if (m.type == LogMessageType.Warning) continue;      // errors only
                    string text = m.text ?? "";
                    ModContentPack who = Blame(text, m.StackTrace, byNs, byName, byDir);
                    if (who == null) continue;
                    LogBlame b;
                    if (!res.TryGetValue(who, out b))
                    {
                        ModMetaData meta = MSM.FindMod(who.PackageId);
                        if (meta == null) continue;
                        string first = text.Split('\n')[0];
                        res[who] = b = new LogBlame { mod = meta, sample = first.Length > 220 ? first.Substring(0, 220) + "…" : first };
                    }
                    b.count += Math.Max(1, m.repeats);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] log check failed: " + e.Message);
            }
            return res.Values.OrderByDescending(b => b.count).Take(20).ToList();
        }

        private static ModContentPack Blame(string text, string stack, Dictionary<string, ModContentPack> byNs, Dictionary<string, ModContentPack> byName, List<KeyValuePair<string, ModContentPack>> byDir)
        {
            // "[Mod name] …" – many mods start their messages with their name
            if (text.StartsWith("["))
            {
                int end = text.IndexOf(']');
                ModContentPack p;
                if (end > 1 && byName.TryGetValue(text.Substring(1, end - 1).Trim(), out p)) return p;
            }
            // a file of the mod (def loading, textures, patches)
            string norm = text.Replace('\\', '/');
            foreach (var kv in byDir)
                if (norm.IndexOf(kv.Key, StringComparison.OrdinalIgnoreCase) >= 0) return kv.Value;
            // the mod's code highest in the stack trace (the error message itself may hold the trace too)
            // only the top frames: mods that run the game loop (performance mods, tick loops) are in almost
            // every trace further down and are not the cause
            int frames = 0;
            foreach (string raw in ((text ?? "") + "\n" + (stack ?? "")).Split('\n'))
            {
                string line = raw.Trim();
                if (!line.StartsWith("at ")) continue;
                if (++frames > 8) break;
                string call = line.Substring(3);
                int dot = call.IndexOf('.');
                if (dot <= 0) continue;
                string first = call.Substring(0, dot);
                if (first.StartsWith("(wrapper")) continue;
                ModContentPack p;
                if (byNs.TryGetValue(first, out p)) return p;
            }
            return null;
        }
    }

    // ---------------- checks on the edited list ----------------
    public enum Sev { Info = 0, Warn = 1, Error = 2 }

    public class Issue
    {
        public int group;       // 0 duplicates, 1 may break, 2 same thing
        public Sev sev;
        public string title, text;
        public List<KeyValuePair<string, Action>> actions = new List<KeyValuePair<string, Action>>();
    }

    public static class Sorter
    {
        public static bool Present(List<ModMetaData> list, string id, out int index)
        {
            for (int i = 0; i < list.Count; i++)
                if (list[i].SamePackageId(id, true)) { index = i; return true; }
            index = -1;
            return false;
        }

        // smart sort: the mods' own rules (load before/after, dependencies), Harmony first,
        // Core and DLCs before everything else; otherwise the current order is kept
        public static bool Sort(List<ModMetaData> list, out string error)
        {
            error = null;
            List<ModMetaData> r = TrySort(list, true);
            if (r == null) r = TrySort(list, false);
            if (r == null)
            {
                error = "MSM_SortCycle".Translate();
                return false;
            }
            // 0.3.0: remember which rule moved which mod (shown in the mod's details)
            LastMoved.Clear();
            foreach (var kv in hits)
                if (list.IndexOf(kv.Key) != r.IndexOf(kv.Key)) LastMoved[kv.Key] = kv.Value;
            list.Clear();
            list.AddRange(r);
            return true;
        }

        // 0.3.0: mods moved by a load order rule in the last smart sort
        public static readonly Dictionary<ModMetaData, List<string>> LastMoved = new Dictionary<ModMetaData, List<string>>();
        private static readonly Dictionary<ModMetaData, List<string>> hits = new Dictionary<ModMetaData, List<string>>();

        private static void Hit(ModMetaData m, OrderRule r)
        {
            List<string> l;
            if (!hits.TryGetValue(m, out l)) hits[m] = l = new List<string>();
            string d = r.Describe();
            if (!l.Contains(d)) l.Add(d);
        }

        // would the edge a -> b close a loop (b already reaches a)?
        private static bool Reaches(List<int>[] after, int from, int to)
        {
            var stack = new Stack<int>();
            var seen = new HashSet<int>();
            stack.Push(from);
            while (stack.Count > 0)
            {
                int x = stack.Pop();
                if (x == to) return true;
                if (!seen.Add(x)) continue;
                foreach (int y in after[x]) stack.Push(y);
            }
            return false;
        }

        private static readonly string[] Dlc = { "ludeon.rimworld", "ludeon.rimworld.royalty", "ludeon.rimworld.ideology", "ludeon.rimworld.biotech", "ludeon.rimworld.anomaly", "ludeon.rimworld.odyssey" };

        private static List<ModMetaData> TrySort(List<ModMetaData> list, bool implicitRules)
        {
            int n = list.Count;
            var after = new List<int>[n];   // edges i -> j : i before j
            for (int i = 0; i < n; i++) after[i] = new List<int>();
            var indeg = new int[n];
            var seen = new HashSet<long>();
            Action<int, int> edge = (a, b) =>
            {
                if (a == b || a < 0 || b < 0) return;
                if (!seen.Add(((long)a << 32) | (uint)b)) return;
                after[a].Add(b);
                indeg[b]++;
            };
            int j;
            for (int i = 0; i < n; i++)
            {
                ModMetaData m = list[i];
                foreach (string id in m.LoadBefore.Concat(m.ForceLoadBefore))
                    if (Present(list, id, out j)) edge(i, j);
                foreach (string id in m.LoadAfter.Concat(m.ForceLoadAfter))
                    if (Present(list, id, out j)) edge(j, i);
            }
            if (implicitRules)
            {
                int harmony;
                Present(list, "brrainz.harmony", out harmony);
                var official = new List<int>();
                for (int i = 0; i < n; i++) if (list[i].Official) official.Add(i);
                // core before DLCs, DLCs in release order
                int prev = -1;
                foreach (string id in Dlc)
                    if (Present(list, id, out j)) { if (prev >= 0) edge(prev, j); prev = j; }
                for (int i = 0; i < n; i++)
                {
                    ModMetaData m = list[i];
                    if (harmony >= 0 && i != harmony) edge(harmony, i);
                    if (m.Official || i == harmony) continue;
                    // a mod that asks to load before an official one keeps that
                    bool early = m.LoadBefore.Concat(m.ForceLoadBefore).Any(x => x.StartsWith("ludeon.", StringComparison.OrdinalIgnoreCase));
                    if (!early)
                        foreach (int o in official) edge(o, i);
                    foreach (ModDependency d in m.Dependencies)
                        if (d != null && !d.packageId.NullOrEmpty() && Present(list, d.packageId, out j)) edge(j, i);
                }
            }
            // 0.3.0: load order rules (the player's, shipped and RimSort's); top / bottom only change
            // which ready mod goes first, after / before are added unless they would make a loop
            var bucket = new int[n];
            for (int i = 0; i < n; i++) bucket[i] = 1;
            hits.Clear();
            if (implicitRules)
            {
                try
                {
                    var idx = new Dictionary<string, int>();
                    for (int i = 0; i < n; i++) { string id = Rules.Id(list[i]); if (!idx.ContainsKey(id)) idx[id] = i; }
                    var doneTB = new HashSet<int>();
                    foreach (OrderRule rule in Rules.ForSort())
                    {
                        int a, b;
                        if (!idx.TryGetValue(rule.mod, out a) || list[a].Official) continue;
                        if (rule.kind == "top" || rule.kind == "bottom")
                        {
                            if (!doneTB.Add(a)) continue;   // the first rule wins (the player's come first)
                            bucket[a] = rule.kind == "top" ? 0 : 2;
                            Hit(list[a], rule);
                            continue;
                        }
                        if (!idx.TryGetValue(rule.other, out b) || a == b) continue;
                        int from = rule.kind == "after" ? b : a, to = rule.kind == "after" ? a : b;
                        if (seen.Contains(((long)from << 32) | (uint)to)) continue;
                        if (Reaches(after, to, from)) continue;
                        edge(from, to);
                        Hit(list[a], rule);
                    }
                }
                catch (Exception e) { Log.Warning("[Mod Sort Manager] load order rules: " + e.Message); }
            }
            var ready = new SortedSet<long>();
            for (int i = 0; i < n; i++) if (indeg[i] == 0) ready.Add((long)bucket[i] << 32 | (uint)i);
            var res = new List<ModMetaData>(n);
            while (ready.Count > 0)
            {
                long key = ready.Min;
                ready.Remove(key);
                int i = (int)(key & 0xFFFFFFFF);
                res.Add(list[i]);
                foreach (int k in after[i])
                    if (--indeg[k] == 0) ready.Add((long)bucket[k] << 32 | (uint)k);
            }
            return res.Count == n ? res : null;
        }

        // load order rules broken right now (mod -> text)
        public static List<string> OrderProblems(List<ModMetaData> list, Dictionary<ModMetaData, Sev> marks)
        {
            var res = new List<string>();
            int j;
            for (int i = 0; i < list.Count; i++)
            {
                ModMetaData m = list[i];
                foreach (string id in m.LoadBefore.Concat(m.ForceLoadBefore))
                    if (Present(list, id, out j) && j < i)
                    {
                        res.Add("MSM_OrderBefore".Translate(m.Name, list[j].Name));
                        Mark(marks, m, Sev.Warn);
                    }
                foreach (string id in m.LoadAfter.Concat(m.ForceLoadAfter))
                    if (Present(list, id, out j) && j > i)
                    {
                        res.Add("MSM_OrderAfter".Translate(m.Name, list[j].Name));
                        Mark(marks, m, Sev.Warn);
                    }
            }
            return res;
        }

        public static void Mark(Dictionary<ModMetaData, Sev> marks, ModMetaData m, Sev s)
        {
            if (m == null) return;
            Sev old;
            if (!marks.TryGetValue(m, out old) || old < s) marks[m] = s;
        }
    }
}


namespace ModSortManager
{
    public enum UpdState { Recent, Needs, Busy, Done }

    public class UpdRow
    {
        public ModMetaData mod;
        public ulong id;
        public UpdState state;
        public float progress;
        public DateTime updated;
    }

    // Steam Workshop update state of the subscribed mods. Steamworks is reached by reflection:
    // on 1.6 it lives in Assembly-CSharp-firstpass, on 1.7 inside Assembly-CSharp.
    public static class Updates
    {
        private const uint Installed = 4, NeedsUpdate = 8, Downloading = 16, DownloadPending = 32;
        private static bool init, ok;
        private static Type pfidType;
        private static MethodInfo getState, download, getDownloadInfo, getInstallInfo;
        private static readonly Dictionary<ulong, UpdRow> rows = new Dictionary<ulong, UpdRow>();
        private static readonly HashSet<ulong> started = new HashSet<ulong>();
        public static List<UpdRow> Rows = new List<UpdRow>();
        public static int Pending, Busy, Finished;
        private static float lastFull = -999f;

        public static bool Available { get { Init(); return ok && SteamManager.Initialized; } }

        private static void Init()
        {
            if (init) return;
            init = true;
            try
            {
                Type ugc = AccessTools.TypeByName("Steamworks.SteamUGC");
                pfidType = AccessTools.TypeByName("Steamworks.PublishedFileId_t");
                if (ugc == null || pfidType == null) return;
                getState = ugc.GetMethod("GetItemState", new[] { pfidType });
                download = ugc.GetMethod("DownloadItem", new[] { pfidType, typeof(bool) });
                getDownloadInfo = ugc.GetMethod("GetItemDownloadInfo", new[] { pfidType, typeof(ulong).MakeByRefType(), typeof(ulong).MakeByRefType() });
                getInstallInfo = ugc.GetMethod("GetItemInstallInfo", new[] { pfidType, typeof(ulong).MakeByRefType(), typeof(string).MakeByRefType(), typeof(uint), typeof(uint).MakeByRefType() });
                ok = getState != null && download != null;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] Steam updates not available: " + e.Message);
            }
        }

        // 0.1.9: subscribing to a missing requirement from the game
        private static MethodInfo subscribe;
        public static bool CanSubscribe
        {
            get
            {
                Init();
                if (!ok || !SteamManager.Initialized) return false;
                if (subscribe == null)
                    subscribe = AccessTools.TypeByName("Steamworks.SteamUGC")?.GetMethod("SubscribeItem", new[] { pfidType });
                return subscribe != null;
            }
        }

        // the number in "steam://url/CommunityFilePage/123" or "...filedetails/?id=123"
        public static ulong WorkshopId(string url)
        {
            if (url.NullOrEmpty()) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(url, @"(?:CommunityFilePage/|[?&]id=)(\d{6,})");
            ulong id;
            return m.Success && ulong.TryParse(m.Groups[1].Value, out id) ? id : 0;
        }

        public static bool Subscribe(ulong id)
        {
            try
            {
                if (!CanSubscribe) return false;
                subscribe.Invoke(null, new[] { Pfid(id) });
                download.Invoke(null, new object[] { Pfid(id), true });
                started.Add(id);
                Log.Message("[Mod Sort Manager] subscribed to Workshop item " + id + " – Steam downloads it.");
                return true;
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] subscribe " + id + ": " + e.Message);
                return false;
            }
        }

        private static object Pfid(ulong id)
        {
            object p = Activator.CreateInstance(pfidType);
            FieldInfo f = pfidType.GetField("m_PublishedFileId");
            if (f != null) { f.SetValue(p, id); return p; }
            return Activator.CreateInstance(pfidType, id);
        }

        private static uint State(ulong id)
        {
            try { return Convert.ToUInt32(getState.Invoke(null, new[] { Pfid(id) })); } catch { return 0; }
        }

        private static DateTime InstalledAt(ulong id, ModMetaData m)
        {
            try
            {
                if (getInstallInfo != null)
                {
                    object[] a = { Pfid(id), 0UL, null, 1024u, 0u };
                    if ((bool)getInstallInfo.Invoke(null, a))
                    {
                        uint ts = (uint)a[4];
                        if (ts > 0) return new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(ts).ToLocalTime();
                    }
                }
            }
            catch { }
            try { return Directory.GetLastWriteTime(m.RootDir.FullName); } catch { return DateTime.MinValue; }
        }

        // every second while the tab is open; a full scan of all mods at most every 30 s (or on demand)
        public static void Refresh(bool force = false)
        {
            if (!Available) return;
            bool full = force || Time.realtimeSinceStartup - lastFull > 30f;
            if (full)
            {
                lastFull = Time.realtimeSinceStartup;
                foreach (ModMetaData m in ModLister.AllInstalledMods)
                {
                    ulong id;
                    if (!m.OnSteamWorkshop || !ulong.TryParse(m.RootDir.Name, out id)) continue;
                    UpdRow r;
                    if (!rows.TryGetValue(id, out r)) rows[id] = r = new UpdRow { mod = m, id = id, updated = InstalledAt(id, m) };
                    r.mod = m;
                }
            }
            Pending = Busy = Finished = 0;
            DateTime recent = DateTime.Now.AddDays(-7);
            foreach (UpdRow r in rows.Values)
            {
                if (!full && r.state == UpdState.Recent && !started.Contains(r.id)) { continue; }
                uint s = State(r.id);
                if ((s & (Downloading | DownloadPending)) != 0)
                {
                    r.state = UpdState.Busy;
                    try
                    {
                        object[] a = { Pfid(r.id), 0UL, 0UL };
                        if (getDownloadInfo != null && (bool)getDownloadInfo.Invoke(null, a) && (ulong)a[2] > 0)
                            r.progress = (float)((ulong)a[1] / (double)(ulong)a[2]);
                    }
                    catch { }
                }
                else if ((s & NeedsUpdate) != 0) r.state = UpdState.Needs;
                else if (started.Contains(r.id)) { r.state = UpdState.Done; r.updated = DateTime.Now; }
                else r.state = UpdState.Recent;
            }
            foreach (UpdRow r in rows.Values)
            {
                if (r.state == UpdState.Needs) Pending++;
                else if (r.state == UpdState.Busy) Busy++;
                else if (r.state == UpdState.Done) Finished++;
            }
            Rows = rows.Values.Where(r => r.state != UpdState.Recent || r.updated >= recent)
                .OrderBy(r => r.state == UpdState.Needs ? 0 : r.state == UpdState.Busy ? 1 : r.state == UpdState.Done ? 2 : 3)
                .ThenByDescending(r => r.updated).ToList();
        }

        public static void Download(UpdRow r)
        {
            try
            {
                if ((bool)download.Invoke(null, new[] { Pfid(r.id), (object)true }))
                {
                    started.Add(r.id);
                    r.state = UpdState.Busy;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] update of " + r.mod.Name + " failed: " + e.Message);
            }
        }

        public static void DownloadAll()
        {
            foreach (UpdRow r in rows.Values.Where(x => x.state == UpdState.Needs).ToList())
                Download(r);
            Log.Message("[Mod Sort Manager] updating " + started.Count + " Workshop mods.");
        }
    }
}


namespace ModSortManager
{
    [HarmonyPatch]
    public static class Patch_UploadDescription
    {
        public static bool Prepare() => TargetMethod() != null;

        public static MethodBase TargetMethod()
        {
            Type w = AccessTools.TypeByName("Verse.Steam.Workshop");
            return w == null ? null : AccessTools.Method(w, "SetWorkshopItemDataFrom");
        }

        public static void Postfix(object[] __args)
        {
            try
            {
                if (Time.realtimeSinceStartup > MSM.uploadWithDescriptionUntil || __args == null || __args.Length < 3) return;
                if (__args[2] is bool && (bool)__args[2]) return;   // creating: the game sends it itself
                object hook = __args[1];
                string desc = hook?.GetType().GetProperty("Description")?.GetValue(hook, null) as string;
                if (desc.NullOrEmpty()) return;
                Type ugc = AccessTools.TypeByName("Steamworks.SteamUGC");
                MethodInfo set = ugc?.GetMethod("SetItemDescription");
                if (set == null) return;
                set.Invoke(null, new[] { __args[0], desc });
                Log.Message("[Mod Sort Manager] Workshop description updated from About.xml.");
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] description update: " + e.Message);
            }
        }
    }
}
