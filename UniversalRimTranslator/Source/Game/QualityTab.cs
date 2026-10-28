using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using RimWorld;
using UnityEngine;
using Urt2;
using Verse;
using Verse.Sound;

namespace UniversalRimTranslator
{
    // 2.4.0: quality check. Machine translations that passed the automatic checks but still look
    // wrong: a lost number, English words left in, a translated name, a cut end, odd characters,
    // or the same short text translated differently in different places. Your own translations
    // are never touched.
    public static class QualityTab
    {
        public enum Kind { Number, English, Name, Cut, Chars, Differs }

        public sealed class Hit
        {
            public Kind Kind;
            public string Source, Text, Why, Mod, Better;
            public string ShowSrc, ShowTxt;
            public float ShowW;
        }

        private static volatile List<Hit> found;
        private static volatile bool scanning;
        private static List<Hit> hits;
        private static int scannedVersion = -1;
        private static HashSet<string> ok;
        private static string okTarget;
        private static Hit sel;
        private static string editText = "", note;
        private static Vector2 listScroll, srcScroll;
        private static int filter = -1;   // -1 = all kinds
        private static string query = "";

        public static string TabLabel()
        {
            string l = "URT_Tab_Quality".Translate();
            return hits != null && hits.Count > 0 ? l + " (" + hits.Count + ")" : l;
        }

        // ---------------- remembered "it is fine" ----------------
        private static string OkPath => Path.Combine(Runtime.Folder, "quality_ok_" + Runtime.Target + ".txt");
        private static string OkKey(Kind k, string src) => k + "\t" + src.Replace("\r", "").Replace("\n", "\\n");

        private static HashSet<string> Ok()
        {
            if (ok != null && okTarget == Runtime.Target) return ok;
            ok = new HashSet<string>();
            okTarget = Runtime.Target;
            try { if (File.Exists(OkPath)) foreach (string l in File.ReadAllLines(OkPath)) ok.Add(l); } catch { }
            return ok;
        }

        private static void MarkOk(Hit h)
        {
            string k = OkKey(h.Kind, h.Source);
            if (!Ok().Add(k)) return;
            try { File.AppendAllText(OkPath, k + "\n"); } catch { }
        }

        // ---------------- the check (background) ----------------
        private static readonly Regex Num = new Regex(@"(?<![\p{L}0-9{])\d+(?:[.,]\d+)?(?![0-9}])", RegexOptions.Compiled);
        private static readonly Regex Latin = new Regex(@"[A-Za-z][A-Za-z']{3,}", RegexOptions.Compiled);
        private static readonly Regex Markup = new Regex(@"</?[A-Za-z][^<>]*>|\{[^{}]*\}|\[[^\[\]]*\]", RegexOptions.Compiled);
        private static readonly string[] Entities = { "&amp;", "&lt;", "&gt;", "&quot;", "&apos;", "&#", "\\n", "\\t", "�" };

        private static string Plain(string s) => Markup.Replace(s ?? "", " ");

        private static string Norm(string s)
        {
            s = (s ?? "").Trim().TrimEnd(':', '.', '!', '?', '…', ' ').Trim();
            return s.ToLowerInvariant();
        }

