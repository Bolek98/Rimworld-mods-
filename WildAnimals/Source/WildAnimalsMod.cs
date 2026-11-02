using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace WildAnimals
{
    public class WildAnimalsMod : Mod
    {
        public static WASettings Settings;
        private int tab;
        private BiomeDef sel;
        private Dictionary<PawnKindDef, float> edit;
        private BiomeDef editFor;
        private Vector2 leftScroll, rightScroll, mapScroll;
        private string biomeSearch = "", animalSearch = "";
        private bool showAll;

        public WildAnimalsMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<WASettings>();
            MNews.Register(content, WA.Version);
            MUI.Init(WASettings.Recommended(), new[] { "enabled", "respawnHours", "groupFactor", "predatorMaxPct", "predatorHomeRadius", "rareLetters", "pressureAlert" });
            var h = new Harmony("bolek98.wildanimals");
            h.PatchAll();
            MUI.PatchWindow(h, typeof(WildAnimalsMod));
        }

        public override string SettingsCategory() => "WA_Title".Translate();

        public override void DoSettingsWindowContents(Rect inRect)
        {
            WASettings s = Settings;
            MUI.Version(inRect, "v" + WA.Version + " · Bolek98");
            float y = inRect.y;
            y += MUI.RecommendedCard(new Rect(inRect.x, y, inRect.width, 0f), s, s.recommended, v =>
            {
                s.recommended = v;
                if (v) { MUI.ApplyAll(s); s.recommended = true; edit = null; }
            }, new List<string> { "WA_Bullet1".Translate(), "WA_Bullet2".Translate(), "WA_Bullet3".Translate(), "WA_Bullet4".Translate() }) + 6f;
            if (s.recommended)
            {
                // 0.1.1: nothing is locked – changing anything switches the recommended settings off
                Rect note = new Rect(inRect.x, y, inRect.width, 24f);
                MUI.Fill(note, MUI.CardBg2);
                U.Label(note.ContractedBy(8f, 0f), "WA_EditNote".Translate(), MUI.Muted, GameFont.Tiny, TextAnchor.MiddleLeft);
                y += 28f;
            }
            var tabs = new List<TabRecord>
            {
                new TabRecord("WA_TabBiomes".Translate(), () => tab = 0, tab == 0),
                new TabRecord("WA_TabRules".Translate(), () => tab = 1, tab == 1),
                new TabRecord("WA_TabMap".Translate(), () => tab = 2, tab == 2)
            };
            Rect body = new Rect(inRect.x, y + 32f, inRect.width, inRect.yMax - y - 32f);
            TabDrawer.DrawTabs(body, tabs, 1, 200f);
            body = body.ContractedBy(10f);
            if (tab == 0) DrawBiomes(body);
            else if (tab == 1) DrawRules(body);
            else DrawMap(body);
        }

        // ---------------- biomes ----------------
        private void DrawBiomes(Rect r)
        {
            WASettings s = Settings;
            Rect left = new Rect(r.x, r.y, 280f, r.height);
            Rect right = new Rect(left.xMax + 12f, r.y, r.width - left.width - 12f, r.height);
            float ly = left.y;
            float hw = (left.width - 6f) / 2f;
            if (Widgets.ButtonText(new Rect(left.x, ly, hw, 28f), "WA_Presets".Translate()))
            {
                var opts = new List<FloatMenuOption>();
                for (int i = 0; i < 4; i++)
                {
                    int which = i;
                    opts.Add(new FloatMenuOption(("WA_Preset" + which).Translate(), () =>
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("WA_PresetConfirm".Translate(("WA_Preset" + which).Translate()), delegate
                        {
                            WA.Preset(which);
                            edit = null;
                            Messages.Message("WA_PresetDone".Translate(("WA_Preset" + which).Translate()), MessageTypeDefOf.NeutralEvent, false);
                        }, true))));
                }
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(new Rect(left.x, ly, hw, 28f), "WA_PresetsTip".Translate());
            if (Widgets.ButtonText(new Rect(left.x + hw + 6f, ly, hw, 28f), "WA_Share".Translate()))
            {
                Find.WindowStack.Add(new FloatMenu(new List<FloatMenuOption>
                {
                    new FloatMenuOption("WA_ShareCopy".Translate(), () =>
                    {
                        GUIUtility.systemCopyBuffer = WA.Export();
                        Messages.Message("WA_ShareCopied".Translate(), MessageTypeDefOf.NeutralEvent, false);
                    }),
                    new FloatMenuOption("WA_SharePaste".Translate(), () =>
                    {
                        string code = GUIUtility.systemCopyBuffer;
                        Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("WA_SharePasteConfirm".Translate(), delegate
                        {
                            int n = WA.Import(code);
                            edit = null;
                            Messages.Message(n < 0 ? "WA_ShareBad".Translate() : "WA_ShareRead".Translate(n), n < 0 ? MessageTypeDefOf.RejectInput : MessageTypeDefOf.NeutralEvent, false);
                        }, true));
                    })
                }));
            }
            TooltipHandler.TipRegion(new Rect(left.x + hw + 6f, ly, hw, 28f), "WA_ShareTip".Translate());
            ly += 34f;
            biomeSearch = Widgets.TextField(new Rect(left.x, ly, left.width, 28f), biomeSearch);
            if (biomeSearch.NullOrEmpty())
                U.Label(new Rect(left.x + 6f, ly, left.width, 28f), "WA_SearchBiome".Translate(), MUI.Muted, GameFont.Tiny);
            ly += 32f;
            var biomes = DefDatabase<BiomeDef>.AllDefsListForReading
                .Where(b => b.animalDensity > 0f || b.AllWildAnimals.Any() || s.lists.ContainsKey(b.defName))
                .Where(b => biomeSearch.NullOrEmpty() || WA.Label(b).IndexOf(biomeSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderBy(b => WA.Label(b)).ToList();
            Rect outR = new Rect(left.x, ly, left.width, left.yMax - ly);
            Rect view = new Rect(0f, 0f, outR.width - 16f, biomes.Count * 42f);
            Widgets.BeginScrollView(outR, ref leftScroll, view);
            for (int i = 0; i < biomes.Count; i++)
            {
                BiomeDef b = biomes[i];
                Rect row = new Rect(0f, i * 42f, view.width, 40f);
                if (sel == b) MUI.Fill(row, MUI.OnBg);
                else if (Mouse.IsOver(row)) MUI.Fill(row, MUI.CardBg2);
                bool custom = WA.Custom(b);
                U.Label(new Rect(row.x + 6f, row.y + 1f, row.width - 12f, 22f), WA.Label(b), custom ? MUI.Accent : MUI.Text1);
                U.Label(new Rect(row.x + 6f, row.y + 19f, row.width - 12f, 18f),
                    WA.ModName(b) + " · " + "WA_NAnimals".Translate(WA.List(b).Count(kv => kv.Value > 0f), Mathf.RoundToInt(WA.TargetBase(b))), MUI.Muted, GameFont.Tiny);
                if (Widgets.ButtonInvisible(row)) { sel = b; edit = null; rightScroll = Vector2.zero; }
            }
            Widgets.EndScrollView();
            DrawBiome(right);
        }

        private void DrawBiome(Rect r)
        {
            WASettings s = Settings;
            MUI.Card(r);
            r = r.ContractedBy(10f);
            if (sel == null)
            {
                U.Label(r, "WA_PickBiome".Translate(), MUI.Muted, GameFont.Small, TextAnchor.UpperLeft);
                return;
            }
            if (edit == null || editFor != sel)
            {
                edit = new Dictionary<PawnKindDef, float>(WA.List(sel));
                editFor = sel;
            }
            float y = r.y;
            U.Label(new Rect(r.x, y, r.width - 330f, 30f), WA.Label(sel), MUI.Text1, GameFont.Medium);
            Rect bCopy = new Rect(r.xMax - 324f, y, 158f, 28f);
            if (Widgets.ButtonText(bCopy, "WA_CopyFrom".Translate()))
            {
                var opts = DefDatabase<BiomeDef>.AllDefsListForReading.Where(b => b != sel && WA.List(b).Count > 0).OrderBy(b => WA.Label(b))
                    .Select(b => new FloatMenuOption(WA.Label(b), () => { Unlock(); WA.Store(sel, new Dictionary<PawnKindDef, float>(WA.List(b))); edit = null; })).ToList();
                if (opts.Count > 0) Find.WindowStack.Add(new FloatMenu(opts));
            }
            Rect bReset = new Rect(r.xMax - 160f, y, 160f, 28f);
            if (Widgets.ButtonText(bReset, "WA_ResetBiome".Translate()))
            {
                Unlock();
                s.lists.Remove(sel.defName);
                s.target.Remove(sel.defName);
                edit = null;
            }
            y += 36f;
            // how many
            float def = WA.DefaultTarget(sel);
            float t = WA.TargetBase(sel);
            U.Label(new Rect(r.x, y, r.width, 22f), "WA_Target".Translate(Mathf.RoundToInt(t), Mathf.RoundToInt(def)), MUI.Text1);
            y += 22f;
            float nt = U.Slider(new Rect(r.x, y, r.width, 22f), t, 0f, Mathf.Max(120f, def * 3f), 1f);
            if (Mathf.Abs(nt - t) > 0.01f) { Unlock(); s.target[sel.defName] = nt; }
            y += 24f;
            Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            string sizes = "WA_Sizes".Translate(Mathf.RoundToInt(t * 40000f / WA.BaseCells), Mathf.RoundToInt(t * 90000f / WA.BaseCells));
            if (map != null && map.Biome == sel) sizes += "  ·  " + "WA_ThisMap".Translate(WA.Target(map));
            U.Label(new Rect(r.x, y, r.width, 20f), sizes, MUI.Muted, GameFont.Tiny);
            y += 26f;
            // the list
            animalSearch = Widgets.TextField(new Rect(r.x, y, 260f, 26f), animalSearch);
            if (animalSearch.NullOrEmpty())
                U.Label(new Rect(r.x + 6f, y, 260f, 26f), "WA_SearchAnimal".Translate(), MUI.Muted, GameFont.Tiny);
            Widgets.CheckboxLabeled(new Rect(r.x + 276f, y, 230f, 26f), "WA_ShowAll".Translate(), ref showAll);
            TooltipHandler.TipRegion(new Rect(r.x + 276f, y, 230f, 26f), "WA_ShowAllTip".Translate());
            y += 30f;
            // header
            // 0.1.2: the controls take 500 px from the right (slider, value, limit, seasons, ★, °) – they all fit now
            float nameW = Mathf.Max(150f, r.width - 16f - 32f - 508f);
            float xs = r.x + 32f + nameW;
            U.Label(new Rect(xs, y, 230f, 18f), "WA_HCommon".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(xs + 234f, y, 70f, 18f), "WA_HCap".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(xs + 304f, y, 112f, 18f), "WA_HSeasons".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(xs + 416f, y, 30f, 18f), "★", MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(xs + 448f, y, 34f, 18f), "°C", MUI.Muted, GameFont.Tiny);
            TooltipHandler.TipRegion(new Rect(xs + 444f, y, 34f, 18f), "WA_TempHeadTip".Translate());
            TooltipHandler.TipRegion(new Rect(xs + 416f, y, 30f, 18f), "WA_RareTip".Translate());
            y += 20f;

            IEnumerable<PawnKindDef> kinds = showAll ? WA.AllAnimals.Concat(edit.Keys).Distinct() : edit.Keys;
            List<PawnKindDef> rows = kinds.Where(k => animalSearch.NullOrEmpty() || WA.Label(k).IndexOf(animalSearch, StringComparison.OrdinalIgnoreCase) >= 0
                || WA.ModName(k).IndexOf(animalSearch, StringComparison.OrdinalIgnoreCase) >= 0)
                .OrderByDescending(k => edit.TryGetValue(k, out float w) && w > 0f).ThenBy(k => WA.Label(k)).ToList();
            float total = edit.Values.Where(v => v > 0f).Sum();
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            const float rowH = 46f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, Mathf.Max(rows.Count * rowH, 30f));
            Widgets.BeginScrollView(outR, ref rightScroll, view);
            if (rows.Count == 0)
                U.Label(new Rect(0f, 0f, view.width, 30f), "WA_NoAnimals".Translate(), MUI.Muted);
            int first = Mathf.Max(0, (int)(rightScroll.y / rowH) - 1), last = Mathf.Min(rows.Count - 1, (int)((rightScroll.y + outR.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                PawnKindDef k = rows[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (i % 2 == 0) MUI.Fill(row, MUI.CardBg2);
                bool inList = edit.TryGetValue(k, out float v);
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 8f, 28f, 28f), k.race);
                string tag = k.RaceProps.predator ? "  · " + "WA_Predator".Translate() : "";
                if (WA.Coastal(sel, k)) tag += "  · " + "WA_Coastal".Translate();
                if (!WA.Native(sel, k)) tag += "  · " + "WA_NotNative".Translate();
                string tw = WA.TempWarning(sel, k);
                if (tw != null) tag += "  · ⚠ " + tw;
                Text.Font = GameFont.Small;
                U.Label(new Rect(row.x + 34f, row.y + 1f, nameW - 8f, 24f), WA.Label(k).Truncate(nameW - 8f), inList && v > 0f ? MUI.Text1 : MUI.Muted);
                Text.Font = GameFont.Tiny;
                string sub = WA.ModName(k) + tag;
                Rect subR = new Rect(row.x + 34f, row.y + 24f, nameW - 8f, 20f);
                U.Label(subR, sub.Truncate(nameW - 8f), k.RaceProps.predator || tw != null ? MUI.Warn : MUI.Muted, GameFont.Tiny);
                if (Mouse.IsOver(subR)) TooltipHandler.TipRegion(subR, sub);
                Text.Font = GameFont.Small;
                float sx = row.x + 32f + nameW;
                    float nv = U.Slider(new Rect(sx, row.y + 14f, 160f, 18f), v, 0f, 1f, 0.001f);
                if (Mathf.Abs(nv - v) > 0.00001f) { Unlock(); edit[k] = nv; WA.Store(sel, edit); }
                    float pct = total > 0f && v > 0f ? v / total * 100f : 0f;
                U.Label(new Rect(sx + 164f, row.y, 68f, rowH), v.ToString("0.###") + " (" + pct.ToString("0.#") + "%)", MUI.Muted, GameFont.Tiny);
                // per animal (all biomes): cap, seasons, rare
                AnimalCfg c = WA.Cfg(k);
                Rect cap = new Rect(sx + 234f, row.y + 11f, 64f, 24f);
                if (Widgets.ButtonText(cap, c.cap == 0 ? "∞" : c.cap.ToString()))
                {
                    var opts = new List<FloatMenuOption>();
                    foreach (int n in new[] { 0, 1, 2, 3, 5, 8, 10, 15, 20, 30, 50 })
                    {
                        int nn = n;
                        opts.Add(new FloatMenuOption(n == 0 ? "WA_NoCap".Translate().ToString() : n.ToString(), () => { AnimalCfg cc = WA.Cfg(k); cc = new AnimalCfg { cap = nn, seasons = cc.seasons, rare = cc.rare, temp = cc.temp }; WA.SetCfg(k, cc); }));
                    }
                    Find.WindowStack.Add(new FloatMenu(opts));
                }
                TooltipHandler.TipRegion(cap, "WA_CapTip".Translate(WA.Label(k)));
                for (int b = 0; b < 4; b++)
                {
                    int bit = 1 << b;
                    Rect sr = new Rect(sx + 304f + b * 27f, row.y + 11f, 25f, 24f);
                    bool on = (c.seasons & bit) != 0;
                    MUI.Fill(sr, on ? MUI.OnBg : MUI.CardBg);
                    MUI.Frame(sr, on ? MUI.Accent : MUI.Border);
                    U.Label(sr, ("WA_S" + b).Translate(), on ? MUI.Accent : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                    TooltipHandler.TipRegion(sr, "WA_SeasonTip".Translate(("WA_Season" + b).Translate()));
                    if (Widgets.ButtonInvisible(sr))
                    {
                        int ns = c.seasons ^ bit;
                        if (ns == 0) ns = bit;   // at least one season
                        WA.SetCfg(k, new AnimalCfg { cap = c.cap, seasons = ns, rare = c.rare, temp = c.temp });
                    }
                }
                bool rare = c.rare;
                Widgets.Checkbox(new Vector2(sx + 416f, row.y + 12f), ref rare, 22f);
                if (rare != c.rare) WA.SetCfg(k, new AnimalCfg { cap = c.cap, seasons = c.seasons, rare = rare, temp = c.temp });
                // only when the outdoor temperature suits it
                bool tOn = WA.TempOnly(sel, k);
                Rect tr = new Rect(sx + 444f, row.y + 11f, 34f, 24f);
                MUI.Fill(tr, tOn ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(tr, tOn ? MUI.Accent : MUI.Border);
                U.Label(tr, "°C", tOn ? MUI.Accent : MUI.Muted, GameFont.Tiny, TextAnchor.MiddleCenter);
                TooltipHandler.TipRegion(tr, "WA_TempTip".Translate(WA.ComfyMin(k).ToStringTemperature("F0"), WA.ComfyMax(k).ToStringTemperature("F0")));
                if (Widgets.ButtonInvisible(tr))
                    WA.SetCfg(k, new AnimalCfg { cap = c.cap, seasons = c.seasons, rare = c.rare, temp = tOn ? 0 : 1 });
            }
            Widgets.EndScrollView();
        }

        // ---------------- rules ----------------
        // 0.1.1: the first change switches the recommended settings off (values start from them)
        private static void Unlock()
        {
            WASettings s = Settings;
            if (s == null || !s.recommended) return;
            s.recommended = false;
        }

        private void DrawRules(Rect r)
        {
            WASettings s = Settings;
            var l = new Listing_Standard();
            l.Begin(r);
            bool en0 = s.enabled, rl0 = s.rareLetters, pa0 = s.pressureAlert;
            MUI.Section(l, "WA_SecSpawn".Translate());
            MUI.Check(l, "WA_Enabled".Translate(), "WA_EnabledTip".Translate(), ref s.enabled, s, "enabled");
            MUI.RowLabel(l, "WA_Respawn".Translate(Hours(s.respawnHours)), "WA_RespawnTip".Translate(), s, "respawnHours");
            float nh = U.Slider(l.GetRect(22f), s.respawnHours, 1f, 120f, 1f);
            if (Mathf.Abs(nh - s.respawnHours) > 0.01f) { Unlock(); s.respawnHours = nh; }
            MUI.RowLabel(l, "WA_Group".Translate(s.groupFactor.ToString("0.##")), "WA_GroupTip".Translate(), s, "groupFactor");
            float ng = U.Slider(l.GetRect(22f), s.groupFactor, 0.25f, 3f, 0.05f);
            if (Mathf.Abs(ng - s.groupFactor) > 0.001f) { Unlock(); s.groupFactor = ng; }
            l.Gap(8f);
            MUI.Section(l, "WA_SecPredators".Translate());
            MUI.RowLabel(l, s.predatorMaxPct >= 100 ? "WA_PredMaxOff".Translate().ToString() : "WA_PredMax".Translate(s.predatorMaxPct).ToString(), "WA_PredMaxTip".Translate(), s, "predatorMaxPct");
            float np = U.Slider(l.GetRect(22f), s.predatorMaxPct, 0f, 100f, 5f);
            if (Mathf.RoundToInt(np) != s.predatorMaxPct) { Unlock(); s.predatorMaxPct = Mathf.RoundToInt(np); }
            MUI.RowLabel(l, s.predatorHomeRadius <= 0 ? "WA_PredHomeOff".Translate().ToString() : "WA_PredHome".Translate(s.predatorHomeRadius).ToString(), "WA_PredHomeTip".Translate(), s, "predatorHomeRadius");
            float nr = U.Slider(l.GetRect(22f), s.predatorHomeRadius, 0f, 100f, 5f);
            if (Mathf.RoundToInt(nr) != s.predatorHomeRadius) { Unlock(); s.predatorHomeRadius = Mathf.RoundToInt(nr); }
            l.Gap(8f);
            MUI.Section(l, "WA_SecInfo".Translate());
            MUI.Check(l, "WA_RareLetters".Translate(), "WA_RareLettersTip".Translate(), ref s.rareLetters, s, "rareLetters");
            MUI.Check(l, "WA_Pressure".Translate(), "WA_PressureTip".Translate(), ref s.pressureAlert, s, "pressureAlert");
            if (en0 != s.enabled || rl0 != s.rareLetters || pa0 != s.pressureAlert)
            {
                bool e = s.enabled, rl = s.rareLetters, pa = s.pressureAlert;
                Unlock();
                s.enabled = e; s.rareLetters = rl; s.pressureAlert = pa;
            }
            l.Gap(10f);
            l.Label("WA_EventsNote".Translate());
            l.End();
        }

        private static string Hours(float h)
        {
            if (h < 24f) return "WA_Hours".Translate(Mathf.RoundToInt(h));
            return "WA_Days".Translate((h / 24f).ToString("0.#"));
        }

        // ---------------- the current map ----------------
        private void DrawMap(Rect r)
        {
            WASettings s = Settings;
            Map map = Current.ProgramState == ProgramState.Playing ? Find.CurrentMap : null;
            if (map == null)
            {
                U.Label(r, "WA_NoMap".Translate(), MUI.Muted, GameFont.Small, TextAnchor.UpperLeft);
                return;
            }
            List<Pawn> wild = WA.WildOn(map);
            int target = WA.Target(map);
            float y = r.y;
            U.Label(new Rect(r.x, y, r.width, 30f), "WA_MapHead".Translate(WA.Label(map.Biome), wild.Count, target), MUI.Text1, GameFont.Medium);
            y += 32f;
            WA.MapLog log = WA.Log(map);
            int now = Find.TickManager.TicksGame;
            string next;
            if (!s.enabled) next = "WA_MapOff".Translate();
            else if (wild.Count >= target) next = "WA_MapFull".Translate();
            else
            {
                int left = Mathf.Max(0, WA.Ticks - (now - log.lastArrival));
                next = "WA_MapNext".Translate(left.ToStringTicksToPeriod());
            }
            U.Label(new Rect(r.x, y, r.width, 22f), next + "  ·  " + "WA_MapSeason".Translate(GenLocalDate.Season(map).LabelCap()), MUI.Muted);
            y += 24f;
            bool pressure = WA.Pressure(map, out int kills, out int arrivals);
            int preds = wild.Count(p => p.RaceProps.predator);
            U.Label(new Rect(r.x, y, r.width, 22f), "WA_MapStats".Translate(kills, arrivals, preds, wild.Count > 0 ? Mathf.RoundToInt(preds * 100f / wild.Count) : 0),
                pressure ? MUI.Warn : MUI.Muted);
            y += 24f;
            if (pressure)
            {
                U.Label(new Rect(r.x, y, r.width, 40f), "⚠ " + "WA_AlertText".Translate(kills, arrivals), MUI.Warn, GameFont.Tiny, TextAnchor.UpperLeft);
                y += 40f;
            }
            y += 6f;
            // by species
            var list = WA.List(map.Biome);
            int bit = WA.SeasonBit(map);
            var kinds = wild.Select(p => p.kindDef).Concat(list.Where(kv => kv.Value > 0f).Select(kv => kv.Key)).Distinct()
                .OrderByDescending(k => wild.Count(p => p.kindDef == k)).ThenBy(k => WA.Label(k)).ToList();
            float total = list.Values.Where(v => v > 0f).Sum();
            U.Label(new Rect(r.x + 32f, y, 220f, 18f), "WA_HAnimal".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(r.x + 256f, y, 80f, 18f), "WA_HNow".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(r.x + 336f, y, 90f, 18f), "WA_HExpected".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(r.x + 426f, y, 80f, 18f), "WA_HCap".Translate(), MUI.Muted, GameFont.Tiny);
            U.Label(new Rect(r.x + 506f, y, 200f, 18f), "WA_HState".Translate(), MUI.Muted, GameFont.Tiny);
            y += 20f;
            Rect outR = new Rect(r.x, y, r.width, r.yMax - y);
            Rect view = new Rect(0f, 0f, outR.width - 16f, kinds.Count * 30f);
            Widgets.BeginScrollView(outR, ref mapScroll, view);
            for (int i = 0; i < kinds.Count; i++)
            {
                PawnKindDef k = kinds[i];
                Rect row = new Rect(0f, i * 30f, view.width, 28f);
                if (i % 2 == 0) MUI.Fill(row, MUI.CardBg2);
                int have = wild.Count(p => p.kindDef == k);
                float w = list.TryGetValue(k, out float ww) ? ww : 0f;
                int expect = total > 0f && w > 0f ? Mathf.RoundToInt(target * w / total) : 0;
                AnimalCfg c = WA.Cfg(k);
                Widgets.DefIcon(new Rect(row.x + 2f, row.y + 2f, 24f, 24f), k.race);
                string nm = WA.Label(k) + (k.RaceProps.predator ? " (" + "WA_Predator".Translate() + ")" : "");
                Text.Font = GameFont.Small;
                Rect nr = new Rect(row.x + 32f, row.y, 220f, 28f);
                U.Label(nr, nm.Truncate(218f), k.RaceProps.predator ? MUI.Warn : MUI.Text1);
                if (Mouse.IsOver(nr)) TooltipHandler.TipRegion(nr, nm);
                U.Label(new Rect(row.x + 256f, row.y, 80f, 28f), have.ToString(), MUI.Text1);
                U.Label(new Rect(row.x + 336f, row.y, 90f, 28f), w > 0f ? "~" + expect : "–", MUI.Muted);
                U.Label(new Rect(row.x + 426f, row.y, 80f, 28f), c.cap == 0 ? "∞" : c.cap.ToString(), c.cap > 0 && have >= c.cap ? MUI.Warn : MUI.Muted);
                string state;
                if (w <= 0f) state = "WA_StNotInList".Translate();
                else if (!WA.InSeason(map, k, bit)) state = "WA_StSeason".Translate();
                else if (c.cap > 0 && have >= c.cap) state = "WA_StCap".Translate();
                else state = "WA_StOk".Translate();
                Text.Font = GameFont.Tiny;
                U.Label(new Rect(row.x + 506f, row.y, row.width - 506f, 28f), state.Truncate(row.width - 510f), MUI.Muted, GameFont.Tiny);
                Text.Font = GameFont.Small;
            }
            Widgets.EndScrollView();
        }
    }
}
