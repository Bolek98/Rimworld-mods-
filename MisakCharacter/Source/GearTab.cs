// GENERATED from MisakTabs by MisakTabs/Tools/split.py – edit MisakTabs, not this file.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.Sound;

namespace MisakCharacter
{
    // ---------------------------------------------------------------------------
    // 0.13.1 Gear tab in the Mišák look (no body figure).
    //  Top: name, role (Steward – Roles), comfortable temperature, carried mass, gear condition.
    //  Left "On the body": one row per body location the pawn wears something on (head, face, neck,
    //  shoulders, torso layers, arms, hands, belt, legs, feet … also modded groups); a few core places
    //  stay visible dashed when empty. Click = info card, "Take off" = the game's remove job.
    //  Middle: weapons (primary + weapons carried in the inventory), overall armor, what Smart
    //  Colonist Gear takes next. Right: inventory by group with eat / drop, free mass.
    //  Bottom: "Gear up now" (Smart Colonist Gear), apparel policy, battle outfit, drug policy.
    //  The original tab is one click away; any drawing error falls back to it.
    // ---------------------------------------------------------------------------
    public static class GearTab
    {
        private static readonly AccessTools.FieldRef<InspectTabBase, Vector2> sizeRef = AccessTools.FieldRefAccess<InspectTabBase, Vector2>("size");
        private static readonly Color Muted = new Color32(160, 167, 174, 255);
        private static readonly Color Dim = new Color32(111, 119, 126, 255);
        public static bool original;
        private static Vector2 leftScroll, rightScroll, midScroll;
        private static int invGroup;   // 0 all, 1 food, 2 medicine, 3 drugs, 4 other

        public static Vector2 Size => TabSize.Fit(MTMod.S.gearW, MTMod.S.gearH, 660f, 430f);

        public static void Patch(Harmony h)
        {
            try
            {
                h.Patch(AccessTools.Method(typeof(ITab_Pawn_Gear), "FillTab"),
                    prefix: new HarmonyMethod(typeof(GearTab), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(GearTab), nameof(Postfix)));
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] gear tab not available: " + e.Message); }
        }

        private static Pawn SelPawn
        {
            get
            {
                Thing t = Find.Selector.SingleSelectedThing;
                if (t is Pawn p) return p;
                if (t is Corpse c) return c.InnerPawn;
                return null;
            }
        }

        private static bool Ours(Pawn p) => MTMod.S.gear && !original && p != null && p.RaceProps.Humanlike;

        public static bool Prefix(ITab_Pawn_Gear __instance)
        {
            Pawn pawn = SelPawn;
            if (!Ours(pawn)) { sizeRef(__instance) = new Vector2(460f, 450f); return true; }
            sizeRef(__instance) = Size;
            try { Draw(new Rect(0f, 0f, Size.x, Size.y), pawn); TabSize.Overlay("gear"); }
            catch (Exception e)
            {
                Look.ResetGui();
                try { Widgets.EndScrollView(); } catch { }
                Log.ErrorOnce("[Mišák Tabs] gear tab: " + e, 0x51A7B24);
                original = true;
                sizeRef(__instance) = new Vector2(460f, 450f);
            }
            return false;
        }

        public static void Postfix(ITab_Pawn_Gear __instance)
        {
            if (!MTMod.S.gear || !original) return;
            Vector2 sz = sizeRef(__instance);
            if (Look.Button(new Rect(sz.x - 126f, sz.y - 30f, 116f, 24f), "MT_OurLook".Translate(), true)) original = false;
        }

        // ---------- control (copied from ITab_Pawn_Gear) ----------
        private static bool CanControl(Pawn p)
        {
            if (p.Dead || p.Downed || p.InMentalState || p.CarriedBy != null) return false;
            if (p.Faction != Faction.OfPlayer && !p.IsPrisonerOfColony) return false;
            if (p.IsPrisonerOfColony && p.Spawned && !p.Map.mapPawns.AnyFreeColonistSpawned) return false;
            if (p.IsPrisonerOfColony && (PrisonBreakUtility.IsPrisonBreaking(p) || (p.CurJob != null && p.CurJob.exitMapOnArrival))) return false;
            return true;
        }

        private static bool CanControlColonist(Pawn p) => CanControl(p) && p.IsColonistPlayerControlled;

        private static bool CanDrop(Pawn p, Thing t, bool inventory, out string why)
        {
            why = null;
            if (!CanControl(p) || !(inventory || CanControlColonist(p) || (p.Spawned && !p.Map.IsPlayerHome))) { why = ""; return false; }
            if (p.IsQuestLodger() && (inventory || !EquipmentUtility.QuestLodgerCanUnequip(t, p))) { why = "DropThingLodger".Translate(); return false; }
            if (t is Apparel ap && p.apparel != null && p.apparel.IsLocked(ap)) { why = "DropThingLocked".Translate(); return false; }
            if (!inventory && p.kindDef.destroyGearOnDrop) { why = ""; return false; }
            return true;
        }

