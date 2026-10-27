using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;

namespace Urt2
{
    public enum FailKind { Network, RateLimit, Auth, Blocked, Unsupported, BadAnswer }

    public sealed class ProviderError : Exception
    {
        public readonly FailKind Kind;
        public readonly int RetryAfterSeconds;
        public ProviderError(FailKind kind, string message, int retryAfter = 0) : base(message) { Kind = kind; RetryAfterSeconds = retryAfter; }
    }

    // Settings a provider needs (keys, own endpoints). Filled from the mod settings.
    public sealed class ProviderConfig
    {
        public string DeepLKey = "";
        public string AzureKey = "", AzureRegion = "";
        public string GoogleCloudKey = "";
        public string GeminiKey = "", GeminiModel = "gemini-2.5-flash";
        public string AiUrl = "", AiKey = "", AiModel = "";          // OpenAI-compatible (OpenAI, OpenRouter, Ollama, LM Studio …)
        public string LibreUrl = "", LibreKey = "";                  // LibreTranslate-compatible server
        public string MyMemoryEmail = "";
        public string LingvaUrl = "https://lingva.lunar.icu";
    }

    // 2.5.2: what an AI service may know about a batch: one hint per text (null = none) and
    // general notes. Ignored by the plain translation services.
    public sealed class TranslateContext
    {
        public List<string> Hints;
        public string Notes;
    }

    public abstract class Provider
    {
        public abstract string Id { get; }
        public abstract string Name { get; }
        public virtual bool Free => true;
        public virtual bool NeedsKey => false;
        public virtual int MaxItems => 25;
        public virtual int MaxChars => 4500;
        public virtual double MinIntervalSeconds => 0.4;     // pause between requests
        public virtual bool IsAi => false;
        public virtual bool Configured(ProviderConfig c) => true;
        public virtual string Code(string iso) => iso;

        // texts are already masked ({0} placeholders, single lines); src null = detect.
        // Returns one entry per text (null where that one failed). Throws ProviderError when the
        // whole request failed.
        public abstract List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct);