        public static void Scan()
        {
            Store store = Runtime.Store;
            if (store == null || scanning) return;
            scanning = true;
            scannedVersion = store.Version;
            Item[] items = Runtime.Items.ToArray();
            List<string> wholeNames = Live.WholeNames();
            HashSet<string> okSet = new HashSet<string>(Ok());
            var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (store.Glossary != null)
                foreach (var p in store.Glossary.Pairs)
                    if (string.Equals(p.Key, p.Value, StringComparison.OrdinalIgnoreCase)) kept.Add(p.Key);
            bool english = (Runtime.Target ?? "").StartsWith("en");
            // texts are taken on the main thread; the check only fills in {0}
            string fNum = "URT_Q_WhyNumber".Translate(), fEng = "URT_Q_WhyEnglish".Translate(), fName = "URT_Q_WhyName".Translate(),
                   fCut = "URT_Q_WhyCut".Translate(), fChars = "URT_Q_WhyChars".Translate(), fSpaces = "URT_Q_WhySpaces".Translate(),
                   fDiff = "URT_Q_WhyDiffers".Translate();
            string F(string fmt, string a, string b = "") => fmt.Replace("{0}", a).Replace("{1}", b);
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                var res = new List<Hit>();
                try
                {
                    var mods = new Dictionary<string, string>();
                    foreach (Item it in items)
                    {
                        string k = Store.Key(it.Source);
                        if (k.Length > 0 && !mods.ContainsKey(k)) mods[k] = it.Mod;
                    }
                    var snap = store.Snapshot();
                    var groups = new Dictionary<string, List<KeyValuePair<string, Rec>>>();
                    foreach (var kv in snap)
                    {
                        Rec r = kv.Value;
                        if (r.Manual || r.Verdict != Verdict.Ok || string.IsNullOrEmpty(r.Text)) continue;
                        string src = kv.Key, tr = r.Text;
                        mods.TryGetValue(src, out string mod);
                        void Add(Kind k, string why, string better = null)
                        {
                            if (okSet.Contains(OkKey(k, src))) return;
                            res.Add(new Hit { Kind = k, Source = src, Text = tr, Why = why, Mod = mod, Better = better });
                        }

                        // 1. a number of the original is missing
                        foreach (Match m in Num.Matches(Plain(src)))
                            if (tr.IndexOf(m.Value, StringComparison.Ordinal) < 0 && tr.IndexOf(m.Value.Replace('.', ','), StringComparison.Ordinal) < 0)
                            {
                                Add(Kind.Number, F(fNum, m.Value));
                                break;
                            }

                        // 2. English words copied into the translation
                        if (!english && tr != src)
                        {
                            string ps = Plain(src), pt = Plain(tr);
                            var left = new List<string>();
                            foreach (Match m in Latin.Matches(pt))
                            {
                                string w = m.Value;
                                if (kept.Contains(w) || left.Contains(w)) continue;
                                if (char.IsUpper(w[0]) && m.Index > 0 && pt[m.Index - 1] != '\n') continue;     // a name in the middle
                                if (!Live.IsEnglishWord(w)) continue;
                                if (Regex.IsMatch(ps, @"\b" + Regex.Escape(w) + @"\b", RegexOptions.IgnoreCase)) left.Add(w);
                            }
                            int words = Regex.Matches(pt, @"\p{L}+").Count;
                            if (words >= 2 && (left.Count >= 2 || (left.Count == 1 && words <= 8)))
                                Add(Kind.English, F(fEng, string.Join(", ", left.Take(4).ToArray())));
                        }

                        // 3. a name (colonist, faction, place, mod) was translated
                        foreach (string n in wholeNames)
                            if (src.IndexOf(n, StringComparison.Ordinal) >= 0 && tr.IndexOf(n, StringComparison.Ordinal) < 0)
                            {
                                Add(Kind.Name, F(fName, n));
                                break;
                            }

                        // 4. cut off
                        string st = src.TrimEnd(), tt = tr.TrimEnd();
                        if (st.Length > 30 && tt.Length > 0)
                        {
                            char se = st[st.Length - 1], te = tt[tt.Length - 1];
                            bool srcEnds = ".!?)\"”".IndexOf(se) >= 0;
                            if ((srcEnds && char.IsLetter(te) && tt.Length < st.Length * 0.6f) || (tt.EndsWith("...") && !st.EndsWith("...")))
                                Add(Kind.Cut, fCut);
                        }

                        // 5. odd characters
                        foreach (string e in Entities)
                            if (tr.Contains(e) && !src.Contains(e))
                            {
                                Add(Kind.Chars, F(fChars, e));
                                goto charsDone;
                            }
                        if (tr.Contains("  ") && !src.Contains("  ")) Add(Kind.Chars, fSpaces);
                        charsDone:

                        // 6. short texts: the same text should read the same everywhere
                        string norm = Norm(src);
                        if (norm.Length >= 2 && norm.Split(' ').Length <= 4)
                        {
                            if (!groups.TryGetValue(norm, out var g)) groups[norm] = g = new List<KeyValuePair<string, Rec>>();
                            g.Add(kv);
                        }
                    }
                    foreach (var g in groups.Values)
                    {
                        if (g.Count < 2) continue;
                        var votes = g.GroupBy(x => Norm(x.Value.Text)).OrderByDescending(x => x.Count()).ToList();
                        if (votes.Count < 2) continue;
                        string best = votes[0].First().Value.Text;
                        foreach (var vote in votes.Skip(1))
                            foreach (var kv in vote)
                            {
                                if (okSet.Contains(OkKey(Kind.Differs, kv.Key))) continue;
                                mods.TryGetValue(kv.Key, out string mod);
                                res.Add(new Hit
                                {
                                    Kind = Kind.Differs, Source = kv.Key, Text = kv.Value.Text, Mod = mod, Better = best,
                                    Why = F(fDiff, Norm(best), g.Count.ToString())
                                });
                            }
                    }
                }
                catch (Exception e)
                {
                    MLog.Warn("[URT] quality check: " + e.Message);
                }
                found = res.OrderBy(h => (int)h.Kind).ThenBy(h => h.Source.Length).ToList();
                scanning = false;
            });
        }

        // ---------------- drawing ----------------
        public static void Draw(Rect r)
        {
            Store store = Runtime.Store;
            if (!Runtime.Active || store == null)
            {
                Widgets.Label(r, "URT_Status_Inactive".Translate(Runtime.Phase));
                return;
            }
            List<Hit> done = found;
            if (done != null)
            {
                found = null;
                hits = done;
                if (sel != null) sel = hits.FirstOrDefault(h => h.Source == sel.Source && h.Kind == sel.Kind);
            }
            if (hits == null && !scanning) Scan();

            float y = r.y;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, 24f), "URT_Q_Intro".Translate());
            GUI.color = Color.white;
            y += 26f;

            // kinds
            float fx = r.x;
            for (int k = -1; k <= (int)Kind.Differs; k++)
            {
                int n = hits == null ? 0 : k < 0 ? hits.Count : hits.Count(h => (int)h.Kind == k);
                string lab = (k < 0 ? "URT_Q_All".Translate().ToString() : ("URT_Q_K_" + (Kind)k).Translate().ToString()) + " " + n;
                float w = Mathf.Max(70f, Text.CalcSize(lab).x + 18f);
                Rect b = new Rect(fx, y, w, 28f);
                if (filter == k) MUI.Fill(b, MUI.OnBg);
                if (Widgets.ButtonText(b, lab, drawBackground: filter != k)) { filter = k; listScroll = Vector2.zero; }
                if (k >= 0) TooltipHandler.TipRegion(b, ("URT_Q_K_" + (Kind)k + "_Tip").Translate());
                fx += w + 4f;
            }
            y += 34f;
            query = Widgets.TextField(new Rect(r.x, y, r.width * 0.3f, 28f), query ?? "");
            float bx = r.x + r.width * 0.3f + 8f;
            if (Widgets.ButtonText(new Rect(bx, y, 170f, 28f), scanning ? "URT_Q_Checking".Translate() : "URT_Q_Recheck".Translate(), active: !scanning)) Scan();
            bx += 178f;
            List<Hit> shown = Shown();
            int canAgain = shown.Count(h => h.Kind != Kind.Differs);
            if (Widgets.ButtonText(new Rect(bx, y, 260f, 28f), "URT_Q_AgainAll".Translate(canAgain), active: canAgain > 0))
            {
                List<Hit> todo = shown.Where(h => h.Kind != Kind.Differs).ToList();
                Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation("URT_Q_AgainAllConfirm".Translate(todo.Count), () =>
                {
                    foreach (Hit h in todo) Again(store, h, false);
                    Runtime.RetryFailed();
                    hits.RemoveAll(h => todo.Contains(h));
                    Runtime.Reapply();
                }));
            }
            TooltipHandler.TipRegion(new Rect(bx, y, 260f, 28f), "URT_Q_AgainAllTip".Translate());
            bx += 268f;
            int canUnify = shown.Count(h => h.Kind == Kind.Differs);
            if (canUnify > 0 && Widgets.ButtonText(new Rect(bx, y, 220f, 28f), "URT_Q_UnifyAll".Translate(canUnify)))
            {
                foreach (Hit h in shown.Where(x => x.Kind == Kind.Differs).ToList()) Unify(store, h, false);
                Runtime.Reapply();
            }
            y += 34f;

            Rect listR = new Rect(r.x, y, r.width * 0.55f, r.yMax - y);
            Rect editR = new Rect(listR.xMax + 10f, y, r.xMax - listR.xMax - 10f, r.yMax - y);
            DrawList(listR, shown);
            DrawEdit(editR, store);
        }

        private static List<Hit> Shown()
        {
            if (hits == null) return new List<Hit>();
            string q = (query ?? "").Trim();
            return hits.Where(h => (filter < 0 || (int)h.Kind == filter) &&
                (q.Length == 0 || h.Source.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0 || (h.Text ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0
                 || (h.Mod ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
        }

        private static void DrawList(Rect r, List<Hit> list)
        {
            MUI.Card(r);
            Rect inner = r.ContractedBy(6f);
            if (list.Count == 0)
            {
                GUI.color = scanning ? MUI.Muted : MUI.Accent;
                Widgets.Label(inner, scanning ? "URT_Q_Checking".Translate() : "URT_Q_None".Translate());
                GUI.color = Color.white;
                return;
            }
            Text.Font = GameFont.Tiny;
            const float rowH = 62f;
            Rect view = new Rect(0f, 0f, inner.width - 16f, list.Count * rowH);
            Widgets.BeginScrollView(inner, ref listScroll, view);
            int first = Mathf.Max(0, (int)(listScroll.y / rowH) - 1);
            int last = Mathf.Min(list.Count - 1, (int)((listScroll.y + inner.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                Hit h = list[i];
                Rect rr = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (sel == h) MUI.Fill(rr, MUI.OnBg);
                else if (Mouse.IsOver(rr)) Widgets.DrawHighlight(rr);
                if (h.ShowSrc == null || h.ShowW != rr.width)
                {
                    h.ShowW = rr.width;
                    h.ShowSrc = Cut(One(h.Source), rr.width - 8f);
                    h.ShowTxt = Cut("→ " + One(h.Text), rr.width - 8f);
                }
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 1f, rr.width - 8f, 18f), h.ShowSrc);
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 20f, rr.width - 8f, 18f), h.ShowTxt);
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(rr.x + 4f, rr.y + 39f, rr.width - 8f, 18f), ("⚠ " + h.Why + (h.Mod != null ? "  ·  " + h.Mod : "")).Truncate(rr.width - 8f));
                GUI.color = Color.white;
                if (Widgets.ButtonInvisible(rr))
                {
                    sel = h;
                    editText = h.Text ?? "";
                    note = null;
                }
            }
            Widgets.EndScrollView();
            Text.Font = GameFont.Small;
        }

        private static string Cut(string s, float width)
        {
            int max = Mathf.Max(8, (int)(width / 5f));
            if (s.Length > max) s = s.Substring(0, max) + "…";
            return s.Truncate(width);
        }

        private static string One(string s) => (s ?? "").Replace("\r", "").Replace("\n", " ⏎ ");

        private static void DrawEdit(Rect r, Store store)
        {
            MUI.Card(r);
            Rect c = r.ContractedBy(8f);
            if (sel == null)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(c, "URT_Q_Pick".Translate());
                GUI.color = Color.white;
                return;
            }
            float y = c.y;
            GUI.color = MUI.Warn;
            float wh = Text.CalcHeight("⚠ " + sel.Why, c.width);
            Widgets.Label(new Rect(c.x, y, c.width, wh), "⚠ " + sel.Why);
            GUI.color = Color.white;
            y += wh + 6f;
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(c.x, y, c.width, 18f), "URT_Ed_Original".Translate() + (sel.Mod != null ? "  ·  " + sel.Mod : ""));
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 18f;
            float sh = Mathf.Min(Text.CalcHeight(sel.Source, c.width), 110f);
            Widgets.LabelScrollable(new Rect(c.x, y, c.width, sh), sel.Source, ref srcScroll);
            y += sh + 8f;
            Text.Font = GameFont.Tiny;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(c.x, y, c.width, 18f), "URT_Q_YourFix".Translate());
            GUI.color = Color.white;
            Text.Font = GameFont.Small;
            y += 18f;
            float th = Mathf.Max(60f, c.yMax - y - 120f);
            editText = Widgets.TextArea(new Rect(c.x, y, c.width, th), editText ?? "");
            y += th + 8f;
            float bw = (c.width - 6f) / 2f;
            if (Widgets.ButtonText(new Rect(c.x, y, bw, 30f), "URT_Q_Save".Translate()))
            {
                if (string.IsNullOrWhiteSpace(editText)) note = "URT_Ed_EmptyNote".Translate();
                else
                {
                    store.PutManual(sel.Source, editText);
                    store.TryGet(sel.Source, out Rec after);
                    if (after != null && after.Usable) Resolve(sel, "URT_Ed_Saved".Translate());
                    else note = "URT_Ed_Broken".Translate(after?.Verdict.ToString() ?? "?");
                }
            }
            TooltipHandler.TipRegion(new Rect(c.x, y, bw, 30f), "URT_Q_SaveTip".Translate());
            if (sel != null && sel.Kind == Kind.Differs && sel.Better != null)
            {
                if (Widgets.ButtonText(new Rect(c.x + bw + 6f, y, bw, 30f), "URT_Q_Unify".Translate()))
                    Unify(store, sel, true);
            }
            else if (sel != null && Widgets.ButtonText(new Rect(c.x + bw + 6f, y, bw, 30f), "URT_Ed_Again".Translate()))
            {
                Again(store, sel, true);
            }
            y += 36f;
            if (sel != null && Widgets.ButtonText(new Rect(c.x, y, bw, 30f), "URT_Q_Fine".Translate()))
            {
                MarkOk(sel);
                Resolve(sel, "URT_Q_FineNote".Translate());
            }
            if (sel != null && Widgets.ButtonText(new Rect(c.x + bw + 6f, y, bw, 30f), "URT_Ed_KeepOriginal".Translate()))
            {
                store.PutManual(sel.Source, sel.Source);
                Resolve(sel, "URT_Ed_Kept".Translate());
            }
            y += 34f;
            if (note != null)
            {
                Text.Font = GameFont.Tiny;
                GUI.color = MUI.Accent;
                Widgets.Label(new Rect(c.x, y, c.width, 36f), note);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
            }
        }

        private static void Again(Store store, Hit h, bool one)
        {
            store.Forget(h.Source);
            Live.Requeue(h.Source);
            if (one)
            {
                Runtime.RetryFailed();
                Resolve(h, "URT_Ed_AgainNote".Translate());
            }
        }

        // the most common translation, with this text's own capital letter and ending
        private static void Unify(Store store, Hit h, bool one)
        {
            if (h.Better == null) return;
            string core = h.Better.Trim().TrimEnd(':', '.', '!', '?', '…', ' ');
            string src = h.Source.Trim();
            if (src.Length > 0 && core.Length > 0)
            {
                core = char.IsUpper(src[0]) ? char.ToUpper(core[0]) + core.Substring(1) : char.ToLower(core[0]) + core.Substring(1);
                int e = src.Length;
                while (e > 0 && ":.!?… ".IndexOf(src[e - 1]) >= 0) e--;
                core += src.Substring(e);
            }
            store.PutAuto(h.Source, core, "URT", Validate.Check(Store.Key(h.Source), core, Runtime.Target));
            if (one) Resolve(h, "URT_Q_Unified".Translate());
            else hits?.Remove(h);
        }

        private static void Resolve(Hit h, string msg)
        {
            List<Hit> list = Shown();
            int i = list.IndexOf(h);
            hits?.RemoveAll(x => x.Source == h.Source && (x.Kind == h.Kind || x.Kind != Kind.Differs));
            list = Shown();
            sel = list.Count == 0 ? null : list[Mathf.Clamp(i, 0, list.Count - 1)];
            editText = sel?.Text ?? "";
            note = msg;
            SoundDefOf.Tick_High.PlayOneShotOnCamera();
            Runtime.Reapply();
        }
    }
}
