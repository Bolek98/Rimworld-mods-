using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace ModSortManager
{
    // ===========================================================================
    // 0.8.0: folders for the inactive list (an idea from a tester). The player sorts his switched-off
    // mods into collapsible folders however he likes and can switch a whole folder on or off at once.
    // A ★ Favorites folder is always on top. Folders are only a view of the inactive list – the active
    // list keeps its load order, and categories stay independent (a mod in a folder still shows in its
    // category). Saved in the MSM settings by packageId (lower case); a mod is in one folder at most.
    // ===========================================================================
    public static class ModFolders
    {
        public const string Fav = "*fav";
        public static int Version;

        private static Dictionary<string, string> member;   // packageid → folder

        private static MSMSettings S => MSMMod.Settings;

        // folder names in their order, Favorites first
        public static List<string> Order()
        {
            var l = new List<string> { Fav };
            if (S != null) foreach (string f in S.folders) if (f != Fav && !l.Contains(f)) l.Add(f);
            return l;
        }

        public static string Label(string folder) => folder == Fav ? "MSM_FolderFav".Translate().ToString() : folder;

        private static string Key(ModMetaData m) => (m?.PackageId ?? "").ToLowerInvariant();

        private static Dictionary<string, string> Members
        {
            get
            {
                if (member != null) return member;
                var d = new Dictionary<string, string>();
                var known = new HashSet<string>(Order());
                foreach (string line in S?.folderMembers ?? new List<string>())
                {
                    int i = line.LastIndexOf('|');
                    if (i <= 0 || i >= line.Length - 1) continue;
                    string f = line.Substring(i + 1);
                    if (known.Contains(f)) d[line.Substring(0, i)] = f;
                }
                member = d;
                return member;
            }
        }

        public static string Of(ModMetaData m) => m != null && Members.TryGetValue(Key(m), out string f) ? f : null;

        public static bool AnyUsed => Members.Count > 0 || (S != null && S.folders.Count > 0);

        public static bool Collapsed(string f) => S != null && S.folderCollapsed.Contains(f);

        public static void Toggle(string f)
        {
            if (S == null) return;
            if (!S.folderCollapsed.Remove(f)) S.folderCollapsed.Add(f);
            Changed();
        }

        public static void Put(IEnumerable<ModMetaData> mods, string folder)
        {
            if (S == null) return;
            foreach (ModMetaData m in mods)
            {
                if (m == null) continue;
                string k = Key(m);
                S.folderMembers.RemoveAll(l => l.StartsWith(k + "|", StringComparison.Ordinal));
                if (folder != null) S.folderMembers.Add(k + "|" + folder);
            }
            Changed();
        }

        private static string Clean(string name) => (name ?? "").Replace('|', '/').Replace('\n', ' ').Replace('\r', ' ').Trim();

        public static string Create(string name)
        {
            name = Clean(name);
            if (S == null || name.Length == 0 || name == Fav) return null;
            if (!S.folders.Contains(name)) S.folders.Add(name);
            Changed();
            return name;
        }

        public static void Rename(string old, string name)
        {
            name = Clean(name);
            if (S == null || old == Fav || name.Length == 0 || name == Fav || name == old || S.folders.Contains(name)) return;
            int i = S.folders.IndexOf(old);
            if (i < 0) return;
            S.folders[i] = name;
            for (int j = 0; j < S.folderMembers.Count; j++)
                if (S.folderMembers[j].EndsWith("|" + old, StringComparison.Ordinal))
                    S.folderMembers[j] = S.folderMembers[j].Substring(0, S.folderMembers[j].Length - old.Length) + name;
            if (S.folderCollapsed.Remove(old)) S.folderCollapsed.Add(name);
            Changed();
        }

        // the folder goes, its mods become loose again
        public static void Delete(string f)
        {
            if (S == null || f == Fav) return;
            S.folders.Remove(f);
            S.folderMembers.RemoveAll(l => l.EndsWith("|" + f, StringComparison.Ordinal));
            S.folderCollapsed.Remove(f);
            Changed();
        }

        public static void Shift(string f, int dir)
        {
            if (S == null || f == Fav) return;
            int i = S.folders.IndexOf(f), j = i + dir;
            if (i < 0 || j < 0 || j >= S.folders.Count) return;
            S.folders[i] = S.folders[j];
            S.folders[j] = f;
            Changed();
        }

        private static void Changed()
        {
            member = null;
            Version++;
            S?.Write();
        }
    }

    public class Dialog_FolderName : Window
    {
        private readonly Action<string> ok;
        private readonly string title;
        private string text;
        private bool focused;

        public Dialog_FolderName(string title, string initial, Action<string> ok)
        {
            this.title = title;
            this.ok = ok;
            text = initial ?? "";
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
            closeOnClickedOutside = true;
        }

        public override Vector2 InitialSize => new Vector2(420f, 170f);

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y, r.width, 26f), title);
            GUI.SetNextControlName("MSMFolder");
            text = Widgets.TextField(new Rect(r.x, r.y + 32f, r.width, 32f), text, 60);
            if (!focused) { UI.FocusControl("MSMFolder", this); focused = true; }
            bool enter = Event.current.type == EventType.KeyDown && (Event.current.keyCode == KeyCode.Return || Event.current.keyCode == KeyCode.KeypadEnter);
            if (Widgets.ButtonText(new Rect(r.xMax - 120f, r.yMax - 36f, 120f, 34f), "OK".Translate()) || enter)
            {
                if (text.Trim().Length > 0) ok?.Invoke(text);
                if (enter) Event.current.Use();
                Close();
            }
            if (Widgets.ButtonText(new Rect(r.xMax - 250f, r.yMax - 36f, 120f, 34f), "CancelButton".Translate())) Close();
        }
    }

    public partial class Dialog_ModSortManager
    {
        // the inactive list as shown: folder headers are null rows, vHeads says which folder each is
        private List<ModMetaData> vInact;
        private readonly Dictionary<int, string> vHeads = new Dictionary<int, string>();
        private readonly Dictionary<string, int[]> folderCounts = new Dictionary<string, int[]>();   // folder → {active, installed}
        private readonly List<KeyValuePair<Rect, string>> headRects = new List<KeyValuePair<Rect, string>>();   // screen rects of visible headers
        private int cFolderStamp = -1;
        private static readonly Color FolderBg = new Color32(36, 42, 50, 255);
        private static readonly Color FolderCol = new Color32(232, 196, 96, 255);

        // builds the shown inactive list from the filtered one
        private void BuildFolderView(List<ModMetaData> filtered, bool filterOn)
        {
            cFolderStamp = ModFolders.Version;
            vHeads.Clear();
            folderCounts.Clear();
            if (!ModFolders.AnyUsed) { vInact = filtered; return; }
            List<string> order = ModFolders.Order();
            var byFolder = order.ToDictionary(f => f, f => new List<ModMetaData>());
            var loose = new List<ModMetaData>();
            foreach (ModMetaData m in filtered)
            {
                string f = ModFolders.Of(m);
                if (f != null && byFolder.TryGetValue(f, out var l)) l.Add(m); else loose.Add(m);
            }
            // counts over every installed mod of the folder (one per packageId)
            var activeIds = new HashSet<string>(active.Select(a => a.PackageIdNonUnique.ToLowerInvariant()));
            foreach (string f in order) folderCounts[f] = new int[2];
            var seen = new HashSet<string>();
            foreach (ModMetaData m in ModLister.AllInstalledMods)
            {
                string f = ModFolders.Of(m);
                if (f == null || !folderCounts.TryGetValue(f, out int[] c)) continue;
                string id = m.PackageIdNonUnique.ToLowerInvariant();
                if (!seen.Add(id)) continue;
                c[1]++;
                if (activeIds.Contains(id)) c[0]++;
            }
            var v = new List<ModMetaData>();
            foreach (string f in order)
            {
                List<ModMetaData> l = byFolder[f];
                // with a search or category filter only folders with matching mods are shown
                if (filterOn && l.Count == 0) continue;
                vHeads[v.Count] = f;
                v.Add(null);
                if (!ModFolders.Collapsed(f) || filterOn) v.AddRange(l);
            }
            v.AddRange(loose);
            vInact = v;
        }

        private List<ModMetaData> FolderMods(string f, bool activeOnes)
        {
            var activeIds = new HashSet<string>(active.Select(a => a.PackageIdNonUnique.ToLowerInvariant()));
            var res = new List<ModMetaData>();
            var seen = new HashSet<string>();
            foreach (ModMetaData m in ModLister.AllInstalledMods)
            {
                if (ModFolders.Of(m) != f || Removal.IsGone(m)) continue;
                string id = m.PackageIdNonUnique.ToLowerInvariant();
                if (activeIds.Contains(id) != activeOnes) continue;
                if (activeOnes) { if (active.Contains(m)) res.Add(m); continue; }
                if (!seen.Add(id)) continue;
                // of two copies of the same mod the one listed (the newest) is taken
                res.Add(inactiveCache.FirstOrDefault(x => x.PackageIdNonUnique.ToLowerInvariant() == id) ?? m);
            }
            return res;
        }

        private void EnableFolder(string f)
        {
            List<ModMetaData> mods = FolderMods(f, false);
            if (mods.Count == 0) return;
            ActivateMany(mods);
            var ids = new HashSet<string>(active.Select(a => a.PackageIdNonUnique.ToLowerInvariant()));
            int missing = 0;
            foreach (ModMetaData m in mods)
            {
                try
                {
                    if (m.Dependencies != null && m.Dependencies.Any(d => d != null && !d.packageId.NullOrEmpty() && !ids.Contains(d.packageId.ToLowerInvariant()))) missing++;
                }
                catch { }
            }
            Messages.Message("MSM_FolderEnabled".Translate(mods.Count, ModFolders.Label(f)).ToString() + (missing > 0 ? " " + "MSM_FolderMissingDeps".Translate(missing).ToString() : ""),
                missing > 0 ? MessageTypeDefOf.CautionInput : MessageTypeDefOf.NeutralEvent, false);
        }

        private void DisableFolder(string f)
        {
            List<ModMetaData> mods = FolderMods(f, true);
            if (mods.Count == 0) return;
            DeactivateMany(mods);
            Messages.Message("MSM_FolderDisabled".Translate(mods.Count(m => !active.Contains(m)), ModFolders.Label(f)), MessageTypeDefOf.NeutralEvent, false);
        }

        // one header row of the inactive list; true when the event was used
        private bool DrawFolderHeader(Rect row, string f)
        {
            Event ev = Event.current;
            bool collapsed = ModFolders.Collapsed(f);
            folderCounts.TryGetValue(f, out int[] c);
            int nAct = c?[0] ?? 0, nAll = c?[1] ?? 0;
            Rect scr = new Rect(GUIUtility.GUIToScreenPoint(row.position), row.size);
            if (rInactScreen.Overlaps(scr)) headRects.Add(new KeyValuePair<Rect, string>(scr, f));

            bool dropHere = (xDragging || dragging) && Mouse.IsOver(row);
            MUI.Fill(row, dropHere ? MUI.OnBg : FolderBg);
            if (dropHere) MUI.Frame(row, MUI.Accent);
            MUI.Fill(new Rect(row.x, row.yMax - 1f, row.width, 1f), Line);

            // buttons on the right, sized from their texts
            float bx = row.xMax - 4f;
            Rect bOff = default, bOn = default;
            string tOff = "MSM_FolderOff".Translate(), tOn = "MSM_FolderOn".Translate();
            float wOff = Text.CalcSize(tOff).x * 0.8f + 14f, wOn = Text.CalcSize(tOn).x * 0.8f + 14f;
            if (nAct > 0) { bOff = new Rect(bx - wOff, row.y + 3f, wOff, row.height - 6f); bx = bOff.x - 4f; }
            if (nAll - nAct > 0) { bOn = new Rect(bx - wOn, row.y + 3f, wOn, row.height - 6f); bx = bOn.x - 4f; }
            string cnt = nAct + " / " + nAll;
            float cw = Text.CalcSize(cnt).x * 0.8f + 8f;
            Rect cr = new Rect(bx - cw, row.y, cw, row.height);
            bx = cr.x - 4f;
            Label(cr, cnt, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleRight, false);
            TooltipHandler.TipRegion(cr, "MSM_FolderCountTip".Translate(nAct, nAll));

            string arrow = collapsed ? "▶" : "▼";
            Label(new Rect(row.x + 6f, row.y, 16f, row.height), arrow, MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft, false);
            string name = (f == ModFolders.Fav ? "★ " : "▪ ") + ModFolders.Label(f);
            float nx = row.x + 22f;
            Label(new Rect(nx, row.y, Mathf.Max(0f, bx - nx), row.height), name.Truncate(Mathf.Max(0f, bx - nx)), FolderCol, GameFont.Small, TextAnchor.MiddleLeft, false);
            TooltipHandler.TipRegion(new Rect(row.x, row.y, Mathf.Max(0f, bx - row.x), row.height), "MSM_FolderTip".Translate());

            if (nAct > 0 && Button(bOff, tOff, false, true)) { DisableFolder(f); SoundDefOf.Click.PlayOneShotOnCamera(); return true; }
            if (nAll - nAct > 0 && Button(bOn, tOn, true, true)) { EnableFolder(f); SoundDefOf.Click.PlayOneShotOnCamera(); return true; }

            if (ev.type == EventType.MouseDown && Mouse.IsOver(row))
            {
                if (ev.button == 1) { FolderMenu(f); ev.Use(); return true; }
                if (ev.button == 0) { ModFolders.Toggle(f); SoundDefOf.Tick_Tiny.PlayOneShotOnCamera(); ev.Use(); return true; }
            }
            return false;
        }

        private void FolderMenu(string f)
        {
            var opts = new List<FloatMenuOption>();
            if (f != ModFolders.Fav)
            {
                opts.Add(new FloatMenuOption("MSM_FolderRename".Translate(), () =>
                    Find.WindowStack.Add(new Dialog_FolderName("MSM_FolderRenameTitle".Translate(), f, n => ModFolders.Rename(f, n)))));
                opts.Add(new FloatMenuOption("MSM_FolderUp".Translate(), () => ModFolders.Shift(f, -1)));
                opts.Add(new FloatMenuOption("MSM_FolderDown".Translate(), () => ModFolders.Shift(f, 1)));
                opts.Add(new FloatMenuOption("MSM_FolderDelete".Translate(), () => ModFolders.Delete(f)));
            }
            opts.Add(new FloatMenuOption("MSM_FolderNew".Translate(), NewFolderDialog));
            Find.WindowStack.Add(new FloatMenu(opts, ModFolders.Label(f)));
        }

        private void NewFolderDialog() => NewFolderDialog(null);

        private void NewFolderDialog(List<ModMetaData> putIn)
        {
            Find.WindowStack.Add(new Dialog_FolderName("MSM_FolderNewTitle".Translate(), "", n =>
            {
                string made = ModFolders.Create(n);
                if (made != null && putIn != null) ModFolders.Put(putIn, made);
            }));
        }

        // right-click a mod → Move to folder…
        private void FolderMoveMenu(ModMetaData m)
        {
            List<ModMetaData> mods = multi.Count > 1 && multi.Contains(m) ? Selection() : new List<ModMetaData> { m };
            string cur = mods.Count == 1 ? ModFolders.Of(m) : null;
            var opts = new List<FloatMenuOption>();
            foreach (string f in ModFolders.Order())
            {
                string ff = f;
                opts.Add(new FloatMenuOption((f == cur ? "✓ " : "") + ModFolders.Label(f), () => { ModFolders.Put(mods, ff); multi.Clear(); }));
            }
            opts.Add(new FloatMenuOption("MSM_FolderNew".Translate(), () => NewFolderDialog(mods)));
            if (mods.Any(x => ModFolders.Of(x) != null))
                opts.Add(new FloatMenuOption("MSM_FolderNone".Translate(), () => { ModFolders.Put(mods, null); multi.Clear(); }));
            Find.WindowStack.Add(new FloatMenu(opts));
        }

        // screen point → folder header under it (for drag & drop)
        private string FolderAt(Vector2 screen)
        {
            if (!rInactScreen.Contains(screen)) return null;
            foreach (var kv in headRects) if (kv.Key.Contains(screen)) return kv.Value;
            return null;
        }
    }
}
