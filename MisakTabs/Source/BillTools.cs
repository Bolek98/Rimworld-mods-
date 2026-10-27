using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace MisakTabs
{
    // ---------------------------------------------------------------------------
    // 0.5.0: linked bills (like Better Workbench Management) – bills with the same recipe on
    // different workbenches share their settings: repeat mode and count, target, pause, ingredient
    // filter, radius, skill range, store and count-in stockpile. When one changes (in our tab, the
    // game's bill dialog or because a product was finished), the others get the same settings.
    // ---------------------------------------------------------------------------
    public class BillLinks : GameComponent
    {
        private Dictionary<string, int> group = new Dictionary<string, int>();
        private int nextGroup = 1;
        private static BillLinks I;

        // runtime only
        private readonly Dictionary<string, int> lastHash = new Dictionary<string, int>();
        private readonly Dictionary<string, Bill> cache = new Dictionary<string, Bill>();
        private int cacheFrame = -9999;
        private static bool warned;

        public BillLinks(Game g) { I = this; }

        public override void FinalizeInit() { I = this; lastHash.Clear(); cache.Clear(); cacheFrame = -9999; }

        public override void ExposeData()
        {
            if (Scribe.mode == LoadSaveMode.Saving) Prune();
            Scribe_Collections.Look(ref group, "billLinks", LookMode.Value, LookMode.Value);
            Scribe_Values.Look(ref nextGroup, "billLinksNext", 1);
            if (group == null) group = new Dictionary<string, int>();
            I = this;
        }

        private static IEnumerable<Bill> AllBills()
        {
            foreach (Map m in Find.Maps)
                foreach (Building_WorkTable t in m.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
                    foreach (Bill b in t.billStack.Bills)
                        if (b != null && !b.deleted) yield return b;
        }

        private void RebuildCache()
        {
            cache.Clear();
            foreach (Bill b in AllBills()) cache[b.GetUniqueLoadID()] = b;
            cacheFrame = Time.frameCount;
        }

        // forget links of bills that no longer exist
        private void Prune()
        {
            try
            {
                RebuildCache();
                foreach (string k in group.Keys.ToList()) if (!cache.ContainsKey(k)) group.Remove(k);
                foreach (var g in group.GroupBy(kv => kv.Value).Where(g => g.Count() < 2).ToList())
                    foreach (var kv in g) group.Remove(kv.Key);
            }
            catch { }
        }

        public static int GroupOf(Bill b)
        {
            if (I == null || b == null) return 0;
            return I.group.TryGetValue(b.GetUniqueLoadID(), out int g) ? g : 0;
        }

        // the other bills in the same link group (alive ones)
        public static List<Bill> Others(Bill b)
        {
            var res = new List<Bill>();
            int g = GroupOf(b);
            if (g == 0) return res;
            if (Time.frameCount - I.cacheFrame > 120) I.RebuildCache();
            string id = b.GetUniqueLoadID();
            foreach (var kv in I.group)
                if (kv.Value == g && kv.Key != id && I.cache.TryGetValue(kv.Key, out Bill o) && !o.deleted) res.Add(o);
            return res;
        }

        // bills with the same recipe on other workbenches of the map that are not linked with it yet
        public static List<Bill> Candidates(Bill b, Map map)
        {
            var res = new List<Bill>();
            if (b == null || map == null) return res;
            int g = GroupOf(b);
            foreach (Building_WorkTable t in map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>())
            {
                if (t.billStack == b.billStack) continue;
                foreach (Bill o in t.billStack.Bills)
                    if (o != null && !o.deleted && o.recipe == b.recipe && o.GetType() == b.GetType() && (g == 0 || GroupOf(o) != g)) res.Add(o);
            }
            return res;
        }

        public static void Link(Bill from, Bill to)
        {
            if (I == null || from == null || to == null) return;
            int ga = GroupOf(from), gb = GroupOf(to);
            int g = ga != 0 ? ga : gb != 0 ? gb : I.nextGroup++;
            if (ga != 0 && gb != 0 && ga != gb)
                foreach (string k in I.group.Where(kv => kv.Value == gb).Select(kv => kv.Key).ToList()) I.group[k] = g;
            I.group[from.GetUniqueLoadID()] = g;
            I.group[to.GetUniqueLoadID()] = g;
            CopySettings(from, to);
            foreach (Bill o in Others(from)) { if (o != to) CopySettings(from, o); }
            I.RebuildCache();
            I.lastHash.Clear();
        }

        public static void Unlink(Bill b)
        {
            if (I == null || b == null) return;
            int g = GroupOf(b);
            I.group.Remove(b.GetUniqueLoadID());
            var rest = I.group.Where(kv => kv.Value == g).Select(kv => kv.Key).ToList();
            if (rest.Count < 2) foreach (string k in rest) I.group.Remove(k);
        }

        public static void CopySettings(Bill f, Bill t)
        {
            if (f == null || t == null || f == t) return;
            t.suspended = f.suspended;
            t.ingredientFilter.CopyAllowancesFrom(f.ingredientFilter);
            t.ingredientSearchRadius = f.ingredientSearchRadius;
            t.allowedSkillRange = f.allowedSkillRange;
            if (f is Bill_Production a && t is Bill_Production b)
            {
                b.repeatMode = a.repeatMode;
                b.repeatCount = a.repeatCount;
                b.targetCount = a.targetCount;
                b.pauseWhenSatisfied = a.pauseWhenSatisfied;
                b.unpauseWhenYouHave = a.unpauseWhenYouHave;
                b.includeEquipped = a.includeEquipped;
                b.includeTainted = a.includeTainted;
                b.hpRange = a.hpRange;
                b.qualityRange = a.qualityRange;
                b.limitToAllowedStuff = a.limitToAllowedStuff;
                if (a.Map == b.Map)
                {
                    b.SetStoreMode(a.GetStoreMode(), a.GetSlotGroup());
                    b.SetIncludeGroup(a.GetIncludeSlotGroup());
                }
            }
        }

        private static int FilterHash(ThingFilter f)
        {
            if (f == null) return 0;
            unchecked
            {
                int h = f.AllowedDefCount * 7919;
                foreach (ThingDef d in f.AllowedThingDefs) h += d.shortHash * (int)2654435761u;
                h = h * 31 + f.AllowedHitPointsPercents.GetHashCode();
                h = h * 31 + f.AllowedQualityLevels.GetHashCode();
                List<SpecialThingFilterDef> sfs = DefDatabase<SpecialThingFilterDef>.AllDefsListForReading;
                for (int i = 0; i < sfs.Count; i++) if (f.Allows(sfs[i])) h = h * 31 + i + 1;
                return h;
            }
        }

        private static int Hash(Bill b)
        {
            unchecked
            {
                int h = b.suspended ? 1 : 0;
                h = h * 31 + b.ingredientSearchRadius.GetHashCode();
                h = h * 31 + b.allowedSkillRange.min * 100 + b.allowedSkillRange.max;
                h = h * 31 + FilterHash(b.ingredientFilter);
                if (b is Bill_Production p)
                {
                    h = h * 31 + (p.repeatMode?.shortHash ?? 0);
                    h = h * 31 + p.repeatCount;
                    h = h * 31 + p.targetCount;
                    h = h * 31 + (p.pauseWhenSatisfied ? 1 : 0);
                    h = h * 31 + p.unpauseWhenYouHave;
                    h = h * 31 + (p.includeEquipped ? 1 : 0) + (p.includeTainted ? 2 : 0) + (p.limitToAllowedStuff ? 4 : 0);
                    h = h * 31 + p.hpRange.GetHashCode();
                    h = h * 31 + p.qualityRange.GetHashCode();
                    h = h * 31 + (p.GetStoreMode()?.shortHash ?? 0);
                    h = h * 31 + (p.GetSlotGroup()?.GetHashCode() ?? 0);
                    h = h * 31 + (p.GetIncludeSlotGroup()?.GetHashCode() ?? 0);
                }
                return h;
            }
        }

        // a cheap check twice a second (and only while some bills are linked)
        public override void GameComponentUpdate()
        {
            if (group.Count == 0 || Time.frameCount % 30 != 0) return;
            try { Sync(); }
            catch (Exception e)
            {
                if (!warned) { warned = true; Log.Warning("[Mišák Tabs] linked bills: " + e.Message); }
            }
        }

        private void Sync()
        {
            if (Time.frameCount - cacheFrame > 300) RebuildCache();
            foreach (var g in group.GroupBy(kv => kv.Value))
            {
                var bills = new List<Bill>();
                foreach (var kv in g)
                    if (cache.TryGetValue(kv.Key, out Bill b) && !b.deleted && b.billStack != null) bills.Add(b);
                if (bills.Count < 2) continue;
                Bill changed = null;
                var hashes = new int[bills.Count];
                for (int i = 0; i < bills.Count; i++)
                {
                    hashes[i] = Hash(bills[i]);
                    if (changed == null && lastHash.TryGetValue(bills[i].GetUniqueLoadID(), out int old) && old != hashes[i]) changed = bills[i];
                }
                if (changed != null)
                {
                    foreach (Bill o in bills) if (o != changed) CopySettings(changed, o);
                    for (int i = 0; i < bills.Count; i++) hashes[i] = Hash(bills[i]);
                }
                for (int i = 0; i < bills.Count; i++) lastHash[bills[i].GetUniqueLoadID()] = hashes[i];
            }
        }
    }

    // ---------------------------------------------------------------------------
    // 0.5.0: copy all bills of a workbench and paste them into another one (or several)
    // ---------------------------------------------------------------------------
    public static class BenchClipboard
    {
        private static List<Bill> clip;
        private static List<int> prios;
        public static string From;

        public static int Count => clip?.Count ?? 0;

        public static void Copy(Building_WorkTable t)
        {
            clip = new List<Bill>();
            prios = new List<int>();
            foreach (Bill b in t.billStack.Bills)
            {
                clip.Add(b.Clone());
                prios.Add(BillPrio.Get(b));
            }
            From = t.LabelCap;
            Messages.Message("MT_BenchCopied".Translate(clip.Count, From), MessageTypeDefOf.SilentInput, false);
        }

        private static bool Fits(Bill c, Building_WorkTable t) =>
            t.def.AllRecipes.Contains(c.recipe) && c.recipe.AvailableNow && c.recipe.AvailableOnNow(t);

        public static bool CanPaste(Building_WorkTable t) =>
            clip != null && t != null && t.billStack.Count < BillStack.MaxCount && clip.Any(c => Fits(c, t));

        // returns how many bills were added
        public static int PasteInto(Building_WorkTable t)
        {
            if (clip == null || t == null) return 0;
            int added = 0;
            bool anyPrio = false;
            for (int i = 0; i < clip.Count; i++)
            {
                if (t.billStack.Count >= BillStack.MaxCount) break;
                Bill c = clip[i];
                if (!Fits(c, t)) continue;
                Bill b = c.Clone();
                b.InitializeAfterClone();
                t.billStack.AddBill(b);
                if (prios[i] != BillPrio.Normal) { BillPrio.SetRaw(b, prios[i]); anyPrio = true; }
                added++;
            }
            if (anyPrio) BillPrio.Resort(t);
            return added;
        }
    }

    // 0.5.0: paste the copied bills into several workbenches of the same kind on the map
    public class Dialog_PasteBenches : Window
    {
        private readonly List<Building_WorkTable> benches;
        private readonly HashSet<Building_WorkTable> chosen;
        private readonly Building_WorkTable current;
        private Vector2 scroll;

        public override Vector2 InitialSize => new Vector2(460f, 520f);

        public Dialog_PasteBenches(Building_WorkTable from)
        {
            current = from;
            benches = from.Map.listerBuildings.AllBuildingsColonistOfClass<Building_WorkTable>().Where(x => x.def == from.def).ToList();
            chosen = new HashSet<Building_WorkTable>(benches.Where(x => x != from));
            doCloseX = true;
            closeOnClickedOutside = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "MT_PasteSeveral".Translate());
            Text.Font = GameFont.Small;
            Widgets.Label(new Rect(r.x, r.y + 34f, r.width, 24f), "MT_PasteSeveralSub".Translate(BenchClipboard.Count, BenchClipboard.From ?? "-"));
            float y = r.y + 62f;
            if (Widgets.ButtonText(new Rect(r.x, y, 90f, 26f), "MT_All".Translate())) { chosen.Clear(); foreach (var b in benches) chosen.Add(b); }
            if (Widgets.ButtonText(new Rect(r.x + 96f, y, 90f, 26f), "MT_None".Translate())) chosen.Clear();
            y += 32f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y - 44f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, benches.Count * 28f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            try
            {
                for (int i = 0; i < benches.Count; i++)
                {
                    Building_WorkTable t = benches[i];
                    bool on = chosen.Contains(t);
                    string label = t.LabelCap + " (" + t.Position.x + ", " + t.Position.z + ") – " + "MT_BillsN".Translate(t.billStack.Count).ToString() + (t == current ? " – " + "MT_ThisBench".Translate().ToString() : "");
                    bool was = on;
                    Widgets.CheckboxLabeled(new Rect(0f, i * 28f, view.width, 26f), label, ref on);
                    if (on != was) { if (on) chosen.Add(t); else chosen.Remove(t); }
                }
            }
            finally { Widgets.EndScrollView(); }
            if (Widgets.ButtonText(new Rect(r.xMax - 160f, r.yMax - 36f, 160f, 34f), "MT_PasteN".Translate(chosen.Count)) && chosen.Count > 0)
            {
                int bills = 0;
                foreach (Building_WorkTable t in chosen) bills += BenchClipboard.PasteInto(t);
                Messages.Message("MT_PastedInto".Translate(bills, chosen.Count), MessageTypeDefOf.TaskCompletion, false);
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                Close();
            }
        }
    }
}
