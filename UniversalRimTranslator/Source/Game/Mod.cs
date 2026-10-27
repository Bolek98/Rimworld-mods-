using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Urt2;
using Verse;

namespace UniversalRimTranslator
{
    public static class UrtVersion
    {
        public const string Number = "2.6.2";
        public static string Display => Number + " (test)";
    }

    public class UrtSettings : ModSettings
    {
        public bool recommended = true;
        public bool enabled = true;
        public bool translateDefs = true;
        public bool translateKeyed = true;
        public bool translateRules = true;
        public bool grammarFallback = true;
        public bool translateLive = true;   // 2.1.0: texts made in code, translated where they are drawn

        public string order = "edge,yandex,google,mymemory,lingva,deepl,azure,googlecloud,gemini,openai,libre";
        public string disabled = "lingva";

        // keys keep the names of version 0.9, so they carry over
        public string deepLApiKey = "", googleApiKey = "", geminiApiKey = "", geminiModel = "gemini-2.5-flash";
        public string azureApiKey = "", azureRegion = "global";
        public string myMemoryEmail = "";
        public string customEndpointType = "LibreTranslate", customEndpointUrl = "", customEndpointModel = "", customEndpointKey = "";
        public string aiUrl = "", aiKey = "", aiModel = "";
        public string skippedMods = "";     // 2.3.0: mods left in English (one name per line)
        public bool welcomeShown;           // 2.5.0: the first-start window was shown
        public bool pickFix = true;         // 2.5.0: Alt + right click on a text opens it in the editor
        public bool translateRaceNames;     // 2.5.2: race and xenotype names of mods are translated too (off = kept)
        public bool holdNewMods;            // 2.5.2: texts of newly added mods wait until the player lets them in
        public bool offlineOnly;            // 2.5.2: no services, only the translation memory

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "urt2_recommended", true);
            Scribe_Values.Look(ref enabled, "urt2_enabled", true);
            Scribe_Values.Look(ref translateDefs, "urt2_translateDefs", true);
            Scribe_Values.Look(ref translateKeyed, "urt2_translateKeyed", true);
            Scribe_Values.Look(ref translateRules, "urt2_translateRules", true);
            Scribe_Values.Look(ref grammarFallback, "urt2_grammarFallback", true);
            Scribe_Values.Look(ref translateLive, "urt2_translateLive", true);
            Scribe_Values.Look(ref order, "urt2_order", "edge,yandex,google,mymemory,lingva,deepl,azure,googlecloud,gemini,openai,libre");
            Scribe_Values.Look(ref disabled, "urt2_disabled", "lingva");
            Scribe_Values.Look(ref deepLApiKey, "deepLApiKey", "");
            Scribe_Values.Look(ref googleApiKey, "googleApiKey", "");
            Scribe_Values.Look(ref geminiApiKey, "geminiApiKey", "");
            Scribe_Values.Look(ref geminiModel, "urt2_geminiModel", "gemini-2.5-flash");
            Scribe_Values.Look(ref azureApiKey, "azureApiKey", "");
            Scribe_Values.Look(ref azureRegion, "azureRegion", "global");
            Scribe_Values.Look(ref myMemoryEmail, "myMemoryEmail", "");
            Scribe_Values.Look(ref customEndpointType, "customEndpointType", "LibreTranslate");
            Scribe_Values.Look(ref customEndpointUrl, "customEndpointUrl", "");
            Scribe_Values.Look(ref customEndpointModel, "customEndpointModel", "");
            Scribe_Values.Look(ref customEndpointKey, "customEndpointKey", "");
            Scribe_Values.Look(ref aiUrl, "urt2_aiUrl", "");
            Scribe_Values.Look(ref aiKey, "urt2_aiKey", "");
            Scribe_Values.Look(ref aiModel, "urt2_aiModel", "");
            Scribe_Values.Look(ref skippedMods, "urt2_skippedMods", "");
            Scribe_Values.Look(ref welcomeShown, "urt2_welcomeShown", false);
            Scribe_Values.Look(ref pickFix, "urt2_pickFix", true);
            Scribe_Values.Look(ref translateRaceNames, "urt2_translateRaceNames", false);
            Scribe_Values.Look(ref holdNewMods, "urt2_holdNewMods", false);
            Scribe_Values.Look(ref offlineOnly, "urt2_offlineOnly", false);
            if (Scribe.mode == LoadSaveMode.PostLoadInit) Normalize();
        }

        public void Normalize()
        {
            // services added later appear at the end of the order
            var o = order.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            foreach (Provider p in Providers.All) if (!o.Contains(p.Id)) o.Add(p.Id);
            o.RemoveAll(id => Providers.Get(id) == null);
            order = string.Join(",", o.ToArray());
        }

        public List<string> AllOrder()
        {
            Normalize();
            return order.Split(',').ToList();
        }

        public bool IsOn(string id) => !disabled.Split(',').Contains(id);

        public void SetOn(string id, bool on)
        {
            var d = disabled.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            d.Remove(id);
            if (!on) d.Add(id);
            disabled = string.Join(",", d.ToArray());
        }

        public List<string> ProviderOrder() => AllOrder().Where(IsOn).ToList();

        public ProviderConfig ProviderConfig()
        {
            string found = AzureProvider.FoundRegion;
            if (found != null && !string.Equals(found, azureRegion, StringComparison.OrdinalIgnoreCase))
            {
                azureRegion = found;
                Log.Message("[URT] Azure Translator: the key belongs to region \"" + (found.Length == 0 ? "(none)" : found) + "\" – saved in the settings.");
            }
            bool customIsLibre = (customEndpointType ?? "").IndexOf("libre", StringComparison.OrdinalIgnoreCase) >= 0;
            return new ProviderConfig
            {
                DeepLKey = deepLApiKey ?? "",
                AzureKey = azureApiKey ?? "", AzureRegion = azureRegion ?? "",
                GoogleCloudKey = googleApiKey ?? "",
                GeminiKey = geminiApiKey ?? "", GeminiModel = geminiModel ?? "gemini-2.5-flash",
                MyMemoryEmail = myMemoryEmail ?? "",
                LibreUrl = customIsLibre ? customEndpointUrl ?? "" : "", LibreKey = customIsLibre ? customEndpointKey ?? "" : "",
                AiUrl = !string.IsNullOrWhiteSpace(aiUrl) ? aiUrl : (!customIsLibre ? customEndpointUrl ?? "" : ""),
                AiKey = !string.IsNullOrWhiteSpace(aiKey) ? aiKey : (!customIsLibre ? customEndpointKey ?? "" : ""),
                AiModel = !string.IsNullOrWhiteSpace(aiModel) ? aiModel : (!customIsLibre ? customEndpointModel ?? "" : "")
            };
        }
    }

    public sealed class UniversalRimTranslatorMod : Mod
    {
        public static UrtSettings Settings;
        public static UniversalRimTranslatorMod Instance;

        // 2.5.0: open URT's window on a tab (3 = editor)
        public static void OpenTab(int t)
        {
            if (Instance == null) return;
            Instance.tab = t;
            // the settings dialog moved between namespaces (Verse in 1.6, RimWorld in 1.7) – found by name
            Type dt = AccessTools.TypeByName("RimWorld.Dialog_ModSettings") ?? AccessTools.TypeByName("Verse.Dialog_ModSettings");
            if (dt == null) return;
            foreach (Window w in Find.WindowStack.Windows)
                if (dt.IsInstanceOfType(w)) return;
            if (Activator.CreateInstance(dt, Instance) is Window win)
                Find.WindowStack.Add(win);
        }
        private static List<string> recBullets;
        private int tab;
        private Vector2 scroll;
        // 2.5.2: written by the test thread, read while drawing – thread-safe
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> testResult = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> testing = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        public UniversalRimTranslatorMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<UrtSettings>();
            Instance = this;
            RemoveOldGeneratedFiles(content?.RootDir);
            Settings.Normalize();
            MUI.Init(new UrtSettings(), new[] { "enabled", "translateDefs", "translateKeyed", "translateRules", "grammarFallback", "translateLive" });
            MNews.Register(content, UrtVersion.Number);
            if (Settings.recommended) MUI.ApplyAll(Settings);
            try { MUI.PatchWindow(new Harmony("bolek98.urt2.ui"), typeof(UniversalRimTranslatorMod)); } catch { }
            MLog.Game("[Universal RimTranslator] v" + UrtVersion.Display + " loaded (messages: Logs\\URT.log).");
        }

        // 0.9 could leave URT_*.xml language files in this mod's folder; they would load as a language pack
        private static void RemoveOldGeneratedFiles(string root)
        {
            try
            {
                if (string.IsNullOrEmpty(root)) return;
                string lang = System.IO.Path.Combine(root, "Languages");
                if (!System.IO.Directory.Exists(lang)) return;
                foreach (string f in System.IO.Directory.GetFiles(lang, "URT_*.xml", System.IO.SearchOption.AllDirectories))
                    try { System.IO.File.Delete(f); } catch { }
            }
            catch { }
        }

        public override string SettingsCategory() => "Universal RimTranslator";

        public override void WriteSettings()
        {
            base.WriteSettings();
            Runtime.Configure();
            Runtime.Kick();
        }

        public override void DoSettingsWindowContents(Rect r)
        {
            // 2.2.1: URT's own window is never live-translated (the editor shows the originals,
            // and translating every row while drawing made it stutter)
            Live.Suspended++;
            try { DrawWindow(r); }
            finally { Live.Suspended--; }
        }

        private void DrawWindow(Rect r)
        {
            MUI.Version(r, "v" + UrtVersion.Display + " · Bolek98");
            string[] tabs = { "URT_Tab_Overview".Translate(), "URT_Tab_Services".Translate(), "URT_Tab_Mods".Translate(), "URT_Tab_Editor".Translate(), QualityTab.TabLabel() };
            float tw = Mathf.Min(180f, (r.width - 6f * (tabs.Length - 1)) / tabs.Length);
            for (int i = 0; i < tabs.Length; i++)
            {
                Rect t = new Rect(r.x + i * (tw + 6f), r.y, tw, 30f);
                MUI.Fill(t, tab == i ? MUI.OnBg : MUI.CardBg);
                MUI.Frame(t, tab == i ? MUI.Accent : MUI.Border);
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(t, tabs[i]);
                Text.Anchor = TextAnchor.UpperLeft;
                if (Widgets.ButtonInvisible(t)) tab = i;
            }
            Rect body = new Rect(r.x, r.y + 40f, r.width, r.height - 40f);
            if (tab == 0) Overview(body);
            else if (tab == 1) Services(body);
            else if (tab == 2) ModsTab.Draw(body);
            else if (tab == 3) EditorTab.Draw(body);
            else QualityTab.Draw(body);
        }

        // ---------------- Overview ----------------
        private void Overview(Rect r)
        {
            UrtSettings s = Settings;
            if (recBullets == null)
                recBullets = new List<string> { "URT_Rec1".Translate(), "URT_Rec2".Translate(), "URT_Rec3".Translate() };
            float y = r.y;
            y += MUI.RecommendedCard(new Rect(r.x, y, r.width, 0f), s, s.recommended, v =>
            {
                s.recommended = v;
                if (v) MUI.ApplyAll(s);
                Runtime.Reapply();
            }, recBullets) + 8f;

            // status card
            var lines = new List<string>();
            if (!Runtime.Active)
                lines.Add("URT_Status_Inactive".Translate(Runtime.Phase));
            else
            {
                lines.Add("URT_Status_Language".Translate(Runtime.TargetName, Runtime.Target));
                lines.Add("URT_Status_Phase".Translate(PhaseText()));
                lines.Add("URT_Status_Counts".Translate(Runtime.Items.Count, Runtime.Store?.Count ?? 0, Runtime.Applied, Runtime.PendingCached));
                lines.Add("URT_Status_Session".Translate(Runtime.Translated, Runtime.Rejected, Runtime.Failed, GrammarFallback.Used));
                if (Live.On) lines.Add("URT_Status_Live".Translate(Live.Shown, Live.Queued, Live.Pending.Count, Live.Arrived));
                if (!string.IsNullOrEmpty(Runtime.MigrationReport)) lines.Add("URT_Status_Migrated".Translate(Runtime.MigrationReport));
            }
            string statusText = string.Join("\n", lines.ToArray());
            // 2.6.0: progress of "translate everything now" and the diagnostics line
            string diag = null;
            if (Runtime.Active)
            {
                string prog = UrtTools.ProgressLine();
                diag = (prog != null ? prog + "\n" : "") + UrtTools.Diagnostics();
            }
            float statusH = Text.CalcHeight(statusText, r.width - 24f);
            float diagH = diag == null ? 0f : Text.CalcHeight(diag, r.width - 24f) + 4f;
            Rect card = new Rect(r.x, y, r.width, Mathf.Max(150f, statusH + diagH + 24f));
            MUI.Card(card);
            Rect c = card.ContractedBy(12f);
            Widgets.Label(new Rect(c.x, c.y, c.width, statusH), statusText);
            if (diag != null)
            {
                GUI.color = UrtTools.AllRunning ? MUI.Accent : MUI.Muted;
                Widgets.Label(new Rect(c.x, c.y + statusH + 4f, c.width, diagH), diag);
                GUI.color = Color.white;
            }
            y = card.yMax + 8f;

            if (Runtime.Active)
            {
                // 2.6.0: one click – retry, forget failed machine translations, let waiting mods in, rescan, translate
                float big = 300f;
                Rect allR = new Rect(r.x, y, big, 34f);
                MUI.Fill(allR, MUI.OnBg);
                MUI.Frame(allR, MUI.Accent);
                if (Widgets.ButtonText(allR, UrtTools.AllRunning ? "URT_All_Running".Translate() : "URT_All_Button".Translate()) && !UrtTools.AllRunning)
                    UrtTools.ConfirmAll();
                TooltipHandler.TipRegion(allR, "URT_All_Tip".Translate());
                float bw = 200f;
                if (Widgets.ButtonText(new Rect(r.x + big + 8f, y, bw, 34f), Runtime.Paused ? "URT_Resume".Translate() : "URT_Pause".Translate()))
                {
                    Runtime.Paused = !Runtime.Paused;
                    Runtime.Kick();
                }
                if (Widgets.ButtonText(new Rect(r.x + big + bw + 16f, y, bw, 34f), "URT_RetryFailed".Translate()))
                    Runtime.RetryFailed();
                y += 40f;
                // 2.6.0: import from other translators, translation packs
                float iw = Mathf.Min(220f, (r.width - 16f) / 3f);
                Rect imp = new Rect(r.x, y, iw, 30f);
                if (Widgets.ButtonText(imp, "URT_Import_Button".Translate(), active: !UrtTools.Busy) && !UrtTools.Busy)
                    UrtTools.ImportOthers();
                TooltipHandler.TipRegion(imp, "URT_Import_Tip".Translate(System.IO.Path.GetFileName(UrtTools.TsvImportPath)));
                Rect exp = new Rect(r.x + iw + 8f, y, iw, 30f);
                if (Widgets.ButtonText(exp, "URT_Pack_Export".Translate(), active: !UrtTools.Busy) && !UrtTools.Busy)
                    Find.WindowStack.Add(new Dialog_UrtPack());
                TooltipHandler.TipRegion(exp, "URT_Pack_ExportTip".Translate());
                Rect pim = new Rect(r.x + 2f * (iw + 8f), y, iw, 30f);
                if (Widgets.ButtonText(pim, "URT_Pack_Import".Translate(), active: !UrtTools.Busy) && !UrtTools.Busy)
                    UrtTools.ImportPackMenu();
                TooltipHandler.TipRegion(pim, "URT_Pack_ImportTip".Translate());
                y += 34f;
                string note = UrtTools.Note;
                if (!string.IsNullOrEmpty(note))
                {
                    GUI.color = MUI.Muted;
                    Text.Font = GameFont.Tiny;
                    Widgets.Label(new Rect(r.x, y, r.width, 20f), note.Truncate(r.width));
                    Text.Font = GameFont.Small;
                    GUI.color = Color.white;
                    y += 22f;
                }
                y += 4f;
            }

            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
            float col2Y = 0f;
            var l = new Listing_Standard();
            MUI.BeginLock(s.recommended);
            try
            {
                l.Begin(rest);
                l.ColumnWidth = (rest.width - 24f) / 2f;
                MUI.Section(l, "URT_Sec_What".Translate());
                Check(l, "URT_Enabled", ref s.enabled, "enabled");
                Check(l, "URT_Defs", ref s.translateDefs, "translateDefs");
                Check(l, "URT_Keyed", ref s.translateKeyed, "translateKeyed");
                Check(l, "URT_Live", ref s.translateLive, "translateLive");
                Check(l, "URT_PickFix", ref s.pickFix, null);
                l.NewColumn();
                MUI.Section(l, "URT_Sec_Grammar".Translate());
                Check(l, "URT_Rules", ref s.translateRules, "translateRules");
                Check(l, "URT_GrammarFallback", ref s.grammarFallback, "grammarFallback");
                col2Y = l.CurHeight;
                l.End();
            }
            finally
            {
                MUI.EndLock(rest);
            }
            // 2.5.2: these three are the player's own choice – never locked by "recommended"
            var l2 = new Listing_Standard();
            float cw = (rest.width - 24f) / 2f;
            l2.Begin(new Rect(rest.x + cw + 17f, rest.y + col2Y, cw, Mathf.Max(0f, rest.height - col2Y)));
            MUI.Section(l2, "URT_Sec_Control".Translate());
            bool race0 = s.translateRaceNames, hold0 = s.holdNewMods;
            Check(l2, "URT_Offline", ref s.offlineOnly, null);
            Check(l2, "URT_HoldNew", ref s.holdNewMods, null);
            Check(l2, "URT_RaceNames", ref s.translateRaceNames, null);
            l2.End();
            if (hold0 != s.holdNewMods && !s.holdNewMods) Runtime.AllowNewMods();
            if (race0 != s.translateRaceNames) Live.ClearCache();
            if (GUI.changed) { Runtime.Reapply(); Runtime.Kick(); }
        }

        private static string PhaseText() => Runtime.Phase;

        private static void Check(Listing_Standard l, string key, ref bool v, string field) =>
            MUI.Check(l, key.Translate(), (key + "_Desc").Translate(), ref v, Settings, field);

        // ---------------- Services ----------------
        private void Services(Rect r)
        {
            UrtSettings s = Settings;
            string intro = "URT_Services_Intro".Translate();
            float ih = Text.CalcHeight(intro, r.width);
            GUI.color = MUI.Muted;
            Widgets.Label(new Rect(r.x, r.y, r.width, ih), intro);
            GUI.color = Color.white;
            Rect outer = new Rect(r.x, r.y + ih + 6f, r.width, r.height - ih - 6f);
            List<string> order = s.AllOrder();
            float rowH = 64f;
            float keysH = 0f;
            foreach (string id in order) keysH += KeyFieldCount(id) * 30f;
            Rect view = new Rect(0f, 0f, outer.width - 20f, order.Count * (rowH + 6f) + keysH + 20f);
            Widgets.BeginScrollView(outer, ref scroll, view);
            float y = 0f;
            for (int i = 0; i < order.Count; i++)
            {
                Provider p = Providers.Get(order[i]);
                float h = rowH + KeyFieldCount(p.Id) * 30f;
                Rect row = new Rect(0f, y, view.width, h);
                bool on = s.IsOn(p.Id);
                MUI.Card(row, on);
                Rect sw = new Rect(row.x + 10f, row.y + 12f, 48f, 24f);
                if (MUI.Switch(sw, on)) { s.SetOn(p.Id, !on); Runtime.Configure(); }
                Text.Font = GameFont.Small;
                Widgets.Label(new Rect(row.x + 68f, row.y + 8f, 330f, 26f), (i + 1) + ". " + p.Name);
                GUI.color = p.NeedsKey ? MUI.Warn : MUI.Accent;
                Text.Font = GameFont.Tiny;
                Widgets.Label(new Rect(row.x + 68f, row.y + 32f, 330f, 22f), p.NeedsKey ? (p.IsAi ? "URT_Badge_AiKey".Translate() : "URT_Badge_Key".Translate()) : "URT_Badge_Free".Translate());
                GUI.color = MUI.Muted;
                string st = !p.Configured(s.ProviderConfig()) ? "URT_NotSetUp".Translate().ToString() : Runtime.Chain.Status(p.Id);
                if (testResult.TryGetValue(p.Id, out string tr)) st = tr;
                Widgets.Label(new Rect(row.x + 400f, row.y + 8f, view.width - 400f - 250f, 48f), st);
                GUI.color = Color.white;
                Text.Font = GameFont.Small;
                // order and test
                float bx = row.xMax - 240f;
                if (i > 0 && Widgets.ButtonText(new Rect(bx, row.y + 14f, 34f, 28f), "▲")) { Swap(order, i, i - 1); s.order = string.Join(",", order.ToArray()); Runtime.Configure(); }
                if (i < order.Count - 1 && Widgets.ButtonText(new Rect(bx + 38f, row.y + 14f, 34f, 28f), "▼")) { Swap(order, i, i + 1); s.order = string.Join(",", order.ToArray()); Runtime.Configure(); }
                if (Widgets.ButtonText(new Rect(bx + 80f, row.y + 14f, 150f, 28f), "URT_Test".Translate())) Test(p);
                // keys
                float ky = row.y + rowH - 4f;
                foreach (var (label, get, set) in KeyFields(p.Id))
                {
                    Widgets.Label(new Rect(row.x + 68f, ky, 170f, 26f), label);
                    string v = get();
                    string nv = Widgets.TextField(new Rect(row.x + 240f, ky, view.width - 260f, 26f), v ?? "");
                    if (nv != v) { set(nv); Runtime.Configure(); }
                    ky += 30f;
                }
                y += h + 6f;
            }
            Widgets.EndScrollView();
        }

        private static void Swap(List<string> l, int a, int b) { string t = l[a]; l[a] = l[b]; l[b] = t; }

        private static int KeyFieldCount(string id) => KeyFields(id).Count;

        private static List<(string, Func<string>, Action<string>)> KeyFields(string id)
        {
            UrtSettings s = Settings;
            var l = new List<(string, Func<string>, Action<string>)>();
            switch (id)
            {
                case "deepl": l.Add(("URT_Key".Translate(), () => s.deepLApiKey, v => s.deepLApiKey = v)); break;
                case "azure":
                    l.Add(("URT_Key".Translate(), () => s.azureApiKey, v => { if (v != s.azureApiKey) AzureProvider.KeyChanged(); s.azureApiKey = v; }));
                    l.Add(("URT_Region".Translate(), () => s.azureRegion, v => { if (v != s.azureRegion) AzureProvider.KeyChanged(); s.azureRegion = v; }));
                    break;
                case "googlecloud": l.Add(("URT_Key".Translate(), () => s.googleApiKey, v => s.googleApiKey = v)); break;
                case "gemini":
                    l.Add(("URT_Key".Translate(), () => s.geminiApiKey, v => s.geminiApiKey = v));
                    l.Add(("URT_Model".Translate(), () => s.geminiModel, v => s.geminiModel = v));
                    break;
                case "openai":
                    l.Add(("URT_Url".Translate(), () => s.aiUrl, v => s.aiUrl = v));
                    l.Add(("URT_Key".Translate(), () => s.aiKey, v => s.aiKey = v));
                    l.Add(("URT_Model".Translate(), () => s.aiModel, v => s.aiModel = v));
                    break;
                case "libre":
                    l.Add(("URT_Url".Translate(), () => s.customEndpointUrl, v => { s.customEndpointUrl = v; s.customEndpointType = "LibreTranslate"; }));
                    l.Add(("URT_Key".Translate(), () => s.customEndpointKey, v => s.customEndpointKey = v));
                    break;
                case "mymemory": l.Add(("URT_Email".Translate(), () => s.myMemoryEmail, v => s.myMemoryEmail = v)); break;
            }
            return l;
        }

        // one short sentence through this service only
        private static void Test(Provider p)
        {
            if (p == null) return;
            // 2.5.2: empty fields – say so at once instead of starting a request; one test at a time
            ProviderConfig cfg;
            try { cfg = Settings.ProviderConfig(); }
            catch (Exception e) { testResult[p.Id] = "✗ " + e.Message; return; }
            if (!p.Configured(cfg)) { testResult[p.Id] = "✗ " + "URT_NotSetUp".Translate(); return; }
            if (!testing.TryAdd(p.Id, 0)) return;
            testResult[p.Id] = "URT_Testing".Translate();
            string target = Runtime.Target ?? Lang.ToIso(LanguageDatabase.activeLanguage?.folderName);
            if (string.IsNullOrEmpty(target) || Lang.Same(target, "en")) target = "cs";
            var chain = new Chain { Order = new List<string> { p.Id }, Config = cfg, Log = m => MLog.Info("[URT] test: " + m) };
            try
            {
                new Thread(() =>
                {
                    try
                    {
                        var job = new Job { Source = "The colonist is hungry and wants to eat {0}.", SourceLang = "en" };
                        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                        chain.Run(new List<Job> { job }, target, cts.Token);
                        string st = chain.Status(p.Id);
                        // 2.6.2: show the service's real error (HTTP code, message) instead of only "no service could translate it"
                        string why = st.StartsWith("ready") || st.StartsWith("ok") ? (job.Error ?? st) : st;
                        testResult[p.Id] = job.Result != null ? "✓ " + job.Result : "✗ " + why;
                        MLog.Info("[URT] test " + p.Name + ": " + (job.Result != null ? "ok" : why) + (p is AzureProvider ? " (" + AzureProvider.Mode + ")" : ""));
                    }
                    catch (Exception e) { testResult[p.Id] = "✗ " + e.Message; }
                    finally { testing.TryRemove(p.Id, out _); }
                }) { IsBackground = true }.Start();
            }
            catch (Exception e)
            {
                testing.TryRemove(p.Id, out _);
                testResult[p.Id] = "✗ " + e.Message;
            }
        }
    }
}
