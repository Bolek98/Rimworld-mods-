using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Urt2;
using Verse;
using Verse.Sound;

namespace UniversalRimTranslator
{
    // 2.2.0: your own translations. Find any text (original or translation), write your own
    // version, go back to the machine one or keep the original. Below: your words, used in
    // every translation ("kolonista" → "osadník").
    public static class EditorTab
    {
        private enum Filter { All, Mine, Problems, Untranslated }

        private sealed class Row
        {
            public string Source, Text;
            public bool Manual;
            public Verdict Verdict;
            public bool Has;
        }

        private static string query = "", lastQuery = null;
        private static Filter filter = Filter.All, lastFilter = Filter.All;
        private static int lastVersion = -1;
        private static List<Row> rows = new List<Row>();
        private static int total;
        private static Vector2 listScroll, editScroll, glossScroll;
        private static Row sel;
        private static string editText = "";
        private static string note;
        private static List<KeyValuePair<string, string>> gloss;
        private static string newFrom = "", newTo = "";
        private const int MaxRows = 300;
        private static string typed;
        private static float typedAt, refreshedAt;

        public static void Draw(Rect r)
        {
            Store store = Runtime.Store;
            if (!Runtime.Active || store == null)
            {
                Widgets.Label(r, "URT_Status_Inactive".Translate(Runtime.Phase));
                return;
            }
            // search line
            float y = r.y;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), "URT_Ed_Intro".Translate());
            GUI.color = Color.white;
            y += 26f;
            query = Widgets.TextField(new Rect(r.x, y, r.width * 0.45f, 30f), query ?? "");
            float fx = r.x + r.width * 0.45f + 8f;
            foreach (Filter f in Enum.GetValues(typeof(Filter)))
            {
                string lab = ("URT_Ed_F_" + f).Translate();
                float w = Mathf.Max(90f, Text.CalcSize(lab).x + 20f);
                Rect b = new Rect(fx, y, w, 30f);
                if (filter == f) MUI.Fill(b, MUI.OnBg);
                if (Widgets.ButtonText(b, lab, drawBackground: filter != f)) filter = f;
                fx += w + 4f;
            }
            y += 36f;

            // the memory holds hundreds of thousands of texts: search after a short pause in typing,
            // and new machine translations only refresh the list every few seconds
            float now = Time.realtimeSinceStartup;
            if (query != lastQuery || filter != lastFilter)
            {
                if (query != typed || filter != lastFilter) { typed = query; typedAt = now; }
                if (now - typedAt > 0.35f || filter != lastFilter) Refresh(store);
            }
            else if (store.Version != lastVersion && now - refreshedAt > 5f)
                Refresh(store);

