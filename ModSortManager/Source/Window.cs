using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ModSortManager
{
    public class Dialog_ModSortManager : Window
    {
        // ---- colors (dark MUI look) ----
        private static readonly Color Bg = new Color32(24, 27, 31, 255);
        private static readonly Color Line = new Color32(44, 50, 56, 255);
        private static readonly Color Row1 = new Color32(28, 32, 36, 255);
        private static readonly Color Hover = new Color32(36, 42, 48, 255);
        private static readonly Color Err = new Color32(224, 87, 74, 255);
        private static readonly Color Info = new Color32(94, 155, 191, 255);
        private static readonly Color Field = new Color32(17, 20, 23, 255);

        private const float Pad = 14f;
        private const float RowH = 26f;

        private List<ModMetaData> active;
        private List<ModMetaData> opened;
        private List<ModMetaData> inactiveCache;
        private ModMetaData selected;
        private int tab;
        private string fActive = "", fInactive = "";
        private bool onlyProblems;
        private bool cProb;
        private bool ActiveFiltered => !fActive.NullOrEmpty() || onlyProblems;
        private Vector2 sInactive, sActive, sDetail, sProblems, sProfiles, sBackups;

        private bool dirty = true;
        private List<Issue> issues = new List<Issue>();
        private readonly Dictionary<ModMetaData, Sev> marks = new Dictionary<ModMetaData, Sev>();
        private readonly Dictionary<ModMetaData, List<KeyValuePair<Sev, string>>> notes = new Dictionary<ModMetaData, List<KeyValuePair<Sev, string>>>();
        private List<List<DupCopy>> dups = new List<List<DupCopy>>();
        private List<SamePair> same = new List<SamePair>();
        private List<LogBlame> blames = new List<LogBlame>();   // 0.1.8
        private bool needsRestart;
        private bool closeConfirmed;
        private readonly bool[] collapsed = new bool[4];

        private int dragFrom = -1;
        private bool dragging;
        // 0.3.1: several mods selected at once (Ctrl+click, Shift+click, Ctrl+A); 'selected' is the last one clicked
        private readonly HashSet<ModMetaData> multi = new HashSet<ModMetaData>();
        private float dragStartY;

        private string profileName = "";
        private List<FileInfo> profileFiles, backupFiles;
        private readonly HashSet<ModMetaData> previewLoaded = new HashSet<ModMetaData>();

        private static bool InGame => Current.ProgramState == ProgramState.Playing;

        public override Vector2 InitialSize => new Vector2(Mathf.Min(1400f, UI.screenWidth - 40f), Mathf.Min(880f, UI.screenHeight - 40f));

        protected override float Margin => 0f;

        private readonly List<string> initialIds;

        // 0.1.2: open with a prepared list (order guard: "restore my order")
        public Dialog_ModSortManager(List<string> ids) : this()
        {
            initialIds = ids;
        }

        public Dialog_ModSortManager()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = false;
            closeOnAccept = false;
            closeOnCancel = true;
            draggable = false;
        }

        public override void PreOpen()
        {
            base.PreOpen();
            try
            {
                if (!InGame)
                    ModLister.RebuildModList();
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] " + e.Message); }
            NewMods.Refresh();
            try { var _ = ModChanges.Changed; } catch { }
            active = ModsConfig.ActiveModsInLoadOrder.ToList();
            opened = active.ToList();
            selected = active.FirstOrDefault();
            dups = Duplicates.Scan();
            same = SameThing.Analyze();
            blames = LogErrors.Analyze();
            dirty = true;
            if (initialIds != null) ApplyIds(initialIds, null);
            else if (!Bisect.Active) OfferNextGroup();
        }

        public override void PostClose()
        {
            base.PostClose();
            // free the loaded pictures properly (the game's UnsetPreviewImage would leave them blank for the session)
            foreach (ModMetaData m in previewLoaded)
                try
                {
                    var t = Traverse.Create(m);
                    var tex = t.Field("previewImage").GetValue<Texture2D>();
                    if (tex != null) UnityEngine.Object.Destroy(tex);
                    t.Field("previewImage").SetValue(null);
                    t.Field("previewImageWasLoaded").SetValue(false);
                }
                catch { }
            previewLoaded.Clear();
        }

        private bool Changed
        {
            get
            {
                if (active.Count != opened.Count) return true;
                for (int i = 0; i < active.Count; i++)
                    if (active[i] != opened[i]) return true;
                return false;
            }
        }

        public override bool OnCloseRequest()
        {
            if (closeConfirmed || !Changed)
                return true;
            // 0.1.1: unsaved changes – save, throw away, or go back
            var box = new Dialog_MessageBox("MSM_CloseAsk".Translate(), "MSM_SaveRestart".Translate(), SaveAndRestart,
                "MSM_DiscardShort".Translate(), delegate { closeConfirmed = true; Close(); }, null, false, null, null);
            // 0.3.3: save now, the new list is used the next time you start the game yourself
            if (!Bisect.Active)
            {
                box.buttonCText = "MSM_SaveContinue".Translate();
                box.buttonCAction = SaveAndContinue;
            }
            Find.WindowStack.Add(box);
            return false;
        }

        // ================= analysis =================
        private int ver;
        private int cVer = -1;
        private string cInactF, cActF;
        private List<ModMetaData> cInact, cAct;
        private readonly Dictionary<Issue, float> heights = new Dictionary<Issue, float>();
        private float heightsW;

        private int nProbCache;
        private readonly Dictionary<ModMetaData, int> indexOf = new Dictionary<ModMetaData, int>();

        private void Recompute()
        {
            dirty = false;
            ver++;
            heights.Clear();
            issues = new List<Issue>();
            marks.Clear();
            notes.Clear();
            var set = new HashSet<ModMetaData>(active);
            inactiveCache = ModLister.AllInstalledMods.Where(m => !set.Contains(m) && !Removal.IsGone(m)).OrderByDescending(m => NewMods.IsNew(m) ? NewMods.AddedAt(m).Value.Ticks : 0L).ThenByDescending(m => m.Official).ThenBy(m => m.Name).ToList();

            // duplicates
            foreach (List<DupCopy> g in dups)
            {
                if (g.Count(c => File.Exists(Path.Combine(c.path, "About", "About.xml")) || Directory.Exists(c.path)) < 2)
                    continue;
                int local = g.Count(c => !c.steam);
                var iss = new Issue { group = 0, sev = local >= 2 ? Sev.Error : Sev.Warn };
                iss.title = "MSM_DupTitle".Translate(g[0].name, g.Count);
                var lines = new List<string>();
                DupCopy newest = Duplicates.Newest(g);
                foreach (DupCopy c in g)
                {
                    string state = c.meta == null ? "MSM_DupIgnored".Translate().ToString()
                        : (active.Contains(c.meta) ? "MSM_DupActive".Translate().ToString() : "MSM_DupInstalled".Translate().ToString());
                    lines.Add("• " + Path.GetFileName(c.path) + " · v" + (c.version.NullOrEmpty() ? "?" : c.version) + " · "
                        + (c.steam ? "MSM_SrcSteam".Translate().ToString() : "MSM_SrcLocal".Translate().ToString()) + " · " + state + (c == newest ? " · " + "MSM_DupNewest".Translate() : ""));
                    if (c.meta != null)
                    {
                        Sorter.Mark(marks, c.meta, iss.sev);
                        Note(c.meta, iss.sev, "MSM_NoteDup".Translate());
                    }
                }
                iss.text = (local >= 2 ? "MSM_DupLocalText".Translate().ToString() : "MSM_DupSteamText".Translate().ToString()) + "\n" + string.Join("\n", lines.ToArray());
                List<DupCopy> gg = g;
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_KeepNewest".Translate(), () => KeepOnly(gg, Duplicates.Newest(gg))));
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_OpenFolders".Translate(), () => { foreach (DupCopy c in gg) MSM.OpenFolder(c.path); }));
                issues.Add(iss);
            }

            // missing dependencies
            int j;
            foreach (ModMetaData m in active)
            {
                foreach (ModDependency d in m.Dependencies)
                {
                    if (d == null || d.packageId.NullOrEmpty()) continue;
                    var ids = new List<string> { d.packageId };
                    if (d.alternativePackageIds != null) ids.AddRange(d.alternativePackageIds);
                    if (ids.Any(id => Sorter.Present(active, id, out j))) continue;
                    ModMetaData inst = ids.Select(id => ModLister.GetModWithIdentifier(id, true)).FirstOrDefault(x => x != null);
                    var iss = new Issue { group = 1, sev = Sev.Error, title = "MSM_DepTitle".Translate(m.Name, d.displayName ?? d.packageId) };
                    iss.text = inst != null ? "MSM_DepInstalled".Translate().ToString() : "MSM_DepMissing".Translate().ToString();
                    ModMetaData mm = m;
                    if (inst != null)
                        iss.actions.Add(new KeyValuePair<string, Action>("MSM_Activate".Translate(inst.Name.Truncate(120f)), () => Activate(inst)));
                    else if (!d.Url.NullOrEmpty())
                    {
                        string url = d.Url;
                        // 0.1.9: a Workshop mod is subscribed right here – Steam downloads it
                        ulong wid = Updates.WorkshopId(d.steamWorkshopUrl ?? url);
                        string dname = d.displayName ?? d.packageId;
                        if (wid != 0 && Updates.CanSubscribe)
                            iss.actions.Add(new KeyValuePair<string, Action>("MSM_Subscribe".Translate(), () =>
                            {
                                if (Updates.Subscribe(wid))
                                    Messages.Message("MSM_Subscribed".Translate(dname), MessageTypeDefOf.TaskCompletion, false);
                                else
                                    SteamUtility.OpenUrl(url);
                            }));
                        iss.actions.Add(new KeyValuePair<string, Action>("MSM_Download".Translate(), () => SteamUtility.OpenUrl(url)));
                    }
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_Deactivate".Translate(), () => Deactivate(mm)));
                    issues.Add(iss);
                    Sorter.Mark(marks, m, Sev.Error);
                    Note(m, Sev.Error, "MSM_NoteDep".Translate(d.displayName ?? d.packageId));
                }
            }

            // incompatible pairs
            var seen = new HashSet<string>();
            for (int i = 0; i < active.Count; i++)
            {
                ModMetaData a = active[i];
                foreach (string id in a.IncompatibleWith)
                {
                    if (!Sorter.Present(active, id, out j) || j == i) continue;
                    ModMetaData b = active[j];
                    string key = i < j ? i + "|" + j : j + "|" + i;
                    if (!seen.Add(key)) continue;
                    var iss = new Issue { group = 1, sev = Sev.Error, title = "MSM_IncTitle".Translate(a.Name, b.Name), text = "MSM_IncText".Translate(a.Name) };
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateX".Translate(a.Name.Truncate(120f)), () => Deactivate(a)));
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateX".Translate(b.Name.Truncate(120f)), () => Deactivate(b)));
                    issues.Add(iss);
                    Sorter.Mark(marks, a, Sev.Error);
                    Sorter.Mark(marks, b, Sev.Error);
                    Note(a, Sev.Error, "MSM_NoteInc".Translate(b.Name));
                    Note(b, Sev.Error, "MSM_NoteInc".Translate(a.Name));
                }
            }

            // 0.2.1: known mods that do the same job (one switches itself off, or they fight)
            foreach (Rivals.Hit h in Rivals.Find(active))
            {
                var iss = new Issue { group = 1, sev = h.rule.sev, title = "MSM_RivalTitle".Translate(h.a.Name, h.b.Name) };
                iss.text = ("MSM_Rival_" + h.rule.key).Translate(h.a.Name, h.b.Name).ToString() + "\n" + "MSM_RivalHint".Translate();
                ModMetaData ra = h.a, rb = h.b;
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateX".Translate(ra.Name.Truncate(120f)), () => Deactivate(ra)));
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateX".Translate(rb.Name.Truncate(120f)), () => Deactivate(rb)));
                issues.Add(iss);
                Sorter.Mark(marks, ra, h.rule.sev);
                Sorter.Mark(marks, rb, h.rule.sev);
                Note(ra, h.rule.sev, "MSM_NoteRival".Translate(rb.Name));
                Note(rb, h.rule.sev, "MSM_NoteRival".Translate(ra.Name));
            }

            // load order rules
            List<string> order = Sorter.OrderProblems(active, marks);
            if (order.Count > 0)
            {
                var iss = new Issue { group = 1, sev = Sev.Warn, title = "MSM_OrderTitle".Translate(order.Count) };
                iss.text = string.Join("\n", order.Take(8).Select(x => "• " + x).ToArray()) + (order.Count > 8 ? "\n…" : "");
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_SmartSort".Translate(), SmartSort));
                issues.Add(iss);
                foreach (ModMetaData m in active)
                    if (marks.ContainsKey(m) && marks[m] == Sev.Warn) Note(m, Sev.Warn, "MSM_NoteOrder".Translate());
            }

            // made for an older game version
            List<ModMetaData> old = active.Where(m => !Rules.VersionOk(m)).ToList();   // 0.3.0: no-warning lists
            if (old.Count > 0)
            {
                string cur = VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor;
                var iss = new Issue { group = 1, sev = Sev.Warn, title = "MSM_OldTitle".Translate(old.Count, cur) };
                iss.text = "MSM_OldText".Translate() + "\n" + string.Join(", ", old.Select(m => m.Name + " (" + MSM.Versions(m) + ")").ToArray());
                issues.Add(iss);
                foreach (ModMetaData m in old)
                {
                    Sorter.Mark(marks, m, Sev.Warn);
                    Note(m, Sev.Warn, "MSM_NoteOld".Translate(MSM.Versions(m)));
                }
            }

            // doing the same thing
            hiddenSameCount = 0;
            foreach (SamePair sp in same)
            {
                if (!active.Contains(sp.a) || !active.Contains(sp.b)) continue;
                // 0.2.1: "works for me" – the player hid this pair
                string hk = SameKey(sp);
                bool hidden = MSMMod.Settings.hiddenSame.Contains(hk);
                if (hidden) hiddenSameCount++;
                if (hidden && !showHiddenSame) continue;
                var iss = new Issue { group = 2, sev = Sev.Info, title = sp.a.Name + " × " + sp.b.Name + (hidden ? "  · " + "MSM_SameHiddenTag".Translate() : "") };
                iss.text = (sp.hot ? "MSM_SameHot".Translate() : "MSM_SameText".Translate()).ToString() + " (" + sp.methods.Count + "): " + string.Join(", ", sp.methods.Take(4).ToArray()) + (sp.methods.Count > 4 ? "…" : "");
                // 0.1.7: how they change it – first two methods, the rest in the details window
                foreach (SameMethod d in sp.detail.Take(2))
                    iss.text += "\n• " + d.method + ": " + sp.a.Name.Truncate(160f) + " – " + d.kindsA + " · " + sp.b.Name.Truncate(160f) + " – " + d.kindsB;
                iss.text += "\n" + (sp.blocking ? "MSM_SameBlocking" : "MSM_SameAdditive").Translate();
                SamePair p = sp;
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_SameDetails".Translate(), () => Find.WindowStack.Add(new Dialog_SameDetail(p))));
                if (sp.blocking)
                {
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateNamed".Translate(sp.a.Name.Truncate(120f)), () => Deactivate(p.a)));
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_DeactivateNamed".Translate(sp.b.Name.Truncate(120f)), () => Deactivate(p.b)));
                }
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_Show".Translate(sp.a.Name.Truncate(120f)), () => { selected = p.a; tab = 0; }));
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_Show".Translate(sp.b.Name.Truncate(120f)), () => { selected = p.b; tab = 0; }));
                if (hidden)
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_SameUnhide".Translate(), () => { MSMMod.Settings.hiddenSame.Remove(hk); MSMMod.Settings.Write(); dirty = true; }));
                else
                    iss.actions.Add(new KeyValuePair<string, Action>("MSM_SameWorks".Translate(), () => { if (!MSMMod.Settings.hiddenSame.Contains(hk)) MSMMod.Settings.hiddenSame.Add(hk); MSMMod.Settings.Write(); dirty = true; }));
                issues.Add(iss);
                if (hidden) continue;
                Sorter.Mark(marks, sp.a, Sev.Info);
                Sorter.Mark(marks, sp.b, Sev.Info);
                Note(sp.a, Sev.Info, "MSM_NoteSame".Translate(sp.b.Name));
                Note(sp.b, Sev.Info, "MSM_NoteSame".Translate(sp.a.Name));
            }

            // 0.1.8: errors in this session's log, by the mod that caused them
            foreach (LogBlame b in blames)
            {
                if (!active.Contains(b.mod)) continue;
                var iss = new Issue { group = 3, sev = Sev.Warn, title = "MSM_LogTitle".Translate(b.mod.Name, b.count) };
                iss.text = "MSM_LogText".Translate() + "\n“" + b.sample + "”";
                LogBlame bb = b;
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_Show".Translate(b.mod.Name.Truncate(120f)), () => { selected = bb.mod; tab = 0; }));
                iss.actions.Add(new KeyValuePair<string, Action>("MSM_Deactivate".Translate(), () => Deactivate(bb.mod)));
                issues.Add(iss);
                Sorter.Mark(marks, b.mod, Sev.Warn);
                Note(b.mod, Sev.Warn, "MSM_NoteLog".Translate(b.count));
            }

            nProbCache = active.Count(x => marks.ContainsKey(x));
            indexOf.Clear();
            for (int k = 0; k < active.Count; k++) indexOf[active[k]] = k;
        }

        // 0.2.1: hidden "works for me" pairs
        private int hiddenSameCount;
        private bool showHiddenSame;

        private static string SameKey(SamePair sp)
        {
            string a = sp.a.PackageId.ToLowerInvariant(), b = sp.b.PackageId.ToLowerInvariant();
            return string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;
        }

        private void Note(ModMetaData m, Sev s, string text)
        {
            List<KeyValuePair<Sev, string>> l;
            if (!notes.TryGetValue(m, out l)) notes[m] = l = new List<KeyValuePair<Sev, string>>();
            if (!l.Any(x => x.Value == text)) l.Add(new KeyValuePair<Sev, string>(s, text));
        }

        // ================= edits =================
        private void Activate(ModMetaData m)
        {
            if (m == null || active.Contains(m)) return;
            int j;
            if (Sorter.Present(active, m.PackageIdNonUnique, out j))
                active[j] = m;   // the other copy of the same mod
            else
                active.Add(m);
            selected = m;
            dirty = true;
        }

        private void Deactivate(ModMetaData m)
        {
            if (m == null) return;
            if (m.IsCoreMod)
            {
                Messages.Message("MSM_CoreStays".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            active.Remove(m);
            dirty = true;
        }

        private void Move(int from, int to)
        {
            if (from < 0 || from >= active.Count) return;
            to = Mathf.Clamp(to, 0, active.Count - 1);
            if (from == to) return;
            ModMetaData m = active[from];
            active.RemoveAt(from);
            active.Insert(to, m);
            dirty = true;
        }

        private void SmartSort()
        {
            string err;
            if (Sorter.Sort(active, out err))
            {
                dirty = true;
                // 0.3.0: say when load order rules placed some mods
                if (Sorter.LastMoved.Count > 0) Messages.Message("MSM_SortedRules".Translate(Sorter.LastMoved.Count), MessageTypeDefOf.TaskCompletion, false);
                else Messages.Message("MSM_Sorted".Translate(), MessageTypeDefOf.TaskCompletion, false);
            }
            else
                Find.WindowStack.Add(new Dialog_MessageBox(err));
        }

        private void RemoveMenu(ModMetaData m)
        {
            var opts = new List<FloatMenuOption>();
            void Done(bool now)
            {
                if (active.Contains(m)) { active.Remove(m); }
                dirty = true;
                needsRestart = true;
                if (selected == m) selected = active.FirstOrDefault();
                Messages.Message((now ? "MSM_RemovedNow" : "MSM_RemovedLater").Translate(m.Name), MessageTypeDefOf.TaskCompletion, false);
            }
            if (m.OnSteamWorkshop)
                opts.Add(new FloatMenuOption("MSM_RemoveUnsub".Translate(), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_RemoveUnsubConfirm".Translate(m.Name), () =>
                    {
                        if (MSM.Unsubscribe(m)) { Removal.Gone.Add(m.RootDir.FullName); Done(true); }
                        else Messages.Message("MSM_RemoveFailed".Translate(m.Name), MessageTypeDefOf.RejectInput, false);
                    }, true))));
            else
            {
                opts.Add(new FloatMenuOption("MSM_RemovePark".Translate(), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_RemoveParkConfirm".Translate(m.Name, MSM.ParkDir), () =>
                        Done(Removal.RemoveFolder(m, false)), false))));
                opts.Add(new FloatMenuOption("MSM_RemoveDelete".Translate(), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_RemoveDeleteConfirm".Translate(m.Name, m.RootDir.FullName), () =>
                        Done(Removal.RemoveFolder(m, true)), true))));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // 0.3.2: the same as RemoveMenu for several mods; Workshop and local mods are handled each their way
        private void RemoveManyMenu(List<ModMetaData> mods)
        {
            List<ModMetaData> steam = mods.Where(x => x.OnSteamWorkshop).ToList();
            List<ModMetaData> local = mods.Where(x => !x.OnSteamWorkshop).ToList();
            string Names(List<ModMetaData> l) => string.Join("\n", l.Take(15).Select(x => "• " + x.Name).ToArray()) + (l.Count > 15 ? "\n…" : "");
            void Finish(List<ModMetaData> done, int failed)
            {
                foreach (ModMetaData m in done)
                {
                    active.Remove(m);
                    multi.Remove(m);
                }
                if (done.Count > 0) { dirty = true; needsRestart = true; }
                if (selected != null && done.Contains(selected)) selected = active.FirstOrDefault();
                Messages.Message("MSM_GroupRemoved".Translate(done.Count, failed), failed > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.TaskCompletion, false);
            }
            var opts = new List<FloatMenuOption>();
            if (steam.Count > 0)
                opts.Add(new FloatMenuOption("MSM_GroupUnsub".Translate(steam.Count), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_GroupUnsubConfirm".Translate(steam.Count, Names(steam)), () =>
                    {
                        var done = new List<ModMetaData>();
                        int failed = 0;
                        foreach (ModMetaData m in steam)
                        {
                            if (MSM.Unsubscribe(m)) { Removal.Gone.Add(m.RootDir.FullName); done.Add(m); }
                            else failed++;
                        }
                        Finish(done, failed);
                    }, true))));
            if (local.Count > 0)
            {
                opts.Add(new FloatMenuOption("MSM_GroupPark".Translate(local.Count), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_GroupParkConfirm".Translate(local.Count, MSM.ParkDir, Names(local)), () =>
                    {
                        var done = new List<ModMetaData>();
                        foreach (ModMetaData m in local) { Removal.RemoveFolder(m, false); done.Add(m); }
                        Finish(done, 0);
                    }, false))));
                opts.Add(new FloatMenuOption("MSM_GroupDelete".Translate(local.Count), () =>
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_GroupDeleteConfirm".Translate(local.Count, Names(local)), () =>
                    {
                        var done = new List<ModMetaData>();
                        foreach (ModMetaData m in local) { Removal.RemoveFolder(m, true); done.Add(m); }
                        Finish(done, 0);
                    }, true))));
            }
            if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void KeepOnly(List<DupCopy> g, DupCopy keep)
        {
            var others = g.Where(c => c != keep).ToList();
            string names = string.Join("\n", others.Select(c => "• " + c.path + (c.steam ? " (" + "MSM_Unsub".Translate() + ")" : "")).ToArray());
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_KeepConfirm".Translate(keep.path, names), delegate
            {
                var failed = new List<string>();
                foreach (DupCopy c in others)
                {
                    bool ok;
                    if (c.steam)
                        ok = c.meta != null && MSM.Unsubscribe(c.meta);
                    else
                    {
                        try
                        {
                            string dst = Path.Combine(MSM.ParkDir, Path.GetFileName(c.path) + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                            Directory.Move(c.path, dst);
                            ok = true;
                        }
                        catch (Exception e)
                        {
                            Log.Warning("[Mod Sort Manager] could not move " + c.path + ": " + e.Message);
                            ok = false;
                        }
                    }
                    if (!ok) { failed.Add(c.path); continue; }
                    Duplicates.Handled.Add(c.path);
                    // the active entry points to the kept copy
                    if (c.meta != null && active.Contains(c.meta))
                    {
                        int idx = active.IndexOf(c.meta);
                        if (keep.meta != null && !active.Contains(keep.meta)) active[idx] = keep.meta;
                        else if (keep.meta == null) { /* same id after restart */ }
                        else active.RemoveAt(idx);
                    }
                }
                needsRestart = true;
                dups = Duplicates.Scan();
                dirty = true;
                if (failed.Count > 0)
                    Find.WindowStack.Add(new Dialog_MessageBox("MSM_KeepFailed".Translate(string.Join("\n", failed.ToArray()), MSM.ParkDir)));
                else
                    Messages.Message("MSM_KeepDone".Translate(MSM.ParkDir), MessageTypeDefOf.TaskCompletion, false);
            }, true));
        }

        // 0.1.3: used by the check before saving
        internal List<ModMetaData> ActiveList => active;
        internal List<ModMetaData> OpenedList => opened;
        internal void ActivateMod(ModMetaData m) { Activate(m); }
        internal void DeactivateMod(ModMetaData m) { Deactivate(m); }
        private bool preSaveOk;
        private List<string> queueOrder;
        private List<List<string>> queueAfterSave;

        internal void SetQueueAfterSave(List<string> order, List<List<string>> rest)
        {
            queueOrder = order;
            queueAfterSave = rest;
        }

        internal void ContinueSave()
        {
            preSaveOk = true;
            try { SaveAndRestart(); } finally { preSaveOk = false; }
        }

        // 0.1.3: a group of new mods left for later by the check before saving
        private void OfferNextGroup()
        {
            List<string> order;
            List<List<string>> groups;
            if (!PreSave.LoadQueue(out order, out groups)) return;
            var ids = new HashSet<string>(active.Select(PreSave.Id));
            var byId = new Dictionary<string, ModMetaData>();
            foreach (ModMetaData m in ModLister.AllInstalledMods) { string k = PreSave.Id(m); if (!byId.ContainsKey(k)) byId[k] = m; }
            groups = groups.Select(g => g.Where(x => byId.ContainsKey(x) && !ids.Contains(x)).ToList()).Where(g => g.Count > 0).ToList();
            if (groups.Count == 0) { PreSave.DropQueue(); return; }
            List<ModMetaData> next = groups[0].Select(x => byId[x]).ToList();
            var dlg = new Dialog_MessageBox("MSM_PS_NextGroup".Translate(next.Count, string.Join("\n", next.Select(m => "• " + m.Name).ToArray())),
                "MSM_PS_AddGroup".Translate(), delegate
                {
                    foreach (ModMetaData m in next) PreSave.Insert(active, m, order);
                    dirty = true;
                    // the queue moves on only when the list is really saved; then save and restart right away
                    SetQueueAfterSave(order, groups.Skip(1).ToList());
                    SaveAndRestart();
                },
                "MSM_PS_Later".Translate(), null);
            dlg.buttonCText = "MSM_PS_DropQueue".Translate();
            dlg.buttonCAction = PreSave.DropQueue;
            Find.WindowStack.Add(dlg);
        }

        private void SaveAndRestart()
        {
            // nothing changed: do not touch ModsConfig.xml at all
            if (!Changed && !needsRestart)
            {
                closeConfirmed = true;
                Close();
                return;
            }
            if (Bisect.Active)
            {
                // the list in the window holds only the tested half – cancel the search and bring all mods back instead
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_BisectRunning".Translate(), delegate { closeConfirmed = true; Bisect.Finish(false); }, true));
                return;
            }
            if (!preSaveOk && Changed && MSMMod.Settings.preSaveCheck)
            {
                Find.WindowStack.Add(new Dialog_PreSave(this));
                return;
            }
            if (InGame && !(MSMMod.Settings.returnToGame && CanSaveGame()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_NoSaveConfirm".Translate(), DoSaveAndRestart, true));
                return;
            }
            DoSaveAndRestart();
        }

        // 0.1.5: restart the game without changing the mod list (the return to the game works the same)
        private void JustRestart()
        {
            if (Bisect.Active)
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_BisectRunning".Translate(), delegate { closeConfirmed = true; Bisect.Finish(false); }, true));
                return;
            }
            Action go = delegate
            {
                PrepareReturn();
                Log.Message("[Mod Sort Manager] restart without changing the mod list");
                closeConfirmed = true;
                GenCommandLine.Restart();
            };
            if (InGame && !(MSMMod.Settings.returnToGame && CanSaveGame()))
            {
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_NoSaveConfirm".Translate(), go, true));
                return;
            }
            go();
        }

        private void DoSaveAndRestart()
        {
            PrepareReturn();
            SaveListAndRestart();
        }

        // in a game: the return save first, while the loaded game still matches the mod list
        private void PrepareReturn()
        {
            try
            {
                if (InGame)
                {
                    if (MSMMod.Settings.returnToGame && CanSaveGame())
                    {
                        string path = GenFilePaths.FilePathForSavedGame("MSM_navrat");
                        if (File.Exists(path)) File.Delete(path);
                        DateTime start = DateTime.Now.AddSeconds(-2);
                        GameDataSaveLoader.SaveGame("MSM_navrat");
                        if (File.Exists(path) && File.GetLastWriteTime(path) >= start)
                            ReturnToSave.Write("MSM_navrat");
                        else
                            Log.Warning("[Mod Sort Manager] the return save was not written – no return after the restart.");
                    }
                }
                else if (MSMMod.Settings.loadLastSave)
                {
                    string n = ReturnToSave.NewestSave();
                    if (n != null) ReturnToSave.Write(n, true);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Mod Sort Manager] return to game not prepared: " + e.Message);
            }
        }

        private void SaveAndContinue()
        {
            if (!SaveList()) return;
            Messages.Message("MSM_SavedNextStart".Translate(), MessageTypeDefOf.TaskCompletion, false);
            closeConfirmed = true;
            Close();
        }

        private void SaveListAndRestart()
        {
            if (!SaveList()) return;
            closeConfirmed = true;
            GenCommandLine.Restart();
        }

        private bool SaveList()
        {
            try
            {
                MSM.BackupModsConfig();
                List<string> ids = active.Select(m => m.PackageId).ToList();
                ModsConfig.SetActiveToList(ids);
                ModsConfig.Save();
                // 0.1.1: read the file back – the order on disk must be exactly ours
                List<string> back = MSM.ReadModsConfigIds(GenFilePaths.ModsConfigFilePath);
                bool same = back.Count == ids.Count;
                for (int i = 0; same && i < ids.Count; i++)
                    same = string.Equals(back[i], ids[i], StringComparison.OrdinalIgnoreCase);
                Log.Message("[Mod Sort Manager] saved " + ids.Count + " mods to " + GenFilePaths.ModsConfigFilePath + (same ? " (verified)" : " – FILE DIFFERS (" + back.Count + " read back)"));
                if (!same)
                {
                    Find.WindowStack.Add(new Dialog_MessageBox("MSM_SaveFailed".Translate("ModsConfig.xml")));
                    return false;
                }
                LoadedModManager.GetMod<MSMMod>()?.WriteSettings();
                if (queueAfterSave != null) PreSave.WriteQueue(queueOrder, queueAfterSave);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("[Mod Sort Manager] saving the mod list failed: " + e);
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_SaveFailed".Translate(e.Message)));
                return false;
            }
        }

        private static bool CanSaveGame()
        {
            return Current.Game != null && !GameDataSaveLoader.SavingIsTemporarilyDisabled && !Current.Game.Info.permadeathMode;
        }

        // ================= drawing helpers =================
        internal static void Label(Rect r, string text, Color c, GameFont font = GameFont.Small, TextAnchor anchor = TextAnchor.MiddleLeft, bool wrap = true)
        {
            GameFont f = Text.Font;
            TextAnchor a = Text.Anchor;
            bool w = Text.WordWrap;
            Color g = GUI.color;
            Text.Font = font;
            Text.Anchor = anchor;
            Text.WordWrap = wrap;
            GUI.color = c;
            Widgets.Label(r, text);
            GUI.color = g;
            Text.WordWrap = w;
            Text.Anchor = a;
            Text.Font = f;
        }

        internal static bool Button(Rect r, string label, bool accent = false, bool enabled = true)
        {
            bool over = enabled && Mouse.IsOver(r);
            MUI.Fill(r, accent ? new Color32(43, 74, 42, 255) : (over ? (Color)Hover : (Color)new Color32(34, 39, 44, 255)), 4f);
            MUI.Frame(r, accent ? MUI.Accent : (over ? MUI.Muted : MUI.Border), 1f, 4f);
            Label(r, label, enabled ? (accent ? new Color32(223, 242, 212, 255) : MUI.Text1) : MUI.Muted, GameFont.Small, TextAnchor.MiddleCenter, false);
            if (!enabled) return false;
            if (Widgets.ButtonInvisible(r))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                return true;
            }
            return false;
        }

        private static Color SevColor(Sev s)
        {
            return s == Sev.Error ? Err : (s == Sev.Warn ? MUI.Warn : Info);
        }

        private Color Dot(ModMetaData m)
        {
            Sev s;
            return marks.TryGetValue(m, out s) ? SevColor(s) : MUI.Accent;
        }

        private static void DrawDot(Rect r, Color c)
        {
            MUI.Fill(new Rect(r.x, r.center.y - 4f, 8f, 8f), c, 4f);
        }

        private static string SearchField(Rect r, string value, string hint)
        {
            MUI.Fill(r, Field, 4f);
            MUI.Frame(r, MUI.Border, 1f, 4f);
            string v = Widgets.TextField(r.ContractedBy(2f), value ?? "", 100);
            if (v.NullOrEmpty())
                Label(new Rect(r.x + 8f, r.y, r.width - 16f, r.height), hint, new Color32(110, 118, 125, 255), GameFont.Small);
            return v;
        }

        // ================= window =================
        private static string Key(ModMetaData m)
        {
            try { return m.RootDir != null ? m.RootDir.FullName.ToLowerInvariant() : m.PackageId; } catch { return m.PackageId; }
        }

        // 0.1.3: rebuild the installed list after Steam installs or removes a mod; the unsaved order stays
        private void CheckInstalled()
        {
            if (!ModWatch.Changed || dragging || Time.realtimeSinceStartup - ModWatch.LastEvent < 1.5f) return;
            ModWatch.Changed = false;
            try
            {
                int before = ModLister.AllInstalledMods.Count();
                var aKeys = active.Select(m => new KeyValuePair<string, ModMetaData>(Key(m), m)).ToList();
                var oKeys = opened.Select(m => new KeyValuePair<string, ModMetaData>(Key(m), m)).ToList();
                string sKey = selected != null ? Key(selected) : null;
                // the game would drop active mods that are still downloading – keep the saved list as it was
                List<string> cfg = null;
                try { cfg = new List<string>(Traverse.Create(typeof(ModsConfig)).Field("data").Field("activeMods").GetValue<List<string>>()); } catch { }
                ModLister.RebuildModList();
                if (cfg != null)
                {
                    try
                    {
                        List<string> now = Traverse.Create(typeof(ModsConfig)).Field("data").Field("activeMods").GetValue<List<string>>();
                        if (now != null && now.Count != cfg.Count)
                        {
                            now.Clear();
                            now.AddRange(cfg);
                            AccessTools.Method(typeof(ModsConfig), "RecacheActiveMods")?.Invoke(null, null);
                        }
                    }
                    catch { }
                }
                var map = new Dictionary<string, ModMetaData>();
                foreach (ModMetaData m in ModLister.AllInstalledMods) { string k = Key(m); if (!map.ContainsKey(k)) map[k] = m; }
                ModMetaData pick(KeyValuePair<string, ModMetaData> kv) { ModMetaData n; return map.TryGetValue(kv.Key, out n) ? n : kv.Value; }
                active = aKeys.Select(pick).ToList();
                opened = oKeys.Select(pick).ToList();
                ModMetaData sel;
                selected = sKey != null && map.TryGetValue(sKey, out sel) ? sel : active.FirstOrDefault();
                previewLoaded.Clear();
                NewMods.Refresh();
                dups = Duplicates.Scan();
                same = SameThing.Analyze();
            blames = LogErrors.Analyze();
                try { Updates.Refresh(true); } catch { }
                dirty = true;
                int diff = ModLister.AllInstalledMods.Count() - before;
                Messages.Message("MSM_ListRefreshed".Translate(diff > 0 ? "+" + diff : diff.ToString()), MessageTypeDefOf.SilentInput, false);
            }
            catch (Exception e) { Log.Warning("[Mod Sort Manager] refreshing the mod list: " + e.Message); }
        }

        public override void DoWindowContents(Rect inRect)
        {
            CheckInstalled();
            if (dirty) Recompute();
            MUI.Fill(inRect, Bg);
            Text.Font = GameFont.Small;

            // header
            Rect head = new Rect(inRect.x + Pad + 4f, inRect.y, inRect.width - 2 * Pad, 58f);
            Label(new Rect(head.x, head.y + 6f, 260f, 44f), "Mod Sort Manager", MUI.Text1, GameFont.Medium);
            Label(new Rect(head.x + 220f, head.y + 8f, 400f, 40f), "MSM_Subtitle".Translate(MSMMod.Version), MUI.Muted, GameFont.Tiny);
            float bx = head.xMax - 40f;
            bx -= 170f;
            if (Button(new Rect(bx, head.y + 12f, 170f, 32f), "MSM_SmartSort".Translate(), true)) SmartSort();
            bx -= 140f;
            if (Button(new Rect(bx, head.y + 12f, 132f, 32f), "MSM_Revert".Translate(), false, Changed))
            {
                active = opened.ToList();
                dirty = true;
            }
            if (!InGame)
            {
                bx -= 150f;
                if (Button(new Rect(bx, head.y + 12f, 142f, 32f), "MSM_Vanilla".Translate()))
                {
                    if (Changed)
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_Discard".Translate(), delegate { closeConfirmed = true; Close(); MSM.OpenVanilla(); }, true));
                    else
                    {
                        closeConfirmed = true;
                        Close();
                        MSM.OpenVanilla();
                    }
                }
            }
            MUI.Fill(new Rect(inRect.x, head.yMax, inRect.width, 1f), Line);

            // tabs
            Rect tabs = new Rect(inRect.x + Pad, head.yMax + 1f, inRect.width - 2 * Pad, 36f);
            int nErr = issues.Count(i => i.sev == Sev.Error), nWarn = issues.Count(i => i.sev == Sev.Warn), nInfo = issues.Count(i => i.sev == Sev.Info);
            string[] names = { "MSM_TabOrder".Translate(), "MSM_TabProblems".Translate(), "MSM_TabProfiles".Translate(), "MSM_TabUpdates".Translate(), "MSM_TabReplace".Translate() };
            int nRep = ReplaceCount();
            int nUpd = Updates.Pending;
            float tx = tabs.x;
            for (int i = 0; i < names.Length; i++)
            {
                float w = Text.CalcSize(names[i]).x + 34f + (i == 1 ? 80f : 0f) + (i == 3 && nUpd > 0 ? 40f : 0f) + (i == 4 && nRep > 0 ? 40f : 0f);
                Rect t = new Rect(tx, tabs.y, w, tabs.height);
                if (Mouse.IsOver(t)) MUI.Fill(t, Hover);
                Label(new Rect(t.x + 16f, t.y, w, t.height), names[i], tab == i ? MUI.Text1 : MUI.Muted);
                if (i == 1)
                {
                    float cx = t.x + 22f + Text.CalcSize(names[i]).x;
                    if (nErr > 0) { Pill(new Rect(cx, t.y + 9f, 26f, 18f), nErr.ToString(), Err); cx += 30f; }
                    if (nWarn > 0) { Pill(new Rect(cx, t.y + 9f, 26f, 18f), nWarn.ToString(), MUI.Warn); }
                }
                if (i == 3 && nUpd > 0)
                    Pill(new Rect(t.x + 22f + Text.CalcSize(names[i]).x, t.y + 9f, 30f, 18f), nUpd.ToString(), Info);
                if (i == 4 && nRep > 0)
                    Pill(new Rect(t.x + 22f + Text.CalcSize(names[i]).x, t.y + 9f, 30f, 18f), nRep.ToString(), MUI.Warn);
                if (tab == i) MUI.Fill(new Rect(t.x, t.yMax - 2f, t.width, 2f), MUI.Accent);
                if (Widgets.ButtonInvisible(t)) tab = i;
                tx += w + 2f;
            }
            // 0.1.9: the Workshop inside the game (Steam overlay) – no need to leave the game
            Rect ws = new Rect(tabs.xMax - 200f, tabs.y + 4f, 200f, tabs.height - 8f);
            if (Widgets.ButtonText(ws, "MSM_OpenWorkshop".Translate()))
            {
                try { SteamUtility.OpenSteamWorkshopPage(); }
                catch { Application.OpenURL("https://steamcommunity.com/app/294100/workshop/"); }
            }
            TooltipHandler.TipRegion(ws, "MSM_OpenWorkshopTip".Translate());
            MUI.Fill(new Rect(inRect.x, tabs.yMax, inRect.width, 1f), Line);

            // footer
            Rect foot = new Rect(inRect.x, inRect.yMax - 56f, inRect.width, 56f);
            MUI.Fill(new Rect(foot.x, foot.y, foot.width, 1f), Line);
            DrawFooter(foot.ContractedBy(Pad, 10f), nErr, nWarn, nInfo);

            Rect body = new Rect(inRect.x, tabs.yMax + 1f, inRect.width, foot.y - tabs.yMax - 1f);
            if (tab == 0) DrawOrder(body);
            else if (tab == 1) DrawProblems(body);
            else if (tab == 2) DrawProfiles(body);
            else if (tab == 3) DrawUpdates(body);
            else DrawReplace(body);
        }

        private static void Pill(Rect r, string text, Color c)
        {
            MUI.Fill(r, new Color(c.r * 0.3f, c.g * 0.3f, c.b * 0.3f, 1f), 9f);
            Label(r, text, c, GameFont.Tiny, TextAnchor.MiddleCenter, false);
        }

        private void DrawFooter(Rect r, int nErr, int nWarn, int nInfo)
        {
            float x = r.x;
            x = Count(x, r, nErr, "MSM_CountErr", Err);
            x = Count(x, r, nWarn, "MSM_CountWarn", MUI.Warn);
            x = Count(x, r, nInfo, "MSM_CountInfo", Info);
            Label(new Rect(x, r.y, 220f, r.height), "MSM_CountActive".Translate(active.Count), MUI.Muted, GameFont.Small);

            bool restart = Changed || needsRestart;
            string saveLabel = !restart ? "MSM_Done".Translate().ToString()
                : (InGame && MSMMod.Settings.returnToGame && CanSaveGame() ? "MSM_SaveReturn".Translate().ToString() : "MSM_SaveRestart".Translate().ToString());
            float sw = Mathf.Max(200f, Text.CalcSize(saveLabel).x + 40f);
            Rect save = new Rect(r.xMax - sw, r.y, sw, r.height);
            if (Button(save, saveLabel, true)) SaveAndRestart();
            Rect close = new Rect(save.x - 130f, r.y, 120f, r.height);
            if (Button(close, "MSM_Close".Translate())) Close();
            // 0.1.5: a plain restart when nothing in the list changed
            if (!restart)
            {
                string rl = "MSM_Restart".Translate();
                float rw = Mathf.Max(120f, Text.CalcSize(rl).x + 30f);
                Rect rr = new Rect(close.x - 10f - rw, r.y, rw, r.height);
                if (Button(rr, rl)) JustRestart();
                TooltipHandler.TipRegion(rr, "MSM_RestartTip".Translate());
                close = rr;
            }

            // after the restart
            bool val = InGame ? MSMMod.Settings.returnToGame : MSMMod.Settings.loadLastSave;
            string lbl = InGame ? "MSM_ReturnToGame".Translate() : "MSM_LoadLastSave".Translate();
            float lw = Text.CalcSize(lbl).x + 8f;
            Rect sr = new Rect(close.x - 20f - 46f, r.y + (r.height - 24f) / 2f, 46f, 24f);
            Rect lr = new Rect(sr.x - lw - 6f, r.y, lw, r.height);
            Label(lr, lbl, MUI.Muted, GameFont.Small, TextAnchor.MiddleRight, false);
            if (InGame && !CanSaveGame())
                TooltipHandler.TipRegion(lr, "MSM_NoReturnTip".Translate());
            if (MUI.Switch(sr, val))
            {
                if (InGame) MSMMod.Settings.returnToGame = !val; else MSMMod.Settings.loadLastSave = !val;
            }
        }

        private static float Count(float x, Rect r, int n, string key, Color c)
        {
            if (n <= 0) return x;
            string t = key.Translate(n);
            DrawDot(new Rect(x, r.y, 8f, r.height), c);
            float w = Text.CalcSize(t).x + 8f;
            Label(new Rect(x + 14f, r.y, w, r.height), t, c, GameFont.Small, TextAnchor.MiddleLeft, false);
            return x + 14f + w + 14f;
        }

        // ================= tab: load order =================
        // 0.2.2: Up/Down moves the selection, Shift+Up/Down moves the selected mod in the active list
        // (only while no text field has the keyboard)
        private float hActive = 400f, hInactive = 400f;

        // 0.3.1: arrows or WASD, several mods at once:
        //   Up/Down (W/S)          move the cursor          Shift+Up/Down  move the selected mods
        //   Left/A, Delete         switch the selected off   Right/D        switch the selected on
        //   Ctrl+A                 select the whole list     Esc            only the cursor stays selected
        private List<ModMetaData> ListOf(ModMetaData m) => m == null ? null : active.Contains(m) ? (ActiveFiltered ? cAct : active) : cInact;

        public List<ModMetaData> Selection()
        {
            var l = new List<ModMetaData>();
            if (multi.Count > 0) l.AddRange(multi);
            if (selected != null && !l.Contains(selected)) l.Add(selected);
            return l;
        }

        private void KeyNav()
        {
            Event ev = Event.current;
            if (ev == null || ev.type != EventType.KeyDown) return;
            if (GUIUtility.keyboardControl != 0 || selected == null || dragging) return;
            KeyCode k = ev.keyCode;
            bool up = k == KeyCode.UpArrow || k == KeyCode.W, down = k == KeyCode.DownArrow || k == KeyCode.S;
            bool off = k == KeyCode.LeftArrow || k == KeyCode.A && !(ev.control || ev.command) || k == KeyCode.Delete;
            bool on = k == KeyCode.RightArrow || k == KeyCode.D;
            bool all = k == KeyCode.A && (ev.control || ev.command);
            if (k == KeyCode.Escape && multi.Count > 0) { multi.Clear(); ev.Use(); return; }
            if (!up && !down && !off && !on && !all) return;
            bool inActive = active.Contains(selected);
            List<ModMetaData> list = ListOf(selected);
            if (list == null) return;
            int i = list.IndexOf(selected);
            if (i < 0) return;
            if (all)
            {
                multi.Clear();
                foreach (ModMetaData m in list) multi.Add(m);
                ev.Use();
                return;
            }
            if (off || on)
            {
                List<ModMetaData> sel = Selection().Where(m => active.Contains(m) == off).ToList();
                if (sel.Count == 0) { ev.Use(); return; }
                // the cursor goes to the next mod that stays in this list
                ModMetaData next = list.Skip(i + 1).FirstOrDefault(m => !sel.Contains(m)) ?? list.Take(i).LastOrDefault(m => !sel.Contains(m));
                if (off) DeactivateMany(sel); else ActivateMany(sel);
                multi.Clear();
                selected = next ?? selected;
                SoundDefOf.Click.PlayOneShotOnCamera();
                ev.Use();
                return;
            }
            int dir = up ? -1 : 1;
            if (ev.shift)
            {
                if (!inActive || ActiveFiltered) return;   // only the full active list can be reordered
                MoveGroup(dir);
                SoundDefOf.Tick_Tiny.PlayOneShotOnCamera();
                i = active.IndexOf(selected);
            }
            else
            {
                int to = Mathf.Clamp(i + dir, 0, list.Count - 1);
                multi.Clear();
                if (to == i) { ev.Use(); return; }
                selected = list[to];
                i = to;
            }
            // keep the row in view
            float rowY = i * RowH, h = inActive ? hActive : hInactive;
            if (inActive)
            {
                if (rowY < sActive.y) sActive.y = rowY;
                else if (rowY + RowH > sActive.y + h) sActive.y = rowY + RowH - h;
            }
            else
            {
                if (rowY < sInactive.y) sInactive.y = rowY;
                else if (rowY + RowH > sInactive.y + h) sInactive.y = rowY + RowH - h;
            }
            ev.Use();
        }

        // ---- 0.3.1: several mods at once ----
        private void ActivateMany(List<ModMetaData> mods)
        {
            ModMetaData keep = selected;
            foreach (ModMetaData m in mods) Activate(m);
            selected = keep;
        }

        private void DeactivateMany(List<ModMetaData> mods)
        {
            int core = 0;
            foreach (ModMetaData m in mods)
            {
                if (m.IsCoreMod) { core++; continue; }
                active.Remove(m);
            }
            if (core > 0 && mods.Count == core) Messages.Message("MSM_CoreStays".Translate(), MessageTypeDefOf.RejectInput, false);
            dirty = true;
        }

        // one step up or down for every selected active mod, keeping their order; Core stays first
        private void MoveGroup(int dir)
        {
            var sel = new HashSet<ModMetaData>(Selection().Where(m => active.Contains(m) && !m.IsCoreMod));
            if (sel.Count == 0) return;
            if (dir < 0)
            {
                for (int i = 1; i < active.Count; i++)
                    if (sel.Contains(active[i]) && !sel.Contains(active[i - 1]) && !active[i - 1].IsCoreMod)
                    { ModMetaData t = active[i - 1]; active[i - 1] = active[i]; active[i] = t; }
            }
            else
            {
                for (int i = active.Count - 2; i >= 0; i--)
                    if (sel.Contains(active[i]) && !sel.Contains(active[i + 1]))
                    { ModMetaData t = active[i + 1]; active[i + 1] = active[i]; active[i] = t; }
            }
            dirty = true;
        }

        // the selected active mods as one block at a position (their order kept)
        private void MoveGroupTo(int index)
        {
            List<ModMetaData> sel = active.Where(m => Selection().Contains(m) && !m.IsCoreMod).ToList();
            if (sel.Count == 0) return;
            int before = active.Take(Mathf.Clamp(index, 0, active.Count)).Count(m => sel.Contains(m));
            foreach (ModMetaData m in sel) active.Remove(m);
            int at = Mathf.Clamp(index - before, 0, active.Count);
            // never above Core and the DLCs
            int firstFree = 0;
            while (firstFree < active.Count && (active[firstFree].IsCoreMod || active[firstFree].Official)) firstFree++;
            at = Mathf.Max(at, firstFree);
            active.InsertRange(at, sel);
            dirty = true;
        }

        private void DrawOrder(Rect body)
        {
            float detailW = Mathf.Max(340f, body.width * 0.3f);
            float colW = (body.width - detailW) / 2f;
            Rect left = new Rect(body.x, body.y, colW, body.height);
            Rect mid = new Rect(left.xMax, body.y, colW, body.height);
            Rect right = new Rect(mid.xMax, body.y, detailW, body.height);
            MUI.Fill(new Rect(left.xMax, body.y, 1f, body.height), Line);
            MUI.Fill(new Rect(mid.xMax, body.y, 1f, body.height), Line);

            // inactive
            Rect l = left.ContractedBy(Pad, 10f);
            Label(new Rect(l.x, l.y, l.width, 22f), "MSM_Inactive".Translate(inactiveCache.Count), MUI.Muted, GameFont.Tiny);
            string nf = SearchField(new Rect(l.x, l.y + 24f, l.width, 30f), fInactive, "MSM_SearchHint".Translate());
            if (nf != fInactive) fInactive = nf;
            if (cVer != ver || cInactF != fInactive || cActF != fActive || cProb != onlyProblems)
            {
                cInact = Filter(inactiveCache, fInactive);
                cAct = Filter(active, fActive);
                if (onlyProblems) cAct = cAct.Where(x => marks.ContainsKey(x)).ToList();
                cProb = onlyProblems;
                cInactF = fInactive;
                cActF = fActive;
                cVer = ver;
            }
            List<ModMetaData> inact = cInact;
            KeyNav();   // 0.2.2
            hInactive = l.height - 62f;
            hActive = mid.ContractedBy(Pad, 10f).height - 62f;
            DrawList(new Rect(l.x, l.y + 62f, l.width, l.height - 62f), inact, ref sInactive, false);

            // active
            Rect m = mid.ContractedBy(Pad, 10f);
            Label(new Rect(m.x, m.y, m.width, 22f), "MSM_Active".Translate(active.Count), MUI.Muted, GameFont.Tiny);
            // 0.1.2: only mods with a problem
            int nProb = nProbCache;
            string pl = "MSM_OnlyProblems".Translate(nProb);
            float pw = Mathf.Min(m.width * 0.45f, Text.CalcSize(pl).x + 24f);
            string af = SearchField(new Rect(m.x, m.y + 24f, m.width - pw - 6f, 30f), fActive, "MSM_SearchActiveHint".Translate());
            if (af != fActive) { fActive = af; dragFrom = -1; dragging = false; }
            if (Button(new Rect(m.xMax - pw, m.y + 24f, pw, 30f), pl.Truncate(pw - 8f), onlyProblems, nProb > 0 || onlyProblems))
            {
                onlyProblems = !onlyProblems;
                dragFrom = -1;
                dragging = false;
            }
            if (onlyProblems && nProb == 0) onlyProblems = false;
            List<ModMetaData> act = ActiveFiltered ? cAct : active;
            DrawList(new Rect(m.x, m.y + 62f, m.width, m.height - 62f), act, ref sActive, true);

            DrawDetail(right.ContractedBy(Pad, 10f));
        }

        private static List<ModMetaData> Filter(List<ModMetaData> list, string f)
        {
            if (f.NullOrEmpty()) return list;
            string q = f.ToLowerInvariant();
            return list.Where(m => (m.Name ?? "").ToLowerInvariant().Contains(q) || (m.AuthorsString ?? "").ToLowerInvariant().Contains(q) || m.PackageIdPlayerFacing.ToLowerInvariant().Contains(q)).ToList();
        }

        private void DrawList(Rect outRect, List<ModMetaData> list, ref Vector2 scroll, bool isActive)
        {
            bool canDrag = isActive && !ActiveFiltered;
            Rect view = new Rect(0f, 0f, outRect.width - 16f, Mathf.Max(list.Count * RowH, outRect.height - 2f));
            Widgets.BeginScrollView(outRect, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / RowH) - 1);
            int last = Mathf.Min(list.Count - 1, (int)((scroll.y + outRect.height) / RowH) + 1);
            Event ev = Event.current;
            for (int i = first; i <= last; i++)
            {
                ModMetaData mod = list[i];
                Rect row = new Rect(0f, i * RowH, view.width, RowH);
                bool sel = mod == selected || multi.Contains(mod);   // 0.3.1
                if (sel) { MUI.Fill(row, MUI.OnBg); MUI.Frame(row, mod == selected ? MUI.Accent : MUI.Border); }
                else if (Mouse.IsOver(row)) MUI.Fill(row, Hover);
                else if (i % 2 == 0) MUI.Fill(row, Row1);
                float x = 6f;
                if (isActive)
                {
                    int ix;
                    Label(new Rect(x, row.y, 34f, RowH), ((indexOf.TryGetValue(mod, out ix) ? ix : active.IndexOf(mod)) + 1).ToString(), new Color32(111, 119, 126, 255), GameFont.Tiny, TextAnchor.MiddleRight, false);
                    x += 40f;
                }
                DrawDot(new Rect(x, row.y, 8f, RowH), isActive ? Dot(mod) : (Rules.VersionOk(mod) ? MUI.Accent : MUI.Warn));
                x += 14f;
                Rect btn = new Rect(row.xMax - 24f, row.y + 3f, 20f, 20f);
                float nameW = row.width - x - 30f;
                if (NewMods.IsNew(mod))
                {
                    string nl = "MSM_New".Translate();
                    float nw = Text.CalcSize(nl).x * 0.8f + 14f;
                    Pill(new Rect(row.xMax - 30f - nw, row.y + 4f, nw, 18f), nl, MUI.Accent);
                    nameW -= nw + 6f;
                }
                string side = isActive ? ShortNote(mod) : (Rules.VersionOk(mod) ? "" : "MSM_OldShort".Translate(MSM.Versions(mod)).ToString());
                float sideW = side.NullOrEmpty() ? 0f : Mathf.Min(nameW * 0.45f, Text.CalcSize(side).x * 0.85f + 6f);
                Label(new Rect(x, row.y, nameW - sideW, RowH), mod.Name.Truncate(nameW - sideW), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
                if (sideW > 0f)
                    Label(new Rect(x + nameW - sideW, row.y, sideW, RowH), side.Truncate(sideW), isActive ? Dot(mod) : MUI.Warn, GameFont.Tiny, TextAnchor.MiddleRight, false);
                DateTime? addedAt = NewMods.AddedAt(mod);
                TooltipHandler.TipRegion(row, mod.Name + "\n" + mod.PackageIdPlayerFacing + (mod.AuthorsString.NullOrEmpty() ? "" : "\n" + mod.AuthorsString)
                    + (addedAt.HasValue ? "\n" + "MSM_Added".Translate(addedAt.Value.ToString("d.M.yyyy HH:mm")) : ""));

                // add / remove
                bool overBtn = Mouse.IsOver(btn);
                if (!(isActive && mod.IsCoreMod))
                {
                    MUI.Fill(btn, overBtn ? (isActive ? Err : MUI.Accent) : (Color)new Color32(44, 50, 56, 255), 4f);
                    Label(btn, isActive ? "−" : "+", MUI.Text1, GameFont.Small, TextAnchor.MiddleCenter, false);
                    if (overBtn && ev.type == EventType.MouseDown && ev.button == 0)
                    {
                        if (isActive) Deactivate(mod); else Activate(mod);
                        SoundDefOf.Click.PlayOneShotOnCamera();
                        ev.Use();
                        break;
                    }
                }

                if (ev.type == EventType.MouseDown && ev.button == 0 && Mouse.IsOver(row))
                {
                    // 0.3.1: Ctrl+click adds or removes a mod, Shift+click selects everything between
                    if ((ev.control || ev.command) && ev.clickCount == 1)
                    {
                        if (multi.Count == 0 && selected != null && list.Contains(selected)) multi.Add(selected);
                        if (!multi.Remove(mod)) multi.Add(mod);
                        selected = mod;
                        ev.Use();
                        break;
                    }
                    if (ev.shift && ev.clickCount == 1 && selected != null && list.Contains(selected))
                    {
                        int a0 = list.IndexOf(selected);
                        multi.Clear();
                        for (int q = Mathf.Min(a0, i); q <= Mathf.Max(a0, i); q++) multi.Add(list[q]);
                        ev.Use();
                        break;
                    }
                    if (!(multi.Count > 1 && multi.Contains(mod) && canDrag)) multi.Clear();
                    selected = mod;
                    if (ev.clickCount == 2)
                    {
                        if (isActive) Deactivate(mod); else Activate(mod);
                        ev.Use();
                        break;
                    }
                    else if (canDrag)
                    {
                        dragFrom = i;
                        dragStartY = ev.mousePosition.y;
                        dragging = false;
                    }
                    ev.Use();
                }
            }

            if (canDrag && dragFrom >= 0)
            {
                if (ev.type == EventType.MouseDrag && Mathf.Abs(ev.mousePosition.y - dragStartY) > 5f)
                {
                    dragging = true;
                    if (ev.mousePosition.y < scroll.y + 24f) scroll.y = Mathf.Max(0f, scroll.y - 12f);
                    else if (ev.mousePosition.y > scroll.y + outRect.height - 24f) scroll.y += 12f;
                    ev.Use();
                }
                int target = Mathf.Clamp(Mathf.RoundToInt(ev.mousePosition.y / RowH), 0, list.Count);
                if (dragging && ev.type == EventType.Repaint)
                {
                    MUI.Fill(new Rect(0f, target * RowH - 1f, view.width, 3f), MUI.Accent);
                    if (dragFrom < list.Count)
                        Label(new Rect(view.width * 0.35f, ev.mousePosition.y - 12f, view.width * 0.65f, 24f), list[dragFrom].Name, MUI.Accent, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                }
                if (ev.rawType == EventType.MouseUp)
                {
                    if (dragging)
                    {
                        if (multi.Count > 1 && dragFrom < list.Count && multi.Contains(list[dragFrom])) MoveGroupTo(target);   // 0.3.1
                        else Move(dragFrom, target > dragFrom ? target - 1 : target);
                    }
                    dragFrom = -1;
                    dragging = false;
                }
            }
            Widgets.EndScrollView();
        }

        private string ShortNote(ModMetaData m)
        {
            List<KeyValuePair<Sev, string>> l;
            if (!notes.TryGetValue(m, out l) || l.Count == 0) return "";
            return l.OrderByDescending(x => x.Key).First().Value;
        }

        // 0.3.1: several mods selected – one panel for all of them
        private void DrawGroup(Rect r, List<ModMetaData> sel)
        {
            float y = r.y;
            Label(new Rect(r.x, y, r.width, 30f), "MSM_GroupTitle".Translate(sel.Count), MUI.Text1, GameFont.Medium, TextAnchor.MiddleLeft, false);
            y += 34f;
            int nAct = sel.Count(x => active.Contains(x)), nIn = sel.Count - nAct;
            foreach (ModMetaData x in sel.OrderBy(x => active.Contains(x) ? active.IndexOf(x) : 100000).Take(8))
            {
                Label(new Rect(r.x, y, r.width, 20f), "• " + x.Name.Truncate(r.width - 14f), active.Contains(x) ? MUI.Text1 : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                y += 20f;
            }
            if (sel.Count > 8) { Label(new Rect(r.x, y, r.width, 20f), "MSM_GroupMore".Translate(sel.Count - 8), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false); y += 20f; }
            y += 8f;
            float bw = (r.width - 8f) / 2f;
            if (Button(new Rect(r.x, y, bw, 30f), "MSM_GroupOn".Translate(nIn), true, nIn > 0)) ActivateMany(sel.Where(x => !active.Contains(x)).ToList());
            if (Button(new Rect(r.x + bw + 8f, y, bw, 30f), "MSM_GroupOff".Translate(nAct), false, nAct > 0)) DeactivateMany(sel.Where(x => active.Contains(x)).ToList());
            y += 36f;
            if (nAct > 0 && !ActiveFiltered)
            {
                float qw = (r.width - 24f) / 4f;
                if (Button(new Rect(r.x, y, qw, 28f), "MSM_Top".Translate())) MoveGroupTo(0);
                if (Button(new Rect(r.x + (qw + 8f), y, qw, 28f), "▲")) MoveGroup(-1);
                if (Button(new Rect(r.x + 2 * (qw + 8f), y, qw, 28f), "▼")) MoveGroup(1);
                if (Button(new Rect(r.x + 3 * (qw + 8f), y, qw, 28f), "MSM_Bottom".Translate())) MoveGroupTo(active.Count);
                y += 34f;
            }
            // 0.3.2: remove several mods at once (unsubscribe / move away / delete)
            List<ModMetaData> removable = sel.Where(x => !x.Official && !x.IsCoreMod).ToList();
            if (removable.Count > 0)
            {
                Rect rr = new Rect(r.x, y, r.width, 28f);
                if (Button(rr, "MSM_GroupRemove".Translate(removable.Count))) RemoveManyMenu(removable);
                TooltipHandler.TipRegion(rr, "MSM_RemoveTip".Translate());
                y += 34f;
            }
            if (Button(new Rect(r.x, y, r.width, 28f), "MSM_GroupClear".Translate())) multi.Clear();
            y += 40f;
            float hh = Text.CalcHeight("MSM_KeysHelp".Translate(), r.width);
            Label(new Rect(r.x, y, r.width, hh + 40f), "MSM_KeysHelp".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
        }

        private void DrawDetail(Rect r)
        {
            List<ModMetaData> group = Selection();
            if (group.Count > 1) { DrawGroup(r, group); return; }   // 0.3.1
            ModMetaData m = selected;
            if (m == null)
            {
                Label(r, "MSM_SelectHint".Translate(), MUI.Muted, GameFont.Small, TextAnchor.MiddleCenter);
                return;
            }
            float y = r.y;
            Label(new Rect(r.x, y, r.width, 30f), m.Name, MUI.Text1, GameFont.Medium, TextAnchor.MiddleLeft, false);
            y += 30f;
            Label(new Rect(r.x, y, r.width, 20f), m.AuthorsString, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            y += 22f;
            string info = "MSM_Info".Translate(m.ModVersion.NullOrEmpty() ? "?" : m.ModVersion, MSM.Versions(m), MSM.SourceLabel(m));
            Label(new Rect(r.x, y, r.width, 20f), info, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            y += 20f;
            Label(new Rect(r.x, y, r.width, 20f), m.PackageIdPlayerFacing + " · " + m.RootDir.Name, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            y += 20f;
            DateTime? addedAt = NewMods.AddedAt(m);
            if (addedAt.HasValue)
            {
                bool isNew = NewMods.IsNew(m);
                Label(new Rect(r.x, y, r.width, 20f), (isNew ? "● " : "") + "MSM_Added".Translate(addedAt.Value.ToString("d.M.yyyy HH:mm")), isNew ? MUI.Accent : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                y += 20f;
            }
            // 0.2.0: updated since the last start, and what the mod adds to loading
            ModChange chg = ModChanges.Of(m);
            if (chg != null)
            {
                Rect cr = new Rect(r.x, y, r.width, 20f);
                string ver = chg.from == chg.to || chg.to.NullOrEmpty() ? "" : " " + (chg.from.NullOrEmpty() ? "?" : chg.from) + " → " + chg.to;
                Label(cr, "● " + "MSM_ChangedSince".Translate(ver), Info, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                Widgets.DrawHighlightIfMouseover(cr);
                if (Widgets.ButtonInvisible(cr)) Find.WindowStack.Add(new Dialog_Changes(m));
                y += 20f;
            }
            if (!m.Official)
            {
                Rect wr = new Rect(r.x, y, r.width, 20f);
                Label(wr, LoadWeight.Line(m), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                Widgets.DrawHighlightIfMouseover(wr);
                TooltipHandler.TipRegion(wr, "MSM_WeightTip".Translate());
                if (Widgets.ButtonInvisible(wr)) Find.WindowStack.Add(new Dialog_LoadWeight(active));
                y += 20f;
            }
            y += 6f;

            // notes
            List<KeyValuePair<Sev, string>> l;
            if (notes.TryGetValue(m, out l))
                foreach (var n in l.OrderByDescending(x => x.Key))
                {
                    float h = Mathf.Max(22f, Text.CalcHeight(n.Value, r.width - 16f) + 2f);
                    DrawDot(new Rect(r.x, y + 2f, 8f, 18f), SevColor(n.Key));
                    Label(new Rect(r.x + 14f, y, r.width - 14f, h), n.Value, SevColor(n.Key), GameFont.Small);
                    y += h;
                }
            y += 6f;

            // buttons
            bool isActive = active.Contains(m);
            float bw = (r.width - 8f) / 2f;
            if (!m.IsCoreMod && Button(new Rect(r.x, y, bw, 30f), isActive ? "MSM_Deactivate".Translate() : "MSM_ActivateShort".Translate(), !isActive))
            {
                if (isActive) Deactivate(m); else Activate(m);
            }
            if (Button(new Rect(r.x + bw + 8f, y, bw, 30f), "MSM_Folder".Translate())) MSM.OpenFolder(m.RootDir.FullName);
            y += 36f;
            // 0.2.1: get rid of a mod – unsubscribe (Workshop) or move away / delete (local)
            if (!m.Official && !m.IsCoreMod)
            {
                Rect rr = new Rect(r.x, y, r.width, 28f);
                if (Button(rr, "MSM_Remove".Translate())) RemoveMenu(m);
                TooltipHandler.TipRegion(rr, "MSM_RemoveTip".Translate());
                y += 34f;
            }
            if (isActive)
            {
                int idx = active.IndexOf(m);
                float qw = (r.width - 24f) / 4f;
                if (Button(new Rect(r.x, y, qw, 28f), "MSM_Top".Translate(), false, idx > 0)) Move(idx, 0);
                if (Button(new Rect(r.x + (qw + 8f), y, qw, 28f), "▲", false, idx > 0)) Move(idx, idx - 1);
                if (Button(new Rect(r.x + 2 * (qw + 8f), y, qw, 28f), "▼", false, idx < active.Count - 1)) Move(idx, idx + 1);
                if (Button(new Rect(r.x + 3 * (qw + 8f), y, qw, 28f), "MSM_Bottom".Translate(), false, idx < active.Count - 1)) Move(idx, active.Count - 1);
                y += 34f;
            }
            // 0.3.0: the player's load order rules for this mod, and which rule moved it in the last sort
            if (!m.Official) y = DrawRules(r, y, m);
            if (MSM.CanOpenPage(m))
            {
                if (Button(new Rect(r.x, y, r.width, 28f), m.OnSteamWorkshop ? "MSM_Workshop".Translate() : "MSM_Web".Translate())) MSM.OpenPage(m);
                y += 34f;
            }
            Mod handle = MSM.SettingsHandle(m);
            bool upload = !InGame && MSM.CanUpload(m);
            if (handle != null || upload)
            {
                float hw = handle != null && upload ? (r.width - 8f) / 2f : r.width;
                float hx = r.x;
                if (handle != null)
                {
                    if (Button(new Rect(hx, y, hw, 28f), "MSM_ModSettings".Translate())) Find.WindowStack.Add(new Dialog_ModSettings(handle));
                    hx += hw + 8f;
                }
                if (upload && Button(new Rect(hx, y, hw, 28f), "MSM_Upload".Translate(), true)) MSM.Upload(m);
                y += 34f;
            }

            // preview + description
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            if (outR.height < 40f) return;
            Texture2D pic = null;
            try { pic = m.PreviewImage; if (pic != null) previewLoaded.Add(m); } catch { }
            float picH = pic != null ? (r.width - 16f) * pic.height / Mathf.Max(1f, pic.width) : 0f;
            string desc = Requirements(m) + (m.Description ?? "");
            float textH = Text.CalcHeight(desc, r.width - 16f);
            Rect view = new Rect(0f, 0f, r.width - 16f, picH + (picH > 0 ? 8f : 0f) + textH + 10f);
            Widgets.BeginScrollView(outR, ref sDetail, view);
            if (pic != null) GUI.DrawTexture(new Rect(0f, 0f, view.width, picH), pic, ScaleMode.ScaleToFit);
            Label(new Rect(0f, picH + (picH > 0 ? 8f : 0f), view.width, textH + 4f), desc, new Color32(200, 204, 208, 255), GameFont.Small, TextAnchor.UpperLeft);
            Widgets.EndScrollView();
        }

        // 0.3.0: rule editor – one line per rule with ✕, "+ Rule" opens a menu; plus "works in this version"
        private float DrawRules(Rect r, float y, ModMetaData m)
        {
            List<string> moved;
            if (Sorter.LastMoved.TryGetValue(m, out moved) && moved.Count > 0)
            {
                string t = "MSM_RuleMoved".Translate(string.Join("; ", moved.ToArray()));
                float h = Mathf.Max(20f, Text.CalcHeight(t, r.width) + 2f);
                Label(new Rect(r.x, y, r.width, h), t, Info, GameFont.Tiny, TextAnchor.UpperLeft, true);
                y += h + 2f;
            }
            OrderRule remove = null;
            foreach (OrderRule rule in Rules.UserFor(m))
            {
                Label(new Rect(r.x, y, r.width - 30f, 24f), "MSM_RuleSec".Translate() + ": " + rule.Describe(), MUI.Text1, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                if (Button(new Rect(r.xMax - 26f, y, 26f, 24f), "✕")) remove = rule;
                y += 26f;
            }
            if (remove != null) Rules.RemoveUser(remove);
            float bw = (r.width - 8f) / 2f;
            Rect add = new Rect(r.x, y, bw, 28f);
            if (Button(add, "MSM_RuleAdd".Translate())) RuleMenu(m);
            TooltipHandler.TipRegion(add, "MSM_RuleAddTip".Translate());
            if (!m.VersionCompatible)
            {
                bool user = Rules.UserNoWarn(m);
                Rect nw = new Rect(r.x + bw + 8f, y, bw, 28f);
                if (Button(nw, user ? "MSM_NoWarnUndo".Translate() : "MSM_NoWarn".Translate())) { Rules.SetUserNoWarn(m, !user); dirty = true; }
                TooltipHandler.TipRegion(nw, "MSM_NoWarnTip".Translate());
            }
            return y + 34f;
        }

        private void RuleMenu(ModMetaData m)
        {
            string id = Rules.Id(m);
            var opts = new List<FloatMenuOption>
            {
                new FloatMenuOption("MSM_RuleTop".Translate().CapitalizeFirst(), () => Rules.AddUser(new OrderRule { mod = id, kind = "top" })),
                new FloatMenuOption("MSM_RuleBottom".Translate().CapitalizeFirst(), () => Rules.AddUser(new OrderRule { mod = id, kind = "bottom" }))
            };
            foreach (string kind in new[] { "after", "before" })
            {
                string k = kind;
                opts.Add(new FloatMenuOption((k == "after" ? "MSM_RuleAfter" : "MSM_RuleBefore").Translate("…").CapitalizeFirst(), () =>
                {
                    var sub = new List<FloatMenuOption>();
                    foreach (ModMetaData o in active.Where(x => x != m).OrderBy(x => x.Name))
                    {
                        string oid = Rules.Id(o);
                        sub.Add(new FloatMenuOption(o.Name, () => Rules.AddUser(new OrderRule { mod = id, kind = k, other = oid })));
                    }
                    if (sub.Count > 0) Find.WindowStack.Add(new FloatMenu(sub));
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // 0.1.7: what the mod needs and who needs it (shown above the description)
        private string Requirements(ModMetaData m)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                var deps = m.Dependencies.Where(d => d != null && !d.packageId.NullOrEmpty()).ToList();
                sb.Append("<b>" + "MSM_Requires".Translate() + "</b> ");
                if (deps.Count == 0) sb.Append("MSM_ReqNone".Translate());
                sb.Append("\n");
                foreach (ModDependency d in deps)
                {
                    ModMetaData dm = MSM.FindMod(d.packageId);
                    string name = dm?.Name ?? d.displayName ?? d.packageId;
                    if (dm != null && active.Contains(dm)) sb.Append("<color=#7CBF5E>✓</color> " + name + "\n");
                    else if (dm != null) sb.Append("<color=#E0A24A>○</color> " + name + " – " + "MSM_ReqInactive".Translate() + "\n");
                    else sb.Append("<color=#E06A5A>✗</color> " + name + " – " + "MSM_ReqMissing".Translate() + "\n");
                }
                var by = active.Where(x => x != m && x.Dependencies.Any(d => d != null && m.SamePackageId(d.packageId, true))).Select(x => x.Name).ToList();
                if (by.Count > 0)
                    sb.Append("<b>" + "MSM_RequiredBy".Translate(by.Count) + "</b> " + string.Join(", ", by.Take(12).ToArray()) + (by.Count > 12 ? " …" : "") + "\n");
                return sb.Append("\n").ToString();
            }
            catch { return ""; }
        }

        private static float SameExplainH(float w)
        {
            Text.Font = GameFont.Small;
            return Text.CalcHeight("MSM_SameExplain".Translate(), w - 20f) + 14f;
        }

        // ================= tab: problems =================
        private void DrawProblems(Rect body)
        {
            float sideW = 330f;
            Rect left = new Rect(body.x, body.y, body.width - sideW, body.height).ContractedBy(Pad, 10f);
            Rect side = new Rect(body.xMax - sideW, body.y, sideW, body.height).ContractedBy(Pad, 10f);
            MUI.Fill(new Rect(body.xMax - sideW, body.y, 1f, body.height), Line);

            string[] groupKeys = { "MSM_GrpDup", "MSM_GrpBreak", "MSM_GrpSame", "MSM_GrpLog" };
            Color[] groupCol = { Err, MUI.Warn, Info, MUI.Warn };
            float cardW = left.width - 16f;
            // measure
            float total = 0f;
            for (int g = 0; g < 4; g++)
            {
                total += 32f;
                List<Issue> gi = issues.Where(i => i.group == g).ToList();
                if (gi.Count == 0) { total += 30f; continue; }
                if (collapsed[g]) continue;
                if (g == 2) total += SameExplainH(cardW) + 8f;
                foreach (Issue i in gi) total += Height(i, cardW) + 8f;
            }
            Rect view = new Rect(0f, 0f, cardW, Mathf.Max(total + 10f, left.height - 2f));
            Widgets.BeginScrollView(left, ref sProblems, view);
            float y = 0f;
            Action pending = null;
            for (int g = 0; g < 4; g++)
            {
                List<Issue> gi = issues.Where(i => i.group == g).ToList();
                Rect hr = new Rect(0f, y + 6f, cardW, 24f);
                DrawDot(hr, groupCol[g]);
                string head = groupKeys[g].Translate().ToString().ToUpperInvariant() + " · " + gi.Count + (gi.Count > 0 ? (collapsed[g] ? "  ▸" : "  ▾") : "");
                Label(new Rect(16f, hr.y, cardW - 16f, 24f), head, groupCol[g], GameFont.Small, TextAnchor.MiddleLeft, false);
                if (g == 2)
                {
                    Label(new Rect(16f + Text.CalcSize(head).x + 16f, hr.y, 360f, 24f), "MSM_SameHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                    // 0.2.1: pairs hidden with "works for me" can be shown again
                    if (hiddenSameCount > 0)
                    {
                        string hl = showHiddenSame ? "MSM_SameHideHidden".Translate().ToString() : "MSM_SameShowHidden".Translate(hiddenSameCount).ToString();
                        Text.Font = GameFont.Tiny;
                        float bw = Text.CalcSize(hl).x + 20f;
                        Text.Font = GameFont.Small;
                        if (Button(new Rect(hr.xMax - bw, hr.y + 1f, bw, 22f), hl))
                        {
                            showHiddenSame = !showHiddenSame;
                            dirty = true;
                        }
                    }
                }
                if (gi.Count > 0 && Widgets.ButtonInvisible(hr)) collapsed[g] = !collapsed[g];
                y += 32f;
                if (gi.Count == 0)
                {
                    Label(new Rect(16f, y, cardW, 24f), "MSM_None".Translate(), MUI.Accent, GameFont.Small);
                    y += 30f;
                    continue;
                }
                if (collapsed[g]) continue;
                if (g == 2)
                {
                    // 0.2.1: these are not duplicates – explain before the list (people removed mods they needed)
                    float eh = SameExplainH(cardW);
                    Rect er = new Rect(0f, y, cardW, eh);
                    MUI.Fill(er, new Color(Info.r, Info.g, Info.b, 0.08f));
                    MUI.Fill(new Rect(er.x, er.y, 3f, er.height), Info);
                    Label(new Rect(er.x + 12f, er.y + 6f, er.width - 20f, er.height - 12f), "MSM_SameExplain".Translate(), MUI.Muted, GameFont.Small, TextAnchor.UpperLeft, true);
                    y += eh + 8f;
                }
                foreach (Issue i in gi)
                {
                    float h = Height(i, cardW);
                    Action a = DrawCard(new Rect(0f, y, cardW, h), i);
                    if (a != null) pending = a;
                    y += h + 8f;
                }
            }
            Widgets.EndScrollView();
            if (pending != null) { pending(); dirty = true; }

            // summary – in a scroll view, long translations must not push the buttons off the window
            Rect sideOut = side;
            Rect sideView = new Rect(0f, 0f, side.width - 16f, Mathf.Max(sideH, side.height - 2f));
            Widgets.BeginScrollView(sideOut, ref sSide, sideView);
            side = new Rect(0f, 0f, sideView.width, sideView.height);
            float sy = side.y;
            Label(new Rect(side.x, sy, side.width, 22f), "MSM_Summary".Translate(), MUI.Muted, GameFont.Tiny);
            sy += 26f;
            int compat = active.Count(m => Rules.VersionOk(m));
            string cur = VersionControl.CurrentMajor + "." + VersionControl.CurrentMinor;
            sy += SideCard(new Rect(side.x, sy, side.width, 0f), "MSM_SumActive".Translate(active.Count, cur),
                "MSM_SumCompat".Translate(compat, cur) + "\n" + "MSM_SumOld".Translate(active.Count - compat)) + 10f;
            sy += SideCard(new Rect(side.x, sy, side.width, 0f), "MSM_SumRestartTitle".Translate(),
                InGame ? "MSM_SumRestartGame".Translate() : "MSM_SumRestartMenu".Translate()) + 10f;
            sy += SideCard(new Rect(side.x, sy, side.width, 0f), "MSM_SumBackupTitle".Translate(), "MSM_SumBackup".Translate()) + 10f;
            if (Button(new Rect(side.x, sy, side.width, 34f), "MSM_SmartSort".Translate(), true)) SmartSort();
            sy += 44f;

            // 0.1.2: find the faulty mod by halving
            sy += SideCard(new Rect(side.x, sy, side.width, 0f), "MSM_BisectCardTitle".Translate(), "MSM_BisectCardText".Translate()) + 10f;
            Rect bb = new Rect(side.x, sy, side.width, 34f);
            bool canBisect = !InGame && !Bisect.Active;
            if (Button(bb, "MSM_BisectStart".Translate(), false, canBisect)) StartBisect();
            if (InGame) TooltipHandler.TipRegion(bb, "MSM_BisectMenuOnly".Translate());
            sideH = sy + 44f;
            Widgets.EndScrollView();
        }

        private Vector2 sSide;
        private float sideH = 600f;

        private void StartBisect()
        {
            List<ModMetaData> sus = Bisect.Suspects(active);
            if (sus.Count < 2)
            {
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_BisectFew".Translate()));
                return;
            }
            int restarts = Mathf.CeilToInt(Mathf.Log(sus.Count, 2f));
            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_BisectConfirm".Translate(sus.Count, restarts), delegate
            {
                closeConfirmed = true;
                Bisect.Start(active.ToList());
            }, true));
        }

        private float SideCard(Rect r, string title, string text)
        {
            float th = Text.CalcHeight(text, r.width - 24f);
            Rect c = new Rect(r.x, r.y, r.width, 40f + th);
            MUI.Card(c);
            Label(new Rect(c.x + 12f, c.y + 8f, c.width - 24f, 24f), title, MUI.Text1, GameFont.Small);
            Label(new Rect(c.x + 12f, c.y + 32f, c.width - 24f, th + 2f), text, new Color32(168, 174, 181, 255), GameFont.Small, TextAnchor.UpperLeft);
            return c.height;
        }

        private const float CardBtnW = 190f;

        private float Height(Issue i, float w)
        {
            if (w != heightsW) { heights.Clear(); heightsW = w; }
            float h;
            if (!heights.TryGetValue(i, out h)) heights[i] = h = CardHeight(i, w);
            return h;
        }

        private static float CardHeight(Issue i, float w)
        {
            float textW = w - 28f - (i.actions.Count > 0 ? CardBtnW + 12f : 0f);
            Text.Font = GameFont.Small;
            float th = Text.CalcHeight(i.text ?? "", textW);
            float left = 12f + 24f + th + 12f;
            float right = 12f + i.actions.Count * 34f + 8f;
            return Mathf.Max(left, right);
        }

        private Action DrawCard(Rect r, Issue i)
        {
            MUI.Fill(r, MUI.CardBg, 4f);
            MUI.Frame(r, MUI.Border, 1f, 4f);
            MUI.Fill(new Rect(r.x, r.y, 3f, r.height), SevColor(i.sev));
            float textW = r.width - 28f - (i.actions.Count > 0 ? CardBtnW + 12f : 0f);
            Label(new Rect(r.x + 14f, r.y + 10f, textW, 24f), i.title, MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
            float th = Text.CalcHeight(i.text ?? "", textW);
            Label(new Rect(r.x + 14f, r.y + 36f, textW, th + 2f), i.text ?? "", new Color32(168, 174, 181, 255), GameFont.Small, TextAnchor.UpperLeft);
            Action res = null;
            float by = r.y + 12f;
            foreach (var a in i.actions)
            {
                if (Button(new Rect(r.xMax - 12f - CardBtnW, by, CardBtnW, 28f), a.Key.Truncate(CardBtnW - 12f)))
                    res = a.Value;
                by += 34f;
            }
            return res;
        }

        // ================= tab: profiles =================
        // ================= tab: updates (Steam Workshop) =================
        private Vector2 sUpdates;
        private float lastUpdRefresh;

        // ---- 0.3.1: replacements tab ----
        private List<ReplaceRow> repRows;
        private float repAt = -100f;
        private Vector2 sReplace;

        private int ReplaceCount()
        {
            if (repRows == null || Time.realtimeSinceStartup - repAt > 5f)
            {
                repAt = Time.realtimeSinceStartup;
                try { repRows = Replacements.Find(active, blames, MSMMod.Settings.replaceHidden); }
                catch (Exception e) { repRows = new List<ReplaceRow>(); Log.WarningOnce("[Mod Sort Manager] replacements: " + e.Message, 0x3A5E01); }
            }
            return repRows.Count(r => r.oldActive);
        }

        private static ModMetaData FindInstalled(ReplaceRow r)
        {
            if (r.newMod != null) return r.newMod;
            ModMetaData m = ModLister.AllInstalledMods.FirstOrDefault(x => x != r.oldMod && Replacements.IdOf(x) == r.rule.newId);
            if (m == null && r.rule.newPackage.Length > 0)
                m = ModLister.AllInstalledMods.FirstOrDefault(x => x != r.oldMod && string.Equals(x.PackageIdNonUnique, r.rule.newPackage, StringComparison.OrdinalIgnoreCase) && Replacements.IdOf(x) != Replacements.IdOf(r.oldMod));
            return m;
        }

        // the new mod takes the old one's place in the load order
        private void SwapIn(ReplaceRow r, ModMetaData nm)
        {
            int i = active.IndexOf(r.oldMod);
            if (active.Contains(nm)) { if (i >= 0) active.RemoveAt(i); }
            else if (i >= 0) active[i] = nm;
            else active.Add(nm);
            selected = nm;
            multi.Clear();
            dirty = true;
            repRows = null;
            Messages.Message("MSM_RepDone".Translate(r.oldMod.Name, nm.Name), MessageTypeDefOf.PositiveEvent, false);
        }

        private void DrawReplace(Rect body)
        {
            Rect r = body.ContractedBy(Pad, 10f);
            ReplaceCount();
            float y = r.y;
            Label(new Rect(r.x, y, r.width - 220f, 22f), "MSM_RepTitle".Translate(repRows.Count), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
            if (Button(new Rect(r.xMax - 200f, y - 4f, 200f, 32f), "MSM_RepReload".Translate()))
            {
                Replacements.Reload();
                repRows = null;
            }
            y += 30f;
            string src = Replacements.SourceInfo;
            Label(new Rect(r.x, y, r.width, 40f), "MSM_RepHint".Translate().ToString() + (src.NullOrEmpty() ? "" : "\n" + "MSM_RepSource".Translate(src).ToString()), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            y += 42f;
            if (!Replacements.HasUti)
            {
                Rect note = new Rect(r.x, y, r.width, 54f);
                MUI.Fill(note, Row1, 6f);
                Label(new Rect(note.x + 12f, note.y, note.width - 250f, note.height), "MSM_RepNoUti".Translate(), MUI.Warn, GameFont.Small, TextAnchor.MiddleLeft);
                Rect b = new Rect(note.xMax - 230f, note.y + 11f, 218f, 32f);
                if (Updates.CanSubscribe)
                {
                    if (Button(b, "MSM_RepGetUti".Translate(), true) && Updates.Subscribe(3396308787UL))
                        Messages.Message("MSM_RepSubscribed".Translate("Use This Instead"), MessageTypeDefOf.PositiveEvent, false);
                }
                else if (Button(b, "MSM_Workshop".Translate()))
                    Application.OpenURL("https://steamcommunity.com/sharedfiles/filedetails/?id=3396308787");
                y += 62f;
            }
            const float H = 58f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(repRows.Count * (H + 4f), outR.height - 2f));
            Widgets.BeginScrollView(outR, ref sReplace, view);
            ReplaceRow act = null; string what = null;
            for (int i = 0; i < repRows.Count; i++)
            {
                ReplaceRow rr = repRows[i];
                Rect row = new Rect(0f, i * (H + 4f), view.width, H);
                if (row.yMax < sReplace.y || row.y > sReplace.y + outR.height) continue;
                MUI.Fill(row, i % 2 == 0 ? Row1 : Bg, 6f);
                Color c = rr.oldActive ? (rr.newForThisVersion && !rr.oldForThisVersion || rr.errors > 0 ? MUI.Warn : Info) : MUI.Muted;
                DrawDot(new Rect(row.x + 8f, row.y, 8f, row.height), c);
                float tw = row.width - 24f - 420f;
                string newName = rr.rule.newName.NullOrEmpty() ? rr.rule.newId : rr.rule.newName;
                if (!rr.rule.newAuthor.NullOrEmpty()) newName += " (" + rr.rule.newAuthor + ")";
                Label(new Rect(row.x + 24f, row.y + 4f, tw, 24f), (rr.oldMod.Name + "  →  " + newName).Truncate(tw), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
                var why = new List<string>();
                if (!rr.oldActive) why.Add("MSM_RepInactive".Translate());
                if (!rr.oldForThisVersion && rr.newForThisVersion) why.Add("MSM_RepVersion".Translate(Replacements.Cur));
                else if (rr.newForThisVersion) why.Add("MSM_RepBoth".Translate(Replacements.Cur));
                if (rr.errors > 0) why.Add("MSM_RepErrors".Translate(rr.errors));
                if (!rr.rule.why.NullOrEmpty()) why.Add(rr.rule.why);
                why.Add(FindInstalled(rr) != null ? "MSM_RepHave".Translate().ToString() : "MSM_RepNotHave".Translate().ToString());
                Label(new Rect(row.x + 24f, row.y + 28f, tw, 24f), string.Join(" · ", why).Truncate(tw), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
                TooltipHandler.TipRegion(new Rect(row.x, row.y, tw + 24f, row.height), rr.oldMod.Name + "\n→ " + newName + "\n\n" + string.Join("\n", why) + "\n\n" + "MSM_RepFrom".Translate(rr.rule.source));
                float bx = row.xMax - 8f;
                Rect bHide = new Rect(bx - 90f, row.y + 14f, 86f, 30f); bx -= 94f;
                Rect bPage = new Rect(bx - 110f, row.y + 14f, 106f, 30f); bx -= 114f;
                Rect bMain = new Rect(bx - 200f, row.y + 14f, 196f, 30f);
                if (Button(bHide, "MSM_RepHide".Translate())) { act = rr; what = "hide"; }
                if (Button(bPage, "MSM_Workshop".Translate())) Application.OpenURL("https://steamcommunity.com/sharedfiles/filedetails/?id=" + rr.rule.newId);
                ModMetaData have = FindInstalled(rr);
                if (have != null)
                {
                    if (rr.oldActive && Button(bMain, "MSM_RepSwap".Translate(), true)) { act = rr; what = "swap"; }
                }
                else if (Updates.CanSubscribe)
                {
                    if (Button(bMain, "MSM_RepSubscribe".Translate(), true)) { act = rr; what = "sub"; }
                }
            }
            if (repRows.Count == 0)
                Label(new Rect(0f, 0f, view.width, 30f), Replacements.Rules.Count == 0 ? "MSM_RepNoList".Translate() : "MSM_RepNone".Translate(), Replacements.Rules.Count == 0 ? MUI.Muted : MUI.Accent);
            Widgets.EndScrollView();
            if (act != null)
            {
                if (what == "hide")
                {
                    string id = Replacements.IdOf(act.oldMod);
                    MSMMod.Settings.replaceHidden.Add(id + ">" + act.rule.newId);
                    MSMMod.Settings.Write();
                    repRows = null;
                }
                else if (what == "swap") SwapIn(act, FindInstalled(act));
                else if (what == "sub" && ulong.TryParse(act.rule.newId, out ulong nid) && Updates.Subscribe(nid))
                    Messages.Message("MSM_RepSubscribed".Translate(act.rule.newName), MessageTypeDefOf.PositiveEvent, false);
            }
        }

        private void DrawUpdates(Rect body)
        {
            if (Time.realtimeSinceStartup - lastUpdRefresh > 1f)
            {
                lastUpdRefresh = Time.realtimeSinceStartup;
                Updates.Refresh();
                if (Updates.Finished > 0) needsRestart = true;
            }
            Rect r = body.ContractedBy(Pad, 10f);
            int nChanged = ModChanges.Changed.Count;
            if (!Updates.Available)
            {
                Label(new Rect(r.x, r.y, r.width, 60f), "MSM_UpdNoSteam".Translate(), MUI.Muted);
                if (nChanged > 0 && Button(new Rect(r.x, r.y + 64f, 220f, 32f), "MSM_Changes".Translate(nChanged)))
                    Find.WindowStack.Add(new Dialog_Changes());
                return;
            }
            if (Button(new Rect(r.xMax - 620f, r.y - 4f, 200f, 32f), "MSM_Changes".Translate(nChanged), false, nChanged > 0))
                Find.WindowStack.Add(new Dialog_Changes());
            float y = r.y;
            Label(new Rect(r.x, y, r.width - 630f, 22f), "MSM_UpdTitle".Translate(Updates.Pending, Updates.Busy), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
            if (Button(new Rect(r.xMax - 200f, y - 4f, 200f, 32f), "MSM_UpdAll".Translate(Updates.Pending), true) && Updates.Pending > 0)
                Updates.DownloadAll();
            if (Button(new Rect(r.xMax - 410f, y - 4f, 200f, 32f), "MSM_UpdCheck".Translate()))
                Updates.Refresh(true);
            y += 30f;
            Label(new Rect(r.x, y, r.width, 40f), "MSM_UpdHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            y += 40f;
            List<UpdRow> rows = Updates.Rows;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows.Count * 34f, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref sUpdates, view);
            int first = Mathf.Max(0, (int)(sUpdates.y / 34f) - 1), last = Mathf.Min(rows.Count - 1, (int)((sUpdates.y + outR.height) / 34f) + 1);
            for (int i = first; i <= last; i++)
            {
                UpdRow u = rows[i];
                Rect row = new Rect(0f, i * 34f, view.width, 30f);
                MUI.Fill(row, i % 2 == 0 ? Row1 : Bg, 4f);
                Color c = u.state == UpdState.Needs ? Info : (u.state == UpdState.Busy ? MUI.Warn : (u.state == UpdState.Done ? MUI.Accent : MUI.Muted));
                DrawDot(new Rect(row.x + 8f, row.y, 8f, row.height), c);
                Label(new Rect(row.x + 24f, row.y, row.width * 0.5f, row.height), u.mod.Name.Truncate(row.width * 0.5f), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
                string st = u.state == UpdState.Needs ? "MSM_UpdNeeds".Translate().ToString()
                    : u.state == UpdState.Busy ? "MSM_UpdBusy".Translate(Mathf.RoundToInt(u.progress * 100f)).ToString()
                    : u.state == UpdState.Done ? "MSM_UpdDone".Translate().ToString()
                    : "MSM_UpdRecent".Translate(u.updated.ToString("d.M.yyyy HH:mm")).ToString();
                Label(new Rect(row.x + row.width * 0.5f + 30f, row.y, row.width * 0.5f - 150f, row.height), st, c, GameFont.Small, TextAnchor.MiddleLeft, false);
                if (u.state == UpdState.Needs && Button(new Rect(row.xMax - 130f, row.y + 2f, 124f, 26f), "MSM_UpdOne".Translate()))
                    Updates.Download(u);
            }
            if (rows.Count == 0)
                Label(new Rect(0f, 0f, view.width, 30f), "MSM_UpdNone".Translate(), MUI.Accent);
            Widgets.EndScrollView();
        }

        private void DrawProfiles(Rect body)
        {
            if (profileFiles == null)
            {
                try { profileFiles = GenFilePaths.AllModListFiles.OrderByDescending(f => f.LastWriteTime).ToList(); } catch { profileFiles = new List<FileInfo>(); }
                try { backupFiles = new DirectoryInfo(MSM.BackupDir).GetFiles("ModsConfig_*.xml").OrderByDescending(f => f.Name).ToList(); } catch { backupFiles = new List<FileInfo>(); }
            }
            float half = body.width / 2f;
            Rect left = new Rect(body.x, body.y, half, body.height).ContractedBy(Pad, 10f);
            Rect right = new Rect(body.x + half, body.y, half, body.height).ContractedBy(Pad, 10f);
            MUI.Fill(new Rect(body.x + half, body.y, 1f, body.height), Line);

            Label(new Rect(left.x, left.y, left.width, 22f), "MSM_Profiles".Translate(), MUI.Muted, GameFont.Tiny);
            Label(new Rect(left.x, left.y + 22f, left.width, 40f), "MSM_ProfilesHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            float y = left.y + 62f;
            profileName = SearchField(new Rect(left.x, y, left.width - 190f, 32f), profileName, "MSM_ProfileNameHint".Translate());
            if (Button(new Rect(left.xMax - 180f, y, 180f, 32f), "MSM_SaveProfile".Translate(), true, !profileName.NullOrEmpty()))
                SaveProfile();
            y += 40f;
            // 0.2.0: the list as a text code to share (friends, Discord …)
            float sw = (left.width - 8f) / 2f;
            Rect copyR = new Rect(left.x, y, sw, 30f), pasteR = new Rect(left.x + sw + 8f, y, sw, 30f);
            if (Button(copyR, "MSM_ShareCopy".Translate()))
            {
                GUIUtility.systemCopyBuffer = ShareCode.Make(active);
                Messages.Message("MSM_ShareCopied".Translate(active.Count), MessageTypeDefOf.TaskCompletion, false);
            }
            TooltipHandler.TipRegion(copyR, "MSM_ShareCopyTip".Translate());
            if (Button(pasteR, "MSM_SharePaste".Translate()))
            {
                List<ShareEntry> got = ShareCode.Parse(GUIUtility.systemCopyBuffer);
                if (got == null) Messages.Message("MSM_ShareBad".Translate(), MessageTypeDefOf.RejectInput, false);
                else ApplyIds(got.Select(e => e.id).ToList(), got.Select(e => e.name).ToList(), got.Select(e => e.wid).ToList());
            }
            TooltipHandler.TipRegion(pasteR, "MSM_SharePasteTip".Translate());
            y += 40f;
            // 0.2.2: the mod list of a save – what is missing or different, and use it
            Rect saveR = new Rect(left.x, y, left.width, 30f);
            if (Button(saveR, "MSM_FromSave".Translate())) SaveMenu();
            TooltipHandler.TipRegion(saveR, "MSM_FromSaveTip".Translate());
            y += 40f;
            Rect outL = new Rect(left.x, y, left.width, left.yMax - y);
            Rect viewL = new Rect(0f, 0f, outL.width - 16f, Mathf.Max(profileFiles.Count * 40f, outL.height - 2f));
            Widgets.BeginScrollView(outL, ref sProfiles, viewL);
            FileInfo toLoad = null, toDelete = null;
            for (int i = 0; i < profileFiles.Count; i++)
            {
                FileInfo f = profileFiles[i];
                Rect row = new Rect(0f, i * 40f, viewL.width, 36f);
                MUI.Fill(row, i % 2 == 0 ? Row1 : Bg, 4f);
                Label(new Rect(row.x + 10f, row.y, row.width - 230f, 36f), Path.GetFileNameWithoutExtension(f.Name) + "  ·  " + f.LastWriteTime.ToString("d.M.yyyy HH:mm"), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
                if (Button(new Rect(row.xMax - 214f, row.y + 4f, 110f, 28f), "MSM_Load".Translate())) toLoad = f;
                if (Button(new Rect(row.xMax - 98f, row.y + 4f, 94f, 28f), "MSM_Delete".Translate())) toDelete = f;
            }
            if (profileFiles.Count == 0)
                Label(new Rect(0f, 0f, viewL.width, 30f), "MSM_NoProfiles".Translate(), MUI.Muted);
            Widgets.EndScrollView();
            if (toLoad != null) LoadProfile(toLoad);
            if (toDelete != null)
            {
                FileInfo d = toDelete;
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MSM_DeleteConfirm".Translate(Path.GetFileNameWithoutExtension(d.Name)), delegate
                {
                    try { d.Delete(); } catch (Exception e) { Messages.Message(e.Message, MessageTypeDefOf.RejectInput, false); }
                    profileFiles = null;
                }, true));
            }

            Label(new Rect(right.x, right.y, right.width, 22f), "MSM_Backups".Translate(), MUI.Muted, GameFont.Tiny);
            Label(new Rect(right.x, right.y + 22f, right.width, 40f), "MSM_BackupsHint".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            float ry = right.y + 62f;
            Rect outR = new Rect(right.x, ry, right.width, right.yMax - ry);
            Rect viewR = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(backupFiles.Count * 40f, outR.height - 2f));
            Widgets.BeginScrollView(outR, ref sBackups, viewR);
            FileInfo restore = null;
            for (int i = 0; i < backupFiles.Count; i++)
            {
                FileInfo f = backupFiles[i];
                Rect row = new Rect(0f, i * 40f, viewR.width, 36f);
                MUI.Fill(row, i % 2 == 0 ? Row1 : Bg, 4f);
                Label(new Rect(row.x + 10f, row.y, row.width - 140f, 36f), f.LastWriteTime.ToString("d.M.yyyy HH:mm:ss"), MUI.Text1, GameFont.Small, TextAnchor.MiddleLeft, false);
                if (Button(new Rect(row.xMax - 124f, row.y + 4f, 120f, 28f), "MSM_Load".Translate())) restore = f;
            }
            if (backupFiles.Count == 0)
                Label(new Rect(0f, 0f, viewR.width, 30f), "MSM_NoBackups".Translate(), MUI.Muted);
            Widgets.EndScrollView();
            if (restore != null) ApplyIds(MSM.ReadModsConfigIds(restore.FullName), null);
        }

        // 0.2.2: pick a save (newest first) and show its mod list
        private void SaveMenu()
        {
            List<FileInfo> saves = SaveMods.Files();
            if (saves.Count == 0)
            {
                Messages.Message("MSM_NoSaves".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            var opts = new List<FloatMenuOption>();
            foreach (FileInfo f in saves.Take(60))
            {
                FileInfo file = f;
                opts.Add(new FloatMenuOption(Path.GetFileNameWithoutExtension(f.Name) + "  ·  " + f.LastWriteTime.ToString("d.M.yyyy HH:mm"), delegate
                {
                    SaveModList d = SaveMods.Read(file);
                    if (d == null)
                    {
                        Messages.Message("MSM_SaveNoMods".Translate(), MessageTypeDefOf.RejectInput, false);
                        return;
                    }
                    Find.WindowStack.Add(new Dialog_SaveMods(d, active, () => ApplyIds(d.ids, d.names, d.wids)));
                }));
            }
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        private void SaveProfile()
        {
            try
            {
                string name = GenFile.SanitizedFileName(profileName);
                var ml = new ModList { ids = active.Select(m => m.PackageId).ToList(), names = active.Select(m => m.Name).ToList() };
                GameDataSaveLoader.SaveModList(ml, GenFilePaths.AbsFilePathForModList(name));
                Messages.Message("MSM_ProfileSaved".Translate(name), MessageTypeDefOf.TaskCompletion, false);
                profileName = "";
                profileFiles = null;
            }
            catch (Exception e)
            {
                Messages.Message(e.Message, MessageTypeDefOf.RejectInput, false);
            }
        }

        private void LoadProfile(FileInfo f)
        {
            ModList ml;
            if (!GameDataSaveLoader.TryLoadModList(f.FullName, out ml) || ml == null || ml.ids == null)
            {
                Messages.Message("MSM_ProfileBad".Translate(), MessageTypeDefOf.RejectInput, false);
                return;
            }
            ApplyIds(ml.ids, ml.names);
        }

        private void ApplyIds(List<string> ids, List<string> names, List<ulong> wids = null)
        {
            var list = new List<ModMetaData>();
            var missing = new List<string>();
            var canGet = new List<ulong>();
            for (int i = 0; i < ids.Count; i++)
            {
                ModMetaData m = MSM.FindMod(ids[i]);
                if (m == null)
                {
                    missing.Add(names != null && i < names.Count ? names[i] : ids[i]);
                    if (wids != null && i < wids.Count && wids[i] != 0) canGet.Add(wids[i]);
                    continue;
                }
                if (!list.Contains(m)) list.Add(m);
            }
            ModMetaData core = ModLister.AllInstalledMods.FirstOrDefault(x => x.IsCoreMod);
            if (core != null && !list.Contains(core)) list.Insert(0, core);
            active = list;
            dirty = true;
            tab = 0;
            if (missing.Count > 0 && canGet.Count > 0 && Updates.CanSubscribe)
            {
                List<ulong> get = canGet.Distinct().ToList();
                string text = "MSM_Missing".Translate(missing.Count, string.Join("\n", missing.Take(40).ToArray()) + (missing.Count > 40 ? "\n…" : ""));
                Find.WindowStack.Add(new Dialog_MessageBox(text, "MSM_SubscribeMissing".Translate(get.Count), delegate
                {
                    int ok = get.Count(id => Updates.Subscribe(id));
                    needsRestart |= ok > 0;
                    Messages.Message("MSM_SubscribedN".Translate(ok), MessageTypeDefOf.TaskCompletion, false);
                }, "CloseButton".Translate(), null));
            }
            else if (missing.Count > 0)
                Find.WindowStack.Add(new Dialog_MessageBox("MSM_Missing".Translate(missing.Count, string.Join("\n", missing.Take(40).ToArray()) + (missing.Count > 40 ? "\n…" : ""))));
            else
                Messages.Message("MSM_Loaded".Translate(list.Count), MessageTypeDefOf.TaskCompletion, false);
        }
    }
}


namespace ModSortManager
{
    // 0.1.7: every method two mods both change, and how each of them does it
    public class Dialog_SameDetail : Verse.Window
    {
        private readonly SamePair pair;
        private UnityEngine.Vector2 scroll;

        public override UnityEngine.Vector2 InitialSize => new UnityEngine.Vector2(860f, 620f);

        public Dialog_SameDetail(SamePair p)
        {
            pair = p;
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(UnityEngine.Rect r)
        {
            Verse.Text.Font = Verse.GameFont.Medium;
            Verse.Widgets.Label(new UnityEngine.Rect(r.x, r.y, r.width, 32f), pair.a.Name + " × " + pair.b.Name);
            Verse.Text.Font = Verse.GameFont.Small;
            string legend = "MSM_SameLegend".Translate();
            float lh = Verse.Text.CalcHeight(legend, r.width);
            UnityEngine.GUI.color = MUI.Muted;
            Verse.Widgets.Label(new UnityEngine.Rect(r.x, r.y + 36f, r.width, lh), legend);
            UnityEngine.GUI.color = UnityEngine.Color.white;
            float top = r.y + 40f + lh;
            var outR = new UnityEngine.Rect(r.x, top, r.width, r.yMax - top);
            float rowH = 66f;
            var view = new UnityEngine.Rect(0f, 0f, outR.width - 16f, pair.detail.Count * rowH);
            Verse.Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < pair.detail.Count; i++)
            {
                SameMethod d = pair.detail[i];
                var row = new UnityEngine.Rect(0f, i * rowH, view.width, rowH - 4f);
                if (i % 2 == 1) Verse.Widgets.DrawLightHighlight(row);
                Verse.Widgets.Label(new UnityEngine.Rect(row.x + 6f, row.y + 2f, row.width - 12f, 22f), "<b>" + d.method + "</b>" + (d.runsFirst != null ? "   " + "MSM_RunsFirst".Translate(d.runsFirst) : ""));
                Verse.Widgets.Label(new UnityEngine.Rect(row.x + 18f, row.y + 22f, row.width - 24f, 20f), pair.a.Name + ": " + d.kindsA);
                Verse.Widgets.Label(new UnityEngine.Rect(row.x + 18f, row.y + 41f, row.width - 24f, 20f), pair.b.Name + ": " + d.kindsB);
            }
            Verse.Widgets.EndScrollView();
        }
    }
}
