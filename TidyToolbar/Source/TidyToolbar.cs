using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace TidyToolbar
{
    // ---------------------------------------------------------------------------
    // Tidy Toolbar: the bottom bar with too many buttons from mods. Every button gets the width its
    // label needs (no more cut labels), buttons with an icon stay small, and what does not fit goes
    // into a "More" menu at the end of the bar. Right click a button to hide it, move it or open the
    // settings. Hotkeys of hidden buttons still work.
    // ---------------------------------------------------------------------------
    public sealed class TTSettings : ModSettings
    {
        public List<string> order = new List<string>();     // user order (defNames); empty = the game's order
        public List<string> hidden = new List<string>();    // always in the "More" menu
        public bool autoMove = true;                        // what does not fit goes into "More"
        public bool iconsSmall = true;                      // buttons with an icon take only the icon's width

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref order, "order", LookMode.Value);
            Scribe_Collections.Look(ref hidden, "hidden", LookMode.Value);
            Scribe_Values.Look(ref autoMove, "autoMove", true);
            Scribe_Values.Look(ref iconsSmall, "iconsSmall", true);
            if (order == null) order = new List<string>();
            if (hidden == null) hidden = new List<string>();
        }
    }

    public sealed class TidyToolbarMod : Mod
    {
        public const string Version = "1.0.0";
        public static TTSettings S;
        private Vector2 scroll;

        public TidyToolbarMod(ModContentPack content) : base(content)
        {
            S = GetSettings<TTSettings>();
            try
            {
                var h = new Harmony("bolek98.tidytoolbar");
                h.Patch(AccessTools.Method(typeof(MainButtonsRoot), "DoButtons"),
                    prefix: new HarmonyMethod(typeof(Bar), nameof(Bar.Prefix)) { priority = Priority.Low });
                Log.Message("[Tidy Toolbar] v" + Version + " loaded.");
            }
            catch (Exception e)
            {
                Log.Error("[Tidy Toolbar] could not patch the toolbar: " + e);
            }
        }

        public override string SettingsCategory() => "Tidy Toolbar";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var top = new Listing_Standard();
            top.Begin(new Rect(inRect.x, inRect.y, inRect.width, 110f));
            top.CheckboxLabeled("TT_AutoMove".Translate(), ref S.autoMove, "TT_AutoMoveTip".Translate());
            top.CheckboxLabeled("TT_IconsSmall".Translate(), ref S.iconsSmall, "TT_IconsSmallTip".Translate());
            top.End();
            GUI.color = new Color(0.7f, 0.72f, 0.76f);
            Widgets.Label(new Rect(inRect.x, inRect.y + 60f, inRect.width - 200f, 44f), "TT_Hint".Translate());
            GUI.color = Color.white;
            if (Widgets.ButtonText(new Rect(inRect.xMax - 190f, inRect.y + 64f, 190f, 30f), "TT_Reset".Translate()))
            {
                S.order.Clear();
                S.hidden.Clear();
            }

            List<MainButtonDef> all = Bar.Ordered(DefDatabase<MainButtonDef>.AllDefsListForReading.Where(d => d.buttonVisible).ToList());
            Rect outR = new Rect(inRect.x, inRect.y + 110f, inRect.width, inRect.height - 110f);
            const float rowH = 30f;
            Rect view = new Rect(0f, 0f, outR.width - 16f, all.Count * rowH);
            Widgets.BeginScrollView(outR, ref scroll, view);
            for (int i = 0; i < all.Count; i++)
            {
                MainButtonDef d = all[i];
                Rect r = new Rect(0f, i * rowH, view.width, rowH);
                if (i % 2 == 1) Widgets.DrawLightHighlight(r);
                float x = 4f;
                if (d.Icon != null) GUI.DrawTexture(new Rect(x, r.y + 3f, 24f, 24f), d.Icon);
                x += 30f;
                bool onBar = !S.hidden.Contains(d.defName);
                bool was = onBar;
                Text.Anchor = TextAnchor.MiddleLeft;
                Widgets.Label(new Rect(x, r.y, r.width - x - 260f, rowH), d.LabelCap + "  <color=#888888>(" + (d.modContentPack?.Name ?? "?") + ")</color>");
                Text.Anchor = TextAnchor.UpperLeft;
                Widgets.CheckboxLabeled(new Rect(r.xMax - 250f, r.y + 3f, 160f, 24f), "TT_OnBar".Translate(), ref onBar);
                if (onBar != was) Bar.SetHidden(d, !onBar);
                if (i > 0 && Widgets.ButtonImage(new Rect(r.xMax - 74f, r.y + 3f, 24f, 24f), TexButton.ReorderUp))
                    Bar.Move(all, i, -1);
                if (i < all.Count - 1 && Widgets.ButtonImage(new Rect(r.xMax - 36f, r.y + 3f, 24f, 24f), TexButton.ReorderDown))
                    Bar.Move(all, i, +1);
            }
            Widgets.EndScrollView();
        }
    }

    public static class Bar
    {
        private static readonly AccessTools.FieldRef<MainButtonsRoot, List<MainButtonDef>> allRef =
            AccessTools.FieldRefAccess<MainButtonsRoot, List<MainButtonDef>>("allButtonsInOrder");

        private const float IconW = 44f;
        private const float MinText = 56f;
        private const float Pad = 22f;
        private static bool failed;

        // the buttons in the user's order; buttons the user never moved keep their place after the
        // button that comes before them in the game's order
        public static List<MainButtonDef> Ordered(List<MainButtonDef> defs)
        {
            var game = defs.OrderBy(d => d.order).ToList();
            List<string> order = TidyToolbarMod.S.order;
            if (order.Count == 0)
                return game;
            var result = new List<MainButtonDef>();
            foreach (string n in order)
            {
                MainButtonDef d = game.FirstOrDefault(x => x.defName == n);
                if (d != null) result.Add(d);
            }
            for (int i = 0; i < game.Count; i++)
            {
                if (result.Contains(game[i])) continue;
                int at = 0;
                for (int j = i - 1; j >= 0; j--)
                {
                    int k = result.IndexOf(game[j]);
                    if (k >= 0) { at = k + 1; break; }
                }
                result.Insert(at, game[i]);
            }
            return result;
        }

        public static void Move(List<MainButtonDef> list, int i, int dir)
        {
            int j = i + dir;
            if (j < 0 || j >= list.Count) return;
            MainButtonDef t = list[i];
            list[i] = list[j];
            list[j] = t;
            TidyToolbarMod.S.order = list.Select(d => d.defName).ToList();
            Save();
        }

        public static void SetHidden(MainButtonDef d, bool hide)
        {
            List<string> h = TidyToolbarMod.S.hidden;
            if (hide && !h.Contains(d.defName)) h.Add(d.defName);
            if (!hide) h.Remove(d.defName);
            Save();
        }

        private static void Save()
        {
            try { LoadedModManager.GetMod<TidyToolbarMod>()?.WriteSettings(); } catch { }
        }

        private static bool IsMenu(MainButtonDef d) => d == MainButtonDefOf.Menu;

        private static float Want(MainButtonDef d, bool shortened)
        {
            if (d.Icon != null && (TidyToolbarMod.S.iconsSmall || d.minimized))
                return IconW;
            Text.Font = GameFont.Small;
            float w = shortened ? d.ShortenedLabelCapWidth : d.LabelCapWidth;
            return Mathf.Max(MinText, w + Pad);
        }

        public static bool Prefix(MainButtonsRoot __instance)
        {
            if (failed) return true;
            try
            {
                Draw(__instance);
                return false;
            }
            catch (Exception e)
            {
                failed = true;
                Log.Error("[Tidy Toolbar] drawing the toolbar failed, the game's toolbar is used instead: " + e);
                return true;
            }
        }

        private static void Draw(MainButtonsRoot root)
        {
            List<MainButtonDef> all = allRef(root);
            List<MainButtonDef> visible = Ordered(all.Where(d => d.Worker.Visible).ToList());
            var bar = new List<MainButtonDef>();
            var more = new List<MainButtonDef>();
            foreach (MainButtonDef d in visible)
                (TidyToolbarMod.S.hidden.Contains(d.defName) && !IsMenu(d) ? more : bar).Add(d);

            float screen = UI.screenWidth;
            float moreW = MoreWidth();
            bool shortened = false;
            Func<float> need = () => bar.Sum(d => Want(d, shortened)) + (more.Count > 0 ? moreW : 0f);
            if (need() > screen) shortened = true;
            if (TidyToolbarMod.S.autoMove)
                while (need() > screen && bar.Count > 1)
                {
                    // the last button that is not the main menu goes into "More"
                    int k = bar.FindLastIndex(d => !IsMenu(d));
                    if (k < 0) break;
                    more.Insert(0, bar[k]);
                    bar.RemoveAt(k);
                }

            // widths: what each needs, the space left goes to the text buttons
            var w = bar.Select(d => Want(d, shortened)).ToList();
            float used = w.Sum() + (more.Count > 0 ? moreW : 0f);
            float left = screen - used;
            if (left > 0f)
            {
                var growable = Enumerable.Range(0, bar.Count).Where(i => w[i] > IconW).ToList();
                if (growable.Count == 0) growable = Enumerable.Range(0, bar.Count).ToList();
                if (growable.Count > 0)
                {
                    float total = growable.Sum(i => w[i]);
                    foreach (int i in growable) w[i] += left * w[i] / Mathf.Max(1f, total);
                }
            }
            else if (left < 0f)
            {
                float f = (screen - (more.Count > 0 ? moreW : 0f)) / Mathf.Max(1f, w.Sum());
                for (int i = 0; i < w.Count; i++) w[i] *= f;
            }

            GUI.color = Color.white;
            float y = UI.screenHeight - 35;
            float x = 0f;
            // "More" sits right before the main menu (or at the very end)
            int menuAt = bar.FindIndex(IsMenu);
            int moreAt = more.Count == 0 ? -1 : (menuAt >= 0 ? menuAt : bar.Count);
            for (int i = 0; i <= bar.Count; i++)
            {
                if (i == moreAt)
                {
                    Rect mr = new Rect(Mathf.Round(x), y, Mathf.Round(moreW), 36f);
                    DrawMore(mr, more);
                    x += moreW;
                }
                if (i == bar.Count) break;
                bool last = i == bar.Count - 1 && moreAt != bar.Count;
                float bw = last ? screen - Mathf.Round(x) : Mathf.Round(x + w[i]) - Mathf.Round(x);
                Rect r = new Rect(Mathf.Round(x), y, bw, 36f);
                MainButtonDef d = bar[i];
                if (Event.current.type == EventType.MouseDown && Event.current.button == 1 && Mouse.IsOver(r))
                {
                    Event.current.Use();
                    Find.WindowStack.Add(new FloatMenu(ButtonMenu(d, visible)));
                }
                d.Worker.DoButton(r);
                x += w[i];
            }
        }

        private static float MoreWidth()
        {
            Text.Font = GameFont.Small;
            return Mathf.Max(MinText, Text.CalcSize("TT_More".Translate()).x + Pad);
        }

        private static void DrawMore(Rect r, List<MainButtonDef> more)
        {
            bool open = more.Any(d => Find.MainTabsRoot.OpenTab == d);
            if (open) Widgets.DrawHighlight(r);
            if (Widgets.ButtonTextSubtle(r, "TT_More".Translate(), 0f, -1f, SoundDefOf.Mouseover_Category))
            {
                var opts = new List<FloatMenuOption>();
                foreach (MainButtonDef d in more)
                {
                    MainButtonDef def = d;
                    string label = def.LabelCap;
                    if (def.Worker.Disabled) { opts.Add(new FloatMenuOption(label, null, def.Icon, Color.white)); continue; }
                    opts.Add(new FloatMenuOption(label, () => def.Worker.InterfaceTryActivate(), def.Icon ?? BaseContent.ClearTex, Color.white));
                }
                opts.Add(new FloatMenuOption("TT_Edit".Translate(), OpenSettings));
                Find.WindowStack.Add(new FloatMenu(opts));
            }
            TooltipHandler.TipRegion(r, "TT_MoreTip".Translate(string.Join(", ", more.Select(d => d.LabelCap.ToString()).ToArray())));
        }

        private static List<FloatMenuOption> ButtonMenu(MainButtonDef d, List<MainButtonDef> visible)
        {
            var opts = new List<FloatMenuOption>();
            if (!IsMenu(d))
                opts.Add(new FloatMenuOption("TT_Hide".Translate(d.LabelCap), () => SetHidden(d, true)));
            var all = Ordered(DefDatabase<MainButtonDef>.AllDefsListForReading.Where(x => x.buttonVisible).ToList());
            int i = all.IndexOf(d);
            if (i > 0)
                opts.Add(new FloatMenuOption("TT_Left".Translate(), () => MoveVisible(all, d, -1, visible)));
            if (i >= 0 && i < all.Count - 1)
                opts.Add(new FloatMenuOption("TT_Right".Translate(), () => MoveVisible(all, d, +1, visible)));
            opts.Add(new FloatMenuOption("TT_Edit".Translate(), OpenSettings));
            return opts;
        }

        // moves past the next button that is actually on the bar (invisible ones are skipped)
        private static void MoveVisible(List<MainButtonDef> all, MainButtonDef d, int dir, List<MainButtonDef> visible)
        {
            int i = all.IndexOf(d);
            int j = i + dir;
            while (j >= 0 && j < all.Count && !visible.Contains(all[j])) j += dir;
            if (j < 0 || j >= all.Count) return;
            all.RemoveAt(i);
            all.Insert(j, d);
            TidyToolbarMod.S.order = all.Select(x => x.defName).ToList();
            Save();
        }

        private static void OpenSettings()
        {
            Mod mod = LoadedModManager.GetMod<TidyToolbarMod>();
            if (mod != null) Find.WindowStack.Add(new Dialog_ModSettings(mod));
        }
    }
}
