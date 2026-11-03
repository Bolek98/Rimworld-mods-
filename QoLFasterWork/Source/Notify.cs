using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace QoLFW
{
    // Letters and messages by kind: normal, silent (no sound) or hidden (only in History).
    public static class Notify
    {
        public static bool HiddenLetter(LetterDef d) => d != null && QoLMod.S.notifications && QoLMod.S.hiddenLetters.Contains(d.defName);
        public static bool MutedLetter(LetterDef d) => d != null && QoLMod.S.notifications && QoLMod.S.mutedLetters.Contains(d.defName);
        public static bool HiddenMessage(MessageTypeDef d) => d != null && QoLMod.S.notifications && QoLMod.S.hiddenMessages.Contains(d.defName);
        public static bool MutedMessage(MessageTypeDef d) => d != null && QoLMod.S.notifications && QoLMod.S.mutedMessages.Contains(d.defName);

        private static string Pretty(Def d)
        {
            string l = d.label;
            if (!l.NullOrEmpty()) return l.CapitalizeFirst();
            return GenText.SplitCamelCase(d.defName);
        }

        public static void SettingsPage(Listing_Standard l)
        {
            QSettings s = QoLMod.S;
            MUI.Section(l, "QFW_SecNotify".Translate());
            bool v = s.notifications;
            MUI.Check(l, "QFW_Notifications".Translate(), "QFW_Notifications_Desc".Translate(), ref s.notifications, s, "notifications");
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label("QFW_NotifyNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            if (!s.notifications) return;

            MUI.Section(l, "QFW_SecDismiss".Translate());
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            l.Label("QFW_DismissNote".Translate());
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            s.dismissPositive = Secs(l, "QFW_DismissPositive", s.dismissPositive);
            s.dismissNeutral = Secs(l, "QFW_DismissNeutral", s.dismissNeutral);
            s.dismissNegative = Secs(l, "QFW_DismissNegative", s.dismissNegative);
            s.dismissThreat = Secs(l, "QFW_DismissThreat", s.dismissThreat);
            MUI.Section(l, "QFW_SecLetters".Translate());
            Header(l);
            foreach (LetterDef d in DefDatabase<LetterDef>.AllDefsListForReading.OrderBy(x => x.defName))
                Row(l, d, s.mutedLetters, s.hiddenLetters, true);
            MUI.Section(l, "QFW_SecMessages".Translate());
            Header(l);
            foreach (MessageTypeDef d in DefDatabase<MessageTypeDef>.AllDefsListForReading.OrderBy(x => x.defName))
                Row(l, d, s.mutedMessages, s.hiddenMessages, true);
        }

        private static int Secs(Listing_Standard l, string key, int v)
        {
            string val = v <= 0 ? "QFW_DismissNever".Translate().ToString() : "QFW_DismissSecs".Translate(v).ToString();
            Rect r = l.GetRect(28f);
            Widgets.Label(new Rect(r.x, r.y + 3f, r.width * 0.45f, 24f), key.Translate() + ": " + val);
            float nv = MUI.SliderBar(new Rect(r.x + r.width * 0.47f, r.y + 6f, r.width * 0.5f, 18f), v, 0f, 120f, 5f);
            l.Gap(2f);
            return Mathf.RoundToInt(nv);
        }

        // 0 = keep, otherwise seconds until the letter closes by itself
        public static int DismissAfter(Letter let)
        {
            QSettings s = QoLMod.S;
            if (!s.notifications || let?.def == null || let is ChoiceLetter) return 0;
            LetterDef d = let.def;
            if (d == LetterDefOf.ThreatBig || d == LetterDefOf.ThreatSmall) return s.dismissThreat;
            if (d == LetterDefOf.NegativeEvent || d == LetterDefOf.Death) return s.dismissNegative;
            if (d == LetterDefOf.PositiveEvent) return s.dismissPositive;
            return s.dismissNeutral;
        }

        private static readonly Dictionary<Letter, float> seen = new Dictionary<Letter, float>();

        public static void AutoDismiss()
        {
            QSettings s = QoLMod.S;
            if (!s.notifications || (s.dismissPositive <= 0 && s.dismissNeutral <= 0 && s.dismissNegative <= 0 && s.dismissThreat <= 0)) { seen.Clear(); return; }
            List<Letter> list = Find.LetterStack?.LettersListForReading;
            if (list == null) return;
            float now = Time.realtimeSinceStartup;
            for (int i = list.Count - 1; i >= 0; i--)
            {
                Letter let = list[i];
                int secs = DismissAfter(let);
                if (secs <= 0) continue;
                float t;
                if (!seen.TryGetValue(let, out t)) { seen[let] = now; continue; }
                if (now - t >= secs) { seen.Remove(let); Find.LetterStack.RemoveLetter(let); }
            }
            if (seen.Count > 200)
                foreach (Letter k in seen.Keys.ToList()) if (!list.Contains(k)) seen.Remove(k);
        }

        private static void Header(Listing_Standard l)
        {
            Rect r = l.GetRect(22f);
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.Label(new Rect(r.xMax - 200f, r.y, 100f, 22f), "QFW_ColMute".Translate());
            Widgets.Label(new Rect(r.xMax - 100f, r.y, 100f, 22f), "QFW_ColHide".Translate());
            Text.Anchor = TextAnchor.UpperLeft;
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
        }

        private static void Row(Listing_Standard l, Def d, List<string> muted, List<string> hidden, bool canMute)
        {
            Rect r = l.GetRect(26f);
            Widgets.DrawHighlightIfMouseover(r);
            Widgets.Label(new Rect(r.x + 4f, r.y + 2f, r.width - 210f, 24f), Pretty(d) + "  ");
            GUI.color = MUI.Muted;
            Text.Font = GameFont.Tiny;
            float lw = Text.CalcSize(Pretty(d) + "  ").x;
            Text.Font = GameFont.Small;
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(r.x + 8f + lw, r.y + 5f, r.width - 220f - lw, 20f), d.defName);
            Text.Font = GameFont.Small;
            GUI.color = Color.white;
            bool m = muted.Contains(d.defName), h = hidden.Contains(d.defName);
            bool m2 = m, h2 = h;
            if (canMute) Widgets.Checkbox(r.xMax - 150f - 12f, r.y + 1f, ref m2, 24f);
            Widgets.Checkbox(r.xMax - 50f - 12f, r.y + 1f, ref h2, 24f);
            if (m2 != m) { if (m2) muted.Add(d.defName); else muted.Remove(d.defName); }
            if (h2 != h) { if (h2) hidden.Add(d.defName); else hidden.Remove(d.defName); }
        }
    }

    [HarmonyPatch(typeof(LetterStack), "LettersOnGUI")]
    public static class Patch_LettersOnGUI
    {
        public static void Prefix()
        {
            if (Event.current.type != EventType.Repaint) return;
            try { Notify.AutoDismiss(); } catch (Exception e) { Log.WarningOnce("[QoL Faster Work] auto dismiss: " + e.Message, 81723416); }
        }
    }

    [HarmonyPatch(typeof(LetterStack), "ReceiveLetter", new[] { typeof(Letter), typeof(string), typeof(int), typeof(bool) })]
    public static class Patch_Letter
    {
        public static bool Prefix(Letter let, int delayTicks, ref bool playSound)
        {
            try
            {
                if (let == null || delayTicks > 0) return true;
                if (Notify.HiddenLetter(let.def))
                {
                    let.arrivalTick = Find.TickManager.TicksGame;
                    Find.Archive?.Add(let);
                    return false;
                }
                if (Notify.MutedLetter(let.def)) playSound = false;
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] letter filter: " + e.Message, 81723405); }
            return true;
        }
    }

    [HarmonyPatch(typeof(Messages), "Message", new[] { typeof(Message), typeof(bool) })]
    public static class Patch_Message
    {
        public static bool Prefix(Message msg, bool historical, out SoundDef __state)
        {
            __state = null;
            try
            {
                if (msg?.def == null) return true;
                if (Notify.HiddenMessage(msg.def))
                {
                    if (historical) Find.Archive?.Add(msg);
                    return false;
                }
                if (Notify.MutedMessage(msg.def) && msg.def.sound != null)
                {
                    __state = msg.def.sound;
                    msg.def.sound = null;
                }
            }
            catch (Exception e) { Log.WarningOnce("[QoL Faster Work] message filter: " + e.Message, 81723406); }
            return true;
        }

        public static void Finalizer(Message msg, SoundDef __state)
        {
            if (__state != null && msg?.def != null) msg.def.sound = __state;
        }
    }
}