        private static void Drop(Pawn p, Thing t)
        {
            Action act = delegate
            {
                SoundDefOf.Tick_High.PlayOneShotOnCamera();
                var twc = t as ThingWithComps;
                if (t is Apparel ap && p.apparel != null && p.apparel.WornApparel.Contains(ap))
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.RemoveApparel, ap), JobTag.Misc);
                else if (twc != null && p.equipment != null && p.equipment.AllEquipmentListForReading.Contains(twc))
                    p.jobs.TryTakeOrderedJob(JobMaker.MakeJob(JobDefOf.DropEquipment, twc), JobTag.Misc);
                else if (!t.def.destroyOnDrop)
                    p.inventory.innerContainer.TryDrop(t, p.Position, p.Map, ThingPlaceMode.Near, out Thing _);
            };
            if (!ModsConfig.BiotechActive || !MechanitorUtility.TryConfirmBandwidthLossFromDroppingThing(p, t, act)) act();
        }

        // ---------- main ----------
        public static void Draw(Rect r, Pawn pawn)
        {
            Look.Box(r, Look.Bg);
            Rect head = new Rect(r.x, r.y, r.width, 54f);
            PawnTabs.Header(head, pawn, ref original, "gear", ref MTMod.S.gearW, ref MTMod.S.gearH, 660f, 430f);
            float y = head.yMax + 8f;
            Stats(new Rect(r.x + 10f, y, r.width - 20f, 50f), pawn);
            y += 58f;
            bool footer = pawn.IsColonistPlayerControlled && !pawn.Dead;
            float footH = footer ? 40f : 0f;
            Rect body = new Rect(r.x + 10f, y, r.width - 20f, r.yMax - y - 10f - footH);
            float w1 = Mathf.Floor(body.width * 0.35f), w3 = Mathf.Floor(body.width * 0.29f), w2 = body.width - w1 - w3 - 20f;
            Rect c1 = new Rect(body.x, body.y, w1, body.height);
            Rect c2 = new Rect(c1.xMax + 10f, body.y, w2, body.height);
            Rect c3 = new Rect(c2.xMax + 10f, body.y, w3, body.height);
            Worn(c1, pawn);
            Weapons(c2, pawn);
            Inventory(c3, pawn);
            if (footer) Footer(new Rect(r.x + 10f, r.yMax - 10f - 34f, r.width - 20f, 34f), pawn);
        }

        private static void Card(Rect r, string title, string right)
        {
            Look.Box(r, Look.Card, Look.Line);
            Look.Text(new Rect(r.x + 10f, r.y + 6f, r.width * 0.55f, 18f), title.ToUpper(), Muted, GameFont.Tiny);
            if (!right.NullOrEmpty())
                Look.Text(new Rect(r.x + r.width * 0.4f, r.y + 6f, r.width * 0.6f - 10f, 18f), right.Truncate(r.width * 0.6f - 10f), Dim, GameFont.Tiny, TextAnchor.MiddleRight);
        }

        // ---------- top stats ----------
        private static void Stats(Rect r, Pawn p)
        {
            var cards = new List<(string label, string value, float bar, Color c, string tip)>();
            string role = RoleOf(p);
            if (!role.NullOrEmpty()) cards.Add(("MT_G_Role".Translate(), role, -1f, Look.Green, null));
            try
            {
                float mn = p.GetStatValue(StatDefOf.ComfyTemperatureMin), mx = p.GetStatValue(StatDefOf.ComfyTemperatureMax);
                cards.Add(("MT_G_Comfy".Translate(), mn.ToStringTemperature("F0") + " ~ " + mx.ToStringTemperature("F0"), -1f, Color.white, "ComfyTemperatureRange".Translate()));
            }
            catch { }
            try
            {
                float m = MassUtility.GearAndInventoryMass(p), cap = MassUtility.Capacity(p);
                cards.Add(("MT_G_Mass".Translate(), m.ToString("0.#") + " / " + cap.ToString("0.#") + " kg", cap > 0f ? m / cap : 0f, m > cap ? Look.Red : Look.Green,
                    "MassCarried".Translate(m.ToString("0.##"), cap.ToString("0.##"))));
            }
            catch { }
            var hp = new List<float>();
            if (p.apparel != null) foreach (Apparel a in p.apparel.WornApparel) if (a.def.useHitPoints) hp.Add((float)a.HitPoints / a.MaxHitPoints);
            if (p.equipment?.Primary != null && p.equipment.Primary.def.useHitPoints) hp.Add((float)p.equipment.Primary.HitPoints / p.equipment.Primary.MaxHitPoints);
            if (hp.Count > 0)
            {
                float avg = hp.Average();
                cards.Add(("MT_G_Condition".Translate(), avg.ToStringPercent(), avg, avg < 0.5f ? Look.Red : avg < 0.8f ? Look.Orange : Look.Green, "MT_G_ConditionTip".Translate()));
            }
            if (cards.Count == 0) return;
            float w = (r.width - 8f * (cards.Count - 1)) / cards.Count;
            for (int i = 0; i < cards.Count; i++)
            {
                var c = cards[i];
                Rect cr = new Rect(r.x + i * (w + 8f), r.y, w, r.height);
                Look.Box(cr, Look.Card, Look.Line);
                Look.Text(new Rect(cr.x + 10f, cr.y + 4f, cr.width - 20f, 16f), c.label, Muted, GameFont.Tiny);
                Look.Text(new Rect(cr.x + 10f, cr.y + 18f, cr.width - 20f, 22f), c.value.Truncate(cr.width - 20f), c.c == Color.white ? Color.white : c.c);
                if (c.bar >= 0f) Look.Bar(new Rect(cr.x + 10f, cr.yMax - 8f, cr.width - 20f, 4f), c.bar, c.c);
                if (!c.tip.NullOrEmpty()) TooltipHandler.TipRegion(cr, c.tip);
            }
        }

        // Steward – Roles (optional): ColonyForeman.RolesGame.Of(pawn) → RoleWork.Label(role)
        private static MethodInfo roleOf, roleLabel;
        private static bool roleTried;

        private static string RoleOf(Pawn p)
        {
            try
            {
                if (!roleTried)
                {
                    roleTried = true;
                    Type g = GenTypes.GetTypeInAnyAssembly("ColonyForeman.RolesGame");
                    Type w = GenTypes.GetTypeInAnyAssembly("ColonyForeman.RoleWork");
                    roleOf = g != null ? AccessTools.Method(g, "Of", new[] { typeof(Pawn) }) : null;
                    roleLabel = w != null ? AccessTools.Method(w, "Label") : null;
                }
                if (roleOf == null || roleLabel == null) return null;
                object role = roleOf.Invoke(null, new object[] { p });
                if (role == null || role.ToString() == "None") return null;
                return roleLabel.Invoke(null, new[] { role }) as string;
            }
            catch { return null; }
        }

        // ---------- left: worn apparel by body location ----------
        private struct Slot
        {
            public string key, label;
            public int order;
            public Apparel item;
        }

        private static readonly string[] CoreKeys = { "head", "face", "torsoShell", "torsoMiddle", "torsoSkin", "belt", "legs" };

        private static int Order(string key)
        {
            switch (key)
            {
                case "head": return 0;
                case "face": return 1;
                case "ears": return 2;
                case "neck": return 3;
                case "shoulders": return 4;
                case "torsoShell": return 5;
                case "torsoMiddle": return 6;
                case "torsoSkin": return 7;
                case "torso": return 8;
                case "arms": return 9;
                case "hands": return 10;
                case "belt": return 11;
                case "waist": return 12;
                case "legs": return 13;
                case "feet": return 14;
                default: return 50;
            }
        }

        private static string KeyLabel(string key, string fallback)
        {
            switch (key)
            {
                case "head": case "face": case "ears": case "neck": case "shoulders": case "torsoShell": case "torsoMiddle":
                case "torsoSkin": case "torso": case "arms": case "hands": case "belt": case "waist": case "legs": case "feet":
                    return ("MT_G_Loc_" + key).Translate();
                default: return fallback;
            }
        }

        private static readonly string[] GroupPriority = { "FullHead", "UpperHead", "Eyes", "Teeth", "Mouth", "Ears", "Neck", "Torso", "Shoulders", "Arms", "Hands", "LeftHand", "RightHand", "Waist", "Legs", "Feet" };

        private static void Locate(Apparel a, out string key, out string label)
        {
            ApparelProperties ap = a.def.apparel;
            List<BodyPartGroupDef> groups = ap?.bodyPartGroups ?? new List<BodyPartGroupDef>();
            ApparelLayerDef last = ap?.LastLayer;
            if (last == ApparelLayerDefOf.Belt) { key = "belt"; label = KeyLabel(key, null); return; }
            BodyPartGroupDef main = null;
            foreach (string n in GroupPriority)
            {
                main = groups.FirstOrDefault(g => g.defName == n);
                if (main != null) break;
            }
            if (main == null) main = groups.FirstOrDefault();
            if (main == null) { key = "other:" + (last?.defName ?? "?"); label = last?.LabelCap ?? (string)"MT_G_Loc_other".Translate(); return; }
            switch (main.defName)
            {
                case "FullHead": case "UpperHead": key = "head"; break;
                case "Eyes": case "Teeth": case "Mouth": key = "face"; break;
                case "Ears": key = "ears"; break;
                case "Neck": key = "neck"; break;
                case "Shoulders": key = "shoulders"; break;
                case "Arms": key = "arms"; break;
                case "Hands": case "LeftHand": case "RightHand": key = "hands"; break;
                case "Waist": key = "waist"; break;
                case "Legs": key = "legs"; break;
                case "Feet": key = "feet"; break;
                case "Torso":
                    if (last == ApparelLayerDefOf.Shell) key = "torsoShell";
                    else if (last == ApparelLayerDefOf.Middle) key = "torsoMiddle";
                    else if (last == ApparelLayerDefOf.OnSkin) key = "torsoSkin";
                    else key = "torso";
                    break;
                default:
                    key = "group:" + main.defName;
                    label = main.LabelCap;
                    if (last != null && groups.Count > 0) label += " – " + last.label;
                    return;
            }
            label = KeyLabel(key, main.LabelCap);
        }

        private static readonly List<Slot> slots = new List<Slot>();

        private static void Worn(Rect r, Pawn p)
        {
            slots.Clear();
            var have = new HashSet<string>();
            if (p.apparel != null)
                foreach (Apparel a in p.apparel.WornApparel)
                {
                    Locate(a, out string key, out string label);
                    slots.Add(new Slot { key = key, label = label, order = Order(key), item = a });
                    have.Add(key);
                }
            foreach (string k in CoreKeys)
                if (!have.Contains(k)) slots.Add(new Slot { key = k, label = KeyLabel(k, k), order = Order(k), item = null });
            slots.Sort((a, b) => a.order != b.order ? a.order.CompareTo(b.order) : string.Compare(a.label, b.label, StringComparison.Ordinal));

            Card(r, "MT_G_Worn".Translate(), "MT_G_Pieces".Translate(p.apparel?.WornApparel.Count ?? 0));
            Rect list = new Rect(r.x + 8f, r.y + 28f, r.width - 16f, r.height - 36f);
            const float rowH = 46f;
            float h = slots.Count * (rowH + 5f);
            Rect view = new Rect(0f, 0f, list.width - (h > list.height ? 16f : 0f), h);
            Widgets.BeginScrollView(list, ref leftScroll, view);
            try
            {
                float y = 0f;
                foreach (Slot s in slots)
                {
                    WornRow(new Rect(0f, y, view.width, rowH), p, s);
                    y += rowH + 5f;
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void WornRow(Rect r, Pawn p, Slot s)
        {
            Apparel a = s.item;
            if (a == null)
            {
                Widgets.DrawBoxSolid(r, Look.Card2);
                GUI.color = Look.Line;
                // dashed look: thin border segments
                for (float x = r.x; x < r.xMax; x += 8f) { Widgets.DrawLineHorizontal(x, r.y, Mathf.Min(4f, r.xMax - x)); Widgets.DrawLineHorizontal(x, r.yMax - 1f, Mathf.Min(4f, r.xMax - x)); }
                GUI.color = Color.white;
                Look.Text(new Rect(r.x + 8f, r.y, 96f, r.height), s.label.ToUpper().Truncate(92f), Dim, GameFont.Tiny);
                Look.Text(new Rect(r.x + 108f, r.y, r.width - 116f, r.height), "MT_G_Empty".Translate(), Dim, GameFont.Tiny);
                return;
            }
            float hpPct = a.def.useHitPoints ? (float)a.HitPoints / a.MaxHitPoints : 1f;
            QualityCategory q;
            bool hasQ = a.TryGetQuality(out q);
            Color border = hpPct < 0.5f ? Look.Orange : (hasQ && q >= QualityCategory.Excellent ? Look.Green : Look.Line);
            Look.Box(r, Look.Card2, border);
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            Look.Text(new Rect(r.x + 8f, r.y, 96f, r.height), s.label.ToUpper().Truncate(92f), Muted, GameFont.Tiny);
            bool drop = CanDrop(p, a, false, out string why);
            float btnW = 78f;
            float nameW = r.width - 112f - (drop || !why.NullOrEmpty() ? btnW + 8f : 4f);
            Widgets.ThingIcon(new Rect(r.x + 104f, r.y + 9f, 28f, 28f), a);
            string name = a.LabelNoCount.CapitalizeFirst();
            if (p.outfits != null && p.outfits.forcedHandler.IsForced(a)) name += " · " + "ApparelForcedLower".Translate();
            Look.Text(new Rect(r.x + 138f, r.y + 4f, nameW - 30f, 20f), name.Truncate(nameW - 30f), Color.white);
            string sub = (hasQ ? q.GetLabel() + " · " : "") + (a.def.useHitPoints ? hpPct.ToStringPercent() : "") + (hpPct < 0.5f ? " ⚠" : "");
            if (a.WornByCorpse) sub += " · " + "WasWornByCorpse".Translate();
            Look.Text(new Rect(r.x + 138f, r.y + 23f, nameW - 30f, 18f), sub.Truncate(nameW - 30f), hpPct < 0.5f ? Look.Orange : Muted, GameFont.Tiny);
            Rect info = new Rect(r.x, r.y, r.width - (drop || !why.NullOrEmpty() ? btnW + 8f : 0f), r.height);
            TooltipHandler.TipRegion(info, a.GetTooltip());
            if (Widgets.ButtonInvisible(info)) Find.WindowStack.Add(new Dialog_InfoCard(a));
            if (drop)
            {
                if (Look.Button(new Rect(r.xMax - btnW - 6f, r.y + 11f, btnW, 24f), "MT_G_TakeOff".Translate())) Drop(p, a);
            }
            else if (!why.NullOrEmpty())
            {
                Rect lr = new Rect(r.xMax - btnW - 6f, r.y + 11f, btnW, 24f);
                Look.Text(lr, "ApparelLockedLower".Translate(), Dim, GameFont.Tiny, TextAnchor.MiddleCenter);
                TooltipHandler.TipRegion(lr, why);
            }
        }

        // ---------- middle: weapons, armor, next ----------
        private static void Weapons(Rect r, Pawn p)
        {
            Card(r, "MT_G_Weapons".Translate(), SCG.Loaded ? "Smart Colonist Gear" : null);
            Rect inner = new Rect(r.x + 8f, r.y + 28f, r.width - 16f, r.height - 36f);
            var weapons = new List<(ThingWithComps t, bool primary)>();
            if (p.equipment != null) foreach (ThingWithComps e in p.equipment.AllEquipmentListForReading) weapons.Add((e, e == p.equipment.Primary));
            if (p.inventory != null) foreach (Thing t in p.inventory.innerContainer) if (t.def.IsWeapon && t is ThingWithComps tw) weapons.Add((tw, false));
            string next = SCG.Next(p);
            float h = Mathf.Max(1, weapons.Count) * 62f + 14f + 3 * 24f + 30f + (next.NullOrEmpty() ? 0f : 52f);
            Rect view = new Rect(0f, 0f, inner.width - (h > inner.height ? 16f : 0f), h);
            Widgets.BeginScrollView(inner, ref midScroll, view);
            try
            {
                float y = 0f;
                if (weapons.Count == 0)
                {
                    Look.Text(new Rect(0f, y, view.width, 56f), "MT_G_NoWeapon".Translate(), Dim, GameFont.Small, TextAnchor.MiddleCenter);
                    y += 62f;
                }
                foreach (var w in weapons)
                {
                    WeaponRow(new Rect(0f, y, view.width, 56f), p, w.t, w.primary);
                    y += 62f;
                }
                y += 6f;
                Look.Text(new Rect(0f, y, view.width, 18f), "MT_G_Armor".Translate().ToString().ToUpper(), Muted, GameFont.Tiny);
                Look.Text(new Rect(0f, y, view.width, 18f), "MT_G_ArmorAll".Translate(), Dim, GameFont.Tiny, TextAnchor.MiddleRight);
                y += 22f;
                ArmorBar(new Rect(0f, y, view.width, 22f), p, StatDefOf.ArmorRating_Sharp, "ArmorSharp".Translate(), Look.Green); y += 24f;
                ArmorBar(new Rect(0f, y, view.width, 22f), p, StatDefOf.ArmorRating_Blunt, "ArmorBlunt".Translate(), Look.Green); y += 24f;
                ArmorBar(new Rect(0f, y, view.width, 22f), p, StatDefOf.ArmorRating_Heat, "ArmorHeat".Translate(), Look.Orange); y += 24f;
                if (!next.NullOrEmpty())
                {
                    y += 8f;
                    Look.Text(new Rect(0f, y, view.width, 18f), "MT_G_Next".Translate().ToString().ToUpper(), Muted, GameFont.Tiny);
                    y += 20f;
                    Look.Text(new Rect(0f, y, view.width, 22f), next.Truncate(view.width), Look.Green, GameFont.Small);
                }
            }
            finally { Widgets.EndScrollView(); }
        }

        private static void WeaponRow(Rect r, Pawn p, ThingWithComps w, bool primary)
        {
            Look.Box(r, Look.Card2, primary ? Look.Green : Look.Line);
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            Look.Box(new Rect(r.x + 8f, r.y + 8f, 40f, 40f), Look.Bg, Look.Line);
            Widgets.ThingIcon(new Rect(r.x + 12f, r.y + 12f, 32f, 32f), w);
            float btnW = 74f;
            bool canAct = CanControlColonist(p);
            float textW = r.width - 60f - (canAct ? btnW + 10f : 6f);
            string tag = primary ? "MT_G_Primary".Translate() : "MT_G_Sidearm".Translate();
            Text.Font = GameFont.Small;
            string name = w.LabelNoCount.CapitalizeFirst();
            float nw = Mathf.Min(textW - 70f, Text.CalcSize(name).x);
            Look.Text(new Rect(r.x + 56f, r.y + 6f, nw, 22f), name.Truncate(nw), Color.white);
            Text.Font = GameFont.Tiny;
            float tw = Text.CalcSize(tag).x + 14f;
            Text.Font = GameFont.Small;
            Rect tr = new Rect(r.x + 60f + nw, r.y + 8f, tw, 18f);
            Look.Box(tr, primary ? Look.GreenBg : Look.Card, primary ? Look.Green : Look.Blue);
            Look.Text(tr, tag, primary ? Look.Green : Look.Blue, GameFont.Tiny, TextAnchor.MiddleCenter);
            Look.Text(new Rect(r.x + 56f, r.y + 30f, textW, 20f), WeaponLine(w, p).Truncate(textW), Muted, GameFont.Tiny);
            Rect info = new Rect(r.x, r.y, r.width - (canAct ? btnW + 10f : 0f), r.height);
            TooltipHandler.TipRegion(info, w.GetTooltip());
            if (Widgets.ButtonInvisible(info)) Find.WindowStack.Add(new Dialog_InfoCard(w));
            if (!canAct) return;
            Rect b = new Rect(r.xMax - btnW - 8f, r.y + 16f, btnW, 24f);
            if (primary)
            {
                if (CanDrop(p, w, false, out string why))
                {
                    if (Look.Button(b, "MT_G_Drop".Translate())) Drop(p, w);
                }
                else if (!why.NullOrEmpty()) TooltipHandler.TipRegion(b, why);
            }
            else if (Look.Button(b, "MT_G_ToHand".Translate()))
                ToHand(p, w);
        }

        // swaps a weapon from the inventory into the hands (the old one goes into the inventory)
        private static void ToHand(Pawn p, ThingWithComps w)
        {
            try
            {
                if (p.equipment == null || !p.inventory.innerContainer.Contains(w)) return;
                if (EquipmentUtility.CanEquip(w, p, out string reason) == false)
                {
                    Messages.Message(reason, p, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                ThingWithComps old = p.equipment.Primary;
                if (old != null && !p.equipment.TryTransferEquipmentToContainer(old, p.inventory.innerContainer))
                    return;
                if (p.inventory.innerContainer.Remove(w))
                {
                    p.equipment.AddEquipment(w);
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                }
            }
            catch (Exception e) { Log.Warning("[Mišák Tabs] weapon to hand: " + e.Message); }
        }

        private static string WeaponLine(ThingWithComps w, Pawn p)
        {
            var bits = new List<string>();
            try
            {
                if (w.TryGetQuality(out QualityCategory q)) bits.Add(q.GetLabel());
                if (w.def.useHitPoints) bits.Add(((float)w.HitPoints / w.MaxHitPoints).ToStringPercent());
                VerbProperties v = w.def.Verbs?.FirstOrDefault(x => x.isPrimary) ?? w.def.Verbs?.FirstOrDefault();
                if (w.def.IsRangedWeapon && v != null)
                {
                    if (v.burstShotCount > 1) bits.Add("MT_G_Burst".Translate(v.burstShotCount));
                    bits.Add("MT_G_Range".Translate(v.range.ToString("0")));
                    float dmg = v.defaultProjectile?.projectile != null ? v.defaultProjectile.projectile.GetDamageAmount(w) : 0f;
                    float cycle = v.warmupTime + w.GetStatValue(StatDefOf.RangedWeapon_Cooldown) + (v.burstShotCount - 1) * v.ticksBetweenBurstShots / 60f;
                    if (dmg > 0f && cycle > 0f) bits.Add("DPS " + (dmg * v.burstShotCount / cycle).ToString("0.0"));
                }
                else
                {
                    float dps = w.GetStatValue(StatDefOf.MeleeWeapon_AverageDPS);
                    if (dps > 0f) bits.Add("DPS " + dps.ToString("0.0"));
                }
            }
            catch { }
            return string.Join(" · ", bits);
        }

        private static float OverallArmor(Pawn p, StatDef stat)
        {
            float total = 0f;
            float natural = Mathf.Clamp01(p.GetStatValue(stat) / 2f);
            List<BodyPartRecord> parts = p.RaceProps.body.AllParts;
            List<Apparel> worn = p.apparel?.WornApparel;
            for (int i = 0; i < parts.Count; i++)
            {
                float left = 1f - natural;
                if (worn != null)
                    for (int j = 0; j < worn.Count; j++)
                        if (worn[j].def.apparel.CoversBodyPart(parts[i]))
                            left *= 1f - Mathf.Clamp01(worn[j].GetStatValue(stat) / 2f);
                total += parts[i].coverageAbs * (1f - left);
            }
            return Mathf.Clamp(total * 2f, 0f, 2f);
        }

        private static void ArmorBar(Rect r, Pawn p, StatDef stat, string label, Color c)
        {
            float v = 0f;
            try { v = OverallArmor(p, stat); } catch { }
            Look.Text(new Rect(r.x, r.y, 70f, r.height), label.Truncate(68f), Color.white, GameFont.Tiny);
            Look.Bar(new Rect(r.x + 74f, r.y + 8f, r.width - 124f, 7f), v, c);
            Look.Text(new Rect(r.xMax - 46f, r.y, 46f, r.height), v.ToStringPercent(), Color.white, GameFont.Tiny, TextAnchor.MiddleRight);
        }

        // ---------- right: inventory ----------
        private static int GroupOf(Thing t)
        {
            ThingDef d = t.def;
            if (d.IsDrug) return 3;
            if (d.IsMedicine) return 2;
            if (d.IsNutritionGivingIngestible) return 1;
            return 4;
        }

        private static void Inventory(Rect r, Pawn p)
        {
            var items = new List<Thing>();
            if (p.inventory != null) foreach (Thing t in p.inventory.innerContainer) if (!(t.def.IsWeapon && t is ThingWithComps)) items.Add(t);
            Card(r, "MT_G_Inventory".Translate(), items.Count.ToString());
            float y = r.y + 28f;
            string[] tabs = { "MT_G_All", "MT_G_Food", "MT_G_Med", "MT_G_Drugs", "MT_G_Other" };
            float cx = r.x + 8f;
            for (int i = 0; i < tabs.Length; i++)
            {
                string l = tabs[i].Translate();
                Text.Font = GameFont.Tiny;
                float w = Text.CalcSize(l).x + 14f;
                Text.Font = GameFont.Small;
                if (cx + w > r.xMax - 8f) { cx = r.x + 8f; y += 22f; }
                if (Look.Chip(new Rect(cx, y, w, 20f), l, invGroup == i)) invGroup = i;
                cx += w + 4f;
            }
            y += 26f;
            List<Thing> shown = invGroup == 0 ? items : items.Where(t => GroupOf(t) == invGroup).ToList();
            float massH = 46f;
            Rect list = new Rect(r.x + 8f, y, r.width - 16f, r.yMax - y - massH - 8f);
            const float rowH = 32f;
            float h = shown.Count * rowH;
            Rect view = new Rect(0f, 0f, list.width - (h > list.height ? 16f : 0f), Mathf.Max(h, 1f));
            Widgets.BeginScrollView(list, ref rightScroll, view);
            try
            {
                if (shown.Count == 0) Look.Text(new Rect(0f, 0f, view.width, 30f), "MT_G_Nothing".Translate(), Dim, GameFont.Tiny, TextAnchor.MiddleCenter);
                float ry = 0f;
                foreach (Thing t in shown.ToList())
                {
                    InvRow(new Rect(0f, ry, view.width, rowH - 2f), p, t);
                    ry += rowH;
                }
            }
            finally { Widgets.EndScrollView(); }
            // free mass
            try
            {
                float m = MassUtility.GearAndInventoryMass(p), cap = MassUtility.Capacity(p);
                float yy = r.yMax - massH - 2f;
                Look.Text(new Rect(r.x + 10f, yy, r.width - 20f, 16f), "MT_G_FreeMass".Translate().ToString().ToUpper(), Muted, GameFont.Tiny);
                Look.Bar(new Rect(r.x + 10f, yy + 18f, r.width - 20f, 6f), cap > 0f ? m / cap : 0f, m > cap ? Look.Red : Look.Green);
                Look.Text(new Rect(r.x + 10f, yy + 26f, r.width - 20f, 16f), "MT_G_FreeKg".Translate(Mathf.Max(0f, cap - m).ToString("0.#")), Dim, GameFont.Tiny);
            }
            catch { }
        }

        private static void InvRow(Rect r, Pawn p, Thing t)
        {
            if (Mouse.IsOver(r)) Widgets.DrawHighlight(r);
            Widgets.DrawBoxSolid(new Rect(r.x, r.yMax, r.width, 1f), Look.Card2);
            Widgets.ThingIcon(new Rect(r.x + 2f, r.y + 2f, 26f, 26f), t);
            float bx = r.xMax;
            bool drop = CanDrop(p, t, true, out string _);
            if (drop)
            {
                bx -= 62f;
                if (Look.Button(new Rect(bx, r.y + 4f, 60f, 22f), "MT_G_Drop".Translate())) Drop(p, t);
            }
            if (CanControlColonist(p) && FoodUtility.WillIngestFromInventoryNow(p, t))
            {
                bx -= 56f;
                Rect er = new Rect(bx, r.y + 4f, 54f, 22f);
                if (Look.Button(er, t.def.IsDrug || t.def.IsMedicine ? "MT_G_Use".Translate() : "MT_G_Eat".Translate()))
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    FoodUtility.IngestFromInventoryNow(p, t);
                }
                TooltipHandler.TipRegionByKey(er, "ConsumeThing", t.LabelNoCount, t);
            }
            string cnt = "×" + t.stackCount;
            Look.Text(new Rect(bx - 44f, r.y, 40f, r.height), cnt, Muted, GameFont.Tiny, TextAnchor.MiddleRight);
            float nw = bx - 48f - 34f;
            Rect nr = new Rect(r.x + 34f, r.y, nw, r.height);
            Look.Text(nr, t.LabelNoCount.CapitalizeFirst().Truncate(nw), Color.white, GameFont.Tiny);
            TooltipHandler.TipRegion(nr, t.GetTooltip());
            if (Widgets.ButtonInvisible(nr)) Find.WindowStack.Add(new Dialog_InfoCard(t));
        }

        // ---------- footer ----------
        private static void Footer(Rect r, Pawn p)
        {
            float x = r.x;
            if (SCG.CanGearUp(p))
            {
                Rect b = new Rect(x, r.y, 190f, r.height);
                if (Look.Button(b, "⟳ " + "MT_G_GearUp".Translate(), true)) SCG.GearUp(p);
                TooltipHandler.TipRegion(b, "MT_G_GearUpTip".Translate());
                x += 198f;
            }
            if (p.outfits != null && Current.Game?.outfitDatabase != null)
            {
                ApparelPolicy cur = p.outfits.CurrentApparelPolicy;
                Rect b = new Rect(x, r.y, 190f, r.height);
                if (Look.Button(b, ("MT_G_Outfit".Translate() + ": " + (cur?.label ?? "-") + " ▾").Truncate(186f)))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (ApparelPolicy o in Current.Game.outfitDatabase.AllOutfits)
                    {
                        ApparelPolicy oo = o;
                        opts.Add(new FloatMenuOption(oo.label, () => p.outfits.CurrentApparelPolicy = oo));
                    }
                    opts.Add(new FloatMenuOption("AssignTabEdit".Translate() + "…", () => Find.WindowStack.Add(new Dialog_ManageApparelPolicies(p.outfits.CurrentApparelPolicy))));
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                x += 198f;
                if (SCG.HasBattle && cur != null)
                {
                    ApparelPolicy battle = SCG.BattleFor(cur);
                    Rect bb = new Rect(x, r.y, 190f, r.height);
                    if (Look.Button(bb, ("MT_G_Battle".Translate() + ": " + (battle?.label ?? (string)"MT_G_None".Translate()) + " ▾").Truncate(186f)))
                    {
                        var opts = new List<FloatMenuOption> { new FloatMenuOption("MT_G_None".Translate(), () => SCG.SetBattle(cur, null)) };
                        foreach (ApparelPolicy o in Current.Game.outfitDatabase.AllOutfits)
                        {
                            if (o == cur) continue;
                            ApparelPolicy oo = o;
                            opts.Add(new FloatMenuOption(oo.label, () => SCG.SetBattle(cur, oo)));
                        }
                        Find.WindowStack.Add(new FloatMenu(opts));
                    }
                    TooltipHandler.TipRegion(bb, "MT_G_BattleTip".Translate());
                    x += 198f;
                }
            }
            if (p.drugs != null && Current.Game?.drugPolicyDatabase != null && x + 190f <= r.xMax)
            {
                DrugPolicy cur = p.drugs.CurrentPolicy;
                Rect b = new Rect(x, r.y, 190f, r.height);
                if (Look.Button(b, ("MT_G_DrugPolicy".Translate() + ": " + (cur?.label ?? "-") + " ▾").Truncate(186f)))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (DrugPolicy d in Current.Game.drugPolicyDatabase.AllPolicies)
                    {
                        DrugPolicy dd = d;
                        opts.Add(new FloatMenuOption(dd.label, () => p.drugs.CurrentPolicy = dd));
                    }
                    opts.Add(new FloatMenuOption("AssignTabEdit".Translate() + "…", () => Find.WindowStack.Add(new Dialog_ManageDrugPolicies(p.drugs.CurrentPolicy))));
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
            }
        }

        // ---------- Smart Colonist Gear (optional, by reflection) ----------
        private static class SCG
        {
            private static bool tried;
            private static MethodInfo next, battleFor, setBattle, equipNow;
            private static Type mapComp;

            private static void Init()
            {
                if (tried) return;
                tried = true;
                try
                {
                    Type ov = GenTypes.GetTypeInAnyAssembly("ResearchedLoadout.GearOverview");
                    Type bg = GenTypes.GetTypeInAnyAssembly("ResearchedLoadout.BattleGear");
                    mapComp = GenTypes.GetTypeInAnyAssembly("ResearchedLoadout.ResearchedLoadoutMapComponent");
                    if (ov != null) next = AccessTools.Method(ov, "Next", new[] { typeof(Pawn), typeof(Map) });
                    if (bg != null)
                    {
                        battleFor = AccessTools.Method(bg, "BattleFor", new[] { typeof(ApparelPolicy) });
                        setBattle = AccessTools.Method(bg, "SetBattle", new[] { typeof(ApparelPolicy), typeof(ApparelPolicy) });
                    }
                    if (mapComp != null) equipNow = AccessTools.Method(mapComp, "EquipNow", new[] { typeof(Pawn) });
                }
                catch { }
            }

            public static bool Loaded { get { Init(); return mapComp != null; } }
            public static bool HasBattle { get { Init(); return battleFor != null && setBattle != null; } }

            public static string Next(Pawn p)
            {
                Init();
                if (next == null || p.MapHeld == null || !p.IsColonist) return null;
                try { return next.Invoke(null, new object[] { p, p.MapHeld }) as string; } catch { return null; }
            }

            public static ApparelPolicy BattleFor(ApparelPolicy work)
            {
                try { return battleFor?.Invoke(null, new object[] { work }) as ApparelPolicy; } catch { return null; }
            }

            public static void SetBattle(ApparelPolicy work, ApparelPolicy battle)
            {
                try { setBattle?.Invoke(null, new object[] { work, battle }); } catch { }
            }

            private static MapComponent Comp(Pawn p)
            {
                if (mapComp == null || p.Map == null) return null;
                foreach (MapComponent c in p.Map.components) if (mapComp.IsInstanceOfType(c)) return c;
                return null;
            }

            public static bool CanGearUp(Pawn p)
            {
                Init();
                return equipNow != null && p.Spawned && p.IsColonistPlayerControlled && Comp(p) != null;
            }

            public static void GearUp(Pawn p)
            {
                try
                {
                    MapComponent c = Comp(p);
                    if (c == null) return;
                    equipNow.Invoke(c, new object[] { p });
                    SoundDefOf.Click.PlayOneShotOnCamera();
                }
                catch (Exception e) { Log.Warning("[Mišák Tabs] gear up: " + e.Message); }
            }
        }
    }
}
