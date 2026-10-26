using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    // 2.5.0: fix a text where you see it – hold Alt and right-click any text on the screen; URT's
    // editor opens with exactly that text (the original found from the translation shown).
    public static class Pick
    {
        private static string candidate;
        private static float candArea;
        private static int candFrame = -1;
        private static Dictionary<string, string> reverse;
        private static int reverseVersion = -1;

        public static void Patch(Harmony h)
        {
            var pre = new HarmonyMethod(typeof(Pick), nameof(Prefix)) { priority = Priority.First };
            h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.Label), new[] { typeof(Rect), typeof(string) }), prefix: pre);
            h.Patch(AccessTools.Method(typeof(Widgets), nameof(Widgets.LabelEllipses), new[] { typeof(Rect), typeof(string) }), prefix: pre);
            h.Patch(AccessTools.Method(typeof(UIRoot), nameof(UIRoot.UIRootOnGUI)), postfix: new HarmonyMethod(typeof(Pick), nameof(AfterGui)));
        }

        public static void Prefix(Rect rect, string label)
        {
            Event e = Event.current;
            if (e == null || e.type != EventType.MouseDown || e.button != 1 || !e.alt) return;
            if (string.IsNullOrEmpty(label) || UniversalRimTranslatorMod.Settings == null || !UniversalRimTranslatorMod.Settings.pickFix || !Runtime.Active) return;
            if (!Mouse.IsOver(rect)) return;
            float area = rect.width * rect.height;
            if (candFrame != Time.frameCount || candidate == null || area < candArea)
            {
                candidate = label;
                candArea = area;
                candFrame = Time.frameCount;
            }
        }

        public static void AfterGui()
        {
            if (candidate == null) return;
            string shown = candidate;
            candidate = null;
            // the play screen draws its texts after this point, so the click is handled at the next GUI event
            if (Time.frameCount - candFrame > 2) return;
            try
            {
                string src = Resolve(shown);
                EditorTab.Pick(src, shown);
                UniversalRimTranslatorMod.OpenTab(3);
            }
            catch (Exception ex)
            {
                MLog.Warn("[URT] pick: " + ex.Message);
            }
        }

        // the original English text behind what is shown (or null)
        public static string Resolve(string shown)
        {
            Store store = Runtime.Store;
            if (store == null) return null;
            string k = Store.Key(shown);
            if (k.Length == 0) return null;
            if (store.TryGet(k, out _)) return k;              // the original itself (not translated yet)
            if (reverse == null || reverseVersion != store.Version)
            {
                var d = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in store.Snapshot())
                    if (!string.IsNullOrEmpty(kv.Value.Text))
                    {
                        string t = Store.Key(kv.Value.Text);
                        if (t.Length > 0 && !d.ContainsKey(t)) d[t] = kv.Key;
                    }
                reverse = d;
                reverseVersion = store.Version;
            }
            if (reverse.TryGetValue(k, out string src)) return src;
            // an untranslated text of a mod (in the scan, no translation yet)
            foreach (Item it in Runtime.Items)
                if (Store.Key(it.Source) == k) return k;
            return null;
        }
    }

    // 2.5.0: shown once at the first start – what URT does, without any setup
    public class Dialog_UrtWelcome : Window
    {
        public override Vector2 InitialSize => new Vector2(620f, Runtime.Active ? 470f : 300f);

        public Dialog_UrtWelcome()
        {
            doCloseX = true;
            forcePause = true;
            absorbInputAroundWindow = true;
        }

        public override void DoWindowContents(Rect r)
        {
            Live.Suspended++;
            try
            {
                MUI.Fill(r, MUI.CardBg);
                Text.Font = GameFont.Medium;
                Widgets.Label(new Rect(r.x + 8f, r.y + 4f, r.width - 16f, 34f), "URT_Welcome_Title".Translate());
                Text.Font = GameFont.Small;
                string body = Runtime.Active
                    ? (string)"URT_Welcome_Body".Translate(Runtime.TargetName ?? "?")
                    : (string)"URT_Welcome_English".Translate();
                Widgets.Label(new Rect(r.x + 8f, r.y + 44f, r.width - 16f, r.height - 100f), body);
                float bw = 180f;
                if (Widgets.ButtonText(new Rect(r.xMax - bw - 8f, r.yMax - 40f, bw, 34f), "URT_Welcome_Ok".Translate()))
                    Close();
                if (Runtime.Active && Widgets.ButtonText(new Rect(r.xMax - 2 * bw - 16f, r.yMax - 40f, bw, 34f), "URT_Welcome_Settings".Translate()))
                {
                    Close();
                    UniversalRimTranslatorMod.OpenTab(0);
                }
            }
            finally { Live.Suspended--; }
        }

        public static void ShowOnce()
        {
            var s = UniversalRimTranslatorMod.Settings;
            if (s == null || s.welcomeShown) return;
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    s.welcomeShown = true;
                    UniversalRimTranslatorMod.Instance?.WriteSettings();
                    Find.WindowStack.Add(new Dialog_UrtWelcome());
                }
                catch (Exception e) { MLog.Warn("[URT] welcome: " + e.Message); }
            });
        }
    }
}
