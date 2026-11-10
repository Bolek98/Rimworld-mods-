using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace UniversalRimTranslator
{
    // 1.2.0: the "Sources" tab – translations taken from other mods, the library and URL packs.
    public static class SourcesTab
    {
        private static Vector2 scroll;
        private static string newUrl = "";

        private static UrtSettings S => UniversalRimTranslatorMod.Settings;

        public static void Draw(Rect r)
        {
            float y = r.y;
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, y, r.width, 46f), "URT_Src_Intro".Translate());
            GUI.color = Color.white;
            y += 48f;

            // master switch
            Rect sw = new Rect(r.x, y, 52f, 26f);
            bool on = S.importOn;
            if (MUI.Switch(sw, on)) { S.importOn = !on; Sources.RestartNeeded = true; }
            Widgets.Label(new Rect(sw.xMax + 10f, y + 2f, r.width - 400f, 26f), "URT_Src_Use".Translate());
            float bw = 170f;
            if (Widgets.ButtonText(new Rect(r.xMax - 2 * bw - 8f, y - 2f, bw, 30f), "URT_Src_Rescan".Translate())) Sources.Rescan();
            if (Widgets.ButtonText(new Rect(r.xMax - bw, y - 2f, bw, 30f), "URT_Src_OpenLibrary".Translate())) Sources.OpenLibrary();
            TooltipHandler.TipRegion(new Rect(r.xMax - bw, y - 2f, bw, 30f), "URT_Src_LibraryTip".Translate(Sources.LibraryRoot));
            y += 34f;

            List<Sources.Source> list = Sources.Found ?? new List<Sources.Source>();
            GUI.color = MUI.Muted;
            string sum = "URT_Src_Sum".Translate(list.Count, list.Count(x => x.On), Sources.TotalUsed, Sources.PerMod.Count);
            if (Sources.RestartNeeded) sum += "   " + "URT_Src_Restart".Translate();
            Widgets.Label(new Rect(r.x, y, r.width, 24f), sum.Truncate(r.width));
            GUI.color = Color.white;
            y += 26f;
            if (!string.IsNullOrEmpty(Sources.LastError))
            {
                GUI.color = MUI.Warn;
                Widgets.Label(new Rect(r.x, y, r.width, 24f), ("✗ " + Sources.LastError).Truncate(r.width));
                GUI.color = Color.white;
                y += 26f;
            }

            // URL packs (bottom part)
            List<string> urls = Sources.Lines(S.importUrls);
            float urlH = 30f + 34f + Mathf.Min(urls.Count, 4) * 28f + 28f;
            Rect listR = new Rect(r.x, y, r.width, r.yMax - y - urlH - 8f);
            DrawList(listR, list);
            DrawUrls(new Rect(r.x, listR.yMax + 8f, r.width, urlH), urls);
        }

        private static void DrawList(Rect outer, List<Sources.Source> list)
        {
            MUI.Fill(outer, MUI.CardBg);
            if (list.Count == 0)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(outer.ContractedBy(10f), "URT_Src_None".Translate());
                GUI.color = Color.white;
                return;
            }
            const float rowH = 44f;
            Rect view = new Rect(0f, 0f, outer.width - 16f, list.Count * rowH);
            Widgets.BeginScrollView(outer, ref scroll, view);
            int first = Mathf.Max(0, (int)(scroll.y / rowH) - 1);
            int last = Mathf.Min(list.Count - 1, (int)((scroll.y + outer.height) / rowH) + 1);
            for (int i = first; i <= last; i++)
            {
                Sources.Source s = list[i];
                Rect row = new Rect(0f, i * rowH, view.width, rowH - 2f);
                if (i % 2 == 1) Widgets.DrawLightHighlight(row);
                bool on = s.On, was = on;
                Widgets.Checkbox(new Vector2(row.x + 6f, row.y + 10f), ref on);
                if (on != was) Sources.SetOn(s, on);

                // ▲ ▼ priority (higher in the list wins when two packs translate the same text)
                float ax = row.xMax - 56f;
                if (i > 0 && Widgets.ButtonText(new Rect(ax, row.y + 8f, 26f, 26f), "▲")) Sources.Move(s, -1);
                if (i < list.Count - 1 && Widgets.ButtonText(new Rect(ax + 28f, row.y + 8f, 26f, 26f), "▼")) Sources.Move(s, 1);
                TooltipHandler.TipRegion(new Rect(ax, row.y, 56f, row.height), "URT_Src_OrderTip".Translate());

                float statW = Mathf.Min(260f, row.width * 0.3f);
                Rect name = new Rect(row.x + 36f, row.y + 2f, row.width - 36f - statW - 66f, 22f);
                GUI.color = on ? Color.white : MUI.Muted;
                Widgets.Label(name, ((i + 1) + ". " + s.Name).Truncate(name.width));
                GUI.color = MUI.Muted;
                Text.Font = GameFont.Tiny;
                string covers = s.Covers.Count == 0 ? (string)"URT_Src_CoversNone".Translate()
                    : string.Join(", ", s.Covers.OrderByDescending(kv => kv.Value).Take(4).Select(kv => kv.Key).ToArray()) + (s.Covers.Count > 4 ? " +" + (s.Covers.Count - 4) : "");
                Rect sub = new Rect(name.x, row.y + 23f, name.width, 18f);
                Widgets.Label(sub, (KindLabel(s.Kind) + " · " + covers).Truncate(sub.width));
                Rect stat = new Rect(name.xMax + 6f, row.y + 4f, statW, 36f);
                int used = s.UsedKeyed + s.UsedDefs + s.UsedStrings;
                Widgets.Label(stat, "URT_Src_Counts".Translate(used, s.Keyed + s.Inject + s.Strings));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                TooltipHandler.TipRegion(new Rect(row.x, row.y, row.width - 60f, row.height), Tip(s));
            }
            Widgets.EndScrollView();
        }

        private static string KindLabel(string k) => ("URT_Src_Kind_" + k).Translate();

        private static string Tip(Sources.Source s)
        {
            string covers = s.Covers.Count == 0 ? (string)"URT_Src_CoversNone".Translate()
                : string.Join("\n", s.Covers.OrderByDescending(kv => kv.Value).Take(25).Select(kv => "• " + kv.Key + ": " + kv.Value).ToArray());
            return s.Name + "\n" + s.Root + "\n\n" + "URT_Src_TipCounts".Translate(s.UsedKeyed, s.UsedDefs, s.UsedStrings, s.Files) + "\n\n" + covers;
        }

        private static void DrawUrls(Rect r, List<string> urls)
        {
            float y = r.y;
            Widgets.Label(new Rect(r.x, y, r.width - 260f, 28f), "URT_Src_Urls".Translate());
            Rect auto = new Rect(r.xMax - 250f, y, 250f, 28f);
            bool a = S.importUrlAuto;
            Widgets.CheckboxLabeled(auto, "URT_Src_UrlAuto".Translate(), ref a);
            S.importUrlAuto = a;
            TooltipHandler.TipRegion(auto, "URT_Src_UrlAutoTip".Translate());
            y += 30f;

            float bw = 140f;
            newUrl = Widgets.TextField(new Rect(r.x, y, r.width - 2 * bw - 16f, 30f), newUrl ?? "");
            if (string.IsNullOrEmpty(newUrl))
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(r.x + 6f, y + 5f, r.width - 2 * bw - 30f, 24f), "URT_Src_UrlHint".Translate());
                GUI.color = Color.white;
            }
            if (Widgets.ButtonText(new Rect(r.xMax - 2 * bw - 8f, y, bw, 30f), "URT_Src_UrlAdd".Translate()))
            {
                string u = (newUrl ?? "").Trim();
                if (Urt2.SourcesLogic.ToDownloadUrl(u, out string _, out string err) == null)
                    Messages.Message("✗ " + err, MessageTypeDefOf.RejectInput, false);
                else if (!urls.Contains(u))
                {
                    urls.Add(u);
                    S.importUrls = string.Join("\n", urls.ToArray());
                    newUrl = "";
                    S.Write();
                }
            }
            string upd = Sources.Downloading ? (string)"URT_Src_UrlBusy".Translate() : (string)"URT_Src_UrlUpdate".Translate();
            if (Widgets.ButtonText(new Rect(r.xMax - bw, y, bw, 30f), upd) && !Sources.Downloading) Sources.UpdateAll(true);
            y += 34f;

            foreach (string u in urls.Take(4).ToList())
            {
                Rect row = new Rect(r.x, y, r.width, 26f);
                string st;
                lock (Sources.UrlStatus) Sources.UrlStatus.TryGetValue(u, out st);
                if (st == null) st = Sources.MetaInfo(u);
                float stW = Mathf.Min(320f, r.width * 0.35f);
                Widgets.Label(new Rect(row.x, row.y, row.width - stW - 40f, 26f), u.Truncate(row.width - stW - 40f));
                GUI.color = st.StartsWith("✗") ? MUI.Warn : MUI.Muted;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.xMax - stW - 34f, row.y + 3f, stW, 22f), st.Truncate(stW));
                Text.Font = GameFont.Small;
                GUI.color = Color.white;
                if (Widgets.ButtonText(new Rect(row.xMax - 28f, row.y, 28f, 26f), "×"))
                {
                    urls.Remove(u);
                    S.importUrls = string.Join("\n", urls.ToArray());
                    S.Write();
                }
                y += 28f;
            }
            if (urls.Count > 4)
            {
                GUI.color = MUI.Muted;
                Widgets.Label(new Rect(r.x, y, r.width, 24f), "+" + (urls.Count - 4));
                GUI.color = Color.white;
            }
        }
    }
}
