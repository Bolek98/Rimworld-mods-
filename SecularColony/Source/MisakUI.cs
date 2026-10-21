using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace SecularColony
{
    // ------------------------------------------------------------------
    // Shared settings look of Bolek98's mods: a "Recommended settings"
    // switch on top, locked details while it is on, and a CHANGED mark on
    // every value that differs from the recommended one.
    // ------------------------------------------------------------------
    public static class MUI
    {
        public static readonly Color Accent = new Color32(124, 191, 94, 255);
        public static readonly Color Warn = new Color32(224, 162, 74, 255);
        public static readonly Color Muted = new Color32(160, 167, 174, 255);
        public static readonly Color Text1 = new Color32(230, 227, 220, 255);
        public static readonly Color OnBg = new Color32(31, 42, 34, 255);
        public static readonly Color CardBg = new Color32(29, 33, 38, 255);
        public static readonly Color CardBg2 = new Color32(38, 44, 50, 255);
        public static readonly Color Border = new Color32(58, 65, 72, 255);
        public static readonly Color Knob = new Color32(244, 242, 236, 255);
        public static readonly Color OffKnob = new Color32(154, 161, 168, 255);

        // ---------------- recommended values ----------------
        private static object rec;
        private static readonly List<FieldInfo> fields = new List<FieldInfo>();
        private static readonly Dictionary<string, FieldInfo> byName = new Dictionary<string, FieldInfo>();

        // Every public bool/int/float/enum field of the settings class takes part,
        // except the names in skip. With include given, only those names.
        public static void Init(object recommended, string[] include, params string[] skip)
        {
            rec = recommended;
            fields.Clear();
            byName.Clear();
            var skipSet = new HashSet<string>(skip ?? new string[0]);
            HashSet<string> inc = include == null ? null : new HashSet<string>(include);
            foreach (FieldInfo f in recommended.GetType().GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                Type t = f.FieldType;
                if (t != typeof(bool) && t != typeof(int) && t != typeof(float) && !t.IsEnum)
                    continue;
                if (skipSet.Contains(f.Name) || (inc != null && !inc.Contains(f.Name)))
                    continue;
                fields.Add(f);
                byName[f.Name] = f;
            }
        }

        public static void SetRecommended(object recommended) { rec = recommended; }

        private static bool Eq(object a, object b)
        {
            if (a is float && b is float)
                return Mathf.Abs((float)a - (float)b) < 0.0005f;
            return Equals(a, b);
        }

        public static void ApplyAll(object target)
        {
            if (rec == null || target == null) return;
            for (int i = 0; i < fields.Count; i++)
                fields[i].SetValue(target, fields[i].GetValue(rec));
        }

        public static int Diff(object target)
        {
            if (rec == null || target == null) return 0;
            int n = 0;
            for (int i = 0; i < fields.Count; i++)
                if (!Eq(fields[i].GetValue(target), fields[i].GetValue(rec)))
                    n++;
            return n;
        }

        public static bool Changed(object target, string field)
        {
            FieldInfo f;
            if (rec == null || target == null || field == null || !byName.TryGetValue(field, out f))
                return false;
            return !Eq(f.GetValue(target), f.GetValue(rec));
        }

        public static void Reset(object target, string field)
        {
            FieldInfo f;
            if (rec != null && target != null && field != null && byName.TryGetValue(field, out f))
                f.SetValue(target, f.GetValue(rec));
        }

        public static object RecValue(string field)
        {
            FieldInfo f;
            return rec != null && field != null && byName.TryGetValue(field, out f) ? f.GetValue(rec) : null;
        }

        private static string Show(object v)
        {
            if (v is bool) return (bool)v ? "MUI_Yes".Translate().ToString() : "MUI_No".Translate().ToString();
            if (v is float) return ((float)v).ToString("0.##");
            if (v is Enum) return v.ToString();
            return v == null ? "" : v.ToString();
        }

        // ---------------- drawing ----------------
        public static void Fill(Rect r, Color c, float radius = 0f)
        {
            if (Event.current.type != EventType.Repaint) return;
            GUI.DrawTexture(r, BaseContent.WhiteTex, ScaleMode.StretchToFill, true, 0f, c, 0f, radius);
        }

        public static void Frame(Rect r, Color c, float width = 1f, float radius = 0f)
        {
            if (Event.current.type != EventType.Repaint) return;
            // 1.7 (Waylith) ignores the border width of GUI.DrawTexture and fills the whole box –
            // the CHANGED marks were plain orange boxes. Four thin edges instead.
            if (width <= 0f) width = 1f;
            Fill(new Rect(r.x, r.y, r.width, width), c);
            Fill(new Rect(r.x, r.yMax - width, r.width, width), c);
            Fill(new Rect(r.x, r.y, width, r.height), c);
            Fill(new Rect(r.xMax - width, r.y, width, r.height), c);
        }

        public static void Card(Rect r, bool highlight = false)
        {
            Fill(r, highlight ? OnBg : CardBg);
            Frame(r, highlight ? Accent : Border);
        }

        private static void Label(Rect r, string text, Color c, GameFont font = GameFont.Small, TextAnchor anchor = TextAnchor.MiddleLeft)
        {
            GameFont f = Text.Font;
            TextAnchor a = Text.Anchor;
            Color g = GUI.color;
            Text.Font = font;
            Text.Anchor = anchor;
            GUI.color = new Color(c.r, c.g, c.b, c.a * g.a);
            Widgets.Label(r, text);
            GUI.color = g;
            Text.Anchor = a;
            Text.Font = f;
        }

        // A pill switch. Returns true when clicked.
        public static bool Switch(Rect r, bool on)
        {
            Fill(r, on ? Accent : Border, r.height / 2f);
            float k = r.height - 8f;
            Rect knob = new Rect(on ? r.xMax - 4f - k : r.x + 4f, r.y + 4f, k, k);
            Fill(knob, on ? Knob : OffKnob, k / 2f);
            if (Widgets.ButtonInvisible(r))
            {
                (on ? SoundDefOf.Checkbox_TurnedOff : SoundDefOf.Checkbox_TurnedOn).PlayOneShotOnCamera();
                return true;
            }
            return false;
        }

        // Version under the window title, right side.
        public static void Version(Rect inRect, string text)
        {
            Label(new Rect(inRect.xMax - 360f, inRect.y - 36f, 190f, 30f), text, Muted, GameFont.Tiny, TextAnchor.MiddleRight);
        }

        // The "Recommended settings" card. Returns its height.
        // bullets: what the mod does on its own while the switch is on.
        public static float RecommendedCard(Rect area, object target, bool on, Action<bool> set, List<string> bullets)
        {
            int changed = on ? 0 : Diff(target);
            int rows = on ? (bullets.Count + 1) / 2 : 0;
            float h = on ? 70f + rows * 24f + 8f : 58f;
            Rect r = new Rect(area.x, area.y, area.width, h);
            Card(r, on);

            Rect sw = new Rect(r.xMax - 16f - 60f, r.y + (on ? 18f : 14f), 60f, 30f);
            Label(new Rect(sw.x - 110f, sw.y, 102f, 30f), on ? "MUI_On".Translate() : "MUI_Off".Translate(),
                on ? Accent : Muted, GameFont.Small, TextAnchor.MiddleRight);
            if (Switch(sw, on))
            {
                if (on)
                    set(false);
                else if (changed == 0)
                    set(true);
                else
                    Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("MUI_ConfirmOn".Translate(changed), delegate { set(true); }, true));
            }

            float textW = sw.x - 120f - (r.x + 16f);
            if (on)
            {
                Label(new Rect(r.x + 16f, r.y + 10f, textW, 30f), "MUI_Title".Translate(), Text1, GameFont.Medium);
                Label(new Rect(r.x + 16f, r.y + 38f, r.width - 32f, 24f), "MUI_DescOn".Translate(), Muted);
                float colW = (r.width - 48f) / 2f;
                for (int i = 0; i < bullets.Count; i++)
                {
                    float x = r.x + 16f + (i % 2) * (colW + 16f);
                    float y = r.y + 66f + (i / 2) * 24f;
                    GUI.DrawTexture(new Rect(x, y + 4f, 16f, 16f), Widgets.CheckboxOnTex);
                    Label(new Rect(x + 24f, y, colW - 24f, 24f), bullets[i], Text1);
                }
            }
            else
            {
                Label(new Rect(r.x + 16f, r.y + 6f, textW, 26f), "MUI_TitleOff".Translate(), Text1);
                Label(new Rect(r.x + 16f, r.y + 30f, textW, 22f),
                    changed == 0 ? "MUI_DescOffSame".Translate().ToString() : "MUI_DescOff".Translate(changed).ToString(), Muted, GameFont.Tiny);
                if (changed > 0)
                {
                    Rect b = new Rect(sw.x - 120f - 200f, r.y + 14f, 196f, 30f);
                    if (Widgets.ButtonText(b, "MUI_ResetAll".Translate()))
                        ApplyAll(target);
                }
            }
            return h;
        }

        // The line under the card while the details are locked.
        public static float LockNote(Rect area)
        {
            Rect r = new Rect(area.x, area.y, area.width, 28f);
            Fill(r, CardBg2);
            Label(r.ContractedBy(10f, 0f), "MUI_Locked".Translate(), Muted, GameFont.Tiny);
            return 28f;
        }

        private static bool lockOn;
        private static Vector2 lockMouse;
        private static bool lockMoved;

        // Locked details: everything is drawn, but no control reacts. GUI.enabled
        // alone is not enough – RimWorld checkboxes and sliders read the mouse
        // themselves, so the mouse is moved far away while the locked part is drawn
        // (scrolling still works). EndLock dims the area.
        public static void BeginLock(bool locked)
        {
            lockOn = locked;
            lockMoved = false;
            if (!locked) return;
            GUI.enabled = false;
            if (Event.current.type != EventType.ScrollWheel)
            {
                lockMouse = Event.current.mousePosition;
                Event.current.mousePosition = new Vector2(-99999f, -99999f);
                lockMoved = true;
            }
        }

        public static void EndLock(Rect dim = default(Rect))
        {
            if (!lockOn) return;
            if (lockMoved)
                Event.current.mousePosition = lockMouse;
            GUI.enabled = true;
            lockOn = false;
            lockMoved = false;
            if (dim.width > 0f)
                Fill(dim, new Color(0.09f, 0.1f, 0.12f, 0.45f));
        }

        // CHANGED mark at the right edge of r. Click = back to the recommended value.
        // Returns the width it took.
        private static float Mark(Rect r, object target, string field)
        {
            if (lockOn || !Changed(target, field))
                return 0f;
            string t = "MUI_Changed".Translate();
            GameFont f = Text.Font;
            Text.Font = GameFont.Tiny;
            float w = Text.CalcSize(t).x + 12f;
            Text.Font = f;
            Rect m = new Rect(r.xMax - w, r.y + (r.height - 20f) / 2f, w, 20f);
            Frame(m, Warn, 1f, 3f);
            Label(m, t, Warn, GameFont.Tiny, TextAnchor.MiddleCenter);
            TooltipHandler.TipRegion(m, "MUI_ChangedTip".Translate(Show(RecValue(field))));
            if (Widgets.ButtonInvisible(m))
            {
                Reset(target, field);
                SoundDefOf.Tick_Low.PlayOneShotOnCamera();
                GUI.changed = true;
            }
            return w + 8f;
        }

        // Checkbox row: label, CHANGED mark, box on the right. The whole row clicks.
        public static void Check(Listing_Standard l, string label, string tip, ref bool value, object target, string field)
        {
            float w = l.ColumnWidth;
            float h = Mathf.Max(28f, Text.CalcHeight(label, w - 130f) + 6f);
            Rect row = l.GetRect(h);
            Widgets.DrawHighlightIfMouseover(row);
            if (!tip.NullOrEmpty())
                TooltipHandler.TipRegion(row, tip);
            Rect box = new Rect(row.xMax - 24f, row.y + (h - 24f) / 2f, 24f, 24f);
            float mw = Mark(new Rect(row.x, row.y, box.x - 6f - row.x, h), target, field);
            Rect lab = new Rect(row.x, row.y, box.x - 6f - mw - row.x, h);
            Label(lab, label, Text1);
            bool before = value;
            Widgets.Checkbox(box.x, box.y, ref value, 24f);
            if (value == before && Widgets.ButtonInvisible(lab))
            {
                value = !value;
                (value ? SoundDefOf.Checkbox_TurnedOn : SoundDefOf.Checkbox_TurnedOff).PlayOneShotOnCamera();
            }
            l.Gap(2f);
        }

        // Only the label line of a slider (with the CHANGED mark); the slider is drawn by the caller.
        public static void RowLabel(Listing_Standard l, string label, string tip, object target, string field)
        {
            float w = l.ColumnWidth;
            float h = Text.CalcHeight(label, w - 110f) + 4f;
            Rect top = l.GetRect(h);
            if (!tip.NullOrEmpty())
                TooltipHandler.TipRegion(top, tip);
            float mw = Mark(top, target, field);
            Label(new Rect(top.x, top.y, top.width - mw, h), label, Text1);
        }

        // Section title inside a listing.
        public static void Section(Listing_Standard l, string title)
        {
            l.Gap(6f);
            Rect r = l.GetRect(30f);
            Label(r, title, Text1, GameFont.Medium);
            Fill(new Rect(r.x, r.yMax, r.width, 1f), Border);
            l.Gap(6f);
        }

        // Profile card (Evergrowth style). Returns true when clicked.
        public static bool ProfileCard(Rect r, string title, string desc, bool selected, bool recommended)
        {
            Fill(r, selected ? OnBg : CardBg);
            Frame(r, selected ? Accent : Border, selected ? 2f : 1f);
            Widgets.DrawHighlightIfMouseover(r);
            Rect t = new Rect(r.x + 12f, r.y + 8f, r.width - 24f, 28f);
            Label(t, title, Text1, GameFont.Medium);
            if (recommended)
            {
                string tag = "MUI_RecTag".Translate();
                GameFont f = Text.Font;
                Text.Font = GameFont.Tiny;
                float w = Text.CalcSize(tag).x + 12f;
                Text.Font = f;
                Rect m = new Rect(r.xMax - 10f - w, r.y + 12f, w, 20f);
                Frame(m, Accent, 1f, 3f);
                Label(m, tag, Accent, GameFont.Tiny, TextAnchor.MiddleCenter);
            }
            Label(new Rect(r.x + 12f, r.y + 38f, r.width - 24f, r.height - 44f), desc, Muted, GameFont.Tiny, TextAnchor.UpperLeft);
            if (Widgets.ButtonInvisible(r))
            {
                SoundDefOf.Click.PlayOneShotOnCamera();
                return true;
            }
            return false;
        }

        // Slider row: label + value on top, slider under it with a green tick at the recommended value.
        public static float Slider(Listing_Standard l, string label, float value, float min, float max, string tip, object target, string field)
        {
            float w = l.ColumnWidth;
            float h = Text.CalcHeight(label, w - 110f) + 4f;
            Rect top = l.GetRect(h);
            if (!tip.NullOrEmpty())
                TooltipHandler.TipRegion(top, tip);
            float mw = Mark(top, target, field);
            Label(new Rect(top.x, top.y, top.width - mw, h), label, Text1);
            Rect s = l.GetRect(22f);
            if (!tip.NullOrEmpty())
                TooltipHandler.TipRegion(s, tip);
            object rv = RecValue(field);
            if (rv != null && max > min)
            {
                float v = rv is int ? (int)rv : rv is float ? (float)rv : float.NaN;
                if (!float.IsNaN(v))
                {
                    float x = s.x + 6f + (s.width - 12f) * Mathf.Clamp01((v - min) / (max - min));
                    Fill(new Rect(x - 1f, s.y + 2f, 2f, 18f), Accent);
                }
            }
            float nv = Widgets.HorizontalSlider(s, value, min, max, true);
            l.Gap(6f);
            return nv;
        }

        // Slider bar that only reports a change when the player really drags it.
        // (Widgets.HorizontalSlider with roundTo returns a rounded value every frame when the
        // stored value is not on the step – that plays the drag sound over and over.)
        public static float SliderBar(Rect r, float value, float min, float max, float roundTo = -1f)
        {
            float nv = Widgets.HorizontalSlider(r, value, min, max, true);
            if (nv == value)
                return value;
            if (roundTo > 0f)
                nv = Mathf.Clamp(Mathf.Round(nv / roundTo) * roundTo, min, max);
            return nv;
        }

        // ---------------- bigger settings window ----------------
        private static readonly AccessTools.FieldRef<Dialog_ModSettings, Mod> modRef =
            AccessTools.FieldRefAccess<Dialog_ModSettings, Mod>("mod");
        private static Type ourMod;

        public static void PatchWindow(Harmony h, Type modType)
        {
            ourMod = modType;
            try
            {
                h.Patch(AccessTools.PropertyGetter(typeof(Dialog_ModSettings), "InitialSize"),
                    postfix: new HarmonyMethod(typeof(MUI), "SizePostfix"));
            }
            catch (Exception e)
            {
#if MISAK_MLOG
                MLog.Warn("[" + modType.Name + "] bigger settings window: " + e.Message);
#else
                Log.Warning("[" + modType.Name + "] bigger settings window: " + e.Message);
#endif
            }
        }

        private static void SizePostfix(Dialog_ModSettings __instance, ref Vector2 __result)
        {
            Mod m = modRef(__instance);
            if (m == null || ourMod == null || m.GetType() != ourMod)
                return;
            __result = new Vector2(Mathf.Min(1040f, UI.screenWidth - 20f), Mathf.Min(880f, UI.screenHeight - 20f));
        }
    }
}
