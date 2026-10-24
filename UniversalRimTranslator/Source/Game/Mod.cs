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
        public const string Number = "2.0.1";
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

        public string order = "edge,yandex,google,mymemory,lingva,deepl,azure,googlecloud,gemini,openai,libre";
        public string disabled = "lingva";

        // keys keep the names of version 0.9, so they carry over
        public string deepLApiKey = "", googleApiKey = "", geminiApiKey = "", geminiModel = "gemini-2.5-flash";
        public string azureApiKey = "", azureRegion = "global";
        public string myMemoryEmail = "";
        public string customEndpointType = "LibreTranslate", customEndpointUrl = "", customEndpointModel = "", customEndpointKey = "";
        public string aiUrl = "", aiKey = "", aiModel = "";

        public override void ExposeData()
        {
            Scribe_Values.Look(ref recommended, "urt2_recommended", true);
            Scribe_Values.Look(ref enabled, "urt2_enabled", true);
            Scribe_Values.Look(ref translateDefs, "urt2_translateDefs", true);
            Scribe_Values.Look(ref translateKeyed, "urt2_translateKeyed", true);
            Scribe_Values.Look(ref translateRules, "urt2_translateRules", true);
            Scribe_Values.Look(ref grammarFallback, "urt2_grammarFallback", true);
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
        private static List<string> recBullets;
        private int tab;
        private Vector2 scroll;
        private static readonly Dictionary<string, string> testResult = new Dictionary<string, string>();

        public UniversalRimTranslatorMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<UrtSettings>();
            RemoveOldGeneratedFiles(content?.RootDir);
            Settings.Normalize();
            MUI.Init(new UrtSettings(), new[] { "enabled", "translateDefs", "translateKeyed", "translateRules", "grammarFallback" });
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
            MUI.Version(r, "v" + UrtVersion.Display + " · Bolek98");
            string[] tabs = { "URT_Tab_Overview".Translate(), "URT_Tab_Services".Translate() };
            float tw = 180f;
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
            else Services(body);
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
            Rect card = new Rect(r.x, y, r.width, 150f);
            MUI.Card(card);
            Rect c = card.ContractedBy(12f);
            var lines = new List<string>();
            if (!Runtime.Active)
                lines.Add("URT_Status_Inactive".Translate(Runtime.Phase));
            else
            {
                lines.Add("URT_Status_Language".Translate(Runtime.TargetName, Runtime.Target));
                lines.Add("URT_Status_Phase".Translate(PhaseText()));
                lines.Add("URT_Status_Counts".Translate(Runtime.Items.Count, Runtime.Store?.Count ?? 0, Runtime.Applied, Runtime.PendingCached));
                lines.Add("URT_Status_Session".Translate(Runtime.Translated, Runtime.Rejected, Runtime.Failed, GrammarFallback.Used));
                if (!string.IsNullOrEmpty(Runtime.MigrationReport)) lines.Add("URT_Status_Migrated".Translate(Runtime.MigrationReport));
            }
            Widgets.Label(c, string.Join("\n", lines.ToArray()));
            y = card.yMax + 8f;

            if (Runtime.Active)
            {
                float bw = 220f;
                if (Widgets.ButtonText(new Rect(r.x, y, bw, 32f), Runtime.Paused ? "URT_Resume".Translate() : "URT_Pause".Translate()))
                {
                    Runtime.Paused = !Runtime.Paused;
                    Runtime.Kick();
                }
                if (Widgets.ButtonText(new Rect(r.x + bw + 8f, y, bw, 32f), "URT_RetryFailed".Translate()))
                    Runtime.RetryFailed();
                y += 40f;
            }

            Rect rest = new Rect(r.x, y, r.width, r.yMax - y);
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
                l.NewColumn();
                MUI.Section(l, "URT_Sec_Grammar".Translate());
                Check(l, "URT_Rules", ref s.translateRules, "translateRules");
                Check(l, "URT_GrammarFallback", ref s.grammarFallback, "grammarFallback");
                l.End();
            }
            finally
            {
                MUI.EndLock(rest);
            }
            if (GUI.changed) Runtime.Reapply();
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
                    l.Add(("URT_Key".Translate(), () => s.azureApiKey, v => s.azureApiKey = v));
                    l.Add(("URT_Region".Translate(), () => s.azureRegion, v => s.azureRegion = v));
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
            testResult[p.Id] = "URT_Testing".Translate();
            string target = Runtime.Target ?? Lang.ToIso(LanguageDatabase.activeLanguage?.folderName);
            if (Lang.Same(target, "en")) target = "cs";
            var chain = new Chain { Order = new List<string> { p.Id }, Config = Settings.ProviderConfig() };
            new Thread(() =>
            {
                try
                {
                    var job = new Job { Source = "The colonist is hungry and wants to eat {0}.", SourceLang = "en" };
                    chain.Run(new List<Job> { job }, target, CancellationToken.None);
                    testResult[p.Id] = job.Result != null ? "✓ " + job.Result : "✗ " + (job.Error ?? chain.Status(p.Id));
                }
                catch (Exception e) { testResult[p.Id] = "✗ " + e.Message; }
            }) { IsBackground = true }.Start();
        }
    }
}
