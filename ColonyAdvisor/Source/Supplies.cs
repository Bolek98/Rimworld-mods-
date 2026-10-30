using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using UnityEngine;
using Verse;

namespace ColonyAdvisor
{
    // ---------------------------------------------------------------------------
    // 0.2.0: supplies for the goal. A list (per save) of things the colony has to make for the
    // chosen ending – lavish meals for the High Stellarch, survival meals for the journey … –
    // with one-click bills. Food and drugs on the list can be set aside: colonists, prisoners
    // and your animals do not eat them at home (unless starving); guests (the Stellarch) do.
    // ---------------------------------------------------------------------------
    public class SupplyEntry : IExposable
    {
        public string defName;
        public int want;
        public bool reserve;
        public ThingDef Def => DefDatabase<ThingDef>.GetNamedSilentFail(defName);

        public void ExposeData()
        {
            Scribe_Values.Look(ref defName, "defName");
            Scribe_Values.Look(ref want, "want");
            Scribe_Values.Look(ref reserve, "reserve");
        }
    }

    public class GoalSupplies : GameComponent
    {
        public List<SupplyEntry> list = new List<SupplyEntry>();
        private static HashSet<string> reserved;

        public GoalSupplies(Game g) { }

        public static GoalSupplies I => Current.Game?.GetComponent<GoalSupplies>();

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref list, "list", LookMode.Deep);
            if (list == null) list = new List<SupplyEntry>();
            list.RemoveAll(e => e?.defName == null);
            reserved = null;
        }

        public static void Changed() => reserved = null;

        public static bool Reserved(ThingDef d)
        {
            if (d == null) return false;
            if (reserved == null)
            {
                GoalSupplies g = I;
                reserved = g == null ? new HashSet<string>() : new HashSet<string>(g.list.Where(e => e.reserve).Select(e => e.defName));
            }
            return reserved.Count > 0 && reserved.Contains(d.defName);
        }

        public void Add(string defName, int want, bool reserve)
        {
            ThingDef d = DefDatabase<ThingDef>.GetNamedSilentFail(defName);
            if (d == null) return;
            SupplyEntry e = list.FirstOrDefault(x => x.defName == defName);
            if (e == null) list.Add(e = new SupplyEntry { defName = defName });
            e.want = Math.Max(e.want, want);
            e.reserve = e.reserve || (reserve && CanReserve(d));
            Changed();
        }

        public static bool CanReserve(ThingDef d) => d != null && d.IsIngestible;

        public static int Have(Map map, ThingDef d)
        {
            if (map == null || d == null) return 0;
            int n = 0;
            foreach (Thing t in map.listerThings.ThingsOfDef(d))
                if (!t.IsForbidden(Faction.OfPlayer)) n += t.stackCount;
            return n;
        }

        // recommended supplies for an ending
        public static IEnumerable<(string defName, int want, bool reserve)> Preset(string ending)
        {
            switch (ending)
            {
                case "Royal":
                    yield return ("MealLavish", 24, true);
                    yield return ("Beer", 15, true);
                    yield return ("PsychiteTea", 10, true);
                    yield return ("MedicineIndustrial", 20, false);
                    yield return ("MealSurvivalPack", 20, false);
                    break;
                case "Journey":
                    yield return ("MealSurvivalPack", 40, true);
                    yield return ("Pemmican", 200, true);
                    yield return ("MedicineIndustrial", 15, false);
                    break;
                case "Ship":
                    yield return ("ComponentIndustrial", 20, false);
                    yield return ("MealSurvivalPack", 20, true);
                    yield return ("MedicineIndustrial", 15, false);
                    break;
                case "Void":
                    yield return ("MedicineIndustrial", 20, false);
                    yield return ("MealSurvivalPack", 20, true);
                    break;
                case "Gravship":
                    yield return ("MealSurvivalPack", 40, true);
                    yield return ("MedicineIndustrial", 15, false);
                    yield return ("ComponentIndustrial", 15, false);
                    break;
                case "Archonexus":
                    yield return ("MealSurvivalPack", 20, true);
                    yield return ("MedicineIndustrial", 10, false);
                    break;
            }
        }
    }

    // set-aside food and drugs: not for the colony at home
    [HarmonyPatch(typeof(FoodUtility), nameof(FoodUtility.WillEat), new[] { typeof(Pawn), typeof(Thing), typeof(Pawn), typeof(bool), typeof(bool) })]
    public static class Patch_WillEatReserved
    {
        public static void Postfix(Pawn p, Thing food, ref bool __result)
        {
            if (!__result || p == null || food == null) return;
            try
            {
                if (!GoalSupplies.Reserved(food.def)) return;
                Map map = food.MapHeld;
                if (map == null || !map.IsPlayerHome) return;
                bool ours = p.Faction == Faction.OfPlayer || p.IsPrisonerOfColony || p.IsSlaveOfColony;
                if (!ours || p.IsCaravanMember()) return;     // guests (the Stellarch) may eat
                if (p.needs?.food != null && p.needs.food.CurCategory >= HungerCategory.Starving) return;
                __result = false;
            }
            catch { }
        }
    }

    // the tab
    public static class SuppliesTab
    {
        private static string T(string key, params NamedArgument[] a) => key.Translate(a).Resolve();

        public static float Draw(Rect view, Report r, Func<float, float, string, string, float> title)
        {
            GoalSupplies g = GoalSupplies.I;
            if (g == null) return 0f;
            Map map = r.map;
            float y = title(0f, view.width, T("CA_Sup_Title"), T("CA_Sup_Sub"));
            Ending goal = Advisor.Goal(r);
            float bx = 0f;
            if (goal != null && GoalSupplies.Preset(goal.key).Any())
            {
                string lbl = T("CA_Sup_AddPreset", goal.title);
                float w = Text.CalcSize(lbl).x + 24f;
                if (Widgets.ButtonText(new Rect(bx, y, w, 30f), lbl))
                {
                    foreach (var p in GoalSupplies.Preset(goal.key))
                        g.Add(p.defName, p.want, p.reserve);
                }
                bx += w + 8f;
            }
            if (Widgets.ButtonText(new Rect(bx, y, 170f, 30f), T("CA_Sup_AddItem")))
                Find.WindowStack.Add(new Dialog_PickSupply());
            bx += 178f;
            if (g.list.Count > 0 && Widgets.ButtonText(new Rect(bx, y, 170f, 30f), T("CA_Sup_AllBills")))
            {
                foreach (SupplyEntry e in g.list.ToList())
                {
                    ThingDef d = e.Def;
                    if (d == null) continue;
                    int have = GoalSupplies.Have(map, d);
                    if (have >= e.want) continue;
                    Need nd = Advisor.MakeNeed(map, d, have, e.want, "", false);
                    if (nd.bench != null && nd.recipe != null) Advisor.AddBill(nd);
                }
            }
            y += 40f;
            if (g.list.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(0f, y, view.width, 40f), T("CA_Sup_Empty"));
                GUI.color = Color.white;
                return y + 40f;
            }
            SupplyEntry remove = null;
            foreach (SupplyEntry e in g.list)
            {
                ThingDef d = e.Def;
                if (d == null) continue;
                int have = GoalSupplies.Have(map, d);
                bool done = have >= e.want;
                Need nd = Advisor.MakeNeed(map, d, have, e.want, "", false);
                Rect card = new Rect(0f, y, view.width, 64f);
                MUI.Card(card, !done);
                try { Widgets.ThingIcon(new Rect(card.x + 10f, card.y + 12f, 40f, 40f), d); } catch { }
                GUI.color = done ? MUI.Accent : MUI.Text1;
                Widgets.Label(new Rect(card.x + 60f, card.y + 6f, card.width - 470f, 24f), (done ? "✓ " : "") + d.LabelCap + "  " + T("CA_Make_Count", have, e.want));
                GUI.color = MUI.Muted;
                string where = nd.recipe == null ? T("CA_Make_NoRecipe")
                    : nd.existing != null ? T("CA_Make_HasBill", nd.bench.LabelCap, nd.existing.repeatMode == BillRepeatModeDefOf.TargetCount ? nd.existing.targetCount.ToString() : "∞")
                    : nd.bench != null ? T("CA_Make_At", nd.bench.LabelCap)
                    : T("CA_Make_Build", nd.benchToBuild?.LabelCap.Resolve() ?? "?");
                Widgets.Label(new Rect(card.x + 60f, card.y + 30f, card.width - 470f, 30f), where);
                GUI.color = Color.white;

                // amount
                float x = card.xMax - 400f;
                if (Widgets.ButtonText(new Rect(x, card.y + 18f, 28f, 28f), "−")) e.want = Math.Max(1, e.want - Step(e.want));
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(new Rect(x + 30f, card.y + 18f, 44f, 28f), e.want.ToString());
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonText(new Rect(x + 76f, card.y + 18f, 28f, 28f), "+")) e.want += Step(e.want);
                x += 112f;

                // set aside
                if (GoalSupplies.CanReserve(d))
                {
                    Rect rr = new Rect(x, card.y + 20f, 110f, 24f);
                    bool on = e.reserve, was = on;
                    Widgets.CheckboxLabeled(rr, T("CA_Sup_Reserve"), ref on);
                    TooltipHandler.TipRegion(rr, T("CA_Sup_Reserve_Tip"));
                    if (on != was) { e.reserve = on; GoalSupplies.Changed(); }
                }
                x += 118f;

                // bill
                if (!done && nd.bench != null && nd.recipe != null)
                {
                    if (Widgets.ButtonText(new Rect(x, card.y + 17f, 120f, 30f), nd.existing != null ? T("CA_Sup_Raise") : T("CA_Btn_AddBill"))) Advisor.AddBill(nd);
                }
                else if (!done && nd.bench == null && nd.benchToBuild != null && nd.benchToBuild.BuildableByPlayer)
                {
                    if (Widgets.ButtonText(new Rect(x, card.y + 17f, 120f, 30f), T("CA_Btn_Build")))
                    {
                        Designator_Build des = BuildCopyCommandUtility.FindAllowedDesignator(nd.benchToBuild);
                        if (des != null) Find.DesignatorManager.Select(des);
                    }
                }
                if (Widgets.ButtonText(new Rect(card.xMax - 36f, card.y + 18f, 28f, 28f), "×")) remove = e;
                y = card.yMax + 6f;
            }
            if (remove != null) { g.list.Remove(remove); GoalSupplies.Changed(); }
            return y;
        }

        private static int Step(int n) => n >= 100 ? 25 : n >= 20 ? 5 : 1;
    }

    // choose something that can be made here
    public class Dialog_PickSupply : Window
    {
        private string search = "";
        private Vector2 scroll;
        private readonly List<ThingDef> all;

        public override Vector2 InitialSize => new Vector2(520f, 640f);

        public Dialog_PickSupply()
        {
            doCloseX = true;
            doCloseButton = true;
            absorbInputAroundWindow = true;
            var made = new HashSet<ThingDef>();
            foreach (RecipeDef rd in DefDatabase<RecipeDef>.AllDefsListForReading)
                if (rd.products != null)
                    foreach (ThingDefCountClass p in rd.products)
                        if (p?.thingDef != null && p.thingDef.category == ThingCategory.Item) made.Add(p.thingDef);
            all = made.OrderBy(d => d.label ?? d.defName).ToList();
        }

        public override void DoWindowContents(Rect r)
        {
            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(r.x, r.y, r.width, 32f), "CA_Sup_PickTitle".Translate());
            Text.Font = GameFont.Small;
            search = Widgets.TextField(new Rect(r.x, r.y + 38f, r.width, 28f), search ?? "");
            List<ThingDef> list = all.Where(d => search.NullOrEmpty() || (d.label ?? d.defName).IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
            Rect outR = new Rect(r.x, r.y + 72f, r.width, r.height - 72f - 50f);
            Rect view = new Rect(0f, 0f, outR.width - 16f, list.Count * 30f);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < list.Count; i++)
            {
                Rect row = new Rect(0f, i * 30f, view.width, 30f);
                if (row.yMax < scroll.y || row.y > scroll.y + outR.height) continue;
                ThingDef d = list[i];
                if (i % 2 == 0) Widgets.DrawLightHighlight(row);
                Widgets.DrawHighlightIfMouseover(row);
                try { Widgets.DefIcon(new Rect(row.x + 2f, row.y + 3f, 24f, 24f), d); } catch { }
                Widgets.Label(new Rect(row.x + 32f, row.y + 4f, row.width - 32f, 24f), d.LabelCap);
                TooltipHandler.TipRegion(row, d.description ?? "");
                if (Widgets.ButtonInvisible(row))
                {
                    GoalSupplies.I?.Add(d.defName, d.stackLimit > 1 ? 10 : 1, false);
                    Close();
                }
            }
            Widgets.EndScrollView();
        }
    }
}