            float glossH = 190f;
            Rect top = new Rect(r.x, y, r.width, r.yMax - y - glossH - 8f);
            Rect listR = new Rect(top.x, top.y, top.width * 0.48f, top.height);
            Rect editR = new Rect(listR.xMax + 10f, top.y, top.xMax - listR.xMax - 10f, top.height);
            DrawList(listR);
            DrawEdit(editR, store);
            DrawGlossary(new Rect(r.x, top.yMax + 8f, r.width, glossH), store);
        }

        private static void Refresh(Store store)
        {
            lastQuery = query;
            lastFilter = filter;
            lastVersion = store.Version;
            refreshedAt = Time.realtimeSinceStartup;
            string q = (query ?? "").Trim();
            var all = new Dictionary<string, Row>();
            foreach (var kv in store.Snapshot())
                all[kv.Key] = new Row { Source = kv.Key, Text = kv.Value.Text, Manual = kv.Value.Manual, Verdict = kv.Value.Verdict, Has = true };
            foreach (Item it in Runtime.Items)
            {
                string k = Store.Key(it.Source);
                if (k.Length > 0 && !all.ContainsKey(k)) all[k] = new Row { Source = k, Verdict = Verdict.Empty };
            }
            IEnumerable<Row> sel2 = all.Values;
            switch (filter)
            {
                case Filter.Mine: sel2 = sel2.Where(x => x.Manual); break;
                case Filter.Problems: sel2 = sel2.Where(x => x.Has && x.Verdict != Verdict.Ok); break;
                case Filter.Untranslated: sel2 = sel2.Where(x => !x.Has); break;
            }
            if (q.Length > 0)
                sel2 = sel2.Where(x => x.Source.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 ||
                                       (x.Text != null && x.Text.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0));
            else if (filter == Filter.All)
                sel2 = sel2.Where(x => x.Manual);      // nothing searched yet: show your own first
            var list = sel2.ToList();
            total = list.Count;
            rows = list.OrderBy(x => x.Source.Length).ThenBy(x => x.Source, StringComparer.OrdinalIgnoreCase).Take(MaxRows).ToList();
            if (sel != null)
                sel = rows.FirstOrDefault(x => x.Source == sel.Source) ?? sel;
        }

        private static void DrawList(Rect r)
        {
            MUI.Card(r);
            Rect inner = r.ContractedBy(6f);
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            string head = total > rows.Count ? "URT_Ed_CountMore".Translate(rows.Count, total) : "URT_Ed_Count".Translate(total);
            if (rows.Count == 0 && (query ?? "").Trim().Length == 0 && filter == Filter.All) head = "URT_Ed_SearchHint".Translate();
            Widgets.Label(new Rect(inner.x, inner.y, inner.width, 20f), head);
            GUI.color = Color.white;
            Rect outer = new Rect(inner.x, inner.y + 22f, inner.width, inner.height - 22f);
            const float rowH = 44f;
            Rect view = new Rect(0f, 0f, outer.width - 16f, rows.Count * rowH);
            Widgets.BeginScrollView(outer, ref listScroll, view);
            for (int i = 0; i < rows.Count; i++)
            {
                Row x = rows[i];
                Rect rr = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (sel == x) MUI.Fill(rr, MUI.OnBg);
                else if (Mouse.IsOver(rr)) Widgets.DrawHighlight(rr);
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 1f, rr.width - 8f, 20f), One(x.Source).Truncate(rr.width - 8f));
                GUI.color = x.Manual ? MUI.Accent : !x.Has ? MUI.Warn : x.Verdict != Verdict.Ok ? MUI.Warn : MUI.Muted;
                string t = x.Has ? (x.Manual ? "✎ " : "") + One(x.Text ?? "") : (string)"URT_Ed_NoTranslation".Translate();
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 21f, rr.width - 8f, 20f), t.Truncate(rr.width - 8f));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(rr))
                {
                    sel = x;
                    editText = x.Text ?? "";
                    note = null;
                }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static string One(string s) => (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");

        private static void DrawEdit(Rect r, Store store)
        {
            MUI.Card(r);
            Rect c = r.ContractedBy(8f);
            if (sel == null)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(c, "URT_Ed_Pick".Translate());
                GUI.color = Color.white;
                return;
            }
            store.TryGet(sel.Source, out Rec rec);
            float y = c.y;
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(c.x, y, c.width, 18f), "URT_Ed_Original".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 18f;
            float sh = Mathf.Min(Text.CalcHeight(sel.Source, c.width), 90f);
            Rect so = new Rect(c.x, y, c.width, sh);
            Widgets.LabelScrollable(so, sel.Source, ref editScroll);
            y += sh + 8f;
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            string who = rec == null ? "URT_Ed_NoTranslation".Translate() : rec.Manual ? "URT_Ed_YoursNow".Translate() : "URT_Ed_MachineNow".Translate(rec.Provider ?? "?");
            Widgets.Label(new Rect(c.x, y, c.width, 18f), "URT_Ed_Yours".Translate() + " · " + who);
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 18f;
            float th = c.yMax - y - 80f;
            editText = Widgets.TextArea(new Rect(c.x, y, c.width, Mathf.Max(60f, th)), editText ?? "");
            y += Mathf.Max(60f, th) + 6f;
            if (rec != null && rec.Manual && !string.IsNullOrEmpty(rec.Auto))
            {
                Text.Font = GameFont.Tiny;
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(c.x, y, c.width, 18f), ("URT_Ed_Machine".Translate() + " " + One(rec.Auto)).Truncate(c.width));
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
            y += 20f;
            float bw = (c.width - 18f) / 4f;
            if (Widgets.ButtonText(new Rect(c.x, y, bw, 30f), "URT_Ed_Save".Translate()))
            {
                if (string.IsNullOrWhiteSpace(editText)) note = "URT_Ed_EmptyNote".Translate();
                else
                {
                    store.PutManual(sel.Source, editText);
                    store.TryGet(sel.Source, out Rec after);
                    note = after != null && after.Usable ? (string)"URT_Ed_Saved".Translate() : (string)"URT_Ed_Broken".Translate(after?.Verdict.ToString() ?? "?");
                    Done();
                }
            }
            if (Widgets.ButtonText(new Rect(c.x + (bw + 6f), y, bw, 30f), "URT_Ed_Revert".Translate()))
            {
                store.PutManual(sel.Source, null);
                store.TryGet(sel.Source, out Rec after);
                editText = after?.Text ?? "";
                note = "URT_Ed_Reverted".Translate();
                Done();
            }
            if (Widgets.ButtonText(new Rect(c.x + 2 * (bw + 6f), y, bw, 30f), "URT_Ed_KeepOriginal".Translate()))
            {
                store.PutManual(sel.Source, sel.Source);
                editText = sel.Source;
                note = "URT_Ed_Kept".Translate();
                Done();
            }
            if (Widgets.ButtonText(new Rect(c.x + 3 * (bw + 6f), y, bw, 30f), "URT_Ed_Again".Translate()))
            {
                store.Forget(sel.Source);
                editText = "";
                note = "URT_Ed_AgainNote".Translate();
                Runtime.RetryFailed();
                Done();
            }
            TooltipHandler.TipRegion(new Rect(c.x + 3 * (bw + 6f), y, bw, 30f), "URT_Ed_AgainTip".Translate());
            if (note != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = MUI.Accent;
                Widgets.Label(new Rect(c.x, y + 32f, c.width, 18f), note);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }

        private static void Done()
        {
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Runtime.Reapply();
        }

        private static void DrawGlossary(Rect r, Store store)
        {
            MUI.Card(r);
            Rect c = r.ContractedBy(8f);
            if (store.Glossary == null) return;
            if (gloss == null) gloss = store.Glossary.Pairs;
            Widgets.Label(new Rect(c.x, c.y, c.width, 24f), "URT_Gl_Title".Translate());
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(c.x, c.y + 22f, c.width, 18f), "URT_Gl_Hint".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            float y = c.y + 44f;
            // add a word
            float w = (c.width - 140f) / 2f;
            newFrom = Widgets.TextField(new Rect(c.x, y, w - 20f, 28f), newFrom ?? "");
            Widgets.Label(new Rect(c.x + w - 16f, y + 2f, 20f, 26f), "→");
            newTo = Widgets.TextField(new Rect(c.x + w + 6f, y, w - 6f, 28f), newTo ?? "");
            if (Widgets.ButtonText(new Rect(c.xMax - 128f, y, 128f, 28f), "URT_Gl_Add".Translate()) && !string.IsNullOrWhiteSpace(newFrom))
            {
                gloss.RemoveAll(p => string.Equals(p.Key, newFrom.Trim(), StringComparison.OrdinalIgnoreCase));
                gloss.Add(new KeyValuePair<string, string>(newFrom.Trim(), (newTo ?? "").Trim()));
                SaveGlossary(store);
                newFrom = newTo = "";
            }
            y += 34f;
            Rect outer = new Rect(c.x, y, c.width, c.yMax - y);
            const float rowH = 26f;
            Rect view = new Rect(0f, 0f, outer.width - 16f, gloss.Count * rowH);
            Widgets.BeginScrollView(outer, ref glossScroll, view);
            int remove = -1;
            for (int i = 0; i < gloss.Count; i++)
            {
                Rect rr = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (Mouse.IsOver(rr)) Widgets.DrawHighlight(rr);
                Widgets.Label(new Rect(rr.x + 4f, rr.y, rr.width - 120f, rowH), gloss[i].Key + "  →  " + (gloss[i].Value.Length == 0 ? "URT_Gl_Remove".Translate().ToString() : gloss[i].Value));
                if (Widgets.ButtonText(new Rect(rr.xMax - 100f, rr.y, 96f, rowH - 2f), "URT_Gl_Delete".Translate())) remove = i;
            }
            Widgets.EndScrollView();
            if (remove >= 0)
            {
                gloss.RemoveAt(remove);
                SaveGlossary(store);
            }
        }

        private static void SaveGlossary(Store store)
        {
            store.Glossary.Set(gloss);
            gloss = store.Glossary.Pairs;
            Done();
        }
    }
}