        // 2.5.2: the same with context (only AI services use it)
        public virtual List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct, TranslateContext ctx)
            => Translate(texts, src, tgt, cfg, ct);

        // 2.5.2: an own server address that is not a valid http(s) URL fails at once with a clear message
        // (before, an empty or broken address threw deep inside the HTTP client)
        protected Uri CheckUrl(string url)
        {
            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri u) || (u.Scheme != "http" && u.Scheme != "https"))
                throw new ProviderError(FailKind.Auth, Name + ": the address is not a valid http(s) URL");
            return u;
        }

        // ---- HTTP helpers ----
        private static HttpClient client;
        protected static HttpClient Http
        {
            get
            {
                if (client == null)
                {
                    var h = new HttpClientHandler();
                    try { h.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate; } catch { }
                    var c = new HttpClient(h) { Timeout = TimeSpan.FromSeconds(30) };
                    c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
                    client = c;
                }
                return client;
            }
        }

        protected string Send(HttpRequestMessage req, CancellationToken ct)
        {
            HttpResponseMessage resp;
            try
            {
                resp = Http.SendAsync(req, ct).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new ProviderError(FailKind.Network, Name + ": timed out");
            }
            catch (HttpRequestException e)
            {
                throw new ProviderError(FailKind.Network, Name + ": " + e.Message);
            }
            using (resp)
            {
                string body = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                int code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                    return body;
                int retry = 0;
                try { if (resp.Headers.RetryAfter?.Delta != null) retry = (int)resp.Headers.RetryAfter.Delta.Value.TotalSeconds; } catch { }
                string msg = Name + ": HTTP " + code + " " + Short(body);
                if (code == 429 || code == 503 || code == 502 || code == 504) throw new ProviderError(FailKind.RateLimit, msg, retry);
                if (code == 401 || code == 403)
                    throw new ProviderError(NeedsKey ? FailKind.Auth : FailKind.Blocked, msg, retry);
                if (code == 456) throw new ProviderError(FailKind.RateLimit, Name + ": quota used up (HTTP 456)", 3600);
                if (code == 400 || code == 404) throw new ProviderError(FailKind.Unsupported, msg);
                throw new ProviderError(FailKind.Network, msg);
            }
        }

        protected string Get(string url, CancellationToken ct) => Send(new HttpRequestMessage(HttpMethod.Get, url), ct);

        protected string PostJson(string url, string json, CancellationToken ct, params (string k, string v)[] headers)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.k, h.v);
            return Send(req, ct);
        }

        protected string PostForm(string url, IEnumerable<KeyValuePair<string, string>> form, CancellationToken ct, params (string k, string v)[] headers)
        {
            var sb = new StringBuilder();
            foreach (var kv in form)
            {
                if (sb.Length > 0) sb.Append('&');
                sb.Append(Uri.EscapeDataString(kv.Key)).Append('=').Append(Uri.EscapeDataString(kv.Value ?? ""));
            }
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/x-www-form-urlencoded") };
            foreach (var h in headers) req.Headers.TryAddWithoutValidation(h.k, h.v);
            return Send(req, ct);
        }

        protected object ParseJson(string body)
        {
            try { return Json.Parse(body); }
            catch (Exception e) { throw new ProviderError(FailKind.BadAnswer, Name + ": unreadable answer (" + e.Message + ") " + Short(body)); }
        }

        protected static string Short(string s) => string.IsNullOrEmpty(s) ? "" : (s.Length > 160 ? s.Substring(0, 160) + "…" : s).Replace("\n", " ");

        protected static List<string> Nulls(int n) => Enumerable.Repeat<string>(null, n).ToList();

        // Azure-style codes (Edge, Azure): zh-Hans, zh-Hant, pt (Brazil) / pt-pt, nb, sr-Cyrl, fil
        protected static string AzureCode(string iso)
        {
            switch (iso)
            {
                case "pt-BR": return "pt";
                case "pt": return "pt-pt";
                case "zh-Hans": case "zh-Hant": return iso;
                case "sr": return "sr-Cyrl";
                default: return Lang.Base(iso);
            }
        }

        protected static string GoogleCode(string iso)
        {
            switch (iso)
            {
                case "zh-Hans": return "zh-CN";
                case "zh-Hant": return "zh-TW";
                case "nb": return "no";
                case "he": return "iw";
                case "pt-BR": return "pt";
                case "fil": return "tl";
                default: return Lang.Base(iso);
            }
        }
    }

    // ================= free, no key =================

    public sealed class EdgeProvider : Provider
    {
        public override string Id => "edge";
        public override string Name => "Microsoft Edge";
        public override int MaxItems => 50;
        public override int MaxChars => 12000;
        public override double MinIntervalSeconds => 0.3;

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            string url = "https://edge.microsoft.com/translate/translatetext?from=" + (src == null ? "" : Uri.EscapeDataString(AzureCode(src))) +
                         "&to=" + Uri.EscapeDataString(AzureCode(tgt)) + "&isEnterpriseClient=false";
            object j = ParseJson(PostJson(url, Json.Array(texts), ct));
            var res = Nulls(texts.Count);
            var arr = j as List<object>;
            if (arr == null) throw new ProviderError(FailKind.BadAnswer, Name + ": unexpected answer");
            for (int i = 0; i < texts.Count && i < arr.Count; i++)
                res[i] = Json.Str(arr[i], "translations", 0, "text");
            return res;
        }
    }

    public sealed class YandexProvider : Provider
    {
        public override string Id => "yandex";
        public override string Name => "Yandex";
        public override int MaxItems => 30;
        public override int MaxChars => 9000;
        public override double MinIntervalSeconds => 0.5;

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            string lang = (src == null ? "" : YCode(src) + "-") + YCode(tgt);
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("lang", lang) };
            foreach (string t in texts) form.Add(new KeyValuePair<string, string>("text", t));
            object j = ParseJson(PostForm("https://browser.translate.yandex.net/api/v1/tr.json/translate?srv=browser_video_translation", form, ct));
            var arr = Json.Arr(j, "text");
            if (arr == null) throw new ProviderError(FailKind.BadAnswer, Name + ": " + Short(Json.Str(j, "message")));
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count && i < arr.Count; i++) res[i] = arr[i] as string;
            return res;
        }

        private static string YCode(string iso) => iso == "zh-Hans" || iso == "zh-Hant" ? "zh" : iso == "nb" ? "no" : Lang.Base(iso);
    }

    public sealed class GoogleWebProvider : Provider
    {
        public override string Id => "google";
        public override string Name => "Google (web)";
        public override int MaxItems => 1;
        public override int MaxChars => 4500;
        public override double MinIntervalSeconds => 0.25;

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count; i++)
            {
                string url = "https://translate.googleapis.com/translate_a/single?client=gtx&sl=" + (src == null ? "auto" : GoogleCode(src)) +
                             "&tl=" + GoogleCode(tgt) + "&dt=t&q=" + Uri.EscapeDataString(texts[i]);
                string body = Get(url, ct);
                if (body.Length == 0 || body[0] != '[')
                    throw new ProviderError(FailKind.Blocked, Name + ": blocked (no translation in the answer)", 600);
                var segs = Json.Arr(ParseJson(body), 0);
                if (segs == null) continue;
                var sb = new StringBuilder();
                foreach (object seg in segs)
                    if (Json.Get(seg, 0) is string part) sb.Append(part);
                res[i] = sb.ToString();
            }
            return res;
        }
    }

    public sealed class MyMemoryProvider : Provider
    {
        public override string Id => "mymemory";
        public override string Name => "MyMemory";
        public override int MaxItems => 1;
        public override int MaxChars => 480;     // MyMemory takes at most 500 bytes per text
        public override double MinIntervalSeconds => 1.0;

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count; i++)
            {
                string pair = (src == null ? "en" : Lang.Base(src) == "zh" ? "zh-CN" : Lang.Base(src)) + "|" + (tgt.StartsWith("zh") ? (tgt == "zh-Hant" ? "zh-TW" : "zh-CN") : Lang.Base(tgt));
                string url = "https://api.mymemory.translated.net/get?q=" + Uri.EscapeDataString(texts[i]) + "&langpair=" + Uri.EscapeDataString(pair) +
                             (string.IsNullOrEmpty(cfg.MyMemoryEmail) ? "" : "&de=" + Uri.EscapeDataString(cfg.MyMemoryEmail));
                object j = ParseJson(Get(url, ct));
                if (Json.Get(j, "quotaFinished") is bool q && q)
                    throw new ProviderError(FailKind.RateLimit, Name + ": daily limit used up", 6 * 3600);
                string status = Json.Get(j, "responseStatus")?.ToString();
                if (status != null && status != "200")
                    throw new ProviderError(status == "429" ? FailKind.RateLimit : FailKind.BadAnswer, Name + ": " + Short(Json.Str(j, "responseDetails")), 3600);
                res[i] = Json.Str(j, "responseData", "translatedText");
            }
            return res;
        }
    }

    public sealed class LingvaProvider : Provider
    {
        public override string Id => "lingva";
        public override string Name => "Lingva";
        public override int MaxItems => 1;
        public override int MaxChars => 2000;
        public override double MinIntervalSeconds => 0.6;

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            var res = Nulls(texts.Count);
            string host = (string.IsNullOrWhiteSpace(cfg.LingvaUrl) ? "https://lingva.lunar.icu" : cfg.LingvaUrl).TrimEnd('/');
            for (int i = 0; i < texts.Count; i++)
            {
                string url = host + "/api/v1/" + (src == null ? "auto" : GoogleCode(src)) + "/" + GoogleCode(tgt) + "/" + Uri.EscapeDataString(texts[i]);
                object j = ParseJson(Get(url, ct));
                res[i] = Json.Str(j, "translation");
            }
            return res;
        }
    }

    // ================= own key =================

    public sealed class DeepLProvider : Provider
    {
        public override string Id => "deepl";
        public override string Name => "DeepL";
        public override bool Free => false;
        public override bool NeedsKey => true;
        public override int MaxItems => 50;
        public override int MaxChars => 25000;
        public override double MinIntervalSeconds => 0.2;
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.DeepLKey);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            string key = cfg.DeepLKey.Trim();
            string url = key.EndsWith(":fx") ? "https://api-free.deepl.com/v2/translate" : "https://api.deepl.com/v2/translate";
            var form = new List<KeyValuePair<string, string>> { new KeyValuePair<string, string>("target_lang", Target(tgt)) };
            if (src != null) form.Add(new KeyValuePair<string, string>("source_lang", Lang.Base(src).ToUpperInvariant()));
            foreach (string t in texts) form.Add(new KeyValuePair<string, string>("text", t));
            object j = ParseJson(PostForm(url, form, ct, ("Authorization", "DeepL-Auth-Key " + key)));
            var arr = Json.Arr(j, "translations");
            if (arr == null) throw new ProviderError(FailKind.BadAnswer, Name + ": unexpected answer");
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count && i < arr.Count; i++) res[i] = Json.Str(arr[i], "text");
            return res;
        }

        private static string Target(string iso)
        {
            switch (iso)
            {
                case "pt-BR": return "PT-BR";
                case "pt": return "PT-PT";
                case "zh-Hans": return "ZH-HANS";
                case "zh-Hant": return "ZH-HANT";
                case "en": return "EN-US";
                default: return Lang.Base(iso).ToUpperInvariant();
            }
        }
    }

    public sealed class AzureProvider : Provider
    {
        public override string Id => "azure";
        public override string Name => "Azure Translator";
        public override bool Free => false;
        public override bool NeedsKey => true;
        public override int MaxItems => 100;
        public override int MaxChars => 40000;
        public override double MinIntervalSeconds => 0.2;
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.AzureKey);

        // 2.6.1: a key from a regional (or multi-service) Azure resource only works when the
        // request names that resource's region; "global" or a wrong region gives HTTP 401.
        // On the first 401 we try the request without a region and then the common regions,
        // remember the one that works and hand it back to the settings (FoundRegion).
        public static volatile string FoundRegion;
        private static volatile bool probed, useQuery;
        private static readonly string[] Regions =
        {
            "", "global", "westeurope", "northeurope", "germanywestcentral", "swedencentral", "francecentral",
            "uksouth", "switzerlandnorth", "polandcentral", "norwayeast", "italynorth", "eastus", "eastus2",
            "westus", "westus2", "westus3", "centralus", "southcentralus", "northcentralus", "canadacentral",
            "brazilsouth", "eastasia", "southeastasia", "japaneast", "koreacentral", "australiaeast",
            "centralindia", "uaenorth", "southafricanorth"
        };
        private const string Base = "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0";

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            string url = Base + "&to=" + AzureCode(tgt) + (src == null ? "" : "&from=" + AzureCode(src));
            string body = "[" + string.Join(",", texts.Select(t => "{\"Text\":" + Json.Quote(t) + "}").ToArray()) + "]";
            string key = new string((cfg.AzureKey ?? "").Where(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c != '\uFEFF' && c != '\u200B').ToArray());
            string region = FoundRegion ?? (cfg.AzureRegion ?? "").Trim();
            string answer;
            try { answer = Call(url, body, key, region, useQuery, ct); }
            catch (ProviderError e) when (e.Kind == FailKind.Auth && !probed)
            {
                probed = true;
                // 2.6.2: the key header can get lost on the way (Unity's Mono HTTP client); the
                // Translator also accepts the key in the address – try that first, then the regions
                const string probe = "[{\"Text\":\"ok\"}]";
                string probeUrl = Base + "&to=de&from=en";
                string found = null; bool q = false;
                foreach (bool mode in new[] { !useQuery, true })
                {
                    foreach (string r in mode == useQuery ? Regions.Where(x => !string.Equals(x, region, StringComparison.OrdinalIgnoreCase)) : new[] { region }.Concat(Regions))
                    {
                        if (ct.IsCancellationRequested) break;
                        try { Call(probeUrl, probe, key, r, mode, ct); found = r; q = mode; break; }
                        catch (ProviderError pe) when (pe.Kind == FailKind.Auth) { Thread.Sleep(120); }
                    }
                    if (found != null) break;
                }
                if (found == null)
                    throw new ProviderError(FailKind.Auth, e.Message + " – the key works in no Azure region; check that it is the Translator key (Keys and Endpoint), not a subscription or Foundry project key");
                FoundRegion = found; useQuery = q;
                answer = Call(url, body, key, found, q, ct);
            }
            object j = ParseJson(answer);
            var arr = j as List<object>;
            if (arr == null) throw new ProviderError(FailKind.BadAnswer, Name + ": unexpected answer");
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count && i < arr.Count; i++) res[i] = Json.Str(arr[i], "translations", 0, "text");
            return res;
        }

        private string Call(string url, string body, string key, string region, bool inQuery, CancellationToken ct)
        {
            bool hasRegion = !string.IsNullOrWhiteSpace(region);
            if (inQuery)
            {
                url += "&Subscription-Key=" + Uri.EscapeDataString(key) + (hasRegion ? "&Subscription-Region=" + Uri.EscapeDataString(region.Trim()) : "");
                try { return PostJson(url, body, ct); }
                catch (ProviderError e) when (key.Length > 0 && e.Message.Contains(key))
                {
                    throw new ProviderError(e.Kind, e.Message.Replace(Uri.EscapeDataString(key), "***").Replace(key, "***"), e.RetryAfterSeconds);   // never log the key
                }
            }
            var headers = new List<(string, string)> { ("Ocp-Apim-Subscription-Key", key) };
            if (hasRegion) headers.Add(("Ocp-Apim-Subscription-Region", region.Trim()));
            return PostJson(url, body, ct, headers.ToArray());
        }

        public static string Mode => (useQuery ? "key in address" : "key in header") + ", region " + (FoundRegion == null ? "from settings" : FoundRegion.Length == 0 ? "none" : FoundRegion);

        public static void KeyChanged() { probed = false; FoundRegion = null; useQuery = false; }
    }

    public sealed class GoogleCloudProvider : Provider
    {
        public override string Id => "googlecloud";
        public override string Name => "Google Cloud";
        public override bool Free => false;
        public override bool NeedsKey => true;
        public override int MaxItems => 100;
        public override int MaxChars => 25000;
        public override double MinIntervalSeconds => 0.2;
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.GoogleCloudKey);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            var form = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("target", GoogleCode(tgt)),
                new KeyValuePair<string, string>("format", "text")
            };
            if (src != null) form.Add(new KeyValuePair<string, string>("source", GoogleCode(src)));
            foreach (string t in texts) form.Add(new KeyValuePair<string, string>("q", t));
            object j = ParseJson(PostForm("https://translation.googleapis.com/language/translate/v2?key=" + Uri.EscapeDataString(cfg.GoogleCloudKey.Trim()), form, ct));
            var arr = Json.Arr(j, "data", "translations");
            if (arr == null) throw new ProviderError(FailKind.BadAnswer, Name + ": " + Short(Json.Str(j, "error", "message")));
            var res = Nulls(texts.Count);
            for (int i = 0; i < texts.Count && i < arr.Count; i++) res[i] = WebUtility.HtmlDecode(Json.Str(arr[i], "translatedText"));
            return res;
        }
    }

    public sealed class LibreProvider : Provider
    {
        public override string Id => "libre";
        public override string Name => "LibreTranslate (own server)";
        public override bool Free => false;
        public override bool NeedsKey => true;
        public override int MaxItems => 25;
        public override double MinIntervalSeconds => 0.3;
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.LibreUrl);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
        {
            CheckUrl(cfg.LibreUrl);
            string body = "{\"q\":" + Json.Array(texts) + ",\"source\":" + Json.Quote(src == null ? "auto" : Lang.Base(src)) + ",\"target\":" + Json.Quote(Lang.Base(tgt)) +
                          ",\"format\":\"text\"" + (string.IsNullOrWhiteSpace(cfg.LibreKey) ? "" : ",\"api_key\":" + Json.Quote(cfg.LibreKey.Trim())) + "}";
            object j = ParseJson(PostJson(cfg.LibreUrl.TrimEnd('/') + "/translate", body, ct));
            var res = Nulls(texts.Count);
            if (Json.Get(j, "translatedText") is List<object> arr)
                for (int i = 0; i < texts.Count && i < arr.Count; i++) res[i] = arr[i] as string;
            else if (texts.Count == 1)
                res[0] = Json.Str(j, "translatedText");
            return res;
        }
    }

    // ---- AI translators: the batch goes in as a numbered JSON list and must come back the same ----
    public abstract class AiProvider : Provider
    {
        public override bool Free => false;
        public override bool NeedsKey => true;
        public override bool IsAi => true;
        public override int MaxItems => 40;
        public override int MaxChars => 8000;
        public override double MinIntervalSeconds => 1.0;

        protected static string Prompt(List<string> texts, string src, string tgt, TranslateContext ctx = null)
        {
            var sb = new StringBuilder();
            sb.Append("You translate texts of the video game RimWorld (a colony sim) ").Append(src == null ? "" : "from " + src + " ")
              .Append("into the language with code '").Append(tgt).Append("'.\n");
            sb.Append("Rules: keep every placeholder like {0} {1} exactly as it is; keep the meaning, short UI style for short labels; " +
                      "keep proper names (people, races, xenotypes, factions, places) unchanged; " +
                      "do not add explanations. Answer ONLY with a JSON array of strings, the same length and order as the input.\n");
            // 2.5.2: context – the kind of text and its mod, and the player's own words
            if (ctx != null)
            {
                if (ctx.Hints != null && ctx.Hints.Count == texts.Count && ctx.Hints.Any(h => !string.IsNullOrEmpty(h)))
                    sb.Append("Context of each input text (same order, do not translate this): ").Append(Json.Array(ctx.Hints.Select(h => h ?? "").ToList())).Append("\n");
                if (!string.IsNullOrEmpty(ctx.Notes))
                    sb.Append(ctx.Notes).Append("\n");
            }
            sb.Append("Input: ").Append(Json.Array(texts));
            return sb.ToString();
        }

        protected static List<string> ReadArray(string answer, int n)
        {
            if (answer == null) return null;
            int a = answer.IndexOf('['), b = answer.LastIndexOf(']');
            if (a < 0 || b <= a) return null;
            try
            {
                var arr = Json.Parse(answer.Substring(a, b - a + 1)) as List<object>;
                if (arr == null || arr.Count != n) return null;
                return arr.Select(x => x as string).ToList();
            }
            catch { return null; }
        }
    }

    public sealed class GeminiProvider : AiProvider
    {
        public override string Id => "gemini";
        public override string Name => "Google Gemini (AI)";
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.GeminiKey);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
            => Translate(texts, src, tgt, cfg, ct, null);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct, TranslateContext ctx)
        {
            string model = string.IsNullOrWhiteSpace(cfg.GeminiModel) ? "gemini-2.5-flash" : cfg.GeminiModel.Trim();
            string url = "https://generativelanguage.googleapis.com/v1beta/models/" + Uri.EscapeDataString(model) + ":generateContent?key=" + Uri.EscapeDataString(cfg.GeminiKey.Trim());
            string body = "{\"contents\":[{\"parts\":[{\"text\":" + Json.Quote(Prompt(texts, src, tgt, ctx)) + "}]}],\"generationConfig\":{\"temperature\":0.2,\"responseMimeType\":\"application/json\"}}";
            object j = ParseJson(PostJson(url, body, ct));
            string answer = Json.Str(j, "candidates", 0, "content", "parts", 0, "text");
            return ReadArray(answer, texts.Count) ?? throw new ProviderError(FailKind.BadAnswer, Name + ": answer was not a list of " + texts.Count + " texts");
        }
    }

    public sealed class OpenAiProvider : AiProvider
    {
        public override string Id => "openai";
        public override string Name => "AI (OpenAI-compatible)";
        public override bool Configured(ProviderConfig c) => !string.IsNullOrWhiteSpace(c.AiUrl) && !string.IsNullOrWhiteSpace(c.AiModel);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct)
            => Translate(texts, src, tgt, cfg, ct, null);

        public override List<string> Translate(List<string> texts, string src, string tgt, ProviderConfig cfg, CancellationToken ct, TranslateContext ctx)
        {
            CheckUrl(cfg.AiUrl);
            string url = cfg.AiUrl.Trim().TrimEnd('/');
            if (!url.EndsWith("/chat/completions")) url += "/chat/completions";
            string body = "{\"model\":" + Json.Quote(cfg.AiModel.Trim()) + ",\"temperature\":0.2,\"messages\":[{\"role\":\"user\",\"content\":" + Json.Quote(Prompt(texts, src, tgt, ctx)) + "}]}";
            var headers = string.IsNullOrWhiteSpace(cfg.AiKey) ? new (string, string)[0] : new[] { ("Authorization", "Bearer " + cfg.AiKey.Trim()) };
            object j = ParseJson(PostJson(url, body, ct, headers));
            string answer = Json.Str(j, "choices", 0, "message", "content");
            return ReadArray(answer, texts.Count) ?? throw new ProviderError(FailKind.BadAnswer, Name + ": answer was not a list of " + texts.Count + " texts");
        }
    }

    public static class Providers
    {
        public static readonly List<Provider> All = new List<Provider>
        {
            new EdgeProvider(), new YandexProvider(), new GoogleWebProvider(), new MyMemoryProvider(), new LingvaProvider(),
            new DeepLProvider(), new AzureProvider(), new GoogleCloudProvider(), new GeminiProvider(), new OpenAiProvider(), new LibreProvider()
        };

        public static Provider Get(string id) => All.FirstOrDefault(p => p.Id == id);
    }
}
